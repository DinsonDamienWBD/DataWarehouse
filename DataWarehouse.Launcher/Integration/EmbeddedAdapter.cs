using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Launcher.Integration;

/// <summary>
/// Lightweight embedded adapter for running DataWarehouse in-process.
/// This adapter provides a minimal footprint for single-user scenarios.
/// </summary>
public sealed class EmbeddedAdapter : IKernelAdapter
{
    private ILogger<EmbeddedAdapter> _logger = NullLogger<EmbeddedAdapter>.Instance;
    private KernelState _state = KernelState.Uninitialized;
    private DateTime _startedAt;
    private string _kernelId = "";

    public string KernelId => _kernelId;

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

    public Task InitializeAsync(AdapterOptions options, CancellationToken cancellationToken = default)
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
                _logger = options.LoggerFactory.CreateLogger<EmbeddedAdapter>();
            }

            _kernelId = options.KernelId;
            _logger.LogInformation("Initializing embedded adapter: {KernelId}", _kernelId);

            // Extract embedded configuration from custom config
            var maxMemoryMb = options.CustomConfig.TryGetValue("MaxMemoryMb", out var mem) ? Convert.ToInt32(mem) : 256;
            var persistData = options.CustomConfig.TryGetValue("PersistData", out var persist) ? Convert.ToBoolean(persist) : true;

            _logger.LogInformation("Embedded mode: MaxMemory={MaxMemoryMb}MB, PersistData={PersistData}",
                maxMemoryMb, persistData);

            State = KernelState.Ready;
            return Task.CompletedTask;
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

        _logger.LogInformation("Starting embedded adapter: {KernelId}", _kernelId);
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

        _logger.LogInformation("Stopping embedded adapter: {KernelId}", _kernelId);
        State = KernelState.Stopping;
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
            PluginCount = 0,
            OperationsProcessed = 0,
            BytesProcessed = 0,
            CpuUsagePercent = 0,
            MemoryUsedBytes = GC.GetTotalMemory(false)
        };
    }

    /// <summary>
    /// Gets the capability registry for dynamic endpoint generation.
    /// Embedded adapter doesn't have a full kernel, so returns null.
    /// </summary>
    public IPluginCapabilityRegistry? GetCapabilityRegistry()
    {
        // Embedded adapter is lightweight and doesn't include capability registry
        return null;
    }

    public ValueTask DisposeAsync()
    {
        if (State != KernelState.Stopped && State != KernelState.Failed)
        {
            State = KernelState.Stopped;
        }

        _logger.LogInformation("Disposed embedded adapter: {KernelId}", _kernelId);
        return ValueTask.CompletedTask;
    }
}
