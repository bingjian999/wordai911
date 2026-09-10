/**
 * word_* 工具注册（typebox schema，逐个映射到网关方法）
 *
 * 6 个工具覆盖网关全部 7 个 word.* 方法：
 *  - word_document    -> word.getDocumentInfo / word.getSelection（读操作合并）
 *  - word_insert_text -> word.insertText
 *  - word_find        -> word.find
 *  - word_replace     -> word.replace（高危：执行前 ctx.ui.confirm 强制确认）
 *  - word_apply_style -> word.applyStyle
 *  - word_save        -> word.save（高危：执行前 ctx.ui.confirm 强制确认）
 *
 * 每个工具同时配套一个 /word.xxx 扩展命令（不走 LLM），宿主可随时自检全链路健康。
 */
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import { WordGatewayClient } from "./gateway-client.js";
import { GatewayError } from "./errors.js";

export interface WordToolDeps {
  gw: WordGatewayClient;
}

/** 工具结果统一为 JSON 文本；失败时返回 { error: { code, display } }，不抛异常 */
function textResult(data: unknown): { type: "text"; text: string } {
  return { type: "text", text: JSON.stringify(data, null, 2) };
}

function errorResult(e: unknown, method: string) {
  const code = e instanceof GatewayError ? e.code : "E_UNKNOWN";
  const display = e instanceof GatewayError ? e.display : String((e as any)?.message || e);
  return {
    content: [textResult({ error: { code, display } })],
    details: { method, failed: true },
  };
}

