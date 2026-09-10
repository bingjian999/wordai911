# Word Agent 模块技术方案（v3）

> 本文档从飞书原文档导出（2026-09-10，POC v3 已验证通过 11/11 项端到端测试）。
> 原文档：https://uih.feishu.cn/docx/CrfUdWQl5oBuV3xZFNVceL4Sntf
> 项目：wordai911 —— 基于上述技术方案的正式实现。

基于 Pi Coding Agent 重构 Word Agent 模块技术方案
**方案状态**：技术方案（POC v3 已验证通过 11/11 项端到端测试，代码见附录）
**版本基线**：Pi coding agent v0.85.1（与原项目内嵌版本一致）
**编写日期**：2026-09-10
一、背景与目标

原 CPAHelperForWord 项目的源码经逆向恢复后仍存在结构性缺陷：约 182 个命名空间被混淆器破坏、无法自动还原，13 个类型反编译失败，人工修复成本接近重写。因此决策：**放弃修复逆向代码，基于开源项目 Pi coding agent 重新开发 Word Agent 模块，架构分层与原项目保持一致**。

本方案已完成关键技术验证：在沙箱环境搭建了完整 POC，验证了「C# 宿主 ↔ Pi 进程 ↔ TS 扩展 ↔ TCP 网关 ↔ Word 宿主」全链路通信可行性，全部 6 项端到端测试通过（见第四章）。

**设计原则**：

- 架构一致：沿用原项目四层架构与三条通信通道的划分，降低迁移理解成本
- 干净重写：所有新代码从零编写，不携带任何逆向产物的混淆命名与损坏方法体
- 底层复用：Agent 大脑直接复用开源 Pi（MIT 许可），自研代码只写扩展与集成层
- 原样迁移：TS 扩展与 skills 属原始源码（未混淆），可平移复用作为参考实现二、总体架构

四层架构与原项目逐层对应：
[表格见飞书原文档]
一次典型 AI 请求的完整数据流：

- 用户在 Word 侧 AI 面板输入指令，C# 侧通过 RPC 通道向 Pi 进程发送 `prompt` 命令（JSONL，经 stdin）
- Pi 调用 LLM；LLM 决定调用 `word_document` 等注册工具
- TS 扩展收到工具调用，经本地 TCP 通道把操作请求发给 C# 网关
- C# 网关在 Word COM 对象上执行实际文档操作，结果沿 TCP 回传
- 工具结果进入 LLM 上下文，循环直至任务完成
- 流式事件（message_update）沿 RPC 通道实时回传 C# 渲染到面板三、通信通道设计（三条通道，均已在 POC 中实测）
通道 A：C# ↔ Pi 子进程（stdin/stdout JSONL，RPC 模式）

C# 以子进程方式启动 `pi --mode rpc --no-session`，通过标准输入输出交换 JSON Lines：

- **关键命令**：`prompt`（发消息，支持 steering/followUp 打断模式）、`get_state`（状态轮询/心跳）、`abort`（取消）、`set_model`（切模型）、`get_entries`（增量拉取会话记录，支持断线续传）
- **关键事件**：`message_update`（流式增量，含 text_delta/toolcall_delta）、`tool_execution_*`（工具执行生命周期）、`agent_settled`（一轮完全结束）、`extension_error`（扩展异常）
- **请求关联**：命令可带 `id`，响应与 `bash_execution_update` 事件回带同 `id`
- **分帧注意**：严格按 LF（`\n`）切分记录；Pi 官方文档明确警告 Node readline 因额外切分 U+2028/U+2029 不合规，C# 侧需自实现按 `\n` 切分的缓冲读取器，不要直接用 `ReadLine` 之外的懒办法通道 B：TS 扩展 ↔ C# 网关（本地 TCP JSON Lines，127.0.0.1:47611）

Pi 扩展运行在 Pi 进程内，与 Word 进程分离，工具执行需要直达 Word 的低延迟通道：

