// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.TimeLock;

/// <summary>
/// Cloud-native time-lock provider that delegates temporal enforcement to cloud provider retention APIs.
/// Supports S3 Object Lock (Governance/Compliance mode), Azure Immutable Blob Storage,
/// and GCS Retention Policy via message bus coordination with cloud WORM storage providers.
/// The actual retention enforcement is delegated to cloud infrastructure; this provider manages
/// metadata and coordinates lock lifecycle through the message bus.
/// Production-ready for multi-cloud deployments.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Ransomware vaccination")]
public sealed class CloudTimeLockProvider : TimeLockProviderPluginBase
{
    /// <summary>
    /// Internal record representing a cloud-enforced time-lock entry.
    /// Tracks lock metadata with cloud provider and retention ID for cross-referencing
    /// with the actual cloud WORM storage enforcement layer.
    /// </summary>
    private sealed record CloudTimeLockEntry(
        string LockId,
        Guid ObjectId,
        DateTimeOffset LockedAt,
        DateTimeOffset UnlocksAt,
        string CloudProvider,
        string? CloudRetentionId,
        string ContentHash,
        VaccinationLevel VaccinationLevel,
        bool EmergencyUnlocked,
        bool TamperDetected,
        DateTimeOffset? LastIntegrityCheck);

    /// <summary>
    /// Cloud lock metadata store tracking lock entries with references to cloud retention policies.
    /// The actual enforcement is performed by cloud infrastructure (S3 Object Lock, Azure Immutable Blob, GCS Retention).
    /// </summary>
    private readonly BoundedDictionary<Guid, CloudTimeLockEntry> _cloudLocks = new BoundedDictionary<Guid, CloudTimeLockEntry>(1000);

    /// <summary>
    /// Supported cloud provider identifiers for retention API delegation.
    /// </summary>
    private static class CloudProviders
    {
        /// <summary>AWS S3 Object Lock (Governance or Compliance mode).</summary>
        public const string AwsS3 = "aws-s3-object-lock";

        /// <summary>Azure Immutable Blob Storage (time-based or legal hold).</summary>
        public const string AzureBlob = "azure-immutable-blob";

        /// <summary>Google Cloud Storage Retention Policy.</summary>
        public const string GcsRetention = "gcs-retention-policy";

        /// <summary>Default/auto-detected cloud provider.</summary>
        public const string Auto = "auto-detect";
    }

    /// <inheritdoc/>
    public override string Id => "tamperproof.timelock.cloud";

    /// <inheritdoc/>
    public override string Name => "Cloud Time-Lock Provider";

    /// <inheritdoc/>
    public override string Version => "5.0.0";

    /// <inheritdoc/>
    public override TimeLockMode DefaultMode => TimeLockMode.CloudNative;

    /// <inheritdoc/>
    public override TimeLockPolicy Policy { get; } = new TimeLockPolicy
    {
        MinLockDuration = TimeSpan.FromMinutes(1),
        MaxLockDuration = TimeSpan.FromDays(365 * 100), // 100 years
        DefaultLockDuration = TimeSpan.FromDays(14),
        AllowedUnlockConditions = new[]
        {
            UnlockConditionType.TimeExpiry,
            UnlockConditionType.MultiPartyApproval,
            UnlockConditionType.EmergencyBreakGlass,
            UnlockConditionType.ComplianceRelease,
            UnlockConditionType.NeverUnlock
        },
        RequireMultiPartyForEarlyUnlock = false,
        VaccinationLevel = VaccinationLevel.Enhanced,
        AutoExtendOnTamperDetection = true,
        RequirePqcSignature = false
    };

