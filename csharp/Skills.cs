// Skills system — discover, parse, and execute .claude/skills/<name>/SKILL.md
// Mirrors src/skills.ts.

using System.Text;
using System.Text.Json.Nodes;

namespace MiniClaude;

public class SkillDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? WhenToUse { get; set; }
    public List<string>? AllowedTools { get; set; }
    public bool UserInvocable { get; set; } = true;
    public string Context { get; set; } = "inline"; // "inline" | "fork"
    public string PromptTemplate { get; set; } = "";
    public string Source { get; set; } = "project"; // "project" | "user"
    public string SkillDir { get; set; } = "";
}

public class ExecutedSkill
{
    public string Prompt { get; set; } = "";
    public List<string>? AllowedTools { get; set; }
    public string Context { get; set; } = "inline";
}

public static class Skills
{
    private static List<SkillDefinition>? _cached;

    public static void ResetCache() => _cached = null;

    public static List<SkillDefinition> DiscoverSkills()
    {
        if (_cached != null) return _cached;
        var skills = new Dictionary<string, SkillDefinition>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LoadSkillsFromDir(Path.Combine(home, ".claude", "skills"), "user", skills);
        LoadSkillsFromDir(Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills"), "project", skills);

        _cached = skills.Values.ToList();
        return _cached;
    }

    private static void LoadSkillsFromDir(string baseDir, string source, Dictionary<string, SkillDefinition> skills)
    {
        if (!Directory.Exists(baseDir)) return;
        string[] entries;
        try { entries = Directory.GetDirectories(baseDir); } catch { return; }
        foreach (var skillDir in entries)
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            var skill = ParseSkillFile(skillFile, source, skillDir);
            if (skill != null) skills[skill.Name] = skill;
        }
    }

    private static SkillDefinition? ParseSkillFile(string filePath, string source, string skillDir)
    {
        try
        {
            var raw = File.ReadAllText(filePath);
            var fm = Frontmatter.Parse(raw);
            var meta = fm.Meta;

            var name = meta.TryGetValue("name", out var n) ? n : (Path.GetFileName(skillDir) ?? "unknown");
            var userInvocable = !(meta.TryGetValue("user-invocable", out var ui) && ui == "false");
            var context = (meta.TryGetValue("context", out var ct) && ct == "fork") ? "fork" : "inline";

            List<string>? allowedTools = null;
            if (meta.TryGetValue("allowed-tools", out var at))
            {
                if (at.StartsWith("["))
                {
                    try
                    {
                        var arr = JsonNode.Parse(at) as JsonArray;
                        if (arr != null)
                            allowedTools = arr.Where(x => x != null).Select(x => x!.ToString()).ToList();
                    }
                    catch
                    {
                        allowedTools = at.Replace("[", "").Replace("]", "").Split(',').Select(s => s.Trim()).ToList();
                    }
                }
                else
                {
                    allowedTools = at.Split(',').Select(s => s.Trim()).ToList();
                }
            }

            return new SkillDefinition
            {
                Name = name,
                Description = meta.TryGetValue("description", out var d) ? d : "",
                WhenToUse = meta.TryGetValue("when_to_use", out var wt1) ? wt1 :
                            meta.TryGetValue("when-to-use", out var wt2) ? wt2 : null,
                AllowedTools = allowedTools,
                UserInvocable = userInvocable,
                Context = context,
                PromptTemplate = fm.Body,
                Source = source,
                SkillDir = skillDir,
            };
        }
        catch { return null; }
    }

    public static SkillDefinition? GetSkillByName(string name)
        => DiscoverSkills().FirstOrDefault(s => s.Name == name);

    public static string ResolveSkillPrompt(SkillDefinition skill, string args)
    {
        var prompt = skill.PromptTemplate
            .Replace("$ARGUMENTS", args)
            .Replace("${ARGUMENTS}", args)
            .Replace("${CLAUDE_SKILL_DIR}", skill.SkillDir);
        return prompt;
    }

    public static ExecutedSkill? ExecuteSkill(string skillName, string args)
    {
        var skill = GetSkillByName(skillName);
        if (skill == null) return null;
        return new ExecutedSkill
        {
            Prompt = ResolveSkillPrompt(skill, args),
            AllowedTools = skill.AllowedTools,
            Context = skill.Context,
        };
    }

    public static string BuildSkillDescriptions()
    {
        var skills = DiscoverSkills();
        if (skills.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("# Available Skills");
        sb.AppendLine();

        var invocable = skills.Where(s => s.UserInvocable).ToList();
        var autoOnly = skills.Where(s => !s.UserInvocable).ToList();

        if (invocable.Count > 0)
        {
            sb.AppendLine("User-invocable skills (user types /<name> to invoke):");
            foreach (var s in invocable)
            {
                sb.AppendLine($"- **/{s.Name}**: {s.Description}");
                if (!string.IsNullOrEmpty(s.WhenToUse)) sb.AppendLine($"  When to use: {s.WhenToUse}");
            }
            sb.AppendLine();
        }

        if (autoOnly.Count > 0)
        {
            sb.AppendLine("Auto-invocable skills (use the skill tool when appropriate):");
            foreach (var s in autoOnly)
            {
                sb.AppendLine($"- **{s.Name}**: {s.Description}");
                if (!string.IsNullOrEmpty(s.WhenToUse)) sb.AppendLine($"  When to use: {s.WhenToUse}");
            }
            sb.AppendLine();
        }

        sb.Append("To invoke a skill programmatically, use the `skill` tool with the skill name and optional arguments.");
        return sb.ToString();
    }
}
