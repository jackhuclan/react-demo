using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Chat;
using ReactDemo.Agent;

namespace ReactDemo.Demo;

/// <summary>
/// 真实 LLM 适配层：通过 OpenAI 官方包调用本地 OpenAI 兼容端点（vLLM）。
/// 这里是 pi 所说的"只在 LLM 调用边界做消息格式转换"的位置：
/// AgentMessage[] → ChatMessage[]，流式增量 → StreamEvent。
/// </summary>
public static class OpenAiLlm
{
    public sealed record Options
    {
        public required string BaseUrl { get; init; }
        public required string ApiKey { get; init; }
        public required string Model { get; init; }
        public int MaxTokens { get; init; } = 12800;
        public float Temperature { get; init; } = 0.6f;
    }

    public static Options FromEnvironment()
    {
        return new Options
        {
            BaseUrl = Require("OPENAI_BASE_URL"),
            ApiKey = Require("OPENAI_API_KEY"),
            Model = Require("OPENAI_MODEL"),
            MaxTokens = int.TryParse(Environment.GetEnvironmentVariable("OPENAI_MAX_TOKENS"), out var max) ? max : 12800,
            Temperature = float.TryParse(Environment.GetEnvironmentVariable("OPENAI_TEMPERATURE"), out var temp) ? temp : 0.6f,
        };

        static string Require(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"缺少环境变量 {name}，请检查 .env 文件。");
    }

    public static StreamFn CreateStreamFn(Options options)
    {
        // 注意 endpoint 必须以 / 结尾，否则相对路径拼接会丢掉 /v1 段
        var client = new ChatClient(options.Model, new ApiKeyCredential(options.ApiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        });
        return (context, ct) => StreamAsync(client, options, context, ct);
    }

    private static async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatClient client,
        Options options,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chatOptions = BuildCompletionOptions(options, context.Tools);
        var chatMessages = ToChatMessages(context.Messages);

        var textBuffer = new StringBuilder();
        // 流式工具调用参数按 ToolCallId 累积（与 pi 的 toolcall_delta 累积同理）
        var toolCalls = new Dictionary<string, ToolCallAccumulator>();
        ChatFinishReason? finishReason = null;
        var started = false;
        var partial = new AssistantMessage();

        await foreach (var update in client.CompleteChatStreamingAsync(chatMessages, chatOptions, ct))
        {
            if (!started)
            {
                started = true;
                yield return new StreamStartEvent(partial);
            }

            // 文本增量（ContentUpdate 是内容分片集合，只取文本分片）
            foreach (var part in update.ContentUpdate)
            {
                var delta = part.Text;
                if (string.IsNullOrEmpty(delta)) continue;
                textBuffer.Append(delta);
                partial = partial with { Content = [new TextContent(textBuffer.ToString())] };
                yield return new StreamDeltaEvent(partial, delta, IsToolCallDelta: false);
            }

            // 工具调用增量：首个增量携带 id/name，后续参数增量通常不带 id，
            // 必须并入上一个累积器，否则会产生空名字的"幽灵调用"
            if (update.ToolCallUpdates is { Count: > 0 })
            {
                var announced = false;
                ToolCallAccumulator? current = null;
                foreach (var tcUpdate in update.ToolCallUpdates)
                {
                    if (!string.IsNullOrEmpty(tcUpdate.ToolCallId))
                    {
                        if (!toolCalls.TryGetValue(tcUpdate.ToolCallId, out var found))
                        {
                            found = new ToolCallAccumulator(tcUpdate.ToolCallId);
                            toolCalls[tcUpdate.ToolCallId] = found;
                        }
                        current = found;
                    }
                    else
                    {
                        // 无 id 增量：优先并入本批次已开始的调用，否则新建并登记
                        if (current is null)
                        {
                            var last = toolCalls.Values.LastOrDefault();
                            if (last is null)
                            {
                                last = new ToolCallAccumulator($"call_{toolCalls.Count}");
                                toolCalls[last.Id] = last;
                            }
                            current = last;
                        }
                    }
                    var acc = current;

                    if (!string.IsNullOrEmpty(tcUpdate.FunctionName))
                    {
                        acc.Name = tcUpdate.FunctionName;
                        announced = true;
                    }
                    if (tcUpdate.FunctionArgumentsUpdate is { } argsDelta)
                    {
                        acc.Arguments.Append(argsDelta.ToString());
                    }
                }
                if (announced)
                {
                    yield return new StreamDeltaEvent(partial, "[toolCall]", IsToolCallDelta: true);
                }
            }

            if (update.FinishReason is { } fr)
            {
                finishReason = fr;
            }
        }

