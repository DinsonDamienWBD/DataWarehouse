using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Innovation
{
    /// <summary>
    /// Temporal organization strategy that automatically organizes data by time-based patterns.
    /// </summary>
    public class TemporalOrganizationStrategy : UltimateStorageStrategyBase
    {
        private string _baseStoragePath = string.Empty;
        private string _organizationPattern = "yyyy/MM/dd";
        private readonly SemaphoreSlim _initLock = new(1, 1);
        // Key → full file path index, prevents duplicate-filename collisions and enables O(1) lookup.
        private readonly ConcurrentDictionary<string, string> _keyToPath = new();

        public override string StrategyId => "temporal-organization";
        public override string Name => "Temporal Organization Storage";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false,
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = null,
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong
        };

        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                _baseStoragePath = GetConfiguration<string>("BaseStoragePath")
                    ?? throw new InvalidOperationException("BaseStoragePath is required");

                _organizationPattern = GetConfiguration("OrganizationPattern", "yyyy/MM/dd");
                Directory.CreateDirectory(_baseStoragePath);
                await Task.CompletedTask;
            }
            finally
            {
                _initLock.Release();
            }
        }

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();

            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, ct);
            var dataBytes = ms.ToArray();

            var timestamp = DateTime.UtcNow;
            var datePath = timestamp.ToString(_organizationPattern);
            // Use a hash of the key as filename to avoid collisions between keys that share the same Path.GetFileName.
            var safeFileName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".bin";
            var fullPath = Path.Combine(_baseStoragePath, datePath, safeFileName);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(fullPath, dataBytes, ct);
            _keyToPath[key] = fullPath;

            var fileInfo = new FileInfo(fullPath);
            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            // O(1) lookup via in-memory index
            if (_keyToPath.TryGetValue(key, out var filePath) && File.Exists(filePath))
            {
                var data = await File.ReadAllBytesAsync(filePath, ct);
                return new MemoryStream(data);
            }

            throw new FileNotFoundException($"Object with key '{key}' not found", key);
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            if (_keyToPath.TryRemove(key, out var filePath) && File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return Task.CompletedTask;
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            return Task.FromResult(_keyToPath.TryGetValue(key, out var path) && File.Exists(path));
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();

            foreach (var kvp in _keyToPath)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(prefix) && !kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (!File.Exists(kvp.Value))
                    continue;

                var fileInfo = new FileInfo(kvp.Value);
                yield return new StorageObjectMetadata
                {
                    Key = kvp.Key,
                    Size = fileInfo.Length,
                    Created = fileInfo.CreationTimeUtc,
                    Modified = fileInfo.LastWriteTimeUtc,
                    Tier = Tier
                };
            }

            await Task.CompletedTask;
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            foreach (var directory in Directory.EnumerateDirectories(_baseStoragePath, "*", SearchOption.AllDirectories))
            {
                var filePath = Path.Combine(directory, Path.GetFileName(key));
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    await Task.CompletedTask;
                    return new StorageObjectMetadata
                    {
                        Key = key,
                        Size = fileInfo.Length,
                        Created = fileInfo.CreationTimeUtc,
                        Modified = fileInfo.LastWriteTimeUtc,
                        Tier = Tier
                    };
                }
            }

            throw new FileNotFoundException($"Object with key '{key}' not found", key);
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();

            return new StorageHealthInfo
            {
                Status = HealthStatus.Healthy,
                LatencyMs = 2,
                Message = $"Organization pattern: {_organizationPattern}",
                CheckedAt = DateTime.UtcNow
            };
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();
            var driveInfo = new DriveInfo(Path.GetPathRoot(_baseStoragePath) ?? "C:\\");
            await Task.CompletedTask;
            return driveInfo.AvailableFreeSpace;
        }
    }
}
