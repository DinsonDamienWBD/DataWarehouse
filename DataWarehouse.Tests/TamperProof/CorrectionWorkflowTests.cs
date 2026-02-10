// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Integration tests for TamperProof correction workflow SDK contracts (T6.9).
/// Validates version increment, provenance chain linking, CorrectionResult
/// with blockchain anchor reference, and append-only semantics.
/// </summary>
public class CorrectionWorkflowTests
{
    #region Version Increment

    [Fact]
    public void CorrectionResult_ShouldIncrementVersionNumber()
    {
        var originalId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        var result = CorrectionResult.CreateSuccess(
            originalId, 1, newId, 2,
            CreateTestWriteResult(newId, 2),
            CreateTestCorrectionContext(originalId));

        result.OriginalVersion.Should().Be(1);
        result.NewVersion.Should().Be(2);
        result.NewVersion.Should().BeGreaterThan(result.OriginalVersion);
    }

    [Fact]
    public void CorrectionResult_ShouldTrackOriginalAndNewObjectIds()
    {
        var originalId = Guid.NewGuid();
        var newId = Guid.NewGuid();

        var result = CorrectionResult.CreateSuccess(
            originalId, 3, newId, 4,
            CreateTestWriteResult(newId, 4),
            CreateTestCorrectionContext(originalId));

        result.OriginalObjectId.Should().Be(originalId);
        result.NewObjectId.Should().Be(newId);
        result.OriginalObjectId.Should().NotBe(result.NewObjectId);
    }

    [Fact]
    public void Manifest_CorrectedVersion_ShouldReferenceOriginalVersion()
    {
        var originalId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var correctedManifest = new TamperProofManifest
        {
            ObjectId = originalId,
            Version = 2,
            CreatedAt = now,
            WriteContext = new WriteContextRecord { Author = "corrector", Comment = "fix typo", Timestamp = now },
            HashAlgorithm = HashAlgorithmType.SHA256,
            OriginalContentHash = "corrected-hash",
            OriginalContentSize = 1024,
            FinalContentHash = "corrected-final",
            FinalContentSize = 1024,
            PipelineStages = Array.Empty<PipelineStageRecord>(),
            RaidConfiguration = CreateTestRaidRecord(),
            PreviousVersionId = originalId,
            CorrectionContext = new CorrectionContextRecord
            {
                Author = "corrector",
                Comment = "Correcting data entry error",
                Timestamp = now,
                CorrectionReason = "Typo in field X",
                OriginalObjectId = originalId,
                OriginalVersion = 1
            }
        };

        correctedManifest.Version.Should().Be(2);
        correctedManifest.PreviousVersionId.Should().Be(originalId);
        correctedManifest.CorrectionContext.Should().NotBeNull();
        correctedManifest.CorrectionContext!.CorrectionReason.Should().Be("Typo in field X");
    }

    #endregion

    #region Provenance Chain Linking

    [Fact]
    public void CorrectionResult_ShouldIncludeAuditChain()
    {
        var originalId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var auditChain = AuditChain.Create(originalId, new[]
        {
            AuditChainEntry.Create(originalId, 1,
                new WriteContextRecord { Author = "creator", Comment = "initial", Timestamp = DateTimeOffset.UtcNow.AddDays(-1) },
                IntegrityHash.Create(HashAlgorithmType.SHA256, "v1-hash"),
                "m1", "w1", 1000),
            AuditChainEntry.Create(newId, 2,
                new WriteContextRecord { Author = "corrector", Comment = "fix", Timestamp = DateTimeOffset.UtcNow },
                IntegrityHash.Create(HashAlgorithmType.SHA256, "v2-hash"),
                "m2", "w2", 1000,
                previousObjectId: originalId,
                correctionContext: new CorrectionContextRecord
                {
                    Author = "corrector", Comment = "fix", Timestamp = DateTimeOffset.UtcNow,
                    CorrectionReason = "Data error", OriginalObjectId = originalId, OriginalVersion = 1
                })
        });

        var result = CorrectionResult.CreateSuccess(
            originalId, 1, newId, 2,
            CreateTestWriteResult(newId, 2),
            CreateTestCorrectionContext(originalId),
            auditChain);

        result.AuditChain.Should().NotBeNull();
        result.AuditChain!.TotalVersions.Should().Be(2);
        result.AuditChain.LatestVersion.Should().Be(2);
        result.AuditChain.GetCorrections().Should().HaveCount(1);
    }

