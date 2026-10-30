using System.Text.Json.Nodes;

namespace ReactDemo.Agent;

/// <summary>消息内容块（对应 pi 的 TextContent / ToolCallContent）。</summary>
public abstract record ContentBlock;

public sealed record TextContent(string Text) : ContentBlock
{
    public override string ToString() => Text;
}

/// <summary>assistant 消息中的工具调用请求。</summary>
public sealed record ToolCallContent(string Id, string Name, JsonObject Arguments) : ContentBlock;

public enum StopReason
{
    Stop,
    ToolUse,
    Length,
    Error,
    Aborted,
}

/// <summary>消息基类。内部全程使用 AgentMessage，只在 LLM 调用边界转换（对应 pi 的 AgentMessage）。</summary>
public abstract record AgentMessage(DateTimeOffset Timestamp);

/// <summary>
/// 系统消息：承载 system prompt 与工具装载变化声明。
/// 对应 pi 的设计：toolsAdded / toolsRemoved 被持久化进对话历史，session 可精确 replay。
/// </summary>
public sealed record SystemMessage : AgentMessage
{
    public string? Text { get; init; }
    public List<string> ToolsAdded { get; init; } = [];
    public List<string> ToolsRemoved { get; init; } = [];

    public SystemMessage() : base(DateTimeOffset.Now) { }
}

public sealed record UserMessage(IReadOnlyList<ContentBlock> Content) : AgentMessage(DateTimeOffset.Now)
{
    public UserMessage(string text) : this([new TextContent(text)]) { }

    public string GetText() => string.Concat(Content.OfType<TextContent>().Select(t => t.Text));
}

public sealed record AssistantMessage : AgentMessage
{
    public IReadOnlyList<ContentBlock> Content { get; init; } = [];
    public StopReason StopReason { get; init; } = StopReason.Stop;
    public string? ErrorMessage { get; init; }

    public AssistantMessage() : base(DateTimeOffset.Now) { }

    /// <summary>取全部文本（调试与演示用）。</summary>
    public string GetText() => string.Concat(Content.OfType<TextContent>().Select(t => t.Text));
}

/// <summary>观察结果：工具返回值写回上下文后成为下一轮的模型输入。</summary>
public sealed record ToolResultMessage : AgentMessage
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public IReadOnlyList<ContentBlock> Content { get; init; } = [];
    public bool IsError { get; init; }

    public ToolResultMessage() : base(DateTimeOffset.Now) { }

    public string GetText() => string.Concat(Content.OfType<TextContent>().Select(t => t.Text));
}
