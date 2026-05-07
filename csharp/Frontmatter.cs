// Shared YAML frontmatter parser for memory and skills files.
// Handles simple `key: value` pairs between `---` delimiters.

namespace MiniClaude;

public static class Frontmatter
{
    public record Result(Dictionary<string, string> Meta, string Body);

    public static Result Parse(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return new Result(new Dictionary<string, string>(), content);

        int endIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { endIdx = i; break; }
        }
        if (endIdx == -1)
            return new Result(new Dictionary<string, string>(), content);

        var meta = new Dictionary<string, string>();
        for (int i = 1; i < endIdx; i++)
        {
            int colonIdx = lines[i].IndexOf(':');
            if (colonIdx == -1) continue;
            var key = lines[i].Substring(0, colonIdx).Trim();
            var value = lines[i].Substring(colonIdx + 1).Trim();
            if (!string.IsNullOrEmpty(key)) meta[key] = value;
        }

        var body = string.Join("\n", lines.Skip(endIdx + 1)).Trim();
        return new Result(meta, body);
    }

    public static string Format(Dictionary<string, string> meta, string body)
    {
        var lines = new List<string> { "---" };
        foreach (var kv in meta)
            lines.Add($"{kv.Key}: {kv.Value}");
        lines.Add("---");
        lines.Add("");
        lines.Add(body);
        return string.Join("\n", lines);
    }
}