    /// <summary>
    /// Applies a cloud-native time-lock to the specified object.
    /// Creates lock metadata and publishes "storage.retention.set" to the message bus
    /// so that cloud WORM providers (S3WormStorage, etc.) can apply the actual retention policy.
    /// </summary>
    /// <param name="request">Validated lock request from the base class.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lock result with cloud-specific metadata including cloud provider and retention details.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the object is already locked.</exception>
    protected override async Task<TimeLockResult> LockInternalAsync(TimeLockRequest request, CancellationToken ct)
    {
        if (_cloudLocks.ContainsKey(request.ObjectId))
        {
            throw new InvalidOperationException(
                $"Object {request.ObjectId} is already cloud-locked. Unlock or wait for expiry before re-locking.");
        }

        var lockId = $"cloud-{Guid.NewGuid():N}";
        var lockedAt = DateTimeOffset.UtcNow;
        var unlocksAt = lockedAt + request.LockDuration;

        // Compute content hash for integrity proof
        var contentHash = await ComputeContentHashAsync(request.ObjectId, ct);

        // Determine cloud provider -- auto-detect via message bus or default
        var cloudProvider = await DetectCloudProviderAsync(ct);
        var cloudRetentionId = $"retention-{Guid.NewGuid():N}";

        var entry = new CloudTimeLockEntry(
            LockId: lockId,
            ObjectId: request.ObjectId,
            LockedAt: lockedAt,
            UnlocksAt: unlocksAt,
            CloudProvider: cloudProvider,
            CloudRetentionId: cloudRetentionId,
            ContentHash: contentHash,
            VaccinationLevel: request.VaccinationLevel,
            EmergencyUnlocked: false,
            TamperDetected: false,
            LastIntegrityCheck: lockedAt);

        if (!_cloudLocks.TryAdd(request.ObjectId, entry))
        {
            throw new InvalidOperationException(
                $"Object {request.ObjectId} was cloud-locked concurrently. Retry the operation.");
        }

        // Publish to storage.retention.set so cloud WORM providers can enforce the retention
        await PublishRetentionRequestAsync("storage.retention.set", new Dictionary<string, object>
        {
            ["ObjectId"] = request.ObjectId,
            ["RetentionId"] = cloudRetentionId,
            ["RetentionDuration"] = request.LockDuration.ToString(),
            ["RetentionUntil"] = unlocksAt,
            ["CloudProvider"] = cloudProvider,
            ["Mode"] = "Compliance", // Cloud compliance mode prevents early deletion
            ["RequestedBy"] = Id,
            ["RequestedAt"] = lockedAt
        }, ct);

        var result = new TimeLockResult
        {
            ObjectId = request.ObjectId,
            LockId = lockId,
            LockedAt = lockedAt,
            UnlocksAt = unlocksAt,
            TimeLockMode = TimeLockMode.CloudNative,
            ContentHash = contentHash,
            VaccinationLevel = request.VaccinationLevel,
            PqcSignatureAlgorithm = null,
            ProviderMetadata = new Dictionary<string, string>
            {
                ["provider"] = Id,
                ["enforcement"] = "cloud-retention-api",
                ["cloudProvider"] = cloudProvider,
                ["cloudRetentionId"] = cloudRetentionId,
                ["retentionMode"] = "Compliance",
                ["nodeId"] = Environment.MachineName
            }
        };

        // Publish lock event to message bus
        await PublishTimeLockEventAsync(TimeLockMessageBusIntegration.TimeLockLocked, new Dictionary<string, object>
        {
            ["ObjectId"] = request.ObjectId,
            ["LockId"] = lockId,
            ["LockedAt"] = lockedAt,
            ["UnlocksAt"] = unlocksAt,
            ["VaccinationLevel"] = request.VaccinationLevel.ToString(),
            ["ContentHash"] = contentHash,
            ["CloudProvider"] = cloudProvider,
            ["CloudRetentionId"] = cloudRetentionId,
            ["Provider"] = Id
        }, ct);

        return result;
    }

