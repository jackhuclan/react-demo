// ---------------------------------------------------------------------------
// 技能管理（对应 pi 的 packages/coding-agent/src/core/skills.ts + utils/frontmatter.ts）。
// pi 的技能系统完全对齐 Anthropic Agent Skills 规范（agentskills.io），核心是
// "两级渐进式披露"（progressive disclosure），没有 skill 工具、没有关键词匹配：
//   1. 启动扫描：把目录里的 SKILL.md 解析成受校验的内存列表（frontmatter 的
//      name/description + 目录名回退 + first-wins 冲突处理）；
//   2. 元数据摘要注入 system prompt：<available_skills><skill><name/description/location>
//      XML 段——这是模型"看见"技能的唯一途径；
//   3. 正文按需加载：模型依据 description 自主调用 read 工具读取 <location> 文件；
//   4. 显式调用：用户输入 /skill:name args → 读文件剥 frontmatter → 包成
//      <skill ...> 文本块作为 user 消息注入（确定性逃生口）。
// 触发完全由 LLM 自主决定（描述写得好 + 模型自己会读文件）。
// ---------------------------------------------------------------------------

using System.Text;
using System.Text.RegularExpressions;

namespace ReactDemo.Agent;

/// <summary>已加载的技能（对应 pi 的 Skill 接口）。正文不在加载时保留，调用时再读盘。</summary>
public sealed record Skill
{
    /// <summary>技能名：frontmatter name，缺省用父目录名（用于 lookup 与模型可见列表）。</summary>
    public required string Name { get; init; }

    /// <summary>路由描述：模型据此判断何时使用该技能（必填，缺失则整个技能被丢弃）。</summary>
    public required string Description { get; init; }

    /// <summary>SKILL.md 绝对路径（注入 prompt 的 location；正文相对路径的解析基准）。</summary>
    public required string FilePath { get; init; }

    /// <summary>技能目录（FilePath 的 dirname）。正文引用的 scripts/references 相对它解析。</summary>
    public required string BaseDir { get; init; }

    /// <summary>来源标识："project" | "user" | "path"（用于展示与冲突诊断）。</summary>
    public required string Source { get; init; }

    /// <summary>true → 不进 system prompt 技能列表，仅可 /skill:name 显式调用。</summary>
    public bool DisableModelInvocation { get; init; }
}

/// <summary>加载诊断：warning（继续加载）/ collision（同名冲突，先到者胜）。</summary>
public sealed record SkillDiagnostic(string Type, string Message, string? Path = null)
{
    public override string ToString() => Path is null ? $"{Type}: {Message}" : $"{Type}: {Message} ({Path})";
}

/// <summary>批量加载结果：技能列表（发现顺序）+ 诊断。</summary>
public sealed record SkillLoadResult(IReadOnlyList<Skill> Skills, IReadOnlyList<SkillDiagnostic> Diagnostics)
{
    public static readonly SkillLoadResult Empty = new([], []);
}

/// <summary>
/// 技能加载与格式化（对应 pi 的 loadSkills / formatSkillsForPrompt / _expandSkillCommand）。
/// </summary>
public static partial class Skills
{
    /// <summary>规范上限（对应 pi 的 MAX_NAME_LENGTH / MAX_DESCRIPTION_LENGTH）。</summary>
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    /// <summary>项目技能目录（对应 pi 的 &lt;cwd&gt;/.pi/skills，提交进仓库可随项目分发）。</summary>
    public static string ProjectSkillsDir(string cwd) => Path.Combine(cwd, ".pi", "skills");

    /// <summary>用户技能目录（对应 pi 的 ~/.pi/agent/skills；本项目用 ~/.reactdemo/skills）。</summary>
    public static string UserSkillsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reactdemo", "skills");

