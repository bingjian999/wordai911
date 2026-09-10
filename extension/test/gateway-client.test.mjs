/**
 * 通道 B 网关客户端 E2E 测试（对 mock 网关，覆盖协议语义全表）
 * 运行：node --test test/（需先 npm run build）
 */
import test from "node:test";
import assert from "node:assert/strict";
import net from "node:net";
import { startMockGateway } from "../mock/mock-gateway.mjs";
import { WordGatewayClient } from "../dist/gateway-client.js";
import { GatewayError } from "../dist/errors.js";

const TOKEN = "test-token";

async function withGateway(fn, opts = {}) {
  const server = await startMockGateway({ token: TOKEN, ...opts });
  const port = (server.address()).port;
  try {
    await fn(port, server);
  } finally {
    // server.close() 会等待存量连接结束——测试失败时客户端可能未 close，
    // 先强制断开全部连接防止 finally 永久挂死
    if (server.destroyAll) server.destroyAll();
    await new Promise((r) => server.close(r));
  }
}

function makeClient(port, extra = {}) {
  return new WordGatewayClient({ port, token: TOKEN, ...extra });
}

test("auth 握手建立会话并缓存 session 信息", async () => {
  await withGateway(async (port) => {
    const gw = makeClient(port);
    await gw.ensureConnected();
    assert.ok(gw.isConnected());
    assert.equal(gw.session.protocolVersion, 1);
    assert.match(gw.session.sessionId, /^[0-9a-f-]{36}$/);
    assert.ok(gw.session.capabilities.length > 0);
    gw.close();
  });
});

test("七个 word.* 方法全部往返成功且副作用正确", async () => {
  await withGateway(async (port, server) => {
    const gw = makeClient(port);
    const doc = server.doc;

    // 1. getDocumentInfo
    const info = await gw.call("word.getDocumentInfo");
    assert.equal(info.fileName, "测试文档.docx");
    assert.equal(info.paragraphCount, 3);

    // 2. getSelection
    const sel = await gw.call("word.getSelection");
    assert.equal(sel.styleName, "Normal");

    // 3. insertText
    const ins = await gw.call("word.insertText", { text: "追加段落", position: "documentEnd" });
    assert.equal(ins.charactersInserted, 4);
    assert.equal(doc.paragraphs.length, 4);
    assert.equal(doc.paragraphs[3], "追加段落");

    // 4. find
    const found = await gw.call("word.find", { query: "hello" });
    assert.equal(found.totalMatches, 1);
    assert.ok(found.hits[0].context.includes("hello"));

    // 5. replace（all=false 首个）
    const rep = await gw.call("word.replace", { find: "hello", replace: "你好", all: false });
    assert.ok(rep.replacementsMade >= 1);
    assert.ok(doc.fullText().includes("你好"));

    // 6. applyStyle
    const style = await gw.call("word.applyStyle", { paragraphStyle: "Heading 1" });
    assert.equal(style.styleName, "Heading 1");

    // 7. save
    assert.equal(doc.unsaved, true);
    const saved = await gw.call("word.save", {});
    assert.deepEqual(saved, { saved: true });
    assert.equal(doc.unsaved, false);
    gw.close();
  });
});

test("未知方法返回 -32601 且连接保持可用", async () => {
  await withGateway(async (port) => {
    const gw = makeClient(port);
    await assert.rejects(
      () => gw.call("word.nope"),
      (e) => e instanceof GatewayError && e.code === "GATEWAY_REMOTE_-32601"
    );
    // 连接仍然可用
    const info = await gw.call("word.getDocumentInfo");
    assert.equal(info.fileName, "测试文档.docx");
    gw.close();
  });
});

test("错误码与展示文案分离：GatewayError.message 只有机器码", async () => {
  await withGateway(async (port) => {
    const gw = makeClient(port);
    try {
      await gw.call("word.nope");
      assert.fail("应抛出");
    } catch (e) {
      assert.ok(e instanceof GatewayError);
      assert.equal(e.message, "GATEWAY_REMOTE_-32601"); // 机器码
      assert.ok(!e.message.includes("网关")); // 展示文案不进 message
      assert.ok(e.display.length > 0); // 展示文案在 display
      assert.equal(e.gatewayCode, -32601);
    }
    gw.close();
  });
});

test("本端超时：GATEWAY_TIMEOUT + cancel 通知，迟到回包静默丢弃", async () => {
  await withGateway(async (port) => {
    const gw = makeClient(port);
    const t0 = Date.now();
    await assert.rejects(
      () => gw.call("word.slowEcho", { text: "x", delayMs: 1500 }, 300),
      (e) => e instanceof GatewayError && e.code === "GATEWAY_TIMEOUT"
    );
    assert.ok(Date.now() - t0 < 1000, "应在超时时立即返回，而非等满慢调用");
    // 等待迟到回包到达（不可中断操作执行完被网关丢弃）——客户端静默无副作用
    await new Promise((r) => setTimeout(r, 1600));
    // 连接仍可用（迟到包按未知 id 丢弃，不算错误）
    const info = await gw.call("word.getDocumentInfo");
    assert.equal(info.fileName, "测试文档.docx");
    gw.close();
  });
});

