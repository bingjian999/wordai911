using System;
using System.Linq;
using System.Text;
using WordAI.Agent.Protocol;
using Xunit;

namespace WordAI.Agent.Tests
{
    public class LineDecoderTests
    {
        [Fact]
        public void SplitsMultipleLinesInOneChunk()
        {
            var d = new LineDecoder(1000);
            var bytes = Encoding.UTF8.GetBytes("{\"a\":1}\n{\"a\":2}\n");
            var lines = d.Append(bytes, bytes.Length).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Equal("{\"a\":1}", lines[0]);
            Assert.Equal("{\"a\":2}", lines[1]);
        }

        [Fact]
        public void FramesAcrossChunkBoundaries()
        {
            var d = new LineDecoder(1000);
            var part1 = Encoding.UTF8.GetBytes("{\"id\":\"c1\",\"met");
            var part2 = Encoding.UTF8.GetBytes("hod\":\"x\"}\n");

            var l1 = d.Append(part1, part1.Length).ToList();
            Assert.Empty(l1);
            Assert.True(d.BufferedLength > 0);

            var l2 = d.Append(part2, part2.Length).ToList();
            Assert.Single(l2);
            Assert.Equal("{\"id\":\"c1\",\"method\":\"x\"}", l2[0]);
            Assert.Equal(0, d.BufferedLength);
        }

        [Fact]
        public void HandlesUtf8MultibyteAcrossBoundary()
        {
            var d = new LineDecoder(1000);
            // “中” = 3 字节，故意在中间切断
            var full = Encoding.UTF8.GetBytes("中文行\n");
            var p1 = new byte[] { full[0], full[1] };
            var rest = new byte[full.Length - 2];
            Array.Copy(full, 2, rest, 0, rest.Length);

            Assert.Empty(d.Append(p1, p1.Length).ToList());
            var lines = d.Append(rest, rest.Length).ToList();
            Assert.Single(lines);
            Assert.Equal("中文行", lines[0]);
        }

        [Fact]
        public void ThrowsWhenLineExceedsLimit()
        {
            var d = new LineDecoder(16);
            var big = Encoding.UTF8.GetBytes(new string('x', 100));
            // 无换行持续累积 → 超限
            Assert.Throws<LineDecoder.LineTooLongException>(() => d.Append(big, big.Length));
        }

        [Fact]
        public void IgnoresCarriageReturnAsPartOfLine()
        {
            var d = new LineDecoder(1000);
            var bytes = Encoding.UTF8.GetBytes("{\"a\":1}\r\n");
            var lines = d.Append(bytes, bytes.Length).ToList();
            Assert.Single(lines);
            Assert.Equal("{\"a\":1}\r", lines[0]); // \r 归属行内容，由 JSON 解析容错
        }
    }

    public class GatewayMessageTests
    {
        [Fact]
        public void TryParse_ValidRequest()
        {
            var json = "{\"version\":1,\"id\":\"r1\",\"method\":\"session.echo\",\"params\":{\"x\":1},\"deadlineMs\":500}";
            var ok = GatewayRequest.TryParse(json, out var req, out var err);
            Assert.True(ok);
            Assert.Null(err);
            Assert.Equal("r1", req.Id);
            Assert.Equal("session.echo", req.Method);
            Assert.Equal(1, req.Version);
            Assert.Equal(500, req.DeadlineMs);
            Assert.Equal(1, (int)req.Params["x"]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("{\"id\":\"r1\"}")]           // 缺 method
        [InlineData("{\"method\":\"x\"}")]          // 缺 id
        [InlineData("[1,2,3]")]                    // 非对象
        public void TryParse_BadInput_ReturnsParseError(string json)
        {
            var ok = GatewayRequest.TryParse(json, out var req, out var err);
            Assert.False(ok);
            Assert.Null(req);
            Assert.NotNull(err);
            Assert.Equal(GatewayErrorCodes.ParseError, err.Code);
        }

        [Fact]
        public void Response_ToLine_RoundTrip()
        {
            var resp = new GatewayResponse { Id = "r9", Result = new Newtonsoft.Json.Linq.JValue(42) };
            var line = resp.ToLine().TrimEnd('\n');
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(line);
            Assert.Equal("r9", (string)parsed["id"]);
            Assert.Equal(42, (int)parsed["result"]);
            Assert.Null(parsed["error"]);
        }

        [Fact]
        public void Response_ToLine_ErrorShape()
        {
            var resp = new GatewayResponse { Id = "r9", Error = GatewayError.Cancelled() };
            var line = resp.ToLine().TrimEnd('\n');
            Assert.EndsWith("\n", resp.ToLine()); // JSON Lines 必须以换行结尾
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(line);
            Assert.Equal(GatewayErrorCodes.Cancelled, (int)parsed["error"]["code"]);
            Assert.Equal("cancelled", (string)parsed["error"]["message"]);
        }

        [Fact]
        public void ErrorCodes_AreStable()
        {
            // 机器码是协议契约，禁止漂移
            Assert.Equal(-32700, GatewayErrorCodes.ParseError);
            Assert.Equal(-32601, GatewayErrorCodes.MethodNotFound);
            Assert.Equal(-32603, GatewayErrorCodes.InternalError);
            Assert.Equal(-32001, GatewayErrorCodes.AuthFailed);
            Assert.Equal(-32002, GatewayErrorCodes.MessageTooLong);
            Assert.Equal(-32003, GatewayErrorCodes.VersionMismatch);
            Assert.Equal(-32004, GatewayErrorCodes.DeadlineExceeded);
            Assert.Equal(-32005, GatewayErrorCodes.Cancelled);
        }
    }
}
