# 通道 B 协议规范（v1）

TS 扩展（运行在 Pi 进程内）与 C# Word 网关之间的本地 TCP JSON Lines 协议。

- 端点：`127.0.0.1:47611`（仅本机回环，不对外监听）
- 分帧：一行一条 JSON 消息，`\n` 结尾，与通道 A 的 JSONL 语义一致
- 单行长度上限：网关默认 1 MB（可经环境变量配置），超限返回 `-32002` 并断开连接，防止文档正文、表格、HTML 内容把 TCP 缓冲区无限撑大

## 消息信封（v1）

**请求**：

```json
{"version": 1, "id": "<uuid>", "method": "word.getDocumentInfo", "params": {...}, "deadlineMs": 10000}
```

- `version`：协议版本，不匹配返回 `-32003`
- `deadlineMs`：相对截止时间（毫秒）；网关超期未完成返回 `-32004`

**响应**：

```json
{"id": "<uuid>", "result": {...}}
```

或

```json
{"id": "<uuid>", "error": {"code": -32601, "message": "..."}}
```

## 方法命名空间

- `word.*`：文档操作
- `session.*`：会话辅助
- `system.*`：环境信息

## 会话认证

连接 → `AUTH` → `SESSION ESTABLISHED`：

1. TCP 连接建立后，首条消息必须是 auth 请求（携带 token，token 由网关启动时随机生成、经环境变量注入 Pi 子进程）
2. 认证成功返回 SESSION ESTABLISHED，携带 `sessionId`、`protocolVersion`、`serverVersion`、`clientVersion`、`capabilities`（为后续扩展 `getDocument` / `insertText` / `replaceText` / `find` / `table` / `range` / `style` / `comment` / `trackChanges` 提供能力协商基础）
3. 未认证调用与错误 token 返回 `-32001`，连续 3 次未认证直接断开

## 取消语义（三段式）

- **超时** = Agent 不再等待结果：客户端 reject，网关侧操作可能仍在执行
- **cancel 通知** = 网关尽力停止尚未开始或可协作取消的任务（对应 .NET 的 Cooperative CancellationToken）
- **已进入不可中断 COM 调用**（如 `InsertAfter` / `TypeText`）的操作让其执行完毕后丢弃结果——网关侧以 `interruptible` 标志区分两类任务，客户端对迟到回包按未知 id 静默丢弃

## 错误码

| code | 含义 |
|---|---|
| -32001 | 认证失败（未认证 / token 错误） |
| -32002 | 单行消息超长 |
| -32003 | 协议版本不匹配 |
| -32004 | deadline 超期 |
| -32601 | method not found |
| -32700 | parse error |

## 断线竞态防护

客户端为每次连接维护 generation（连接代数）：旧连接迟到的 `error` / `close` 事件不清理新连接的 pending；pending 请求按所属 socket 归属失败，网关重启后新调用不受影响。服务端对迟到回包按未知 id 静默丢弃。
