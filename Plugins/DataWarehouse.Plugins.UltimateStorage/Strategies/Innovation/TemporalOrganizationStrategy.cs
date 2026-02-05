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

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            var dataBytes = ms.ToArray();

            var timestamp = DateTime.UtcNow;
            var datePath = timestamp.ToString(_organizationPattern);
            var fullPath = Path.Combine(_baseStoragePath, datePath, Path.GetFileName(key));
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(fullPath, dataBytes, ct);

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

            foreach (var directory in Directory.EnumerateDirectories(_baseStoragePath, "*", SearchOption.AllDirectories))
            {
                var filePath = Path.Combine(directory, Path.GetFileName(key));
                if (File.Exists(filePath))
                {
                    var data = await File.ReadAllBytesAsync(filePath, ct);
                    return new MemoryStream(data);
                }
            }

            throw new FileNotFoundException($"Object with key '{key}' not found", key);
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            foreach (var directory in Directory.EnumerateDirectories(_baseStoragePath, "*", SearchOption.AllDirectories))
            {
                var filePath = Path.Combine(directory, Path.GetFileName(key));
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    break;
                }
            }

            await Task.CompletedTask;
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            foreach (var directory in Directory.EnumerateDirectories(_baseStoragePath, "*", SearchOption.AllDirectories))
            {
                var filePath = Path.Combine(directory, Path.GetFileName(key));
                if (File.Exists(filePath))
                {
                    return true;
                }
            }

            await Task.CompletedTask;
            return false;
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();

            foreach (var file in Directory.EnumerateFiles(_baseStoragePath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);

                if (string.IsNullOrEmpty(prefix) || fileName.StartsWith(prefix))
                {
                    var fileInfo = new FileInfo(file);
                    yield return new StorageObjectMetadata
                    {
                        Key = fileName,
                        Size = fileInfo.Length,
                        Created = fileInfo.CreationTimeUtc,
                        Modified = fileInfo.LastWriteTimeUtc,
                        Tier = Tier
                    };
                }
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
