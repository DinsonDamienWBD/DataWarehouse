using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed.Discovery
{
    /// <summary>
    /// Zero-configuration cluster bootstrap that wires mDNS service discovery
    /// to SWIM cluster membership for automatic node joining.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 40: Zero-config cluster bootstrap")]
    public sealed class ZeroConfigClusterBootstrap : IDisposable, IAsyncDisposable
    {
        private readonly IClusterMembership _membership;
        private readonly MdnsServiceDiscovery _discovery;
        private readonly ZeroConfigOptions _options;
        private readonly SemaphoreSlim _joinLock = new(1, 1);
        private readonly HashSet<string> _pendingJoins = new();
        private readonly object _pendingLock = new();

        private CancellationTokenSource? _debounceCts;
        private Task? _debounceTask;

        /// <summary>
        /// Creates a new zero-config cluster bootstrap.
        /// </summary>
        /// <param name="membership">Cluster membership service to wire discovery to.</param>
        /// <param name="discovery">mDNS service discovery.</param>
        /// <param name="options">Optional configuration.</param>
        public ZeroConfigClusterBootstrap(
            IClusterMembership membership,
            MdnsServiceDiscovery discovery,
            ZeroConfigOptions? options = null)
        {
            _membership = membership ?? throw new ArgumentNullException(nameof(membership));
            _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
            _options = options ?? new ZeroConfigOptions();
        }

        /// <summary>
        /// Starts zero-config cluster bootstrap:
        /// - Announces this node via mDNS
        /// - Listens for other nodes via mDNS
        /// - Automatically joins discovered nodes to the cluster
        /// </summary>
        /// <param name="ct">Cancellation token to stop bootstrap.</param>
        public async Task StartAsync(CancellationToken ct = default)
        {
            // Subscribe to discovery events before starting
            _discovery.OnServiceDiscovered += HandleServiceDiscovered;

            // Start announcing ourselves
            await _discovery.StartAnnouncingAsync(ct);

            // Start listening for others
            await _discovery.StartListeningAsync(ct);
        }

        /// <summary>
        /// Stops zero-config cluster bootstrap and optionally leaves the cluster.
        /// </summary>
        public async Task StopAsync()
        {
            // Unsubscribe from discovery events
            _discovery.OnServiceDiscovered -= HandleServiceDiscovered;

            // Stop mDNS discovery
            await _discovery.StopAsync();

            // Cancel any pending debounce operations
            _debounceCts?.Cancel();
            if (_debounceTask != null)
                await _debounceTask.ConfigureAwait(false);

            // Optionally leave the cluster
            if (_options.AutoLeaveOnStop)
            {
                try
                {
                    await _membership.LeaveAsync("Zero-config bootstrap stopped");
                }
                catch
                {
                    // Best effort - ignore errors
                }
            }
        }

        private void HandleServiceDiscovered(DiscoveredService service)
        {
            // Check if this node is already a known member
            var existingMembers = _membership.GetMembers();
            if (existingMembers.Any(m => m.NodeId == service.NodeId))
            {
                // Already a member, skip
                return;
            }

            // Add to pending joins (debounced)
            lock (_pendingLock)
            {
                _pendingJoins.Add(service.NodeId);
            }

            // Start or restart debounce timer
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var localCts = _debounceCts;

            _debounceTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_options.DiscoveryDebounceMs, localCts.Token);

                    // Debounce period expired - process pending joins
                    List<DiscoveredService> servicesToJoin;
                    lock (_pendingLock)
                    {
                        var allDiscovered = _discovery.GetDiscoveredServices();
                        servicesToJoin = allDiscovered
                            .Where(s => _pendingJoins.Contains(s.NodeId))
                            .ToList();
                        _pendingJoins.Clear();
                    }

                    if (servicesToJoin.Any())
                    {
                        await JoinDiscoveredNodesAsync(servicesToJoin, localCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when debounce is cancelled
                }
            });
        }

        private async Task JoinDiscoveredNodesAsync(List<DiscoveredService> services, CancellationToken ct)
        {
            // Serialize join operations
            await _joinLock.WaitAsync(ct);
            try
            {
                foreach (var service in services)
                {
                    if (ct.IsCancellationRequested) break;

                    // Create join request for the discovered node
                    var joinRequest = new ClusterJoinRequest
                    {
                        NodeId = service.NodeId,
                        Address = service.Address,
                        Port = service.Port,
                        RequestedRole = ClusterNodeRole.Follower,
                        Metadata = new Dictionary<string, string>
                        {
                            { "discovery", "mdns" },
                            { "version", service.Version },
                            { "discovered_at", service.DiscoveredAt.ToString("O") }
                        }
                    };

                    // Retry join with exponential backoff
                    var attempt = 0;
                    var success = false;
                    while (attempt < _options.MaxJoinAttempts && !success && !ct.IsCancellationRequested)
                    {
                        attempt++;
                        try
                        {
                            Console.WriteLine($"[ZeroConfigClusterBootstrap] Attempting to join node {service.NodeId} (attempt {attempt}/{_options.MaxJoinAttempts})");
                            await _membership.JoinAsync(joinRequest, ct);
                            success = true;
                            Console.WriteLine($"[ZeroConfigClusterBootstrap] Successfully joined node {service.NodeId}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ZeroConfigClusterBootstrap] Failed to join node {service.NodeId} (attempt {attempt}): {ex.Message}");

                            if (attempt < _options.MaxJoinAttempts)
                            {
                                var delayMs = _options.JoinRetryDelayMs * (int)Math.Pow(2, attempt - 1); // Exponential backoff
                                await Task.Delay(delayMs, ct);
                            }
                        }
                    }

                    if (!success)
                    {
                        Console.WriteLine($"[ZeroConfigClusterBootstrap] Failed to join node {service.NodeId} after {_options.MaxJoinAttempts} attempts");
                    }
                }
            }
            finally
            {
                _joinLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously disposes cluster bootstrap resources.
        /// Preferred over <see cref="Dispose"/> to avoid sync-over-async.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _joinLock.Dispose();
        }

        /// <summary>
        /// Synchronously disposes cluster bootstrap resources. Prefer <see cref="DisposeAsync"/>.
        /// </summary>
        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _joinLock.Dispose();
        }
    }

    /// <summary>
    /// Configuration options for zero-config cluster bootstrap.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 40: Zero-config cluster bootstrap")]
    public record ZeroConfigOptions
    {
        /// <summary>
        /// Whether to automatically leave the cluster when StopAsync is called (default true).
        /// </summary>
        public bool AutoLeaveOnStop { get; init; } = true;

        /// <summary>
        /// Debounce time in milliseconds for batching multiple discoveries (default 2000ms).
        /// </summary>
        public int DiscoveryDebounceMs { get; init; } = 2000;

        /// <summary>
        /// Maximum number of join attempts per discovered node (default 3).
        /// </summary>
        public int MaxJoinAttempts { get; init; } = 3;

        /// <summary>
        /// Initial retry delay in milliseconds (exponential backoff applied, default 5000ms).
        /// </summary>
        public int JoinRetryDelayMs { get; init; } = 5000;
    }
}
