// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
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
        var rootCommand = new RootCommand("DataWarehouse CLI - Command-line interface for DataWarehouse management");

        // Global options
        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            description: "Enable verbose output");

        var configOption = new Option<string?>(
            aliases: new[] { "--config", "-c" },
            description: "Path to configuration file");

        var formatOption = new Option<OutputFormat>(
            aliases: new[] { "--format", "-f" },
            description: "Output format (table, json, yaml, csv)",
            getDefaultValue: () => OutputFormat.Table);

        var instanceOption = new Option<string?>(
            aliases: new[] { "--instance", "-i" },
            description: "Saved instance profile to use");

        rootCommand.Add(verboseOption);
        rootCommand.Add(configOption);
        rootCommand.Add(formatOption);
        rootCommand.Add(instanceOption);

        // Add command groups
        rootCommand.Add(CreateStorageCommand(formatOption));
        rootCommand.Add(CreateBackupCommand(formatOption));
        rootCommand.Add(CreatePluginCommand(formatOption));
        rootCommand.Add(CreateHealthCommand(formatOption));
        rootCommand.Add(CreateConfigCommand(formatOption));
        rootCommand.Add(CreateRaidCommand(formatOption));
        rootCommand.Add(CreateAuditCommand(formatOption));
        rootCommand.Add(CreateServerCommand(formatOption));
        rootCommand.Add(CreateBenchmarkCommand(formatOption));
        rootCommand.Add(CreateSystemCommand(formatOption));
        rootCommand.Add(CreateCompletionsCommand());
        rootCommand.Add(CreateInteractiveCommand());
        rootCommand.Add(CreateConnectCommand());

        // Default handler
        rootCommand.Handler = CommandHandler.Create(() =>
        {
            ShowWelcome();
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

        command.Add(CreateSubCommand("list", "List all storage pools", "storage.list", formatOption));
        command.Add(CreateSubCommand("create", "Create a new storage pool", "storage.create", formatOption,
            new Argument<string>("name", description: "Pool name"),
            new Option<string>("--type", description: "Pool type", getDefaultValue: () => "Standard"),
            new Option<long>("--capacity", description: "Capacity in bytes", getDefaultValue: () => 100L * 1024 * 1024 * 1024)));
        command.Add(CreateSubCommand("delete", "Delete a storage pool", "storage.delete", formatOption,
            new Argument<string>("id", description: "Pool ID"),
            new Option<bool>("--force", description: "Force deletion")));
        command.Add(CreateSubCommand("info", "Show pool information", "storage.info", formatOption,
            new Argument<string>("id", description: "Pool ID")));
        command.Add(CreateSubCommand("stats", "Show storage statistics", "storage.stats", formatOption));

        return command;
    }

    private static Command CreateBackupCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("backup", "Backup and restore operations");

        command.Add(CreateSubCommand("create", "Create a backup", "backup.create", formatOption,
            new Argument<string>("name", description: "Backup name"),
            new Option<string?>("--destination", description: "Backup destination path"),
            new Option<bool>("--incremental", description: "Create incremental backup"),
            new Option<bool>("--compress", description: "Compress backup", getDefaultValue: () => true),
            new Option<bool>("--encrypt", description: "Encrypt backup")));
        command.Add(CreateSubCommand("list", "List all backups", "backup.list", formatOption));
        command.Add(CreateSubCommand("restore", "Restore from backup", "backup.restore", formatOption,
            new Argument<string>("id", description: "Backup ID"),
            new Option<string?>("--target", description: "Target restore location"),
            new Option<bool>("--verify", description: "Verify after restore", getDefaultValue: () => true)));
        command.Add(CreateSubCommand("verify", "Verify backup integrity", "backup.verify", formatOption,
            new Argument<string>("id", description: "Backup ID")));
        command.Add(CreateSubCommand("delete", "Delete a backup", "backup.delete", formatOption,
            new Argument<string>("id", description: "Backup ID"),
            new Option<bool>("--force", description: "Force deletion")));

        return command;
    }

    private static Command CreatePluginCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("plugin", "Manage plugins");

        command.Add(CreateSubCommand("list", "List all plugins", "plugin.list", formatOption,
            new Option<string?>("--category", description: "Filter by category")));
        command.Add(CreateSubCommand("info", "Show plugin details", "plugin.info", formatOption,
            new Argument<string>("id", description: "Plugin ID")));
        command.Add(CreateSubCommand("enable", "Enable a plugin", "plugin.enable", formatOption,
            new Argument<string>("id", description: "Plugin ID")));
        command.Add(CreateSubCommand("disable", "Disable a plugin", "plugin.disable", formatOption,
            new Argument<string>("id", description: "Plugin ID")));
        command.Add(CreateSubCommand("reload", "Reload a plugin", "plugin.reload", formatOption,
            new Argument<string>("id", description: "Plugin ID")));

        return command;
    }

    private static Command CreateHealthCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("health", "System health and monitoring");

        command.Add(CreateSubCommand("status", "Show system health status", "health.status", formatOption));
        command.Add(CreateSubCommand("metrics", "Show system metrics", "health.metrics", formatOption));
        command.Add(CreateSubCommand("alerts", "Show active alerts", "health.alerts", formatOption,
            new Option<bool>("--all", description: "Include acknowledged alerts")));
        command.Add(CreateSubCommand("check", "Run health check", "health.check", formatOption,
            new Option<string?>("--component", description: "Specific component to check")));

        return command;
    }

    private static Command CreateConfigCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("config", "Configuration management");

        command.Add(CreateSubCommand("show", "Show current configuration", "config.show", formatOption,
            new Option<string?>("--section", description: "Configuration section")));
        command.Add(CreateSubCommand("set", "Set configuration value", "config.set", formatOption,
            new Argument<string>("key", description: "Configuration key"),
            new Argument<string>("value", description: "Configuration value")));
        command.Add(CreateSubCommand("get", "Get configuration value", "config.get", formatOption,
            new Argument<string>("key", description: "Configuration key")));
        command.Add(CreateSubCommand("export", "Export configuration", "config.export", formatOption,
            new Argument<string>("path", description: "Export file path")));
        command.Add(CreateSubCommand("import", "Import configuration", "config.import", formatOption,
            new Argument<string>("path", description: "Import file path"),
            new Option<bool>("--merge", description: "Merge with existing config")));

        return command;
    }

    private static Command CreateRaidCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("raid", "RAID management");

        command.Add(CreateSubCommand("list", "List RAID configurations", "raid.list", formatOption));
        command.Add(CreateSubCommand("create", "Create a RAID array", "raid.create", formatOption,
            new Argument<string>("name", description: "RAID array name"),
            new Option<string>("--level", description: "RAID level", getDefaultValue: () => "5"),
            new Option<int>("--disks", description: "Number of disks", getDefaultValue: () => 4),
            new Option<int>("--stripe-size", description: "Stripe size in KB", getDefaultValue: () => 64)));
        command.Add(CreateSubCommand("status", "Show RAID status", "raid.status", formatOption,
            new Argument<string>("id", description: "RAID array ID")));
        command.Add(CreateSubCommand("rebuild", "Start RAID rebuild", "raid.rebuild", formatOption,
            new Argument<string>("id", description: "RAID array ID")));
        command.Add(CreateSubCommand("levels", "List supported RAID levels", "raid.levels", formatOption));

        return command;
    }

    private static Command CreateAuditCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("audit", "Audit log operations");

        command.Add(CreateSubCommand("list", "List audit log entries", "audit.list", formatOption,
            new Option<int>("--limit", description: "Maximum entries", getDefaultValue: () => 50),
            new Option<string?>("--category", description: "Filter by category"),
            new Option<string?>("--user", description: "Filter by user"),
            new Option<DateTime?>("--since", description: "Show entries since date")));
        command.Add(CreateSubCommand("export", "Export audit log", "audit.export", formatOption,
            new Argument<string>("path", description: "Export file path"),
            new Option<string>("--format", description: "Export format (json, csv)", getDefaultValue: () => "json")));
        command.Add(CreateSubCommand("stats", "Show audit statistics", "audit.stats", formatOption,
            new Option<string>("--period", description: "Time period", getDefaultValue: () => "24h")));

        return command;
    }

    private static Command CreateServerCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("server", "Server management");

        command.Add(CreateSubCommand("start", "Start DataWarehouse server", "server.start", formatOption,
            new Option<int>("--port", description: "Server port", getDefaultValue: () => 5000),
            new Option<string>("--mode", description: "Operating mode", getDefaultValue: () => "Workstation")));
        command.Add(CreateSubCommand("stop", "Stop DataWarehouse server", "server.stop", formatOption,
            new Option<bool>("--graceful", description: "Graceful shutdown", getDefaultValue: () => true)));
        command.Add(CreateSubCommand("status", "Show server status", "server.status", formatOption));
        command.Add(CreateSubCommand("info", "Show server information", "server.info", formatOption));

        return command;
    }

    private static Command CreateBenchmarkCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("benchmark", "Performance benchmarks");

        command.Add(CreateSubCommand("run", "Run benchmarks", "benchmark.run", formatOption,
            new Option<string>("--type", description: "Benchmark type", getDefaultValue: () => "all"),
            new Option<int>("--duration", description: "Test duration in seconds", getDefaultValue: () => 30),
            new Option<string>("--size", description: "Test data size", getDefaultValue: () => "1MB")));
        command.Add(CreateSubCommand("report", "Show benchmark report", "benchmark.report", formatOption,
            new Option<string?>("--id", description: "Specific benchmark ID")));

        return command;
    }

    private static Command CreateSystemCommand(Option<OutputFormat> formatOption)
    {
        var command = new Command("system", "System information");

        command.Add(CreateSubCommand("info", "Get system information", "system.info", formatOption));
        command.Add(CreateSubCommand("capabilities", "Get instance capabilities", "system.capabilities", formatOption));
        command.Add(CreateSubCommand("help", "Show help", "help", formatOption,
            new Argument<string?>("command", description: "Command to get help for") { Arity = ArgumentArity.ZeroOrOne }));

        return command;
    }

    private static Command CreateCompletionsCommand()
    {
        var command = new Command("completions", "Generate shell completion scripts");

        var bashCmd = new Command("bash", "Generate bash completions");
        bashCmd.Handler = CommandHandler.Create(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Bash));
        });

        var zshCmd = new Command("zsh", "Generate zsh completions");
        zshCmd.Handler = CommandHandler.Create(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Zsh));
        });

        var fishCmd = new Command("fish", "Generate fish completions");
        fishCmd.Handler = CommandHandler.Create(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.Fish));
        });

        var pwshCmd = new Command("powershell", "Generate PowerShell completions");
        pwshCmd.Handler = CommandHandler.Create(() =>
        {
            var generator = new ShellCompletionGenerator(_executor!);
            Console.WriteLine(generator.Generate(ShellType.PowerShell));
        });

        command.Add(bashCmd);
        command.Add(zshCmd);
        command.Add(fishCmd);
        command.Add(pwshCmd);

        return command;
    }

    private static Command CreateInteractiveCommand()
    {
        var command = new Command("interactive", "Run interactive mode");
        command.Handler = CommandHandler.Create(async () => await RunInteractiveModeAsync());
        return command;
    }

    private static Command CreateConnectCommand()
    {
        var command = new Command("connect", "Connect to a DataWarehouse instance");

        var hostOption = new Option<string?>("--host", description: "Remote host address");
        var portOption = new Option<int>("--port", description: "Remote port", getDefaultValue: () => 8080);
        var localPathOption = new Option<string?>("--local-path", description: "Path to local instance");

        command.Add(hostOption);
        command.Add(portOption);
        command.Add(localPathOption);

        command.Handler = CommandHandler.Create<string?, int, string?>(async (host, port, localPath) =>
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
        });

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

        command.Handler = CommandHandler.Create<InvocationContext>(async (context) =>
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