    // -----------------------------------------------------------------------
    // 多源聚合：按传入优先级顺序扫描，同名 first-wins（先到者胜，后到者产生
    // collision 诊断）；同一物理文件（软链/重复路径）静默去重。对应 pi 的 loadSkills。
    // 注意：真实产品的语义是 project > user > package > builtin（CLI 显式路径最高），
    // 所以调用方必须把高优先级目录排在前面。
    // -----------------------------------------------------------------------
    public static SkillLoadResult Load(params (string Dir, string Source)[] sources)
    {
        var byName = new Dictionary<string, Skill>();
        var realPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<SkillDiagnostic>();

        foreach (var (dir, source) in sources)
        {
            var result = LoadFromDir(dir, source);
            diagnostics.AddRange(result.Diagnostics);
            foreach (var skill in result.Skills)
            {
                var canonical = Canonicalize(skill.FilePath);
                if (!realPaths.Add(canonical))
                {
                    continue; // 同一物理文件（软链等），静默跳过
                }
                if (byName.TryGetValue(skill.Name, out var winner))
                {
                    diagnostics.Add(new SkillDiagnostic("collision",
                        $"name \"{skill.Name}\" collision（保留 {winner.FilePath}，忽略本条）", skill.FilePath));
                    continue;
                }
                byName.Add(skill.Name, skill);
            }
        }

        return new SkillLoadResult([.. byName.Values], diagnostics);
    }

    // -----------------------------------------------------------------------
    // 目录扫描（对应 pi 的 loadSkillsFromDirInternal）。三条关键规则：
    //   1. 目录含 SKILL.md → 即"技能根"，解析后立即返回，不再向下递归；
    //   2. 仅顶层调用（includeRootFiles=true）时，根级 *.md 可作为单文件技能；
    //      嵌套子目录里的散落 .md 不算技能；
    //   3. 跳过以 "." 开头的条目与 node_modules。
    // （pi 还支持 .gitignore/.ignore 忽略规则与符号链接跟随，演示规模从简。）
    // -----------------------------------------------------------------------
    public static SkillLoadResult LoadFromDir(string dir, string source, bool includeRootFiles = true)
    {
        var skills = new List<Skill>();
        var diagnostics = new List<SkillDiagnostic>();
        if (!Directory.Exists(dir))
        {
            return new SkillLoadResult(skills, diagnostics);
        }

        // 规则 1：SKILL.md 短路
        var skillFile = Path.Combine(dir, "SKILL.md");
        if (File.Exists(skillFile))
        {
            var (skill, diags) = LoadFromFile(skillFile, source);
            skills.AddIfNotNull(skill);
            diagnostics.AddRange(diags);
            return new SkillLoadResult(skills, diagnostics);
        }

        // 规则 2/3：递归子目录 + 顶层单文件技能
        foreach (var entry in SafeEnumerate(dir))
        {
            if (entry.IsDirectory)
            {
                var sub = LoadFromDir(entry.FullPath, source, includeRootFiles: false);
                skills.AddRange(sub.Skills);
                diagnostics.AddRange(sub.Diagnostics);
            }
            else if (includeRootFiles && entry.FullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var (skill, diags) = LoadFromFile(entry.FullPath, source);
                skills.AddIfNotNull(skill);
                diagnostics.AddRange(diags);
            }
        }

        return new SkillLoadResult(skills, diagnostics);
    }

    /// <summary>解析单个技能文件（对应 pi 的 loadSkillFromFile）。description 缺失 → 丢弃；name/description 不合规 → 仅告警。</summary>
    public static (Skill? Skill, IReadOnlyList<SkillDiagnostic> Diagnostics) LoadFromFile(string filePath, string source)
    {
        var diagnostics = new List<SkillDiagnostic>();
        var isDeclaredSkill = Path.GetFileName(filePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);

        string raw;
        try
        {
            raw = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new SkillDiagnostic("warning", $"读取技能文件失败：{ex.Message}", filePath));
            return (null, diagnostics);
        }

