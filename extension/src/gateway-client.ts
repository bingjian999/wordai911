/**
 * Word 宿主网关客户端（通道 B 生产版，基于 POC v3 验证通过的逻辑）
 *
 * 关键设计（均经 POC 11/11 端到端验证）：
 *  1. 连接代数 generation 防竞态：error/close 事件必须确认属于当前连接
 *     （generation / socket 同一性）才处理状态迁移；pending 请求按
 *     「发起时所用的 socket」归属清理，旧连接的迟到事件永远不会
 *     fail 掉新连接上的请求（Word 重启/快速重启场景的关键保护）
 *  2. 消息信封 v1：{version: 1, id, method, params, deadlineMs}；
 *     单行长度上限（WORD_GATEWAY_MAX_LINE，默认 64MB），超限按协议违规
 *     断开重连，防止接收缓冲无限增长
 *  3. 会话认证：AUTH -> SESSION ESTABLISHED，握手响应校验 protocolVersion，
 *     缓存 sessionId / capabilities
 *  4. cancel 三段语义：
 *     - 超时 = 本端不再等待结果（reject GATEWAY_TIMEOUT，网关侧操作可能仍在执行）
 *     - cancel 通知 = 通知网关尽力取消（尚未开始/可协作取消的任务）
 *     - 迟到的响应（网关不可中断操作执行完才回的包）按未知 id 静默丢弃
 *  5. 断线重连：指数退避（上限 5s），session_shutdown 后停止
 */
import net from "node:net";
import { randomUUID } from "node:crypto";
import { GatewayError } from "./errors.js";

export interface GatewaySession {
  sessionId: string;
  protocolVersion: number;
  serverVersion: string;
  capabilities: string[];
}

export interface GatewayClientOptions {
  host?: string;
  port?: number;
  token?: string;
  maxLineBytes?: number;
  connectTimeoutMs?: number;
  authTimeoutMs?: number;
}

interface Pending {
  sock: net.Socket; // 发起该请求的连接：竞态清理的归属依据
  method: string;
  resolve: (v: any) => void;
  reject: (e: GatewayError) => void;
  timer: NodeJS.Timeout;
}

export const PROTOCOL_VERSION = 1;
export const CLIENT_VERSION = "wordai911-ext/0.3.0";

export class WordGatewayClient {
  private readonly host: string;
  private readonly port: number;
  private readonly token: string;
  private readonly maxLine: number;
  private readonly connectTimeoutMs: number;
  private readonly authTimeoutMs: number;

  private sock: net.Socket | null = null;
  private buf = "";
  private pending = new Map<string, Pending>();
  private authed = false;
  private shuttingDown = false;
  private reconnectTimer: NodeJS.Timeout | null = null;
  private reconnectAttempts = 0;
  private connecting: Promise<void> | null = null;
  /** 连接代数：每次 rawConnect +1；事件只在自己代数仍是当前代时才处理状态迁移 */
  private generation = 0;

  /** 最近一次会话信息（AUTH -> SESSION ESTABLISHED 后缓存） */
  session: GatewaySession | null = null;

  constructor(opts: GatewayClientOptions = {}) {
    this.host = opts.host || process.env.WORD_GATEWAY_HOST || "127.0.0.1";
    this.port = opts.port ?? Number(process.env.WORD_GATEWAY_PORT || 47611);
    this.token = opts.token ?? process.env.WORD_GATEWAY_TOKEN ?? "";
    this.maxLine = opts.maxLineBytes ?? Number(process.env.WORD_GATEWAY_MAX_LINE || 64 * 1024 * 1024);
    this.connectTimeoutMs = opts.connectTimeoutMs ?? 5000;
    this.authTimeoutMs = opts.authTimeoutMs ?? 5000;
  }

  private get connected(): boolean {
    return !!this.sock && !this.sock.destroyed && this.authed;
  }

  isConnected(): boolean {
    return this.connected;
  }

  /** 只清理「发起在该 socket 上」的 pending —— 旧连接的迟到事件不波及新连接 */
  private failPendingsOn(sock: net.Socket, e: GatewayError) {
    for (const [id, p] of this.pending) {
      if (p.sock === sock) {
        clearTimeout(p.timer);
        this.pending.delete(id);
        p.reject(e);
      }
    }
  }

