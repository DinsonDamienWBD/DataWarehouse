using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DataWarehouse.SDK.Contracts
{
    #region Deduplication Provider

    /// <summary>
    /// Interface for deduplication providers.
    /// Supports content-defined chunking, fingerprinting, and dedup storage.
    /// </summary>
    public interface IDeduplicationProvider : IPlugin
    {
        /// <summary>
        /// Chunking algorithm used (e.g., "Rabin", "FastCDC", "Fixed").
        /// </summary>
        string ChunkingAlgorithm { get; }

        /// <summary>
        /// Hash algorithm for chunk fingerprints (e.g., "SHA256", "Blake2b", "XXHash").
        /// </summary>
        string HashAlgorithm { get; }

        /// <summary>
        /// Average chunk size in bytes.
        /// </summary>
        int AverageChunkSize { get; }

        /// <summary>
        /// Chunks data and returns chunk references.
        /// </summary>
        Task<ChunkingResult> ChunkDataAsync(Stream data, CancellationToken ct = default);

        /// <summary>
        /// Reassembles data from chunk references.
        /// </summary>
        Task<Stream> ReassembleAsync(IReadOnlyList<ChunkReference> chunks, CancellationToken ct = default);

        /// <summary>
        /// Stores a chunk if it doesn't already exist.
        /// </summary>
        Task<bool> StoreChunkAsync(byte[] hash, Stream data, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a chunk by its hash.
        /// </summary>
        Task<Stream?> GetChunkAsync(byte[] hash, CancellationToken ct = default);

        /// <summary>
        /// Checks if a chunk exists.
        /// </summary>
        Task<bool> ChunkExistsAsync(byte[] hash, CancellationToken ct = default);

        /// <summary>
        /// Gets deduplication statistics.
        /// </summary>
        Task<DeduplicationStatistics> GetStatisticsAsync(CancellationToken ct = default);

        /// <summary>
        /// Performs garbage collection on orphaned chunks.
        /// </summary>
        Task<GarbageCollectionResult> CollectGarbageAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Result of chunking operation.
    /// </summary>
    public class ChunkingResult
    {
        public IReadOnlyList<ChunkReference> Chunks { get; init; } = Array.Empty<ChunkReference>();
        public long OriginalSize { get; init; }
        public long DeduplicatedSize { get; init; }
        public int TotalChunks { get; init; }
        public int UniqueChunks { get; init; }
        public int DuplicateChunks { get; init; }
        public double DeduplicationRatio => OriginalSize > 0 ? (double)DeduplicatedSize / OriginalSize : 1.0;
    }

    /// <summary>
    /// Reference to a stored chunk.
    /// </summary>
    public class ChunkReference
    {
        public byte[] Hash { get; init; } = Array.Empty<byte>();
        public int Size { get; init; }
        public long Offset { get; init; }
        public bool IsNew { get; init; }
    }

    /// <summary>
    /// Deduplication statistics.
    /// </summary>
    public class DeduplicationStatistics
    {
        public long TotalChunks { get; init; }
        public long UniqueChunks { get; init; }
        public long TotalLogicalBytes { get; init; }
        public long TotalPhysicalBytes { get; init; }
        public double GlobalDeduplicationRatio => TotalLogicalBytes > 0
            ? (double)TotalPhysicalBytes / TotalLogicalBytes : 1.0;
        public double SpaceSavingsPercent => TotalLogicalBytes > 0
            ? (1.0 - (double)TotalPhysicalBytes / TotalLogicalBytes) * 100 : 0;
    }

    /// <summary>
    /// Result of garbage collection.
    /// </summary>
    public class GarbageCollectionResult
    {
        public int ChunksScanned { get; init; }
        public int ChunksDeleted { get; init; }
        public long BytesReclaimed { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Abstract base class for deduplication provider plugins.
    /// Provides content-defined chunking with Rabin fingerprinting.
    /// </summary>
    public abstract class DeduplicationPluginBase : FeaturePluginBase, IDeduplicationProvider
    {
        private readonly ConcurrentDictionary<string, long> _chunkRefCounts = new();
        private long _totalLogicalBytes;
        private long _totalPhysicalBytes;
        private long _totalChunks;
        private long _uniqueChunks;

        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        /// <summary>
        /// Chunking algorithm identifier.
        /// </summary>
        public abstract string ChunkingAlgorithm { get; }

        /// <summary>
        /// Hash algorithm for fingerprinting.
        /// </summary>
        public virtual string HashAlgorithm => "SHA256";

        /// <summary>
        /// Target average chunk size. Default is 64KB.
        /// </summary>
        public virtual int AverageChunkSize => 65536;

        /// <summary>
        /// Minimum chunk size. Default is 16KB.
        /// </summary>
        protected virtual int MinChunkSize => 16384;

        /// <summary>
        /// Maximum chunk size. Default is 256KB.
        /// </summary>
        protected virtual int MaxChunkSize => 262144;

        public abstract Task<ChunkingResult> ChunkDataAsync(Stream data, CancellationToken ct = default);
        public abstract Task<Stream> ReassembleAsync(IReadOnlyList<ChunkReference> chunks, CancellationToken ct = default);
        public abstract Task<bool> StoreChunkAsync(byte[] hash, Stream data, CancellationToken ct = default);
        public abstract Task<Stream?> GetChunkAsync(byte[] hash, CancellationToken ct = default);
        public abstract Task<bool> ChunkExistsAsync(byte[] hash, CancellationToken ct = default);

        /// <summary>
        /// Computes hash for a chunk using configured algorithm.
        /// </summary>
        protected virtual byte[] ComputeHash(byte[] data)
        {
            using var hasher = HashAlgorithm switch
            {
                "SHA256" => SHA256.Create() as System.Security.Cryptography.HashAlgorithm,
                "SHA512" => SHA512.Create(),
                "MD5" => MD5.Create(),
                _ => SHA256.Create()
            };
            return hasher.ComputeHash(data);
        }

        /// <summary>
        /// Converts hash bytes to hex string for storage keys.
        /// </summary>
        protected static string HashToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

        public virtual Task<DeduplicationStatistics> GetStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new DeduplicationStatistics
            {
                TotalChunks = Interlocked.Read(ref _totalChunks),
                UniqueChunks = Interlocked.Read(ref _uniqueChunks),
                TotalLogicalBytes = Interlocked.Read(ref _totalLogicalBytes),
                TotalPhysicalBytes = Interlocked.Read(ref _totalPhysicalBytes)
            });
        }

        public virtual Task<GarbageCollectionResult> CollectGarbageAsync(CancellationToken ct = default)
        {
            // Default implementation - override for actual GC
            return Task.FromResult(new GarbageCollectionResult
            {
                ChunksScanned = 0,
                ChunksDeleted = 0,
                BytesReclaimed = 0,
                Duration = TimeSpan.Zero
            });
        }

        /// <summary>
        /// Increments reference count for a chunk.
        /// </summary>
        protected void IncrementChunkRef(string hashHex, int size)
        {
            var isNew = _chunkRefCounts.AddOrUpdate(hashHex, 1, (_, count) => count + 1) == 1;
            Interlocked.Increment(ref _totalChunks);
            Interlocked.Add(ref _totalLogicalBytes, size);
            if (isNew)
            {
                Interlocked.Increment(ref _uniqueChunks);
                Interlocked.Add(ref _totalPhysicalBytes, size);
            }
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Deduplication";
            metadata["ChunkingAlgorithm"] = ChunkingAlgorithm;
            metadata["HashAlgorithm"] = HashAlgorithm;
            metadata["AverageChunkSize"] = AverageChunkSize;
            metadata["MinChunkSize"] = MinChunkSize;
            metadata["MaxChunkSize"] = MaxChunkSize;
            return metadata;
        }
    }

    #endregion

    #region Versioning Provider

    /// <summary>
    /// Interface for versioning providers.
    /// Supports file version history, branching, and delta storage.
    /// </summary>
    public interface IVersioningProvider : IPlugin
    {
        /// <summary>
        /// Whether this provider supports branching.
        /// </summary>
        bool SupportsBranching { get; }

        /// <summary>
        /// Whether this provider supports delta storage (storing only changes).
        /// </summary>
        bool SupportsDeltaStorage { get; }

        /// <summary>
        /// Creates a new version of an object.
        /// </summary>
        Task<VersionInfo> CreateVersionAsync(
            string objectId,
            Stream data,
            VersionMetadata? metadata = null,
            CancellationToken ct = default);

        /// <summary>
        /// Gets a specific version of an object.
        /// </summary>
        Task<Stream> GetVersionAsync(string objectId, string versionId, CancellationToken ct = default);

        /// <summary>
        /// Lists all versions of an object.
        /// </summary>
        Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(
            string objectId,
            int limit = 100,
            CancellationToken ct = default);

        /// <summary>
        /// Deletes a specific version.
        /// </summary>
        Task<bool> DeleteVersionAsync(string objectId, string versionId, CancellationToken ct = default);

        /// <summary>
        /// Restores an object to a specific version.
        /// </summary>
        Task<VersionInfo> RestoreVersionAsync(
            string objectId,
            string versionId,
            CancellationToken ct = default);

        /// <summary>
        /// Gets the diff between two versions.
        /// </summary>
        Task<VersionDiff> GetDiffAsync(
            string objectId,
            string fromVersionId,
            string toVersionId,
            CancellationToken ct = default);

        /// <summary>
        /// Creates a branch from a specific version.
        /// </summary>
        Task<BranchInfo> CreateBranchAsync(
            string objectId,
            string versionId,
            string branchName,
            CancellationToken ct = default);

        /// <summary>
        /// Merges a branch into another.
        /// </summary>
        Task<MergeResult> MergeBranchAsync(
            string objectId,
            string sourceBranch,
            string targetBranch,
            MergeStrategy strategy = MergeStrategy.ThreeWay,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Information about a version.
    /// </summary>
    public class VersionInfo
    {
        public string VersionId { get; init; } = string.Empty;
        public string ObjectId { get; init; } = string.Empty;
        public long Size { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
        public string? Message { get; init; }
        public string? ParentVersionId { get; init; }
        public string? BranchName { get; init; }
        public bool IsLatest { get; init; }
        public string? ContentHash { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new();
    }

    /// <summary>
    /// Metadata for creating a version.
    /// </summary>
    public class VersionMetadata
    {
        public string? Message { get; init; }
        public string? Author { get; init; }
        public string? BranchName { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new();
    }

    /// <summary>
    /// Diff between two versions.
    /// </summary>
    public class VersionDiff
    {
        public string FromVersionId { get; init; } = string.Empty;
        public string ToVersionId { get; init; } = string.Empty;
        public long BytesAdded { get; init; }
        public long BytesRemoved { get; init; }
        public long BytesModified { get; init; }
        public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
    }

    /// <summary>
    /// A single diff hunk.
    /// </summary>
    public class DiffHunk
    {
        public long OldStart { get; init; }
        public long OldLength { get; init; }
        public long NewStart { get; init; }
        public long NewLength { get; init; }
        public byte[]? OldData { get; init; }
        public byte[]? NewData { get; init; }
    }

    /// <summary>
    /// Information about a branch.
    /// </summary>
    public class BranchInfo
    {
        public string Name { get; init; } = string.Empty;
        public string HeadVersionId { get; init; } = string.Empty;
        public string BaseVersionId { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
    }

    /// <summary>
    /// Result of a merge operation.
    /// </summary>
    public class MergeResult
    {
        public bool Success { get; init; }
        public string? ResultVersionId { get; init; }
        public bool HasConflicts { get; init; }
        public IReadOnlyList<MergeConflict> Conflicts { get; init; } = Array.Empty<MergeConflict>();
    }

    /// <summary>
    /// A merge conflict.
    /// </summary>
    public class MergeConflict
    {
        public long Offset { get; init; }
        public long Length { get; init; }
        public byte[]? BaseData { get; init; }
        public byte[]? SourceData { get; init; }
        public byte[]? TargetData { get; init; }
    }

    /// <summary>
    /// Merge strategy.
    /// </summary>
    public enum MergeStrategy
    {
        ThreeWay,
        Ours,
        Theirs,
        Union
    }

    /// <summary>
    /// Abstract base class for versioning provider plugins.
    /// </summary>
    public abstract class VersioningPluginBase : FeaturePluginBase, IVersioningProvider
    {
        public override PluginCategory Category => PluginCategory.StorageProvider;

        public abstract bool SupportsBranching { get; }
        public abstract bool SupportsDeltaStorage { get; }

        public abstract Task<VersionInfo> CreateVersionAsync(string objectId, Stream data, VersionMetadata? metadata = null, CancellationToken ct = default);
        public abstract Task<Stream> GetVersionAsync(string objectId, string versionId, CancellationToken ct = default);
        public abstract Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(string objectId, int limit = 100, CancellationToken ct = default);
        public abstract Task<bool> DeleteVersionAsync(string objectId, string versionId, CancellationToken ct = default);
        public abstract Task<VersionInfo> RestoreVersionAsync(string objectId, string versionId, CancellationToken ct = default);
        public abstract Task<VersionDiff> GetDiffAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct = default);

        public virtual Task<BranchInfo> CreateBranchAsync(string objectId, string versionId, string branchName, CancellationToken ct = default)
        {
            if (!SupportsBranching)
                throw new NotSupportedException("Branching is not supported by this provider");

            // Default implementation: create a simple branch metadata record
            return Task.FromResult(new BranchInfo
            {
                Name = branchName,
                HeadVersionId = versionId,
                BaseVersionId = versionId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "system"
            });
        }

        public virtual Task<MergeResult> MergeBranchAsync(string objectId, string sourceBranch, string targetBranch, MergeStrategy strategy = MergeStrategy.ThreeWay, CancellationToken ct = default)
        {
            if (!SupportsBranching)
                throw new NotSupportedException("Branching is not supported by this provider");

            // Default implementation: return unsuccessful merge (derived classes must override for actual merge logic)
            return Task.FromResult(new MergeResult
            {
                Success = false,
                HasConflicts = true,
                Conflicts = new[]
                {
                    new MergeConflict
                    {
                        Offset = 0,
                        Length = 0
                    }
                }
            });
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Versioning";
            metadata["SupportsBranching"] = SupportsBranching;
            metadata["SupportsDeltaStorage"] = SupportsDeltaStorage;
            return metadata;
        }
    }

    #endregion

    #region Snapshot Provider

    /// <summary>
    /// Interface for snapshot providers.
    /// Supports point-in-time snapshots, legal holds, and retention policies.
    /// </summary>
    public interface ISnapshotProvider : IPlugin
    {
        /// <summary>
        /// Whether this provider supports incremental snapshots.
        /// </summary>
        bool SupportsIncremental { get; }

        /// <summary>
        /// Whether this provider supports legal holds.
        /// </summary>
        bool SupportsLegalHold { get; }

        /// <summary>
        /// Creates a snapshot.
        /// </summary>
        Task<SnapshotInfo> CreateSnapshotAsync(
            SnapshotRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Lists snapshots with optional filtering.
        /// </summary>
        Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
            SnapshotFilter? filter = null,
            CancellationToken ct = default);

        /// <summary>
        /// Gets snapshot details.
        /// </summary>
        Task<SnapshotInfo?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default);

        /// <summary>
        /// Deletes a snapshot (fails if under legal hold).
        /// </summary>
        Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default);

        /// <summary>
        /// Restores from a snapshot.
        /// </summary>
        Task<RestoreResult> RestoreSnapshotAsync(
            string snapshotId,
            RestoreOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Places a legal hold on a snapshot.
        /// </summary>
        Task<bool> PlaceLegalHoldAsync(
            string snapshotId,
            LegalHoldInfo holdInfo,
            CancellationToken ct = default);

        /// <summary>
        /// Removes a legal hold from a snapshot.
        /// </summary>
        Task<bool> RemoveLegalHoldAsync(
            string snapshotId,
            string holdId,
            CancellationToken ct = default);

        /// <summary>
        /// Sets retention policy for a snapshot.
        /// </summary>
        Task<bool> SetRetentionPolicyAsync(
            string snapshotId,
            SnapshotRetentionPolicy policy,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Snapshot creation request.
    /// </summary>
    public class SnapshotRequest
    {
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public IReadOnlyList<string>? IncludePaths { get; init; }
        public IReadOnlyList<string>? ExcludePaths { get; init; }
        public bool Incremental { get; init; }
        public string? BaseSnapshotId { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new();
        public SnapshotRetentionPolicy? RetentionPolicy { get; init; }
    }

    /// <summary>
    /// Information about a snapshot.
    /// </summary>
    public class SnapshotInfo
    {
        public string SnapshotId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
        public long SizeBytes { get; init; }
        public int ObjectCount { get; init; }
        public bool IsIncremental { get; init; }
        public string? BaseSnapshotId { get; init; }
        public SnapshotState State { get; init; }
        public IReadOnlyList<LegalHoldInfo> LegalHolds { get; init; } = Array.Empty<LegalHoldInfo>();
        public SnapshotRetentionPolicy? RetentionPolicy { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new();
    }

    /// <summary>
    /// Snapshot state.
    /// </summary>
    public enum SnapshotState
    {
        Creating,
        Available,
        Deleting,
        Deleted,
        Error
    }

    /// <summary>
    /// Filter for listing snapshots.
    /// </summary>
    public class SnapshotFilter
    {
        public DateTime? CreatedAfter { get; init; }
        public DateTime? CreatedBefore { get; init; }
        public string? NamePattern { get; init; }
        public Dictionary<string, string>? Tags { get; init; }
        public bool? HasLegalHold { get; init; }
        public int Limit { get; init; } = 100;
    }

    /// <summary>
    /// Legal hold information.
    /// </summary>
    public class LegalHoldInfo
    {
        public string HoldId { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public string? CaseNumber { get; init; }
        public DateTime PlacedAt { get; init; }
        public string? PlacedBy { get; init; }
        public DateTime? ExpiresAt { get; init; }
    }

    /// <summary>
    /// Snapshot retention policy.
    /// </summary>
    public class SnapshotRetentionPolicy
    {
        public TimeSpan? RetainFor { get; init; }
        public DateTime? RetainUntil { get; init; }
        public bool DeleteAfterExpiry { get; init; } = true;
        public bool AllowEarlyDeletion { get; init; }
    }

    /// <summary>
    /// Options for restoring a snapshot.
    /// </summary>
    public class RestoreOptions
    {
        public string? TargetPath { get; init; }
        public bool OverwriteExisting { get; init; }
        public IReadOnlyList<string>? IncludePaths { get; init; }
        public IReadOnlyList<string>? ExcludePaths { get; init; }
    }

    /// <summary>
    /// Result of a restore operation.
    /// </summary>
    public class RestoreResult
    {
        public bool Success { get; init; }
        public int ObjectsRestored { get; init; }
        public long BytesRestored { get; init; }
        public TimeSpan Duration { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Abstract base class for snapshot provider plugins.
    /// </summary>
    public abstract class SnapshotPluginBase : FeaturePluginBase, ISnapshotProvider
    {
        public override PluginCategory Category => PluginCategory.StorageProvider;

        public abstract bool SupportsIncremental { get; }
        public abstract bool SupportsLegalHold { get; }

        public abstract Task<SnapshotInfo> CreateSnapshotAsync(SnapshotRequest request, CancellationToken ct = default);
        public abstract Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(SnapshotFilter? filter = null, CancellationToken ct = default);
        public abstract Task<SnapshotInfo?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default);
        public abstract Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default);
        public abstract Task<RestoreResult> RestoreSnapshotAsync(string snapshotId, RestoreOptions? options = null, CancellationToken ct = default);

        public virtual Task<bool> PlaceLegalHoldAsync(string snapshotId, LegalHoldInfo holdInfo, CancellationToken ct = default)
        {
            if (!SupportsLegalHold)
                throw new NotSupportedException("Legal holds are not supported by this provider");

            // Default implementation: legal hold not actually enforced (derived classes must override)
            // Return false to indicate the operation is not fully implemented
            return Task.FromResult(false);
        }

        public virtual Task<bool> RemoveLegalHoldAsync(string snapshotId, string holdId, CancellationToken ct = default)
        {
            if (!SupportsLegalHold)
                throw new NotSupportedException("Legal holds are not supported by this provider");

            // Default implementation: legal hold removal not actually enforced (derived classes must override)
            // Return false to indicate the operation is not fully implemented
            return Task.FromResult(false);
        }

        public abstract Task<bool> SetRetentionPolicyAsync(string snapshotId, SnapshotRetentionPolicy policy, CancellationToken ct = default);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Snapshot";
            metadata["SupportsIncremental"] = SupportsIncremental;
            metadata["SupportsLegalHold"] = SupportsLegalHold;
            return metadata;
        }
    }

    #endregion

    #region Telemetry Provider

    /// <summary>
    /// Interface for telemetry providers.
    /// Supports metrics, tracing, and logging collection/export.
    /// </summary>
    public interface ITelemetryProvider : IPlugin
    {
        /// <summary>
        /// Supported telemetry types.
        /// </summary>
        TelemetryCapabilities Capabilities { get; }

        /// <summary>
        /// Records a metric value.
        /// </summary>
        void RecordMetric(string name, double value, Dictionary<string, string>? tags = null);

        /// <summary>
        /// Increments a counter.
        /// </summary>
        void IncrementCounter(string name, long delta = 1, Dictionary<string, string>? tags = null);

        /// <summary>
        /// Records a histogram value.
        /// </summary>
        void RecordHistogram(string name, double value, Dictionary<string, string>? tags = null);

        /// <summary>
        /// Starts a new trace span.
        /// </summary>
        ITraceSpan StartSpan(string operationName, SpanKind kind = SpanKind.Internal, ITraceSpan? parent = null);

        /// <summary>
        /// Gets the current active span.
        /// </summary>
        ITraceSpan? CurrentSpan { get; }

        /// <summary>
        /// Logs a structured event.
        /// </summary>
        void LogEvent(LogLevel level, string message, Dictionary<string, object>? properties = null, Exception? exception = null);

        /// <summary>
        /// Flushes all pending telemetry.
        /// </summary>
        Task FlushAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets current metric values.
        /// </summary>
        Task<IReadOnlyDictionary<string, MetricSnapshot>> GetMetricsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Telemetry capabilities flags.
    /// </summary>
    [Flags]
    public enum TelemetryCapabilities
    {
        None = 0,
        Metrics = 1,
        Tracing = 2,
        Logging = 4,
        Profiling = 8,
        All = Metrics | Tracing | Logging | Profiling
    }

    /// <summary>
    /// Span kind for tracing.
    /// </summary>
    public enum SpanKind
    {
        Internal,
        Server,
        Client,
        Producer,
        Consumer
    }

    /// <summary>
    /// Log level.
    /// </summary>
    public enum LogLevel
    {
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Interface for a trace span.
    /// </summary>
    public interface ITraceSpan : IDisposable
    {
        string TraceId { get; }
        string SpanId { get; }
        string OperationName { get; }
        SpanKind Kind { get; }
        DateTime StartTime { get; }
        TimeSpan? Duration { get; }
        bool IsRecording { get; }

        void SetAttribute(string key, object value);
        void AddEvent(string name, Dictionary<string, object>? attributes = null);
        void SetStatus(SpanStatus status, string? description = null);
        void RecordException(Exception exception);
        void End();
    }

    /// <summary>
    /// Span status.
    /// </summary>
    public enum SpanStatus
    {
        Unset,
        Ok,
        Error
    }

    /// <summary>
    /// Snapshot of a metric.
    /// </summary>
    public class MetricSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public double Value { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        public double? Mean { get; init; }
        public long? Count { get; init; }
        public DateTime Timestamp { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new();
    }

    /// <summary>
    /// Abstract base class for telemetry provider plugins.
    /// </summary>
    public abstract class TelemetryPluginBase : FeaturePluginBase, ITelemetryProvider
    {
        private readonly AsyncLocal<ITraceSpan?> _currentSpan = new();

        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public abstract TelemetryCapabilities Capabilities { get; }

        public ITraceSpan? CurrentSpan => _currentSpan.Value;

        public abstract void RecordMetric(string name, double value, Dictionary<string, string>? tags = null);
        public abstract void IncrementCounter(string name, long delta = 1, Dictionary<string, string>? tags = null);
        public abstract void RecordHistogram(string name, double value, Dictionary<string, string>? tags = null);
        public abstract void LogEvent(LogLevel level, string message, Dictionary<string, object>? properties = null, Exception? exception = null);
        public abstract Task FlushAsync(CancellationToken ct = default);
        public abstract Task<IReadOnlyDictionary<string, MetricSnapshot>> GetMetricsAsync(CancellationToken ct = default);

        public virtual ITraceSpan StartSpan(string operationName, SpanKind kind = SpanKind.Internal, ITraceSpan? parent = null)
        {
            var span = CreateSpan(operationName, kind, parent ?? _currentSpan.Value);
            _currentSpan.Value = span;
            return span;
        }

        /// <summary>
        /// Creates a new span. Override to provide custom implementation.
        /// </summary>
        protected abstract ITraceSpan CreateSpan(string operationName, SpanKind kind, ITraceSpan? parent);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => FlushAsync(CancellationToken.None);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Telemetry";
            metadata["Capabilities"] = Capabilities.ToString();
            metadata["SupportsMetrics"] = Capabilities.HasFlag(TelemetryCapabilities.Metrics);
            metadata["SupportsTracing"] = Capabilities.HasFlag(TelemetryCapabilities.Tracing);
            metadata["SupportsLogging"] = Capabilities.HasFlag(TelemetryCapabilities.Logging);
            return metadata;
        }
    }

    #endregion

    #region Threat Detection Provider

    /// <summary>
    /// Interface for threat detection providers.
    /// Supports ransomware detection, anomaly detection, and security scanning.
    /// </summary>
    public interface IThreatDetectionProvider : IPlugin
    {
        /// <summary>
        /// Types of threats this provider can detect.
        /// </summary>
        ThreatDetectionCapabilities DetectionCapabilities { get; }

        /// <summary>
        /// Scans data for threats.
        /// </summary>
        Task<ThreatScanResult> ScanAsync(
            Stream data,
            string? filename = null,
            ScanOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Analyzes file behavior patterns for anomalies.
        /// </summary>
        Task<AnomalyAnalysisResult> AnalyzeBehaviorAsync(
            BehaviorData behavior,
            CancellationToken ct = default);

        /// <summary>
        /// Calculates entropy of data (high entropy may indicate encryption/ransomware).
        /// </summary>
        Task<EntropyResult> CalculateEntropyAsync(Stream data, CancellationToken ct = default);

        /// <summary>
        /// Registers a file baseline for future anomaly detection.
        /// </summary>
        Task RegisterBaselineAsync(
            string objectId,
            BaselineData baseline,
            CancellationToken ct = default);

        /// <summary>
        /// Compares current state against baseline.
        /// </summary>
        Task<BaselineComparisonResult> CompareToBaselineAsync(
            string objectId,
            Stream currentData,
            CancellationToken ct = default);

        /// <summary>
        /// Gets threat detection statistics.
        /// </summary>
        Task<ThreatStatistics> GetStatisticsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Threat detection capabilities.
    /// </summary>
    [Flags]
    public enum ThreatDetectionCapabilities
    {
        None = 0,
        Ransomware = 1,
        Malware = 2,
        Anomaly = 4,
        Entropy = 8,
        Signature = 16,
        Behavioral = 32,
        All = Ransomware | Malware | Anomaly | Entropy | Signature | Behavioral
    }

    /// <summary>
    /// Scan options.
    /// </summary>
    public class ScanOptions
    {
        public bool DeepScan { get; init; }
        public bool CheckSignatures { get; init; } = true;
        public bool CheckEntropy { get; init; } = true;
        public bool CheckBehavior { get; init; }
        public TimeSpan? Timeout { get; init; }
    }

    /// <summary>
    /// Result of a threat scan.
    /// </summary>
    public class ThreatScanResult
    {
        public bool IsThreatDetected { get; init; }
        public ThreatSeverity Severity { get; init; }
        public IReadOnlyList<DetectedThreat> Threats { get; init; } = Array.Empty<DetectedThreat>();
        public TimeSpan ScanDuration { get; init; }
        public string? Recommendation { get; init; }
    }

    /// <summary>
    /// Threat severity levels.
    /// </summary>
    public enum ThreatSeverity
    {
        None,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// A detected threat.
    /// </summary>
    public class DetectedThreat
    {
        public string ThreatType { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ThreatSeverity Severity { get; init; }
        public double Confidence { get; init; }
        public string? Description { get; init; }
        public long? Offset { get; init; }
        public string? Signature { get; init; }
    }

    /// <summary>
    /// Behavior data for analysis.
    /// </summary>
    public class BehaviorData
    {
        public string ObjectId { get; init; } = string.Empty;
        public IReadOnlyList<AccessRecord> RecentAccesses { get; init; } = Array.Empty<AccessRecord>();
        public IReadOnlyList<ModificationRecord> RecentModifications { get; init; } = Array.Empty<ModificationRecord>();
    }

    /// <summary>
    /// Access record.
    /// </summary>
    public class AccessRecord
    {
        public DateTime Timestamp { get; init; }
        public string UserId { get; init; } = string.Empty;
        public string Operation { get; init; } = string.Empty;
        public string? SourceIP { get; init; }
    }

    /// <summary>
    /// Modification record.
    /// </summary>
    public class ModificationRecord
    {
        public DateTime Timestamp { get; init; }
        public string UserId { get; init; } = string.Empty;
        public long BytesChanged { get; init; }
        public double EntropyDelta { get; init; }
    }

    /// <summary>
    /// Result of anomaly analysis.
    /// </summary>
    public class AnomalyAnalysisResult
    {
        public bool IsAnomalous { get; init; }
        public double AnomalyScore { get; init; }
        public IReadOnlyList<DetectedAnomaly> Anomalies { get; init; } = Array.Empty<DetectedAnomaly>();
    }

    /// <summary>
    /// A detected anomaly.
    /// </summary>
    public class DetectedAnomaly
    {
        public string Type { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public double Score { get; init; }
        public DateTime DetectedAt { get; init; }
    }

    /// <summary>
    /// Result of entropy calculation.
    /// </summary>
    public class EntropyResult
    {
        public double Entropy { get; init; }
        public double NormalizedEntropy { get; init; }
        public bool IsHighEntropy { get; init; }
        public string? Assessment { get; init; }
    }

    /// <summary>
    /// Baseline data for anomaly detection.
    /// </summary>
    public class BaselineData
    {
        public string ContentHash { get; init; } = string.Empty;
        public long Size { get; init; }
        public double Entropy { get; init; }
        public string? ContentType { get; init; }
        public Dictionary<string, double> Features { get; init; } = new();
    }

    /// <summary>
    /// Result of baseline comparison.
    /// </summary>
    public class BaselineComparisonResult
    {
        public bool HasDeviation { get; init; }
        public double DeviationScore { get; init; }
        public IReadOnlyList<string> ChangedFeatures { get; init; } = Array.Empty<string>();
        public string? Assessment { get; init; }
    }

    /// <summary>
    /// Threat detection statistics.
    /// </summary>
    public class ThreatStatistics
    {
        public long TotalScans { get; init; }
        public long ThreatsDetected { get; init; }
        public long AnomaliesDetected { get; init; }
        public Dictionary<string, long> ThreatsByType { get; init; } = new();
        public DateTime LastScanTime { get; init; }
    }

    /// <summary>
    /// Abstract base class for threat detection provider plugins.
    /// </summary>
    public abstract class ThreatDetectionPluginBase : FeaturePluginBase, IThreatDetectionProvider
    {
        private long _totalScans;
        private long _threatsDetected;
        private DateTime _lastScanTime;

        public override PluginCategory Category => PluginCategory.SecurityProvider;

        public abstract ThreatDetectionCapabilities DetectionCapabilities { get; }

        public abstract Task<ThreatScanResult> ScanAsync(Stream data, string? filename = null, ScanOptions? options = null, CancellationToken ct = default);
        public abstract Task<AnomalyAnalysisResult> AnalyzeBehaviorAsync(BehaviorData behavior, CancellationToken ct = default);
        public abstract Task RegisterBaselineAsync(string objectId, BaselineData baseline, CancellationToken ct = default);
        public abstract Task<BaselineComparisonResult> CompareToBaselineAsync(string objectId, Stream currentData, CancellationToken ct = default);

        /// <summary>
        /// Calculates Shannon entropy of data.
        /// </summary>
        public virtual async Task<EntropyResult> CalculateEntropyAsync(Stream data, CancellationToken ct = default)
        {
            var frequencies = new long[256];
            var buffer = new byte[8192];
            long totalBytes = 0;
            int bytesRead;

            while ((bytesRead = await data.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    frequencies[buffer[i]]++;
                }
                totalBytes += bytesRead;
            }

            if (totalBytes == 0)
            {
                return new EntropyResult { Entropy = 0, NormalizedEntropy = 0, IsHighEntropy = false, Assessment = "Empty data" };
            }

            double entropy = 0;
            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    double probability = (double)frequencies[i] / totalBytes;
                    entropy -= probability * Math.Log2(probability);
                }
            }

            double normalized = entropy / 8.0; // Max entropy for byte data is 8 bits
            bool isHigh = normalized > 0.9;

            return new EntropyResult
            {
                Entropy = entropy,
                NormalizedEntropy = normalized,
                IsHighEntropy = isHigh,
                Assessment = isHigh ? "High entropy - possibly encrypted or compressed" : "Normal entropy"
            };
        }

        public virtual Task<ThreatStatistics> GetStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new ThreatStatistics
            {
                TotalScans = Interlocked.Read(ref _totalScans),
                ThreatsDetected = Interlocked.Read(ref _threatsDetected),
                LastScanTime = _lastScanTime
            });
        }

        protected void RecordScan(bool threatDetected)
        {
            Interlocked.Increment(ref _totalScans);
            if (threatDetected) Interlocked.Increment(ref _threatsDetected);
            _lastScanTime = DateTime.UtcNow;
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "ThreatDetection";
            metadata["Capabilities"] = DetectionCapabilities.ToString();
            metadata["SupportsRansomware"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Ransomware);
            metadata["SupportsAnomaly"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Anomaly);
            metadata["SupportsEntropy"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Entropy);
            return metadata;
        }
    }

    #endregion

    #region Backup Provider

    /// <summary>
    /// Interface for backup providers.
    /// Supports continuous, incremental, and scheduled backups.
    /// </summary>
    public interface IBackupProvider : IPlugin
    {
        /// <summary>
        /// Backup capabilities.
        /// </summary>
        BackupCapabilities Capabilities { get; }

        /// <summary>
        /// Starts a backup job.
        /// </summary>
        Task<BackupJob> StartBackupAsync(
            BackupRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Gets the status of a backup job.
        /// </summary>
        Task<BackupJob?> GetBackupStatusAsync(string jobId, CancellationToken ct = default);

        /// <summary>
        /// Lists backup jobs.
        /// </summary>
        Task<IReadOnlyList<BackupJob>> ListBackupsAsync(
            BackupListFilter? filter = null,
            CancellationToken ct = default);

        /// <summary>
        /// Cancels a running backup job.
        /// </summary>
        Task<bool> CancelBackupAsync(string jobId, CancellationToken ct = default);

        /// <summary>
        /// Restores from a backup.
        /// </summary>
        Task<BackupRestoreResult> RestoreBackupAsync(
            string jobId,
            BackupRestoreOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Verifies backup integrity.
        /// </summary>
        Task<BackupVerificationResult> VerifyBackupAsync(
            string jobId,
            CancellationToken ct = default);

        /// <summary>
        /// Deletes a backup.
        /// </summary>
        Task<bool> DeleteBackupAsync(string jobId, CancellationToken ct = default);

        /// <summary>
        /// Schedules recurring backups.
        /// </summary>
        Task<BackupSchedule> ScheduleBackupAsync(
            BackupScheduleRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Gets backup statistics.
        /// </summary>
        Task<BackupStatistics> GetStatisticsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Backup capabilities.
    /// </summary>
    [Flags]
    public enum BackupCapabilities
    {
        None = 0,
        Full = 1,
        Incremental = 2,
        Differential = 4,
        Continuous = 8,
        Synthetic = 16,
        Dedup = 32,
        Compression = 64,
        Encryption = 128,
        Scheduling = 256,
        Verification = 512,
        TestRestore = 1024,
        All = Full | Incremental | Differential | Continuous | Synthetic | Dedup | Compression | Encryption | Scheduling | Verification | TestRestore
    }

    /// <summary>
    /// Backup request.
    /// </summary>
    public class BackupRequest
    {
        public string Name { get; init; } = string.Empty;
        public BackupType Type { get; init; } = BackupType.Full;
        public IReadOnlyList<string>? SourcePaths { get; init; }
        public string? DestinationId { get; init; }
        public bool Compress { get; init; } = true;
        public bool Encrypt { get; init; }
        public string? EncryptionKeyId { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new();
    }

    /// <summary>
    /// Backup type.
    /// </summary>
    public enum BackupType
    {
        Full,
        Incremental,
        Differential,
        Synthetic
    }

    /// <summary>
    /// Backup job information.
    /// </summary>
    public class BackupJob
    {
        public string JobId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public BackupType Type { get; init; }
        public BackupJobState State { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public long BytesProcessed { get; init; }
        public long BytesTransferred { get; init; }
        public int FilesProcessed { get; init; }
        public double Progress { get; init; }
        public string? ErrorMessage { get; init; }
        public Dictionary<string, string> Tags { get; init; } = new();
    }

    /// <summary>
    /// Backup job state.
    /// </summary>
    public enum BackupJobState
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled,
        Verifying
    }

    /// <summary>
    /// Filter for listing backups.
    /// </summary>
    public class BackupListFilter
    {
        public DateTime? StartedAfter { get; init; }
        public DateTime? StartedBefore { get; init; }
        public BackupJobState? State { get; init; }
        public BackupType? Type { get; init; }
        public int Limit { get; init; } = 100;
    }

    /// <summary>
    /// Options for restoring a backup.
    /// </summary>
    public class BackupRestoreOptions
    {
        public string? TargetPath { get; init; }
        public bool OverwriteExisting { get; init; }
        public IReadOnlyList<string>? IncludePaths { get; init; }
        public IReadOnlyList<string>? ExcludePaths { get; init; }
        public DateTime? PointInTime { get; init; }
    }

    /// <summary>
    /// Result of backup restore.
    /// </summary>
    public class BackupRestoreResult
    {
        public bool Success { get; init; }
        public int FilesRestored { get; init; }
        public long BytesRestored { get; init; }
        public TimeSpan Duration { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Result of backup verification.
    /// </summary>
    public class BackupVerificationResult
    {
        public bool IsValid { get; init; }
        public int FilesVerified { get; init; }
        public int FilesCorrupted { get; init; }
        public IReadOnlyList<string> CorruptedFiles { get; init; } = Array.Empty<string>();
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Request to schedule backups.
    /// </summary>
    public class BackupScheduleRequest
    {
        public string Name { get; init; } = string.Empty;
        public string CronExpression { get; init; } = string.Empty;
        public BackupRequest BackupTemplate { get; init; } = new();
        public int RetentionDays { get; init; } = 30;
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// Backup schedule information.
    /// </summary>
    public class BackupSchedule
    {
        public string ScheduleId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string CronExpression { get; init; } = string.Empty;
        public DateTime? NextRunTime { get; init; }
        public DateTime? LastRunTime { get; init; }
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// Backup statistics.
    /// </summary>
    public class BackupStatistics
    {
        public int TotalBackups { get; init; }
        public int SuccessfulBackups { get; init; }
        public int FailedBackups { get; init; }
        public long TotalBytesBackedUp { get; init; }
        public long TotalBytesAfterDedup { get; init; }
        public DateTime? LastBackupTime { get; init; }
        public TimeSpan AverageBackupDuration { get; init; }
    }

    /// <summary>
    /// Abstract base class for backup provider plugins.
    /// </summary>
    public abstract class BackupPluginBase : FeaturePluginBase, IBackupProvider
    {
        public override PluginCategory Category => PluginCategory.StorageProvider;

        public abstract BackupCapabilities Capabilities { get; }

        public abstract Task<BackupJob> StartBackupAsync(BackupRequest request, CancellationToken ct = default);
        public abstract Task<BackupJob?> GetBackupStatusAsync(string jobId, CancellationToken ct = default);
        public abstract Task<IReadOnlyList<BackupJob>> ListBackupsAsync(BackupListFilter? filter = null, CancellationToken ct = default);
        public abstract Task<bool> CancelBackupAsync(string jobId, CancellationToken ct = default);
        public abstract Task<BackupRestoreResult> RestoreBackupAsync(string jobId, BackupRestoreOptions? options = null, CancellationToken ct = default);
        public abstract Task<BackupVerificationResult> VerifyBackupAsync(string jobId, CancellationToken ct = default);
        public abstract Task<bool> DeleteBackupAsync(string jobId, CancellationToken ct = default);
        public abstract Task<BackupSchedule> ScheduleBackupAsync(BackupScheduleRequest request, CancellationToken ct = default);
        public abstract Task<BackupStatistics> GetStatisticsAsync(CancellationToken ct = default);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Backup";
            metadata["Capabilities"] = Capabilities.ToString();
            metadata["SupportsIncremental"] = Capabilities.HasFlag(BackupCapabilities.Incremental);
            metadata["SupportsContinuous"] = Capabilities.HasFlag(BackupCapabilities.Continuous);
            metadata["SupportsDedup"] = Capabilities.HasFlag(BackupCapabilities.Dedup);
            metadata["SupportsScheduling"] = Capabilities.HasFlag(BackupCapabilities.Scheduling);
            return metadata;
        }
    }

    #endregion

    #region Operations Provider

    /// <summary>
    /// Interface for operations/deployment providers.
    /// Supports upgrades, monitoring, and deployment management.
    /// </summary>
    public interface IOperationsProvider : IPlugin
    {
        /// <summary>
        /// Operations capabilities.
        /// </summary>
        OperationsCapabilities Capabilities { get; }

        /// <summary>
        /// Performs a zero-downtime upgrade.
        /// </summary>
        Task<UpgradeResult> PerformUpgradeAsync(
            UpgradeRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Rolls back to previous version.
        /// </summary>
        Task<RollbackResult> RollbackAsync(
            string deploymentId,
            CancellationToken ct = default);

        /// <summary>
        /// Gets deployment status.
        /// </summary>
        Task<DeploymentStatus> GetDeploymentStatusAsync(
            string? deploymentId = null,
            CancellationToken ct = default);

        /// <summary>
        /// Hot-reloads configuration.
        /// </summary>
        Task<ConfigReloadResult> ReloadConfigurationAsync(
            Dictionary<string, object>? newConfig = null,
            CancellationToken ct = default);

        /// <summary>
        /// Gets operational metrics.
        /// </summary>
        Task<OperationalMetrics> GetMetricsAsync(CancellationToken ct = default);

        /// <summary>
        /// Creates or updates an alert rule.
        /// </summary>
        Task<AlertRule> ConfigureAlertAsync(
            AlertRuleConfig config,
            CancellationToken ct = default);

        /// <summary>
        /// Gets active alerts.
        /// </summary>
        Task<IReadOnlyList<Alert>> GetActiveAlertsAsync(CancellationToken ct = default);

        /// <summary>
        /// Acknowledges an alert.
        /// </summary>
        Task<bool> AcknowledgeAlertAsync(
            string alertId,
            string? message = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Operations capabilities.
    /// </summary>
    [Flags]
    public enum OperationsCapabilities
    {
        None = 0,
        ZeroDowntimeUpgrade = 1,
        Rollback = 2,
        HotReload = 4,
        Alerting = 8,
        Monitoring = 16,
        AutoScaling = 32,
        All = ZeroDowntimeUpgrade | Rollback | HotReload | Alerting | Monitoring | AutoScaling
    }

    /// <summary>
    /// Upgrade request.
    /// </summary>
    public class UpgradeRequest
    {
        public string TargetVersion { get; init; } = string.Empty;
        public string? PackagePath { get; init; }
        public bool DryRun { get; init; }
        public bool AutoRollbackOnFailure { get; init; } = true;
        public TimeSpan? HealthCheckTimeout { get; init; }
    }

    /// <summary>
    /// Upgrade result.
    /// </summary>
    public class UpgradeResult
    {
        public bool Success { get; init; }
        public string DeploymentId { get; init; } = string.Empty;
        public string PreviousVersion { get; init; } = string.Empty;
        public string NewVersion { get; init; } = string.Empty;
        public TimeSpan Duration { get; init; }
        public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Rollback result.
    /// </summary>
    public class RollbackResult
    {
        public bool Success { get; init; }
        public string RestoredVersion { get; init; } = string.Empty;
        public TimeSpan Duration { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Deployment status.
    /// </summary>
    public class DeploymentStatus
    {
        public string DeploymentId { get; init; } = string.Empty;
        public string CurrentVersion { get; init; } = string.Empty;
        public DeploymentState State { get; init; }
        public DateTime DeployedAt { get; init; }
        public TimeSpan Uptime { get; init; }
        public IReadOnlyList<string> AvailableRollbackVersions { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Deployment state.
    /// </summary>
    public enum DeploymentState
    {
        Running,
        Upgrading,
        RollingBack,
        Degraded,
        Failed
    }

    /// <summary>
    /// Config reload result.
    /// </summary>
    public class ConfigReloadResult
    {
        public bool Success { get; init; }
        public IReadOnlyList<string> ChangedKeys { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Operational metrics.
    /// </summary>
    public class OperationalMetrics
    {
        public double CpuUsagePercent { get; init; }
        public double MemoryUsagePercent { get; init; }
        public long ActiveConnections { get; init; }
        public long RequestsPerSecond { get; init; }
        public double AverageLatencyMs { get; init; }
        public long ErrorsLastHour { get; init; }
        public Dictionary<string, double> CustomMetrics { get; init; } = new();
    }

    /// <summary>
    /// Alert rule configuration.
    /// </summary>
    public class AlertRuleConfig
    {
        public string? RuleId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Metric { get; init; } = string.Empty;
        public AlertCondition Condition { get; init; }
        public double Threshold { get; init; }
        public TimeSpan EvaluationWindow { get; init; } = TimeSpan.FromMinutes(5);
        public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;
        public IReadOnlyList<string>? NotificationChannels { get; init; }
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// Alert condition.
    /// </summary>
    public enum AlertCondition
    {
        GreaterThan,
        LessThan,
        Equals,
        NotEquals,
        GreaterThanOrEqual,
        LessThanOrEqual
    }

    /// <summary>
    /// Alert severity.
    /// </summary>
    public enum AlertSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Alert rule.
    /// </summary>
    public class AlertRule
    {
        public string RuleId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? LastTriggeredAt { get; init; }
    }

    /// <summary>
    /// Active alert.
    /// </summary>
    public class Alert
    {
        public string AlertId { get; init; } = string.Empty;
        public string RuleId { get; init; } = string.Empty;
        public string RuleName { get; init; } = string.Empty;
        public AlertSeverity Severity { get; init; }
        public string Message { get; init; } = string.Empty;
        public double CurrentValue { get; init; }
        public double Threshold { get; init; }
        public DateTime TriggeredAt { get; init; }
        public bool IsAcknowledged { get; init; }
        public string? AcknowledgedBy { get; init; }
    }

    /// <summary>
    /// Abstract base class for operations provider plugins.
    /// </summary>
    public abstract class OperationsPluginBase : FeaturePluginBase, IOperationsProvider
    {
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public abstract OperationsCapabilities Capabilities { get; }

        public abstract Task<UpgradeResult> PerformUpgradeAsync(UpgradeRequest request, CancellationToken ct = default);
        public abstract Task<RollbackResult> RollbackAsync(string deploymentId, CancellationToken ct = default);
        public abstract Task<DeploymentStatus> GetDeploymentStatusAsync(string? deploymentId = null, CancellationToken ct = default);
        public abstract Task<ConfigReloadResult> ReloadConfigurationAsync(Dictionary<string, object>? newConfig = null, CancellationToken ct = default);
        public abstract Task<OperationalMetrics> GetMetricsAsync(CancellationToken ct = default);
        public abstract Task<AlertRule> ConfigureAlertAsync(AlertRuleConfig config, CancellationToken ct = default);
        public abstract Task<IReadOnlyList<Alert>> GetActiveAlertsAsync(CancellationToken ct = default);
        public abstract Task<bool> AcknowledgeAlertAsync(string alertId, string? message = null, CancellationToken ct = default);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Operations";
            metadata["Capabilities"] = Capabilities.ToString();
            metadata["SupportsZeroDowntime"] = Capabilities.HasFlag(OperationsCapabilities.ZeroDowntimeUpgrade);
            metadata["SupportsRollback"] = Capabilities.HasFlag(OperationsCapabilities.Rollback);
            metadata["SupportsHotReload"] = Capabilities.HasFlag(OperationsCapabilities.HotReload);
            metadata["SupportsAlerting"] = Capabilities.HasFlag(OperationsCapabilities.Alerting);
            return metadata;
        }
    }

    #endregion
}