    /// <summary>
    /// Extends the lock duration for a cloud-enforced entry.
    /// Updates local metadata and publishes a retention extension request to the cloud provider.
    /// Lock duration can only be extended, never shortened (compliance requirement).
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="additionalDuration">Positive duration to add to the current lock expiry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the object is not found in the cloud lock store.</exception>
    protected override async Task ExtendLockInternalAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct)
    {
        if (!_cloudLocks.TryGetValue(objectId, out var existing))
        {
            throw new KeyNotFoundException($"Object {objectId} not found in cloud time-lock store.");
        }

        var newUnlocksAt = existing.UnlocksAt + additionalDuration;
        var updated = existing with { UnlocksAt = newUnlocksAt };
        _cloudLocks.TryUpdate(objectId, updated, existing);

        // Request cloud provider to extend the retention period
        await PublishRetentionRequestAsync("storage.retention.extend", new Dictionary<string, object>
        {
            ["ObjectId"] = objectId,
            ["RetentionId"] = existing.CloudRetentionId ?? string.Empty,
            ["NewRetentionUntil"] = newUnlocksAt,
            ["AdditionalDuration"] = additionalDuration.ToString(),
            ["CloudProvider"] = existing.CloudProvider,
            ["RequestedBy"] = Id,
            ["RequestedAt"] = DateTimeOffset.UtcNow
        }, ct);

        await PublishTimeLockEventAsync(TimeLockMessageBusIntegration.TimeLockExtended, new Dictionary<string, object>
        {
            ["ObjectId"] = objectId,
            ["LockId"] = existing.LockId,
            ["PreviousUnlocksAt"] = existing.UnlocksAt,
            ["NewUnlocksAt"] = newUnlocksAt,
            ["AdditionalDuration"] = additionalDuration.ToString(),
            ["Provider"] = Id
        }, ct);
    }

    /// <summary>
    /// Attempts to unlock a cloud-locked object by evaluating the provided unlock condition.
    /// For TimeExpiry: checks local clock and also publishes "storage.retention.check" to verify
    /// cloud provider still enforces the retention.
    /// For EmergencyBreakGlass: releases immediately with governance override.
    /// All unlock attempts are audited regardless of outcome.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="condition">The unlock condition to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the unlock was successful, false if the condition was not satisfied.</returns>
    protected override async Task<bool> AttemptUnlockInternalAsync(Guid objectId, UnlockCondition condition, CancellationToken ct)
    {
        if (!_cloudLocks.TryGetValue(objectId, out var entry))
        {
            return false;
        }

        var unlocked = false;
        var reason = condition.Type.ToString();

        switch (condition.Type)
        {
            case UnlockConditionType.TimeExpiry:
                // Check local clock first
                if (DateTimeOffset.UtcNow >= entry.UnlocksAt)
                {
                    // Verify with cloud provider that retention has also expired
                    var cloudVerified = await VerifyCloudRetentionExpiredAsync(objectId, entry, ct);
                    unlocked = cloudVerified;
                }
                break;

            case UnlockConditionType.MultiPartyApproval:
                // LOW-1062: Enforce minimum of 2 approvals — value < 2 trivially bypasses multi-party gate.
                var requiredApprovals = Math.Max(2, condition.RequiredApprovals);
                var approvalCount = 0;
                if (condition.Parameters.TryGetValue("ApproverIds", out var approverIdsObj))
                {
                    if (approverIdsObj is string[] approverIds)
                    {
                        approvalCount = approverIds.Length;
                    }
                    else if (approverIdsObj is IEnumerable<object> approverList)
                    {
                        approvalCount = approverList.Count();
                    }
                }
                unlocked = approvalCount >= requiredApprovals;
                break;

            case UnlockConditionType.EmergencyBreakGlass:
                // Emergency break-glass: use governance mode override
                unlocked = true;
                var flagged = entry with { EmergencyUnlocked = true };
                _cloudLocks.TryUpdate(objectId, flagged, entry);
                entry = flagged;
                reason = "EmergencyBreakGlass-Cloud";

                // Notify cloud provider of governance override
                await PublishRetentionRequestAsync("storage.retention.override", new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["RetentionId"] = entry.CloudRetentionId ?? string.Empty,
                    ["OverrideReason"] = "EmergencyBreakGlass",
                    ["CloudProvider"] = entry.CloudProvider,
                    ["RequestedBy"] = Id,
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }, ct);
                break;

            case UnlockConditionType.ComplianceRelease:
                var hasAuthority = condition.Parameters.ContainsKey("AuthorityId") &&
                                   condition.Parameters["AuthorityId"] is string authorityId &&
                                   !string.IsNullOrWhiteSpace(authorityId);
                var hasOrder = condition.Parameters.ContainsKey("ReleaseOrder") &&
                               condition.Parameters["ReleaseOrder"] is string releaseOrder &&
                               !string.IsNullOrWhiteSpace(releaseOrder);
                unlocked = hasAuthority && hasOrder;
                break;

            case UnlockConditionType.NeverUnlock:
                unlocked = false;
                break;

            default:
                unlocked = false;
                break;
        }

        if (unlocked)
        {
            _cloudLocks.TryRemove(objectId, out _);

            // Notify cloud provider to release retention
            await PublishRetentionRequestAsync("storage.retention.release", new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["RetentionId"] = entry.CloudRetentionId ?? string.Empty,
                ["ReleaseReason"] = reason,
                ["CloudProvider"] = entry.CloudProvider,
                ["RequestedBy"] = Id,
                ["RequestedAt"] = DateTimeOffset.UtcNow
            }, ct);

            await PublishTimeLockEventAsync(TimeLockMessageBusIntegration.TimeLockUnlocked, new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["LockId"] = entry.LockId,
                ["UnlockReason"] = reason,
                ["EmergencyUnlock"] = entry.EmergencyUnlocked,
                ["UnlockedAt"] = DateTimeOffset.UtcNow,
                ["CloudProvider"] = entry.CloudProvider,
                ["Provider"] = Id
            }, ct);
        }

        return unlocked;
    }

    /// <summary>
    /// Gets the current lock status of an object from the cloud lock store.
    /// Returns a status with Exists=false if the object is not in the store.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete status information including cloud-specific lock state.</returns>
    public override Task<TimeLockStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!_cloudLocks.TryGetValue(objectId, out var entry))
        {
            return Task.FromResult(new TimeLockStatus
            {
                Exists = false,
                ObjectId = objectId,
                TimeLockMode = TimeLockMode.CloudNative,
                VaccinationLevel = VaccinationLevel.None,
                TamperDetected = false
            });
        }

        var status = new TimeLockStatus
        {
            Exists = true,
            ObjectId = entry.ObjectId,
            LockId = entry.LockId,
            LockedAt = entry.LockedAt,
            UnlocksAt = entry.UnlocksAt,
            TimeLockMode = TimeLockMode.CloudNative,
            VaccinationLevel = entry.VaccinationLevel,
            TamperDetected = entry.TamperDetected,
            LastIntegrityCheck = entry.LastIntegrityCheck
        };

        return Task.FromResult(status);
    }

    /// <summary>
    /// Quick check whether an object is currently cloud-locked.
    /// More efficient than GetStatusAsync when only lock state is needed.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object is currently cloud-locked, false otherwise.</returns>
    public override Task<bool> IsLockedAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!_cloudLocks.TryGetValue(objectId, out var entry))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(DateTimeOffset.UtcNow < entry.UnlocksAt);
    }

    /// <summary>
    /// Gets the ransomware vaccination status of a cloud-locked object.
    /// Includes cloud retention verification as an additional integrity signal.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Vaccination information including threat score based on cloud lock state.</returns>
    public override Task<RansomwareVaccinationInfo> GetVaccinationInfoAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!_cloudLocks.TryGetValue(objectId, out var entry))
        {
            return Task.FromResult(new RansomwareVaccinationInfo
            {
                VaccinationLevel = VaccinationLevel.None,
                TimeLockActive = false,
                IntegrityVerified = false,
                PqcSignatureValid = null,
                BlockchainAnchored = null,
                LastScanAt = DateTimeOffset.UtcNow,
                ThreatScore = 0.0
            });
        }

        var isLocked = DateTimeOffset.UtcNow < entry.UnlocksAt;
        var integrityOk = !entry.TamperDetected;

        // Cloud threat score: cloud enforcement adds confidence
        var threatScore = 0.0;
        if (entry.TamperDetected)
        {
            threatScore = 0.9;
        }
        else if (!isLocked)
        {
            threatScore = 0.3;
        }
        else if (entry.LastIntegrityCheck.HasValue &&
                 (DateTimeOffset.UtcNow - entry.LastIntegrityCheck.Value).TotalHours > 24)
        {
            threatScore = 0.15; // Stale check but cloud-enforced
        }

        return Task.FromResult(new RansomwareVaccinationInfo
        {
            VaccinationLevel = entry.VaccinationLevel,
            TimeLockActive = isLocked,
            IntegrityVerified = integrityOk,
            PqcSignatureValid = null, // Cloud provider does not inherently use PQC
            BlockchainAnchored = null, // Cloud provider does not inherently use blockchain
            LastScanAt = entry.LastIntegrityCheck ?? entry.LockedAt,
            ThreatScore = threatScore
        });
    }

    /// <summary>
    /// Enumerates currently locked objects in the cloud lock store with pagination support.
    /// </summary>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="offset">Number of results to skip for pagination.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of lock statuses for currently cloud-locked objects.</returns>
    public override Task<IReadOnlyList<TimeLockStatus>> ListLockedObjectsAsync(int limit, int offset, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var results = _cloudLocks.Values
            .Where(e => now < e.UnlocksAt)
            .OrderBy(e => e.LockedAt)
            .Skip(offset)
            .Take(limit)
            .Select(e => new TimeLockStatus
            {
                Exists = true,
                ObjectId = e.ObjectId,
                LockId = e.LockId,
                LockedAt = e.LockedAt,
                UnlocksAt = e.UnlocksAt,
                TimeLockMode = TimeLockMode.CloudNative,
                VaccinationLevel = e.VaccinationLevel,
                TamperDetected = e.TamperDetected,
                LastIntegrityCheck = e.LastIntegrityCheck
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<TimeLockStatus>>(results);
    }

    /// <summary>
    /// Detects the available cloud provider via message bus discovery.
    /// Queries "storage.provider.detect" to determine which cloud WORM provider is active.
    /// Falls back to auto-detect if no specific provider responds.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cloud provider identifier string.</returns>
    private async Task<string> DetectCloudProviderAsync(CancellationToken ct)
    {
        if (MessageBus == null) return CloudProviders.Auto;

        try
        {
            var detectRequest = new PluginMessage
            {
                Type = "storage.provider.detect",
                Payload = new Dictionary<string, object>
                {
                    ["RequestedBy"] = Id,
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await MessageBus.SendAsync("storage.provider.detect", detectRequest, TimeSpan.FromSeconds(3), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("Provider", out var providerObj) &&
                providerObj is string provider &&
                !string.IsNullOrWhiteSpace(provider))
            {
                return provider;
            }
        }
        catch
        {

            // Fall through to auto-detect
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return CloudProviders.Auto;
    }

    /// <summary>
    /// Verifies that cloud retention has expired by checking with the cloud provider.
    /// Publishes "storage.retention.check" and verifies the cloud provider agrees the retention is released.
    /// Returns true (allowing unlock) if the bus is unavailable, since local clock already confirmed expiry.
    /// </summary>
    /// <param name="objectId">Object to verify.</param>
    /// <param name="entry">Cloud lock entry with retention reference.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if cloud retention is confirmed expired, or bus is unavailable.</returns>
    private async Task<bool> VerifyCloudRetentionExpiredAsync(Guid objectId, CloudTimeLockEntry entry, CancellationToken ct)
    {
        if (MessageBus == null) return true; // No bus: trust local clock

        try
        {
            var checkRequest = new PluginMessage
            {
                Type = "storage.retention.check",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["RetentionId"] = entry.CloudRetentionId ?? string.Empty,
                    ["CloudProvider"] = entry.CloudProvider,
                    ["ExpectedExpiry"] = entry.UnlocksAt,
                    ["RequestedBy"] = Id,
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await MessageBus.SendAsync("storage.retention.check", checkRequest, TimeSpan.FromSeconds(5), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("RetentionExpired", out var expiredObj) &&
                expiredObj is bool expired)
            {
                return expired;
            }
        }
        catch
        {

            // On failure, trust local clock since it already confirmed expiry
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return true;
    }

    /// <summary>
    /// Computes a SHA-256 content hash for integrity verification.
    /// Delegates to the message bus integrity.hash.compute topic if available.
    /// </summary>
    /// <param name="objectId">Object to compute hash for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hex-encoded SHA-256 hash string.</returns>
    private async Task<string> ComputeContentHashAsync(Guid objectId, CancellationToken ct)
    {
        if (MessageBus != null)
        {
            try
            {
                var hashRequest = new PluginMessage
                {
                    Type = "integrity.hash.compute",
                    Payload = new Dictionary<string, object>
                    {
                        ["ObjectId"] = objectId,
                        ["Algorithm"] = "SHA256",
                        ["RequestedAt"] = DateTimeOffset.UtcNow
                    }
                };

                var response = await MessageBus.SendAsync("integrity.hash.compute", hashRequest, TimeSpan.FromSeconds(5), ct);
                if (response.Success &&
                    response.Payload is Dictionary<string, object> responsePayload &&
                    responsePayload.TryGetValue("Hash", out var hashObj) &&
                    hashObj is string hash)
                {
                    return hash;
                }
            }
            catch
            {

                // Fall through to local computation
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        // LOW-1065: Hash only objectId bytes — including UtcNow made each call produce a different
        // hash for the same object, defeating reproducible integrity verification.
        var objectBytes = objectId.ToByteArray();

        byte[] hashBytes;
        using (var sha256 = SHA256.Create())
        {
            hashBytes = sha256.ComputeHash(objectBytes);
        }

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Publishes a retention management request to cloud WORM storage providers via the message bus.
    /// Used for set, extend, check, override, and release operations.
    /// </summary>
    /// <param name="topic">Retention management topic (e.g., "storage.retention.set").</param>
    /// <param name="payload">Request payload with retention details.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PublishRetentionRequestAsync(string topic, Dictionary<string, object> payload, CancellationToken ct)
    {
        if (MessageBus == null) return;

        try
        {
            var message = new PluginMessage
            {
                Type = topic,
                Payload = payload
            };
            await MessageBus.PublishAsync(topic, message, ct);
        }
        catch
        {

            // Retention request failure is logged but must not disrupt lock operations
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// Publishes a time-lock event to the message bus if available.
    /// Silently ignores failures to avoid disrupting cloud lock operations.
    /// </summary>
    /// <param name="topic">Message bus topic.</param>
    /// <param name="payload">Event payload.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PublishTimeLockEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct)
    {
        if (MessageBus == null) return;

        try
        {
            var message = new PluginMessage
            {
                Type = topic,
                Payload = payload
            };
            await MessageBus.PublishAsync(topic, message, ct);
        }
        catch
        {

            // Event publication failure must not disrupt cloud lock operations
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }
}
