using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Migration;

/// <summary>
/// Lifecycle status of a migration job.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public enum MigrationStatus
{
    Pending,
    Preparing,
    InProgress,
    ReadForwarding,
    Completing,
    Completed,
    Failed,
    Cancelled,
    Paused
}

/// <summary>
/// A single object to be migrated between storage nodes.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record MigrationObject(
    string ObjectKey,
    long SizeBytes,
    string SourceLocation,
    string TargetLocation,
    int Priority);

/// <summary>
/// Defines the migration plan: source/target nodes, objects to move, and operational settings.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record MigrationPlan(
    string SourceNode,
    string TargetNode,
    IReadOnlyList<MigrationObject> Objects,
    long? ThrottleBytesPerSec,
    bool EnableReadForwarding,
    bool ZeroDowntime,
    bool ValidateChecksums);

/// <summary>
/// Tracks the lifecycle and progress of a migration job.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record MigrationJob(
    string JobId,
    string Description,
    MigrationPlan Plan,
    MigrationStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    long TotalObjects,
    long MigratedObjects,
    long FailedObjects,
    long BytesMigrated,
    double CurrentThroughputBytesPerSec);

/// <summary>
/// A read-forwarding entry that redirects reads from the old location to the new location
/// during a zero-downtime migration.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record ReadForwardingEntry(
    string ObjectKey,
    string OriginalNode,
    string NewNode,
    DateTimeOffset ExpiresUtc,
    int Hops);

/// <summary>
/// A checkpoint recording migration progress for crash recovery.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Zero-Gravity Storage")]
public sealed record MigrationCheckpoint(
    string JobId,
    string LastProcessedKey,
    long ProcessedCount,
    DateTimeOffset TimestampUtc);
