using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily;
using DataWarehouse.Plugins.UltimateEncryption.Strategies.Aes;
using FluentAssertions;
using Xunit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Tests.Performance;

/// <summary>
/// Performance baseline tests for critical operations.
/// All thresholds are generous (5-10x expected) to avoid CI flakiness.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceBaselineTests
{
    private static byte[] GenerateTestData(int sizeBytes)
    {
        var data = new byte[sizeBytes];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    [Fact]
    public void SHA256_1MB_ShouldCompleteWithin500ms()
    {
        var data = GenerateTestData(1024 * 1024);
        var sw = Stopwatch.StartNew();

        var hash = SHA256.HashData(data);

        sw.Stop();
        hash.Should().HaveCount(32);
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "SHA-256 of 1MB should complete well within 500ms");
    }

    [Fact]
    public async Task GZipCompression_1MB_ShouldCompleteWithin2000ms()
    {
        var strategy = new GZipStrategy();
        var data = GenerateTestData(1024 * 1024);
        var ct = TestContext.Current.CancellationToken;
        var sw = Stopwatch.StartNew();

        var compressed = await strategy.CompressAsync(data, ct);
        var decompressed = await strategy.DecompressAsync(compressed, ct);

        sw.Stop();
        decompressed.Length.Should().Be(data.Length);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "GZip roundtrip of 1MB should complete within 2s");
    }

    [Fact]
    public async Task AesGcmEncryption_1MB_ShouldCompleteWithin2000ms()
    {
        var strategy = new AesGcmStrategy();
        var key = RandomNumberGenerator.GetBytes(32);
        var data = GenerateTestData(1024 * 1024);
        var ct = TestContext.Current.CancellationToken;
        var sw = Stopwatch.StartNew();

        var encrypted = await strategy.EncryptAsync(data, key, cancellationToken: ct);
        var decrypted = await strategy.DecryptAsync(encrypted, key, cancellationToken: ct);

        sw.Stop();
        decrypted.Length.Should().Be(data.Length);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "AES-GCM roundtrip of 1MB should complete within 2s");
    }

    [Fact]
    public void JsonSerialization_10KObjects_ShouldCompleteWithin5000ms()
    {
        var objects = Enumerable.Range(0, 10_000)
            .Select(i => new TestSerializationObject
            {
                Id = i,
                Name = $"Object-{i}",
                Timestamp = DateTime.UtcNow,
                Tags = [$"tag-{i % 10}", $"group-{i % 5}"],
                Value = i * 3.14
            })
            .ToList();

        var sw = Stopwatch.StartNew();

        var json = JsonSerializer.Serialize(objects);
        var deserialized = JsonSerializer.Deserialize<List<TestSerializationObject>>(json);

        sw.Stop();
        deserialized.Should().HaveCount(10_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "JSON roundtrip of 10K objects should complete within 5s");
    }

    [Fact]
    public void ConcurrentDictionary_100KOps_ShouldCompleteWithin5000ms()
    {
        var dict = new BoundedDictionary<string, byte[]>(1000);
        var sw = Stopwatch.StartNew();

        // Write 100K entries
        Parallel.For(0, 100_000, i =>
        {
            dict[$"key-{i}"] = Encoding.UTF8.GetBytes($"value-{i}");
        });

        // Read 100K entries
        Parallel.For(0, 100_000, i =>
        {
            dict.TryGetValue($"key-{i}", out _);
        });

        sw.Stop();
        dict.Count.Should().Be(100_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "100K concurrent dictionary ops should complete within 5s");
    }

    [Fact]
    public void HMACSHA256_100KHashes_ShouldCompleteWithin5000ms()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var data = Encoding.UTF8.GetBytes("test-data-to-hash");
        var sw = Stopwatch.StartNew();

        using var hmac = new HMACSHA256(key);
        for (int i = 0; i < 100_000; i++)
        {
            hmac.ComputeHash(data);
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "100K HMAC-SHA256 computations should complete within 5s");
    }

    private record TestSerializationObject
    {
        public int Id { get; init; }
        public required string Name { get; init; }
        public DateTime Timestamp { get; init; }
        public required string[] Tags { get; init; }
        public double Value { get; init; }
    }
}
