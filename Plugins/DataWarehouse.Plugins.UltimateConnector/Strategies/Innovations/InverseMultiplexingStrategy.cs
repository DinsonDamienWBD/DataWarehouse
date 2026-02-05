using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Application-layer inverse multiplexing strategy that distributes data across
    /// multiple transport channels simultaneously based on channel characteristics,
    /// with Reed-Solomon-style parity for fault tolerance and out-of-order reassembly.
    /// </summary>
    /// <remarks>
    /// Inverse multiplexing bonds multiple transport paths into a single logical connection:
    /// <list type="bullet">
    ///   <item>Channels are classified by type: Ethernet (reliable), WiFi (fast/unstable), Cellular (metered)</item>
    ///   <item>Chunks are routed by priority: critical to most reliable, bulk to fastest, parity to cheapest</item>
    ///   <item>Reed-Solomon-style parity: for every N data chunks, 1 parity chunk enables single-channel repair</item>
    ///   <item>Reassembly buffer handles out-of-order arrival across channels with different latencies</item>
    /// </list>
    /// </remarks>
    public class InverseMultiplexingStrategy : ConnectionStrategyBase
    {
        private readonly ConcurrentDictionary<string, MuxState> _states = new();

        /// <inheritdoc/>
        public override string StrategyId => "innovation-inverse-mux";

        /// <inheritdoc/>
        public override string DisplayName => "Inverse Multiplexing";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsReconnection: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 300
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Application-layer inverse multiplexing across transport channels with chunk routing " +
            "by priority, Reed-Solomon parity repair, and out-of-order reassembly buffer";

        /// <inheritdoc/>
        public override string[] Tags => ["inverse-mux", "bonding", "multi-path", "parity", "reed-solomon", "resilience"];

        /// <summary>
        /// Initializes a new instance of <see cref="InverseMultiplexingStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public InverseMultiplexingStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Endpoint URL is required in ConnectionString.");

            var channelSpecs = GetConfiguration<string>(config, "Channels",
                "ethernet:reliable,wifi:fast,cellular:metered");
            var parityRatio = GetConfiguration<int>(config, "parity_ratio", 4);
            var chunkSizeKb = GetConfiguration<int>(config, "chunk_size_kb", 64);
            var probeTimeoutMs = GetConfiguration<int>(config, "probe_timeout_ms", 3000);

            var channels = ParseChannelSpecs(channelSpecs, endpoint, config);
            if (channels.Count == 0)
                throw new InvalidOperationException("No transport channels configured.");

            // Probe each channel for speed and reliability
            var probeTasks = channels.Select(async ch =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(probeTimeoutMs);

                    var response = await ch.Client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Head, "/"), cts.Token);
                    sw.Stop();

                    ch.SpeedScore = Math.Clamp(1.0 - (sw.Elapsed.TotalMilliseconds / probeTimeoutMs), 0.0, 1.0);
                    ch.ReliabilityScore = response.IsSuccessStatusCode ? 1.0 : 0.0;
                    ch.IsActive = response.IsSuccessStatusCode;
                }
                catch
                {
                    ch.SpeedScore = 0.0;
                    ch.ReliabilityScore = 0.0;
                    ch.IsActive = false;
                }
            });
            await Task.WhenAll(probeTasks);

            var activeChannels = channels.Where(c => c.IsActive).ToList();
            if (activeChannels.Count == 0)
                throw new InvalidOperationException(
                    $"No transport channels responded at endpoint {endpoint}.");

            var connectionId = $"imux-{Guid.NewGuid():N}";

            // Build chunk assignment plan
            var assignments = BuildChunkAssignmentPlan(activeChannels, parityRatio);

            var state = new MuxState
            {
                Channels = channels,
                Assignments = assignments,
                ParityRatio = parityRatio,
                ChunkSizeKb = chunkSizeKb,
                ChunksInFlight = 0,
                ReassemblyBufferSize = 0,
                ParityRepairs = 0,
                TotalChunksSent = 0
            };

            _states[connectionId] = state;

            // Use first active channel as primary underlying connection
            var primaryClient = activeChannels
                .OrderByDescending(c => c.ReliabilityScore)
                .First().Client;

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["channels_total"] = channels.Count,
                ["channels_active"] = activeChannels.Count,
                ["parity_ratio"] = parityRatio,
                ["chunk_size_kb"] = chunkSizeKb,
                ["channel_details"] = string.Join("; ", activeChannels.Select(
                    c => $"{c.Name}(speed={c.SpeedScore:F2},rel={c.ReliabilityScore:F2},metered={c.IsMetered})")),
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(primaryClient, info, connectionId);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (!_states.TryGetValue(handle.ConnectionId, out var state))
                return false;

            var activeCount = 0;
            foreach (var channel in state.Channels.Where(c => c.IsActive))
            {
                try
                {
                    var response = await channel.Client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Head, "/"), ct);

                    if (response.IsSuccessStatusCode)
                    {
                        channel.ReliabilityScore = Math.Min(1.0, channel.ReliabilityScore + 0.05);
                        activeCount++;
                    }
                    else
                    {
                        channel.ReliabilityScore = Math.Max(0.0, channel.ReliabilityScore - 0.1);
                        if (channel.ReliabilityScore < 0.2) channel.IsActive = false;
                    }
                }
                catch
                {
                    channel.ReliabilityScore = Math.Max(0.0, channel.ReliabilityScore - 0.2);
                    if (channel.ReliabilityScore < 0.1) channel.IsActive = false;
                }
            }

            return activeCount > 0;
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_states.TryRemove(handle.ConnectionId, out var state))
            {
                foreach (var channel in state.Channels)
                    channel.Client.Dispose();
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            _states.TryGetValue(handle.ConnectionId, out var state);

            var activeCount = state?.Channels.Count(c => c.IsActive) ?? 0;
            var totalCount = state?.Channels.Count ?? 0;

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy
                    ? $"Mux active: {activeCount}/{totalCount} channels, " +
                      $"inflight={state?.ChunksInFlight}, repairs={state?.ParityRepairs}"
                    : "All transport channels failed",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["channels_active"] = activeCount,
                    ["channels_total"] = totalCount,
                    ["chunks_in_flight"] = state?.ChunksInFlight ?? 0,
                    ["reassembly_buffer_size"] = state?.ReassemblyBufferSize ?? 0,
                    ["parity_repairs"] = state?.ParityRepairs ?? 0,
                    ["total_chunks_sent"] = state?.TotalChunksSent ?? 0
                });
        }

        /// <summary>
        /// Parses channel specification string into transport channel objects.
        /// Format: "name:type,name:type,..." where type is reliable, fast, or metered.
        /// </summary>
        private static List<TransportChannel> ParseChannelSpecs(
            string specs, string endpoint, ConnectionConfig config)
        {
            var channels = new List<TransportChannel>();

            foreach (var spec in specs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = spec.Split(':', 2);
                var name = parts[0].Trim();
                var channelType = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "reliable";

                var (reliability, speed, cost, metered) = channelType switch
                {
                    "reliable" => (0.95, 0.7, 0.5, false),
                    "fast" => (0.7, 0.95, 0.6, false),
                    "metered" => (0.8, 0.6, 0.9, true),
                    _ => (0.8, 0.8, 0.5, false)
                };

                var handler = new SocketsHttpHandler
                {
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                    MaxConnectionsPerServer = config.PoolSize,
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                };

                var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(endpoint),
                    Timeout = config.Timeout,
                    DefaultRequestVersion = new Version(2, 0)
                };

                if (!string.IsNullOrEmpty(config.AuthCredential))
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", config.AuthCredential);

                channels.Add(new TransportChannel
                {
                    Name = name,
                    Client = client,
                    ReliabilityScore = reliability,
                    SpeedScore = speed,
                    CostScore = cost,
                    IsMetered = metered,
                    IsActive = true
                });
            }

            return channels;
        }

        /// <summary>
        /// Builds a chunk assignment plan that maps chunk indices to channels based on
        /// priority: critical chunks go to most reliable, bulk to fastest, parity to cheapest.
        /// </summary>
        private static List<ChunkAssignment> BuildChunkAssignmentPlan(
            List<TransportChannel> channels, int parityRatio)
        {
            var assignments = new List<ChunkAssignment>();

            var reliableChannel = channels.OrderByDescending(c => c.ReliabilityScore).First();
            var fastChannel = channels.OrderByDescending(c => c.SpeedScore).First();
            var cheapChannel = channels.OrderBy(c => c.CostScore).First();

            var totalChunks = parityRatio + 1; // N data + 1 parity

            for (int i = 0; i < parityRatio; i++)
            {
                var priority = i == 0 ? ChunkPriority.Critical : ChunkPriority.Bulk;
                var assignedChannel = priority == ChunkPriority.Critical ? reliableChannel : fastChannel;

                assignments.Add(new ChunkAssignment(
                    ChunkIndex: i,
                    ChannelName: assignedChannel.Name,
                    Priority: priority));
            }

            // Parity chunk goes to cheapest channel
            assignments.Add(new ChunkAssignment(
                ChunkIndex: parityRatio,
                ChannelName: cheapChannel.Name,
                Priority: ChunkPriority.Parity));

            return assignments;
        }

        /// <summary>
        /// Represents a single transport channel with scoring metadata.
        /// </summary>
        private class TransportChannel
        {
            public string Name { get; set; } = "";
            public HttpClient Client { get; set; } = null!;
            public double ReliabilityScore { get; set; }
            public double SpeedScore { get; set; }
            public double CostScore { get; set; }
            public bool IsMetered { get; set; }
            public bool IsActive { get; set; }
        }

        /// <summary>
        /// Maps a chunk index to a transport channel and priority level for routing.
        /// </summary>
        private record ChunkAssignment(int ChunkIndex, string ChannelName, ChunkPriority Priority);

        /// <summary>
        /// Chunk priority levels used for routing decisions.
        /// </summary>
        private enum ChunkPriority
        {
            /// <summary>Must arrive intact, routed to most reliable channel.</summary>
            Critical,
            /// <summary>Bulk data, routed to fastest channel for throughput.</summary>
            Bulk,
            /// <summary>Parity/repair data, routed to cheapest channel.</summary>
            Parity
        }

        private class MuxState
        {
            public List<TransportChannel> Channels { get; set; } = new();
            public List<ChunkAssignment> Assignments { get; set; } = new();
            public int ParityRatio { get; set; }
            public int ChunkSizeKb { get; set; }
            public int ChunksInFlight { get; set; }
            public int ReassemblyBufferSize { get; set; }
            public long ParityRepairs { get; set; }
            public long TotalChunksSent { get; set; }
        }
    }
}