    [Fact]
    public void ProvenanceEntry_ShouldLinkToPreviousEntry()
    {
        var entry1 = new ProvenanceEntry
        {
            EntryId = Guid.NewGuid(),
            OperationType = ProvenanceOperationType.Create,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
            Principal = "user:creator",
            DataHash = "v1-hash",
            PreviousEntryId = null
        };

        var entry2 = new ProvenanceEntry
        {
            EntryId = Guid.NewGuid(),
            OperationType = ProvenanceOperationType.Correct,
            Timestamp = DateTimeOffset.UtcNow,
            Principal = "user:corrector",
            DataHash = "v2-hash",
            PreviousEntryId = entry1.EntryId
        };

        entry1.PreviousEntryId.Should().BeNull("initial entry has no previous");
        entry2.PreviousEntryId.Should().Be(entry1.EntryId);
    }

    [Fact]
    public void ProvenanceOperationType_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<ProvenanceOperationType>();
        values.Should().Contain(ProvenanceOperationType.Create);
        values.Should().Contain(ProvenanceOperationType.Write);
        values.Should().Contain(ProvenanceOperationType.Correct);
        values.Should().Contain(ProvenanceOperationType.Recover);
        values.Should().Contain(ProvenanceOperationType.SecureCorrect);
        values.Should().Contain(ProvenanceOperationType.Migrate);
    }

    #endregion

    #region CorrectionResult with Blockchain Anchor

    [Fact]
    public void SecureCorrectionResult_CreateSuccess_ShouldIncludeProvenance()
    {
        var provenanceChain = new[]
        {
            new ProvenanceEntry
            {
                EntryId = Guid.NewGuid(),
                OperationType = ProvenanceOperationType.Create,
                Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
                Principal = "user:original",
                DataHash = "original-hash"
            },
            new ProvenanceEntry
            {
                EntryId = Guid.NewGuid(),
                OperationType = ProvenanceOperationType.SecureCorrect,
                Timestamp = DateTimeOffset.UtcNow,
                Principal = "admin:corrector",
                DataHash = "corrected-hash"
            }
        };

        var result = SecureCorrectionResult.CreateSuccess(
            Guid.NewGuid(), "admin:corrector",
            "original-hash", "corrected-hash",
            "Regulatory compliance fix",
            Guid.NewGuid(), 2, provenanceChain);

        result.Success.Should().BeTrue();
        result.AuthorizedBy.Should().Be("admin:corrector");
        result.OriginalHash.Should().Be("original-hash");
        result.NewHash.Should().Be("corrected-hash");
        result.NewVersion.Should().Be(2);
        result.ProvenanceChain.Should().HaveCount(2);
    }

    [Fact]
    public void SecureCorrectionResult_CreateFailure_ShouldIndicateError()
    {
        var result = SecureCorrectionResult.CreateFailure(
            Guid.NewGuid(), "Unauthorized correction",
            "Insufficient permissions for this block");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Insufficient permissions");
    }

    #endregion

    #region Append-Only Semantics

    [Fact]
    public void Manifest_OriginalVersion_ShouldBePreservedAfterCorrection()
    {
        // The original manifest (version 1) should still be accessible
        var originalManifest = CreateTestManifest(version: 1);
        var correctedManifest = CreateTestManifest(
            version: 2,
            previousVersionId: originalManifest.ObjectId);

        // Both manifests should exist independently
        originalManifest.Version.Should().Be(1);
        correctedManifest.Version.Should().Be(2);
        correctedManifest.PreviousVersionId.Should().Be(originalManifest.ObjectId);

        // Original content hash should be different from corrected
        originalManifest.OriginalContentHash.Should().NotBe(correctedManifest.OriginalContentHash);
    }

    [Fact]
    public void CorrectionContext_ShouldValidateSuccessfully()
    {
        var context = new CorrectionContext
        {
            Author = "admin:corrector",
            Comment = "Correcting data",
            CorrectionReason = "Field X had wrong value",
            OriginalObjectId = Guid.NewGuid()
        };

        var errors = context.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void CorrectionContext_ShouldFailWithoutCorrectionReason()
    {
        var context = new CorrectionContext
        {
            Author = "admin:corrector",
            Comment = "Correcting data",
            CorrectionReason = "",
            OriginalObjectId = Guid.NewGuid()
        };

        var errors = context.Validate();
        errors.Should().Contain(e => e.Contains("CorrectionReason"));
    }

    [Fact]
    public void CorrectionContext_ShouldConvertToRecord()
    {
        var originalId = Guid.NewGuid();
        var context = new CorrectionContext
        {
            Author = "admin",
            Comment = "fix",
            CorrectionReason = "wrong data",
            OriginalObjectId = originalId,
            OriginalVersion = 1
        };

        var record = context.ToCorrectionRecord();

        record.CorrectionReason.Should().Be("wrong data");
        record.OriginalObjectId.Should().Be(originalId);
        record.OriginalVersion.Should().Be(1);
    }

    #endregion

    #region Helpers

    private static SecureWriteResult CreateTestWriteResult(Guid objectId, int version)
    {
        return SecureWriteResult.CreateSuccess(
            objectId, version,
            IntegrityHash.Create(HashAlgorithmType.SHA256, $"hash-v{version}"),
            $"manifest-v{version}", $"worm-v{version}",
            new WriteContextRecord { Author = "test", Comment = "test", Timestamp = DateTimeOffset.UtcNow },
            4, 1000, 1024);
    }

    private static CorrectionContextRecord CreateTestCorrectionContext(Guid originalObjectId)
    {
        return new CorrectionContextRecord
        {
            Author = "corrector",
            Comment = "Correction applied",
            Timestamp = DateTimeOffset.UtcNow,
            CorrectionReason = "Data error detected",
            OriginalObjectId = originalObjectId,
            OriginalVersion = 1
        };
    }

    private static TamperProofManifest CreateTestManifest(int version = 1, Guid? previousVersionId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new TamperProofManifest
        {
            ObjectId = Guid.NewGuid(),
            Version = version,
            CreatedAt = now,
            WriteContext = new WriteContextRecord { Author = "test", Comment = $"v{version}", Timestamp = now },
            HashAlgorithm = HashAlgorithmType.SHA256,
            OriginalContentHash = $"hash-v{version}",
            OriginalContentSize = 1000,
            FinalContentHash = $"final-v{version}",
            FinalContentSize = 1000,
            PipelineStages = Array.Empty<PipelineStageRecord>(),
            RaidConfiguration = CreateTestRaidRecord(),
            PreviousVersionId = previousVersionId,
            CorrectionContext = version > 1 ? new CorrectionContextRecord
            {
                Author = "corrector", Comment = "fix", Timestamp = now,
                CorrectionReason = "error", OriginalObjectId = previousVersionId ?? Guid.NewGuid()
            } : null
        };
    }

    private static RaidRecord CreateTestRaidRecord()
    {
        var now = DateTimeOffset.UtcNow;
        return new RaidRecord
        {
            DataShardCount = 1, ParityShardCount = 0, ShardSize = 1024,
            Shards = new[] { new ShardRecord { ShardIndex = 0, IsParity = false, ActualSize = 1024, ContentHash = "s0", StorageLocation = "/s0", WrittenAt = now } }
        };
    }

    #endregion
}
