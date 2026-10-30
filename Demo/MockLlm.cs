using System.Text;
using System.Text.Json.Nodes;
using ReactDemo.Agent;

namespace ReactDemo.Demo;

/// <summary>
/// 离线脚本化 LLM（对应 pi 测试里的 faux provider），不联网即可演示完整 ReAct 循环。
/// 脚本：先调 calculate 但故意缺参数 → 收到错误观察后带完整参数重试 → 最终作答。
/// 如果检测到 steering 消息（用户消息多于一条），最终回答会响应追加要求。
/// </summary>
public static class MockLlm
{
    public static StreamFn Create() => (context, ct) => StreamAsync(context, ct);

    private static async IAsyncEnumerable<StreamEvent> StreamAsync(
        Agent.AgentContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var lastToolResult = context.Messages.OfType<ToolResultMessage>().LastOrDefault();
        var userCount = context.Messages.OfType<UserMessage>().Count();

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
        else
        {
            thinking = "拿到 408，组织最终答案。";
            reply = userCount > 1
                ? "12 × 34 = 408。收到追加要求：408 的平方 = 166464。"
                : "12 × 34 = 408。任务完成。";
        }

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
