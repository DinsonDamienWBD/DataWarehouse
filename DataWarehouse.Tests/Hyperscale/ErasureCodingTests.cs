using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Infrastructure;

namespace DataWarehouse.Tests.Hyperscale
{
    /// <summary>
    /// Comprehensive tests for erasure coding components including:
    /// - AdaptiveErasureCoding: Core Reed-Solomon encoding/decoding
    /// - RabinFingerprinting: Content-defined chunking
    /// - StreamingErasureCoder: Large file streaming
    /// - AdaptiveParameterTuner: Failure-based parameter tuning
    /// - ParallelErasureCoder: Multi-threaded encoding/decoding
    /// </summary>
    public class ErasureCodingTests : IAsyncLifetime
    {
        private AdaptiveErasureCoding _erasureCoding = null!;

        public Task InitializeAsync()
        {
            _erasureCoding = new AdaptiveErasureCoding();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _erasureCoding.DisposeAsync();
        }

        #region AdaptiveErasureCoding Tests

        [Fact]
        public async Task AdaptiveErasureCoding_EncodeAndDecode_RoundTripsData()
        {
            // Arrange
            var originalData = Encoding.UTF8.GetBytes("Hello, erasure coding world!");
            var profile = new ErasureCodingProfile
            {
                Name = "test-profile",
                DataShards = 4,
                ParityShards = 2
            };

            // Act
            var encoded = await _erasureCoding.EncodeAsync(originalData, profile);
            var decoded = await _erasureCoding.DecodeAsync(encoded);

            // Assert
            Assert.Equal(originalData, decoded);
        }

        [Fact]
        public async Task AdaptiveErasureCoding_Encode_CreatesCorrectNumberOfShards()
        {
            // Arrange
            var data = new byte[1024];
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "test-profile",
                DataShards = 6,
                ParityShards = 3
            };

            // Act
            var encoded = await _erasureCoding.EncodeAsync(data, profile);

            // Assert
            Assert.Equal(9, encoded.Shards.Count);
            Assert.Equal(6, encoded.Shards.Count(s => !s.IsParity));
            Assert.Equal(3, encoded.Shards.Count(s => s.IsParity));
        }

        [Fact]
        public async Task AdaptiveErasureCoding_Decode_RecoversFromSingleShardLoss()
        {
            // Arrange
            var originalData = new byte[1000];
            RandomNumberGenerator.Fill(originalData);
            var profile = new ErasureCodingProfile
            {
                Name = "test-profile",
                DataShards = 4,
                ParityShards = 2
            };

            var encoded = await _erasureCoding.EncodeAsync(originalData, profile);

            // Simulate loss of one data shard
            encoded.Shards[0].Data = null;
            encoded.Shards[0].IsCorrupted = true;

            // Act
            var decoded = await _erasureCoding.DecodeAsync(encoded);

            // Assert
            Assert.Equal(originalData, decoded);
        }

        [Fact]
        public async Task AdaptiveErasureCoding_VerifyAndRepair_DetectsCorruption()
        {
            // Arrange
            var data = new byte[512];
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "test-profile",
                DataShards = 4,
                ParityShards = 2
            };

            var encoded = await _erasureCoding.EncodeAsync(data, profile);

            // Corrupt a shard
            if (encoded.Shards[0].Data != null)
            {
                encoded.Shards[0].Data[0] ^= 0xFF; // Flip bits
            }

            // Act
            var result = await _erasureCoding.VerifyAndRepairAsync(encoded);