        yield return new StreamDoneEvent(BuildFinal(textBuffer, toolCalls, finishReason));
    }

    private static AssistantMessage BuildFinal(
        StringBuilder textBuffer, Dictionary<string, ToolCallAccumulator> toolCalls, ChatFinishReason? finishReason)
    {
        var content = new List<ContentBlock>();
        if (textBuffer.Length > 0) content.Add(new TextContent(textBuffer.ToString()));

        foreach (var acc in toolCalls.Values)        {
            JsonObject args;
            try
            {
                args = acc.Arguments.Length == 0
                    ? new JsonObject()
                    : JsonNode.Parse(acc.Arguments.ToString()) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                // 参数被截断或非法时保留空对象，交给循环的参数校验报错（错误即观察）
                args = new JsonObject();
            }
            content.Add(new ToolCallContent(acc.Id, acc.Name, args));
        }

        var stop = finishReason switch
        {
            ChatFinishReason.ToolCalls => StopReason.ToolUse,
            ChatFinishReason.Length => StopReason.Length,
            ChatFinishReason.Stop => StopReason.Stop,
            _ => StopReason.Stop,
        };
        return new AssistantMessage { Content = content, StopReason = stop };
    }

    private sealed class ToolCallAccumulator(string id)
    {
        public string Id { get; set; } = id;
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }

    private static ChatCompletionOptions BuildCompletionOptions(Options options, IReadOnlyList<AgentTool> tools)
    {
        var chatOptions = new ChatCompletionOptions
        {
            Temperature = options.Temperature,
            MaxOutputTokenCount = options.MaxTokens,
            ToolChoice = ChatToolChoice.CreateAutoChoice(),
        };
        foreach (var tool in tools)
        {
            chatOptions.Tools.Add(ChatTool.CreateFunctionTool(
                functionName: tool.Name,
                functionDescription: tool.Description,
                functionParameters: tool.ParametersSchema is null ? null : BinaryData.FromString(tool.ParametersSchema)));
        }
        return chatOptions;
    }

    /// <summary>AgentMessage[] → ChatMessage[]。pi 的 convertToLlm 边界。</summary>
    private static List<ChatMessage> ToChatMessages(IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case SystemMessage sys:
                {
                    var text = sys.Text ?? "";
                    if (sys.ToolsAdded.Count > 0)
                    {
                        text += (text.Length > 0 ? "\n" : "") + $"可用工具变更：新增 {string.Join(", ", sys.ToolsAdded)}。";
                    }
                    if (sys.ToolsRemoved.Count > 0)
                    {
                        text += (text.Length > 0 ? "\n" : "") + $"可用工具变更：移除 {string.Join(", ", sys.ToolsRemoved)}。";
                    }
                    if (text.Length > 0) result.Add(new SystemChatMessage(text));
                    break;
                }
                case UserMessage user:
                    result.Add(new UserChatMessage(user.GetText()));
                    break;
                case AssistantMessage assistant:
                {
                    var chatAssistant = new AssistantChatMessage(assistant.GetText());
                    foreach (var call in assistant.Content.OfType<ToolCallContent>())
                    {
                        chatAssistant.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                            call.Id, call.Name, BinaryData.FromString(call.Arguments.ToJsonString())));
                    }
                    result.Add(chatAssistant);
                    break;
                }
                case ToolResultMessage toolResult:
                    result.Add(new ToolChatMessage(toolResult.ToolCallId, toolResult.GetText()));
                    break;
            }
        }
        return result;
    }
}
