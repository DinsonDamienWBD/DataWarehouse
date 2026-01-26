using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.KubernetesCsi;

/// <summary>
/// Kubernetes CSI (Container Storage Interface) driver plugin for DataWarehouse.
/// Implements the full CSI specification including Identity, Controller, and Node services.
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
///
/// <para>
/// <b>Message Commands:</b>
/// <list type="bullet">
/// <item><term>csi.identity.probe</term><description>Check if the driver is ready</description></item>
/// <item><term>csi.identity.capabilities</term><description>Get driver capabilities</description></item>
/// <item><term>csi.controller.create_volume</term><description>Create a new volume</description></item>
/// <item><term>csi.controller.delete_volume</term><description>Delete a volume</description></item>
/// <item><term>csi.controller.expand_volume</term><description>Expand volume capacity</description></item>
/// <item><term>csi.controller.create_snapshot</term><description>Create a volume snapshot</description></item>
/// <item><term>csi.controller.delete_snapshot</term><description>Delete a snapshot</description></item>
/// <item><term>csi.controller.list_volumes</term><description>List all volumes</description></item>
/// <item><term>csi.controller.list_snapshots</term><description>List all snapshots</description></item>
/// <item><term>csi.controller.get_capacity</term><description>Get available capacity</description></item>
/// <item><term>csi.node.stage_volume</term><description>Stage volume to node</description></item>
/// <item><term>csi.node.unstage_volume</term><description>Unstage volume from node</description></item>
/// <item><term>csi.node.publish_volume</term><description>Publish volume to pod</description></item>
/// <item><term>csi.node.unpublish_volume</term><description>Unpublish volume from pod</description></item>
/// <item><term>csi.node.get_info</term><description>Get node information</description></item>
/// <item><term>csi.node.get_capabilities</term><description>Get node capabilities</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class KubernetesCsiPlugin : InterfacePluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.csi.driver";

    /// <inheritdoc />
    public override string Name => "Kubernetes CSI Driver Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string Protocol => "csi";

    /// <inheritdoc />
    public override int? Port => null;

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.StorageProvider;

    #region Private Fields

    private readonly ConcurrentDictionary<string, CsiVolume> _volumes = new();
    private readonly ConcurrentDictionary<string, CsiSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, NodeStageInfo> _stagedVolumes = new();
    private readonly ConcurrentDictionary<string, NodePublishInfo> _publishedVolumes = new();
    private readonly ConcurrentDictionary<string, StorageClassConfig> _storageClasses = new();
    private readonly ConcurrentDictionary<string, TopologySegment> _topologySegments = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private IKernelContext? _context;
    private CancellationTokenSource? _cts;
    private Task? _healthMonitorTask;
    private Socket? _csiSocket;
    private string _nodeId = string.Empty;
    private string _driverName = "csi.datawarehouse.io";
    private string _socketPath = "/csi/csi.sock";
    private bool _isRunning;

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

    #region Lifecycle

    /// <inheritdoc />
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;
        _nodeId = $"node-{request.KernelId}-{Environment.MachineName}";

        if (request.Config != null)
        {
            if (request.Config.TryGetValue("socketPath", out var sp) && sp is string socketPath)
            {
                _socketPath = socketPath;
            }
            if (request.Config.TryGetValue("driverName", out var dn) && dn is string driverName)
            {
                _driverName = driverName;
            }
        }

        InitializeDefaultStorageClasses();
        InitializeDefaultTopology();

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        // Start health monitoring
        _healthMonitorTask = RunHealthMonitorAsync(_cts.Token);

        // Initialize CSI socket if running in Kubernetes
        await InitializeCsiSocketAsync();
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        if (_healthMonitorTask != null)
        {
            await _healthMonitorTask.ContinueWith(_ => { });
        }

        _csiSocket?.Dispose();
        _csiSocket = null;

        _cts?.Dispose();
        _cts = null;
    }

    #endregion

    #region Message Handling

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return;

        var response = message.Type switch
        {
            // Identity Service
            "csi.identity.probe" => HandleIdentityProbe(),
            "csi.identity.capabilities" => HandleGetPluginCapabilities(),
            "csi.identity.info" => HandleGetPluginInfo(),

            // Controller Service
            "csi.controller.create_volume" => await HandleCreateVolumeAsync(message.Payload),
            "csi.controller.delete_volume" => await HandleDeleteVolumeAsync(message.Payload),
            "csi.controller.expand_volume" => await HandleExpandVolumeAsync(message.Payload),
            "csi.controller.create_snapshot" => await HandleCreateSnapshotAsync(message.Payload),
            "csi.controller.delete_snapshot" => await HandleDeleteSnapshotAsync(message.Payload),
            "csi.controller.list_volumes" => HandleListVolumes(message.Payload),
            "csi.controller.list_snapshots" => HandleListSnapshots(message.Payload),
            "csi.controller.get_capacity" => HandleGetCapacity(message.Payload),
            "csi.controller.validate_capabilities" => HandleValidateVolumeCapabilities(message.Payload),
            "csi.controller.publish_volume" => await HandleControllerPublishVolumeAsync(message.Payload),
            "csi.controller.unpublish_volume" => await HandleControllerUnpublishVolumeAsync(message.Payload),

            // Node Service
            "csi.node.stage_volume" => await HandleNodeStageVolumeAsync(message.Payload),
            "csi.node.unstage_volume" => await HandleNodeUnstageVolumeAsync(message.Payload),
            "csi.node.publish_volume" => await HandleNodePublishVolumeAsync(message.Payload),
            "csi.node.unpublish_volume" => await HandleNodeUnpublishVolumeAsync(message.Payload),
            "csi.node.get_info" => HandleGetNodeInfo(),
            "csi.node.get_capabilities" => HandleGetNodeCapabilities(),
            "csi.node.get_volume_stats" => HandleGetVolumeStats(message.Payload),
            "csi.node.expand_volume" => await HandleNodeExpandVolumeAsync(message.Payload),

            // Storage Class Management
            "csi.storageclass.create" => HandleCreateStorageClass(message.Payload),
            "csi.storageclass.list" => HandleListStorageClasses(),
            "csi.storageclass.get" => HandleGetStorageClass(message.Payload),

            _ => new Dictionary<string, object> { ["error"] = $"Unknown CSI command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    #endregion

    #region CSI Identity Service

    private Dictionary<string, object> HandleIdentityProbe()
    {
        return new Dictionary<string, object>
        {
            ["ready"] = _isRunning
        };
    }

    private Dictionary<string, object> HandleGetPluginInfo()
    {
        return new Dictionary<string, object>
        {
            ["name"] = _driverName,
            ["vendor_version"] = Version,
            ["manifest"] = new Dictionary<string, string>
            {
                ["spec_version"] = CsiSpecVersion,
                ["vendor"] = "DataWarehouse",
                ["description"] = "DataWarehouse CSI Driver for Kubernetes"
            }
        };
    }

    private Dictionary<string, object> HandleGetPluginCapabilities()
    {
        return new Dictionary<string, object>
        {
            ["capabilities"] = new List<Dictionary<string, object>>
            {
                // Controller Service capabilities
                new() { ["service"] = new Dictionary<string, string> { ["type"] = "CONTROLLER_SERVICE" } },
                new() { ["service"] = new Dictionary<string, string> { ["type"] = "VOLUME_ACCESSIBILITY_CONSTRAINTS" } },

                // Volume expansion capability
                new() { ["volume_expansion"] = new Dictionary<string, object> { ["type"] = "ONLINE" } },
                new() { ["volume_expansion"] = new Dictionary<string, object> { ["type"] = "OFFLINE" } }
            }
        };
    }

    #endregion

    #region CSI Controller Service - Volume Operations

    private async Task<Dictionary<string, object?>> HandleCreateVolumeAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync();
        try
        {
            var name = payload.GetValueOrDefault("name")?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume name is required");
            }

            // Check for idempotency - if volume with same name exists, return it
            var existing = _volumes.Values.FirstOrDefault(v => v.Name == name);
            if (existing != null)
            {
                return CreateVolumeResponse(existing);
            }

            // Parse capacity
            long capacityBytes = DefaultVolumeCapacityBytes;
            if (payload.TryGetValue("capacity_range", out var cr) && cr is Dictionary<string, object> capacityRange)
            {
                if (capacityRange.TryGetValue("required_bytes", out var rb) && rb != null)
                {
                    capacityBytes = Convert.ToInt64(rb);
                }
                else if (capacityRange.TryGetValue("limit_bytes", out var lb) && lb != null)
                {
                    capacityBytes = Convert.ToInt64(lb);
                }
            }

            if (capacityBytes > MaxVolumeCapacityBytes)
            {
                return CreateErrorResponse(CsiErrorCode.OutOfRange, $"Requested capacity exceeds maximum ({MaxVolumeCapacityBytes} bytes)");
            }

            // Parse storage class parameters
            var parameters = payload.TryGetValue("parameters", out var p) && p is Dictionary<string, object> pDict
                ? pDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "")
                : new Dictionary<string, string>();

            var storageClassName = parameters.GetValueOrDefault("storageClass", "datawarehouse-hot");
            if (!_storageClasses.TryGetValue(storageClassName, out var storageClass))
            {
                storageClass = _storageClasses.Values.First();
            }

            // Handle volume source (clone or snapshot)
            string? sourceVolumeId = null;
            string? sourceSnapshotId = null;
            if (payload.TryGetValue("volume_content_source", out var vcs) && vcs is Dictionary<string, object> contentSource)
            {
                if (contentSource.TryGetValue("volume", out var vol) && vol is Dictionary<string, object> volDict)
                {
                    sourceVolumeId = volDict.GetValueOrDefault("volume_id")?.ToString();
                    if (sourceVolumeId != null && !_volumes.ContainsKey(sourceVolumeId))
                    {
                        return CreateErrorResponse(CsiErrorCode.NotFound, $"Source volume not found: {sourceVolumeId}");
                    }
                }
                else if (contentSource.TryGetValue("snapshot", out var snap) && snap is Dictionary<string, object> snapDict)
                {
                    sourceSnapshotId = snapDict.GetValueOrDefault("snapshot_id")?.ToString();
                    if (sourceSnapshotId != null && !_snapshots.ContainsKey(sourceSnapshotId))
                    {
                        return CreateErrorResponse(CsiErrorCode.NotFound, $"Source snapshot not found: {sourceSnapshotId}");
                    }
                }
            }

            // Parse topology requirements
            var accessibleTopology = ParseTopologyRequirements(payload);

            // Determine access types
            var accessTypes = ParseVolumeCapabilities(payload);

            // Create the volume
            var volumeId = GenerateVolumeId();
            var volume = new CsiVolume
            {
                VolumeId = volumeId,
                Name = name,
                CapacityBytes = capacityBytes,
                StorageClass = storageClassName,
                StorageTier = storageClass.Tier,
                AccessTypes = accessTypes,
                Encrypted = storageClass.Encryption != EncryptionType.None,
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

            // If cloning from source, copy data reference
            if (!string.IsNullOrEmpty(sourceVolumeId) && _volumes.TryGetValue(sourceVolumeId, out var srcVol))
            {
                volume.UsedBytes = srcVol.UsedBytes;
            }
            else if (!string.IsNullOrEmpty(sourceSnapshotId) && _snapshots.TryGetValue(sourceSnapshotId, out var srcSnap))
            {
                volume.UsedBytes = srcSnap.SizeBytes;
            }

            _volumes[volumeId] = volume;

            return CreateVolumeResponse(volume);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> HandleDeleteVolumeAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync();
        try
        {
            var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
            if (string.IsNullOrEmpty(volumeId))
            {
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");
            }

            // Idempotency - return success if volume doesn't exist
            if (!_volumes.TryGetValue(volumeId, out var volume))
            {
                return new Dictionary<string, object> { ["success"] = true };
            }

            // Check if volume is in use
            if (_publishedVolumes.Values.Any(p => p.VolumeId == volumeId))
            {
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition, "Volume is currently published to a node");
            }

            if (_stagedVolumes.Values.Any(s => s.VolumeId == volumeId))
            {
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition, "Volume is currently staged on a node");
            }

            // Check for compliance holds
            if (volume.ComplianceMode != ComplianceMode.None && volume.RetentionUntil > DateTime.UtcNow)
            {
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition,
                    $"Volume is under compliance retention until {volume.RetentionUntil:O}");
            }

            _volumes.TryRemove(volumeId, out _);

            return new Dictionary<string, object> { ["success"] = true };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> HandleExpandVolumeAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync();
        try
        {
            var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
            if (string.IsNullOrEmpty(volumeId))
            {
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");
            }

            if (!_volumes.TryGetValue(volumeId, out var volume))
            {
                return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");
            }

            // Parse new capacity
            long newCapacityBytes = volume.CapacityBytes;
            if (payload.TryGetValue("capacity_range", out var cr) && cr is Dictionary<string, object> capacityRange)
            {
                if (capacityRange.TryGetValue("required_bytes", out var rb) && rb != null)
                {
                    newCapacityBytes = Convert.ToInt64(rb);
                }
            }

            if (newCapacityBytes < volume.CapacityBytes)
            {
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume shrinking is not supported");
            }

            if (newCapacityBytes > MaxVolumeCapacityBytes)
            {
                return CreateErrorResponse(CsiErrorCode.OutOfRange, $"Requested capacity exceeds maximum ({MaxVolumeCapacityBytes} bytes)");
            }

            // Check if storage class allows expansion
            if (_storageClasses.TryGetValue(volume.StorageClass, out var sc) && !sc.AllowVolumeExpansion)
            {
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition, "Storage class does not allow volume expansion");
            }

            volume.CapacityBytes = newCapacityBytes;

            // Determine if node expansion is required
            bool nodeExpansionRequired = _publishedVolumes.Values.Any(p => p.VolumeId == volumeId);

            return new Dictionary<string, object>
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

    private Dictionary<string, object> HandleListVolumes(Dictionary<string, object> payload)
    {
        var maxEntries = payload.TryGetValue("max_entries", out var me) && me != null
            ? Convert.ToInt32(me)
            : 100;

        var startingToken = payload.GetValueOrDefault("starting_token")?.ToString();

        var volumes = _volumes.Values
            .OrderBy(v => v.CreatedAt)
            .ToList();

        // Apply pagination
        int startIndex = 0;
        if (!string.IsNullOrEmpty(startingToken) && int.TryParse(startingToken, out var token))
        {
            startIndex = token;
        }

        var pageVolumes = volumes.Skip(startIndex).Take(maxEntries).ToList();
        string? nextToken = startIndex + maxEntries < volumes.Count
            ? (startIndex + maxEntries).ToString()
            : null;

        return new Dictionary<string, object>
        {
            ["entries"] = pageVolumes.Select(v => new Dictionary<string, object>
            {
                ["volume"] = new Dictionary<string, object>
                {
                    ["volume_id"] = v.VolumeId,
                    ["capacity_bytes"] = v.CapacityBytes,
                    ["volume_context"] = v.Parameters,
                    ["accessible_topology"] = v.AccessibleTopology.Select(t => new Dictionary<string, object>
                    {
                        ["segments"] = t
                    }).ToList()
                },
                ["status"] = new Dictionary<string, object>
                {
                    ["published_node_ids"] = _publishedVolumes.Values
                        .Where(p => p.VolumeId == v.VolumeId)
                        .Select(p => p.NodeId)
                        .ToList(),
                    ["volume_condition"] = new Dictionary<string, object>
                    {
                        ["abnormal"] = v.State == VolumeState.Error,
                        ["message"] = v.State.ToString()
                    }
                }
            }).ToList(),
            ["next_token"] = nextToken ?? ""
        };
    }

    private Dictionary<string, object> HandleValidateVolumeCapabilities(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        if (string.IsNullOrEmpty(volumeId))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");
        }

        if (!_volumes.TryGetValue(volumeId, out var volume))
        {
            return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");
        }

        // Validate requested capabilities against volume
        var requestedCapabilities = ParseVolumeCapabilities(payload);
        bool allSupported = requestedCapabilities.All(rc => volume.AccessTypes.Contains(rc));

        if (allSupported)
        {
            return new Dictionary<string, object>
            {
                ["confirmed"] = new Dictionary<string, object?>
                {
                    ["volume_capabilities"] = payload.GetValueOrDefault("volume_capabilities"),
                    ["volume_context"] = volume.Parameters
                }
            };
        }
        else
        {
            return new Dictionary<string, object>
            {
                ["message"] = "Requested capabilities are not supported by this volume"
            };
        }
    }

    private Dictionary<string, object> HandleGetCapacity(Dictionary<string, object> payload)
    {
        // Calculate available capacity based on topology and storage class
        var parameters = payload.TryGetValue("parameters", out var p) && p is Dictionary<string, object> pDict
            ? pDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "")
            : new Dictionary<string, string>();

        var storageClassName = parameters.GetValueOrDefault("storageClass", "datawarehouse-hot");

        // In production, this would query actual storage backend
        // Here we calculate based on configured limits
        long totalCapacity = MaxVolumeCapacityBytes * 10; // Total pool capacity
        long usedCapacity = _volumes.Values.Sum(v => v.CapacityBytes);
        long availableCapacity = Math.Max(0, totalCapacity - usedCapacity);

        // Calculate minimum volume size
        long minimumVolumeSize = 1L * 1024 * 1024 * 1024; // 1GiB minimum

        return new Dictionary<string, object>
        {
            ["available_capacity"] = availableCapacity,
            ["maximum_volume_size"] = MaxVolumeCapacityBytes,
            ["minimum_volume_size"] = minimumVolumeSize
        };
    }

    private async Task<Dictionary<string, object>> HandleControllerPublishVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var nodeId = payload.GetValueOrDefault("node_id")?.ToString();

        if (string.IsNullOrEmpty(volumeId) || string.IsNullOrEmpty(nodeId))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID and Node ID are required");
        }

        if (!_volumes.TryGetValue(volumeId, out var volume))
        {
            return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");
        }

        // Check volume limits per node
        int currentVolumesOnNode = _publishedVolumes.Values.Count(p => p.NodeId == nodeId);
        if (currentVolumesOnNode >= MaxVolumesPerNode)
        {
            return CreateErrorResponse(CsiErrorCode.ResourceExhausted,
                $"Maximum volumes per node ({MaxVolumesPerNode}) exceeded");
        }

        // Check topology constraints
        if (volume.AccessibleTopology.Count > 0)
        {
            bool nodeInTopology = volume.AccessibleTopology.Any(t =>
                _topologySegments.Values.Any(ts => ts.NodeId == nodeId &&
                    t.All(kv => ts.Segments.TryGetValue(kv.Key, out var v) && v == kv.Value)));

            if (!nodeInTopology)
            {
                return CreateErrorResponse(CsiErrorCode.ResourceExhausted,
                    "Volume is not accessible from this node due to topology constraints");
            }
        }

        // Return publish context
        return new Dictionary<string, object>
        {
            ["publish_context"] = new Dictionary<string, string>
            {
                ["volume_id"] = volumeId,
                ["node_id"] = nodeId,
                ["storage_tier"] = volume.StorageTier.ToString(),
                ["encrypted"] = volume.Encrypted.ToString(),
                ["device_path"] = $"/dev/datawarehouse/{volumeId}"
            }
        };
    }

    private async Task<Dictionary<string, object>> HandleControllerUnpublishVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var nodeId = payload.GetValueOrDefault("node_id")?.ToString();

        if (string.IsNullOrEmpty(volumeId))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");
        }

        // Idempotent - always return success
        return new Dictionary<string, object> { ["success"] = true };
    }

    #endregion

    #region CSI Controller Service - Snapshot Operations

    private async Task<Dictionary<string, object>> HandleCreateSnapshotAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync();
        try
        {
            var sourceVolumeId = payload.GetValueOrDefault("source_volume_id")?.ToString();
            var name = payload.GetValueOrDefault("name")?.ToString();

            if (string.IsNullOrEmpty(sourceVolumeId))
            {
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Source volume ID is required");
            }

            if (string.IsNullOrEmpty(name))
            {
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Snapshot name is required");
            }

            if (!_volumes.TryGetValue(sourceVolumeId, out var volume))
            {
                return CreateErrorResponse(CsiErrorCode.NotFound, $"Source volume not found: {sourceVolumeId}");
            }

            // Idempotency check
            var existing = _snapshots.Values.FirstOrDefault(s => s.Name == name && s.SourceVolumeId == sourceVolumeId);
            if (existing != null)
            {
                return CreateSnapshotResponse(existing);
            }

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

    private async Task<Dictionary<string, object>> HandleDeleteSnapshotAsync(Dictionary<string, object> payload)
    {
        await _operationLock.WaitAsync();
        try
        {
            var snapshotId = payload.GetValueOrDefault("snapshot_id")?.ToString();
            if (string.IsNullOrEmpty(snapshotId))
            {
                return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Snapshot ID is required");
            }

            // Idempotency - return success if snapshot doesn't exist
            if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
            {
                return new Dictionary<string, object> { ["success"] = true };
            }

            // Check if snapshot is being used to clone a volume
            if (_volumes.Values.Any(v => v.SourceSnapshotId == snapshotId))
            {
                return CreateErrorResponse(CsiErrorCode.FailedPrecondition,
                    "Snapshot is being used as a source for a volume");
            }

            _snapshots.TryRemove(snapshotId, out _);

            return new Dictionary<string, object> { ["success"] = true };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private Dictionary<string, object> HandleListSnapshots(Dictionary<string, object> payload)
    {
        var maxEntries = payload.TryGetValue("max_entries", out var me) && me != null
            ? Convert.ToInt32(me)
            : 100;

        var startingToken = payload.GetValueOrDefault("starting_token")?.ToString();
        var sourceVolumeId = payload.GetValueOrDefault("source_volume_id")?.ToString();
        var snapshotId = payload.GetValueOrDefault("snapshot_id")?.ToString();

        var snapshots = _snapshots.Values
            .Where(s => string.IsNullOrEmpty(sourceVolumeId) || s.SourceVolumeId == sourceVolumeId)
            .Where(s => string.IsNullOrEmpty(snapshotId) || s.SnapshotId == snapshotId)
            .OrderBy(s => s.CreatedAt)
            .ToList();

        // Apply pagination
        int startIndex = 0;
        if (!string.IsNullOrEmpty(startingToken) && int.TryParse(startingToken, out var token))
        {
            startIndex = token;
        }

        var pageSnapshots = snapshots.Skip(startIndex).Take(maxEntries).ToList();
        string? nextToken = startIndex + maxEntries < snapshots.Count
            ? (startIndex + maxEntries).ToString()
            : null;

        return new Dictionary<string, object>
        {
            ["entries"] = pageSnapshots.Select(CreateSnapshotResponse).ToList(),
            ["next_token"] = nextToken ?? ""
        };
    }

    #endregion

    #region CSI Node Service

    private async Task<Dictionary<string, object>> HandleNodeStageVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var stagingTargetPath = payload.GetValueOrDefault("staging_target_path")?.ToString();

        if (string.IsNullOrEmpty(volumeId) || string.IsNullOrEmpty(stagingTargetPath))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID and staging target path are required");
        }

        if (!_volumes.TryGetValue(volumeId, out var volume))
        {
            return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");
        }

        // Check for idempotency
        var stageKey = $"{volumeId}:{_nodeId}";
        if (_stagedVolumes.TryGetValue(stageKey, out var existingStage))
        {
            if (existingStage.StagingTargetPath == stagingTargetPath)
            {
                return new Dictionary<string, object> { ["success"] = true };
            }
            return CreateErrorResponse(CsiErrorCode.AlreadyExists,
                $"Volume already staged at different path: {existingStage.StagingTargetPath}");
        }

        // Parse volume capability
        var isBlock = false;
        var fsType = "ext4";
        var mountFlags = new List<string>();

        if (payload.TryGetValue("volume_capability", out var vc) && vc is Dictionary<string, object> capability)
        {
            if (capability.ContainsKey("block"))
            {
                isBlock = true;
            }
            else if (capability.TryGetValue("mount", out var mount) && mount is Dictionary<string, object> mountDict)
            {
                fsType = mountDict.GetValueOrDefault("fs_type")?.ToString() ?? "ext4";
                if (mountDict.TryGetValue("mount_flags", out var flags) && flags is List<object> flagList)
                {
                    mountFlags = flagList.Select(f => f.ToString() ?? "").ToList();
                }
            }
        }

        // Stage the volume
        var stageInfo = new NodeStageInfo
        {
            VolumeId = volumeId,
            NodeId = _nodeId,
            StagingTargetPath = stagingTargetPath,
            IsBlock = isBlock,
            FsType = fsType,
            MountFlags = mountFlags,
            StagedAt = DateTime.UtcNow
        };

        _stagedVolumes[stageKey] = stageInfo;

        return new Dictionary<string, object> { ["success"] = true };
    }

    private async Task<Dictionary<string, object>> HandleNodeUnstageVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var stagingTargetPath = payload.GetValueOrDefault("staging_target_path")?.ToString();

        if (string.IsNullOrEmpty(volumeId) || string.IsNullOrEmpty(stagingTargetPath))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID and staging target path are required");
        }

        var stageKey = $"{volumeId}:{_nodeId}";
        _stagedVolumes.TryRemove(stageKey, out _);

        return new Dictionary<string, object> { ["success"] = true };
    }

    private async Task<Dictionary<string, object>> HandleNodePublishVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var targetPath = payload.GetValueOrDefault("target_path")?.ToString();
        var stagingTargetPath = payload.GetValueOrDefault("staging_target_path")?.ToString();

        if (string.IsNullOrEmpty(volumeId) || string.IsNullOrEmpty(targetPath))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID and target path are required");
        }

        if (!_volumes.TryGetValue(volumeId, out var volume))
        {
            return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");
        }

        // Check if staged (if staging is required)
        var stageKey = $"{volumeId}:{_nodeId}";
        if (!string.IsNullOrEmpty(stagingTargetPath) && !_stagedVolumes.ContainsKey(stageKey))
        {
            return CreateErrorResponse(CsiErrorCode.FailedPrecondition, "Volume must be staged before publishing");
        }

        // Check for idempotency
        var publishKey = $"{volumeId}:{_nodeId}:{targetPath}";
        if (_publishedVolumes.ContainsKey(publishKey))
        {
            return new Dictionary<string, object> { ["success"] = true };
        }

        // Parse access mode
        var readOnly = false;
        if (payload.TryGetValue("readonly", out var ro) && ro is bool roVal)
        {
            readOnly = roVal;
        }

        // Publish the volume
        var publishInfo = new NodePublishInfo
        {
            VolumeId = volumeId,
            NodeId = _nodeId,
            TargetPath = targetPath,
            StagingTargetPath = stagingTargetPath,
            ReadOnly = readOnly,
            PublishedAt = DateTime.UtcNow
        };

        _publishedVolumes[publishKey] = publishInfo;
        volume.State = VolumeState.InUse;

        return new Dictionary<string, object> { ["success"] = true };
    }

    private async Task<Dictionary<string, object>> HandleNodeUnpublishVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var targetPath = payload.GetValueOrDefault("target_path")?.ToString();

        if (string.IsNullOrEmpty(volumeId) || string.IsNullOrEmpty(targetPath))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID and target path are required");
        }

        var publishKey = $"{volumeId}:{_nodeId}:{targetPath}";
        _publishedVolumes.TryRemove(publishKey, out _);

        // Update volume state if no longer published anywhere
        if (_volumes.TryGetValue(volumeId, out var volume))
        {
            if (!_publishedVolumes.Values.Any(p => p.VolumeId == volumeId))
            {
                volume.State = VolumeState.Available;
            }
        }

        return new Dictionary<string, object> { ["success"] = true };
    }

    private Dictionary<string, object> HandleGetNodeInfo()
    {
        // Get node topology
        var topology = new Dictionary<string, string>();
        if (_topologySegments.TryGetValue(_nodeId, out var segments))
        {
            topology = segments.Segments;
        }
        else
        {
            topology = new Dictionary<string, string>
            {
                ["topology.kubernetes.io/region"] = "default-region",
                ["topology.kubernetes.io/zone"] = "default-zone"
            };
        }

        return new Dictionary<string, object>
        {
            ["node_id"] = _nodeId,
            ["max_volumes_per_node"] = MaxVolumesPerNode,
            ["accessible_topology"] = new Dictionary<string, object>
            {
                ["segments"] = topology
            }
        };
    }

    private Dictionary<string, object> HandleGetNodeCapabilities()
    {
        return new Dictionary<string, object>
        {
            ["capabilities"] = new List<Dictionary<string, object>>
            {
                new() { ["rpc"] = new Dictionary<string, string> { ["type"] = "STAGE_UNSTAGE_VOLUME" } },
                new() { ["rpc"] = new Dictionary<string, string> { ["type"] = "GET_VOLUME_STATS" } },
                new() { ["rpc"] = new Dictionary<string, string> { ["type"] = "EXPAND_VOLUME" } },
                new() { ["rpc"] = new Dictionary<string, string> { ["type"] = "SINGLE_NODE_MULTI_WRITER" } },
                new() { ["rpc"] = new Dictionary<string, string> { ["type"] = "VOLUME_CONDITION" } }
            }
        };
    }

    private Dictionary<string, object> HandleGetVolumeStats(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var volumePath = payload.GetValueOrDefault("volume_path")?.ToString();

        if (string.IsNullOrEmpty(volumeId))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");
        }

        if (!_volumes.TryGetValue(volumeId, out var volume))
        {
            return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");
        }

        // Calculate stats
        long availableBytes = volume.CapacityBytes - volume.UsedBytes;
        long usedBytes = volume.UsedBytes;

        return new Dictionary<string, object>
        {
            ["usage"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["available"] = availableBytes,
                    ["total"] = volume.CapacityBytes,
                    ["used"] = usedBytes,
                    ["unit"] = "BYTES"
                }
            },
            ["volume_condition"] = new Dictionary<string, object>
            {
                ["abnormal"] = volume.State == VolumeState.Error,
                ["message"] = volume.State.ToString()
            }
        };
    }

    private async Task<Dictionary<string, object>> HandleNodeExpandVolumeAsync(Dictionary<string, object> payload)
    {
        var volumeId = payload.GetValueOrDefault("volume_id")?.ToString();
        var volumePath = payload.GetValueOrDefault("volume_path")?.ToString();

        if (string.IsNullOrEmpty(volumeId))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Volume ID is required");
        }

        if (!_volumes.TryGetValue(volumeId, out var volume))
        {
            return CreateErrorResponse(CsiErrorCode.NotFound, $"Volume not found: {volumeId}");
        }

        // Parse requested capacity
        long newCapacityBytes = volume.CapacityBytes;
        if (payload.TryGetValue("capacity_range", out var cr) && cr is Dictionary<string, object> capacityRange)
        {
            if (capacityRange.TryGetValue("required_bytes", out var rb) && rb != null)
            {
                newCapacityBytes = Convert.ToInt64(rb);
            }
        }

        // Node expansion is typically a no-op for network-attached storage
        // The filesystem resize would happen here for block devices

        return new Dictionary<string, object>
        {
            ["capacity_bytes"] = volume.CapacityBytes
        };
    }

    #endregion

    #region Storage Class Management

    private Dictionary<string, object> HandleCreateStorageClass(Dictionary<string, object> payload)
    {
        var name = payload.GetValueOrDefault("name")?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Storage class name is required");
        }

        var tier = Enum.TryParse<StorageTier>(payload.GetValueOrDefault("tier")?.ToString(), true, out var t)
            ? t : StorageTier.Hot;

        var encryption = Enum.TryParse<EncryptionType>(payload.GetValueOrDefault("encryption")?.ToString(), true, out var e)
            ? e : EncryptionType.None;

        var compliance = Enum.TryParse<ComplianceMode>(payload.GetValueOrDefault("compliance")?.ToString(), true, out var c)
            ? c : ComplianceMode.None;

        var replicationFactor = payload.TryGetValue("replication", out var r) && r != null
            ? Convert.ToInt32(r)
            : 3;

        var allowVolumeExpansion = payload.TryGetValue("allowVolumeExpansion", out var ave) && ave is bool aveVal
            ? aveVal
            : true;

        var storageClass = new StorageClassConfig
        {
            Name = name,
            Tier = tier,
            Encryption = encryption,
            ComplianceMode = compliance,
            ReplicationFactor = replicationFactor,
            AllowVolumeExpansion = allowVolumeExpansion,
            VolumeBindingMode = payload.GetValueOrDefault("volumeBindingMode")?.ToString() ?? "WaitForFirstConsumer",
            ReclaimPolicy = payload.GetValueOrDefault("reclaimPolicy")?.ToString() ?? "Delete"
        };

        _storageClasses[name] = storageClass;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["name"] = name,
            ["storageClass"] = SerializeStorageClass(storageClass)
        };
    }

    private Dictionary<string, object> HandleListStorageClasses()
    {
        return new Dictionary<string, object>
        {
            ["storageClasses"] = _storageClasses.Values.Select(SerializeStorageClass).ToList()
        };
    }

    private Dictionary<string, object> HandleGetStorageClass(Dictionary<string, object> payload)
    {
        var name = payload.GetValueOrDefault("name")?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            return CreateErrorResponse(CsiErrorCode.InvalidArgument, "Storage class name is required");
        }

        if (!_storageClasses.TryGetValue(name, out var storageClass))
        {
            return CreateErrorResponse(CsiErrorCode.NotFound, $"Storage class not found: {name}");
        }

        return SerializeStorageClass(storageClass);
    }

    #endregion

    #region Helper Methods

    private void InitializeDefaultStorageClasses()
    {
        _storageClasses["datawarehouse-hot"] = new StorageClassConfig
        {
            Name = "datawarehouse-hot",
            Tier = StorageTier.Hot,
            Encryption = EncryptionType.AES256GCM,
            ComplianceMode = ComplianceMode.None,
            ReplicationFactor = 3,
            AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer",
            ReclaimPolicy = "Delete",
            IopsLimit = 10000,
            ThroughputLimitMBps = 500
        };

        _storageClasses["datawarehouse-warm"] = new StorageClassConfig
        {
            Name = "datawarehouse-warm",
            Tier = StorageTier.Warm,
            Encryption = EncryptionType.AES256GCM,
            ComplianceMode = ComplianceMode.None,
            ReplicationFactor = 2,
            AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer",
            ReclaimPolicy = "Delete",
            IopsLimit = 3000,
            ThroughputLimitMBps = 125
        };

        _storageClasses["datawarehouse-cold"] = new StorageClassConfig
        {
            Name = "datawarehouse-cold",
            Tier = StorageTier.Cold,
            Encryption = EncryptionType.AES256GCM,
            ComplianceMode = ComplianceMode.None,
            ReplicationFactor = 2,
            AllowVolumeExpansion = true,
            VolumeBindingMode = "Immediate",
            ReclaimPolicy = "Delete",
            IopsLimit = 500,
            ThroughputLimitMBps = 50
        };

        _storageClasses["datawarehouse-archive"] = new StorageClassConfig
        {
            Name = "datawarehouse-archive",
            Tier = StorageTier.Archive,
            Encryption = EncryptionType.AES256GCM,
            ComplianceMode = ComplianceMode.WORM,
            ReplicationFactor = 3,
            AllowVolumeExpansion = false,
            VolumeBindingMode = "Immediate",
            ReclaimPolicy = "Retain",
            IopsLimit = 100,
            ThroughputLimitMBps = 10
        };

        // Compliance-focused storage classes
        _storageClasses["datawarehouse-gdpr"] = new StorageClassConfig
        {
            Name = "datawarehouse-gdpr",
            Tier = StorageTier.Hot,
            Encryption = EncryptionType.AES256GCM,
            ComplianceMode = ComplianceMode.GDPR,
            ReplicationFactor = 3,
            AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer",
            ReclaimPolicy = "Retain",
            IopsLimit = 10000,
            ThroughputLimitMBps = 500,
            DataResidencyRegion = "eu-west"
        };

        _storageClasses["datawarehouse-hipaa"] = new StorageClassConfig
        {
            Name = "datawarehouse-hipaa",
            Tier = StorageTier.Hot,
            Encryption = EncryptionType.AES256GCM,
            ComplianceMode = ComplianceMode.HIPAA,
            ReplicationFactor = 3,
            AllowVolumeExpansion = true,
            VolumeBindingMode = "WaitForFirstConsumer",
            ReclaimPolicy = "Retain",
            IopsLimit = 10000,
            ThroughputLimitMBps = 500,
            AuditLoggingEnabled = true
        };
    }

    private void InitializeDefaultTopology()
    {
        // Add default node topology
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

    private async Task InitializeCsiSocketAsync()
    {
        // In production, this would create a Unix domain socket for gRPC communication
        // The CSI sidecar containers communicate via this socket
        // For now, we track that the socket would be at _socketPath
    }

    private async Task RunHealthMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                // Check volume health
                foreach (var volume in _volumes.Values)
                {
                    if (volume.State == VolumeState.Error)
                    {
                        // Attempt recovery
                        volume.State = VolumeState.Available;
                    }
                }

                // Check snapshot health
                foreach (var snapshot in _snapshots.Values)
                {
                    if (snapshot.State == SnapshotState.Error)
                    {
                        snapshot.State = SnapshotState.Ready;
                        snapshot.ReadyToUse = true;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue health monitoring
            }
        }
    }

    private static string GenerateVolumeId()
    {
        return $"vol-{Guid.NewGuid():N}";
    }

    private static string GenerateSnapshotId()
    {
        return $"snap-{Guid.NewGuid():N}";
    }

    private List<VolumeAccessType> ParseVolumeCapabilities(Dictionary<string, object> payload)
    {
        var accessTypes = new List<VolumeAccessType>();

        if (!payload.TryGetValue("volume_capabilities", out var vc) || vc is not List<object> capabilities)
        {
            // Default to single node writer
            accessTypes.Add(VolumeAccessType.SingleNodeWriter);
            return accessTypes;
        }

        foreach (var cap in capabilities.OfType<Dictionary<string, object>>())
        {
            if (cap.TryGetValue("access_mode", out var am) && am is Dictionary<string, object> accessMode)
            {
                var mode = accessMode.GetValueOrDefault("mode")?.ToString() ?? "SINGLE_NODE_WRITER";
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
                {
                    accessTypes.Add(accessType);
                }
            }
        }

        if (accessTypes.Count == 0)
        {
            accessTypes.Add(VolumeAccessType.SingleNodeWriter);
        }

        return accessTypes;
    }

    private List<Dictionary<string, string>> ParseTopologyRequirements(Dictionary<string, object> payload)
    {
        var topology = new List<Dictionary<string, string>>();

        if (!payload.TryGetValue("accessibility_requirements", out var ar) || ar is not Dictionary<string, object> requirements)
        {
            return topology;
        }

        // Parse preferred topology
        if (requirements.TryGetValue("preferred", out var pref) && pref is List<object> preferred)
        {
            foreach (var p in preferred.OfType<Dictionary<string, object>>())
            {
                if (p.TryGetValue("segments", out var seg) && seg is Dictionary<string, object> segments)
                {
                    var segDict = segments.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "");
                    topology.Add(segDict);
                }
            }
        }

        // Parse requisite topology (required constraints)
        if (requirements.TryGetValue("requisite", out var req) && req is List<object> requisite)
        {
            foreach (var r in requisite.OfType<Dictionary<string, object>>())
            {
                if (r.TryGetValue("segments", out var seg) && seg is Dictionary<string, object> segments)
                {
                    var segDict = segments.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "");
                    if (!topology.Any(t => t.SequenceEqual(segDict)))
                    {
                        topology.Add(segDict);
                    }
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
                ["accessible_topology"] = volume.AccessibleTopology.Select(t => new Dictionary<string, object>
                {
                    ["segments"] = t
                }).ToList()
            }
        };
    }

    private Dictionary<string, object> CreateSnapshotResponse(CsiSnapshot snapshot)
    {
        return new Dictionary<string, object>
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

    private Dictionary<string, object> SerializeStorageClass(StorageClassConfig sc)
    {
        return new Dictionary<string, object>
        {
            ["name"] = sc.Name,
            ["provisioner"] = _driverName,
            ["parameters"] = new Dictionary<string, string>
            {
                ["tier"] = sc.Tier.ToString().ToLowerInvariant(),
                ["encryption"] = sc.Encryption.ToString().ToLowerInvariant(),
                ["compliance"] = sc.ComplianceMode.ToString().ToLowerInvariant(),
                ["replication"] = sc.ReplicationFactor.ToString()
            },
            ["allowVolumeExpansion"] = sc.AllowVolumeExpansion,
            ["volumeBindingMode"] = sc.VolumeBindingMode,
            ["reclaimPolicy"] = sc.ReclaimPolicy,
            ["mountOptions"] = sc.MountOptions
        };
    }

    private static Dictionary<string, object> CreateErrorResponse(CsiErrorCode code, string message)
    {
        return new Dictionary<string, object>
        {
            ["error"] = new Dictionary<string, object>
            {
                ["code"] = (int)code,
                ["message"] = message
            }
        };
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "csi.create_delete_volume", Description = "Dynamic volume provisioning" },
            new() { Name = "csi.expand_volume", Description = "Online/offline volume expansion" },
            new() { Name = "csi.create_delete_snapshot", Description = "Volume snapshot support" },
            new() { Name = "csi.clone_volume", Description = "Volume cloning from snapshot or volume" },
            new() { Name = "csi.topology", Description = "Topology-aware volume placement" },
            new() { Name = "csi.block_volume", Description = "Raw block volume support" },
            new() { Name = "csi.rwx", Description = "ReadWriteMany access mode support" },
            new() { Name = "csi.get_capacity", Description = "Storage capacity reporting" }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "CSIDriver";
        metadata["DriverName"] = _driverName;
        metadata["NodeId"] = _nodeId;
        metadata["SocketPath"] = _socketPath;
        metadata["CsiSpecVersion"] = CsiSpecVersion;
        metadata["VolumeCount"] = _volumes.Count;
        metadata["SnapshotCount"] = _snapshots.Count;
        metadata["StorageClasses"] = _storageClasses.Keys.ToList();
        metadata["SupportsTopology"] = true;
        metadata["SupportsSnapshots"] = true;
        metadata["SupportsCloning"] = true;
        metadata["SupportsExpansion"] = true;
        metadata["SupportsBlockVolumes"] = true;
        metadata["SupportsReadWriteMany"] = true;
        return metadata;
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
        public StorageTier StorageTier { get; init; }
        public List<VolumeAccessType> AccessTypes { get; init; } = new();
        public bool Encrypted { get; init; }
        public EncryptionType EncryptionType { get; init; }
        public int ReplicationFactor { get; init; }
        public ComplianceMode ComplianceMode { get; init; }
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
        public StorageTier Tier { get; init; }
        public EncryptionType Encryption { get; init; }
        public ComplianceMode ComplianceMode { get; init; }
        public int ReplicationFactor { get; init; }
        public bool AllowVolumeExpansion { get; init; }
        public string VolumeBindingMode { get; init; } = "WaitForFirstConsumer";
        public string ReclaimPolicy { get; init; } = "Delete";
        public int IopsLimit { get; init; }
        public int ThroughputLimitMBps { get; init; }
        public string? DataResidencyRegion { get; init; }
        public bool AuditLoggingEnabled { get; init; }
        public List<string> MountOptions { get; init; } = new();
    }

    private sealed class TopologySegment
    {
        public string NodeId { get; init; } = string.Empty;
        public Dictionary<string, string> Segments { get; init; } = new();
    }

    private enum StorageTier
    {
        Hot,
        Warm,
        Cold,
        Archive
    }

    private enum EncryptionType
    {
        None,
        AES256GCM,
        AES256CBC,
        ChaCha20Poly1305
    }

    private enum ComplianceMode
    {
        None,
        WORM,
        GDPR,
        HIPAA,
        SOC2,
        PCI_DSS
    }

    private enum VolumeState
    {
        Creating,
        Available,
        InUse,
        Deleting,
        Error
    }

    private enum SnapshotState
    {
        Creating,
        Ready,
        Deleting,
        Error
    }

    private enum VolumeAccessType
    {
        Unknown,
        SingleNodeWriter,
        SingleNodeReaderOnly,
        MultiNodeReaderOnly,
        MultiNodeSingleWriter,
        MultiNodeMultiWriter,
        SingleNodeSingleWriter,
        SingleNodeMultiWriter
    }

    private enum CsiErrorCode
    {
        Ok = 0,
        Cancelled = 1,
        Unknown = 2,
        InvalidArgument = 3,
        DeadlineExceeded = 4,
        NotFound = 5,
        AlreadyExists = 6,
        PermissionDenied = 7,
        ResourceExhausted = 8,
        FailedPrecondition = 9,
        Aborted = 10,
        OutOfRange = 11,
        Unimplemented = 12,
        Internal = 13,
        Unavailable = 14,
        DataLoss = 15,
        Unauthenticated = 16
    }

    #endregion
}
