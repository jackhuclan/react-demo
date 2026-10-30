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

---

## R6：技能管理（Skills）+ 版本/包集中管理（2026-10-30）

### 背景

R5 之后 agent 已具备会话与记忆能力，但缺少 pi 的另一个核心扩展机制：**技能（skills）**——把领域知识（操作规范、模板、流程说明）以 `SKILL.md` 文件形式分发给模型按需使用。同时工程上需要补齐版本管理与 NuGet 中心化包管理（参照 wpf-float-agent 项目的实践）。

### 调研结论（D:/repo/front/pi，核心在 packages/coding-agent/src/core/skills.ts + utils/frontmatter.ts）

**总体设计：两级渐进式披露（progressive disclosure），无调度器**

| 通道 | 内容 | 触发 |
|------|------|------|
| system prompt `<skills>` 段 | 每个技能的 name + description + location 的 XML 列表（`<available_skills>`） | 启动/reload 时全量注入 |
| `read` 工具 | 模型读到 SKILL.md 完整正文 | **模型自主决定**（依据 description） |
| `/skill:name args` 斜杠命令 | 正文剥 frontmatter 后包成 `<skill ...>` 文本块作为 **user 消息** 注入 | 用户显式输入 |

- **没有 skill 工具、没有关键词匹配、没有向量检索**——触发 = 描述写得好 + 模型 agentic 判断 + 自己会读文件。
- **发现机制**：目录含 `SKILL.md` → 即"技能根"，**停止向下递归**；根级 `*.md` 仅顶层可作单文件技能；跳过 `.*` 与 `node_modules`；pi 另支持 `.gitignore`/`.ignore` 忽略规则与符号链接跟随。
- **多源优先级**（`resourcePrecedenceRank`）：CLI 显式路径 > project（`.pi/skills`、`.agents/skills` 祖先链，需项目受信任）> user（`~/.pi/agent/skills`、`~/.agents/skills`）> package > builtin；聚合时**同名 first-wins**（先到者胜，后到者产生 `type:"collision"` 诊断），同一物理文件（软链）静默去重。
- **SKILL.md 格式**：YAML frontmatter（`name` 缺省取父目录名；`description` 必填，缺失/空白 → 整条技能丢弃；`disable-model-invocation: true` → 不进 prompt 仅可显式调用）+ Markdown 正文（相对路径相对技能目录解析）。
- **校验规则**：name `^[a-z0-9-]+$`、≤64、首尾不得为 `-`、不得含 `--`；description ≤1024——不合规**仅告警不拒绝**；frontmatter 解析（剥 BOM → CRLF/CR 归一 → `---` 边界）。
- **prompt 注入格式**（`formatSkillsForPrompt`，Agent Skills 规范 XML）：

```
The following skills provide specialized instructions for specific tasks.
Use the read tool to load a skill's file when the task matches its description.
When a skill file references a relative path, resolve it against the skill directory ...

<available_skills>
  <skill>
    <name>pdf-tools</name>
    <description>Extract text and tables from PDF files.</description>
    <location>D:/.../.pi/skills/pdf-tools/SKILL.md</location>
  </skill>
</available_skills>
```

- **`/skill:` 展开**：`<skill name="..." location="...">\nReferences are relative to <dir>.\n\n<body>\n</skill>\n\n<args>`；未知技能原样透传；正文是 role:user 的普通文本消息（不是 system、无特殊 block）。
- **正文不缓存**：pi 刻意每次调用重新读盘（简单、无状态）。
- 可省略的外围：Package 分发、`resources_discover` 扩展事件、TUI 折叠渲染、settings 覆盖语法、`SourceInfo.origin` 维度。

### 设计方案（C# 复刻，单文件 `Agent/Skills.cs`）

