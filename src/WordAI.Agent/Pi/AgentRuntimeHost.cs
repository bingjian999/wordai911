using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WordAI.Agent.Protocol;

namespace WordAI.Agent.Pi
{
    /// <summary>
    /// Pi 运行时目录的锁定清单（pi-runtime.json）：
    /// 确保 C# 宿主启动的 Pi 版本与验证时一致，运行时被篡改/升级即拒绝启动。
    /// </summary>
    public sealed class PiRuntimeLock
    {
        public string RuntimeVersion { get; }
        public string UpstreamSha256 { get; }

        private PiRuntimeLock(string version, string sha)
        {
            RuntimeVersion = version;
            UpstreamSha256 = sha;
        }

        /// <summary>从 pi-runtime.json 加载并校验 entry 文件的 sha256。</summary>
        public static PiRuntimeLock Load(string runtimeDir)
        {
            var manifest = Path.Combine(runtimeDir, "pi-runtime.json");
            if (!File.Exists(manifest))
                throw new FileNotFoundException("pi-runtime.json not found in " + runtimeDir, manifest);

            var json = JObject.Parse(File.ReadAllText(manifest));
            var version = (string)json["runtimeVersion"];
            var sha = (string)json["upstreamSha256"];
            var entry = (string)json["entry"];
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(sha) || string.IsNullOrEmpty(entry))
                throw new InvalidDataException("pi-runtime.json missing runtimeVersion/upstreamSha256/entry");

            var entryPath = Path.Combine(runtimeDir, entry);
            if (!File.Exists(entryPath))
                throw new FileNotFoundException("pi entry not found: " + entryPath, entryPath);

            var actual = Sha256File(entryPath);
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                throw new PiLockException($"pi runtime integrity check failed: entry sha256 {actual} != manifest {sha}");

            return new PiRuntimeLock(version, sha);
        }

        internal static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    public sealed class PiLockException : Exception
    {
        public PiLockException(string message) : base(message) { }
    }

    /// <summary>
    /// 大脑层宿主：Pi 子进程生命周期管理（见 docs/technical-plan.md 第三章）。
    ///
    /// - 启动：node + 入口文件，注入环境变量（会话 token / 网关无关）
    /// - 心跳：周期 get_state，连续 3 次失败判定运行时僵死 → 重启
    /// - 异常退出：指数退避重启，上限 3 次，超过抛出并停止
    /// - 优雅关闭：先发 shutdown 通知并等退出，超时强杀
    /// </summary>
    public sealed class AgentRuntimeHost : IDisposable
    {
        /// <summary>子进程启动配置。</summary>
        public sealed class Options
        {
            /// <summary>node 可执行文件路径。</summary>
            public string NodePath = "node";

            /// <summary>Pi 运行时目录（含 pi-runtime.json 与入口）。</summary>
            public string RuntimeDir;

            /// <summary>心跳间隔（毫秒）。</summary>
            public int HeartbeatIntervalMs = 5000;

            /// <summary>心跳调用超时（毫秒）。</summary>
            public int HeartbeatTimeoutMs = 3000;

            /// <summary>连续心跳失败判定阈值。</summary>
            public int HeartbeatFailLimit = 3;

            /// <summary>异常退出最大重启次数（超过后 OnFatal 触发、不再重启）。</summary>
            public int MaxRestarts = 3;

            /// <summary>优雅关闭等待时长（毫秒）。</summary>
            public int GracefulShutdownMs = 5000;
        }

        private readonly Options _opt;
        private readonly Action<string> _log;
        private Process _process;
        private PiRpcClient _rpc;
        private CancellationTokenSource _lifetimeCts;
        private Task _supervisor;
        private int _restarts;
        private int _heartbeatFails;
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
        private volatile bool _disposed;
        private volatile bool _intentionalStop;

        /// <summary>运行时被启动并完成握手后为 true。</summary>
        public bool IsRunning => _rpc != null && !_lifetimeCts.IsCancellationRequested;

        /// <summary>Pi RPC 就绪（握手成功）后触发一次。</summary>
        public event Action Started;

        /// <summary>运行时异常退出且已达重启上限（或重启失败）时触发——宿主应提示用户。</summary>
        public event Action<Exception> OnFatal;

        public AgentRuntimeHost(Options opt, Action<string> log = null)
        {
            _opt = opt ?? throw new ArgumentNullException(nameof(opt));
            _log = log ?? (_ => { });
        }

