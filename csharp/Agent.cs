// Agent — main loop with double backend (Anthropic + OpenAI), 4-tier compression,
// streaming tool early-start, sub-agent fork-return, plan mode, budget control.
// Mirrors src/agent.ts.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.SDK;
using Anthropic.SDK.Common;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using OpenAI;
using OpenAI.Chat;

namespace MiniClaude;

public class AgentOptions
{
    public PermissionMode? PermissionMode { get; set; }
    public bool Yolo { get; set; }
    public string? Model { get; set; }
    public string? ApiBase { get; set; }
    public string? AnthropicBaseURL { get; set; }
    public string? ApiKey { get; set; }
    public bool Thinking { get; set; }
    public double? MaxCostUsd { get; set; }
    public int? MaxTurns { get; set; }
    public Func<string, Task<bool>>? ConfirmFn { get; set; }
    public Func<string, Task<PlanApprovalResult>>? PlanApprovalFn { get; set; }
    public string? CustomSystemPrompt { get; set; }
    public List<ToolDef>? CustomTools { get; set; }
    public bool IsSubAgent { get; set; }
}

public class PlanApprovalResult
{
    public string Choice { get; set; } = "execute"; // clear-and-execute | execute | manual-execute | keep-planning
    public string? Feedback { get; set; }
}

public partial class Agent
{
    // ─── Configuration ──────────────────────────────────────
    private readonly AnthropicClient? _anthropicClient;
    private readonly OpenAIClient? _openaiClient;
    private readonly ChatClient? _chatClient;
    private readonly bool _useOpenAI;
    private PermissionMode _permissionMode;
    private readonly bool _thinking;
    private readonly string _thinkingMode; // "adaptive" | "enabled" | "disabled"
    private readonly string _model;
    private string _systemPrompt;
    private List<ToolDef> _tools;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _lastInputTokenCount;
    private readonly int _effectiveWindow;
    private readonly string _sessionId;
    private readonly string _sessionStartTime;
    private readonly bool _isSubAgent;

    // MCP
    private readonly McpManager _mcpManager = new();
    private bool _mcpInitialized;

    // Budget
    private readonly double? _maxCostUsd;
    private readonly int? _maxTurns;
    private int _currentTurns;

    // Compression state
    private long _lastApiCallTime;

    // Abort
    private CancellationTokenSource? _abortController;

    // Permission whitelist
    private readonly HashSet<string> _confirmedPaths = new();

    // Plan mode
    private PermissionMode? _prePlanMode;
    private string? _planFilePath;
    private string _baseSystemPrompt = "";
    private bool _contextCleared;

    // Callbacks
    private Func<string, Task<bool>>? _confirmFn;
    private Func<string, Task<PlanApprovalResult>>? _planApprovalFn;

    // Sub-agent output buffer
    private List<string>? _outputBuffer;

    // Read-before-edit tracking (absolutePath → mtime ticks)
    private readonly Dictionary<string, long> _readFileState = new();

    // Memory
    private readonly HashSet<string> _alreadySurfacedMemories = new();
    private long _sessionMemoryBytes;

    // Message histories — separate per backend
    private readonly List<JsonObject> _anthropicMessages = new(); // Anthropic message format
    private readonly List<ChatMessage> _openaiMessages = new();

    // ─── Compression constants ───────────────────────────────
    private static readonly HashSet<string> SnippableTools = new() { "read_file", "grep_search", "list_files", "run_shell" };
    private const string SnipPlaceholder = "[Content snipped - re-read if needed]";
    private const double SnipThreshold = 0.60;
    private const int MicrocompactIdleMs = 5 * 60 * 1000;
    private const int KeepRecentResults = 3;

    public bool IsProcessing => _abortController != null;

