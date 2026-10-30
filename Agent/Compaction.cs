using System.Text;

namespace ReactDemo.Agent;

// ---------------------------------------------------------------------------
// 上下文管理（Compaction），对应 pi 的 packages/coding-agent/src/core/compaction/。
// 设计要点（与 pi 一致）：
//   1. 触发：contextTokens > contextWindow - reserveTokens（绝对 token 预算，非百分比）。
//   2. 切点：从尾部向前累计估算 token，达到 keepRecentTokens 即确定保留起点；
//      toolResult 永不作为切点（必须与其 toolCall 成对保留/丢弃）。
//   3. 摘要：独立一次 LLM 调用，结构化 Markdown（Goal/Progress/Decisions/...）；
//      携带 previousSummary 做迭代式增量更新，而非每次从头摘要。
//   4. Split turn：切点落在回合中间（单个回合超过 keepRecentTokens）时，
//      历史与"回合前缀"分别摘要后合并。
//   5. system prompt 不参与摘要，始终保留在上下文头部。
// 与 pi 的差异（演示规模的取舍）：
//   - token 估算只用 chars/4 启发式（pi 优先用 provider 返回的真实 usage，
//     我们的 StreamFn 未透出 usage，留作扩展点）；
//   - 不做会话树持久化，直接在内存 transcript 上原位替换（pi 只改投影不删原始数据）；
//   - 未实现 overflow 恢复（需要 usage/错误分类，留作扩展点）；
//   - 未实现文件操作追踪 <read-files>/<modified-files>（演示工具无文件读写）。
// ---------------------------------------------------------------------------

/// <summary>压缩配置（对应 pi 的 CompactionSettings；ContextWindow 在 pi 中来自模型信息）。</summary>
public sealed record CompactionSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>模型上下文窗口（token）。</summary>
    public int ContextWindow { get; init; } = 128000;
    /// <summary>为压缩触发与摘要输出预留的 token 预算。窗口剩余低于此值即触发。</summary>
    public int ReserveTokens { get; init; } = 16384;
    /// <summary>压缩后原样保留的近期消息 token 数。</summary>
    public int KeepRecentTokens { get; init; } = 20000;
}

/// <summary>压缩触发原因（对应 pi 的 manual / threshold / overflow）。</summary>
public enum CompactionReason
{
    /// <summary>手动触发（Agent.CompactAsync）。</summary>
    Manual,
    /// <summary>上下文超过阈值自动触发。</summary>
    Threshold,
    /// <summary>上下文溢出恢复（本项目未实现，保留枚举位）。</summary>
    Overflow,
}

/// <summary>压缩准备结果（对应 pi 的 CompactionPreparation）：纯计算，无 IO。</summary>
public sealed record CompactionPreparation
{
    /// <summary>保留起点下标（该消息及之后原样保留）。</summary>
    public required int FirstKeptIndex { get; init; }
    /// <summary>split turn 时回合起点下标，否则 -1。</summary>
    public required int TurnStartIndex { get; init; }
    public required bool IsSplitTurn { get; init; }
    /// <summary>将被摘要并丢弃的消息（不含 system）。</summary>
    public required IReadOnlyList<AgentMessage> MessagesToSummarize { get; init; }
    /// <summary>split turn 时回合前缀消息（单独摘要）。</summary>
    public required IReadOnlyList<AgentMessage> TurnPrefixMessages { get; init; }
    /// <summary>压缩前的上下文 token 估算。</summary>
    public required int TokensBefore { get; init; }
    /// <summary>上一次压缩的摘要（迭代式更新用）。</summary>
    public string? PreviousSummary { get; init; }
}

/// <summary>压缩执行结果（对应 pi 的 CompactionResult）。</summary>
public sealed record CompactionResult
{
    public required string Summary { get; init; }
    public required int TokensBefore { get; init; }
    public required int TokensAfter { get; init; }
    public required CompactionReason Reason { get; init; }
}

/// <summary>摘要生成失败（stopReason 为 error/length、出现 toolCall 或空文本时抛出，对应 pi 的 getSummarizationFailure）。</summary>
public sealed class CompactionException(string message) : Exception(message);

public static class Compaction
{
    /// <summary>序列化对话时工具结果的最大保留字符数（对应 pi 的 TOOL_RESULT_MAX_CHARS）。</summary>
    private const int ToolResultMaxChars = 2000;

    // ---- Prompt（与 pi 保持一致，英文原样） --------------------------------

    public const string SummarizationSystemPrompt =
        "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n\n" +
        "Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";

