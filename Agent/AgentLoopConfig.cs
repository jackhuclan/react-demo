using System.Text.Json.Nodes;

namespace ReactDemo.Agent;

public enum QueueMode
{
    OneAtATime,
    All,
}

/// <summary>循环的工作上下文：当前 transcript + 可执行工具集（对应 pi 的 AgentContext）。</summary>
public sealed record AgentContext
{
    public required IReadOnlyList<AgentMessage> Messages { get; init; }
    public required IReadOnlyList<AgentTool> Tools { get; init; }
}

// ---------------------------------------------------------------------------
// LLM 流式接口：对应 pi 的 StreamFn。真实实现（OpenAiLlm）在此边界把
// AgentMessage 转为 provider 消息；MockLlm 直接读 AgentMessage 模拟脚本。
// ---------------------------------------------------------------------------

public delegate IAsyncEnumerable<StreamEvent> StreamFn(AgentContext context, CancellationToken ct);

public abstract record StreamEvent;

/// <summary>流开始，携带初始 partial assistant 消息。</summary>
public sealed record StreamStartEvent(AssistantMessage Partial) : StreamEvent;

/// <summary>文本/工具调用增量，Partial 是累积到当前为止的完整消息。</summary>
public sealed record StreamDeltaEvent(AssistantMessage Partial, string DeltaText, bool IsToolCallDelta) : StreamEvent;

/// <summary>流结束，携带最终 assistant 消息（含 stopReason）。</summary>
public sealed record StreamDoneEvent(AssistantMessage Final) : StreamEvent;

public enum TurnDecision
{
    None,
    /// <summary>finishTurn 强制结束循环。</summary>
    End,
    /// <summary>即使没有工具调用也再跑一轮（explicitContinuation）。</summary>
    Continue,
}

public sealed record BeforeToolCallContext(AssistantMessage AssistantMessage, ToolCallContent ToolCall, JsonObject Args);

/// <summary>Block = true 时拒绝执行，拒绝原因作为错误观察喂回模型。权限确认挂载点。</summary>
public sealed record BeforeToolCallResult(bool Block = false, string? Reason = null);

public sealed record AfterToolCallContext(AssistantMessage AssistantMessage, ToolCallContent ToolCall, ToolResult Result);

/// <summary>允许改写工具返回文本。</summary>
public sealed record AfterToolCallResult(string? Text = null)
{
    public ToolResult Apply(ToolResult result) =>
        Text is null ? result : new ToolResult([new TextContent(Text)], result.IsError, result.Terminate);
}

public sealed record TurnContext(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults);

/// <summary>循环配置（对应 pi 的 AgentLoopConfig）。</summary>
public sealed class AgentLoopConfig
{
    public required StreamFn Stream { get; init; }
    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
    public Func<Task<IReadOnlyList<AgentMessage>>>? GetSteeringMessages { get; init; }
    public Func<Task<IReadOnlyList<AgentMessage>>>? GetFollowUpMessages { get; init; }
    public Func<BeforeToolCallContext, Task<BeforeToolCallResult?>>? BeforeToolCall { get; init; }
    public Func<AfterToolCallContext, Task<AfterToolCallResult?>>? AfterToolCall { get; init; }
    public Func<TurnContext, Task<TurnDecision>>? FinishTurn { get; init; }
}