- **请求（信封 v1）**：`{"version": 1, "id": "", "method": "word.getDocumentInfo", "params": {...}, "deadlineMs": 10000}`；版本不匹配返回 -32003，超期未完成返回 -32004
- **响应**：`{"id": "", "result": {...}}` 或 `{"id": "", "error": {"code": -32601, "message": "..."}}`
- **方法命名空间**：`word.*`（文档操作）、`session.*`（会话辅助）、`system.*`（环境信息）
- 一次请求一响应，`\n` 结尾，与通道 A 的 JSONL 语义一致
- **会话认证**：连接 → AUTH → SESSION ESTABLISHED；认证成功返回 sessionId、protocolVersion、serverVersion、clientVersion、capabilities（为后续扩展 getDocument / insertText / replaceText / find / table / range / style / comment / trackChanges 提供能力协商基础）；未认证调用与错误 token 返回 -32001，连续 3 次未认证直接断开（POC v3 已实测）
- **取消语义（三段式）**：超时 = Agent 不再等待结果（客户端 reject，网关侧操作可能仍在执行）；cancel 通知 = 网关尽力停止尚未开始或可协作取消的任务（真实实现对应 Cooperative cancellation token）；已进入不可中断 COM 调用（如 InsertAfter / TypeText）的操作让其执行完毕后丢弃结果——网关侧以 interruptible 标志区分两类任务，客户端对迟到回包按未知 id 静默丢弃（POC v3 已实测）
- **消息长度上限**：单行超过上限（网关默认 1MB，可经环境变量配置）返回 -32002 并断开连接，防止 Word 文档正文、表格、HTML 内容把 TCP 缓冲区无限撑大（POC v3 已实测 2MB 超长消息被拒）
- **断线竞态防护**：客户端为每次连接维护 generation 连接代数，旧连接迟到的 error / close 事件不清理新连接的 pending；pending 请求按所属 socket 归属失败，网关重启后新调用不受影响（POC v3 已实测）通道 C：扩展 UI 对话子协议（extension_ui_request / extension_ui_response）

Pi 扩展调用 `ctx.ui.confirm/select/input` 时，RPC 模式下自动转为 stdout 上的 `extension_ui_request`，C# 宿主收到后：

- 解析 method（confirm/select/input）与内容
- 在 Word 侧弹原生对话框（或映射到 AI 面板交互）
- 通过 stdin 回传 `{"type": "extension_ui_response", "id": "", "confirmed": true}` 等结果
- 带 `timeout` 的请求到期后 Pi 侧自动以默认值收场，宿主无需自行计时四、POC 实测验证

测试环境：Node v22.22.0、`@earendil-works/pi-coding-agent@0.85.1`（npm 安装）、1 Core / 4GB 沙箱。POC 组成：一个模拟 C# 宿主的 Node TCP 网关（mock-word-gateway.js，v3 起含会话认证、消息信封 v1、单行长度上限、deadline 与 interruptible 取消语义）+ 一个真实 Pi 扩展（word-agent-extension.ts，注册 `word_document` 工具与 `/word.ping`、`/word.slow`、`/word.slowlong`、`/word.confirm` 命令，v3 起含连接代数 generation 防竞态、按 socket 归属的 pending 清理）+ 一个以 RPC 模式驱动 Pi 进程的端到端测试脚本（rpc-e2e-test.js，模拟 C# 侧的 JSONL 读写，网关由测试托管以验证重启自愈与断线竞态隔离）。
[表格见飞书原文档]
附加验证：SDK 嵌入路线（`AgentSession`、`createAgentSession`、`ExtensionRunner` 等）可从 ESM 正常导入，保留为备选宿主方案。工具的完整 LLM 调用链（LLM 决定调用 `word_document`）需真实 API key，在用户环境接入后验证；本 POC 用扩展命令替代驱动了同一执行路径（工具 handler 与命令 handler 走同一网关客户端）。

**评审修订（v2）**：针对首轮代码评审的 6 条意见全部落实并回归测试——网关增加 token 握手认证；扩展增加断线自动重连（后台指数退避 + 调用前惰性重连）；调用超时后向网关发 cancel 通知消除孤儿请求；错误码（稳定机器码，进 Error.message）与展示文案（仅 UI 层）分离；word_document 的 action 参数改为 Type.Union 字面量枚举；package.json 补齐 test/gateway 脚本并将 pi 依赖锁定为精确版本 0.85.1。

**评审修订（v3）**：针对第二轮 A 级评审的 4 条意见全部落实并回归测试（11/11 通过）——取消语义按三段式重新定义（超时 = Agent 不再等待结果；cancel = 网关尽力停止尚未开始/可协作取消的任务；已进入不可中断 COM 调用的操作执行完毕后丢弃结果，网关以 interruptible 标志区分）；WordGatewayClient 增加连接代数 generation，旧连接迟到的 error / close 事件按 socket 归属清理 pending，不误伤新连接；消息信封升级为 v1（version / id / method / params / deadlineMs）并增加单行长度上限（网关默认 1MB，超限返回 -32002 断开）；auth 升级为连接会话认证（连接 → AUTH → SESSION ESTABLISHED，返回 sessionId / protocolVersion / serverVersion / clientVersion / capabilities）。
五、模块详细设计
5.1 C# 侧：AgentHelper 库（net472 类库，被 VSTO 主插件引用）

