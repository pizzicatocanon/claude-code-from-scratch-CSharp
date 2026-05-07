// CLI entry — argument parsing, REPL, slash commands, Ctrl+C handling.
// Mirrors src/cli.ts.

using System.Text.Json.Nodes;

namespace MiniClaude;

internal class ParsedArgs
{
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Default;
    public string Model { get; set; } = "claude-opus-4-6";
    public string? ApiBase { get; set; }
    public string? Prompt { get; set; }
    public bool Resume { get; set; }
    public bool Thinking { get; set; }
    public double? MaxCost { get; set; }
    public int? MaxTurns { get; set; }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Force UTF-8 console I/O so Chinese / emoji output isn't garbled on Windows.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch { }

        var parsed = ParseArgs(args);
        if (parsed == null) return 0;

        // Resolve API config from env vars
        string? resolvedApiBase = parsed.ApiBase;
        string? resolvedApiKey = null;
        bool resolvedUseOpenAI = !string.IsNullOrEmpty(parsed.ApiBase);

        var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var openaiBase = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var anthropicBase = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");

        if (!string.IsNullOrEmpty(openaiKey) && !string.IsNullOrEmpty(openaiBase))
        {
            resolvedApiKey = openaiKey;
            resolvedApiBase ??= openaiBase;
            resolvedUseOpenAI = true;
        }
        else if (!string.IsNullOrEmpty(anthropicKey))
        {
            resolvedApiKey = anthropicKey;
            resolvedApiBase ??= anthropicBase;
            resolvedUseOpenAI = false;
        }
        else if (!string.IsNullOrEmpty(openaiKey))
        {
            resolvedApiKey = openaiKey;
            resolvedApiBase ??= openaiBase;
            resolvedUseOpenAI = true;
        }

        if (string.IsNullOrEmpty(resolvedApiKey) && !string.IsNullOrEmpty(parsed.ApiBase))
        {
            resolvedApiKey = openaiKey ?? anthropicKey;
            resolvedUseOpenAI = true;
        }

        if (string.IsNullOrEmpty(resolvedApiKey))
        {
            Ui.PrintError(
                "API key is required.\n" +
                "  Set ANTHROPIC_API_KEY (+ optional ANTHROPIC_BASE_URL) for Anthropic format,\n" +
                "  or OPENAI_API_KEY + OPENAI_BASE_URL for OpenAI-compatible format.");
            return 1;
        }

        var agent = new Agent(new AgentOptions
        {
            PermissionMode = parsed.PermissionMode,
            Model = parsed.Model,
            Thinking = parsed.Thinking,
            MaxCostUsd = parsed.MaxCost,
            MaxTurns = parsed.MaxTurns,
            ApiBase = resolvedUseOpenAI ? resolvedApiBase : null,
            AnthropicBaseURL = !resolvedUseOpenAI ? resolvedApiBase : null,
            ApiKey = resolvedApiKey,
        });

        if (parsed.Resume)
        {
            var sid = Session.GetLatestSessionId();
            if (sid != null)
            {
                var data = Session.LoadSession(sid);
                if (data != null) agent.RestoreSession(data.AnthropicMessages, data.OpenaiMessages);
                else Ui.PrintInfo("No session found to resume.");
            }
            else Ui.PrintInfo("No previous sessions found.");
        }

        if (!string.IsNullOrEmpty(parsed.Prompt))
        {
            try { await agent.ChatAsync(parsed.Prompt); }
            catch (Exception e) { Ui.PrintError(e.Message); return 1; }
            return 0;
        }

        await RunReplAsync(agent);
        return 0;
    }

    private static ParsedArgs? ParseArgs(string[] args)
    {
        var result = new ParsedArgs();
        var envModel = Environment.GetEnvironmentVariable("MINI_CLAUDE_MODEL");
        if (!string.IsNullOrEmpty(envModel)) result.Model = envModel;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--yolo":
                case "-y":
                    result.PermissionMode = PermissionMode.BypassPermissions; break;
                case "--plan":
                    result.PermissionMode = PermissionMode.Plan; break;
                case "--accept-edits":
                    result.PermissionMode = PermissionMode.AcceptEdits; break;
                case "--dont-ask":
                    result.PermissionMode = PermissionMode.DontAsk; break;
                case "--thinking":
                    result.Thinking = true; break;
                case "--model":
                case "-m":
                    if (i + 1 < args.Length) result.Model = args[++i]; break;
                case "--api-base":
                    if (i + 1 < args.Length) result.ApiBase = args[++i]; break;
                case "--resume":
                    result.Resume = true; break;
                case "--max-cost":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var mc)) result.MaxCost = mc; break;
                case "--max-turns":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var mt)) result.MaxTurns = mt; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return null;
                default:
                    positional.Add(args[i]); break;
            }
        }
        if (positional.Count > 0) result.Prompt = string.Join(" ", positional);
        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
Usage: mini-claude [options] [prompt]

Options:
  --yolo, -y          Skip all confirmation prompts (bypassPermissions mode)
  --plan              Plan mode: read-only, describe changes without executing
  --accept-edits      Auto-approve file edits, still confirm dangerous shell
  --dont-ask          Auto-deny anything needing confirmation (for CI)
  --thinking          Enable extended thinking (Anthropic only)
  --model, -m         Model to use (default: claude-opus-4-6, or MINI_CLAUDE_MODEL env)
  --api-base URL      Use OpenAI-compatible API endpoint (key via env var)
  --resume            Resume the last session
  --max-cost USD      Stop when estimated cost exceeds this amount
  --max-turns N       Stop after N agentic turns
  --help, -h          Show this help

