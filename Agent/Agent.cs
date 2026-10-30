using System.Collections.Concurrent;

namespace ReactDemo.Agent;

/// <summary>
/// 有状态封装（对应 pi 的 Agent 类）：持有 transcript 与工具集，
/// 提供 steering / followUp 队列、abort、事件订阅。
/// </summary>
public sealed class Agent
{
    private readonly List<AgentMessage> _messages = [];
    private readonly List<AgentTool> _tools = [];
    private readonly List<Action<AgentEvent>> _listeners = [];
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private CancellationTokenSource? _cts;

    public Agent(string? systemPrompt = null)
    {
        _steeringQueue = new PendingMessageQueue(QueueMode.OneAtATime);
        _followUpQueue = new PendingMessageQueue(QueueMode.OneAtATime);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            _messages.Add(new SystemMessage { Text = systemPrompt });
        }
    }

    public required StreamFn Stream { get; init; }
    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.Parallel;

    /// <summary>行动前钩子：可拦截工具调用，权限确认挂载点。</summary>
    public Func<BeforeToolCallContext, Task<BeforeToolCallResult?>>? BeforeToolCall { get; set; }

    /// <summary>行动后钩子：可改写工具返回结果。</summary>
    public Func<AfterToolCallContext, Task<AfterToolCallResult?>>? AfterToolCall { get; set; }

    /// <summary>每轮结束钩子：可强制 End / Continue。</summary>
    public Func<TurnContext, Task<TurnDecision>>? FinishTurn { get; set; }

    public IReadOnlyList<AgentMessage> Messages => _messages;
    public IReadOnlyList<AgentTool> Tools => _tools;
    public bool IsStreaming { get; private set; }
    public int PendingToolCallCount { get; private set; }

    public void AddTool(AgentTool tool) => _tools.Add(tool);

    /// <summary>当前轮结束后立即注入（对应 pi 的 agent.steer）。</summary>
    public void Steer(AgentMessage message) => _steeringQueue.Enqueue(message);

    /// <summary>agent 本来要停时才消费（对应 pi 的 agent.followUp）。</summary>
    public void FollowUp(AgentMessage message) => _followUpQueue.Enqueue(message);

    public void Abort() => _cts?.Cancel();

    public IDisposable Subscribe(Action<AgentEvent> listener)
    {
        _listeners.Add(listener);
        return new Unsubscriber(_listeners, listener);
    }

    public async Task PromptAsync(string text, CancellationToken ct = default)
    {
        if (IsStreaming)
        {
            throw new InvalidOperationException("Agent 正在处理中，请使用 Steer() 排队消息，或等待完成。");
        }
        await RunWithLifecycleAsync([new UserMessage(text)], ct);
    }

    /// <summary>从当前 transcript 继续，最后一条消息必须是 user 或 toolResult（用于重试/续跑）。</summary>
    public async Task ContinueAsync(CancellationToken ct = default)
    {
        if (IsStreaming)
        {
            throw new InvalidOperationException("Agent 正在处理中。");
        }
        var last = _messages.LastOrDefault();
        if (last is AssistantMessage or null)
        {
            throw new InvalidOperationException("最后一条消息不是 user / toolResult，无法继续。");
        }
        await RunWithLifecycleAsync([], ct);
    }

    private async Task RunWithLifecycleAsync(IReadOnlyList<AgentMessage> prompts, CancellationToken outerCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        IsStreaming = true;
        PendingToolCallCount = 0;
        try
        {
            var context = new AgentContext { Messages = _messages, Tools = _tools };
            var config = new AgentLoopConfig
            {
                Stream = Stream,
                ToolExecution = ToolExecution,
                BeforeToolCall = BeforeToolCall,
                AfterToolCall = AfterToolCall,
                FinishTurn = FinishTurn,
                GetSteeringMessages = () => Task.FromResult<IReadOnlyList<AgentMessage>>(_steeringQueue.Drain()),
                GetFollowUpMessages = () => Task.FromResult<IReadOnlyList<AgentMessage>>(_followUpQueue.Drain()),
            };

            var newMessages = await AgentLoop.RunAsync(prompts, context, config, Dispatch, _cts.Token);
            // 循环在工作副本上运行，结束后把本轮新消息合并回真实 transcript
            _messages.AddRange(newMessages);
        }
        finally
        {
            IsStreaming = false;
            PendingToolCallCount = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>状态归约 + 事件分发（对应 pi 的 processEvents）。演示用同步分发保证顺序。</summary>
    private void Dispatch(AgentEvent e)
    {
        switch (e)
        {
            case ToolExecutionStartEvent:
                PendingToolCallCount++;
                break;
            case ToolExecutionEndEvent:
                PendingToolCallCount = Math.Max(0, PendingToolCallCount - 1);
                break;
        }
        foreach (var listener in _listeners)
        {
            listener(e);
        }
    }

    private sealed class Unsubscriber(List<Action<AgentEvent>> listeners, Action<AgentEvent> listener) : IDisposable
    {
        public void Dispose() => listeners.Remove(listener);
    }
}

/// <summary>待处理消息队列，对应 pi 的 PendingMessageQueue。</summary>
public sealed class PendingMessageQueue(QueueMode mode)
{
    private readonly ConcurrentQueue<AgentMessage> _messages = new();

    public QueueMode Mode { get; set; } = mode;

    public void Enqueue(AgentMessage message) => _messages.Enqueue(message);

    public IReadOnlyList<AgentMessage> Drain()
    {
        if (Mode == QueueMode.All)
        {
            var all = new List<AgentMessage>();
            while (_messages.TryDequeue(out var m)) all.Add(m);
            return all;
        }
        return _messages.TryDequeue(out var one) ? [one] : [];
    }
}
