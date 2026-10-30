using DotNetEnv;
using ReactDemo.Agent;
using ReactDemo.Demo;

// ---------------------------------------------------------------------------
// C# 版 ReAct 演示：感知 -> 推理 -> 行动 -> 观察 循环。
//   默认模式：调用本地真实大模型（.env 配置，OpenAI 兼容端点）
//   mock 模式：dotnet run -- mock      离线脚本化演示（含"错误即观察"与 steering）
//   compact 模式：dotnet run -- compact  离线演示上下文压缩（阈值触发 + LLM 摘要 + 原位替换）
//   skills 模式：dotnet run -- skills    离线演示技能管理（摘要注入 + read 按需加载 + /skill: 展开）
//   附加参数：--new 强制新建会话（默认继续最近会话）；--tree 打印会话条目树；
//             --no-skills 禁用技能加载
// ---------------------------------------------------------------------------

Env.Load(Path.Combine(AppContext.BaseDirectory, ".env"));

var useMock = args.Contains("mock", StringComparer.OrdinalIgnoreCase);
var useCompactDemo = args.Contains("compact", StringComparer.OrdinalIgnoreCase);
var useSkillsDemo = args.Contains("skills", StringComparer.OrdinalIgnoreCase);
var noSkills = args.Contains("--no-skills", StringComparer.OrdinalIgnoreCase);
var forceNewSession = args.Contains("--new", StringComparer.OrdinalIgnoreCase);
var printTree = args.Contains("--tree", StringComparer.OrdinalIgnoreCase);
// 自定义提示词：--prompt "..."（省略时用内置演示提示词）
var promptIndex = Array.FindIndex(args, a => a.Equals("--prompt", StringComparison.OrdinalIgnoreCase));
var customPrompt = promptIndex >= 0 && promptIndex + 1 < args.Length ? args[promptIndex + 1] : null;

// ---- 长期记忆（对应 pi 的 context files：AGENTS.md 注入 system prompt）--------
var memoryFiles = MemoryLoader.LoadContextFiles(Environment.CurrentDirectory);
if (memoryFiles.Count > 0)
{
    Console.WriteLine($"已加载长期记忆: {string.Join(", ", memoryFiles.Select(f => f.Path))}");
}
var memorySection = memoryFiles.Count > 0
    ? "\n\n" + MemoryLoader.RenderProjectContext(memoryFiles)
    : "";

// ---- 技能管理（对应 pi 的 skills：两级渐进式披露）----------------------------
//   发现：项目 .pi/skills（优先，随仓库分发）> 用户 ~/.reactdemo/skills，同名 first-wins
//   注入：<available_skills> 元数据摘要进 system prompt；正文由模型用 read_file 按需读取
SkillLoadResult skillLoad;
if (noSkills)
{
    skillLoad = SkillLoadResult.Empty;
    Console.WriteLine("技能已禁用（--no-skills）");
}
else
{
    skillLoad = Skills.Load(
        (Skills.ProjectSkillsDir(Environment.CurrentDirectory), "project"),
        (Skills.UserSkillsDir, "user"));
    foreach (var skill in skillLoad.Skills)
    {
        Console.WriteLine($"已加载技能: {skill.Name} ({skill.Source}) <- {skill.FilePath}");
    }
    foreach (var diagnostic in skillLoad.Diagnostics)
    {
        Console.WriteLine($"[skills/{diagnostic.Type}] {diagnostic}");
    }
}
var skillsSection = skillLoad.Skills.Count > 0
    ? "\n\n" + Skills.FormatForPrompt(skillLoad.Skills)
    : "";

// ---- 会话管理：默认继续最近会话（无则新建），--new 强制新建 ---------------------
var session = forceNewSession
    ? SessionManager.Create()
    : SessionManager.ContinueRecent();