REPL commands:
  /clear              Clear conversation history
  /plan               Toggle plan mode (read-only ↔ normal)
  /cost               Show token usage and cost
  /compact            Manually compact conversation
  /memory             List saved memories
  /skills             List available skills
  /<skill-name>       Invoke a skill (e.g. /commit ""fix types"")

Examples:
  mini-claude ""fix the bug in Program.cs""
  mini-claude --yolo ""run all tests and fix failures""
  mini-claude --plan ""how would you refactor this?""
  mini-claude --max-cost 0.50 --max-turns 20 ""implement feature X""
  mini-claude --resume
");
    }

    private static async Task RunReplAsync(Agent agent)
    {
        Ui.PrintWelcome();

        // Confirmation callback shares stdin
        agent.SetConfirmFn(message =>
        {
            Console.Write("  Allow? (y/n): ");
            var line = Console.ReadLine() ?? "";
            return Task.FromResult(line.Trim().ToLowerInvariant().StartsWith("y"));
        });

        // Plan approval callback
        agent.SetPlanApprovalFn(planContent =>
        {
            Ui.PrintPlanForApproval(planContent);
            Ui.PrintPlanApprovalOptions();
            while (true)
            {
                Console.Write("  Enter choice (1-4): ");
                var ans = (Console.ReadLine() ?? "").Trim();
                switch (ans)
                {
                    case "1": return Task.FromResult(new PlanApprovalResult { Choice = "clear-and-execute" });
                    case "2": return Task.FromResult(new PlanApprovalResult { Choice = "execute" });
                    case "3": return Task.FromResult(new PlanApprovalResult { Choice = "manual-execute" });
                    case "4":
                        Console.Write("  Feedback (what to change): ");
                        var fb = (Console.ReadLine() ?? "").Trim();
                        return Task.FromResult(new PlanApprovalResult
                        {
                            Choice = "keep-planning",
                            Feedback = string.IsNullOrEmpty(fb) ? null : fb,
                        });
                    default:
                        Console.WriteLine("  Invalid choice. Enter 1, 2, 3, or 4.");
                        break;
                }
            }
        });

        // Ctrl+C handling
        int sigintCount = 0;
        Console.CancelKeyPress += (sender, e) =>
        {
            if (agent.IsProcessing)
            {
                e.Cancel = true;
                agent.Abort();
                Console.WriteLine("\n  (interrupted)");
                sigintCount = 0;
                Ui.PrintUserPrompt();
            }
            else
            {
                sigintCount++;
                if (sigintCount >= 2)
                {
                    Console.WriteLine("\nBye!\n");
                    Environment.Exit(0);
                }
                e.Cancel = true;
                Console.WriteLine("\n  Press Ctrl+C again to exit.");
                Ui.PrintUserPrompt();
            }
        };

        while (true)
        {
            Ui.PrintUserPrompt();
            var input = (Console.ReadLine() ?? "").Trim();
            sigintCount = 0;

            if (string.IsNullOrEmpty(input)) continue;
            if (input == "exit" || input == "quit")
            {
                Console.WriteLine("\nBye!\n");
                return;
            }

            // Slash commands
            if (input == "/clear") { agent.ClearHistory(); continue; }
            if (input == "/plan") { agent.TogglePlanMode(); continue; }
            if (input == "/cost") { agent.ShowCost(); continue; }
            if (input == "/compact")
            {
                try { await agent.CompactAsync(); }
                catch (Exception e) { Ui.PrintError(e.Message); }
                continue;
            }
            if (input == "/memory")
            {
                var memories = Memory.ListMemories();
                if (memories.Count == 0) Ui.PrintInfo("No memories saved yet.");
                else
                {
                    Ui.PrintInfo($"{memories.Count} memories:");
                    foreach (var m in memories)
                        Console.WriteLine($"    [{m.Type}] {m.Name} — {m.Description}");
                }
                continue;
            }
            if (input == "/skills")
            {
                var skills = Skills.DiscoverSkills();
                if (skills.Count == 0)
                    Ui.PrintInfo("No skills found. Add skills to .claude/skills/<name>/SKILL.md");
                else
                {
                    Ui.PrintInfo($"{skills.Count} skills:");
                    foreach (var s in skills)
                    {
                        var tag = s.UserInvocable ? $"/{s.Name}" : s.Name;
                        Console.WriteLine($"    {tag} ({s.Source}) — {s.Description}");
                    }
                }
                continue;
            }

            // Skill invocation: /<skill-name> [args]
            if (input.StartsWith("/"))
            {
                int spaceIdx = input.IndexOf(' ');
                var cmdName = spaceIdx > 0 ? input.Substring(1, spaceIdx - 1) : input.Substring(1);
                var cmdArgs = spaceIdx > 0 ? input.Substring(spaceIdx + 1) : "";
                var skill = Skills.GetSkillByName(cmdName);
                if (skill != null && skill.UserInvocable)
                {
                    Ui.PrintInfo($"Invoking skill: {skill.Name}");
                    try
                    {
                        if (skill.Context == "fork")
                        {
                            await agent.ChatAsync($"Use the skill tool to invoke \"{skill.Name}\" with args: {(string.IsNullOrEmpty(cmdArgs) ? "(none)" : cmdArgs)}");
                        }
                        else
                        {
                            var resolved = Skills.ResolveSkillPrompt(skill, cmdArgs);
                            await agent.ChatAsync(resolved);
                        }
                    }
                    catch (Exception e)
                    {
                        if (!(e is OperationCanceledException))
                            Ui.PrintError(e.Message);
                    }
                    continue;
                }
                // Unknown command — fall through to regular input
            }

            try { await agent.ChatAsync(input); }
            catch (OperationCanceledException) { /* handled by SIGINT */ }
            catch (Exception e) { Ui.PrintError(e.Message); }
        }
    }
}
