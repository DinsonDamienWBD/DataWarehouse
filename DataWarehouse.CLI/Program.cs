// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.CommandLine.Parsing;
using DataWarehouse.CLI.ShellCompletions;
using DataWarehouse.SDK.AI;
using DataWarehouse.Shared;
using DataWarehouse.Shared.Commands;
using DataWarehouse.Shared.Services;
using Spectre.Console;

namespace DataWarehouse.CLI;

/// <summary>
/// DataWarehouse CLI - Production-ready command-line interface for DataWarehouse management.
/// This is a THIN WRAPPER that delegates all business logic to DataWarehouse.Shared.
/// Supports AI-powered natural language processing and conversational context.
/// </summary>
public static class Program
{
    private static InstanceManager? _instanceManager;
    private static CapabilityManager? _capabilityManager;
    private static CommandExecutor? _executor;
    private static ConsoleRenderer _renderer = new();
    private static NaturalLanguageProcessor? _nlp;
    private static CommandHistory? _history;
    private static IAIProviderRegistry? _aiRegistry;
    private static string? _sessionId;

    /// <summary>
    /// Main entry point for the DataWarehouse CLI.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        // Initialize shared services
        _instanceManager = new InstanceManager();
        _capabilityManager = new CapabilityManager(_instanceManager);
        _executor = new CommandExecutor(_instanceManager, _capabilityManager);
        _history = new CommandHistory();

        // Try to get AI provider registry from environment or default
        _aiRegistry = TryGetAIRegistry();

        // Initialize NLP with AI support if available
        var learningStorePath = GetLearningStorePath();
        _nlp = new NaturalLanguageProcessor(_aiRegistry, learningStorePath);

        // Check for conversational mode flag
        var conversational = args.Contains("--conversational") || args.Contains("-c");
        if (conversational)
        {
            args = args.Where(a => a != "--conversational" && a != "-c").ToArray();
            _sessionId = Guid.NewGuid().ToString("N")[..12];
        }

        // Check for AI help query
        if (args.Length >= 2 && (args[0] == "ai-help" || args[0] == "ask"))
        {
            return await HandleAIHelpAsync(string.Join(" ", args.Skip(1)));
        }

        // Check for natural language mode (quoted argument)
        if (args.Length == 1 && !args[0].StartsWith('-') && !args[0].Contains('.') && args[0].Contains(' '))
        {
            return await HandleNaturalLanguageAsync(args[0], conversational);
        }

        // Check for interactive mode
        if (args.Length == 0 || (args.Length == 1 && args[0] == "interactive"))
        {
            return await RunInteractiveModeAsync();
        }

        // Build the command-line parser and invoke
        var rootCommand = BuildRootCommand();
        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    /// <summary>
    /// Tries to initialize the AI provider registry if configured.
    /// </summary>
    private static IAIProviderRegistry? TryGetAIRegistry()
    {
        // Check for environment variables that indicate AI is configured
        var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrEmpty(openaiKey) && string.IsNullOrEmpty(anthropicKey))
        {
            return null;
        }

