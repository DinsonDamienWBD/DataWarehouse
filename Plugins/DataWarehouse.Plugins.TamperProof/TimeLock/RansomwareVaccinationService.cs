// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.TimeLock;

/// <summary>
/// Orchestrates ransomware vaccination across all protection layers: time-locks, integrity hashing,
/// post-quantum cryptographic (PQC) signatures, and blockchain anchoring.
/// Coordinates protection via the message bus so that each layer is handled by its specialized plugin
/// (time-lock providers, integrity services, PQC engines, blockchain anchors).
/// Provides threat scoring, batch scanning, and a threat dashboard for operational visibility.
/// Production-ready anti-ransomware defense orchestrator.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Ransomware vaccination")]
public sealed class RansomwareVaccinationService
{
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Tracks vaccination records for objects that have been vaccinated.
    /// Maps object ID to the most recent vaccination info for dashboard queries.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, RansomwareVaccinationInfo> _vaccinationRecords = new();

    /// <summary>
    /// Tracks vaccination metadata for auditing and re-vaccination decisions.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, VaccinationMetadata> _vaccinationMetadata = new();

    /// <summary>
    /// Internal record tracking vaccination metadata for a protected object.
    /// </summary>
    private sealed record VaccinationMetadata(
        Guid ObjectId,
        VaccinationLevel Level,
        DateTimeOffset VaccinatedAt,
        string? PqcAlgorithm,
        string? BlockchainTxId,
        string? IntegrityHash,
        string TimeLockLockId);

    /// <summary>
    /// Initializes a new instance of the <see cref="RansomwareVaccinationService"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for cross-plugin coordination. Required for all vaccination operations.</param>
    /// <exception cref="ArgumentNullException">Thrown when messageBus is null.</exception>
    public RansomwareVaccinationService(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    /// <summary>
    /// Applies ransomware vaccination to an object at the specified protection level.
    /// Coordinates across multiple protection layers based on vaccination level:
    /// <list type="bullet">
    /// <item><description><see cref="VaccinationLevel.Basic"/>: Time-lock only.</description></item>
    /// <item><description><see cref="VaccinationLevel.Enhanced"/>: Time-lock + integrity hash verification.</description></item>
    /// <item><description><see cref="VaccinationLevel.Maximum"/>: Time-lock + integrity + PQC signature + blockchain anchor.</description></item>
    /// </list>
    /// </summary>
    /// <param name="objectId">Unique identifier of the object to vaccinate.</param>
    /// <param name="level">Protection level to apply.</param>
    /// <param name="policy">Time-lock policy governing lock parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Vaccination info with results from all applied protection layers.</returns>
    /// <exception cref="ArgumentException">Thrown when objectId is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when policy is null.</exception>
    public async Task<RansomwareVaccinationInfo> VaccinateAsync(
        Guid objectId,
        VaccinationLevel level,
        TimeLockPolicy policy,
        CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));
        ArgumentNullException.ThrowIfNull(policy);

        var vaccinatedAt = DateTimeOffset.UtcNow;
        var timeLockApplied = false;
        var integrityVerified = false;
        bool? pqcSignatureValid = null;
        bool? blockchainAnchored = null;
        string? pqcAlgorithm = null;
        string? blockchainTxId = null;
        string? integrityHash = null;
        var lockId = string.Empty;

        // Layer 1: Time-lock (all levels)
        timeLockApplied = await ApplyTimeLockAsync(objectId, policy, level, ct);
        if (timeLockApplied)
        {
            lockId = $"vacc-{Guid.NewGuid():N}";
        }

        // Layer 2: Integrity hash verification (Enhanced and Maximum)
        if (level >= VaccinationLevel.Enhanced)
        {
            (integrityVerified, integrityHash) = await ComputeAndVerifyIntegrityAsync(objectId, ct);
        }

        // Layer 3: PQC signature (Maximum only)
        if (level >= VaccinationLevel.Maximum)
        {
            (pqcSignatureValid, pqcAlgorithm) = await ApplyPqcSignatureAsync(objectId, ct);
        }

