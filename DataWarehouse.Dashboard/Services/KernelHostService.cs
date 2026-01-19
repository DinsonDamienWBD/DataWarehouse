using DataWarehouse.Kernel;
using DataWarehouse.Kernel.Plugins;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Hosts and manages the DataWarehouse Kernel instance for the Dashboard.
/// Provides access to Kernel services and handles lifecycle management.
/// </summary>
public interface IKernelHostService
{
    /// <summary>
    /// Gets the hosted Kernel instance.
    /// </summary>
    DataWarehouseKernel? Kernel { get; }

    /// <summary>
    /// Whether the Kernel is initialized and ready.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets the Kernel's plugin registry.
    /// </summary>
    PluginRegistry? Plugins { get; }

    /// <summary>
    /// Gets the message bus for inter-plugin communication.
    /// </summary>
    IMessageBus? MessageBus { get; }

    /// <summary>
    /// Gets the pipeline orchestrator.
    /// </summary>
    IPipelineOrchestrator? PipelineOrchestrator { get; }

    /// <summary>
    /// Event raised when Kernel state changes.
    /// </summary>
    event EventHandler<KernelStateChangedEventArgs>? StateChanged;
}

public class KernelStateChangedEventArgs : EventArgs
{
    public bool IsReady { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Background service that initializes and manages the DataWarehouse Kernel.
/// </summary>
public class KernelHostService : BackgroundService, IKernelHostService
{
    private readonly ILogger<KernelHostService> _logger;
    private readonly IConfiguration _configuration;
    private DataWarehouseKernel? _kernel;
    private bool _isReady;

    public event EventHandler<KernelStateChangedEventArgs>? StateChanged;

    public DataWarehouseKernel? Kernel => _kernel;
    public bool IsReady => _isReady;
    public PluginRegistry? Plugins => _kernel?.Plugins;
    public IMessageBus? MessageBus => _kernel?.MessageBus;
    public IPipelineOrchestrator? PipelineOrchestrator => _kernel?.PipelineOrchestrator;

    public KernelHostService(ILogger<KernelHostService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Initializing DataWarehouse Kernel...");

        try
        {
            // Get configuration from appsettings
            var operatingMode = _configuration.GetValue("Kernel:OperatingMode", "Workstation");
            var pluginPath = _configuration.GetValue<string>("Kernel:PluginPath")
                ?? Path.Combine(AppContext.BaseDirectory, "Plugins");

            // Build and initialize the Kernel
            _kernel = await KernelBuilder.Create()
                .WithKernelId($"dashboard-{Environment.MachineName}")
                .WithOperatingMode(Enum.Parse<OperatingMode>(operatingMode, ignoreCase: true))
                .WithPluginPath(pluginPath)
                .BuildAndInitializeAsync(stoppingToken);

            _isReady = true;
            _logger.LogInformation("DataWarehouse Kernel initialized successfully. KernelId: {KernelId}", _kernel.KernelId);

            StateChanged?.Invoke(this, new KernelStateChangedEventArgs
            {
                IsReady = true,
                Message = $"Kernel {_kernel.KernelId} ready"
            });

            // Keep running until stopped
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Kernel host service stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DataWarehouse Kernel");
            StateChanged?.Invoke(this, new KernelStateChangedEventArgs
            {
                IsReady = false,
                Message = $"Kernel initialization failed: {ex.Message}"
            });
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Shutting down DataWarehouse Kernel...");

        if (_kernel != null)
        {
            await _kernel.DisposeAsync();
            _kernel = null;
        }

        _isReady = false;
        StateChanged?.Invoke(this, new KernelStateChangedEventArgs
        {
            IsReady = false,
            Message = "Kernel shutdown complete"
        });

        await base.StopAsync(cancellationToken);
    }
}
