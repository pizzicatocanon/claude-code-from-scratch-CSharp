// Tool definitions, permission system, and tool execution.
// Mirrors src/tools.ts (858 lines).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace MiniClaude;

// ─── Permission modes ──────────────────────────────────────
public enum PermissionMode
{
    Default,
    Plan,
    AcceptEdits,
    BypassPermissions,
    DontAsk,
}

public static class PermissionModeExt
{
    public static PermissionMode Parse(string s) => s switch
    {
        "plan" => PermissionMode.Plan,
        "acceptEdits" => PermissionMode.AcceptEdits,
        "bypassPermissions" => PermissionMode.BypassPermissions,
        "dontAsk" => PermissionMode.DontAsk,
        _ => PermissionMode.Default,
    };

    public static string ToWire(this PermissionMode m) => m switch
    {
        PermissionMode.Plan => "plan",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.BypassPermissions => "bypassPermissions",
        PermissionMode.DontAsk => "dontAsk",
        _ => "default",
    };
}

// ─── Tool definition (mirrors Anthropic.Tool + deferred flag) ──
public class ToolDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonObject InputSchema { get; set; } = new();
    public bool Deferred { get; set; } = false;
}

public class PermissionResult
{
    public string Action { get; set; } = "allow"; // "allow" | "deny" | "confirm"
    public string? Message { get; set; }
}

public static class Tools
{
    public static readonly bool IsWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static readonly HashSet<string> ReadTools = new() { "read_file", "list_files", "grep_search", "web_fetch" };
    public static readonly HashSet<string> EditTools = new() { "write_file", "edit_file" };
    public static readonly HashSet<string> ConcurrencySafeTools = new() { "read_file", "list_files", "grep_search", "web_fetch" };

