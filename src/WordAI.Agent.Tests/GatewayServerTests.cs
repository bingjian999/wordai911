using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WordAI.Agent.Gateway;
using Xunit;

namespace WordAI.Agent.Tests
{
    /// <summary>
    /// 网关端到端测试（真 TCP + JSON Lines + FakeWordHost），
    /// 与 docs/protocol-b.md 及技术方案第四章测试表一一对应。
    /// </summary>
    public class GatewayServerTests : IDisposable
    {
        private readonly FakeWordHost _host = new FakeWordHost();
        private readonly WordGatewayServer _server;
        private readonly int _port;
        private static readonly object LogLock = new object();
        internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> LogLines =
            new System.Collections.Concurrent.ConcurrentQueue<string>();

        public GatewayServerTests()
        {
            // 动态取空闲端口：pid 派生的固定端口在共享沙箱里易与其它监听服务冲突
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            _port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _server = WordGatewayServer.Start(_port, "test-token", _host, serverVersion: "test/1.0.0",
                log: m => { lock (LogLock) { System.IO.File.AppendAllText("/tmp/wordai-test.log", m + "\n"); } LogLines.Enqueue(m); });
        }

        private GwClient Authed()
        {
            var c = GwClient.Connect(_port);
            c.Send("a1", "auth", "{\"token\":\"test-token\",\"protocolVersion\":1,\"clientVersion\":\"test\"}");
            var resp = JObject.Parse(c.ReadLine());
            Assert.True((bool)resp["result"]["authed"]);
            return c;
        }

        public void Dispose() => _server.Dispose();

        // ---- 认证与会话 ----

        [Fact]
        public void Auth_Success_EstablishesSession()
        {
            using (var c = GwClient.Connect(_port))
            {
                c.Send("a1", "auth", "{\"token\":\"test-token\",\"protocolVersion\":1,\"clientVersion\":\"ext/0.85.1\"}");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Null(resp["error"]);
                var r = resp["result"];
                Assert.True((bool)r["authed"]);
                Assert.Equal(1, (int)r["protocolVersion"]);
                Assert.Equal("test/1.0.0", (string)r["serverVersion"]);
                Assert.Equal("ext/0.85.1", (string)r["clientVersion"]);
                Assert.NotEmpty((string)r["sessionId"]);
                Assert.Contains("word.doc.read", (JArray)r["capabilities"]);
            }
        }

        [Fact]
        public void Auth_WrongToken_RejectedThenAllowed()
        {
            using (var c = GwClient.Connect(_port))
            {
                c.Send("a1", "auth", "{\"token\":\"WRONG\",\"protocolVersion\":1}");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Equal(-32001, (int)resp["error"]["code"]);

                // token 错误不算最终失败：正确 token 仍可认证
                c.Send("a2", "auth", "{\"token\":\"test-token\",\"protocolVersion\":1}");
                Assert.True((bool)JObject.Parse(c.ReadLine())["result"]["authed"]);
            }
        }

        [Fact]
        public void Request_BeforeAuth_Gets32001()
        {
            using (var c = GwClient.Connect(_port))
            {
                c.Send("r1", "session.echo", "{\"x\":1}");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Equal("r1", (string)resp["id"]);
                Assert.Equal(-32001, (int)resp["error"]["code"]);
            }
        }

        [Fact]
        public void ThreeUnauthAttempts_DisconnectClient()
        {
            using (var c = GwClient.Connect(_port))
            {
                for (int i = 0; i < 3; i++)
                {
                    c.Send("u" + i, "session.echo");
                    var resp = JObject.Parse(c.ReadLine());
                    Assert.Equal(-32001, (int)resp["error"]["code"]);
                }
                Assert.True(c.WaitClosed(), "连续 3 次未认证后服务端应断开连接");
            }
        }

        [Fact]
        public void Auth_VersionMismatch_ClosesConnection()
        {
            using (var c = GwClient.Connect(_port))
            {
                c.Send("a1", "auth", "{\"token\":\"test-token\",\"protocolVersion\":2}");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Equal(-32003, (int)resp["error"]["code"]);
                Assert.True(c.WaitClosed(), "版本不匹配应断开连接");
            }
        }

        [Fact]
        public void BusinessRequest_WrongEnvelopeVersion_Gets32003()
        {
            using (var c = Authed())
            {
                c.Send("r1", "session.echo", "{\"x\":1}", version: 3);
                var resp = JObject.Parse(c.ReadLine());
                Assert.Equal(-32003, (int)resp["error"]["code"]);
            }
        }

