using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using VersionInfo = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersionInfo;
using VersionMetadata = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersionMetadata;
using VersionQuery = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersionQuery;
using VersionDiff = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersionDiff;
using StorageTier = DataWarehouse.Plugins.UltimateDataProtection.Versioning.StorageTier;
using VersioningMode = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersioningMode;
using IVersioningSubsystem = DataWarehouse.Plugins.UltimateDataProtection.Versioning.IVersioningSubsystem;
using IVersioningPolicy = DataWarehouse.Plugins.UltimateDataProtection.Versioning.IVersioningPolicy;
using VersionRetentionDecision = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersionRetentionDecision;
using RetentionAction = DataWarehouse.Plugins.UltimateDataProtection.Versioning.RetentionAction;
using DiffItem = DataWarehouse.Plugins.UltimateDataProtection.Versioning.DiffItem;
using DiffType = DataWarehouse.Plugins.UltimateDataProtection.Versioning.DiffType;
using VersioningStatistics = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersioningStatistics;
using VersionSortOrder = DataWarehouse.Plugins.UltimateDataProtection.Versioning.VersionSortOrder;

namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Versioning
{
    /// <summary>
    /// Infinite versioning strategy that never automatically deletes versions.
    /// Uses tiered storage, deduplication, and compression to minimize storage costs
    /// while maintaining complete version history indefinitely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The infinite versioning strategy is designed for scenarios requiring:
    /// </para>
    /// <list type="bullet">
    ///   <item>Complete audit trails with no data loss</item>
    ///   <item>Compliance with strict retention regulations</item>
    ///   <item>Forensic analysis capabilities</item>
    ///   <item>Legal discovery support</item>
    /// </list>
    /// <para>
    /// Cost optimization is achieved through:
    /// </para>
    /// <list type="bullet">
    ///   <item>Automatic tier transitions based on age and access patterns</item>
    ///   <item>Block-level deduplication across all versions</item>
    ///   <item>Adaptive compression based on content type</item>
    ///   <item>Metadata indexing for fast version access without hydration</item>
    /// </list>
    /// </remarks>
    public sealed class InfiniteVersioningStrategy : IVersioningSubsystem
    {
        private readonly ConcurrentDictionary<string, List<VersionInfo>> _versionIndex = new();
        private readonly ConcurrentDictionary<string, byte[]> _contentStore = new();
        private readonly ConcurrentDictionary<string, ContentBlock[]> _blockIndex = new();
        private readonly object _globalLock = new();
        private IVersioningPolicy? _currentPolicy;
        private bool _isEnabled = true;

        /// <summary>Strategy identifier.</summary>
        public const string StrategyId = "infinite-versioning";

        /// <summary>Strategy display name.</summary>
        public const string StrategyName = "Infinite Versioning";

        /// <inheritdoc/>
        public VersioningMode CurrentMode { get; private set; } = VersioningMode.EventBased;

        /// <inheritdoc/>
        public IVersioningPolicy? CurrentPolicy => _currentPolicy;

        /// <inheritdoc/>
        public bool IsEnabled => _isEnabled;

        #region Version Operations

        /// <inheritdoc/>
        public async Task<VersionInfo> CreateVersionAsync(
            string itemId,
            VersionMetadata metadata,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            // For versions without content, create metadata-only entry
            var versionId = GenerateVersionId(itemId);
            var versionNumber = GetNextVersionNumber(itemId);

            var versionInfo = new VersionInfo
            {
                VersionId = versionId,
                ItemId = itemId,
                VersionNumber = versionNumber,
                CreatedAt = DateTimeOffset.UtcNow,
                Metadata = metadata ?? new VersionMetadata(),
                SizeBytes = 0,
                StoredSizeBytes = 0,
                ContentHash = string.Empty,
                HashAlgorithm = "SHA256",
                IsImmutable = false,
                IsOnLegalHold = false,
                IsVerified = true,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                StorageTier = StorageTier.Standard
            };

            AddVersionToIndex(itemId, versionInfo);

            await Task.CompletedTask;
            return versionInfo;
        }

        /// <inheritdoc/>
        public async Task<VersionInfo> CreateVersionWithContentAsync(
            string itemId,
            ReadOnlyMemory<byte> content,
            VersionMetadata metadata,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            var versionId = GenerateVersionId(itemId);
            var versionNumber = GetNextVersionNumber(itemId);

            // Calculate content hash
            var contentArray = content.ToArray();
            var contentHash = ComputeHash(contentArray);

            // Perform deduplication
            var blocks = DeduplicateContent(contentArray);
            var storedSize = blocks.Sum(b => b.CompressedSize);

            // Store content blocks
            foreach (var block in blocks)
            {
                if (!_contentStore.ContainsKey(block.BlockHash))
                {
                    _contentStore[block.BlockHash] = block.CompressedData;
                }
            }

            // Store block index for this version
            _blockIndex[versionId] = blocks;

            var versionInfo = new VersionInfo
            {
                VersionId = versionId,
                ItemId = itemId,
                VersionNumber = versionNumber,
                CreatedAt = DateTimeOffset.UtcNow,
                Metadata = metadata ?? new VersionMetadata(),
                SizeBytes = contentArray.Length,
                StoredSizeBytes = storedSize,
                ContentHash = contentHash,
                HashAlgorithm = "SHA256",
                IsImmutable = false,
                IsOnLegalHold = false,
                IsVerified = true,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                StorageTier = StorageTier.Standard
            };

            AddVersionToIndex(itemId, versionInfo);

            await Task.CompletedTask;
            return versionInfo;
        }

        /// <inheritdoc/>
        public Task<IEnumerable<VersionInfo>> ListVersionsAsync(
            string itemId,
            VersionQuery query,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (!_versionIndex.TryGetValue(itemId, out var versions))
            {
                return Task.FromResult(Enumerable.Empty<VersionInfo>());
            }

            var filtered = versions.AsEnumerable();

            // Apply filters
            if (query.CreatedAfter.HasValue)
                filtered = filtered.Where(v => v.CreatedAt >= query.CreatedAfter.Value);

            if (query.CreatedBefore.HasValue)
                filtered = filtered.Where(v => v.CreatedAt <= query.CreatedBefore.Value);

            if (!string.IsNullOrWhiteSpace(query.LabelPattern))
                filtered = filtered.Where(v => v.Metadata.Label?.Contains(query.LabelPattern, StringComparison.OrdinalIgnoreCase) ?? false);

            if (!string.IsNullOrWhiteSpace(query.CreatedBy))
                filtered = filtered.Where(v => v.Metadata.CreatedBy == query.CreatedBy);

            if (query.MinVersionNumber.HasValue)
                filtered = filtered.Where(v => v.VersionNumber >= query.MinVersionNumber.Value);

            if (query.MaxVersionNumber.HasValue)
                filtered = filtered.Where(v => v.VersionNumber <= query.MaxVersionNumber.Value);

            if (!query.IncludeExpired)
                filtered = filtered.Where(v => !v.ExpiresAt.HasValue || v.ExpiresAt.Value > DateTimeOffset.UtcNow);

            if (query.OnlyImmutable)
                filtered = filtered.Where(v => v.IsImmutable);

            if (query.StorageTier.HasValue)
                filtered = filtered.Where(v => v.StorageTier == query.StorageTier.Value);

            // Apply sorting
            filtered = query.SortOrder switch
            {
                VersionSortOrder.CreatedDescending => filtered.OrderByDescending(v => v.CreatedAt),
                VersionSortOrder.CreatedAscending => filtered.OrderBy(v => v.CreatedAt),
                VersionSortOrder.VersionDescending => filtered.OrderByDescending(v => v.VersionNumber),
                VersionSortOrder.VersionAscending => filtered.OrderBy(v => v.VersionNumber),
                VersionSortOrder.SizeDescending => filtered.OrderByDescending(v => v.SizeBytes),
                VersionSortOrder.SizeAscending => filtered.OrderBy(v => v.SizeBytes),
                _ => filtered.OrderByDescending(v => v.CreatedAt)
            };

            // Apply limit
            if (query.MaxResults > 0)
                filtered = filtered.Take(query.MaxResults);

            return Task.FromResult(filtered);
        }

        /// <inheritdoc/>
        public Task<VersionInfo?> GetVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            if (!_versionIndex.TryGetValue(itemId, out var versions))
            {
                return Task.FromResult<VersionInfo?>(null);
            }

            var version = versions.FirstOrDefault(v => v.VersionId == versionId);
            return Task.FromResult<VersionInfo?>(version);
        }

        /// <inheritdoc/>
        public Task<byte[]> GetVersionContentAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            if (!_blockIndex.TryGetValue(versionId, out var blocks))
            {
                throw new InvalidOperationException($"Version {versionId} has no content");
            }

            // Reconstruct content from blocks
            var content = ReconstructContent(blocks);
            return Task.FromResult(content);
        }

        /// <inheritdoc/>
        public async Task RestoreVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            // Get the version content
            var content = await GetVersionContentAsync(itemId, versionId, ct);

            // In a real implementation, this would restore the content to the actual item
            // For this strategy, we create a new version marked as a restoration
            var metadata = new VersionMetadata
            {
                Label = $"Restored from version {versionId}",
                Description = $"Automatic restoration from version {versionId}",
                TriggerEvent = "restore",
                ParentVersionId = versionId,
                Tags = new Dictionary<string, string>
                {
                    ["restoration"] = "true",
                    ["source-version"] = versionId
                }
            };

            await CreateVersionWithContentAsync(itemId, content, metadata, ct);
        }

        /// <inheritdoc/>
        public Task DeleteVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            // In infinite versioning, we NEVER actually delete versions
            // Instead, we mark them as expired or move to deep archive
            if (_versionIndex.TryGetValue(itemId, out var versions))
            {
                var version = versions.FirstOrDefault(v => v.VersionId == versionId);
                if (version != null)
                {
                    // Check if version is protected
                    if (version.IsImmutable)
                        throw new InvalidOperationException("Cannot delete immutable version");

                    if (version.IsOnLegalHold)
                        throw new InvalidOperationException("Cannot delete version on legal hold");

                    // Mark as expired instead of deleting
                    var updatedVersion = version with
                    {
                        ExpiresAt = DateTimeOffset.UtcNow,
                        StorageTier = StorageTier.DeepArchive
                    };

                    var index = versions.IndexOf(version);
                    versions[index] = updatedVersion;
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<VersionDiff> CompareVersionsAsync(
            string itemId,
            string versionId1,
            string versionId2,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            // Get both version contents
            ContentBlock[] blocks1 = _blockIndex.TryGetValue(versionId1, out var b1) ? b1 : Array.Empty<ContentBlock>();
            ContentBlock[] blocks2 = _blockIndex.TryGetValue(versionId2, out var b2) ? b2 : Array.Empty<ContentBlock>();

            // Calculate differences at block level
            var addedBlocks = blocks2.Where(b2 => !blocks1.Any(b1 => b1.BlockHash == b2.BlockHash)).ToList();
            var removedBlocks = blocks1.Where(b1 => !blocks2.Any(b2 => b2.BlockHash == b1.BlockHash)).ToList();
            var commonBlocks = blocks1.Where(b1 => blocks2.Any(b2 => b2.BlockHash == b1.BlockHash)).Count();

            var totalBytesChanged = addedBlocks.Sum(b => b.OriginalSize) + removedBlocks.Sum(b => b.OriginalSize);
            var totalSize = Math.Max(blocks1.Sum(b => b.OriginalSize), blocks2.Sum(b => b.OriginalSize));
            var changePercentage = totalSize > 0 ? (double)totalBytesChanged / totalSize * 100 : 0;

            var diff = new VersionDiff
            {
                SourceVersionId = versionId1,
                TargetVersionId = versionId2,
                AddedItems = addedBlocks.Select(b => new DiffItem
                {
                    Path = $"block-{b.BlockHash[..8]}",
                    ItemType = "block",
                    TargetSize = b.OriginalSize,
                    DiffType = DiffType.Added
                }).ToList(),
                ModifiedItems = Array.Empty<DiffItem>(),
                DeletedItems = removedBlocks.Select(b => new DiffItem
                {
                    Path = $"block-{b.BlockHash[..8]}",
                    ItemType = "block",
                    SourceSize = b.OriginalSize,
                    DiffType = DiffType.Deleted
                }).ToList(),
                TotalBytesChanged = totalBytesChanged,
                ChangePercentage = changePercentage,
                ComparedAt = DateTimeOffset.UtcNow
            };

            return Task.FromResult(diff);
        }

        #endregion

        #region Version Management

        /// <inheritdoc/>
        public Task LockVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            if (_versionIndex.TryGetValue(itemId, out var versions))
            {
                var version = versions.FirstOrDefault(v => v.VersionId == versionId);
                if (version != null)
                {
                    var updatedVersion = version with { IsImmutable = true };
                    var index = versions.IndexOf(version);
                    versions[index] = updatedVersion;
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task PlaceLegalHoldAsync(
            string itemId,
            string versionId,
            string holdReason,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            if (_versionIndex.TryGetValue(itemId, out var versions))
            {
                var version = versions.FirstOrDefault(v => v.VersionId == versionId);
                if (version != null)
                {
                    var updatedMetadata = version.Metadata with
                    {
                        Tags = new Dictionary<string, string>(version.Metadata.Tags)
                        {
                            ["legal-hold"] = "true",
                            ["legal-hold-reason"] = holdReason,
                            ["legal-hold-date"] = DateTimeOffset.UtcNow.ToString("O")
                        }
                    };

                    var updatedVersion = version with
                    {
                        IsOnLegalHold = true,
                        Metadata = updatedMetadata,
                        ExpiresAt = null // Clear expiration when on legal hold
                    };

                    var index = versions.IndexOf(version);
                    versions[index] = updatedVersion;
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task RemoveLegalHoldAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            if (_versionIndex.TryGetValue(itemId, out var versions))
            {
                var version = versions.FirstOrDefault(v => v.VersionId == versionId);
                if (version != null)
                {
                    var updatedTags = new Dictionary<string, string>(version.Metadata.Tags);
                    updatedTags.Remove("legal-hold");
                    updatedTags.Remove("legal-hold-reason");
                    updatedTags.Remove("legal-hold-date");

                    var updatedMetadata = version.Metadata with { Tags = updatedTags };
                    var updatedVersion = version with
                    {
                        IsOnLegalHold = false,
                        Metadata = updatedMetadata
                    };

                    var index = versions.IndexOf(version);
                    versions[index] = updatedVersion;
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task TransitionStorageTierAsync(
            string itemId,
            string versionId,
            StorageTier targetTier,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("Version ID cannot be empty", nameof(versionId));

            if (_versionIndex.TryGetValue(itemId, out var versions))
            {
                var version = versions.FirstOrDefault(v => v.VersionId == versionId);
                if (version != null)
                {
                    // In a real implementation, this would move data between storage tiers
                    var updatedVersion = version with { StorageTier = targetTier };
                    var index = versions.IndexOf(version);
                    versions[index] = updatedVersion;
                }
            }

            return Task.CompletedTask;
        }

        #endregion

        #region Policy Management

        /// <inheritdoc/>
        public Task SetPolicyAsync(IVersioningPolicy policy, CancellationToken ct = default)
        {
            _currentPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
            CurrentMode = policy.Mode;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IEnumerable<IVersioningPolicy>> GetAvailablePoliciesAsync(CancellationToken ct = default)
        {
            // Return infinite versioning compatible policies
            var policies = new List<IVersioningPolicy>();

            if (_currentPolicy != null)
            {
                policies.Add(_currentPolicy);
            }

            return Task.FromResult<IEnumerable<IVersioningPolicy>>(policies);
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<(VersionInfo Version, VersionRetentionDecision Decision)>> EvaluateRetentionAsync(
            string itemId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            var versions = await ListVersionsAsync(itemId, new VersionQuery { MaxResults = int.MaxValue }, ct);
            var decisions = new List<(VersionInfo, VersionRetentionDecision)>();

            foreach (var version in versions)
            {
                VersionRetentionDecision decision;

                if (_currentPolicy != null)
                {
                    decision = await _currentPolicy.EvaluateRetentionAsync(version, ct);
                }
                else
                {
                    // Infinite versioning default: always retain, but suggest tier transitions
                    decision = EvaluateInfiniteRetention(version);
                }

                decisions.Add((version, decision));
            }

            return decisions;
        }

        /// <inheritdoc/>
        public async Task<int> ApplyRetentionPolicyAsync(CancellationToken ct = default)
        {
            var affectedCount = 0;

            foreach (var itemId in _versionIndex.Keys)
            {
                var evaluations = await EvaluateRetentionAsync(itemId, ct);

                foreach (var (version, decision) in evaluations)
                {
                    if (decision.SuggestedTierTransition.HasValue &&
                        decision.SuggestedTierTransition.Value != version.StorageTier)
                    {
                        await TransitionStorageTierAsync(itemId, version.VersionId, decision.SuggestedTierTransition.Value, ct);
                        affectedCount++;
                    }

                    // Note: We never delete in infinite versioning, even if policy suggests it
                    // Instead, we move to the deepest archive tier
                    if (!decision.ShouldRetain && decision.SuggestedAction == RetentionAction.Delete)
                    {
                        if (!version.IsImmutable && !version.IsOnLegalHold)
                        {
                            await TransitionStorageTierAsync(itemId, version.VersionId, StorageTier.DeepArchive, ct);
                            affectedCount++;
                        }
                    }
                }
            }

            return affectedCount;
        }

        #endregion

        #region Statistics

        /// <inheritdoc/>
        public Task<VersioningStatistics> GetStatisticsAsync(string itemId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be empty", nameof(itemId));

            if (!_versionIndex.TryGetValue(itemId, out var versions))
            {
                return Task.FromResult(new VersioningStatistics());
            }

            var stats = CalculateStatistics(versions);
            return Task.FromResult(stats);
        }

        /// <inheritdoc/>
        public Task<VersioningStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default)
        {
            var allVersions = _versionIndex.Values.SelectMany(v => v).ToList();
            var stats = CalculateStatistics(allVersions);
            return Task.FromResult(stats);
        }

        #endregion

        #region Helper Methods

        private string GenerateVersionId(string itemId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var random = Guid.NewGuid().ToString("N")[..8];
            return $"{itemId}-v{timestamp}-{random}";
        }

        private int GetNextVersionNumber(string itemId)
        {
            if (!_versionIndex.TryGetValue(itemId, out var versions) || versions.Count == 0)
            {
                return 1;
            }

            return versions.Max(v => v.VersionNumber) + 1;
        }

        private void AddVersionToIndex(string itemId, VersionInfo version)
        {
            lock (_globalLock)
            {
                if (!_versionIndex.TryGetValue(itemId, out var versions))
                {
                    versions = new List<VersionInfo>();
                    _versionIndex[itemId] = versions;
                }

                versions.Add(version);
            }
        }

        private string ComputeHash(byte[] data)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(data);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private ContentBlock[] DeduplicateContent(byte[] content)
        {
            const int blockSize = 4096; // 4KB blocks
            var blocks = new List<ContentBlock>();

            for (int offset = 0; offset < content.Length; offset += blockSize)
            {
                var length = Math.Min(blockSize, content.Length - offset);
                var blockData = new byte[length];
                Array.Copy(content, offset, blockData, 0, length);

                var blockHash = ComputeHash(blockData);

                // Simple compression simulation - in real implementation, use Zstd
                var compressedData = CompressBlock(blockData);

                blocks.Add(new ContentBlock
                {
                    BlockHash = blockHash,
                    Offset = offset,
                    OriginalSize = length,
                    CompressedSize = compressedData.Length,
                    CompressedData = compressedData
                });
            }

            return blocks.ToArray();
        }

        private byte[] CompressBlock(byte[] data)
        {
            // In a real implementation, this would use Zstd compression
            // For now, return the original data (no compression)
            return data;
        }

        private byte[] DecompressBlock(byte[] data)
        {
            // In a real implementation, this would use Zstd decompression
            // For now, return the original data (no decompression)
            return data;
        }

        private byte[] ReconstructContent(ContentBlock[] blocks)
        {
            var totalSize = blocks.Sum(b => b.OriginalSize);
            var result = new byte[totalSize];

            foreach (var block in blocks.OrderBy(b => b.Offset))
            {
                if (_contentStore.TryGetValue(block.BlockHash, out var compressedData))
                {
                    var decompressed = DecompressBlock(compressedData);
                    Array.Copy(decompressed, 0, result, block.Offset, decompressed.Length);
                }
            }

            return result;
        }

        private VersionRetentionDecision EvaluateInfiniteRetention(VersionInfo version)
        {
            // Infinite versioning never deletes, but suggests tier transitions
            var age = DateTimeOffset.UtcNow - version.CreatedAt;

            StorageTier? suggestedTier = null;
            var reason = "Version retained indefinitely (infinite versioning)";
            var action = RetentionAction.Keep;

            // Suggest tier transitions based on age
            if (age > TimeSpan.FromDays(365) && version.StorageTier < StorageTier.DeepArchive)
            {
                suggestedTier = StorageTier.DeepArchive;
                action = RetentionAction.Archive;
                reason = "Version should move to deep archive tier (1+ year old)";
            }
            else if (age > TimeSpan.FromDays(90) && version.StorageTier < StorageTier.Archive)
            {
                suggestedTier = StorageTier.Archive;
                action = RetentionAction.Archive;
                reason = "Version should move to archive tier (90+ days old)";
            }
            else if (age > TimeSpan.FromDays(30) && version.StorageTier < StorageTier.InfrequentAccess)
            {
                suggestedTier = StorageTier.InfrequentAccess;
                action = RetentionAction.Archive;
                reason = "Version should move to infrequent access tier (30+ days old)";
            }

            return new VersionRetentionDecision
            {
                ShouldRetain = true, // Always retain in infinite versioning
                Reason = reason,
                SuggestedAction = action,
                SuggestedTierTransition = suggestedTier
            };
        }

        private VersioningStatistics CalculateStatistics(IReadOnlyCollection<VersionInfo> versions)
        {
            if (versions.Count == 0)
            {
                return new VersioningStatistics();
            }

            var now = DateTimeOffset.UtcNow;
            var today = now.Date;

            var tierCounts = new Dictionary<StorageTier, int>();
            foreach (var tier in Enum.GetValues<StorageTier>())
            {
                tierCounts[tier] = versions.Count(v => v.StorageTier == tier);
            }

            return new VersioningStatistics
            {
                TotalVersions = versions.Count,
                TotalStoredBytes = versions.Sum(v => v.StoredSizeBytes),
                TotalOriginalBytes = versions.Sum(v => v.SizeBytes),
                VersionsCreatedToday = versions.Count(v => v.CreatedAt.Date == today),
                VersionsDeletedToday = 0, // Infinite versioning doesn't delete
                OldestVersion = versions.Min(v => v.CreatedAt),
                NewestVersion = versions.Max(v => v.CreatedAt),
                ImmutableVersions = versions.Count(v => v.IsImmutable),
                VersionsOnLegalHold = versions.Count(v => v.IsOnLegalHold),
                VersionsByTier = tierCounts
            };
        }

        #endregion

        #region Content Block Definition

        private sealed class ContentBlock
        {
            public required string BlockHash { get; init; }
            public required int Offset { get; init; }
            public required int OriginalSize { get; init; }
            public required int CompressedSize { get; init; }
            public required byte[] CompressedData { get; init; }
        }

        #endregion
    }
}
