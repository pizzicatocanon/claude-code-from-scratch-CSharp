// Memory system — file-based persistent memory with semantic recall.
// Mirrors src/memory.ts.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MiniClaude;

public enum MemoryType { User, Feedback, Project, Reference }

public class MemoryHeader
{
    public string Filename { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long MtimeMs { get; set; }
    public string? Description { get; set; }
    public MemoryType? Type { get; set; }
}

public class RelevantMemory
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public long MtimeMs { get; set; }
    public string Header { get; set; } = "";
}

public class MemoryListItem
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
}

public class MemoryPrefetch
{
    public Task<List<RelevantMemory>> Promise { get; set; } = Task.FromResult(new List<RelevantMemory>());
    public bool Settled { get; set; }
    public bool Consumed { get; set; }
}

// Side-query function: (system, userMessage, ct) → completion text
public delegate Task<string> SideQueryFn(string system, string userMessage, CancellationToken ct);

public static class Memory
{
    private const int MaxMemoryFiles = 200;
    private const int MaxMemoryBytesPerFile = 8192;
    private const int MaxSessionMemoryBytes = 65536;

    public static string GetMemoryDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".claude", "memory");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<MemoryListItem> ListMemories()
    {
        var headers = ScanMemoryHeaders();
        return headers.Select(h => new MemoryListItem
        {
            Name = Path.GetFileNameWithoutExtension(h.Filename),
            Type = h.Type?.ToString().ToLowerInvariant() ?? "",
            Description = h.Description ?? "",
            Path = h.FilePath,
        }).ToList();
    }

    public static List<MemoryHeader> ScanMemoryHeaders()
    {
        var dir = GetMemoryDir();
        var headers = new List<MemoryHeader>();
        string[] files;
        try { files = Directory.GetFiles(dir, "*.md"); } catch { return headers; }

        foreach (var filePath in files)
        {
            var filename = Path.GetFileName(filePath);
            if (filename.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var raw = File.ReadAllText(filePath);
                var fm = Frontmatter.Parse(raw);
                var fi = new FileInfo(filePath);
                MemoryType? type = null;
                if (fm.Meta.TryGetValue("type", out var t))
                {
                    type = t.ToLowerInvariant() switch
                    {
                        "user" => MemoryType.User,
                        "feedback" => MemoryType.Feedback,
                        "project" => MemoryType.Project,
                        "reference" => MemoryType.Reference,
                        _ => null,
                    };
                }
                headers.Add(new MemoryHeader
                {
                    Filename = filename,
                    FilePath = filePath,
                    MtimeMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    Description = fm.Meta.TryGetValue("description", out var d) ? d : null,
                    Type = type,
                });
            }
            catch { }
        }
        headers.Sort((a, b) => b.MtimeMs.CompareTo(a.MtimeMs));
        return headers.Take(MaxMemoryFiles).ToList();
    }

    public static string FormatMemoryManifest(IEnumerable<MemoryHeader> headers)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var h in headers)
        {
            if (!first) sb.AppendLine();
            first = false;
            var tag = h.Type.HasValue ? $"[{h.Type.Value.ToString().ToLowerInvariant()}] " : "";
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(h.MtimeMs).ToString("yyyy-MM-ddTHH:mm:ssZ");
            sb.Append(string.IsNullOrEmpty(h.Description)
                ? $"- {tag}{h.Filename} ({ts})"
                : $"- {tag}{h.Filename} ({ts}): {h.Description}");
        }
        return sb.ToString();
    }

    public static string MemoryAge(long mtimeMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var days = (int)Math.Max(0, (nowMs - mtimeMs) / 86_400_000);
        return days switch { 0 => "today", 1 => "yesterday", _ => $"{days} days ago" };
    }

    public static string MemoryFreshnessWarning(long mtimeMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var days = (int)Math.Max(0, (nowMs - mtimeMs) / 86_400_000);
        if (days <= 1) return "";
        return $"This memory is {days} days old. Memories are point-in-time observations, not live state — claims about code behavior may be outdated. Verify against current code before asserting as fact.";
    }

    private const string SelectMemoriesPrompt = @"You are selecting memories that will be useful to an AI coding assistant as it processes a user's query. You will be given the user's query and a list of available memory files with their filenames and descriptions.

