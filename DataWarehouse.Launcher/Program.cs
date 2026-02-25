using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using DataWarehouse.Launcher.Integration;
using DataWarehouse.Launcher.Adapters;
using DataWarehouse.SDK.Hosting;

namespace DataWarehouse.Launcher;

/// <summary>
/// DataWarehouse Service Launcher - Service-only runtime.
///
/// The Launcher is now exclusively for running DataWarehouse as a service (daemon).
/// For Install, Connect, and Embedded modes, use the CLI or GUI applications.
///
/// Architecture (per TODO.md recommendations):
/// - Launcher = runtime (the service, runs 24/7, exposes gRPC/REST)
/// - CLI/GUI = management tools (clients for Install, Connect, Embedded)
///
/// Usage:
///   DataWarehouse.Launcher [options]
///
/// Options:
///   --kernel-mode &lt;mode&gt;    Kernel operating mode: Laptop, Workstation, Server, Hyperscale (default: Server)
///   --kernel-id &lt;id&gt;        Unique kernel identifier (default: auto-generated)
///   --profile &lt;profile&gt;     Service profile: server, client, auto (default: auto)
///   --plugin-path &lt;path&gt;    Path to plugins directory (default: ./plugins)
///   --config &lt;path&gt;         Path to configuration file (default: appsettings.json)
///   --log-level &lt;level&gt;     Log level: Debug, Info, Warning, Error (default: Info)
///   --log-path &lt;path&gt;       Path for log files (default: ./logs)
///   --help                  Show this help message
///
/// Examples:
///   # Run as service (default)
///   DataWarehouse.Launcher
///
///   # Run with custom configuration
///   DataWarehouse.Launcher --kernel-mode Hyperscale --log-level Debug
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
        var options = ServiceOptions.FromConfiguration(configuration);

        // Show help if requested
        if (options.ShowHelp)
        {
            ShowHelp();
            return 0;
        }

        // Setup logging
        var loggerFactory = SetupLogging(options);

        // Display banner
        DisplayBanner(options);

        // Register the DataWarehouse adapter
        AdapterFactory.Register<DataWarehouseAdapter>("DataWarehouse");
        AdapterFactory.SetDefault("DataWarehouse");
        Log.Debug("Registered DataWarehouse adapter");

        // Create the service host
        await using var host = new ServiceHost(loggerFactory);

        // Setup graceful shutdown handlers
        var cts = new CancellationTokenSource();
        SetupShutdownHandlers(cts);

        try
        {
            Log.Information("=== SERVICE MODE ===");
            Log.Information("Starting as {OS} service/daemon with profile {Profile}",
                OperatingSystem.IsWindows() ? "Windows" :
                OperatingSystem.IsLinux() ? "Linux" : "system",
                options.Profile);

            var result = await host.RunAsync(options, cts.Token);

            Log.Information("Service shutdown complete");
            return result;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DataWarehouse service terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static ILoggerFactory SetupLogging(ServiceOptions options)
    {
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(options.LogLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .Enrich.WithProperty("EnvironmentName", Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production")
            .Enrich.WithProperty("Mode", "Service")
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

    private static void DisplayBanner(ServiceOptions options)
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

        Console.WriteLine($"  Version 2.0.0 | .NET {Environment.Version}");
        Console.WriteLine($"  Mode: Service (daemon) | Profile: {options.Profile}");
        Console.WriteLine();
    }

    private static void SetupShutdownHandlers(CancellationTokenSource cts)
    {
        // Handle Ctrl+C (SIGINT)
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Information("Shutdown signal received (Ctrl+C)");
            cts.Cancel();
        };

        // Handle process termination (SIGTERM on Linux/macOS)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Process exit requested");
            cts.Cancel();
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

    private static void ShowHelp()
    {
        Console.WriteLine(@"
DataWarehouse Service Launcher (Service-Only)

USAGE:
    DataWarehouse.Launcher [options]

OPTIONS:
    --kernel-mode <mode>    Kernel mode: Laptop, Workstation, Server, Hyperscale (default: Server)
    --kernel-id <id>        Unique kernel identifier
    --profile <profile>     Service profile: server, client, auto (default: auto)
                            server  - Load full server plugin set (dispatchers, storage, intelligence)
                            client  - Load minimal client set (courier, watchdog, policy)
                            auto    - Auto-detect from available plugins and configuration
    --plugin-path <path>    Path to plugins directory (default: ./plugins)
    --config <path>         Path to configuration file
    --log-level <level>     Log level: Debug, Info, Warning, Error (default: Info)
    --log-path <path>       Path for log files (default: ./logs)
    --help                  Show this help message

NOTE:
    For Install, Connect, and Embedded modes, use the CLI or GUI applications.
    The Launcher is exclusively for running DataWarehouse as a service/daemon.

EXAMPLES:
    # Run as server daemon (default)
    DataWarehouse.Launcher --profile server

    # Run as client daemon
    DataWarehouse.Launcher --profile client

    # Auto-detect profile from available plugins
    DataWarehouse.Launcher --profile auto

    # Run with custom configuration
    DataWarehouse.Launcher --kernel-mode Hyperscale --log-level Debug

    # For installation, use CLI:
    dw --mode install --path C:/DataWarehouse

    # For connection management, use CLI:
    dw --mode connect --host 192.168.1.100
");
    }
}

/// <summary>
/// Configuration options for the DataWarehouse Service.
/// </summary>
public sealed class ServiceOptions
{
    /// <summary>
    /// Kernel operating mode (Laptop, Workstation, Server, Hyperscale).
    /// </summary>
    public string KernelMode { get; set; } = "Server";

    /// <summary>
    /// Unique kernel identifier.
    /// </summary>
    public string KernelId { get; set; } = $"service-{Environment.MachineName.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..6]}";

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
    /// Show help and exit.
    /// </summary>
    public bool ShowHelp { get; set; }

    /// <summary>
    /// Whether to enable the HTTP API server.
    /// </summary>
    public bool EnableHttp { get; set; } = true;

    /// <summary>
    /// Port for the HTTP API server.
    /// Configurable via appsettings.json ("Http:Port"), command-line ("--http-port"),
    /// or environment variable ("DW_HTTP_PORT").
    /// </summary>
    public int HttpPort { get; set; } = 8080;

    /// <summary>
    /// Service profile type for plugin loading.
    /// Determines which plugins are loaded at startup based on deployment role.
    /// Configurable via command-line ("--profile"), appsettings.json ("Profile"), or env ("DW_PROFILE").
    /// </summary>
    public ServiceProfileType Profile { get; set; } = ServiceProfileType.Auto;

    /// <summary>
    /// Creates options from configuration.
    /// </summary>
    public static ServiceOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new ServiceOptions();

        // Parse kernel settings
        options.KernelMode = configuration["kernel-mode"] ?? configuration["KernelMode"] ?? "Server";
        options.KernelId = configuration["kernel-id"] ?? configuration["KernelId"] ?? options.KernelId;

        // Parse paths
        options.PluginPath = configuration["plugin-path"] ?? configuration["PluginPath"] ?? "./plugins";
        options.ConfigPath = configuration["config"] ?? configuration["ConfigPath"];
        options.LogPath = configuration["log-path"] ?? configuration["LogPath"] ?? "./logs";

        // Parse log level
        var logLevelStr = configuration["log-level"] ?? configuration["LogLevel"];
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

        // Parse service profile
        var profileStr = configuration["profile"] ?? configuration["Profile"];
        if (!string.IsNullOrEmpty(profileStr))
        {
            options.Profile = profileStr.ToLowerInvariant() switch
            {
                "server" => ServiceProfileType.Server,
                "client" => ServiceProfileType.Client,
                "both" => ServiceProfileType.Both,
                "auto" => ServiceProfileType.Auto,
                "none" => ServiceProfileType.None,
                _ => ServiceProfileType.Auto
            };
        }

        // Check for help
        var helpStr = configuration["help"] ?? configuration["Help"] ?? configuration["?"];
        options.ShowHelp = !string.IsNullOrEmpty(helpStr);

        return options;
    }
}