        /// <summary>启动 Pi 子进程并开始监督循环。</summary>
        public async Task StartAsync(CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AgentRuntimeHost));
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_supervisor != null) return; // 幂等
                _lifetimeCts = new CancellationTokenSource();
                _supervisor = Task.Run(() => SuperviseAsync(_lifetimeCts.Token));
                // 等待首轮启动完成（或失败）
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action onStart = () => tcs.TrySetResult(true);
                Started += onStart;
                try
                {
                    if (IsRunning) { tcs.TrySetResult(true); }
                    var delay = Task.Delay(_opt.HeartbeatIntervalMs * 2 + 10000, ct);
                    var done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
                    if (done != tcs.Task)
                        throw new TimeoutException("pi runtime did not become ready in time");
                }
                finally { Started -= onStart; }
            }
            finally { _mutex.Release(); }
        }

        private async Task SuperviseAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Exception fault = null;
                try
                {
                    await StartProcessAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    fault = ex;
                }

                if (fault != null || _process == null || _process.HasExited)
                {
                    _intentionalStop = ct.IsCancellationRequested;
                    if (!_intentionalStop)
                    {
                        if (_restarts < _opt.MaxRestarts)
                        {
                            _restarts++;
                            var backoff = TimeSpan.FromSeconds(Math.Min(30, 1 << _restarts)); // 2s, 4s, 8s...
                            _log($"[runtime] pi exited unexpectedly, restart {_restarts}/{_opt.MaxRestarts} in {backoff.TotalSeconds}s");
                            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                            continue;
                        }
                        OnFatal?.Invoke(fault ?? new Exception("pi exited unexpectedly (restart budget exhausted)"));
                        return;
                    }
                    return;
                }

                // 进程就绪：进入心跳监督
                try
                {
                    await HeartbeatLoopAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                // 心跳循环退出而 ct 未取消 = 僵死或退出，走重启逻辑
                _heartbeatFails = 0;
            }
        }

        private async Task StartProcessAsync(CancellationToken ct)
        {
            var lockFile = PiRuntimeLock.Load(_opt.RuntimeDir); // 校验失败直接抛 → OnFatal
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(_opt.RuntimeDir, "pi-runtime.json")));
            var entry = (string)manifest["entry"];

            var psi = new ProcessStartInfo
            {
                FileName = _opt.NodePath,
                Arguments = Quote(entry),
                WorkingDirectory = _opt.RuntimeDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["WORDAI_SESSION"] = Guid.NewGuid().ToString("N");
            psi.EnvironmentVariables["WORDAI_RUNTIME_VERSION"] = lockFile.RuntimeVersion;

            _process = Process.Start(psi);
            _process.EnableRaisingEvents = true;
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _log("[pi-stderr] " + e.Data); };
            _process.BeginErrorReadLine();

            _rpc = new PiRpcClient(new ProcessDuplexStream(_process));
            _rpc.Closed += ex =>
            {
                _log("[runtime] pi stdout closed" + (ex == null ? "" : ": " + ex.Message));
                _lifetimeCts?.Cancel(); // 触发监督循环收敛
            };
            _rpc.Start();

            // 握手：get_state 快速验证通道可用
            var state = await _rpc.CallAsync("get_state", new JObject(), ct).ConfigureAwait(false);
            if (state == null) throw new InvalidDataException("pi get_state returned null");
            _log($"[runtime] pi ready (version={lockFile.RuntimeVersion}, pid={_process.Id})");
            Started?.Invoke();
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _process != null && !_process.HasExited)
            {
                await Task.Delay(_opt.HeartbeatIntervalMs, ct).ConfigureAwait(false);
                if (_process.HasExited) return;

                try
                {
                    using (var timeout = new CancellationTokenSource(_opt.HeartbeatTimeoutMs))
                    {
                        await _rpc.CallAsync("get_state", new JObject(), timeout.Token).ConfigureAwait(false);
                    }
                    _heartbeatFails = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    _heartbeatFails++;
                    _log($"[runtime] heartbeat failed ({_heartbeatFails}/{_opt.HeartbeatFailLimit})");
                    if (_heartbeatFails >= _opt.HeartbeatFailLimit)
                    {
                        _log("[runtime] pi unresponsive, killing for restart");
                        try { _process.Kill(); } catch { }
                        return; // 退出心跳循环 → 监督循环重启
                    }
                }
            }
        }

        /// <summary>优雅关闭：发 shutdown 通知 → 等待退出 → 超时强杀。</summary>
        public async Task StopAsync()
        {
            if (_disposed || _supervisor == null) return;
            _intentionalStop = true;
            try
            {
                if (_rpc != null)
                    await _rpc.NotifyAsync("shutdown", new JObject(), CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* 通知失败不阻断关闭 */ }

            var exited = _process != null && _process.HasExited;
            if (!exited && _process != null)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < _opt.GracefulShutdownMs && !_process.HasExited)
                    await Task.Delay(100).ConfigureAwait(false);
                if (!_process.HasExited)
                {
                    _log("[runtime] graceful shutdown timeout, killing");
                    try { _process.Kill(); } catch { }
                }
            }

            _lifetimeCts?.Cancel();
            try { await _supervisor.ConfigureAwait(false); } catch { }
            Cleanup();
        }

        private void Cleanup()
        {
            try { _rpc?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }
            _rpc = null;
            _process = null;
        }

        /// <summary>向 Pi 转发请求（由网关扩展命令 / UI 桥调用）。</summary>
        public Task<JToken> CallPiAsync(string method, JObject args, CancellationToken ct)
        {
            var rpc = _rpc;
            if (rpc == null) throw new PiRpcException(-32603, "pi runtime not running");
            return rpc.CallAsync(method, args, ct);
        }

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
            ((IDisposable)_mutex).Dispose();
        }
    }

    /// <summary>子进程 stdin/stdout 适配为 IDuplexStream。</summary>
    internal sealed class ProcessDuplexStream : IDuplexStream
    {
        private readonly Process _p;
        public ProcessDuplexStream(Process p) { _p = p; }
        public Stream In => _p.StandardOutput.BaseStream;
        public Stream Out => _p.StandardInput.BaseStream;
        public void Dispose() { /* 进程由 AgentRuntimeHost 管理 */ }
    }
}