  /** 建立连接并发起会话认证握手（AUTH -> SESSION ESTABLISHED） */
  private rawConnect(): Promise<void> {
    const gen = ++this.generation;
    return new Promise((resolve, reject) => {
      const s = net.createConnection({ port: this.port, host: this.host });
      const timer = setTimeout(() => {
        s.destroy();
        reject(new GatewayError("GATEWAY_CONNECT_TIMEOUT", `Word 网关连接超时 (${this.host}:${this.port})`));
      }, this.connectTimeoutMs);
      s.on("connect", () => {
        if (this.generation !== gen) {
          // 已有更新一代连接建立，本连接作废
          s.destroy();
          clearTimeout(timer);
          reject(new GatewayError("GATEWAY_SUPERSEDED", "连接已被更新一代连接取代"));
          return;
        }
        clearTimeout(timer);
        this.sock = s;
        this.buf = "";
        this.authed = false;
        // AUTH 握手走 pending 表，复用同一响应分发路径
        const authId = randomUUID();
        const authTimer = setTimeout(() => {
          this.pending.delete(authId);
          s.destroy();
          reject(new GatewayError("GATEWAY_AUTH_TIMEOUT", "Word 网关认证握手超时"));
        }, this.authTimeoutMs);
        this.pending.set(authId, {
          sock: s,
          method: "auth",
          timer: authTimer,
          resolve: (r: any) => {
            // SESSION ESTABLISHED：校验协议版本并缓存会话信息
            if (!r || r.protocolVersion !== PROTOCOL_VERSION) {
              this.authed = false;
              s.destroy();
              reject(new GatewayError("GATEWAY_PROTOCOL", "网关协议版本不匹配"));
              return;
            }
            this.authed = true;
            this.session = {
              sessionId: r.sessionId,
              protocolVersion: r.protocolVersion,
              serverVersion: r.serverVersion,
              capabilities: r.capabilities || [],
            };
            resolve();
          },
          reject,
        });
        s.write(
          JSON.stringify({
            version: PROTOCOL_VERSION,
            id: authId,
            method: "auth",
            params: { token: this.token, clientVersion: CLIENT_VERSION },
          }) + "\n"
        );
      });
      s.on("data", (d) => this.onData(d));
      s.on("error", () => {
        clearTimeout(timer);
        // 竞态防护核心：只清理属于本 socket 的 pending；不影响（可能的）新连接
        this.failPendingsOn(s, new GatewayError("GATEWAY_DISCONNECTED", "Word 网关连接断开"));
        if (this.sock === s) {
          this.sock = null;
          this.authed = false;
          if (!this.shuttingDown) this.scheduleReconnect();
        }
        reject(new GatewayError("GATEWAY_DISCONNECTED", "Word 网关连接异常"));
      });
      s.on("close", () => {
        this.failPendingsOn(s, new GatewayError("GATEWAY_DISCONNECTED", "Word 网关连接断开"));
        if (this.sock === s) {
          this.sock = null;
          this.authed = false;
          if (!this.shuttingDown) this.scheduleReconnect();
        }
      });
    });
  }

  /** 断线后后台自动重连：指数退避，上限 5s；close 后停止 */
  private scheduleReconnect() {
    if (this.shuttingDown || this.connected || this.reconnectTimer) return;
    const delay = Math.min(1000 * 2 ** this.reconnectAttempts++, 5000);
    this.reconnectTimer = setTimeout(async () => {
      this.reconnectTimer = null;
      if (this.shuttingDown || this.connected) return;
      try {
        await this.ensureConnected();
      } catch {
        this.scheduleReconnect();
      }
    }, delay);
  }

  /** 对外连接入口：并发去重；网关重启后由后台重连或 call() 惰性触发自愈 */
  async ensureConnected(): Promise<void> {
    if (this.connected) return;
    if (this.connecting) return this.connecting;
    this.connecting = this.rawConnect()
      .then(() => {
        this.reconnectAttempts = 0;
      })
      .finally(() => {
        this.connecting = null;
      });
    return this.connecting;
  }