Agent agent;
if (useMock || useCompactDemo || useSkillsDemo)
{
    Console.WriteLine(useCompactDemo ? "=== 模式：离线 compact（上下文压缩演示）==="
        : useSkillsDemo ? "=== 模式：离线 skills（技能管理演示）==="
        : "=== 模式：离线 mock（脚本化 LLM）===");
    Console.WriteLine();

    if (useSkillsDemo)
    {
        // 技能演示：展示 system prompt 注入形态与 /skill: 显式展开，再跑一条
        // "模型自主 read_file 读技能正文 → 按技能作答" 的脚本化全链路
        var skillsPrompt = Skills.FormatForPrompt(skillLoad.Skills);
        Console.WriteLine("--- system prompt 中的技能摘要段 ---");
        Console.WriteLine(skillsPrompt.Length > 0 ? skillsPrompt : "（无可用技能）");
        Console.WriteLine();

        const string command = "/skill:report-format 请按该技能的模板汇报 12*34*2 的结果。";
        var expanded = Skills.ExpandCommand(skillLoad.Skills, command);
        Console.WriteLine("--- /skill: 显式调用展开 ---");
        Console.WriteLine(expanded.Length > 0 && expanded != command ? expanded : command);
        Console.WriteLine();

        agent = new Agent("你是运行在终端里的演示助手。任务与技能描述匹配时，先用 read_file 工具加载技能文件再执行。" + memorySection + skillsSection)
        {
            Stream = MockLlm.Create(),
        };
        agent.AddTool(new ReadFileTool());
        agent.AddTool(new CalculateTool());
    }
    else
    {
        agent = new Agent("你是运行在终端里的演示助手，擅长使用工具回答问题。" + memorySection + skillsSection)
        {
            Stream = MockLlm.Create(),
        };
        agent.AddTool(new CalculateTool());
        agent.AddTool(new ReadFileTool());
        // 演示 steering 队列：循环开始时被注入，模型可见两条用户消息
        agent.Steer(new UserMessage("追加要求：最后请给出计算结果的平方。"));
        if (useCompactDemo)
        {
            // 极小窗口迫使阈值触发：真实场景按模型窗口配置（默认 128000/16384/20000）
            agent.CompactionSettings = new CompactionSettings
            {
                ContextWindow = 64,
                ReserveTokens = 8,
                KeepRecentTokens = 24,
            };
        }
    }
}
else
{
    var options = OpenAiLlm.FromEnvironment();
    Console.WriteLine($"=== 模式：真实模型 {options.Model} @ {options.BaseUrl} ===");
    Console.WriteLine();
    agent = new Agent("你是运行在终端里的演示助手。需要计算时请使用 calculate 工具，不要心算；任务与技能描述匹配时，先用 read_file 工具加载技能文件再执行。" + memorySection + skillsSection)
    {
        Stream = OpenAiLlm.CreateStreamFn(options),
    };
    agent.AddTool(new CalculateTool());
    agent.AddTool(new ReadFileTool());

    // 压缩配置：可用环境变量覆盖默认值（不配置则用内置默认）
    agent.CompactionSettings = new CompactionSettings
    {
        ContextWindow = int.TryParse(Environment.GetEnvironmentVariable("COMPACTION_CONTEXT_WINDOW"), out var w) ? w : 128000,
        ReserveTokens = int.TryParse(Environment.GetEnvironmentVariable("COMPACTION_RESERVE_TOKENS"), out var r) ? r : 16384,
        KeepRecentTokens = int.TryParse(Environment.GetEnvironmentVariable("COMPACTION_KEEP_RECENT_TOKENS"), out var k) ? k : 20000,
    };
}

// 会话接线：新消息 append 进会话树、压缩事件转 compaction 条目；恢复历史用投影重放
agent.Session = session;
if (session.EntryCount > 0)
{
    Console.WriteLine($"继续会话 {session.SessionId[..8]}…（已恢复 {session.EntryCount} 条记录，文件 {session.SessionFile}）");
    agent.RestoreTranscript(session.BuildProjection());
}
else
{
    Console.WriteLine($"新会话 {session.SessionId[..8]}…（首次对话后写入 {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\.reactdemo\\sessions\\）");
}
Console.WriteLine();

// 事件订阅：循环核心对 UI 一无所知，全部通过 AgentEvent 解耦
using var _subscription = agent.Subscribe(ConsoleEventPrinter.Print);

// 行动前钩子（对应 pi 的 beforeToolCall）：权限确认挂载点
agent.BeforeToolCall = ctx =>
{
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine($"  [hook] beforeToolCall: {ctx.ToolCall.Name} {ctx.ToolCall.Arguments.ToJsonString()}");
    Console.ResetColor();
    return Task.FromResult<BeforeToolCallResult?>(null); // 返回 Block=true 可拦截
};

// 行动后钩子：可改写工具结果
agent.AfterToolCall = ctx => Task.FromResult<AfterToolCallResult?>(
    ctx.Result.IsError ? null : new AfterToolCallResult(ctx.Result.GetText() + "（已由 afterToolCall 钩子确认）"));