    public static readonly List<ToolDef> ToolDefinitions = new()
    {
        new ToolDef
        {
            Name = "read_file",
            Description = "Read the contents of a file. Returns the file content with line numbers.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file_path": { "type": "string", "description": "The path to the file to read" }
              },
              "required": ["file_path"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "write_file",
            Description = "Write content to a file. Creates the file if it doesn't exist, overwrites if it does.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file_path": { "type": "string", "description": "The path to the file to write" },
                "content": { "type": "string", "description": "The content to write to the file" }
              },
              "required": ["file_path", "content"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "edit_file",
            Description = "Edit a file by replacing an exact string match with new content. The old_string must match exactly (including whitespace and indentation).",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file_path": { "type": "string", "description": "The path to the file to edit" },
                "old_string": { "type": "string", "description": "The exact string to find and replace" },
                "new_string": { "type": "string", "description": "The string to replace it with" }
              },
              "required": ["file_path", "old_string", "new_string"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "list_files",
            Description = "List files matching a glob pattern. Returns matching file paths.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "pattern": { "type": "string", "description": "Glob pattern to match files (e.g., \"**/*.ts\", \"src/**/*\")" },
                "path": { "type": "string", "description": "Base directory to search from. Defaults to current directory." }
              },
              "required": ["pattern"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "grep_search",
            Description = "Search for a pattern in files. Returns matching lines with file paths and line numbers.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "pattern": { "type": "string", "description": "The regex pattern to search for" },
                "path": { "type": "string", "description": "Directory or file to search in. Defaults to current directory." },
                "include": { "type": "string", "description": "File glob pattern to include (e.g., \"*.cs\", \"*.py\")" }
              },
              "required": ["pattern"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "run_shell",
            Description = "Execute a shell command and return its output. Use this for running tests, installing packages, git operations, etc.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "The shell command to execute" },
                "timeout": { "type": "number", "description": "Timeout in milliseconds (default: 30000)" }
              },
              "required": ["command"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "skill",
            Description = "Invoke a registered skill by name. Skills are prompt templates loaded from .claude/skills/.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "skill_name": { "type": "string", "description": "The name of the skill to invoke" },
                "args": { "type": "string", "description": "Optional arguments to pass to the skill" }
              },
              "required": ["skill_name"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "web_fetch",
            Description = "Fetch a URL and return its content as text. For HTML pages, tags are stripped to return readable text.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "url": { "type": "string", "description": "The URL to fetch" },
                "max_length": { "type": "number", "description": "Maximum content length in characters (default 50000)" }
              },
              "required": ["url"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "enter_plan_mode",
            Description = "Enter plan mode to switch to a read-only planning phase.",
            InputSchema = JsonNode.Parse("""{ "type": "object", "properties": {} }""")!.AsObject(),
            Deferred = true,
        },
        new ToolDef
        {
            Name = "exit_plan_mode",
            Description = "Exit plan mode after you have finished writing your plan to the plan file.",
            InputSchema = JsonNode.Parse("""{ "type": "object", "properties": {} }""")!.AsObject(),
            Deferred = true,
        },
        new ToolDef
        {
            Name = "agent",
            Description = "Launch a sub-agent. Types: 'explore' (read-only fast search), 'plan' (read-only planning), 'general' (full tools).",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "description": { "type": "string", "description": "Short description of the sub-agent's task" },
                "prompt": { "type": "string", "description": "Detailed task instructions for the sub-agent" },
                "type": { "type": "string", "enum": ["explore", "plan", "general"], "description": "Agent type" }
              },
              "required": ["description", "prompt"]
            }
            """)!.AsObject(),
        },
        new ToolDef
        {
            Name = "tool_search",
            Description = "Search for available tools by name or keyword. Returns full schema for matching deferred tools.",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Tool name or search keywords" }
              },
              "required": ["query"]
            }
            """)!.AsObject(),
        },
    };

    // ─── Deferred tool activation ────────────────────────────
    private static readonly HashSet<string> ActivatedTools = new();

    public static void ResetActivatedTools() => ActivatedTools.Clear();

    public static List<ToolDef> GetActiveToolDefinitions(IEnumerable<ToolDef>? allTools = null)
    {
        var tools = allTools ?? ToolDefinitions;
        return tools.Where(t => !t.Deferred || ActivatedTools.Contains(t.Name)).ToList();
    }

    public static List<string> GetDeferredToolNames(IEnumerable<ToolDef>? allTools = null)
    {
        var tools = allTools ?? ToolDefinitions;
        return tools.Where(t => t.Deferred && !ActivatedTools.Contains(t.Name)).Select(t => t.Name).ToList();
    }

    // ─── Tool execution ──────────────────────────────────────

    private const int MaxResultChars = 50000;

    private static string TruncateResult(string s)
    {
        if (s.Length <= MaxResultChars) return s;
        var keepEach = (MaxResultChars - 60) / 2;
        return s.Substring(0, keepEach) +
            $"\n\n[... truncated {s.Length - keepEach * 2} chars ...]\n\n" +
            s.Substring(s.Length - keepEach);
    }

    public static async Task<string> ExecuteAsync(
        string name,
        Dictionary<string, object?> input,
        Dictionary<string, long>? readFileState = null,
        CancellationToken cancellationToken = default)
    {
        string result;
        switch (name)
        {
            case "read_file":
                result = ReadFile(GetString(input, "file_path"));
                if (readFileState != null && !result.StartsWith("Error"))
                {
                    var abs = Path.GetFullPath(GetString(input, "file_path"));
                    try { readFileState[abs] = new FileInfo(abs).LastWriteTime.Ticks; } catch { }
                }
                break;

            case "write_file":
                {
                    var fp = GetString(input, "file_path");
                    var abs = Path.GetFullPath(fp);
                    if (readFileState != null && File.Exists(abs))
                    {
                        if (!readFileState.ContainsKey(abs))
                            return "Error: You must read this file before writing. Use read_file first to see its current contents.";
                        var cur = new FileInfo(abs).LastWriteTime.Ticks;
                        if (cur != readFileState[abs])
                            return $"Warning: {fp} was modified externally since your last read. Please read_file again before writing.";
                    }
                    result = WriteFile(fp, GetString(input, "content"));
                    if (readFileState != null && !result.StartsWith("Error"))
                    {
                        try { readFileState[abs] = new FileInfo(abs).LastWriteTime.Ticks; } catch { }
                    }
                    break;
                }

            case "edit_file":
                {
                    var fp = GetString(input, "file_path");
                    var abs = Path.GetFullPath(fp);
                    if (readFileState != null && File.Exists(abs))
                    {
                        if (!readFileState.ContainsKey(abs))
                            return "Error: You must read this file before editing. Use read_file first to see its current contents.";
                        var cur = new FileInfo(abs).LastWriteTime.Ticks;
                        if (cur != readFileState[abs])
                            return $"Warning: {fp} was modified externally since your last read. Please read_file again before editing.";
                    }
                    result = EditFile(fp, GetString(input, "old_string"), GetString(input, "new_string"));
                    if (readFileState != null && File.Exists(abs) && !result.StartsWith("Error"))
                    {
                        try { readFileState[abs] = new FileInfo(abs).LastWriteTime.Ticks; } catch { }
                    }
                    break;
                }

            case "list_files":
                result = ListFiles(GetString(input, "pattern"), GetStringOrNull(input, "path"));
                break;

            case "grep_search":
                result = GrepSearch(
                    GetString(input, "pattern"),
                    GetStringOrNull(input, "path"),
                    GetStringOrNull(input, "include"));
                break;

            case "run_shell":
                {
                    int timeout = 30000;
                    if (input.TryGetValue("timeout", out var t) && t != null)
                    {
                        if (int.TryParse(t.ToString(), out var ti)) timeout = ti;
                    }
                    result = RunShell(GetString(input, "command"), timeout);
                    break;
                }

            case "web_fetch":
                {
                    int maxLen = 50000;
                    if (input.TryGetValue("max_length", out var ml) && ml != null)
                    {
                        if (int.TryParse(ml.ToString(), out var mli)) maxLen = mli;
                    }
                    result = await WebFetchAsync(GetString(input, "url"), maxLen, cancellationToken);
                    break;
                }

            case "tool_search":
                {
                    var query = GetString(input, "query").ToLowerInvariant();
                    var deferred = ToolDefinitions.Where(td => td.Deferred).ToList();
                    var matches = deferred
                        .Where(td => td.Name.ToLowerInvariant().Contains(query) ||
                                     (td.Description ?? "").ToLowerInvariant().Contains(query))
                        .ToList();
                    if (matches.Count == 0) return "No matching deferred tools found.";
                    foreach (var m in matches) ActivatedTools.Add(m.Name);
                    var arr = new JsonArray();
                    foreach (var m in matches)
                    {
                        arr.Add(new JsonObject
                        {
                            ["name"] = m.Name,
                            ["description"] = m.Description,
                            ["input_schema"] = JsonNode.Parse(m.InputSchema.ToJsonString()),
                        });
                    }
                    return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                }

            // "skill" and "agent" handled in Agent.cs
            default:
                return $"Unknown tool: {name}";
        }

        return TruncateResult(result);
    }

    // ─── Individual tool implementations ─────────────────────

    private static string ReadFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var lines = content.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append((i + 1).ToString().PadLeft(4)).Append(" | ").Append(lines[i]);
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"Error reading file: {e.Message}";
        }
    }

    private static string WriteFile(string filePath, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, content);
            AutoUpdateMemoryIndex(filePath);

            var lines = content.Split('\n');
            var lineCount = lines.Length;
            var preview = new StringBuilder();
            for (int i = 0; i < Math.Min(30, lineCount); i++)
            {
                if (i > 0) preview.Append('\n');
                preview.Append((i + 1).ToString().PadLeft(4)).Append(" | ").Append(lines[i]);
            }
            var truncNote = lineCount > 30 ? $"\n  ... ({lineCount} lines total)" : "";
            return $"Successfully wrote to {filePath} ({lineCount} lines)\n\n{preview}{truncNote}";
        }
        catch (Exception e)
        {
            return $"Error writing file: {e.Message}";
        }
    }

    private static void AutoUpdateMemoryIndex(string filePath)
    {
        try
        {
            var memDir = Memory.GetMemoryDir();
            var abs = Path.GetFullPath(filePath);
            if (abs.StartsWith(memDir) && abs.EndsWith(".md") && !abs.EndsWith("MEMORY.md"))
            {
                var files = Directory.GetFiles(memDir, "*.md")
                    .Where(f => !Path.GetFileName(f).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var sb = new StringBuilder();
                sb.AppendLine("# Memory Index");
                sb.AppendLine();
                foreach (var f in files)
                {
                    try
                    {
                        var raw = File.ReadAllText(f);
                        var nameMatch = Regex.Match(raw, @"^name:\s*(.+)$", RegexOptions.Multiline);
                        var typeMatch = Regex.Match(raw, @"^type:\s*(.+)$", RegexOptions.Multiline);
                        var descMatch = Regex.Match(raw, @"^description:\s*(.+)$", RegexOptions.Multiline);
                        if (nameMatch.Success && typeMatch.Success)
                        {
                            sb.AppendLine($"- **[{nameMatch.Groups[1].Value.Trim()}]({Path.GetFileName(f)})** ({typeMatch.Groups[1].Value.Trim()}) — {(descMatch.Success ? descMatch.Groups[1].Value.Trim() : "")}");
                        }
                    }
                    catch { }
                }
                File.WriteAllText(Path.Combine(memDir, "MEMORY.md"), sb.ToString());
            }
        }
        catch { }
    }

    // ─── Edit helpers: quote normalization + diff ────────────

    private static string NormalizeQuotes(string s)
    {
        return s
            .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u2032', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u2033', '"');
    }

    private static string? FindActualString(string fileContent, string searchString)
    {
        if (fileContent.Contains(searchString)) return searchString;
        var normSearch = NormalizeQuotes(searchString);
        var normFile = NormalizeQuotes(fileContent);
        var idx = normFile.IndexOf(normSearch, StringComparison.Ordinal);
        if (idx != -1) return fileContent.Substring(idx, searchString.Length);
        return null;
    }

    private static string GenerateDiff(string oldContent, string oldString, string newString)
    {
        var beforeChange = oldContent.Substring(0, oldContent.IndexOf(oldString, StringComparison.Ordinal));
        var lineNum = beforeChange.Count(c => c == '\n') + 1;
        var oldLines = oldString.Split('\n');
        var newLines = newString.Split('\n');

        var sb = new StringBuilder();
        sb.AppendLine($"@@ -{lineNum},{oldLines.Length} +{lineNum},{newLines.Length} @@");
        foreach (var l in oldLines) sb.AppendLine($"- {l}");
        foreach (var l in newLines) sb.Append($"+ {l}").AppendLine();
        return sb.ToString().TrimEnd();
    }

    private static string EditFile(string filePath, string oldString, string newString)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var actual = FindActualString(content, oldString);
            if (actual == null) return $"Error: old_string not found in {filePath}";

            int count = 0;
            int idx = 0;
            while ((idx = content.IndexOf(actual, idx, StringComparison.Ordinal)) != -1)
            {
                count++;
                idx += actual.Length;
            }
            if (count > 1) return $"Error: old_string found {count} times in {filePath}. Must be unique.";

            // Use Replace via split/join semantics — string.Replace is fine in C# (no $ specials)
            var newContent = content.Replace(actual, newString);
            File.WriteAllText(filePath, newContent);

            var diff = GenerateDiff(content, actual, newString);
            var quoteNote = actual != oldString ? " (matched via quote normalization)" : "";
            return $"Successfully edited {filePath}{quoteNote}\n\n{diff}";
        }
        catch (Exception e)
        {
            return $"Error editing file: {e.Message}";
        }
    }

    private static string ListFiles(string pattern, string? basePath)
    {
        try
        {
            var root = string.IsNullOrEmpty(basePath) ? Directory.GetCurrentDirectory() : basePath;
            var matcher = new Matcher();
            matcher.AddInclude(pattern);
            matcher.AddExclude("**/node_modules/**");
            matcher.AddExclude("**/.git/**");
            matcher.AddExclude("**/bin/**");
            matcher.AddExclude("**/obj/**");

            var dirInfo = new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(root));
            var result = matcher.Execute(dirInfo);
            if (!result.HasMatches) return "No files found matching the pattern.";
            var files = result.Files.Select(f => f.Path).ToList();
            var shown = files.Take(200).ToList();
            var sb = new StringBuilder();
            sb.Append(string.Join("\n", shown));
            if (files.Count > 200) sb.Append($"\n... and {files.Count - 200} more");
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"Error listing files: {e.Message}";
        }
    }

    private static string GrepSearch(string pattern, string? path, string? include)
    {
        var dir = string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path;
        // Use pure C# walker (cross-platform, no system grep dependency)
        try
        {
            var re = new Regex(pattern);
            Regex? includeRe = null;
            if (!string.IsNullOrEmpty(include))
            {
                var rx = "^" + Regex.Escape(include).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                includeRe = new Regex(rx);
            }

            var matches = new List<string>();
            void Walk(string d)
            {
                if (matches.Count >= 200) return;
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(d); } catch { return; }
                foreach (var full in entries)
                {
                    var name = Path.GetFileName(full);
                    if (name.StartsWith(".") || name == "node_modules" || name == "bin" || name == "obj") continue;
                    bool isDir;
                    try { isDir = Directory.Exists(full); } catch { continue; }
                    if (isDir) { Walk(full); continue; }
                    if (includeRe != null && !includeRe.IsMatch(name)) continue;
                    try
                    {
                        var text = File.ReadAllText(full);
                        var lines = text.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (re.IsMatch(lines[i]))
                            {
                                matches.Add($"{full}:{i + 1}:{lines[i]}");
                                if (matches.Count >= 200) return;
                            }
                        }
                    }
                    catch { }
                }
            }
            Walk(dir);
            if (matches.Count == 0) return "No matches found.";
            var shown = matches.Take(100).ToList();
            var sb = new StringBuilder();
            sb.Append(string.Join("\n", shown));
            if (matches.Count > 100) sb.Append($"\n... and {matches.Count - 100} more matches");
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"Error: {e.Message}";
        }
    }

    private static string RunShell(string command, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (IsWin)
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = $"-NoProfile -Command \"{command.Replace("\"", "`\"")}\"";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
            }

            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return "Command failed (timeout exceeded)";
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                var sb = new StringBuilder();
                sb.Append($"Command failed (exit code {process.ExitCode})");
                if (!string.IsNullOrEmpty(stdout)) sb.Append($"\nStdout: {stdout}");
                if (!string.IsNullOrEmpty(stderr)) sb.Append($"\nStderr: {stderr}");
                return sb.ToString();
            }
            return string.IsNullOrEmpty(stdout) ? "(no output)" : stdout;
        }
        catch (Exception e)
        {
            return $"Command failed: {e.Message}";
        }
    }

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static Tools()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("mini-claude/1.0");
    }

    private static async Task<string> WebFetchAsync(string url, int maxLength, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var res = await HttpClient.GetAsync(url, cts.Token);
            if (!res.IsSuccessStatusCode)
                return $"HTTP error: {(int)res.StatusCode} {res.ReasonPhrase}";
            var contentType = res.Content.Headers.ContentType?.MediaType ?? "";
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            if (contentType.Contains("html"))
            {
                text = Regex.Replace(text, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"<[^>]*>", " ");
                text = text.Replace("&nbsp;", " ").Replace("&amp;", "&")
                    .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
                text = Regex.Replace(text, @"\s{2,}", " ");
                text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
            }
            if (text.Length > maxLength)
                text = text.Substring(0, maxLength) + $"\n\n[... truncated at {maxLength} characters]";
            return string.IsNullOrEmpty(text) ? "(empty response)" : text;
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out (30s)";
        }
        catch (Exception e)
        {
            return $"Error fetching {url}: {e.Message}";
        }
    }

    // ─── Dangerous command patterns ──────────────────────────

    private static readonly Regex[] DangerousPatterns =
    {
        new(@"\brm\s"),
        new(@"\bgit\s+(push|reset|clean|checkout\s+\.)"),
        new(@"\bsudo\b"),
        new(@"\bmkfs\b"),
        new(@"\bdd\s"),
        new(@">\s*/dev/"),
        new(@"\bkill\b"),
        new(@"\bpkill\b"),
        new(@"\breboot\b"),
        new(@"\bshutdown\b"),
        new(@"\bdel\s", RegexOptions.IgnoreCase),
        new(@"\brmdir\s", RegexOptions.IgnoreCase),
        new(@"\bformat\s", RegexOptions.IgnoreCase),
        new(@"\btaskkill\s", RegexOptions.IgnoreCase),
        new(@"\bRemove-Item\s", RegexOptions.IgnoreCase),
        new(@"\bStop-Process\s", RegexOptions.IgnoreCase),
    };

    public static bool IsDangerous(string command) => DangerousPatterns.Any(p => p.IsMatch(command));

    // ─── Permission rules (.claude/settings.json) ────────────

    private record ParsedRule(string Tool, string? Pattern);

    private class PermissionRules
    {
        public List<ParsedRule> Allow { get; set; } = new();
        public List<ParsedRule> Deny { get; set; } = new();
    }

    private static PermissionRules? _cachedRules;

    public static void ResetPermissionCache() => _cachedRules = null;

    private static ParsedRule ParseRule(string rule)
    {
        var m = Regex.Match(rule, @"^([a-z_]+)\((.+)\)$");
        if (m.Success) return new ParsedRule(m.Groups[1].Value, m.Groups[2].Value);
        return new ParsedRule(rule, null);
    }

    private static JsonNode? LoadSettings(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static PermissionRules LoadPermissionRules()
    {
        if (_cachedRules != null) return _cachedRules;
        var rules = new PermissionRules();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sources = new[]
        {
            LoadSettings(Path.Combine(home, ".claude", "settings.json")),
            LoadSettings(Path.Combine(Directory.GetCurrentDirectory(), ".claude", "settings.json")),
        };
        foreach (var settings in sources)
        {
            var perms = settings?["permissions"];
            if (perms == null) continue;
            if (perms["allow"] is JsonArray allowArr)
                foreach (var n in allowArr) if (n != null) rules.Allow.Add(ParseRule(n.ToString()));
            if (perms["deny"] is JsonArray denyArr)
                foreach (var n in denyArr) if (n != null) rules.Deny.Add(ParseRule(n.ToString()));
        }
        _cachedRules = rules;
        return rules;
    }

    private static bool MatchesRule(ParsedRule rule, string toolName, IReadOnlyDictionary<string, object?> input)
    {
        if (rule.Tool != toolName) return false;
        if (rule.Pattern == null) return true;
        string value = "";
        if (toolName == "run_shell") value = GetString(input, "command");
        else if (input.TryGetValue("file_path", out var fp)) value = fp?.ToString() ?? "";
        else return true;

        if (rule.Pattern.EndsWith("*"))
            return value.StartsWith(rule.Pattern.Substring(0, rule.Pattern.Length - 1));
        return value == rule.Pattern;
    }

    private static string? CheckPermissionRules(string toolName, IReadOnlyDictionary<string, object?> input)
    {
        var rules = LoadPermissionRules();
        foreach (var r in rules.Deny) if (MatchesRule(r, toolName, input)) return "deny";
        foreach (var r in rules.Allow) if (MatchesRule(r, toolName, input)) return "allow";
        return null;
    }

    // ─── Unified permission check ────────────────────────────

    public static PermissionResult CheckPermission(
        string toolName,
        IReadOnlyDictionary<string, object?> input,
        PermissionMode mode = PermissionMode.Default,
        string? planFilePath = null)
    {
        if (mode == PermissionMode.BypassPermissions) return new PermissionResult { Action = "allow" };

        var ruleResult = CheckPermissionRules(toolName, input);
        if (ruleResult == "deny") return new PermissionResult { Action = "deny", Message = $"Denied by permission rule for {toolName}" };
        if (ruleResult == "allow") return new PermissionResult { Action = "allow" };

        if (ReadTools.Contains(toolName)) return new PermissionResult { Action = "allow" };

        if (mode == PermissionMode.Plan)
        {
            if (EditTools.Contains(toolName))
            {
                var fp = GetStringOrNull(input, "file_path") ?? GetStringOrNull(input, "path");
                if (planFilePath != null && fp == planFilePath) return new PermissionResult { Action = "allow" };
                return new PermissionResult { Action = "deny", Message = $"Blocked in plan mode: {toolName}" };
            }
            if (toolName == "run_shell") return new PermissionResult { Action = "deny", Message = "Shell commands blocked in plan mode" };
        }

        if (toolName == "enter_plan_mode" || toolName == "exit_plan_mode")
            return new PermissionResult { Action = "allow" };

        if (mode == PermissionMode.AcceptEdits && EditTools.Contains(toolName))
            return new PermissionResult { Action = "allow" };

        // Built-in dangerous pattern checks
        bool needsConfirm = false;
        string confirmMessage = "";
        if (toolName == "run_shell" && IsDangerous(GetString(input, "command")))
        {
            needsConfirm = true;
            confirmMessage = GetString(input, "command");
        }
        else if (toolName == "write_file" && !File.Exists(GetString(input, "file_path")))
        {
            needsConfirm = true;
            confirmMessage = $"write new file: {GetString(input, "file_path")}";
        }
        else if (toolName == "edit_file" && !File.Exists(GetString(input, "file_path")))
        {
            needsConfirm = true;
            confirmMessage = $"edit non-existent file: {GetString(input, "file_path")}";
        }

        if (needsConfirm)
        {
            if (mode == PermissionMode.DontAsk)
                return new PermissionResult { Action = "deny", Message = $"Auto-denied (dontAsk mode): {confirmMessage}" };
            return new PermissionResult { Action = "confirm", Message = confirmMessage };
        }

        return new PermissionResult { Action = "allow" };
    }

    // ─── Helpers ─────────────────────────────────────────────

    public static string GetString(IReadOnlyDictionary<string, object?> input, string key)
    {
        if (input.TryGetValue(key, out var v) && v != null) return v.ToString() ?? "";
        return "";
    }

    public static string? GetStringOrNull(IReadOnlyDictionary<string, object?> input, string key)
    {
        if (input.TryGetValue(key, out var v) && v != null) return v.ToString();
        return null;
    }
}
