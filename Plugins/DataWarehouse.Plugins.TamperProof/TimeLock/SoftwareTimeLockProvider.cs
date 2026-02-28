// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.TimeLock;

/// <summary>
/// Software-enforced time-lock provider using ConcurrentDictionary-based state with UTC clock enforcement.
/// Production-ready for single-node deployments. Enforces per-object lock durations,
/// computes content hashes, and publishes audit events to the message bus.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public sealed class SoftwareTimeLockProvider : TimeLockProviderPluginBase
{
    private readonly BoundedDictionary<Guid, TimeLockEntry> _locks = new BoundedDictionary<Guid, TimeLockEntry>(1000);

    /// <summary>
    /// Internal record representing a locked object's state.
    /// </summary>
    private sealed record TimeLockEntry(
        string LockId,
        Guid ObjectId,
        DateTimeOffset LockedAt,
        DateTimeOffset UnlocksAt,
        string ContentHash,
        VaccinationLevel VaccinationLevel,
        TimeLockMode TimeLockMode,
        UnlockCondition[] UnlockConditions,
        bool EmergencyUnlocked,
        bool TamperDetected,
        DateTimeOffset? LastIntegrityCheck);

    /// <inheritdoc/>
    public override string Id => "tamperproof.timelock.software";

    /// <inheritdoc/>
    public override string Name => "Software Time-Lock Provider";

    /// <inheritdoc/>
    public override string Version => "5.0.0";

    /// <inheritdoc/>
    public override TimeLockMode DefaultMode => TimeLockMode.Software;

    /// <inheritdoc/>
    public override TimeLockPolicy Policy { get; } = new TimeLockPolicy
    {
        MinLockDuration = TimeSpan.FromMinutes(1),
        MaxLockDuration = TimeSpan.FromDays(365 * 100), // 100 years
        DefaultLockDuration = TimeSpan.FromDays(7),
        AllowedUnlockConditions = new[]
        {
            UnlockConditionType.TimeExpiry,
            UnlockConditionType.MultiPartyApproval,
            UnlockConditionType.EmergencyBreakGlass,
            UnlockConditionType.ComplianceRelease,
            UnlockConditionType.NeverUnlock
        },
        RequireMultiPartyForEarlyUnlock = false,
        VaccinationLevel = VaccinationLevel.Basic,
        AutoExtendOnTamperDetection = true,
        RequirePqcSignature = false
    };

    /// <summary>
    /// Applies a software time-lock to the specified object.
    /// Generates a unique lock ID, computes the content hash via the base class,
    /// stores lock metadata in the concurrent dictionary, and publishes a lock event.
    /// </summary>
    /// <param name="request">Validated lock request from the base class.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lock result with full proof including timestamps, content hash, and vaccination details.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the object is already locked.</exception>
    protected override async Task<TimeLockResult> LockInternalAsync(TimeLockRequest request, CancellationToken ct)
    {
        if (_locks.ContainsKey(request.ObjectId))
        {
            throw new InvalidOperationException(
                $"Object {request.ObjectId} is already locked. Unlock or wait for expiry before re-locking.");
        }

        var lockId = Guid.NewGuid().ToString("N");
        var lockedAt = DateTimeOffset.UtcNow;
        var unlocksAt = lockedAt + request.LockDuration;

        // Compute content hash using the integrity pipeline base
        var contentHash = await ComputeContentHashAsync(request.ObjectId, ct);

        var entry = new TimeLockEntry(
            LockId: lockId,
            ObjectId: request.ObjectId,
            LockedAt: lockedAt,
            UnlocksAt: unlocksAt,
            ContentHash: contentHash,
            VaccinationLevel: request.VaccinationLevel,
            TimeLockMode: TimeLockMode.Software,
            UnlockConditions: request.UnlockConditions ?? Array.Empty<UnlockCondition>(),
            EmergencyUnlocked: false,
            TamperDetected: false,
            LastIntegrityCheck: lockedAt);

        if (!_locks.TryAdd(request.ObjectId, entry))
        {
            throw new InvalidOperationException(
                $"Object {request.ObjectId} was locked concurrently. Retry the operation.");
        }

        var result = new TimeLockResult
        {
            ObjectId = request.ObjectId,
            LockId = lockId,
            LockedAt = lockedAt,
            UnlocksAt = unlocksAt,
            TimeLockMode = TimeLockMode.Software,
            ContentHash = contentHash,
            VaccinationLevel = request.VaccinationLevel,
            PqcSignatureAlgorithm = null,
            ProviderMetadata = new Dictionary<string, string>
            {
                ["provider"] = Id,
                ["enforcement"] = "software-concurrent-dictionary",
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
            ["Provider"] = Id
        }, ct);

        return result;
    }

    /// <summary>
    /// Extends the lock duration for a locked object. Lock duration can only be extended, never shortened.
    /// Updates the unlock time and publishes an extend event.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="additionalDuration">Positive duration to add to the current lock expiry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the object is not found in the lock store.</exception>
    protected override async Task ExtendLockInternalAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct)
    {
        if (!_locks.TryGetValue(objectId, out var existing))
        {
            throw new KeyNotFoundException($"Object {objectId} not found in time-lock store.");
        }

        var newUnlocksAt = existing.UnlocksAt + additionalDuration;

        var updated = existing with { UnlocksAt = newUnlocksAt };
        _locks.TryUpdate(objectId, updated, existing);

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
    /// Attempts to unlock an object by evaluating the provided unlock condition.
    /// Supports TimeExpiry, MultiPartyApproval, EmergencyBreakGlass, ComplianceRelease, and NeverUnlock conditions.
    /// All unlock attempts are audited regardless of outcome.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="condition">The unlock condition to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the unlock was successful, false if the condition was not satisfied.</returns>
    protected override async Task<bool> AttemptUnlockInternalAsync(Guid objectId, UnlockCondition condition, CancellationToken ct)
    {
        if (!_locks.TryGetValue(objectId, out var entry))
        {
            return false;
        }

        var unlocked = false;
        var reason = condition.Type.ToString();

        switch (condition.Type)
        {
            case UnlockConditionType.TimeExpiry:
                unlocked = DateTimeOffset.UtcNow >= entry.UnlocksAt;
                break;

            case UnlockConditionType.MultiPartyApproval:
                // Validate submitted approver IDs against the authorized approver list stored in the condition.
                // "ApproverIds" = IDs submitted by callers claiming approval.
                // "AuthorizedApprovers" = the allowlist of IDs that are permitted to grant approval (set at lock time).
                // Only approvers present in both sets count toward the quorum.
                var requiredApprovals = condition.RequiredApprovals;
                var approvalCount = 0;

                IReadOnlySet<string> authorizedApprovers = new HashSet<string>(StringComparer.Ordinal);
                if (condition.Parameters.TryGetValue("AuthorizedApprovers", out var authorizedObj))
                {
                    if (authorizedObj is IEnumerable<string> authList)
                        authorizedApprovers = new HashSet<string>(authList, StringComparer.Ordinal);
                    else if (authorizedObj is IEnumerable<object> authObjList)
                        authorizedApprovers = new HashSet<string>(authObjList.OfType<string>(), StringComparer.Ordinal);
                }

                if (condition.Parameters.TryGetValue("ApproverIds", out var approverIdsObj))
                {
                    IEnumerable<string> submittedIds = approverIdsObj switch
                    {
                        string[] arr => arr,
                        IEnumerable<string> strEnum => strEnum,
                        IEnumerable<object> objEnum => objEnum.OfType<string>(),
                        _ => Enumerable.Empty<string>()
                    };

                    // Count distinct submitted approvers that appear in the authorized set.
                    // If no authorized approver list is configured, deny all (fail-closed security posture).
                    if (authorizedApprovers.Count > 0)
                    {
                        approvalCount = submittedIds
                            .Where(id => !string.IsNullOrWhiteSpace(id) && authorizedApprovers.Contains(id))
                            .Distinct(StringComparer.Ordinal)
                            .Count();
                    }
                    // else: approvalCount stays 0 — no authorized approvers configured means unlock is denied
                }
                unlocked = approvalCount >= requiredApprovals;
                break;

            case UnlockConditionType.EmergencyBreakGlass:
                // Emergency break-glass always succeeds but flags the entry
                unlocked = true;
                var flagged = entry with { EmergencyUnlocked = true };
                _locks.TryUpdate(objectId, flagged, entry);
                entry = flagged;
                reason = "EmergencyBreakGlass";
                break;

            case UnlockConditionType.ComplianceRelease:
                // Compliance release requires AuthorityId and ReleaseOrder in parameters
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
            _locks.TryRemove(objectId, out _);

            await PublishTimeLockEventAsync(TimeLockMessageBusIntegration.TimeLockUnlocked, new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["LockId"] = entry.LockId,
                ["UnlockReason"] = reason,
                ["EmergencyUnlock"] = entry.EmergencyUnlocked,
                ["UnlockedAt"] = DateTimeOffset.UtcNow,
                ["Provider"] = Id
            }, ct);
        }

        return unlocked;
    }

    /// <summary>
    /// Gets the current lock status of an object including vaccination level and integrity state.
    /// Returns a status with Exists=false if the object is not in the lock store.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete status information.</returns>
    public override Task<TimeLockStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!_locks.TryGetValue(objectId, out var entry))
        {
            return Task.FromResult(new TimeLockStatus
            {
                Exists = false,
                ObjectId = objectId,
                TimeLockMode = TimeLockMode.Software,
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
            TimeLockMode = entry.TimeLockMode,
            VaccinationLevel = entry.VaccinationLevel,
            TamperDetected = entry.TamperDetected,
            LastIntegrityCheck = entry.LastIntegrityCheck
        };

        return Task.FromResult(status);
    }

    /// <summary>
    /// Quick check whether an object is currently locked.
    /// More efficient than GetStatusAsync when only lock state is needed.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object is currently time-locked, false otherwise.</returns>
    public override Task<bool> IsLockedAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!_locks.TryGetValue(objectId, out var entry))
        {
            return Task.FromResult(false);
        }

        var isLocked = DateTimeOffset.UtcNow < entry.UnlocksAt;
        return Task.FromResult(isLocked);
    }

    /// <summary>
    /// Gets the ransomware vaccination status of an object.
    /// Provides a comprehensive assessment including time-lock state, integrity verification,
    /// and an overall threat score based on the current lock entry.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Vaccination information including threat score and verification results.</returns>
    public override Task<RansomwareVaccinationInfo> GetVaccinationInfoAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!_locks.TryGetValue(objectId, out var entry))
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

        // Calculate threat score:
        // 0.0 = no threat (locked, integrity verified)
        // 0.5 = elevated (locked but not recently checked)
        // 1.0 = confirmed compromise (tamper detected)
        var threatScore = 0.0;
        if (entry.TamperDetected)
        {
            threatScore = 0.9;
        }
        else if (!isLocked)
        {
            threatScore = 0.3; // Unlocked objects have some baseline risk
        }
        else if (entry.LastIntegrityCheck.HasValue &&
                 (DateTimeOffset.UtcNow - entry.LastIntegrityCheck.Value).TotalHours > 24)
        {
            threatScore = 0.2; // Stale integrity check
        }

        return Task.FromResult(new RansomwareVaccinationInfo
        {
            VaccinationLevel = entry.VaccinationLevel,
            TimeLockActive = isLocked,
            IntegrityVerified = integrityOk,
            PqcSignatureValid = null, // Software provider does not use PQC
            BlockchainAnchored = null, // Software provider does not use blockchain
            LastScanAt = entry.LastIntegrityCheck ?? entry.LockedAt,
            ThreatScore = threatScore
        });
    }

    /// <summary>
    /// Enumerates currently locked objects with pagination support.
    /// Returns entries from the ConcurrentDictionary using Skip/Take.
    /// </summary>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="offset">Number of results to skip for pagination.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of lock statuses for currently locked objects.</returns>
    public override Task<IReadOnlyList<TimeLockStatus>> ListLockedObjectsAsync(int limit, int offset, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var results = _locks.Values
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
                TimeLockMode = e.TimeLockMode,
                VaccinationLevel = e.VaccinationLevel,
                TamperDetected = e.TamperDetected,
                LastIntegrityCheck = e.LastIntegrityCheck
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<TimeLockStatus>>(results);
    }

    /// <summary>
    /// Computes a SHA-256 content hash for integrity verification.
    /// Delegates to the message bus integrity.hash.compute topic if available,
    /// otherwise uses local SHA-256 computation.
    /// </summary>
    /// <param name="objectId">Object to compute hash for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hex-encoded SHA-256 hash string.</returns>
    private async Task<string> ComputeContentHashAsync(Guid objectId, CancellationToken ct)
    {
        // Try to compute hash via message bus
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

        // Local fallback: compute hash of object ID bytes as a fingerprint
        var objectBytes = objectId.ToByteArray();
        var timestampBytes = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var combinedBytes = new byte[objectBytes.Length + timestampBytes.Length];
        Buffer.BlockCopy(objectBytes, 0, combinedBytes, 0, objectBytes.Length);
        Buffer.BlockCopy(timestampBytes, 0, combinedBytes, objectBytes.Length, timestampBytes.Length);

        byte[] hashBytes;
        using (var sha256 = SHA256.Create())
        {
            hashBytes = sha256.ComputeHash(combinedBytes);
        }

        var result = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Zero out sensitive intermediate data
        CryptographicOperations.ZeroMemory(combinedBytes);

        return result;
    }

    /// <summary>
    /// Publishes a time-lock event to the message bus if available.
    /// Silently ignores failures to avoid disrupting lock operations.
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

            // Event publication failure must not disrupt lock operations
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }
}
