using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Distribution
{
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
    /// Client status enumeration.
    /// </summary>
    public enum ClientStatus
    {
        /// <summary>Client is online and available.</summary>
        Online,

        /// <summary>Client is online but busy processing.</summary>
        Busy,

        /// <summary>Client is online but away/idle.</summary>
        Away,

        /// <summary>Client is offline.</summary>
        Offline
    }

    /// <summary>
    /// Job status enumeration.
    /// </summary>
    public enum JobStatus
    {
        /// <summary>Job is queued and waiting to be processed.</summary>
        Queued,

        /// <summary>Job is currently being processed.</summary>
        InProgress,

        /// <summary>Job completed successfully to all targets.</summary>
        Completed,

        /// <summary>Job completed but some targets failed.</summary>
        PartiallyCompleted,

        /// <summary>Job failed completely.</summary>
        Failed,

        /// <summary>Job was cancelled by administrator.</summary>
        Cancelled
    }

    /// <summary>
    /// Client capability flags.
    /// </summary>
    [Flags]
    public enum ClientCapabilities
    {
        /// <summary>No capabilities.</summary>
        None = 0,

        /// <summary>Can receive passive downloads.</summary>
        ReceivePassive = 1,

        /// <summary>Can receive and display notifications.</summary>
        ReceiveNotify = 2,

        /// <summary>Can execute signed code.</summary>
        ExecuteSigned = 4,

        /// <summary>Supports interactive file editing.</summary>
        Interactive = 8,

        /// <summary>Can participate in P2P mesh.</summary>
        P2PMesh = 16,

        /// <summary>Supports delta sync.</summary>
        DeltaSync = 32,

        /// <summary>Can function as air-gap mule.</summary>
        AirGapMule = 64,

        /// <summary>All capabilities enabled.</summary>
        All = ReceivePassive | ReceiveNotify | ExecuteSigned | Interactive | P2PMesh | DeltaSync | AirGapMule
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
        /// <param name="config">Control plane configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        Task ConnectAsync(ControlPlaneConfig config, CancellationToken ct = default);

        /// <summary>Disconnect from the control plane.</summary>
        /// <returns>Task representing the async operation.</returns>
        Task DisconnectAsync();

        /// <summary>Send an intent manifest to clients.</summary>
        /// <param name="manifest">Intent manifest to send.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        Task SendManifestAsync(IntentManifest manifest, CancellationToken ct = default);

        /// <summary>Receive intent manifests (client-side).</summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async stream of intent manifests.</returns>
        IAsyncEnumerable<IntentManifest> ReceiveManifestsAsync(CancellationToken ct = default);

        /// <summary>Send heartbeat.</summary>
        /// <param name="heartbeat">Heartbeat message.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        Task SendHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct = default);

        /// <summary>Subscribe to a channel.</summary>
        /// <param name="channelId">Channel identifier to subscribe to.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        Task SubscribeChannelAsync(string channelId, CancellationToken ct = default);

        /// <summary>Unsubscribe from a channel.</summary>
        /// <param name="channelId">Channel identifier to unsubscribe from.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        Task UnsubscribeChannelAsync(string channelId, CancellationToken ct = default);
    }

    /// <summary>
    /// Control plane configuration.
    /// </summary>
    public record ControlPlaneConfig(
        string ServerUrl,
        string ClientId,
        string AuthToken,
        TimeSpan HeartbeatInterval,
        TimeSpan ReconnectDelay,
        Dictionary<string, string>? Options = null
    );

    /// <summary>
    /// Heartbeat message sent by clients.
    /// </summary>
    public record HeartbeatMessage(
        string ClientId,
        DateTimeOffset Timestamp,
        ClientStatus Status,
        Dictionary<string, object>? Metrics = null
    );

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
        /// <param name="payloadId">Payload identifier to download.</param>
        /// <param name="config">Data plane configuration.</param>
        /// <param name="progress">Progress reporter (optional).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Stream containing the downloaded payload.</returns>
        Task<Stream> DownloadAsync(string payloadId, DataPlaneConfig config, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);

        /// <summary>Download with delta sync (if available).</summary>
        /// <param name="payloadId">Payload identifier to download.</param>
        /// <param name="baseVersion">Base version for delta computation.</param>
        /// <param name="config">Data plane configuration.</param>
        /// <param name="progress">Progress reporter (optional).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Stream containing the delta-synced payload.</returns>
        Task<Stream> DownloadDeltaAsync(string payloadId, string baseVersion, DataPlaneConfig config, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);

        /// <summary>Upload a payload to the server.</summary>
        /// <param name="data">Data stream to upload.</param>
        /// <param name="metadata">Payload metadata.</param>
        /// <param name="config">Data plane configuration.</param>
        /// <param name="progress">Progress reporter (optional).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Payload identifier assigned by the server.</returns>
        Task<string> UploadAsync(Stream data, PayloadMetadata metadata, DataPlaneConfig config, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);

        /// <summary>Check if a payload exists on the server.</summary>
        /// <param name="payloadId">Payload identifier to check.</param>
        /// <param name="config">Data plane configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the payload exists, false otherwise.</returns>
        Task<bool> ExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct = default);

        /// <summary>Get payload info without downloading.</summary>
        /// <param name="payloadId">Payload identifier.</param>
        /// <param name="config">Data plane configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Payload descriptor if found, null otherwise.</returns>
        Task<PayloadDescriptor?> GetPayloadInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct = default);
    }

    /// <summary>
    /// Data plane configuration.
    /// </summary>
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
    public record PayloadMetadata(
        string Name,
        string ContentType,
        long SizeBytes,
        string ContentHash,
        Dictionary<string, object>? Tags = null
    );

    #endregion

    #region Interfaces - Server Components

    /// <summary>
    /// Server-side dispatcher for managing distribution jobs.
    /// </summary>
    public interface IServerDispatcher
    {
        /// <summary>Queue a new distribution job.</summary>
        /// <param name="manifest">Intent manifest defining the distribution job.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Unique job identifier.</returns>
        Task<string> QueueJobAsync(IntentManifest manifest, CancellationToken ct = default);

        /// <summary>Get job status.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current job status.</returns>
        Task<JobStatus> GetJobStatusAsync(string jobId, CancellationToken ct = default);

        /// <summary>Cancel a queued job.</summary>
        /// <param name="jobId">Job identifier to cancel.</param>
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
        /// <returns>Registered AEDS client.</returns>
        Task<AedsClient> RegisterClientAsync(ClientRegistration registration, CancellationToken ct = default);

        /// <summary>Update client trust level (admin operation).</summary>
        /// <param name="clientId">Client identifier.</param>
        /// <param name="newLevel">New trust level to assign.</param>
        /// <param name="adminId">Administrator performing the change.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        Task UpdateClientTrustAsync(string clientId, ClientTrustLevel newLevel, string adminId, CancellationToken ct = default);

        /// <summary>Create a distribution channel.</summary>
        /// <param name="channel">Channel creation information.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Created distribution channel.</returns>
        Task<DistributionChannel> CreateChannelAsync(ChannelCreation channel, CancellationToken ct = default);

        /// <summary>List all channels.</summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of all distribution channels.</returns>
        Task<IReadOnlyList<DistributionChannel>> ListChannelsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Distribution job record.
    /// </summary>
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
    /// Job filter criteria.
    /// </summary>
    public record JobFilter(
        JobStatus? Status = null,
        DateTimeOffset? Since = null,
        string? ChannelId = null,
        int Limit = 100
    );

    /// <summary>
    /// Client registration request.
    /// </summary>
    public record ClientRegistration(
        string ClientName,
        string PublicKey,
        string VerificationPin,
        ClientCapabilities Capabilities
    );

    /// <summary>
    /// Channel creation request.
    /// </summary>
    public record ChannelCreation(
        string Name,
        string Description,
        SubscriptionType SubscriptionType,
        ClientTrustLevel MinTrustLevel
    );

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
    /// Sentinel configuration.
    /// </summary>
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
    /// The Executor: Parses Manifests and executes actions.
    /// </summary>
    public interface IClientExecutor
    {
        /// <summary>Execute an intent manifest.</summary>
        /// <param name="manifest">Intent manifest to execute.</param>
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
        /// <returns>Policy decision.</returns>
        Task<PolicyDecision> EvaluatePolicyAsync(IntentManifest manifest, IClientPolicyEngine policy);
    }

    /// <summary>
    /// Executor configuration.
    /// </summary>
    public record ExecutorConfig(
        string CachePath,
        bool AllowExecute,
        bool AllowInteractive,
        TimeSpan ExecutionTimeout,
        Dictionary<string, string>? Options = null
    );

    /// <summary>
    /// Execution result.
    /// </summary>
    public record ExecutionResult(
        bool Success,
        string? ErrorMessage = null,
        Dictionary<string, object>? Metadata = null
    );

    /// <summary>
    /// Policy decision.
    /// </summary>
    public record PolicyDecision(
        bool Allowed,
        string Reason,
        string? RequiredApproval = null
    );

    /// <summary>
    /// Client policy engine interface.
    /// </summary>
    public interface IClientPolicyEngine
    {
        /// <summary>Evaluate whether a manifest action is allowed.</summary>
        /// <param name="manifest">Manifest to evaluate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Policy decision.</returns>
        Task<PolicyDecision> EvaluateAsync(IntentManifest manifest, CancellationToken ct = default);
    }

    /// <summary>
    /// The Watchdog: Monitors interactive file changes and syncs back.
    /// </summary>
    public interface IClientWatchdog
    {
        /// <summary>Start watching a file for changes.</summary>
        /// <param name="filePath">File path to watch.</param>
        /// <param name="manifest">Original manifest.</param>
        /// <param name="config">Watchdog configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        Task StartWatchingAsync(string filePath, IntentManifest manifest, WatchdogConfig config, CancellationToken ct = default);

        /// <summary>Stop watching a file.</summary>
        /// <param name="filePath">File path to stop watching.</param>
        /// <returns>Task representing the async operation.</returns>
        Task StopWatchingAsync(string filePath);

        /// <summary>Event raised when a file change is detected.</summary>
        event EventHandler<FileChangedEventArgs>? FileChanged;
    }

    /// <summary>
    /// Watchdog configuration.
    /// </summary>
    public record WatchdogConfig(
        TimeSpan DebounceDelay,
        bool AutoSync,
        string ServerUrl,
        string AuthToken,
        Dictionary<string, string>? Options = null
    );

    /// <summary>
    /// Event args for file changed event.
    /// </summary>
    public class FileChangedEventArgs : EventArgs
    {
        /// <summary>File path that changed.</summary>
        public required string FilePath { get; init; }

        /// <summary>When the change was detected.</summary>
        public required DateTimeOffset DetectedAt { get; init; }

        /// <summary>Original manifest.</summary>
        public required IntentManifest Manifest { get; init; }
    }

    #endregion
}
