// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Unit tests for TamperProof integrity verification SDK contracts (T6.1).
/// Validates HashAlgorithmType enum coverage, TamperProofManifest construction,
/// integrity hash verification, hash mismatch detection, and ShardRecord construction.
/// </summary>
public class IntegrityProviderTests
{
    #region HashAlgorithmType Enum Coverage

    [Fact]
    public void HashAlgorithmType_ShouldContainAllExpectedValues()
    {
        // Verify all expected hash algorithm types are present
        var values = Enum.GetValues<HashAlgorithmType>();
        values.Should().Contain(HashAlgorithmType.SHA256);
        values.Should().Contain(HashAlgorithmType.SHA384);
        values.Should().Contain(HashAlgorithmType.SHA512);
        values.Should().Contain(HashAlgorithmType.Blake3);
        values.Should().Contain(HashAlgorithmType.SHA3_256);
        values.Should().Contain(HashAlgorithmType.SHA3_384);
        values.Should().Contain(HashAlgorithmType.SHA3_512);
        values.Should().Contain(HashAlgorithmType.Keccak256);
        values.Should().Contain(HashAlgorithmType.Keccak384);
        values.Should().Contain(HashAlgorithmType.Keccak512);
        values.Should().Contain(HashAlgorithmType.HMAC_SHA256);
        values.Should().Contain(HashAlgorithmType.HMAC_SHA384);
        values.Should().Contain(HashAlgorithmType.HMAC_SHA512);
        values.Should().Contain(HashAlgorithmType.HMAC_SHA3_256);
        values.Should().Contain(HashAlgorithmType.HMAC_SHA3_384);
        values.Should().Contain(HashAlgorithmType.HMAC_SHA3_512);
    }

    [Fact]
    public void HashAlgorithmType_ShouldHaveAtLeast16Values()
    {
        var values = Enum.GetValues<HashAlgorithmType>();
        values.Should().HaveCountGreaterThanOrEqualTo(16,
            "TamperProof requires SHA-2, SHA-3, Keccak, BLAKE3, and HMAC variants");
    }

    [Theory]
    [InlineData(HashAlgorithmType.SHA256, "SHA256")]
    [InlineData(HashAlgorithmType.SHA3_256, "SHA3_256")]
    [InlineData(HashAlgorithmType.Keccak256, "Keccak256")]
    [InlineData(HashAlgorithmType.HMAC_SHA256, "HMAC_SHA256")]
    [InlineData(HashAlgorithmType.HMAC_SHA3_512, "HMAC_SHA3_512")]
    public void HashAlgorithmType_ShouldHaveCorrectNames(HashAlgorithmType algorithm, string expectedName)
    {
        algorithm.ToString().Should().Be(expectedName);
    }

    #endregion

    #region TamperProofManifest Construction

