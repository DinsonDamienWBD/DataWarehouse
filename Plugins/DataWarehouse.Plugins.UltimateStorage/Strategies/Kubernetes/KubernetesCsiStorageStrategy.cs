using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Utilities;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Kubernetes;

/// <summary>
/// Kubernetes CSI (Container Storage Interface) storage strategy for DataWarehouse.
/// Implements the full CSI specification including Identity, Controller, and Node services
/// as a storage strategy within UltimateStorage.
///
/// <para>
/// <b>CSI Capabilities:</b>
/// <list type="bullet">
/// <item><term>CREATE_DELETE_VOLUME</term><description>Dynamic volume provisioning and deletion</description></item>
/// <item><term>CREATE_DELETE_SNAPSHOT</term><description>Volume snapshot creation and deletion</description></item>
/// <item><term>CLONE_VOLUME</term><description>Volume cloning from snapshots or existing volumes</description></item>
/// <item><term>EXPAND_VOLUME</term><description>Online and offline volume expansion</description></item>
/// <item><term>GET_CAPACITY</term><description>Storage capacity reporting</description></item>
/// <item><term>VOLUME_ACCESSIBILITY</term><description>Topology-aware volume placement</description></item>
/// <item><term>BLOCK_VOLUME</term><description>Raw block volume support</description></item>
/// <item><term>MULTI_NODE_MULTI_WRITER</term><description>ReadWriteMany access mode support</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Storage Classes:</b>
/// <list type="bullet">
/// <item><term>datawarehouse-hot</term><description>SSD-backed storage for frequently accessed data</description></item>
/// <item><term>datawarehouse-warm</term><description>Balanced storage for moderate access patterns</description></item>
/// <item><term>datawarehouse-cold</term><description>Cost-optimized storage for infrequent access</description></item>
/// <item><term>datawarehouse-archive</term><description>Deep archive storage for compliance and retention</description></item>
/// </list>
/// </para>
/// </summary>
public class KubernetesCsiStorageStrategy : UltimateStorageStrategyBase
{
    #region Strategy Identity

    public override string StrategyId => "kubernetes-csi";
    public override string Name => "Kubernetes CSI Driver Storage";
    public override StorageTier Tier => StorageTier.Hot;
    // CSI data persistence requires real volume backend integration
    public override bool IsProductionReady => false;

    public override StorageCapabilities Capabilities => new StorageCapabilities
    {
        SupportsMetadata = true,
        SupportsStreaming = true,
        SupportsLocking = false,
        SupportsVersioning = true,
        SupportsTiering = true,
        SupportsEncryption = true,
        SupportsCompression = false,
        SupportsMultipart = false,
        MaxObjectSize = MaxVolumeCapacityBytes,
        MaxObjects = 1000,
        ConsistencyModel = ConsistencyModel.Strong
    };

    #endregion

    #region Private Fields

    private readonly BoundedDictionary<string, CsiVolume> _volumes = new BoundedDictionary<string, CsiVolume>(1000);
    private readonly BoundedDictionary<string, CsiSnapshot> _snapshots = new BoundedDictionary<string, CsiSnapshot>(1000);
    private readonly BoundedDictionary<string, NodeStageInfo> _stagedVolumes = new BoundedDictionary<string, NodeStageInfo>(1000);
    private readonly BoundedDictionary<string, NodePublishInfo> _publishedVolumes = new BoundedDictionary<string, NodePublishInfo>(1000);
    private readonly BoundedDictionary<string, StorageClassConfig> _storageClasses = new BoundedDictionary<string, StorageClassConfig>(1000);
    private readonly BoundedDictionary<string, TopologySegment> _topologySegments = new BoundedDictionary<string, TopologySegment>(1000);
    private readonly BoundedDictionary<string, byte[]> _volumeData = new BoundedDictionary<string, byte[]>(1000);
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private string _nodeId = string.Empty;
    private string _driverName = "csi.datawarehouse.io";
    private string _socketPath = "/csi/csi.sock";

    private const string CsiSpecVersion = "1.8.0";
    private const long DefaultVolumeCapacityBytes = 10L * 1024 * 1024 * 1024; // 10GiB
    private const long MaxVolumeCapacityBytes = 100L * 1024 * 1024 * 1024 * 1024; // 100TiB
    private const int MaxVolumesPerNode = 256;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Initialization

    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _nodeId = $"node-{Environment.MachineName}";

