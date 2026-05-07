// UI helpers — colored terminal output, tool icons, spinner.
// Mirrors src/ui.ts.

using Spectre.Console;

namespace MiniClaude;

public static class Ui
{
    private static readonly object ConsoleLock = new();
    private static System.Threading.CancellationTokenSource? _spinnerCts;
    private static Task? _spinnerTask;

    private static readonly Dictionary<string, string> ToolIcons = new()
    {
        ["read_file"] = "📖",
        ["write_file"] = "📝",
        ["edit_file"] = "✏️",
        ["list_files"] = "📂",
        ["grep_search"] = "🔍",
        ["run_shell"] = "⚡",
        ["web_fetch"] = "🌐",
        ["agent"] = "🤖",
        ["skill"] = "🎯",
        ["enter_plan_mode"] = "📋",
        ["exit_plan_mode"] = "✅",
        ["tool_search"] = "🔎",
    };

    public static void PrintWelcome()
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine("[bold cyan]Mini Claude (.NET)[/] — type 'exit' to quit, /help for commands\n");
        }
    }

    public static void PrintUserPrompt()
    {
        lock (ConsoleLock)
        {
            AnsiConsole.Markup("[bold green]>[/] ");
        }
    }

    public static void PrintAssistantText(string text)
    {
        lock (ConsoleLock)
        {
            Console.Write(text);
        }
    }

    public static void PrintToolCall(string name, IReadOnlyDictionary<string, object?> input)
    {
        lock (ConsoleLock)
        {
            var icon = ToolIcons.TryGetValue(name, out var i) ? i : "🔧";
            var summary = SummarizeInput(name, input);
            AnsiConsole.MarkupLine($"\n  {icon} [yellow]{Markup.Escape(name)}[/] {Markup.Escape(summary)}");
        }
    }

    public static void PrintToolResult(string name, string result)
    {
        lock (ConsoleLock)
        {
            var lines = result.Split('\n');
            var preview = string.Join("\n", lines.Take(10));
            if (lines.Length > 10) preview += $"\n  ... ({lines.Length} lines total)";
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(preview)}[/]");
        }
    }

    public static void PrintError(string message)
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine($"\n  [red]Error:[/] {Markup.Escape(message)}");
        }
    }

    public static void PrintInfo(string message)
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(message)}[/]");
        }
    }

    public static void PrintConfirmation(string command)
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine($"\n  [yellow]Confirm:[/] {Markup.Escape(command)}");
        }
    }

    public static void PrintDivider()
    {
        lock (ConsoleLock)
        {
            AnsiConsole.WriteLine();
        }
    }

    public static void PrintCost(long inputTokens, long outputTokens)
    {
        var costIn = (inputTokens / 1_000_000.0) * 3;
        var costOut = (outputTokens / 1_000_000.0) * 15;
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine($"\n  [dim]Tokens: {inputTokens} in / {outputTokens} out | ${(costIn + costOut):F4}[/]");
        }
    }

    public static void PrintRetry(int attempt, int max, string reason)
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine($"  [yellow]Retry {attempt}/{max} ({Markup.Escape(reason)})[/]");
        }
    }

    public static void PrintSubAgentStart(string type, string description)
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine($"\n  [magenta]── sub-agent[{Markup.Escape(type)}] start: {Markup.Escape(description)} ──[/]");
        }
    }

    public static void PrintSubAgentEnd(string type, string description)
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine($"  [magenta]── sub-agent[{Markup.Escape(type)}] end ──[/]\n");
        }
    }

    public static void PrintPlanForApproval(string planContent)
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine("\n  [bold cyan]── Plan ready for approval ──[/]");
            AnsiConsole.WriteLine(planContent);
        }
    }

    public static void PrintPlanApprovalOptions()
    {
        lock (ConsoleLock)
        {
            AnsiConsole.MarkupLine("\n  Options:");
            AnsiConsole.MarkupLine("    1. Clear context and execute");
            AnsiConsole.MarkupLine("    2. Execute with current context");
            AnsiConsole.MarkupLine("    3. Manual execute (exit plan mode, keep context)");
            AnsiConsole.MarkupLine("    4. Keep planning (provide feedback)");
        }
    }

    public static void StartSpinner()
    {
        lock (ConsoleLock)
        {
            if (_spinnerCts != null) return;
            _spinnerCts = new System.Threading.CancellationTokenSource();
            var token = _spinnerCts.Token;
            var frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
            _spinnerTask = Task.Run(async () =>
            {
                int idx = 0;
                while (!token.IsCancellationRequested)
                {
                    lock (ConsoleLock)
                    {
                        Console.Write($"\r  {frames[idx]} thinking...");
                    }
                    idx = (idx + 1) % frames.Length;
                    try { await Task.Delay(80, token); } catch { }
                }
                lock (ConsoleLock)
                {
                    Console.Write("\r" + new string(' ', 40) + "\r");
                }
            }, token);
        }
    }

    public static void StopSpinner()
    {
        System.Threading.CancellationTokenSource? cts;
        Task? task;
        lock (ConsoleLock)
        {
            cts = _spinnerCts;
            task = _spinnerTask;
            _spinnerCts = null;
            _spinnerTask = null;
        }
        if (cts != null)
        {
            cts.Cancel();
            try { task?.Wait(500); } catch { }
            cts.Dispose();
        }
    }

    private static string SummarizeInput(string name, IReadOnlyDictionary<string, object?> input)
    {
        if (input.TryGetValue("file_path", out var fp) && fp != null) return fp.ToString() ?? "";
        if (input.TryGetValue("path", out var p) && p != null) return p.ToString() ?? "";
        if (input.TryGetValue("pattern", out var pt) && pt != null) return pt.ToString() ?? "";
        if (input.TryGetValue("command", out var c) && c != null)
        {
            var s = c.ToString() ?? "";
            return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
        }
        if (input.TryGetValue("url", out var u) && u != null) return u.ToString() ?? "";
        if (input.TryGetValue("description", out var d) && d != null) return d.ToString() ?? "";
        if (input.TryGetValue("query", out var q) && q != null) return q.ToString() ?? "";
        if (input.TryGetValue("skill_name", out var sn) && sn != null) return sn.ToString() ?? "";
        return "";
    }
}