    public Agent(AgentOptions? options = null)
    {
        options ??= new AgentOptions();
        _permissionMode = options.PermissionMode
            ?? (options.Yolo ? PermissionMode.BypassPermissions : PermissionMode.Default);
        _thinking = options.Thinking;
        _model = options.Model ?? "claude-opus-4-6";
        _thinkingMode = ResolveThinkingMode();
        _useOpenAI = !string.IsNullOrEmpty(options.ApiBase);
        _isSubAgent = options.IsSubAgent;
        _tools = options.CustomTools ?? Tools.ToolDefinitions;
        _maxCostUsd = options.MaxCostUsd;
        _maxTurns = options.MaxTurns;
        _confirmFn = options.ConfirmFn;
        _planApprovalFn = options.PlanApprovalFn;
        _effectiveWindow = GetContextWindow(_model) - 20000;
        _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        _sessionStartTime = DateTime.UtcNow.ToString("o");

        _baseSystemPrompt = options.CustomSystemPrompt ?? PromptBuilder.BuildSystemPrompt();
        if (_permissionMode == PermissionMode.Plan)
        {
            _planFilePath = GeneratePlanFilePath();
            _systemPrompt = _baseSystemPrompt + BuildPlanModePrompt();
        }
        else
        {
            _systemPrompt = _baseSystemPrompt;
        }

        if (_useOpenAI)
        {
            var apiKey = options.ApiKey ?? "";
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrEmpty(options.ApiBase))
                clientOptions.Endpoint = new Uri(options.ApiBase);
            _openaiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions);
            _chatClient = _openaiClient.GetChatClient(_model);
            _openaiMessages.Add(new SystemChatMessage(_systemPrompt));
        }
        else
        {
            _anthropicClient = new AnthropicClient(options.ApiKey);
            // Note: Anthropic.SDK doesn't expose baseURL override in v5.6 in a simple way;
            // ANTHROPIC_BASE_URL env var is respected by the SDK natively where supported.
        }
    }

    private string ResolveThinkingMode()
    {
        if (!_thinking) return "disabled";
        if (!ModelSupportsThinking(_model)) return "disabled";
        if (ModelSupportsAdaptiveThinking(_model)) return "adaptive";
        return "enabled";
    }

    public void SetConfirmFn(Func<string, Task<bool>> fn) => _confirmFn = fn;
    public void SetPlanApprovalFn(Func<string, Task<PlanApprovalResult>> fn) => _planApprovalFn = fn;

    // ─── Static helpers ──────────────────────────────────────

    private static readonly Dictionary<string, int> ModelContext = new()
    {
        ["claude-opus-4-6"] = 200000,
        ["claude-sonnet-4-6"] = 200000,
        ["claude-sonnet-4-20250514"] = 200000,
        ["claude-haiku-4-5-20251001"] = 200000,
        ["claude-opus-4-20250514"] = 200000,
        ["gpt-4o"] = 128000,
        ["gpt-4o-mini"] = 128000,
    };

    private static int GetContextWindow(string model)
        => ModelContext.TryGetValue(model, out var w) ? w : 200000;

    private static bool ModelSupportsThinking(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("claude-3-") || m.Contains("3-5-") || m.Contains("3-7-")) return false;
        if (m.Contains("claude") && (m.Contains("opus") || m.Contains("sonnet") || m.Contains("haiku"))) return true;
        return false;
    }

    private static bool ModelSupportsAdaptiveThinking(string model)
    {
        var m = model.ToLowerInvariant();
        return m.Contains("opus-4-6") || m.Contains("sonnet-4-6");
    }

    private static int GetMaxOutputTokens(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("opus-4-6")) return 64000;
        if (m.Contains("sonnet-4-6")) return 32000;
        if (m.Contains("opus-4") || m.Contains("sonnet-4") || m.Contains("haiku-4")) return 32000;
        return 16384;
    }

    // ─── Retry with exponential backoff ─────────────────────

    private static bool IsRetryable(Exception e)
    {
        var msg = e.Message ?? "";
        if (msg.Contains("429") || msg.Contains("503") || msg.Contains("529")) return true;
        if (msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> fn, CancellationToken ct, int maxRetries = 3)
    {
        var rng = new Random();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await fn(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && IsRetryable(e) && attempt < maxRetries)
            {
                var delay = Math.Min(1000 * (int)Math.Pow(2, attempt), 30000) + rng.Next(0, 1000);
                Ui.PrintRetry(attempt + 1, maxRetries, e.Message);
                await Task.Delay(delay, ct);
            }
        }
    }

    // ─── Public API ──────────────────────────────────────────

    public async Task ChatAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (!_isSubAgent && !_mcpInitialized)
        {
            _mcpInitialized = true;
            try
            {
                await _mcpManager.LoadAndConnectAsync();
                var mcpDefs = _mcpManager.GetToolDefinitions();
                if (mcpDefs.Count > 0)
                    _tools = _tools.Concat(mcpDefs).ToList();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[mcp] Init failed: {e.Message}");
            }
        }

        _abortController = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (_useOpenAI) await ChatOpenAIAsync(userMessage);
            else await ChatAnthropicAsync(userMessage);
        }
        finally
        {
            _abortController?.Dispose();
            _abortController = null;
        }

        if (!_isSubAgent)
        {
            Ui.PrintDivider();
            AutoSave();
        }
    }

    public async Task<(string Text, long InputTokens, long OutputTokens)> RunOnceAsync(string prompt)
    {
        _outputBuffer = new List<string>();
        var prevIn = _totalInputTokens;
        var prevOut = _totalOutputTokens;
        await ChatAsync(prompt);
        var text = string.Concat(_outputBuffer);
        _outputBuffer = null;
        return (text, _totalInputTokens - prevIn, _totalOutputTokens - prevOut);
    }

    public void Abort()
    {
        try { _abortController?.Cancel(); } catch { }
    }

    // ─── Output helper ───────────────────────────────────────

    private void EmitText(string text)
    {
        if (_outputBuffer != null) _outputBuffer.Add(text);
        else Ui.PrintAssistantText(text);
    }

    // ─── REPL commands ───────────────────────────────────────

    public void ClearHistory()
    {
        _anthropicMessages.Clear();
        _openaiMessages.Clear();
        if (_useOpenAI) _openaiMessages.Add(new SystemChatMessage(_systemPrompt));
        _totalInputTokens = 0;
        _totalOutputTokens = 0;
        _lastInputTokenCount = 0;
        Ui.PrintInfo("Conversation cleared.");
    }

    public PermissionMode TogglePlanMode()
    {
        if (_permissionMode == PermissionMode.Plan)
        {
            _permissionMode = _prePlanMode ?? PermissionMode.Default;
            _prePlanMode = null;
            _planFilePath = null;
            _systemPrompt = _baseSystemPrompt;
            Ui.PrintInfo($"Exited plan mode → {_permissionMode.ToWire()}");
        }
        else
        {
            _prePlanMode = _permissionMode;
            _permissionMode = PermissionMode.Plan;
            _planFilePath = GeneratePlanFilePath();
            _systemPrompt = _baseSystemPrompt + BuildPlanModePrompt();
            Ui.PrintInfo("Entered plan mode (read-only)");
        }
        if (_useOpenAI && _openaiMessages.Count > 0)
            _openaiMessages[0] = new SystemChatMessage(_systemPrompt);
        return _permissionMode;
    }

    public void ShowCost()
    {
        var total = GetCurrentCostUsd();
        var budgetInfo = _maxCostUsd.HasValue ? $" / ${_maxCostUsd.Value} budget" : "";
        var turnInfo = _maxTurns.HasValue ? $" | Turns: {_currentTurns}/{_maxTurns.Value}" : "";
        Ui.PrintInfo($"Tokens: {_totalInputTokens} in / {_totalOutputTokens} out\n  Estimated cost: ${total:F4}{budgetInfo}{turnInfo}");
    }

    private double GetCurrentCostUsd()
    {
        var costIn = (_totalInputTokens / 1_000_000.0) * 3;
        var costOut = (_totalOutputTokens / 1_000_000.0) * 15;
        return costIn + costOut;
    }

    private (bool Exceeded, string? Reason) CheckBudget()
    {
        if (_maxCostUsd.HasValue && GetCurrentCostUsd() >= _maxCostUsd.Value)
            return (true, $"Cost limit reached (${GetCurrentCostUsd():F4} >= ${_maxCostUsd.Value})");
        if (_maxTurns.HasValue && _currentTurns >= _maxTurns.Value)
            return (true, $"Turn limit reached ({_currentTurns} >= {_maxTurns.Value})");
        return (false, null);
    }

    public Task CompactAsync() => CompactConversationAsync();

    public void RestoreSession(JsonNode? anthropicMessages, JsonNode? openaiMessages)
    {
        if (anthropicMessages is JsonArray aArr)
        {
            _anthropicMessages.Clear();
            foreach (var m in aArr) if (m is JsonObject mo) _anthropicMessages.Add(mo);
        }
        if (openaiMessages is JsonArray oArr)
        {
            // OpenAI message restoration is non-trivial due to type erasure;
            // we store as raw JSON and skip rehydration for simplicity.
            // (Original TS version does a structural restore.)
        }
        Ui.PrintInfo($"Session restored ({GetMessageCount()} messages).");
    }

    private int GetMessageCount() => _useOpenAI ? _openaiMessages.Count : _anthropicMessages.Count;

    private void AutoSave()
    {
        try
        {
            var data = new SessionData
            {
                Metadata = new SessionMetadata
                {
                    Id = _sessionId,
                    Model = _model,
                    Cwd = Directory.GetCurrentDirectory(),
                    StartTime = _sessionStartTime,
                    MessageCount = GetMessageCount(),
                },
                AnthropicMessages = _useOpenAI ? null : SerializeAnthropicMessages(),
                // OpenAI messages: skip persistence for simplicity
            };
            Session.SaveSession(_sessionId, data);
        }
        catch { }
    }

    private JsonNode? SerializeAnthropicMessages()
    {
        var arr = new JsonArray();
        foreach (var m in _anthropicMessages) arr.Add(JsonNode.Parse(m.ToJsonString()));
        return arr;
    }

    // ─── Plan mode ───────────────────────────────────────────

    private string GeneratePlanFilePath()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), ".claude", "plans");
        try { Directory.CreateDirectory(dir); } catch { }
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(dir, $"plan-{ts}-{_sessionId}.md");
    }

    private string BuildPlanModePrompt()
    {
        return $@"

# Plan Mode (READ-ONLY)
You are now in plan mode. You can ONLY:
- Read files (read_file, list_files, grep_search, web_fetch)
- Write to the designated plan file: {_planFilePath}

You CANNOT:
- Modify any other file (write_file/edit_file are blocked except for the plan file)
- Run shell commands (run_shell is blocked)

Your task: explore the codebase, then write a detailed implementation plan to:
{_planFilePath}

When the plan is complete, call exit_plan_mode and the user will review.";
    }

    private async Task<bool> ConfirmDangerousAsync(string command)
    {
        Ui.PrintConfirmation(command);
        if (_confirmFn != null) return await _confirmFn(command);
        Console.Write("  Allow? (y/n): ");
        var line = Console.ReadLine() ?? "";
        return line.Trim().ToLowerInvariant().StartsWith("y");
    }

    // ─── Tool execution dispatch ─────────────────────────────

    private async Task<string> ExecuteToolCallAsync(string name, Dictionary<string, object?> input)
    {
        // Plan mode tools
        if (name == "enter_plan_mode" || name == "exit_plan_mode")
            return await ExecutePlanModeToolAsync(name, input);

        // Skill tool
        if (name == "skill")
        {
            var skillName = Tools.GetString(input, "skill_name");
            var args = Tools.GetString(input, "args");
            var result = Skills.ExecuteSkill(skillName, args);
            if (result == null) return $"Unknown skill: {skillName}";
            if (result.Context == "fork")
            {
                var subAgent = new Agent(new AgentOptions
                {
                    Model = _model,
                    ApiBase = _useOpenAI ? Environment.GetEnvironmentVariable("OPENAI_BASE_URL") : null,
                    ApiKey = _useOpenAI ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                    CustomSystemPrompt = result.Prompt,
                    IsSubAgent = true,
                    PermissionMode = PermissionMode.BypassPermissions,
                });
                var sub = await subAgent.RunOnceAsync(result.Prompt);
                _totalInputTokens += sub.InputTokens;
                _totalOutputTokens += sub.OutputTokens;
                return string.IsNullOrEmpty(sub.Text) ? "(Skill produced no output)" : sub.Text;
            }
            return result.Prompt;
        }

        // Agent (sub-agent) tool
        if (name == "agent") return await ExecuteAgentToolAsync(input);

        // MCP tool
        if (_mcpManager.IsMcpTool(name))
        {
            var jsonArgs = JsonNode.Parse(JsonSerializer.Serialize(input));
            try { return await _mcpManager.CallToolAsync(name, jsonArgs); }
            catch (Exception e) { return $"MCP error: {e.Message}"; }
        }

        // Built-in
        return await Tools.ExecuteAsync(name, input, _readFileState, _abortController?.Token ?? CancellationToken.None);
    }

    private async Task<string> ExecutePlanModeToolAsync(string name, Dictionary<string, object?> input)
    {
        if (name == "enter_plan_mode")
        {
            _prePlanMode = _permissionMode;
            _permissionMode = PermissionMode.Plan;
            _planFilePath = GeneratePlanFilePath();
            _systemPrompt = _baseSystemPrompt + BuildPlanModePrompt();
            if (_useOpenAI && _openaiMessages.Count > 0)
                _openaiMessages[0] = new SystemChatMessage(_systemPrompt);
            return $"Entered plan mode. Write your plan to: {_planFilePath}";
        }

        if (name == "exit_plan_mode")
        {
            string planContent = "";
            string? savedPlanPath = _planFilePath;
            if (_planFilePath != null && File.Exists(_planFilePath))
            {
                try { planContent = await File.ReadAllTextAsync(_planFilePath); } catch { }
            }
            else
            {
                planContent = "(No plan file written)";
            }

            if (_planApprovalFn != null && !string.IsNullOrEmpty(planContent))
            {
                var approval = await _planApprovalFn(planContent);
                if (approval.Choice == "keep-planning")
                {
                    var feedback = approval.Feedback ?? "(no feedback)";
                    return $"User chose to keep planning. Feedback: {feedback}\n\nContinue refining the plan.";
                }
                var targetMode = _prePlanMode ?? PermissionMode.Default;
                _permissionMode = targetMode;
                _prePlanMode = null;
                _systemPrompt = _baseSystemPrompt;
                if (_useOpenAI && _openaiMessages.Count > 0)
                    _openaiMessages[0] = new SystemChatMessage(_systemPrompt);

                if (approval.Choice == "manual-execute")
                {
                    Ui.PrintInfo($"Plan saved to {savedPlanPath}. Exited plan mode for manual execution.");
                    return $"User will execute the plan manually. Plan: {savedPlanPath}";
                }
                if (approval.Choice == "clear-and-execute")
                {
                    ClearHistoryKeepSystem();
                    _contextCleared = true;
                    Ui.PrintInfo($"Plan approved. Context cleared, executing in {targetMode.ToWire()} mode.");
                    return $"User approved the plan. Context was cleared. Permission mode: {targetMode.ToWire()}\n\nPlan file: {savedPlanPath}\n\n## Approved Plan:\n{planContent}\n\nProceed with implementation.";
                }
                Ui.PrintInfo($"Plan approved. Executing in {targetMode.ToWire()} mode.");
                return $"User approved the plan. Permission mode: {targetMode.ToWire()}\n\n## Approved Plan:\n{planContent}\n\nProceed with implementation.";
            }

            // Fallback: no approval function (sub-agent)
            _permissionMode = _prePlanMode ?? PermissionMode.Default;
            _prePlanMode = null;
            _planFilePath = null;
            _systemPrompt = _baseSystemPrompt;
            if (_useOpenAI && _openaiMessages.Count > 0)
                _openaiMessages[0] = new SystemChatMessage(_systemPrompt);
            Ui.PrintInfo($"Exited plan mode. Restored to {_permissionMode.ToWire()} mode.");
            return $"Exited plan mode. Permission mode restored to: {_permissionMode.ToWire()}\n\n## Your Plan:\n{planContent}";
        }

        return $"Unknown plan mode tool: {name}";
    }

    private void ClearHistoryKeepSystem()
    {
        _anthropicMessages.Clear();
        _openaiMessages.Clear();
        if (_useOpenAI) _openaiMessages.Add(new SystemChatMessage(_systemPrompt));
        _lastInputTokenCount = 0;
    }

    private async Task<string> ExecuteAgentToolAsync(Dictionary<string, object?> input)
    {
        var type = Tools.GetString(input, "type");
        if (string.IsNullOrEmpty(type)) type = "general";
        var description = Tools.GetString(input, "description");
        if (string.IsNullOrEmpty(description)) description = "sub-agent task";
        var prompt = Tools.GetString(input, "prompt");

        Ui.PrintSubAgentStart(type, description);
        var config = SubAgent.GetSubAgentConfig(type);
        var subAgent = new Agent(new AgentOptions
        {
            Model = _model,
            ApiBase = _useOpenAI ? Environment.GetEnvironmentVariable("OPENAI_BASE_URL") : null,
            ApiKey = _useOpenAI ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            CustomSystemPrompt = config.SystemPrompt,
            CustomTools = config.Tools,
            IsSubAgent = true,
            PermissionMode = _permissionMode == PermissionMode.Plan ? PermissionMode.Plan : PermissionMode.BypassPermissions,
        });
        try
        {
            var sub = await subAgent.RunOnceAsync(prompt);
            _totalInputTokens += sub.InputTokens;
            _totalOutputTokens += sub.OutputTokens;
            Ui.PrintSubAgentEnd(type, description);
            return string.IsNullOrEmpty(sub.Text) ? "(Sub-agent produced no output)" : sub.Text;
        }
        catch (Exception e)
        {
            Ui.PrintSubAgentEnd(type, description);
            return $"Sub-agent error: {e.Message}";
        }
    }

    // ─── Persist large results to disk ───────────────────────

    private const int LargeResultThreshold = 30000;
    private string PersistLargeResult(string toolName, string raw)
    {
        if (raw.Length < LargeResultThreshold) return raw;
        try
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), ".claude", "tool-results");
            Directory.CreateDirectory(dir);
            var fname = $"{toolName}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt";
            var fpath = Path.Combine(dir, fname);
            File.WriteAllText(fpath, raw);
            var preview = raw.Substring(0, Math.Min(2000, raw.Length));
            return $"{preview}\n\n[... result too large ({raw.Length} chars), full content saved to: {fpath} ...]";
        }
        catch { return raw; }
    }

    // ─── Compression pipeline ────────────────────────────────

    private void RunCompressionPipeline()
    {
        if (_useOpenAI)
        {
            // OpenAI message types are not easily mutable; we skip in-place mutation
            // and rely on Tier 4 (auto-compact) for OpenAI backend.
            // Tools.ExecuteAsync already truncates to MaxResultChars upstream.
        }
        else
        {
            BudgetToolResultsAnthropic();
            SnipStaleResultsAnthropic();
            MicrocompactAnthropic();
        }
    }

    private void BudgetToolResultsAnthropic()
    {
        var utilization = (double)_lastInputTokenCount / _effectiveWindow;
        if (utilization < 0.5) return;
        var budget = utilization > 0.7 ? 15000 : 30000;

        foreach (var msg in _anthropicMessages)
        {
            if (msg["role"]?.ToString() != "user") continue;
            if (msg["content"] is not JsonArray contentArr) continue;
            foreach (var block in contentArr)
            {
                if (block?["type"]?.ToString() == "tool_result")
                {
                    var contentNode = block["content"];
                    if (contentNode is JsonValue cv && cv.TryGetValue<string>(out var s) && s.Length > budget)
                    {
                        var keepEach = (budget - 80) / 2;
                        var newContent = s.Substring(0, keepEach) +
                            $"\n\n[... budgeted: {s.Length - keepEach * 2} chars truncated ...]\n\n" +
                            s.Substring(s.Length - keepEach);
                        block["content"] = newContent;
                    }
                }
            }
        }
    }

    private (string Name, JsonObject? Input)? FindToolUseById(string toolUseId)
    {
        foreach (var msg in _anthropicMessages)
        {
            if (msg["role"]?.ToString() != "assistant") continue;
            if (msg["content"] is not JsonArray contentArr) continue;
            foreach (var block in contentArr)
            {
                if (block?["type"]?.ToString() == "tool_use" && block["id"]?.ToString() == toolUseId)
                {
                    return (block["name"]?.ToString() ?? "", block["input"] as JsonObject);
                }
            }
        }
        return null;
    }

    private void SnipStaleResultsAnthropic()
    {
        var utilization = (double)_lastInputTokenCount / _effectiveWindow;
        if (utilization < SnipThreshold) return;

        var results = new List<(int MsgIdx, int BlockIdx, string ToolName, string? FilePath)>();
        for (int mi = 0; mi < _anthropicMessages.Count; mi++)
        {
            var msg = _anthropicMessages[mi];
            if (msg["role"]?.ToString() != "user") continue;
            if (msg["content"] is not JsonArray contentArr) continue;
            for (int bi = 0; bi < contentArr.Count; bi++)
            {
                var block = contentArr[bi];
                if (block?["type"]?.ToString() == "tool_result")
                {
                    var content = block["content"]?.ToString();
                    if (content == null || content == SnipPlaceholder) continue;
                    var toolUseId = block["tool_use_id"]?.ToString();
                    if (toolUseId == null) continue;
                    var info = FindToolUseById(toolUseId);
                    if (info.HasValue && SnippableTools.Contains(info.Value.Name))
                    {
                        var fp = info.Value.Input?["file_path"]?.ToString();
                        results.Add((mi, bi, info.Value.Name, fp));
                    }
                }
            }
        }

        if (results.Count <= KeepRecentResults) return;

        var toSnip = new HashSet<int>();
        var seenFiles = new Dictionary<string, List<int>>();
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.FilePath != null)
            {
                if (!seenFiles.TryGetValue(r.FilePath, out var list)) { list = new List<int>(); seenFiles[r.FilePath] = list; }
                list.Add(i);
            }
        }
        foreach (var list in seenFiles.Values)
            for (int j = 0; j < list.Count - 1; j++) toSnip.Add(list[j]);

        var keepThreshold = results.Count - KeepRecentResults;
        for (int i = 0; i < keepThreshold; i++) toSnip.Add(i);

        foreach (var idx in toSnip)
        {
            var r = results[idx];
            var msg = _anthropicMessages[r.MsgIdx];
            if (msg["content"] is JsonArray arr && r.BlockIdx < arr.Count)
            {
                var b = arr[r.BlockIdx];
                if (b != null) b["content"] = SnipPlaceholder;
            }
        }
    }

    private void MicrocompactAnthropic()
    {
        if (_lastApiCallTime == 0) return;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _lastApiCallTime < MicrocompactIdleMs) return;

        foreach (var msg in _anthropicMessages)
        {
            if (msg["role"]?.ToString() != "user") continue;
            if (msg["content"] is not JsonArray contentArr) continue;
            foreach (var block in contentArr)
            {
                if (block?["type"]?.ToString() == "tool_result")
                {
                    var content = block["content"]?.ToString();
                    if (!string.IsNullOrEmpty(content) && content != SnipPlaceholder && content.Length > 1000)
                    {
                        block["content"] = SnipPlaceholder;
                    }
                }
            }
        }
    }

    private async Task CheckAndCompactAsync()
    {
        if (_lastInputTokenCount > _effectiveWindow * 0.85)
        {
            Ui.PrintInfo("Context window filling up, compacting conversation...");
            await CompactConversationAsync();
        }
    }

    private async Task CompactConversationAsync()
    {
        if (_useOpenAI) await CompactOpenAIAsync();
        else await CompactAnthropicAsync();
        Ui.PrintInfo("Conversation compacted.");
    }

    // ─── SideQuery for memory recall ─────────────────────────

    private SideQueryFn? BuildSideQuery()
    {
        if (_anthropicClient != null)
        {
            return async (system, userMessage, ct) =>
            {
                try
                {
                    var req = new MessageParameters
                    {
                        Model = _model,
                        MaxTokens = 256,
                        System = new List<SystemMessage> { new SystemMessage(system) },
                        Messages = new List<Message>
                        {
                            new Message(RoleType.User, userMessage),
                        },
                    };
                    var resp = await _anthropicClient.Messages.GetClaudeMessageAsync(req, ctx: ct);
                    return resp.Content.OfType<TextContent>().Select(c => c.Text).Aggregate("", (a, b) => a + b);
                }
                catch { return ""; }
            };
        }
        if (_chatClient != null)
        {
            return async (system, userMessage, ct) =>
            {
                try
                {
                    var msgs = new List<ChatMessage>
                    {
                        new SystemChatMessage(system),
                        new UserChatMessage(userMessage),
                    };
                    var resp = await _chatClient.CompleteChatAsync(msgs, new ChatCompletionOptions { MaxOutputTokenCount = 256 }, ct);
                    return resp.Value.Content.Count > 0 ? resp.Value.Content[0].Text : "";
                }
                catch { return ""; }
            };
        }
        return null;
    }
}