        IReadOnlyDictionary<string, string> fields;
        try
        {
            (fields, _) = ParseFrontmatter(raw);
        }
        catch (Exception ex)
        {
            if (isDeclaredSkill)
            {
                diagnostics.Add(new SkillDiagnostic("warning", $"frontmatter 解析失败：{ex.Message}", filePath));
            }
            return (null, diagnostics);
        }

        // description 必填（缺失/空白 → 整条技能丢弃；超长仅告警）
        var description = fields.GetValueOrDefault("description") ?? "";
        if (string.IsNullOrWhiteSpace(description))
        {
            return (null, diagnostics);
        }
        if (description.Length > MaxDescriptionLength)
        {
            diagnostics.Add(new SkillDiagnostic("warning",
                $"description 超过 {MaxDescriptionLength} 字符（当前 {description.Length}）", filePath));
        }

        // name：frontmatter 优先，缺省回退父目录名；不合规仅告警不拒绝
        var name = fields.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        }
        foreach (var error in ValidateName(name))
        {
            diagnostics.Add(new SkillDiagnostic("warning", error, filePath));
        }

        return (new Skill
        {
            Name = name,
            Description = description,
            FilePath = Path.GetFullPath(filePath),
            BaseDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!,
            Source = source,
            DisableModelInvocation = fields.TryGetValue("disable-model-invocation", out var flag)
                && bool.TryParse(flag, out var b) && b, // 严格布尔语义（对应 pi 的 === true）
        }, diagnostics);
    }

    // -----------------------------------------------------------------------
    // system prompt 注入（对应 pi 的 formatSkillsForPrompt，Agent Skills 规范的 XML 格式）。
    // disable-model-invocation=true 的技能被排除；列表为空返回空串（不产生空 section）。
    // -----------------------------------------------------------------------
    public static string FormatForPrompt(IReadOnlyList<Skill> skills, string readToolName = "read_file")
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToList();
        if (visible.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine("The following skills provide specialized instructions for specific tasks.");
        sb.AppendLine($"Use the {readToolName} tool to load a skill's file when the task matches its description.");
        sb.AppendLine("When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.");
        sb.AppendLine();
        sb.AppendLine("<available_skills>");
        foreach (var skill in visible)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            sb.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{EscapeXml(skill.FilePath)}</location>");
            sb.AppendLine("  </skill>");
        }
        sb.Append("</available_skills>");
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // 显式调用（对应 pi 的 _expandSkillCommand / formatSkillInvocation）：
    // "/skill:name args" → 读文件 + 剥 frontmatter + <skill> 文本块 + args。
    // 非技能命令或未知技能原样透传（不报错）。正文作为 user 消息进入 transcript。
    // -----------------------------------------------------------------------
    public static string ExpandCommand(IReadOnlyList<Skill> skills, string text)
    {
        if (!text.StartsWith("/skill:", StringComparison.Ordinal))
        {
            return text;
        }

        var spaceIndex = text.IndexOf(' ');
        var skillName = spaceIndex == -1 ? text[7..] : text[7..spaceIndex];
        var args = spaceIndex == -1 ? "" : text[(spaceIndex + 1)..].Trim();

        var skill = skills.FirstOrDefault(s => s.Name == skillName);
        if (skill is null)
        {
            return text; // 未知技能，原样透传
        }

        var block = $"""
            <skill name="{skill.Name}" location="{skill.FilePath}">
            References are relative to {skill.BaseDir}.

            {ReadBody(skill)}
            </skill>
            """;
        return args.Length > 0 ? $"{block}\n\n{args}" : block;
    }

    /// <summary>读取技能正文（剥 frontmatter；对应 pi 的 readFileSync + stripFrontmatter）。pi 不缓存正文——刻意保持简单无状态。</summary>
    public static string ReadBody(Skill skill)
    {
        try
        {
            return ParseFrontmatter(File.ReadAllText(skill.FilePath)).Body.Trim();
        }
        catch (Exception ex)
        {
            return $"（技能正文读取失败：{ex.Message}）";
        }
    }

    // -----------------------------------------------------------------------
    // frontmatter 解析（对应 pi 的 utils/frontmatter.ts 朴素算法）：
    // 剥 BOM → CRLF/CR 归一为 LF → 必须以 --- 开头 → 找第一个 "\n---" 作结束边界。
    // YAML 演示规模只支持子集：key: value 单行、key: | 块标量、# 注释、空行。
    // -----------------------------------------------------------------------
    public static (IReadOnlyDictionary<string, string> Fields, string Body) ParseFrontmatter(string content)
    {
        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith("---"))
        {
            return (new Dictionary<string, string>(), normalized);
        }

        var endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex == -1)
        {
            return (new Dictionary<string, string>(), normalized);
        }

        var yaml = normalized[4..endIndex];
        var body = normalized[(endIndex + 4)..];
        return (ParseSimpleYaml(yaml), body);
    }

    /// <summary>极简 YAML 子集解析（pi 用完整 yaml 包；SKILL.md 实际只用 key: value 与块标量）。</summary>
    private static Dictionary<string, string> ParseSimpleYaml(string yaml)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;
        var blockLines = new List<string>();

        void FlushBlock()
        {
            if (currentKey is not null)
            {
                fields[currentKey] = string.Join("\n", blockLines).TrimEnd();
            }
            currentKey = null;
            blockLines.Clear();
        }

        foreach (var rawLine in yaml.Split('\n'))
        {
            // 块标量正文行（有缩进或空行，且处于块标量状态）
            if (currentKey is not null && (rawLine.StartsWith(' ') || rawLine.StartsWith('\t') || rawLine.Length == 0))
            {
                blockLines.Add(rawLine.Length == 0 ? "" : rawLine.TrimStart());
                continue;
            }

            FlushBlock();
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue; // 非 key: value 结构，静默忽略（对应 pi 的 unknown-field 语义）
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is "|" or ">" || (value.StartsWith("|") && value.Length == 1))
            {
                currentKey = key; // 块标量开始，等后续缩进行
            }
            else
            {
                // 去掉成对引号
                if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                {
                    value = value[1..^1];
                }
                fields[key] = value;
            }
        }
        FlushBlock();
        return fields;
    }

    /// <summary>name 校验（对应 pi 的 validateName）：小写字母/数字/连字符，≤64，首尾不得为 -，不得含 --。返回错误列表（仅告警不拒绝）。</summary>
    private static IEnumerable<string> ValidateName(string name)
    {
        if (name.Length > MaxNameLength)
        {
            yield return $"name 超过 {MaxNameLength} 字符（当前 {name.Length}）";
        }
        if (!NamePattern().IsMatch(name))
        {
            yield return "name 含非法字符（只允许小写 a-z、0-9、连字符）";
        }
        if (name.StartsWith('-') || name.EndsWith('-'))
        {
            yield return "name 不能以连字符开头或结尾";
        }
        if (name.Contains("--"))
        {
            yield return "name 不能包含连续连字符";
        }
    }

    private static IEnumerable<(string FullPath, bool IsDirectory)> SafeEnumerate(string dir)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(dir).EnumerateFileSystemInfos();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            // 规则 3：跳过隐藏/点开头条目与 node_modules
            if (entry.Name.StartsWith('.') || entry.Name == "node_modules")
            {
                continue;
            }
            var isDirectory = entry is DirectoryInfo { } d ? true
                : entry.Attributes.HasFlag(FileAttributes.Directory);
            yield return (entry.FullName, isDirectory);
        }
    }

    private static void AddIfNotNull(this List<Skill> list, Skill? skill)
    {
        if (skill is not null)
        {
            list.Add(skill);
        }
    }

    /// <summary>规范化路径用于物理去重（跟随符号链接的最终目标；pi 的 canonicalizePath）。</summary>
    private static string Canonicalize(string path)
    {
        try
        {
            var final = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return final?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return Path.GetFullPath(path);
        }
    }

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NamePattern();
}
