using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using WordAI.Agent.Ui;

namespace WordAI.Addin.Ui
{
    /// <summary>
    /// IUiBridge 的 WinForms 实现（通道 C 的宿主侧）。
    ///
    /// 当前骨架覆盖 confirm 类请求（高危工具 replace/save 的确认门禁）：
    /// 经主线程 SynchronizationContext 弹出模态 MessageBox。
    /// 正式版可替换为常驻任务窗格（Task Pane）以支持 select/input/progress，
    /// 并在超时后自动收起。
    /// </summary>
    public sealed class WinFormsUiBridge : IUiBridge
    {
        private readonly SynchronizationContext _ui;

        public bool IsAvailable => _ui != null;

        public WinFormsUiBridge(SynchronizationContext ui)
        {
            _ui = ui;
        }

        public Task<string> RequestUiAsync(string kind, string payload, int timeoutMs, CancellationToken ct)
        {
            if (_ui == null)
                throw new UiBridgeException("unavailable", "无 Word UI 线程上下文，无法呈现 UI 请求");
            if (kind != "confirm")
                throw new UiBridgeException("unavailable", "暂不支持的 UI 请求类型: " + kind);

            string title = "Word Agent 确认";
            string message = "";
            try
            {
                var j = JObject.Parse(payload ?? "{}");
                title = (string)j["title"] ?? title;
                message = (string)j["message"] ?? "";
            }
            catch
            {
                // payload 非法时按默认标题 + 原文呈现，不让确认门禁失效
                message = payload ?? "";
            }

            var tcs = new TaskCompletionSource<string>();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeoutMs > 0) linked.CancelAfter(timeoutMs);
                linked.Token.Register(() =>
                    tcs.TrySetException(new UiBridgeException("timeout", "用户响应超时")));
            }

            _ui.Post(_ =>
            {
                var dr = MessageBox.Show(message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                tcs.TrySetResult("{\"action\":\"confirm\",\"accepted\":"
                    + (dr == DialogResult.OK ? "true" : "false") + "}");
            }, null);

            return tcs.Task;
        }

        public void Dispose()
        {
            // MessageBox 无需释放；正式版换任务窗格时在此清理
        }
    }
}
