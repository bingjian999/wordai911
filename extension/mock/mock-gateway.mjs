/**
 * 模拟 C# Word 宿主网关（测试替身）
 * 真实架构中此角色由 C# AgentHelper 的 WordGatewayServer 承担：
 *   - 在 Word 进程侧监听本地 TCP（仅 127.0.0.1）
 *   - 接收 Pi 扩展发来的 JSON-RPC 风格请求
 *   - 在 Word COM 对象上执行实际文档操作后回写结果
 *
 * 本 mock 实现与 C# 网关一致的协议语义：
 *   - 会话认证门禁（AUTH -> SESSION ESTABLISHED；3 次未认证断开）
 *   - 消息信封 v1 + deadline（超期 -32004）
 *   - 单行长度上限（超限 -32002 断开）
 *   - cancel 三段式（未开始/可协作/不可中断执行完丢弃）
 *   - 全部 7 个 word.* 方法（内存文档模型）
 */
import net from "node:net";
import crypto from "node:crypto";

export const DEFAULT_PORT = 47611;
export const PROTOCOL_VERSION = 1;

/** 内存文档模型：段落字符串数组 */
class FakeDoc {
  paragraphs = ["第一段：WordAI911 测试文档。", "第二段：hello world。", "第三段：待替换的目标文本 target。"];
  fileName = "测试文档.docx";
  unsaved = false;

  fullText() {
    return this.paragraphs.join("\n");
  }

  insertText(text, position) {
    if (position === "documentEnd" || position === "document-end") {
      this.paragraphs.push(...String(text).split("\n"));
    } else if (position === "documentStart" || position === "document-start") {
      this.paragraphs.unshift(...String(text).split("\n"));
    } else {
      // selection：插到最后一段末尾（模拟光标在文末）
      this.paragraphs[this.paragraphs.length - 1] += String(text);
    }
    this.unsaved = true;
    const total = this.fullText().length;
    return { charactersInserted: String(text).length, totalCharacters: total };
  }

  find(query, maxHits) {
    const text = this.fullText();
    const hits = [];
    let i = text.indexOf(query);
    while (i >= 0 && hits.length < (maxHits || 50)) {
      hits.push({
        startOffset: i,
        endOffset: i + query.length,
        context: text.slice(Math.max(0, i - 32), Math.min(text.length, i + query.length + 32)),
      });
      i = text.indexOf(query, i + 1);
    }
    return { hits, totalMatches: hits.length };
  }

  replace(find, replace, all) {
    const text = this.fullText();
    let count = 0;
    let idx = text.indexOf(find);
    if (idx >= 0) {
      count = 1;
      if (all !== false) {
        let rest = text.slice(idx + find.length).split(find).length - 1;
        count += rest;
      }
      this.paragraphs = text.split(find).join(all !== false ? replace : replace).split("\n");
      // all=false 只替换首个：手动重建
      if (all === false) {
        const firstIdx = text.indexOf(find);
        this.paragraphs = (text.slice(0, firstIdx) + replace + text.slice(firstIdx + find.length)).split("\n");
      }
      this.unsaved = true;
    }
    return { replacementsMade: count, totalMatches: count };
  }

  info() {
    const text = this.fullText();
    return {
      fileName: this.fileName,
      filePath: "/tmp/" + this.fileName,
      paragraphCount: this.paragraphs.length,
      wordCount: text.split(/\s+/).filter(Boolean).length,
      characterCount: text.length,
      saved: !this.unsaved,
      readProtectionState: "none",
    };
  }
}

/**
 * 启动 mock 网关。返回 Promise<server>（resolve 于 listen 完成后）。
 * server 上挂 doc（FakeDoc 实例）供测试断言，close() 关闭。
 */
