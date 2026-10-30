using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReactDemo.Agent;

// ---------------------------------------------------------------------------
// 会话管理（对应 pi 的 packages/coding-agent/src/core/session-manager.ts）。
// 设计要点（与 pi 一致）：
//   1. JSONL append-only 持久化：首行 session header，其余为 entries；
//      只追加不修改，坏行解析时跳过；懒写盘（首次 append 才创建文件）。
//   2. 树结构：每个 entry 有 id/parentId；leaf 指针标记当前位置；
//      追加 = 在 leaf 下挂子节点并推进 leaf；分支 = 把 leaf 指回历史节点。
//   3. 投影：BuildProjection 从 leaf 回溯到根，压缩感知地重建模型可见消息
//      （对应 pi 的 buildSessionPath / buildContextEntries / buildSessionProjection）。
// 与 pi 的差异（演示规模的取舍）：
//   - entry 类型裁剪为 message / compaction / branch_summary / session_info 四种
//     （pi 还有 usage/label/context_edit/custom 等）；
//   - 无版本迁移框架（v1 起步，保留 version 字段与 header 校验）；
//   - list 不做并发流式读取（演示规模直接全量解析）；
//   - 未实现 context_edit / forkFrom / createBranchedSession。
// ---------------------------------------------------------------------------

/// <summary>会话文件首行（对应 pi 的 SessionHeader）。</summary>
public sealed record SessionHeader
{
    public required string Type { get; init; }   // 固定 "session"
    public int Version { get; init; } = 1;
    public required string Id { get; init; }
    public required string Timestamp { get; init; }
    public required string Cwd { get; init; }
    public string? ParentSession { get; init; }
}

/// <summary>会话条目基类：id/parentId 构成树（对应 pi 的 SessionEntryBase）。</summary>
public abstract record SessionEntry(string Id, string? ParentId, DateTimeOffset Timestamp);

/// <summary>消息条目：transcript 中的任意消息（含 system 工具装载声明）。</summary>
public sealed record MessageEntry(string Id, string? ParentId, DateTimeOffset Timestamp, AgentMessage Message)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>压缩条目：摘要 + system 快照 + 保留起点指针。投影时替换其之前的历史。</summary>
public sealed record CompactionEntry(string Id, string? ParentId, DateTimeOffset Timestamp,
    string Summary, string FirstKeptEntryId, int TokensBefore, IReadOnlyList<SystemMessage> SystemMessages)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>分支摘要条目：离开旧分支时生成的 LLM 摘要。</summary>
public sealed record BranchSummaryEntry(string Id, string? ParentId, DateTimeOffset Timestamp,
    string FromId, string Summary)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>会话元数据条目：重命名等（append-only，取最新一条生效）。</summary>
public sealed record SessionInfoEntry(string Id, string? ParentId, DateTimeOffset Timestamp, string Name)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>会话概要信息（对应 pi 的 SessionInfo，用于 list 枚举）。</summary>
public sealed record SessionInfo(
    string Path, string Id, string? Name, DateTimeOffset Modified, int MessageCount, string FirstMessage);

/// <summary>
/// 会话管理器：append-only JSONL 会话树 + 投影。
/// 对应 pi 的 SessionManager（演示规模裁剪版）。
/// </summary>
public sealed class SessionManager
{
    private readonly List<SessionEntry> _entries = [];
    private readonly Dictionary<string, SessionEntry> _byId = new(StringComparer.Ordinal);
    // transcript 消息实例 → 所属 entry id（引用弱表；投影重建的消息也登记，保证压缩切点可回查）
    private readonly ConditionalWeakTable<AgentMessage, string> _messageEntryIds = new();

    private readonly SessionHeader _header;
    private readonly bool _persist;
    private readonly string _dir;
    private string? _file;
    private bool _fileCreated;
    private string? _leafId;

    private SessionManager(SessionHeader header, string dir, bool persist)
    {
        _header = header;
        _dir = dir;
        _persist = persist;
    }

