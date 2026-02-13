using DataWarehouse.SDK.Distribution;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Base class for Control Plane transport plugins.
/// </summary>
/// <remarks>
/// <para>
/// Control Plane transports handle low-bandwidth signaling, commands, and heartbeats between
/// server and clients. Implementations might use WebSocket, MQTT, gRPC streaming, or custom protocols.
/// </para>
/// <para>
/// This base class provides connection lifecycle management and delegates transport-specific
/// implementation to abstract methods.
/// </para>
/// </remarks>
public abstract class ControlPlaneTransportPluginBase : LegacyFeaturePluginBase, IControlPlaneTransport
{
    /// <summary>
    /// Gets the unique transport identifier (e.g., "websocket", "mqtt", "grpc").
    /// </summary>
    public abstract string TransportId { get; }

    /// <summary>
    /// Gets whether this transport is currently connected.
    /// </summary>
    public bool IsConnected { get; protected set; }

    /// <summary>
    /// Gets the current control plane configuration.
    /// </summary>
    protected ControlPlaneConfig? Config { get; private set; }

    /// <summary>
    /// Establishes connection to the control plane server.
    /// </summary>
    /// <param name="config">Connection configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct);

    /// <summary>
    /// Closes the connection to the control plane server.
    /// </summary>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task CloseConnectionAsync();

    /// <summary>
    /// Transmits an intent manifest over the control plane.
    /// </summary>
    /// <param name="manifest">Manifest to transmit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct);

    /// <summary>
    /// Listens for incoming intent manifests from the control plane.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of received manifests.</returns>
    protected abstract IAsyncEnumerable<IntentManifest> ListenForManifestsAsync(CancellationToken ct);

    /// <summary>
    /// Transmits a heartbeat message to maintain connection.
    /// </summary>
    /// <param name="heartbeat">Heartbeat message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task TransmitHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct);

    /// <summary>
    /// Joins (subscribes to) a distribution channel.
    /// </summary>
    /// <param name="channelId">Channel ID to join.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task JoinChannelAsync(string channelId, CancellationToken ct);

    /// <summary>
    /// Leaves (unsubscribes from) a distribution channel.
    /// </summary>
    /// <param name="channelId">Channel ID to leave.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task LeaveChannelAsync(string channelId, CancellationToken ct);

    /// <inheritdoc />
    public async Task ConnectAsync(ControlPlaneConfig config, CancellationToken ct = default)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        Config = config;
        await EstablishConnectionAsync(config, ct);
        IsConnected = true;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        await CloseConnectionAsync();
        IsConnected = false;
    }

    /// <inheritdoc />
    public Task SendManifestAsync(IntentManifest manifest, CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        return TransmitManifestAsync(manifest, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IntentManifest> ReceiveManifestsAsync(CancellationToken ct = default)
        => ListenForManifestsAsync(ct);

    /// <inheritdoc />
    public Task SendHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct = default)
    {
        if (heartbeat == null)
            throw new ArgumentNullException(nameof(heartbeat));

        return TransmitHeartbeatAsync(heartbeat, ct);
    }

    /// <inheritdoc />
    public Task SubscribeChannelAsync(string channelId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(channelId))
            throw new ArgumentException("Channel ID cannot be null or empty.", nameof(channelId));

        return JoinChannelAsync(channelId, ct);
    }

    /// <inheritdoc />
    public Task UnsubscribeChannelAsync(string channelId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(channelId))
            throw new ArgumentException("Channel ID cannot be null or empty.", nameof(channelId));

        return LeaveChannelAsync(channelId, ct);
    }
}

/// <summary>
/// Base class for Data Plane transport plugins.
/// </summary>
/// <remarks>
/// <para>
/// Data Plane transports handle high-bandwidth binary blob transfers between server and clients.
/// Implementations might use HTTP/3 QUIC, raw QUIC streams, HTTP/2, or WebTransport.
/// </para>
/// <para>
/// This base class provides standardized payload transfer operations and delegates transport-specific
/// implementation to abstract methods.
/// </para>
/// </remarks>
public abstract class DataPlaneTransportPluginBase : LegacyFeaturePluginBase, IDataPlaneTransport
{
    /// <summary>
    /// Gets the unique transport identifier (e.g., "http3", "quic", "http2").
    /// </summary>
    public abstract string TransportId { get; }