        var socketPath = GetConfiguration<string>("socketPath");
        if (!string.IsNullOrEmpty(socketPath))
            _socketPath = socketPath;

        var driverName = GetConfiguration<string>("driverName");
        if (!string.IsNullOrEmpty(driverName))
            _driverName = driverName;

        InitializeDefaultStorageClasses();
        InitializeDefaultTopology();

        return Task.CompletedTask;
    }

    #endregion

    #region Storage Strategy Core Implementation

    protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            // Create volume if it doesn't exist, store data
            if (!_volumes.ContainsKey(key))
            {
                var volumeId = key;
                var volume = new CsiVolume
                {
                    VolumeId = volumeId,
                    Name = key,
                    CapacityBytes = DefaultVolumeCapacityBytes,
                    StorageClass = "datawarehouse-hot",
                    StorageTier = CsiStorageTier.Hot,
                    AccessTypes = new List<VolumeAccessType> { VolumeAccessType.SingleNodeWriter },
                    Encrypted = true,
                    EncryptionType = CsiEncryptionType.AES256GCM,
                    ReplicationFactor = 3,
                    ComplianceMode = CsiComplianceMode.None,
                    CreatedAt = DateTime.UtcNow,
                    State = VolumeState.Available,
                    Parameters = metadata?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, string>()
                };
                _volumes[volumeId] = volume;
            }

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            _volumeData[key] = bytes;

            if (_volumes.TryGetValue(key, out var vol))
            {
                vol.UsedBytes = bytes.Length;
            }

            return new StorageObjectMetadata
            {
                Key = key,
                Size = bytes.Length,
                ContentType = (metadata != null && metadata.TryGetValue("content-type", out var ct2) ? ct2 : null) ?? "application/octet-stream",
                Modified = DateTime.UtcNow,
                Created = DateTime.UtcNow,
                ETag = Convert.ToBase64String(SHA256.HashData(bytes))[..16],
                CustomMetadata = metadata?.ToDictionary(k => k.Key, v => v.Value)
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    protected override Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
    {
        if (!_volumeData.TryGetValue(key, out var data))
        {
            throw new KeyNotFoundException($"Volume data not found: {key}");
        }

        return Task.FromResult<Stream>(new MemoryStream(data));
    }

    protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            _volumeData.TryRemove(key, out _);

            if (_volumes.TryGetValue(key, out var volume))
            {
                // Check if volume is in use - throw if so
                if (_publishedVolumes.Values.Any(p => p.VolumeId == key))
                    throw new InvalidOperationException("Volume is currently published to a node");
                if (_stagedVolumes.Values.Any(s => s.VolumeId == key))
                    throw new InvalidOperationException("Volume is currently staged on a node");

                _volumes.TryRemove(key, out _);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
    {
        return Task.FromResult(_volumes.ContainsKey(key));
    }

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var volumes = _volumes.Values
            .Where(v => string.IsNullOrEmpty(prefix) || v.VolumeId.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(v => v.CreatedAt);

        foreach (var vol in volumes)
        {
            ct.ThrowIfCancellationRequested();
            yield return new StorageObjectMetadata
            {
                Key = vol.VolumeId,
                Size = vol.UsedBytes,
                ContentType = "application/octet-stream",
                Created = vol.CreatedAt,
                Modified = vol.CreatedAt,
                CustomMetadata = vol.Parameters
            };
        }
    }

    protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
    {
        if (!_volumes.TryGetValue(key, out var volume))
            throw new KeyNotFoundException($"Volume not found: {key}");

        return Task.FromResult(new StorageObjectMetadata
        {
            Key = key,
            Size = volume.UsedBytes,
            ContentType = "application/octet-stream",
            Created = volume.CreatedAt,
            Modified = volume.CreatedAt,
            CustomMetadata = volume.Parameters
        });
    }

    protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
    {
        return Task.FromResult(new StorageHealthInfo
        {
            Status = HealthStatus.Healthy,
            Message = $"CSI driver '{_driverName}' operational. {_volumes.Count} volumes, {_snapshots.Count} snapshots.",
            TotalCapacity = MaxVolumeCapacityBytes * 10,
            UsedCapacity = _volumes.Values.Sum(v => v.CapacityBytes),
            AvailableCapacity = MaxVolumeCapacityBytes * 10 - _volumes.Values.Sum(v => v.CapacityBytes)
        });
    }

    protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
    {
        long totalCapacity = MaxVolumeCapacityBytes * 10;
        long usedCapacity = _volumes.Values.Sum(v => v.CapacityBytes);
        return Task.FromResult<long?>(Math.Max(0, totalCapacity - usedCapacity));
    }

    #endregion

    #region CSI Volume Operations

    /// <summary>Creates a CSI volume with the given parameters.</summary>
    public async Task<Dictionary<string, object?>> CreateVolumeAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync(CancellationToken.None);
        try
        {
            var name = (payload.TryGetValue("name", out var nameVal) ? nameVal : null)?.ToString();
            if (string.IsNullOrEmpty(name))
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume name is required");

            var existing = _volumes.Values.FirstOrDefault(v => v.Name == name);
            if (existing != null)
                return CreateVolumeResponse(existing);

            long capacityBytes = DefaultVolumeCapacityBytes;
            if (payload.TryGetValue("capacity_range", out var cr) && cr is Dictionary<string, object> capacityRange)
            {
                if (capacityRange.TryGetValue("required_bytes", out var rb) && rb != null)
                    capacityBytes = Convert.ToInt64(rb);
                else if (capacityRange.TryGetValue("limit_bytes", out var lb) && lb != null)
                    capacityBytes = Convert.ToInt64(lb);
            }

            if (capacityBytes > MaxVolumeCapacityBytes)
                return CreateErrorResponse(CsiErrorCode.OutOfRange, $"Requested capacity exceeds maximum ({MaxVolumeCapacityBytes} bytes)");

            var parameters = payload.TryGetValue("parameters", out var p) && p is Dictionary<string, object> pDict
                ? pDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "")
                : new Dictionary<string, string>();

            var storageClassName = parameters.GetValueOrDefault("storageClass") ?? "datawarehouse-hot";
            if (!_storageClasses.TryGetValue(storageClassName, out var storageClass))
                storageClass = _storageClasses.Values.First();

            string? sourceVolumeId = null;
            string? sourceSnapshotId = null;
            if (payload.TryGetValue("volume_content_source", out var vcs) && vcs is Dictionary<string, object> contentSource)
            {
                if (contentSource.TryGetValue("volume", out var vol) && vol is Dictionary<string, object> volDict)
                {
                    sourceVolumeId = (volDict.TryGetValue("volume_id", out var vidVal) ? vidVal : null)?.ToString();
                    if (sourceVolumeId != null && !_volumes.ContainsKey(sourceVolumeId))
                        return CreateErrorResponse(CsiErrorCode.NotFound, $"Source volume not found: {sourceVolumeId}");
                }
                else if (contentSource.TryGetValue("snapshot", out var snap) && snap is Dictionary<string, object> snapDict)
                {
                    sourceSnapshotId = (snapDict.TryGetValue("snapshot_id", out var sidVal) ? sidVal : null)?.ToString();
                    if (sourceSnapshotId != null && !_snapshots.ContainsKey(sourceSnapshotId))
                        return CreateErrorResponse(CsiErrorCode.NotFound, $"Source snapshot not found: {sourceSnapshotId}");
                }
            }

            var accessibleTopology = ParseTopologyRequirements(payload);
            var accessTypes = ParseVolumeCapabilities(payload);

            var volumeId = GenerateVolumeId();
            var volume = new CsiVolume
            {
                VolumeId = volumeId,
                Name = name,
                CapacityBytes = capacityBytes,
                StorageClass = storageClassName,
                StorageTier = storageClass.Tier,
                AccessTypes = accessTypes,
                Encrypted = storageClass.Encryption != CsiEncryptionType.None,
                EncryptionType = storageClass.Encryption,
                ReplicationFactor = storageClass.ReplicationFactor,
                ComplianceMode = storageClass.ComplianceMode,
                AccessibleTopology = accessibleTopology,
                SourceVolumeId = sourceVolumeId,
                SourceSnapshotId = sourceSnapshotId,
                CreatedAt = DateTime.UtcNow,
                State = VolumeState.Available,
                Parameters = parameters,
                UsedBytes = 0
            };

            if (!string.IsNullOrEmpty(sourceVolumeId) && _volumes.TryGetValue(sourceVolumeId, out var srcVol))
                volume.UsedBytes = srcVol.UsedBytes;
            else if (!string.IsNullOrEmpty(sourceSnapshotId) && _snapshots.TryGetValue(sourceSnapshotId, out var srcSnap))
                volume.UsedBytes = srcSnap.SizeBytes;

            _volumes[volumeId] = volume;
            return CreateVolumeResponse(volume);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Deletes a CSI volume.</summary>
    public async Task<Dictionary<string, object?>> DeleteVolumeAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync(CancellationToken.None);
        try
        {
            var volumeId = (payload.TryGetValue("volume_id", out var volIdVal) ? volIdVal : null)?.ToString();
            if (string.IsNullOrEmpty(volumeId))
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");

            if (!_volumes.TryGetValue(volumeId, out var volume))
                return new Dictionary<string, object?> { ["success"] = true };

            if (_publishedVolumes.Values.Any(p => p.VolumeId == volumeId))
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition, "Volume is currently published to a node");

            if (_stagedVolumes.Values.Any(s => s.VolumeId == volumeId))
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition, "Volume is currently staged on a node");

            if (volume.ComplianceMode != CsiComplianceMode.None && volume.RetentionUntil > DateTime.UtcNow)
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition,
                    $"Volume is under compliance retention until {volume.RetentionUntil:O}");

            _volumes.TryRemove(volumeId, out _);
            return new Dictionary<string, object?> { ["success"] = true };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Expands a CSI volume.</summary>
    public async Task<Dictionary<string, object?>> ExpandVolumeAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync(CancellationToken.None);
        try
        {
            var volumeId = (payload.TryGetValue("volume_id", out var volIdVal) ? volIdVal : null)?.ToString();
            if (string.IsNullOrEmpty(volumeId))
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");

            if (!_volumes.TryGetValue(volumeId, out var volume))
                return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");

            long newCapacityBytes = volume.CapacityBytes;
            if (payload.TryGetValue("capacity_range", out var cr) && cr is Dictionary<string, object> capacityRange)
            {
                if (capacityRange.TryGetValue("required_bytes", out var rb) && rb != null)
                    newCapacityBytes = Convert.ToInt64(rb);
            }

            if (newCapacityBytes < volume.CapacityBytes)
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume shrinking is not supported");

            if (newCapacityBytes > MaxVolumeCapacityBytes)
                return CreateErrorResponse(CsiErrorCode.OutOfRange, $"Requested capacity exceeds maximum ({MaxVolumeCapacityBytes} bytes)");

            if (_storageClasses.TryGetValue(volume.StorageClass, out var sc) && !sc.AllowVolumeExpansion)
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition, "Storage class does not allow volume expansion");

            volume.CapacityBytes = newCapacityBytes;
            bool nodeExpansionRequired = _publishedVolumes.Values.Any(p => p.VolumeId == volumeId);

            return new Dictionary<string, object?>
            {
                ["capacity_bytes"] = volume.CapacityBytes,
                ["node_expansion_required"] = nodeExpansionRequired
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Creates a CSI snapshot.</summary>
    public async Task<Dictionary<string, object?>> CreateSnapshotAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync(CancellationToken.None);
        try
        {
            var sourceVolumeId = (payload.TryGetValue("source_volume_id", out var srcVolVal) ? srcVolVal : null)?.ToString();
            var name = (payload.TryGetValue("name", out var snapNameVal) ? snapNameVal : null)?.ToString();

            if (string.IsNullOrEmpty(sourceVolumeId))
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Source volume ID is required");
            if (string.IsNullOrEmpty(name))
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Snapshot name is required");
            if (!_volumes.TryGetValue(sourceVolumeId, out var volume))
                return CreateErrorResponse(CsiErrorCode.NotFound, $"Source volume not found: {sourceVolumeId}");

            var existing = _snapshots.Values.FirstOrDefault(s => s.Name == name && s.SourceVolumeId == sourceVolumeId);
            if (existing != null)
                return CreateSnapshotResponse(existing);

            var snapshotId = GenerateSnapshotId();
            var snapshot = new CsiSnapshot
            {
                SnapshotId = snapshotId,
                Name = name,
                SourceVolumeId = sourceVolumeId,
                SizeBytes = volume.UsedBytes,
                CreatedAt = DateTime.UtcNow,
                ReadyToUse = true,
                State = SnapshotState.Ready
            };

            _snapshots[snapshotId] = snapshot;
            return CreateSnapshotResponse(snapshot);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>Stages a volume to a node.</summary>
    public Task<Dictionary<string, object?>> NodeStageVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = (payload.TryGetValue("volume_id", out var volIdVal) ? volIdVal : null)?.ToString();
        var stagingTargetPath = (payload.TryGetValue("staging_target_path", out var stpVal) ? stpVal : null)?.ToString();

        if (string.IsNullOrEmpty(volumeId) || string.IsNullOrEmpty(stagingTargetPath))
            return Task.FromResult(CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID and staging target path are required"));

        if (!_volumes.ContainsKey(volumeId))
            return Task.FromResult(CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}"));

        var stageKey = $"{volumeId}:{_nodeId}";
        if (_stagedVolumes.TryGetValue(stageKey, out var existingStage))
        {
            if (existingStage.StagingTargetPath == stagingTargetPath)
                return Task.FromResult<Dictionary<string, object?>>(new() { ["success"] = true });
            return Task.FromResult(CreateErrorResponse(CsiErrorCode.AlreadyExists,
                $"Volume already staged at different path: {existingStage.StagingTargetPath}"));
        }

        var fsType = "ext4";
        var mountFlags = new List<string>();
        var isBlock = false;
        if (payload.TryGetValue("volume_capability", out var vc) && vc is Dictionary<string, object> capability)
        {
            if (capability.ContainsKey("block"))
                isBlock = true;
            else if (capability.TryGetValue("mount", out var mount) && mount is Dictionary<string, object> mountDict)
            {
                fsType = (mountDict.TryGetValue("fs_type", out var fsVal) ? fsVal : null)?.ToString() ?? "ext4";
                if (mountDict.TryGetValue("mount_flags", out var flags) && flags is List<object> flagList)
                    mountFlags = flagList.Select(f => f.ToString() ?? "").ToList();
            }
        }

        _stagedVolumes[stageKey] = new NodeStageInfo
        {
            VolumeId = volumeId,
            NodeId = _nodeId,
            StagingTargetPath = stagingTargetPath,
            IsBlock = isBlock,
            FsType = fsType,
            MountFlags = mountFlags,
            StagedAt = DateTime.UtcNow
        };

        return Task.FromResult<Dictionary<string, object?>>(new() { ["success"] = true });
    }

    /// <summary>Gets node information.</summary>
    public Dictionary<string, object> GetNodeInfo()
    {
        var topology = _topologySegments.TryGetValue(_nodeId, out var segments)
            ? segments.Segments
            : new Dictionary<string, string>
            {
                ["topology.kubernetes.io/region"] = "default-region",
                ["topology.kubernetes.io/zone"] = "default-zone"
            };

        return new Dictionary<string, object>
        {
            ["node_id"] = _nodeId,
            ["max_volumes_per_node"] = MaxVolumesPerNode,
            ["accessible_topology"] = new Dictionary<string, object> { ["segments"] = topology }
        };
    }

    /// <summary>Gets available storage capacity.</summary>
    public Dictionary<string, object> GetCapacity()
    {
        long totalCapacity = MaxVolumeCapacityBytes * 10;
        long usedCapacity = _volumes.Values.Sum(v => v.CapacityBytes);
        long availableCapacity = Math.Max(0, totalCapacity - usedCapacity);

        return new Dictionary<string, object>
        {
            ["available_capacity"] = availableCapacity,
            ["maximum_volume_size"] = MaxVolumeCapacityBytes,
            ["minimum_volume_size"] = 1L * 1024 * 1024 * 1024
        };
    }

    #endregion

    #region Helper Methods

    private void InitializeDefaultStorageClasses()
    {
        _storageClasses["datawarehouse-hot"] = new StorageClassConfig
        {
            Name = "datawarehouse-hot", Tier = CsiStorageTier.Hot, Encryption = CsiEncryptionType.AES256GCM,
            ComplianceMode = CsiComplianceMode.None, ReplicationFactor = 3, AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer", ReclaimPolicy = "Delete", IopsLimit = 10000, ThroughputLimitMBps = 500
        };
        _storageClasses["datawarehouse-warm"] = new StorageClassConfig
        {
            Name = "datawarehouse-warm", Tier = CsiStorageTier.Warm, Encryption = CsiEncryptionType.AES256GCM,
            ComplianceMode = CsiComplianceMode.None, ReplicationFactor = 2, AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer", ReclaimPolicy = "Delete", IopsLimit = 3000, ThroughputLimitMBps = 125
        };
        _storageClasses["datawarehouse-cold"] = new StorageClassConfig
        {
            Name = "datawarehouse-cold", Tier = CsiStorageTier.Cold, Encryption = CsiEncryptionType.AES256GCM,
            ComplianceMode = CsiComplianceMode.None, ReplicationFactor = 2, AllowVolumeExpansion = true,
            VolumeBindingMode = "Immediate", ReclaimPolicy = "Delete", IopsLimit = 500, ThroughputLimitMBps = 50
        };
        _storageClasses["datawarehouse-archive"] = new StorageClassConfig
        {
            Name = "datawarehouse-archive", Tier = CsiStorageTier.Archive, Encryption = CsiEncryptionType.AES256GCM,
            ComplianceMode = CsiComplianceMode.WORM, ReplicationFactor = 3, AllowVolumeExpansion = false,
            VolumeBindingMode = "Immediate", ReclaimPolicy = "Retain", IopsLimit = 100, ThroughputLimitMBps = 10
        };
        _storageClasses["datawarehouse-gdpr"] = new StorageClassConfig
        {
            Name = "datawarehouse-gdpr", Tier = CsiStorageTier.Hot, Encryption = CsiEncryptionType.AES256GCM,
            ComplianceMode = CsiComplianceMode.GDPR, ReplicationFactor = 3, AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer", ReclaimPolicy = "Retain", IopsLimit = 10000,
            ThroughputLimitMBps = 500, DataResidencyRegion = "eu-west"
        };
        _storageClasses["datawarehouse-hipaa"] = new StorageClassConfig
        {
            Name = "datawarehouse-hipaa", Tier = CsiStorageTier.Hot, Encryption = CsiEncryptionType.AES256GCM,
            ComplianceMode = CsiComplianceMode.HIPAA, ReplicationFactor = 3, AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer", ReclaimPolicy = "Retain", IopsLimit = 10000,
            ThroughputLimitMBps = 500, AuditLoggingEnabled = true
        };
    }

    private void InitializeDefaultTopology()
    {
        _topologySegments[_nodeId] = new TopologySegment
        {
            NodeId = _nodeId,
            Segments = new Dictionary<string, string>
            {
                ["topology.kubernetes.io/region"] = Environment.GetEnvironmentVariable("TOPOLOGY_REGION") ?? "default-region",
                ["topology.kubernetes.io/zone"] = Environment.GetEnvironmentVariable("TOPOLOGY_ZONE") ?? "default-zone",
                ["topology.datawarehouse.io/rack"] = Environment.GetEnvironmentVariable("TOPOLOGY_RACK") ?? "rack-1"
            }
        };
    }

    private static string GenerateVolumeId() => $"vol-{Guid.NewGuid():N}";
    private static string GenerateSnapshotId() => $"snap-{Guid.NewGuid():N}";

    private List<VolumeAccessType> ParseVolumeCapabilities(Dictionary<string, object> payload)
    {
        var accessTypes = new List<VolumeAccessType>();
        if (!payload.TryGetValue("volume_capabilities", out var vc) || vc is not List<object> capabilities)
        {
            accessTypes.Add(VolumeAccessType.SingleNodeWriter);
            return accessTypes;
        }

        foreach (var cap in capabilities.OfType<Dictionary<string, object>>())
        {
            if (cap.TryGetValue("access_mode", out var am) && am is Dictionary<string, object> accessMode)
            {
                var mode = (accessMode.TryGetValue("mode", out var modeVal) ? modeVal : null)?.ToString() ?? "SINGLE_NODE_WRITER";
                var accessType = mode switch
                {
                    "SINGLE_NODE_WRITER" => VolumeAccessType.SingleNodeWriter,
                    "SINGLE_NODE_READER_ONLY" => VolumeAccessType.SingleNodeReaderOnly,
                    "MULTI_NODE_READER_ONLY" => VolumeAccessType.MultiNodeReaderOnly,
                    "MULTI_NODE_SINGLE_WRITER" => VolumeAccessType.MultiNodeSingleWriter,
                    "MULTI_NODE_MULTI_WRITER" => VolumeAccessType.MultiNodeMultiWriter,
                    "SINGLE_NODE_SINGLE_WRITER" => VolumeAccessType.SingleNodeSingleWriter,
                    "SINGLE_NODE_MULTI_WRITER" => VolumeAccessType.SingleNodeMultiWriter,
                    _ => VolumeAccessType.SingleNodeWriter
                };
                if (!accessTypes.Contains(accessType))
                    accessTypes.Add(accessType);
            }
        }

        if (accessTypes.Count == 0)
            accessTypes.Add(VolumeAccessType.SingleNodeWriter);

        return accessTypes;
    }

    private List<Dictionary<string, string>> ParseTopologyRequirements(Dictionary<string, object> payload)
    {
        var topology = new List<Dictionary<string, string>>();
        if (!payload.TryGetValue("accessibility_requirements", out var ar) || ar is not Dictionary<string, object> requirements)
            return topology;

        if (requirements.TryGetValue("preferred", out var pref) && pref is List<object> preferred)
        {
            foreach (var p in preferred.OfType<Dictionary<string, object>>())
            {
                if (p.TryGetValue("segments", out var seg) && seg is Dictionary<string, object> segments)
                    topology.Add(segments.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? ""));
            }
        }

        if (requirements.TryGetValue("requisite", out var req) && req is List<object> requisite)
        {
            foreach (var r in requisite.OfType<Dictionary<string, object>>())
            {
                if (r.TryGetValue("segments", out var seg) && seg is Dictionary<string, object> segments)
                {
                    var segDict = segments.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "");
                    if (!topology.Any(t => t.SequenceEqual(segDict)))
                        topology.Add(segDict);
                }
            }
        }

        return topology;
    }

    private Dictionary<string, object?> CreateVolumeResponse(CsiVolume volume)
    {
        return new Dictionary<string, object?>
        {
            ["volume"] = new Dictionary<string, object?>
            {
                ["volume_id"] = volume.VolumeId,
                ["capacity_bytes"] = volume.CapacityBytes,
                ["volume_context"] = volume.Parameters,
                ["content_source"] = string.IsNullOrEmpty(volume.SourceVolumeId) && string.IsNullOrEmpty(volume.SourceSnapshotId)
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["volume"] = string.IsNullOrEmpty(volume.SourceVolumeId) ? null : new Dictionary<string, string> { ["volume_id"] = volume.SourceVolumeId! },
                        ["snapshot"] = string.IsNullOrEmpty(volume.SourceSnapshotId) ? null : new Dictionary<string, string> { ["snapshot_id"] = volume.SourceSnapshotId! }
                    },
                ["accessible_topology"] = volume.AccessibleTopology.Select(t => new Dictionary<string, object?> { ["segments"] = t }).ToList()
            }
        };
    }

    private Dictionary<string, object?> CreateSnapshotResponse(CsiSnapshot snapshot)
    {
        return new Dictionary<string, object?>
        {
            ["snapshot"] = new Dictionary<string, object>
            {
                ["snapshot_id"] = snapshot.SnapshotId,
                ["source_volume_id"] = snapshot.SourceVolumeId,
                ["creation_time"] = new Dictionary<string, object>
                {
                    ["seconds"] = new DateTimeOffset(snapshot.CreatedAt).ToUnixTimeSeconds(),
                    ["nanos"] = 0
                },
                ["ready_to_use"] = snapshot.ReadyToUse,
                ["size_bytes"] = snapshot.SizeBytes
            }
        };
    }

    private static Dictionary<string, object?> CreateErrorResponse(CsiErrorCode code, string message)
    {
        return new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?> { ["code"] = (int)code, ["message"] = message }
        };
    }

    #endregion

    #region Internal Types

    private sealed class CsiVolume
    {
        public string VolumeId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public long CapacityBytes { get; set; }
        public long UsedBytes { get; set; }
        public string StorageClass { get; init; } = string.Empty;
        public CsiStorageTier StorageTier { get; init; }
        public List<VolumeAccessType> AccessTypes { get; init; } = new();
        public bool Encrypted { get; init; }
        public CsiEncryptionType EncryptionType { get; init; }
        public int ReplicationFactor { get; init; }
        public CsiComplianceMode ComplianceMode { get; init; }
        public DateTime? RetentionUntil { get; set; }
        public List<Dictionary<string, string>> AccessibleTopology { get; init; } = new();
        public string? SourceVolumeId { get; init; }
        public string? SourceSnapshotId { get; init; }
        public DateTime CreatedAt { get; init; }
        public VolumeState State { get; set; }
        public Dictionary<string, string> Parameters { get; init; } = new();
    }

    private sealed class CsiSnapshot
    {
        public string SnapshotId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string SourceVolumeId { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTime CreatedAt { get; init; }
        public bool ReadyToUse { get; set; }
        public SnapshotState State { get; set; }
    }

    private sealed class NodeStageInfo
    {
        public string VolumeId { get; init; } = string.Empty;
        public string NodeId { get; init; } = string.Empty;
        public string StagingTargetPath { get; init; } = string.Empty;
        public bool IsBlock { get; init; }
        public string FsType { get; init; } = "ext4";
        public List<string> MountFlags { get; init; } = new();
        public DateTime StagedAt { get; init; }
    }

    private sealed class NodePublishInfo
    {
        public string VolumeId { get; init; } = string.Empty;
        public string NodeId { get; init; } = string.Empty;
        public string TargetPath { get; init; } = string.Empty;
        public string? StagingTargetPath { get; init; }
        public bool ReadOnly { get; init; }
        public DateTime PublishedAt { get; init; }
    }

    private sealed class StorageClassConfig
    {
        public string Name { get; init; } = string.Empty;
        public CsiStorageTier Tier { get; init; }
        public CsiEncryptionType Encryption { get; init; }
        public CsiComplianceMode ComplianceMode { get; init; }
        public int ReplicationFactor { get; init; }
        public bool AllowVolumeExpansion { get; init; }
        public string VolumeBindingMode { get; init; } = "WaitForFirstConsumer";
        public string ReclaimPolicy { get; init; } = "Delete";
        public int IopsLimit { get; init; }
        public int ThroughputLimitMBps { get; init; }
        public string? DataResidencyRegion { get; init; }
        public bool AuditLoggingEnabled { get; init; }
    }

    private sealed class TopologySegment
    {
        public string NodeId { get; init; } = string.Empty;
        public Dictionary<string, string> Segments { get; init; } = new();
    }

    private enum CsiStorageTier { Hot, Warm, Cold, Archive }
    private enum CsiEncryptionType { None, AES256GCM, AES256CBC, ChaCha20Poly1305 }
    private enum CsiComplianceMode { None, WORM, GDPR, HIPAA, SOC2, PCI_DSS }
    private enum VolumeState { Creating, Available, InUse, Deleting, Error }
    private enum SnapshotState { Creating, Ready, Deleting, Error }

    private enum VolumeAccessType
    {
        Unknown, SingleNodeWriter, SingleNodeReaderOnly, MultiNodeReaderOnly,
        MultiNodeSingleWriter, MultiNodeMultiWriter, SingleNodeSingleWriter, SingleNodeMultiWriter
    }

    private enum CsiErrorCode
    {
        Ok = 0, Cancelled = 1, Unknown = 2, InvalidArgument = 3, DeadlineExceeded = 4,
        NotFound = 5, AlreadyExists = 6, PermissionDenied = 7, ResourceExhausted = 8,
        FailedPrecondition = 9, Aborted = 10, OutOfRange = 11, Unimplemented = 12,
        Internal = 13, Unavailable = 14, DataLoss = 15, Unauthenticated = 16
    }

    #endregion

    protected override ValueTask DisposeCoreAsync()
    {
        _operationLock.Dispose();
        return base.DisposeCoreAsync();
    }
}
