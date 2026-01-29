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
        if (args.Length == 1 && !args[0].StartsWith("-") && !args[0].Contains('.') && args[0].Contains(' '))
        {
            return await HandleNaturalLanguageAsync(args[0], conversational);
        }

        // Check for interactive mode
        if (args.Length == 0 || (args.Length == 1 && args[0] == "interactive"))
        {
            return await RunInteractiveModeAsync();
        }

        // Build the command-line parser
        var rootCommand = BuildRootCommand();
        return await rootCommand.InvokeAsync(args);
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
        var rootCommand = new RootCommand("DataWarehouse CLI - Command-line interface for DataWarehouse management")
        {
            Name = "dw"
        };

        // Global options
        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            description: "Enable verbose output");

        var configOption = new Option<string?>(
            aliases: new[] { "--config", "-c" },
            description: "Path to configuration file");

        var formatOption = new Option<OutputFormat>(
            aliases: new[] { "--format", "-f" },
            getDefaultValue: () => OutputFormat.Table,
            description: "Output format (table, json, yaml, csv)");

        var instanceOption = new Option<string?>(
            aliases: new[] { "--instance", "-i" },
            description: "Saved instance profile to use");

        rootCommand.AddGlobalOption(verboseOption);
        rootCommand.AddGlobalOption(configOption);
        rootCommand.AddGlobalOption(formatOption);
        rootCommand.AddGlobalOption(instanceOption);

        // Add command groups
        rootCommand.AddCommand(CreateStorageCommand(formatOption));
        rootCommand.AddCommand(CreateBackupCommand(formatOption));
        rootCommand.AddCommand(CreatePluginCommand(formatOption));
        rootCommand.AddCommand(CreateHealthCommand(formatOption));
        rootCommand.AddCommand(CreateConfigCommand(formatOption));
        rootCommand.AddCommand(CreateRaidCommand(formatOption));
        rootCommand.AddCommand(CreateAuditCommand(formatOption));
        rootCommand.AddCommand(CreateServerCommand(formatOption));
        rootCommand.AddCommand(CreateBenchmarkCommand(formatOption));
        rootCommand.AddCommand(CreateSystemCommand(formatOption));
        rootCommand.AddCommand(CreateCompletionsCommand());
        rootCommand.AddCommand(CreateInteractiveCommand());
        rootCommand.AddCommand(CreateConnectCommand());

        // Default handler
        rootCommand.SetHandler((context) =>
        {
            if (context.ParseResult.UnmatchedTokens.Count == 0)
            {
                ShowWelcome();
            }
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

        command.AddCommand(CreateSubCommand("list", "List all storage pools", "storage.list", formatOption));
        command.AddCommand(CreateSubCommand("create", "Create a new storage pool", "storage.create", formatOption,
            new Argument<string>("name", "Pool name"),
            new Option<string>("--type", () => "Standard", "Pool type"),
            new Option<long>("--capacity", () => 100L * 1024 * 1024 * 1024, "Capacity in bytes")));
        command.AddCommand(CreateSubCommand("delete", "Delete a storage pool", "storage.delete", formatOption,
            new Argument<string>("id", "Pool ID"),
            new Option<bool>("--force", "Force deletion")));
        command.AddCommand(CreateSubCommand("info", "Show pool information", "storage.info", formatOption,
            new Argument<string>("id", "Pool ID")));
        command.AddCommand(CreateSubCommand("stats", "Show storage statistics", "storage.stats", formatOption));

        return command;
    }

    private static Command CreateBackupCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("backup", "Backup and restore operations");

        command.AddCommand(CreateSubCommand("create", "Create a backup", "backup.create", formatOption,
            new Argument<string>("name", "Backup name"),
            new Option<string?>("--destination", "Backup destination path"),
            new Option<bool>("--incremental", "Create incremental backup"),
            new Option<bool>("--compress", () => true, "Compress backup"),
            new Option<bool>("--encrypt", "Encrypt backup")));
        command.AddCommand(CreateSubCommand("list", "List all backups", "backup.list", formatOption));
        command.AddCommand(CreateSubCommand("restore", "Restore from backup", "backup.restore", formatOption,
            new Argument<string>("id", "Backup ID"),
            new Option<string?>("--target", "Target restore location"),
            new Option<bool>("--verify", () => true, "Verify after restore")));
        command.AddCommand(CreateSubCommand("verify", "Verify backup integrity", "backup.verify", formatOption,
            new Argument<string>("id", "Backup ID")));
        command.AddCommand(CreateSubCommand("delete", "Delete a backup", "backup.delete", formatOption,
            new Argument<string>("id", "Backup ID"),
            new Option<bool>("--force", "Force deletion")));

        return command;
    }

    private static Command CreatePluginCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("plugin", "Manage plugins");

        command.AddCommand(CreateSubCommand("list", "List all plugins", "plugin.list", formatOption,
            new Option<string?>("--category", "Filter by category")));
        command.AddCommand(CreateSubCommand("info", "Show plugin details", "plugin.info", formatOption,
            new Argument<string>("id", "Plugin ID")));
        command.AddCommand(CreateSubCommand("enable", "Enable a plugin", "plugin.enable", formatOption,
            new Argument<string>("id", "Plugin ID")));
        command.AddCommand(CreateSubCommand("disable", "Disable a plugin", "plugin.disable", formatOption,
            new Argument<string>("id", "Plugin ID")));
        command.AddCommand(CreateSubCommand("reload", "Reload a plugin", "plugin.reload", formatOption,
            new Argument<string>("id", "Plugin ID")));

        return command;
    }

    private static Command CreateHealthCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("health", "System health and monitoring");

        command.AddCommand(CreateSubCommand("status", "Show system health status", "health.status", formatOption));
        command.AddCommand(CreateSubCommand("metrics", "Show system metrics", "health.metrics", formatOption));
        command.AddCommand(CreateSubCommand("alerts", "Show active alerts", "health.alerts", formatOption,
            new Option<bool>("--all", "Include acknowledged alerts")));
        command.AddCommand(CreateSubCommand("check", "Run health check", "health.check", formatOption,
            new Option<string?>("--component", "Specific component to check")));

        return command;
    }

    private static Command CreateConfigCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("config", "Configuration management");

        command.AddCommand(CreateSubCommand("show", "Show current configuration", "config.show", formatOption,
            new Option<string?>("--section", "Configuration section")));
        command.AddCommand(CreateSubCommand("set", "Set configuration value", "config.set", formatOption,
            new Argument<string>("key", "Configuration key"),
            new Argument<string>("value", "Configuration value")));
        command.AddCommand(CreateSubCommand("get", "Get configuration value", "config.get", formatOption,
            new Argument<string>("key", "Configuration key")));
        command.AddCommand(CreateSubCommand("export", "Export configuration", "config.export", formatOption,
            new Argument<string>("path", "Export file path")));
        command.AddCommand(CreateSubCommand("import", "Import configuration", "config.import", formatOption,
            new Argument<string>("path", "Import file path"),
            new Option<bool>("--merge", "Merge with existing config")));

        return command;
    }

    private static Command CreateRaidCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("raid", "RAID management");

        command.AddCommand(CreateSubCommand("list", "List RAID configurations", "raid.list", formatOption));
        command.AddCommand(CreateSubCommand("create", "Create a RAID array", "raid.create", formatOption,
            new Argument<string>("name", "RAID array name"),
            new Option<string>("--level", () => "5", "RAID level"),
            new Option<int>("--disks", () => 4, "Number of disks"),
            new Option<int>("--stripe-size", () => 64, "Stripe size in KB")));
        command.AddCommand(CreateSubCommand("status", "Show RAID status", "raid.status", formatOption,
            new Argument<string>("id", "RAID array ID")));
        command.AddCommand(CreateSubCommand("rebuild", "Start RAID rebuild", "raid.rebuild", formatOption,
            new Argument<string>("id", "RAID array ID")));
        command.AddCommand(CreateSubCommand("levels", "List supported RAID levels", "raid.levels", formatOption));

        return command;
    }

    private static Command CreateAuditCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("audit", "Audit log operations");

        command.AddCommand(CreateSubCommand("list", "List audit log entries", "audit.list", formatOption,
            new Option<int>("--limit", () => 50, "Maximum entries"),
            new Option<string?>("--category", "Filter by category"),
            new Option<string?>("--user", "Filter by user"),
            new Option<DateTime?>("--since", "Show entries since date")));
        command.AddCommand(CreateSubCommand("export", "Export audit log", "audit.export", formatOption,
            new Argument<string>("path", "Export file path"),
            new Option<string>("--format", () => "json", "Export format (json, csv)")));
        command.AddCommand(CreateSubCommand("stats", "Show audit statistics", "audit.stats", formatOption,
            new Option<string>("--period", () => "24h", "Time period")));

        return command;
    }

    private static Command CreateServerCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("server", "Server management");

        command.AddCommand(CreateSubCommand("start", "Start DataWarehouse server", "server.start", formatOption,
            new Option<int>("--port", () => 5000, "Server port"),
            new Option<string>("--mode", () => "Workstation", "Operating mode")));
        command.AddCommand(CreateSubCommand("stop", "Stop DataWarehouse server", "server.stop", formatOption,
            new Option<bool>("--graceful", () => true, "Graceful shutdown")));
        command.AddCommand(CreateSubCommand("status", "Show server status", "server.status", formatOption));
        command.AddCommand(CreateSubCommand("info", "Show server information", "server.info", formatOption));

        return command;
    }

    private static Command CreateBenchmarkCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("benchmark", "Performance benchmarks");

        command.AddCommand(CreateSubCommand("run", "Run benchmarks", "benchmark.run", formatOption,
            new Option<string>("--type", () => "all", "Benchmark type"),
            new Option<int>("--duration", () => 30, "Test duration in seconds"),
            new Option<string>("--size", () => "1MB", "Test data size")));
        command.AddCommand(CreateSubCommand("report", "Show benchmark report", "benchmark.report", formatOption,
            new Option<string?>("--id", "Specific benchmark ID")));

        return command;
    }

    private static Command CreateSystemCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("system", "System information");

        command.AddCommand(CreateSubCommand("info", "Get system information", "system.info", formatOption));
        command.AddCommand(CreateSubCommand("capabilities", "Get instance capabilities", "system.capabilities", formatOption));
        command.AddCommand(CreateSubCommand("help", "Show help", "help", formatOption,
            new Argument<string?>("command", () => null!, "Command to get help for")));

        return command;
    }

    private static Command CreateCompletionsCommand()
    {
        var command = new Command("completions", "Generate shell completion scripts");

        var bashCmd = new Command("bash", "Generate bash completions");
        bashCmd.SetHandler(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Bash));
        });

        var zshCmd = new Command("zsh", "Generate zsh completions");
        zshCmd.SetHandler(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Zsh));
        });

        var fishCmd = new Command("fish", "Generate fish completions");
        fishCmd.SetHandler(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Fish));
        });

        var pwshCmd = new Command("powershell", "Generate PowerShell completions");
        pwshCmd.SetHandler(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.PowerShell));
        });

        command.AddCommand(bashCmd);
        command.AddCommand(zshCmd);
        command.AddCommand(fishCmd);
        command.AddCommand(pwshCmd);

        return command;
    }

    private static Command CreateInteractiveCommand()
    {
        var command = new Command("interactive", "Run interactive mode");
        command.SetHandler(async () => await RunInteractiveModeAsync());
        return command;
    }

    private static Command CreateConnectCommand()
    {
        var command = new Command("connect", "Connect to a DataWarehouse instance");

        var hostOption = new Option<string?>("--host", "Remote host address");
        var portOption = new Option<int>("--port", () => 8080, "Remote port");
        var localPathOption = new Option<string?>("--local-path", "Path to local instance");

        command.AddOption(hostOption);
        command.AddOption(portOption);
        command.AddOption(localPathOption);

        command.SetHandler(async (host, port, localPath) =>
        {
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
        }, hostOption, portOption, localPathOption);

        return command;
    }

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
                command.AddArgument(arg);
                arguments.Add(arg);
            }
            else if (symbol is Option opt)
            {
                command.AddOption(opt);
                options.Add(opt);
            }
        }

        command.SetHandler(async (context) =>
        {
            var parameters = new Dictionary<string, object?>();

            // Extract argument values
            foreach (var arg in arguments)
            {
                var value = context.ParseResult.GetValueForArgument(arg);
                if (value != null)
                {
                    parameters[arg.Name] = value;
                }
            }

            // Extract option values
            foreach (var opt in options)
            {
                var value = context.ParseResult.GetValueForOption(opt);
                if (value != null)
                {
                    var optName = opt.Name.TrimStart('-').Replace("-", "");
                    parameters[optName] = value;
                }
            }

            // Get format
            var format = context.ParseResult.GetValueForOption(formatOption);

            // Execute command
            var result = await _executor!.ExecuteAsync(sharedCommandName, parameters);
            _renderer.Render(result, format);

            _history?.Add(sharedCommandName, parameters, result.Success);

            context.ExitCode = result.ExitCode;
        });

        return command;
    }

    #endregion
}