    /// <summary>
    /// Fetches a payload from the server.
    /// </summary>
    /// <param name="payloadId">Payload ID to fetch.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream containing the payload data.</returns>
    protected abstract Task<Stream> FetchPayloadAsync(
        string payloadId,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// Fetches a delta (diff) between base version and target payload.
    /// </summary>
    /// <param name="payloadId">Target payload ID.</param>
    /// <param name="baseVersion">Base version for delta computation.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream containing the delta data.</returns>
    protected abstract Task<Stream> FetchDeltaAsync(
        string payloadId,
        string baseVersion,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// Pushes a payload to the server.
    /// </summary>
    /// <param name="data">Payload data stream.</param>
    /// <param name="metadata">Payload metadata.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Payload ID assigned by the server.</returns>
    protected abstract Task<string> PushPayloadAsync(
        Stream data,
        PayloadMetadata metadata,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// Checks if a payload exists on the server.
    /// </summary>
    /// <param name="payloadId">Payload ID to check.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if payload exists, false otherwise.</returns>
    protected abstract Task<bool> CheckExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);

    /// <summary>
    /// Fetches payload information without downloading the payload.
    /// </summary>
    /// <param name="payloadId">Payload ID to query.</param>
    /// <param name="config">Data plane configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Payload descriptor if found, null otherwise.</returns>
    protected abstract Task<PayloadDescriptor?> FetchInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);

    /// <inheritdoc />
    public Task<Stream> DownloadAsync(
        string payloadId,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payloadId))
            throw new ArgumentException("Payload ID cannot be null or empty.", nameof(payloadId));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return FetchPayloadAsync(payloadId, config, progress, ct);
    }

    /// <inheritdoc />
    public Task<Stream> DownloadDeltaAsync(
        string payloadId,
        string baseVersion,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payloadId))
            throw new ArgumentException("Payload ID cannot be null or empty.", nameof(payloadId));
        if (string.IsNullOrEmpty(baseVersion))
            throw new ArgumentException("Base version cannot be null or empty.", nameof(baseVersion));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return FetchDeltaAsync(payloadId, baseVersion, config, progress, ct);
    }

    /// <inheritdoc />
    public Task<string> UploadAsync(
        Stream data,
        PayloadMetadata metadata,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (metadata == null)
            throw new ArgumentNullException(nameof(metadata));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return PushPayloadAsync(data, metadata, config, progress, ct);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payloadId))
            throw new ArgumentException("Payload ID cannot be null or empty.", nameof(payloadId));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return CheckExistsAsync(payloadId, config, ct);
    }

    /// <inheritdoc />
    public Task<PayloadDescriptor?> GetPayloadInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payloadId))
            throw new ArgumentException("Payload ID cannot be null or empty.", nameof(payloadId));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return FetchInfoAsync(payloadId, config, ct);
    }
}

/// <summary>
/// Base class for Server Dispatcher plugins.
/// </summary>
/// <remarks>
/// <para>
/// Server dispatchers manage distribution job queues, client registrations, and channel subscriptions.
/// They orchestrate the delivery of intent manifests to targeted clients or channels.
/// </para>
/// <para>
/// This base class provides in-memory storage and delegates job processing logic to abstract methods.
/// Production implementations should use persistent storage (database, message queue, etc.).
/// </para>
/// </remarks>
public abstract class ServerDispatcherPluginBase : LegacyFeaturePluginBase, IServerDispatcher
{
    /// <summary>
    /// In-memory job storage (use persistent storage in production).
    /// </summary>
    protected readonly Dictionary<string, DistributionJob> _jobs = new();

    /// <summary>
    /// In-memory client registry (use persistent storage in production).
    /// </summary>
    protected readonly Dictionary<string, AedsClient> _clients = new();

    /// <summary>
    /// In-memory channel registry (use persistent storage in production).
    /// </summary>
    protected readonly Dictionary<string, DistributionChannel> _channels = new();

