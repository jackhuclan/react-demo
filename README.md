# ReactDemo — C# 版 ReAct Agent 演示

![CI](https://github.com/jackhuclan/react-demo/actions/workflows/ci.yml/badge.svg)
![License](https://img.shields.io/badge/license-MIT-blue.svg)

一个用 C# (.NET 10) 实现的 **ReAct（Reasoning + Acting）智能体循环**演示项目，参考 [pi-mono](https://github.com/badlogic/pi-mono) 的 Agent 设计，完整演示了：

```
感知 -> 推理 -> 行动 -> 观察 ->（循环）-> 结束
```

以及 **上下文管理（Compaction）**：上下文逼近窗口上限时自动 LLM 摘要压缩，agent 无限对话不爆窗口。历次需求与设计决策见 [PRD.md](PRD.md)。

## 项目结构

```
ReactDemo/
├── Agent/                    # Agent 核心库（与 UI 无关，可独立复用）
│   ├── Agent.cs              # Agent 有状态封装：transcript、工具集、steering/followUp 队列、abort、事件订阅
│   ├── AgentLoop.cs          # 推理-行动循环核心（含每轮后的压缩检查点）
│   ├── AgentLoopConfig.cs    # 循环配置（最大轮次、结束决策、压缩配置等）
│   ├── AgentEvents.cs        # AgentEvent 事件定义（循环与 UI 解耦，含压缩事件）
│   ├── AgentMessages.cs      # 消息与内容块类型（System/User/Assistant/ToolResult/CompactionSummary）
│   ├── AgentTool.cs          # 工具抽象：JSON Schema 参数定义、并行/顺序执行、错误即观察
│   └── Compaction.cs         # 上下文压缩：阈值触发、切点算法、split turn、LLM 摘要、原位替换
├── Demo/                     # 演示层
│   ├── ConsoleEventPrinter.cs# 控制台事件打印（订阅 AgentEvent 渲染，含压缩事件）
│   ├── DemoTools.cs          # 示例工具 calculate
│   ├── MockLlm.cs            # 离线脚本化 LLM（mock / compact 模式，可应答摘要请求）
│   └── OpenAiLlm.cs          # OpenAI 兼容端点的流式 LLM 接入
├── Program.cs                # 入口：组装 Agent、注册钩子、运行演示
├── PRD.md                    # 历次需求记录（PRD 片段）
└── ReactDemo.slnx            # 解决方案
```

## 核心特性

- **ReAct 循环**：`AgentLoop.cs` 实现纯循环核心，对 UI 一无所知，全部通过 `AgentEvent` 事件解耦。
- **工具调用**：通过 OpenAI function calling 传递 JSON Schema 参数；支持并行/顺序两种执行模式；工具失败时置 `IsError` 而不是抛异常——**错误也作为观察喂回模型**。
- **钩子机制**：
  - `BeforeToolCall`：行动前拦截（权限确认挂载点，返回 `Block=true` 可拦截调用）；
  - `AfterToolCall`：行动后改写工具结果；
  - `FinishTurn`：每轮结束决策（强制 End / Continue）。
- **Steering / FollowUp 队列**：`Steer()` 在当前轮结束后立即注入消息；`FollowUp()` 仅在 agent 本来要停止时消费。
- **事件订阅**：`agent.Subscribe(...)` 返回 `IDisposable`，UI 渲染与循环完全解耦。
- **双模式运行**：
  - 默认：调用本地真实大模型（`.env` 配置，任意 OpenAI 兼容端点）；
  - `mock`：`dotnet run -- mock` 离线脚本化演示（含"错误即观察"与 steering 演示）；
  - `compact`：`dotnet run -- compact` 离线上下文压缩演示（极小窗口迫使阈值触发）。
- **上下文管理（Compaction）**：对应 pi 的 compaction 机制——
  - 阈值触发：`contextTokens > contextWindow - reserveTokens`，每轮工具结果落库后检查（`prepareNextTurn` 语义）；
  - 切点算法：从尾部累计 token 达 `keepRecentTokens` 确定保留起点，`toolResult` 永不单独丢弃（与 `toolCall` 成对保留），单回合超长自动 split turn（历史与回合前缀分别摘要后合并）；
  - LLM 摘要：结构化 Markdown（Goal/Progress/Key Decisions/Next Steps/...），携带上一次摘要做迭代式增量更新；
  - 替换语义：摘要转为一条 user 消息（`<summary>` 包装）放回上下文头部，system prompt 永不参与摘要；
  - 事件通知：`CompactionStartEvent / CompactionEndEvent`（reason：Manual/Threshold/Overflow），UI 可渲染压缩过程；
  - 手动压缩：`agent.CompactAsync(customInstructions)`（对应 pi 的 `/compact`）。

## 快速开始

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 配置

复制示例配置并填入实际值：

```bash
cp .env.example .env
```

`.env`（OpenAI 兼容格式）：

```env
OPENAI_BASE_URL=http://localhost:8000/v1   # 任意 OpenAI 兼容端点
OPENAI_API_KEY=sk-your-api-key
OPENAI_MODEL=Qwen2.5-7B-Instruct
OPENAI_MAX_TOKENS=12800
OPENAI_TEMPERATURE=0.6

# 可选：上下文压缩（不配置则用默认值 128000 / 16384 / 20000）
COMPACTION_CONTEXT_WINDOW=128000
COMPACTION_RESERVE_TOKENS=16384
COMPACTION_KEEP_RECENT_TOKENS=20000
```

> ⚠️ `.env` 含真实密钥，已在 `.gitignore` 中排除，请勿提交到仓库。

### 运行

```bash
# 真实模型模式
dotnet run

# 离线 mock 模式（无需 API Key）
dotnet run -- mock

# 离线上下文压缩演示（无需 API Key，观察 compaction_start / compaction_end 事件）
dotnet run -- compact
```

运行后会执行示例任务："先计算 12×34，再把结果乘以 2"，可以在控制台看到完整的推理-工具调用-观察过程，以及最终的 transcript 摘要。

## 设计要点

| 概念 | 说明 | 对应 pi 概念 |
|------|------|--------------|
| `Agent` | 有状态封装，持有 transcript 与工具集 | `Agent` |
| `AgentLoop` | 无状态推理-行动循环核心 | `agentLoop` |
| `AgentTool` | 工具抽象 + JSON Schema 定义 | `AgentTool` |
| `Steer()` / `FollowUp()` | 消息注入队列 | `agent.steer` / `agent.followUp` |
| `BeforeToolCall` / `AfterToolCall` | 行动前后钩子 | `beforeToolCall` 等 |
| `AgentEvent` | 事件流，UI 解耦 | `agent_events` |
| `Compaction` | 上下文压缩：阈值/切点/摘要/替换 | `compaction`（prepareNextTurn 集成） |
| `CompactAsync()` | 手动压缩入口 | `/compact` |

## License

MIT
