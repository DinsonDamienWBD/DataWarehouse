using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Hosting;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AedsCore;

/// <summary>
/// Default server-side dispatcher implementation for AEDS job management.
/// </summary>
/// <remarks>
/// <para>
/// The ServerDispatcherPlugin manages the lifecycle of distribution jobs from queuing through
/// completion. It handles:
/// <list type="bullet">
/// <item><description>Job queue management with priority-based ordering</description></item>
/// <item><description>Client registration and trust level management</description></item>
/// <item><description>Distribution channel creation and subscription tracking</description></item>
/// <item><description>Target resolution (unicast, broadcast, multicast)</description></item>
/// <item><description>Job status tracking and progress reporting</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> This implementation uses concurrent collections and locks
/// for thread-safe operations across multiple concurrent distribution jobs.
/// </para>
/// <para>
/// <strong>Storage:</strong> For production deployments, extend this plugin to persist jobs,
/// clients, and channels to a database or distributed cache.
/// </para>
/// </remarks>
[PluginProfile(ServiceProfileType.Server)]
public class ServerDispatcherPlugin : ServerDispatcherPluginBase
{
    private readonly ILogger<ServerDispatcherPlugin> _logger;
    private readonly ConcurrentDictionary<string, Task> _activeJobs = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private int _jobCounter = 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerDispatcherPlugin"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public ServerDispatcherPlugin(ILogger<ServerDispatcherPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.aeds.dispatcher.default";

