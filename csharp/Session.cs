// Session persistence — save/load/list to ~/.mini-claude/sessions/<id>.json
// Mirrors src/session.ts.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniClaude;

public class SessionMetadata
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string StartTime { get; set; } = "";
    public int MessageCount { get; set; }
}

public class SessionData
{
    public SessionMetadata Metadata { get; set; } = new();
    public JsonNode? AnthropicMessages { get; set; }
    public JsonNode? OpenaiMessages { get; set; }
}

public static class Session
{
    private static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mini-claude", "sessions");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void EnsureDir()
    {
        if (!Directory.Exists(SessionDir))
            Directory.CreateDirectory(SessionDir);
    }

    public static void SaveSession(string id, SessionData data)
    {
        EnsureDir();
        var path = Path.Combine(SessionDir, $"{id}.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch { /* non-critical */ }
    }

    public static SessionData? LoadSession(string id)
    {
        var file = Path.Combine(SessionDir, $"{id}.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(file), JsonOptions);
        }
        catch { return null; }
    }

    public static List<SessionMetadata> ListSessions()
    {
        EnsureDir();
        var result = new List<SessionMetadata>();
        try
        {
            foreach (var f in Directory.GetFiles(SessionDir, "*.json"))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(f), JsonOptions);
                    if (data?.Metadata != null) result.Add(data.Metadata);
                }
                catch { /* skip */ }
            }
        }
        catch { }
        return result;
    }

    public static string? GetLatestSessionId()
    {
        var sessions = ListSessions();
        if (sessions.Count == 0) return null;
        sessions.Sort((a, b) =>
        {
            if (!DateTime.TryParse(b.StartTime, out var bd)) bd = DateTime.MinValue;
            if (!DateTime.TryParse(a.StartTime, out var ad)) ad = DateTime.MinValue;
            return bd.CompareTo(ad);
        });
        return sessions[0].Id;
    }
}
