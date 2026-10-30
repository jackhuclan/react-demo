using System.Text.Json.Nodes;

namespace ReactDemo.Agent;

/// <summary>生命周期事件（对应 pi 的 AgentEvent）。循环核心对 UI 一无所知，全部通过事件解耦。</summary>
public abstract record AgentEvent;

public sealed record AgentStartEvent : AgentEvent;
public sealed record AgentEndEvent(IReadOnlyList<AgentMessage> Messages) : AgentEvent;
public sealed record TurnStartEvent : AgentEvent;
public sealed record TurnEndEvent(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;

public sealed record MessageStartEvent(AgentMessage Message) : AgentEvent;

/// <summary>流式增量：partial 消息持续替换进上下文，UI 可边流边渲染。</summary>
public sealed record MessageUpdateEvent(AgentMessage Message) : AgentEvent;
public sealed record MessageEndEvent(AgentMessage Message) : AgentEvent;

public sealed record ToolExecutionStartEvent(string ToolCallId, string ToolName, JsonObject Arguments) : AgentEvent;
public sealed record ToolExecutionUpdateEvent(string ToolCallId, string ToolName, ToolResult PartialResult) : AgentEvent;
public sealed record ToolExecutionEndEvent(string ToolCallId, string ToolName, ToolResult Result, bool IsError) : AgentEvent;
