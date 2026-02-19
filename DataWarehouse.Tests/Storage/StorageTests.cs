using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Storage
{
    /// <summary>
    /// Comprehensive storage operation tests.
    /// </summary>
    public class StorageOperationTests
    {
        #region Basic Storage Operations

        [Fact]
        public async Task Storage_WriteAndRead_RoundTripsData()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var testData = Encoding.UTF8.GetBytes("Hello, DataWarehouse!");
            var key = "test/document.txt";

            // Act
            await storage.WriteAsync(key, testData);
            var retrieved = await storage.ReadAsync(key);

            // Assert
            Assert.Equal(testData, retrieved);
        }

        [Fact]
        public async Task Storage_Exists_ReturnsTrueForExistingItem()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var key = "test/exists.txt";
            await storage.WriteAsync(key, new byte[] { 1, 2, 3 });

            // Act
            var exists = await storage.ExistsAsync(key);

            // Assert
            Assert.True(exists);
        }

        [Fact]
        public async Task Storage_Exists_ReturnsFalseForNonExistingItem()
        {
            // Arrange
            var storage = new InMemoryTestStorage();

            // Act
            var exists = await storage.ExistsAsync("nonexistent");

            // Assert
            Assert.False(exists);
        }

        [Fact]
        public async Task Storage_Delete_RemovesItem()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var key = "test/delete-me.txt";
            await storage.WriteAsync(key, new byte[] { 1, 2, 3 });

            // Act
            await storage.DeleteAsync(key);

            // Assert
            Assert.False(await storage.ExistsAsync(key));
        }

        [Fact]
        public async Task Storage_List_ReturnsAllItems()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            await storage.WriteAsync("prefix/file1.txt", new byte[] { 1 });
            await storage.WriteAsync("prefix/file2.txt", new byte[] { 2 });
            await storage.WriteAsync("other/file3.txt", new byte[] { 3 });

            // Act
            var items = await storage.ListAsync("prefix/").ToListAsync();

            // Assert
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.StartsWith("prefix/", i));
        }

        #endregion

        #region Concurrent Operations

        [Fact]
        public async Task Storage_ConcurrentWrites_AreThreadSafe()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var tasks = new List<Task>();

            // Act - write 100 items concurrently
            for (int i = 0; i < 100; i++)
            {
                var key = $"concurrent/item-{i}";
                var data = Encoding.UTF8.GetBytes($"Data {i}");
                tasks.Add(storage.WriteAsync(key, data));
            }
            await Task.WhenAll(tasks);

            // Assert - all items should exist
            for (int i = 0; i < 100; i++)
            {
                Assert.True(await storage.ExistsAsync($"concurrent/item-{i}"));
            }
        }

        [Fact]
        public async Task Storage_ConcurrentReadsAndWrites_AreThreadSafe()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var key = "concurrent/shared";
            await storage.WriteAsync(key, new byte[] { 0 });

            var tasks = new List<Task>();
            var readResults = new List<byte[]>();
            var syncLock = new object();

            // Act - concurrent reads and writes
            for (int i = 0; i < 50; i++)
            {
                // Write task
                var writeValue = (byte)i;
                tasks.Add(storage.WriteAsync(key, new[] { writeValue }));

                // Read task
                tasks.Add(Task.Run(async () =>
                {
                    var data = await storage.ReadAsync(key);
                    lock (syncLock)
                    {
                        readResults.Add(data);
                    }
                }));
            }
            await Task.WhenAll(tasks);

            // Assert - all operations completed without exception
            Assert.Equal(50, readResults.Count);
        }

        #endregion

        #region Large Data Handling

        [Fact]
        public async Task Storage_LargeData_HandlesEfficiently()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var largeData = new byte[10 * 1024 * 1024]; // 10MB
            RandomNumberGenerator.Fill(largeData);
            var key = "large/bigfile.bin";

            // Act
            var writeStart = DateTime.UtcNow;
            await storage.WriteAsync(key, largeData);
            var writeTime = DateTime.UtcNow - writeStart;

            var readStart = DateTime.UtcNow;
            var retrieved = await storage.ReadAsync(key);
            var readTime = DateTime.UtcNow - readStart;

            // Assert
            Assert.Equal(largeData, retrieved);
            Assert.True(writeTime.TotalSeconds < 5, "Write took too long");
            Assert.True(readTime.TotalSeconds < 5, "Read took too long");
        }

        [Fact]
        public async Task Storage_StreamingRead_WorksCorrectly()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var data = new byte[1024 * 1024]; // 1MB
            RandomNumberGenerator.Fill(data);
            await storage.WriteAsync("stream/test.bin", data);

            // Act
            using var stream = await storage.OpenReadStreamAsync("stream/test.bin");
            using var ms = new MemoryStream(data.Length);
            await stream.CopyToAsync(ms);

            // Assert
            Assert.Equal(data, ms.ToArray());
        }

        #endregion

        #region Error Handling

        [Fact]
        public async Task Storage_ReadNonExistent_ThrowsException()
        {
            // Arrange
            var storage = new InMemoryTestStorage();

            // Act & Assert
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                storage.ReadAsync("nonexistent/path"));
        }

        [Fact]
        public async Task Storage_DeleteNonExistent_DoesNotThrow()
        {
            // Arrange
            var storage = new InMemoryTestStorage();

            // Act
            await storage.DeleteAsync("nonexistent/path");

            // Assert - delete completed without throwing, storage remains functional
            var exists = await storage.ExistsAsync("nonexistent/path");
            exists.Should().BeFalse("non-existent path should still not exist after delete");
        }

        #endregion

        #region Metadata Operations

        [Fact]
        public async Task Storage_GetMetadata_ReturnsCorrectInfo()
        {
            // Arrange
            var storage = new InMemoryTestStorage();
            var data = new byte[1024];
            RandomNumberGenerator.Fill(data);
            var key = "metadata/test.bin";
            await storage.WriteAsync(key, data);

            // Act
            var metadata = await storage.GetMetadataAsync(key);

            // Assert
            Assert.Equal(1024, metadata.Size);
            Assert.True(metadata.LastModified <= DateTimeOffset.UtcNow);
            Assert.True(metadata.LastModified > DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        #endregion
    }

    /// <summary>
    /// Tests for storage checksums and integrity.
    /// </summary>
    public class StorageIntegrityTests
    {
        [Fact]
        public void Checksum_ComputesConsistently()
        {
            var data = Encoding.UTF8.GetBytes("Test data for checksum");

            var checksum1 = ComputeChecksum(data);
            var checksum2 = ComputeChecksum(data);

            Assert.Equal(checksum1, checksum2);
        }

        [Fact]
        public void Checksum_DetectsModification()
        {
            var original = Encoding.UTF8.GetBytes("Original data");
            var modified = Encoding.UTF8.GetBytes("Modified data");

            var checksum1 = ComputeChecksum(original);
            var checksum2 = ComputeChecksum(modified);

            Assert.NotEqual(checksum1, checksum2);
        }

        [Fact]
        public void Checksum_DifferentForSingleBitChange()
        {
            var data1 = new byte[] { 0x00, 0x01, 0x02, 0x03 };
            var data2 = new byte[] { 0x00, 0x01, 0x02, 0x03 };
            data2[0] ^= 0x01; // Flip one bit

            var checksum1 = ComputeChecksum(data1);
            var checksum2 = ComputeChecksum(data2);

            Assert.NotEqual(checksum1, checksum2);
        }

        private static string ComputeChecksum(byte[] data)
        {
            return Convert.ToHexString(SHA256.HashData(data));
        }
    }

    /// <summary>
    /// Tests for storage path handling.
    /// </summary>
    public class StoragePathTests
    {
        [Theory]
        [InlineData("simple.txt", true)]
        [InlineData("folder/file.txt", true)]
        [InlineData("deep/nested/path/file.txt", true)]
        [InlineData("with-dashes.txt", true)]
        [InlineData("with_underscores.txt", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void PathValidation_ValidatesCorrectly(string? path, bool expectedValid)
        {
            var isValid = IsValidPath(path);
            Assert.Equal(expectedValid, isValid);
        }

        [Theory]
        [InlineData("../escape.txt")]
        [InlineData("folder/../../../etc/passwd")]
        [InlineData("..\\windows\\traversal")]
        public void PathValidation_RejectsTraversalAttempts(string path)
        {
            Assert.False(IsSafePath(path));
        }

        [Fact]
        public void PathNormalization_HandlesSlashes()
        {
            var path1 = NormalizePath("folder/subfolder/file.txt");
            var path2 = NormalizePath("folder\\subfolder\\file.txt");
            var path3 = NormalizePath("folder/subfolder\\file.txt");

            Assert.Equal(path1, path2);
            Assert.Equal(path2, path3);
        }

        private static bool IsValidPath(string? path) =>
            !string.IsNullOrEmpty(path);

        private static bool IsSafePath(string path) =>
            !path.Contains("..") && !path.StartsWith("/") && !path.StartsWith("\\");

        private static string NormalizePath(string path) =>
            path.Replace('\\', '/');
    }

    /// <summary>
    /// In-memory storage implementation for testing.
    /// </summary>
    internal class InMemoryTestStorage
    {
        private readonly Dictionary<string, StorageItem> _items = new();
        private readonly object _lock = new();

        public Task WriteAsync(string key, byte[] data)
        {
            lock (_lock)
            {
                _items[key] = new StorageItem
                {
                    Data = data.ToArray(),
                    LastModified = DateTimeOffset.UtcNow
                };
            }
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(string key)
        {
            lock (_lock)
            {
                if (!_items.TryGetValue(key, out var item))
                    throw new KeyNotFoundException($"Key not found: {key}");
                return Task.FromResult(item.Data.ToArray());
            }
        }

        public Task<bool> ExistsAsync(string key)
        {
            lock (_lock)
            {
                return Task.FromResult(_items.ContainsKey(key));
            }
        }

        public Task DeleteAsync(string key)
        {
            lock (_lock)
            {
                _items.Remove(key);
            }
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListAsync(string prefix)
        {
            List<string> keys;
            lock (_lock)
            {
                keys = _items.Keys
                    .Where(k => k.StartsWith(prefix))
                    .ToList();
            }

            foreach (var key in keys)
            {
                yield return key;
            }
            await Task.CompletedTask;
        }

        public Task<Stream> OpenReadStreamAsync(string key)
        {
            lock (_lock)
            {
                if (!_items.TryGetValue(key, out var item))
                    throw new KeyNotFoundException($"Key not found: {key}");
                return Task.FromResult<Stream>(new MemoryStream(item.Data));
            }
        }

        public Task<StorageMetadata> GetMetadataAsync(string key)
        {
            lock (_lock)
            {
                if (!_items.TryGetValue(key, out var item))
                    throw new KeyNotFoundException($"Key not found: {key}");
                return Task.FromResult(new StorageMetadata
                {
                    Size = item.Data.Length,
                    LastModified = item.LastModified
                });
            }
        }

        private class StorageItem
        {
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public DateTimeOffset LastModified { get; init; }
        }
    }

    internal class StorageMetadata
    {
        public long Size { get; init; }
        public DateTimeOffset LastModified { get; init; }
    }
}
