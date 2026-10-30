using System.Text.Json.Nodes;

namespace ReactDemo.Agent;

public enum ToolExecutionMode
{
    Parallel,
    Sequential,
}

/// <summary>工具返回值。失败时置 IsError 而不是抛异常——错误也会作为观察喂回模型。</summary>
public sealed record ToolResult(IReadOnlyList<ContentBlock> Content, bool IsError = false, bool Terminate = false)
{
    public static ToolResult Text(string text, bool isError = false) =>
        new([new TextContent(text)], isError);

    public string GetText() => string.Concat(Content.OfType<TextContent>().Select(t => t.Text));
}

/// <summary>
/// 工具定义（对应 pi 的 AgentTool）。ParametersSchema 是 JSON Schema 字符串，
/// 传给 OpenAI function calling；RequiredArguments 用于演示轻量参数校验。
/// </summary>
public abstract class AgentTool
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public IReadOnlyList<string> RequiredArguments { get; init; } = [];
    public string? ParametersSchema { get; init; }
    public ToolExecutionMode ExecutionMode { get; init; } = ToolExecutionMode.Parallel;

    /// <summary>执行工具。onPartialResult 用于推送部分结果（对应 pi 的 tool_execution_update 事件）。</summary>
    public abstract Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonObject arguments,
        CancellationToken ct,
        Action<ToolResult>? onPartialResult = null);
}
