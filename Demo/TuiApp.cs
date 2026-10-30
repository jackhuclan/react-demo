using ReactDemo.Agent;
using Spectre.Console;

namespace ReactDemo.Demo;

// 命名空间成员（ReactDemo.Agent）优先于编译单元级 using，Agent 类别名必须放在 namespace 内
using Agent = ReactDemo.Agent.Agent;

/// <summary>TUI 启动选项。</summary>
public sealed record TuiOptions
{
    /// <summary>true → 使用离线脚本化 LLM（无需 API Key）。</summary>
    public bool UseMock { get; init; }

    /// <summary>强制新建会话（默认继续最近会话）。</summary>
    public bool ForceNewSession { get; init; }

    /// <summary>禁用技能加载。</summary>
    public bool NoSkills { get; init; }

    /// <summary>启动后自动提交的初始提示词。</summary>
    public string? InitialPrompt { get; init; }
}

/// <summary>
/// 交互式终端界面（对应 pi 的 modes/interactive + 自研 TUI 框架，演示级 Spectre.Console 复刻）。
/// 复刻 pi 的核心语义：
///   - REPL 提交循环（getUserInput → session.prompt）；
///   - slash 命令在提交处分发（/new /resume /compact /skills /skill: /tree /quit...）；
///   - 事件 → UI 映射（message_update 流式渲染、tool_execution 状态、compaction 通知）；
///   - 会话/技能/长期记忆全部集成。
/// 外围（自研组件树、行级差分渲染、多行编辑器、自动补全、overlay）以 Spectre 内置能力替代。
/// </summary>
public sealed class TuiApp
{
    private readonly TuiOptions _options;
    private readonly StreamFn _stream;
    private string _systemPrompt = "";
    private IReadOnlyList<MemoryLoader.MemoryFile> _memory = [];
    private SkillLoadResult _skills = SkillLoadResult.Empty;
    private Agent _agent = null!;
    private SessionManager _session = null!;
    private bool _exitRequested;
    private readonly List<string> _history = [];

    private TuiApp(TuiOptions options)
    {
        _options = options;
        _stream = options.UseMock ? MockLlm.Create() : OpenAiLlm.CreateStreamFn(OpenAiLlm.FromEnvironment());
    }

