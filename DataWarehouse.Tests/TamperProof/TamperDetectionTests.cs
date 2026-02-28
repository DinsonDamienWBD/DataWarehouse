// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Integration tests for TamperProof tamper detection SDK contracts (T6.7).
/// Validates TamperIncidentReport construction, TamperSeverity via AttributionConfidence,
/// attribution analysis, incident creation with suspects, and access log correlation.
/// </summary>
public class TamperDetectionTests
{
    #region TamperIncidentReport Construction

    [Fact]
    public void TamperIncidentReport_Create_ShouldPopulateRequiredFields()
    {
        var objectId = Guid.NewGuid();
        var evidence = TamperEvidence.Create(
            HashAlgorithmType.SHA256, 4096, 4096, "manifest-checksum");

        var report = TamperIncidentReport.Create(
            objectId, "expected-hash", "actual-hash",
            "Primary-SSD", TamperRecoveryBehavior.AutoRecoverWithReport,
            true, evidence);

        report.IncidentId.Should().NotBe(Guid.Empty);
        report.ObjectId.Should().Be(objectId);
        report.ExpectedHash.Should().Be("expected-hash");
        report.ActualHash.Should().Be("actual-hash");
        report.AffectedInstance.Should().Be("Primary-SSD");
        report.RecoveryAction.Should().Be(TamperRecoveryBehavior.AutoRecoverWithReport);
        report.RecoverySucceeded.Should().BeTrue();
        report.AttributionConfidence.Should().Be(AttributionConfidence.Unknown);
        report.DetectedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TamperIncidentReport_CreateWithAttribution_ShouldIncludeAttributionData()
    {
        var objectId = Guid.NewGuid();
        var evidence = TamperEvidence.Create(
            HashAlgorithmType.SHA256, 4096, 4096, "manifest-checksum");
        var attribution = AttributionAnalysis.CreateConfirmed(
            "user:admin-rogue",
            new List<AccessLogEntry> { new() { EntryId = Guid.NewGuid(), ObjectId = Guid.NewGuid(), Principal = "admin", AccessType = AccessType.Read, Timestamp = DateTimeOffset.UtcNow, Succeeded = true }, new() { EntryId = Guid.NewGuid(), ObjectId = Guid.NewGuid(), Principal = "admin", AccessType = AccessType.Write, Timestamp = DateTimeOffset.UtcNow, Succeeded = true } },
            "Logged write operation directly correlates with hash mismatch",
            DateTimeOffset.UtcNow.AddMinutes(-5));

        var report = TamperIncidentReport.CreateWithAttribution(
            objectId, "expected", "actual",
            "Archive-Tier", TamperRecoveryBehavior.AlertAndWait,
            false, evidence, attribution);

        report.AttributionConfidence.Should().Be(AttributionConfidence.Confirmed);
        report.SuspectedPrincipal.Should().Be("user:admin-rogue");
        report.IsInternalTampering.Should().BeTrue();
        report.AttributionReasoning.Should().Contain("correlates");
    }

    [Fact]
    public void TamperIncidentReport_ShouldConvertToSummary()
    {
        var evidence = TamperEvidence.Create(
            HashAlgorithmType.SHA256, 1000, 1000, "cs");
        var report = TamperIncidentReport.Create(
            Guid.NewGuid(), "aabb", "ccdd", "Primary",
            TamperRecoveryBehavior.AutoRecoverSilent, true, evidence);

        var summary = report.ToSummary();

        summary.IncidentId.Should().Be(report.IncidentId);
        summary.ObjectId.Should().Be(report.ObjectId);
        summary.AffectedInstance.Should().Be("Primary");
        summary.RecoverySucceeded.Should().BeTrue();
    }

    [Fact]
    public void TamperIncidentReport_ShouldTrackAffectedShards()
    {
        var evidence = TamperEvidence.Create(
            HashAlgorithmType.SHA256, 1000, 1000, "cs");

        var report = new TamperIncidentReport
        {
            IncidentId = Guid.NewGuid(),
            ObjectId = Guid.NewGuid(),
            DetectedAt = DateTimeOffset.UtcNow,
            ExpectedHash = "aabb",
            ActualHash = "ccdd",
            AffectedInstance = "Data-Tier",
            AffectedShards = new List<int> { 0, 3 },
            RecoveryAction = TamperRecoveryBehavior.AutoRecoverWithReport,
            RecoverySucceeded = true,
            AttributionConfidence = AttributionConfidence.Suspected,
            SuspectedPrincipal = "service:etl",
            Evidence = evidence
        };

        report.AffectedShards.Should().HaveCount(2);
        report.AffectedShards.Should().Contain(new[] { 0, 3 });
    }

    #endregion

    #region AttributionAnalysis

    [Fact]
    public void AttributionAnalysis_CreateUnknown_ShouldHaveUnknownConfidence()
    {
        var analysis = AttributionAnalysis.CreateUnknown();

        analysis.Confidence.Should().Be(AttributionConfidence.Unknown);
        analysis.SuspectedPrincipal.Should().BeNull();
        analysis.Reasoning.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AttributionAnalysis_CreateSuspected_ShouldPopulateFields()
    {
        var analysis = AttributionAnalysis.CreateSuspected(
            "user:suspect-1",
            new List<AccessLogEntry> { new() { EntryId = Guid.NewGuid(), ObjectId = Guid.NewGuid(), Principal = "u1", AccessType = AccessType.Read, Timestamp = DateTimeOffset.UtcNow, Succeeded = true } },
            "Multiple principals accessed during time window",
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-1));

        analysis.Confidence.Should().Be(AttributionConfidence.Suspected);
        analysis.SuspectedPrincipal.Should().Be("user:suspect-1");
        analysis.EstimatedTamperTimeFrom.Should().NotBeNull();
        analysis.EstimatedTamperTimeTo.Should().NotBeNull();
    }

    [Fact]
    public void AttributionAnalysis_CreateLikely_ShouldIndicateInternalTampering()
    {
        var analysis = AttributionAnalysis.CreateLikely(
            "user:insider",
            new List<AccessLogEntry> { new() { EntryId = Guid.NewGuid(), ObjectId = Guid.NewGuid(), Principal = "u1", AccessType = AccessType.Read, Timestamp = DateTimeOffset.UtcNow, Succeeded = true }, new() { EntryId = Guid.NewGuid(), ObjectId = Guid.NewGuid(), Principal = "u2", AccessType = AccessType.Write, Timestamp = DateTimeOffset.UtcNow, Succeeded = true } },
            "Exclusive access during window");

        analysis.Confidence.Should().Be(AttributionConfidence.Likely);
        analysis.IsInternalTampering.Should().BeTrue();
    }

    [Fact]
    public void AttributionAnalysis_CreateConfirmed_ShouldHaveExactTamperTime()
    {
        var tamperTime = DateTimeOffset.UtcNow.AddMinutes(-15);
        var analysis = AttributionAnalysis.CreateConfirmed(
            "user:confirmed-tamperer",
            new List<AccessLogEntry>(),
            "Direct write correlation",
            tamperTime);

        analysis.Confidence.Should().Be(AttributionConfidence.Confirmed);
        analysis.EstimatedTamperTimeFrom.Should().Be(tamperTime);
        analysis.EstimatedTamperTimeTo.Should().Be(tamperTime);
    }

    #endregion

    #region TamperEvidence

    [Fact]
    public void TamperEvidence_Create_ShouldPopulateBasicFields()
    {
        var evidence = TamperEvidence.Create(
            HashAlgorithmType.SHA512, 4000, 4096, "manifest-cs");

        evidence.HashAlgorithm.Should().Be(HashAlgorithmType.SHA512);
        evidence.CorruptedDataSize.Should().Be(4000);
        evidence.ExpectedDataSize.Should().Be(4096);
        evidence.ManifestChecksum.Should().Be("manifest-cs");
    }

    [Fact]
    public void TamperEvidence_CreateWithRecovery_ShouldIncludeRecoveryInfo()
    {
        var evidence = TamperEvidence.CreateWithRecovery(
            HashAlgorithmType.SHA256, 4000, 4096, "cs",
            "WORM-Tier", "recovered-hash-value");

        evidence.RecoverySource.Should().Be("WORM-Tier");
        evidence.RecoveredDataHash.Should().Be("recovered-hash-value");
    }

    [Fact]
    public void TamperEvidence_CreateDetailed_ShouldIncludeByteCorruptionInfo()
    {
        var evidence = TamperEvidence.CreateDetailed(
            HashAlgorithmType.SHA256, 4096, 4096, "cs",
            3, new List<long> { 100, 200, 300 });

        evidence.CorruptedByteCount.Should().Be(3);
        evidence.CorruptedByteOffsets.Should().HaveCount(3);
        evidence.CorruptedByteOffsets.Should().Contain(new long[] { 100, 200, 300 });
    }

    #endregion

    #region TamperIncident (from Results)

    [Fact]
    public void TamperIncident_ShouldConstructWithAllFields()
    {
        var incident = new TamperIncident
        {
            IncidentId = Guid.NewGuid(),
            ObjectId = Guid.NewGuid(),
            Version = 1,
            DetectedAt = DateTimeOffset.UtcNow,
            TamperedComponent = "shard-3",
            ExpectedHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "expected"),
            ActualHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "actual"),
            AttributionConfidence = AttributionConfidence.Likely,
            SuspectedPrincipal = "user:suspect",
            RecoveryPerformed = true,
            RecoveryDetails = "Recovered from WORM backup",
            AdminNotified = true
        };

