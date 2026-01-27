using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Launcher.Integration;

/// <summary>
/// DataWarehouse Service Host - Runs DataWarehouse as a Windows service or Linux daemon.
///
/// This is the simplified service-only host. For Install, Connect, and Embedded modes,
/// use the DataWarehouseHost class in CLI/GUI applications.
///
/// Architecture (per TODO.md recommendations):
/// - Launcher (ServiceHost) = runtime only
/// - CLI/GUI = management tools using DataWarehouseHost
/// </summary>
public sealed class ServiceHost : IAsyncDisposable
{
    private readonly ILogger<ServiceHost> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AdapterRunner _runner;
    private bool _disposed;

    /// <summary>
    /// Creates a new Service Host.
    /// </summary>
    public ServiceHost(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ServiceHost>();
        _runner = new AdapterRunner(_loggerFactory);
    }

    /// <summary>
    /// Runs the DataWarehouse service.
    /// </summary>
    /// <param name="options">Service configuration options.</param>
    /// <param name="cancellationToken">Cancellation token for graceful shutdown.</param>
    /// <returns>Exit code (0 for success).</returns>
    public async Task<int> RunAsync(ServiceOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogInformation("Starting DataWarehouse service");
        _logger.LogInformation("Kernel ID: {KernelId}", options.KernelId);
        _logger.LogInformation("Kernel Mode: {KernelMode}", options.KernelMode);
        _logger.LogInformation("Plugin Path: {PluginPath}", options.PluginPath);

        // Configure adapter options for service mode
        var adapterOptions = new AdapterOptions
        {
            KernelId = options.KernelId,
            OperatingMode = options.KernelMode,
            PluginPath = options.PluginPath,
            LoggerFactory = _loggerFactory,
            CustomConfig =
            {
                ["RunAsService"] = true,
                ["KernelMode"] = options.KernelMode
            }
        };

        // Run the adapter (blocks until shutdown)
        return await _runner.RunAsync(adapterOptions, "DataWarehouse", cancellationToken);
    }

    /// <summary>
    /// Requests graceful shutdown of the service.
    /// </summary>
    public void RequestShutdown()
    {
        _logger.LogInformation("Shutdown requested");
        _runner.RequestShutdown();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("Disposing service host");
        await _runner.DisposeAsync();
    }
}
