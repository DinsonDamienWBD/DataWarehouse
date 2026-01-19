using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using DataWarehouse.Kernel;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Launcher;

/// <summary>
/// DataWarehouse Production Launcher
///
/// A lightweight, production-ready launcher that initializes and runs the DataWarehouse Kernel
/// as a standalone process. Supports all deployment scenarios from laptops to hyperscale clusters.
///
/// Usage:
///   DataWarehouse.Launcher [options]
///
/// Options:
///   --mode <mode>           Operating mode: Laptop, Workstation, Server, Hyperscale (default: Workstation)
///   --kernel-id <id>        Unique kernel identifier (default: auto-generated)
///   --plugin-path <path>    Path to plugins directory (default: ./plugins)
///   --config <path>         Path to configuration file (default: appsettings.json)
///   --log-level <level>     Log level: Debug, Info, Warning, Error (default: Info)
///   --log-path <path>       Path for log files (default: ./logs)
///   --daemon                Run as background daemon (Linux/macOS)
///   --help                  Show this help message
/// </summary>
public static class Program
{
    private static DataWarehouseKernel? _kernel;
    private static readonly CancellationTokenSource _cts = new();
    private static ILogger<DataWarehouseKernel>? _logger;

    public static async Task<int> Main(string[] args)
    {
        // Setup configuration from multiple sources
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables("DW_")
            .AddCommandLine(args)
            .Build();

        // Parse command line options
        var options = LauncherOptions.FromConfiguration(configuration);

        // Setup logging
        SetupLogging(options);

        // Display banner
        DisplayBanner(options);

        // Setup graceful shutdown handlers
        SetupShutdownHandlers();

        try
        {
            // Initialize and run the kernel
            await RunKernelAsync(options, _cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log.Information("DataWarehouse shutdown requested");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DataWarehouse terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static void SetupLogging(LauncherOptions options)
    {
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(options.LogLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProperty("KernelId", options.KernelId)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code);

        if (!string.IsNullOrEmpty(options.LogPath))
        {
            Directory.CreateDirectory(options.LogPath);
            logConfig.WriteTo.File(
                Path.Combine(options.LogPath, "datawarehouse-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = logConfig.CreateLogger();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(Log.Logger);
        });

        _logger = loggerFactory.CreateLogger<DataWarehouseKernel>();
    }

    private static void DisplayBanner(LauncherOptions options)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  ____        _    __        __              _
 |  _ \  __ _| |_ __ \_      / /_ _ _ __ ___| |__   ___  _   _ ___  ___
 | | | |/ _` | __/ _` \ \ /\ / / _` | '__/ _ \ '_ \ / _ \| | | / __|/ _ \
 | |_| | (_| | || (_| |\ V  V / (_| | | |  __/ | | | (_) | |_| \__ \  __/
 |____/ \__,_|\__\__,_| \_/\_/ \__,_|_|  \___|_| |_|\___/ \__,_|___/\___|
");
        Console.ResetColor();

        Console.WriteLine($"  Version 1.0.0 | .NET {Environment.Version}");
        Console.WriteLine($"  Mode: {options.Mode} | Kernel ID: {options.KernelId}");
        Console.WriteLine();
    }

    private static void SetupShutdownHandlers()
    {
        // Handle Ctrl+C (SIGINT)
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Information("Shutdown signal received (Ctrl+C)");
            _cts.Cancel();
        };

        // Handle process termination (SIGTERM on Linux/macOS)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Process exit requested");
            _cts.Cancel();

            // Give kernel time to shutdown gracefully
            if (_kernel != null)
            {
                _kernel.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30));
            }
        };

        // Handle unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal((Exception)e.ExceptionObject, "Unhandled exception");
        };

        // Handle task exceptions
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    private static async Task RunKernelAsync(LauncherOptions options, CancellationToken cancellationToken)
    {
        Log.Information("Initializing DataWarehouse Kernel...");
        Log.Information("Operating Mode: {Mode}", options.Mode);
        Log.Information("Plugin Path: {PluginPath}", options.PluginPath);

        // Build and initialize the kernel
        var builder = KernelBuilder.Create()
            .WithKernelId(options.KernelId)
            .WithOperatingMode(options.Mode);

        if (!string.IsNullOrEmpty(options.PluginPath))
        {
            builder.WithPluginPath(options.PluginPath);
        }

        _kernel = await builder.BuildAndInitializeAsync(cancellationToken);

        Log.Information("DataWarehouse Kernel initialized successfully");
        Log.Information("Kernel ID: {KernelId}", _kernel.KernelId);
        Log.Information("Plugins loaded: {PluginCount}", _kernel.Plugins.GetAllPlugins().Count());

        // Print loaded plugins
        foreach (var plugin in _kernel.Plugins.GetAllPlugins())
        {
            Log.Debug("  Plugin: {PluginId} ({Category})", plugin.PluginId, plugin.Category);
        }

        Log.Information("DataWarehouse is now running. Press Ctrl+C to shutdown.");
        Console.WriteLine();

        // Keep running until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        // Graceful shutdown
        Log.Information("Initiating graceful shutdown...");

        await _kernel.DisposeAsync();
        _kernel = null;

        Log.Information("DataWarehouse shutdown complete");
    }
}

/// <summary>
/// Configuration options for the DataWarehouse Launcher.
/// </summary>
public sealed class LauncherOptions
{
    /// <summary>
    /// Operating mode (Laptop, Workstation, Server, Hyperscale).
    /// </summary>
    public OperatingMode Mode { get; set; } = OperatingMode.Workstation;

    /// <summary>
    /// Unique kernel identifier.
    /// </summary>
    public string KernelId { get; set; } = $"dw-{Environment.MachineName.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>
    /// Path to plugins directory.
    /// </summary>
    public string PluginPath { get; set; } = "./plugins";

    /// <summary>
    /// Log level.
    /// </summary>
    public LogEventLevel LogLevel { get; set; } = LogEventLevel.Information;

    /// <summary>
    /// Path for log files.
    /// </summary>
    public string LogPath { get; set; } = "./logs";

    /// <summary>
    /// Run as daemon (background process).
    /// </summary>
    public bool Daemon { get; set; } = false;

    /// <summary>
    /// Creates options from configuration.
    /// </summary>
    public static LauncherOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new LauncherOptions();

        // Parse mode
        var modeStr = configuration["mode"] ?? configuration["Mode"] ?? configuration["Kernel:Mode"];
        if (!string.IsNullOrEmpty(modeStr) && Enum.TryParse<OperatingMode>(modeStr, ignoreCase: true, out var mode))
        {
            options.Mode = mode;
        }

        // Parse kernel ID
        var kernelId = configuration["kernel-id"] ?? configuration["KernelId"] ?? configuration["Kernel:KernelId"];
        if (!string.IsNullOrEmpty(kernelId))
        {
            options.KernelId = kernelId;
        }

        // Parse plugin path
        var pluginPath = configuration["plugin-path"] ?? configuration["PluginPath"] ?? configuration["Kernel:PluginPath"];
        if (!string.IsNullOrEmpty(pluginPath))
        {
            options.PluginPath = pluginPath;
        }

        // Parse log level
        var logLevelStr = configuration["log-level"] ?? configuration["LogLevel"] ?? configuration["Logging:LogLevel:Default"];
        if (!string.IsNullOrEmpty(logLevelStr))
        {
            options.LogLevel = logLevelStr.ToLowerInvariant() switch
            {
                "debug" or "verbose" => LogEventLevel.Debug,
                "info" or "information" => LogEventLevel.Information,
                "warn" or "warning" => LogEventLevel.Warning,
                "error" => LogEventLevel.Error,
                "fatal" or "critical" => LogEventLevel.Fatal,
                _ => LogEventLevel.Information
            };
        }

        // Parse log path
        var logPath = configuration["log-path"] ?? configuration["LogPath"] ?? configuration["Logging:Path"];
        if (!string.IsNullOrEmpty(logPath))
        {
            options.LogPath = logPath;
        }

        // Parse daemon mode
        var daemonStr = configuration["daemon"];
        if (!string.IsNullOrEmpty(daemonStr) && bool.TryParse(daemonStr, out var daemon))
        {
            options.Daemon = daemon;
        }

        return options;
    }
}