- **数据模型**：`Skill`（Name/Description/FilePath/BaseDir/Source/DisableModelInvocation）、`SkillDiagnostic`（warning/collision）、`SkillLoadResult`。
- **`Skills.Load((dir, source)[])`**：按传入优先级顺序扫描聚合——同名 first-wins + `File.ResolveLinkTarget` 物理路径去重 + collision 诊断（对齐 pi 真实产品语义：**调用方把高优先级目录排前面**，绕开 pi `includeDefaults` 分支 user/project 顺序相反的坑）。
- **`LoadFromDir`**：SKILL.md 短路、根级 `*.md` 单文件技能、跳过 `.*`/`node_modules`；**`LoadFromFile`**：frontmatter 解析 + 校验（description 必填否则丢弃，name 不合规仅告警；`disable-model-invocation` 严格布尔语义）。
- **`ParseFrontmatter`**：照抄 pi 朴素算法（BOM 剥离 → 换行归一 → `startsWith("---")` + 第一个 `"\n---"` 边界）；YAML 用极简子集解析器（`key: value`、`key: |` 块标量、`#` 注释）——SKILL.md 实际只用这些。
- **`FormatForPrompt`**：`<available_skills>` XML（XML 五字符转义；排除 disable-model-invocation；空列表返回空串不产生空 section）。
- **`ExpandCommand`**：`/skill:name args` → 读文件剥 frontmatter → `<skill>` 文本块；未知技能透传。
- **`ReadBody`**：每次读盘不缓存（对齐 pi 刻意设计）。
- **目录约定**：项目 `<cwd>/.pi/skills`（随仓库分发）> 用户 `~/.reactdemo/skills`。
- **集成**：
  - system prompt 组装：base + 记忆段（R5）+ 技能摘要段；
  - 新增 `ReadFileTool`（Demo/DemoTools.cs，限制对齐 pi：2000 行/单行 2000 字符）作为正文按需加载通道，所有模式注册；
  - `Program`：启动加载技能并打印（含诊断）；`/skill:` 在提交用户输入前展开（对应 pi 的 `_expandSkillCommand` 时机）；`--no-skills` 禁用；
  - **MockLlm 技能脚本**（`skills` 离线模式）：模拟"看到摘要 → 自主 read_file → 拿到正文 → calculate → 按技能模板作答"全链路，并把流式输出重构为可复用的 `EmitTurn`；
  - 仓库自带演示技能 `.pi/skills/report-format/SKILL.md`（固定模板汇报计算结果）。

### 工程化：版本与包管理（参照 wpf-float-agent）

- **`Directory.Build.props`**：Version（1.0.0，同时作用于 AssemblyVersion/FileVersion/NuGet 包版本）、Authors/Company、Product/Description、仓库地址、MIT 许可、`IncludeSourceRevisionInInformationalVersion`（源码修订号注入 ProductVersion）。
- **`Directory.Packages.props`**：`ManagePackageVersionsCentrally=true`、禁用版本 override，`DotNetEnv 3.2.0` / `OpenAI 2.14.0` 统一维护；csproj 中 PackageReference 只写 Include。
- 构建产物验证：`FileVersion 1.0.0.0`、`ProductVersion 1.0.0+<git-sha>`。

### 实现中修复的问题

1. `ParseFrontmatter` 返回值与局部变量 `frontmatter`（string[]）重名导致类型错乱——改名为 `fields`。
2. `EmitTurn` 是异步迭代器，消费侧必须 `await foreach`；技能分支漏赋值 `toolCall2 = null` 引发 CS0165。

### 与 pi 的差异（演示规模取舍）

| 项 | pi | 本项目 |
|----|----|--------|
| 忽略规则 | `.gitignore`/`.ignore`/`.fdignore` + `prefixIgnorePattern` | 未实现（仅跳 `.*`/node_modules） |
| 符号链接 | follow + realpath 去重 | realpath 去重保留（`ResolveLinkTarget`），扫描不 follow |
| YAML | 完整 yaml 包 | 极简子集（key: value / 块标量 / 注释） |
| 技能来源 | CLI/settings/package/builtin/扩展事件 | project + user 两级 |
| system prompt diff | section 化 + 增量 patch 消息 | 每次组装完整 prompt（技能集合固定） |
| 项目信任门控 | projectTrusted 才加载项目技能 | 未实现（本地演示） |

### 验收标准

- `dotnet run -- skills --new`：打印技能发现结果、`<available_skills>` 注入形态、`/skill:` 展开文本，并完整跑通"read_file 读技能 → calculate → 按模板作答"。
- 真实模型：`--prompt` 触发后模型自主 read_file 加载技能并按模板输出。
- 同名冲突：用户目录放同名技能 → project 胜出 + collision 诊断。
- `--no-skills`：禁用加载，system prompt 无技能段。
- mock / compact 回归正常；Release 构建 0 错误；CI 绿灯。
- 程序集元数据来自 Directory.Build.props（版本 1.0.0 + git sha）。