        // ---- 业务方法 ----

        [Fact]
        public void SessionEcho_RoundTrips()
        {
            using (var c = Authed())
            {
                c.Send("e1", "session.echo", "{\"hello\":\"world\",\"n\":42}");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Equal("e1", (string)resp["id"]);
                var r = resp["result"];
                Assert.True((bool)r["echoed"]);
                Assert.Equal("world", (string)r["hello"]);
                Assert.Equal(42, (int)r["n"]);
            }
        }

        [Fact]
        public void WordInsertText_CallsHost()
        {
            using (var c = Authed())
            {
                c.Send("i1", "word.insertText", "{\"text\":\"插入测试\",\"position\":\"documentEnd\"}");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Null(resp["error"]);
                Assert.Equal(4, (int)resp["result"]["charactersInserted"]);
                Assert.Contains(_host.Calls, s => s.Contains("insertText@DocumentEnd:插入测试"));
            }
        }

        [Fact]
        public void WordFind_ReturnsHits()
        {
            using (var c = Authed())
            {
                c.Send("f1", "word.find", "{\"query\":\"hello\",\"maxHits\":5}");
                var resp = JObject.Parse(c.ReadLine());
                var r = resp["result"];
                Assert.Equal(2, (int)r["totalMatches"]);
                Assert.Equal(2, ((JArray)r["hits"]).Count);
            }
        }

        [Fact]
        public void UnknownMethod_Gets32601()
        {
            using (var c = Authed())
            {
                c.Send("x1", "word.nonexistent");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Equal(-32601, (int)resp["error"]["code"]);
                Assert.Contains("word.nonexistent", (string)resp["error"]["message"]);
            }
        }

        [Fact]
        public void GarbageLine_GetsParseError_ThenStillWorks()
        {
            using (var c = Authed())
            {
                c.SendRaw("<<<this is not json>>>");
                var resp = JObject.Parse(c.ReadLine());
                Assert.Equal(-32700, (int)resp["error"]["code"]);

                // 解析错误不断开：下一条正常处理
                c.Send("e2", "session.echo", "{\"ok\":1}");
                Assert.True((bool)JObject.Parse(c.ReadLine())["result"]["echoed"]);
            }
        }

        // ---- 取消三段语义 ----

        [Fact]
        public async Task Cancel_InFlightCooperative_NoResponse()
        {
            _host.FindBlocksUntilCancelled = true;
            using (var c = Authed())
            {
                c.Send("cf1", "word.find", "{\"query\":\"hello\"}");
                await Task.Delay(150); // 确保 find 已开始并阻塞
                c.Send("c1", "cancel", "{\"id\":\"cf1\"}");

                // 协作取消：find 内部抛 OperationCanceledException → 网关不回包
                var resp = c.ReadLine(1200);
                Assert.Null(resp);
            }
        }

        [Fact]
        public async Task Cancel_BeforeQueuedExecution_Skipped()
        {
            // 确定性挡住 COM 队列：q1 执行直到 gate 放行
            _host.ReplaceGate = new System.Threading.ManualResetEventSlim(false);
            using (var c = Authed())
            {
                // 先占住 COM 串行队列
                c.Send("q1", "word.replace", "{\"find\":\"a\",\"replace\":\"b\"}");
                await WaitUntil(() => _host.Calls.Exists(s => s.StartsWith("replace:")), 2000);

                // q2 排队中（尚未开始）
                c.Send("q2", "word.replace", "{\"find\":\"c\",\"replace\":\"d\"}");
                await Task.Delay(100); // 确保 q2 已进入 COM 等待队列
                c.Send("c1", "cancel", "{\"id\":\"q2\"}");

                // 确认网关已处理 cancel（单核沙箱下读循环续体可能晚于信号量排队者，不能靠墙钟计时）
                await WaitUntil(() => ContainsLog("cancel requested for in-flight q2") || ContainsLog("cancel accepted for q2"), 3000);

                _host.ReplaceGate.Set(); // 放行 q1

                var r1 = JObject.Parse(c.ReadLine(5000)); // q1 正常完成
                Assert.Null(r1["error"]);

                // q2 被取消：排队阶段跳过、不回包
                var r2 = c.ReadLine(1500);
                Assert.Null(r2);
            }
        }

        private static async Task WaitUntil(Func<bool> cond, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(20);
            Assert.True(cond(), "condition not met within " + timeoutMs + "ms");
        }

