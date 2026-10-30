// ---------------------------------------------------------------------------
// 长期记忆（对应 pi 的 context files / resource-loader 机制）。
// pi 没有独立的 "memory" 子系统——长期记忆就是用户手写的 Markdown 上下文文件：
//   - 全局：~/.pi/agent/AGENTS.md
//   - 项目：从 cwd 沿父目录逐级向上发现（AGENTS.override.md > AGENTS.md > CLAUDE.md）
//   - 注入：system prompt 追加 <project_instructions path="..."> 段
//   - 更新：纯用户手动维护（+ /reload 热加载），运行时不自动改写
// 短期记忆则会话内的 transcript（配合 Compaction 压缩），见 Agent/Compaction.cs。
// ---------------------------------------------------------------------------

namespace ReactDemo.Agent;

/// <summary>长期记忆加载器：发现并渲染 context files（对应 pi 的 loadProjectContextFiles）。</summary>
public static class MemoryLoader
{
    /// <summary>候选文件名，按优先级排列（对应 pi 的 candidates，override 覆盖同名普通文件）。</summary>
    private static readonly string[] Candidates =
        ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];

    public sealed record MemoryFile(string Path, string Content);

    /// <summary>全局记忆目录（对应 pi 的 agentDir；本项目的等价物）。</summary>
    public static string DefaultAgentDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reactdemo");

    /// <summary>
    /// 发现长期记忆文件：全局优先，然后从 cwd 沿父目录逐级向上，
    /// 越靠近 cwd 的越具体（排在列表末尾）。每目录最多取一个文件。
    /// </summary>
    public static IReadOnlyList<MemoryFile> LoadContextFiles(string cwd, string? agentDir = null)
    {
        var files = new List<MemoryFile>();

        // 1) 全局
        var global = LoadFromDir(agentDir ?? DefaultAgentDir);
        if (global is not null) files.Add(global);

        // 2) 项目：cwd 向上遍历到根，结果反序（越靠近 cwd 越靠后 = 越具体）
        var ancestors = new List<MemoryFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = Path.GetFullPath(cwd);
        while (true)
        {
            var found = LoadFromDir(current);
            if (found is not null && seen.Add(found.Path)) ancestors.Add(found);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        ancestors.Reverse();
        files.AddRange(ancestors);
        return files;
    }

    /// <summary>渲染为 system prompt 附加段（对应 pi 的 renderProjectContext）。</summary>
    public static string RenderProjectContext(IReadOnlyList<MemoryFile> files)
    {
        if (files.Count == 0) return "";
        return "Project-specific instructions and guidelines:\n\n"
            + string.Join("\n\n", files.Select(f =>
                $"<project_instructions path=\"{f.Path}\">\n{f.Content.TrimEnd()}\n</project_instructions>"));
    }

    private static MemoryFile? LoadFromDir(string dir)
    {
        foreach (var name in Candidates)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                return new MemoryFile(path, File.ReadAllText(path).TrimStart('\uFEFF'));
            }
            catch (IOException)
            {
                return null;
            }
        }
        return null;
    }
}
