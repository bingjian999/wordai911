using System;

namespace WordAI.Agent.Protocol
{
    /// <summary>通道 B 协议常量（见 docs/protocol-b.md）。</summary>
    public static class ProtocolConstants
    {
        /// <summary>当前消息信封版本。</summary>
        public const int ProtocolVersion = 1;

        /// <summary>网关默认监听端口（仅本机回环）。</summary>
        public const int DefaultPort = 47611;

        /// <summary>单行消息默认上限（字节），可经 <see cref="MaxLineEnvVar"/> 覆盖。</summary>
        public const int DefaultMaxLineBytes = 1024 * 1024;

        /// <summary>覆盖单行上限的环境变量名。</summary>
        public const string MaxLineEnvVar = "WORDAI_GATEWAY_MAX_LINE";

        /// <summary>连续未认证尝试的最大次数，超过即断开连接。</summary>
        public const int MaxUnauthAttempts = 3;

        /// <summary>通道 A（Pi 子进程 stdout）单行消息上限（字符数）。</summary>
        public const int MaxPiLineChars = 1024 * 1024;

        /// <summary>网关能力声明。</summary>
        public static readonly string[] Capabilities =
        {
            "word.doc.read", "word.doc.write", "cancel.cooperative", "deadline"
        };
    }

    /// <summary>通道 B 协议错误码（机器码，进 error.code；展示文案由客户端 UI 层负责）。</summary>
    public static class GatewayErrorCodes
    {
        public const int ParseError = -32700;      // JSON 解析失败
        public const int MethodNotFound = -32601;  // 方法不存在
        public const int AuthFailed = -32001;     // 未认证 / token 错误
        public const int MessageTooLong = -32002;  // 单行消息超长
        public const int VersionMismatch = -32003;// 协议版本不匹配
        public const int DeadlineExceeded = -32004;// deadline 超期
        public const int Cancelled = -32005;       // 任务被取消（协作取消或尚未开始即取消）
        public const int InternalError = -32603;  // 内部错误
    }
}