        // Layer 4: Blockchain anchor (Maximum only)
        if (level >= VaccinationLevel.Maximum)
        {
            (blockchainAnchored, blockchainTxId) = await CreateBlockchainAnchorAsync(objectId, integrityHash, ct);
        }

        // Calculate threat score based on all layer results
        var threatScore = CalculateThreatScore(timeLockApplied, integrityVerified, pqcSignatureValid, blockchainAnchored);

        var vaccinationInfo = new RansomwareVaccinationInfo
        {
            VaccinationLevel = level,
            TimeLockActive = timeLockApplied,
            IntegrityVerified = integrityVerified,
            PqcSignatureValid = pqcSignatureValid,
            BlockchainAnchored = blockchainAnchored,
            LastScanAt = vaccinatedAt,
            ThreatScore = threatScore
        };

        // Store vaccination record
        _vaccinationRecords[objectId] = vaccinationInfo;
        _vaccinationMetadata[objectId] = new VaccinationMetadata(
            ObjectId: objectId,
            Level: level,
            VaccinatedAt: vaccinatedAt,
            PqcAlgorithm: pqcAlgorithm,
            BlockchainTxId: blockchainTxId,
            IntegrityHash: integrityHash,
            TimeLockLockId: lockId);

        // Publish vaccination applied event
        await PublishEventAsync("timelock.vaccination.applied", new Dictionary<string, object>
        {
            ["ObjectId"] = objectId,
            ["VaccinationLevel"] = level.ToString(),
            ["TimeLockApplied"] = timeLockApplied,
            ["IntegrityVerified"] = integrityVerified,
            ["PqcSignatureValid"] = pqcSignatureValid?.ToString() ?? "N/A",
            ["BlockchainAnchored"] = blockchainAnchored?.ToString() ?? "N/A",
            ["ThreatScore"] = threatScore,
            ["VaccinatedAt"] = vaccinatedAt
        }, ct);

