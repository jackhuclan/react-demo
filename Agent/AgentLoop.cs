using System.Text.Json.Nodes;

namespace ReactDemo.Agent;

/// <summary>
/// ReAct 循环本体，对应 pi 的 packages/agent/src/agent-loop.ts 的 runLoop。
/// 感知：DeclareToolChanges + 队列消息注入 → 推理：StreamAssistantResponse
/// → 行动：ExecuteToolCalls → 观察：ToolResult 写回 transcript，直到无工具调用。
/// </summary>
public static class AgentLoop
{
    public static async Task<IReadOnlyList<AgentMessage>> RunAsync(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext context,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken ct)
    {
        // transcript 是工作副本；压缩可能原位替换历史区间，
        // 循环结束后由 Agent 用返回的最终 transcript 整体覆盖真实状态
        var messages = new List<AgentMessage>(context.Messages);
        var newMessages = new List<AgentMessage>();

        var initial = DeclareToolChanges(messages, context.Tools, [.. prompts]);
        messages.AddRange(initial);
        newMessages.AddRange(initial);

        emit(new AgentStartEvent());
        foreach (var m in initial)
        {
            emit(new MessageStartEvent(m));
            emit(new MessageEndEvent(m));
        }

        await RunLoopAsync(messages, context, newMessages, config, emit, ct);
        return messages;
    }

    private static async Task RunLoopAsync(
        List<AgentMessage> messages,
        AgentContext context,
        List<AgentMessage> newMessages,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken ct)
    {
        // 循环开始先捞 steering：用户可能在等待期间已经插话
        var pendingMessages = await (config.GetSteeringMessages?.Invoke() ?? Task.FromResult<IReadOnlyList<AgentMessage>>([]));
        var explicitContinuation = false;
        // 上次压缩时的消息数：防止压缩后上下文仍超阈值时死循环压缩
        var lastCompactionCount = 0;

        // 外层循环：agent 本来要停时检查 followUp 队列
        while (true)
        {
            var hasMoreToolCalls = true;

            // 内层循环：处理工具调用与 steering 消息；每轮开始发 turn_start
            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                emit(new TurnStartEvent());

                // 感知①：注入 pending 消息（steering / followUp），并声明工具装载变化
                var injected = DeclareToolChanges(messages, context.Tools, [.. pendingMessages]);
                foreach (var m in injected)
                {
                    emit(new MessageStartEvent(m));
                    emit(new MessageEndEvent(m));
                    messages.Add(m);
                    newMessages.Add(m);
                }
                pendingMessages = [];

                // 感知②：流式拿模型决策（推理阶段）
                var assistant = await StreamAssistantResponseAsync(messages, context, config, emit, ct);
                newMessages.Add(assistant);
                // assistant 响应必须进入 transcript：既作为下一轮 LLM 上下文（toolCall 与 toolResult 成对），
                // 也参与上下文 token 估算与压缩切点判定
                messages.Add(assistant);

                if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
                {
                    emit(new TurnEndEvent(assistant, []));
                    emit(new AgentEndEvent(newMessages));
                    return;
                }

                // 行动阶段：有 toolCall 就执行；"length" 说明输出被截断，工具参数可能残缺，全部判失败
                var toolCalls = assistant.Content.OfType<ToolCallContent>().ToList();
                var toolResults = new List<ToolResultMessage>();
                hasMoreToolCalls = false;
                if (toolCalls.Count > 0)
                {
                    var batch = assistant.StopReason == StopReason.Length
                        ? await FailTruncatedToolCallsAsync(toolCalls, emit)
                        : await ExecuteToolCallsAsync(context, assistant, toolCalls, config, emit, ct);
                    toolResults.AddRange(batch.Messages);
                    hasMoreToolCalls = !batch.Terminate;

                    // 观察阶段：toolResult 写回 transcript，成为下一轮输入
                    messages.AddRange(toolResults);
                    newMessages.AddRange(toolResults);
                }

                emit(new TurnEndEvent(assistant, toolResults));

                // 上下文压缩检查（对应 pi 在 prepareNextTurn 中嵌入的 compaction）：
                // 工具结果落库后、下一条 assistant 响应前检查阈值；上次压缩后无新消息则跳过，防死循环
                if (config.Compaction is { Enabled: true } && messages.Count != lastCompactionCount)
                {
                    if (Compaction.ShouldCompact(Compaction.EstimateContextTokens(messages), config.Compaction))
                    {
                        lastCompactionCount = messages.Count;
                        await Compaction.RunAsync(
                            messages, config.Compaction, config.Stream, emit, CompactionReason.Threshold, ct: ct);
                    }
                }

                var decision = config.FinishTurn is null
                    ? TurnDecision.None
                    : await config.FinishTurn(new TurnContext(assistant, toolResults));
                if (decision == TurnDecision.End)
                {
                    emit(new AgentEndEvent(newMessages));
                    return;
                }
                if (decision == TurnDecision.Continue)
                {
                    explicitContinuation = true;
                }

                pendingMessages = await (config.GetSteeringMessages?.Invoke() ?? Task.FromResult<IReadOnlyList<AgentMessage>>([]));
                if (hasMoreToolCalls || pendingMessages.Count > 0)
                {
                    explicitContinuation = false;
                }
            }

            // agent 本来要停：有 followUp 就继续
            var followUps = await (config.GetFollowUpMessages?.Invoke() ?? Task.FromResult<IReadOnlyList<AgentMessage>>([]));
            if (followUps.Count > 0)
            {
                explicitContinuation = false;
                pendingMessages = followUps;
                continue;
            }

            // finishTurn 要求强制续跑：只跑一轮纯上下文轮
            if (explicitContinuation)
            {
                explicitContinuation = false;
                continue;
            }

            break;
        }

