using DotNetEnv;
using ReactDemo.Agent;
using ReactDemo.Demo;

// ---------------------------------------------------------------------------
// C# 版 ReAct 演示：感知 -> 推理 -> 行动 -> 观察 循环。
//   默认模式：调用本地真实大模型（.env 配置，OpenAI 兼容端点）
//   mock 模式：dotnet run -- mock  离线脚本化演示（含"错误即观察"与 steering）
// ---------------------------------------------------------------------------

Env.Load(Path.Combine(AppContext.BaseDirectory, ".env"));

var useMock = args.Contains("mock", StringComparer.OrdinalIgnoreCase);

Agent agent;
if (useMock)
{
    Console.WriteLine("=== 模式：离线 mock（脚本化 LLM）===");
    Console.WriteLine();
    agent = new Agent("你是运行在终端里的演示助手，擅长使用工具回答问题。")
    {
        Stream = MockLlm.Create(),
    };
    agent.AddTool(new CalculateTool());
    // 演示 steering 队列：循环开始时被注入，模型可见两条用户消息
    agent.Steer(new UserMessage("追加要求：最后请给出计算结果的平方。"));
}
else
{
    var options = OpenAiLlm.FromEnvironment();
    Console.WriteLine($"=== 模式：真实模型 {options.Model} @ {options.BaseUrl} ===");
    Console.WriteLine();
    agent = new Agent("你是运行在终端里的演示助手。需要计算时请使用 calculate 工具，不要心算。")
    {
        Stream = OpenAiLlm.CreateStreamFn(options),
    };
    agent.AddTool(new CalculateTool());
}

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

var prompt = "请先计算 12*34，再把结果乘以 2，告诉我最终答案。";
Console.WriteLine($"用户: {prompt}");
Console.WriteLine();

try
{
    await agent.PromptAsync(prompt);
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
        _ => message.GetType().Name,
    };
    Console.WriteLine($"  {summary}");
}

return 0;
