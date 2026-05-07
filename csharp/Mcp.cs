// MCP Client — JSON-RPC 2.0 over stdio.
// Mirrors src/mcp.ts.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniClaude;

internal class McpServerConfig
{
    public string Command { get; set; } = "";
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

public class McpToolInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonNode? InputSchema { get; set; }
    public string ServerName { get; set; } = "";
}

internal class McpConnection : IDisposable
{
    private readonly string _serverName;
    private readonly McpServerConfig _config;
    private Process? _process;
    private int _nextId = 1;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _readerCts;

    public McpConnection(string serverName, McpServerConfig config)
    {
        _serverName = serverName;
        _config = config;
    }

    public Task ConnectAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (_config.Args != null)
            foreach (var a in _config.Args) psi.ArgumentList.Add(a);
        if (_config.Env != null)
            foreach (var kv in _config.Env) psi.EnvironmentVariables[kv.Key] = kv.Value;

        _process = Process.Start(psi)
            ?? throw new Exception($"Failed to start MCP server {_serverName}");

        _readerCts = new CancellationTokenSource();
        var token = _readerCts.Token;
        _ = Task.Run(async () =>
        {
            var reader = _process.StandardOutput;
            while (!token.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); }
                catch { break; }
                if (line == null) break;
                try
                {
                    var msg = JsonNode.Parse(line);
                    var idNode = msg?["id"];
                    if (idNode != null && int.TryParse(idNode.ToString(), out var id))
                    {
                        TaskCompletionSource<JsonNode?>? tcs;
                        lock (_lock)
                        {
                            _pending.TryGetValue(id, out tcs);
                            if (tcs != null) _pending.Remove(id);
                        }
                        if (tcs != null)
                        {
                            var error = msg?["error"];
                            if (error != null)
                                tcs.TrySetException(new Exception($"MCP error: {error.ToJsonString()}"));
                            else
                                tcs.TrySetResult(msg?["result"]);
                        }
                    }
                }
                catch { /* ignore non-JSON lines */ }
            }
        }, token);

        // Drain stderr silently
        _ = Task.Run(async () =>
        {
            try { await _process.StandardError.ReadToEndAsync(); } catch { }
        });

        return Task.CompletedTask;
    }

    private async Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params = null)
    {
        if (_process == null || _process.HasExited)
            throw new Exception($"MCP server '{_serverName}' is not connected");

        int id;
        TaskCompletionSource<JsonNode?> tcs = new();
        lock (_lock)
        {
            id = _nextId++;
            _pending[id] = tcs;
        }
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params ?? new JsonObject(),
        };
        await _process.StandardInput.WriteLineAsync(msg.ToJsonString());
        await _process.StandardInput.FlushAsync();
        return await tcs.Task;
    }

    private void SendNotification(string method, JsonNode? @params = null)
    {
        if (_process == null || _process.HasExited) return;
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params ?? new JsonObject(),
        };
        try
        {
            _process.StandardInput.WriteLine(msg.ToJsonString());
            _process.StandardInput.Flush();
        }
        catch { }
    }

    public async Task InitializeAsync()
    {
        await SendRequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "mini-claude", ["version"] = "1.0.0" },
        });
        SendNotification("notifications/initialized");
    }

    public async Task<List<McpToolInfo>> ListToolsAsync()
    {
        var result = await SendRequestAsync("tools/list");
        var tools = result?["tools"] as JsonArray;
        var list = new List<McpToolInfo>();
        if (tools == null) return list;
        foreach (var t in tools)
        {
            if (t == null) continue;
            list.Add(new McpToolInfo
            {
                Name = t["name"]?.ToString() ?? "",
                Description = t["description"]?.ToString() ?? "",
                InputSchema = t["inputSchema"],
                ServerName = _serverName,
            });
        }
        return list;
    }

    public async Task<string> CallToolAsync(string name, JsonNode? args)
    {
        var result = await SendRequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = args ?? new JsonObject(),
        });
        var contentArr = result?["content"] as JsonArray;
        if (contentArr != null)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var c in contentArr)
            {
                if (c?["type"]?.ToString() == "text")
                {
                    if (!first) sb.AppendLine();
                    first = false;
                    sb.Append(c["text"]?.ToString() ?? "");
                }
            }
            return sb.ToString();
        }
        return result?.ToJsonString() ?? "";
    }

    public void Dispose()
    {
        try { _readerCts?.Cancel(); } catch { }
        try { _process?.Kill(); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
}

public class McpManager
{
    private readonly Dictionary<string, McpConnection> _connections = new();
    private readonly List<McpToolInfo> _tools = new();
    private bool _connected;

    public async Task LoadAndConnectAsync()
    {
        if (_connected) return;
        _connected = true;

        var configs = LoadConfigs();
        if (configs.Count == 0) return;

        const int timeoutMs = 15_000;
        foreach (var (name, config) in configs)
        {
            var conn = new McpConnection(name, config);
            try
            {
                await conn.ConnectAsync();
                using var initCts = new CancellationTokenSource(timeoutMs);
                await conn.InitializeAsync().WaitAsync(initCts.Token);
                using var listCts = new CancellationTokenSource(timeoutMs);
                var serverTools = await conn.ListToolsAsync().WaitAsync(listCts.Token);
                _connections[name] = conn;
                _tools.AddRange(serverTools);
                Console.Error.WriteLine($"[mcp] Connected to '{name}' — {serverTools.Count} tools");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[mcp] Failed to connect to '{name}': {e.Message}");
                conn.Dispose();
            }
        }
    }

    public List<ToolDef> GetToolDefinitions()
    {
        return _tools.Select(t => new ToolDef
        {
            Name = $"mcp__{t.ServerName}__{t.Name}",
            Description = string.IsNullOrEmpty(t.Description) ? $"MCP tool {t.Name} from {t.ServerName}" : t.Description,
            InputSchema = (t.InputSchema as JsonObject) ?? JsonNode.Parse("""{ "type": "object", "properties": {} }""")!.AsObject(),
        }).ToList();
    }

    public bool IsMcpTool(string name) => name.StartsWith("mcp__");

    public async Task<string> CallToolAsync(string prefixedName, JsonNode? args)
    {
        var parts = prefixedName.Split("__");
        if (parts.Length < 3) throw new Exception($"Invalid MCP tool name: {prefixedName}");
        var serverName = parts[1];
        var toolName = string.Join("__", parts.Skip(2));
        if (!_connections.TryGetValue(serverName, out var conn))
            throw new Exception($"MCP server '{serverName}' not connected");
        return await conn.CallToolAsync(toolName, args);
    }

    public void DisconnectAll()
    {
        foreach (var c in _connections.Values) c.Dispose();
        _connections.Clear();
        _tools.Clear();
        _connected = false;
    }

    private Dictionary<string, McpServerConfig> LoadConfigs()
    {
        var merged = new Dictionary<string, McpServerConfig>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        MergeConfigFile(Path.Combine(home, ".claude", "settings.json"), merged);
        MergeConfigFile(Path.Combine(Directory.GetCurrentDirectory(), ".claude", "settings.json"), merged);
        MergeConfigFile(Path.Combine(Directory.GetCurrentDirectory(), ".mcp.json"), merged);
        return merged;
    }

    private static void MergeConfigFile(string filePath, Dictionary<string, McpServerConfig> target)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var raw = JsonNode.Parse(File.ReadAllText(filePath));
            var servers = raw?["mcpServers"] ?? raw;
            if (servers is not JsonObject obj) return;
            foreach (var kv in obj)
            {
                var v = kv.Value;
                if (v is not JsonObject cfg) continue;
                var cmd = cfg["command"]?.ToString();
                if (string.IsNullOrEmpty(cmd)) continue;
                var sc = new McpServerConfig { Command = cmd };
                if (cfg["args"] is JsonArray argsArr)
                    sc.Args = argsArr.Where(a => a != null).Select(a => a!.ToString()).ToList();
                if (cfg["env"] is JsonObject envObj)
                {
                    sc.Env = new Dictionary<string, string>();
                    foreach (var ekv in envObj)
                        if (ekv.Value != null) sc.Env[ekv.Key] = ekv.Value.ToString();
                }
                target[kv.Key] = sc;
            }
        }
        catch { }
    }
}
