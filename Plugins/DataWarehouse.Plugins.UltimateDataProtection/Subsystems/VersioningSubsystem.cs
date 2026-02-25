using System.Security.Cryptography;
using System.Text;
using DataWarehouse.Plugins.UltimateDataProtection.Versioning;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Subsystems
{
    /// <summary>
    /// Implementation of the versioning subsystem providing version lifecycle management.
    /// Supports manual, scheduled, event-based, continuous, and intelligent versioning modes.
    /// </summary>
    public sealed class VersioningSubsystem : IVersioningSubsystem
    {
        private readonly BoundedDictionary<string, BoundedDictionary<string, VersionInfo>> _versions = new BoundedDictionary<string, BoundedDictionary<string, VersionInfo>>(1000);
        private readonly BoundedDictionary<string, byte[]> _versionContent = new BoundedDictionary<string, byte[]>(1000);
        private readonly BoundedDictionary<string, HashSet<string>> _contentHashes = new BoundedDictionary<string, HashSet<string>>(1000);
        private IVersioningPolicy? _currentPolicy;
        private VersioningMode _currentMode = VersioningMode.Manual;
        private bool _isEnabled = true;

        /// <inheritdoc/>
        public VersioningMode CurrentMode => _currentMode;

        /// <inheritdoc/>
        public IVersioningPolicy? CurrentPolicy => _currentPolicy;

        /// <inheritdoc/>
        public bool IsEnabled => _isEnabled;

        #region Version Operations

        /// <inheritdoc/>
        public Task<VersionInfo> CreateVersionAsync(
            string itemId,
            VersionMetadata metadata,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentNullException.ThrowIfNull(metadata);

            var itemVersions = _versions.GetOrAdd(itemId, _ => new BoundedDictionary<string, VersionInfo>(1000));
            var versionNumber = itemVersions.Count + 1;
            var versionId = $"{itemId}_v{versionNumber}_{Guid.NewGuid():N}";

            var versionInfo = new VersionInfo
            {
                VersionId = versionId,
                ItemId = itemId,
                VersionNumber = versionNumber,
                CreatedAt = DateTimeOffset.UtcNow,
                Metadata = metadata,
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

            itemVersions[versionId] = versionInfo;
            return Task.FromResult(versionInfo);
        }

        /// <inheritdoc/>
        public Task<VersionInfo> CreateVersionWithContentAsync(
            string itemId,
            ReadOnlyMemory<byte> content,
            VersionMetadata metadata,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentNullException.ThrowIfNull(metadata);

            var contentArray = content.ToArray();
            var contentHash = ComputeHash(contentArray);

            // Check for deduplication
            var itemHashes = _contentHashes.GetOrAdd(itemId, _ => new HashSet<string>());
            var isDuplicate = itemHashes.Contains(contentHash);

            var itemVersions = _versions.GetOrAdd(itemId, _ => new BoundedDictionary<string, VersionInfo>(1000));
            var versionNumber = itemVersions.Count + 1;
            var versionId = $"{itemId}_v{versionNumber}_{Guid.NewGuid():N}";

            var storedSize = isDuplicate ? 0 : contentArray.Length;

            var versionInfo = new VersionInfo
            {
                VersionId = versionId,
                ItemId = itemId,
                VersionNumber = versionNumber,
                CreatedAt = DateTimeOffset.UtcNow,
                Metadata = metadata,
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

            itemVersions[versionId] = versionInfo;

            // Store content if not duplicate
            if (!isDuplicate)
            {
                _versionContent[versionId] = contentArray;
                itemHashes.Add(contentHash);
            }

            return Task.FromResult(versionInfo);
        }

        /// <inheritdoc/>
        public Task<IEnumerable<VersionInfo>> ListVersionsAsync(
            string itemId,
            VersionQuery query,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentNullException.ThrowIfNull(query);

            if (!_versions.TryGetValue(itemId, out var itemVersions))
            {
                return Task.FromResult(Enumerable.Empty<VersionInfo>());
            }

            var results = itemVersions.Values.AsEnumerable();

            // Apply filters
            if (query.CreatedAfter.HasValue)
            {
                results = results.Where(v => v.CreatedAt >= query.CreatedAfter.Value);
            }

            if (query.CreatedBefore.HasValue)
            {
                results = results.Where(v => v.CreatedAt <= query.CreatedBefore.Value);
            }

            if (!string.IsNullOrEmpty(query.LabelPattern))
            {
                results = results.Where(v => v.Metadata.Label?.Contains(query.LabelPattern, StringComparison.OrdinalIgnoreCase) == true);
            }

            if (!string.IsNullOrEmpty(query.CreatedBy))
            {
                results = results.Where(v => v.Metadata.CreatedBy == query.CreatedBy);
            }

            if (query.MinVersionNumber.HasValue)
            {
                results = results.Where(v => v.VersionNumber >= query.MinVersionNumber.Value);
            }

            if (query.MaxVersionNumber.HasValue)
            {
                results = results.Where(v => v.VersionNumber <= query.MaxVersionNumber.Value);
            }

            if (!query.IncludeExpired)
            {
                results = results.Where(v => !v.ExpiresAt.HasValue || v.ExpiresAt.Value > DateTimeOffset.UtcNow);
            }

            if (query.OnlyImmutable)
            {
                results = results.Where(v => v.IsImmutable);
            }

            if (query.StorageTier.HasValue)
            {
                results = results.Where(v => v.StorageTier == query.StorageTier.Value);
            }

            // Apply sorting
            results = query.SortOrder switch
            {
                VersionSortOrder.CreatedDescending => results.OrderByDescending(v => v.CreatedAt),
                VersionSortOrder.CreatedAscending => results.OrderBy(v => v.CreatedAt),
                VersionSortOrder.VersionDescending => results.OrderByDescending(v => v.VersionNumber),
                VersionSortOrder.VersionAscending => results.OrderBy(v => v.VersionNumber),
                VersionSortOrder.SizeDescending => results.OrderByDescending(v => v.SizeBytes),
                VersionSortOrder.SizeAscending => results.OrderBy(v => v.SizeBytes),
                _ => results.OrderByDescending(v => v.CreatedAt)
            };

            // Apply limit
            results = results.Take(query.MaxResults);

            return Task.FromResult(results);
        }

        /// <inheritdoc/>
        public Task<VersionInfo?> GetVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            if (!_versions.TryGetValue(itemId, out var itemVersions))
            {
                return Task.FromResult<VersionInfo?>(null);
            }

            itemVersions.TryGetValue(versionId, out var version);
            return Task.FromResult<VersionInfo?>(version);
        }

        /// <inheritdoc/>
        public Task<byte[]> GetVersionContentAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            if (!_versionContent.TryGetValue(versionId, out var content))
            {
                // Check if version exists but content was deduplicated
                if (_versions.TryGetValue(itemId, out var itemVersions) && itemVersions.ContainsKey(versionId))
                {
                    // Find content by hash
                    var version = itemVersions[versionId];
                    var matchingVersion = _versionContent
                        .Where(kvp => _versions.Values
                            .SelectMany(iv => iv.Values)
                            .Any(v => v.VersionId == kvp.Key && v.ContentHash == version.ContentHash))
                        .FirstOrDefault();

                    if (matchingVersion.Value != null)
                    {
                        return Task.FromResult(matchingVersion.Value);
                    }
                }

                throw new InvalidOperationException($"Content for version '{versionId}' not found.");
            }

            return Task.FromResult(content);
        }

        /// <inheritdoc/>
        public Task RestoreVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            // Verify version exists
            if (!_versions.TryGetValue(itemId, out var itemVersions) || !itemVersions.ContainsKey(versionId))
            {
                throw new InvalidOperationException($"Version '{versionId}' not found for item '{itemId}'.");
            }

            // In a real implementation, this would restore the content to the item
            // For now, we just validate that the version exists and is accessible
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task DeleteVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            if (!_versions.TryGetValue(itemId, out var itemVersions))
            {
                return Task.CompletedTask;
            }

            if (!itemVersions.TryGetValue(versionId, out var version))
            {
                return Task.CompletedTask;
            }

            // Cannot delete immutable or legal hold versions
            if (version.IsImmutable)
            {
                throw new InvalidOperationException($"Cannot delete immutable version '{versionId}'.");
            }

            if (version.IsOnLegalHold)
            {
                throw new InvalidOperationException($"Cannot delete version '{versionId}' that is on legal hold.");
            }

            // Remove version
            itemVersions.TryRemove(versionId, out _);
            _versionContent.TryRemove(versionId, out _);

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<VersionDiff> CompareVersionsAsync(
            string itemId,
            string versionId1,
            string versionId2,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId1);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId2);

            if (!_versions.TryGetValue(itemId, out var itemVersions))
            {
                throw new InvalidOperationException($"Item '{itemId}' not found.");
            }

            if (!itemVersions.TryGetValue(versionId1, out var version1))
            {
                throw new InvalidOperationException($"Version '{versionId1}' not found.");
            }

            if (!itemVersions.TryGetValue(versionId2, out var version2))
            {
                throw new InvalidOperationException($"Version '{versionId2}' not found.");
            }

            // Calculate differences
            var totalBytesChanged = Math.Abs(version2.SizeBytes - version1.SizeBytes);
            var changePercentage = version1.SizeBytes > 0
                ? (double)totalBytesChanged / version1.SizeBytes * 100
                : 0;

            var diff = new VersionDiff
            {
                SourceVersionId = versionId1,
                TargetVersionId = versionId2,
                TotalBytesChanged = totalBytesChanged,
                ChangePercentage = changePercentage,
                ComparedAt = DateTimeOffset.UtcNow,
                AddedItems = Array.Empty<DiffItem>(),
                ModifiedItems = new[]
                {
                    new DiffItem
                    {
                        Path = itemId,
                        ItemType = "version",
                        SourceSize = version1.SizeBytes,
                        TargetSize = version2.SizeBytes,
                        DiffType = DiffType.Modified
                    }
                },
                DeletedItems = Array.Empty<DiffItem>()
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
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            if (!_versions.TryGetValue(itemId, out var itemVersions) || !itemVersions.TryGetValue(versionId, out var version))
            {
                throw new InvalidOperationException($"Version '{versionId}' not found.");
            }

            itemVersions[versionId] = version with { IsImmutable = true };
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task PlaceLegalHoldAsync(
            string itemId,
            string versionId,
            string holdReason,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            if (!_versions.TryGetValue(itemId, out var itemVersions) || !itemVersions.TryGetValue(versionId, out var version))
            {
                throw new InvalidOperationException($"Version '{versionId}' not found.");
            }

            itemVersions[versionId] = version with { IsOnLegalHold = true };
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task RemoveLegalHoldAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            if (!_versions.TryGetValue(itemId, out var itemVersions) || !itemVersions.TryGetValue(versionId, out var version))
            {
                throw new InvalidOperationException($"Version '{versionId}' not found.");
            }

            itemVersions[versionId] = version with { IsOnLegalHold = false };
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task TransitionStorageTierAsync(
            string itemId,
            string versionId,
            StorageTier targetTier,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

            if (!_versions.TryGetValue(itemId, out var itemVersions) || !itemVersions.TryGetValue(versionId, out var version))
            {
                throw new InvalidOperationException($"Version '{versionId}' not found.");
            }

            itemVersions[versionId] = version with { StorageTier = targetTier };
            return Task.CompletedTask;
        }

        #endregion

        #region Policy Management

        /// <inheritdoc/>
        public Task SetPolicyAsync(IVersioningPolicy policy, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(policy);

            _currentPolicy = policy;
            _currentMode = policy.Mode;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IEnumerable<IVersioningPolicy>> GetAvailablePoliciesAsync(CancellationToken ct = default)
        {
            // In a real implementation, this would return all registered policies
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
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

            if (_currentPolicy == null)
            {
                return Enumerable.Empty<(VersionInfo, VersionRetentionDecision)>();
            }

            if (!_versions.TryGetValue(itemId, out var itemVersions))
            {
                return Enumerable.Empty<(VersionInfo, VersionRetentionDecision)>();
            }

            var results = new List<(VersionInfo, VersionRetentionDecision)>();

            foreach (var version in itemVersions.Values)
            {
                var decision = await _currentPolicy.EvaluateRetentionAsync(version, ct);
                results.Add((version, decision));
            }

            return results;
        }

        /// <inheritdoc/>
        public async Task<int> ApplyRetentionPolicyAsync(CancellationToken ct = default)
        {
            if (_currentPolicy == null)
            {
                return 0;
            }

            int affectedCount = 0;

            foreach (var itemId in _versions.Keys.ToList())
            {
                var decisions = await EvaluateRetentionAsync(itemId, ct);

                foreach (var (version, decision) in decisions)
                {
                    if (!decision.ShouldRetain && decision.SuggestedAction == RetentionAction.Delete)
                    {
                        await DeleteVersionAsync(itemId, version.VersionId, ct);
                        affectedCount++;
                    }
                    else if (decision.SuggestedTierTransition.HasValue)
                    {
                        await TransitionStorageTierAsync(itemId, version.VersionId, decision.SuggestedTierTransition.Value, ct);
                        affectedCount++;
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
            ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

            if (!_versions.TryGetValue(itemId, out var itemVersions))
            {
                return Task.FromResult(new VersioningStatistics());
            }

            var versions = itemVersions.Values.ToList();
            var stats = new VersioningStatistics
            {
                TotalVersions = versions.Count,
                TotalStoredBytes = versions.Sum(v => v.StoredSizeBytes),
                TotalOriginalBytes = versions.Sum(v => v.SizeBytes),
                VersionsCreatedToday = versions.Count(v => v.CreatedAt.Date == DateTimeOffset.UtcNow.Date),
                VersionsDeletedToday = 0,
                OldestVersion = versions.Any() ? versions.Min(v => v.CreatedAt) : null,
                NewestVersion = versions.Any() ? versions.Max(v => v.CreatedAt) : null,
                ImmutableVersions = versions.Count(v => v.IsImmutable),
                VersionsOnLegalHold = versions.Count(v => v.IsOnLegalHold),
                VersionsByTier = versions.GroupBy(v => v.StorageTier).ToDictionary(g => g.Key, g => g.Count())
            };

            return Task.FromResult(stats);
        }

        /// <inheritdoc/>
        public Task<VersioningStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default)
        {
            var allVersions = _versions.Values
                .SelectMany(itemVersions => itemVersions.Values)
                .ToList();

            var stats = new VersioningStatistics
            {
                TotalVersions = allVersions.Count,
                TotalStoredBytes = allVersions.Sum(v => v.StoredSizeBytes),
                TotalOriginalBytes = allVersions.Sum(v => v.SizeBytes),
                VersionsCreatedToday = allVersions.Count(v => v.CreatedAt.Date == DateTimeOffset.UtcNow.Date),
                VersionsDeletedToday = 0,
                OldestVersion = allVersions.Any() ? allVersions.Min(v => v.CreatedAt) : null,
                NewestVersion = allVersions.Any() ? allVersions.Max(v => v.CreatedAt) : null,
                ImmutableVersions = allVersions.Count(v => v.IsImmutable),
                VersionsOnLegalHold = allVersions.Count(v => v.IsOnLegalHold),
                VersionsByTier = allVersions.GroupBy(v => v.StorageTier).ToDictionary(g => g.Key, g => g.Count())
            };

            return Task.FromResult(stats);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Computes SHA256 hash of content.
        /// </summary>
        private static string ComputeHash(byte[] content)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(content);
            return Convert.ToHexString(hash);
        }

        #endregion
    }
}