var prompt = useSkillsDemo
    ? "/skill:report-format 请按该技能的模板汇报 12*34*2 的结果。"
    : customPrompt ?? "请先计算 12*34，再把结果乘以 2，告诉我最终答案。";
// /skill:name 显式调用在提交用户输入前展开（对应 pi 的 _expandSkillCommand 时机）；
// 非技能命令原样透传
prompt = Skills.ExpandCommand(skillLoad.Skills, prompt);
Console.WriteLine($"用户: {prompt.Replace('\n', ' ')}");
Console.WriteLine();

try
{
    await agent.PromptAsync(prompt);

    // compact 演示：第二轮对话把上下文推过阈值，触发压缩（事件流里可见 compaction_start/end）
    if (useCompactDemo)
    {
        var prompt2 = "很好。请再计算 100+23，告诉我答案。";
        Console.WriteLine();
        Console.WriteLine($"用户: {prompt2}");
        Console.WriteLine();
        await agent.PromptAsync(prompt2);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("[已中止]");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[运行失败] {ex.Message}");
    Console.ResetColor();
    return 1;
}

Console.WriteLine();
Console.WriteLine("=== 最终 transcript ===");
foreach (var message in agent.Messages)
{
    var summary = message switch
    {
        SystemMessage sys => $"system: {sys.Text ?? "(无文本)"}"
            + (sys.ToolsAdded.Count > 0 ? $" | 新增工具: {string.Join(",", sys.ToolsAdded)}" : "")
            + (sys.ToolsRemoved.Count > 0 ? $" | 移除工具: {string.Join(",", sys.ToolsRemoved)}" : ""),
        UserMessage u => $"user: {u.GetText()}",
        AssistantMessage a => $"assistant ({a.StopReason}): {a.GetText()}"
            + string.Join("", a.Content.OfType<ToolCallContent>()
                .Select(c => $" | toolCall {c.Name}({c.Arguments.ToJsonString()})")),
        ToolResultMessage t => $"toolResult {t.ToolName}{(t.IsError ? " [isError]" : "")}: {t.GetText()}",
        CompactionSummaryMessage c => $"compactionSummary (tokensBefore={c.TokensBefore}):\n{Compaction.SummaryPrefix}{c.Summary}{Compaction.SummarySuffix}",
        _ => message.GetType().Name,
    };
    Console.WriteLine($"  {summary}");
}

// 会话持久化结果 + 可选的条目树打印
Console.WriteLine();
Console.WriteLine($"=== 会话已保存: {session.SessionFile}（共 {session.EntryCount} 条记录）===");
if (printTree)
{
    Console.WriteLine();
    Console.WriteLine("=== 会话条目树（id type 概要）===");
    PrintSessionTree(session);
}

return 0;

/// <summary>按 parentId 缩进打印会话树（演示 SessionManager 的树结构与分支关系）。</summary>
static void PrintSessionTree(SessionManager session)
{
    var roots = new List<SessionEntry>();
    var children = new Dictionary<string, List<SessionEntry>>();
    foreach (var entry in session.Entries)
    {
        if (entry.ParentId is { } parent)
        {
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }
            list.Add(entry);
        }
        else
        {
            roots.Add(entry);
        }
    }

    foreach (var root in roots)
    {
        PrintNode(root, 0);
    }
    return;

    void PrintNode(SessionEntry entry, int depth)
    {
        var indent = new string(' ', depth * 2);
        var marker = entry.Id == session.LeafId ? " <- leaf" : "";
        var detail = entry switch
        {
            MessageEntry m => m.Message switch
            {
                UserMessage u => $"user: {Truncate(u.GetText())}",
                AssistantMessage a => $"assistant: {Truncate(a.GetText())}",
                ToolResultMessage t => $"toolResult: {Truncate(t.GetText())}",
                SystemMessage => "system",
                _ => "",
            },
            CompactionEntry c => $"compaction: {Truncate(c.Summary, 40)} (tokensBefore={c.TokensBefore})",
            BranchSummaryEntry => "branch_summary",
            SessionInfoEntry s => $"session_info: {s.Name}",
            _ => "",
        };
        Console.WriteLine($"{indent}{entry.Id}  {marker}");
        if (detail.Length > 0) Console.WriteLine($"{indent}    {detail}");
        foreach (var child in children.GetValueOrDefault(entry.Id, []))
        {
            PrintNode(child, depth + 1);
        }
    }

    static string Truncate(string text, int max = 50) =>
        text.Length <= max ? text.Replace('\n', ' ') : text[..max].Replace('\n', ' ') + "…";
}
