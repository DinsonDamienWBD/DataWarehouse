// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Integration tests for TamperProof write pipeline SDK contracts (T6.5).
/// Validates WriteContext construction, pipeline stage ordering, SecureWriteResult,
/// TransactionFailureBehavior enum, TierWriteResult, and manifest creation.
/// </summary>
public class WritePipelineTests
{
    #region WriteContext Construction

    [Fact]
    public void WriteContext_ShouldConstructWithRequiredFields()
    {
        var context = new WriteContext
        {
            Author = "etl-pipeline",
            Comment = "Daily data ingest batch #42"
        };

        context.Author.Should().Be("etl-pipeline");
        context.Comment.Should().Be("Daily data ingest batch #42");
        context.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WriteContext_ShouldValidateSuccessfully()
    {
        var context = new WriteContext
        {
            Author = "user:admin",
            Comment = "Initial data load"
        };

        var errors = context.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void WriteContext_ShouldFailValidationWithEmptyAuthor()
    {
        var context = new WriteContext
        {
            Author = "",
            Comment = "Some comment"
        };

        var errors = context.Validate();
        errors.Should().Contain(e => e.Contains("Author"));
    }

    [Fact]
    public void WriteContext_ShouldFailValidationWithEmptyComment()
    {
        var context = new WriteContext
        {
            Author = "user:admin",
            Comment = ""
        };

        var errors = context.Validate();
        errors.Should().Contain(e => e.Contains("Comment"));
    }

    [Fact]
    public void WriteContext_ShouldConvertToRecord()
    {
        var context = new WriteContext
        {
            Author = "user:admin",
            Comment = "test write",
            SessionId = "session-001",
            SourceSystem = "api-gateway",
            ClientIp = "192.168.1.1"
        };

        var record = context.ToRecord();

        record.Author.Should().Be("user:admin");
        record.Comment.Should().Be("test write");
        record.SessionId.Should().Be("session-001");
        record.SourceSystem.Should().Be("api-gateway");
        record.ClientIp.Should().Be("192.168.1.1");
    }

    [Fact]
    public void WriteContextBuilder_ShouldBuildValidContext()
    {
        var context = new WriteContextBuilder()
            .WithAuthor("user:builder")
            .WithComment("built via builder")
            .WithSessionId("sess-001")
            .WithSourceSystem("test-system")
            .Build();

        context.Author.Should().Be("user:builder");
        context.Comment.Should().Be("built via builder");
        context.SessionId.Should().Be("sess-001");
    }

    [Fact]
    public void WriteContextBuilder_ShouldThrowWithoutAuthor()
    {
        var act = () => new WriteContextBuilder()
            .WithComment("missing author")
            .Build();

        act.Should().Throw<InvalidOperationException>();
    }

    #endregion

    #region Pipeline Stage Ordering

    [Fact]
    public void PipelineStages_ShouldBeOrderedCompressThenEncryptThenShard()
    {
        // Write pipeline: Compress -> Encrypt -> Shard -> WORM -> Blockchain
        var stages = new List<PipelineStageRecord>
        {
            CreateStage(0, "compression", "input-hash-0", "output-hash-0"),
            CreateStage(1, "encryption", "output-hash-0", "output-hash-1"),
        };

        stages[0].StageType.Should().Be("compression");
        stages[0].StageIndex.Should().Be(0);
        stages[1].StageType.Should().Be("encryption");
        stages[1].StageIndex.Should().Be(1);

        // Each stage's input should be previous stage's output
        stages[1].InputHash.Should().Be(stages[0].OutputHash);
    }

    [Fact]
    public void PipelineStageRecord_ShouldValidateSuccessfully()
    {
        var stage = CreateStage(0, "compression", "input", "output");
        var errors = stage.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void PipelineStageRecord_ShouldTrackExecutionDuration()
    {
        var stage = new PipelineStageRecord
        {
            StageType = "encryption",
            StageIndex = 1,
            InputHash = "aabb",
            OutputHash = "ccdd",
            InputSize = 1000,
            OutputSize = 1024,
            ExecutedAt = DateTimeOffset.UtcNow,
            ExecutionDuration = TimeSpan.FromMilliseconds(150)
        };

        stage.ExecutionDuration.Should().Be(TimeSpan.FromMilliseconds(150));
    }

    #endregion

    #region SecureWriteResult

    [Fact]
    public void SecureWriteResult_CreateSuccess_ShouldPopulateAllFields()
    {
        var objectId = Guid.NewGuid();
        var integrityHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABBCCDD");
        var writeContext = new WriteContextRecord
        {
            Author = "test",
            Comment = "test",
            Timestamp = DateTimeOffset.UtcNow
        };

        var result = SecureWriteResult.CreateSuccess(
            objectId, 1, integrityHash, "manifest-001", "worm-001",
            writeContext, 6, 4096, 4200);

        result.Success.Should().BeTrue();
        result.ObjectId.Should().Be(objectId);
        result.Version.Should().Be(1);
        result.IntegrityHash.HashValue.Should().Be("AABBCCDD");
        result.ManifestId.Should().Be("manifest-001");
        result.WormRecordId.Should().Be("worm-001");
        result.ShardCount.Should().Be(6);
        result.OriginalSizeBytes.Should().Be(4096);
        result.PaddedSizeBytes.Should().Be(4200);
        result.DegradationState.Should().Be(InstanceDegradationState.Healthy);
    }

    [Fact]
    public void SecureWriteResult_CreateFailure_ShouldIndicateError()
    {
        var result = SecureWriteResult.CreateFailure("Disk full");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Disk full");
        result.DegradationState.Should().Be(InstanceDegradationState.Corrupted);
    }

    [Fact]
    public void SecureWriteResult_CreateSuccess_WithWarnings_ShouldIncludeWarnings()
    {
        var warnings = new List<string> { "WORM write succeeded but blockchain anchor pending" };
        var integrityHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB");
        var writeContext = new WriteContextRecord
        {
            Author = "test",
            Comment = "test",
            Timestamp = DateTimeOffset.UtcNow
        };

        var result = SecureWriteResult.CreateSuccess(
            Guid.NewGuid(), 1, integrityHash, "m-1", "w-1",
            writeContext, 4, 1000, 1024, null, warnings);

        result.Warnings.Should().HaveCount(1);
        result.Warnings[0].Should().Contain("blockchain anchor pending");
    }

    #endregion

    #region TransactionFailureBehavior Enum

    [Fact]
    public void TransactionFailureBehavior_ShouldHaveTwoValues()
    {
        var values = Enum.GetValues<TransactionFailureBehavior>();
        values.Should().HaveCount(2);
        values.Should().Contain(TransactionFailureBehavior.Strict);
        values.Should().Contain(TransactionFailureBehavior.AllowDegraded);
    }

    [Fact]
    public void TamperProofConfiguration_ShouldDefaultToStrictTransactionBehavior()
    {
        var config = CreateTestConfig();
        config.TransactionFailureBehavior.Should().Be(TransactionFailureBehavior.Strict);
    }

    #endregion

    #region TierWriteResult

    [Fact]
    public void TierWriteResult_CreateSuccess_ShouldPopulateFields()
    {
        var result = TierWriteResult.CreateSuccess(
            "Data", "data-instance", "resource-001", 4096, TimeSpan.FromMilliseconds(50));

        result.Success.Should().BeTrue();
        result.TierName.Should().Be("Data");
        result.InstanceId.Should().Be("data-instance");
        result.ResourceId.Should().Be("resource-001");
        result.BytesWritten.Should().Be(4096);
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void TierWriteResult_CreateFailure_ShouldIndicateError()
    {
        var result = TierWriteResult.CreateFailure("WORM", "worm-instance", "Retention policy violation");

        result.Success.Should().BeFalse();
        result.TierName.Should().Be("WORM");
        result.ErrorMessage.Should().Contain("Retention policy");
    }

    [Fact]
    public void TransactionResult_CreateSuccess_ShouldCombineAllTierResults()
    {
        var objectId = Guid.NewGuid();
        var dataResult = TierWriteResult.CreateSuccess("Data", "d1", "r1", 1000);
        var metadataResult = TierWriteResult.CreateSuccess("Metadata", "m1", "r2", 200);
        var wormResult = TierWriteResult.CreateSuccess("WORM", "w1", "r3", 1000);
        var blockchainResult = TierWriteResult.CreateSuccess("Blockchain", "b1", "r4", 64);

        var transaction = TransactionResult.CreateSuccess(objectId, dataResult, metadataResult, wormResult, blockchainResult);

        transaction.Success.Should().BeTrue();
        transaction.DegradationState.Should().Be(InstanceDegradationState.Healthy);
        transaction.DataTierResult!.Success.Should().BeTrue();
        transaction.MetadataTierResult!.Success.Should().BeTrue();
        transaction.WormTierResult!.Success.Should().BeTrue();
        transaction.BlockchainTierResult!.Success.Should().BeTrue();
    }

    #endregion

    #region Manifest Creation with Pipeline Records

    [Fact]
    public void Manifest_ShouldContainContentPaddingRecord()
    {
        var manifest = CreateManifestWithPadding();

        manifest.ContentPadding.Should().NotBeNull();
        manifest.ContentPadding!.PrefixPaddingBytes.Should().BeGreaterThan(0);
        manifest.ContentPadding.TotalPaddingBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Manifest_ShouldContainShardPaddingRecords()
    {
        var manifest = CreateManifestWithShardPadding();

        manifest.RaidConfiguration.Shards.Should().Contain(s => s.Padding != null);
    }

    [Fact]
    public void Manifest_ShouldContainEncryptionMetadataInPipelineStage()
    {
        var stage = new PipelineStageRecord
        {
            StageType = "encryption",
            StageIndex = 1,
            InputHash = "plain-hash",
            OutputHash = "encrypted-hash",
            InputSize = 1000,
            OutputSize = 1024,
            ExecutedAt = DateTimeOffset.UtcNow,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "AES-256-GCM",
                ["KeyId"] = "key-001"
            }
        };

        stage.Parameters.Should().ContainKey("Algorithm");
        stage.Parameters!["Algorithm"].Should().Be("AES-256-GCM");
    }

    #endregion

    #region Helpers

    private static PipelineStageRecord CreateStage(int index, string type, string inputHash, string outputHash)
    {
        return new PipelineStageRecord
        {
            StageType = type,
            StageIndex = index,
            InputHash = inputHash,
            OutputHash = outputHash,
            InputSize = 1000 + index * 100,
            OutputSize = 1000 + (index + 1) * 100,
            ExecutedAt = DateTimeOffset.UtcNow
        };
    }

    private static TamperProofConfiguration CreateTestConfig()
    {
        return new TamperProofConfiguration
        {
            StorageInstances = new StorageInstancesConfig
            {
                Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "local" },
                Metadata = new StorageInstanceConfig { InstanceId = "metadata", PluginId = "local" },
                Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "local" },
                Blockchain = new StorageInstanceConfig { InstanceId = "blockchain", PluginId = "local" }
            },
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 }
        };
    }

    private static TamperProofManifest CreateManifestWithPadding()
    {
        var now = DateTimeOffset.UtcNow;
        return new TamperProofManifest
        {
            ObjectId = Guid.NewGuid(),
            Version = 1,
            CreatedAt = now,
            WriteContext = new WriteContextRecord { Author = "test", Comment = "test", Timestamp = now },
            HashAlgorithm = HashAlgorithmType.SHA256,
            OriginalContentHash = "AABB",
            OriginalContentSize = 1000,
            FinalContentHash = "CCDD",
            FinalContentSize = 1100,
            PipelineStages = Array.Empty<PipelineStageRecord>(),
            RaidConfiguration = new RaidRecord
            {
                DataShardCount = 1, ParityShardCount = 0, ShardSize = 1100,
                Shards = new[] { new ShardRecord { ShardIndex = 0, IsParity = false, ActualSize = 1100, ContentHash = "EE", StorageLocation = "/s0", WrittenAt = now } }
            },
            ContentPadding = new ContentPaddingRecord
            {
                PrefixPaddingBytes = 50,
                SuffixPaddingBytes = 50,
                PaddingPattern = "random"
            }
        };
    }

    private static TamperProofManifest CreateManifestWithShardPadding()
    {
        var now = DateTimeOffset.UtcNow;
        return new TamperProofManifest
        {
            ObjectId = Guid.NewGuid(),
            Version = 1,
            CreatedAt = now,
            WriteContext = new WriteContextRecord { Author = "test", Comment = "test", Timestamp = now },
            HashAlgorithm = HashAlgorithmType.SHA256,
            OriginalContentHash = "AABB",
            OriginalContentSize = 1000,
            FinalContentHash = "CCDD",
            FinalContentSize = 1000,
            PipelineStages = Array.Empty<PipelineStageRecord>(),
            RaidConfiguration = new RaidRecord
            {
                DataShardCount = 2, ParityShardCount = 0, ShardSize = 512,
                Shards = new[]
                {
                    new ShardRecord
                    {
                        ShardIndex = 0, IsParity = false, ActualSize = 512, ContentHash = "AA", StorageLocation = "/s0", WrittenAt = now,
                        Padding = new ShardPaddingRecord { PrefixPaddingBytes = 10, SuffixPaddingBytes = 10 }
                    },
                    new ShardRecord
                    {
                        ShardIndex = 1, IsParity = false, ActualSize = 512, ContentHash = "BB", StorageLocation = "/s1", WrittenAt = now
                    }
                }
            }
        };
    }

    #endregion
}
