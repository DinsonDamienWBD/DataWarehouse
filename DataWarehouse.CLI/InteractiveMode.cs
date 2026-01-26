// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Commands;
using DataWarehouse.Shared.Services;
using Spectre.Console;

namespace DataWarehouse.CLI;

/// <summary>
/// Interactive TUI mode for the DataWarehouse CLI using Spectre.Console.
/// Provides a REPL-like interface with command history, autocomplete, and rich output.
/// </summary>
public sealed class InteractiveMode
{
    private readonly CommandExecutor _executor;
    private readonly ConsoleRenderer _renderer = new();
    private readonly NaturalLanguageProcessor _nlp = new();
    private readonly CommandHistory _history;
    private readonly CancellationTokenSource _cts = new();

    private OutputFormat _outputFormat = OutputFormat.Table;
    private bool _verbose = false;

    /// <summary>
    /// Creates a new InteractiveMode instance.
    /// </summary>
    public InteractiveMode(CommandExecutor executor, CommandHistory history)
    {
        _executor = executor;
        _history = history;
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
        AnsiConsole.MarkupLine("[grey]Interactive CLI Mode[/]\n");
    }

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        AnsiConsole.MarkupLine("  [cyan]help[/]              - Show this help");
        AnsiConsole.MarkupLine("  [cyan]exit[/], [cyan]quit[/]        - Exit interactive mode");
        AnsiConsole.MarkupLine("  [cyan]history[/]           - Show command history");
        AnsiConsole.MarkupLine("  [cyan]clear[/]             - Clear screen");
        AnsiConsole.MarkupLine("  [cyan]format <fmt>[/]      - Set output format (table, json, yaml, csv)");
        AnsiConsole.MarkupLine("  [cyan]verbose[/]           - Toggle verbose mode");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Natural Language:[/]");
        AnsiConsole.MarkupLine("  Type naturally: \"[cyan]show my storage pools[/]\" or \"[cyan]backup my database[/]\"");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[grey]Press Ctrl+C to exit[/]\n");
    }

    private string ReadInput()
    {
        // Show prompt with connection status
        var prompt = _executor.AllCommands.Count > 0
            ? "[blue]dw[/] [grey]>[/] "
            : "[yellow]dw[/] [grey](disconnected) >[/] ";

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

            default:
                return false;
        }
    }

    private async Task ExecuteCommandAsync(string input)
    {
        var startTime = DateTime.UtcNow;

        // Try to parse as natural language first
        var intent = _nlp.Process(input);

        if (intent.Confidence < 0.3)
        {
            // Try as direct command (e.g., "storage.list")
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
            AnsiConsole.MarkupLine("[grey]Try 'help' for available commands[/]");

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

        // Show interpretation if verbose or low confidence
        if (_verbose || intent.Confidence < 0.8)
        {
            AnsiConsole.MarkupLine($"[grey]{intent.Explanation}[/]");
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
