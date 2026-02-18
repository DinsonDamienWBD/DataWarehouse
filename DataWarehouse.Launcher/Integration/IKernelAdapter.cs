using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Launcher.Integration;

/// <summary>
/// Plug-and-play adapter interface for running any kernel-based application.
/// Copy this adapter pattern to other projects for seamless integration.
///
/// Usage:
/// 1. Implement this interface for your specific kernel/service
/// 2. Register the adapter with the AdapterFactory
/// 3. Use AdapterRunner to execute your application
///
/// This pattern allows:
/// - Decoupled kernel initialization
/// - Standardized lifecycle management
/// - Easy testing with mock adapters
/// - Reusable launcher infrastructure
/// </summary>
public interface IKernelAdapter : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for this kernel instance.
    /// </summary>
    string KernelId { get; }

    /// <summary>
    /// Current state of the kernel.
    /// </summary>
    KernelState State { get; }

    /// <summary>
    /// Initializes the kernel with the provided options.
    /// </summary>
    Task InitializeAsync(AdapterOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the kernel and begins processing.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully stops the kernel.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets kernel statistics and metrics.
    /// </summary>
    KernelStats GetStats();

    /// <summary>
    /// Gets the capability registry for dynamic endpoint generation.
    /// Returns null if capability registry is not available in this adapter.
    /// </summary>
    IPluginCapabilityRegistry? GetCapabilityRegistry();

    /// <summary>
    /// Event raised when the kernel state changes.
    /// </summary>
    event EventHandler<KernelStateChangedEventArgs>? StateChanged;
}

/// <summary>
/// Kernel operational states.
/// </summary>
public enum KernelState
{
    Uninitialized,
    Initializing,
    Ready,
    Running,
    Stopping,
    Stopped,
    Failed
}

/// <summary>
/// Event args for kernel state changes.
/// </summary>
public sealed class KernelStateChangedEventArgs : EventArgs
{
    public KernelState PreviousState { get; init; }
    public KernelState NewState { get; init; }
    public string? Message { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Configuration options passed to the adapter.
/// Extend this class for custom adapter options.
/// </summary>
public class AdapterOptions
{
    /// <summary>
    /// Unique identifier for this kernel instance.
    /// </summary>
    public string KernelId { get; set; } = $"kernel-{Guid.NewGuid():N}"[..16];

    /// <summary>
    /// Operating mode for the kernel.
    /// </summary>
    public string OperatingMode { get; set; } = "Default";

    /// <summary>
    /// Path to plugins directory.
    /// </summary>
    public string? PluginPath { get; set; }

    /// <summary>
    /// Path to configuration file.
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Additional custom configuration values.
    /// </summary>
    public Dictionary<string, object> CustomConfig { get; set; } = new();

    /// <summary>
    /// Logger factory for creating loggers.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}

/// <summary>
/// Kernel statistics and metrics.
/// </summary>
public sealed class KernelStats
{
    public string KernelId { get; init; } = "";
    public KernelState State { get; init; }
    public DateTime StartedAt { get; init; }
    public TimeSpan Uptime { get; init; }
    public int PluginCount { get; init; }
    public long OperationsProcessed { get; init; }
    public long BytesProcessed { get; init; }
    public double CpuUsagePercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public Dictionary<string, object> CustomMetrics { get; init; } = new();
}