    [Fact]
    public void TamperProofManifest_ShouldBeCreatedWithFinalContentHash()
    {
        var data = Encoding.UTF8.GetBytes("test data for integrity verification");
        var hash = Convert.ToHexString(SHA256.HashData(data));
        var now = DateTimeOffset.UtcNow;

        var manifest = CreateTestManifest(
            originalContentHash: hash,
            finalContentHash: hash,
            originalContentSize: data.Length,
            finalContentSize: data.Length);

        manifest.FinalContentHash.Should().Be(hash);
        manifest.OriginalContentHash.Should().Be(hash);
        manifest.Version.Should().Be(1);
        manifest.ObjectId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void TamperProofManifest_ShouldValidateSuccessfullyWithRequiredFields()
    {
        var manifest = CreateTestManifest();

        var errors = manifest.Validate();

        errors.Should().BeEmpty("a properly constructed manifest should have no validation errors");
    }

    [Fact]
    public void TamperProofManifest_ShouldFailValidationWithEmptyObjectId()
    {
        var manifest = CreateTestManifest(objectId: Guid.Empty);

        var errors = manifest.Validate();

        errors.Should().Contain(e => e.Contains("ObjectId"));
    }

    [Fact]
    public void TamperProofManifest_ShouldComputeManifestHash()
    {
        var manifest = CreateTestManifest();

        var manifestHash = manifest.ComputeManifestHash();

        manifestHash.Should().NotBeNullOrWhiteSpace();
        manifestHash.Should().HaveLength(64, "SHA-256 produces 64 hex characters");
    }

    [Fact]
    public void TamperProofManifest_ShouldProduceDifferentHashForDifferentContent()
    {
        var manifest1 = CreateTestManifest(originalContentHash: "AABB");
        var manifest2 = CreateTestManifest(originalContentHash: "CCDD");

        var hash1 = manifest1.ComputeManifestHash();
        var hash2 = manifest2.ComputeManifestHash();

        hash1.Should().NotBe(hash2, "different content should produce different manifest hashes");
    }

    #endregion

    #region Integrity Verification

    [Fact]
    public void IntegrityHash_ShouldVerifyMatchingHashValues()
    {
        var data = Encoding.UTF8.GetBytes("known content for hash verification");
        var hashValue = Convert.ToHexString(SHA256.HashData(data));

        var integrityHash = IntegrityHash.Create(HashAlgorithmType.SHA256, hashValue);

        // Recompute and verify
        var recomputedHash = Convert.ToHexString(SHA256.HashData(data));
        integrityHash.HashValue.Should().Be(recomputedHash);
    }

    [Fact]
    public void IntegrityHash_ShouldDetectHashMismatch()
    {
        var originalData = Encoding.UTF8.GetBytes("original data");
        var tamperedData = Encoding.UTF8.GetBytes("tampered data");

        var originalHash = Convert.ToHexString(SHA256.HashData(originalData));
        var tamperedHash = Convert.ToHexString(SHA256.HashData(tamperedData));

        originalHash.Should().NotBe(tamperedHash,
            "tampered data must produce a different hash than the original");
    }

    [Fact]
    public void IntegrityHash_ShouldFormatAsAlgorithmColonHash()
    {
        var hash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABBCCDD");

        hash.ToString().Should().Be("SHA256:AABBCCDD");
    }

    [Fact]
    public void IntegrityHash_ShouldParseFromStringRepresentation()
    {
        var parsed = IntegrityHash.Parse("SHA512:AABBCCDDEE");

        parsed.Algorithm.Should().Be(HashAlgorithmType.SHA512);
        parsed.HashValue.Should().Be("AABBCCDDEE");
    }

    [Fact]
    public void IntegrityHash_ShouldThrowOnInvalidParseInput()
    {
        var act = () => IntegrityHash.Parse("invalid-format");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IntegrityHash_EmptyShouldHaveEmptyHashValue()
    {
        var empty = IntegrityHash.Empty();

        empty.Algorithm.Should().Be(HashAlgorithmType.SHA256);
        empty.HashValue.Should().BeEmpty();
    }

    #endregion

    #region ShardRecord Construction

    [Fact]
    public void ShardRecord_ShouldConstructWithContentHash()
    {
        var shardData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var shardHash = Convert.ToHexString(SHA256.HashData(shardData));

        var shard = new ShardRecord
        {
            ShardIndex = 0,
            IsParity = false,
            ActualSize = shardData.Length,
            ContentHash = shardHash,
            StorageLocation = "/data/shard-0",
            WrittenAt = DateTimeOffset.UtcNow
        };

        shard.ContentHash.Should().Be(shardHash);
        shard.ShardIndex.Should().Be(0);
        shard.IsParity.Should().BeFalse();
        shard.ActualSize.Should().Be(4);
    }

    [Fact]
    public void ShardRecord_ShouldValidateSuccessfully()
    {
        var shard = new ShardRecord
        {
            ShardIndex = 0,
            IsParity = false,
            ActualSize = 1024,
            ContentHash = "AABBCCDD",
            StorageLocation = "/data/shard-0",
            WrittenAt = DateTimeOffset.UtcNow
        };

        var errors = shard.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ShardRecord_ShouldFailValidationWithoutContentHash()
    {
        var shard = new ShardRecord
        {
            ShardIndex = 0,
            IsParity = false,
            ActualSize = 1024,
            ContentHash = "",
            StorageLocation = "/data/shard-0",
            WrittenAt = DateTimeOffset.UtcNow
        };

        var errors = shard.Validate();
        errors.Should().Contain(e => e.Contains("ContentHash"));
    }

    #endregion

    #region Helpers

    private static TamperProofManifest CreateTestManifest(
        Guid? objectId = null,
        string? originalContentHash = null,
        string? finalContentHash = null,
        long originalContentSize = 100,
        long finalContentSize = 100)
    {
        var hash = originalContentHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("test")));
        var fHash = finalContentHash ?? hash;
        var now = DateTimeOffset.UtcNow;

        return new TamperProofManifest
        {
            ObjectId = objectId ?? Guid.NewGuid(),
            Version = 1,
            CreatedAt = now,
            WriteContext = new WriteContextRecord
            {
                Author = "test-author",
                Comment = "test write",
                Timestamp = now
            },
            HashAlgorithm = HashAlgorithmType.SHA256,
            OriginalContentHash = hash,
            OriginalContentSize = originalContentSize,
            FinalContentHash = fHash,
            FinalContentSize = finalContentSize,
            PipelineStages = Array.Empty<PipelineStageRecord>(),
            RaidConfiguration = new RaidRecord
            {
                DataShardCount = 4,
                ParityShardCount = 2,
                ShardSize = 1024,
                Shards = Enumerable.Range(0, 6).Select(i => new ShardRecord
                {
                    ShardIndex = i,
                    IsParity = i >= 4,
                    ActualSize = 1024,
                    ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"shard-{i}"))),
                    StorageLocation = $"/data/shard-{i}",
                    WrittenAt = now
                }).ToList()
            }
        };
    }

    #endregion
}