export function registerWordTools(pi: ExtensionAPI, deps: WordToolDeps): void {
  const { gw } = deps;

  // ---- 1. word_document：文档信息 / 当前选区（读操作） ----
  pi.registerTool({
    name: "word_document",
    label: "Word 文档",
    description:
      "查询当前 Word 文档状态。action=getInfo 返回文件名、段落数、字数、保存状态等；" +
      "action=getSelection 返回当前选区文本与位置偏移。只读操作，无副作用。",
    parameters: Type.Object({
      action: Type.Union([Type.Literal("getInfo"), Type.Literal("getSelection")], {
        description: "getInfo=文档信息；getSelection=当前选区",
      }),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, _ctx) {
      const method = params.action === "getInfo" ? "word.getDocumentInfo" : "word.getSelection";
      try {
        const result = await gw.call(method, params.action === "getInfo" ? {} : {});
        return { content: [textResult(result)], details: { method } };
      } catch (e) {
        return errorResult(e, method);
      }
    },
  });

  // ---- 2. word_insert_text：插入文本 ----
  pi.registerTool({
    name: "word_insert_text",
    label: "Word 插入文本",
    description:
      "在 Word 文档中插入文本。position=selection 插入到当前光标/选区处（替换选区），" +
      "documentEnd 追加到文档末尾，documentStart 插入到文档开头。",
    parameters: Type.Object({
      text: Type.String({ description: "要插入的文本（支持 \\n 换行）" }),
      position: Type.Optional(
        Type.Union(
          [Type.Literal("selection"), Type.Literal("documentEnd"), Type.Literal("documentStart")],
          { description: "插入位置，默认 selection" }
        )
      ),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, _ctx) {
      const method = "word.insertText";
      try {
        const result = await gw.call(method, {
          text: params.text,
          position: params.position || "selection",
        });
        return { content: [textResult(result)], details: { method } };
      } catch (e) {
        return errorResult(e, method);
      }
    },
  });

  // ---- 3. word_find：查找 ----
  pi.registerTool({
    name: "word_find",
    label: "Word 查找",
    description:
      "在文档中查找文本，返回命中位置偏移与上下文片段。长操作前优先用本工具定位，再局部操作。",
    parameters: Type.Object({
      query: Type.String({ description: "查找文本" }),
      maxHits: Type.Optional(
        Type.Integer({ minimum: 1, maximum: 500, description: "最大命中数，默认 50" })
      ),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, _ctx) {
      const method = "word.find";
      try {
        const result = await gw.call(method, { query: params.query, maxHits: params.maxHits ?? 50 }, 30000);
        return { content: [textResult(result)], details: { method } };
      } catch (e) {
        return errorResult(e, method);
      }
    },
  });

  // ---- 4. word_replace：替换（高危，confirm 强制确认） ----
  pi.registerTool({
    name: "word_replace",
    label: "Word 替换",
    description:
      "在文档中查找并替换文本（高危写操作，执行前会向用户确认）。all=true 替换全部命中，否则仅替换首个。",
    parameters: Type.Object({
      find: Type.String({ description: "查找文本" }),
      replace: Type.String({ description: "替换文本" }),
      all: Type.Optional(Type.Boolean({ description: "是否替换全部命中，默认 true" })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
      const method = "word.replace";
      try {
        const scope = params.all !== false ? "全部" : "首个";
        const ok = await ctx.ui.confirm(
          "Word 替换确认",
          `将替换「${params.find}」→「${params.replace}」（${scope}），是否继续？`
        );
        if (!ok) {
          return {
            content: [textResult({ cancelled: true, reason: "用户拒绝替换操作" })],
            details: { method, cancelled: true },
          };
        }
        const result = await gw.call(method, {
          find: params.find,
          replace: params.replace,
          all: params.all !== false,
        });
        return { content: [textResult(result)], details: { method } };
      } catch (e) {
        return errorResult(e, method);
      }
    },
  });

  // ---- 5. word_apply_style：段落样式 ----
  pi.registerTool({
    name: "word_apply_style",
    label: "Word 段落样式",
    description: "对当前选区所在段落应用（或清除）段落样式，如 Heading 1 / Normal。",
    parameters: Type.Object({
      paragraphStyle: Type.String({ description: "段落样式名，如 'Heading 1'、'Normal'" }),
      apply: Type.Optional(Type.Boolean({ description: "true=应用（默认），false=清除样式" })),
    }),
    async execute(_toolCallId, params, _signal, _onUpdate, _ctx) {
      const method = "word.applyStyle";
      try {
        const result = await gw.call(method, {
          paragraphStyle: params.paragraphStyle,
          apply: params.apply !== false,
        });
        return { content: [textResult(result)], details: { method } };
      } catch (e) {
        return errorResult(e, method);
      }
    },
  });

  // ---- 6. word_save：保存（高危，confirm 强制确认） ----
  pi.registerTool({
    name: "word_save",
    label: "Word 保存",
    description: "保存当前 Word 文档（高危写操作，执行前会向用户确认）。",
    parameters: Type.Object({}),
    async execute(_toolCallId, _params, _signal, _onUpdate, ctx) {
      const method = "word.save";
      try {
        const ok = await ctx.ui.confirm("Word 保存确认", "是否保存当前文档？");
        if (!ok) {
          return {
            content: [textResult({ cancelled: true, reason: "用户拒绝保存操作" })],
            details: { method, cancelled: true },
          };
        }
        const result = await gw.call(method, {});
        return { content: [textResult(result)], details: { method } };
      } catch (e) {
        return errorResult(e, method);
      }
    },
  });

  // ---- 测试后门命令（不走 LLM，宿主自检全链路健康） ----
  const commands: Array<[string, string, () => Promise<any>]> = [
    ["/word.ping", "连通性自检", () => gw.call("word.getDocumentInfo", {})],
    [
      "/word.insert",
      "插入文本自检",
      () => gw.call("word.insertText", { text: "hello", position: "documentEnd" }),
    ],
    ["/word.find", "查找自检", () => gw.call("word.find", { query: "hello" })],
  ];
  for (const [name, description, fn] of commands) {
    pi.registerCommand(name, {
      description,
      handler: async (_args, ctx) => {
        try {
          const r = await fn();
          ctx.ui.notify(`${name}: ${JSON.stringify(r)}`, "info");
        } catch (e: any) {
          const code = e instanceof GatewayError ? e.code : "E_UNKNOWN";
          ctx.ui.notify(`${name} 失败 [${code}]`, "warning");
        }
      },
    });
  }
}
