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

/// <summary>
/// 演示工具：读取文件文本（对应 pi 的 read 工具）。
/// 技能系统依赖它实现"正文按需加载"：模型看到 &lt;available_skills&gt; 摘要后，
/// 自主调用本工具读取 &lt;location&gt; 指向的 SKILL.md 全文。
/// 限制对齐 pi：最多 2000 行，单行截断到 2000 字符。
/// </summary>
public sealed class ReadFileTool : AgentTool
{
    private const int MaxLines = 2000;
    private const int MaxLineLength = 2000;

    public ReadFileTool()
    {
        Name = "read_file";
        Label = "读文件";
        Description = "读取指定路径的文本文件并返回内容（最多 2000 行，单行超长会截断）。path 参数为绝对路径。";
        RequiredArguments = ["path"];
        ParametersSchema = """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "要读取的文件的绝对路径" }
              },
              "required": ["path"]
            }
            """;
    }

    public override Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonObject arguments, CancellationToken ct, Action<ToolResult>? onPartialResult = null)
    {
        var path = arguments["path"]?.ToString() ?? "";
        try
        {
            if (!File.Exists(path))
            {
                return Task.FromResult(ToolResult.Text($"文件不存在：{path}", isError: true));
            }
            var lines = File.ReadAllLines(path);
            var truncated = lines.Take(MaxLines)
                .Select(l => l.Length > MaxLineLength ? l[..MaxLineLength] + "…[truncated]" : l);
            var note = lines.Length > MaxLines ? $"\n…（共 {lines.Length} 行，仅显示前 {MaxLines} 行）" : "";
            return Task.FromResult(ToolResult.Text(string.Join('\n', truncated) + note));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Text($"读取失败：{ex.Message}", isError: true));
        }
    }
}