        // In a real implementation, this would create a registry with configured providers
        // For now, we return null if no specific provider configuration is available
        // The actual AI providers are registered through the plugin system
        return null;
    }

    /// <summary>
    /// Gets the path for the learning store persistence file.
    /// </summary>
    private static string? GetLearningStorePath()
    {
        var dataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(dataPath))
        {
            return null;
        }

        var dwPath = Path.Combine(dataPath, "DataWarehouse", "CLI");
        return Path.Combine(dwPath, "learning.json");
    }

    /// <summary>
    /// Handles AI-powered help queries.
    /// </summary>
    private static async Task<int> HandleAIHelpAsync(string query)
    {
        var result = await _nlp!.GetAIHelpAsync(query);

        if (result.UsedAI)
        {
            AnsiConsole.MarkupLine("[cyan]AI-Powered Help:[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Help (AI not available):[/]");
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

        return 0;
    }

    /// <summary>
    /// Handles natural language input like: dw "backup my database"
    /// </summary>
    private static async Task<int> HandleNaturalLanguageAsync(string input, bool conversational = false)
    {
        CommandIntent intent;

        if (conversational && !string.IsNullOrEmpty(_sessionId))
        {
            // Use conversational processing with context
            intent = await _nlp!.ProcessConversationalAsync(input, _sessionId);
        }
        else if (_aiRegistry != null)
        {
            // Use AI fallback if available
            intent = await _nlp!.ProcessWithAIFallbackAsync(input);
        }
        else
        {
            // Fallback to pattern matching only
            intent = _nlp!.Process(input);
        }

        // Handle special CLI commands
        if (intent.CommandName == "cli.context.clear")
        {
            AnsiConsole.MarkupLine("[green]Conversation context cleared.[/]");
            return 0;
        }

        if (intent.Confidence < 0.3)
        {
            AnsiConsole.MarkupLine($"[yellow]Could not understand:[/] {input}");
            AnsiConsole.MarkupLine("[grey]Try 'dw help' for available commands[/]");
            AnsiConsole.MarkupLine("[grey]Or use 'dw ask <question>' for AI-powered help[/]");

            var suggestions = _nlp.GetCompletions(input).Take(3).ToList();
            if (suggestions.Count > 0)
            {
                AnsiConsole.MarkupLine("[grey]Did you mean:[/]");
                foreach (var suggestion in suggestions)
                {
                    AnsiConsole.MarkupLine($"  [cyan]{suggestion}[/]");
                }
            }
            return 1;
        }

        // Show interpretation with AI indicator if applicable
        var aiIndicator = intent.ProcessedByAI ? " [dim](AI)[/]" : "";
        var sessionIndicator = !string.IsNullOrEmpty(intent.SessionId) ? $" [dim](session: {intent.SessionId})[/]" : "";
        AnsiConsole.MarkupLine($"[grey]{intent.Explanation}{aiIndicator}{sessionIndicator}[/]");

        var result = await _executor!.ExecuteAsync(intent.CommandName, intent.Parameters);
        _renderer.Render(result);

        _history?.Add(intent.CommandName, intent.Parameters, result.Success);

        // Record success for learning
        if (result.Success)
        {
            // Learning is handled automatically by NaturalLanguageProcessor
        }

        return result.ExitCode;
    }

    /// <summary>
    /// Runs the interactive TUI mode.
    /// </summary>
    private static async Task<int> RunInteractiveModeAsync()
    {
        var interactive = new InteractiveMode(_executor!, _history!, _nlp!, _aiRegistry);
        await interactive.RunAsync();
        return 0;
    }

    /// <summary>
    /// Builds the root command with all subcommands.
    /// </summary>
    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("DataWarehouse CLI - Command-line interface for DataWarehouse management");

        // Global options
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Enable verbose output" };

        var configOption = new Option<string?>("--config", "-c") { Description = "Path to configuration file" };

        var formatOption = new Option<OutputFormat>("--format", "-f") { Description = "Output format (table, json, yaml, csv)", DefaultValueFactory = _ => OutputFormat.Table };

        var instanceOption = new Option<string?>("--instance", "-i") { Description = "Saved instance profile to use" };

        rootCommand.Options.Add(verboseOption);
        rootCommand.Options.Add(configOption);
        rootCommand.Options.Add(formatOption);
        rootCommand.Options.Add(instanceOption);

        // Add command groups
        rootCommand.Subcommands.Add(CreateStorageCommand(formatOption));
        rootCommand.Subcommands.Add(CreateBackupCommand(formatOption));
        rootCommand.Subcommands.Add(CreatePluginCommand(formatOption));
        rootCommand.Subcommands.Add(CreateHealthCommand(formatOption));
        rootCommand.Subcommands.Add(CreateConfigCommand(formatOption));
        rootCommand.Subcommands.Add(CreateRaidCommand(formatOption));
        rootCommand.Subcommands.Add(CreateAuditCommand(formatOption));
        rootCommand.Subcommands.Add(CreateServerCommand(formatOption));
        rootCommand.Subcommands.Add(CreateBenchmarkCommand(formatOption));
        rootCommand.Subcommands.Add(CreateSystemCommand(formatOption));
        rootCommand.Subcommands.Add(CreateCompletionsCommand());
        rootCommand.Subcommands.Add(CreateInteractiveCommand());
        rootCommand.Subcommands.Add(CreateConnectCommand());

        // Default handler
        rootCommand.SetAction((ParseResult parseResult) =>
        {
            ShowWelcome();
            return 0;
        });

        return rootCommand;
    }

    private static void ShowWelcome()
    {
        AnsiConsole.Write(new FigletText("DataWarehouse").Color(Color.Blue));
        AnsiConsole.MarkupLine("[grey]Command-line interface for DataWarehouse management[/]\n");
        AnsiConsole.MarkupLine("Usage: [cyan]dw [command] [options][/]");
        AnsiConsole.MarkupLine("       [cyan]dw \"natural language query\"[/]");
        AnsiConsole.MarkupLine("       [cyan]dw interactive[/] (or just [cyan]dw[/])\n");
        AnsiConsole.MarkupLine("Commands:");
        AnsiConsole.MarkupLine("  [cyan]storage[/]     - Manage storage pools");
        AnsiConsole.MarkupLine("  [cyan]backup[/]      - Backup and restore operations");
        AnsiConsole.MarkupLine("  [cyan]plugin[/]      - Manage plugins");
        AnsiConsole.MarkupLine("  [cyan]health[/]      - System health monitoring");
        AnsiConsole.MarkupLine("  [cyan]config[/]      - Configuration management");
        AnsiConsole.MarkupLine("  [cyan]raid[/]        - RAID management");
        AnsiConsole.MarkupLine("  [cyan]audit[/]       - Audit log operations");
        AnsiConsole.MarkupLine("  [cyan]server[/]      - Server management");
        AnsiConsole.MarkupLine("  [cyan]benchmark[/]   - Performance benchmarks");
        AnsiConsole.MarkupLine("  [cyan]system[/]      - System information");
        AnsiConsole.MarkupLine("  [cyan]completions[/] - Generate shell completions");
        AnsiConsole.MarkupLine("\nUse [cyan]dw [command] --help[/] for more information.");
    }

    #region Command Builders

    private static Command CreateStorageCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("storage", "Manage storage pools");

        command.Subcommands.Add(CreateSubCommand("list", "List all storage pools", "storage.list", formatOption));
        command.Subcommands.Add(CreateSubCommand("create", "Create a new storage pool", "storage.create", formatOption,
            MakeArg<string>("name", "Name of the storage pool"),
            MakeOpt<string>("--type", "Pool type", "Standard"),
            MakeOpt<long>("--capacity", "Capacity in bytes", 100L * 1024 * 1024 * 1024)));
        command.Subcommands.Add(CreateSubCommand("delete", "Delete a storage pool", "storage.delete", formatOption,
            MakeArg<string>("id", "Storage pool ID"),
            MakeOpt<bool>("--force", "Force deletion")));
        command.Subcommands.Add(CreateSubCommand("info", "Show pool information", "storage.info", formatOption,
            MakeArg<string>("id", "Storage pool ID")));
        command.Subcommands.Add(CreateSubCommand("stats", "Show storage statistics", "storage.stats", formatOption));

        return command;
    }

    private static Command CreateBackupCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("backup", "Backup and restore operations");

        command.Subcommands.Add(CreateSubCommand("create", "Create a backup", "backup.create", formatOption,
            MakeArg<string>("name", "Name of the backup"),
            MakeOpt<string?>("--destination", "Backup destination path"),
            MakeOpt<bool>("--incremental", "Create incremental backup"),
            MakeOpt<bool>("--compress", "Compress backup", true),
            MakeOpt<bool>("--encrypt", "Encrypt backup")));
        command.Subcommands.Add(CreateSubCommand("list", "List all backups", "backup.list", formatOption));
        command.Subcommands.Add(CreateSubCommand("restore", "Restore from backup", "backup.restore", formatOption,
            MakeArg<string>("id", "Backup ID"),
            MakeOpt<string?>("--target", "Target restore location"),
            MakeOpt<bool>("--verify", "Verify after restore", true)));
        command.Subcommands.Add(CreateSubCommand("verify", "Verify backup integrity", "backup.verify", formatOption,
            MakeArg<string>("id", "Backup ID")));
        command.Subcommands.Add(CreateSubCommand("delete", "Delete a backup", "backup.delete", formatOption,
            MakeArg<string>("id", "Backup ID"),
            MakeOpt<bool>("--force", "Force deletion")));

        return command;
    }

    private static Command CreatePluginCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("plugin", "Manage plugins");

        command.Subcommands.Add(CreateSubCommand("list", "List all plugins", "plugin.list", formatOption,
            MakeOpt<string?>("--category", "Filter by category")));
        command.Subcommands.Add(CreateSubCommand("info", "Show plugin details", "plugin.info", formatOption,
            MakeArg<string>("id", "Plugin ID")));
        command.Subcommands.Add(CreateSubCommand("enable", "Enable a plugin", "plugin.enable", formatOption,
            MakeArg<string>("id", "Plugin ID")));
        command.Subcommands.Add(CreateSubCommand("disable", "Disable a plugin", "plugin.disable", formatOption,
            MakeArg<string>("id", "Plugin ID")));
        command.Subcommands.Add(CreateSubCommand("reload", "Reload a plugin", "plugin.reload", formatOption,
            MakeArg<string>("id", "Plugin ID")));

        return command;
    }

    private static Command CreateHealthCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("health", "System health and monitoring");

        command.Subcommands.Add(CreateSubCommand("status", "Show system health status", "health.status", formatOption));
        command.Subcommands.Add(CreateSubCommand("metrics", "Show system metrics", "health.metrics", formatOption));
        command.Subcommands.Add(CreateSubCommand("alerts", "Show active alerts", "health.alerts", formatOption,
            MakeOpt<bool>("--all", "Include acknowledged alerts")));
        command.Subcommands.Add(CreateSubCommand("check", "Run health check", "health.check", formatOption,
            MakeOpt<string?>("--component", "Specific component to check")));

        return command;
    }

    private static Command CreateConfigCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("config", "Configuration management");

        command.Subcommands.Add(CreateSubCommand("show", "Show current configuration", "config.show", formatOption,
            MakeOpt<string?>("--section", "Configuration section")));
        command.Subcommands.Add(CreateSubCommand("set", "Set configuration value", "config.set", formatOption,
            MakeArg<string>("key", "Configuration key"),
            MakeArg<string>("value", "Configuration value")));
        command.Subcommands.Add(CreateSubCommand("get", "Get configuration value", "config.get", formatOption,
            MakeArg<string>("key", "Configuration key")));
        command.Subcommands.Add(CreateSubCommand("export", "Export configuration", "config.export", formatOption,
            MakeArg<string>("path", "Export file path")));
        command.Subcommands.Add(CreateSubCommand("import", "Import configuration", "config.import", formatOption,
            MakeArg<string>("path", "Import file path"),
            MakeOpt<bool>("--merge", "Merge with existing config")));

        return command;
    }

    private static Command CreateRaidCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("raid", "RAID management");

        command.Subcommands.Add(CreateSubCommand("list", "List RAID configurations", "raid.list", formatOption));
        command.Subcommands.Add(CreateSubCommand("create", "Create a RAID array", "raid.create", formatOption,
            MakeArg<string>("name", "Name of the RAID array"),
            MakeOpt<string>("--level", "RAID level", "5"),
            MakeOpt<int>("--disks", "Number of disks", 4),
            MakeOpt<int>("--stripe-size", "Stripe size in KB", 64)));
        command.Subcommands.Add(CreateSubCommand("status", "Show RAID status", "raid.status", formatOption,
            MakeArg<string>("id", "RAID array ID")));
        command.Subcommands.Add(CreateSubCommand("rebuild", "Start RAID rebuild", "raid.rebuild", formatOption,
            MakeArg<string>("id", "RAID array ID")));
        command.Subcommands.Add(CreateSubCommand("levels", "List supported RAID levels", "raid.levels", formatOption));

        return command;
    }

    private static Command CreateAuditCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("audit", "Audit log operations");

        command.Subcommands.Add(CreateSubCommand("list", "List audit log entries", "audit.list", formatOption,
            MakeOpt<int>("--limit", "Maximum entries", 50),
            MakeOpt<string?>("--category", "Filter by category"),
            MakeOpt<string?>("--user", "Filter by user"),
            MakeOpt<DateTime?>("--since", "Show entries since date")));
        command.Subcommands.Add(CreateSubCommand("export", "Export audit log", "audit.export", formatOption,
            MakeArg<string>("path", "Export file path"),
            MakeOpt<string>("--format", "Export format (json, csv)", "json")));
        command.Subcommands.Add(CreateSubCommand("stats", "Show audit statistics", "audit.stats", formatOption,
            MakeOpt<string>("--period", "Time period", "24h")));

        return command;
    }

    private static Command CreateServerCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("server", "Server management");

        command.Subcommands.Add(CreateSubCommand("start", "Start DataWarehouse server", "server.start", formatOption,
            MakeOpt<int>("--port", "Server port", 5000),
            MakeOpt<string>("--mode", "Operating mode", "Workstation")));
        command.Subcommands.Add(CreateSubCommand("stop", "Stop DataWarehouse server", "server.stop", formatOption,
            MakeOpt<bool>("--graceful", "Graceful shutdown", true)));
        command.Subcommands.Add(CreateSubCommand("status", "Show server status", "server.status", formatOption));
        command.Subcommands.Add(CreateSubCommand("info", "Show server information", "server.info", formatOption));

        return command;
    }

    private static Command CreateBenchmarkCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("benchmark", "Performance benchmarks");

        command.Subcommands.Add(CreateSubCommand("run", "Run benchmarks", "benchmark.run", formatOption,
            MakeOpt<string>("--type", "Benchmark type", "all"),
            MakeOpt<int>("--duration", "Test duration in seconds", 30),
            MakeOpt<string>("--size", "Test data size", "1MB")));
        command.Subcommands.Add(CreateSubCommand("report", "Show benchmark report", "benchmark.report", formatOption,
            MakeOpt<string?>("--id", "Specific benchmark ID")));

        return command;
    }

    private static Command CreateSystemCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("system", "System information");

        command.Subcommands.Add(CreateSubCommand("info", "Get system information", "system.info", formatOption));
        command.Subcommands.Add(CreateSubCommand("capabilities", "Get instance capabilities", "system.capabilities", formatOption));
        var helpCommandArg = new Argument<string?>("command") { Description = "Command to get help for", Arity = ArgumentArity.ZeroOrOne };
        command.Subcommands.Add(CreateSubCommand("help", "Show help", "help", formatOption, helpCommandArg));

        return command;
    }

    private static Command CreateCompletionsCommand()
    {
        var command = new Command("completions", "Generate shell completion scripts");

        var bashCmd = new Command("bash", "Generate bash completions");
        bashCmd.SetAction((ParseResult parseResult) =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Bash));
            return 0;
        });

        var zshCmd = new Command("zsh", "Generate zsh completions");
        zshCmd.SetAction((ParseResult parseResult) =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Zsh));
            return 0;
        });

        var fishCmd = new Command("fish", "Generate fish completions");
        fishCmd.SetAction((ParseResult parseResult) =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Fish));
            return 0;
        });

        var pwshCmd = new Command("powershell", "Generate PowerShell completions");
        pwshCmd.SetAction((ParseResult parseResult) =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.PowerShell));
            return 0;
        });

        command.Subcommands.Add(bashCmd);
        command.Subcommands.Add(zshCmd);
        command.Subcommands.Add(fishCmd);
        command.Subcommands.Add(pwshCmd);

        return command;
    }

    private static Command CreateInteractiveCommand()
    {
        var command = new Command("interactive", "Run interactive mode");
        command.SetAction(async (ParseResult parseResult, CancellationToken token) =>
        {
            await RunInteractiveModeAsync();
            return 0;
        });
        return command;
    }

    private static Command CreateConnectCommand()
    {
        var command = new Command("connect", "Connect to a DataWarehouse instance");

        var hostOption = new Option<string?>("--host") { Description = "Remote host address" };
        var portOption = new Option<int>("--port") { Description = "Remote port", DefaultValueFactory = _ => 8080 };
        var localPathOption = new Option<string?>("--local-path") { Description = "Path to local instance" };

        command.Options.Add(hostOption);
        command.Options.Add(portOption);
        command.Options.Add(localPathOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken token) =>
        {
            var host = parseResult.GetValue(hostOption);
            var port = parseResult.GetValue(portOption);
            var localPath = parseResult.GetValue(localPathOption);

            var connected = false;

            if (!string.IsNullOrEmpty(host))
            {
                connected = await _instanceManager!.ConnectRemoteAsync(host, port);
            }
            else if (!string.IsNullOrEmpty(localPath))
            {
                connected = await _instanceManager!.ConnectLocalAsync(localPath);
            }
            else
            {
                connected = await _instanceManager!.ConnectInProcessAsync();
            }

            if (connected)
            {
                await _capabilityManager!.RefreshCapabilitiesAsync();
                AnsiConsole.MarkupLine("[green]Connected to DataWarehouse instance.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Failed to connect.[/]");
            }

            return connected ? 0 : 1;
        });

        return command;
    }

    #region Helper Methods

    /// <summary>
    /// Creates an Argument with the System.CommandLine 2.0 stable API.
    /// </summary>
    private static Argument<T> MakeArg<T>(string name, string description)
    {
        return new Argument<T>(name) { Description = description };
    }

    /// <summary>
    /// Creates an Option with no default value using the System.CommandLine 2.0 stable API.
    /// </summary>
    private static Option<T> MakeOpt<T>(string name, string description)
    {
        return new Option<T>(name) { Description = description };
    }

    /// <summary>
    /// Creates an Option with a default value using the System.CommandLine 2.0 stable API.
    /// </summary>
    private static Option<T> MakeOpt<T>(string name, string description, T defaultValue)
    {
        return new Option<T>(name) { Description = description, DefaultValueFactory = _ => defaultValue };
    }

    #endregion

    /// <summary>
    /// Creates a subcommand that delegates to the shared CommandExecutor.
    /// </summary>
    private static Command CreateSubCommand(
        string name,
        string description,
        string sharedCommandName,
        Option<OutputFormat> formatOption,
        params Symbol[] symbols)
    {
        var command = new Command(name, description);

        var arguments = new List<Argument>();
        var options = new List<Option>();

        foreach (var symbol in symbols)
        {
            if (symbol is Argument arg)
            {
                command.Arguments.Add(arg);
                arguments.Add(arg);
            }
            else if (symbol is Option opt)
            {
                command.Options.Add(opt);
                options.Add(opt);
            }
        }

        command.SetAction(async (ParseResult parseResult, CancellationToken token) =>
        {
            var parameters = new Dictionary<string, object?>();

            // Extract argument values via ArgumentResult
            foreach (var arg in arguments)
            {
                var argResult = parseResult.GetResult(arg);
                if (argResult != null && argResult.Tokens.Count > 0)
                {
                    parameters[arg.Name] = argResult.Tokens[0].Value;
                }
            }

            // Extract option values via OptionResult
            foreach (var opt in options)
            {
                var optResult = parseResult.GetResult(opt);
                if (optResult != null && optResult.Tokens.Count > 0)
                {
                    var optName = opt.Name.TrimStart('-').Replace("-", "", StringComparison.Ordinal);
                    parameters[optName] = optResult.Tokens[0].Value;
                }
            }

            // Get format
            var format = parseResult.GetValue(formatOption);

            // Execute command
            var result = await _executor!.ExecuteAsync(sharedCommandName, parameters);
            _renderer.Render(result, format);

            _history?.Add(sharedCommandName, parameters, result.Success);

            return result.ExitCode;
        });

        return command;
    }

    #endregion
}
