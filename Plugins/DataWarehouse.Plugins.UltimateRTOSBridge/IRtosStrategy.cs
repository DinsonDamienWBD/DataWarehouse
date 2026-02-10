using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateRTOSBridge;

/// <summary>
/// Interface for RTOS bridge strategies providing safety-critical system integration.
/// </summary>
public interface IRtosStrategy
{
    /// <summary>
    /// Gets the unique strategy identifier.
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Gets the human-readable strategy name.
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// Gets the strategy capabilities.
    /// </summary>
    RtosCapabilities Capabilities { get; }

    /// <summary>
    /// Initializes the strategy with configuration.
    /// </summary>
    Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken ct = default);

    /// <summary>
    /// Executes an RTOS operation.
    /// </summary>
    Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}

/// <summary>
/// Capabilities of an RTOS strategy.
/// </summary>
public record RtosCapabilities
{
    /// <summary>Supports deterministic I/O with guaranteed latency.</summary>
    public bool SupportsDeterministicIo { get; init; }

    /// <summary>Supports real-time scheduling.</summary>
    public bool SupportsRealTimeScheduling { get; init; }

    /// <summary>Supports safety certifications.</summary>
    public bool SupportsSafetyCertifications { get; init; }

    /// <summary>Supported safety standards.</summary>
    public string[] SupportedStandards { get; init; } = Array.Empty<string>();

    /// <summary>Maximum guaranteed latency in microseconds.</summary>
    public int MaxGuaranteedLatencyMicroseconds { get; init; }

    /// <summary>Supported RTOS platforms.</summary>
    public string[] SupportedPlatforms { get; init; } = Array.Empty<string>();

    /// <summary>Supports fault tolerance.</summary>
    public bool SupportsFaultTolerance { get; init; }

    /// <summary>Supports watchdog integration.</summary>
    public bool SupportsWatchdog { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Context for RTOS operations.
/// </summary>
public record RtosOperationContext
{
    /// <summary>Operation type.</summary>
    public required RtosOperationType OperationType { get; init; }

    /// <summary>Target resource or path.</summary>
    public required string ResourcePath { get; init; }

    /// <summary>Data payload.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Priority level (0-255, 0 = highest).</summary>
    public int Priority { get; init; } = 128;

    /// <summary>Deadline in microseconds (0 = no deadline).</summary>
    public long DeadlineMicroseconds { get; init; }

    /// <summary>Required safety integrity level.</summary>
    public SafetyIntegrityLevel RequiredSil { get; init; } = SafetyIntegrityLevel.None;

    /// <summary>Additional parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// Result of an RTOS operation.
/// </summary>
public record RtosOperationResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Result data.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Actual latency in microseconds.</summary>
    public long ActualLatencyMicroseconds { get; init; }

    /// <summary>Whether the deadline was met.</summary>
    public bool DeadlineMet { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Safety certification applied.</summary>
    public string? SafetyCertification { get; init; }

    /// <summary>Audit trail for safety compliance.</summary>
    public RtosAuditEntry? AuditEntry { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// RTOS operation types.
/// </summary>
public enum RtosOperationType
{
    /// <summary>Read operation.</summary>
    Read,
    /// <summary>Write operation.</summary>
    Write,
    /// <summary>Control operation.</summary>
    Control,
    /// <summary>Status query.</summary>
    Status,
    /// <summary>Synchronization operation.</summary>
    Sync,
    /// <summary>Interrupt handling.</summary>
    Interrupt,
    /// <summary>Watchdog reset.</summary>
    WatchdogReset
}

/// <summary>
/// Safety Integrity Level per IEC 61508.
/// </summary>
public enum SafetyIntegrityLevel
{
    /// <summary>No safety requirements.</summary>
    None = 0,
    /// <summary>SIL 1 - Low safety integrity.</summary>
    Sil1 = 1,
    /// <summary>SIL 2 - Medium safety integrity.</summary>
    Sil2 = 2,
    /// <summary>SIL 3 - High safety integrity.</summary>
    Sil3 = 3,
    /// <summary>SIL 4 - Highest safety integrity.</summary>
    Sil4 = 4
}

/// <summary>
/// Audit entry for RTOS operations (safety compliance).
/// </summary>
public record RtosAuditEntry
{
    /// <summary>Unique operation ID.</summary>
    public required string OperationId { get; init; }

    /// <summary>Timestamp of operation.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Operation type.</summary>
    public RtosOperationType OperationType { get; init; }

    /// <summary>Resource accessed.</summary>
    public required string ResourcePath { get; init; }

    /// <summary>Applied safety integrity level.</summary>
    public SafetyIntegrityLevel AppliedSil { get; init; }

    /// <summary>Whether operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Latency in microseconds.</summary>
    public long LatencyMicroseconds { get; init; }

    /// <summary>Hash of operation data for integrity.</summary>
    public string? DataHash { get; init; }

    /// <summary>Safety certification reference.</summary>
    public string? CertificationReference { get; init; }
}

/// <summary>
/// Base class for RTOS strategies.
/// </summary>
public abstract class RtosStrategyBase : IRtosStrategy
{
    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public abstract RtosCapabilities Capabilities { get; }

    /// <summary>
    /// Configuration dictionary.
    /// </summary>
    protected Dictionary<string, object> Configuration { get; private set; } = new();

    /// <inheritdoc/>
    public virtual Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken ct = default)
    {
        Configuration = configuration ?? new Dictionary<string, object>();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public abstract Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);

    /// <summary>
    /// Creates an audit entry for an operation.
    /// </summary>
    protected RtosAuditEntry CreateAuditEntry(
        RtosOperationContext context,
        bool success,
        long latencyMicroseconds,
        string? dataHash = null)
    {
        return new RtosAuditEntry
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            OperationType = context.OperationType,
            ResourcePath = context.ResourcePath,
            AppliedSil = context.RequiredSil,
            Success = success,
            LatencyMicroseconds = latencyMicroseconds,
            DataHash = dataHash
        };
    }
}