    private const string SummarizationPrompt = """
        Summarize the conversation in <conversation> following this exact Markdown structure:

        ## Goal
        ## Constraints & Preferences
        ## Progress
        ### Done
        ### In Progress
        ### Blocked
        ## Key Decisions
        ## Next Steps
        ## Critical Context

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    private const string UpdateSummarizationPrompt = """
        You are updating an existing conversation summary with new progress. Update the summary in <previous-summary> using the latest conversation turns in <conversation>, following this exact Markdown structure:

        ## Goal
        ## Constraints & Preferences
        ## Progress
        ### Done
        ### In Progress
        ### Blocked
        ## Key Decisions
        ## Next Steps
        ## Critical Context

        Rules:
        - Preserve all information from the previous summary unless directly contradicted.
        - Move completed items from "In Progress" to "Done"; add new progress items.
        - Keep "Next Steps" current and actionable.

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    private const string TurnPrefixSummarizationPrompt = """
        Summarize the following partial turn of a conversation (it may be cut off mid-tool-call). Capture everything needed to continue the turn seamlessly: the user's request, decisions made, and any tool calls with their results so far.
        """;

    /// <summary>压缩摘要消息包装（对应 pi 的 COMPACTION_SUMMARY_PREFIX/SUFFIX，转成 user 消息发给 LLM）。</summary>
    public const string SummaryPrefix = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";
    public const string SummarySuffix = "\n</summary>";

    // ---- Token 估算 ---------------------------------------------------------

    /// <summary>单条消息的启发式 token 估算（chars/4，对应 pi 的 estimateTokens）。</summary>
    public static int EstimateTokens(AgentMessage message) => (int)Math.Ceiling(EstimateChars(message) / 4.0);

    private static int EstimateChars(AgentMessage message) => message switch
    {
        SystemMessage sys => (sys.Text?.Length ?? 0)
            + string.Join(",", sys.ToolsAdded).Length + string.Join(",", sys.ToolsRemoved).Length,
        UserMessage u => u.GetText().Length,
        AssistantMessage a => a.GetText().Length
            + a.Content.OfType<ToolCallContent>().Sum(c => c.Name.Length + c.Arguments.ToJsonString().Length),
        ToolResultMessage t => t.GetText().Length,
        CompactionSummaryMessage c => c.Summary.Length,
        _ => 0,
    };

    /// <summary>整个 transcript 的 token 估算（对应 pi 的 estimateContextTokens 的纯启发式分支）。</summary>
    public static int EstimateContextTokens(IReadOnlyList<AgentMessage> messages) =>
        messages.Sum(EstimateTokens);

    /// <summary>是否应压缩：contextTokens > contextWindow - reserveTokens（对应 pi 的 shouldCompact）。</summary>
    public static bool ShouldCompact(int contextTokens, CompactionSettings settings) =>
        settings.Enabled && contextTokens > settings.ContextWindow - settings.ReserveTokens;

    // ---- 切点算法 ------------------------------------------------------------

    /// <summary>
    /// 从尾部向前累计估算 token，达到 keepRecentTokens 时确定保留起点（对应 pi 的 findCutPoint）。
    /// 合法切点：user / compactionSummary / 回合已结束的 assistant（后一条不是 toolResult）；
    /// toolResult 永不作为切点。找不到合法切点或无可摘要内容时返回 null。
    /// </summary>
    public static CompactionPreparation? Prepare(IReadOnlyList<AgentMessage> messages, CompactionSettings settings)
    {
        // system prompt 区域（头部连续的 SystemMessage）不参与摘要
        var head = 0;
        while (head < messages.Count && messages[head] is SystemMessage) head++;
        if (head >= messages.Count) return null;

        // 从尾部向前累计 token，找到累计达到 keepRecentTokens 的下标 i，
        // 再取 >= i 的最近合法切点（找不到则回退到最早的合法切点）
        var cutPoints = new List<int>();
        for (var i = head; i < messages.Count; i++)
        {
            if (IsCutPoint(messages, i)) cutPoints.Add(i);
        }
        if (cutPoints.Count == 0) return null;

        var accumulated = 0;
        var cutIndex = -1;
        for (var i = messages.Count - 1; i >= head; i--)
        {
            var tokens = EstimateTokens(messages[i]);
            if (tokens == 0) continue;
            accumulated += tokens;
            if (accumulated >= settings.KeepRecentTokens)
            {
                cutIndex = cutPoints.LastOrDefault(c => c >= i, cutPoints[0]);
                break;
            }
        }
        // 尾部全部加起来都不到 keepRecentTokens：没有可摘要的空间
        if (cutIndex < 0) return null;
        if (cutIndex <= head) return null;

        // split turn 判定：切点落在回合中间（切点是 assistant 且往前能找到回合起点）
        var turnStartIndex = -1;
        var isSplitTurn = false;
        if (messages[cutIndex] is AssistantMessage)
        {
            for (var i = cutIndex - 1; i > head; i--)
            {
                if (messages[i] is UserMessage or CompactionSummaryMessage) { turnStartIndex = i; break; }
            }
            isSplitTurn = turnStartIndex > head;
        }

        var toSummarize = messages.Skip(head).Take(cutIndex - head)
            .Where(m => m is not SystemMessage).ToList();
        if (toSummarize.Count == 0) return null;

        var turnPrefix = isSplitTurn
            ? messages.Skip(turnStartIndex).Take(cutIndex - turnStartIndex).Where(m => m is not SystemMessage).ToList()
            : [];

        return new CompactionPreparation
        {
            FirstKeptIndex = cutIndex,
            TurnStartIndex = turnStartIndex,
            IsSplitTurn = isSplitTurn,
            MessagesToSummarize = toSummarize,
            TurnPrefixMessages = turnPrefix,
            TokensBefore = EstimateContextTokens(messages),
            PreviousSummary = toSummarize.OfType<CompactionSummaryMessage>().LastOrDefault()?.Summary,
        };
    }

