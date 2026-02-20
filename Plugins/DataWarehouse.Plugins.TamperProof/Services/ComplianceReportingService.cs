// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Interface for compliance reporting and attestation services.
/// Supports regulatory compliance standards including SEC 17a-4, HIPAA, GDPR, SOX, PCI-DSS, and FINRA.
/// </summary>
public interface IComplianceReportingService
{
    /// <summary>
    /// Generates a comprehensive compliance report for the specified standards and time range.
    /// </summary>
    /// <param name="request">Report generation request with scope and standards.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compliance report with violations and overall status.</returns>
    Task<ComplianceReport> GenerateReportAsync(ComplianceReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates a cryptographic attestation for a set of blocks.
    /// The attestation proves the integrity state at a point in time.
    /// </summary>
    /// <param name="request">Attestation request with block IDs and attester identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Attestation result with token that can be independently verified.</returns>
    Task<AttestationResult> CreateAttestationAsync(AttestationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verifies a previously created attestation token.
    /// </summary>
    /// <param name="attestationToken">The attestation token to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the attestation is valid and matches current state.</returns>
    Task<bool> VerifyAttestationAsync(string attestationToken, CancellationToken ct = default);

    /// <summary>
    /// Gets all compliance violations detected since the specified date.
    /// </summary>
    /// <param name="since">Start date for violation query (null for all violations).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of compliance violations.</returns>
    Task<IReadOnlyList<ComplianceViolation>> GetViolationsAsync(DateTime? since = null, CancellationToken ct = default);

    /// <summary>
    /// Gets retention policy status for all tracked blocks.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Retention policy compliance summary.</returns>
    Task<RetentionPolicySummary> GetRetentionPolicyStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets legal hold status for all blocks under legal hold.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Legal hold summary.</returns>
    Task<LegalHoldSummary> GetLegalHoldStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Service for compliance reporting and cryptographic attestation.
/// Provides comprehensive compliance reporting for regulatory standards and
/// creates verifiable attestations of data integrity state.
/// </summary>
public class ComplianceReportingService : IComplianceReportingService
{
    private readonly TamperIncidentService _incidentService;
    private readonly BlockchainVerificationService _blockchainService;
    private readonly TamperProofConfiguration _config;
    private readonly ILogger<ComplianceReportingService> _logger;

    // In-memory storage for violations and attestations (production would use persistent storage)
    private readonly BoundedDictionary<Guid, ComplianceViolation> _violations = new BoundedDictionary<Guid, ComplianceViolation>(1000);
    private readonly BoundedDictionary<string, AttestationRecord> _attestations = new BoundedDictionary<string, AttestationRecord>(1000);
    private readonly BoundedDictionary<Guid, BlockTrackingInfo> _trackedBlocks = new BoundedDictionary<Guid, BlockTrackingInfo>(1000);
    private readonly BoundedDictionary<string, LegalHoldInfo> _legalHolds = new BoundedDictionary<string, LegalHoldInfo>(1000);

    /// <summary>
    /// Creates a new compliance reporting service instance.
    /// </summary>
    /// <param name="incidentService">Tamper incident service for violation data.</param>
    /// <param name="blockchainService">Blockchain verification service.</param>
    /// <param name="config">TamperProof configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public ComplianceReportingService(
        TamperIncidentService incidentService,
        BlockchainVerificationService blockchainService,
        TamperProofConfiguration config,
        ILogger<ComplianceReportingService> logger)
    {
        _incidentService = incidentService ?? throw new ArgumentNullException(nameof(incidentService));
        _blockchainService = blockchainService ?? throw new ArgumentNullException(nameof(blockchainService));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ComplianceReport> GenerateReportAsync(
        ComplianceReportRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Generating compliance report for standards {Standards} from {FromDate} to {ToDate}",
            string.Join(", ", request.Standards),
            request.FromDate,
            request.ToDate);

        var reportId = Guid.NewGuid();
        var fromDate = request.FromDate ?? DateTime.UtcNow.AddYears(-1);
        var toDate = request.ToDate ?? DateTime.UtcNow;

        try
        {
            // Gather all violations
            var violations = new List<ComplianceViolation>();

            // Check tamper incidents as violations
            var incidents = await _incidentService.QueryIncidentsAsync(
                new DateTimeOffset(fromDate),
                new DateTimeOffset(toDate),
                ct);

            foreach (var incident in incidents)
            {
                foreach (var standard in request.Standards)
                {
                    var violation = CreateViolationFromIncident(incident, standard);
                    violations.Add(violation);
                    _violations.TryAdd(violation.BlockId, violation);
                }
            }

            // Check retention policy violations
            var retentionViolations = await CheckRetentionPolicyViolationsAsync(
                fromDate, toDate, request.Standards, ct);
            violations.AddRange(retentionViolations);

            // Check legal hold violations
            var legalHoldViolations = await CheckLegalHoldViolationsAsync(
                fromDate, toDate, request.Standards, ct);
            violations.AddRange(legalHoldViolations);

            // Check audit trail completeness
            var auditViolations = await CheckAuditTrailViolationsAsync(
                fromDate, toDate, request.Standards, ct);
            violations.AddRange(auditViolations);

            // Calculate statistics
            var stats = await _incidentService.GetStatisticsAsync(ct);
            var blocksInScope = _trackedBlocks.Values
                .Where(b => b.CreatedAt >= fromDate && b.CreatedAt <= toDate)
                .Count();
            var blocksVerified = _trackedBlocks.Values
                .Where(b => b.LastVerifiedAt.HasValue &&
                            b.LastVerifiedAt.Value >= fromDate &&
                            b.LastVerifiedAt.Value <= toDate)
                .Count();

            // Determine overall status
            var overallStatus = DetermineOverallStatus(violations, request.Standards);

            // Compute report hash for integrity
            var reportHash = ComputeReportHash(
                reportId, fromDate, toDate, violations, overallStatus);

            var report = new ComplianceReport(
                ReportId: reportId,
                GeneratedAt: DateTime.UtcNow,
                FromDate: fromDate,
                ToDate: toDate,
                Standards: request.Standards,
                TotalBlocksInScope: blocksInScope > 0 ? blocksInScope : stats.AffectedObjectCount,
                BlocksVerified: blocksVerified > 0 ? blocksVerified : stats.TotalIncidents - stats.FailedRecoveryCount,
                BlocksWithViolations: violations.Select(v => v.BlockId).Distinct().Count(),
                Violations: violations,
                OverallStatus: overallStatus,
                ReportHash: reportHash);

            _logger.LogInformation(
                "Compliance report {ReportId} generated: Status={Status}, Violations={Count}",
                reportId,
                overallStatus,
                violations.Count);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate compliance report");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<AttestationResult> CreateAttestationAsync(
        AttestationRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Creating attestation for {Count} blocks by {Attester} - Purpose: {Purpose}",
            request.BlockIds.Length,
            request.AttesterIdentity,
            request.Purpose);

        try
        {
            // Step 1: Gather all integrity proofs for the scope
            var proofs = new List<IntegrityProof>();
            foreach (var blockId in request.BlockIds)
            {
                var proof = await GatherIntegrityProofAsync(blockId, ct);
                if (proof != null)
                {
                    proofs.Add(proof);
                }
            }

            if (proofs.Count == 0)
            {
                return new AttestationResult(
                    Success: false,
                    AttestationToken: null,
                    ValidUntil: null,
                    MerkleRoot: string.Empty,
                    Error: "No valid blocks found for attestation");
            }

            // Step 2: Create Merkle root of all proofs
            var merkleRoot = ComputeMerkleRoot(proofs);

            // Step 3: Create attestation payload
            var attestationPayload = new AttestationPayload
            {
                AttestationId = Guid.NewGuid(),
                AttesterIdentity = request.AttesterIdentity,
                Purpose = request.Purpose,
                BlockIds = request.BlockIds,
                MerkleRoot = merkleRoot,
                Timestamp = DateTimeOffset.UtcNow,
                ValidUntil = DateTimeOffset.UtcNow.AddYears(1),
                ProofCount = proofs.Count
            };

            // Step 4: Sign with attestation key
            var signature = SignAttestation(attestationPayload);

            // Step 5: Create attestation token
            var attestationToken = CreateAttestationToken(attestationPayload, signature);

            // Store attestation record for verification
            var record = new AttestationRecord
            {
                AttestationId = attestationPayload.AttestationId,
                Payload = attestationPayload,
                Signature = signature,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _attestations.TryAdd(attestationToken, record);

            _logger.LogInformation(
                "Attestation {AttestationId} created with Merkle root {MerkleRoot}",
                attestationPayload.AttestationId,
                merkleRoot);

            return new AttestationResult(
                Success: true,
                AttestationToken: attestationToken,
                ValidUntil: attestationPayload.ValidUntil.DateTime,
                MerkleRoot: merkleRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create attestation");
            return new AttestationResult(
                Success: false,
                AttestationToken: null,
                ValidUntil: null,
                MerkleRoot: string.Empty,
                Error: $"Attestation creation failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> VerifyAttestationAsync(string attestationToken, CancellationToken ct = default)
    {
        _logger.LogDebug("Verifying attestation token");

        try
        {
            // Step 1: Parse token
            var (payload, signature) = ParseAttestationToken(attestationToken);
            if (payload == null)
            {
                _logger.LogWarning("Failed to parse attestation token");
                return false;
            }

            // Step 2: Verify signature
            if (!VerifySignature(payload, signature))
            {
                _logger.LogWarning("Attestation signature verification failed");
                return false;
            }

            // Step 3: Verify timestamp is valid
            if (payload.ValidUntil < DateTimeOffset.UtcNow)
            {
                _logger.LogWarning("Attestation has expired");
                return false;
            }

            // Step 4: Verify Merkle root matches current state
            var currentProofs = new List<IntegrityProof>();
            foreach (var blockId in payload.BlockIds)
            {
                var proof = await GatherIntegrityProofAsync(blockId, ct);
                if (proof != null)
                {
                    currentProofs.Add(proof);
                }
            }

            var currentMerkleRoot = ComputeMerkleRoot(currentProofs);
            if (!string.Equals(currentMerkleRoot, payload.MerkleRoot, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Attestation Merkle root mismatch. Expected: {Expected}, Actual: {Actual}",
                    payload.MerkleRoot,
                    currentMerkleRoot);
                return false;
            }

            _logger.LogInformation("Attestation {AttestationId} verified successfully", payload.AttestationId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attestation verification failed");
            return false;
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ComplianceViolation>> GetViolationsAsync(
        DateTime? since = null,
        CancellationToken ct = default)
    {
        var violations = _violations.Values
            .Where(v => since == null || v.DetectedAt >= since.Value)
            .OrderByDescending(v => v.DetectedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ComplianceViolation>>(violations);
    }

    /// <inheritdoc/>
    public Task<RetentionPolicySummary> GetRetentionPolicyStatusAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var blocks = _trackedBlocks.Values.ToList();

        var expired = blocks.Where(b => b.RetentionExpiresAt.HasValue && b.RetentionExpiresAt.Value <= now).ToList();
        var expiringSoon = blocks.Where(b =>
            b.RetentionExpiresAt.HasValue &&
            b.RetentionExpiresAt.Value > now &&
            b.RetentionExpiresAt.Value <= now.AddDays(30)).ToList();
        var compliant = blocks.Where(b =>
            !b.RetentionExpiresAt.HasValue ||
            b.RetentionExpiresAt.Value > now.AddDays(30)).ToList();

        var summary = new RetentionPolicySummary(
            TotalBlocks: blocks.Count,
            CompliantBlocks: compliant.Count,
            ExpiredBlocks: expired.Count,
            ExpiringSoonBlocks: expiringSoon.Count,
            DefaultRetentionPeriod: _config.DefaultRetentionPeriod,
            AsOfDate: now.DateTime);

        return Task.FromResult(summary);
    }

    /// <inheritdoc/>
    public Task<LegalHoldSummary> GetLegalHoldStatusAsync(CancellationToken ct = default)
    {
        var holds = _legalHolds.Values.ToList();
        var activeHolds = holds.Where(h => h.IsActive).ToList();

        var blocksUnderHold = _trackedBlocks.Values
            .Where(b => b.LegalHoldIds.Count > 0)
            .ToList();

        var summary = new LegalHoldSummary(
            TotalLegalHolds: holds.Count,
            ActiveLegalHolds: activeHolds.Count,
            BlocksUnderHold: blocksUnderHold.Count,
            LegalHoldDetails: activeHolds.Select(h => new LegalHoldDetail(
                HoldId: h.HoldId,
                HoldName: h.HoldName,
                CreatedAt: h.CreatedAt.DateTime,
                CreatedBy: h.CreatedBy,
                AffectedBlockCount: _trackedBlocks.Values.Count(b => b.LegalHoldIds.Contains(h.HoldId))
            )).ToList());

        return Task.FromResult(summary);
    }

    /// <summary>
    /// Tracks a block for compliance monitoring.
    /// </summary>
    public void TrackBlock(Guid blockId, DateTimeOffset createdAt, DateTimeOffset? retentionExpiresAt)
    {
        _trackedBlocks.TryAdd(blockId, new BlockTrackingInfo
        {
            BlockId = blockId,
            CreatedAt = createdAt.DateTime,
            RetentionExpiresAt = retentionExpiresAt,
            LegalHoldIds = new List<string>()
        });
    }

    /// <summary>
    /// Records a block verification.
    /// </summary>
    public void RecordBlockVerification(Guid blockId, bool integrityValid)
    {
        if (_trackedBlocks.TryGetValue(blockId, out var info))
        {
            info.LastVerifiedAt = DateTime.UtcNow;
            info.LastVerificationResult = integrityValid;
        }
    }

    /// <summary>
    /// Applies a legal hold to a block.
    /// </summary>
    public void ApplyLegalHold(Guid blockId, string holdId)
    {
        if (_trackedBlocks.TryGetValue(blockId, out var info))
        {
            if (!info.LegalHoldIds.Contains(holdId))
            {
                info.LegalHoldIds.Add(holdId);
            }
        }
    }

    /// <summary>
    /// Creates a legal hold.
    /// </summary>
    public void CreateLegalHold(string holdId, string holdName, string createdBy)
    {
        _legalHolds.TryAdd(holdId, new LegalHoldInfo
        {
            HoldId = holdId,
            HoldName = holdName,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            IsActive = true
        });
    }

    #region Private Helper Methods

    private ComplianceViolation CreateViolationFromIncident(
        TamperIncidentReport incident,
        ComplianceStandard standard)
    {
        var violationType = incident.RecoverySucceeded
            ? "IntegrityViolationRecovered"
            : "IntegrityViolationUnrecovered";

        var severity = incident.RecoverySucceeded
            ? ViolationSeverity.Medium
            : ViolationSeverity.Critical;

        return new ComplianceViolation(
            BlockId: incident.ObjectId,
            ViolationType: violationType,
            Description: $"Tampering detected: Expected hash {incident.ExpectedHash}, " +
                        $"Actual hash {incident.ActualHash}. Recovery: {(incident.RecoverySucceeded ? "Succeeded" : "Failed")}",
            AffectedStandard: standard,
            DetectedAt: incident.DetectedAt.DateTime,
            Severity: severity);
    }

    private Task<List<ComplianceViolation>> CheckRetentionPolicyViolationsAsync(
        DateTime fromDate,
        DateTime toDate,
        ComplianceStandard[] standards,
        CancellationToken ct)
    {
        var violations = new List<ComplianceViolation>();
        var now = DateTime.UtcNow;

        foreach (var block in _trackedBlocks.Values)
        {
            // Check for premature deletion (retention period not met)
            if (block.RetentionExpiresAt.HasValue &&
                block.RetentionExpiresAt.Value > DateTimeOffset.UtcNow &&
                block.IsDeleted)
            {
                foreach (var standard in standards)
                {
                    violations.Add(new ComplianceViolation(
                        BlockId: block.BlockId,
                        ViolationType: "PrematureDeletion",
                        Description: $"Block deleted before retention period expired. " +
                                    $"Retention expires: {block.RetentionExpiresAt}",
                        AffectedStandard: standard,
                        DetectedAt: now,
                        Severity: ViolationSeverity.Critical));
                }
            }

            // Check for missing retention policy
            if (!block.RetentionExpiresAt.HasValue)
            {
                foreach (var standard in standards.Where(s =>
                    s == ComplianceStandard.SEC17a4 ||
                    s == ComplianceStandard.FINRA))
                {
                    violations.Add(new ComplianceViolation(
                        BlockId: block.BlockId,
                        ViolationType: "MissingRetentionPolicy",
                        Description: "Block lacks retention policy required by financial regulations",
                        AffectedStandard: standard,
                        DetectedAt: now,
                        Severity: ViolationSeverity.High));
                }
            }
        }

        return Task.FromResult(violations);
    }

    private Task<List<ComplianceViolation>> CheckLegalHoldViolationsAsync(
        DateTime fromDate,
        DateTime toDate,
        ComplianceStandard[] standards,
        CancellationToken ct)
    {
        var violations = new List<ComplianceViolation>();
        var now = DateTime.UtcNow;

        foreach (var block in _trackedBlocks.Values)
        {
            // Check for deleted blocks under legal hold
            if (block.LegalHoldIds.Count > 0 && block.IsDeleted)
            {
                foreach (var standard in standards)
                {
                    violations.Add(new ComplianceViolation(
                        BlockId: block.BlockId,
                        ViolationType: "LegalHoldViolation",
                        Description: $"Block under legal hold was deleted. " +
                                    $"Hold IDs: {string.Join(", ", block.LegalHoldIds)}",
                        AffectedStandard: standard,
                        DetectedAt: now,
                        Severity: ViolationSeverity.Critical));
                }
            }

            // Check for modified blocks under legal hold
            if (block.LegalHoldIds.Count > 0 && block.ModifiedAfterHold)
            {
                foreach (var standard in standards)
                {
                    violations.Add(new ComplianceViolation(
                        BlockId: block.BlockId,
                        ViolationType: "LegalHoldModification",
                        Description: "Block under legal hold was modified",
                        AffectedStandard: standard,
                        DetectedAt: now,
                        Severity: ViolationSeverity.Critical));
                }
            }
        }

        return Task.FromResult(violations);
    }

    private async Task<List<ComplianceViolation>> CheckAuditTrailViolationsAsync(
        DateTime fromDate,
        DateTime toDate,
        ComplianceStandard[] standards,
        CancellationToken ct)
    {
        var violations = new List<ComplianceViolation>();
        var now = DateTime.UtcNow;

        // Check blockchain integrity
        var blockchainValid = await _blockchainService.ValidateChainIntegrityAsync(ct);
        if (!blockchainValid)
        {
            foreach (var standard in standards.Where(s =>
                s == ComplianceStandard.SEC17a4 ||
                s == ComplianceStandard.SOX ||
                s == ComplianceStandard.FINRA))
            {
                violations.Add(new ComplianceViolation(
                    BlockId: Guid.Empty,
                    ViolationType: "AuditTrailIntegrityFailure",
                    Description: "Blockchain audit trail integrity validation failed",
                    AffectedStandard: standard,
                    DetectedAt: now,
                    Severity: ViolationSeverity.Critical));
            }
        }

        // Check for blocks without blockchain anchors (required for audit standards)
        foreach (var block in _trackedBlocks.Values.Where(b => !b.HasBlockchainAnchor))
        {
            foreach (var standard in standards.Where(s =>
                s == ComplianceStandard.SEC17a4 ||
                s == ComplianceStandard.SOX))
            {
                violations.Add(new ComplianceViolation(
                    BlockId: block.BlockId,
                    ViolationType: "MissingBlockchainAnchor",
                    Description: "Block lacks blockchain anchor required for audit trail",
                    AffectedStandard: standard,
                    DetectedAt: now,
                    Severity: ViolationSeverity.High));
            }
        }

        return violations;
    }

    private ComplianceStatus DetermineOverallStatus(
        List<ComplianceViolation> violations,
        ComplianceStandard[] standards)
    {
        if (violations.Count == 0)
        {
            return ComplianceStatus.Compliant;
        }

        var criticalViolations = violations.Where(v => v.Severity == ViolationSeverity.Critical).ToList();
        var highViolations = violations.Where(v => v.Severity == ViolationSeverity.High).ToList();

        if (criticalViolations.Count > 0)
        {
            return ComplianceStatus.NonCompliant;
        }

        if (highViolations.Count > 0)
        {
            return ComplianceStatus.PartiallyCompliant;
        }

        // Only medium/low violations
        return ComplianceStatus.PartiallyCompliant;
    }

    private string ComputeReportHash(
        Guid reportId,
        DateTime fromDate,
        DateTime toDate,
        List<ComplianceViolation> violations,
        ComplianceStatus status)
    {
        var sb = new StringBuilder();
        sb.Append(reportId.ToString());
        sb.Append('|');
        sb.Append(fromDate.ToString("O"));
        sb.Append('|');
        sb.Append(toDate.ToString("O"));
        sb.Append('|');
        sb.Append(status.ToString());
        sb.Append('|');
        sb.Append(violations.Count);

        foreach (var violation in violations.OrderBy(v => v.BlockId))
        {
            sb.Append('|');
            sb.Append(violation.BlockId);
            sb.Append(':');
            sb.Append(violation.ViolationType);
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private async Task<IntegrityProof?> GatherIntegrityProofAsync(Guid blockId, CancellationToken ct)
    {
        if (!_trackedBlocks.TryGetValue(blockId, out var blockInfo))
        {
            return null;
        }

        // Create integrity proof from block tracking info
        return new IntegrityProof
        {
            BlockId = blockId,
            ContentHash = blockInfo.ContentHash ?? ComputeBlockHash(blockId),
            Timestamp = DateTimeOffset.UtcNow,
            HasBlockchainAnchor = blockInfo.HasBlockchainAnchor,
            RetentionValid = !blockInfo.RetentionExpiresAt.HasValue ||
                            blockInfo.RetentionExpiresAt.Value > DateTimeOffset.UtcNow
        };
    }

    private string ComputeMerkleRoot(List<IntegrityProof> proofs)
    {
        if (proofs.Count == 0)
        {
            return string.Empty;
        }

        // Compute leaf hashes
        var leaves = proofs
            .OrderBy(p => p.BlockId)
            .Select(p => ComputeProofHash(p))
            .ToList();

        // Build Merkle tree
        while (leaves.Count > 1)
        {
            var newLevel = new List<byte[]>();
            for (int i = 0; i < leaves.Count; i += 2)
            {
                if (i + 1 < leaves.Count)
                {
                    var combined = new byte[leaves[i].Length + leaves[i + 1].Length];
                    Buffer.BlockCopy(leaves[i], 0, combined, 0, leaves[i].Length);
                    Buffer.BlockCopy(leaves[i + 1], 0, combined, leaves[i].Length, leaves[i + 1].Length);
                    // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
                    newLevel.Add(SHA256.HashData(combined));
                }
                else
                {
                    // Odd number of leaves - duplicate the last one
                    var combined = new byte[leaves[i].Length * 2];
                    Buffer.BlockCopy(leaves[i], 0, combined, 0, leaves[i].Length);
                    Buffer.BlockCopy(leaves[i], 0, combined, leaves[i].Length, leaves[i].Length);
                    // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
                    newLevel.Add(SHA256.HashData(combined));
                }
            }
            leaves = newLevel;
        }

        return Convert.ToHexString(leaves[0]);
    }

    private byte[] ComputeProofHash(IntegrityProof proof)
    {
        var data = $"{proof.BlockId}|{proof.ContentHash}|{proof.Timestamp:O}|{proof.HasBlockchainAnchor}|{proof.RetentionValid}";
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        return SHA256.HashData(Encoding.UTF8.GetBytes(data));
    }

    private string ComputeBlockHash(Guid blockId)
    {
        // Placeholder - in production, would compute actual content hash
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        return Convert.ToHexString(SHA256.HashData(blockId.ToByteArray()));
    }

    private string SignAttestation(AttestationPayload payload)
    {
        // In production, would use HSM or secure key management
        // For now, use HMAC with a derived key
        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        // Derive signing key from config (in production, use proper key management)
        var keyMaterial = Encoding.UTF8.GetBytes($"attestation-key-{_config.WormMode}");
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        using var hmac = new HMACSHA256(SHA256.HashData(keyMaterial));

        var signature = hmac.ComputeHash(payloadBytes);
        return Convert.ToBase64String(signature);
    }

    private string CreateAttestationToken(AttestationPayload payload, string signature)
    {
        var tokenData = new
        {
            payload,
            signature
        };

        var json = JsonSerializer.Serialize(tokenData);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes);
    }

    private (AttestationPayload? Payload, string Signature) ParseAttestationToken(string token)
    {
        try
        {
            var bytes = Convert.FromBase64String(token);
            var json = Encoding.UTF8.GetString(bytes);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var payloadJson = root.GetProperty("payload").GetRawText();
            var payload = JsonSerializer.Deserialize<AttestationPayload>(payloadJson);
            var signature = root.GetProperty("signature").GetString() ?? string.Empty;

            return (payload, signature);
        }
        catch
        {
            return (null, string.Empty);
        }
    }

    private bool VerifySignature(AttestationPayload payload, string signature)
    {
        var expectedSignature = SignAttestation(payload);
        return string.Equals(signature, expectedSignature, StringComparison.Ordinal);
    }

    #endregion

    #region Internal Types

    private class IntegrityProof
    {
        public Guid BlockId { get; init; }
        public required string ContentHash { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public bool HasBlockchainAnchor { get; init; }
        public bool RetentionValid { get; init; }
    }

    private class AttestationPayload
    {
        public Guid AttestationId { get; init; }
        public required string AttesterIdentity { get; init; }
        public required string Purpose { get; init; }
        public required Guid[] BlockIds { get; init; }
        public required string MerkleRoot { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public DateTimeOffset ValidUntil { get; init; }
        public int ProofCount { get; init; }
    }

    private class AttestationRecord
    {
        public Guid AttestationId { get; init; }
        public required AttestationPayload Payload { get; init; }
        public required string Signature { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private class BlockTrackingInfo
    {
        public Guid BlockId { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? LastVerifiedAt { get; set; }
        public bool? LastVerificationResult { get; set; }
        public DateTimeOffset? RetentionExpiresAt { get; init; }
        public required List<string> LegalHoldIds { get; init; }
        public bool IsDeleted { get; set; }
        public bool ModifiedAfterHold { get; set; }
        public bool HasBlockchainAnchor { get; set; }
        public string? ContentHash { get; set; }
    }

    private class LegalHoldInfo
    {
        public required string HoldId { get; init; }
        public required string HoldName { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public required string CreatedBy { get; init; }
        public bool IsActive { get; set; }
    }

    #endregion
}

#region Public Records and Enums

/// <summary>
/// Request for generating a compliance report.
/// </summary>
/// <param name="FromDate">Start date for the report period (null for 1 year ago).</param>
/// <param name="ToDate">End date for the report period (null for now).</param>
/// <param name="Standards">Compliance standards to check against.</param>
/// <param name="IncludeDetails">Whether to include detailed violation information.</param>
public record ComplianceReportRequest(
    DateTime? FromDate,
    DateTime? ToDate,
    ComplianceStandard[] Standards,
    bool IncludeDetails);

/// <summary>
/// Regulatory compliance standards supported by the reporting service.
/// </summary>
public enum ComplianceStandard
{
    /// <summary>SEC Rule 17a-4 for financial records retention.</summary>
    SEC17a4,

    /// <summary>HIPAA for healthcare data protection.</summary>
    HIPAA,

    /// <summary>GDPR for EU data protection.</summary>
    GDPR,

    /// <summary>Sarbanes-Oxley Act for financial reporting.</summary>
    SOX,

    /// <summary>PCI-DSS for payment card data security.</summary>
    PCI_DSS,

    /// <summary>FINRA regulations for financial industry.</summary>
    FINRA,

    /// <summary>Custom compliance standard.</summary>
    Custom
}

/// <summary>
/// Compliance report with all findings and overall status.
/// </summary>
/// <param name="ReportId">Unique identifier for this report.</param>
/// <param name="GeneratedAt">When the report was generated.</param>
/// <param name="FromDate">Start of the reporting period.</param>
/// <param name="ToDate">End of the reporting period.</param>
/// <param name="Standards">Compliance standards that were checked.</param>
/// <param name="TotalBlocksInScope">Total number of blocks in the reporting scope.</param>
/// <param name="BlocksVerified">Number of blocks that were verified.</param>
/// <param name="BlocksWithViolations">Number of blocks with compliance violations.</param>
/// <param name="Violations">List of all compliance violations found.</param>
/// <param name="OverallStatus">Overall compliance status.</param>
/// <param name="ReportHash">Cryptographic hash of the report for integrity verification.</param>
public record ComplianceReport(
    Guid ReportId,
    DateTime GeneratedAt,
    DateTime FromDate,
    DateTime ToDate,
    ComplianceStandard[] Standards,
    long TotalBlocksInScope,
    long BlocksVerified,
    long BlocksWithViolations,
    IReadOnlyList<ComplianceViolation> Violations,
    ComplianceStatus OverallStatus,
    string ReportHash);

/// <summary>
/// Overall compliance status.
/// </summary>
public enum ComplianceStatus
{
    /// <summary>Fully compliant with all standards.</summary>
    Compliant,

    /// <summary>Not compliant - critical violations found.</summary>
    NonCompliant,

    /// <summary>Partially compliant - non-critical violations found.</summary>
    PartiallyCompliant,

    /// <summary>Compliance status cannot be determined.</summary>
    Unknown
}

/// <summary>
/// A single compliance violation.
/// </summary>
/// <param name="BlockId">Block ID where the violation was detected.</param>
/// <param name="ViolationType">Type of violation.</param>
/// <param name="Description">Human-readable description of the violation.</param>
/// <param name="AffectedStandard">Compliance standard affected by this violation.</param>
/// <param name="DetectedAt">When the violation was detected.</param>
/// <param name="Severity">Severity of the violation.</param>
public record ComplianceViolation(
    Guid BlockId,
    string ViolationType,
    string Description,
    ComplianceStandard AffectedStandard,
    DateTime DetectedAt,
    ViolationSeverity Severity);

/// <summary>
/// Severity level of a compliance violation.
/// </summary>
public enum ViolationSeverity
{
    /// <summary>Low severity - informational.</summary>
    Low,

    /// <summary>Medium severity - should be addressed.</summary>
    Medium,

    /// <summary>High severity - must be addressed soon.</summary>
    High,

    /// <summary>Critical severity - immediate action required.</summary>
    Critical
}

/// <summary>
/// Request for creating an attestation.
/// </summary>
/// <param name="BlockIds">Block IDs to include in the attestation.</param>
/// <param name="AttesterIdentity">Identity of the person/system creating the attestation.</param>
/// <param name="Purpose">Purpose of the attestation.</param>
public record AttestationRequest(
    Guid[] BlockIds,
    string AttesterIdentity,
    string Purpose);

/// <summary>
/// Result of an attestation creation.
/// </summary>
/// <param name="Success">Whether the attestation was created successfully.</param>
/// <param name="AttestationToken">The attestation token (null if failed).</param>
/// <param name="ValidUntil">When the attestation expires (null if failed).</param>
/// <param name="MerkleRoot">Merkle root of all proofs included in the attestation.</param>
/// <param name="Error">Error message if creation failed.</param>
public record AttestationResult(
    bool Success,
    string? AttestationToken,
    DateTime? ValidUntil,
    string MerkleRoot,
    string? Error = null);

/// <summary>
/// Summary of retention policy compliance.
/// </summary>
/// <param name="TotalBlocks">Total number of tracked blocks.</param>
/// <param name="CompliantBlocks">Number of blocks compliant with retention policy.</param>
/// <param name="ExpiredBlocks">Number of blocks past retention period.</param>
/// <param name="ExpiringSoonBlocks">Number of blocks expiring within 30 days.</param>
/// <param name="DefaultRetentionPeriod">Default retention period configured.</param>
/// <param name="AsOfDate">Date of the summary.</param>
public record RetentionPolicySummary(
    int TotalBlocks,
    int CompliantBlocks,
    int ExpiredBlocks,
    int ExpiringSoonBlocks,
    TimeSpan DefaultRetentionPeriod,
    DateTime AsOfDate);

/// <summary>
/// Summary of legal holds.
/// </summary>
/// <param name="TotalLegalHolds">Total number of legal holds (active and inactive).</param>
/// <param name="ActiveLegalHolds">Number of currently active legal holds.</param>
/// <param name="BlocksUnderHold">Number of blocks currently under legal hold.</param>
/// <param name="LegalHoldDetails">Details of each active legal hold.</param>
public record LegalHoldSummary(
    int TotalLegalHolds,
    int ActiveLegalHolds,
    int BlocksUnderHold,
    IReadOnlyList<LegalHoldDetail> LegalHoldDetails);

/// <summary>
/// Details of a single legal hold.
/// </summary>
/// <param name="HoldId">Unique identifier for the legal hold.</param>
/// <param name="HoldName">Human-readable name of the hold.</param>
/// <param name="CreatedAt">When the hold was created.</param>
/// <param name="CreatedBy">Who created the hold.</param>
/// <param name="AffectedBlockCount">Number of blocks affected by this hold.</param>
public record LegalHoldDetail(
    string HoldId,
    string HoldName,
    DateTime CreatedAt,
    string CreatedBy,
    int AffectedBlockCount);

#endregion