### 状态

✅ 已完成

---

## R7：交互式 TUI + System.CommandLine 命令路由（2026-10-30）

### 背景

R6 之后所有能力（会话/压缩/记忆/技能）只有 headless 一次性演示入口，缺少 pi 的核心使用形态：**交互式终端（TUI）**。同时入口需要结构化命令路由，把各演示分支（mock/compact/skills）从 `if(args.Contains(...))` 升级为 System.CommandLine 子命令，TUI 成为默认入口。

### 调研结论（D:/repo/front/pi，核心在 packages/tui + modes/interactive）

**技术栈**：pi **没有用任何第三方 TUI 框架**——自研 `@earendil-works/pi-tui`（约 13k 行，唯一运行时依赖是 marked），底层直接操作 ANSI：组件树 + 每帧 `render(width)=>string[]` + **行级差分渲染**（只重画变化行）；双渲染器（主屏 scrollback / 备用屏受约束布局）；CSI 2026 同步输出防闪烁；光标用零宽 APC 标记定位（IME 服务）。

**应用层中枢**：`interactive-mode.ts` 单文件 6904 行，职责 = 组件树组装 + 事件映射 + slash 命令分发 + 退出流程。

**完整数据流**：
```
stdin(raw mode) → Editor（多行编辑：换行/提交/历史/粘贴占位符）
  → onSubmit（3096 行）："/xxx" → 命令处理器；"!cmd" → bash；
    streaming 中 → prompt(text, steer)；否则 → 主循环 session.prompt()
  → AgentSession 循环 → _emit(event) → handleEvent switch（3297–3685 行）
  → 操作组件树 → ui.requestRender()（16ms 节流 + 输入抢占）→ 差分写终端
```

**关键映射（事件 → UI）**：turn_start → spinner；message_start/update → AssistantMessage 组件整段重建（靠 (text,width) 缓存摊平成本）；toolCall 出现 → 独立 ToolExecutionComponent（pendingTools Map 按 toolCallId 索引，背景色表达 pending/success/error，Ctrl+O 折叠展开）；tool_execution_end → updateResult + 从 pendingTools 删除；agent_end → 清 spinner；queue_update → dock 区渲染 steering/followUp 队列；compaction → 压缩 spinner + 结束后按新 context 重渲。

**slash 命令体系**：`BUILTIN_SLASH_COMMANDS` 清单（settings/model/tree/thinking/export/import/copy/name/session/new/compact/resume/reload/quit...）**只用于自动补全**；实际分发是 onSubmit 里一长串 if。动态命令来源：prompt 模板、extension 注册、`skill:<name>`。自动补全：`/` 开头无空格 → 命令名模糊匹配；`@` → fd 文件补全；下拉列表直接追加在编辑器渲染输出之后。