    /// <summary>下标 i 是否为合法切点。toolResult 永不作为切点；assistant 仅在回合结束时（后一条不是 toolResult）可以。</summary>
    private static bool IsCutPoint(IReadOnlyList<AgentMessage> messages, int i)
    {
        if (i <= 0 || i >= messages.Count) return false;
        return messages[i] switch
        {
            UserMessage or CompactionSummaryMessage => true,
            AssistantMessage => i + 1 >= messages.Count || messages[i + 1] is not ToolResultMessage,
            _ => false,
        };
    }

    // ---- 对话序列化与摘要 prompt ---------------------------------------------

    /// <summary>把消息序列化为纯文本对话（对应 pi 的 serializeConversation），包在 conversation 标签里防止模型续写。</summary>
    public static string SerializeConversation(IReadOnlyList<AgentMessage> messages)
    {
        var parts = new List<string>();
        foreach (var m in messages)
        {
            switch (m)
            {
                case UserMessage u:
                    parts.Add($"[User]: {u.GetText()}");
                    break;
                case CompactionSummaryMessage c:
                    parts.Add($"[Previous summary]: {c.Summary}");
                    break;
                case AssistantMessage a:
                {
                    var text = a.GetText();
                    if (text.Length > 0) parts.Add($"[Assistant]: {text}");
                    var calls = a.Content.OfType<ToolCallContent>().ToList();
                    if (calls.Count > 0)
                    {
                        parts.Add($"[Assistant tool calls]: {string.Join("; ", calls.Select(c => $"{c.Name}({c.Arguments.ToJsonString()})"))}");
                    }
                    break;
                }
                case ToolResultMessage t:
                    parts.Add($"[Tool result]: {Truncate(t.GetText(), ToolResultMaxChars)}");
                    break;
            }
        }
        return string.Join("\n\n", parts);
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars
            ? text
            : $"{text[..maxChars]}\n\n[... {text.Length - maxChars} more characters truncated]";

    /// <summary>组装摘要请求的 user prompt（对应 pi 的 prompt 拼装顺序）。</summary>
    public static string BuildSummaryPrompt(
        IReadOnlyList<AgentMessage> messages, string? previousSummary, string? customInstructions,
        bool forTurnPrefix = false)
    {
        var sb = new StringBuilder();
        sb.Append("<conversation>\n").Append(SerializeConversation(messages)).Append("\n</conversation>\n\n");
        if (!string.IsNullOrEmpty(previousSummary))
        {
            sb.Append("<previous-summary>\n").Append(previousSummary).Append("\n</previous-summary>\n\n");
        }
        sb.Append(forTurnPrefix ? TurnPrefixSummarizationPrompt
            : previousSummary is null ? SummarizationPrompt : UpdateSummarizationPrompt);
        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            sb.Append($"\n\nAdditional focus: {customInstructions}");
        }
        return sb.ToString();
    }

    // ---- 执行 ----------------------------------------------------------------