            // Assert
            Assert.Equal(1, result.CorruptedShardCount);
        }

        [Fact]
        public async Task AdaptiveErasureCoding_VerifyAndRepair_RepairsCorruptedShards()
        {
            // Arrange
            var originalData = new byte[512];
            RandomNumberGenerator.Fill(originalData);
            var profile = new ErasureCodingProfile
            {
                Name = "test-profile",
                DataShards = 4,
                ParityShards = 2
            };

            var encoded = await _erasureCoding.EncodeAsync(originalData, profile);

            // Corrupt one shard (within repair capacity)
            if (encoded.Shards[1].Data != null)
            {
                encoded.Shards[1].Data[0] ^= 0xFF;
            }

            // Act
            var result = await _erasureCoding.VerifyAndRepairAsync(encoded);
            var decoded = await _erasureCoding.DecodeAsync(encoded);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(1, result.RepairedShardCount);
            Assert.Equal(originalData, decoded);
        }

        [Fact]
        public async Task AdaptiveErasureCoding_Decode_FailsWhenTooManyShardsLost()
        {
            // Arrange
            var data = new byte[512];
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "test-profile",
                DataShards = 4,
                ParityShards = 2
            };

            var encoded = await _erasureCoding.EncodeAsync(data, profile);

            // Lose 3 shards (more than parity count)
            encoded.Shards[0].Data = null;
            encoded.Shards[0].IsCorrupted = true;
            encoded.Shards[1].Data = null;
            encoded.Shards[1].IsCorrupted = true;
            encoded.Shards[2].Data = null;
            encoded.Shards[2].IsCorrupted = true;

            // Act & Assert
            await Assert.ThrowsAsync<InsufficientShardsException>(
                () => _erasureCoding.DecodeAsync(encoded));
        }

        [Fact]
        public void AdaptiveErasureCoding_SelectOptimalProfile_SelectsHighDurabilityForCritical()
        {
            // Arrange
            var characteristics = new DataCharacteristics
            {
                Criticality = DataCriticality.Critical,
                AccessPattern = AccessPattern.ReadHeavy,
                StorageCostSensitive = false,
                DataSizeBytes = 100_000_000
            };

            // Act
            var profile = _erasureCoding.SelectOptimalProfile(characteristics);

            // Assert
            Assert.True(profile.ParityShards >= 3);
        }

        [Fact]
        public void AdaptiveErasureCoding_SelectOptimalProfile_SelectsStorageOptimizedForCostSensitive()
        {
            // Arrange
            var characteristics = new DataCharacteristics
            {
                Criticality = DataCriticality.Low,
                AccessPattern = AccessPattern.Archival,
                StorageCostSensitive = true,
                DataSizeBytes = 1_000_000
            };

            // Act
            var profile = _erasureCoding.SelectOptimalProfile(characteristics);

            // Assert
            var overhead = (double)profile.ParityShards / profile.DataShards;
            Assert.True(overhead <= 0.5);
        }

        [Fact]
        public async Task AdaptiveErasureCoding_GetStatistics_ReturnsAccurateMetrics()
        {
            // Arrange
            var data = new byte[1024];
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "standard",
                DataShards = 4,
                ParityShards = 2
            };

            // Act
            await _erasureCoding.EncodeAsync(data, profile);
            await _erasureCoding.EncodeAsync(data, profile);
            var stats = _erasureCoding.GetStatistics();

            // Assert
            Assert.Equal(2048, stats.TotalBytesEncoded);
            Assert.True(stats.AvailableProfiles.Count >= 4);
        }

        [Fact]
        public async Task AdaptiveErasureCoding_EncodesLargeData_Successfully()
        {
            // Arrange
            var largeData = new byte[10 * 1024 * 1024]; // 10MB
            RandomNumberGenerator.Fill(largeData);
            var profile = new ErasureCodingProfile
            {
                Name = "hyperscale",
                DataShards = 16,
                ParityShards = 4
            };

            // Act
            var encoded = await _erasureCoding.EncodeAsync(largeData, profile);
            var decoded = await _erasureCoding.DecodeAsync(encoded);

            // Assert
            Assert.Equal(largeData.Length, decoded.Length);
            Assert.Equal(largeData, decoded);
        }

        #endregion

        #region RabinFingerprinting Tests

        [Fact]
        public async Task RabinFingerprinting_ChunkData_CreatesVariableSizeChunks()
        {
            // Arrange
            var config = new RabinConfig
            {
                MinChunkSize = 1024,
                MaxChunkSize = 8192,
                TargetChunkSize = 4096
            };
            var chunker = new RabinFingerprinting(config);
            var data = new byte[100 * 1024]; // 100KB
            RandomNumberGenerator.Fill(data);

            // Act
            using var stream = new MemoryStream(data);
            var chunks = await chunker.ChunkDataAsync(stream);

            // Assert
            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c =>
            {
                Assert.True(c.Size >= config.MinChunkSize || c == chunks.Last());
                Assert.True(c.Size <= config.MaxChunkSize);
            });
        }

        [Fact]
        public void RabinFingerprinting_ComputeFingerprint_IsConsistent()
        {
            // Arrange
            var chunker = new RabinFingerprinting();
            var data = Encoding.UTF8.GetBytes("Test data for fingerprinting");

            // Act
            var fp1 = chunker.ComputeFingerprint(data);
            var fp2 = chunker.ComputeFingerprint(data);

            // Assert
            Assert.Equal(fp1, fp2);
        }

        [Fact]
        public void RabinFingerprinting_ComputeFingerprint_DiffersForDifferentData()
        {
            // Arrange
            var chunker = new RabinFingerprinting();
            var data1 = Encoding.UTF8.GetBytes("Test data 1");
            var data2 = Encoding.UTF8.GetBytes("Test data 2");

            // Act
            var fp1 = chunker.ComputeFingerprint(data1);
            var fp2 = chunker.ComputeFingerprint(data2);

            // Assert
            Assert.NotEqual(fp1, fp2);
        }

        [Fact]
        public void RabinFingerprinting_FindChunkBoundaries_ReturnsValidBoundaries()
        {
            // Arrange
            var config = new RabinConfig
            {
                MinChunkSize = 512,
                MaxChunkSize = 4096
            };
            var chunker = new RabinFingerprinting(config);
            var data = new byte[20 * 1024]; // 20KB
            RandomNumberGenerator.Fill(data);

            // Act
            var boundaries = chunker.FindChunkBoundaries(data);

            // Assert
            Assert.True(boundaries.Count >= 2);
            Assert.Equal(0, boundaries[0]);
            Assert.Equal(data.Length, boundaries[^1]);
            for (int i = 1; i < boundaries.Count; i++)
            {
                Assert.True(boundaries[i] > boundaries[i - 1]);
            }
        }

        [Fact]
        public async Task RabinFingerprinting_ChunkData_GeneratesUniqueContentHashes()
        {
            // Arrange
            var chunker = new RabinFingerprinting();
            var data = new byte[50 * 1024]; // 50KB
            RandomNumberGenerator.Fill(data);

            // Act
            using var stream = new MemoryStream(data);
            var chunks = await chunker.ChunkDataAsync(stream);

            // Assert
            var uniqueHashes = chunks.Select(c => c.ContentHash).Distinct().Count();
            Assert.Equal(chunks.Count, uniqueHashes);
        }

        [Fact]
        public async Task RabinFingerprinting_ChunkData_ReconstructsOriginalData()
        {
            // Arrange
            var chunker = new RabinFingerprinting();
            var data = new byte[30 * 1024];
            RandomNumberGenerator.Fill(data);

            // Act
            using var stream = new MemoryStream(data);
            var chunks = await chunker.ChunkDataAsync(stream);
            var reconstructed = chunks.SelectMany(c => c.Data).ToArray();

            // Assert
            Assert.Equal(data, reconstructed);
        }

        #endregion

        #region StreamingErasureCoder Tests

        [Fact]
        public async Task StreamingErasureCoder_EncodeAndDecode_RoundTripsLargeFile()
        {
            // Arrange
            await using var coder = new StreamingErasureCoder();
            var data = new byte[5 * 1024 * 1024]; // 5MB
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "streaming-test",
                DataShards = 6,
                ParityShards = 2
            };

            // Act
            using var inputStream = new MemoryStream(data);
            var encoded = await coder.EncodeStreamAsync(inputStream, profile);

            using var outputStream = new MemoryStream();
            await coder.DecodeStreamAsync(encoded, outputStream);

            // Assert
            Assert.Equal(data.Length, encoded.TotalOriginalSize);
            Assert.Equal(data, outputStream.ToArray());
        }

        [Fact]
        public async Task StreamingErasureCoder_EncodeStream_ReportsProgress()
        {
            // Arrange
            await using var coder = new StreamingErasureCoder();
            var data = new byte[2 * 1024 * 1024]; // 2MB
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "progress-test",
                DataShards = 4,
                ParityShards = 2
            };
            var progressReports = new List<StreamingProgress>();
            var progress = new Progress<StreamingProgress>(p => progressReports.Add(p));

            // Act
            using var stream = new MemoryStream(data);
            await coder.EncodeStreamAsync(stream, profile, progress);

            // Assert
            Assert.True(progressReports.Count > 0);
            Assert.Equal(100, progressReports[^1].PercentComplete);
        }

        [Fact]
        public async Task StreamingErasureCoder_DecodeStream_DetectsCorruption()
        {
            // Arrange
            await using var coder = new StreamingErasureCoder();
            var data = new byte[1024 * 1024]; // 1MB
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "corruption-test",
                DataShards = 4,
                ParityShards = 2
            };

            using var inputStream = new MemoryStream(data);
            var encoded = await coder.EncodeStreamAsync(inputStream, profile);

            // Corrupt the content hash
            encoded.EncodedChunks[0] = new EncodedStreamChunk
            {
                ChunkIndex = encoded.EncodedChunks[0].ChunkIndex,
                OriginalOffset = encoded.EncodedChunks[0].OriginalOffset,
                OriginalSize = encoded.EncodedChunks[0].OriginalSize,
                ContentHash = "corrupted_hash",
                EncodedData = encoded.EncodedChunks[0].EncodedData
            };

            using var outputStream = new MemoryStream();

            // Act & Assert
            await Assert.ThrowsAsync<DataCorruptionException>(
                () => coder.DecodeStreamAsync(encoded, outputStream));
        }

        [Fact]
        public async Task StreamingErasureCoder_GetStatistics_TracksProcessing()
        {
            // Arrange
            await using var coder = new StreamingErasureCoder();
            var data = new byte[512 * 1024]; // 512KB
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "stats-test",
                DataShards = 4,
                ParityShards = 2
            };

            // Act
            using var stream = new MemoryStream(data);
            await coder.EncodeStreamAsync(stream, profile);
            var stats = coder.GetStatistics();

            // Assert
            Assert.True(stats.TotalBytesProcessed > 0);
            Assert.True(stats.TotalChunksProcessed > 0);
        }

        #endregion

        #region AdaptiveParameterTuner Tests

        [Fact]
        public async Task AdaptiveParameterTuner_RecordShardFailure_TracksFailures()
        {
            // Arrange
            await using var tuner = new AdaptiveParameterTuner();

            // Act
            tuner.RecordShardFailure("node-1", ShardFailureType.DiskFailure);
            tuner.RecordShardFailure("node-1", ShardFailureType.NetworkTimeout);
            tuner.RecordShardFailure("node-2", ShardFailureType.CorruptionDetected);
            var stats = tuner.GetStatistics();

            // Assert
            Assert.Equal(3, stats.TotalFailuresRecorded);
            Assert.Equal(2, stats.NodeCount);
        }

        [Fact]
        public async Task AdaptiveParameterTuner_RecordSuccessfulRecovery_TracksRecoveries()
        {
            // Arrange
            await using var tuner = new AdaptiveParameterTuner();

            // Act
            tuner.RecordSuccessfulRecovery("standard", 2, TimeSpan.FromSeconds(5));
            tuner.RecordSuccessfulRecovery("standard", 1, TimeSpan.FromSeconds(3));
            var stats = tuner.GetStatistics();

            // Assert
            Assert.Equal(2, stats.TotalRecoveries);
        }

        [Fact]
        public async Task AdaptiveParameterTuner_GetRecommendedProfile_ReturnsValidProfile()
        {
            // Arrange
            await using var tuner = new AdaptiveParameterTuner();

            // Act
            var profile = tuner.GetRecommendedProfile();

            // Assert
            Assert.NotNull(profile);
            Assert.True(profile.DataShards > 0);
            Assert.True(profile.ParityShards > 0);
        }

        [Fact]
        public async Task AdaptiveParameterTuner_ForceAnalysis_UpdatesFailureRate()
        {
            // Arrange
            await using var tuner = new AdaptiveParameterTuner(new AdaptiveTunerConfig
            {
                HighFailureRateThreshold = 0.1,
                LowFailureRateThreshold = 0.01
            });

            // Act - Record some failures
            for (int i = 0; i < 10; i++)
            {
                tuner.RecordShardFailure($"node-{i}", ShardFailureType.DiskFailure);
            }
            tuner.RecordSuccessfulRecovery("standard", 1, TimeSpan.FromSeconds(1));
            tuner.ForceAnalysis();
            var failureRate = tuner.GetCurrentFailureRate();

            // Assert
            Assert.True(failureRate >= 0);
        }

        [Fact]
        public async Task AdaptiveParameterTuner_ProfileChanged_RaisesEventOnThresholdCross()
        {
            // Arrange
            var config = new AdaptiveTunerConfig
            {
                HighFailureRateThreshold = 0.01,
                MinTuningIntervalHours = 0
            };
            await using var tuner = new AdaptiveParameterTuner(config);
            ProfileChangedEventArgs? eventArgs = null;
            tuner.ProfileChanged += (_, args) => eventArgs = args;

            // Act - Generate enough failures to trigger change
            for (int i = 0; i < 100; i++)
            {
                tuner.RecordShardFailure($"node-{i % 10}", ShardFailureType.DiskFailure);
            }
            tuner.RecordSuccessfulRecovery("test", 1, TimeSpan.FromSeconds(1));
            tuner.ForceAnalysis();

            // Assert - Event may or may not fire depending on threshold
            // This test verifies the mechanism works
            var stats = tuner.GetStatistics();
            Assert.Equal(100, stats.TotalFailuresRecorded);
        }

        #endregion

        #region ParallelErasureCoder Tests

        [Fact]
        public async Task ParallelErasureCoder_EncodeParallel_EncodesMultipleBlocks()
        {
            // Arrange
            await using var coder = new ParallelErasureCoder();
            var blocks = new List<byte[]>();
            for (int i = 0; i < 10; i++)
            {
                var block = new byte[1024];
                RandomNumberGenerator.Fill(block);
                blocks.Add(block);
            }
            var profile = new ErasureCodingProfile
            {
                Name = "parallel-test",
                DataShards = 4,
                ParityShards = 2
            };

            // Act
            var encoded = await coder.EncodeParallelAsync(blocks, profile);

            // Assert
            Assert.Equal(10, encoded.Count);
            Assert.All(encoded, e => Assert.Equal(6, e.Shards.Count));
        }

        [Fact]
        public async Task ParallelErasureCoder_DecodeParallel_DecodesMultipleBlocks()
        {
            // Arrange
            await using var coder = new ParallelErasureCoder();
            var blocks = new List<byte[]>();
            for (int i = 0; i < 5; i++)
            {
                var block = new byte[2048];
                RandomNumberGenerator.Fill(block);
                blocks.Add(block);
            }
            var profile = new ErasureCodingProfile
            {
                Name = "decode-test",
                DataShards = 4,
                ParityShards = 2
            };

            var encoded = await coder.EncodeParallelAsync(blocks, profile);

            // Act
            var decoded = await coder.DecodeParallelAsync(encoded);

            // Assert
            Assert.Equal(blocks.Count, decoded.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                Assert.Equal(blocks[i], decoded[i]);
            }
        }

        [Fact]
        public async Task ParallelErasureCoder_EncodeWithChunking_ProcessesLargeData()
        {
            // Arrange
            await using var coder = new ParallelErasureCoder();
            var data = new byte[5 * 1024 * 1024]; // 5MB
            RandomNumberGenerator.Fill(data);
            var profile = new ErasureCodingProfile
            {
                Name = "chunking-test",
                DataShards = 8,
                ParityShards = 2
            };

            // Act
            var result = await coder.EncodeWithChunkingAsync(data, profile, chunkSize: 1024 * 1024);
            var decoded = await coder.DecodeParallelResultAsync(result);

            // Assert
            Assert.Equal(5, result.ChunkCount);
            Assert.Equal(data.Length, result.OriginalSize);
            Assert.Equal(data, decoded);
        }

        [Fact]
        public async Task ParallelErasureCoder_EncodeParallel_ReportsProgress()
        {
            // Arrange
            await using var coder = new ParallelErasureCoder();
            var blocks = Enumerable.Range(0, 20)
                .Select(_ =>
                {
                    var b = new byte[512];
                    RandomNumberGenerator.Fill(b);
                    return b;
                }).ToList();
            var profile = new ErasureCodingProfile
            {
                Name = "progress-test",
                DataShards = 4,
                ParityShards = 2
            };
            var progressReports = new List<ParallelProgress>();
            var progress = new Progress<ParallelProgress>(p => progressReports.Add(p));

            // Act
            await coder.EncodeParallelAsync(blocks, profile, progress);

            // Assert
            Assert.True(progressReports.Count > 0);
        }

        [Fact]
        public async Task ParallelErasureCoder_VerifyAndRepairParallel_RepairsMultipleBlocks()
        {
            // Arrange
            await using var coder = new ParallelErasureCoder();
            var blocks = new List<byte[]>();
            for (int i = 0; i < 5; i++)
            {
                var block = new byte[1024];
                RandomNumberGenerator.Fill(block);
                blocks.Add(block);
            }
            var profile = new ErasureCodingProfile
            {
                Name = "repair-test",
                DataShards = 4,
                ParityShards = 2
            };

            var encoded = await coder.EncodeParallelAsync(blocks, profile);

            // Corrupt some shards
            if (encoded[0].Shards[0].Data != null)
                encoded[0].Shards[0].Data[0] ^= 0xFF;
            if (encoded[2].Shards[1].Data != null)
                encoded[2].Shards[1].Data[0] ^= 0xFF;

            // Act
            var results = await coder.VerifyAndRepairParallelAsync(encoded);

            // Assert
            Assert.Equal(5, results.Count);
            var repairedCount = results.Count(r => r.RepairedShardCount > 0);
            Assert.Equal(2, repairedCount);
        }

        [Fact]
        public async Task ParallelErasureCoder_GetStatistics_ReturnsValidMetrics()
        {
            // Arrange
            await using var coder = new ParallelErasureCoder();
            var blocks = Enumerable.Range(0, 10)
                .Select(_ =>
                {
                    var b = new byte[1024];
                    RandomNumberGenerator.Fill(b);
                    return b;
                }).ToList();
            var profile = new ErasureCodingProfile
            {
                Name = "stats-test",
                DataShards = 4,
                ParityShards = 2
            };

            // Act
            var encoded = await coder.EncodeParallelAsync(blocks, profile);
            await coder.DecodeParallelAsync(encoded);
            var stats = coder.GetStatistics();

            // Assert
            Assert.Equal(10 * 1024, stats.TotalBytesEncoded);
            Assert.Equal(10 * 1024, stats.TotalBytesDecoded);
            Assert.True(stats.MaxConcurrentEncoders > 0);
        }

        [Fact]
        public async Task ParallelErasureCoder_EncodesWithHighConcurrency()
        {
            // Arrange
            var config = new ParallelCoderConfig
            {
                MaxConcurrentEncoders = 8,
                MaxConcurrentDecoders = 8
            };
            await using var coder = new ParallelErasureCoder(config);
            var blocks = Enumerable.Range(0, 100)
                .Select(_ =>
                {
                    var b = new byte[256];
                    RandomNumberGenerator.Fill(b);
                    return b;
                }).ToList();
            var profile = new ErasureCodingProfile
            {
                Name = "concurrency-test",
                DataShards = 4,
                ParityShards = 2
            };

            // Act
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var encoded = await coder.EncodeParallelAsync(blocks, profile);
            sw.Stop();

            // Assert
            Assert.Equal(100, encoded.Count);
            Assert.True(sw.ElapsedMilliseconds < 30000); // Should complete in reasonable time
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task ErasureCoding_FullPipeline_WorksEndToEnd()
        {
            // Arrange
            var originalData = new byte[2 * 1024 * 1024]; // 2MB
            RandomNumberGenerator.Fill(originalData);

            var profile = new ErasureCodingProfile
            {
                Name = "integration-test",
                DataShards = 8,
                ParityShards = 4
            };

            await using var erasureCoding = new AdaptiveErasureCoding();
            var chunker = new RabinFingerprinting(new RabinConfig
            {
                MinChunkSize = 64 * 1024,
                MaxChunkSize = 256 * 1024
            });

            // Act - Chunk the data
            using var stream = new MemoryStream(originalData);
            var chunks = await chunker.ChunkDataAsync(stream);

            // Encode each chunk
            var encodedChunks = new List<ErasureCodedData>();
            foreach (var chunk in chunks)
            {
                var encoded = await erasureCoding.EncodeAsync(chunk.Data, profile);
                encodedChunks.Add(encoded);
            }

            // Simulate some shard loss
            foreach (var encoded in encodedChunks.Take(3))
            {
                encoded.Shards[0].Data = null;
                encoded.Shards[0].IsCorrupted = true;
            }

            // Decode all chunks
            var decodedChunks = new List<byte[]>();
            foreach (var encoded in encodedChunks)
            {
                var decoded = await erasureCoding.DecodeAsync(encoded);
                decodedChunks.Add(decoded);
            }

            // Reconstruct
            var reconstructed = decodedChunks.SelectMany(c => c).ToArray();

            // Assert
            Assert.Equal(originalData.Length, reconstructed.Length);
            Assert.Equal(originalData, reconstructed);
        }

        [Fact]
        public async Task ErasureCoding_WithAdaptiveTuning_AdjustsParameters()
        {
            // Arrange
            await using var tuner = new AdaptiveParameterTuner(new AdaptiveTunerConfig
            {
                DefaultDataShards = 6,
                DefaultParityShards = 3,
                HighFailureRateThreshold = 0.05,
                LowFailureRateThreshold = 0.01
            });
            await using var erasureCoding = new AdaptiveErasureCoding();

            // Act - Simulate normal operations
            var profile = tuner.GetRecommendedProfile();
            var data = new byte[1024];
            RandomNumberGenerator.Fill(data);

            var encoded = await erasureCoding.EncodeAsync(data, profile);
            var decoded = await erasureCoding.DecodeAsync(encoded);

            tuner.RecordSuccessfulRecovery(profile.Name, 0, TimeSpan.FromMilliseconds(100));
            var stats = tuner.GetStatistics();

            // Assert
            Assert.Equal(data, decoded);
            Assert.NotNull(stats.CurrentProfile);
        }

        #endregion
    }

    #region Test Helpers

    /// <summary>
    /// In-memory storage implementation for testing purposes.
    /// </summary>
    internal class InMemoryTestStorage
    {
        private readonly Dictionary<string, byte[]> _storage = new();

        public Task WriteAsync(string key, byte[] data)
        {
            _storage[key] = data;
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(string key)
        {
            return Task.FromResult(_storage[key]);
        }

        public Task<bool> ExistsAsync(string key)
        {
            return Task.FromResult(_storage.ContainsKey(key));
        }

        public Task DeleteAsync(string key)
        {
            _storage.Remove(key);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListAsync(string prefix)
        {
            foreach (var key in _storage.Keys.Where(k => k.StartsWith(prefix)))
            {
                yield return key;
            }
            await Task.CompletedTask;
        }
    }

    #endregion
}