**编辑器提交语义**（editor.ts）：Enter 提交 / Shift+Enter·Ctrl+J 换行（不支持的终端用 `\`+Enter 兼容）；Up/Down 仅在第一可视行才翻历史（多行冲突的经典解法）；bracketed paste，大粘贴（>10 行或 >1000 字符）插 `[paste #N]` 占位符提交时展开。

**启动/退出**：main.ts 解析 CLI → runtime（extensions/skills/context files）→ SessionManager.create/open/continueRecent → InteractiveMode.run()：init（信号处理、组件挂载、raw mode、资源总览、rebindCurrentSession、renderInitialMessages 回放历史）→ `while(true) getUserInput → session.prompt`；/quit 或空 Ctrl+D → drainInput → ui.stop → 打印 `pi --session <uuid>` 恢复命令；Ctrl+C 两次退出（第一次清空编辑器）。

**复刻优先级**：必须复刻 = 事件→UI 映射、slash 分发、提交语义、footer/spinner；可简化 = 差分渲染器（Spectre 下整屏重绘）；可省略 = overlay 栈、alt-screen 布局引擎、图片协议、kill-ring/undo、extension/widget、session 树 UI。

### 设计方案（C# 复刻，Spectre.Console + System.CommandLine）

- **`Demo/TuiApp.cs`**（交互式 TUI）：
  - REPL 主循环 = `AnsiConsole.Prompt(TextPrompt "›")` → `HandleInputAsync`（对应 pi 的 run() 主循环 + onSubmit 分发）；检测 `Console.IsInputRedirected` 非交互时提示退出（对应 pi 的 TTY 检测）；
  - slash 命令：`/help /new /resume /session /tree /skills /compact [说明] /skill:NAME 参数 /exit /quit`（demo 子集；`/skill:` 落到对话路径先 ExpandCommand 再提交，与 pi 的展开时机一致）；`/new`、`/resume` 重建 Agent 并重接会话（对应 pi 的 runtimeHost.newSession / rebindCurrentSession）；
  - 事件 → UI（内嵌 `TuiEventPrinter`，对齐 pi handleEvent 语义）：assistant 流式游标差量输出、工具调用 `⚒ name(args)` + 结果状态色（绿/红）、压缩事件黄色通知、TurnStart 无边框分隔；
  - 启动横幅：FigletText + 资源总览表（模式/会话/技能/长期记忆/命令，对应 pi 的 loaded resources 分组展示）；
  - `BuildAgent()` 统一重建（对应 pi rebind）：system prompt = base + 记忆 + 技能摘要；工具 calculate + read_file；真实模型用环境变量压缩配置。
- **`Demo/Bootstrap.cs`**（共享启动装配，对应 pi createAgentSessionRuntime）：记忆加载、技能加载（含诊断打印）、BuildSystemPrompt、OpenSession、CreateRealModelAgent/CreateMockAgent、RealModelCompactionSettings、PrintSessionTree（自 Program 迁出）。
- **`Demo/HeadlessDemo.cs`**：原 headless 流程迁入（mock/compact/skills 三分支复用 Bootstrap），TUI 不再重复。
- **`Program.cs`**（System.CommandLine 2.0.14 GA）：**默认 root action = TUI**；`mock / compact / skills` 为子命令（各带 `--new/--tree/--no-skills/--prompt`）；`--help` 自动生成、`--version` 自动输出 `1.0.0+<git-sha>`（版本来自 R6 的 Directory.Build.props）。
- **新依赖**（经 CPM 加入 Directory.Packages.props）：`Spectre.Console 0.57.2`、`System.CommandLine 2.0.14`。

### 实现中修复的问题

1. **C# 名字查找坑**：`ReactDemo.Demo` 内裸写 `Agent` 被解析为**命名空间** `ReactDemo.Agent`（外层命名空间成员优先于编译单元级 using 别名）——别名 `using Agent = ReactDemo.Agent.Agent;` 必须放在 `namespace` 声明**内部**才生效。
2. System.CommandLine GA API 与 beta 文档差异：`Command.Subcommands`（非 Commands）、`rootCommand.Parse(args).InvokeAsync()`（非 rootCommand.InvokeAsync）、`SetAction(Func<ParseResult, Task<int>>)`。通过包内 XML 文档 dump 确认真实 API 面。
3. `dotnet add package` 在 CPM 下自动把版本写入 Directory.Packages.props（csproj 保持无版本）。

### 与 pi 的差异（演示规模取舍）

| 项 | pi | 本项目 |
|----|----|--------|
| TUI 框架 | 自研 ANSI 组件树 + 行级差分渲染 | Spectre.Console 直接输出（整帧重绘） |
| 编辑器 | 多行编辑/粘贴占位符/kill-ring/undo | 单行 TextPrompt（终端自带历史） |
| 自动补全 | 命令 + `@`文件 + 参数级 | 无（/help 展示命令表） |
| 命令集 | 20+ 内置 + extension 动态 | 9 个核心命令 |
| 流式中插话 | steer/followUp 队列 + dock 区显示 | 无（REPL 单回合同步） |
| footer | token 统计/上下文百分比/git 分支 | 资源总览在启动横幅（不驻留） |
| 退出恢复提示 | `pi --session <uuid>` | 提示下次启动自动继续（ContinueRecent 语义） |

### 验收标准

- `dotnet run -- --help`：显示根命令（TUI 默认）与 mock/compact/skills 子命令。
- `dotnet run -- --version`：输出 `1.0.0+<git-sha>`。
- `dotnet run -- mock / compact / skills`：headless 回归全部通过。
- TUI：真实终端进入交互循环，slash 命令可用；输入重定向时优雅退出。
- Release 构建 0 错误；CI 绿灯。

### 状态

✅ 已完成