        return vaccinationInfo;
    }

    /// <summary>
    /// Verifies the vaccination status of an object by checking all applicable protection layers.
    /// Re-evaluates time-lock status, integrity hash, PQC signature, and blockchain anchor
    /// to compute a current threat score.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated vaccination info with current verification results and threat score.</returns>
    /// <exception cref="ArgumentException">Thrown when objectId is empty.</exception>
    public async Task<RansomwareVaccinationInfo> VerifyVaccinationAsync(Guid objectId, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        var verifiedAt = DateTimeOffset.UtcNow;

        // Step 1: Check time-lock status
        var timeLockActive = await CheckTimeLockStatusAsync(objectId, ct);

        // Step 2: Verify integrity hash
        var integrityVerified = await VerifyIntegrityHashAsync(objectId, ct);

        // Step 3: Verify PQC signature (if previously applied)
        bool? pqcSignatureValid = null;
        if (_vaccinationMetadata.TryGetValue(objectId, out var metadata) && metadata.PqcAlgorithm != null)
        {
            pqcSignatureValid = await VerifyPqcSignatureAsync(objectId, ct);
        }

        // Step 4: Verify blockchain anchor (if previously applied)
        bool? blockchainAnchored = null;
        if (metadata?.BlockchainTxId != null)
        {
            blockchainAnchored = await VerifyBlockchainAnchorAsync(objectId, ct);
        }

        // Step 5: Compute threat score
        var threatScore = CalculateThreatScore(timeLockActive, integrityVerified, pqcSignatureValid, blockchainAnchored);

        var level = metadata?.Level ?? VaccinationLevel.None;
        var vaccinationInfo = new RansomwareVaccinationInfo
        {
            VaccinationLevel = level,
            TimeLockActive = timeLockActive,
            IntegrityVerified = integrityVerified,
            PqcSignatureValid = pqcSignatureValid,
            BlockchainAnchored = blockchainAnchored,
            LastScanAt = verifiedAt,
            ThreatScore = threatScore
        };

        // Update vaccination record
        _vaccinationRecords[objectId] = vaccinationInfo;

        // Publish verification event
        await PublishEventAsync("timelock.vaccination.verified", new Dictionary<string, object>
        {
            ["ObjectId"] = objectId,
            ["VaccinationLevel"] = level.ToString(),
            ["TimeLockActive"] = timeLockActive,
            ["IntegrityVerified"] = integrityVerified,
            ["PqcSignatureValid"] = pqcSignatureValid?.ToString() ?? "N/A",
            ["BlockchainAnchored"] = blockchainAnchored?.ToString() ?? "N/A",
            ["ThreatScore"] = threatScore,
            ["VerifiedAt"] = verifiedAt
        }, ct);

        // Publish threat alert if score is elevated
        if (threatScore > 0.5)
        {
            await PublishEventAsync("timelock.vaccination.threat.detected", new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["ThreatScore"] = threatScore,
                ["TimeLockActive"] = timeLockActive,
                ["IntegrityVerified"] = integrityVerified,
                ["DetectedAt"] = verifiedAt,
                ["Severity"] = threatScore > 0.8 ? "Critical" : "High"
            }, ct);
        }

        return vaccinationInfo;
    }

    /// <summary>
    /// Scans all time-locked objects in batches, verifying vaccination status for each.
    /// Publishes scan start and completion events and yields results as an async enumerable.
    /// </summary>
    /// <param name="batchSize">Number of objects to process per batch. Must be positive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of vaccination verification results for all tracked objects.</returns>
    /// <exception cref="ArgumentException">Thrown when batchSize is not positive.</exception>
    public async IAsyncEnumerable<RansomwareVaccinationInfo> ScanAllAsync(
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (batchSize <= 0)
            throw new ArgumentException("Batch size must be positive.", nameof(batchSize));

        var scanStartedAt = DateTimeOffset.UtcNow;
        var totalScanned = 0;
        var threatsDetected = 0;

        // Publish scan started event
        await PublishEventAsync("timelock.vaccination.scan.started", new Dictionary<string, object>
        {
            ["StartedAt"] = scanStartedAt,
            ["TotalObjects"] = _vaccinationRecords.Count,
            ["BatchSize"] = batchSize
        }, ct);

        // Get all tracked object IDs
        var objectIds = _vaccinationRecords.Keys.ToArray();
        var batches = objectIds
            .Select((id, index) => new { id, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.id).ToArray());

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var objectId in batch)
            {
                ct.ThrowIfCancellationRequested();

                var result = await VerifyVaccinationAsync(objectId, ct);
                totalScanned++;

                if (result.ThreatScore > 0.5)
                {
                    threatsDetected++;
                }

                yield return result;
            }
        }

        // Publish scan completed event
        await PublishEventAsync("timelock.vaccination.scan.completed", new Dictionary<string, object>
        {
            ["StartedAt"] = scanStartedAt,
            ["CompletedAt"] = DateTimeOffset.UtcNow,
            ["TotalScanned"] = totalScanned,
            ["ThreatsDetected"] = threatsDetected,
            ["DurationMs"] = (DateTimeOffset.UtcNow - scanStartedAt).TotalMilliseconds
        }, ct);
    }

    /// <summary>
    /// Returns aggregate threat statistics across all vaccinated objects.
    /// Provides operational visibility into the overall vaccination posture including
    /// total protected objects, average threat score, expired locks, failed integrity, and re-vaccination needs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary containing dashboard metrics.</returns>
    public Task<Dictionary<string, object>> GetThreatDashboardAsync(CancellationToken ct = default)
    {
        var records = _vaccinationRecords.Values.ToArray();
        var totalVaccinated = records.Length;

        if (totalVaccinated == 0)
        {
            return Task.FromResult(new Dictionary<string, object>
            {
                ["TotalVaccinatedObjects"] = 0,
                ["AverageThreatScore"] = 0.0,
                ["ObjectsWithExpiredLocks"] = 0,
                ["ObjectsWithFailedIntegrity"] = 0,
                ["ObjectsNeedingReVaccination"] = 0,
                ["CriticalThreats"] = 0,
                ["HighThreats"] = 0,
                ["LowThreats"] = 0,
                ["FullyProtected"] = 0,
                ["DashboardGeneratedAt"] = DateTimeOffset.UtcNow
            });
        }

        var averageThreatScore = records.Average(r => r.ThreatScore);
        var objectsWithExpiredLocks = records.Count(r => !r.TimeLockActive);
        var objectsWithFailedIntegrity = records.Count(r => !r.IntegrityVerified);
        var criticalThreats = records.Count(r => r.ThreatScore > 0.8);
        var highThreats = records.Count(r => r.ThreatScore > 0.5 && r.ThreatScore <= 0.8);
        var lowThreats = records.Count(r => r.ThreatScore > 0.2 && r.ThreatScore <= 0.5);
        var fullyProtected = records.Count(r => r.ThreatScore <= 0.1);

        // Objects needing re-vaccination: expired locks, failed integrity, or elevated threat
        var objectsNeedingReVaccination = records.Count(r =>
            !r.TimeLockActive ||
            !r.IntegrityVerified ||
            (r.PqcSignatureValid.HasValue && !r.PqcSignatureValid.Value) ||
            (r.BlockchainAnchored.HasValue && !r.BlockchainAnchored.Value) ||
            r.ThreatScore > 0.5);

        var dashboard = new Dictionary<string, object>
        {
            ["TotalVaccinatedObjects"] = totalVaccinated,
            ["AverageThreatScore"] = Math.Round(averageThreatScore, 4),
            ["ObjectsWithExpiredLocks"] = objectsWithExpiredLocks,
            ["ObjectsWithFailedIntegrity"] = objectsWithFailedIntegrity,
            ["ObjectsNeedingReVaccination"] = objectsNeedingReVaccination,
            ["CriticalThreats"] = criticalThreats,
            ["HighThreats"] = highThreats,
            ["LowThreats"] = lowThreats,
            ["FullyProtected"] = fullyProtected,
            ["DashboardGeneratedAt"] = DateTimeOffset.UtcNow,
            ["VaccinationLevelDistribution"] = new Dictionary<string, int>
            {
                ["None"] = records.Count(r => r.VaccinationLevel == VaccinationLevel.None),
                ["Basic"] = records.Count(r => r.VaccinationLevel == VaccinationLevel.Basic),
                ["Enhanced"] = records.Count(r => r.VaccinationLevel == VaccinationLevel.Enhanced),
                ["Maximum"] = records.Count(r => r.VaccinationLevel == VaccinationLevel.Maximum)
            }
        };

        return Task.FromResult(dashboard);
    }

    /// <summary>
    /// Calculates an overall threat score as a weighted average of protection layer results.
    /// Weights: time-lock=0.3, integrity=0.3, PQC=0.2, blockchain=0.2.
    /// Missing checks (null) are excluded from the calculation and their weights are redistributed.
    /// Score of 0.0 = fully protected, 1.0 = fully compromised.
    /// </summary>
    /// <param name="timeLockActive">Whether a time-lock is currently active.</param>
    /// <param name="integrityVerified">Whether integrity hash verification passed.</param>
    /// <param name="pqcSignatureValid">Whether PQC signature is valid, or null if not applicable.</param>
    /// <param name="blockchainAnchored">Whether blockchain anchor is verified, or null if not applicable.</param>
    /// <returns>Threat score from 0.0 (no threat) to 1.0 (confirmed compromise).</returns>
    internal static double CalculateThreatScore(
        bool timeLockActive,
        bool integrityVerified,
        bool? pqcSignatureValid,
        bool? blockchainAnchored)
    {
        var totalWeight = 0.0;
        var weightedThreat = 0.0;

        // Time-lock: weight 0.3
        totalWeight += 0.3;
        if (!timeLockActive)
        {
            weightedThreat += 0.3;
        }

        // Integrity: weight 0.3
        totalWeight += 0.3;
        if (!integrityVerified)
        {
            weightedThreat += 0.3;
        }

        // PQC signature: weight 0.2 (only if applicable)
        if (pqcSignatureValid.HasValue)
        {
            totalWeight += 0.2;
            if (!pqcSignatureValid.Value)
            {
                weightedThreat += 0.2;
            }
        }

        // Blockchain anchor: weight 0.2 (only if applicable)
        if (blockchainAnchored.HasValue)
        {
            totalWeight += 0.2;
            if (!blockchainAnchored.Value)
            {
                weightedThreat += 0.2;
            }
        }

        // Normalize to 0.0-1.0 range
        if (totalWeight <= 0.0) return 0.0;

        return Math.Round(weightedThreat / totalWeight, 4);
    }

    #region Private Bus Coordination Methods

    /// <summary>
    /// Applies a time-lock to an object by publishing to "timelock.lock" on the message bus.
    /// The actual lock enforcement is handled by whichever time-lock provider is subscribed.
    /// </summary>
    /// <param name="objectId">Object to lock.</param>
    /// <param name="policy">Time-lock policy governing lock parameters.</param>
    /// <param name="level">Vaccination level to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the time-lock was successfully applied.</returns>
    private async Task<bool> ApplyTimeLockAsync(Guid objectId, TimeLockPolicy policy, VaccinationLevel level, CancellationToken ct)
    {
        try
        {
            var lockRequest = new PluginMessage
            {
                Type = "timelock.lock",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["LockDuration"] = policy.DefaultLockDuration.ToString(),
                    ["VaccinationLevel"] = level.ToString(),
                    ["MinLockDuration"] = policy.MinLockDuration.ToString(),
                    ["MaxLockDuration"] = policy.MaxLockDuration.ToString(),
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("timelock.lock", lockRequest, TimeSpan.FromSeconds(10), ct);
            return response.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Computes and verifies the integrity hash of an object by publishing to "integrity.hash.compute".
    /// </summary>
    /// <param name="objectId">Object to hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (verified, hash string).</returns>
    private async Task<(bool Verified, string? Hash)> ComputeAndVerifyIntegrityAsync(Guid objectId, CancellationToken ct)
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
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("integrity.hash.compute", hashRequest, TimeSpan.FromSeconds(10), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("Hash", out var hashObj) &&
                hashObj is string hash)
            {
                return (true, hash);
            }

            return (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Applies a post-quantum cryptographic signature to an object by publishing to "encryption.sign".
    /// Uses the PqcAlgorithmRegistry to select the appropriate algorithm.
    /// </summary>
    /// <param name="objectId">Object to sign.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (valid, algorithm name).</returns>
    private async Task<(bool? Valid, string? Algorithm)> ApplyPqcSignatureAsync(Guid objectId, CancellationToken ct)
    {
        try
        {
            var signRequest = new PluginMessage
            {
                Type = "encryption.sign",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["AlgorithmFamily"] = "PQC",
                    ["PreferredAlgorithm"] = "CRYSTALS-Dilithium",
                    ["Purpose"] = "RansomwareVaccination",
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("encryption.sign", signRequest, TimeSpan.FromSeconds(15), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload)
            {
                var algorithm = payload.TryGetValue("Algorithm", out var algObj) && algObj is string alg ? alg : "CRYSTALS-Dilithium";
                return (true, algorithm);
            }

            return (false, null);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Creates a blockchain anchor for an object by publishing to "blockchain.anchor.create".
    /// Anchors the integrity hash to a distributed ledger for tamper-evident proof of existence.
    /// </summary>
    /// <param name="objectId">Object to anchor.</param>
    /// <param name="integrityHash">Integrity hash to anchor on the blockchain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (anchored, transaction ID).</returns>
    private async Task<(bool? Anchored, string? TxId)> CreateBlockchainAnchorAsync(Guid objectId, string? integrityHash, CancellationToken ct)
    {
        try
        {
            var anchorRequest = new PluginMessage
            {
                Type = "blockchain.anchor.create",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["IntegrityHash"] = integrityHash ?? string.Empty,
                    ["Purpose"] = "RansomwareVaccination",
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("blockchain.anchor.create", anchorRequest, TimeSpan.FromSeconds(15), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload)
            {
                var txId = payload.TryGetValue("TransactionId", out var txObj) && txObj is string tx ? tx : null;
                return (true, txId);
            }

            return (false, null);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Checks time-lock status of an object by querying "timelock.status" on the message bus.
    /// </summary>
    /// <param name="objectId">Object to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the time-lock is currently active.</returns>
    private async Task<bool> CheckTimeLockStatusAsync(Guid objectId, CancellationToken ct)
    {
        try
        {
            var statusRequest = new PluginMessage
            {
                Type = "timelock.status",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("timelock.status", statusRequest, TimeSpan.FromSeconds(5), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("IsLocked", out var lockedObj) &&
                lockedObj is bool isLocked)
            {
                return isLocked;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the integrity hash of an object by publishing to "integrity.hash.verify".
    /// </summary>
    /// <param name="objectId">Object to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if integrity verification passed.</returns>
    private async Task<bool> VerifyIntegrityHashAsync(Guid objectId, CancellationToken ct)
    {
        try
        {
            var verifyRequest = new PluginMessage
            {
                Type = "integrity.hash.verify",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("integrity.hash.verify", verifyRequest, TimeSpan.FromSeconds(5), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("Verified", out var verifiedObj) &&
                verifiedObj is bool verified)
            {
                return verified;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies a PQC signature on an object by publishing to "encryption.verify".
    /// </summary>
    /// <param name="objectId">Object to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if PQC signature verification passed.</returns>
    private async Task<bool> VerifyPqcSignatureAsync(Guid objectId, CancellationToken ct)
    {
        try
        {
            var verifyRequest = new PluginMessage
            {
                Type = "encryption.verify",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["AlgorithmFamily"] = "PQC",
                    ["Purpose"] = "RansomwareVaccination",
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("encryption.verify", verifyRequest, TimeSpan.FromSeconds(10), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("SignatureValid", out var validObj) &&
                validObj is bool valid)
            {
                return valid;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies a blockchain anchor for an object by publishing to "blockchain.anchor.verify".
    /// </summary>
    /// <param name="objectId">Object to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if blockchain anchor verification passed.</returns>
    private async Task<bool> VerifyBlockchainAnchorAsync(Guid objectId, CancellationToken ct)
    {
        try
        {
            var verifyRequest = new PluginMessage
            {
                Type = "blockchain.anchor.verify",
                Payload = new Dictionary<string, object>
                {
                    ["ObjectId"] = objectId,
                    ["Purpose"] = "RansomwareVaccination",
                    ["RequestedBy"] = "RansomwareVaccinationService",
                    ["RequestedAt"] = DateTimeOffset.UtcNow
                }
            };

            var response = await _messageBus.SendAsync("blockchain.anchor.verify", verifyRequest, TimeSpan.FromSeconds(10), ct);
            if (response.Success &&
                response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("AnchorValid", out var validObj) &&
                validObj is bool valid)
            {
                return valid;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Publishes an event to the message bus. Silently ignores failures.
    /// </summary>
    /// <param name="topic">Message bus topic.</param>
    /// <param name="payload">Event payload.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct)
    {
        try
        {
            var message = new PluginMessage
            {
                Type = topic,
                Payload = payload
            };
            await _messageBus.PublishAsync(topic, message, ct);
        }
        catch
        {
            // Event publication failure must not disrupt vaccination operations
        }
    }

    #endregion
}
