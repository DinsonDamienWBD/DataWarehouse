// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using DataWarehouse.Shared.Commands;
using DataWarehouse.Shared.Services;
using Spectre.Console;

namespace DataWarehouse.CLI;

/// <summary>
/// Interactive TUI mode for the DataWarehouse CLI using Spectre.Console.
/// Provides a REPL-like interface with command history, autocomplete, rich output,
/// conversational context, and AI-powered features.
/// </summary>
public sealed class InteractiveMode
{
    private readonly CommandExecutor _executor;
    private readonly ConsoleRenderer _renderer = new();
    private readonly NaturalLanguageProcessor _nlp;
    private readonly IAIProviderRegistry? _aiRegistry;
    private readonly CommandHistory _history;
    private readonly CancellationTokenSource _cts = new();

    private OutputFormat _outputFormat = OutputFormat.Table;
    private bool _verbose = false;
    private bool _conversationalMode = true;
    private string? _sessionId;

    /// <summary>
    /// Creates a new InteractiveMode instance.
    /// </summary>
    public InteractiveMode(
        CommandExecutor executor,
        CommandHistory history,
        NaturalLanguageProcessor? nlp = null,
        IAIProviderRegistry? aiRegistry = null)
    {
        _executor = executor;
        _history = history;
        _nlp = nlp ?? new NaturalLanguageProcessor(aiRegistry);
        _aiRegistry = aiRegistry;
        _sessionId = Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>
    /// Runs the interactive mode loop.
    /// </summary>
    public async Task RunAsync()
    {
        AnsiConsole.Clear();
        PrintBanner();
        PrintHelp();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var input = ReadInput();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

                // Handle built-in commands
                if (await HandleBuiltInCommandAsync(input))
                    continue;

                // Execute the command
                await ExecuteCommandAsync(input);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            }
        }

