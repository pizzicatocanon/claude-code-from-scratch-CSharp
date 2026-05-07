// Sub-agent system — built-in (explore/plan/general) + custom .claude/agents/*.md
// Mirrors src/subagent.ts.

using System.Text;

namespace MiniClaude;

public class SubAgentConfig
{
    public string SystemPrompt { get; set; } = "";
    public List<ToolDef> Tools { get; set; } = new();
}

public class CustomAgentDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string>? AllowedTools { get; set; }
    public string SystemPrompt { get; set; } = "";
}

public static class SubAgent
{
    private static readonly HashSet<string> ReadOnlyToolNames = new() { "read_file", "list_files", "grep_search" };

    private const string ExplorePrompt = @"You are a file search specialist for Mini Claude Code. You excel at thoroughly navigating and exploring codebases.

=== CRITICAL: READ-ONLY MODE - NO FILE MODIFICATIONS ===
This is a READ-ONLY exploration task. You are STRICTLY PROHIBITED from:
- Creating new files (no write_file, touch, or file creation of any kind)
- Modifying existing files (no edit_file operations)
- Deleting files (no rm or deletion)
- Running ANY commands that change system state

Your role is EXCLUSIVELY to search and analyze existing code.

Guidelines:
- Use list_files for broad file pattern matching
- Use grep_search for searching file contents with regex
- Use read_file when you know the specific file path you need to read
- Adapt your search approach based on the thoroughness level specified by the caller

NOTE: You are meant to be a fast agent that returns output as quickly as possible.";

    private const string PlanPrompt = @"You are a Plan agent — a READ-ONLY sub-agent specialized for designing implementation plans.

IMPORTANT CONSTRAINTS:
- You are READ-ONLY. You only have access to read_file, list_files, and grep_search.
- Do NOT attempt to modify any files.

Your job:
- Analyze the codebase to understand the current architecture
- Design a step-by-step implementation plan
- Identify critical files that need modification
- Consider architectural trade-offs

Return a structured plan with:
1. Summary of current state
2. Step-by-step implementation steps
3. Critical files for implementation
4. Potential risks or considerations";

    private const string GeneralPrompt = @"You are an agent for Mini Claude Code. Given the user's message, you should use the tools available to complete the task. Complete the task fully—don't gold-plate, but don't leave it half-done. When you complete the task, respond with a concise report covering what was done and any key findings.

Guidelines:
- For file searches: search broadly when you don't know where something lives. Use read_file when you know the specific file path.
- For analysis: Start broad and narrow down. Use multiple search strategies if the first doesn't yield results.
- Be thorough: Check multiple locations, consider different naming conventions, look for related files.
- NEVER create files unless absolutely necessary. ALWAYS prefer editing an existing file to creating a new one.";

    private static List<ToolDef> GetReadOnlyTools()
        => Tools.ToolDefinitions.Where(t => ReadOnlyToolNames.Contains(t.Name)).ToList();

    private static Dictionary<string, CustomAgentDef>? _cachedCustomAgents;

    public static void ResetCache() => _cachedCustomAgents = null;

    private static Dictionary<string, CustomAgentDef> DiscoverCustomAgents()
    {
        if (_cachedCustomAgents != null) return _cachedCustomAgents;
        var agents = new Dictionary<string, CustomAgentDef>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LoadAgentsFromDir(Path.Combine(home, ".claude", "agents"), agents);
        LoadAgentsFromDir(Path.Combine(Directory.GetCurrentDirectory(), ".claude", "agents"), agents);
        _cachedCustomAgents = agents;
        return agents;
    }

    private static void LoadAgentsFromDir(string dir, Dictionary<string, CustomAgentDef> agents)
    {
        if (!Directory.Exists(dir)) return;
        string[] entries;
        try { entries = Directory.GetFiles(dir, "*.md"); } catch { return; }
        foreach (var filePath in entries)
        {
            try
            {
                var raw = File.ReadAllText(filePath);
                var fm = Frontmatter.Parse(raw);
                var meta = fm.Meta;
                var name = meta.TryGetValue("name", out var n) ? n : Path.GetFileNameWithoutExtension(filePath);
                List<string>? allowedTools = null;
                if (meta.TryGetValue("allowed-tools", out var at))
                    allowedTools = at.Split(',').Select(s => s.Trim()).ToList();
                agents[name] = new CustomAgentDef
                {
                    Name = name,
                    Description = meta.TryGetValue("description", out var d) ? d : "",
                    AllowedTools = allowedTools,
                    SystemPrompt = fm.Body,
                };
            }
            catch { }
        }
    }

    public static SubAgentConfig GetSubAgentConfig(string type)
    {
        var custom = DiscoverCustomAgents();
        if (custom.TryGetValue(type, out var def))
        {
            var tools = def.AllowedTools != null
                ? Tools.ToolDefinitions.Where(t => def.AllowedTools!.Contains(t.Name)).ToList()
                : Tools.ToolDefinitions.Where(t => t.Name != "agent").ToList();
            return new SubAgentConfig { SystemPrompt = def.SystemPrompt, Tools = tools };
        }

        return type switch
        {
            "explore" => new SubAgentConfig { SystemPrompt = ExplorePrompt, Tools = GetReadOnlyTools() },
            "plan" => new SubAgentConfig { SystemPrompt = PlanPrompt, Tools = GetReadOnlyTools() },
            _ => new SubAgentConfig
            {
                SystemPrompt = GeneralPrompt,
                Tools = Tools.ToolDefinitions.Where(t => t.Name != "agent").ToList(),
            },
        };
    }

    public static List<(string Name, string Description)> GetAvailableAgentTypes()
    {
        var types = new List<(string, string)>
        {
            ("explore", "Fast, read-only codebase search and exploration"),
            ("plan", "Read-only analysis with structured implementation plans"),
            ("general", "Full tools for independent tasks"),
        };
        foreach (var kv in DiscoverCustomAgents())
            types.Add((kv.Key, kv.Value.Description));
        return types;
    }

    public static string BuildAgentDescriptions()
    {
        var types = GetAvailableAgentTypes();
        if (types.Count <= 3) return "";
        var custom = types.Skip(3).ToList();
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# Custom Agent Types");
        sb.AppendLine();
        foreach (var t in custom)
            sb.AppendLine($"- **{t.Name}**: {t.Description}");
        return sb.ToString().TrimEnd();
    }
}