test("网关侧 deadline：超期返回 -32004（原始 socket 协议验证）", async () => {
  // 客户端 call() 把 deadlineMs 设为 timeoutMs，本端超时必然先于服务端 -32004 到达，
  // 因此网关侧 deadline 语义只能用原始 socket 直连验证。
  await withGateway(async (port) => {
    const reply = await new Promise((resolve, reject) => {
      const s = net.createConnection({ port, host: "127.0.0.1" });
      let buf = "";
      const timer = setTimeout(() => reject(new Error("等 -32004 回包超时")), 5000);
      s.on("connect", () => {
        // 先认证
        s.write(JSON.stringify({ version: 1, id: "a1", method: "auth", params: { token: TOKEN } }) + "\n");
        // deadline 300ms < delay 800ms → 网关判超期
        s.write(JSON.stringify({
          version: 1, id: "d1", method: "word.slowEcho",
          params: { text: "x", delayMs: 800 }, deadlineMs: 300,
        }) + "\n");
      });
      s.on("data", (d) => {
        buf += d.toString();
        for (const line of buf.split("\n")) {
          if (!line.trim()) continue;
          let msg;
          try { msg = JSON.parse(line); } catch { continue; }
          if (msg.id === "d1") {
            clearTimeout(timer);
            s.destroy();
            resolve(msg);
          }
        }
      });
      s.on("error", (e) => { clearTimeout(timer); reject(e); });
    });
    assert.equal(reply.error?.code, -32004);
  });
});

test("断线：该 socket 上的 pending 以 GATEWAY_DISCONNECTED 失败", async () => {
  await withGateway(async (port, server) => {
    const gw = makeClient(port);
    await gw.ensureConnected();
    const slowPromise = gw.call("word.slowEcho", { text: "x", delayMs: 5000 }, 10000);
    // 给网关一点时间登记 in-flight，然后硬杀网关
    await new Promise((r) => setTimeout(r, 150));
    server.destroyAll();
    await new Promise((r) => server.close(r));
    await assert.rejects(
      () => slowPromise,
      (e) => e instanceof GatewayError && e.code === "GATEWAY_DISCONNECTED"
    );
    gw.close();
  });
});

test("网关重启后客户端自愈：惰性重连恢复可用", async () => {
  const server1 = await startMockGateway({ token: TOKEN });
  const port = (server1.address()).port;
  const gw = makeClient(port);
  await gw.ensureConnected();
  await new Promise((r) => setTimeout(r, 100));
  server1.destroyAll();
  await new Promise((r) => server1.close(r));
  // 网关重启在同一端口
  const server2 = await startMockGateway({ port, token: TOKEN });
  try {
    // call 惰性触发重连
    const info = await gw.call("word.getDocumentInfo");
    assert.equal(info.fileName, "测试文档.docx");
  } finally {
    gw.close();
    if (server2.destroyAll) server2.destroyAll();
    await new Promise((r) => server2.close(r));
  }
});

test("未认证请求：网关拒绝 -32001，三次后断开", async () => {
  await withGateway(async (port) => {
    const resp = await new Promise((resolve) => {
      const s = net.createConnection({ port, host: "127.0.0.1" });
      let buf = "";
      s.on("connect", () => {
        s.write(JSON.stringify({ version: 1, id: "x1", method: "word.getDocumentInfo", params: {} }) + "\n");
        s.write(JSON.stringify({ version: 1, id: "x2", method: "word.getDocumentInfo", params: {} }) + "\n");
        s.write(JSON.stringify({ version: 1, id: "x3", method: "word.getDocumentInfo", params: {} }) + "\n");
        s.write(JSON.stringify({ version: 1, id: "x4", method: "word.getDocumentInfo", params: {} }) + "\n");
      });
      s.on("data", (d) => {
        buf += d.toString();
        const lines = buf.split("\n").filter((l) => l.trim());
        if (lines.length >= 4) {
          s.destroy();
          resolve(lines.map((l) => JSON.parse(l)));
        }
      });
      s.on("close", () => resolve(buf.split("\n").filter((l) => l.trim()).map((l) => JSON.parse(l))));
      s.on("error", () => resolve(buf.split("\n").filter((l) => l.trim()).map((l) => JSON.parse(l))));
    });
    // 第 3 次未认证即断开：只有前 3 个请求能收到 -32001，第 4 个随连接一起消失
    assert.equal(resp.length, 3);
    for (const r of resp) {
      assert.equal(r.error.code, -32001);
    }
  });
});

test("错误 token：认证失败后 call 报 GATEWAY_UNAVAILABLE", async () => {
  await withGateway(async (port) => {
    const gw = new WordGatewayClient({ port, token: "wrong-token" });
    await assert.rejects(
      () => gw.call("word.getDocumentInfo"),
      (e) => e instanceof GatewayError && e.code === "GATEWAY_UNAVAILABLE"
    );
    gw.close();
  });
});

test("单行超长：协议违规断开（缓冲不无限增长）", async () => {
  await withGateway(async (port) => {
    const gw = new WordGatewayClient({ port, token: TOKEN, maxLineBytes: 1024 });
    await gw.ensureConnected();
    // slowEcho 把大文本原样回传 → 客户端单行超限，按协议违规断开
    await assert.rejects(
      () => gw.call("word.slowEcho", { text: "大文本".repeat(2000), delayMs: 10 }, 8000),
      (e) => e instanceof GatewayError
    );
    // 超限断开后 client 不再可用
    assert.ok(!gw.isConnected());
    gw.close();
  });
});

test("close 后调用快速失败 GATEWAY_CLOSED / GATEWAY_UNAVAILABLE", async () => {
  await withGateway(async (port) => {
    const gw = makeClient(port);
    await gw.ensureConnected();
    const slow = gw.call("word.slowEcho", { text: "x", delayMs: 5000 }, 10000);
    gw.close();
    await assert.rejects(
      () => slow,
      (e) => e instanceof GatewayError && e.code === "GATEWAY_CLOSED"
    );
  });
});
