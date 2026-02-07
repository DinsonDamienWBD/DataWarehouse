using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
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
    /// Intelligence-aware: Supports AI-driven deduplication ratio estimation.
    /// </summary>
    public abstract class DeduplicationPluginBase : FeaturePluginBase, IDeduplicationProvider
    {
        private readonly ConcurrentDictionary<string, long> _chunkRefCounts = new();
        private long _totalLogicalBytes;
        private long _totalPhysicalBytes;
        private long _totalChunks;
        private long _uniqueChunks;

        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this deduplication provider.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.deduplication",
                DisplayName = $"{Name} - Deduplication",
                Description = "Data deduplication with configurable chunking algorithms",
                Category = CapabilityCategory.Storage,
                SubCategory = "Deduplication",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "deduplication", "storage", "optimization", "chunking" },
                SemanticDescription = "Use this for reducing storage costs by eliminating duplicate data blocks",
                Metadata = new Dictionary<string, object>
                {
                    ["chunkingAlgorithm"] = ChunkingAlgorithm,
                    ["hashAlgorithm"] = HashAlgorithm,
                    ["averageChunkSize"] = AverageChunkSize
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.dedup.capability",
                Topic = "deduplication",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Deduplication provider using {ChunkingAlgorithm} chunking with {HashAlgorithm} fingerprinting",
                Payload = new Dictionary<string, object>
                {
                    ["chunkingAlgorithm"] = ChunkingAlgorithm,
                    ["hashAlgorithm"] = HashAlgorithm,
                    ["averageChunkSize"] = AverageChunkSize,
                    ["minChunkSize"] = MinChunkSize,
                    ["maxChunkSize"] = MaxChunkSize
                },
                Tags = new[] { "deduplication", "storage", "chunking", ChunkingAlgorithm.ToLowerInvariant() },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI estimation of deduplication ratio for a data sample.
        /// </summary>
        /// <param name="sampleData">Sample data bytes to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Estimated dedup ratio (0.0-1.0), or null if Intelligence unavailable.</returns>
        protected async Task<double?> RequestDedupRatioEstimateAsync(byte[] sampleData, CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.predict.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["predictionType"] = "dedup_ratio",
                        ["sampleSize"] = sampleData.Length,
                        ["entropy"] = CalculateSampleEntropy(sampleData),
                        ["chunkingAlgorithm"] = ChunkingAlgorithm
                    }
                };

                await MessageBus.PublishAsync("intelligence.predict", request, ct);
                return null; // Actual implementation would await response
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven optimal chunk size recommendation.
        /// </summary>
        /// <param name="dataProfile">Profile describing the data characteristics.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended chunk size, or null if unavailable.</returns>
        protected async Task<int?> RequestOptimalChunkSizeAsync(Dictionary<string, object> dataProfile, CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["recommendationType"] = "chunk_size",
                        ["dataProfile"] = dataProfile,
                        ["currentAverage"] = AverageChunkSize
                    }
                };

                await MessageBus.PublishAsync("intelligence.recommend", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculates sample entropy for AI analysis.
        /// </summary>
        private static double CalculateSampleEntropy(byte[] data)
        {
            if (data.Length == 0) return 0;
            var frequencies = new int[256];
            foreach (var b in data) frequencies[b]++;
            double entropy = 0;
            foreach (var freq in frequencies)
            {
                if (freq > 0)
                {
                    double p = (double)freq / data.Length;
                    entropy -= p * Math.Log2(p);
                }
            }
            return entropy / 8.0;
        }

        #endregion

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
        /// <summary>Alias for CreatedAt for compatibility.</summary>
        public DateTimeOffset Timestamp { get => new DateTimeOffset(CreatedAt); init => CreatedAt = value.DateTime; }
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
    /// Intelligence-aware: Supports AI-driven version conflict resolution and diff analysis.
    /// </summary>
    public abstract class VersioningPluginBase : FeaturePluginBase, IVersioningProvider
    {
        public override PluginCategory Category => PluginCategory.StorageProvider;

        public abstract bool SupportsBranching { get; }
        public abstract bool SupportsDeltaStorage { get; }

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this versioning provider.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.versioning",
                DisplayName = $"{Name} - Version Control",
                Description = "File versioning with history, branching, and diff support",
                Category = CapabilityCategory.Storage,
                SubCategory = "Versioning",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "versioning", "storage", "history", "branching" },
                SemanticDescription = "Use this for maintaining version history and enabling rollback capabilities",
                Metadata = new Dictionary<string, object>
                {
                    ["supportsBranching"] = SupportsBranching,
                    ["supportsDeltaStorage"] = SupportsDeltaStorage
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.versioning.capability",
                Topic = "versioning",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Version control provider with {(SupportsBranching ? "branching" : "linear")} support",
                Payload = new Dictionary<string, object>
                {
                    ["supportsBranching"] = SupportsBranching,
                    ["supportsDeltaStorage"] = SupportsDeltaStorage,
                    ["supportsMerge"] = SupportsBranching
                },
                Tags = new[] { "versioning", "storage", SupportsBranching ? "branching" : "linear" },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI-driven conflict resolution for merge conflicts.
        /// </summary>
        /// <param name="conflict">The merge conflict to resolve.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Resolved data, or null if unavailable.</returns>
        protected async Task<byte[]?> RequestConflictResolutionAsync(MergeConflict conflict, CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.resolve.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["conflictType"] = "merge",
                        ["offset"] = conflict.Offset,
                        ["length"] = conflict.Length,
                        ["hasBase"] = conflict.BaseData != null,
                        ["hasSource"] = conflict.SourceData != null,
                        ["hasTarget"] = conflict.TargetData != null
                    }
                };

                await MessageBus.PublishAsync("intelligence.resolve", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-generated summary of changes between versions.
        /// </summary>
        /// <param name="diff">The version diff to summarize.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Summary text, or null if unavailable.</returns>
        protected async Task<string?> RequestDiffSummaryAsync(VersionDiff diff, CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.summarize.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["contentType"] = "version_diff",
                        ["bytesAdded"] = diff.BytesAdded,
                        ["bytesRemoved"] = diff.BytesRemoved,
                        ["bytesModified"] = diff.BytesModified,
                        ["hunkCount"] = diff.Hunks.Count
                    }
                };

                await MessageBus.PublishAsync("intelligence.summarize", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
                Timestamp = DateTimeOffset.UtcNow,
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
    /// Intelligence-aware: Supports AI-driven snapshot scheduling and retention optimization.
    /// </summary>
    public abstract class SnapshotPluginBase : FeaturePluginBase, ISnapshotProvider
    {
        public override PluginCategory Category => PluginCategory.StorageProvider;

        public abstract bool SupportsIncremental { get; }
        public abstract bool SupportsLegalHold { get; }

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this snapshot provider.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.snapshot",
                DisplayName = $"{Name} - Snapshot Management",
                Description = "Point-in-time snapshots with retention policies and legal hold support",
                Category = CapabilityCategory.Storage,
                SubCategory = "Snapshot",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "snapshot", "storage", "backup", "recovery" },
                SemanticDescription = "Use this for creating point-in-time copies for backup and compliance",
                Metadata = new Dictionary<string, object>
                {
                    ["supportsIncremental"] = SupportsIncremental,
                    ["supportsLegalHold"] = SupportsLegalHold
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.snapshot.capability",
                Topic = "snapshot",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Snapshot provider with {(SupportsIncremental ? "incremental" : "full")} support and {(SupportsLegalHold ? "legal hold" : "standard retention")}",
                Payload = new Dictionary<string, object>
                {
                    ["supportsIncremental"] = SupportsIncremental,
                    ["supportsLegalHold"] = SupportsLegalHold,
                    ["supportsRetentionPolicies"] = true
                },
                Tags = new[] { "snapshot", "storage", SupportsIncremental ? "incremental" : "full" },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI-recommended optimal snapshot schedule based on data patterns.
        /// </summary>
        /// <param name="dataProfile">Profile describing data change patterns.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended schedule as cron expression, or null if unavailable.</returns>
        protected async Task<string?> RequestOptimalScheduleAsync(Dictionary<string, object> dataProfile, CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["recommendationType"] = "snapshot_schedule",
                        ["dataProfile"] = dataProfile,
                        ["supportsIncremental"] = SupportsIncremental
                    }
                };

                await MessageBus.PublishAsync("intelligence.recommend", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven retention policy recommendation.
        /// </summary>
        /// <param name="complianceRequirements">Compliance requirements to consider.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended retention policy, or null if unavailable.</returns>
        protected async Task<SnapshotRetentionPolicy?> RequestRetentionRecommendationAsync(
            Dictionary<string, object> complianceRequirements,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["recommendationType"] = "retention_policy",
                        ["complianceRequirements"] = complianceRequirements,
                        ["supportsLegalHold"] = SupportsLegalHold
                    }
                };

                await MessageBus.PublishAsync("intelligence.recommend", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
    /// Intelligence-aware: Supports AI-driven anomaly detection in metrics and traces.
    /// </summary>
    public abstract class TelemetryPluginBase : FeaturePluginBase, ITelemetryProvider
    {
        private readonly AsyncLocal<ITraceSpan?> _currentSpan = new();

        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public abstract TelemetryCapabilities Capabilities { get; }

        public ITraceSpan? CurrentSpan => _currentSpan.Value;

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this telemetry provider.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.telemetry",
                DisplayName = $"{Name} - Telemetry Collection",
                Description = "Metrics, tracing, and logging collection with export capabilities",
                Category = CapabilityCategory.Pipeline,
                SubCategory = "Telemetry",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "telemetry", "metrics", "tracing", "logging", "observability" },
                SemanticDescription = "Use this for collecting and analyzing system telemetry data",
                Metadata = new Dictionary<string, object>
                {
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsMetrics"] = Capabilities.HasFlag(TelemetryCapabilities.Metrics),
                    ["supportsTracing"] = Capabilities.HasFlag(TelemetryCapabilities.Tracing),
                    ["supportsLogging"] = Capabilities.HasFlag(TelemetryCapabilities.Logging)
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.telemetry.capability",
                Topic = "telemetry",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Telemetry provider supporting {Capabilities}",
                Payload = new Dictionary<string, object>
                {
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsMetrics"] = Capabilities.HasFlag(TelemetryCapabilities.Metrics),
                    ["supportsTracing"] = Capabilities.HasFlag(TelemetryCapabilities.Tracing),
                    ["supportsLogging"] = Capabilities.HasFlag(TelemetryCapabilities.Logging),
                    ["supportsProfiling"] = Capabilities.HasFlag(TelemetryCapabilities.Profiling)
                },
                Tags = new[] { "telemetry", "observability" },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI-driven anomaly detection on metric data.
        /// </summary>
        /// <param name="metricName">Name of the metric to analyze.</param>
        /// <param name="values">Recent metric values.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Anomaly detection result, or null if unavailable.</returns>
        protected async Task<(bool IsAnomaly, double Score)?> RequestMetricAnomalyDetectionAsync(
            string metricName,
            double[] values,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.anomaly.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["metricName"] = metricName,
                        ["values"] = values,
                        ["analysisType"] = "metric_anomaly"
                    }
                };

                await MessageBus.PublishAsync("intelligence.anomaly", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-generated insights from trace data.
        /// </summary>
        /// <param name="traceId">Trace ID to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Insights text, or null if unavailable.</returns>
        protected async Task<string?> RequestTraceInsightsAsync(string traceId, CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.analyze.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["analysisType"] = "trace_insights",
                        ["traceId"] = traceId
                    }
                };

                await MessageBus.PublishAsync("intelligence.analyze", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
    /// Intelligence-aware: Supports AI-driven threat classification and behavioral analysis.
    /// </summary>
    public abstract class ThreatDetectionPluginBase : FeaturePluginBase, IThreatDetectionProvider
    {
        private long _totalScans;
        private long _threatsDetected;
        private DateTime _lastScanTime;

        public override PluginCategory Category => PluginCategory.SecurityProvider;

        public abstract ThreatDetectionCapabilities DetectionCapabilities { get; }

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this threat detection provider.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.threat-detection",
                DisplayName = $"{Name} - Threat Detection",
                Description = "Threat detection with ransomware, malware, and anomaly detection capabilities",
                Category = CapabilityCategory.Security,
                SubCategory = "ThreatDetection",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "security", "threat-detection", "ransomware", "malware", "anomaly" },
                SemanticDescription = "Use this for detecting security threats including ransomware and malware",
                Metadata = new Dictionary<string, object>
                {
                    ["capabilities"] = DetectionCapabilities.ToString(),
                    ["supportsRansomware"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Ransomware),
                    ["supportsMalware"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Malware),
                    ["supportsAnomaly"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Anomaly)
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.threat.capability",
                Topic = "threat-detection",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Threat detection provider supporting {DetectionCapabilities}",
                Payload = new Dictionary<string, object>
                {
                    ["capabilities"] = DetectionCapabilities.ToString(),
                    ["supportsRansomware"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Ransomware),
                    ["supportsMalware"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Malware),
                    ["supportsAnomaly"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Anomaly),
                    ["supportsEntropy"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Entropy),
                    ["supportsBehavioral"] = DetectionCapabilities.HasFlag(ThreatDetectionCapabilities.Behavioral)
                },
                Tags = new[] { "security", "threat-detection" },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI-driven threat classification for detected threats.
        /// </summary>
        /// <param name="threatData">Data about the detected threat.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Classification result with threat type and confidence.</returns>
        protected async Task<(string ThreatType, double Confidence)?> RequestThreatClassificationAsync(
            Dictionary<string, object> threatData,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.classify.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["classifyType"] = "threat",
                        ["threatData"] = threatData
                    }
                };

                await MessageBus.PublishAsync("intelligence.classify", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven behavioral pattern analysis.
        /// </summary>
        /// <param name="behavior">Behavioral data to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Risk score and assessment.</returns>
        protected async Task<(double RiskScore, string Assessment)?> RequestBehavioralAnalysisAsync(
            BehaviorData behavior,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.analyze.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["analysisType"] = "behavioral",
                        ["accessCount"] = behavior.RecentAccesses.Count,
                        ["modificationCount"] = behavior.RecentModifications.Count
                    }
                };

                await MessageBus.PublishAsync("intelligence.analyze", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
    /// Intelligence-aware: Supports AI-driven backup scheduling and recovery optimization.
    /// </summary>
    public abstract class BackupPluginBase : FeaturePluginBase, IBackupProvider
    {
        public override PluginCategory Category => PluginCategory.StorageProvider;

        public abstract BackupCapabilities Capabilities { get; }

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this backup provider.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.backup",
                DisplayName = $"{Name} - Backup Management",
                Description = "Backup operations with scheduling, verification, and restore capabilities",
                Category = CapabilityCategory.Storage,
                SubCategory = "Backup",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "backup", "storage", "recovery", "scheduling" },
                SemanticDescription = "Use this for creating and managing data backups with scheduling support",
                Metadata = new Dictionary<string, object>
                {
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsIncremental"] = Capabilities.HasFlag(BackupCapabilities.Incremental),
                    ["supportsContinuous"] = Capabilities.HasFlag(BackupCapabilities.Continuous),
                    ["supportsDedup"] = Capabilities.HasFlag(BackupCapabilities.Dedup),
                    ["supportsScheduling"] = Capabilities.HasFlag(BackupCapabilities.Scheduling)
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.backup.capability",
                Topic = "backup",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Backup provider supporting {Capabilities}",
                Payload = new Dictionary<string, object>
                {
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsIncremental"] = Capabilities.HasFlag(BackupCapabilities.Incremental),
                    ["supportsDifferential"] = Capabilities.HasFlag(BackupCapabilities.Differential),
                    ["supportsContinuous"] = Capabilities.HasFlag(BackupCapabilities.Continuous),
                    ["supportsDedup"] = Capabilities.HasFlag(BackupCapabilities.Dedup),
                    ["supportsEncryption"] = Capabilities.HasFlag(BackupCapabilities.Encryption),
                    ["supportsVerification"] = Capabilities.HasFlag(BackupCapabilities.Verification)
                },
                Tags = new[] { "backup", "storage", "recovery" },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI-driven optimal backup schedule recommendation.
        /// </summary>
        /// <param name="dataProfile">Profile describing data change patterns.</param>
        /// <param name="rpo">Recovery Point Objective in minutes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended cron expression, or null if unavailable.</returns>
        protected async Task<string?> RequestOptimalBackupScheduleAsync(
            Dictionary<string, object> dataProfile,
            int rpo,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["recommendationType"] = "backup_schedule",
                        ["dataProfile"] = dataProfile,
                        ["rpoMinutes"] = rpo,
                        ["capabilities"] = Capabilities.ToString()
                    }
                };

                await MessageBus.PublishAsync("intelligence.recommend", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven backup type recommendation (full, incremental, differential).
        /// </summary>
        /// <param name="lastFullBackup">Time since last full backup.</param>
        /// <param name="changeRate">Estimated data change rate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Recommended backup type.</returns>
        protected async Task<BackupType?> RequestBackupTypeRecommendationAsync(
            TimeSpan lastFullBackup,
            double changeRate,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.recommend.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["recommendationType"] = "backup_type",
                        ["lastFullBackupHours"] = lastFullBackup.TotalHours,
                        ["changeRate"] = changeRate,
                        ["supportsIncremental"] = Capabilities.HasFlag(BackupCapabilities.Incremental),
                        ["supportsDifferential"] = Capabilities.HasFlag(BackupCapabilities.Differential)
                    }
                };

                await MessageBus.PublishAsync("intelligence.recommend", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
    /// Intelligence-aware: Supports AI-driven deployment risk assessment and alert correlation.
    /// </summary>
    public abstract class OperationsPluginBase : FeaturePluginBase, IOperationsProvider
    {
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public abstract OperationsCapabilities Capabilities { get; }

        #region Intelligence Integration

        /// <summary>
        /// Capabilities declared by this operations provider.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.operations",
                DisplayName = $"{Name} - Operations Management",
                Description = "Deployment operations with upgrade, rollback, and alerting capabilities",
                Category = CapabilityCategory.Pipeline,
                SubCategory = "Operations",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "operations", "deployment", "upgrade", "rollback", "alerting" },
                SemanticDescription = "Use this for managing deployments, upgrades, and operational alerts",
                Metadata = new Dictionary<string, object>
                {
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsZeroDowntime"] = Capabilities.HasFlag(OperationsCapabilities.ZeroDowntimeUpgrade),
                    ["supportsRollback"] = Capabilities.HasFlag(OperationsCapabilities.Rollback),
                    ["supportsHotReload"] = Capabilities.HasFlag(OperationsCapabilities.HotReload),
                    ["supportsAlerting"] = Capabilities.HasFlag(OperationsCapabilities.Alerting)
                }
            }
        };

        /// <summary>
        /// Gets static knowledge for Intelligence registration.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.operations.capability",
                Topic = "operations",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Operations provider supporting {Capabilities}",
                Payload = new Dictionary<string, object>
                {
                    ["capabilities"] = Capabilities.ToString(),
                    ["supportsZeroDowntime"] = Capabilities.HasFlag(OperationsCapabilities.ZeroDowntimeUpgrade),
                    ["supportsRollback"] = Capabilities.HasFlag(OperationsCapabilities.Rollback),
                    ["supportsHotReload"] = Capabilities.HasFlag(OperationsCapabilities.HotReload),
                    ["supportsAlerting"] = Capabilities.HasFlag(OperationsCapabilities.Alerting),
                    ["supportsAutoScaling"] = Capabilities.HasFlag(OperationsCapabilities.AutoScaling)
                },
                Tags = new[] { "operations", "deployment" },
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            });

            return knowledge;
        }

        /// <summary>
        /// Requests AI-driven deployment risk assessment.
        /// </summary>
        /// <param name="upgradeRequest">The proposed upgrade.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Risk score (0.0-1.0) and assessment text.</returns>
        protected async Task<(double RiskScore, string Assessment)?> RequestDeploymentRiskAssessmentAsync(
            UpgradeRequest upgradeRequest,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.assess.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["assessmentType"] = "deployment_risk",
                        ["targetVersion"] = upgradeRequest.TargetVersion,
                        ["dryRun"] = upgradeRequest.DryRun,
                        ["autoRollback"] = upgradeRequest.AutoRollbackOnFailure
                    }
                };

                await MessageBus.PublishAsync("intelligence.assess", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Requests AI-driven alert correlation and root cause analysis.
        /// </summary>
        /// <param name="alerts">Recent alerts to correlate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Correlated alert groups and root cause hypothesis.</returns>
        protected async Task<(IReadOnlyList<string> CorrelatedAlertIds, string RootCauseHypothesis)?> RequestAlertCorrelationAsync(
            IReadOnlyList<Alert> alerts,
            CancellationToken ct = default)
        {
            if (MessageBus == null) return null;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.correlate.request",
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["correlationType"] = "alerts",
                        ["alertCount"] = alerts.Count,
                        ["alertIds"] = alerts.Select(a => a.AlertId).ToArray()
                    }
                };

                await MessageBus.PublishAsync("intelligence.correlate", request, ct);
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

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
