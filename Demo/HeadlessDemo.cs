using ReactDemo.Agent;

namespace ReactDemo.Demo;

/// <summary>
/// 一次性脚本化演示（对应 pi 的 headless/non-interactive 运行模式）：
/// mock（错误即观察 + steering）、compact（压缩）、skills（技能全链路）。
/// </summary>
public static class HeadlessDemo
{
    public static async Task<int> RunAsync(
        string mode, bool forceNewSession, bool printTree, bool noSkills, string? customPrompt)
    {
        var memoryFiles = Bootstrap.LoadMemory();
        var skillLoad = Bootstrap.LoadSkills(noSkills);

        Console.WriteLine($"=== 模式：离线 {mode} ===");
        Console.WriteLine();

        var agent = Bootstrap.CreateMockAgent(
            "你是运行在终端里的演示助手，擅长使用工具回答问题。" + Bootstrap.BuildSystemPrompt("", memoryFiles, skillLoad));
        agent.AddTool(new CalculateTool());
        agent.AddTool(new ReadFileTool());

        if (mode == "mock")
        {
            // 演示 steering 队列：循环开始时被注入，模型可见两条用户消息
            agent.Steer(new UserMessage("追加要求：最后请给出计算结果的平方。"));
        }
        else if (mode == "compact")
        {
            // 极小窗口迫使阈值触发：真实场景按模型窗口配置（默认 128000/16384/20000）
            agent.CompactionSettings = new CompactionSettings
            {
                ContextWindow = 64,
                ReserveTokens = 8,
                KeepRecentTokens = 24,
            };
        }

        // 会话接线：新消息 append 进会话树、压缩事件转 compaction 条目；恢复历史用投影重放
        var session = Bootstrap.OpenSession(forceNewSession);
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

        using var _subscription = agent.Subscribe(ConsoleEventPrinter.Print);

        agent.BeforeToolCall = ctx =>
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [hook] beforeToolCall: {ctx.ToolCall.Name} {ctx.ToolCall.Arguments.ToJsonString()}");
            Console.ResetColor();
            return Task.FromResult<BeforeToolCallResult?>(null); // 返回 Block=true 可拦截
        };
        agent.AfterToolCall = ctx => Task.FromResult<AfterToolCallResult?>(
            ctx.Result.IsError ? null : new AfterToolCallResult(ctx.Result.GetText() + "（已由 afterToolCall 钩子确认）"));

        // 各模式的演示提示词；/skill:name 显式调用在提交前展开（对应 pi 的 _expandSkillCommand 时机）
        var prompt = mode switch
        {
            "skills" => "/skill:report-format 请按该技能的模板汇报 12*34*2 的结果。",
            "compact" => "请先计算 12*34，再把结果乘以 2，告诉我最终答案。",
            _ => customPrompt ?? "请先计算 12*34，再把结果乘以 2，告诉我最终答案。",
        };
        prompt = Skills.ExpandCommand(skillLoad.Skills, prompt);
        Console.WriteLine($"用户: {prompt.Replace('\n', ' ')}");
        Console.WriteLine();

        try
        {
            await agent.PromptAsync(prompt);

            // compact 演示：第二轮对话把上下文推过阈值，触发压缩（事件流里可见 compaction_start/end）
            if (mode == "compact")
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
            Bootstrap.PrintSessionTree(session);
        }

        return 0;
    }
}
