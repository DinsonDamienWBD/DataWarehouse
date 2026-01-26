using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using DataWarehouse.Launcher.Integration;
using DataWarehouse.Launcher.Adapters;

namespace DataWarehouse.Launcher;

/// <summary>
/// DataWarehouse Service Host - The new unified launcher supporting multiple modes.
///
/// This launcher uses the DataWarehouseHost pattern providing 4 operating modes:
/// 1. Install - Install a new DataWarehouse instance
/// 2. Connect - Connect to an existing instance (local or remote)
/// 3. Embedded - Run a lightweight embedded instance (default, backward compatible)
/// 4. Service - Run as a Windows service or Linux daemon
///
/// Usage:
///   DataWarehouse.Launcher [options]
///
/// Options:
///   --mode <mode>           Operating mode: install, connect, embedded, service (default: embedded)
///   --path <path>           Installation path (for install mode) or connection path (for connect mode)
///   --host <host>           Remote host (for connect mode with remote connection)
///   --port <port>           Remote port (for connect mode, default: 8080)
///   --kernel-mode <mode>    Kernel operating mode: Laptop, Workstation, Server, Hyperscale (default: Workstation)
///   --kernel-id <id>        Unique kernel identifier (default: auto-generated)
///   --plugin-path <path>    Path to plugins directory (default: ./plugins)
///   --config <path>         Path to configuration file (default: appsettings.json)
///   --log-level <level>     Log level: Debug, Info, Warning, Error (default: Info)
///   --log-path <path>       Path for log files (default: ./logs)
///   --help                  Show this help message
///
/// Examples:
///   # Run embedded (default, backward compatible)
///   DataWarehouse.Launcher
///
///   # Install new instance
///   DataWarehouse.Launcher --mode install --path C:/DataWarehouse
///
///   # Connect to remote and manage
///   DataWarehouse.Launcher --mode connect --host 192.168.1.100 --port 8080
///
///   # Run as service
///   DataWarehouse.Launcher --mode service
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

        // Register adapters (for backward compatibility with old adapter pattern)
        RegisterAdapters();

        // Create the DataWarehouse host
        await using var host = new DataWarehouseHost(loggerFactory);

        // Setup graceful shutdown handlers
        var cts = new CancellationTokenSource();
        SetupShutdownHandlers(cts);

        try
        {
            // Execute based on operating mode
            return options.Mode switch
            {
                OperatingMode.Install => await RunInstallModeAsync(host, options, cts.Token),
                OperatingMode.Connect => await RunConnectModeAsync(host, options, cts.Token),
                OperatingMode.Embedded => await RunEmbeddedModeAsync(host, options, cts.Token),
                OperatingMode.Service => await RunServiceModeAsync(host, options, cts.Token),
                _ => throw new InvalidOperationException($"Unknown operating mode: {options.Mode}")
            };
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
    /// Runs in Install mode - sets up a new DataWarehouse instance.
    /// </summary>
    private static async Task<int> RunInstallModeAsync(
        DataWarehouseHost host,
        LauncherOptions options,
        CancellationToken cancellationToken)
    {
        Log.Information("=== INSTALL MODE ===");

        var installConfig = new InstallConfiguration
        {
            InstallPath = options.InstallPath ?? throw new ArgumentException("--path is required for install mode"),
            DataPath = options.DataPath,
            CreateService = options.CreateService,
            AutoStart = options.AutoStart,
            CreateDefaultAdmin = true,
            AdminUsername = "admin",
            AdminPassword = options.AdminPassword ?? "admin123" // In production, prompt for this
        };

        var progress = new Progress<InstallProgress>(p =>
        {
            Log.Information("[{Percent}%] {Step}: {Message}",
                p.PercentComplete, p.Step, p.Message);
        });

        var result = await host.InstallAsync(installConfig, progress, cancellationToken);

        if (result.Success)
        {
            Log.Information("Installation completed successfully at: {Path}", result.InstallPath);
            return 0;
        }
        else
        {
            Log.Error("Installation failed: {Message}", result.Message);
            return 1;
        }
    }

    /// <summary>
    /// Runs in Connect mode - connects to an existing instance for management.
    /// </summary>
    private static async Task<int> RunConnectModeAsync(
        DataWarehouseHost host,
        LauncherOptions options,
        CancellationToken cancellationToken)
    {
        Log.Information("=== CONNECT MODE ===");

        // Determine connection type and target
        ConnectionTarget target;

        if (!string.IsNullOrEmpty(options.RemoteHost))
        {
            // Remote connection
            target = ConnectionTarget.Remote(
                options.RemoteHost,
                options.RemotePort,
                options.UseTls);
            Log.Information("Connecting to remote instance: {Host}:{Port}", options.RemoteHost, options.RemotePort);
        }
        else if (!string.IsNullOrEmpty(options.InstallPath))
        {
            // Local connection
            target = ConnectionTarget.Local(options.InstallPath);
            Log.Information("Connecting to local instance: {Path}", options.InstallPath);
        }
        else
        {
            throw new ArgumentException("Either --host or --path is required for connect mode");
        }

        var result = await host.ConnectAsync(target, cancellationToken);

        if (result.Success)
        {
            Log.Information("Connected to instance: {InstanceId}", result.InstanceId);
            Log.Information("Capabilities: {PluginCount} plugins, Version {Version}",
                result.Capabilities?.AvailablePlugins.Count ?? 0,
                result.Capabilities?.Version ?? "unknown");

            // Keep connection alive until shutdown
            Log.Information("Connection established. Press Ctrl+C to disconnect.");
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }

            await host.DisconnectAsync();
            Log.Information("Disconnected from instance");
            return 0;
        }
        else
        {
            Log.Error("Connection failed: {Message}", result.Message);
            return 1;
        }
    }

    /// <summary>
    /// Runs in Embedded mode - lightweight in-process instance (default, backward compatible).
    /// </summary>
    private static async Task<int> RunEmbeddedModeAsync(
        DataWarehouseHost host,
        LauncherOptions options,
        CancellationToken cancellationToken)
    {
        Log.Information("=== EMBEDDED MODE ===");

        var embeddedConfig = new EmbeddedConfiguration
        {
            PersistData = true,
            DataPath = options.DataPath ?? options.PluginPath,
            MaxMemoryMb = 256,
            ExposeHttp = false,
            HttpPort = 8080
        };

        Log.Information("Starting embedded DataWarehouse instance...");
        var result = await host.RunEmbeddedAsync(embeddedConfig, cancellationToken);

        Log.Information("Embedded instance shutdown complete");
        return result;
    }

    /// <summary>
    /// Runs in Service mode - Windows service or Linux daemon.
    /// </summary>
    private static async Task<int> RunServiceModeAsync(
        DataWarehouseHost host,
        LauncherOptions options,
        CancellationToken cancellationToken)
    {
        Log.Information("=== SERVICE MODE ===");

        Log.Information("Starting as {OS} service/daemon",
            OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsLinux() ? "Linux" : "system");

        var result = await host.RunServiceAsync(cancellationToken);

        Log.Information("Service shutdown complete");
        return result;
    }

    /// <summary>
    /// Registers all available adapters with the factory (for backward compatibility).
    /// </summary>
    private static void RegisterAdapters()
    {
        // Register the DataWarehouse adapter as default
        AdapterFactory.Register<DataWarehouseAdapter>("DataWarehouse");
        AdapterFactory.SetDefault("DataWarehouse");

        // Register the Embedded adapter
        AdapterFactory.Register<EmbeddedAdapter>("Embedded");

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
            .Enrich.WithProperty("Mode", options.Mode.ToString())
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

        Console.WriteLine($"  Version 2.0.0 | .NET {Environment.Version}");
        Console.WriteLine($"  Mode: {options.Mode}");
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
DataWarehouse Service Host

USAGE:
    DataWarehouse.Launcher [options]

OPTIONS:
    --mode <mode>           Operating mode: install, connect, embedded, service (default: embedded)
    --path <path>           Installation/connection path
    --host <host>           Remote host for connect mode
    --port <port>           Remote port (default: 8080)
    --kernel-mode <mode>    Kernel mode: Laptop, Workstation, Server, Hyperscale
    --kernel-id <id>        Unique kernel identifier
    --plugin-path <path>    Path to plugins directory
    --config <path>         Path to configuration file
    --log-level <level>     Log level: Debug, Info, Warning, Error
    --log-path <path>       Path for log files
    --help                  Show this help message

MODES:
    embedded (default)      Run lightweight embedded instance
    install                 Install new DataWarehouse instance
    connect                 Connect to existing instance
    service                 Run as Windows service or Linux daemon

EXAMPLES:
    # Run embedded (default)
    DataWarehouse.Launcher

    # Install new instance
    DataWarehouse.Launcher --mode install --path C:/DataWarehouse

    # Connect to remote instance
    DataWarehouse.Launcher --mode connect --host 192.168.1.100

    # Run as service
    DataWarehouse.Launcher --mode service
");
    }
}

