using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WordAI.Agent.Word;

namespace WordAI.Agent.Tests
{
    /// <summary>
    /// 可编程 Fake Word 宿主：模拟 COM 层行为，供网关端到端测试。
    /// </summary>
    public sealed class FakeWordHost : IWordHost
    {
        public readonly List<string> Calls = new List<string>();
        public readonly List<(string Method, int Order)> ComOrder = new List<(string, int)>();

        private int _comCounter;
        private string _docText = "hello world. hello wordai. ";

        /// <summary>Find 阻塞直到 ct 取消（用于测试协作取消）。</summary>
        public bool FindBlocksUntilCancelled;

        /// <summary>Replace 执行时长（毫秒，模拟慢 COM 调用）。</summary>
        public int ReplaceDelayMs;

        /// <summary>若非 null：Replace 阻塞直到 Set()（确定性挡住 COM 队列，测试用）。</summary>
        public System.Threading.ManualResetEventSlim ReplaceGate;

        public DocumentInfo GetDocumentInfo()
        {
            Calls.Add("getDocumentInfo");
            return new DocumentInfo
            {
                FileName = "fake.docx",
                FilePath = "C:\\fake\\fake.docx",
                ParagraphCount = 3,
                WordCount = 6,
                CharacterCount = 28,
                Saved = true,
                ReadProtectionState = "none"
            };
        }

        public InsertResult InsertText(string text, InsertPosition position)
        {
            var order = Interlocked.Increment(ref _comCounter);
            Calls.Add($"insertText@{position}:{text}");
            ComOrder.Add(("insertText", order));
            _docText += text;
            return new InsertResult { CharactersInserted = text.Length, TotalCharacters = _docText.Length };
        }

        public SelectionInfo GetSelection()
        {
            Calls.Add("getSelection");
            return new SelectionInfo { Text = "hello", StartOffset = 0, EndOffset = 5, StyleName = "Normal" };
        }

        public FindResult Find(string query, int maxHits, CancellationToken ct)
        {
            Calls.Add($"find:{query}");
            if (FindBlocksUntilCancelled)
            {
                var mre = new ManualResetEventSlim();
                using (ct.Register(() => mre.Set()))
                {
                    mre.Wait(15000); // 上限防挂死
                }
                ct.ThrowIfCancellationRequested();
            }
            var hits = new List<FindHit>();
            int idx = 0, total = 0;
            while ((idx = _docText.IndexOf(query, idx, StringComparison.Ordinal)) >= 0)
            {
                total++;
                if (hits.Count < maxHits)
                {
                    var ctxStart = Math.Max(0, idx - 12);
                    var ctxEnd = Math.Min(_docText.Length, idx + query.Length + 12);
                    hits.Add(new FindHit { StartOffset = idx, EndOffset = idx + query.Length, Context = _docText.Substring(ctxStart, ctxEnd - ctxStart) });
                }
                idx += query.Length;
            }
            return new FindResult { Hits = hits, TotalMatches = total };
        }

        public ReplaceResult Replace(string find, string replace, bool all, CancellationToken ct)
        {
            Calls.Add($"replace:{find}->{replace}");
            ReplaceGate?.Wait(10000);
            if (ReplaceDelayMs > 0) Thread.Sleep(ReplaceDelayMs);
            _docText = _docText.Replace(find, replace);
            return new ReplaceResult { ReplacementsMade = all ? 2 : 1, TotalMatches = 2 };
        }

        public StyleApplyResult ApplyStyle(string paragraphStyle, bool apply, CancellationToken ct)
        {
            Calls.Add($"applyStyle:{paragraphStyle}:{apply}");
            return new StyleApplyResult { ParagraphsStyled = 2, StyleName = paragraphStyle };
        }

        public void Save()
        {
            Calls.Add("save");
        }
    }

    /// <summary>
    /// 测试用网关客户端：原生 TCP + JSON Lines，带超时读。
    /// </summary>
    public sealed class GwClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly object _writeLock = new object();

        private GwClient(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
        }

        public static GwClient Connect(int port)
        {
            var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            return new GwClient(tcp);
        }

        public void SendRaw(string line)
        {
            lock (_writeLock)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
        }

        /// <summary>发送一条请求信封。</summary>
        public void Send(string id, string method, string paramsJson = "{}", int? deadlineMs = null, int version = 1)
        {
            var p = string.IsNullOrEmpty(paramsJson) ? "" : (paramsJson.StartsWith("{") ? paramsJson : "\"" + paramsJson.Replace("\"", "\\\"") + "\"");
            var dl = deadlineMs.HasValue ? $",\"deadlineMs\":{deadlineMs.Value}" : "";
            SendRaw($"{{\"version\":{version},\"id\":\"{id}\",\"method\":\"{method}\",\"params\":{p}{dl}}}");
        }

        /// <summary>读一行响应；timeoutMs 内无响应返回 null（模拟客户端放弃等待）。</summary>
        public string ReadLine(int timeoutMs = 5000)
        {
            var t = Task.Run(() => _reader.ReadLine());
            return t.Wait(timeoutMs) ? t.Result : null;
        }

        public bool Connected => _tcp.Connected;

        /// <summary>等待服务端断开连接（读返回 null/EOF）。</summary>
        public bool WaitClosed(int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!_tcp.Connected) return true;
                if (_tcp.Client.Poll(10, System.Net.Sockets.SelectMode.SelectRead) && _tcp.Available == 0) return true;
                Thread.Sleep(20);
            }
            return !_tcp.Connected;
        }

        public void Dispose()
        {
            try { _reader.Dispose(); } catch { }
            try { _tcp.Close(); } catch { }
        }
    }
}
