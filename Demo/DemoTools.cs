using System.Data;
using System.Text.Json.Nodes;
using ReactDemo.Agent;

namespace ReactDemo.Demo;

/// <summary>演示工具：四则运算计算器。RequiredArguments 用于触发"错误即观察"路径。</summary>
public sealed class CalculateTool : AgentTool
{
    public CalculateTool()
    {
        Name = "calculate";
        Label = "计算器";
        Description = "计算四则运算表达式并返回结果。expression 参数是表达式字符串，例如 \"12*34+6\"。";
        RequiredArguments = ["expression"];
        ParametersSchema = """
            {
              "type": "object",
              "properties": {
                "expression": { "type": "string", "description": "四则运算表达式，例如 12*34+6" }
              },
              "required": ["expression"]
            }
            """;
    }

    public override Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonObject arguments, CancellationToken ct, Action<ToolResult>? onPartialResult = null)
    {
        var expression = arguments["expression"]?.ToString() ?? "";
        // 演示部分结果事件（tool_execution_update）
        onPartialResult?.Invoke(ToolResult.Text($"正在计算 {expression} ..."));

        try
        {
            using var table = new DataTable();
            var value = table.Compute(expression, filter: null);
            return Task.FromResult(ToolResult.Text($"{expression} = {value}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Text($"表达式无效：{ex.Message}", isError: true));
        }
    }
}

/// <summary>演示工具：获取当前时间（无参数工具，验证 parallel 装载）。</summary>
public sealed class GetTimeTool : AgentTool
{
    public GetTimeTool()
    {
        Name = "get_time";
        Label = "当前时间";
        Description = "获取服务器当前本地时间。无参数。";
        ParametersSchema = """{ "type": "object", "properties": {} }""";
    }

    public override Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonObject arguments, CancellationToken ct, Action<ToolResult>? onPartialResult = null)
    {
        return Task.FromResult(ToolResult.Text(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")));
    }
}