/// <summary>
/// Configuration options for the DataWarehouse Launcher.
/// Now includes support for all 4 operating modes.
/// </summary>
public sealed class LauncherOptions
{
    /// <summary>
    /// Operating mode (Install, Connect, Embedded, Service).
    /// </summary>
    public OperatingMode Mode { get; set; } = OperatingMode.Embedded;

    /// <summary>
    /// Installation path (for Install mode) or connection path (for Connect to local).
    /// </summary>
    public string? InstallPath { get; set; }

    /// <summary>
    /// Data storage path.
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// Remote host for Connect mode with remote connection.
    /// </summary>
    public string? RemoteHost { get; set; }

    /// <summary>
    /// Remote port for Connect mode.
    /// </summary>
    public int RemotePort { get; set; } = 8080;

    /// <summary>
    /// Use TLS for remote connections.
    /// </summary>
    public bool UseTls { get; set; } = true;

    /// <summary>
    /// Kernel operating mode (Laptop, Workstation, Server, Hyperscale).
    /// </summary>
    public string KernelMode { get; set; } = "Workstation";

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
    /// Create service during install.
    /// </summary>
    public bool CreateService { get; set; }

    /// <summary>
    /// Auto-start on boot.
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// Admin password for Install mode.
    /// </summary>
    public string? AdminPassword { get; set; }