        private static bool ContainsLog(string text)
        {
            foreach (var l in LogLines)
                if (l.Contains(text)) return true;
            return false;
        }

        [Fact]
        public void Cancel_NonInterruptible_ResultDiscarded()
        {
            using (var c = Authed())
            {
                // slowEcho 模拟不可中断 COM 调用：800ms 后完成
                c.Send("s1", "session.slowEcho", "{\"delayMs\":800,\"tag\":\"slow\"}");
                c.Send("c1", "cancel", "{\"id\":\"s1\"}");

                // 不可中断：执行完也不回包（丢弃）
                var resp = c.ReadLine(2500);
                Assert.Null(resp);
            }
        }

        [Fact]
        public void Cancel_UnknownId_Harmless()
        {
            using (var c = Authed())
            {
                c.Send("c1", "cancel", "{\"id\":\"nonexistent\"}");
                // 通知无响应；连接仍可用
                c.Send("e1", "session.echo", "{\"ok\":1}");
                Assert.True((bool)JObject.Parse(c.ReadLine())["result"]["echoed"]);
            }
        }

        // ---- deadline ----

        [Fact]
        public void DeadlineExceeded_Gets32004()
        {
            using (var c = Authed())
            {
                c.Send("d1", "session.slowEcho", "{\"delayMs\":600}", deadlineMs: 200);
                var resp = JObject.Parse(c.ReadLine(3000));
                Assert.Equal("d1", (string)resp["id"]);
                Assert.Equal(-32004, (int)resp["error"]["code"]);
            }
        }

        [Fact]
        public void DeadlineNotExceeded_NormalResponse()
        {
            using (var c = Authed())
            {
                c.Send("d2", "session.slowEcho", "{\"delayMs\":100}", deadlineMs: 5000);
                var resp = JObject.Parse(c.ReadLine(3000));
                Assert.Null(resp["error"]);
                Assert.True((bool)resp["result"]["echoed"]);
            }
        }

        // ---- 单行上限 ----

        [Fact]
        public void OversizedLine_Gets32002_AndDisconnect()
        {
            using (var c = Authed())
            {
                // 小上限不可配置于已启动实例，此处发送 2MB 行触发默认 1MB 上限
                var big = new string('y', 2 * 1024 * 1024);
                c.SendRaw("{\"version\":1,\"id\":\"big1\",\"method\":\"session.echo\",\"params\":{\"text\":\"" + big + "\"}}");
                var resp = JObject.Parse(c.ReadLine(3000));
                Assert.Equal(-32002, (int)resp["error"]["code"]);
                Assert.True(c.WaitClosed(), "超长行应回 -32002 后断开连接");
            }
        }

        // ---- 并发 / 串行队列 ----

        [Fact]
        public async Task WordCalls_AreSerialized()
        {
            using (var c = Authed())
            {
                for (int i = 0; i < 10; i++)
                    c.Send("p" + i, "word.insertText", "{\"text\":\"t" + i + "\",\"position\":\"documentEnd\"}");
                for (int i = 0; i < 10; i++)
                {
                    var resp = JObject.Parse(c.ReadLine(5000));
                    Assert.Null(resp["error"]);
                }
                // COM 串行队列保证 insertText 全部落到 host（单线程语义）
                Assert.Equal(10, _host.Calls.FindAll(s => s.StartsWith("insertText")).Count);
                await Task.CompletedTask;
            }
        }

        [Fact]
        public void MultipleConcurrentRequests_AllGetResponses()
        {
            using (var c = Authed())
            using (var c2 = Authed())
            {
                c.Send("m1", "session.echo", "{\"who\":\"c1\"}");
                c2.Send("m2", "session.echo", "{\"who\":\"c2\"}");
                var r1 = JObject.Parse(c.ReadLine());
                var r2 = JObject.Parse(c2.ReadLine());
                Assert.Equal("c1", (string)r1["result"]["who"]);
                Assert.Equal("c2", (string)r2["result"]["who"]);
            }
        }

        [Fact]
        public void SystemInfo_ReportsSession()
        {
            using (var c = Authed())
            {
                c.Send("si1", "system.info");
                var resp = JObject.Parse(c.ReadLine());
                var r = resp["result"];
                Assert.Equal("test/1.0.0", (string)r["serverVersion"]);
                Assert.Equal(1, (int)r["protocolVersion"]);
                Assert.NotEmpty((string)r["sessionId"]);
            }
        }
    }
}
