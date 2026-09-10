# wordai911

基于 [Pi coding agent](https://www.npmjs.com/package/@earendil-works/pi-coding-agent) 重新开发的 Word Agent 模块：让 AI 编码智能体在 Microsoft Word 内读写文档、执行操作。

> 技术方案：[`docs/technical-plan.md`](docs/technical-plan.md)（POC v3 已验证通过 11/11 项端到端测试）
> 通道 B 协议规范：[`docs/protocol-b.md`](docs/protocol-b.md)

## 架构

四层架构、三条通信通道：

| 层 | 位置 | 职责 |
|---|---|---|
| 宿主层 | `src/WordAI.Addin`（VSTO，仅 Windows 构建） | Word 进程内插件、COM 操作、UI |
| 网关层 | `src/WordAI.Agent/Gateway` | 本地 TCP 网关（127.0.0.1:47611，JSON Lines） |
| 大脑层 | Pi 子进程（`src/WordAI.Agent/Pi`） | RPC 模式托管的 AI Agent |
| 扩展层 | `extension/`（TypeScript） | 注册 `word_*` 工具，经 TCP 直达网关 |

## 仓库结构

```
docs/                  技术方案与协议规范
src/WordAI.Agent/      C# AgentHelper 库（netstandard2.0 + net472，Linux 可构建）
src/WordAI.Agent.Tests/ C# 单元测试（net8.0 + xunit）
src/WordAI.Addin/      VSTO Word 插件（net472，需 Windows + VS + VSTO 工作负载）
extension/             TS 扩展 + 测试 + mock 网关
.github/workflows/     CI（Linux 构建 C# 库 + 跑双端测试）
```

## 构建与测试

```bash
# C# 库与单元测试（Linux/macOS/Windows 均可）
cd src && dotnet test WordAI.Agent.Tests

# TS 扩展（单元 + E2E，需 Node >= 20）
cd extension && npm install && npm test
```

VSTO 插件（`src/WordAI.Addin`）需在 Windows 上用 Visual Studio（含 VSTO 工作负载）构建。

## 版本

遵循语义化版本（SemVer），每个里程碑打 tag 推送。见 [Releases](https://github.com/bingjian999/wordai911/releases)。

## 许可

私有仓库，仅限授权协作者。
