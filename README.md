# ReactDemo — C# 版 ReAct Agent 演示

一个用 C# (.NET 10) 实现的 **ReAct（Reasoning + Acting）智能体循环**演示项目，参考 [pi-mono](https://github.com/badlogic/pi-mono) 的 Agent 设计，完整演示了：

```
感知 -> 推理 -> 行动 -> 观察 ->（循环）-> 结束
```

## 项目结构

```
ReactDemo/
├── Agent/                    # Agent 核心库（与 UI 无关，可独立复用）
│   ├── Agent.cs              # Agent 有状态封装：transcript、工具集、steering/followUp 队列、abort、事件订阅
│   ├── AgentLoop.cs          # 推理-行动循环核心
│   ├── AgentLoopConfig.cs    # 循环配置（最大轮次、结束决策等）
│   ├── AgentEvents.cs        # AgentEvent 事件定义（循环与 UI 解耦）
│   ├── AgentMessages.cs      # 消息与内容块类型（System/User/Assistant/ToolResult 等）
│   └── AgentTool.cs          # 工具抽象：JSON Schema 参数定义、并行/顺序执行、错误即观察
├── Demo/                     # 演示层
│   ├── ConsoleEventPrinter.cs# 控制台事件打印（订阅 AgentEvent 渲染）
│   ├── DemoTools.cs          # 示例工具 calculate
│   ├── MockLlm.cs            # 离线脚本化 LLM（mock 模式）
│   └── OpenAiLlm.cs          # OpenAI 兼容端点的流式 LLM 接入
└── Program.cs                # 入口：组装 Agent、注册钩子、运行演示
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
  - `mock`：`dotnet run -- mock` 离线脚本化演示（含"错误即观察"与 steering 演示）。

## 快速开始

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 配置

在项目根目录创建 `.env`（OpenAI 兼容格式）：

```env
OPENAI_API_KEY=sk-xxx
OPENAI_BASE_URL=https://api.openai.com/v1   # 或任意兼容端点
OPENAI_MODEL=gpt-4o                          # 或其他模型名
```

### 运行

```bash
# 真实模型模式
dotnet run

# 离线 mock 模式（无需 API Key）
dotnet run -- mock
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

## License

MIT
