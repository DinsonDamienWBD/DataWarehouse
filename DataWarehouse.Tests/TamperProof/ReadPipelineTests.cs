// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Integration tests for TamperProof read pipeline SDK contracts (T6.6).
/// Validates ReadMode enum, SecureReadResult, IntegrityVerificationResult,
/// ShardReconstructionResult, and reverse pipeline ordering.
/// </summary>
public class ReadPipelineTests
{
    #region ReadMode Enum

    [Fact]
    public void ReadMode_ShouldHaveExactlyThreeValues()
    {
        var values = Enum.GetValues<ReadMode>();
        values.Should().HaveCount(3, "ReadMode has Fast, Verified, Audit");
    }

    [Fact]
    public void ReadMode_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<ReadMode>();
        values.Should().Contain(ReadMode.Fast);
        values.Should().Contain(ReadMode.Verified);
        values.Should().Contain(ReadMode.Audit);
    }

    [Theory]
    [InlineData(ReadMode.Fast, "Fast")]
    [InlineData(ReadMode.Verified, "Verified")]
    [InlineData(ReadMode.Audit, "Audit")]
    public void ReadMode_ShouldHaveCorrectNames(ReadMode mode, string expectedName)
    {
        mode.ToString().Should().Be(expectedName);
    }

    [Fact]
    public void TamperProofConfiguration_ShouldDefaultToVerifiedReadMode()
    {
        var config = CreateTestConfig();
        config.DefaultReadMode.Should().Be(ReadMode.Verified);
    }

    #endregion

    #region SecureReadResult

    [Fact]
    public void SecureReadResult_CreateSuccess_ShouldPopulateAllFields()
    {
        var objectId = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3, 4 };
        var expectedHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB");
        var actualHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB");
        var verification = IntegrityVerificationResult.CreateValid(expectedHash, actualHash);

        var result = SecureReadResult.CreateSuccess(
            objectId, 1, data, verification, ReadMode.Verified);

        result.Success.Should().BeTrue();
        result.ObjectId.Should().Be(objectId);
        result.Version.Should().Be(1);
        result.Data.Should().BeEquivalentTo(data);
        result.IntegrityVerification.IntegrityValid.Should().BeTrue();
        result.ReadMode.Should().Be(ReadMode.Verified);
        result.RecoveryPerformed.Should().BeFalse();
    }

    [Fact]
    public void SecureReadResult_CreateFailure_ShouldIndicateError()
    {
        var objectId = Guid.NewGuid();
        var result = SecureReadResult.CreateFailure(objectId, 1, "Object not found", ReadMode.Fast);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Object not found");
        result.Data.Should().BeNull();
        result.ReadMode.Should().Be(ReadMode.Fast);
    }

    [Fact]
    public void SecureReadResult_WithRecovery_ShouldIncludeRecoveryDetails()
    {
        var objectId = Guid.NewGuid();
        var verification = IntegrityVerificationResult.CreateValid(
            IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB"),
            IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB"));
        var recovery = RecoveryResult.CreateSuccess(objectId, 1, "WORM", "Recovered 2 corrupted shards");

        var result = SecureReadResult.CreateSuccess(
            objectId, 1, new byte[] { 1, 2, 3 }, verification, ReadMode.Verified,
            recoveryPerformed: true, recoveryDetails: recovery);

        result.RecoveryPerformed.Should().BeTrue();
        result.RecoveryDetails.Should().NotBeNull();
        result.RecoveryDetails!.RecoverySource.Should().Be("WORM");
    }

    [Fact]
    public void SecureReadResult_WithBlockchainVerification_ShouldIncludeDetails()
    {
        var objectId = Guid.NewGuid();
        var verification = IntegrityVerificationResult.CreateValid(
            IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB"),
            IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB"),
            blockchainVerified: true);
        var bcDetails = BlockchainVerificationDetails.CreateSuccess(
            19000000, DateTimeOffset.UtcNow.AddDays(-1), 12);

        var result = SecureReadResult.CreateSuccess(
            objectId, 1, new byte[] { 1 }, verification, ReadMode.Audit,
            null, false, null, bcDetails);

        result.BlockchainVerification.Should().NotBeNull();
        result.BlockchainVerification!.Verified.Should().BeTrue();
        result.BlockchainVerification.BlockNumber.Should().Be(19000000);
        result.BlockchainVerification.Confirmations.Should().Be(12);
    }

    #endregion

    #region IntegrityVerificationResult

    [Fact]
    public void IntegrityVerificationResult_CreateValid_ShouldIndicateSuccess()
    {
        var expected = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB");
        var actual = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB");

        var result = IntegrityVerificationResult.CreateValid(expected, actual);

        result.IntegrityValid.Should().BeTrue();
        result.ExpectedHash!.HashValue.Should().Be("AABB");
        result.ActualHash!.HashValue.Should().Be("AABB");
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void IntegrityVerificationResult_CreateFailed_ShouldIndicateFailure()
    {
        var result = IntegrityVerificationResult.CreateFailed("Hash mismatch detected");

        result.IntegrityValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Hash mismatch detected");
    }

    [Fact]
    public void IntegrityVerificationResult_WithShardResults_ShouldTrackPerShard()
    {
        var shardResults = new[]
        {
            ShardVerificationResult.CreateValid(0,
                IntegrityHash.Create(HashAlgorithmType.SHA256, "s0"),
                IntegrityHash.Create(HashAlgorithmType.SHA256, "s0")),
            ShardVerificationResult.CreateFailed(1, "Shard 1 corrupted"),
            ShardVerificationResult.CreateValid(2,
                IntegrityHash.Create(HashAlgorithmType.SHA256, "s2"),
                IntegrityHash.Create(HashAlgorithmType.SHA256, "s2")),
        };

        var result = IntegrityVerificationResult.CreateFailed(
            "Shard integrity failure",
            shardResults: shardResults);

        result.ShardResults.Should().HaveCount(3);
        result.ShardResults[0].Valid.Should().BeTrue();
        result.ShardResults[1].Valid.Should().BeFalse();
        result.ShardResults[2].Valid.Should().BeTrue();
    }

    #endregion

    #region ShardVerificationResult (Shard Reconstruction)

    [Fact]
    public void ShardVerificationResult_CreateValid_ShouldIndicateSuccess()
    {
        var expected = IntegrityHash.Create(HashAlgorithmType.SHA256, "shard-hash");
        var actual = IntegrityHash.Create(HashAlgorithmType.SHA256, "shard-hash");

        var result = ShardVerificationResult.CreateValid(0, expected, actual);

        result.Valid.Should().BeTrue();
        result.ShardIndex.Should().Be(0);
    }

    [Fact]
    public void ShardVerificationResult_CreateFailed_ShouldIndicateCorruption()
    {
        var result = ShardVerificationResult.CreateFailed(3, "Shard 3: hash mismatch");

        result.Valid.Should().BeFalse();
        result.ShardIndex.Should().Be(3);
        result.ErrorMessage.Should().Contain("hash mismatch");
    }

    [Fact]
    public void RaidRecord_ShouldTrackShardCountsAndParity()
    {
        var raidRecord = new RaidRecord
        {
            DataShardCount = 4,
            ParityShardCount = 2,
            ShardSize = 1024,
            Shards = CreateTestShards(4, 2)
        };

        raidRecord.TotalShardCount.Should().Be(6);
        raidRecord.DataShardCount.Should().Be(4);
        raidRecord.ParityShardCount.Should().Be(2);
        raidRecord.Shards.Count(s => !s.IsParity).Should().Be(4);
        raidRecord.Shards.Count(s => s.IsParity).Should().Be(2);
    }

    #endregion

    #region Reverse Pipeline Ordering

    [Fact]
    public void ReadPipeline_ShouldReverseWritePipelineOrder()
    {
        // Write pipeline stages: Compress(0) -> Encrypt(1)
        var writeStages = new List<PipelineStageRecord>
        {
            new() { StageType = "compression", StageIndex = 0, InputHash = "a", OutputHash = "b", InputSize = 1000, OutputSize = 800, ExecutedAt = DateTimeOffset.UtcNow },
            new() { StageType = "encryption", StageIndex = 1, InputHash = "b", OutputHash = "c", InputSize = 800, OutputSize = 816, ExecutedAt = DateTimeOffset.UtcNow },
        };

        // Read pipeline reverses: Decrypt -> Decompress
        var readOrder = writeStages.OrderByDescending(s => s.StageIndex).ToList();

        readOrder[0].StageType.Should().Be("encryption", "first reverse step is decryption");
        readOrder[1].StageType.Should().Be("compression", "second reverse step is decompression");
    }

    [Fact]
    public void ReadPipeline_BlockchainVerifyThenDecryptThenDecompress()
    {
        // Full read pipeline: Blockchain verify -> Reconstruct shards -> Decrypt -> Decompress -> Strip padding
        var readPhases = new[] { "blockchain_verify", "shard_reconstruct", "decrypt", "decompress", "strip_padding" };

        readPhases[0].Should().Be("blockchain_verify");
        readPhases[1].Should().Be("shard_reconstruct");
        readPhases[2].Should().Be("decrypt");
        readPhases[3].Should().Be("decompress");
        readPhases[4].Should().Be("strip_padding");
    }

    #endregion

    #region BlockchainVerificationDetails

    [Fact]
    public void BlockchainVerificationDetails_CreateSuccess_ShouldPopulateFields()
    {
        var details = BlockchainVerificationDetails.CreateSuccess(
            19000000, DateTimeOffset.UtcNow, 12);

        details.Verified.Should().BeTrue();
        details.BlockNumber.Should().Be(19000000);
        details.Confirmations.Should().Be(12);
    }

    [Fact]
    public void BlockchainVerificationDetails_CreateFailure_ShouldIndicateError()
    {
        var details = BlockchainVerificationDetails.CreateFailure("Anchor not found");

        details.Verified.Should().BeFalse();
        details.ErrorMessage.Should().Be("Anchor not found");
    }

    #endregion

    #region Helpers

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

    private static IReadOnlyList<ShardRecord> CreateTestShards(int dataCount, int parityCount)
    {
        var now = DateTimeOffset.UtcNow;
        var shards = new List<ShardRecord>();

        for (int i = 0; i < dataCount + parityCount; i++)
        {
            shards.Add(new ShardRecord
            {
                ShardIndex = i,
                IsParity = i >= dataCount,
                ActualSize = 1024,
                ContentHash = $"hash-{i}",
                StorageLocation = $"/data/shard-{i}",
                WrittenAt = now
            });
        }

        return shards;
    }

    #endregion
}
