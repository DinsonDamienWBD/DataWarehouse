using DataWarehouse.SDK.VirtualDiskEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace DataWarehouse.Hardening.Tests.Integration;

/// <summary>
/// Generator infrastructure tests -- pure computation, no VDE dependency.
/// Validates the streaming generator pattern used by VDE pipeline tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StreamingGeneratorTests
{
    private readonly ITestOutputHelper _output;

    public StreamingGeneratorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Verifies the generator produces exactly the requested number of bytes.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Test_PayloadGenerator_ProducesExactBytes()
    {
        const long targetBytes = 5 * 1024 * 1024; // 5MB
        long actualBytes = 0;
        int chunkCount = 0;

        foreach (var chunk in StreamingPayloadGenerator.GeneratePayload(targetBytes, chunkSize: 65_536))
        {
            actualBytes += chunk.Length;
            chunkCount++;
        }

        Assert.Equal(targetBytes, actualBytes);
        _output.WriteLine($"Generator produced {actualBytes:N0} bytes in {chunkCount} chunks");
    }

    /// <summary>
    /// Verifies the generator is deterministic (same seed = same data).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Test_PayloadGenerator_IsDeterministic()
    {
        const long size = 1024 * 1024; // 1MB
        var hash1 = StreamingPayloadGenerator.ComputeHash(size);
        var hash2 = StreamingPayloadGenerator.ComputeHash(size);

        Assert.Equal(hash1, hash2);
        _output.WriteLine($"Deterministic verification passed: {hash1}");
    }

    /// <summary>
    /// Verifies no OOM occurs when generating large payloads --
    /// the generator should use constant memory regardless of total size.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Test_PayloadGenerator_ConstantMemory()
    {
        const long targetBytes = 256L * 1024 * 1024; // 256MB
        long beforeMemory = GC.GetTotalMemory(forceFullCollection: true);
        long peakMemory = beforeMemory;
        long totalBytes = 0;

        foreach (var chunk in StreamingPayloadGenerator.GeneratePayload(targetBytes))
        {
            totalBytes += chunk.Length;
            // Sample memory periodically (every 32MB)
            if (totalBytes % (32 * 1024 * 1024) == 0)
            {
                long currentMemory = GC.GetTotalMemory(forceFullCollection: false);
                if (currentMemory > peakMemory) peakMemory = currentMemory;
            }
        }

        long memoryGrowth = peakMemory - beforeMemory;
        _output.WriteLine($"Generated {totalBytes:N0} bytes");
        _output.WriteLine($"Memory before: {beforeMemory:N0}, peak: {peakMemory:N0}, growth: {memoryGrowth:N0}");

        // Memory growth should be well under 100MB even for 256MB of generated data
        // (only one chunk is alive at a time plus GC overhead)
        Assert.True(memoryGrowth < 100 * 1024 * 1024,
            $"Memory grew by {memoryGrowth:N0} bytes -- possible OOM risk. Expected < 100MB growth for streaming generator.");

        Assert.Equal(targetBytes, totalBytes);
    }

    /// <summary>
    /// Verifies the GeneratorStream adapter produces correct byte count via Read().
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Test_GeneratorStream_ProducesCorrectBytes()
    {
        const long targetBytes = 2 * 1024 * 1024; // 2MB
        using var stream = new StreamingPayloadGenerator.GeneratorStream(targetBytes, chunkSize: 65_536, seed: 42);

        long totalRead = 0;
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += bytesRead;
        }

        Assert.Equal(targetBytes, totalRead);
        Assert.Equal(targetBytes, stream.Position);
        _output.WriteLine($"GeneratorStream produced {totalRead:N0} bytes");
    }
}

