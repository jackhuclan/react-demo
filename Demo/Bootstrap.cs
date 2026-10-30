using ReactDemo.Agent;

namespace ReactDemo.Demo;

// 命名空间成员（ReactDemo.Agent）优先于编译单元级 using，Agent 类别名必须放在 namespace 内
using Agent = ReactDemo.Agent.Agent;

/// <summary>
/// 演示层共享的启动装配（对应 pi 的 createAgentSessionRuntime 启动序列）：
/// 记忆/技能加载、system prompt 组装、会话打开、Agent 创建。
/// </summary>
public static class Bootstrap
{
    /// <summary>加载长期记忆（对应 pi 的 context files 发现），打印加载结果。</summary>
    public static IReadOnlyList<MemoryLoader.MemoryFile> LoadMemory()
    {
        var memoryFiles = MemoryLoader.LoadContextFiles(Environment.CurrentDirectory);
        if (memoryFiles.Count > 0)
        {
            Console.WriteLine($"已加载长期记忆: {string.Join(", ", memoryFiles.Select(f => f.Path))}");
        }
        return memoryFiles;
    }

    /// <summary>加载技能（项目 .pi/skills 优先于用户 ~/.reactdemo/skills，同名 first-wins），打印诊断。</summary>
    public static SkillLoadResult LoadSkills(bool noSkills)
    {
        if (noSkills)
        {
            return SkillLoadResult.Empty;
        }
        var result = Skills.Load(
            (Skills.ProjectSkillsDir(Environment.CurrentDirectory), "project"),
            (Skills.UserSkillsDir, "user"));
        foreach (var skill in result.Skills)
        {
            Console.WriteLine($"已加载技能: {skill.Name} ({skill.Source}) <- {skill.FilePath}");
        }
        foreach (var diagnostic in result.Diagnostics)
        {
            Console.WriteLine($"[skills/{diagnostic.Type}] {diagnostic}");
        }
        return result;
    }

    /// <summary>组装 system prompt：base + 记忆段（&lt;project_instructions&gt;）+ 技能摘要段（&lt;available_skills&gt;）。</summary>
    public static string BuildSystemPrompt(
        string basePrompt,
        IReadOnlyList<MemoryLoader.MemoryFile> memory,
        SkillLoadResult skills)
    {
        var sections = new List<string> { basePrompt };
        if (memory.Count > 0)
        {
            sections.Add(MemoryLoader.RenderProjectContext(memory));
        }
        var skillsSection = Skills.FormatForPrompt(skills.Skills);
        if (skillsSection.Length > 0)
        {
            sections.Add(skillsSection);
        }
        return string.Join("\n\n", sections);
    }

    /// <summary>打开会话：--new 强制新建，否则继续最近会话。</summary>
    public static SessionManager OpenSession(bool forceNew) => forceNew
        ? SessionManager.Create()
        : SessionManager.ContinueRecent();

    /// <summary>真实模型模式：读取 .env 并构造 OpenAI 兼容流式 LLM。</summary>
    public static Agent CreateRealModelAgent(string systemPrompt) =>
        new(systemPrompt)
        {
            Stream = OpenAiLlm.CreateStreamFn(OpenAiLlm.FromEnvironment()),
        };

    /// <summary>离线脚本化 LLM 模式。</summary>
    public static Agent CreateMockAgent(string systemPrompt) =>
        new(systemPrompt)
        {
            Stream = MockLlm.Create(),
        };

    /// <summary>压缩配置：可用环境变量覆盖默认值（不配置则用内置默认）。</summary>
    public static CompactionSettings RealModelCompactionSettings() => new()
    {
        ContextWindow = int.TryParse(Environment.GetEnvironmentVariable("COMPACTION_CONTEXT_WINDOW"), out var w) ? w : 128000,
        ReserveTokens = int.TryParse(Environment.GetEnvironmentVariable("COMPACTION_RESERVE_TOKENS"), out var r) ? r : 16384,
        KeepRecentTokens = int.TryParse(Environment.GetEnvironmentVariable("COMPACTION_KEEP_RECENT_TOKENS"), out var k) ? k : 20000,
    };

    /// <summary>按 parentId 缩进打印会话树（演示 SessionManager 的树结构与分支关系）。</summary>
    public static void PrintSessionTree(SessionManager session)
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
}
