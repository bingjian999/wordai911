using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WordAI.Agent.Protocol
{
    /// <summary>请求信封（v1）。</summary>
    public sealed class GatewayRequest
    {
        [JsonProperty("version")] public int Version;
        [JsonProperty("id")] public string Id;
        [JsonProperty("method")] public string Method;
        [JsonProperty("params")] public JObject Params;
        [JsonProperty("deadlineMs", NullValueHandling = NullValueHandling.Ignore)]
        public int? DeadlineMs;

        public static bool TryParse(string json, out GatewayRequest req, out GatewayError err)
        {
            req = null;
            err = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                err = GatewayError.Parse("empty message");
                return false;
            }
            try
            {
                req = JsonConvert.DeserializeObject<GatewayRequest>(json);
                if (req == null || string.IsNullOrEmpty(req.Id) || string.IsNullOrEmpty(req.Method))
                {
                    req = null;
                    err = GatewayError.Parse("missing id or method");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                err = GatewayError.Parse(ex.Message);
                return false;
            }
        }
    }

    /// <summary>响应 / 通知（JSON Lines 单行）。</summary>
    public sealed class GatewayResponse
    {
        [JsonProperty("id")] public string Id;

        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Result;

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public GatewayError Error;

        public string ToLine()
        {
            var o = new JObject { ["id"] = Id };
            if (Error != null) o["error"] = JObject.FromObject(Error);
            else o["result"] = Result ?? JValue.CreateNull();
            return JsonConvert.SerializeObject(o, Formatting.None) + "\n";
        }
    }

    /// <summary>协议错误对象（错误码与文案；code 为稳定机器码）。</summary>
    public sealed class GatewayError
    {
        [JsonProperty("code")] public int Code;
        [JsonProperty("message")] public string Message;

        public GatewayError() { }

        public GatewayError(int code, string message)
        {
            Code = code;
            Message = message ?? string.Empty;
        }

        public static GatewayError Parse(string msg) => new GatewayError(GatewayErrorCodes.ParseError, msg);
        public static GatewayError MethodNotFound(string method) => new GatewayError(GatewayErrorCodes.MethodNotFound, "method not found: " + method);
        public static GatewayError AuthFailed() => new GatewayError(GatewayErrorCodes.AuthFailed, "authentication required (AUTH first)");
        public static GatewayError VersionMismatch(int client, int server) => new GatewayError(GatewayErrorCodes.VersionMismatch, $"protocol version mismatch: client={client} server={server}");
        public static GatewayError TooLong() => new GatewayError(GatewayErrorCodes.MessageTooLong, "message exceeds single-line limit");
        public static GatewayError Deadline() => new GatewayError(GatewayErrorCodes.DeadlineExceeded, "deadline exceeded");
        public static GatewayError Cancelled() => new GatewayError(GatewayErrorCodes.Cancelled, "cancelled");
        public static GatewayError Internal(string msg) => new GatewayError(GatewayErrorCodes.InternalError, msg);
    }
}