    /// <summary>
    /// 压缩入口：准备 → LLM 摘要 → 原位替换 transcript（[head, cut) 区间替换为一条摘要消息）。
    /// 返回结果；无可摘要内容返回 null；失败抛 CompactionException（事件照发，transcript 不变）。
    /// 对应 pi 的 prepareCompaction + compact + appendCompaction + buildContextEntries 的合并体。
    /// </summary>
    public static async Task<CompactionResult?> RunAsync(
        List<AgentMessage> messages,
        CompactionSettings settings,
        StreamFn stream,
        Action<AgentEvent> emit,
        CompactionReason reason,
        string? customInstructions = null,
        CancellationToken ct = default)
    {
        emit(new CompactionStartEvent(reason));
        try
        {
            var prep = Prepare(messages, settings);
            if (prep is null)
            {
                emit(new CompactionEndEvent(reason, Summary: null, TokensBefore: 0, TokensAfter: 0, ErrorMessage: "nothing to compact"));
                return null;
            }

            var head = CountLeadingSystem(messages);
            string summary;
            if (prep.IsSplitTurn)
            {
                // split turn：历史摘要（带 previousSummary）+ 回合前缀摘要，合并（对应 pi 的合并格式）
                var history = messages.Skip(head).Take(prep.TurnStartIndex - head)
                    .Where(m => m is not SystemMessage).ToList();
                var historySummary = await GenerateSummaryAsync(stream, history,
                    prep.PreviousSummary, customInstructions, forTurnPrefix: false, ct);
                var prefix = await GenerateSummaryAsync(stream, prep.TurnPrefixMessages,
                    null, customInstructions, forTurnPrefix: true, ct);
                summary = $"{historySummary}\n\n---\n\n**Turn Context (split turn):**\n\n{prefix}";
            }
            else
            {
                summary = await GenerateSummaryAsync(stream, prep.MessagesToSummarize,
                    prep.PreviousSummary, customInstructions, forTurnPrefix: false, ct);
            }

            var tokensBefore = prep.TokensBefore;
            // 压缩边界的 system prompt 快照（对应 pi 的 CompactionEntry.systemMessage）
            var systemSnapshot = messages.Take(head).OfType<SystemMessage>().ToList();
            // 原位替换：[头部 system 之后, cut) 区间 → 一条摘要消息
            messages.RemoveRange(head, prep.FirstKeptIndex - head);
            messages.Insert(head, new CompactionSummaryMessage(summary, tokensBefore));
            var tokensAfter = EstimateContextTokens(messages);
            // 替换后保留的第一条消息（用于会话树的 firstKeptEntryId）
            AgentMessage? firstKept = head + 1 < messages.Count ? messages[head + 1] : null;

            var result = new CompactionResult { Summary = summary, TokensBefore = tokensBefore, TokensAfter = tokensAfter, Reason = reason };
            emit(new CompactionEndEvent(reason, summary, tokensBefore, tokensAfter, ErrorMessage: null, firstKept, systemSnapshot));
            return result;
        }
        catch (CompactionException ex)
        {
            // 失败不破坏 transcript：错误作为事件通知，循环继续用原上下文（对应 pi 的 session_compact_failed）
            emit(new CompactionEndEvent(reason, Summary: null, TokensBefore: 0, TokensAfter: 0, ErrorMessage: ex.Message));
            return null;
        }
    }

    /// <summary>单次摘要 LLM 调用：独立 StreamFn 请求，校验 stopReason 与 toolCall（对应 pi 的 completeSummarization）。</summary>
    public static async Task<string> GenerateSummaryAsync(
        StreamFn stream,
        IReadOnlyList<AgentMessage> messagesToSummarize,
        string? previousSummary,
        string? customInstructions,
        bool forTurnPrefix,
        CancellationToken ct)
    {
        var prompt = BuildSummaryPrompt(messagesToSummarize, previousSummary, customInstructions, forTurnPrefix);
        var context = new AgentContext
        {
            Messages = [new SystemMessage { Text = SummarizationSystemPrompt }, new UserMessage(prompt)],
            Tools = [],
        };

        AssistantMessage? final = null;
        await foreach (var ev in stream(context, ct))
        {
            if (ev is StreamDoneEvent done) final = done.Final;
        }

        var failure = final is null ? "stream ended without result"
            : final.StopReason is StopReason.Error ? $"summarization request failed: {final.ErrorMessage}"
            : final.StopReason is StopReason.Length ? "summarization output was truncated (stopReason=length)"
            : final.Content.OfType<ToolCallContent>().FirstOrDefault()?.Name is { } toolName ? $"summarization response contained a tool call: {toolName}"
            : string.IsNullOrWhiteSpace(final.GetText()) ? "summarization response was empty"
            : null;
        if (failure is not null) throw new CompactionException(failure);

        return final!.GetText().Trim();
    }

    private static int CountLeadingSystem(IReadOnlyList<AgentMessage> messages)
    {
        var n = 0;
        while (n < messages.Count && messages[n] is SystemMessage) n++;
        return n;
    }
}
