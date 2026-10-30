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

---

## R5：会话管理与长短期记忆（2026-10-30）

### 背景

R4 之后 agent 具备了单次运行内的完整能力，但进程结束上下文即丢失：无法继续上次对话、无法回看/分支历史。pi 有一套成熟的会话管理（session tree + JSONL 持久化）与记忆机制，需要调研后在 C# 侧复刻。

### 调研结论（D:/repo/front/pi，核心在 packages/coding-agent/src/core/session-manager.ts）

**A. 会话管理：append-only JSONL 会话树**

1. **持久化**：`~/.pi/agent/sessions/<编码后的cwd>/<ISO时间戳>_<uuidv7>.jsonl`；每行一个 JSON；首行固定 session header（type/id/timestamp/cwd）；坏行解析时跳过；懒写盘（首条消息才建文件）。
2. **树结构**：每个 entry 有 `id`（8 位 hex）/`parentId`，leaf 指针标记当前位置；**追加 = leaf 下挂子节点并推进 leaf；分支 = 把 leaf 指回历史节点，历史绝不修改/删除**。
3. **条目类型**（11 种）：message / compaction / branch_summary / custom / custom_message / context_edit / label / session_info / usage / thinking_level_change / model_change。核心为前三种。
4. **投影算法四件套**（恢复上下文的关键）：
   - `buildSessionPath`：从 leaf 沿 parentId 回溯到根；
   - `buildContextEntries`：压缩感知——`[最新压缩条目] + [firstKeptEntryId 起至压缩点（去 system）] + [压缩点之后全部]`；
   - `sessionEntryToContextMessages`：条目 → 模型消息（compaction 条目 → `[systemMessage 快照, 摘要消息]`；branch_summary → 包裹标签的 user 消息）；
   - `buildSessionProjection`：应用 context_edit，仅最新压缩条目产出摘要消息（index>0 置空）。
5. **API 面**：`create / open / continueRecent / list / appendMessage / appendCompaction / appendBranchSummary / branch / resetLeaf / getBranch / buildSessionProjection` 等。

**B. 记忆机制：pi 没有独立 "memory" 子系统，由三个正交机制构成**

| 机制 | 形态 | 角色 |
|------|------|------|
| Context Files | 用户手写 `AGENTS.md`/`CLAUDE.md`（全局 `~/.pi/agent/` + 项目目录逐级向上发现），注入 system prompt 的 `<project_instructions>` 段 | **长期记忆**（纯手动维护，运行时不自动改写） |
| 会话树 + Compaction | JSONL transcript + LLM 结构化摘要（Goal/Progress/Decisions/Next Steps，带 previousSummary 迭代更新） | **短期记忆**（会话内工作记忆） |
| Branch Summary | 导航离开分支时 LLM 生成摘要条目 | 分支级记忆 |

**C. 数据流**：启动 → ResourceLoader 发现 context files → SessionManager.create/continueRecent → 用户输入 → 循环执行（每个 message_end 事件 appendMessage 持久化；阈值触发 compaction → appendCompaction 且快照 systemMessage）→ 下次启动 open/continueRecent → buildSessionProjection 重放出与中断前一致的上下文。

### 设计方案（C# 复刻，演示规模裁剪）

- **`Agent/SessionManager.cs`**：SessionHeader + 4 种条目（message/compaction/branch_summary/session_info）+ JSONL 读写（懒写盘、坏行容错）+ 树索引 + `GetBranch / BuildContextEntries / BuildProjection` 三算法 + `Create / Open / ContinueRecent / List` 工厂 + `AppendMessage / AppendCompaction / AppendBranchSummary / AppendSessionInfo / Branch / ResetLeaf`。
- **`Agent/Memory.cs`**：`MemoryLoader.LoadContextFiles(cwd)`（全局 `~/.reactdemo/AGENTS.md` 优先，项目目录逐级向上，override 文件优先）+ `RenderProjectContext()` 渲染 `<project_instructions>` 段。
- **集成**（对应 pi 的 AgentSession._handleAgentEvent 挂钩）：
  - `agent.Session = session` 即接入；`RunWithLifecycleAsync` 运行前把 transcript 中未登记消息补录，Dispatch 中 `MessageEndEvent → AppendMessage`；
  - `CompactionEndEvent` 新增 `FirstKeptMessage` 与 `SystemMessages`：前者定位 `firstKeptEntryId`，后者是压缩边界的 system prompt 快照（对齐 pi 的 `CompactionEntry.systemMessage`），恢复会话时随摘要一起重放；
  - `Program` 默认 `ContinueRecent()`（`--new` 强制新建），恢复时 `RestoreTranscript(session.BuildProjection())`；长期记忆在组装 system prompt 时注入。
- **消息 ↔ JSON 编解码**：手写 DTO 映射（system/user/assistant/toolResult/compactionSummary 五种角色 + toolCall 内容块）。

### 实现中发现并修复的问题

1. **投影条件写反**：初版误写为 `i < Count-1` 跳过压缩条目，导致恢复时摘要丢失——pi 规则是**仅 index 0（最新）压缩条目产出摘要**，已修正为 `i > 0` 跳过。
2. **压缩摘要消息被重复持久化**：运行前同步时未跳过 `CompactionSummaryMessage`，导致会话树里同时存在 compaction 条目和摘要消息条目——已在同步处按类型排除。
3. **恢复后 system prompt 丢失**：system 消息位于被摘要区间，投影不含——按 pi 方案在压缩条目中快照 system 消息，投影重放时前插。
4. **消息→条目映射**：压缩切点需要消息的 entryId——用 `ConditionalWeakTable<AgentMessage, string>` 建立引用映射（record 值相等会误判，必须引用语义），投影重建的消息同样登记。

### 与 pi 的差异（演示规模取舍）

| 项 | pi | 本项目 |
|----|----|--------|
| 条目类型 | 11 种 | 4 种（message/compaction/branch_summary/session_info） |
| 会话 id | uuidv7 | `Guid.CreateVersion7()`（.NET 10 原生 uuidv7） |
| context_edit / forkFrom / createBranchedSession | 有 | 未实现（留作扩展点） |
| list 枚举 | 并发流式 + 1MB header 扫描上限 | 全量解析（演示规模） |
| system prompt sections diff | 命名 section + 增量发送 | 整段注入（未做 diff） |
| 记忆更新 | 纯用户手动（+ /reload） | 同（无自动提炼） |

### 验收标准

- `dotnet run -- mock` 连续两次：第二次打印"继续会话"并恢复历史，同一 JSONL 文件追加记录。
- `dotnet run -- compact --new`：压缩条目（含 system 快照）正确写入 JSONL；后续继续会话时投影重放出 `[system, 摘要, 保留消息]`。
- `dotnet run -- mock --tree`：打印会话条目树（id/类型/概要/leaf 标记）。
- 项目目录放 `AGENTS.md` 后运行打印"已加载长期记忆"并注入 system prompt。
- 真实模型模式：新会话 + 继续会话均正常对话（恢复后模型基于历史直接作答）。
- Release 构建 0 警告 0 错误；CI 绿灯。

### 状态

✅ 已完成
