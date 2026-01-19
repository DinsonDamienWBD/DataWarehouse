using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using DataWarehouse.CLI.Commands;
using Spectre.Console;

namespace DataWarehouse.CLI;

/// <summary>
/// DataWarehouse CLI - Production-ready command-line interface for DataWarehouse management.
/// Supports storage operations, plugin management, RAID configuration, backup/restore,
/// system health monitoring, and administrative tasks.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("DataWarehouse CLI - Command-line interface for DataWarehouse management")
        {
            Name = "dw"
        };

        // Add global options
        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            description: "Enable verbose output");

        var configOption = new Option<string?>(
            aliases: new[] { "--config", "-c" },
            description: "Path to configuration file");

        var formatOption = new Option<OutputFormat>(
            aliases: new[] { "--format", "-f" },
            getDefaultValue: () => OutputFormat.Table,
            description: "Output format (table, json, yaml)");

        rootCommand.AddGlobalOption(verboseOption);
        rootCommand.AddGlobalOption(configOption);
        rootCommand.AddGlobalOption(formatOption);

        // Add command groups
        rootCommand.AddCommand(CreateStorageCommand());
        rootCommand.AddCommand(CreatePluginCommand());
        rootCommand.AddCommand(CreateRaidCommand());
        rootCommand.AddCommand(CreateBackupCommand());
        rootCommand.AddCommand(CreateHealthCommand());
        rootCommand.AddCommand(CreateConfigCommand());
        rootCommand.AddCommand(CreateAuditCommand());
        rootCommand.AddCommand(CreateBenchmarkCommand());
        rootCommand.AddCommand(CreateServerCommand());

        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseExceptionHandler((ex, context) =>
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                if (context.ParseResult.GetValueForOption(verboseOption))
                {
                    AnsiConsole.WriteException(ex);
                }
                context.ExitCode = 1;
            })
            .Build();

        return await parser.InvokeAsync(args);
    }

    private static Command CreateStorageCommand()
    {
        var command = new Command("storage", "Manage storage pools and instances");

        // storage list
        var listCommand = new Command("list", "List all storage pools");
        listCommand.SetHandler(StorageCommands.ListPoolsAsync);
        command.AddCommand(listCommand);

        // storage create
        var createCommand = new Command("create", "Create a new storage pool");
        var nameArg = new Argument<string>("name", "Pool name");
        var typeOption = new Option<string>("--type", () => "Standard", "Pool type (Standard, SSD, Archive, Cache)");
        var capacityOption = new Option<long>("--capacity", () => 100L * 1024 * 1024 * 1024, "Capacity in bytes");
        createCommand.AddArgument(nameArg);
        createCommand.AddOption(typeOption);
        createCommand.AddOption(capacityOption);
        createCommand.SetHandler(StorageCommands.CreatePoolAsync, nameArg, typeOption, capacityOption);
        command.AddCommand(createCommand);

        // storage delete
        var deleteCommand = new Command("delete", "Delete a storage pool");
        var idArg = new Argument<string>("id", "Pool ID");
        var forceOption = new Option<bool>("--force", "Force deletion without confirmation");
        deleteCommand.AddArgument(idArg);
        deleteCommand.AddOption(forceOption);
        deleteCommand.SetHandler(StorageCommands.DeletePoolAsync, idArg, forceOption);
        command.AddCommand(deleteCommand);

        // storage info
        var infoCommand = new Command("info", "Show detailed information about a storage pool");
        var poolIdArg = new Argument<string>("id", "Pool ID");
        infoCommand.AddArgument(poolIdArg);
        infoCommand.SetHandler(StorageCommands.ShowPoolInfoAsync, poolIdArg);
        command.AddCommand(infoCommand);

        // storage stats
        var statsCommand = new Command("stats", "Show storage statistics");
        statsCommand.SetHandler(StorageCommands.ShowStatsAsync);
        command.AddCommand(statsCommand);

        return command;
    }

    private static Command CreatePluginCommand()
    {
        var command = new Command("plugin", "Manage plugins");

        // plugin list
        var listCommand = new Command("list", "List all plugins");
        var categoryOption = new Option<string?>("--category", "Filter by category");
        listCommand.AddOption(categoryOption);
        listCommand.SetHandler(PluginCommands.ListPluginsAsync, categoryOption);
        command.AddCommand(listCommand);

        // plugin info
        var infoCommand = new Command("info", "Show plugin details");
        var idArg = new Argument<string>("id", "Plugin ID");
        infoCommand.AddArgument(idArg);
        infoCommand.SetHandler(PluginCommands.ShowPluginInfoAsync, idArg);
        command.AddCommand(infoCommand);

        // plugin enable
        var enableCommand = new Command("enable", "Enable a plugin");
        var enableIdArg = new Argument<string>("id", "Plugin ID");
        enableCommand.AddArgument(enableIdArg);
        enableCommand.SetHandler(PluginCommands.EnablePluginAsync, enableIdArg);
        command.AddCommand(enableCommand);

        // plugin disable
        var disableCommand = new Command("disable", "Disable a plugin");
        var disableIdArg = new Argument<string>("id", "Plugin ID");
        disableCommand.AddArgument(disableIdArg);
        disableCommand.SetHandler(PluginCommands.DisablePluginAsync, disableIdArg);
        command.AddCommand(disableCommand);

        // plugin reload
        var reloadCommand = new Command("reload", "Reload a plugin");
        var reloadIdArg = new Argument<string>("id", "Plugin ID");
        reloadCommand.AddArgument(reloadIdArg);
        reloadCommand.SetHandler(PluginCommands.ReloadPluginAsync, reloadIdArg);
        command.AddCommand(reloadCommand);

        return command;
    }

    private static Command CreateRaidCommand()
    {
        var command = new Command("raid", "Manage RAID configurations");

        // raid list
        var listCommand = new Command("list", "List RAID configurations");
        listCommand.SetHandler(RaidCommands.ListConfigurationsAsync);
        command.AddCommand(listCommand);

        // raid create
        var createCommand = new Command("create", "Create a RAID array");
        var nameArg = new Argument<string>("name", "RAID array name");
        var levelOption = new Option<string>("--level", () => "5", "RAID level (0, 1, 5, 6, 10, etc.)");
        var disksOption = new Option<int>("--disks", () => 4, "Number of disks");
        var stripeSizeOption = new Option<int>("--stripe-size", () => 64, "Stripe size in KB");
        createCommand.AddArgument(nameArg);
        createCommand.AddOption(levelOption);
        createCommand.AddOption(disksOption);
        createCommand.AddOption(stripeSizeOption);
        createCommand.SetHandler(RaidCommands.CreateArrayAsync, nameArg, levelOption, disksOption, stripeSizeOption);
        command.AddCommand(createCommand);

        // raid status
        var statusCommand = new Command("status", "Show RAID status");
        var raidIdArg = new Argument<string>("id", "RAID array ID");
        statusCommand.AddArgument(raidIdArg);
        statusCommand.SetHandler(RaidCommands.ShowStatusAsync, raidIdArg);
        command.AddCommand(statusCommand);

        // raid rebuild
        var rebuildCommand = new Command("rebuild", "Start RAID rebuild");
        var rebuildIdArg = new Argument<string>("id", "RAID array ID");
        rebuildCommand.AddArgument(rebuildIdArg);
        rebuildCommand.SetHandler(RaidCommands.StartRebuildAsync, rebuildIdArg);
        command.AddCommand(rebuildCommand);

        // raid levels
        var levelsCommand = new Command("levels", "List supported RAID levels");
        levelsCommand.SetHandler(RaidCommands.ListLevelsAsync);
        command.AddCommand(levelsCommand);

        return command;
    }

    private static Command CreateBackupCommand()
    {
        var command = new Command("backup", "Backup and restore operations");

        // backup create
        var createCommand = new Command("create", "Create a backup");
        var nameArg = new Argument<string>("name", "Backup name");
        var destOption = new Option<string>("--destination", "Backup destination path");
        var incrementalOption = new Option<bool>("--incremental", "Create incremental backup");
        var compressOption = new Option<bool>("--compress", () => true, "Compress backup");
        var encryptOption = new Option<bool>("--encrypt", "Encrypt backup");
        createCommand.AddArgument(nameArg);
        createCommand.AddOption(destOption);
        createCommand.AddOption(incrementalOption);
        createCommand.AddOption(compressOption);
        createCommand.AddOption(encryptOption);
        createCommand.SetHandler(BackupCommands.CreateBackupAsync, nameArg, destOption, incrementalOption, compressOption, encryptOption);
        command.AddCommand(createCommand);

        // backup list
        var listCommand = new Command("list", "List backups");
        listCommand.SetHandler(BackupCommands.ListBackupsAsync);
        command.AddCommand(listCommand);

        // backup restore
        var restoreCommand = new Command("restore", "Restore from backup");
        var backupIdArg = new Argument<string>("id", "Backup ID");
        var targetOption = new Option<string?>("--target", "Target restore location");
        var verifyOption = new Option<bool>("--verify", () => true, "Verify after restore");
        restoreCommand.AddArgument(backupIdArg);
        restoreCommand.AddOption(targetOption);
        restoreCommand.AddOption(verifyOption);
        restoreCommand.SetHandler(BackupCommands.RestoreBackupAsync, backupIdArg, targetOption, verifyOption);
        command.AddCommand(restoreCommand);

        // backup verify
        var verifyCommand = new Command("verify", "Verify backup integrity");
        var verifyIdArg = new Argument<string>("id", "Backup ID");
        verifyCommand.AddArgument(verifyIdArg);
        verifyCommand.SetHandler(BackupCommands.VerifyBackupAsync, verifyIdArg);
        command.AddCommand(verifyCommand);

        // backup delete
        var deleteCommand = new Command("delete", "Delete a backup");
        var deleteIdArg = new Argument<string>("id", "Backup ID");
        var forceOption = new Option<bool>("--force", "Force deletion");
        deleteCommand.AddArgument(deleteIdArg);
        deleteCommand.AddOption(forceOption);
        deleteCommand.SetHandler(BackupCommands.DeleteBackupAsync, deleteIdArg, forceOption);
        command.AddCommand(deleteCommand);

        return command;
    }

    private static Command CreateHealthCommand()
    {
        var command = new Command("health", "System health and monitoring");

        // health status
        var statusCommand = new Command("status", "Show system health status");
        statusCommand.SetHandler(HealthCommands.ShowStatusAsync);
        command.AddCommand(statusCommand);

        // health metrics
        var metricsCommand = new Command("metrics", "Show system metrics");
        metricsCommand.SetHandler(HealthCommands.ShowMetricsAsync);
        command.AddCommand(metricsCommand);

        // health alerts
        var alertsCommand = new Command("alerts", "Show active alerts");
        var allOption = new Option<bool>("--all", "Include acknowledged alerts");
        alertsCommand.AddOption(allOption);
        alertsCommand.SetHandler(HealthCommands.ShowAlertsAsync, allOption);
        command.AddCommand(alertsCommand);

        // health check
        var checkCommand = new Command("check", "Run health check");
        var componentOption = new Option<string?>("--component", "Specific component to check");
        checkCommand.AddOption(componentOption);
        checkCommand.SetHandler(HealthCommands.RunHealthCheckAsync, componentOption);
        command.AddCommand(checkCommand);

        // health watch
        var watchCommand = new Command("watch", "Watch system health in real-time");
        var intervalOption = new Option<int>("--interval", () => 2, "Update interval in seconds");
        watchCommand.AddOption(intervalOption);
        watchCommand.SetHandler(HealthCommands.WatchHealthAsync, intervalOption);
        command.AddCommand(watchCommand);

        return command;
    }

    private static Command CreateConfigCommand()
    {
        var command = new Command("config", "Configuration management");

        // config show
        var showCommand = new Command("show", "Show current configuration");
        var sectionOption = new Option<string?>("--section", "Configuration section");
        showCommand.AddOption(sectionOption);
        showCommand.SetHandler(ConfigCommands.ShowConfigAsync, sectionOption);
        command.AddCommand(showCommand);

        // config set
        var setCommand = new Command("set", "Set configuration value");
        var keyArg = new Argument<string>("key", "Configuration key");
        var valueArg = new Argument<string>("value", "Configuration value");
        setCommand.AddArgument(keyArg);
        setCommand.AddArgument(valueArg);
        setCommand.SetHandler(ConfigCommands.SetConfigAsync, keyArg, valueArg);
        command.AddCommand(setCommand);

        // config get
        var getCommand = new Command("get", "Get configuration value");
        var getKeyArg = new Argument<string>("key", "Configuration key");
        getCommand.AddArgument(getKeyArg);
        getCommand.SetHandler(ConfigCommands.GetConfigAsync, getKeyArg);
        command.AddCommand(getCommand);

        // config export
        var exportCommand = new Command("export", "Export configuration");
        var exportPathArg = new Argument<string>("path", "Export file path");
        exportCommand.AddArgument(exportPathArg);
        exportCommand.SetHandler(ConfigCommands.ExportConfigAsync, exportPathArg);
        command.AddCommand(exportCommand);

        // config import
        var importCommand = new Command("import", "Import configuration");
        var importPathArg = new Argument<string>("path", "Import file path");
        var mergeOption = new Option<bool>("--merge", "Merge with existing config");
        importCommand.AddArgument(importPathArg);
        importCommand.AddOption(mergeOption);
        importCommand.SetHandler(ConfigCommands.ImportConfigAsync, importPathArg, mergeOption);
        command.AddCommand(importCommand);

        return command;
    }

    private static Command CreateAuditCommand()
    {
        var command = new Command("audit", "Audit log operations");

        // audit list
        var listCommand = new Command("list", "List audit log entries");
        var limitOption = new Option<int>("--limit", () => 50, "Maximum entries to show");
        var categoryOption = new Option<string?>("--category", "Filter by category");
        var userOption = new Option<string?>("--user", "Filter by user");
        var sinceOption = new Option<DateTime?>("--since", "Show entries since date");
        listCommand.AddOption(limitOption);
        listCommand.AddOption(categoryOption);
        listCommand.AddOption(userOption);
        listCommand.AddOption(sinceOption);
        listCommand.SetHandler(AuditCommands.ListEntriesAsync, limitOption, categoryOption, userOption, sinceOption);
        command.AddCommand(listCommand);

        // audit export
        var exportCommand = new Command("export", "Export audit log");
        var exportPathArg = new Argument<string>("path", "Export file path");
        var formatOption = new Option<string>("--format", () => "json", "Export format (json, csv)");
        exportCommand.AddArgument(exportPathArg);
        exportCommand.AddOption(formatOption);
        exportCommand.SetHandler(AuditCommands.ExportAuditLogAsync, exportPathArg, formatOption);
        command.AddCommand(exportCommand);

        // audit stats
        var statsCommand = new Command("stats", "Show audit statistics");
        var periodOption = new Option<string>("--period", () => "24h", "Time period (1h, 24h, 7d, 30d)");
        statsCommand.AddOption(periodOption);
        statsCommand.SetHandler(AuditCommands.ShowStatsAsync, periodOption);
        command.AddCommand(statsCommand);

        return command;
    }

    private static Command CreateBenchmarkCommand()
    {
        var command = new Command("benchmark", "Run performance benchmarks");

        // benchmark run
        var runCommand = new Command("run", "Run benchmarks");
        var typeOption = new Option<string>("--type", () => "all", "Benchmark type (storage, raid, pipeline, all)");
        var durationOption = new Option<int>("--duration", () => 30, "Test duration in seconds");
        var sizeOption = new Option<string>("--size", () => "1MB", "Test data size");
        runCommand.AddOption(typeOption);
        runCommand.AddOption(durationOption);
        runCommand.AddOption(sizeOption);
        runCommand.SetHandler(BenchmarkCommands.RunBenchmarkAsync, typeOption, durationOption, sizeOption);
        command.AddCommand(runCommand);

        // benchmark report
        var reportCommand = new Command("report", "Show benchmark report");
        var reportIdOption = new Option<string?>("--id", "Specific benchmark ID");
        reportCommand.AddOption(reportIdOption);
        reportCommand.SetHandler(BenchmarkCommands.ShowReportAsync, reportIdOption);
        command.AddCommand(reportCommand);

        return command;
    }

    private static Command CreateServerCommand()
    {
        var command = new Command("server", "Server management");

        // server start
        var startCommand = new Command("start", "Start DataWarehouse server");
        var portOption = new Option<int>("--port", () => 5000, "Server port");
        var modeOption = new Option<string>("--mode", () => "Workstation", "Operating mode");
        startCommand.AddOption(portOption);
        startCommand.AddOption(modeOption);
        startCommand.SetHandler(ServerCommands.StartServerAsync, portOption, modeOption);
        command.AddCommand(startCommand);

        // server stop
        var stopCommand = new Command("stop", "Stop DataWarehouse server");
        var gracefulOption = new Option<bool>("--graceful", () => true, "Graceful shutdown");
        stopCommand.AddOption(gracefulOption);
        stopCommand.SetHandler(ServerCommands.StopServerAsync, gracefulOption);
        command.AddCommand(stopCommand);

        // server status
        var statusCommand = new Command("status", "Show server status");
        statusCommand.SetHandler(ServerCommands.ShowStatusAsync);
        command.AddCommand(statusCommand);

        // server info
        var infoCommand = new Command("info", "Show server information");
        infoCommand.SetHandler(ServerCommands.ShowInfoAsync);
        command.AddCommand(infoCommand);

        return command;
    }
}

public enum OutputFormat
{
    Table,
    Json,
    Yaml
}