        incident.TamperedComponent.Should().Be("shard-3");
        incident.AttributionConfidence.Should().Be(AttributionConfidence.Likely);
        incident.SuspectedPrincipal.Should().Be("user:suspect");
        incident.RecoveryPerformed.Should().BeTrue();
        incident.AdminNotified.Should().BeTrue();
    }

    #endregion

    #region Access Log Correlation with Time Window

    [Fact]
    public void SuspiciousAccessAnalysis_CreateUnknown_ShouldProvideRecommendations()
    {
        var analysis = SuspiciousAccessAnalysis.CreateUnknown(
            Guid.NewGuid(), "expected", "actual",
            DateTimeOffset.UtcNow, "No access logs in time window");

        analysis.Confidence.Should().Be(AttributionConfidence.Unknown);
        analysis.SuspectedPrincipals.Should().BeEmpty();
        analysis.RecommendedActions.Should().NotBeEmpty();
        analysis.RecommendedActions.Should().Contain(a => a.Contains("physical access"));
    }

    [Fact]
    public void SuspiciousAccessAnalysis_ShouldValidateCorrectly()
    {
        var analysis = new SuspiciousAccessAnalysis
        {
            ObjectId = Guid.NewGuid(),
            ExpectedHash = "expected",
            ActualHash = "different",
            DetectionTime = DateTimeOffset.UtcNow,
            Confidence = AttributionConfidence.Suspected,
            SuspectedPrincipals = new List<SuspectedPrincipal>
            {
                new()
                {
                    Principal = "user:suspicious",
                    Likelihood = 0.8,
                    LastAccessTime = DateTimeOffset.UtcNow.AddMinutes(-10),
                    AccessTypes = new List<AccessType> { AccessType.Write }
                }
            },
            TamperingWindow = new TimeWindow
            {
                Start = DateTimeOffset.UtcNow.AddHours(-1),
                End = DateTimeOffset.UtcNow
            },
            Evidence = new List<string> { "Write access during window" },
            AnalyzedEntries = new List<AccessLogEntry>()
        };

        analysis.Validate().Should().BeTrue();
    }

    [Fact]
    public void SuspiciousAccessAnalysis_ShouldFailValidationWhenHashesMatch()
    {
        var analysis = new SuspiciousAccessAnalysis
        {
            ObjectId = Guid.NewGuid(),
            ExpectedHash = "same",
            ActualHash = "same",
            DetectionTime = DateTimeOffset.UtcNow,
            Confidence = AttributionConfidence.Unknown,
            SuspectedPrincipals = new List<SuspectedPrincipal>(),
            TamperingWindow = new TimeWindow { Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow },
            Evidence = new List<string> { "test" },
            AnalyzedEntries = new List<AccessLogEntry>()
        };

        analysis.Validate().Should().BeFalse("matching hashes means no tampering");
    }

    #endregion
}