    // ---- 工厂 ---------------------------------------------------------------

    /// <summary>新会话（懒写盘：首次 append 才创建文件）。</summary>
    public static SessionManager Create(string? cwd = null, string? sessionDir = null, string? parentSession = null)
    {
        cwd ??= Environment.CurrentDirectory;
        sessionDir ??= GetSessionDir(cwd);
        var header = new SessionHeader
        {
            Type = "session",
            Version = 1,
            Id = Guid.CreateVersion7().ToString(),
            Timestamp = DateTimeOffset.Now.ToString("O"),
            Cwd = cwd,
            ParentSession = parentSession,
        };
        return new SessionManager(header, sessionDir, persist: true);
    }

    /// <summary>打开指定 JSONL 会话文件。首行必须是合法 header，坏行跳过。</summary>
    public static SessionManager? Open(string path)
    {
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return null;

        SessionHeader? header;
        try
        {
            var node = JsonNode.Parse(lines[0])!.AsObject();
            if (node["type"]?.ToString() != "session") return null;
            header = new SessionHeader
            {
                Type = "session",
                Version = node["version"]?.GetValue<int>() ?? 1,
                Id = node["id"]!.ToString(),
                Timestamp = node["timestamp"]!.ToString(),
                Cwd = node["cwd"]!.ToString(),
                ParentSession = node["parentSession"]?.ToString(),
            };
        }
        catch (Exception)
        {
            return null;
        }

        var manager = new SessionManager(header, Path.GetDirectoryName(path)!, persist: true)
        {
            _file = path,
            _fileCreated = true,
        };
        manager.LoadEntries(lines.Skip(1));
        return manager;
    }

    /// <summary>继续最近会话（按 mtime），没有可用会话时新建（对应 pi 的 continueRecent）。</summary>
    public static SessionManager ContinueRecent(string? cwd = null, string? sessionDir = null)
    {
        cwd ??= Environment.CurrentDirectory;
        sessionDir ??= GetSessionDir(cwd);
        var recent = Directory.Exists(sessionDir)
            ? Directory.GetFiles(sessionDir, "*.jsonl")
                .Select(f => (Path: f, Modified: File.GetLastWriteTime(f)))
                .OrderByDescending(x => x.Modified)
                .ToList()
            : [];
        foreach (var (path, _) in recent)
        {
            var session = Open(path);
            if (session is not null) return session;
        }
        return Create(cwd, sessionDir);
    }

    /// <summary>列出当前项目的全部会话概要（对应 pi 的 list，演示规模全量解析）。</summary>
    public static IReadOnlyList<SessionInfo> List(string? cwd = null, string? sessionDir = null)
    {
        cwd ??= Environment.CurrentDirectory;
        sessionDir ??= GetSessionDir(cwd);
        if (!Directory.Exists(sessionDir)) return [];

        var result = new List<SessionInfo>();
        foreach (var path in Directory.GetFiles(sessionDir, "*.jsonl").OrderByDescending(File.GetLastWriteTime))
        {
            var session = Open(path);
            if (session is null) continue;
            var messageEntries = session._entries.OfType<MessageEntry>().ToList();
            var firstUser = messageEntries.Select(e => e.Message).OfType<UserMessage>().FirstOrDefault()?.GetText();
            result.Add(new SessionInfo(
                Path: path,
                Id: session._header.Id,
                Name: session.GetSessionName(),
                Modified: File.GetLastWriteTime(path),
                MessageCount: messageEntries.Count,
                FirstMessage: firstUser ?? "(无用户消息)"));
        }
        return result;
    }

    // ---- 状态 ---------------------------------------------------------------

    public string SessionId => _header.Id;
    public string? SessionFile => _file;
    public string? LeafId => _leafId;
    public int EntryCount => _entries.Count;
    public IReadOnlyList<SessionEntry> Entries => _entries;

    /// <summary>会话名（最新一条 session_info，对应 pi 的 getSessionName）。</summary>
    public string? GetSessionName() =>
        _entries.OfType<SessionInfoEntry>().LastOrDefault()?.Name;