        emit(new AgentEndEvent(newMessages));
    }

    /// <summary>
    /// 对应 pi 的 declareToolChanges：把"可执行工具集"与"transcript 已声明工具集"的差值
    /// 写成 SystemMessage 的 ToolsAdded / ToolsRemoved，保证 replay 可恢复工具装载。
    /// </summary>
    private static List<AgentMessage> DeclareToolChanges(
        IReadOnlyList<AgentMessage> transcript,
        IReadOnlyList<AgentTool> tools,
        List<AgentMessage> pending)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in transcript.OfType<SystemMessage>())
        {
            foreach (var n in s.ToolsAdded) declared.Add(n);
            foreach (var n in s.ToolsRemoved) declared.Remove(n);
        }

        var current = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var added = current.Except(declared).Order().ToList();
        var removed = declared.Except(current).Order().ToList();
        if (added.Count == 0 && removed.Count == 0) return pending;

        // pending 中已有 system 消息则合并，否则插到第一条非 system 消息之前
        var existing = pending.OfType<SystemMessage>().LastOrDefault();
        if (existing is not null)
        {
            var merged = new SystemMessage
            {
                Text = existing.Text,
                ToolsAdded = [.. existing.ToolsAdded, .. added],
                ToolsRemoved = [.. existing.ToolsRemoved, .. removed],
            };
            pending[pending.IndexOf(existing)] = merged;
            return pending;
        }

        var update = new SystemMessage { ToolsAdded = added, ToolsRemoved = removed };
        var insertAt = pending.FindIndex(m => m is not SystemMessage);
        return insertAt < 0 ? [.. pending, update] : [.. pending.Take(insertAt), update, .. pending.Skip(insertAt)];
    }

    /// <summary>
    /// 推理阶段：调用 StreamFn，把每个增量事件转为 message_update 事件，
    /// partial 消息持续替换进 transcript——UI 边流边渲染，真状态始终是那条生长中的消息。
    /// </summary>
    private static async Task<AssistantMessage> StreamAssistantResponseAsync(
        IReadOnlyList<AgentMessage> messages,
        AgentContext context,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken ct)
    {
        var streamContext = context with { Messages = messages };
        AssistantMessage? partial = null;

        await foreach (var ev in config.Stream(streamContext, ct))
        {
            switch (ev)
            {
                case StreamStartEvent start:
                    partial = start.Partial;
                    emit(new MessageStartEvent(partial));
                    break;
                case StreamDeltaEvent delta:
                    partial = delta.Partial;
                    emit(new MessageUpdateEvent(partial));
                    break;
                case StreamDoneEvent done:
                    partial = done.Final;
                    break;
            }
        }

        var final = partial ?? new AssistantMessage { StopReason = StopReason.Error, ErrorMessage = "stream ended without result" };
        emit(new MessageEndEvent(final));
        return final;
    }

    private static async Task<(List<ToolResultMessage> Messages, bool Terminate)> FailTruncatedToolCallsAsync(
        List<ToolCallContent> toolCalls,
        Action<AgentEvent> emit)
    {
        var messages = new List<ToolResultMessage>();
        foreach (var call in toolCalls)
        {
            emit(new ToolExecutionStartEvent(call.Id, call.Name, call.Arguments));
            var result = ToolResult.Text(
                $"工具 \"{call.Name}\" 未执行：输出被 token 上限截断，参数可能不完整，请重新发起完整调用。", isError: true);
            emit(new ToolExecutionEndEvent(call.Id, call.Name, result, IsError: true));
            var tr = ToToolResultMessage(call, result);
            emit(new MessageStartEvent(tr));
            emit(new MessageEndEvent(tr));
            messages.Add(tr);
        }
        return (messages, Terminate: false);
    }

    /// <summary>
    /// 行动阶段：parallel 模式下准备（含 beforeToolCall 钩子）串行、执行并发；
    /// sequential 模式（或批次内含 sequential 工具）全部串行。对应 pi 的 executeToolCalls。
    /// </summary>
    private static async Task<(List<ToolResultMessage> Messages, bool Terminate)> ExecuteToolCallsAsync(
        AgentContext context,
        AssistantMessage assistant,
        List<ToolCallContent> toolCalls,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken ct)
    {
        var sequential = config.ToolExecution == ToolExecutionMode.Sequential
            || toolCalls.Any(tc => context.Tools.FirstOrDefault(t => t.Name == tc.Name)?.ExecutionMode == ToolExecutionMode.Sequential);

        var outcomes = new List<(ToolCallContent Call, ToolResult Result)>();

        if (sequential)
        {
            foreach (var call in toolCalls)
            {
                emit(new ToolExecutionStartEvent(call.Id, call.Name, call.Arguments));
                outcomes.Add((call, await RunSingleToolCallAsync(context, assistant, call, config, emit, ct)));
                if (ct.IsCancellationRequested) break;
            }
        }
        else
        {
            var prepared = new List<(ToolCallContent Call, ToolResult? ImmediateError, PreparedToolCall? Prepared)>();
            foreach (var call in toolCalls)
            {
                emit(new ToolExecutionStartEvent(call.Id, call.Name, call.Arguments));
                var p = await PrepareToolCallAsync(context, assistant, call, config, ct);
                prepared.Add(p.Prepared is null ? (call, p.ImmediateError, null) : (call, null, p.Prepared));
                if (ct.IsCancellationRequested) break;
            }

            var tasks = prepared.Select(async entry => entry.ImmediateError is not null
                ? (entry.Call, entry.ImmediateError)
                : (entry.Call, await ExecutePreparedAsync(entry.Prepared!, assistant, config, emit, ct)));
            outcomes.AddRange(await Task.WhenAll(tasks));
        }

        var messages = new List<ToolResultMessage>();
        foreach (var (call, result) in outcomes)
        {
            var tr = ToToolResultMessage(call, result);
            emit(new MessageStartEvent(tr));
            emit(new MessageEndEvent(tr));
            messages.Add(tr);
        }

        // 全部工具都要求 terminate 才终止循环
        var terminate = outcomes.Count > 0 && outcomes.All(o => o.Result.Terminate);
        return (messages, terminate);
    }

    private static ToolResultMessage ToToolResultMessage(ToolCallContent call, ToolResult result) => new()
    {
        ToolCallId = call.Id,
        ToolName = call.Name,
        Content = result.Content,
        IsError = result.IsError,
    };

    private sealed record PreparedToolCall(AgentTool Tool, ToolCallContent Call);

    private sealed record PreparedOrError(PreparedToolCall? Prepared, ToolResult? ImmediateError);

    /// <summary>工具执行管线：查找 → 参数校验 → beforeToolCall（可拦截）→ 执行。失败一律转为错误观察，不抛异常。</summary>
    private static async Task<PreparedOrError> PrepareToolCallAsync(
        AgentContext context,
        AssistantMessage assistant,
        ToolCallContent call,
        AgentLoopConfig config,
        CancellationToken ct)
    {
        var tool = context.Tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null)
        {
            return new PreparedOrError(null, ToolResult.Text($"Tool {call.Name} not found", isError: true));
        }

        var validationError = ValidateArguments(tool, call.Arguments);
        if (validationError is not null)
        {
            return new PreparedOrError(null, ToolResult.Text(validationError, isError: true));
        }

        if (config.BeforeToolCall is not null)
        {
            var verdict = await config.BeforeToolCall(new BeforeToolCallContext(assistant, call, call.Arguments));
            if (verdict is { Block: true })
            {
                return new PreparedOrError(null, ToolResult.Text(verdict.Reason ?? "Tool execution was blocked", isError: true));
            }
        }

        if (ct.IsCancellationRequested)
        {
            return new PreparedOrError(null, ToolResult.Text("Operation aborted", isError: true));
        }

        return new PreparedOrError(new PreparedToolCall(tool, call), null);
    }

    /// <summary>轻量参数校验：required 参数存在且非空。生产环境应换 JSON Schema 校验。</summary>
    private static string? ValidateArguments(AgentTool tool, JsonObject args)
    {
        foreach (var name in tool.RequiredArguments)
        {
            var value = args[name];
            if (value is null || value is System.Text.Json.Nodes.JsonValue v && v.ToString().Length == 0)
            {
                return $"工具 \"{tool.Name}\" 缺少必填参数 \"{name}\"。请带上完整参数重试。";
            }
        }
        return null;
    }

    private static async Task<ToolResult> ExecutePreparedAsync(
        PreparedToolCall prepared,
        AssistantMessage assistant,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken ct)
    {
        try
        {
            var result = await prepared.Tool.ExecuteAsync(
                prepared.Call.Id, prepared.Call.Arguments, ct,
                partial => emit(new ToolExecutionUpdateEvent(prepared.Call.Id, prepared.Call.Name, partial)));

            if (config.AfterToolCall is not null)
            {
                var after = await config.AfterToolCall(new AfterToolCallContext(assistant, prepared.Call, result));
                if (after is not null) result = after.Apply(result);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Text("Operation aborted", isError: true);
        }
        catch (Exception ex)
        {
            // 错误即观察：工具异常转为 isError 的 ToolResult，循环永不因工具异常崩溃
            return ToolResult.Text(ex.Message, isError: true);
        }
    }

    private static async Task<ToolResult> RunSingleToolCallAsync(
        AgentContext context,
        AssistantMessage assistant,
        ToolCallContent call,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken ct)
    {
        var p = await PrepareToolCallAsync(context, assistant, call, config, ct);
        if (p.ImmediateError is not null) return p.ImmediateError;
        return await ExecutePreparedAsync(p.Prepared!, assistant, config, emit, ct);
    }
}
