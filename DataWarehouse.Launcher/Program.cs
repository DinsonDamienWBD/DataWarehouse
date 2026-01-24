using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using DataWarehouse.Launcher.Adapters;

namespace DataWarehouse.Launcher;

/// <summary>
/// DataWarehouse Production Launcher with Plug-and-Play Adapter Pattern
///
/// This launcher uses a reusable adapter pattern that can be copied to other projects.
/// To use this pattern in your own project:
/// 1. Copy the entire Adapters folder to your project
/// 2. Implement IKernelAdapter for your specific kernel/service
/// 3. Register your adapter with AdapterFactory
/// 4. Use AdapterRunner to execute your application
///
/// Usage:
///   DataWarehouse.Launcher [options]
///
/// Options:
///   --mode <mode>           Operating mode: Laptop, Workstation, Server, Hyperscale (default: Workstation)
///   --adapter <type>        Adapter type to use (default: DataWarehouse)
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
        var loggerFactory = SetupLogging(options);

        // Display banner
        DisplayBanner(options);

        // Register adapters
        RegisterAdapters();

        // Create the runner
        await using var runner = new AdapterRunner(loggerFactory);

        // Setup graceful shutdown handlers
        SetupShutdownHandlers(runner);

        try
        {
            // Convert to adapter options
            var adapterOptions = new AdapterOptions
            {
                KernelId = options.KernelId,
                OperatingMode = options.Mode,
                PluginPath = options.PluginPath,
                ConfigPath = options.ConfigPath,
                LoggerFactory = loggerFactory
            };

            // Run the adapter
            var result = await runner.RunAsync(adapterOptions, options.AdapterType);

            Log.Information("DataWarehouse shutdown complete");
            return result;
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

    /// <summary>
    /// Registers all available adapters with the factory.
    /// Add custom adapters here or load them dynamically from plugins.
    /// </summary>
    private static void RegisterAdapters()
    {
        // Register the DataWarehouse adapter as default
        AdapterFactory.Register<DataWarehouseAdapter>("DataWarehouse");
        AdapterFactory.SetDefault("DataWarehouse");

        // Additional adapters can be registered here:
        // AdapterFactory.Register("CustomKernel", () => new CustomKernelAdapter());

        Log.Debug("Registered adapters: {Types}", string.Join(", ", AdapterFactory.GetRegisteredTypes()));
    }

    private static ILoggerFactory SetupLogging(LauncherOptions options)
    {
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(options.LogLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .Enrich.WithProperty("EnvironmentName", Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production")
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

        return LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(Log.Logger);
        });
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
        Console.WriteLine($"  Mode: {options.Mode} | Adapter: {options.AdapterType} | Kernel ID: {options.KernelId}");
        Console.WriteLine();
    }

    private static void SetupShutdownHandlers(AdapterRunner runner)
    {
        // Handle Ctrl+C (SIGINT)
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Information("Shutdown signal received (Ctrl+C)");
            runner.RequestShutdown();
        };

        // Handle process termination (SIGTERM on Linux/macOS)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Process exit requested");
            runner.RequestShutdown();
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
}

/// <summary>
/// Configuration options for the DataWarehouse Launcher.
/// </summary>
public sealed class LauncherOptions
{
    /// <summary>
    /// Adapter type to use.
    /// </summary>
    public string AdapterType { get; set; } = "DataWarehouse";

    /// <summary>
    /// Operating mode (Laptop, Workstation, Server, Hyperscale).
    /// </summary>
    public string Mode { get; set; } = "Workstation";

    /// <summary>
    /// Unique kernel identifier.
    /// </summary>
    public string KernelId { get; set; } = $"dw-{Environment.MachineName.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>
    /// Path to plugins directory.
    /// </summary>
    public string PluginPath { get; set; } = "./plugins";

    /// <summary>
    /// Path to configuration file.
    /// </summary>
    public string? ConfigPath { get; set; }

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

        // Parse adapter type
        var adapterType = configuration["adapter"] ?? configuration["Adapter"] ?? configuration["Kernel:Adapter"];
        if (!string.IsNullOrEmpty(adapterType))
        {
            options.AdapterType = adapterType;
        }

        // Parse mode
        var modeStr = configuration["mode"] ?? configuration["Mode"] ?? configuration["Kernel:Mode"];
        if (!string.IsNullOrEmpty(modeStr))
        {
            options.Mode = modeStr;
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

        // Parse config path
        var configPath = configuration["config"] ?? configuration["ConfigPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            options.ConfigPath = configPath;
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
