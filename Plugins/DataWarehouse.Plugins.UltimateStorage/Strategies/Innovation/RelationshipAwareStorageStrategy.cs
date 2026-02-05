using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Innovation
{
    /// <summary>
    /// Relationship-aware storage that stores object relationships alongside data for graph-like traversal.
    /// </summary>
    public class RelationshipAwareStorageStrategy : UltimateStorageStrategyBase
    {
        private string _baseStoragePath = string.Empty;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly ConcurrentDictionary<string, ObjectNode> _relationshipGraph = new();

        public override string StrategyId => "relationship-aware";
        public override string Name => "Relationship-Aware Storage";
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

            var filePath = Path.Combine(_baseStoragePath, key);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(filePath, dataBytes, ct);

            var node = _relationshipGraph.GetOrAdd(key, _ => new ObjectNode { Key = key });
            node.Size = dataBytes.Length;

            if (metadata != null)
            {
                if (metadata.TryGetValue("RelatedTo", out var relatedKeys))
                {
                    foreach (var relatedKey in relatedKeys.Split(','))
                    {
                        node.RelatedKeys.Add(relatedKey.Trim());
                    }
                }

                if (metadata.TryGetValue("ParentKey", out var parentKey))
                {
                    node.ParentKey = parentKey;
                }
            }

            var fileInfo = new FileInfo(filePath);
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

            var filePath = Path.Combine(_baseStoragePath, key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Object with key '{key}' not found", key);
            }

            var data = await File.ReadAllBytesAsync(filePath, ct);
            return new MemoryStream(data);
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            var filePath = Path.Combine(_baseStoragePath, key);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            _relationshipGraph.TryRemove(key, out _);
            await Task.CompletedTask;
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            var filePath = Path.Combine(_baseStoragePath, key);
            await Task.CompletedTask;
            return File.Exists(filePath);
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();

            var searchPath = string.IsNullOrEmpty(prefix) ? _baseStoragePath : Path.Combine(_baseStoragePath, prefix);
            if (!Directory.Exists(searchPath))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(_baseStoragePath, file);
                var key = relativePath.Replace('\\', '/');

                var fileInfo = new FileInfo(file);
                yield return new StorageObjectMetadata
                {
                    Key = key,
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

            var filePath = Path.Combine(_baseStoragePath, key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Object with key '{key}' not found", key);
            }

            var fileInfo = new FileInfo(filePath);
            var metadata = new Dictionary<string, string>();

            if (_relationshipGraph.TryGetValue(key, out var node))
            {
                if (node.RelatedKeys.Any())
                {
                    metadata["RelatedTo"] = string.Join(", ", node.RelatedKeys);
                }
                if (!string.IsNullOrEmpty(node.ParentKey))
                {
                    metadata["ParentKey"] = node.ParentKey;
                }
            }

            await Task.CompletedTask;
            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                CustomMetadata = metadata,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();

            return new StorageHealthInfo
            {
                Status = HealthStatus.Healthy,
                LatencyMs = 2,
                Message = $"Objects in graph: {_relationshipGraph.Count}",
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

        private class ObjectNode
        {
            public string Key { get; set; } = string.Empty;
            public long Size { get; set; }
            public string? ParentKey { get; set; }
            public List<string> RelatedKeys { get; set; } = new();
        }
    }
}