    /// <summary>查询 transcript 消息实例所属的 entry id（投影重建的消息同样可查）。</summary>
    public bool TryGetEntryId(AgentMessage message, out string? entryId)
    {
        if (_messageEntryIds.TryGetValue(message, out var id))
        {
            entryId = id;
            return true;
        }
        entryId = null;
        return false;
    }

    // ---- 追加（append-only，立即落盘） ----------------------------------------

    public string AppendMessage(AgentMessage message)
    {
        // 同一消息实例只登记一次（循环每次 run 会重发已有 transcript 的消息）
        if (_messageEntryIds.TryGetValue(message, out var existing)) return existing;
        var entry = new MessageEntry(NewId(), _leafId, DateTimeOffset.Now, message);
        Append(entry);
        _messageEntryIds.Add(message, entry.Id);
        return entry.Id;
    }

    /// <summary>
    /// 追加压缩条目（对应 pi 的 appendCompaction；firstKeptEntryId 为空时视为全部压缩）。
    /// systemMessages 是压缩边界的 system prompt 快照（对应 pi 的 CompactionEntry.systemMessage），
    /// 投影时重放，保证恢复会话后模型仍能看到 system prompt。
    /// </summary>
    public string AppendCompaction(string summary, string? firstKeptEntryId, int tokensBefore,
        IReadOnlyList<SystemMessage>? systemMessages = null)
    {
        var entry = new CompactionEntry(NewId(), _leafId, DateTimeOffset.Now,
            summary, firstKeptEntryId ?? NewId(), tokensBefore, systemMessages ?? []);
        Append(entry);
        return entry.Id;
    }

    /// <summary>追加分支摘要条目（对应 pi 的 branchWithSummary）。</summary>
    public string AppendBranchSummary(string fromId, string summary)
    {
        var entry = new BranchSummaryEntry(NewId(), _leafId, DateTimeOffset.Now, fromId, summary);
        Append(entry);
        return entry.Id;
    }

    /// <summary>重命名会话（append-only，追加一条 session_info）。</summary>
    public string AppendSessionInfo(string name)
    {
        var entry = new SessionInfoEntry(NewId(), _leafId, DateTimeOffset.Now, name);
        Append(entry);
        return entry.Id;
    }

    // ---- 树与分支 --------------------------------------------------------------

    /// <summary>分支：把 leaf 指回历史节点，历史绝不修改/删除（对应 pi 的 branch）。</summary>
    public void Branch(string entryId)
    {
        if (!_byId.ContainsKey(entryId))
        {
            throw new InvalidOperationException($"会话中不存在条目 {entryId}，无法分支。");
        }
        _leafId = entryId;
    }

    /// <summary>leaf 置空：清空当前路径（对应 pi 的 resetLeaf，重编辑第一条消息）。</summary>
    public void ResetLeaf() => _leafId = null;

    /// <summary>从指定节点（默认 leaf）回溯到根的路径（对应 pi 的 buildSessionPath）。</summary>
    public IReadOnlyList<SessionEntry> GetBranch(string? fromId = null)
    {
        var leaf = fromId is not null ? _byId.GetValueOrDefault(fromId)
            : _leafId is not null ? _byId.GetValueOrDefault(_leafId)
            : null;
        if (leaf is null) return [];

        var path = new List<SessionEntry>();
        var current = leaf;
        while (current is not null)
        {
            path.Add(current);
            current = current.ParentId is not null ? _byId.GetValueOrDefault(current.ParentId) : null;
        }
        path.Reverse();  // 根 → leaf
        return path;
    }

    // ---- 投影 ------------------------------------------------------------------

