using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily;
using DataWarehouse.Plugins.UltimateEncryption.Strategies.Aes;
using DataWarehouse.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// End-to-end tests for the write pipeline: data → compress → encrypt → store → verify.
/// Uses real implementations with in-memory storage.
/// </summary>
[Trait("Category", "Integration")]
public class WritePipelineIntegrationTests
{
    private readonly byte[] _encryptionKey = RandomNumberGenerator.GetBytes(32); // AES-256

    [Fact]
    public async Task WritePipeline_SmallData_ShouldRoundTrip()
    {
        // Arrange
        var originalData = Encoding.UTF8.GetBytes("Hello DataWarehouse!");
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/small-data.dat");

        // Act - Write Pipeline
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Verify Storage
        (await storage.ExistsAsync(uri)).Should().BeTrue();

        // Act - Read Pipeline
        var loadedStream = await storage.LoadAsync(uri);
        var loadedData = new byte[loadedStream.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await loadedStream.ReadAsync(loadedData);
#pragma warning restore CA2022
        var decrypted = await encryptionStrategy.DecryptAsync(loadedData, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);
        Encoding.UTF8.GetString(decompressed).Should().Be("Hello DataWarehouse!");
    }

    [Fact]
    public async Task WritePipeline_LargeData_ShouldRoundTrip()
    {
        // Arrange - 1MB of random data
        var originalData = RandomNumberGenerator.GetBytes(1024 * 1024);
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/large-data.dat");

        // Act - Write Pipeline
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Read Pipeline
        var loadedStream = await storage.LoadAsync(uri);
        var loadedData = new byte[loadedStream.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await loadedStream.ReadAsync(loadedData);
#pragma warning restore CA2022
        var decrypted = await encryptionStrategy.DecryptAsync(loadedData, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);
        decompressed.Length.Should().Be(1024 * 1024);
    }

    [Fact]
    public async Task WritePipeline_EmptyData_ShouldThrowOnCompression()
    {
        // Arrange
        var originalData = Array.Empty<byte>();
        var compressionStrategy = new DeflateStrategy();

        // Act & Assert - Compression strategy doesn't allow empty input
        var compressAction = async () => await compressionStrategy.CompressAsync(originalData);
        await compressAction.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WritePipeline_BinaryData_ShouldRoundTrip()
    {
        // Arrange - Binary data with all byte values 0-255
        var originalData = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/binary-data.dat");

        // Act - Write Pipeline
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Read Pipeline
        var loadedStream = await storage.LoadAsync(uri);
        var loadedData = new byte[loadedStream.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await loadedStream.ReadAsync(loadedData);
#pragma warning restore CA2022
        var decrypted = await encryptionStrategy.DecryptAsync(loadedData, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);
        decompressed.Should().HaveCount(256);
    }

    [Fact]
    public async Task WritePipeline_UnicodeData_ShouldRoundTrip()
    {
        // Arrange - Unicode data with various scripts
        var originalText = "Hello 世界 🌍 Привет مرحبا";
        var originalData = Encoding.UTF8.GetBytes(originalText);
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/unicode-data.dat");

        // Act - Write Pipeline
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Read Pipeline
        var loadedStream = await storage.LoadAsync(uri);
        var loadedData = new byte[loadedStream.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await loadedStream.ReadAsync(loadedData);
#pragma warning restore CA2022
        var decrypted = await encryptionStrategy.DecryptAsync(loadedData, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);
        Encoding.UTF8.GetString(decompressed).Should().Be(originalText);
    }

    [Fact]
    public async Task WritePipeline_MultipleFiles_ShouldMaintainIndependence()
    {
        // Arrange
        var data1 = Encoding.UTF8.GetBytes("File 1 content");
        var data2 = Encoding.UTF8.GetBytes("File 2 content");
        var data3 = Encoding.UTF8.GetBytes("File 3 content");
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();

        var uri1 = new Uri("memory:///integration/file1.dat");
        var uri2 = new Uri("memory:///integration/file2.dat");
        var uri3 = new Uri("memory:///integration/file3.dat");

        // Act - Write all files
        foreach (var (data, uri) in new[] { (data1, uri1), (data2, uri2), (data3, uri3) })
        {
            var compressed = await compressionStrategy.CompressAsync(data);
            var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
            await storage.SaveAsync(uri, new MemoryStream(encrypted));
        }

        // Act - Read file 2
        var loadedStream = await storage.LoadAsync(uri2);
        var loadedData = new byte[loadedStream.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await loadedStream.ReadAsync(loadedData);
#pragma warning restore CA2022
        var decrypted = await encryptionStrategy.DecryptAsync(loadedData, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert - Verify file 2 has correct content
        Encoding.UTF8.GetString(decompressed).Should().Be("File 2 content");
    }

    [Fact]
    public async Task WritePipeline_CompressionSavesSpace_ForRepetitiveData()
    {
        // Arrange - Highly compressible data
        var originalData = Encoding.UTF8.GetBytes(new string('A', 10000));
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();

        // Act
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);

        // Assert - Compressed size should be much smaller than original
        compressed.Length.Should().BeLessThan(originalData.Length / 10, "compression should achieve >10:1 ratio on repetitive data");
        encrypted.Length.Should().BeLessThan(originalData.Length, "encrypted compressed data should still be smaller than original");
    }

    [Fact]
    public async Task WritePipeline_WrongDecryptionKey_ShouldFail()
    {
        // Arrange
        var originalData = Encoding.UTF8.GetBytes("Secret data");
        var correctKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/secret.dat");

        // Act - Write with correct key
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, correctKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Try to decrypt with wrong key
        var loadedStream = await storage.LoadAsync(uri);
        var loadedData = new byte[loadedStream.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await loadedStream.ReadAsync(loadedData);
#pragma warning restore CA2022

        // Assert - Decryption should fail
        var decryptAction = async () => await encryptionStrategy.DecryptAsync(loadedData, wrongKey);
        await decryptAction.Should().ThrowAsync<Exception>("wrong key should fail authentication");
    }
}
