using System.CommandLine;
using DotNetEnv;
using ReactDemo.Demo;

// ---------------------------------------------------------------------------
// C# 版 ReAct 演示：感知 -> 推理 -> 行动 -> 观察 循环。
// 入口用 System.CommandLine 路由不同分支（对应 pi main.ts 的 CLI 参数解析）：
//   （默认）        交互式 TUI（Spectre.Console，真实模型）
//   dotnet run -- mock / compact / skills   一次性 headless 脚本化演示
//   全局参数：--new 强制新建会话；--no-skills 禁用技能；--prompt 指定提示词；
//             --tree 打印会话条目树；--mock（仅 TUI）使用离线脚本化 LLM
// ---------------------------------------------------------------------------

Env.Load(Path.Combine(AppContext.BaseDirectory, ".env"));

var forceNewOption = new Option<bool>("--new") { Description = "强制新建会话（默认继续最近会话）" };
var treeOption = new Option<bool>("--tree") { Description = "结束后打印会话条目树" };
var noSkillsOption = new Option<bool>("--no-skills") { Description = "禁用技能加载" };
var promptOption = new Option<string?>("--prompt") { Description = "自定义提示词" };
var mockTuiOption = new Option<bool>("--mock") { Description = "TUI 使用离线脚本化 LLM（无需 API Key）" };

var mockCommand = new Command("mock") { Description = "离线脚本化演示（错误即观察 + steering）" };
mockCommand.Options.Add(forceNewOption);
mockCommand.Options.Add(treeOption);
mockCommand.Options.Add(noSkillsOption);
mockCommand.Options.Add(promptOption);
mockCommand.SetAction(parseResult => HeadlessDemo.RunAsync(
    "mock",
    parseResult.GetValue(forceNewOption),
    parseResult.GetValue(treeOption),
    parseResult.GetValue(noSkillsOption),
    parseResult.GetValue(promptOption)));

var compactCommand = new Command("compact") { Description = "离线上下文压缩演示（阈值触发 + LLM 摘要 + 原位替换）" };
compactCommand.Options.Add(forceNewOption);
compactCommand.Options.Add(treeOption);
compactCommand.Options.Add(noSkillsOption);
compactCommand.SetAction(parseResult => HeadlessDemo.RunAsync(
    "compact",
    parseResult.GetValue(forceNewOption),
    parseResult.GetValue(treeOption),
    parseResult.GetValue(noSkillsOption),
    null));

var skillsCommand = new Command("skills") { Description = "离线技能管理演示（摘要注入 + read 按需加载 + /skill: 展开）" };
skillsCommand.Options.Add(forceNewOption);
skillsCommand.Options.Add(treeOption);
skillsCommand.Options.Add(noSkillsOption);
skillsCommand.SetAction(parseResult => HeadlessDemo.RunAsync(
    "skills",
    parseResult.GetValue(forceNewOption),
    parseResult.GetValue(treeOption),
    parseResult.GetValue(noSkillsOption),
    null));

var rootCommand = new RootCommand("ReactDemo — C# 版 ReAct Agent 演示（默认进入交互式 TUI）");
rootCommand.Options.Add(mockTuiOption);
rootCommand.Options.Add(forceNewOption);
rootCommand.Options.Add(noSkillsOption);
rootCommand.Options.Add(promptOption);
rootCommand.Subcommands.Add(mockCommand);
rootCommand.Subcommands.Add(compactCommand);
rootCommand.Subcommands.Add(skillsCommand);
rootCommand.SetAction(parseResult => TuiApp.RunAsync(new TuiOptions
{
    UseMock = parseResult.GetValue(mockTuiOption),
    ForceNewSession = parseResult.GetValue(forceNewOption),
    NoSkills = parseResult.GetValue(noSkillsOption),
    InitialPrompt = parseResult.GetValue(promptOption),
}));

return await rootCommand.Parse(args).InvokeAsync();
