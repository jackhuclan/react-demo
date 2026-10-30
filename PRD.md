# PRD — ReactDemo 需求记录

本文件按时间顺序记录项目的历次需求（PRD 片段），作为项目演进历史与后续迭代依据。

---

## R1：项目文档与首次发布（2026-10-30）

### 背景

项目是参考 [pi](https://github.com/badlogic/pi-mono) 设计的 C# 版 ReAct Agent 演示（.NET 10 控制台），代码已完成但缺少文档，且尚未纳入版本管理。

### 需求

1. 编写 `README.md`：项目简介、项目结构、核心特性、快速开始、设计要点（与 pi 概念对照表）。
2. 初始化 Git 仓库并推送到 `https://github.com/jackhuclan/react-demo.git`。

### 方案

- README 基于代码实际内容撰写（Agent/ 核心库 6 文件 + Demo/ 演示层 4 文件）。
- `.gitignore` 排除 `bin/`、`obj/`、`.env`（含真实密钥）。

### 验收标准

- GitHub 仓库可访问，README 在首页正常渲染。
- 密钥文件 `.env` 不入库。

### 状态

✅ 已完成（首次提交 `df5563a`）

---

## R2：环境配置模板、CI 徽章与开源协议（2026-10-30）

### 背景

新用户不知道如何配置 `.env`；仓库缺少 CI 与 License 标识。

### 需求

1. 补充 `.env.example`（真实 `.env` 保留在本地，经确认不提交，避免密钥泄露）。
2. README 顶部加 CI / License 徽章。
3. 添加 MIT `LICENSE` 文件。

### 方案

- CI 徽章指向真实 workflow，因此同时创建 `.github/workflows/ci.yml`（checkout + setup-dotnet 10 + restore + build）。
- README 配置一节改为 `cp .env.example .env` 引导，并标注 `.env` 禁止提交。

### 验收标准

- `.env.example` 入库且不含真实密钥。
- 徽章可点击且状态真实。

### 状态

✅ 已完成（提交 `69bad2c`）

---

## R3：解决方案文件与 CI 修复加固（2026-10-30）

### 背景

CI 连续失败且只显示 "exit code 1"（匿名无法拉取日志）；需要把 `ReactDemo.csproj` 纳入 `ReactDemo.slnx` 解决方案管理。

### 需求

1. 创建 `ReactDemo.slnx` 并包含 `ReactDemo.csproj`。
2. 修复 CI 失败并按 wpf-float-agent 项目的 workflow 实践优化。

### 根因分析（Docker Linux 容器复现）

`ReactDemo.csproj` 中 `<None Include=".env" CopyToOutputDirectory="PreserveNewest" />` 在 CI 的 Linux 环境因 `.env` 不存在报 **MSB3030**（本地 Windows 因存在 `.env` 而侥幸通过）。

### 方案

- csproj 的 `.env` 条目加 `Condition="Exists('.env')"`（跨平台写法）。
- 新增 `ReactDemo.slnx`，CI 改为 `dotnet restore/build ReactDemo.slnx`。
- `ci.yml` 参照 wpf-float-agent 重写：restore/build 失败时把错误行提升为 GitHub annotation；NuGet 缓存；`concurrency` 取消旧任务；`paths-ignore` 纯文档提交不触发；`permissions: contents: read` 最小权限；actions 升级（checkout@v7 / setup-dotnet@v6 / cache@v6）。
- 顺带修复：`.gitignore` 补 `.vs/`、`.workbuddy/`。

### 验收标准

- 本地 Windows 与 Linux 容器（无 `.env` 场景）双端构建通过。
- CI 绿灯。

### 状态

✅ 已完成（提交 `54d8702`，CI Build success）

---

## R4：上下文管理（Compaction）模块（2026-10-30）

### 背景

多轮对话会持续增长上下文，最终超出模型窗口。pi 项目有一套成熟的上下文压缩（compaction）机制，需要在理解其设计后移植到本项目。

### 需求

1. 调研 `D:\repo\front\pi` 的上下文管理模块设计。
2. 用 C# 复刻核心机制并集成进 ReAct 循环。
3. 更新 README；新增 `PRD.md` 记录历次需求。

### 调研结论（pi 设计要点）

- **触发**：`contextTokens > contextWindow - reserveTokens`（绝对 token 预算，默认 reserve=16384）。
- **触发时机**：工具结果落库后、下一条 assistant 响应前（`prepareNextTurn`）；另有 manual（/compact）与 overflow（溢出恢复）两类原因。
- **切点算法**：从尾部向前累计 token 达到 `keepRecentTokens`（默认 20000）确定保留起点；`toolResult` 永不作为切点（与 toolCall 成对保留）；单个回合超长时走 **split turn**（历史与回合前缀分别摘要后合并）。
- **摘要**：独立一次 LLM 调用，结构化 Markdown（Goal/Constraints/Progress/Key Decisions/Next Steps/Critical Context）；携带 `previousSummary` 迭代式增量更新；摘要包在 `<conversation>` 标签中防止模型续写。
- **替换**：压缩摘要转为一条 user 消息（`<summary>` 标签包装）放回上下文头部；system prompt 永不参与摘要。

### 方案（C# 实现）

- 新增 `Agent/Compaction.cs`：`CompactionSettings`（ContextWindow/ReserveTokens/KeepRecentTokens，默认 128000/16384/20000）、token 启发式估算（chars/4）、`Prepare`（切点 + split turn 判定）、`SerializeConversation`、三段摘要 prompt（首次/增量/回合前缀）、`RunAsync`（准备 → LLM 摘要 → 原位替换）。
- 新增 `CompactionSummaryMessage` 消息类型与 `CompactionStartEvent/CompactionEndEvent` 事件（reason：Manual/Threshold/Overflow）。
- `AgentLoop`：每轮 `TurnEnd` 后检查阈值（对应 pi 的 prepareNextTurn 集成），带"无新消息不重复压缩"防死循环保护；循环返回最终 transcript。
- `Agent`：`CompactionSettings` 属性（默认启用）+ `CompactAsync()` 手动压缩入口（reason=Manual，支持自定义侧重点）。
- `OpenAiLlm`：`CompactionSummaryMessage` 转为 user 消息发给 LLM。
- `MockLlm`：识别摘要请求返回固定结构化摘要（离线演示）。
- `Program.cs`：新增 `dotnet run -- compact` 离线演示模式（极小窗口迫使阈值触发）；真实模式支持 `COMPACTION_CONTEXT_WINDOW / COMPACTION_RESERVE_TOKENS / COMPACTION_KEEP_RECENT_TOKENS` 环境变量覆盖。
- 顺带修复既有缺陷：assistant 响应此前未写入循环工作上下文（只进 newMessages），导致 LLM 上下文缺失 toolCall、token 估算漏算——已对齐 pi 行为。

### 与 pi 的差异（演示规模取舍）

| 项 | pi | 本项目 |
|----|----|--------|
| token 估算 | 真实 usage 优先 + 尾部启发式 | 纯 chars/4 启发式（StreamFn 未透出 usage，留作扩展点） |
| transcript 表示 | 会话树持久化，压缩只改投影 | 内存 transcript 原位替换 |
| overflow 恢复 | 溢出错误 → 压缩 → 重试一次 | 未实现（保留枚举位） |
| 文件操作追踪 | `<read-files>/<modified-files>` 追加到摘要 | 未实现（演示工具无文件读写） |
| 摘要输出预算 | `min(0.8*reserveTokens, model.maxTokens)` | 未限制（StreamFn 无该参数） |

### 验收标准

- `dotnet run -- compact` 离线演示：阈值触发 → 摘要 → 原位替换 → agent 基于压缩后上下文继续正确完成任务 → 二次压缩走增量摘要路径。
- `dotnet run -- mock` 回归：默认配置不触发压缩，原流程不变。
- `dotnet build ReactDemo.slnx` 0 警告 0 错误；CI 绿灯。

### 状态

✅ 已完成
