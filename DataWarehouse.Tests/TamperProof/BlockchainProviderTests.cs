// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Unit tests for TamperProof blockchain provider SDK contracts (T6.2).
/// Validates ConsensusMode enum, BlockchainAnchor construction, Merkle root computation,
/// blockchain chain integrity, and batching configuration.
/// </summary>
public class BlockchainProviderTests
{
    #region ConsensusMode Enum

    [Fact]
    public void ConsensusMode_ShouldHaveExactlyThreeValues()
    {
        var values = Enum.GetValues<ConsensusMode>();
        values.Should().HaveCount(3, "TamperProof defines SingleWriter, RaftConsensus, ExternalAnchor");
    }

    [Fact]
    public void ConsensusMode_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<ConsensusMode>();
        values.Should().Contain(ConsensusMode.SingleWriter);
        values.Should().Contain(ConsensusMode.RaftConsensus);
        values.Should().Contain(ConsensusMode.ExternalAnchor);
    }

    [Theory]
    [InlineData(ConsensusMode.SingleWriter, "SingleWriter")]
    [InlineData(ConsensusMode.RaftConsensus, "RaftConsensus")]
    [InlineData(ConsensusMode.ExternalAnchor, "ExternalAnchor")]
    public void ConsensusMode_ShouldHaveCorrectNames(ConsensusMode mode, string expectedName)
    {
        mode.ToString().Should().Be(expectedName);
    }

    #endregion

    #region BlockchainAnchor Construction

    [Fact]
    public void BlockchainAnchor_ShouldConstructWithRequiredFields()
    {
        var objectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var integrityHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABBCCDD");

        var anchor = new BlockchainAnchor
        {
            AnchorId = Guid.NewGuid().ToString(),
            ObjectId = objectId,
            Version = 1,
            IntegrityHash = integrityHash,
            AnchoredAt = now,
            BlockchainTxId = "tx-001",
            Confirmations = 6
        };

        anchor.ObjectId.Should().Be(objectId);
        anchor.Version.Should().Be(1);
        anchor.IntegrityHash.Should().NotBeNull();
        anchor.IntegrityHash.HashValue.Should().Be("AABBCCDD");
        anchor.AnchoredAt.Should().Be(now);
        anchor.BlockchainTxId.Should().Be("tx-001");
        anchor.Confirmations.Should().Be(6);
    }

    [Fact]
    public void BlockchainAnchorReference_ShouldConstructWithAllFields()
    {
        var now = DateTimeOffset.UtcNow;

        var reference = new BlockchainAnchorReference
        {
            Network = "ethereum-mainnet",
            TransactionId = "0x123abc",
            BlockNumber = 19000000,
            BlockHash = "0xblockhash",
            BlockTimestamp = now,
            AnchoredHash = "AABBCCDD",
            ContractAddress = "0xcontract",
            Confirmations = 12
        };

        reference.Network.Should().Be("ethereum-mainnet");
        reference.TransactionId.Should().Be("0x123abc");
        reference.BlockNumber.Should().Be(19000000);
        reference.BlockHash.Should().Be("0xblockhash");
        reference.AnchoredHash.Should().Be("AABBCCDD");
        reference.ContractAddress.Should().Be("0xcontract");
        reference.Confirmations.Should().Be(12);
    }

    [Fact]
    public void BlockchainAnchorReference_ShouldValidateSuccessfully()
    {
        var reference = new BlockchainAnchorReference
        {
            Network = "internal",
            TransactionId = "tx-1",
            BlockNumber = 100,
            BlockTimestamp = DateTimeOffset.UtcNow,
            AnchoredHash = "AABBCCDD"
        };

        var errors = reference.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void BlockchainAnchorReference_ShouldFailValidationWithMissingNetwork()
    {
        var reference = new BlockchainAnchorReference
        {
            Network = "",
            TransactionId = "tx-1",
            BlockNumber = 100,
            BlockTimestamp = DateTimeOffset.UtcNow,
            AnchoredHash = "AABBCCDD"
        };

        var errors = reference.Validate();
        errors.Should().Contain(e => e.Contains("Network"));
    }

    #endregion

    #region Merkle Root Computation

    [Fact]
    public void MerkleRoot_ShouldComputeForKnownLeafHashes()
    {
        // Given known leaf hashes, compute Merkle root
        var leaf1 = ComputeSha256Hex("data-1");
        var leaf2 = ComputeSha256Hex("data-2");

        // Compute expected Merkle root: SHA256(leaf1 + leaf2)
        var combined = leaf1 + leaf2;
        var expectedRoot = ComputeSha256Hex(combined);

        var computedRoot = ComputeMerkleRoot(new[] { leaf1, leaf2 });

        computedRoot.Should().Be(expectedRoot);
    }

    [Fact]
    public void MerkleRoot_ShouldHandleSingleLeaf()
    {
        var singleLeaf = ComputeSha256Hex("single-data");

        var root = ComputeMerkleRoot(new[] { singleLeaf });

        root.Should().Be(singleLeaf, "Merkle root of a single leaf is the leaf itself");
    }

    [Fact]
    public void MerkleRoot_ShouldHandleFourLeaves()
    {
        var leaves = new[]
        {
            ComputeSha256Hex("data-0"),
            ComputeSha256Hex("data-1"),
            ComputeSha256Hex("data-2"),
            ComputeSha256Hex("data-3")
        };

        // Level 1: hash(leaf0+leaf1), hash(leaf2+leaf3)
        var level1_0 = ComputeSha256Hex(leaves[0] + leaves[1]);
        var level1_1 = ComputeSha256Hex(leaves[2] + leaves[3]);

        // Root: hash(level1_0 + level1_1)
        var expectedRoot = ComputeSha256Hex(level1_0 + level1_1);

        var computedRoot = ComputeMerkleRoot(leaves);

        computedRoot.Should().Be(expectedRoot);
    }

    [Fact]
    public void MerkleRoot_ShouldHandleOddNumberOfLeaves()
    {
        var leaves = new[]
        {
            ComputeSha256Hex("data-0"),
            ComputeSha256Hex("data-1"),
            ComputeSha256Hex("data-2")
        };

        // Level 1: hash(leaf0+leaf1), leaf2 promoted
        var level1_0 = ComputeSha256Hex(leaves[0] + leaves[1]);
        var level1_1 = leaves[2]; // promoted

        // Root: hash(level1_0 + level1_1)
        var expectedRoot = ComputeSha256Hex(level1_0 + level1_1);

        var computedRoot = ComputeMerkleRoot(leaves);

        computedRoot.Should().Be(expectedRoot);
    }

    #endregion

    #region Blockchain Chain Integrity

    [Fact]
    public void BlockInfo_ShouldConstructWithRequiredFields()
    {
        var block = BlockInfo.Create(
            blockNumber: 42,
            hash: "blockhash42",
            timestamp: DateTimeOffset.UtcNow,
            transactionCount: 10,
            previousHash: "blockhash41",
            merkleRoot: "merkleroot42");

        block.BlockNumber.Should().Be(42);
        block.Hash.Should().Be("blockhash42");
        block.PreviousHash.Should().Be("blockhash41");
        block.TransactionCount.Should().Be(10);
        block.MerkleRoot.Should().Be("merkleroot42");
    }

    [Fact]
    public void BlockInfo_SequentialBlocks_ShouldHaveValidPreviousHashLinks()
    {
        // Simulate a chain of 5 blocks
        var blocks = new List<BlockInfo>();
        string? previousHash = null;

        for (int i = 0; i < 5; i++)
        {
            var block = BlockInfo.Create(
                blockNumber: i,
                hash: ComputeSha256Hex($"block-{i}-data-{previousHash ?? "genesis"}"),
                timestamp: DateTimeOffset.UtcNow.AddSeconds(i),
                transactionCount: i + 1,
                previousHash: previousHash);
            blocks.Add(block);
            previousHash = block.Hash;
        }

        // Verify chain integrity: each block's PreviousHash matches the previous block's Hash
        for (int i = 1; i < blocks.Count; i++)
        {
            blocks[i].PreviousHash.Should().Be(blocks[i - 1].Hash,
                $"Block {i} PreviousHash should match Block {i - 1} Hash");
        }

        // Genesis block should have no PreviousHash
        blocks[0].PreviousHash.Should().BeNull("Genesis block has no previous hash");
    }

    [Fact]
    public void BlockInfo_TamperedBlock_ShouldBreakChainIntegrity()
    {
        var block0 = BlockInfo.Create(0, ComputeSha256Hex("genesis"), DateTimeOffset.UtcNow, 1);
        var block1 = BlockInfo.Create(1, ComputeSha256Hex("block1"), DateTimeOffset.UtcNow, 2, block0.Hash);
        var block2 = BlockInfo.Create(2, ComputeSha256Hex("block2"), DateTimeOffset.UtcNow, 3, "TAMPERED_HASH");

        // Block2's PreviousHash should NOT match Block1's Hash
        block2.PreviousHash.Should().NotBe(block1.Hash, "tampered PreviousHash should break chain integrity");
    }

    #endregion

    #region Batching Configuration

    [Fact]
    public void BlockchainBatchConfig_ShouldHaveDefaultValues()
    {
        var config = new BlockchainBatchConfig();

        config.MaxBatchSize.Should().Be(100);
        config.MaxBatchDelay.Should().Be(TimeSpan.FromSeconds(5));
        config.WaitForConfirmation.Should().BeTrue();
        config.RequiredConfirmations.Should().Be(1);
    }

    [Fact]
    public void BlockchainBatchConfig_ShouldAcceptCustomValues()
    {
        var config = new BlockchainBatchConfig
        {
            MaxBatchSize = 50,
            MaxBatchDelay = TimeSpan.FromSeconds(10),
            WaitForConfirmation = false,
            RequiredConfirmations = 3
        };

        config.MaxBatchSize.Should().Be(50);
        config.MaxBatchDelay.Should().Be(TimeSpan.FromSeconds(10));
        config.WaitForConfirmation.Should().BeFalse();
        config.RequiredConfirmations.Should().Be(3);
    }

    [Fact]
    public void BlockchainBatchConfig_ShouldValidateSuccessfully()
    {
        var config = new BlockchainBatchConfig();
        var errors = config.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void BlockchainBatchConfig_ShouldFailWithZeroMaxBatchSize()
    {
        var config = new BlockchainBatchConfig { MaxBatchSize = 0 };
        var errors = config.Validate();
        errors.Should().Contain(e => e.Contains("MaxBatchSize"));
    }

    [Fact]
    public void BlockchainBatchConfig_ShouldCloneCorrectly()
    {
        var original = new BlockchainBatchConfig
        {
            MaxBatchSize = 200,
            MaxBatchDelay = TimeSpan.FromSeconds(15),
            RequiredConfirmations = 5
        };

        var clone = original.Clone();

        clone.MaxBatchSize.Should().Be(200);
        clone.MaxBatchDelay.Should().Be(TimeSpan.FromSeconds(15));
        clone.RequiredConfirmations.Should().Be(5);
    }

    #endregion

    #region BatchAnchorResult

    [Fact]
    public void BatchAnchorResult_CreateSuccess_ShouldPopulateFields()
    {
        var anchors = new Dictionary<Guid, BlockchainAnchor>
        {
            [Guid.NewGuid()] = new BlockchainAnchor
            {
                AnchorId = "anc-1",
                ObjectId = Guid.NewGuid(),
                Version = 1,
                IntegrityHash = IntegrityHash.Create(HashAlgorithmType.SHA256, "AABB"),
                AnchoredAt = DateTimeOffset.UtcNow
            }
        };

        var result = BatchAnchorResult.CreateSuccess(42, "merkle-root-hash", anchors, "tx-001");

        result.Success.Should().BeTrue();
        result.BlockNumber.Should().Be(42);
        result.MerkleRoot.Should().Be("merkle-root-hash");
        result.IndividualAnchors.Should().HaveCount(1);
        result.TransactionId.Should().Be("tx-001");
    }

    [Fact]
    public void BatchAnchorResult_CreateFailure_ShouldIndicateError()
    {
        var result = BatchAnchorResult.CreateFailure("Connection timeout");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Connection timeout");
        result.IndividualAnchors.Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute Merkle root matching the BlockchainProviderPluginBase.ComputeMerkleRoot algorithm.
    /// </summary>
    private static string ComputeMerkleRoot(IReadOnlyList<string> hashes)
    {
        if (hashes.Count == 0)
            throw new ArgumentException("Cannot compute Merkle root of empty hash list");

        if (hashes.Count == 1)
            return hashes[0];

        var currentLevel = new List<string>(hashes);

        while (currentLevel.Count > 1)
        {
            var nextLevel = new List<string>();

            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                if (i + 1 < currentLevel.Count)
                {
                    var combined = currentLevel[i] + currentLevel[i + 1];
                    nextLevel.Add(ComputeSha256Hex(combined));
                }
                else
                {
                    nextLevel.Add(currentLevel[i]);
                }
            }

            currentLevel = nextLevel;
        }

        return currentLevel[0];
    }

    #endregion
}
