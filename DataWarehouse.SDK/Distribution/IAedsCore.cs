namespace DataWarehouse.SDK.Distribution;

#region Enums

/// <summary>
/// Delivery mode for distributing content.
/// </summary>
public enum DeliveryMode
{
    /// <summary>Direct delivery to a specific ClientID.</summary>
    Unicast,

    /// <summary>Delivery to all clients subscribed to a ChannelID.</summary>
    Broadcast,

    /// <summary>Delivery to a subset of clients matching criteria.</summary>
    Multicast
}

/// <summary>
/// Post-download action to perform on the client.
/// </summary>
public enum ActionPrimitive
{
    /// <summary>Silent background download (cache only).</summary>
    Passive,

    /// <summary>Download + Toast Notification.</summary>
    Notify,

    /// <summary>Download + Run Executable (Requires Code Signing).</summary>
    Execute,

    /// <summary>Download + Open File + Watchdog (Monitor for edits &amp; Sync back).</summary>
    Interactive,

    /// <summary>Custom action defined by ActionScript.</summary>
    Custom
}

/// <summary>
/// Notification urgency tier.
/// </summary>
public enum NotificationTier
{
    /// <summary>Log entry only, no user notification.</summary>
    Silent = 1,

    /// <summary>Transient popup/toast notification.</summary>
    Toast = 2,

    /// <summary>Persistent modal requiring user acknowledgement.</summary>
    Modal = 3
}

/// <summary>
/// Channel subscription type.
/// </summary>
public enum SubscriptionType
{
    /// <summary>Admin-enforced subscription, cannot be unsubscribed.</summary>
    Mandatory,

    /// <summary>User-initiated subscription, can opt-out.</summary>
    Voluntary
}

/// <summary>
/// Client trust level for zero-trust model.
/// </summary>
public enum ClientTrustLevel
{
    /// <summary>Unknown/unpaired client - all connections rejected.</summary>
    Untrusted = 0,

    /// <summary>Pending admin verification.</summary>
    PendingVerification = 1,

    /// <summary>Verified and paired client.</summary>
    Trusted = 2,

    /// <summary>Elevated trust for executing signed code.</summary>
    Elevated = 3,

    /// <summary>Administrative client with full capabilities.</summary>
    Admin = 4
}

/// <summary>
/// Client status indicator.
/// </summary>
public enum ClientStatus
{
    /// <summary>Client is online and available.</summary>
    Online,

    /// <summary>Client is busy processing a task.</summary>
    Busy,

    /// <summary>Client is away or idle.</summary>
    Away,

    /// <summary>Client is offline.</summary>
    Offline
}

/// <summary>
/// Job execution status.
/// </summary>
public enum JobStatus
{
    /// <summary>Job is queued but not yet started.</summary>
    Queued,

    /// <summary>Job is currently being executed.</summary>
    InProgress,

    /// <summary>Job completed successfully on all targets.</summary>
    Completed,

    /// <summary>Job completed on some but not all targets.</summary>
    PartiallyCompleted,

    /// <summary>Job failed completely.</summary>
    Failed,

    /// <summary>Job was cancelled by user or admin.</summary>
    Cancelled
}

/// <summary>
/// Policy decision action type.
/// </summary>
public enum PolicyAction
{
    /// <summary>Allow the action to proceed.</summary>
    Allow,

    /// <summary>Prompt the user for approval.</summary>
    Prompt,

    /// <summary>Deny the action completely.</summary>
    Deny,

    /// <summary>Allow but execute in sandbox environment.</summary>
    Sandbox
}

/// <summary>
/// File change type for watchdog monitoring.
/// </summary>
public enum FileChangeType
{
    /// <summary>File content was modified.</summary>
    Modified,

    /// <summary>File was deleted.</summary>
    Deleted,

    /// <summary>File was renamed.</summary>
    Renamed
}

/// <summary>
/// Network type for policy evaluation.
/// </summary>
public enum NetworkType
{
    /// <summary>Wired Ethernet connection.</summary>
    Wired,

    /// <summary>WiFi connection.</summary>
    Wifi,