    /// <summary>
    /// Show help and exit.
    /// </summary>
    public bool ShowHelp { get; set; }

    /// <summary>
    /// Creates options from configuration.
    /// </summary>
    public static LauncherOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new LauncherOptions();

        // Parse mode
        var modeStr = configuration["mode"] ?? configuration["Mode"];
        if (!string.IsNullOrEmpty(modeStr))
        {
            options.Mode = modeStr.ToLowerInvariant() switch
            {
                "install" => OperatingMode.Install,
                "connect" => OperatingMode.Connect,
                "embedded" => OperatingMode.Embedded,
                "service" => OperatingMode.Service,
                _ => OperatingMode.Embedded
            };
        }

        // Parse paths
        options.InstallPath = configuration["path"] ?? configuration["InstallPath"];
        options.DataPath = configuration["data-path"] ?? configuration["DataPath"];
        options.PluginPath = configuration["plugin-path"] ?? configuration["PluginPath"] ?? "./plugins";
        options.ConfigPath = configuration["config"] ?? configuration["ConfigPath"];
        options.LogPath = configuration["log-path"] ?? configuration["LogPath"] ?? "./logs";

        // Parse remote connection settings
        options.RemoteHost = configuration["host"] ?? configuration["Host"];
        var portStr = configuration["port"] ?? configuration["Port"];
        if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out var port))
        {
            options.RemotePort = port;
        }

        var tlsStr = configuration["tls"] ?? configuration["UseTls"];
        if (!string.IsNullOrEmpty(tlsStr) && bool.TryParse(tlsStr, out var useTls))
        {
            options.UseTls = useTls;
        }

        // Parse kernel settings
        options.KernelMode = configuration["kernel-mode"] ?? configuration["KernelMode"] ?? "Workstation";
        options.KernelId = configuration["kernel-id"] ?? configuration["KernelId"] ?? options.KernelId;

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

        // Parse install options
        var createServiceStr = configuration["create-service"] ?? configuration["CreateService"];
        if (!string.IsNullOrEmpty(createServiceStr) && bool.TryParse(createServiceStr, out var createService))
        {
            options.CreateService = createService;
        }

        var autoStartStr = configuration["auto-start"] ?? configuration["AutoStart"];
        if (!string.IsNullOrEmpty(autoStartStr) && bool.TryParse(autoStartStr, out var autoStart))
        {
            options.AutoStart = autoStart;
        }

        options.AdminPassword = configuration["admin-password"] ?? configuration["AdminPassword"];

        // Check for help
        var helpStr = configuration["help"] ?? configuration["Help"] ?? configuration["?"];
        options.ShowHelp = !string.IsNullOrEmpty(helpStr);

        return options;
    }
}