    /// <summary>
    /// 压缩感知地重建模型可见消息（对应 pi 的 buildContextEntries + buildSessionProjection）：
    /// [最新压缩条目 → 摘要消息] + [firstKeptEntryId 起到压缩点前的条目（去 system）] + [压缩点之后全部]。
    /// 无压缩条目时就是完整路径。
    /// </summary>
    public IReadOnlyList<AgentMessage> BuildProjection()
    {
        var path = GetBranch(_leafId);
        var contextEntries = BuildContextEntries(path);

        var messages = new List<AgentMessage>();
        for (var i = 0; i < contextEntries.Count; i++)
        {
            var entry = contextEntries[i];
            // 对应 pi 的 index > 0 置空规则：仅最新（index 0）压缩条目产出摘要消息，
            // 迭代式重压缩时旧压缩条目若落在保留区则贡献为空
            if (entry is CompactionEntry && i > 0) continue;
            foreach (var message in EntryToMessages(entry))
            {
                messages.Add(message);
            }
        }
        return messages;
    }

    /// <summary>压缩条目感知的活动条目列表（对应 pi 的 buildContextEntries）。</summary>
    private static List<SessionEntry> BuildContextEntries(IReadOnlyList<SessionEntry> path)
    {
        var compactionIndex = -1;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (path[i] is CompactionEntry) { compactionIndex = i; break; }
        }
        if (compactionIndex < 0) return [.. path];