Return a JSON object with a ""selected_memories"" array of filenames for the memories that will clearly be useful (up to 5). Only include memories that you are certain will be helpful based on their name and description.
- If you are unsure if a memory will be useful, do not include it.
- If no memories would clearly be useful, return an empty array.";

    public static async Task<List<RelevantMemory>> SelectRelevantMemoriesAsync(
        string query,
        SideQueryFn sideQuery,
        HashSet<string> alreadySurfaced,
        CancellationToken ct)
    {
        var headers = ScanMemoryHeaders();
        if (headers.Count == 0) return new List<RelevantMemory>();
        var candidates = headers.Where(h => !alreadySurfaced.Contains(h.FilePath)).ToList();
        if (candidates.Count == 0) return new List<RelevantMemory>();

        var manifest = FormatMemoryManifest(candidates);
        try
        {
            var text = await sideQuery(SelectMemoriesPrompt, $"Query: {query}\n\nAvailable memories:\n{manifest}", ct);
            var jsonMatch = Regex.Match(text, @"\{[\s\S]*\}");
            if (!jsonMatch.Success) return new List<RelevantMemory>();
            var parsed = JsonNode.Parse(jsonMatch.Value);
            var selectedNode = parsed?["selected_memories"];
            var selectedFilenames = new HashSet<string>();
            if (selectedNode is JsonArray arr)
                foreach (var n in arr) if (n != null) selectedFilenames.Add(n.ToString());

            var selected = candidates.Where(h => selectedFilenames.Contains(h.Filename)).Take(5).ToList();
            var result = new List<RelevantMemory>();
            foreach (var h in selected)
            {
                var content = File.ReadAllText(h.FilePath);
                if (Encoding.UTF8.GetByteCount(content) > MaxMemoryBytesPerFile)
                {
                    content = content.Substring(0, MaxMemoryBytesPerFile) + "\n\n[... truncated, memory file too large ...]";
                }
                var freshness = MemoryFreshnessWarning(h.MtimeMs);
                var headerText = !string.IsNullOrEmpty(freshness)
                    ? $"{freshness}\n\nMemory: {h.FilePath}:"
                    : $"Memory (saved {MemoryAge(h.MtimeMs)}): {h.FilePath}:";
                result.Add(new RelevantMemory
                {
                    Path = h.FilePath,
                    Content = content,
                    MtimeMs = h.MtimeMs,
                    Header = headerText,
                });
            }
            return result;
        }
        catch
        {
            return new List<RelevantMemory>();
        }
    }

    private static bool IsQuerySubstantial(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return false;
        var cjk = Regex.Matches(trimmed, @"[\u4e00-\u9fff\u3040-\u30ff\uac00-\ud7af]");
        if (cjk.Count >= 2) return true;
        if (Regex.IsMatch(trimmed, @"\s")) return true;
        return false;
    }

    public static MemoryPrefetch? StartMemoryPrefetch(
        string query,
        SideQueryFn sideQuery,
        HashSet<string> alreadySurfaced,
        long sessionMemoryBytes,
        CancellationToken ct)
    {
        if (!IsQuerySubstantial(query)) return null;
        if (sessionMemoryBytes >= MaxSessionMemoryBytes) return null;
        try
        {
            var dir = GetMemoryDir();
            var hasMemories = Directory.GetFiles(dir, "*.md")
                .Any(f => !Path.GetFileName(f).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase));
            if (!hasMemories) return null;
        }
        catch { return null; }

        var handle = new MemoryPrefetch();
        handle.Promise = SelectRelevantMemoriesAsync(query, sideQuery, alreadySurfaced, ct);
        handle.Promise.ContinueWith(_ => { handle.Settled = true; }, TaskScheduler.Default);
        return handle;
    }

    public static string FormatMemoriesForInjection(IEnumerable<RelevantMemory> memories)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var m in memories)
        {
            if (!first) sb.AppendLine().AppendLine();
            first = false;
            sb.Append("<system-reminder>\n").Append(m.Header).Append("\n\n").Append(m.Content).Append("\n</system-reminder>");
        }
        return sb.ToString();
    }

    public static string BuildMemoryPromptSection()
    {
        var memoryDir = GetMemoryDir();
        return $@"# Memory System

You have a persistent, file-based memory system at `{memoryDir}`.

## Memory Types
- **user**: User's role, preferences, knowledge level
- **feedback**: Corrections and guidance from the user
- **project**: Ongoing work, goals, deadlines, decisions
- **reference**: Pointers to external resources

## How to Save Memories
Use the write_file tool to create a memory file with YAML frontmatter:

```markdown
---
name: memory name
description: one-line description
type: user|feedback|project|reference
---
Memory content here.
```

Save to: `{memoryDir}/`
Filename format: `{{type}}_{{slugified_name}}.md`

The MEMORY.md index is auto-updated when you write to the memory directory.

## What NOT to Save
- Code patterns or architecture (read the code instead)
- Git history (use git log)
- Anything already in CLAUDE.md
- Ephemeral task details
";
    }

    public static long ComputeSessionMemoryBytes(IEnumerable<RelevantMemory> memories)
    {
        long total = 0;
        foreach (var m in memories) total += Encoding.UTF8.GetByteCount(m.Content);
        return total;
    }
}
