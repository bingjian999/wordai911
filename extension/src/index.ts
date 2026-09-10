/**
 * WordAI911 Pi 扩展入口（生产版）
 *
 * 生命周期：连接与后台资源在 session_start 建立、session_shutdown 关闭
 * （遵循 Pi 规范，避免无会话调用期占用资源）。
 */
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { WordGatewayClient } from "./gateway-client.js";
import { GatewayError } from "./errors.js";
import { registerWordTools } from "./tools.js";

export { WordGatewayClient } from "./gateway-client.js";
export { GatewayError, GATEWAY_CODES } from "./errors.js";
export { registerWordTools } from "./tools.js";
export { CLIENT_VERSION, PROTOCOL_VERSION } from "./gateway-client.js";

export default function (pi: ExtensionAPI) {
  const gw = new WordGatewayClient();

  pi.on("session_start", async (_event, ctx) => {
    try {
      await gw.ensureConnected();
      const s = gw.session;
      ctx.ui.notify(
        `Word 网关会话已建立（sessionId=${s ? s.sessionId.slice(0, 8) : "?"} capabilities=${s ? s.capabilities.length : 0}）`,
        "info"
      );
    } catch (e: any) {
      const code = e instanceof GatewayError ? e.code : "E_UNKNOWN";
      ctx.ui.notify(`Word 网关连接失败 [${code}]，工具调用时将自动重试`, "warning");
    }
  });

  pi.on("session_shutdown", () => {
    gw.close();
  });

  registerWordTools(pi, { gw });
}
