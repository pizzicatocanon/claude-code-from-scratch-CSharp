// Agent backends — Anthropic streaming loop + OpenAI streaming loop + compact methods.
// Mirrors the chatAnthropic/chatOpenAI/compactAnthropic/compactOpenAI parts of src/agent.ts.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.SDK.Common;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using OpenAI.Chat;

namespace MiniClaude;

public partial class Agent
{
    // ─── Convert tools to OpenAI ChatTool format ─────────────

    private static List<ChatTool> ToOpenAITools(IEnumerable<ToolDef> tools)
    {
        return tools.Select(t => ChatTool.CreateFunctionTool(
            functionName: t.Name,
            functionDescription: t.Description,
            functionParameters: BinaryData.FromString(t.InputSchema.ToJsonString())
        )).ToList();
    }

    // ─── Convert tools to Anthropic Tool format ──────────────

    private static List<Anthropic.SDK.Common.Tool> ToAnthropicTools(IEnumerable<ToolDef> tools)
    {
        var list = new List<Anthropic.SDK.Common.Tool>();
        foreach (var t in tools)
        {
            // Build a Function from the tool definition then wrap as Tool
            var fn = new Function(t.Name, t.Description, t.InputSchema.ToJsonString());
            list.Add(new Anthropic.SDK.Common.Tool(fn));
        }
        return list;
    }

    // ─── Anthropic backend ───────────────────────────────────