        var compaction = (CompactionEntry)path[compactionIndex];
        var context = new List<SessionEntry> { compaction };
        var foundFirstKept = false;
        for (var i = 0; i < compactionIndex; i++)
        {
            var entry = path[i];
            if (entry.Id == compaction.FirstKeptEntryId) foundFirstKept = true;
            // firstKept 起才保留，且跳过 system 消息（system prompt 由运行时重新声明）
            if (foundFirstKept && entry is not MessageEntry { Message: SystemMessage })
            {
                context.Add(entry);
            }
        }
        context.AddRange(path.Skip(compactionIndex + 1));
        return context;
    }

    /// <summary>条目 → 模型可见消息（对应 pi 的 sessionEntryToContextMessages）。</summary>
    private List<AgentMessage> EntryToMessages(SessionEntry entry)
    {
        switch (entry)
        {
            case MessageEntry m:
                _messageEntryIds.AddOrUpdate(m.Message, m.Id);
                return [m.Message];
            case CompactionEntry c:
            {
                // system 快照重放（对应 pi 的 entry.systemMessage ? [entry.systemMessage, summary] : [summary]）
                var result = new List<AgentMessage>();
                foreach (var sys in c.SystemMessages)
                {
                    _messageEntryIds.AddOrUpdate(sys, c.Id);
                    result.Add(sys);
                }
                var summary = new CompactionSummaryMessage(c.Summary, c.TokensBefore);
                _messageEntryIds.AddOrUpdate(summary, c.Id);
                result.Add(summary);
                return result;
            }
            case BranchSummaryEntry b:
            {
                // 分支摘要按 pi 约定包 user 消息（branchSummary 标签）
                var message = new UserMessage(
                    $"The following is a summary of a branch that this conversation came back from:\n\n<branch-summary>\n{b.Summary}\n</branch-summary>");
                _messageEntryIds.AddOrUpdate(message, b.Id);
                return [message];
            }
            default:
                return [];  // session_info 等不进上下文
        }
    }

    // ---- 内部：持久化 -----------------------------------------------------------

    private void Append(SessionEntry entry)
    {
        _entries.Add(entry);
        _byId[entry.Id] = entry;
        _leafId = entry.Id;
        if (!_persist) return;
        EnsureFile();
        File.AppendAllText(_file!, SessionCodec.Serialize(entry) + "\n");
    }

    private void EnsureFile()
    {
        if (_fileCreated) return;
        Directory.CreateDirectory(_dir);
        var fileTimestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH-mm-ss-fff");
        _file = Path.Combine(_dir, $"{fileTimestamp}_{_header.Id}.jsonl");
        File.WriteAllText(_file, SessionCodec.SerializeHeader(_header) + "\n");
        _fileCreated = true;
    }

    private void LoadEntries(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = SessionCodec.Deserialize(line, _leafId);
                if (entry is null) continue;  // 坏行跳过
                _entries.Add(entry);
                _byId[entry.Id] = entry;
                _leafId = entry.Id;
            }
            catch (Exception)
            {
                // 损坏行跳过（对应 pi 的 parseSessionEntryLine 容错）
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>会话目录：~/.reactdemo/sessions/&lt;编码后的 cwd&gt;（可用 REACTDEMO_SESSION_DIR 覆盖）。</summary>
    public static string GetSessionDir(string cwd)
    {
        var overrideDir = Environment.GetEnvironmentVariable("REACTDEMO_SESSION_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
        var agentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reactdemo");
        var safePath = $"--{cwd.TrimStart('/', '\\').Replace('\\', '-').Replace('/', '-').Replace(':', '-') }--";
        return Path.Combine(agentDir, "sessions", safePath);
    }
}

/// <summary>JSONL 编解码：SessionEntry ↔ JSON 行（演示用手写映射，字段与 pi 语义对齐）。</summary>
internal static class SessionCodec
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string SerializeHeader(SessionHeader header) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["type"] = "session",
            ["version"] = header.Version,
            ["id"] = header.Id,
            ["timestamp"] = header.Timestamp,
            ["cwd"] = header.Cwd,
            ["parentSession"] = header.ParentSession,
        }, Options);

    public static string Serialize(SessionEntry entry)
    {
        var node = new JsonObject
        {
            ["type"] = entry switch
            {
                MessageEntry => "message",
                CompactionEntry => "compaction",
                BranchSummaryEntry => "branch_summary",
                SessionInfoEntry => "session_info",
                _ => throw new NotSupportedException(entry.GetType().Name),
            },
            ["id"] = entry.Id,
            ["parentId"] = entry.ParentId,
            ["timestamp"] = entry.Timestamp.ToString("O"),
        };
        switch (entry)
        {
            case MessageEntry m:
                node["message"] = SerializeMessage(m.Message);
                break;
            case CompactionEntry c:
                node["summary"] = c.Summary;
                node["firstKeptEntryId"] = c.FirstKeptEntryId;
                node["tokensBefore"] = c.TokensBefore;
                node["systemMessages"] = new JsonArray(c.SystemMessages.Select(SerializeMessage).ToArray());
                break;
            case BranchSummaryEntry b:
                node["fromId"] = b.FromId;
                node["summary"] = b.Summary;
                break;
            case SessionInfoEntry s:
                node["name"] = s.Name;
                break;
        }
        return JsonSerializer.Serialize(node, Options);
    }

    public static SessionEntry? Deserialize(string line, string? fallbackParentId)
    {
        var node = JsonNode.Parse(line)?.AsObject();
        if (node is null) return null;
        var id = node["id"]?.ToString();
        if (string.IsNullOrEmpty(id)) return null;
        var parentId = node["parentId"]?.ToString() ?? fallbackParentId;
        var timestamp = DateTimeOffset.TryParse(node["timestamp"]?.ToString(), out var ts) ? ts : DateTimeOffset.Now;

        return node["type"]?.ToString() switch
        {
            "message" when node["message"] is { } messageNode
                && DeserializeMessage(messageNode) is { } message
                => new MessageEntry(id!, parentId, timestamp, message),
            "compaction" when node["summary"] is { } summary && node["firstKeptEntryId"] is { } kept
                => new CompactionEntry(id!, parentId, timestamp, summary.ToString(), kept.ToString(),
                    node["tokensBefore"]?.GetValue<int>() ?? 0,
                    node["systemMessages"] is JsonArray sys
                        ? [.. sys.Select(n => DeserializeMessage(n!)).OfType<SystemMessage>()]
                        : []),
            "branch_summary" when node["summary"] is { } summary && node["fromId"] is { } fromId
                => new BranchSummaryEntry(id!, parentId, timestamp, fromId.ToString(), summary.ToString()),
            "session_info" when node["name"] is { } name
                => new SessionInfoEntry(id!, parentId, timestamp, name.ToString()),
            _ => null,
        };
    }

    // ---- AgentMessage ↔ JSON ----------------------------------------------------

    private static JsonObject SerializeMessage(AgentMessage message)
    {
        var node = new JsonObject { ["role"] = message switch
        {
            SystemMessage => "system",
            UserMessage => "user",
            AssistantMessage => "assistant",
            ToolResultMessage => "toolResult",
            CompactionSummaryMessage => "compactionSummary",
            _ => throw new NotSupportedException(message.GetType().Name),
        } };
        switch (message)
        {
            case SystemMessage sys:
                node["text"] = sys.Text;
                node["toolsAdded"] = new JsonArray(sys.ToolsAdded.Select(t => JsonValue.Create(t)).ToArray());
                node["toolsRemoved"] = new JsonArray(sys.ToolsRemoved.Select(t => JsonValue.Create(t)).ToArray());
                break;
            case UserMessage user:
                node["text"] = user.GetText();
                break;
            case AssistantMessage assistant:
                node["text"] = assistant.GetText();
                node["stopReason"] = assistant.StopReason.ToString();
                node["errorMessage"] = assistant.ErrorMessage;
                // toolCall 参数以 JSON 值形式存放，反序列化时还原为 JsonObject
                node["toolCalls"] = new JsonArray(assistant.Content.OfType<ToolCallContent>().Select(c =>
                    (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["name"] = c.Name,
                        ["arguments"] = JsonNode.Parse(c.Arguments.ToJsonString()),
                    }).ToArray());
                break;
            case ToolResultMessage toolResult:
                node["toolCallId"] = toolResult.ToolCallId;
                node["toolName"] = toolResult.ToolName;
                node["text"] = toolResult.GetText();
                node["isError"] = toolResult.IsError;
                break;
            case CompactionSummaryMessage compaction:
                node["summary"] = compaction.Summary;
                node["tokensBefore"] = compaction.TokensBefore;
                break;
        }
        return node;
    }

    private static AgentMessage? DeserializeMessage(JsonNode node)
    {
        var obj = node.AsObject();
        var role = obj["role"]?.ToString();
        var text = obj["text"]?.ToString();
        return role switch
        {
            "system" => new SystemMessage
            {
                Text = text,
                ToolsAdded = [.. obj["toolsAdded"]?.AsArray().Select(t => t!.ToString()) ?? []],
                ToolsRemoved = [.. obj["toolsRemoved"]?.AsArray().Select(t => t!.ToString()) ?? []],
            },
            "user" => new UserMessage(text ?? ""),
            "assistant" => new AssistantMessage
            {
                Content = BuildAssistantContent(obj, text ?? ""),
                StopReason = Enum.TryParse<StopReason>(obj["stopReason"]?.ToString(), out var sr) ? sr : StopReason.Stop,
                ErrorMessage = obj["errorMessage"]?.ToString(),
            },
            "toolResult" => new ToolResultMessage
            {
                ToolCallId = obj["toolCallId"]?.ToString() ?? "",
                ToolName = obj["toolName"]?.ToString() ?? "",
                Content = [new TextContent(text ?? "")],
                IsError = obj["isError"]?.GetValue<bool>() ?? false,
            },
            "compactionSummary" => new CompactionSummaryMessage(
                obj["summary"]?.ToString() ?? "", obj["tokensBefore"]?.GetValue<int>() ?? 0),
            _ => null,
        };
    }

    private static List<ContentBlock> BuildAssistantContent(JsonObject obj, string text)
    {
        var content = new List<ContentBlock>();
        if (text.Length > 0) content.Add(new TextContent(text));
        if (obj["toolCalls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                var c = call!.AsObject();
                var arguments = c["arguments"] is { } args
                    ? args.Deserialize<JsonObject>() ?? new JsonObject()
                    : new JsonObject();
                content.Add(new ToolCallContent(c["id"]!.ToString(), c["name"]!.ToString(), arguments));
            }
        }
        return content;
    }
}
