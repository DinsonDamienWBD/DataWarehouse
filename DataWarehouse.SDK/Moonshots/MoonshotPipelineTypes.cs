using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Identifies each of the 10 moonshot features in the DataWarehouse system.
/// Each moonshot is an independently-built capability (Phases 55-63) that
/// can participate in cross-moonshot orchestration pipelines.
/// </summary>
public enum MoonshotId
{
    /// <summary>Universal tag system for cross-domain metadata tagging.</summary>
    UniversalTags = 1,

    /// <summary>Data consciousness and self-aware data lifecycle management.</summary>
    DataConsciousness = 2,

    /// <summary>Compliance passports for regulatory and audit tracking.</summary>
    CompliancePassports = 3,

    /// <summary>Data sovereignty mesh for jurisdiction-aware data routing.</summary>
    SovereigntyMesh = 4,

    /// <summary>Zero-gravity storage for weightless, distributed data placement.</summary>
    ZeroGravityStorage = 5,

    /// <summary>Cryptographic time locks for temporal data protection.</summary>
    CryptoTimeLocks = 6,

    /// <summary>Semantic synchronization for meaning-aware data replication.</summary>
    SemanticSync = 7,

    /// <summary>Chaos vaccination for proactive resilience and fault injection.</summary>
    ChaosVaccination = 8,

    /// <summary>Carbon-aware lifecycle management for sustainability-driven operations.</summary>
    CarbonAwareLifecycle = 9,

    /// <summary>Universal fabric for cross-system data transport and integration.</summary>
    UniversalFabric = 10
}

/// <summary>
/// Feature flag controlling whether a specific moonshot is enabled in the pipeline.
/// Disabled moonshots are skipped during pipeline execution.
/// </summary>
/// <param name="Id">The moonshot this flag controls.</param>
/// <param name="Enabled">Whether the moonshot is active in pipelines.</param>
/// <param name="DisabledReason">Optional reason when disabled (for diagnostics/audit).</param>
public sealed record MoonshotFeatureFlag(
    MoonshotId Id,
    bool Enabled,
    string? DisabledReason = null);

/// <summary>
/// Result of executing a single moonshot stage within a pipeline.
/// Immutable record capturing timing, success/failure, and optional metadata.
/// </summary>
/// <param name="Stage">The moonshot stage that produced this result.</param>
/// <param name="Success">Whether the stage completed successfully.</param>
/// <param name="Duration">Wall-clock time the stage took to execute.</param>
/// <param name="Error">Error message if the stage failed; null on success.</param>
/// <param name="Metadata">Optional key-value metadata produced by the stage.</param>
public sealed record MoonshotStageResult(
    MoonshotId Stage,
    bool Success,
    TimeSpan Duration,
    string? Error = null,
    IReadOnlyDictionary<string, object>? Metadata = null);

/// <summary>
/// Aggregate result of executing a complete moonshot pipeline.
/// Contains all individual stage results plus summary timing.
/// </summary>
/// <param name="StageResults">Ordered results from each executed stage.</param>
/// <param name="AllSucceeded">True if every stage succeeded.</param>
/// <param name="TotalDuration">Total wall-clock duration of the entire pipeline.</param>
/// <param name="CompletedAt">UTC timestamp when the pipeline finished.</param>
public sealed record MoonshotPipelineResult(
    IReadOnlyList<MoonshotStageResult> StageResults,
    bool AllSucceeded,
    TimeSpan TotalDuration,
    DateTimeOffset CompletedAt);

/// <summary>
/// Defines a moonshot pipeline: the ordered stages to execute and per-stage feature flags.
/// </summary>
/// <param name="Name">Human-readable pipeline name (e.g. "IngestPipeline", "LifecyclePipeline").</param>
/// <param name="StageOrder">Ordered list of moonshot stages to execute.</param>
/// <param name="FeatureFlags">Per-moonshot feature flags controlling stage enablement.</param>
public sealed record MoonshotPipelineDefinition(
    string Name,
    IReadOnlyList<MoonshotId> StageOrder,
    IReadOnlyDictionary<MoonshotId, MoonshotFeatureFlag> FeatureFlags);

/// <summary>
/// Mutable context that flows through a moonshot pipeline, carrying state between stages.
/// Each stage can read/write typed properties and append its result.
/// Thread-safe for concurrent property access.
/// </summary>
public sealed class MoonshotPipelineContext
{
    private readonly BoundedDictionary<string, object> _properties = new BoundedDictionary<string, object>(1000);
    private readonly List<MoonshotStageResult> _stageResults = new();
    private readonly object _resultsLock = new();

    /// <summary>
    /// Unique identifier of the storage object being processed through the pipeline.
    /// </summary>
    public string ObjectId { get; init; } = string.Empty;

    /// <summary>
    /// Optional metadata about the storage object being processed.
    /// </summary>
    public StorageObjectMetadata? ObjectMetadata { get; init; }

    /// <summary>
    /// Read-only view of all properties set by pipeline stages.
    /// </summary>
    public IReadOnlyDictionary<string, object> Properties => _properties;

    /// <summary>
    /// Cancellation token for cooperative cancellation of the pipeline.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Logger for pipeline-scoped diagnostic output.
    /// </summary>
    public ILogger Logger { get; init; } = null!;

    /// <summary>
    /// Message bus for inter-plugin communication during pipeline execution.
    /// </summary>
    public IMessageBus MessageBus { get; init; } = null!;

    /// <summary>
    /// Read-only view of stage results accumulated during pipeline execution.
    /// </summary>
    public IReadOnlyList<MoonshotStageResult> StageResults
    {
        get
        {
            lock (_resultsLock)
            {
                // Array.AsReadOnly wraps without an extra List copy
                return Array.AsReadOnly(_stageResults.ToArray());
            }
        }
    }

    /// <summary>
    /// Sets a typed property in the pipeline context property bag.
    /// Overwrites any existing value with the same key.
    /// </summary>
    /// <typeparam name="T">Type of the property value.</typeparam>
    /// <param name="key">Property key.</param>
    /// <param name="value">Property value.</param>
    public void SetProperty<T>(string key, T value) where T : notnull
    {
        _properties[key] = value;
    }

    /// <summary>
    /// Retrieves a typed property from the pipeline context property bag.
    /// Returns default if the key is not found or the value is not of the expected type.
    /// </summary>
    /// <typeparam name="T">Expected type of the property value.</typeparam>
    /// <param name="key">Property key.</param>
    /// <returns>The typed value, or default if not found or type mismatch.</returns>
    public T? GetProperty<T>(string key)
    {
        if (_properties.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }

    /// <summary>
    /// Appends a stage result to the pipeline context.
    /// Called by the orchestrator after each stage completes.
    /// </summary>
    /// <param name="result">The stage result to record.</param>
    public void AddStageResult(MoonshotStageResult result)
    {
        lock (_resultsLock)
        {
            _stageResults.Add(result);
        }
    }
}