    /// <summary>Cellular data connection.</summary>
    Cellular,

    /// <summary>Metered connection (usage limits apply).</summary>
    Metered,

    /// <summary>No network connection.</summary>
    Offline
}

#endregion

#region Records - Intent Manifest

/// <summary>
/// The Intent Manifest defines what to deliver and what to do after delivery.
/// This is the fundamental unit of work in AEDS.
/// </summary>
public record IntentManifest
{
    /// <summary>Unique manifest identifier.</summary>
    public required string ManifestId { get; init; }

    /// <summary>When this manifest was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this manifest expires (optional).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The action to perform after delivery.</summary>
    public required ActionPrimitive Action { get; init; }

    /// <summary>Notification tier for user alerts.</summary>
    public NotificationTier NotificationTier { get; init; } = NotificationTier.Toast;

    /// <summary>Delivery mode (unicast, broadcast, multicast).</summary>
    public required DeliveryMode DeliveryMode { get; init; }

    /// <summary>Target ClientIDs for Unicast, or ChannelIDs for Broadcast.</summary>
    public required string[] Targets { get; init; }

    /// <summary>Priority level (0 = lowest, 100 = critical).</summary>
    public int Priority { get; init; } = 50;

    /// <summary>Payload descriptor (what to download).</summary>
    public required PayloadDescriptor Payload { get; init; }

    /// <summary>Custom action script (for ActionPrimitive.Custom).</summary>
    public string? ActionScript { get; init; }