    /// <summary>
    /// Lock for thread-safe access to collections.
    /// </summary>
    protected readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Enqueues a new distribution job.
    /// </summary>
    /// <param name="manifest">Intent manifest to distribute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Job ID assigned to the distribution job.</returns>
    protected abstract Task<string> EnqueueJobAsync(IntentManifest manifest, CancellationToken ct);

    /// <summary>
    /// Processes a queued distribution job.
    /// </summary>
    /// <param name="jobId">Job ID to process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task ProcessJobAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Creates a new client registration.
    /// </summary>
    /// <param name="registration">Client registration information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registered client information.</returns>
    protected abstract Task<AedsClient> CreateClientAsync(ClientRegistration registration, CancellationToken ct);

    /// <summary>
    /// Creates a new distribution channel.
    /// </summary>
    /// <param name="channel">Channel creation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created channel information.</returns>
    protected abstract Task<DistributionChannel> CreateChannelInternalAsync(ChannelCreation channel, CancellationToken ct);

    /// <inheritdoc />
    public Task<string> QueueJobAsync(IntentManifest manifest, CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        return EnqueueJobAsync(manifest, ct);
    }

    /// <inheritdoc />
    public async Task<JobStatus> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jobId))
            throw new ArgumentException("Job ID cannot be null or empty.", nameof(jobId));

        await _lock.WaitAsync(ct);
        try
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                throw new KeyNotFoundException($"Job '{jobId}' not found.");

            return job.Status;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jobId))
            throw new ArgumentException("Job ID cannot be null or empty.", nameof(jobId));

        await _lock.WaitAsync(ct);
        try
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                throw new KeyNotFoundException($"Job '{jobId}' not found.");

            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                throw new InvalidOperationException($"Cannot cancel job in status {job.Status}.");

            _jobs[jobId] = job with
            {
                Status = JobStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DistributionJob>> ListJobsAsync(JobFilter? filter = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            IEnumerable<DistributionJob> query = _jobs.Values;

            if (filter != null)
            {
                if (filter.Status.HasValue)
                    query = query.Where(j => j.Status == filter.Status.Value);

                if (filter.Since.HasValue)
                    query = query.Where(j => j.QueuedAt >= filter.Since.Value);

                if (!string.IsNullOrEmpty(filter.ChannelId))
                    query = query.Where(j => j.Manifest.Targets.Contains(filter.ChannelId));

                query = query.Take(filter.Limit);
            }

            return query.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public Task<AedsClient> RegisterClientAsync(ClientRegistration registration, CancellationToken ct = default)
    {
        if (registration == null)
            throw new ArgumentNullException(nameof(registration));

        return CreateClientAsync(registration, ct);
    }

    /// <inheritdoc />
    public async Task UpdateClientTrustAsync(string clientId, ClientTrustLevel newLevel, string adminId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentException("Client ID cannot be null or empty.", nameof(clientId));
        if (string.IsNullOrEmpty(adminId))
            throw new ArgumentException("Admin ID cannot be null or empty.", nameof(adminId));

        await _lock.WaitAsync(ct);
        try
        {
            if (!_clients.TryGetValue(clientId, out var client))
                throw new KeyNotFoundException($"Client '{clientId}' not found.");

            _clients[clientId] = client with { TrustLevel = newLevel };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public Task<DistributionChannel> CreateChannelAsync(ChannelCreation channel, CancellationToken ct = default)
    {
        if (channel == null)
            throw new ArgumentNullException(nameof(channel));

        return CreateChannelInternalAsync(channel, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DistributionChannel>> ListChannelsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _channels.Values.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Starts the server dispatcher.
    /// </summary>
    public override Task StartAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the server dispatcher.
    /// </summary>
    public override Task StopAsync()
    {
        _lock?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Base class for Client Sentinel plugins.
/// </summary>
/// <remarks>
/// <para>
/// Client sentinels listen for wake-up signals from the Control Plane and raise events
/// when intent manifests are received. They maintain heartbeat connections to the server.
/// </para>
/// <para>
/// This base class provides event handling and delegates listening logic to abstract methods.
/// </para>
/// </remarks>
public abstract class ClientSentinelPluginBase : LegacyFeaturePluginBase, IClientSentinel
{
    /// <inheritdoc />
    public bool IsActive { get; protected set; }

    /// <inheritdoc />
    public event EventHandler<ManifestReceivedEventArgs>? ManifestReceived;

    /// <summary>
    /// Starts listening for manifests from the control plane.
    /// </summary>
    /// <param name="config">Sentinel configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task StartListeningAsync(SentinelConfig config, CancellationToken ct);

    /// <summary>
    /// Stops listening for manifests.
    /// </summary>
    /// <returns>Task representing the async operation.</returns>
    protected abstract Task StopListeningAsync();

    /// <summary>
    /// Raises the <see cref="ManifestReceived"/> event.
    /// </summary>
    /// <param name="manifest">The received manifest.</param>
    protected void OnManifestReceived(IntentManifest manifest)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        ManifestReceived?.Invoke(this, new ManifestReceivedEventArgs
        {
            Manifest = manifest,
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Starts the sentinel (called by FeaturePluginBase lifecycle).
    /// </summary>
    public override Task StartAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the sentinel (called by FeaturePluginBase lifecycle).
    /// </summary>
    public override async Task StopAsync()
    {
        await StopListeningAsync();
        IsActive = false;
    }

    /// <summary>
    /// Starts listening with specific sentinel configuration (IClientSentinel implementation).
    /// </summary>
    /// <param name="config">Sentinel configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    Task IClientSentinel.StartAsync(SentinelConfig config, CancellationToken ct)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return StartSentinelAsync(config, ct);
    }

    /// <summary>
    /// Starts listening with specific sentinel configuration.
    /// </summary>
    /// <param name="config">Sentinel configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    protected async Task StartSentinelAsync(SentinelConfig config, CancellationToken ct = default)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        await StartListeningAsync(config, ct);
        IsActive = true;
    }
}

/// <summary>
/// Base class for Client Executor plugins.
/// </summary>
/// <remarks>
/// <para>
/// Client executors parse intent manifests and execute the specified actions (download, execute, interactive).
/// They verify manifest signatures and evaluate policy decisions before execution.
/// </para>
/// <para>
/// This base class provides validation and delegates execution logic to abstract methods.
/// </para>
/// </remarks>
public abstract class ClientExecutorPluginBase : LegacyFeaturePluginBase, IClientExecutor
{
    /// <summary>
    /// Performs the actual execution of an intent manifest.
    /// </summary>
    /// <param name="manifest">Manifest to execute.</param>
    /// <param name="config">Executor configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    protected abstract Task<ExecutionResult> PerformExecutionAsync(
        IntentManifest manifest,
        ExecutorConfig config,
        CancellationToken ct);

    /// <summary>
    /// Validates the cryptographic signature of a manifest.
    /// </summary>
    /// <param name="manifest">Manifest to validate.</param>
    /// <returns>True if signature is valid, false otherwise.</returns>
    protected abstract Task<bool> ValidateSignatureAsync(IntentManifest manifest);

    /// <summary>
    /// Applies policy evaluation to determine if action is allowed.
    /// </summary>
    /// <param name="manifest">Manifest to evaluate.</param>
    /// <param name="policy">Policy engine to use.</param>
    /// <returns>Policy decision result.</returns>
    protected abstract Task<PolicyDecision> ApplyPolicyAsync(IntentManifest manifest, IClientPolicyEngine policy);

    /// <inheritdoc />
    public Task<ExecutionResult> ExecuteAsync(
        IntentManifest manifest,
        ExecutorConfig config,
        CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return PerformExecutionAsync(manifest, config, ct);
    }

    /// <inheritdoc />
    public Task<bool> VerifySignatureAsync(IntentManifest manifest)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        return ValidateSignatureAsync(manifest);
    }

    /// <inheritdoc />
    public Task<PolicyDecision> EvaluatePolicyAsync(IntentManifest manifest, IClientPolicyEngine policy)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        return ApplyPolicyAsync(manifest, policy);
    }
}
