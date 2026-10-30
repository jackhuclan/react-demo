using ReactDemo.Agent;

namespace ReactDemo.Demo;

/// <summary>把 AgentEvent 打印成控制台日志，演示 message_update 流式差量渲染。</summary>
public static class ConsoleEventPrinter
{
    // 当前 assistant 消息已打印长度（partial 每次 with 都是新对象，用游标做差量）
    private static int _cursor;

    public static void Print(AgentEvent e)
    {
        switch (e)
        {
            case TurnStartEvent:
                Color(ConsoleColor.Cyan, "── new turn ──");
                break;

            case MessageStartEvent { Message: AssistantMessage }:
                _cursor = 0;
                break;

            case MessageUpdateEvent { Message: AssistantMessage a }:
                PrintAssistantDelta(a);
                break;

            case MessageEndEvent { Message: AssistantMessage a2 }:
                PrintAssistantDelta(a2);
                _cursor = 0;
                Console.WriteLine();
                break;

            case MessageStartEvent { Message: ToolResultMessage }:
            case MessageEndEvent { Message: ToolResultMessage }:
                // 工具结果由 ToolExecutionEndEvent 打印，避免重复
                break;

            case ToolExecutionStartEvent tool:
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"  [工具执行] {tool.ToolName}({tool.Arguments.ToJsonString()}) ");
                Console.ResetColor();
                break;

            case ToolExecutionEndEvent toolEnd:
                Color(toolEnd.IsError ? ConsoleColor.Red : ConsoleColor.Green, $"=> {toolEnd.Result.GetText()}");
                break;

            case AgentEndEvent:
                Color(ConsoleColor.Cyan, "── agent_end ──");
                break;
        }
    }

    private static void PrintAssistantDelta(AssistantMessage a)
    {
        var full = a.GetText();
        if (full.Length > _cursor)
        {
            Console.Write(full[_cursor..]);
            _cursor = full.Length;
        }
    }

    private static void Color(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
