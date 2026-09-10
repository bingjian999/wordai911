/**
 * word_* 工具层测试（fake pi 注入，验证工具->网关调用映射与 confirm 门禁）
 * 运行：node --test test/（需先 npm run build）
 */
import test from "node:test";
import assert from "node:assert/strict";
import { startMockGateway } from "../mock/mock-gateway.mjs";
import { registerWordTools } from "../dist/tools.js";
import { WordGatewayClient } from "../dist/gateway-client.js";

const TOKEN = "test-token";

/** fake pi：捕获注册的工具与命令 */
function makeFakePi() {
  const tools = new Map();
  const commands = new Map();
  const handlers = new Map();
  return {
    tools,
    commands,
    handlers,
    registerTool: (t) => tools.set(t.name, t),
    registerCommand: (name, c) => commands.set(name, c),
    on: (event, h) => handlers.set(event, h),
  };
}

/** fake ctx：ui.confirm 可编程 */
function makeFakeCtx({ confirmResult = true } = {}) {
  const confirms = [];
  const notifies = [];
  return {
    confirms,
    notifies,
    ui: {
      confirm: async (title, message) => {
        confirms.push({ title, message });
        return confirmResult;
      },
      notify: (msg, level) => notifies.push({ msg, level }),
    },
  };
}

function parseResult(r) {
  return JSON.parse(r.content[0].text);
}

async function withSetup(fn, opts = {}) {
  const server = await startMockGateway({ token: TOKEN });
  const port = (server.address()).port;
  const gw = new WordGatewayClient({ port, token: TOKEN });
  const pi = makeFakePi();
  registerWordTools(pi, { gw });
  try {
    await fn({ server, gw, pi });
  } finally {
    gw.close();
    await new Promise((r) => server.close(r));
  }
}

test("注册 6 个 word_* 工具 + 测试后门命令", async () => {
  await withSetup(({ pi }) => {
    assert.deepEqual(
      [...pi.tools.keys()].sort(),
      [
        "word_apply_style",
        "word_document",
        "word_find",
        "word_insert_text",
        "word_replace",
        "word_save",
      ]
    );
    assert.ok(pi.commands.size >= 3);
    for (const name of ["/word.ping", "/word.insert", "/word.find"]) {
      assert.ok(pi.commands.has(name), name + " 应存在");
    }
  });
});

test("word_document getInfo/getSelection 映射到正确网关方法", async () => {
  await withSetup(async ({ pi }) => {
    const t = pi.tools.get("word_document");
    const r1 = await t.execute("tc1", { action: "getInfo" }, null, null, makeFakeCtx());
    assert.equal(parseResult(r1).fileName, "测试文档.docx");
    const r2 = await t.execute("tc2", { action: "getSelection" }, null, null, makeFakeCtx());
    assert.equal(parseResult(r2).styleName, "Normal");
  });
});

test("word_insert_text 传递 position 并落库到 mock 文档", async () => {
  await withSetup(async ({ pi, server }) => {
    const t = pi.tools.get("word_insert_text");
    const r = await t.execute(
      "tc3",
      { text: "新段落内容", position: "documentEnd" },
      null,
      null,
      makeFakeCtx()
    );
    assert.equal(parseResult(r).charactersInserted, 5);
    assert.deepEqual(server.doc.paragraphs.slice(-1), ["新段落内容"]);
  });
});

test("word_find 返回命中与上下文", async () => {
  await withSetup(async ({ pi }) => {
    const t = pi.tools.get("word_find");
    const r = await t.execute("tc4", { query: "hello", maxHits: 10 }, null, null, makeFakeCtx());
    const parsed = parseResult(r);
    assert.equal(parsed.totalMatches, 1);
    assert.ok(parsed.hits[0].context.includes("hello"));
  });
});

test("word_replace 高危：confirm 拒绝则不调用网关", async () => {
  await withSetup(async ({ pi, server }) => {
    const t = pi.tools.get("word_replace");
    const ctx = makeFakeCtx({ confirmResult: false });
    const r = await t.execute("tc5", { find: "hello", replace: "hi", all: true }, null, null, ctx);
    assert.equal(parseResult(r).cancelled, true);
    assert.equal(ctx.confirms.length, 1, "应恰好触发一次 confirm");
    assert.ok(!server.doc.fullText().includes("hi"), "拒绝后文档不变");
  });
});

test("word_replace 高危：confirm 同意后执行替换", async () => {
  await withSetup(async ({ pi, server }) => {
    const t = pi.tools.get("word_replace");
    const ctx = makeFakeCtx({ confirmResult: true });
    const r = await t.execute("tc6", { find: "hello", replace: "你好", all: true }, null, null, ctx);
    assert.equal(parseResult(r).replacementsMade, 1);
    assert.ok(server.doc.fullText().includes("你好"));
  });
});

test("word_save 高危：confirm 门禁生效", async () => {
  await withSetup(async ({ pi, server }) => {
    const t = pi.tools.get("word_save");
    server.doc.unsaved = true;

    const ctxNo = makeFakeCtx({ confirmResult: false });
    const r1 = await t.execute("tc7", {}, null, null, ctxNo);
    assert.equal(parseResult(r1).cancelled, true);
    assert.equal(server.doc.unsaved, true, "拒绝后不保存");

    const ctxYes = makeFakeCtx({ confirmResult: true });
    const r2 = await t.execute("tc8", {}, null, null, ctxYes);
    assert.deepEqual(parseResult(r2), { saved: true });
    assert.equal(server.doc.unsaved, false);
  });
});

test("word_apply_style 传递样式参数", async () => {
  await withSetup(async ({ pi }) => {
    const t = pi.tools.get("word_apply_style");
    const r = await t.execute("tc9", { paragraphStyle: "Heading 2" }, null, null, makeFakeCtx());
    assert.equal(parseResult(r).styleName, "Heading 2");
  });
});

test("网关不可用时工具返回错误对象而非抛出异常", async () => {
  // 不连网关：端口无效
  const gw = new WordGatewayClient({ port: 1, token: TOKEN, connectTimeoutMs: 500 });
  const pi = makeFakePi();
  registerWordTools(pi, { gw });
  const t = pi.tools.get("word_document");
  const r = await t.execute("tc10", { action: "getInfo" }, null, null, makeFakeCtx());
  const parsed = parseResult(r);
  assert.ok(parsed.error, "应返回 error 对象");
  assert.equal(parsed.error.code, "GATEWAY_UNAVAILABLE");
  assert.ok(r.details.failed);
});
