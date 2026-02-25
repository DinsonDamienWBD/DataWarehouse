using System.Security.Cryptography;
using System.Text;
using DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily;
using DataWarehouse.Plugins.UltimateEncryption.Strategies.Aes;
using DataWarehouse.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// End-to-end tests for the read pipeline: retrieve → decrypt → decompress → verify.
/// Complements WritePipelineIntegrationTests with read-focused scenarios.
/// </summary>
[Trait("Category", "Integration")]
public class ReadPipelineIntegrationTests
{
    private readonly byte[] _encryptionKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task ReadPipeline_AfterWrite_ShouldRecoverOriginalData()
    {
        // Arrange
        var originalData = Encoding.UTF8.GetBytes("Read pipeline test data");
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/read-test.dat");

        // Write data
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Read Pipeline
        var retrieved = await storage.LoadAsync(uri);
        var retrievedBytes = new byte[retrieved.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await retrieved.ReadAsync(retrievedBytes);
#pragma warning restore CA2022
        var decrypted = await encryptionStrategy.DecryptAsync(retrievedBytes, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);
    }

    [Fact]
    public async Task ReadPipeline_MultipleReads_ShouldReturnSameData()
    {
        // Arrange
        var originalData = Encoding.UTF8.GetBytes("Consistency test");
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/consistency.dat");

        // Write once
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Read multiple times
        var results = new List<byte[]>();
        for (int i = 0; i < 3; i++)
        {
            var stream = await storage.LoadAsync(uri);
            var bytes = new byte[stream.Length];
#pragma warning disable CA2022 // Intentional full read in test
            await stream.ReadAsync(bytes);
#pragma warning restore CA2022
            var decrypted = await encryptionStrategy.DecryptAsync(bytes, _encryptionKey);
            var decompressed = await compressionStrategy.DecompressAsync(decrypted);
            results.Add(decompressed);
        }

        // Assert - All reads should return identical data
        results.Should().AllSatisfy(result => result.Should().BeEquivalentTo(originalData));
    }

    [Fact]
    public async Task ReadPipeline_NonExistentFile_ShouldThrow()
    {
        // Arrange
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/non-existent.dat");

        // Act & Assert
        var loadAction = async () => await storage.LoadAsync(uri);
        await loadAction.Should().ThrowAsync<Exception>("loading non-existent file should fail");
    }

    [Fact]
    public async Task ReadPipeline_CorruptedEncryptedData_ShouldFailAuthentication()
    {
        // Arrange
        var originalData = Encoding.UTF8.GetBytes("Data that will be corrupted");
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/corrupted.dat");

        // Write data
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);

        // Corrupt the encrypted data (flip a bit in the middle)
        if (encrypted.Length > 10)
        {
            encrypted[encrypted.Length / 2] ^= 0xFF;
        }

        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Try to decrypt corrupted data
        var stream = await storage.LoadAsync(uri);
        var bytes = new byte[stream.Length];
#pragma warning disable CA2022 // Intentional full read in test
        await stream.ReadAsync(bytes);
#pragma warning restore CA2022

        var decryptAction = async () => await encryptionStrategy.DecryptAsync(bytes, _encryptionKey);

        // Assert - AES-GCM should detect corruption via authentication tag
        await decryptAction.Should().ThrowAsync<Exception>("corrupted data should fail authentication");
    }

    [Fact]
    public async Task ReadPipeline_LargeFile_ShouldStreamEfficiently()
    {
        // Arrange - 5MB file
        var originalData = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/large-stream.dat");

        // Write
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);
        await storage.SaveAsync(uri, new MemoryStream(encrypted));

        // Act - Read in chunks
        var stream = await storage.LoadAsync(uri);
        var allBytes = new byte[stream.Length];
        int totalRead = 0;
        int chunkSize = 64 * 1024; // 64KB chunks
        var buffer = new byte[chunkSize];

        while (totalRead < allBytes.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, Math.Min(chunkSize, allBytes.Length - totalRead));
            if (bytesRead == 0) break;
            Array.Copy(buffer, 0, allBytes, totalRead, bytesRead);
            totalRead += bytesRead;
        }

        var decrypted = await encryptionStrategy.DecryptAsync(allBytes, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);
    }

    [Fact]
    public async Task ReadPipeline_Exists_ShouldDetectStoredFiles()
    {
        // Arrange
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var existingUri = new Uri("memory:///integration/existing.dat");
        var nonExistingUri = new Uri("memory:///integration/non-existing.dat");

        await storage.SaveAsync(existingUri, new MemoryStream(new byte[10]));

        // Act & Assert
        (await storage.ExistsAsync(existingUri)).Should().BeTrue();
        (await storage.ExistsAsync(nonExistingUri)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadPipeline_Delete_ShouldRemoveFile()
    {
        // Arrange
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("memory:///integration/to-delete.dat");
        await storage.SaveAsync(uri, new MemoryStream(new byte[10]));

        // Act
        await storage.DeleteAsync(uri);

        // Assert
        (await storage.ExistsAsync(uri)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadPipeline_CompressedSize_ShouldBeSmallerThanOriginal()
    {
        // Arrange - Compressible text data
        var originalText = string.Join("\n", Enumerable.Repeat("This is a test line that repeats.", 1000));
        var originalData = Encoding.UTF8.GetBytes(originalText);
        var compressionStrategy = new DeflateStrategy();

        // Act
        var compressed = await compressionStrategy.CompressAsync(originalData);

        // Assert
        compressed.Length.Should().BeLessThan(originalData.Length);
        var decompressed = await compressionStrategy.DecompressAsync(compressed);
        decompressed.Should().BeEquivalentTo(originalData);
    }

    [Fact]
    public async Task ReadPipeline_EncryptedSize_ShouldBeSlightlyLargerThanInput()
    {
        // Arrange
        var data = RandomNumberGenerator.GetBytes(1024);
        var encryptionStrategy = new AesGcmStrategy();

        // Act
        var encrypted = await encryptionStrategy.EncryptAsync(data, _encryptionKey);

        // Assert - AES-GCM adds nonce (12 bytes) + tag (16 bytes) = 28 bytes overhead minimum
        encrypted.Length.Should().BeGreaterThan(data.Length);
        encrypted.Length.Should().BeGreaterThanOrEqualTo(data.Length + 28); // At least nonce + tag
        encrypted.Length.Should().BeLessThan(data.Length + 100); // Reasonable upper bound
    }

    [Fact]
    public async Task ReadPipeline_OrderMatters_DecryptBeforeDecompress()
    {
        // Arrange
        var originalData = Encoding.UTF8.GetBytes("Order matters test");
        var compressionStrategy = new DeflateStrategy();
        var encryptionStrategy = new AesGcmStrategy();

        // Compress then encrypt (correct order)
        var compressed = await compressionStrategy.CompressAsync(originalData);
        var encrypted = await encryptionStrategy.EncryptAsync(compressed, _encryptionKey);

        // Act - Must decrypt before decompress
        var decrypted = await encryptionStrategy.DecryptAsync(encrypted, _encryptionKey);
        var decompressed = await compressionStrategy.DecompressAsync(decrypted);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);

        // Verify wrong order fails
        var wrongOrderAction = async () => await compressionStrategy.DecompressAsync(encrypted);
        await wrongOrderAction.Should().ThrowAsync<Exception>("decompressing encrypted data should fail");
    }
}