    public static async Task<int> RunAsync(TuiOptions options)
    {
        // TUI 是交互式界面，检测到输入重定向时给出提示（对应 pi 检测非 TTY 的处理）
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("[TUI] 检测到输入被重定向，交互式界面需要真实终端。请直接运行 dotnet run。");
            return 1;
        }
        return await new TuiApp(options).RunAsync();
    }

    private async Task<int> RunAsync()
    {
        try
        {
            // ---- 启动序列（对应 pi InteractiveMode.init()）--------------------
            _memory = Bootstrap.LoadMemory();
            _skills = Bootstrap.LoadSkills(_options.NoSkills);
            _session = Bootstrap.OpenSession(_options.ForceNewSession);

            var basePrompt = _options.UseMock
                ? "你是运行在终端里的演示助手，擅长使用工具回答问题。"
                : "你是运行在终端里的演示助手。需要计算时请使用 calculate 工具，不要心算；任务与技能描述匹配时，先用 read_file 工具加载技能文件再执行。";
            _systemPrompt = Bootstrap.BuildSystemPrompt(basePrompt, _memory, _skills);

            BuildAgent();

            // ---- 欢迎横幅 + 已加载资源总览 ------------------------------------
            RenderBanner();

            // 恢复历史（对应 pi renderInitialMessages）
            if (_session.EntryCount > 0)
            {
                _agent.RestoreTranscript(_session.BuildProjection());
                RenderInfo($"继续会话 [aqua]{_session.SessionId[..8]}[/]（恢复 {_session.EntryCount} 条记录）");
            }

            // 启动提示词自动提交（对应 pi --prompt 即首条消息）
            if (!string.IsNullOrWhiteSpace(_options.InitialPrompt))
            {
                await HandleInputAsync(_options.InitialPrompt.Trim());
            }

            // ---- REPL 主循环（对应 pi run() 的 while(true) getUserInput/prompt）----
            while (!_exitRequested)
            {
                string? input;
                try
                {
                    input = AnsiConsole.Prompt(
                        new TextPrompt<string>("[green]›[/]").AllowEmpty().ShowChoices(false));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
                {
                    break; // 非交互场景下 Prompt 失败 → 退出
                }
                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }
                _history.Add(input);

                try
                {
                    await HandleInputAsync(input.Trim());
                }
                catch (OperationCanceledException)
                {
                    RenderWarning("[已中止]");
                }
                catch (Exception ex)
                {
                    RenderError($"运行失败：{ex.Message}");
                }
            }

            RenderInfo("再见。（会话已保存，下次启动自动继续）");
            return 0;
        }
        catch (Exception ex)
        {
            RenderError($"TUI 启动失败：{ex.Message}");
            return 1;
        }
    }

    // -----------------------------------------------------------------------
    // 输入分发（对应 pi 的 setupEditorSubmitHandler）：slash 命令 / 普通对话
    // -----------------------------------------------------------------------
    private async Task HandleInputAsync(string text)
    {
        if (text.StartsWith('/'))
        {
            var command = text.Split(' ', 2)[0].ToLowerInvariant();
            switch (command)
            {
                case "/help":
                    RenderHelp();
                    return;
                case "/new":
                    NewSession();
                    return;
                case "/resume":
                    ResumeRecentSession();
                    return;
                case "/tree":
                    AnsiConsole.Write(new Rule("[dim]会话条目树[/]").RuleStyle("grey"));
                    Bootstrap.PrintSessionTree(_session);
                    return;
                case "/session":
                    RenderSessionInfo();
                    return;
                case "/skills":
                    RenderSkills();
                    return;
                case "/compact":
                    await CompactAsync(text.Length > 8 ? text[8..].Trim() : null);
                    return;
                case "/exit" or "/quit":
                    _exitRequested = true;
                    return;
                case "/skill:":
                    // 落到普通对话路径：提交前展开为 <skill> 文本块（对应 pi 的 _expandSkillCommand）
                    break;
                default:
                    RenderError($"未知命令 {command}，输入 /help 查看可用命令。");
                    return;
            }
        }

        // 普通对话 / /skill: 展开 → agent 循环
        var expanded = Skills.ExpandCommand(_skills.Skills, text);
        await RunAgentTurnAsync(expanded);
    }

    // -----------------------------------------------------------------------
    // 单次 agent 回合（对应 pi session.prompt → handleEvent 渲染）
    // -----------------------------------------------------------------------
    private async Task RunAgentTurnAsync(string text)
    {
        AnsiConsole.MarkupLineInterpolated($"[bold green]▌ 你[/] [dim]{Markup.Escape(FirstLine(text))}[/]");
        var printer = new TuiEventPrinter();
        using var subscription = _agent.Subscribe(printer.Print);
        await _agent.PromptAsync(text);
        AnsiConsole.WriteLine();
    }

    private async Task CompactAsync(string? customInstructions)
    {
        if (_agent.CompactionSettings is not { Enabled: true })
        {
            RenderWarning("压缩未启用。");
            return;
        }
        AnsiConsole.Status().Start("压缩上下文中…", _ => { });
        var result = await _agent.CompactAsync(customInstructions);
        if (result is null)
        {
            RenderWarning("压缩被跳过或失败（详见事件输出）。");
        }
        else
        {
            RenderInfo($"压缩完成：tokens {result.TokensBefore} → {result.TokensAfter}");
        }
    }

    private void NewSession()
    {
        _session = SessionManager.Create();
        BuildAgent(); // 对应 pi runtimeHost.newSession()：重建 session + rebind UI
        RenderInfo($"新会话 [aqua]{_session.SessionId[..8]}[/]");
    }

    private void ResumeRecentSession()
    {
        _session = SessionManager.ContinueRecent();
        BuildAgent();
        _agent.RestoreTranscript(_session.BuildProjection());
        RenderInfo($"继续会话 [aqua]{_session.SessionId[..8]}[/]（恢复 {_session.EntryCount} 条记录）");
    }

    /// <summary>重建 Agent 并重新接线（会话/订阅/工具）——对应 pi 的 rebindCurrentSession。</summary>
    private void BuildAgent()
    {
        _agent = new Agent(_systemPrompt)
        {
            Stream = _stream,
            Session = _session,
            CompactionSettings = _options.UseMock
                ? new CompactionSettings()
                : Bootstrap.RealModelCompactionSettings(),
        };
        _agent.AddTool(new CalculateTool());
        _agent.AddTool(new ReadFileTool());
    }

    // -----------------------------------------------------------------------
    // 渲染辅助（对应 pi 的组件渲染：header/footer/status/error）
    // -----------------------------------------------------------------------
    private void RenderBanner()
    {
        AnsiConsole.Write(
            new FigletText("ReactDemo").Color(_options.UseMock ? Color.Green : Color.Cyan).LeftJustified());
        AnsiConsole.MarkupLine("[dim]C# 版 ReAct Agent 演示 · 交互式终端（输入 /help 查看命令）[/]");
        AnsiConsole.WriteLine();

        var model = _options.UseMock ? "mock（离线脚本化 LLM）" : $"{OpenAiLlm.FromEnvironment().Model}";
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[dim]资源[/]");
        table.AddColumn("[dim]状态[/]");
        table.AddRow("模式", Markup.Escape(model));
        table.AddRow("会话", $"{_session.SessionId[..8]}（{(_session.EntryCount > 0 ? "已恢复" : "新建")}）");
        table.AddRow("技能", _skills.Skills.Count > 0
            ? string.Join(", ", _skills.Skills.Select(s => s.Name))
            : "[dim]无[/]");
        table.AddRow("长期记忆", _memory.Count > 0
            ? string.Join(", ", _memory.Select(m => Path.GetFileName(m.Path)))
            : "[dim]无（放置 AGENTS.md 可启用）[/]");
        table.AddRow("命令", "/help /new /resume /session /tree /skills /compact /skill:NAME /exit");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void RenderHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[dim]命令[/]");
        table.AddColumn("[dim]说明[/]");
        table.AddRow("/help", "显示本帮助");
        table.AddRow("/new", "新建会话（清空上下文）");
        table.AddRow("/resume", "继续最近的会话");
        table.AddRow("/session", "显示当前会话信息");
        table.AddRow("/tree", "打印会话条目树（id/parentId/leaf）");
        table.AddRow("/skills", "列出已加载的技能");
        table.AddRow("/compact [说明]", "手动触发上下文压缩（可带侧重点）");
        table.AddRow("/skill:NAME 参数", "显式调用技能（正文展开后提交）");
        table.AddRow("/exit 或 /quit", "退出");
        AnsiConsole.Write(table);
    }

    private void RenderSkills()
    {
        if (_skills.Skills.Count == 0)
        {
            RenderInfo("未加载任何技能（可放置到 .pi/skills/ 或 ~/.reactdemo/skills/）。");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[dim]名称[/]");
        table.AddColumn("[dim]来源[/]");
        table.AddColumn("[dim]描述[/]");
        foreach (var skill in _skills.Skills)
        {
            table.AddRow(Markup.Escape(skill.Name), skill.Source, Markup.Escape(FirstLine(skill.Description)));
        }
        AnsiConsole.Write(table);
    }

    private void RenderSessionInfo()
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[dim]项[/]");
        table.AddColumn("[dim]值[/]");
        table.AddRow("会话 ID", Markup.Escape(_session.SessionId));
        table.AddRow("文件", Markup.Escape(_session.SessionFile));
        table.AddRow("条目数", _session.EntryCount.ToString());
        table.AddRow("leaf", Markup.Escape(_session.LeafId));
        AnsiConsole.Write(table);
    }

    private static void RenderInfo(string message) => AnsiConsole.MarkupLineInterpolated($"[cyan]ℹ {message}[/]");
    private static void RenderWarning(string message) => AnsiConsole.MarkupLineInterpolated($"[yellow]⚠ {message}[/]");
    private static void RenderError(string message) => AnsiConsole.MarkupLineInterpolated($"[red]✗ {message}[/]");

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    /// <summary>
    /// 事件 → UI 渲染（对应 pi 的 handleEvent switch；流式输出用游标差量，
    /// 工具调用/压缩事件带状态色，对齐 pi 的 toolPendingBg/success/error 语义）。
    /// </summary>
    private sealed class TuiEventPrinter
    {
        private int _cursor;

        public void Print(AgentEvent e)
        {
            switch (e)
            {
                case TurnStartEvent:
                    AnsiConsole.Write(new Rule("[dim]new turn[/]").RuleStyle("grey").NoBorder());
                    break;

                case MessageStartEvent { Message: AssistantMessage }:
                    _cursor = 0;
                    AnsiConsole.Markup("[bold aqua]▌ 助手[/] ");
                    break;

                case MessageUpdateEvent { Message: AssistantMessage a }:
                    PrintAssistantDelta(a);
                    break;

                case MessageEndEvent { Message: AssistantMessage a2 }:
                    PrintAssistantDelta(a2);
                    _cursor = 0;
                    AnsiConsole.WriteLine();
                    break;

                case MessageStartEvent { Message: ToolResultMessage }:
                case MessageEndEvent { Message: ToolResultMessage }:
                    break; // 工具结果由 ToolExecutionEndEvent 打印

                case ToolExecutionStartEvent tool:
                    AnsiConsole.MarkupLineInterpolated(
                        $"[magenta]  ⚒ {tool.ToolName}[/] [dim]{Markup.Escape(tool.Arguments.ToJsonString())}[/]");
                    break;

                case ToolExecutionEndEvent toolEnd:
                    var result = Markup.Escape(toolEnd.Result.GetText());
                    if (toolEnd.IsError)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]    ↳ {result}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated($"[green]    ↳ {result}[/]");
                    }
                    break;

                case CompactionStartEvent c:
                    AnsiConsole.MarkupLineInterpolated($"[yellow]  ⟳ 压缩开始（{c.Reason}）…[/]");
                    break;

                case CompactionEndEvent cEnd:
                    if (cEnd.Summary is null)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[yellow]  ⟳ 压缩结束（{cEnd.Reason}）：{Markup.Escape(cEnd.ErrorMessage ?? "skipped")}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[yellow]  ⟳ 压缩结束（{cEnd.Reason}）：tokens {cEnd.TokensBefore} → {cEnd.TokensAfter}[/]");
                    }
                    break;

                case AgentEndEvent:
                    break;
            }
        }

        private void PrintAssistantDelta(AssistantMessage a)
        {
            var full = a.GetText();
            if (full.Length > _cursor)
            {
                AnsiConsole.Write(new Text(full[_cursor..]));
                _cursor = full.Length;
            }
        }
    }
}