    /// <summary>Cryptographic signature of this manifest.</summary>
    public required ManifestSignature Signature { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Describes the payload to be delivered.
/// </summary>
public record PayloadDescriptor
{
    /// <summary>Unique payload identifier (content-addressable hash).</summary>
    public required string PayloadId { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>MIME type of the payload.</summary>
    public required string ContentType { get; init; }

    /// <summary>Total size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>SHA-256 hash of the complete payload.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Chunk hashes for integrity verification.</summary>
    public string[]? ChunkHashes { get; init; }

    /// <summary>Whether delta sync is available.</summary>
    public bool DeltaAvailable { get; init; }

    /// <summary>Base version for delta sync (if available).</summary>
    public string? DeltaBaseVersion { get; init; }

    /// <summary>Encryption info (null if unencrypted).</summary>
    public PayloadEncryption? Encryption { get; init; }
}

/// <summary>
/// Payload encryption information.
/// </summary>
public record PayloadEncryption
{
    /// <summary>Encryption algorithm used.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Key ID for decryption (client must have access).</summary>
    public required string KeyId { get; init; }

    /// <summary>Key management mode.</summary>
    public required string KeyMode { get; init; }
}

/// <summary>
/// Cryptographic signature for the manifest.
/// </summary>
public record ManifestSignature
{
    /// <summary>Signing key identifier.</summary>
    public required string KeyId { get; init; }

    /// <summary>Signature algorithm (e.g., "Ed25519", "RSA-PSS-SHA256").</summary>
    public required string Algorithm { get; init; }

    /// <summary>Base64-encoded signature.</summary>
    public required string Value { get; init; }

    /// <summary>Certificate chain (for verification).</summary>
    public string[]? CertificateChain { get; init; }

    /// <summary>Whether this is a Release Signing Key (required for Execute action).</summary>
    public required bool IsReleaseKey { get; init; }
}

#endregion

#region Records - Client & Channel

/// <summary>
/// Client capability flags.
/// </summary>
[Flags]
public enum ClientCapabilities
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>Can receive passive background downloads.</summary>
    ReceivePassive = 1,

    /// <summary>Can receive notify actions with toast notifications.</summary>
    ReceiveNotify = 2,

    /// <summary>Can execute signed code.</summary>
    ExecuteSigned = 4,

    /// <summary>Supports interactive mode with watchdog.</summary>
    Interactive = 8,

    /// <summary>Supports P2P mesh networking.</summary>
    P2PMesh = 16,

    /// <summary>Supports delta sync.</summary>
    DeltaSync = 32,

    /// <summary>Can act as air-gap mule (USB transport).</summary>
    AirGapMule = 64,

    /// <summary>All capabilities enabled.</summary>
    All = ReceivePassive | ReceiveNotify | ExecuteSigned | Interactive | P2PMesh | DeltaSync | AirGapMule
}

/// <summary>
/// Registered AEDS client.
/// </summary>
public record AedsClient
{
    /// <summary>Unique client identifier.</summary>
    public required string ClientId { get; init; }

    /// <summary>Human-readable client name.</summary>
    public required string Name { get; init; }

    /// <summary>Client's public key for encrypted communications.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Current trust level.</summary>
    public required ClientTrustLevel TrustLevel { get; init; }

    /// <summary>When the client was registered.</summary>
    public required DateTimeOffset RegisteredAt { get; init; }

    /// <summary>Last heartbeat timestamp.</summary>
    public DateTimeOffset? LastHeartbeat { get; init; }

    /// <summary>Subscribed channel IDs.</summary>
    public required string[] SubscribedChannels { get; init; }

    /// <summary>Client capabilities.</summary>
    public ClientCapabilities Capabilities { get; init; }
}

/// <summary>
/// Distribution channel for pub/sub.
/// </summary>
public record DistributionChannel
{
    /// <summary>Unique channel identifier.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Human-readable channel name.</summary>
    public required string Name { get; init; }

    /// <summary>Channel description.</summary>
    public string? Description { get; init; }

    /// <summary>Whether this is a mandatory subscription channel.</summary>
    public required SubscriptionType SubscriptionType { get; init; }

    /// <summary>Required trust level to receive from this channel.</summary>
    public required ClientTrustLevel MinTrustLevel { get; init; }

    /// <summary>Number of subscribers.</summary>
    public int SubscriberCount { get; init; }

    /// <summary>When the channel was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

#endregion

#region Records - Control Plane

/// <summary>
/// Configuration for control plane connection.
/// </summary>
/// <param name="ServerUrl">Control plane server endpoint URL.</param>
/// <param name="ClientId">Unique client identifier.</param>
/// <param name="AuthToken">Authentication token for secure connection.</param>
/// <param name="HeartbeatInterval">How often to send heartbeat messages.</param>
/// <param name="ReconnectDelay">Delay before attempting reconnection after disconnect.</param>
/// <param name="Options">Additional transport-specific options.</param>
public record ControlPlaneConfig(
    string ServerUrl,
    string ClientId,
    string AuthToken,
    TimeSpan HeartbeatInterval,
    TimeSpan ReconnectDelay,
    Dictionary<string, string>? Options = null
);

/// <summary>
/// Heartbeat message sent by clients to maintain connection.
/// </summary>
/// <param name="ClientId">Client sending the heartbeat.</param>
/// <param name="Timestamp">When the heartbeat was sent.</param>
/// <param name="Status">Current client status.</param>
/// <param name="Metrics">Optional telemetry metrics.</param>
public record HeartbeatMessage(
    string ClientId,
    DateTimeOffset Timestamp,
    ClientStatus Status,
    Dictionary<string, object>? Metrics = null
);

#endregion

#region Records - Data Plane

/// <summary>
/// Configuration for data plane connection.
/// </summary>
/// <param name="ServerUrl">Data plane server endpoint URL.</param>
/// <param name="AuthToken">Authentication token for secure transfers.</param>
/// <param name="MaxConcurrentChunks">Maximum number of concurrent chunk transfers.</param>
/// <param name="ChunkSizeBytes">Size of each chunk in bytes.</param>
/// <param name="Timeout">Transfer timeout duration.</param>
/// <param name="Options">Additional transport-specific options.</param>
public record DataPlaneConfig(
    string ServerUrl,
    string AuthToken,
    int MaxConcurrentChunks,
    int ChunkSizeBytes,
    TimeSpan Timeout,
    Dictionary<string, string>? Options = null
);

/// <summary>
/// Transfer progress information.
/// </summary>
/// <param name="BytesTransferred">Number of bytes transferred so far.</param>
/// <param name="TotalBytes">Total bytes to transfer.</param>
/// <param name="PercentComplete">Progress percentage (0-100).</param>
/// <param name="BytesPerSecond">Current transfer rate.</param>
/// <param name="EstimatedRemaining">Estimated time remaining.</param>
public record TransferProgress(
    long BytesTransferred,
    long TotalBytes,
    double PercentComplete,
    double BytesPerSecond,
    TimeSpan EstimatedRemaining
);

/// <summary>
/// Payload metadata for uploads.
/// </summary>
/// <param name="Name">Human-readable name.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="SizeBytes">Total size in bytes.</param>
/// <param name="ContentHash">SHA-256 hash of content.</param>
/// <param name="Tags">Optional metadata tags.</param>
public record PayloadMetadata(
    string Name,
    string ContentType,
    long SizeBytes,
    string ContentHash,
    Dictionary<string, object>? Tags = null
);

#endregion

#region Records - Server Components

/// <summary>
/// Distribution job tracking information.
/// </summary>
/// <param name="JobId">Unique job identifier.</param>
/// <param name="Manifest">The intent manifest being distributed.</param>
/// <param name="Status">Current job status.</param>
/// <param name="TotalTargets">Total number of targets.</param>
/// <param name="DeliveredCount">Number of successful deliveries.</param>
/// <param name="FailedCount">Number of failed deliveries.</param>
/// <param name="QueuedAt">When the job was queued.</param>
/// <param name="CompletedAt">When the job completed (if applicable).</param>
public record DistributionJob(
    string JobId,
    IntentManifest Manifest,
    JobStatus Status,
    int TotalTargets,
    int DeliveredCount,
    int FailedCount,
    DateTimeOffset QueuedAt,
    DateTimeOffset? CompletedAt
);

/// <summary>
/// Filter criteria for querying jobs.
/// </summary>
/// <param name="Status">Filter by job status.</param>
/// <param name="Since">Return jobs created after this timestamp.</param>
/// <param name="ChannelId">Filter by channel ID.</param>
/// <param name="Limit">Maximum number of results.</param>
public record JobFilter(
    JobStatus? Status = null,
    DateTimeOffset? Since = null,
    string? ChannelId = null,
    int Limit = 100
);

/// <summary>
/// Client registration request.
/// </summary>
/// <param name="ClientName">Friendly name for the client.</param>
/// <param name="PublicKey">Client's public key for encryption.</param>
/// <param name="VerificationPin">PIN code for manual verification.</param>
/// <param name="Capabilities">Client capabilities.</param>
public record ClientRegistration(
    string ClientName,
    string PublicKey,
    string VerificationPin,
    ClientCapabilities Capabilities
);

/// <summary>
/// Channel creation request.
/// </summary>
/// <param name="Name">Channel name.</param>
/// <param name="Description">Channel description.</param>
/// <param name="SubscriptionType">Subscription type (mandatory or voluntary).</param>
/// <param name="MinTrustLevel">Minimum trust level required.</param>
public record ChannelCreation(
    string Name,
    string Description,
    SubscriptionType SubscriptionType,
    ClientTrustLevel MinTrustLevel
);

#endregion

#region Records - Client Components

/// <summary>
/// Configuration for client sentinel.
/// </summary>
/// <param name="ServerUrl">Control plane server URL.</param>
/// <param name="ClientId">This client's unique ID.</param>
/// <param name="PrivateKey">This client's private key.</param>
/// <param name="SubscribedChannels">Channels to subscribe to.</param>
/// <param name="HeartbeatInterval">Heartbeat interval.</param>
public record SentinelConfig(
    string ServerUrl,
    string ClientId,
    string PrivateKey,
    string[] SubscribedChannels,
    TimeSpan HeartbeatInterval
);

/// <summary>
/// Event args for manifest received event.
/// </summary>
public class ManifestReceivedEventArgs : EventArgs
{
    /// <summary>The received manifest.</summary>
    public required IntentManifest Manifest { get; init; }

    /// <summary>When the manifest was received.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>
/// Configuration for client executor.
/// </summary>
/// <param name="CachePath">Local cache directory for downloads.</param>
/// <param name="ExecutionSandbox">Sandbox directory for executing code.</param>
/// <param name="AllowUnsigned">Whether to allow unsigned manifests.</param>
/// <param name="TrustedSigningKeys">Dictionary of trusted signing keys.</param>
public record ExecutorConfig(
    string CachePath,
    string ExecutionSandbox,
    bool AllowUnsigned,
    Dictionary<string, string>? TrustedSigningKeys
);

/// <summary>
/// Result of manifest execution.
/// </summary>
/// <param name="ManifestId">The manifest that was executed.</param>
/// <param name="Success">Whether execution succeeded.</param>
/// <param name="Error">Error message if failed.</param>
/// <param name="LocalPath">Local file path if downloaded.</param>
/// <param name="ExecutedAt">When execution completed.</param>
public record ExecutionResult(
    string ManifestId,
    bool Success,
    string? Error,
    string? LocalPath,
    DateTimeOffset ExecutedAt
);

/// <summary>
/// Policy evaluation result.
/// </summary>
/// <param name="Allowed">Whether the action is allowed.</param>
/// <param name="Reason">Reason for the decision.</param>
/// <param name="RequiredAction">Required action to take.</param>
public record PolicyDecision(
    bool Allowed,
    string Reason,
    PolicyAction RequiredAction
);

/// <summary>
/// Configuration for watchdog.
/// </summary>
/// <param name="DebounceInterval">Time to wait before processing changes.</param>
/// <param name="AutoSync">Whether to automatically sync changes.</param>
/// <param name="SyncTargetUrl">URL to sync changes to.</param>
public record WatchdogConfig(
    TimeSpan DebounceInterval,
    bool AutoSync,
    string SyncTargetUrl
);

/// <summary>
/// Watched file information.
/// </summary>
/// <param name="LocalPath">Local file path being watched.</param>
/// <param name="PayloadId">Associated payload ID.</param>
/// <param name="LastModified">Last modification timestamp.</param>
/// <param name="PendingSync">Whether sync is pending.</param>
public record WatchedFile(
    string LocalPath,
    string PayloadId,
    DateTimeOffset LastModified,
    bool PendingSync
);

/// <summary>
/// Event args for file changed event.
/// </summary>
public class FileChangedEventArgs : EventArgs
{
    /// <summary>Local file path that changed.</summary>
    public required string LocalPath { get; init; }

    /// <summary>Associated payload ID.</summary>
    public required string PayloadId { get; init; }

    /// <summary>Type of change detected.</summary>
    public required FileChangeType ChangeType { get; init; }
}

/// <summary>
/// Policy evaluation context.
/// </summary>
/// <param name="SourceTrustLevel">Trust level of the manifest source.</param>
/// <param name="FileSizeBytes">Size of the payload.</param>
/// <param name="NetworkType">Current network type.</param>
/// <param name="Priority">Manifest priority level.</param>
/// <param name="IsPeer">Whether delivery is from a peer (P2P).</param>
public record PolicyContext(
    ClientTrustLevel SourceTrustLevel,
    long FileSizeBytes,
    NetworkType NetworkType,
    int Priority,
    bool IsPeer
);

/// <summary>
/// Client policy rule.
/// </summary>
/// <param name="Name">Rule name.</param>
/// <param name="Condition">Condition expression to evaluate.</param>
/// <param name="Action">Action to take if condition matches.</param>
/// <param name="Reason">Explanation for the action.</param>
public record PolicyRule(
    string Name,
    string Condition,
    PolicyAction Action,
    string? Reason
);

#endregion

#region Interfaces - Control Plane

/// <summary>
/// Control plane transport provider (WebSocket, MQTT, gRPC, etc.).
/// Handles low-bandwidth signaling, commands, and heartbeats.
/// </summary>
public interface IControlPlaneTransport
{
    /// <summary>Transport identifier (e.g., "websocket", "mqtt").</summary>
    string TransportId { get; }

    /// <summary>Whether this transport is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Connect to the control plane.</summary>
    /// <param name="config">Connection configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task ConnectAsync(ControlPlaneConfig config, CancellationToken ct = default);

    /// <summary>Disconnect from the control plane.</summary>
    /// <returns>Task representing the async operation.</returns>
    Task DisconnectAsync();

    /// <summary>Send an intent manifest to clients.</summary>
    /// <param name="manifest">The manifest to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task SendManifestAsync(IntentManifest manifest, CancellationToken ct = default);

    /// <summary>Receive intent manifests (client-side).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of received manifests.</returns>
    IAsyncEnumerable<IntentManifest> ReceiveManifestsAsync(CancellationToken ct = default);

    /// <summary>Send heartbeat.</summary>
    /// <param name="heartbeat">Heartbeat message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task SendHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct = default);

    /// <summary>Subscribe to a channel.</summary>
    /// <param name="channelId">Channel ID to subscribe to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task SubscribeChannelAsync(string channelId, CancellationToken ct = default);

    /// <summary>Unsubscribe from a channel.</summary>
    /// <param name="channelId">Channel ID to unsubscribe from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task UnsubscribeChannelAsync(string channelId, CancellationToken ct = default);
}

#endregion

#region Interfaces - Data Plane

/// <summary>
/// Data plane transport provider (HTTP/3, QUIC, HTTP/2, etc.).
/// Handles high-bandwidth binary blob transfers.
/// </summary>
public interface IDataPlaneTransport
{
    /// <summary>Transport identifier (e.g., "http3", "quic").</summary>
    string TransportId { get; }

    /// <summary>Download a payload from the server.</summary>
    /// <param name="payloadId">Payload ID to download.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream containing the payload data.</returns>
    Task<Stream> DownloadAsync(
        string payloadId,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Download with delta sync (if available).</summary>
    /// <param name="payloadId">Payload ID to download.</param>
    /// <param name="baseVersion">Base version for delta computation.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream containing the delta data.</returns>
    Task<Stream> DownloadDeltaAsync(
        string payloadId,
        string baseVersion,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Upload a payload to the server.</summary>
    /// <param name="data">Payload data stream.</param>
    /// <param name="metadata">Payload metadata.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Payload ID assigned by the server.</returns>
    Task<string> UploadAsync(
        Stream data,
        PayloadMetadata metadata,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Check if a payload exists on the server.</summary>
    /// <param name="payloadId">Payload ID to check.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if payload exists, false otherwise.</returns>
    Task<bool> ExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct = default);

    /// <summary>Get payload info without downloading.</summary>
    /// <param name="payloadId">Payload ID to query.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Payload descriptor if found, null otherwise.</returns>
    Task<PayloadDescriptor?> GetPayloadInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct = default);
}

#endregion

#region Interfaces - Server Components

/// <summary>
/// Server-side dispatcher for managing distribution jobs.
/// </summary>
public interface IServerDispatcher
{
    /// <summary>Queue a new distribution job.</summary>
    /// <param name="manifest">Intent manifest to distribute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Job ID assigned to the distribution job.</returns>
    Task<string> QueueJobAsync(IntentManifest manifest, CancellationToken ct = default);

    /// <summary>Get job status.</summary>
    /// <param name="jobId">Job ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current job status.</returns>
    Task<JobStatus> GetJobStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>Cancel a queued job.</summary>
    /// <param name="jobId">Job ID to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task CancelJobAsync(string jobId, CancellationToken ct = default);

    /// <summary>List all pending jobs.</summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of distribution jobs matching the filter.</returns>
    Task<IReadOnlyList<DistributionJob>> ListJobsAsync(JobFilter? filter = null, CancellationToken ct = default);

    /// <summary>Register a new client.</summary>
    /// <param name="registration">Client registration information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registered client information.</returns>
    Task<AedsClient> RegisterClientAsync(ClientRegistration registration, CancellationToken ct = default);

    /// <summary>Update client trust level (admin operation).</summary>
    /// <param name="clientId">Client ID to update.</param>
    /// <param name="newLevel">New trust level.</param>
    /// <param name="adminId">Admin performing the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task UpdateClientTrustAsync(string clientId, ClientTrustLevel newLevel, string adminId, CancellationToken ct = default);

    /// <summary>Create a distribution channel.</summary>
    /// <param name="channel">Channel creation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created channel information.</returns>
    Task<DistributionChannel> CreateChannelAsync(ChannelCreation channel, CancellationToken ct = default);

    /// <summary>List all channels.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of all distribution channels.</returns>
    Task<IReadOnlyList<DistributionChannel>> ListChannelsAsync(CancellationToken ct = default);
}

#endregion

#region Interfaces - Client Components

/// <summary>
/// The Sentinel: Listens for wake-up signals from the Control Plane.
/// </summary>
public interface IClientSentinel
{
    /// <summary>Whether the sentinel is active.</summary>
    bool IsActive { get; }

    /// <summary>Start listening for manifests.</summary>
    /// <param name="config">Sentinel configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task StartAsync(SentinelConfig config, CancellationToken ct = default);

    /// <summary>Stop listening.</summary>
    /// <returns>Task representing the async operation.</returns>
    Task StopAsync();

    /// <summary>Event raised when a manifest is received.</summary>
    event EventHandler<ManifestReceivedEventArgs>? ManifestReceived;
}

/// <summary>
/// The Executor: Parses Manifests and executes actions.
/// </summary>
public interface IClientExecutor
{
    /// <summary>Execute an intent manifest.</summary>
    /// <param name="manifest">Manifest to execute.</param>
    /// <param name="config">Executor configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    Task<ExecutionResult> ExecuteAsync(IntentManifest manifest, ExecutorConfig config, CancellationToken ct = default);

    /// <summary>Verify manifest signature.</summary>
    /// <param name="manifest">Manifest to verify.</param>
    /// <returns>True if signature is valid, false otherwise.</returns>
    Task<bool> VerifySignatureAsync(IntentManifest manifest);

    /// <summary>Check if action is allowed by policy.</summary>
    /// <param name="manifest">Manifest to evaluate.</param>
    /// <param name="policy">Policy engine to use.</param>
    /// <returns>Policy decision result.</returns>
    Task<PolicyDecision> EvaluatePolicyAsync(IntentManifest manifest, IClientPolicyEngine policy);
}

/// <summary>
/// The Watchdog: Monitors local files for changes to trigger auto-sync.
/// </summary>
public interface IClientWatchdog
{
    /// <summary>Start watching a file for changes.</summary>
    /// <param name="localPath">Local file path to watch.</param>
    /// <param name="payloadId">Associated payload ID.</param>
    /// <param name="config">Watchdog configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task WatchAsync(string localPath, string payloadId, WatchdogConfig config, CancellationToken ct = default);

    /// <summary>Stop watching a file.</summary>
    /// <param name="localPath">Local file path to stop watching.</param>
    /// <returns>Task representing the async operation.</returns>
    Task UnwatchAsync(string localPath);

    /// <summary>Get all watched files.</summary>
    /// <returns>List of currently watched files.</returns>
    IReadOnlyList<WatchedFile> GetWatchedFiles();

    /// <summary>Event raised when a watched file changes.</summary>
    event EventHandler<FileChangedEventArgs>? FileChanged;
}

/// <summary>
/// Client-side policy engine for controlling behavior.
/// </summary>
public interface IClientPolicyEngine
{
    /// <summary>Evaluate whether to allow an action.</summary>
    /// <param name="manifest">Manifest to evaluate.</param>
    /// <param name="context">Evaluation context.</param>
    /// <returns>Policy decision result.</returns>
    Task<PolicyDecision> EvaluateAsync(IntentManifest manifest, PolicyContext context);

    /// <summary>Load policy rules.</summary>
    /// <param name="policyPath">Path to policy file.</param>
    /// <returns>Task representing the async operation.</returns>
    Task LoadPolicyAsync(string policyPath);

    /// <summary>Add a policy rule.</summary>
    /// <param name="rule">Rule to add.</param>
    void AddRule(PolicyRule rule);
}

#endregion