    /// <inheritdoc />
    public override string Name => "AEDS Server Dispatcher";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc />
    protected override async Task<string> EnqueueJobAsync(IntentManifest manifest, CancellationToken ct)
    {
        await _queueLock.WaitAsync(ct);
        try
        {
            var jobId = $"job-{Interlocked.Increment(ref _jobCounter):D8}";

            var job = new DistributionJob(
                JobId: jobId,
                Manifest: manifest,
                Status: JobStatus.Queued,
                TotalTargets: manifest.Targets.Length,
                DeliveredCount: 0,
                FailedCount: 0,
                QueuedAt: DateTimeOffset.UtcNow,
                CompletedAt: null
            );

            _jobs[jobId] = job;

            _logger.LogInformation(
                "Queued job {JobId} for manifest {ManifestId} targeting {TargetCount} recipients (mode: {DeliveryMode}, priority: {Priority})",
                jobId, manifest.ManifestId, manifest.Targets.Length, manifest.DeliveryMode, manifest.Priority);

            // Start job processing in background
            var processTask = Task.Run(() => ProcessJobAsync(jobId, ct), ct);
            _activeJobs[jobId] = processTask;

            return jobId;
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ProcessJobAsync(string jobId, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Starting processing for job {JobId}", jobId);

            await _lock.WaitAsync(ct);
            DistributionJob job;
            try
            {
                if (!_jobs.TryGetValue(jobId, out job!))
                {
                    _logger.LogError("Job {JobId} not found in job store", jobId);
                    return;
                }

                // Update status to InProgress
                _jobs[jobId] = job with { Status = JobStatus.InProgress };
            }
            finally
            {
                _lock.Release();
            }

            var manifest = job.Manifest;
            int delivered = 0;
            int failed = 0;

            // Resolve targets based on delivery mode
            var targetClients = await ResolveTargetsAsync(manifest, ct);

            _logger.LogInformation("Job {JobId}: Resolved {Count} target clients", jobId, targetClients.Count);

            // Simulate delivery to each target
            foreach (var client in targetClients)
            {
                try
                {
                    // In a real implementation, this would send the manifest via Control Plane
                    // and coordinate Data Plane transfer
                    await Task.Delay(10, ct); // Simulate network latency

                    delivered++;
                    _logger.LogDebug("Job {JobId}: Delivered manifest to client {ClientId}", jobId, client.ClientId);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "Job {JobId}: Failed to deliver to client {ClientId}", jobId, client.ClientId);
                }
            }

            // Update final status
            await _lock.WaitAsync(ct);
            try
            {
                var finalStatus = failed == 0 ? JobStatus.Completed :
                                 delivered == 0 ? JobStatus.Failed :
                                 JobStatus.PartiallyCompleted;

                _jobs[jobId] = job with
                {
                    Status = finalStatus,
                    DeliveredCount = delivered,
                    FailedCount = failed,
                    CompletedAt = DateTimeOffset.UtcNow
                };

                _logger.LogInformation(
                    "Job {JobId} completed with status {Status} (delivered: {Delivered}, failed: {Failed})",
                    jobId, finalStatus, delivered, failed);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId} processing was cancelled", jobId);

            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                if (_jobs.TryGetValue(jobId, out var job))
                {
                    _jobs[jobId] = job with
                    {
                        Status = JobStatus.Cancelled,
                        CompletedAt = DateTimeOffset.UtcNow
                    };
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during job {JobId} processing", jobId);

            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                if (_jobs.TryGetValue(jobId, out var job))
                {
                    _jobs[jobId] = job with
                    {
                        Status = JobStatus.Failed,
                        CompletedAt = DateTimeOffset.UtcNow
                    };
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            _activeJobs.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Resolves target clients based on delivery mode and targeting criteria.
    /// </summary>
    /// <param name="manifest">The intent manifest containing targeting information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of target clients.</returns>
    private async Task<List<AedsClient>> ResolveTargetsAsync(IntentManifest manifest, CancellationToken ct)
    {
        var targets = new List<AedsClient>();

        await _lock.WaitAsync(ct);
        try
        {
            switch (manifest.DeliveryMode)
            {
                case DeliveryMode.Unicast:
                    // Direct targeting by ClientID
                    foreach (var targetId in manifest.Targets)
                    {
                        if (_clients.TryGetValue(targetId, out var client))
                        {
                            targets.Add(client);
                        }
                        else
                        {
                            _logger.LogWarning("Unicast target client {ClientId} not found", targetId);
                        }
                    }
                    break;

                case DeliveryMode.Broadcast:
                    // Send to all clients subscribed to the specified channels
                    foreach (var channelId in manifest.Targets)
                    {
                        if (_channels.TryGetValue(channelId, out var channel))
                        {
                            var subscribers = _clients.Values
                                .Where(c => c.SubscribedChannels.Contains(channelId) &&
                                           c.TrustLevel >= channel.MinTrustLevel)
                                .ToList();

                            targets.AddRange(subscribers);
                            _logger.LogDebug("Broadcast: Found {Count} subscribers for channel {ChannelId}",
                                subscribers.Count, channelId);
                        }
                        else
                        {
                            _logger.LogWarning("Broadcast channel {ChannelId} not found", channelId);
                        }
                    }
                    break;

                case DeliveryMode.Multicast:
                    // Multicast targeting with criteria matching: not yet implemented.
                    _logger.LogWarning("Multicast delivery mode not yet fully implemented");
                    break;

                default:
                    _logger.LogError("Unknown delivery mode: {DeliveryMode}", manifest.DeliveryMode);
                    break;
            }
        }
        finally
        {
            _lock.Release();
        }

        // Remove duplicates
        return targets.DistinctBy(c => c.ClientId).ToList();
    }

    /// <inheritdoc />
    protected override async Task<AedsClient> CreateClientAsync(ClientRegistration registration, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var clientId = $"client-{Guid.NewGuid():N}";

            var client = new AedsClient
            {
                ClientId = clientId,
                Name = registration.ClientName,
                PublicKey = registration.PublicKey,
                TrustLevel = ClientTrustLevel.PendingVerification, // Requires admin approval
                RegisteredAt = DateTimeOffset.UtcNow,
                LastHeartbeat = null,
                SubscribedChannels = Array.Empty<string>(),
                Capabilities = registration.Capabilities
            };

            _clients[clientId] = client;

            _logger.LogInformation(
                "Registered new client {ClientId} (name: {ClientName}, capabilities: {Capabilities}, PIN: {Pin})",
                clientId, registration.ClientName, registration.Capabilities, registration.VerificationPin);

            return client;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<DistributionChannel> CreateChannelInternalAsync(ChannelCreation channel, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var channelId = $"channel-{Guid.NewGuid():N}";

            var distributionChannel = new DistributionChannel
            {
                ChannelId = channelId,
                Name = channel.Name,
                Description = channel.Description,
                SubscriptionType = channel.SubscriptionType,
                MinTrustLevel = channel.MinTrustLevel,
                SubscriberCount = 0,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _channels[channelId] = distributionChannel;

            _logger.LogInformation(
                "Created distribution channel {ChannelId} (name: {Name}, type: {Type}, minTrust: {MinTrust})",
                channelId, channel.Name, channel.SubscriptionType, channel.MinTrustLevel);

            return distributionChannel;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Updates heartbeat timestamp for a client.
    /// </summary>
    /// <param name="clientId">Client ID to update.</param>
    /// <param name="status">Current client status.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    public async Task UpdateHeartbeatAsync(string clientId, ClientStatus status, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_clients.TryGetValue(clientId, out var client))
            {
                _clients[clientId] = client with { LastHeartbeat = DateTimeOffset.UtcNow };
                _logger.LogDebug("Updated heartbeat for client {ClientId} (status: {Status})", clientId, status);
            }
            else
            {
                _logger.LogWarning("Heartbeat received for unknown client {ClientId}", clientId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Subscribes a client to a channel.
    /// </summary>
    /// <param name="clientId">Client ID to subscribe.</param>
    /// <param name="channelId">Channel ID to subscribe to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if subscription succeeded, false otherwise.</returns>
    public async Task<bool> SubscribeClientAsync(string clientId, string channelId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_clients.TryGetValue(clientId, out var client))
            {
                _logger.LogWarning("Cannot subscribe unknown client {ClientId} to channel {ChannelId}", clientId, channelId);
                return false;
            }

            if (!_channels.TryGetValue(channelId, out var channel))
            {
                _logger.LogWarning("Cannot subscribe client {ClientId} to unknown channel {ChannelId}", clientId, channelId);
                return false;
            }

            // Check trust level
            if (client.TrustLevel < channel.MinTrustLevel)
            {
                _logger.LogWarning(
                    "Client {ClientId} (trust: {ClientTrust}) does not meet minimum trust level {MinTrust} for channel {ChannelId}",
                    clientId, client.TrustLevel, channel.MinTrustLevel, channelId);
                return false;
            }

            // Add subscription if not already subscribed
            if (!client.SubscribedChannels.Contains(channelId))
            {
                var updatedChannels = client.SubscribedChannels.Append(channelId).ToArray();
                _clients[clientId] = client with { SubscribedChannels = updatedChannels };

                // Update subscriber count
                _channels[channelId] = channel with { SubscriberCount = channel.SubscriberCount + 1 };

                _logger.LogInformation("Client {ClientId} subscribed to channel {ChannelId}", clientId, channelId);
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _queueLock?.Dispose();
        }
        base.Dispose(disposing);
    }
}
