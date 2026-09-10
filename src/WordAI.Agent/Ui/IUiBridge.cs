using System;
using System.Threading;
using System.Threading.Tasks;

namespace WordAI.Agent.Ui
{
    /// <summary>
    /// 通道 C：Pi 扩展 ↔ C# 宿主的 UI 交互桥（见 docs/technical-plan.md 第三章通道 C）。
    ///
    /// 协议：extension_ui_request / extension_ui_response 消息对。
    /// Pi 侧通过工具发起 UI 请求（如让用户确认危险操作）；VSTO 宿主实现本接口，
    /// 把请求呈现在 Word 任务窗格 / 对话框上，用户动作序列化后回传。
    /// C# 侧无法呈现（无 UI 上下文，如自动化测试）时应拒绝而非挂起。
    /// </summary>
    public interface IUiBridge : IDisposable
    {
        /// <summary>
        /// 向用户呈现一个 UI 请求并等待响应。
        /// 返回值为 UI 动作的 JSON 序列化结果（如 {"action":"confirm","accepted":true}）。
        /// 用户取消 / 桥不可用时抛 <see cref="UiBridgeException"/>。
        /// </summary>
        /// <param name="kind">请求类型：confirm / select / input / progress。</param>
        /// <param name="payload">请求负载 JSON（title, message, options 等由 kind 决定）。</param>
        /// <param name="timeoutMs">用户响应超时（超时按拒绝处理）。</param>
        /// <param name="ct">协作取消。</param>
        Task<string> RequestUiAsync(string kind, string payload, int timeoutMs, CancellationToken ct);

        /// <summary>宿主是否具备呈现 UI 的能力（VSTO=true，无窗体上下文=false）。</summary>
        bool IsAvailable { get; }
    }

    /// <summary>UI 桥异常：用户拒绝、超时或无可用 UI 上下文。</summary>
    public sealed class UiBridgeException : Exception
    {
        /// <summary>机器可读原因：rejected / timeout / unavailable。</summary>
        public string Reason { get; }

        public UiBridgeException(string reason, string message) : base(message)
        {
            Reason = reason;
        }
    }
}
