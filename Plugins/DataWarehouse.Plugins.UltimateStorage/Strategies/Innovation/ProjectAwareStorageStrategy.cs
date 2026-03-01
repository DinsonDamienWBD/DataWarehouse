using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Innovation
{
    /// <summary>
    /// Project-aware storage that organizes data by project and context automatically.
    /// </summary>
    public class ProjectAwareStorageStrategy : UltimateStorageStrategyBase
    {
        private string _baseStoragePath = string.Empty;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private readonly BoundedDictionary<string, string> _objectToProject = new BoundedDictionary<string, string>(1000);

        public override string StrategyId => "project-aware";
        public override string Name => "Project-Aware Storage";
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

            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, ct);
            var dataBytes = ms.ToArray();

            var projectName = "default";
            if (metadata != null && metadata.TryGetValue("ProjectName", out var projName))
            {
                // Sanitize projectName from user-supplied metadata to prevent path traversal.
                projectName = SanitizePathSegment(projName);
            }

            var projectPath = Path.Combine(_baseStoragePath, projectName);
            // Canonicalize and verify the project path stays inside the base storage path.
            projectPath = Path.GetFullPath(projectPath);
            if (!projectPath.StartsWith(Path.GetFullPath(_baseStoragePath), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    $"ProjectName '{projectName}' resolves outside the base storage path.");

            Directory.CreateDirectory(projectPath);

            // Sanitize the key as well to prevent traversal via the key parameter.
            var safeKey = SanitizePathSegment(key);
            var filePath = Path.Combine(projectPath, safeKey);
            // Canonicalize and verify the file path stays inside the project path.
            filePath = Path.GetFullPath(filePath);
            if (!filePath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    $"Key '{key}' resolves outside the project storage path.");

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(filePath, dataBytes, ct);
            _objectToProject[key] = projectName;

            var fileInfo = new FileInfo(filePath);
            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                CustomMetadata = new Dictionary<string, string> { ["ProjectName"] = projectName },
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            if (_objectToProject.TryGetValue(key, out var projectName))
            {
                var filePath = Path.Combine(_baseStoragePath, projectName, key);
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

            if (_objectToProject.TryRemove(key, out var projectName))
            {
                var filePath = Path.Combine(_baseStoragePath, projectName, key);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }

            await Task.CompletedTask;
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            await Task.CompletedTask;
            return _objectToProject.ContainsKey(key);
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();

            foreach (var kvp in _objectToProject)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(prefix) || kvp.Key.StartsWith(prefix))
                {
                    var filePath = Path.Combine(_baseStoragePath, kvp.Value, kvp.Key);
                    if (File.Exists(filePath))
                    {
                        var fileInfo = new FileInfo(filePath);
                        yield return new StorageObjectMetadata
                        {
                            Key = kvp.Key,
                            Size = fileInfo.Length,
                            Created = fileInfo.CreationTimeUtc,
                            Modified = fileInfo.LastWriteTimeUtc,
                            CustomMetadata = new Dictionary<string, string> { ["ProjectName"] = kvp.Value },
                            Tier = Tier
                        };
                    }
                }
            }

            await Task.CompletedTask;
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();

            if (!_objectToProject.TryGetValue(key, out var projectName))
            {
                throw new FileNotFoundException($"Object with key '{key}' not found", key);
            }

            var filePath = Path.Combine(_baseStoragePath, projectName, key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {key}", filePath);
            }

            var fileInfo = new FileInfo(filePath);
            await Task.CompletedTask;

            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                CustomMetadata = new Dictionary<string, string> { ["ProjectName"] = projectName },
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();

            var projectCount = _objectToProject.Values.Distinct().Count();
            return new StorageHealthInfo
            {
                Status = HealthStatus.Healthy,
                LatencyMs = 2,
                Message = $"Active Projects: {projectCount}, Total Objects: {_objectToProject.Count}",
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

        /// <summary>
        /// Sanitizes a caller-supplied path segment by removing traversal sequences,
        /// path separators, and other characters that could escape the intended directory.
        /// </summary>
        private static string SanitizePathSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return "_empty_";

            // Replace path separators and traversal sequences.
            var sanitized = segment
                .Replace('/', '_')
                .Replace('\\', '_')
                .Replace(':', '_');

            // Collapse '..' references.
            while (sanitized.Contains(".."))
                sanitized = sanitized.Replace("..", "__");

            // Remove any remaining invalid file-name characters.
            sanitized = string.Join("_", sanitized.Split(Path.GetInvalidFileNameChars()));

            return string.IsNullOrWhiteSpace(sanitized) ? "_empty_" : sanitized;
        }
    }
}