    private async Task ChatAnthropicAsync(string userMessage)
    {
        var ct = _abortController?.Token ?? CancellationToken.None;
        // Append user message
        _anthropicMessages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userMessage,
        });

        // Memory prefetch
        MemoryPrefetch? memoryPrefetch = null;
        if (!_isSubAgent)
        {
            var sq = BuildSideQuery();
            if (sq != null)
                memoryPrefetch = Memory.StartMemoryPrefetch(userMessage, sq, _alreadySurfacedMemories, _sessionMemoryBytes, ct);
        }

        while (true)
        {
            if (ct.IsCancellationRequested) break;

            RunCompressionPipeline();

            // Consume memory prefetch if settled
            if (memoryPrefetch != null && memoryPrefetch.Settled && !memoryPrefetch.Consumed)
            {
                memoryPrefetch.Consumed = true;
                try
                {
                    var memories = await memoryPrefetch.Promise;
                    if (memories.Count > 0)
                    {
                        var injection = Memory.FormatMemoriesForInjection(memories);
                        var last = _anthropicMessages[^1];
                        if (last["role"]?.ToString() == "user")
                        {
                            var content = last["content"];
                            if (content is JsonValue cv && cv.TryGetValue<string>(out var s))
                                last["content"] = s + "\n\n" + injection;
                            else if (content is JsonArray arr)
                                arr.Add(new JsonObject { ["type"] = "text", ["text"] = injection });
                        }
                        else
                        {
                            _anthropicMessages.Add(new JsonObject { ["role"] = "user", ["content"] = injection });
                        }
                        foreach (var m in memories)
                        {
                            _alreadySurfacedMemories.Add(m.Path);
                            _sessionMemoryBytes += Encoding.UTF8.GetByteCount(m.Content);
                        }
                    }
                }
                catch { }
            }

            if (!_isSubAgent) Ui.StartSpinner();

            // ── Streaming tool early execution ──
            var earlyExecutions = new Dictionary<string, Task<string>>();
            var response = await CallAnthropicStreamAsync((toolUseId, toolName, parsedInput) =>
            {
                if (Tools.ConcurrencySafeTools.Contains(toolName))
                {
                    var perm = Tools.CheckPermission(toolName, parsedInput, _permissionMode, _planFilePath);
                    if (perm.Action == "allow")
                        earlyExecutions[toolUseId] = ExecuteToolCallAsync(toolName, parsedInput);
                }
            }, ct);
            if (!_isSubAgent) Ui.StopSpinner();
            _lastApiCallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _totalInputTokens += response.InputTokens;
            _totalOutputTokens += response.OutputTokens;
            _lastInputTokenCount = response.InputTokens;

            // Store assistant response in history
            _anthropicMessages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = response.ContentArray,
            });

            if (response.ToolUses.Count == 0)
            {
                if (!_isSubAgent) Ui.PrintCost(_totalInputTokens, _totalOutputTokens);
                break;
            }

            // Budget check
            _currentTurns++;
            var (exceeded, reason) = CheckBudget();
            if (exceeded) { Ui.PrintInfo($"Budget exceeded: {reason}"); break; }

            // Process tool calls
            var toolResults = new JsonArray();
            bool contextBreak = false;
            foreach (var toolUse in response.ToolUses)
            {
                if (contextBreak || ct.IsCancellationRequested) break;
                Ui.PrintToolCall(toolUse.Name, toolUse.Input);

                if (earlyExecutions.TryGetValue(toolUse.Id, out var earlyTask))
                {
                    var raw = await earlyTask;
                    var res = PersistLargeResult(toolUse.Name, raw);
                    Ui.PrintToolResult(toolUse.Name, res);
                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUse.Id,
                        ["content"] = res,
                    });
                    continue;
                }

                var perm = Tools.CheckPermission(toolUse.Name, toolUse.Input, _permissionMode, _planFilePath);
                if (perm.Action == "deny")
                {
                    Ui.PrintInfo($"Denied: {perm.Message}");
                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUse.Id,
                        ["content"] = $"Action denied: {perm.Message}",
                    });
                    continue;
                }
                if (perm.Action == "confirm" && perm.Message != null && !_confirmedPaths.Contains(perm.Message))
                {
                    var confirmed = await ConfirmDangerousAsync(perm.Message);
                    if (!confirmed)
                    {
                        toolResults.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolUse.Id,
                            ["content"] = "User denied this action.",
                        });
                        continue;
                    }
                    _confirmedPaths.Add(perm.Message);
                }

                var rawRes = await ExecuteToolCallAsync(toolUse.Name, toolUse.Input);
                var resPersisted = PersistLargeResult(toolUse.Name, rawRes);
                Ui.PrintToolResult(toolUse.Name, resPersisted);

                if (_contextCleared)
                {
                    _contextCleared = false;
                    _anthropicMessages.Add(new JsonObject { ["role"] = "user", ["content"] = resPersisted });
                    contextBreak = true;
                    break;
                }
                toolResults.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUse.Id,
                    ["content"] = resPersisted,
                });
            }

            if (!contextBreak && !_contextCleared && toolResults.Count > 0)
                _anthropicMessages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            _contextCleared = false;

            await CheckAndCompactAsync();
        }
    }

    private class AnthropicResponse
    {
        public JsonArray ContentArray { get; set; } = new();
        public List<(string Id, string Name, Dictionary<string, object?> Input)> ToolUses { get; set; } = new();
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }

    private async Task<AnthropicResponse> CallAnthropicStreamAsync(
        Action<string, string, Dictionary<string, object?>>? onToolBlockComplete,
        CancellationToken ct)
    {
        return await WithRetryAsync(async (token) =>
        {
            var maxOutput = GetMaxOutputTokens(_model);
            var req = new MessageParameters
            {
                Model = _model,
                MaxTokens = _thinkingMode != "disabled" ? maxOutput : 16384,
                System = new List<SystemMessage> { new SystemMessage(_systemPrompt) },
                Messages = ConvertAnthropicMessagesToSdk(),
                Stream = true,
                Tools = ToAnthropicTools(Tools.GetActiveToolDefinitions(_tools)),
            };

            // Stream text deltas to the user; the SDK aggregates the final message
            // with full content (including ToolUseContent blocks) at the end.
            // Note: streaming-tool-early-start (TS optimization) is not implemented
            // here because the .NET SDK doesn't expose per-block content_block_stop
            // events at this level — we wait for the full response, then dispatch
            // tools serially or via the OpenAI backend's batching strategy.
            var textAccumulated = new StringBuilder();
            bool firstText = true;
            MessageResponse? final = null;

            await foreach (var evt in _anthropicClient!.Messages.StreamClaudeMessageAsync(req, ctx: token))
            {
                // The high-level Delta surface text deltas
                if (!string.IsNullOrEmpty(evt.Delta?.Text))
                {
                    if (firstText) { Ui.StopSpinner(); EmitText("\n"); firstText = false; }
                    EmitText(evt.Delta.Text);
                    textAccumulated.Append(evt.Delta.Text);
                }
                // The last event in the stream contains the aggregated MessageResponse
                final = evt;
            }

            long inputTokens = final?.Usage?.InputTokens ?? 0;
            long outputTokens = final?.Usage?.OutputTokens ?? 0;

            // Build the content array (text + tool_use) from final.Content
            var arr = new JsonArray();
            var toolUses = new List<(string Id, string Name, Dictionary<string, object?> Input)>();
            if (final?.Content != null)
            {
                foreach (var block in final.Content)
                {
                    switch (block)
                    {
                        case TextContent tc:
                            arr.Add(new JsonObject { ["type"] = "text", ["text"] = tc.Text ?? "" });
                            break;
                        case ToolUseContent tu:
                            var inputJson = tu.Input?.ToJsonString() ?? "{}";
                            arr.Add(new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = tu.Id ?? "",
                                ["name"] = tu.Name ?? "",
                                ["input"] = JsonNode.Parse(inputJson),
                            });
                            toolUses.Add((tu.Id ?? "", tu.Name ?? "", ParseToolInput(inputJson)));
                            // Optional callback parity (no early-start in .NET version)
                            onToolBlockComplete?.Invoke(tu.Id ?? "", tu.Name ?? "", ParseToolInput(inputJson));
                            break;
                    }
                }
            }
            // If no text blocks but we accumulated text via deltas, keep it
            if (arr.Count == 0 && textAccumulated.Length > 0)
                arr.Add(new JsonObject { ["type"] = "text", ["text"] = textAccumulated.ToString() });

            return new AnthropicResponse
            {
                ContentArray = arr,
                ToolUses = toolUses,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            };
        }, ct);
    }

    private static Dictionary<string, object?> ParseToolInput(string json)
    {
        var dict = new Dictionary<string, object?>();
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                foreach (var kv in obj)
                {
                    dict[kv.Key] = kv.Value switch
                    {
                        JsonValue jv when jv.TryGetValue<string>(out var s) => s,
                        JsonValue jv when jv.TryGetValue<long>(out var l) => l,
                        JsonValue jv when jv.TryGetValue<double>(out var d) => d,
                        JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
                        null => null,
                        _ => kv.Value?.ToString(),
                    };
                }
            }
        }
        catch { }
        return dict;
    }

    private List<Message> ConvertAnthropicMessagesToSdk()
    {
        var list = new List<Message>();
        foreach (var m in _anthropicMessages)
        {
            var role = m["role"]?.ToString() == "assistant" ? RoleType.Assistant : RoleType.User;
            var content = m["content"];
            if (content is JsonValue cv && cv.TryGetValue<string>(out var s))
            {
                list.Add(new Message(role, s));
            }
            else if (content is JsonArray arr)
            {
                // Build content blocks (text/tool_use/tool_result)
                var msg = new Message { Role = role };
                var contentList = new List<ContentBase>();
                foreach (var b in arr)
                {
                    if (b == null) continue;
                    var type = b["type"]?.ToString();
                    switch (type)
                    {
                        case "text":
                            contentList.Add(new TextContent { Text = b["text"]?.ToString() ?? "" });
                            break;
                        case "tool_use":
                            contentList.Add(new ToolUseContent
                            {
                                Id = b["id"]?.ToString() ?? "",
                                Name = b["name"]?.ToString() ?? "",
                                Input = b["input"] as JsonObject,
                            });
                            break;
                        case "tool_result":
                            contentList.Add(new ToolResultContent
                            {
                                ToolUseId = b["tool_use_id"]?.ToString() ?? "",
                                Content = new List<ContentBase> { new TextContent { Text = b["content"]?.ToString() ?? "" } },
                            });
                            break;
                    }
                }
                msg.Content = contentList;
                list.Add(msg);
            }
        }
        return list;
    }

    // ─── Compact: Anthropic ──────────────────────────────────

    private async Task CompactAnthropicAsync()
    {
        if (_anthropicMessages.Count < 4) return;
        var lastUser = _anthropicMessages[^1];
        var summaryReq = new List<JsonObject>(_anthropicMessages.Take(_anthropicMessages.Count - 1))
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Summarize the conversation so far in a concise paragraph, preserving key decisions, file paths, and context needed to continue the work.",
            }
        };

        // Temporarily swap messages, call, then rebuild
        var saved = new List<JsonObject>(_anthropicMessages);
        _anthropicMessages.Clear();
        _anthropicMessages.AddRange(summaryReq);
        var sdkMessages = ConvertAnthropicMessagesToSdk();
        _anthropicMessages.Clear();
        _anthropicMessages.AddRange(saved);

        var req = new MessageParameters
        {
            Model = _model,
            MaxTokens = 2048,
            System = new List<SystemMessage> { new SystemMessage("You are a conversation summarizer. Be concise but preserve important details.") },
            Messages = sdkMessages,
        };
        string summaryText;
        try
        {
            var resp = await _anthropicClient!.Messages.GetClaudeMessageAsync(req);
            summaryText = resp.Content.OfType<TextContent>().Select(c => c.Text).Aggregate("", (a, b) => a + b);
            if (string.IsNullOrEmpty(summaryText)) summaryText = "No summary available.";
        }
        catch { summaryText = "No summary available."; }

        _anthropicMessages.Clear();
        _anthropicMessages.Add(new JsonObject { ["role"] = "user", ["content"] = $"[Previous conversation summary]\n{summaryText}" });
        _anthropicMessages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Understood. I have the context from our previous conversation. How can I continue helping?" });
        if (lastUser["role"]?.ToString() == "user") _anthropicMessages.Add(lastUser);
        _lastInputTokenCount = 0;
    }

    // ─── OpenAI backend ──────────────────────────────────────

    private async Task ChatOpenAIAsync(string userMessage)
    {
        var ct = _abortController?.Token ?? CancellationToken.None;
        _openaiMessages.Add(new UserChatMessage(userMessage));

        MemoryPrefetch? memoryPrefetch = null;
        if (!_isSubAgent)
        {
            var sq = BuildSideQuery();
            if (sq != null)
                memoryPrefetch = Memory.StartMemoryPrefetch(userMessage, sq, _alreadySurfacedMemories, _sessionMemoryBytes, ct);
        }

        while (true)
        {
            if (ct.IsCancellationRequested) break;

            RunCompressionPipeline();

            if (memoryPrefetch != null && memoryPrefetch.Settled && !memoryPrefetch.Consumed)
            {
                memoryPrefetch.Consumed = true;
                try
                {
                    var memories = await memoryPrefetch.Promise;
                    if (memories.Count > 0)
                    {
                        var injection = Memory.FormatMemoriesForInjection(memories);
                        // Append to last user message
                        if (_openaiMessages[^1] is UserChatMessage)
                        {
                            // OpenAI ChatMessage is largely immutable; append a new user message
                            _openaiMessages.Add(new UserChatMessage(injection));
                        }
                        else
                        {
                            _openaiMessages.Add(new UserChatMessage(injection));
                        }
                        foreach (var m in memories)
                        {
                            _alreadySurfacedMemories.Add(m.Path);
                            _sessionMemoryBytes += Encoding.UTF8.GetByteCount(m.Content);
                        }
                    }
                }
                catch { }
            }

            if (!_isSubAgent) Ui.StartSpinner();

            var streamResult = await CallOpenAIStreamAsync(ct);
            if (!_isSubAgent) Ui.StopSpinner();
            _lastApiCallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _totalInputTokens += streamResult.InputTokens;
            _totalOutputTokens += streamResult.OutputTokens;
            _lastInputTokenCount = streamResult.InputTokens;

            // Add assistant message to history
            if (streamResult.ToolCalls.Count == 0 && !string.IsNullOrEmpty(streamResult.Content))
            {
                _openaiMessages.Add(new AssistantChatMessage(streamResult.Content));
            }
            else if (streamResult.ToolCalls.Count > 0)
            {
                var toolCallObjs = streamResult.ToolCalls.Select(tc =>
                {
                    // OpenAI SDK rejects null/empty BinaryData for function arguments.
                    // Some providers (e.g. Volcengine Ark / DeepSeek) emit zero-arg tool
                    // calls with empty argument strings — coerce to "{}" so the SDK accepts them.
                    var args = string.IsNullOrEmpty(tc.Arguments) ? "{}" : tc.Arguments;
                    var id = string.IsNullOrEmpty(tc.Id) ? $"call_{Guid.NewGuid():N}" : tc.Id;
                    var name = string.IsNullOrEmpty(tc.Name) ? "unknown" : tc.Name;
                    return ChatToolCall.CreateFunctionToolCall(id, name, BinaryData.FromString(args));
                }).ToList();
                // OpenAI 2.1: AssistantChatMessage(IEnumerable<ChatToolCall>) ctor exists,
                // but it does not accept a content string in the same call. Use it then
                // attach text via the Content collection if non-empty.
                var assistantMsg = new AssistantChatMessage(toolCallObjs);
                if (!string.IsNullOrEmpty(streamResult.Content))
                    assistantMsg.Content.Add(ChatMessageContentPart.CreateTextPart(streamResult.Content));
                _openaiMessages.Add(assistantMsg);
            }

            if (streamResult.ToolCalls.Count == 0)
            {
                if (!_isSubAgent) Ui.PrintCost(_totalInputTokens, _totalOutputTokens);
                break;
            }

            _currentTurns++;
            var (exceeded, reason) = CheckBudget();
            if (exceeded) { Ui.PrintInfo($"Budget exceeded: {reason}"); break; }

            // Phase 1: parse + permission check
            var checkedCalls = new List<(string Id, string Name, Dictionary<string, object?> Input, bool Allowed, string? FailMsg)>();
            foreach (var tc in streamResult.ToolCalls)
            {
                if (ct.IsCancellationRequested) break;
                Dictionary<string, object?> input;
                try { input = ParseToolInput(tc.Arguments); } catch { input = new(); }
                Ui.PrintToolCall(tc.Name, input);

                var perm = Tools.CheckPermission(tc.Name, input, _permissionMode, _planFilePath);
                if (perm.Action == "deny")
                {
                    Ui.PrintInfo($"Denied: {perm.Message}");
                    checkedCalls.Add((tc.Id, tc.Name, input, false, $"Action denied: {perm.Message}"));
                    continue;
                }
                if (perm.Action == "confirm" && perm.Message != null && !_confirmedPaths.Contains(perm.Message))
                {
                    var confirmed = await ConfirmDangerousAsync(perm.Message);
                    if (!confirmed)
                    {
                        checkedCalls.Add((tc.Id, tc.Name, input, false, "User denied this action."));
                        continue;
                    }
                    _confirmedPaths.Add(perm.Message);
                }
                checkedCalls.Add((tc.Id, tc.Name, input, true, null));
            }

            // Phase 2: group + execute (parallel for consecutive safe tools)
            var batches = new List<(bool Concurrent, List<(string Id, string Name, Dictionary<string, object?> Input, bool Allowed, string? FailMsg)> Items)>();
            foreach (var ck in checkedCalls)
            {
                bool safe = ck.Allowed && Tools.ConcurrencySafeTools.Contains(ck.Name);
                if (safe && batches.Count > 0 && batches[^1].Concurrent)
                    batches[^1].Items.Add(ck);
                else
                    batches.Add((safe, new() { ck }));
            }

            bool oaiContextBreak = false;
            foreach (var batch in batches)
            {
                if (oaiContextBreak || ct.IsCancellationRequested) break;

                if (batch.Concurrent)
                {
                    var tasks = batch.Items.Select(async ck =>
                    {
                        var raw = await ExecuteToolCallAsync(ck.Name, ck.Input);
                        var res = PersistLargeResult(ck.Name, raw);
                        Ui.PrintToolResult(ck.Name, res);
                        return (ck.Id, res);
                    }).ToList();
                    var results = await Task.WhenAll(tasks);
                    foreach (var (id, res) in results)
                        _openaiMessages.Add(new ToolChatMessage(id, res));
                }
                else
                {
                    foreach (var ck in batch.Items)
                    {
                        if (!ck.Allowed)
                        {
                            _openaiMessages.Add(new ToolChatMessage(ck.Id, ck.FailMsg ?? "denied"));
                            continue;
                        }
                        var raw = await ExecuteToolCallAsync(ck.Name, ck.Input);
                        var res = PersistLargeResult(ck.Name, raw);
                        Ui.PrintToolResult(ck.Name, res);
                        if (_contextCleared)
                        {
                            _contextCleared = false;
                            _openaiMessages.Add(new UserChatMessage(res));
                            oaiContextBreak = true;
                            break;
                        }
                        _openaiMessages.Add(new ToolChatMessage(ck.Id, res));
                    }
                }
            }

            _contextCleared = false;
            await CheckAndCompactAsync();
        }
    }

    private class OpenAIStreamResult
    {
        public string Content { get; set; } = "";
        public List<(string Id, string Name, string Arguments)> ToolCalls { get; set; } = new();
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }

    private async Task<OpenAIStreamResult> CallOpenAIStreamAsync(CancellationToken ct)
    {
        return await WithRetryAsync(async (token) =>
        {
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 16384,
            };
            foreach (var t in ToOpenAITools(Tools.GetActiveToolDefinitions(_tools)))
                options.Tools.Add(t);

            // OpenAI 2.1 SDK has a known issue with some providers (Volcengine Ark,
            // DeepSeek-via-Ark, etc.) where streaming tool_call deltas may include
            // a null `function.arguments` field, which causes the SDK's internal
            // `new BinaryData(null)` to throw ArgumentNullException("bytes").
            // To stay compatible with OpenAI-compatible providers across the board,
            // we use the non-streaming path and emit text in one go.
            // (Pure OpenAI gpt-4o etc. work with streaming too — left as future toggle.)
            if (!_isSubAgent) Ui.StopSpinner();

            var resp = await _chatClient!.CompleteChatAsync(_openaiMessages, options, token);
            var completion = resp.Value;

            // Emit any text content
            var contentSb = new StringBuilder();
            foreach (var part in completion.Content)
            {
                if (!string.IsNullOrEmpty(part.Text)) contentSb.Append(part.Text);
            }
            var contentStr = contentSb.ToString();
            if (!string.IsNullOrEmpty(contentStr))
            {
                EmitText("\n");
                EmitText(contentStr);
            }

            // Collect tool calls
            var toolCalls = new List<(string Id, string Name, string Arguments)>();
            foreach (var tc in completion.ToolCalls)
            {
                var args = tc.FunctionArguments?.ToString() ?? "{}";
                if (string.IsNullOrEmpty(args)) args = "{}";
                toolCalls.Add((tc.Id ?? "", tc.FunctionName ?? "", args));
            }

            return new OpenAIStreamResult
            {
                Content = contentStr,
                ToolCalls = toolCalls,
                InputTokens = completion.Usage?.InputTokenCount ?? 0,
                OutputTokens = completion.Usage?.OutputTokenCount ?? 0,
            };
        }, ct);
    }

    // ─── Compact: OpenAI ─────────────────────────────────────

    private async Task CompactOpenAIAsync()
    {
        if (_openaiMessages.Count < 5) return;
        var systemMsg = _openaiMessages[0];
        var lastUser = _openaiMessages[^1];

        var msgs = new List<ChatMessage> { new SystemChatMessage("You are a conversation summarizer. Be concise but preserve important details.") };
        for (int i = 1; i < _openaiMessages.Count - 1; i++) msgs.Add(_openaiMessages[i]);
        msgs.Add(new UserChatMessage("Summarize the conversation so far in a concise paragraph, preserving key decisions, file paths, and context needed to continue the work."));

        string summaryText;
        try
        {
            var resp = await _chatClient!.CompleteChatAsync(msgs, new ChatCompletionOptions { MaxOutputTokenCount = 2048 });
            summaryText = resp.Value.Content.Count > 0 ? resp.Value.Content[0].Text : "No summary available.";
        }
        catch { summaryText = "No summary available."; }

        _openaiMessages.Clear();
        _openaiMessages.Add(systemMsg);
        _openaiMessages.Add(new UserChatMessage($"[Previous conversation summary]\n{summaryText}"));
        _openaiMessages.Add(new AssistantChatMessage("Understood. I have the context from our previous conversation. How can I continue helping?"));
        if (lastUser is UserChatMessage) _openaiMessages.Add(lastUser);
        _lastInputTokenCount = 0;
    }
}
