/**
 * 错误码与展示文案分离（评审 A 级意见）：
 *  - code：稳定机器码，进 Error.message / 日志 / 程序判断 / i18n key
 *  - display：面向用户的展示文案，仅在 UI 层使用，永远不进 Error.message
 */
export class GatewayError extends Error {
  constructor(
    public readonly code: string,
    public readonly display: string,
    public readonly gatewayCode?: number
  ) {
    super(code); // Error.message 只含机器码
    this.name = "GatewayError";
  }
}

/** 网关侧错误码（协议 B 稳定错误码，与 C# WordAI.Agent 一致） */
export const GATEWAY_CODES = {
  PARSE_ERROR: -32700,
  METHOD_NOT_FOUND: -32601,
  INTERNAL: -32603,
  UNAUTHENTICATED: -32001,
  TOO_LONG: -32002,
  BAD_VERSION: -32003,
  DEADLINE_EXCEEDED: -32004,
  CANCELLED: -32005,
} as const;
