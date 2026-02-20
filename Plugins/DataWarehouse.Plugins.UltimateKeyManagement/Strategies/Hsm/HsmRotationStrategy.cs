using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// HSM Key Rotation strategy providing automated key lifecycle management.
    ///
    /// Features:
    /// - Automated rotation scheduling with configurable intervals
    /// - Key versioning with transparent version tracking
    /// - Old-key retention policy (configurable retention period)
    /// - Rotation audit trail with immutable event logging
    /// - Emergency rotation trigger for compromise response
    /// - Graceful transition with dual-key decryption support
    ///
    /// Key rotation follows the principle that all keys have a maximum lifetime.
    /// When a key is rotated, the old key is retained for decryption but new
    /// encryptions use the latest version.
    /// </summary>
    public sealed class HsmRotationStrategy : KeyStoreStrategyBase
    {
        private readonly BoundedDictionary<string, KeyVersion> _keyVersions = new BoundedDictionary<string, KeyVersion>(1000);
        private readonly BoundedDictionary<string, List<RotationAuditEntry>> _auditTrail = new BoundedDictionary<string, List<RotationAuditEntry>>(1000);
        private readonly SemaphoreSlim _rotationLock = new(1, 1);
        private TimeSpan _rotationInterval = TimeSpan.FromDays(90);
        private TimeSpan _retentionPeriod = TimeSpan.FromDays(365);
        private int _maxRetainedVersions = 10;
        private Timer? _rotationTimer;
        private string _currentKeyId = "hsm-rotation-master-v1";

        public override string StrategyId => "hsm-rotation";
        public override string Name => "HSM Key Rotation Manager";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = true,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "HSM Rotation Manager",
                ["SupportsAutoRotation"] = true,
                ["SupportsEmergencyRotation"] = true,
                ["SupportsRetentionPolicy"] = true,
                ["SupportsAuditTrail"] = true,
                ["SupportsVersionTracking"] = true
            }
        };

        protected override Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("hsm.rotation.init");

            if (Configuration.TryGetValue("RotationIntervalDays", out var interval) && interval is int days)
                _rotationInterval = TimeSpan.FromDays(days);

            if (Configuration.TryGetValue("RetentionPeriodDays", out var retention) && retention is int retDays)
                _retentionPeriod = TimeSpan.FromDays(retDays);

            if (Configuration.TryGetValue("MaxRetainedVersions", out var maxVersions) && maxVersions is int max)
                _maxRetainedVersions = max;

            // Initialize auto-rotation timer
            _rotationTimer = new Timer(
                async _ => await CheckAndRotateAsync(CancellationToken.None),
                null,
                _rotationInterval,
                _rotationInterval);

            // Create initial key version if none exists
            if (_keyVersions.IsEmpty)
            {
                var initialVersion = CreateKeyVersion(_currentKeyId, 1);
                _keyVersions[_currentKeyId] = initialVersion;
                RecordAuditEntry(_currentKeyId, "CREATED", "Initial key version created during initialization");
            }

            return Task.CompletedTask;
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            _rotationTimer?.Dispose();
            _rotationTimer = null;
            IncrementCounter("hsm.rotation.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public override Task<string> GetCurrentKeyIdAsync()
            => Task.FromResult(_currentKeyId);

        protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            if (_keyVersions.TryGetValue(keyId, out var version))
            {
                IncrementCounter("hsm.rotation.load");
                return Task.FromResult(version.KeyMaterial);
            }

            // Check if requesting a versioned key (keyId-vN format)
            foreach (var kvp in _keyVersions)
            {
                if (kvp.Value.PreviousVersions.Any(pv => pv.VersionedKeyId == keyId))
                {
                    var prev = kvp.Value.PreviousVersions.First(pv => pv.VersionedKeyId == keyId);
                    IncrementCounter("hsm.rotation.load.previous");
                    return Task.FromResult(prev.KeyMaterial);
                }
            }

            throw new KeyNotFoundException($"Key '{keyId}' not found in rotation store");
        }

        protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var version = _keyVersions.GetOrAdd(keyId, _ => CreateKeyVersion(keyId, 1));
            version.KeyMaterial = keyData;
            version.UpdatedAt = DateTime.UtcNow;
            IncrementCounter("hsm.rotation.save");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Rotates the specified key, creating a new version and retaining the old one.
        /// </summary>
        public async Task<RotationResult> RotateKeyAsync(string keyId, ISecurityContext context, string reason = "Scheduled rotation")
        {
            ValidateSecurityContext(context);
            await _rotationLock.WaitAsync();

            try
            {
                IncrementCounter("hsm.rotation.rotate");

                if (!_keyVersions.TryGetValue(keyId, out var currentVersion))
                    throw new KeyNotFoundException($"Key '{keyId}' not found for rotation");

                // Archive current version
                var archived = new ArchivedKeyVersion
                {
                    VersionedKeyId = $"{keyId}-v{currentVersion.Version}",
                    Version = currentVersion.Version,
                    KeyMaterial = (byte[])currentVersion.KeyMaterial.Clone(),
                    CreatedAt = currentVersion.CreatedAt,
                    RetiredAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(_retentionPeriod)
                };

                currentVersion.PreviousVersions.Add(archived);

                // Enforce retention limits
                EnforceRetentionPolicy(currentVersion);

                // Generate new key material
                var newKeyMaterial = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(newKeyMaterial);
                }

                currentVersion.Version++;
                currentVersion.KeyMaterial = newKeyMaterial;
                currentVersion.CreatedAt = DateTime.UtcNow;
                currentVersion.UpdatedAt = DateTime.UtcNow;
                currentVersion.LastRotatedAt = DateTime.UtcNow;

                RecordAuditEntry(keyId, "ROTATED", $"Key rotated to version {currentVersion.Version}. Reason: {reason}");

                return new RotationResult
                {
                    KeyId = keyId,
                    NewVersion = currentVersion.Version,
                    PreviousVersion = currentVersion.Version - 1,
                    RotatedAt = DateTime.UtcNow,
                    Reason = reason,
                    RetainedVersionCount = currentVersion.PreviousVersions.Count,
                    NextRotationDue = DateTime.UtcNow.Add(_rotationInterval)
                };
            }
            finally
            {
                _rotationLock.Release();
            }
        }

        /// <summary>
        /// Emergency rotation — immediately rotates a key with elevated logging.
        /// Used for incident response when key compromise is suspected.
        /// </summary>
        public async Task<RotationResult> EmergencyRotateAsync(string keyId, ISecurityContext context, string incidentId)
        {
            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Emergency rotation requires system administrator privileges.");

            IncrementCounter("hsm.rotation.emergency");
            RecordAuditEntry(keyId, "EMERGENCY_ROTATION_INITIATED",
                $"Emergency rotation triggered for incident: {incidentId}");

            var result = await RotateKeyAsync(keyId, context, $"Emergency rotation - Incident: {incidentId}");

            RecordAuditEntry(keyId, "EMERGENCY_ROTATION_COMPLETE",
                $"Emergency rotation completed. New version: {result.NewVersion}");

            return result;
        }

        /// <summary>
        /// Gets the rotation audit trail for a key.
        /// </summary>
        public IReadOnlyList<RotationAuditEntry> GetAuditTrail(string keyId)
        {
            return _auditTrail.TryGetValue(keyId, out var trail)
                ? trail.AsReadOnly()
                : Array.Empty<RotationAuditEntry>();
        }

        /// <summary>
        /// Gets key version metadata including rotation history.
        /// </summary>
        public KeyVersionInfo? GetKeyVersionInfo(string keyId)
        {
            if (!_keyVersions.TryGetValue(keyId, out var version))
                return null;

            return new KeyVersionInfo
            {
                KeyId = keyId,
                CurrentVersion = version.Version,
                CreatedAt = version.CreatedAt,
                LastRotatedAt = version.LastRotatedAt,
                NextRotationDue = version.LastRotatedAt?.Add(_rotationInterval) ?? version.CreatedAt.Add(_rotationInterval),
                RetainedVersionCount = version.PreviousVersions.Count,
                RetainedVersionIds = version.PreviousVersions.Select(pv => pv.VersionedKeyId).ToList()
            };
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            var result = await GetCachedHealthAsync(async ct =>
            {
                var overdue = _keyVersions.Values
                    .Where(v => v.LastRotatedAt.HasValue &&
                                DateTime.UtcNow - v.LastRotatedAt.Value > _rotationInterval)
                    .Select(v => v.KeyId)
                    .ToList();

                var message = overdue.Any()
                    ? $"Keys overdue for rotation: {string.Join(", ", overdue)}"
                    : "All keys within rotation schedule";

                return new StrategyHealthCheckResult(!overdue.Any(), message);
            }, TimeSpan.FromSeconds(60), cancellationToken);

            return result.IsHealthy;
        }

        public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            return Task.FromResult<IReadOnlyList<string>>(_keyVersions.Keys.ToList().AsReadOnly());
        }

        public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Only system administrators can delete rotation keys.");

            if (_keyVersions.TryRemove(keyId, out var removed))
            {
                CryptographicOperations.ZeroMemory(removed.KeyMaterial);
                foreach (var prev in removed.PreviousVersions)
                    CryptographicOperations.ZeroMemory(prev.KeyMaterial);

                RecordAuditEntry(keyId, "DELETED", "Key and all versions permanently deleted");
            }

            return Task.CompletedTask;
        }

        public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            if (!_keyVersions.TryGetValue(keyId, out var version))
                return Task.FromResult<KeyMetadata?>(null);

            return Task.FromResult<KeyMetadata?>(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = version.CreatedAt,
                KeySizeBytes = version.KeyMaterial.Length,
                IsActive = true,
                Metadata = new Dictionary<string, object>
                {
                    ["Version"] = version.Version,
                    ["LastRotated"] = version.LastRotatedAt?.ToString("O") ?? "Never",
                    ["RetainedVersions"] = version.PreviousVersions.Count,
                    ["RotationInterval"] = _rotationInterval.TotalDays
                }
            });
        }

        private async Task CheckAndRotateAsync(CancellationToken cancellationToken)
        {
            foreach (var kvp in _keyVersions)
            {
                var version = kvp.Value;
                if (version.LastRotatedAt.HasValue &&
                    DateTime.UtcNow - version.LastRotatedAt.Value > _rotationInterval)
                {
                    try
                    {
                        var context = new SystemAdminSecurityContext();
                        await RotateKeyAsync(kvp.Key, context, "Automated scheduled rotation");
                    }
                    catch (Exception)
                    {
                        RecordAuditEntry(kvp.Key, "ROTATION_FAILED", "Scheduled rotation failed");
                    }
                }
            }
        }

        private void EnforceRetentionPolicy(KeyVersion version)
        {
            // Remove versions exceeding max count
            while (version.PreviousVersions.Count > _maxRetainedVersions)
            {
                var oldest = version.PreviousVersions[0];
                CryptographicOperations.ZeroMemory(oldest.KeyMaterial);
                version.PreviousVersions.RemoveAt(0);
            }

            // Remove expired versions
            var expired = version.PreviousVersions
                .Where(pv => pv.ExpiresAt < DateTime.UtcNow)
                .ToList();

            foreach (var exp in expired)
            {
                CryptographicOperations.ZeroMemory(exp.KeyMaterial);
                version.PreviousVersions.Remove(exp);
            }
        }

        private KeyVersion CreateKeyVersion(string keyId, int version)
        {
            var keyMaterial = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(keyMaterial);
            }

            return new KeyVersion
            {
                KeyId = keyId,
                Version = version,
                KeyMaterial = keyMaterial,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastRotatedAt = null,
                PreviousVersions = new List<ArchivedKeyVersion>()
            };
        }

        private void RecordAuditEntry(string keyId, string action, string details)
        {
            var entry = new RotationAuditEntry
            {
                Timestamp = DateTime.UtcNow,
                KeyId = keyId,
                Action = action,
                Details = details,
                EntryId = Guid.NewGuid().ToString("N")
            };

            _auditTrail.AddOrUpdate(keyId,
                _ => new List<RotationAuditEntry> { entry },
                (_, list) => { list.Add(entry); return list; });
        }

        public override void Dispose()
        {
            _rotationTimer?.Dispose();
            foreach (var version in _keyVersions.Values)
            {
                CryptographicOperations.ZeroMemory(version.KeyMaterial);
                foreach (var prev in version.PreviousVersions)
                    CryptographicOperations.ZeroMemory(prev.KeyMaterial);
            }
            base.Dispose();
        }
    }

    public sealed class RotationResult
    {
        public required string KeyId { get; init; }
        public required int NewVersion { get; init; }
        public required int PreviousVersion { get; init; }
        public required DateTime RotatedAt { get; init; }
        public required string Reason { get; init; }
        public required int RetainedVersionCount { get; init; }
        public required DateTime NextRotationDue { get; init; }
    }

    public sealed class KeyVersionInfo
    {
        public required string KeyId { get; init; }
        public required int CurrentVersion { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? LastRotatedAt { get; init; }
        public required DateTime NextRotationDue { get; init; }
        public required int RetainedVersionCount { get; init; }
        public required List<string> RetainedVersionIds { get; init; }
    }

    public sealed class RotationAuditEntry
    {
        public required DateTime Timestamp { get; init; }
        public required string KeyId { get; init; }
        public required string Action { get; init; }
        public required string Details { get; init; }
        public required string EntryId { get; init; }
    }

    internal sealed class KeyVersion
    {
        public required string KeyId { get; init; }
        public int Version { get; set; }
        public byte[] KeyMaterial { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? LastRotatedAt { get; set; }
        public List<ArchivedKeyVersion> PreviousVersions { get; set; } = new();
    }

    internal sealed class ArchivedKeyVersion
    {
        public required string VersionedKeyId { get; init; }
        public required int Version { get; init; }
        public required byte[] KeyMaterial { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime RetiredAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }

    internal sealed class SystemAdminSecurityContext : DataWarehouse.SDK.Security.ISecurityContext
    {
        public string UserId => "system";
        public string? TenantId => null;
        public IEnumerable<string> Roles => new[] { "admin" };
        public bool IsSystemAdmin => true;
    }
}
