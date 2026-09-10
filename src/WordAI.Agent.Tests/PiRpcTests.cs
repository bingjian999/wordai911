using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WordAI.Agent.Pi;
using Xunit;

namespace WordAI.Agent.Tests
{
    /// <summary>
    /// 阻塞式假输入流：读空时阻塞等待（模拟真实子进程 stdout，
    /// 避免 MemoryStream 立即 EOF 触发客户端的流关闭快速失败）。
    /// </summary>
    internal sealed class BlockingReadStream : Stream
    {
        private readonly object _lock = new object();
        private readonly Queue<byte> _buf = new Queue<byte>();
        private bool _shutdown;

        public void Push(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            lock (_lock)
            {
                foreach (var b in bytes) _buf.Enqueue(b);
                Monitor.PulseAll(_lock);
            }
        }

        public void Shutdown()
        {
            lock (_lock) { _shutdown = true; Monitor.PulseAll(_lock); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                while (_buf.Count == 0 && !_shutdown) Monitor.Wait(_lock);
                if (_buf.Count == 0) return 0;
                int n = Math.Min(count, _buf.Count);
                for (int i = 0; i < n; i++) buffer[offset + i] = _buf.Dequeue();
                return n;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 响应式假输出流：只有当客户端真正写出一行请求后，才推送下一条脚本响应。
    /// 响应模板中的 "{id}" 占位符替换为请求行里的实际 id。
    /// 这消除了“读循环在 CallAsync 注册 pending 之前就消费预置响应”的竞态——
    /// 真实 Pi 子进程不可能在收到请求之前发送响应。
    /// </summary>
    internal sealed class ScriptedOutStream : Stream
    {
        private readonly BlockingReadStream _in;
        private readonly Queue<string> _responses;
        private List<string> _afterAny;
        private readonly StringBuilder _line = new StringBuilder();
        private readonly object _writeLock = new object();

        /// <summary>已写出的完整请求行（按顺序），供测试断言。</summary>
        public readonly List<string> SentLines = new List<string>();

        public ScriptedOutStream(BlockingReadStream input, IEnumerable<string> responses,
            IEnumerable<string> afterAny = null)
        {
            _in = input;
            _responses = new Queue<string>(responses);
            _afterAny = afterAny != null ? new List<string>(afterAny) : null;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_writeLock)
            {
                _line.Append(Encoding.UTF8.GetString(buffer, offset, count));
                while (true)
                {
                    var s = _line.ToString();
                    int nl = s.IndexOf('\n');
                    if (nl < 0) break;
                    var reqLine = s.Substring(0, nl);
                    _line.Remove(0, nl + 1);

                    string id = null;
                    try { id = (string)JObject.Parse(reqLine)["id"]; } catch { }
                    SentLines.Add(reqLine);

                    if (_responses.Count > 0)
                    {
                        var resp = _responses.Dequeue().Replace("{id}", id ?? "");
                        _in.Push(resp + "\n");
                    }
                    if (_afterAny != null)
                    {
                        foreach (var extra in _afterAny) _in.Push(extra + "\n");
                        _afterAny = null;
                    }
                }
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 假双工流：输入端阻塞（模拟子进程 stdout），输出端响应式——
    /// 每写出一行请求才推送下一条脚本响应；可显式关闭输入端模拟 EOF。
    /// </summary>
    internal sealed class ScriptedDuplexStream : IDuplexStream
    {
        private readonly BlockingReadStream _in = new BlockingReadStream();
        private readonly ScriptedOutStream _out;

        public ScriptedDuplexStream(IEnumerable<string> responses, IEnumerable<string> afterAny = null,
            IEnumerable<string> initialLines = null)
        {
            if (initialLines != null)
                foreach (var l in initialLines) _in.Push(l + "\n");
            _out = new ScriptedOutStream(_in, responses, afterAny);
        }

        public Stream In => _in;
        public Stream Out => _out;

        public IReadOnlyList<string> SentLines => _out.SentLines;

        /// <summary>显式关闭读端：后续 Read 返回 0（EOF 语义）。</summary>
        public void Close() => _in.Shutdown();

        public void Dispose() => _in.Shutdown();
    }

    public class PiRpcClientTests
    {
        [Fact]
        public async Task Call_CorrelatesById()
        {
            using (var stream = new ScriptedDuplexStream(new[]
            {
                "{\"id\":\"{id}\",\"result\":{\"state\":\"idle\"}}",
                "{\"id\":\"{id}\",\"result\":{\"state\":\"busy\"}}",
            }))
            using (var rpc = new PiRpcClient(stream))
            {
                rpc.Start();
                var t1 = rpc.CallAsync("get_state", new JObject(), CancellationToken.None);
                var t2 = rpc.CallAsync("get_status", new JObject(), CancellationToken.None);
                var r1 = await t1;
                var r2 = await t2;
                Assert.Equal("idle", (string)r1["state"]);
                Assert.Equal("busy", (string)r2["state"]);

                // 写出的是两条合法 JSONL 请求
                Assert.Equal(2, stream.SentLines.Count);
                Assert.NotNull(JObject.Parse(stream.SentLines[0])["id"]);
                Assert.NotNull(JObject.Parse(stream.SentLines[1])["id"]);
            }
        }

        [Fact]
        public async Task Call_ErrorResponse_ThrowsPiRpcException()
        {
            using (var stream = new ScriptedDuplexStream(new[]
            {
                "{\"id\":\"{id}\",\"error\":{\"code\":-32001,\"message\":\"denied\"}}",
            }))
            using (var rpc = new PiRpcClient(stream))
            {
                rpc.Start();
                var ex = await Assert.ThrowsAsync<PiRpcException>(() =>
                    rpc.CallAsync("forbidden", new JObject(), CancellationToken.None));
                Assert.Equal(-32001, ex.Code);
                Assert.Equal("denied", ex.Message);
            }
        }

        [Fact]
        public async Task Notification_RaisedWithoutId()
        {
            JObject seen = null;
            var mre = new ManualResetEventSlim();
            using (var stream = new ScriptedDuplexStream(
                responses: new[] { "{\"id\":\"{id}\",\"result\":1}" },
                afterAny: new[] { "{\"type\":\"log\",\"level\":\"info\",\"text\":\"pi ready\"}" }))
            using (var rpc = new PiRpcClient(stream))
            {
                rpc.Notification += n => { seen = n; mre.Set(); };
                rpc.Start();
                var r = await rpc.CallAsync("get_state", new JObject(), CancellationToken.None);
                Assert.Equal(1, (int)r);
                Assert.True(mre.Wait(3000));
                Assert.Equal("log", (string)seen["type"]);
                Assert.Equal("pi ready", (string)seen["text"]);
            }
        }

        [Fact]
        public async Task StreamClosedBeforeResponse_FailsPending()
        {
            using (var stream = new ScriptedDuplexStream(responses: new string[0]))
            using (var rpc = new PiRpcClient(stream))
            {
                rpc.Start();
                stream.Close(); // 关闭读端 → 读循环退出，pending 全部失败
                await Task.Delay(100); // 读循环先消费 EOF
                var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                    rpc.CallAsync("get_state", new JObject(), CancellationToken.None));
                Assert.True(ex is PiRpcException || ex is ObjectDisposedException);
            }
        }

        [Fact]
        public async Task NonJsonLine_Ignored()
        {
            using (var stream = new ScriptedDuplexStream(
                responses: new[] { "{\"id\":\"{id}\",\"result\":{\"ok\":true}}" },
                initialLines: new[] { "garbage line" }))
            using (var rpc = new PiRpcClient(stream))
            {
                rpc.Start();
                var r = await rpc.CallAsync("get_state", new JObject(), CancellationToken.None);
                Assert.True((bool)r["ok"]);
            }
        }

        [Fact]
        public async Task Cancellation_WhileWaiting_CancelsTask()
        {
            using (var stream = new ScriptedDuplexStream(responses: new string[0])) // 无响应
            using (var rpc = new PiRpcClient(stream))
            {
                rpc.Start();
                using (var cts = new CancellationTokenSource(200))
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        rpc.CallAsync("get_state", new JObject(), cts.Token));
                }
            }
        }
    }

    public class PiRuntimeLockTests
    {
        [Fact]
        public void Load_MissingManifest_Throws()
        {
            var dir = Path.Combine(Path.GetTempPath(), "wordai-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Throws<FileNotFoundException>(() => WordAI.Agent.Pi.PiRuntimeLock.Load(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Load_TamperedEntry_Throws()
        {
            var dir = Path.Combine(Path.GetTempPath(), "wordai-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var entry = Path.Combine(dir, "pi-entry.js");
                File.WriteAllText(entry, "console.log('ok')");
                var sha = PiRuntimeLock.Sha256File(entry);
                var manifest = new JObject
                {
                    ["runtimeVersion"] = "0.85.1",
                    ["upstreamSha256"] = sha,
                    ["entry"] = "pi-entry.js"
                };
                File.WriteAllText(Path.Combine(dir, "pi-runtime.json"), manifest.ToString());

                var ok = PiRuntimeLock.Load(dir);
                Assert.Equal("0.85.1", ok.RuntimeVersion);

                // 篡改入口文件 → 校验失败
                File.WriteAllText(entry, "console.log('tampered')");
                Assert.Throws<PiLockException>(() => PiRuntimeLock.Load(dir));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
