using System;
using System.Windows.Forms;
using Microsoft.Office.Core;

namespace WordAI.Addin
{
    /// <summary>
    /// 功能区（Ribbon XML 方式）：「WordAI」选项卡 + 网关状态按钮。
    /// ThisAddIn 实现 IRibbonExtensibility 后，VSTO 运行时会自动把
    /// RequestService 路由到本类，无需手写服务 GUID。
    /// </summary>
    public partial class ThisAddIn : IRibbonExtensibility
    {
        private const string RibbonXml = @"<customUI xmlns=""http://schemas.microsoft.com/office/2009/07/customui"">
  <ribbon>
    <tabs>
      <tab id=""wordaiTab"" label=""WordAI"">
        <group id=""wordaiStatusGroup"" label=""Agent 状态"">
          <button id=""wordaiGatewayStatus"" label=""网关状态"" size=""large""
                  screentip=""显示通道 B 网关运行状态与端口""
                  onAction=""OnGatewayStatus"" />
        </group>
      </tab>
    </tabs>
  </ribbon>
</customUI>";

        public string GetCustomUI(string ribbonId)
        {
            return ribbonId == "Microsoft.Word.Document" ? RibbonXml : null;
        }

        /// <summary>网关状态按钮回调（Ribbon 回调在 Word 主线程执行）。</summary>
        public void OnGatewayStatus(IRibbonControl control)
        {
            bool running = _gateway != null;
            string tokenPreview = _token == null ? "-" : _token.Substring(0, 8) + "…";
            MessageBox.Show(
                "通道 B 网关：" + (running ? "运行中" : "已停止") + Environment.NewLine +
                "监听地址：127.0.0.1:" + GatewayPort + Environment.NewLine +
                "本会话 token（前 8 位）：" + tokenPreview + Environment.NewLine +
                "Pi 子进程（通道 A）：" + (_piRuntime != null && _piRuntime.IsRunning ? "运行中" : "未启动"),
                "WordAI Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
