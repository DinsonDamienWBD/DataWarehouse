using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DataWarehouse.Kernel;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Launcher.Adapters;

/// <summary>
/// DataWarehouse-specific kernel adapter implementation.
/// This adapter wraps the DataWarehouse Kernel for use with the plug-and-play launcher.
/// </summary>
public sealed class DataWarehouseAdapter : IKernelAdapter
{
    private DataWarehouseKernel? _kernel;
    private ILogger<DataWarehouseAdapter> _logger = NullLogger<DataWarehouseAdapter>.Instance;
    private KernelState _state = KernelState.Uninitialized;
    private DateTime _startedAt;
    private long _operationsProcessed;

    public string KernelId => _kernel?.KernelId ?? "";

    public KernelState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                var previous = _state;
                _state = value;
                StateChanged?.Invoke(this, new KernelStateChangedEventArgs
                {
                    PreviousState = previous,
                    NewState = value
                });
            }
        }
    }

    public event EventHandler<KernelStateChangedEventArgs>? StateChanged;

    public async Task InitializeAsync(AdapterOptions options, CancellationToken cancellationToken = default)
    {
        if (State != KernelState.Uninitialized)
        {
            throw new InvalidOperationException($"Cannot initialize adapter in state {State}");
        }

        State = KernelState.Initializing;

        try
        {
            if (options.LoggerFactory != null)
            {
                _logger = options.LoggerFactory.CreateLogger<DataWarehouseAdapter>();
            }

            _logger.LogInformation("Initializing DataWarehouse adapter...");

            // Parse operating mode
            var mode = ParseOperatingMode(options.OperatingMode);

            // Build the kernel
            var builder = KernelBuilder.Create()
                .WithKernelId(options.KernelId)
                .WithOperatingMode(mode);

            if (!string.IsNullOrEmpty(options.PluginPath))
            {
                builder.WithPluginPath(options.PluginPath);
            }

            _kernel = await builder.BuildAndInitializeAsync(cancellationToken);

            _logger.LogInformation("DataWarehouse kernel initialized: {KernelId}", _kernel.KernelId);
            _logger.LogInformation("Plugins loaded: {Count}", _kernel.Plugins.GetAllPlugins().Count());

            State = KernelState.Ready;
        }
        catch (Exception ex)
        {
            State = KernelState.Failed;
            StateChanged?.Invoke(this, new KernelStateChangedEventArgs
            {
                PreviousState = KernelState.Initializing,
                NewState = KernelState.Failed,
                Message = "Initialization failed",
                Exception = ex
            });
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State != KernelState.Ready)
        {
            throw new InvalidOperationException($"Cannot start adapter in state {State}");
        }

        _logger.LogInformation("Starting DataWarehouse adapter...");
        _startedAt = DateTime.UtcNow;
        State = KernelState.Running;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (State != KernelState.Running)
        {
            _logger.LogWarning("Adapter not running, skipping stop");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Stopping DataWarehouse adapter...");
        State = KernelState.Stopping;

        // The actual cleanup happens in DisposeAsync
        State = KernelState.Stopped;
        return Task.CompletedTask;
    }

    public KernelStats GetStats()
    {
        return new KernelStats
        {
            KernelId = KernelId,
            State = State,
            StartedAt = _startedAt,
            Uptime = State == KernelState.Running ? DateTime.UtcNow - _startedAt : TimeSpan.Zero,
            PluginCount = _kernel?.Plugins.GetAllPlugins().Count() ?? 0,
            OperationsProcessed = _operationsProcessed,
            BytesProcessed = 0,
            CpuUsagePercent = 0,
            MemoryUsedBytes = GC.GetTotalMemory(false),
            CustomMetrics = new Dictionary<string, object>
            {
                ["GCGen0Collections"] = GC.CollectionCount(0),
                ["GCGen1Collections"] = GC.CollectionCount(1),
                ["GCGen2Collections"] = GC.CollectionCount(2),
                ["ThreadPoolThreads"] = ThreadPool.ThreadCount
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_kernel != null)
        {
            _logger.LogInformation("Disposing DataWarehouse kernel...");
            await _kernel.DisposeAsync();
            _kernel = null;
        }

        if (State != KernelState.Stopped && State != KernelState.Failed)
        {
            State = KernelState.Stopped;
        }
    }

    private static OperatingMode ParseOperatingMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "laptop" => OperatingMode.Laptop,
            "workstation" => OperatingMode.Workstation,
            "server" => OperatingMode.Server,
            "hyperscale" => OperatingMode.Hyperscale,
            _ => OperatingMode.Workstation
        };
    }
}