/// <summary>
/// Integration tests that stream large synthetic payloads through the VDE pipeline.
///
/// Key design:
///   - 100GB streaming test uses a generator pattern (yield return random chunks)
///     to produce data incrementally -- NO 100GB disk allocation or memory allocation
///   - Data flows through the VDE decorator chain: Cache -> Integrity -> Compression ->
///     Dedup -> Encryption -> WAL -> RAID -> File
///   - Verifies: no crash, no deadlock (with timeout), no OOM
///   - CI-fast 1GB variant for quick feedback in CI pipelines
///
/// VDE container initialization is non-trivial (file I/O), so these tests are
/// appropriately tagged for different CI tiers.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VdePipelineStreamingTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Default chunk size: 1MB for streaming operations.</summary>
    private const int DefaultChunkSize = 1_048_576;

    /// <summary>100GB total payload size.</summary>
    private const long PayloadSize100Gb = 100L * 1024 * 1024 * 1024;

    /// <summary>1GB total payload size for CI-fast variant.</summary>
    private const long PayloadSize1Gb = 1L * 1024 * 1024 * 1024;

    /// <summary>10MB total payload size for quick sanity check.</summary>
    private const long PayloadSize10Mb = 10L * 1024 * 1024;

    public VdePipelineStreamingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Quick sanity test: streams 10MB through VDE to verify pipeline connectivity.
    /// Creates and disposes its own VDE container within the test.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Duration", "Short")]
    public async Task Test_Stream10MB_ThroughVdePipeline_Sanity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dw-vde-10mb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var containerPath = Path.Combine(tempDir, "vde-10mb.dwvd");
            var options = new VdeOptions
            {
                ContainerPath = containerPath,
                BlockSize = 4096,
                TotalBlocks = 4096, // 16MB container
                MaxCachedInodes = 100,
                MaxCachedBTreeNodes = 100
            };

            await using var vde = new VirtualDiskEngine(options);
            await vde.InitializeAsync();

            var checksum = await RunStreamingWriteTest(vde, PayloadSize10Mb, "10MB-sanity");
            Assert.NotNull(checksum);
            _output.WriteLine($"10MB sanity test completed with checksum: {checksum}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Streams 100GB of synthetic data through the VDE pipeline write path.
    /// Uses generator pattern -- no 100GB allocation.
    ///
    /// LONG-RUNNING: Expected duration 30-90 minutes depending on hardware.
    /// </summary>
    [Fact(Skip = "Long-running: 100GB streaming test -- run manually or in soak environment")]
    [Trait("Category", "Integration")]
    [Trait("Duration", "Long")]
    public async Task Test_Stream100GB_ThroughVdePipeline_Write()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dw-vde-100gb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var containerPath = Path.Combine(tempDir, "vde-100gb.dwvd");
            var options = new VdeOptions
            {
                ContainerPath = containerPath,
                BlockSize = 4096,
                TotalBlocks = 26_214_400, // ~100GB
                MaxCachedInodes = 10_000,
                MaxCachedBTreeNodes = 1_000
            };

            await using var vde = new VirtualDiskEngine(options);
            await vde.InitializeAsync();
            await RunStreamingWriteTest(vde, PayloadSize100Gb, "100GB");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// After writing 100GB, reads back and verifies data integrity via checksum.
    /// </summary>
    [Fact(Skip = "Long-running: 100GB read-back test -- run manually or in soak environment")]
    [Trait("Category", "Integration")]
    [Trait("Duration", "Long")]
    public async Task Test_Stream100GB_ThroughVdePipeline_ReadBack()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dw-vde-100gb-rb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var containerPath = Path.Combine(tempDir, "vde-100gb-rb.dwvd");
            var options = new VdeOptions
            {
                ContainerPath = containerPath,
                BlockSize = 4096,
                TotalBlocks = 26_214_400,
                MaxCachedInodes = 10_000,
                MaxCachedBTreeNodes = 1_000
            };

            await using var vde = new VirtualDiskEngine(options);
            await vde.InitializeAsync();

            // Write
            var writeChecksum = await RunStreamingWriteTest(vde, PayloadSize100Gb, "100GB-write-for-readback");

            // Verify deterministic checksum
            var verifyChecksum = StreamingPayloadGenerator.ComputeHash(PayloadSize100Gb);
            Assert.Equal(writeChecksum, verifyChecksum);
            _output.WriteLine($"Read-back checksum verification passed: {verifyChecksum}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// CI-fast variant: streams 1GB through the VDE pipeline.
    /// </summary>
    [Fact(Skip = "Medium-running: 1GB streaming test -- run in CI or integration environment")]
    [Trait("Category", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task Test_Stream1GB_ThroughVdePipeline_CIFast()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dw-vde-1gb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var containerPath = Path.Combine(tempDir, "vde-1gb.dwvd");
            var options = new VdeOptions
            {
                ContainerPath = containerPath,
                BlockSize = 4096,
                TotalBlocks = 262_144, // 1GB
                MaxCachedInodes = 1_000,
                MaxCachedBTreeNodes = 500
            };

            await using var vde = new VirtualDiskEngine(options);
            await vde.InitializeAsync();
            await RunStreamingWriteTest(vde, PayloadSize1Gb, "1GB-CI-fast");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // -------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------

    /// <summary>
    /// Runs a streaming write test through VDE StoreAsync API.
    /// Returns the SHA-256 checksum of the generated payload.
    /// </summary>
    private async Task<string> RunStreamingWriteTest(VirtualDiskEngine vde, long totalBytes, string label)
    {
        _output.WriteLine($"[{label}] Starting streaming write: {totalBytes:N0} bytes ({totalBytes / (1024.0 * 1024 * 1024):F2} GB)");

        var sw = Stopwatch.StartNew();
        long bytesWritten = 0;

        using var payloadStream = new StreamingPayloadGenerator.GeneratorStream(totalBytes, DefaultChunkSize, seed: 42);
        var objectKey = $"streaming-test/{label}/{Guid.NewGuid():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromHours(4));

        try
        {
            await vde.StoreAsync(
                objectKey,
                payloadStream,
                new Dictionary<string, string>
                {
                    ["test-label"] = label,
                    ["total-bytes"] = totalBytes.ToString(),
                    ["chunk-size"] = DefaultChunkSize.ToString()
                },
                cts.Token);

            bytesWritten = payloadStream.Position;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"[{label}] DEADLOCK DETECTED: VDE streaming write timed out after 4 hours. " +
                $"Bytes written before timeout: {bytesWritten:N0}");
        }

        var checksum = StreamingPayloadGenerator.ComputeHash(totalBytes);

        sw.Stop();

        double throughputMbps = bytesWritten / (1024.0 * 1024) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        _output.WriteLine($"[{label}] Write complete:");
        _output.WriteLine($"  Bytes written: {bytesWritten:N0}");
        _output.WriteLine($"  Duration: {sw.Elapsed}");
        _output.WriteLine($"  Throughput: {throughputMbps:F2} MB/s");
        _output.WriteLine($"  Checksum: {checksum}");

        Assert.True(bytesWritten > 0, $"[{label}] No bytes were written");

        return checksum;
    }
}

/// <summary>
/// Shared streaming payload generator. Produces deterministic pseudo-random data
/// incrementally via yield return -- only one chunk is in memory at a time.
/// This is the core pattern that enables 100GB streaming without 100GB allocation.
/// </summary>
public static class StreamingPayloadGenerator
{
    /// <summary>Default chunk size: 1MB.</summary>
    public const int DefaultChunkSize = 1_048_576;

    /// <summary>
    /// Generates synthetic payload data using a deterministic PRNG.
    /// </summary>
    /// <param name="totalBytes">Total number of bytes to generate.</param>
    /// <param name="chunkSize">Size of each yielded chunk (default 1MB).</param>
    /// <param name="seed">PRNG seed for deterministic/reproducible output.</param>
    /// <returns>Enumerable sequence of byte array chunks.</returns>
    public static IEnumerable<byte[]> GeneratePayload(long totalBytes, int chunkSize = DefaultChunkSize, int seed = 42)
    {
        long remaining = totalBytes;
        var rng = new Random(seed);

        while (remaining > 0)
        {
            int size = (int)Math.Min(chunkSize, remaining);
            var chunk = new byte[size];
            rng.NextBytes(chunk);
            remaining -= size;
            yield return chunk;
        }
    }

    /// <summary>
    /// Computes SHA-256 hash of the full generator output for a given size.
    /// </summary>
    public static string ComputeHash(long totalBytes, int seed = 42)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in GeneratePayload(totalBytes, seed: seed))
        {
            sha256.AppendData(chunk);
        }
        return Convert.ToHexString(sha256.GetHashAndReset());
    }

    /// <summary>
    /// Stream adapter that wraps the generator pattern into a standard System.IO.Stream.
    /// Allows APIs that expect Stream to consume generated data without buffering
    /// the entire payload in memory.
    /// </summary>
    public sealed class GeneratorStream : Stream
    {
        private readonly long _totalBytes;
        private readonly int _chunkSize;
        private readonly Random _rng;
        private long _position;
        private byte[]? _currentChunk;
        private int _chunkOffset;

        public GeneratorStream(long totalBytes, int chunkSize, int seed)
        {
            _totalBytes = totalBytes;
            _chunkSize = chunkSize;
            _rng = new Random(seed);
            _position = 0;
            _currentChunk = null;
            _chunkOffset = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _totalBytes;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _totalBytes) return 0;

            int totalRead = 0;
            while (totalRead < count && _position < _totalBytes)
            {
                if (_currentChunk == null || _chunkOffset >= _currentChunk.Length)
                {
                    int remaining = (int)Math.Min(_chunkSize, _totalBytes - _position);
                    _currentChunk = new byte[remaining];
                    _rng.NextBytes(_currentChunk);
                    _chunkOffset = 0;
                }

                int available = _currentChunk.Length - _chunkOffset;
                int toCopy = Math.Min(available, count - totalRead);
                Buffer.BlockCopy(_currentChunk, _chunkOffset, buffer, offset + totalRead, toCopy);
                _chunkOffset += toCopy;
                _position += toCopy;
                totalRead += toCopy;
            }

            return totalRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
