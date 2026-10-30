using System.Text;
using System.Text.Json.Nodes;
using ReactDemo.Agent;

namespace ReactDemo.Demo;

/// <summary>
/// 离线脚本化 LLM（对应 pi 测试里的 faux provider），不联网即可演示完整 ReAct 循环。
/// 脚本：先调 calculate 但故意缺参数 → 收到错误观察后带完整参数重试 → 最终作答。
/// 如果检测到 steering 消息（用户消息多于一条），最终回答会响应追加要求。
/// 摘要请求（system prompt 含 context summarization）返回固定结构化摘要，供 compact 演示。
/// </summary>
public static class MockLlm
{
    public static StreamFn Create() => (context, ct) => StreamAsync(context, ct);

    private static async IAsyncEnumerable<StreamEvent> StreamAsync(
        Agent.AgentContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 压缩摘要请求：返回固定结构化摘要（对应 pi 的 SUMMARIZATION_PROMPT 输出格式）
        if (context.Messages.OfType<SystemMessage>()
                .Any(s => s.Text?.Contains("context summarization", StringComparison.OrdinalIgnoreCase) == true))
        {
            var summaryPrompt = context.Messages.OfType<UserMessage>().LastOrDefault()?.GetText() ?? "";
            yield return new StreamStartEvent(new AssistantMessage());
            yield return new StreamDeltaEvent(
                new AssistantMessage { Content = [new TextContent("[summary]")] }, "[summary]", IsToolCallDelta: false);
            var canned = $"""
                ## Goal
                帮用户完成计算任务（演示摘要）。

                ## Progress
                ### Done
                - 发起 calculate 调用并收到观察结果。

                ## Next Steps
                - 根据观察结果给出最终答案。

                ## Critical Context
                - 压缩前上下文约 {summaryPrompt.Length} 字符。
                """;
            yield return new StreamDoneEvent(new AssistantMessage
            {
                Content = [new TextContent(canned)],
                StopReason = StopReason.Stop,
            });
            yield break;
        }

        var lastToolResult = context.Messages.OfType<ToolResultMessage>().LastOrDefault();
        var userCount = context.Messages.OfType<UserMessage>().Count();
        var lastUserText = context.Messages.OfType<UserMessage>().LastOrDefault()?.GetText() ?? "";
        var systemText = context.Messages.OfType<SystemMessage>()
            .FirstOrDefault(s => s.Text?.Contains("<available_skills>", StringComparison.Ordinal) == true)?.Text;

        // 技能演示脚本（模拟渐进式披露）：
        //   1. 看到 <available_skills> 摘要 → 自主调用 read_file 读取 <location>；
        //   2. 拿到技能正文 → 调 calculate 计算；
        //   3. 拿到计算结果 → 严格按技能模板作答。
        if (systemText is not null && lastUserText.Contains("report-format", StringComparison.Ordinal))
        {
            string thinking2;
            string reply2;
            ToolCallContent? toolCall2;

            if (lastToolResult is null)
            {
                // 从 system prompt 的技能摘要中提取 <location>，自主读取技能文件
                var location = System.Text.RegularExpressions.Regex
                    .Match(systemText, "<location>(.*?)</location>").Groups[1].Value;
                thinking2 = $"用户要求按 report-format 技能汇报。系统提示里有该技能的 location，我先读取它的正文。";
                reply2 = "我先读取技能文件了解汇报格式。";
                toolCall2 = new ToolCallContent("call_skill_1", "read_file",
                    new JsonObject { ["path"] = location });
            }
            else if (lastToolResult.ToolName == "read_file")
            {
                thinking2 = "已拿到技能正文，格式要求明确。现在调用 calculate 计算表达式。";
                reply2 = "技能已加载，现在计算。";
                toolCall2 = new ToolCallContent("call_skill_2", "calculate",
                    new JsonObject { ["expression"] = "12*34*2" });
            }
            else
            {
                thinking2 = "拿到 816，按技能模板组织最终答案。";
                reply2 = """
                    [计算过程]
                    1. 12*34 = 408
                    2. 408*2 = 816

                    [最终答案]
                    816

                    [备注]
                    使用 calculate 工具完成计算，汇报格式遵循 report-format 技能。
                    """;
                toolCall2 = null;
            }

            await foreach (var e in EmitTurn(thinking2, reply2, toolCall2, ct))
            {
                yield return e;
            }
            yield break;
        }

        string thinking;
        string reply;
        ToolCallContent? toolCall = null;

        if (lastToolResult is null)
        {
            thinking = userCount > 1
                ? "用户要求计算并追加了要求。先调用 calculate 工具。"
                : "用户要求计算。先调用 calculate 工具。";
            reply = "好的，我先用计算器工具算一下。";
            // 故意缺参数，触发"错误即观察"路径
            toolCall = new ToolCallContent("call_demo_1", "calculate", new JsonObject());
        }
        else if (lastToolResult.IsError)
        {
            thinking = "上次调用缺少 expression 参数，这次带完整参数重试。";
            reply = "刚才参数没传全，我重新调用一次。";
            toolCall = new ToolCallContent("call_demo_2", "calculate",
                new JsonObject { ["expression"] = "12*34" });
        }
        else if (lastUserText.Contains("100+23") && !lastToolResult.GetText().Contains("123"))
        {
            // compact 演示第二轮：压缩后根据摘要中的上下文继续处理新请求
            thinking = "上下文已压缩，摘要记录了之前的进展。现在处理新请求：计算 100+23。";
            reply = "收到，我来算 100+23。";
            toolCall = new ToolCallContent("call_demo_3", "calculate",
                new JsonObject { ["expression"] = "100+23" });
        }
        else
        {
            thinking = lastUserText.Contains("100+23")
                ? "拿到 123，组织最终答案。"
                : "拿到 408，组织最终答案。";
            reply = lastUserText.Contains("100+23")
                ? "100 + 23 = 123。任务完成。"
                : userCount > 1
                    ? "12 × 34 = 408。收到追加要求：408 的平方 = 166464。"
                    : "12 × 34 = 408。任务完成。";
        }

        await foreach (var e in EmitTurn(thinking, reply, toolCall, ct))
        {
            yield return e;
        }
    }