  private onData(d: Buffer) {
    this.buf += d.toString("utf8");
    // 单行长度上限：协议违规，断开走重连，防止接收缓冲无限增长
    if (this.buf.length > this.maxLine) {
      this.sock?.destroy();
      this.buf = "";
      return;
    }
    let idx;
    while ((idx = this.buf.indexOf("\n")) >= 0) {
      const line = this.buf.slice(0, idx);
      this.buf = this.buf.slice(idx + 1);
      if (!line.trim()) continue;
      try {
        const msg = JSON.parse(line);
        const p = this.pending.get(msg.id);
        if (p) {
          this.pending.delete(msg.id);
          clearTimeout(p.timer);
          if (msg.error) {
            const gw = typeof msg.error.code === "number" ? msg.error.code : undefined;
            p.reject(
              new GatewayError(
                "GATEWAY_REMOTE_" + msg.error.code,
                msg.error.message || "网关返回错误",
                gw
              )
            );
          } else {
            p.resolve(msg.result);
          }
        }
        // 无匹配 id 的响应 = 迟到的回包（取消语义第三段：不可中断操作执行完的产物），
        // 静默丢弃，不算错误 —— 客户端早已超时不再等待
      } catch {
        // 忽略无法解析的行（真实实现应记日志）
      }
    }
  }

  /**
   * 调用网关方法。
   * timeoutMs 同时作为信封里的 deadlineMs 传给网关（网关侧超期返回 -32004）。
   * 本端超时语义：Agent 不再等待结果；会向网关发 cancel 通知（尽力而为）。
   * 断线竞态自愈：写入时连接已死但尚未感知（如网关刚重启）会以 GATEWAY_DISCONNECTED
   * 失败——此时重连一次并重试；close() 后不再重试。
   */
  async call<T = any>(method: string, params: object = {}, timeoutMs = 10000): Promise<T> {
    try {
      return await this.callOnce<T>(method, params, timeoutMs);
    } catch (e) {
      if (e instanceof GatewayError && e.code === "GATEWAY_DISCONNECTED" && !this.shuttingDown) {
        try {
          await this.ensureConnected();
          return await this.callOnce<T>(method, params, timeoutMs);
        } catch {
          throw e; // 重连失败：抛原始断线错误
        }
      }
      throw e;
    }
  }

  private async callOnce<T>(method: string, params: object, timeoutMs: number): Promise<T> {
    if (!this.connected) {
      try {
        await this.ensureConnected();
      } catch {
        throw new GatewayError("GATEWAY_UNAVAILABLE", "Word 网关不可用（连接失败或认证失败）");
      }
    }
    const sock = this.sock!;
    const id = randomUUID();
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        // 超时 = 本端不再等待；cancel = 通知网关尽力取消尚未开始/可协作取消的任务
        try {
          sock.write(JSON.stringify({ version: PROTOCOL_VERSION, method: "cancel", params: { id } }) + "\n");
        } catch {
          /* socket 已断，无需取消 */
        }
        reject(new GatewayError("GATEWAY_TIMEOUT", `Word 网关调用超时: ${method}（网关侧操作可能仍在执行）`));
      }, timeoutMs);
      this.pending.set(id, { sock, method, resolve, reject, timer });
      sock.write(JSON.stringify({ version: PROTOCOL_VERSION, id, method, params, deadlineMs: timeoutMs }) + "\n");
    });
  }

  close() {
    this.shuttingDown = true;
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.sock?.destroy();
    this.sock = null;
    this.authed = false;
    this.failAllPending(new GatewayError("GATEWAY_CLOSED", "连接已关闭"));
  }

  /** 仅供测试：重置停机标记以复用客户端实例 */
  resetForTest() {
    this.shuttingDown = false;
    this.reconnectAttempts = 0;
  }

  private failAllPending(e: GatewayError) {
    for (const [id, p] of this.pending) {
      clearTimeout(p.timer);
      this.pending.delete(id);
      p.reject(e);
    }
  }
}
