using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WordAI.Agent.Protocol;

namespace WordAI.Agent.Pi
{
    /// <summary>
    /// 双工流抽象：生产环境为子进程 stdin/stdout，测试可注入假流。
    /// </summary>
    public interface IDuplexStream : IDisposable
    {
        /// <summary>读取端（Pi → C#）。</summary>
        Stream In { get; }

        /// <summary>写入端（C# → Pi）。</summary>
        Stream Out { get; }
    }

    /// <summary>
    /// 通道 A：与 Pi 子进程的 JSON Lines RPC 客户端。
    ///
    /// 协议（见 docs/technical-plan.md 第三章通道 A）：
    /// - 请求：{"id":"c1","method":...,"params":{...}} 单行 JSON
    /// - 响应：{"id":"c1","result":...} 或 {"id":"c1","error":{"code":...,"message":...}}
    /// - 通知：无 id 的消息，按 type 字段分发到事件回调
    ///
    /// 读写分离：写方持锁串行；读循环单线程消费，按 id 关联到 pending 字典。
    /// </summary>
    public sealed class PiRpcClient : IDisposable
    {
        private readonly IDuplexStream _stream;
        private readonly LineDecoder _decoder = new LineDecoder(ProtocolConstants.MaxPiLineChars);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JToken>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<JToken>>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private int _nextId;
        private Task _readLoop;
        private volatile bool _disposed;
        private volatile bool _closed;

        /// <summary>收到 Pi 侧通知（无 id 消息）时触发，回调在读循环线程上执行。</summary>
        public event Action<JObject> Notification;

        /// <summary>读循环因流关闭或异常退出时触发（参数为异常，正常关闭为 null）。</summary>
        public event Action<Exception> Closed;

        public PiRpcClient(IDuplexStream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>启动读循环（幂等）。</summary>
        public void Start()
        {
            if (_readLoop != null) return;
            lock (this)
            {
                if (_readLoop != null) return;
                _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
            }
        }

        /// <summary>
        /// 发送请求并等待对应 id 的响应。
        /// 流关闭 / 取消时抛 <see cref="PiRpcException"/>；Pi 返回 error 对象时同样抛出（携带 code/message）。
        /// </summary>
        public async Task<JToken> CallAsync(string method, JObject args, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PiRpcClient));
            var id = "c" + Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs))
                throw new PiRpcException(-32603, "duplicate id: " + id);

            // 流已关闭：快速失败，避免永久挂起（读循环退出后不会再有响应）
            if (_closed && _pending.TryRemove(id, out _))
                throw new PiRpcException(-32603, "pi stream closed before response (id=" + id + ")");

            var req = new JObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = args ?? new JObject()
            };

            try
            {
                await WriteLineAsync(req.ToString(Newtonsoft.Json.Formatting.None), ct).ConfigureAwait(false);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }

            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var resp = await tcs.Task.ConfigureAwait(false);
                var err = resp["error"];
                if (err != null)
                    throw new PiRpcException((int?)err["code"] ?? -32603, (string)err["message"] ?? "unknown pi error");
                return resp["result"];
            }
        }

        /// <summary>发送通知（不等待响应，Pi 侧 fire-and-forget）。</summary>
        public async Task NotifyAsync(string method, JObject args, CancellationToken ct)
        {
            var req = new JObject
            {
                ["method"] = method,
                ["params"] = args ?? new JObject()
            };
            await WriteLineAsync(req.ToString(Newtonsoft.Json.Formatting.None), ct).ConfigureAwait(false);
        }

        private async Task WriteLineAsync(string line, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.Out.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await _stream.Out.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            Exception fault = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = await _stream.In.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    if (n == 0) break; // 对端关闭

                    foreach (var line in _decoder.Append(buffer, n))
                    {
                        HandleLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                fault = ex;
            }
            finally
            {
                _closed = true;
                // 流结束：fail 所有 pending，避免调用方永久挂起
                foreach (var kv in _pending)
                {
                    kv.Value.TrySetException(new PiRpcException(-32603,
                        "pi stream closed before response (id=" + kv.Key + ")"));
                }
                Closed?.Invoke(fault);
            }
        }

        private void HandleLine(string line)
        {
            JObject msg;
            try
            {
                msg = JObject.Parse(line);
            }
            catch
            {
                // 非 JSON 行丢弃：Pi 偶发 stderr 串流或调试输出不应破坏 RPC
                return;
            }

            var id = (string)msg["id"];
            if (id != null)
            {
                if (_pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(msg);
                // 未知 id（已超时取消）直接丢弃
                return;
            }
            Notification?.Invoke(msg);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _stream.Dispose(); } catch { }
            foreach (var kv in _pending)
                kv.Value.TrySetException(new PiRpcException(-32603, "rpc client disposed"));
            ((IDisposable)_writeLock).Dispose();
        }
    }

    /// <summary>Pi RPC 异常：code 为 Pi 侧错误码（无则 -32603 internal）。</summary>
    public sealed class PiRpcException : Exception
    {
        public int Code { get; }

        public PiRpcException(int code, string message) : base(message)
        {
            Code = code;
        }
    }
}