    /// <summary>流式输出一轮响应：thinking 逐块输出；有 toolCall 则附上，否则继续流式输出最终回答。</summary>
    private static async IAsyncEnumerable<StreamEvent> EmitTurn(
        string thinking, string reply, ToolCallContent? toolCall,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sb = new StringBuilder();
        var partial = new AssistantMessage();
        yield return new StreamStartEvent(partial);

        foreach (var chunk in Chunk(thinking))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(chunk);
            partial = partial with { Content = [new TextContent(sb.ToString())] };
            await Task.Delay(15, ct);
            yield return new StreamDeltaEvent(partial, chunk, IsToolCallDelta: false);
        }

        var content = new List<ContentBlock> { new TextContent(sb.ToString()) };
        if (toolCall is not null)
        {
            content.Add(toolCall);
            yield return new StreamDeltaEvent(partial, "[toolCall]", IsToolCallDelta: true);
        }
        else
        {
            // 无工具调用的最终回复：继续流式输出 answer 部分
            foreach (var chunk in Chunk(reply))
            {
                ct.ThrowIfCancellationRequested();
                sb.Append(chunk);
                await Task.Delay(15, ct);
                yield return new StreamDeltaEvent(
                    partial with { Content = [new TextContent(sb.ToString())] }, chunk, IsToolCallDelta: false);
            }
        }

        // 有工具调用：thinking 文本 + toolCall；无工具调用：thinking + 最终回答
        var finalContent = toolCall is not null
            ? content
            : new List<ContentBlock> { new TextContent(sb.ToString()) };

        yield return new StreamDoneEvent(new AssistantMessage
        {
            Content = finalContent,
            StopReason = toolCall is null ? StopReason.Stop : StopReason.ToolUse,
        });
    }

    private static IEnumerable<string> Chunk(string text)
    {
        for (var i = 0; i < text.Length; i += 4)
        {
            yield return text.Substring(i, Math.Min(4, text.Length - i));
        }
    }
}