        AnsiConsole.MarkupLine("\n[grey]Goodbye![/]");
    }

    private void PrintBanner()
    {
        AnsiConsole.Write(new FigletText("DataWarehouse").Color(Color.Blue));
        AnsiConsole.MarkupLine("[grey]Interactive CLI Mode[/]");

        // Show AI/conversational status
        var aiStatus = _aiRegistry != null ? "[green]AI Enabled[/]" : "[yellow]AI Not Available[/]";
        var convStatus = _conversationalMode ? "[green]Conversational[/]" : "[grey]Single-turn[/]";
        AnsiConsole.MarkupLine($"[dim]Status: {aiStatus} | {convStatus} | Session: {_sessionId}[/]\n");
    }

    private void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        AnsiConsole.MarkupLine("  [cyan]help[/]              - Show this help");
        AnsiConsole.MarkupLine("  [cyan]exit[/], [cyan]quit[/]        - Exit interactive mode");
        AnsiConsole.MarkupLine("  [cyan]history[/]           - Show command history");
        AnsiConsole.MarkupLine("  [cyan]clear[/]             - Clear screen");
        AnsiConsole.MarkupLine("  [cyan]format <fmt>[/]      - Set output format (table, json, yaml, csv)");
        AnsiConsole.MarkupLine("  [cyan]verbose[/]           - Toggle verbose mode");
        AnsiConsole.MarkupLine("  [cyan]conversational[/]    - Toggle conversational mode");
        AnsiConsole.MarkupLine("  [cyan]new session[/]       - Start new conversation session");
        AnsiConsole.MarkupLine("  [cyan]ask <question>[/]    - Ask AI for help");
        AnsiConsole.MarkupLine("  [cyan]correct[/]           - Correct the last interpretation");
        AnsiConsole.MarkupLine("  [cyan]learning stats[/]    - Show learning statistics");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Natural Language:[/]");
        AnsiConsole.MarkupLine("  Type naturally: \"[cyan]show my storage pools[/]\" or \"[cyan]backup my database[/]\"");
        AnsiConsole.MarkupLine("  Follow-ups work: \"[cyan]filter by last week[/]\" or \"[cyan]delete that one[/]\"");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[grey]Press Ctrl+C to exit[/]\n");
    }

    private string ReadInput()
    {
        // Show prompt with connection and session status
        var connectionStatus = _executor.AllCommands.Count > 0 ? "blue" : "yellow";
        var modeIndicator = _conversationalMode ? $"[dim]({_sessionId})[/]" : "";
        var prompt = $"[{connectionStatus}]dw[/] {modeIndicator}[grey]>[/] ";

        return AnsiConsole.Prompt(
            new TextPrompt<string>(prompt)
                .AllowEmpty());
    }

    private async Task<bool> HandleBuiltInCommandAsync(string input)
    {
        var command = input.Trim().ToLowerInvariant();
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return true;

        switch (parts[0])
        {
            case "exit":
            case "quit":
            case "q":
                _cts.Cancel();
                return true;

            case "help":
            case "?":
                if (parts.Length > 1)
                {
                    await ShowCommandHelpAsync(parts[1]);
                }
                else
                {
                    PrintHelp();
                    ShowAvailableCommands();
                }
                return true;

            case "history":
                ShowHistory(parts.Length > 1 && int.TryParse(parts[1], out var count) ? count : 10);
                return true;

            case "clear":
            case "cls":
                AnsiConsole.Clear();
                return true;

            case "format":
                if (parts.Length > 1)
                {
                    SetOutputFormat(parts[1]);
                }
                else
                {
                    AnsiConsole.MarkupLine($"[grey]Current format:[/] [cyan]{_outputFormat}[/]");
                }
                return true;

            case "verbose":
                _verbose = !_verbose;
                AnsiConsole.MarkupLine($"[grey]Verbose mode:[/] [cyan]{(_verbose ? "enabled" : "disabled")}[/]");
                return true;

            case "conversational":
                _conversationalMode = !_conversationalMode;
                AnsiConsole.MarkupLine($"[grey]Conversational mode:[/] [cyan]{(_conversationalMode ? "enabled" : "disabled")}[/]");
                if (_conversationalMode && string.IsNullOrEmpty(_sessionId))
                {
                    _sessionId = Guid.NewGuid().ToString("N")[..12];
                    AnsiConsole.MarkupLine($"[grey]New session started:[/] [cyan]{_sessionId}[/]");
                }
                return true;

            case "new":
                if (parts.Length > 1 && parts[1] == "session")
                {
                    if (!string.IsNullOrEmpty(_sessionId))
                    {
                        _nlp.EndSession(_sessionId);
                    }
                    _sessionId = Guid.NewGuid().ToString("N")[..12];
                    AnsiConsole.MarkupLine($"[green]New session started:[/] [cyan]{_sessionId}[/]");
                    return true;
                }
                break;

            case "ask":
                if (parts.Length > 1)
                {
                    await HandleAIHelpAsync(string.Join(" ", parts.Skip(1)));
                    return true;
                }
                AnsiConsole.MarkupLine("[yellow]Usage: ask <your question>[/]");
                return true;

            case "correct":
                await HandleCorrectionAsync();
                return true;

            case "learning":
                if (parts.Length > 1 && parts[1] == "stats")
                {
                    ShowLearningStats();
                    return true;
                }
                break;

            case "search":
                if (parts.Length > 1)
                {
                    SearchHistory(string.Join(" ", parts.Skip(1)));
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]Usage: search <query>[/]");
                }
                return true;

            case "forget":
            case "reset":
                if (!string.IsNullOrEmpty(_sessionId))
                {
                    _nlp.ClearSessionContext(_sessionId);
                    AnsiConsole.MarkupLine("[green]Context cleared for current session.[/]");
                }
                return true;

            default:
                return false;
        }

        return false;
    }

    private async Task HandleAIHelpAsync(string query)
    {
        var result = await _nlp.GetAIHelpAsync(query, _cts.Token);

        if (result.UsedAI)
        {
            AnsiConsole.MarkupLine("[cyan]AI-Powered Help:[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Help (based on pattern matching):[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(result.Answer);

        if (result.SuggestedCommands.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Suggested Commands:[/]");
            foreach (var cmd in result.SuggestedCommands)
            {
                AnsiConsole.MarkupLine($"  [cyan]{cmd}[/]");
            }
        }

        if (result.Examples.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Examples:[/]");
            foreach (var example in result.Examples)
            {
                AnsiConsole.MarkupLine($"  [green]{example}[/]");
            }
        }
    }

    private async Task HandleCorrectionAsync()
    {
        var recent = _history.GetRecent(1).FirstOrDefault();
        if (recent == null)
        {
            AnsiConsole.MarkupLine("[yellow]No recent command to correct.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Last command interpreted as:[/] [cyan]{recent.Command}[/]");
        AnsiConsole.MarkupLine("[grey]What should it have been?[/]");

        var correctCommand = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Correct command:[/] ")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(correctCommand))
        {
            AnsiConsole.MarkupLine("[yellow]Correction cancelled.[/]");
            return;
        }

        // Record the correction
        _nlp.RecordCorrection(
            recent.Command,
            recent.Command,
            correctCommand,
            recent.Parameters);

        AnsiConsole.MarkupLine($"[green]Learned: '{recent.Command}' should be interpreted as '{correctCommand}'[/]");
    }

    private void ShowLearningStats()
    {
        var stats = _nlp.GetLearningStats();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Total Patterns Learned", stats.TotalPatterns.ToString());
        table.AddRow("Corrections Applied", stats.CorrectionsLearned.ToString());
        table.AddRow("Total Successes", stats.TotalSuccesses.ToString());
        table.AddRow("Total Failures", stats.TotalFailures.ToString());
        table.AddRow("Average Confidence", $"{stats.AverageConfidence:P1}");
        table.AddRow("Synonyms Configured", stats.SynonymCount.ToString());
        table.AddRow("User Preferences", stats.PreferenceCount.ToString());

        if (stats.OldestPattern.HasValue)
        {
            table.AddRow("Learning Since", stats.OldestPattern.Value.ToString("yyyy-MM-dd"));
        }

        if (!string.IsNullOrEmpty(stats.MostUsedPattern))
        {
            table.AddRow("Most Used Pattern", stats.MostUsedPattern);
        }

        AnsiConsole.Write(table);
    }

    private async Task ExecuteCommandAsync(string input)
    {
        var startTime = DateTime.UtcNow;
        CommandIntent intent;

        // Use conversational processing if enabled
        if (_conversationalMode && !string.IsNullOrEmpty(_sessionId))
        {
            intent = await _nlp.ProcessConversationalAsync(input, _sessionId, _cts.Token);
        }
        else if (_aiRegistry != null)
        {
            intent = await _nlp.ProcessWithAIFallbackAsync(input, _cts.Token);
        }
        else
        {
            intent = _nlp.Process(input);
        }

        // Handle special CLI commands
        if (intent.CommandName == "cli.context.clear")
        {
            AnsiConsole.MarkupLine("[green]Conversation context cleared.[/]");
            return;
        }

        // Try as direct command if natural language failed
        if (intent.Confidence < 0.3)
        {
            var directParts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (directParts.Length > 0)
            {
                var directCommand = _executor.Resolve(directParts[0]);
                if (directCommand != null)
                {
                    intent = new CommandIntent
                    {
                        CommandName = directParts[0],
                        Parameters = ParseDirectParameters(directParts.Length > 1 ? directParts[1] : ""),
                        OriginalInput = input,
                        Confidence = 1.0
                    };
                }
            }
        }

        if (intent.Confidence < 0.3)
        {
            AnsiConsole.MarkupLine($"[yellow]Could not understand:[/] {input}");
            AnsiConsole.MarkupLine("[grey]Try 'help' for available commands or 'ask <question>' for AI help[/]");

            // Show suggestions
            var suggestions = _nlp.GetCompletions(input).Take(3).ToList();
            if (suggestions.Count > 0)
            {
                AnsiConsole.MarkupLine("[grey]Did you mean:[/]");
                foreach (var suggestion in suggestions)
                {
                    AnsiConsole.MarkupLine($"  [cyan]{suggestion}[/]");
                }
            }
            return;
        }

        // Show interpretation with indicators
        var indicators = new List<string>();
        if (intent.ProcessedByAI) indicators.Add("[dim](AI)[/]");
        if (!string.IsNullOrEmpty(intent.SessionId)) indicators.Add($"[dim](session)[/]");
        var indicatorStr = indicators.Count > 0 ? " " + string.Join(" ", indicators) : "";

        if (_verbose || intent.Confidence < 0.8)
        {
            AnsiConsole.MarkupLine($"[grey]{intent.Explanation}{indicatorStr}[/]");
        }

        // Execute the command
        intent.Parameters["verbose"] = _verbose;
        intent.Parameters["format"] = _outputFormat;

        var result = await ConsoleRenderer.WithStatusAsync("Executing...", async () =>
            await _executor.ExecuteAsync(intent.CommandName, intent.Parameters, _cts.Token));

        _renderer.Render(result, _outputFormat);

        // Add to history
        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        _history.Add(intent.CommandName, intent.Parameters, result.Success, duration);

        AnsiConsole.WriteLine();
    }

    private async Task ShowCommandHelpAsync(string commandName)
    {
        var result = await _executor.ExecuteAsync("help", new Dictionary<string, object?> { ["command"] = commandName });
        _renderer.Render(result, _outputFormat);
    }

    private void ShowAvailableCommands()
    {
        AnsiConsole.MarkupLine("\n[bold]Available Commands:[/]");

        var categories = _executor.GetCommandsByCategory();

        foreach (var (category, commands) in categories)
        {
            AnsiConsole.MarkupLine($"\n  [yellow]{category}[/]");

            foreach (var cmd in commands.Take(5))
            {
                AnsiConsole.MarkupLine($"    [cyan]{cmd.Name,-25}[/] {cmd.Description}");
            }

            if (commands.Count > 5)
            {
                AnsiConsole.MarkupLine($"    [grey]... and {commands.Count - 5} more[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    private void ShowHistory(int count)
    {
        var recent = _history.GetRecent(count).ToList();

        if (recent.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No command history.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("Command");
        table.AddColumn("Time");
        table.AddColumn("Status");

        var index = 1;
        foreach (var entry in recent)
        {
            var status = entry.Success ? "[green]OK[/]" : "[red]FAIL[/]";
            var time = entry.ExecutedAt.ToLocalTime().ToString("HH:mm:ss");
            table.AddRow(index.ToString(), entry.Command, time, status);
            index++;
        }

        AnsiConsole.Write(table);
    }

    private void SearchHistory(string query)
    {
        var results = _history.Search(query, 10).ToList();

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No history entries matching '{query}'[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Found {results.Count} matching entries:[/]");

        foreach (var entry in results)
        {
            var time = entry.ExecutedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            AnsiConsole.MarkupLine($"  [{(entry.Success ? "green" : "red")}]{entry.Command}[/] [grey]({time})[/]");
        }
    }

    private void SetOutputFormat(string format)
    {
        _outputFormat = format.ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "yaml" => OutputFormat.Yaml,
            "csv" => OutputFormat.Csv,
            "table" => OutputFormat.Table,
            _ => _outputFormat
        };

        AnsiConsole.MarkupLine($"[grey]Output format set to:[/] [cyan]{_outputFormat}[/]");
    }

    private static Dictionary<string, object?> ParseDirectParameters(string paramsStr)
    {
        var parameters = new Dictionary<string, object?>();

        if (string.IsNullOrWhiteSpace(paramsStr))
            return parameters;

        // Simple key=value or --key value parsing
        var parts = paramsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part.StartsWith("--"))
            {
                var key = part[2..];
                // Check if next part is the value
                if (i + 1 < parts.Length && !parts[i + 1].StartsWith("--"))
                {
                    parameters[key] = parts[++i];
                }
                else
                {
                    parameters[key] = true;
                }
            }
            else if (part.Contains('='))
            {
                var kv = part.Split('=', 2);
                parameters[kv[0]] = kv.Length > 1 ? kv[1] : true;
            }
            else
            {
                // Positional argument - use index as key
                parameters[$"arg{i}"] = part;
            }
        }

        return parameters;
    }
}
