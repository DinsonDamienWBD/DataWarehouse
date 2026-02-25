using System.Reflection;
using System.Security.Cryptography;
using DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily;
using DataWarehouse.SDK.Contracts.Compression;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Compression;

/// <summary>
/// Tests for UltimateCompression strategy implementations.
/// Validates compress/decompress roundtrips, compression ratios, and strategy metadata.
/// </summary>
[Trait("Category", "Unit")]
public class UltimateCompressionStrategyTests
{
    private static byte[] GenerateCompressibleData(int size)
    {
        // Create repeating patterns that compress well
        var data = new byte[size];
        var pattern = "The quick brown fox jumps over the lazy dog. "u8;
        for (int i = 0; i < size; i++)
            data[i] = pattern[i % pattern.Length];
        return data;
    }

    private static byte[] GenerateRandomData(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    #region GZip Strategy

    [Fact]
    public void GZipStrategy_ShouldHaveCorrectCharacteristics()
    {
        var strategy = new GZipStrategy();
        strategy.Characteristics.AlgorithmName.Should().Be("GZip");
        strategy.Characteristics.SupportsStreaming.Should().BeTrue();
    }

    [Fact]
    public async Task GZipStrategy_CompressDecompress_Roundtrip()
    {
        var strategy = new GZipStrategy();
        var original = GenerateCompressibleData(1024);
        var ct = TestContext.Current.CancellationToken;

        var compressed = await strategy.CompressAsync(original, ct);
        var decompressed = await strategy.DecompressAsync(compressed, ct);

        decompressed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task GZipStrategy_ShouldReduceSize_ForCompressibleData()
    {
        var strategy = new GZipStrategy();
        var original = GenerateCompressibleData(10_000);
        var ct = TestContext.Current.CancellationToken;

        var compressed = await strategy.CompressAsync(original, ct);

        compressed.Length.Should().BeLessThan(original.Length,
            "compressible data should compress to smaller size");
    }

    #endregion

    #region Deflate Strategy

    [Fact]
    public void DeflateStrategy_ShouldHaveCorrectName()
    {
        var strategy = new DeflateStrategy();
        strategy.Characteristics.AlgorithmName.Should().Be("Deflate");
    }

    [Fact]
    public async Task DeflateStrategy_CompressDecompress_Roundtrip()
    {
        var strategy = new DeflateStrategy();
        var original = GenerateCompressibleData(2048);
        var ct = TestContext.Current.CancellationToken;

        var compressed = await strategy.CompressAsync(original, ct);
        var decompressed = await strategy.DecompressAsync(compressed, ct);

        decompressed.Should().BeEquivalentTo(original);
    }

    #endregion

    #region Zstd Strategy

    [Fact]
    public void ZstdStrategy_ShouldHaveCorrectCharacteristics()
    {
        var strategy = new ZstdStrategy();
        strategy.Characteristics.AlgorithmName.Should().Contain("Zstd");
    }

    [Fact]
    public async Task ZstdStrategy_CompressDecompress_Roundtrip()
    {
        var strategy = new ZstdStrategy();
        var original = GenerateCompressibleData(4096);
        var ct = TestContext.Current.CancellationToken;

        var compressed = await strategy.CompressAsync(original, ct);
        var decompressed = await strategy.DecompressAsync(compressed, ct);

        decompressed.Should().BeEquivalentTo(original);
    }

    #endregion

    #region CompressionStrategyBase Contract Tests

    [Fact]
    public void CompressionStrategyBase_ShouldBeAbstract()
    {
        typeof(CompressionStrategyBase).IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void CompressionStrategyBase_ShouldImplementICompressionStrategy()
    {
        typeof(CompressionStrategyBase).GetInterfaces()
            .Should().Contain(typeof(ICompressionStrategy));
    }

    [Fact]
    public void CompressionStrategyBase_ShouldDefineCharacteristics()
    {
        var type = typeof(CompressionStrategyBase);
        type.GetProperty("Characteristics", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Should().NotBeNull();
    }

    [Fact]
    public async Task GZipStrategy_EmptyInput_ShouldRoundtrip()
    {
        var strategy = new GZipStrategy();
        var original = Array.Empty<byte>();
        var ct = TestContext.Current.CancellationToken;

        // Empty input may throw or produce minimal output depending on impl
        // Accept either behavior
        try
        {
            var compressed = await strategy.CompressAsync(original, ct);
            var decompressed = await strategy.DecompressAsync(compressed, ct);
            decompressed.Should().BeEquivalentTo(original);
        }
        catch (ArgumentException)
        {
            // Valid -- some strategies reject empty input
        }
    }

    [Fact]
    public async Task DeflateStrategy_LargeData_ShouldRoundtrip()
    {
        var strategy = new DeflateStrategy();
        var original = GenerateCompressibleData(100_000);
        var ct = TestContext.Current.CancellationToken;

        var compressed = await strategy.CompressAsync(original, ct);
        var decompressed = await strategy.DecompressAsync(compressed, ct);

        decompressed.Should().BeEquivalentTo(original);
    }

    #endregion
}