export function startMockGateway({ port = 0, token = "test-token", maxLine = 1024 * 1024 } = {}) {
  const doc = new FakeDoc();
  const sockets = new Set();
  const server = net.createServer((sock) => {
    sockets.add(sock);
    sock.on("close", () => sockets.delete(sock));
    let buf = "";
    let authed = false;
    let authFailures = 0;
    const cancelled = new Set();
    const inFlight = new Map();

    sock.on("data", (d) => {
      buf += d.toString("utf8");
      if (buf.length > maxLine) {
        try {
          sock.write(
            JSON.stringify({ id: null, error: { code: -32002, message: "message too large (max " + maxLine + " bytes)" } }) + "\n"
          );
        } catch {}
        sock.destroy();
        return;
      }
      let idx;
      while ((idx = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, idx);
        buf = buf.slice(idx + 1);
        if (!line.trim()) continue;
        let req;
        try {
          req = JSON.parse(line);
        } catch {
          sock.write(JSON.stringify({ error: { code: -32700, message: "parse error" } }) + "\n");
          continue;
        }

        // 认证门禁
        if (!authed && req.method !== "auth") {
          sock.write(JSON.stringify({ id: req.id, error: { code: -32001, message: "unauthenticated" } }) + "\n");
          if (++authFailures >= 3) {
            sock.destroy();
            return; // 达到认证失败上限：立即断开，不再处理同一缓冲里的后续行
          }
          continue;
        }
        if (req.method === "auth") {
          let resp;
          if (req.version !== PROTOCOL_VERSION) {
            resp = { id: req.id, error: { code: -32003, message: "unsupported protocol version: " + req.version } };
          } else if (!(req.params && req.params.token === token)) {
            resp = { id: req.id, error: { code: -32001, message: "auth failed: invalid token" } };
          } else {
            authed = true;
            resp = {
              id: req.id,
              result: {
                authed: true,
                sessionId: crypto.randomUUID(),
                protocolVersion: PROTOCOL_VERSION,
                serverVersion: "mock-gateway/0.3.0",
                clientVersion: req.params.clientVersion || "unknown",
                capabilities: ["word.doc.read", "word.doc.write", "cancel.cooperative", "deadline"],
              },
            };
          }
          sock.write(JSON.stringify(resp) + "\n");
          continue;
        }

        // cancel 通知（三段式）
        if (req.method === "cancel" && req.params && req.params.id) {
          const f = inFlight.get(req.params.id);
          if (f && f.interruptible === false) {
            cancelled.add(req.params.id);
          } else if (f) {
            clearTimeout(f.timer);
            inFlight.delete(req.params.id);
          } else {
            cancelled.add(req.params.id);
          }
          continue;
        }

        // 不可中断慢操作（模拟 Word COM InsertAfter/TypeText）
        if (req.method === "word.slowEcho") {
          const delay = req.params && req.params.delayMs ? Math.min(req.params.delayMs, 10000) : 3000;
          const deadline = typeof req.deadlineMs === "number" ? req.deadlineMs : null;
          const startedAt = Date.now();
          const t = setTimeout(() => {
            inFlight.delete(req.id);
            if (cancelled.has(req.id)) {
              cancelled.delete(req.id);
              return; // 已取消：不可中断操作执行完，结果丢弃
            }
            if (deadline != null && Date.now() - startedAt > deadline) {
              sock.write(JSON.stringify({ id: req.id, error: { code: -32004, message: "deadline exceeded" } }) + "\n");
              return;
            }
            sock.write(JSON.stringify({ id: req.id, result: { echoed: (req.params && req.params.text) || "" } }) + "\n");
          }, delay);
          inFlight.set(req.id, { method: req.method, timer: t, interruptible: false });
          continue;
        }

        // word.* 业务方法
        let resp;
        switch (req.method) {
          case "word.getDocumentInfo":
            resp = { id: req.id, result: doc.info() };
            break;
          case "word.getSelection":
            resp = {
              id: req.id,
              result: { text: doc.paragraphs[doc.paragraphs.length - 1], startOffset: 0, endOffset: 0, styleName: "Normal" },
            };
            break;
          case "word.insertText":
            resp = {
              id: req.id,
              result: doc.insertText(req.params?.text ?? "", req.params?.position ?? "selection"),
            };
            break;
          case "word.find":
            resp = { id: req.id, result: doc.find(req.params?.query ?? "", req.params?.maxHits ?? 50) };
            break;
          case "word.replace":
            resp = {
              id: req.id,
              result: doc.replace(req.params?.find ?? "", req.params?.replace ?? "", req.params?.all !== false),
            };
            break;
          case "word.applyStyle":
            resp = {
              id: req.id,
              result: { paragraphsStyled: 1, styleName: req.params?.paragraphStyle ?? "" },
            };
            break;
          case "word.save":
            doc.unsaved = false;
            resp = { id: req.id, result: { saved: true } };
            break;
          default:
            resp = { id: req.id, error: { code: -32601, message: "method not found: " + req.method } };
        }
        sock.write(JSON.stringify(resp) + "\n");
      }
    });
    sock.on("error", () => {});
  });

  return new Promise((resolve) => {
    server.listen(port, "127.0.0.1", () => {
      server.doc = doc;
      /** 测试辅助：强制断开全部客户端连接（server.close 会等待存量连接结束） */
      server.destroyAll = () => {
        for (const s of sockets) s.destroy();
      };
      resolve(server);
    });
  });
}

// 直接运行：作为独立 mock 网关进程
if (process.argv[1] && process.argv[1].endsWith("mock-gateway.mjs")) {
  const token = process.env.WORD_GATEWAY_TOKEN;
  if (!token) {
    console.error("[gateway] 缺少 WORD_GATEWAY_TOKEN 环境变量，拒绝启动");
    process.exit(2);
  }
  const port = Number(process.env.WORD_GATEWAY_PORT || DEFAULT_PORT);
  startMockGateway({ port, token }).then((s) => {
    console.log(`[gateway] mock 网关已监听 127.0.0.1:${port}`);
  });
}
