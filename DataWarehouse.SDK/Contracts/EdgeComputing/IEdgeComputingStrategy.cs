// <copyright file="IEdgeComputingStrategy.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.EdgeComputing;

#region Core Interface

/// <summary>
/// Core interface for edge computing strategies.
/// Provides unified edge node management, data synchronization, offline support,
/// edge-to-cloud communication, analytics, security, resource management, and orchestration.
/// </summary>
public interface IEdgeComputingStrategy
{
    /// <summary>Gets the unique strategy identifier.</summary>
    string StrategyId { get; }

    /// <summary>Gets the human-readable strategy name.</summary>
    string Name { get; }

    /// <summary>Gets the strategy capabilities.</summary>
    EdgeComputingCapabilities Capabilities { get; }

    /// <summary>Gets the current status of the edge computing infrastructure.</summary>
    EdgeComputingStatus Status { get; }

    /// <summary>
    /// Initializes the edge computing strategy.
    /// </summary>
    Task InitializeAsync(EdgeComputingConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Shuts down the edge computing strategy.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct = default);
}

#endregion

#region 109.1: Edge Node Management

/// <summary>
/// Interface for edge node management operations.
/// </summary>
public interface IEdgeNodeManager
{
    /// <summary>
    /// Registers a new edge node with the management system.
    /// </summary>
    Task<EdgeNodeRegistration> RegisterNodeAsync(EdgeNodeInfo node, CancellationToken ct = default);