- **AgentRuntimeHost**：Pi 子进程生命周期管理。启动参数注入（provider/model/扩展路径）、启动超时、get_state 心跳轮询（如 10s）、异常退出自动重启（指数退避，最多 3 次）、Word 退出时优雅关闭（stdin 关闭 + SIGTERM 等价物）。沿用原项目 pi-runtime.json 设计：锁定 runtimeVersion 与 upstreamSha256，启动前校验防止运行时被篡改
- **PiRpcClient**：JSONL 编解码 + 按 id 的命令关联表 + 事件分发器。注意 LF 分帧与半包缓冲；stdout 高频事件（message_update）直接透传给 UI 层渲染，不排队
- **WordGatewayServer**：TCP 监听 127.0.0.1:47611（仅本机回环）。**核心设计：Word COM 单线程约束**——所有 `word.*` 请求进入串行执行队列，统一投递到插件主线程（或专用 STA 线程 marshal 到主线程）执行，天然避免 COM 并发异常；执行结果回写。启动时生成随机 handshake token，扩展首连时校验，防止本机其他进程误连
- **UiBridge**：extension_ui_request → WPF/WinForms 对话框 → extension_ui_response 回传5.2 TS 侧：word-agent-extension.ts

- **GatewayClient**：TCP 客户端 + 请求超时（默认 10s，长操作如全文扫描可传更长）+ 断线重连（指数退避，上限 30s）+ pending 请求表。POC 已实现并测试
- **工具注册**（typebox schema，逐个映射到网关方法）：[表格见飞书原文档]
- **生命周期**：连接与后台资源在 `session_start` 建立、`session_shutdown` 关闭（遵循 Pi 规范，避免无会话调用期占用资源）
- **进度上报**：长操作用 execute 的 onUpdate 增量上报（网关侧可分块回传），LLM 与 UI 均可见
- 原项目 cpahelper-office.ts / cpahelper-office-child.ts / report-review 等扩展作为业务功能平移参考，逐个按新骨架重写5.3 Skills 与配置

`.agent/skills/` 三个技能（multi-search-engine、report-review、skill-creator）是未混淆的原始源码，直接复用；`resources_discover` 事件里注册自定义 skillPaths 指向插件安装目录，实现技能随插件分发。
六、关键技术决策

- **RPC 子进程而非 SDK 嵌入**：宿主是 C#（.NET Framework），Pi 是 Node 程序，只能子进程集成；SDK 路线（AgentSession）仅适用于未来若把宿主换成 Node/Electron 的场景。POC 两条路线都已验证可行
- **工具走 TCP 回连而非 RPC 通道**：工具在 Pi 进程内执行，若经 C# 宿主中转 RPC 通道再绕回 Word，多一跳且耦合消息循环；直达 TCP 网关延迟最低，也和原项目架构一致
- **Word COM 串行化**：网关方法队列单线程投递，规避 COM 线程模型的坑，同时天然提供操作顺序保证
- **版本锁定**：0.85.1 与原项目一致；Pi 在 0.74.0 换过 npm scope（@mariozechner/* → @earendil-works/*），API 有破坏性变更先例。锁死版本 + 升级时按 CHANGELOG 审计 ExtensionAPI 面（参考 DEV.md 的六步审计法）
- **扩展命令作为测试后门**：每个工具配套一个 `/word.xxx` 扩展命令（不走 LLM），宿主可随时自检全链路健康——本 POC 即以此模式在无 API key 环境完成了端到端验证七、可靠性设计
[表格见飞书原文档]八、实施计划
[表格见飞书原文档]
每阶段结束保留可运行产物；M1 完成后即可每日自测链路健康。
九、风险与对策

- **Pi 上游快速迭代**（中风险）：锁版本 + 升级审计流程；扩展仅依赖 ExtensionAPI / typebox / node:net 等稳定面，压缩暴露面
- **LLM 误操作文档**（中风险）：高危工具（replace、save）经 ctx.ui.confirm 强制确认；网关侧对写操作做修订记录（Word 自带修订模式可选启用）
- **Word COM 性能**（低风险）：大文档全文操作慢——网关分块执行 + onUpdate 进度；工具描述里引导 LLM 优先用 find 定位再局部操作
- **本地端口安全**（低风险）：仅绑 127.0.0.1 + 握手 token；不暴露公网
- **无 API key 环境**（低风险）：扩展命令测试后门不依赖 LLM，全部链路可离线自检十、附录：POC 代码与复现

POC 完整代码（4 个文件 + 测试结果）已打包：`wordagent-poc.zip`，含 mock-word-gateway.js（模拟 C# 宿主网关）、word-agent-extension.ts（Pi 扩展）、rpc-e2e-test.js（RPC 端到端测试）、sdk-import-test.mjs（SDK 验证）。复现方式：`npm i --ignore-scripts @earendil-works/pi-coding-agent` → 启动网关 → `node rpc-e2e-test.js`，预期输出 6 项 PASS。

方案经上述验证后，建议按第八章分期启动 M1；如对架构取舍有不同意见（如是否引入子 Agent 编排、是否迁移报告评审扩展），可在评审时确定范围后再动工。
