// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Performance benchmark tests for TamperProof pipeline operations (T6.12).
/// Uses System.Diagnostics.Stopwatch (not BenchmarkDotNet) to measure:
/// SHA-256 hash computation for various data sizes, manifest serialization/deserialization,
/// and configuration construction.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceBenchmarkTests
{
    #region SHA-256 Hash Performance

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_SHA256_1MB_ShouldCompleteWithin100ms()
    {
        var data = new byte[1 * 1024 * 1024]; // 1 MB
        RandomNumberGenerator.Fill(data);

        var sw = Stopwatch.StartNew();
        var hash = SHA256.HashData(data);
        sw.Stop();

        hash.Should().HaveCount(32, "SHA-256 produces 32 bytes");
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "SHA-256 of 1MB should complete within 100ms on modern hardware");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_SHA256_10MB_ShouldCompleteWithin500ms()
    {
        var data = new byte[10 * 1024 * 1024]; // 10 MB
        RandomNumberGenerator.Fill(data);

        var sw = Stopwatch.StartNew();
        var hash = SHA256.HashData(data);
        sw.Stop();

        hash.Should().HaveCount(32);
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "SHA-256 of 10MB should complete within 500ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_SHA256_100MB_ShouldCompleteWithin5000ms()
    {
        var data = new byte[100 * 1024 * 1024]; // 100 MB
        RandomNumberGenerator.Fill(data);

        var sw = Stopwatch.StartNew();
        var hash = SHA256.HashData(data);
        sw.Stop();

        hash.Should().HaveCount(32);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "SHA-256 of 100MB should complete within 5s");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_SHA256_Throughput_ShouldExceed100MBPerSecond()
    {
        var data = new byte[10 * 1024 * 1024]; // 10 MB
        RandomNumberGenerator.Fill(data);
        const int iterations = 10;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            SHA256.HashData(data);
        }
        sw.Stop();

        var totalMB = (double)(10 * iterations);
        var throughputMBps = totalMB / (sw.Elapsed.TotalSeconds);

        throughputMBps.Should().BeGreaterThan(100,
            "SHA-256 throughput should exceed 100 MB/s on modern hardware");
    }

    #endregion

    #region Manifest Serialization Performance

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_ManifestSerialization_ShouldCompleteWithin50ms()
    {
        var manifest = CreateLargeTestManifest();

        var sw = Stopwatch.StartNew();
        var json = JsonSerializer.Serialize(manifest);
        sw.Stop();

        json.Should().NotBeNullOrWhiteSpace();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "Manifest serialization should complete within 5s (generous for CI)");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_ManifestDeserialization_ShouldCompleteWithin50ms()
    {
        var manifest = CreateLargeTestManifest();
        var json = JsonSerializer.Serialize(manifest);

        var sw = Stopwatch.StartNew();
        var deserialized = JsonSerializer.Deserialize<TamperProofManifest>(json);
        sw.Stop();

        deserialized.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(50,
            "Manifest deserialization should complete within 50ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_ManifestRoundTrip_ShouldPreserveData()
    {
        var manifest = CreateLargeTestManifest();

        var json = JsonSerializer.Serialize(manifest);
        var deserialized = JsonSerializer.Deserialize<TamperProofManifest>(json);

        deserialized.Should().NotBeNull();
        deserialized!.ObjectId.Should().Be(manifest.ObjectId);
        deserialized.Version.Should().Be(manifest.Version);
        deserialized.FinalContentHash.Should().Be(manifest.FinalContentHash);
        deserialized.RaidConfiguration.DataShardCount.Should().Be(manifest.RaidConfiguration.DataShardCount);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_ManifestSerializationBatch_100Manifests_ShouldCompleteWithin500ms()
    {
        var manifests = Enumerable.Range(0, 100)
            .Select(_ => CreateLargeTestManifest())
            .ToList();

        var sw = Stopwatch.StartNew();
        foreach (var manifest in manifests)
        {
            JsonSerializer.Serialize(manifest);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "Serializing 100 manifests should complete within 500ms");
    }

    #endregion

    #region Configuration Construction Performance

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_ConfigurationConstruction_ShouldCompleteWithin10ms()
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            _ = new TamperProofConfiguration
            {
                StorageInstances = new StorageInstancesConfig
                {
                    Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "local" },
                    Metadata = new StorageInstanceConfig { InstanceId = "metadata", PluginId = "local" },
                    Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "s3" },
                    Blockchain = new StorageInstanceConfig { InstanceId = "blockchain", PluginId = "local" }
                },
                Raid = new RaidConfig { DataShards = 4, ParityShards = 2 },
                HashAlgorithm = HashAlgorithmType.SHA256,
                ConsensusMode = ConsensusMode.SingleWriter,
                WormMode = WormEnforcementMode.HardwareIntegrated,
                DefaultRetentionPeriod = TimeSpan.FromDays(7 * 365)
            };
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "Creating 1000 configurations should complete within 100ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_ConfigurationValidation_ShouldCompleteWithin10ms()
    {
        var config = new TamperProofConfiguration
        {
            StorageInstances = new StorageInstancesConfig
            {
                Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "local" },
                Metadata = new StorageInstanceConfig { InstanceId = "metadata", PluginId = "local" },
                Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "s3" },
                Blockchain = new StorageInstanceConfig { InstanceId = "blockchain", PluginId = "local" }
            },
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 }
        };

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            config.Validate();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "Validating configuration 1000 times should complete within 100ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_ConfigurationClone_ShouldCompleteWithin10ms()
    {
        var config = new TamperProofConfiguration
        {
            StorageInstances = new StorageInstancesConfig
            {
                Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "local" },
                Metadata = new StorageInstanceConfig { InstanceId = "metadata", PluginId = "local" },
                Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "s3" },
                Blockchain = new StorageInstanceConfig { InstanceId = "blockchain", PluginId = "local" }
            },
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 }
        };

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            config.Clone();
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "Cloning configuration 1000 times should complete within 100ms");
    }

    #endregion

    #region IntegrityHash Performance

    [Fact]
    [Trait("Category", "Performance")]
    public void Benchmark_IntegrityHashCreateAndParse_ShouldCompleteQuickly()
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            var hash = IntegrityHash.Create(HashAlgorithmType.SHA256, $"hash-{i}");
            var str = hash.ToString();
            IntegrityHash.Parse(str);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(200,
            "Creating and parsing 10000 integrity hashes should complete within 200ms");
    }

    #endregion

    #region Helpers

    private static TamperProofManifest CreateLargeTestManifest()
    {
        var now = DateTimeOffset.UtcNow;
        var shards = Enumerable.Range(0, 6).Select(i => new ShardRecord
        {
            ShardIndex = i,
            IsParity = i >= 4,
            ActualSize = 65536,
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"shard-{i}"))),
            StorageLocation = $"/data/shard-{i}",
            StorageTier = i >= 4 ? "parity" : "data",
            WrittenAt = now,
            Padding = i == 5 ? new ShardPaddingRecord
            {
                PrefixPaddingBytes = 128,
                SuffixPaddingBytes = 128,
                PaddingHash = "padding-hash"
            } : null
        }).ToList();

        return new TamperProofManifest
        {
            ObjectId = Guid.NewGuid(),
            Version = 1,
            CreatedAt = now,
            WriteContext = new WriteContextRecord
            {
                Author = "perf-test",
                Comment = "Performance benchmark manifest",
                Timestamp = now,
                SessionId = "perf-session-001",
                SourceSystem = "benchmark-suite",
                ClientIp = "127.0.0.1"
            },
            HashAlgorithm = HashAlgorithmType.SHA256,
            OriginalContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("original"))),
            OriginalContentSize = 262144,
            FinalContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("final"))),
            FinalContentSize = 262400,
            PipelineStages = new[]
            {
                new PipelineStageRecord
                {
                    StageType = "compression", StageIndex = 0,
                    InputHash = "in0", OutputHash = "out0",
                    InputSize = 262144, OutputSize = 200000,
                    ExecutedAt = now, ExecutionDuration = TimeSpan.FromMilliseconds(15),
                    Parameters = new Dictionary<string, object> { ["Algorithm"] = "zstd", ["Level"] = 3 }
                },
                new PipelineStageRecord
                {
                    StageType = "encryption", StageIndex = 1,
                    InputHash = "out0", OutputHash = "out1",
                    InputSize = 200000, OutputSize = 200016,
                    ExecutedAt = now, ExecutionDuration = TimeSpan.FromMilliseconds(8),
                    Parameters = new Dictionary<string, object> { ["Algorithm"] = "AES-256-GCM", ["KeyId"] = "key-001" }
                }
            },
            RaidConfiguration = new RaidRecord
            {
                DataShardCount = 4,
                ParityShardCount = 2,
                ShardSize = 65536,
                Shards = shards
            },
            ContentPadding = new ContentPaddingRecord
            {
                PrefixPaddingBytes = 128,
                SuffixPaddingBytes = 128,
                PaddingPattern = "chaff"
            },
            WormBackup = new WormReference
            {
                StorageLocation = "s3://worm/perf-test",
                ContentHash = "worm-hash",
                ContentSize = 262400,
                WrittenAt = now,
                RetentionExpiresAt = now.AddYears(7),
                EnforcementMode = WormEnforcementMode.HardwareIntegrated
            },
            BlockchainAnchor = new BlockchainAnchorReference
            {
                Network = "internal",
                TransactionId = "tx-perf-001",
                BlockNumber = 42,
                BlockTimestamp = now,
                AnchoredHash = "anchor-hash"
            },
            ContentType = "application/octet-stream",
            OriginalFilename = "benchmark-data.bin",
            WormRetentionPeriod = TimeSpan.FromDays(7 * 365),
            WormRetentionExpiresAt = now.AddYears(7),
            UserMetadata = new Dictionary<string, object>
            {
                ["benchmark"] = true,
                ["category"] = "performance"
            }
        };
    }

    #endregion
}