    /// <summary>
    /// Deregisters an edge node from the management system.
    /// </summary>
    Task<bool> DeregisterNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Gets information about a specific edge node.
    /// </summary>
    Task<EdgeNodeInfo?> GetNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Lists all registered edge nodes with optional filtering.
    /// </summary>
    IAsyncEnumerable<EdgeNodeInfo> ListNodesAsync(EdgeNodeFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Updates edge node configuration.
    /// </summary>
    Task<bool> UpdateNodeConfigAsync(string nodeId, EdgeNodeConfig config, CancellationToken ct = default);

    /// <summary>
    /// Gets health status of an edge node.
    /// </summary>
    Task<EdgeNodeHealth> GetNodeHealthAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Performs discovery to find new edge nodes in the network.
    /// </summary>
    IAsyncEnumerable<EdgeNodeInfo> DiscoverNodesAsync(EdgeDiscoveryOptions options, CancellationToken ct = default);

    /// <summary>
    /// Groups edge nodes into clusters for coordinated management.
    /// </summary>
    Task<EdgeCluster> CreateClusterAsync(string clusterId, IEnumerable<string> nodeIds, CancellationToken ct = default);

    /// <summary>
    /// Event raised when an edge node status changes.
    /// </summary>
    event EventHandler<EdgeNodeStatusChangedEventArgs>? NodeStatusChanged;
}

#endregion

#region 109.2: Data Synchronization with Edge

/// <summary>
/// Interface for data synchronization between edge nodes and cloud.
/// </summary>
public interface IEdgeDataSynchronizer
{
    /// <summary>
    /// Synchronizes data from cloud to edge node.
    /// </summary>
    Task<SyncResult> SyncToEdgeAsync(string nodeId, string dataId, ReadOnlyMemory<byte> data, SyncOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Synchronizes data from edge node to cloud.
    /// </summary>
    Task<SyncResult> SyncToCloudAsync(string nodeId, string dataId, SyncOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Performs bidirectional synchronization.
    /// </summary>
    Task<SyncResult> SyncBidirectionalAsync(string nodeId, string dataId, SyncOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the synchronization status for a data item.
    /// </summary>
    Task<SyncStatus> GetSyncStatusAsync(string nodeId, string dataId, CancellationToken ct = default);

    /// <summary>
    /// Resolves synchronization conflicts.
    /// </summary>
    Task<ConflictResolutionResult> ResolveConflictAsync(SyncConflict conflict, ConflictResolutionStrategy strategy, CancellationToken ct = default);

    /// <summary>
    /// Schedules automatic synchronization.
    /// </summary>
    Task<string> ScheduleSyncAsync(SyncSchedule schedule, CancellationToken ct = default);

    /// <summary>
    /// Performs delta synchronization (only changed data).
    /// </summary>
    Task<DeltaSyncResult> DeltaSyncAsync(string nodeId, string dataId, CancellationToken ct = default);

    /// <summary>
    /// Event raised when synchronization completes.
    /// </summary>
    event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    /// <summary>
    /// Event raised when a sync conflict is detected.
    /// </summary>
    event EventHandler<SyncConflictEventArgs>? ConflictDetected;
}

#endregion

#region 109.3: Offline Operation Support

/// <summary>
/// Interface for offline operation support at edge nodes.
/// </summary>
public interface IOfflineOperationManager
{
    /// <summary>
    /// Queues an operation for execution when connectivity is restored.
    /// </summary>
    Task<string> QueueOperationAsync(OfflineOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Gets all pending offline operations for a node.
    /// </summary>
    IAsyncEnumerable<OfflineOperation> GetPendingOperationsAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Processes queued operations when connectivity is restored.
    /// </summary>
    Task<OperationBatchResult> ProcessPendingOperationsAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a node is currently operating in offline mode.
    /// </summary>
    Task<bool> IsOfflineAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Enables offline mode for a node.
    /// </summary>
    Task EnableOfflineModeAsync(string nodeId, OfflineModeConfig config, CancellationToken ct = default);

    /// <summary>
    /// Disables offline mode and triggers synchronization.
    /// </summary>
    Task DisableOfflineModeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Gets offline storage capacity and usage.
    /// </summary>
    Task<OfflineStorageInfo> GetOfflineStorageInfoAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Caches data locally for offline access.
    /// </summary>
    Task<bool> CacheForOfflineAsync(string nodeId, string dataId, OfflineCachePolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Event raised when connectivity status changes.
    /// </summary>
    event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;
}

#endregion

#region 109.4: Edge-to-Cloud Communication

/// <summary>
/// Interface for edge-to-cloud communication.
/// </summary>
public interface IEdgeCloudCommunicator
{
    /// <summary>
    /// Establishes a communication channel with the cloud.
    /// </summary>
    Task<CommunicationChannel> ConnectAsync(string nodeId, CloudEndpoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// Sends a message from edge to cloud.
    /// </summary>
    Task<MessageResult> SendToCloudAsync(string nodeId, EdgeMessage message, CancellationToken ct = default);

    /// <summary>
    /// Receives messages from cloud at edge node.
    /// </summary>
    IAsyncEnumerable<EdgeMessage> ReceiveFromCloudAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Sends telemetry data to cloud.
    /// </summary>
    Task<bool> SendTelemetryAsync(string nodeId, TelemetryData telemetry, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to cloud events.
    /// </summary>
    Task<string> SubscribeToCloudEventsAsync(string nodeId, EventSubscription subscription, CancellationToken ct = default);

    /// <summary>
    /// Invokes a cloud function from edge.
    /// </summary>
    Task<CloudFunctionResult> InvokeCloudFunctionAsync(string nodeId, string functionId, object? payload, CancellationToken ct = default);

    /// <summary>
    /// Gets the current connection status.
    /// </summary>
    Task<ConnectionStatus> GetConnectionStatusAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Configures communication protocols and quality of service.
    /// </summary>
    Task ConfigureCommunicationAsync(string nodeId, CommunicationConfig config, CancellationToken ct = default);

    /// <summary>
    /// Event raised when a message is received from cloud.
    /// </summary>
    event EventHandler<CloudMessageReceivedEventArgs>? CloudMessageReceived;
}

#endregion

#region 109.5: Edge Analytics

/// <summary>
/// Interface for edge analytics capabilities.
/// </summary>
public interface IEdgeAnalyticsEngine
{
    /// <summary>
    /// Deploys an analytics model to an edge node.
    /// </summary>
    Task<ModelDeploymentResult> DeployModelAsync(string nodeId, AnalyticsModel model, CancellationToken ct = default);

    /// <summary>
    /// Runs inference on edge using deployed model.
    /// </summary>
    Task<InferenceResult> RunInferenceAsync(string nodeId, string modelId, object input, CancellationToken ct = default);

    /// <summary>
    /// Aggregates data at the edge before sending to cloud.
    /// </summary>
    Task<AggregationResult> AggregateDataAsync(string nodeId, AggregationConfig config, CancellationToken ct = default);

    /// <summary>
    /// Performs real-time stream processing at edge.
    /// </summary>
    IAsyncEnumerable<StreamProcessingResult> ProcessStreamAsync(string nodeId, IAsyncEnumerable<DataPoint> stream, StreamProcessingConfig config, CancellationToken ct = default);

    /// <summary>
    /// Detects anomalies in edge data.
    /// </summary>
    Task<AnomalyDetectionResult> DetectAnomaliesAsync(string nodeId, IEnumerable<DataPoint> data, AnomalyConfig config, CancellationToken ct = default);

    /// <summary>
    /// Gets analytics metrics from edge node.
    /// </summary>
    Task<AnalyticsMetrics> GetMetricsAsync(string nodeId, MetricsQuery query, CancellationToken ct = default);

    /// <summary>
    /// Trains a model locally at edge using federated learning.
    /// </summary>
    Task<FederatedLearningResult> TrainLocallyAsync(string nodeId, TrainingConfig config, CancellationToken ct = default);

    /// <summary>
    /// Event raised when an anomaly is detected.
    /// </summary>
    event EventHandler<AnomalyDetectedEventArgs>? AnomalyDetected;
}

#endregion

#region 109.6: Edge Security

/// <summary>
/// Interface for edge security operations.
/// </summary>
public interface IEdgeSecurityManager
{
    /// <summary>
    /// Authenticates an edge node.
    /// </summary>
    Task<AuthenticationResult> AuthenticateNodeAsync(string nodeId, EdgeCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Authorizes an operation at edge.
    /// </summary>
    Task<AuthorizationResult> AuthorizeOperationAsync(string nodeId, string operationId, string resourceId, CancellationToken ct = default);

    /// <summary>
    /// Encrypts data for edge storage or transmission.
    /// </summary>
    Task<EncryptionResult> EncryptAsync(string nodeId, ReadOnlyMemory<byte> data, EncryptionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Decrypts data at edge.
    /// </summary>
    Task<ReadOnlyMemory<byte>> DecryptAsync(string nodeId, ReadOnlyMemory<byte> encryptedData, DecryptionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Manages certificates for edge nodes.
    /// </summary>
    Task<CertificateResult> ManageCertificateAsync(string nodeId, CertificateOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Creates secure tunnels between edge and cloud.
    /// </summary>
    Task<SecureTunnel> CreateSecureTunnelAsync(string nodeId, TunnelConfig config, CancellationToken ct = default);

    /// <summary>
    /// Audits security events at edge.
    /// </summary>
    IAsyncEnumerable<SecurityAuditEntry> GetSecurityAuditAsync(string nodeId, AuditQuery query, CancellationToken ct = default);

    /// <summary>
    /// Enforces security policies at edge.
    /// </summary>
    Task<PolicyEnforcementResult> EnforcePolicyAsync(string nodeId, SecurityPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Event raised when a security incident is detected.
    /// </summary>
    event EventHandler<SecurityIncidentEventArgs>? SecurityIncidentDetected;
}

#endregion

#region 109.7: Edge Resource Management

/// <summary>
/// Interface for edge resource management.
/// </summary>
public interface IEdgeResourceManager
{
    /// <summary>
    /// Gets current resource usage of an edge node.
    /// </summary>
    Task<ResourceUsage> GetResourceUsageAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Allocates resources for a workload at edge.
    /// </summary>
    Task<ResourceAllocation> AllocateResourcesAsync(string nodeId, ResourceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Releases allocated resources.
    /// </summary>
    Task<bool> ReleaseResourcesAsync(string nodeId, string allocationId, CancellationToken ct = default);

    /// <summary>
    /// Sets resource limits for edge node.
    /// </summary>
    Task<bool> SetResourceLimitsAsync(string nodeId, ResourceLimits limits, CancellationToken ct = default);

    /// <summary>
    /// Scales resources at edge node.
    /// </summary>
    Task<ScalingResult> ScaleResourcesAsync(string nodeId, ScalingRequest request, CancellationToken ct = default);

    /// <summary>
    /// Monitors resource utilization in real-time.
    /// </summary>
    IAsyncEnumerable<ResourceSnapshot> MonitorResourcesAsync(string nodeId, TimeSpan interval, CancellationToken ct = default);

    /// <summary>
    /// Optimizes resource allocation based on workload patterns.
    /// </summary>
    Task<OptimizationResult> OptimizeResourcesAsync(string nodeId, OptimizationConfig config, CancellationToken ct = default);

    /// <summary>
    /// Event raised when resource threshold is exceeded.
    /// </summary>
    event EventHandler<ResourceThresholdEventArgs>? ResourceThresholdExceeded;
}

#endregion

#region 109.8: Multi-Edge Orchestration

/// <summary>
/// Interface for multi-edge orchestration.
/// </summary>
public interface IMultiEdgeOrchestrator
{
    /// <summary>
    /// Deploys a workload across multiple edge nodes.
    /// </summary>
    Task<DeploymentResult> DeployWorkloadAsync(WorkloadDefinition workload, DeploymentStrategy strategy, CancellationToken ct = default);

    /// <summary>
    /// Balances load across edge nodes.
    /// </summary>
    Task<LoadBalancingResult> BalanceLoadAsync(LoadBalancingConfig config, CancellationToken ct = default);

    /// <summary>
    /// Triggers failover to backup edge node.
    /// </summary>
    Task<FailoverResult> TriggerFailoverAsync(string failedNodeId, FailoverConfig config, CancellationToken ct = default);

    /// <summary>
    /// Routes requests to optimal edge node.
    /// </summary>
    Task<string> RouteRequestAsync(EdgeRequest request, RoutingPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Coordinates distributed transactions across edges.
    /// </summary>
    Task<DistributedTransactionResult> CoordinateTransactionAsync(DistributedTransaction transaction, CancellationToken ct = default);

    /// <summary>
    /// Gets topology of edge network.
    /// </summary>
    Task<EdgeTopology> GetTopologyAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates orchestration policy.
    /// </summary>
    Task<bool> UpdatePolicyAsync(OrchestrationPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Performs rolling update across edge nodes.
    /// </summary>
    Task<RollingUpdateResult> PerformRollingUpdateAsync(UpdatePackage package, RollingUpdateConfig config, CancellationToken ct = default);

    /// <summary>
    /// Event raised when topology changes.
    /// </summary>
    event EventHandler<TopologyChangedEventArgs>? TopologyChanged;
}

#endregion

#region Types and Enums

/// <summary>
/// Edge computing capabilities descriptor.
/// </summary>
public sealed class EdgeComputingCapabilities
{
    public bool SupportsOfflineMode { get; init; }
    public bool SupportsDeltaSync { get; init; }
    public bool SupportsEdgeAnalytics { get; init; }
    public bool SupportsEdgeML { get; init; }
    public bool SupportsSecureTunnels { get; init; }
    public bool SupportsMultiEdge { get; init; }
    public bool SupportsFederatedLearning { get; init; }
    public bool SupportsStreamProcessing { get; init; }
    public int MaxEdgeNodes { get; init; }
    public long MaxOfflineStorageBytes { get; init; }
    public TimeSpan MaxOfflineDuration { get; init; }
    public string[] SupportedProtocols { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Edge computing configuration.
/// </summary>
public sealed class EdgeComputingConfiguration
{
    public string ClusterId { get; init; } = string.Empty;
    public string CloudEndpoint { get; init; } = string.Empty;
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMinutes(5);
    public bool EnableOfflineMode { get; init; } = true;
    public bool EnableAnalytics { get; init; } = true;
    public bool EnableSecurity { get; init; } = true;
    public Dictionary<string, object> CustomSettings { get; init; } = new();
}

/// <summary>
/// Edge computing status.
/// </summary>
public sealed class EdgeComputingStatus
{
    public bool IsInitialized { get; init; }
    public int TotalNodes { get; init; }
    public int OnlineNodes { get; init; }
    public int OfflineNodes { get; init; }
    public DateTime LastStatusUpdate { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}

/// <summary>
/// Edge node information.
/// </summary>
public sealed class EdgeNodeInfo
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    public EdgeNodeType Type { get; init; }
    public EdgeNodeStatus Status { get; set; }
    public string IpAddress { get; init; } = string.Empty;
    public int LatencyMs { get; set; }
    public long StorageCapacityBytes { get; init; }
    public long StorageUsedBytes { get; set; }
    public double CpuCores { get; init; }
    public long MemoryBytes { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new();
    public DateTime RegisteredAt { get; init; }
    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// Edge node types.
/// </summary>
public enum EdgeNodeType
{
    Gateway,
    Worker,
    Sensor,
    Aggregator,
    Inference,
    Storage,
    Hybrid
}

/// <summary>
/// Edge node status.
/// </summary>
public enum EdgeNodeStatus
{
    Unknown,
    Online,
    Offline,
    Degraded,
    Maintenance,
    Provisioning,
    Decommissioned
}

/// <summary>
/// Edge node registration result.
/// </summary>
public sealed class EdgeNodeRegistration
{
    public required string NodeId { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public DateTime RegisteredAt { get; init; }
    public string? AssignedCluster { get; init; }
}

/// <summary>
/// Edge node filter for listing.
/// </summary>
public sealed class EdgeNodeFilter
{
    public EdgeNodeStatus? Status { get; init; }
    public EdgeNodeType? Type { get; init; }
    public string? Location { get; init; }
    public string? ClusterId { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// Edge node configuration.
/// </summary>
public sealed class EdgeNodeConfig
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public long MaxStorageBytes { get; init; }
    public bool EnableOffline { get; init; } = true;
    public bool EnableAnalytics { get; init; } = true;
    public Dictionary<string, object> CustomConfig { get; init; } = new();
}

/// <summary>
/// Edge node health information.
/// </summary>
public sealed class EdgeNodeHealth
{
    public required string NodeId { get; init; }
    public bool IsHealthy { get; init; }
    public double HealthScore { get; init; }
    public double CpuUsagePercent { get; init; }
    public double MemoryUsagePercent { get; init; }
    public double StorageUsagePercent { get; init; }
    public int LatencyMs { get; init; }
    public int ErrorCount { get; init; }
    public DateTime CheckedAt { get; init; }
    public string? HealthMessage { get; init; }
}

/// <summary>
/// Edge discovery options.
/// </summary>
public sealed class EdgeDiscoveryOptions
{
    public string? NetworkRange { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool IncludeOffline { get; init; }
    public string[]? DiscoveryProtocols { get; init; }
}

/// <summary>
/// Edge cluster for grouped management.
/// </summary>
public sealed class EdgeCluster
{
    public required string ClusterId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string[] NodeIds { get; init; } = Array.Empty<string>();
    public string? LeaderNodeId { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Synchronization options.
/// </summary>
public sealed class SyncOptions
{
    public SyncDirection Direction { get; init; } = SyncDirection.Bidirectional;
    public SyncPriority Priority { get; init; } = SyncPriority.Normal;
    public bool DeltaOnly { get; init; } = true;
    public bool CompressData { get; init; } = true;
    public ConflictResolutionStrategy ConflictStrategy { get; init; } = ConflictResolutionStrategy.LastWriteWins;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Synchronization direction.
/// </summary>
public enum SyncDirection
{
    ToEdge,
    ToCloud,
    Bidirectional
}

/// <summary>
/// Synchronization priority.
/// </summary>
public enum SyncPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// Conflict resolution strategy.
/// </summary>
public enum ConflictResolutionStrategy
{
    LastWriteWins,
    FirstWriteWins,
    CloudWins,
    EdgeWins,
    Manual,
    Merge
}

/// <summary>
/// Synchronization result.
/// </summary>
public sealed class SyncResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long BytesSynced { get; init; }
    public TimeSpan Duration { get; init; }
    public int ItemsSynced { get; init; }
    public int ConflictsResolved { get; init; }
    public DateTime CompletedAt { get; init; }
}

/// <summary>
/// Synchronization status.
/// </summary>
public sealed class SyncStatus
{
    public required string DataId { get; init; }
    public bool IsSynced { get; init; }
    public DateTime? LastSyncedAt { get; init; }
    public SyncDirection LastDirection { get; init; }
    public long LocalVersion { get; init; }
    public long CloudVersion { get; init; }
    public bool HasPendingChanges { get; init; }
}

/// <summary>
/// Synchronization conflict.
/// </summary>
public sealed class SyncConflict
{
    public required string DataId { get; init; }
    public required string NodeId { get; init; }
    public ReadOnlyMemory<byte> LocalData { get; init; }
    public ReadOnlyMemory<byte> CloudData { get; init; }
    public DateTime LocalModified { get; init; }
    public DateTime CloudModified { get; init; }
    public DateTime DetectedAt { get; init; }
}

/// <summary>
/// Conflict resolution result.
/// </summary>
public sealed class ConflictResolutionResult
{
    public bool Success { get; init; }
    public ConflictResolutionStrategy UsedStrategy { get; init; }
    public ReadOnlyMemory<byte> ResolvedData { get; init; }
    public DateTime ResolvedAt { get; init; }
}

/// <summary>
/// Synchronization schedule.
/// </summary>
public sealed class SyncSchedule
{
    public required string NodeId { get; init; }
    public string? DataPattern { get; init; }
    public string CronExpression { get; init; } = "*/5 * * * *";
    public SyncOptions Options { get; init; } = new();
}

/// <summary>
/// Delta synchronization result.
/// </summary>
public sealed class DeltaSyncResult
{
    public bool Success { get; init; }
    public long DeltaBytesTransferred { get; init; }
    public long TotalDataSize { get; init; }
    public double CompressionRatio { get; init; }
    public int ChunksTransferred { get; init; }
}

/// <summary>
/// Offline operation.
/// </summary>
public sealed class OfflineOperation
{
    public required string OperationId { get; init; }
    public required string NodeId { get; init; }
    public required OfflineOperationType Type { get; init; }
    public required string ResourceId { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
    public DateTime QueuedAt { get; init; }
    public int RetryCount { get; set; }
    public OfflineOperationPriority Priority { get; init; }
}

/// <summary>
/// Offline operation type.
/// </summary>
public enum OfflineOperationType
{
    Create,
    Update,
    Delete,
    Read,
    Execute,
    Sync
}

/// <summary>
/// Offline operation priority.
/// </summary>
public enum OfflineOperationPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// Operation batch result.
/// </summary>
public sealed class OperationBatchResult
{
    public int TotalOperations { get; init; }
    public int SuccessfulOperations { get; init; }
    public int FailedOperations { get; init; }
    public TimeSpan Duration { get; init; }
    public string[] FailedOperationIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Offline mode configuration.
/// </summary>
public sealed class OfflineModeConfig
{
    public long MaxStorageBytes { get; init; }
    public TimeSpan MaxOfflineDuration { get; init; }
    public bool QueueWrites { get; init; } = true;
    public bool CacheReads { get; init; } = true;
    public int MaxQueuedOperations { get; init; } = 10000;
}

/// <summary>
/// Offline storage information.
/// </summary>
public sealed class OfflineStorageInfo
{
    public long TotalBytes { get; init; }
    public long UsedBytes { get; init; }
    public long AvailableBytes { get; init; }
    public int CachedItems { get; init; }
    public int PendingOperations { get; init; }
}

/// <summary>
/// Offline cache policy.
/// </summary>
public sealed class OfflineCachePolicy
{
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);
    public CachePriority Priority { get; init; } = CachePriority.Normal;
    public bool PinLocally { get; init; }
}

/// <summary>
/// Cache priority.
/// </summary>
public enum CachePriority
{
    Low,
    Normal,
    High,
    Pinned
}

/// <summary>
/// Cloud endpoint configuration.
/// </summary>
public sealed class CloudEndpoint
{
    public required string Url { get; init; }
    public string Protocol { get; init; } = "https";
    public Dictionary<string, string> Headers { get; init; } = new();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Communication channel.
/// </summary>
public sealed class CommunicationChannel
{
    public required string ChannelId { get; init; }
    public required string NodeId { get; init; }
    public ConnectionState State { get; set; }
    public DateTime EstablishedAt { get; init; }
    public string Protocol { get; init; } = string.Empty;
}

/// <summary>
/// Connection state.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Edge message.
/// </summary>
public sealed class EdgeMessage
{
    public required string MessageId { get; init; }
    public required string Type { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public DateTime Timestamp { get; init; }
    public MessagePriority Priority { get; init; }
}

/// <summary>
/// Message priority.
/// </summary>
public enum MessagePriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>
/// Message result.
/// </summary>
public sealed class MessageResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? CorrelationId { get; init; }
    public DateTime SentAt { get; init; }
}

/// <summary>
/// Telemetry data.
/// </summary>
public sealed class TelemetryData
{
    public required string NodeId { get; init; }
    public DateTime Timestamp { get; init; }
    public Dictionary<string, double> Metrics { get; init; } = new();
    public Dictionary<string, string> Properties { get; init; } = new();
}

/// <summary>
/// Event subscription.
/// </summary>
public sealed class EventSubscription
{
    public required string Topic { get; init; }
    public string? Filter { get; init; }
    public bool IncludeHistory { get; init; }
}

/// <summary>
/// Cloud function result.
/// </summary>
public sealed class CloudFunctionResult
{
    public bool Success { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}

/// <summary>
/// Connection status.
/// </summary>
public sealed class ConnectionStatus
{
    public ConnectionState State { get; init; }
    public int LatencyMs { get; init; }
    public DateTime LastHeartbeat { get; init; }
    public int ReconnectAttempts { get; init; }
}

/// <summary>
/// Communication configuration.
/// </summary>
public sealed class CommunicationConfig
{
    public string Protocol { get; init; } = "mqtt";
    public QualityOfService Qos { get; init; } = QualityOfService.AtLeastOnce;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; init; } = 3;
    public bool EnableCompression { get; init; } = true;
}

/// <summary>
/// Quality of service levels.
/// </summary>
public enum QualityOfService
{
    AtMostOnce,
    AtLeastOnce,
    ExactlyOnce
}

/// <summary>
/// Analytics model.
/// </summary>
public sealed class AnalyticsModel
{
    public required string ModelId { get; init; }
    public required string Name { get; init; }
    public required ModelType Type { get; init; }
    public ReadOnlyMemory<byte> ModelData { get; init; }
    public string Version { get; init; } = "1.0.0";
    public Dictionary<string, object> Config { get; init; } = new();
}

/// <summary>
/// Model type.
/// </summary>
public enum ModelType
{
    Classification,
    Regression,
    AnomalyDetection,
    Clustering,
    TimeSeries,
    ObjectDetection,
    NLP
}

/// <summary>
/// Model deployment result.
/// </summary>
public sealed class ModelDeploymentResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string DeploymentId { get; init; } = string.Empty;
    public DateTime DeployedAt { get; init; }
}

/// <summary>
/// Inference result.
/// </summary>
public sealed class InferenceResult
{
    public bool Success { get; init; }
    public object? Prediction { get; init; }
    public double Confidence { get; init; }
    public TimeSpan LatencyMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Aggregation configuration.
/// </summary>
public sealed class AggregationConfig
{
    public required string DataSource { get; init; }
    public AggregationType Type { get; init; }
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);
    public string[]? GroupByFields { get; init; }
}

/// <summary>
/// Aggregation type.
/// </summary>
public enum AggregationType
{
    Sum,
    Average,
    Min,
    Max,
    Count,
    Percentile
}

/// <summary>
/// Aggregation result.
/// </summary>
public sealed class AggregationResult
{
    public bool Success { get; init; }
    public Dictionary<string, object> AggregatedValues { get; init; } = new();
    public int RecordsProcessed { get; init; }
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
}

/// <summary>
/// Data point for analytics.
/// </summary>
public sealed class DataPoint
{
    public required DateTime Timestamp { get; init; }
    public required Dictionary<string, object> Values { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// Stream processing configuration.
/// </summary>
public sealed class StreamProcessingConfig
{
    public string? FilterExpression { get; init; }
    public string[]? SelectFields { get; init; }
    public AggregationType? Aggregation { get; init; }
    public TimeSpan? WindowSize { get; init; }
}

/// <summary>
/// Stream processing result.
/// </summary>
public sealed class StreamProcessingResult
{
    public required DataPoint ProcessedPoint { get; init; }
    public bool Filtered { get; init; }
    public DateTime ProcessedAt { get; init; }
}

/// <summary>
/// Anomaly detection configuration.
/// </summary>
public sealed class AnomalyConfig
{
    public double SensitivityThreshold { get; init; } = 0.95;
    public string[]? MonitoredFields { get; init; }
    public bool UseHistoricalBaseline { get; init; } = true;
}

/// <summary>
/// Anomaly detection result.
/// </summary>
public sealed class AnomalyDetectionResult
{
    public bool AnomalyDetected { get; init; }
    public DataPoint[]? Anomalies { get; init; }
    public double AnomalyScore { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Metrics query.
/// </summary>
public sealed class MetricsQuery
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string[]? MetricNames { get; init; }
    public TimeSpan? Resolution { get; init; }
}

/// <summary>
/// Analytics metrics.
/// </summary>
public sealed class AnalyticsMetrics
{
    public long TotalInferences { get; init; }
    public double AverageLatencyMs { get; init; }
    public long DataPointsProcessed { get; init; }
    public int AnomaliesDetected { get; init; }
    public Dictionary<string, double> CustomMetrics { get; init; } = new();
}

/// <summary>
/// Training configuration for federated learning.
/// </summary>
public sealed class TrainingConfig
{
    public required string ModelId { get; init; }
    public string DataSource { get; init; } = string.Empty;
    public int Epochs { get; init; } = 10;
    public double LearningRate { get; init; } = 0.01;
    public int BatchSize { get; init; } = 32;
}

/// <summary>
/// Federated learning result.
/// </summary>
public sealed class FederatedLearningResult
{
    public bool Success { get; init; }
    public double LocalAccuracy { get; init; }
    public ReadOnlyMemory<byte> ModelGradients { get; init; }
    public int SamplesUsed { get; init; }
}

/// <summary>
/// Edge credentials.
/// </summary>
public sealed class EdgeCredentials
{
    public string? ApiKey { get; init; }
    public string? Certificate { get; init; }
    public string? Token { get; init; }
    public Dictionary<string, string>? CustomCredentials { get; init; }
}

/// <summary>
/// Authentication result.
/// </summary>
public sealed class AuthenticationResult
{
    public bool Success { get; init; }
    public string? Token { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Authorization result.
/// </summary>
public sealed class AuthorizationResult
{
    public bool Authorized { get; init; }
    public string? Reason { get; init; }
    public string[]? GrantedPermissions { get; init; }
}

/// <summary>
/// Encryption options.
/// </summary>
public sealed class EncryptionOptions
{
    public string Algorithm { get; init; } = "AES-256-GCM";
    public string? KeyId { get; init; }
    public bool IncludeMetadata { get; init; }
}

/// <summary>
/// Encryption result.
/// </summary>
public sealed class EncryptionResult
{
    public bool Success { get; init; }
    public ReadOnlyMemory<byte> EncryptedData { get; init; }
    public string? KeyId { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Decryption options.
/// </summary>
public sealed class DecryptionOptions
{
    public string? KeyId { get; init; }
    public bool VerifyIntegrity { get; init; } = true;
}

/// <summary>
/// Certificate operation.
/// </summary>
public sealed class CertificateOperation
{
    public required CertificateOperationType Type { get; init; }
    public string? CertificateData { get; init; }
    public TimeSpan? ValidityPeriod { get; init; }
}

/// <summary>
/// Certificate operation type.
/// </summary>
public enum CertificateOperationType
{
    Generate,
    Renew,
    Revoke,
    Verify
}

/// <summary>
/// Certificate result.
/// </summary>
public sealed class CertificateResult
{
    public bool Success { get; init; }
    public string? CertificateId { get; init; }
    public string? Certificate { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Secure tunnel configuration.
/// </summary>
public sealed class TunnelConfig
{
    public required string TargetEndpoint { get; init; }
    public string Protocol { get; init; } = "wireguard";
    public bool EnableMutualTls { get; init; } = true;
}

/// <summary>
/// Secure tunnel.
/// </summary>
public sealed class SecureTunnel
{
    public required string TunnelId { get; init; }
    public string LocalEndpoint { get; init; } = string.Empty;
    public string RemoteEndpoint { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime EstablishedAt { get; init; }
}

/// <summary>
/// Audit query.
/// </summary>
public sealed class AuditQuery
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string[]? EventTypes { get; init; }
    public string? UserId { get; init; }
}

/// <summary>
/// Security audit entry.
/// </summary>
public sealed class SecurityAuditEntry
{
    public required string EntryId { get; init; }
    public required string EventType { get; init; }
    public DateTime Timestamp { get; init; }
    public string? UserId { get; init; }
    public string? ResourceId { get; init; }
    public string? Action { get; init; }
    public bool Success { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Security policy.
/// </summary>
public sealed class SecurityPolicy
{
    public required string PolicyId { get; init; }
    public string Name { get; init; } = string.Empty;
    public PolicyRule[] Rules { get; init; } = Array.Empty<PolicyRule>();
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Policy rule.
/// </summary>
public sealed class PolicyRule
{
    public required string RuleId { get; init; }
    public required string Action { get; init; }
    public required string Resource { get; init; }
    public string Effect { get; init; } = "Allow";
    public string[]? Conditions { get; init; }
}

/// <summary>
/// Policy enforcement result.
/// </summary>
public sealed class PolicyEnforcementResult
{
    public bool Success { get; init; }
    public int RulesApplied { get; init; }
    public string[]? ViolatedRules { get; init; }
}

/// <summary>
/// Resource usage information.
/// </summary>
public sealed class ResourceUsage
{
    public required string NodeId { get; init; }
    public double CpuUsagePercent { get; init; }
    public double MemoryUsagePercent { get; init; }
    public double StorageUsagePercent { get; init; }
    public double NetworkBandwidthMbps { get; init; }
    public int ActiveConnections { get; init; }
    public DateTime MeasuredAt { get; init; }
}

/// <summary>
/// Resource request.
/// </summary>
public sealed class ResourceRequest
{
    public double CpuCores { get; init; }
    public long MemoryBytes { get; init; }
    public long StorageBytes { get; init; }
    public string? Priority { get; init; }
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Resource allocation.
/// </summary>
public sealed class ResourceAllocation
{
    public required string AllocationId { get; init; }
    public required string NodeId { get; init; }
    public double AllocatedCpuCores { get; init; }
    public long AllocatedMemoryBytes { get; init; }
    public long AllocatedStorageBytes { get; init; }
    public DateTime AllocatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>
/// Resource limits.
/// </summary>
public sealed class ResourceLimits
{
    public double MaxCpuCores { get; init; }
    public long MaxMemoryBytes { get; init; }
    public long MaxStorageBytes { get; init; }
    public int MaxConnections { get; init; }
    public double MaxBandwidthMbps { get; init; }
}

/// <summary>
/// Scaling request.
/// </summary>
public sealed class ScalingRequest
{
    public ScalingDirection Direction { get; init; }
    public double ScaleFactor { get; init; } = 1.5;
    public ResourceRequest? TargetResources { get; init; }
}

/// <summary>
/// Scaling direction.
/// </summary>
public enum ScalingDirection
{
    Up,
    Down,
    Auto
}

/// <summary>
/// Scaling result.
/// </summary>
public sealed class ScalingResult
{
    public bool Success { get; init; }
    public ResourceAllocation? NewAllocation { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Resource snapshot.
/// </summary>
public sealed class ResourceSnapshot
{
    public DateTime Timestamp { get; init; }
    public required ResourceUsage Usage { get; init; }
}

/// <summary>
/// Optimization configuration.
/// </summary>
public sealed class OptimizationConfig
{
    public string[] OptimizationTargets { get; init; } = new[] { "cost", "performance" };
    public bool AutoScale { get; init; }
    public TimeSpan EvaluationWindow { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Optimization result.
/// </summary>
public sealed class OptimizationResult
{
    public bool Success { get; init; }
    public string[] AppliedOptimizations { get; init; } = Array.Empty<string>();
    public double EstimatedImprovement { get; init; }
}

/// <summary>
/// Workload definition.
/// </summary>
public sealed class WorkloadDefinition
{
    public required string WorkloadId { get; init; }
    public required string Name { get; init; }
    public WorkloadType Type { get; init; }
    public ResourceRequest RequiredResources { get; init; } = new();
    public string[]? PreferredNodes { get; init; }
    public Dictionary<string, object> Config { get; init; } = new();
}

/// <summary>
/// Workload type.
/// </summary>
public enum WorkloadType
{
    Batch,
    Streaming,
    Service,
    Job,
    Function
}

/// <summary>
/// Deployment strategy.
/// </summary>
public sealed class DeploymentStrategy
{
    public DeploymentMode Mode { get; init; } = DeploymentMode.RollingUpdate;
    public int ReplicaCount { get; init; } = 1;
    public bool EnableCanary { get; init; }
    public double CanaryPercentage { get; init; } = 10;
}

/// <summary>
/// Deployment mode.
/// </summary>
public enum DeploymentMode
{
    RollingUpdate,
    BlueGreen,
    Canary,
    Recreate
}

/// <summary>
/// Deployment result.
/// </summary>
public sealed class DeploymentResult
{
    public bool Success { get; init; }
    public string DeploymentId { get; init; } = string.Empty;
    public string[] DeployedNodes { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public DateTime DeployedAt { get; init; }
}

/// <summary>
/// Load balancing configuration.
/// </summary>
public sealed class LoadBalancingConfig
{
    public LoadBalancingAlgorithm Algorithm { get; init; } = LoadBalancingAlgorithm.RoundRobin;
    public string[]? TargetNodes { get; init; }
    public bool EnableHealthChecks { get; init; } = true;
    public int HealthCheckIntervalSeconds { get; init; } = 30;
}

/// <summary>
/// Load balancing algorithm.
/// </summary>
public enum LoadBalancingAlgorithm
{
    RoundRobin,
    LeastConnections,
    WeightedRoundRobin,
    IpHash,
    LatencyBased,
    ResourceBased
}

/// <summary>
/// Load balancing result.
/// </summary>
public sealed class LoadBalancingResult
{
    public bool Success { get; init; }
    public Dictionary<string, int> LoadDistribution { get; init; } = new();
    public string? Error { get; init; }
}

/// <summary>
/// Failover configuration.
/// </summary>
public sealed class FailoverConfig
{
    public string? PreferredTargetNode { get; init; }
    public FailoverMode Mode { get; init; } = FailoverMode.Automatic;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Failover mode.
/// </summary>
public enum FailoverMode
{
    Automatic,
    Manual,
    Scheduled
}

/// <summary>
/// Failover result.
/// </summary>
public sealed class FailoverResult
{
    public bool Success { get; init; }
    public string? NewPrimaryNode { get; init; }
    public TimeSpan FailoverDuration { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Edge request.
/// </summary>
public sealed class EdgeRequest
{
    public required string RequestId { get; init; }
    public required string Operation { get; init; }
    public string? ClientLocation { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// Routing policy.
/// </summary>
public sealed class RoutingPolicy
{
    public RoutingMode Mode { get; init; } = RoutingMode.LatencyBased;
    public string[]? PreferredNodes { get; init; }
    public string[]? ExcludedNodes { get; init; }
}

/// <summary>
/// Routing mode.
/// </summary>
public enum RoutingMode
{
    LatencyBased,
    GeoBased,
    LoadBased,
    CostBased,
    Random
}

/// <summary>
/// Distributed transaction.
/// </summary>
public sealed class DistributedTransaction
{
    public required string TransactionId { get; init; }
    public required TransactionOperation[] Operations { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Transaction operation.
/// </summary>
public sealed class TransactionOperation
{
    public required string NodeId { get; init; }
    public required string Operation { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
/// Distributed transaction result.
/// </summary>
public sealed class DistributedTransactionResult
{
    public bool Success { get; init; }
    public bool Committed { get; init; }
    public string[]? FailedNodes { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Edge topology.
/// </summary>
public sealed class EdgeTopology
{
    public EdgeNodeInfo[] Nodes { get; init; } = Array.Empty<EdgeNodeInfo>();
    public EdgeConnection[] Connections { get; init; } = Array.Empty<EdgeConnection>();
    public EdgeCluster[] Clusters { get; init; } = Array.Empty<EdgeCluster>();
    public DateTime GeneratedAt { get; init; }
}

/// <summary>
/// Edge connection.
/// </summary>
public sealed class EdgeConnection
{
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public int LatencyMs { get; init; }
    public double BandwidthMbps { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>
/// Orchestration policy.
/// </summary>
public sealed class OrchestrationPolicy
{
    public required string PolicyId { get; init; }
    public LoadBalancingAlgorithm LoadBalancing { get; init; }
    public FailoverMode FailoverMode { get; init; }
    public bool AutoScale { get; init; }
    public int MinReplicas { get; init; }
    public int MaxReplicas { get; init; }
}

/// <summary>
/// Update package.
/// </summary>
public sealed class UpdatePackage
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public ReadOnlyMemory<byte> PackageData { get; init; }
    public string? Checksum { get; init; }
}

/// <summary>
/// Rolling update configuration.
/// </summary>
public sealed class RollingUpdateConfig
{
    public int MaxUnavailable { get; init; } = 1;
    public int MaxSurge { get; init; } = 1;
    public TimeSpan MinReadySeconds { get; init; } = TimeSpan.FromSeconds(30);
    public bool EnableRollback { get; init; } = true;
}

/// <summary>
/// Rolling update result.
/// </summary>
public sealed class RollingUpdateResult
{
    public bool Success { get; init; }
    public int UpdatedNodes { get; init; }
    public int FailedNodes { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }
}

#endregion

#region Event Args

/// <summary>
/// Event args for node status changes.
/// </summary>
public sealed class EdgeNodeStatusChangedEventArgs : EventArgs
{
    public required string NodeId { get; init; }
    public EdgeNodeStatus OldStatus { get; init; }
    public EdgeNodeStatus NewStatus { get; init; }
    public DateTime ChangedAt { get; init; }
}

/// <summary>
/// Event args for sync completion.
/// </summary>
public sealed class SyncCompletedEventArgs : EventArgs
{
    public required string NodeId { get; init; }
    public required string DataId { get; init; }
    public required SyncResult Result { get; init; }
}

/// <summary>
/// Event args for sync conflicts.
/// </summary>
public sealed class SyncConflictEventArgs : EventArgs
{
    public required SyncConflict Conflict { get; init; }
}

/// <summary>
/// Event args for connectivity changes.
/// </summary>
public sealed class ConnectivityChangedEventArgs : EventArgs
{
    public required string NodeId { get; init; }
    public bool IsOnline { get; init; }
    public DateTime ChangedAt { get; init; }
}

/// <summary>
/// Event args for cloud messages.
/// </summary>
public sealed class CloudMessageReceivedEventArgs : EventArgs
{
    public required string NodeId { get; init; }
    public required EdgeMessage Message { get; init; }
}

/// <summary>
/// Event args for anomaly detection.
/// </summary>
public sealed class AnomalyDetectedEventArgs : EventArgs
{
    public required string NodeId { get; init; }
    public required AnomalyDetectionResult Result { get; init; }
}

/// <summary>
/// Event args for security incidents.
/// </summary>
public sealed class SecurityIncidentEventArgs : EventArgs
{
    public required string NodeId { get; init; }
    public required string IncidentType { get; init; }
    public string? Description { get; init; }
    public DateTime DetectedAt { get; init; }
}

/// <summary>
/// Event args for resource thresholds.
/// </summary>
public sealed class ResourceThresholdEventArgs : EventArgs
{
    public required string NodeId { get; init; }
    public required string ResourceType { get; init; }
    public double CurrentValue { get; init; }
    public double ThresholdValue { get; init; }
    public DateTime DetectedAt { get; init; }
}

/// <summary>
/// Event args for topology changes.
/// </summary>
public sealed class TopologyChangedEventArgs : EventArgs
{
    public TopologyChangeType ChangeType { get; init; }
    public string? AffectedNodeId { get; init; }
    public DateTime ChangedAt { get; init; }
}

/// <summary>
/// Topology change type.
/// </summary>
public enum TopologyChangeType
{
    NodeAdded,
    NodeRemoved,
    NodeStatusChanged,
    ConnectionAdded,
    ConnectionRemoved,
    ClusterChanged
}

#endregion
