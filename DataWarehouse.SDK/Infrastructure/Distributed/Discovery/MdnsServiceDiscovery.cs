using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed.Discovery
{
    /// <summary>
    /// mDNS/DNS-SD service discovery for DataWarehouse instances.
    /// Implements RFC 6762 multicast DNS for zero-configuration service announcement and discovery.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 40: Zero-config mDNS discovery")]
    public sealed class MdnsServiceDiscovery : IDisposable, IAsyncDisposable
    {
        private const string ServiceType = "_datawarehouse._tcp.local";
        private const string MulticastAddressV4 = "224.0.0.251";
        private const int MdnsPort = 5353;
        private const ushort DnsTypeSrv = 33;
        private const ushort DnsTypeTxt = 16;
        private const ushort DnsClassIn = 1;
        private const ushort DnsClassInCacheFlush = 0x8001;

        private readonly string _nodeId;
        private readonly string _address;
        private readonly int _port;
        private readonly MdnsConfiguration _config;
        private readonly List<DiscoveredService> _discoveredServices = new();
        private readonly object _servicesLock = new();

        private UdpClient? _announceClient;
        private UdpClient? _listenClient;
        private CancellationTokenSource? _announceCts;
        private CancellationTokenSource? _listenCts;
        private Task? _announceTask;
        private Task? _listenTask;

        /// <summary>
        /// Event fired when a new DataWarehouse service is discovered on the network.
        /// </summary>
        public event Action<DiscoveredService>? OnServiceDiscovered;

        /// <summary>
        /// Event fired when a previously discovered service is no longer available.
        /// </summary>
        public event Action<string>? OnServiceLost;

        /// <summary>
        /// Creates a new mDNS service discovery instance.
        /// </summary>
        /// <param name="nodeId">Unique identifier for this node.</param>
        /// <param name="address">Network address for this node.</param>
        /// <param name="port">Port this node listens on.</param>
        /// <param name="config">Optional mDNS configuration.</param>
        public MdnsServiceDiscovery(string nodeId, string address, int port, MdnsConfiguration? config = null)
        {
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _port = port;
            _config = config ?? new MdnsConfiguration();
        }

        /// <summary>
        /// Starts announcing this DataWarehouse instance via mDNS multicast.
        /// Sends rapid announcements at startup, then periodic announcements.
        /// </summary>
        /// <param name="ct">Cancellation token to stop announcing.</param>
        public async Task StartAnnouncingAsync(CancellationToken ct = default)
        {
            _announceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                _announceClient = CreateMulticastClient();
                var multicastEndpoint = new IPEndPoint(IPAddress.Parse(_config.MulticastAddress), _config.Port);

                _announceTask = Task.Run(async () =>
                {
                    // RFC 6762: Send 3 rapid announcements at startup (0ms, 1000ms, 2000ms)
                    var rapidDelays = new[] { 0, 1000, 2000 };
                    foreach (var delay in rapidDelays)
                    {
                        if (_announceCts.Token.IsCancellationRequested) return;
                        if (delay > 0) await Task.Delay(delay, _announceCts.Token);
                        await SendAnnouncementAsync(multicastEndpoint, _announceCts.Token);
                    }

                    // Then periodic announcements
                    while (!_announceCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(_config.AnnounceIntervalMs, _announceCts.Token);
                        await SendAnnouncementAsync(multicastEndpoint, _announceCts.Token);
                    }
                }, _announceCts.Token);
            }
            catch (SocketException ex)
            {
                // Multicast not available - log but don't throw
                Console.WriteLine($"[MdnsServiceDiscovery] Failed to start announcing (multicast unavailable): {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        /// <summary>
        /// Starts listening for other DataWarehouse instances via mDNS multicast.
        /// </summary>
        /// <param name="ct">Cancellation token to stop listening.</param>
        public async Task StartListeningAsync(CancellationToken ct = default)
        {
            _listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                _listenClient = CreateMulticastClient();

                _listenTask = Task.Run(async () =>
                {
                    while (!_listenCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var result = await _listenClient.ReceiveAsync(_listenCts.Token);
                            ProcessIncomingPacket(result.Buffer);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MdnsServiceDiscovery] Error receiving packet: {ex.Message}");
                        }
                    }
                }, _listenCts.Token);
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[MdnsServiceDiscovery] Failed to start listening (multicast unavailable): {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        /// <summary>
        /// Stops announcing and listening.
        /// </summary>
        public async Task StopAsync()
        {
            _announceCts?.Cancel();
            _listenCts?.Cancel();

            if (_announceTask != null)
                await _announceTask.ConfigureAwait(false);
            if (_listenTask != null)
                await _listenTask.ConfigureAwait(false);

            _announceClient?.Dispose();
            _listenClient?.Dispose();
        }

        /// <summary>
        /// Gets all currently discovered services.
        /// </summary>
        public IReadOnlyList<DiscoveredService> GetDiscoveredServices()
        {
            lock (_servicesLock)
            {
                return _discoveredServices.ToList().AsReadOnly();
            }
        }

        private UdpClient CreateMulticastClient()
        {
            var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, _config.Port));
            client.JoinMulticastGroup(IPAddress.Parse(_config.MulticastAddress));
            return client;
        }

        private async Task SendAnnouncementAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            if (_announceClient == null) return;

            var packet = BuildMdnsPacket();
            await _announceClient.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);
        }

        private byte[] BuildMdnsPacket()
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            // DNS Header (12 bytes)
            writer.Write((ushort)0);              // Transaction ID (0 for mDNS)
            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)0x8400)); // Flags: Response, Authoritative
            writer.Write((ushort)0);              // QDCOUNT
            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)1)); // ANCOUNT (1 answer)
            writer.Write((ushort)0);              // NSCOUNT
            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)1)); // ARCOUNT (1 additional)

            // Answer Section: SRV record
            WriteDnsName(writer, ServiceType);
            writer.Write(BinaryPrimitives.ReverseEndianness(DnsTypeSrv));
            writer.Write(BinaryPrimitives.ReverseEndianness(DnsClassInCacheFlush));
            writer.Write(BinaryPrimitives.ReverseEndianness((uint)_config.Ttl)); // TTL

            // SRV RDATA
            var srvData = BuildSrvRdata();
            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)srvData.Length));
            writer.Write(srvData);

            // Additional Section: TXT record
            WriteDnsName(writer, ServiceType);
            writer.Write(BinaryPrimitives.ReverseEndianness(DnsTypeTxt));
            writer.Write(BinaryPrimitives.ReverseEndianness(DnsClassInCacheFlush));
            writer.Write(BinaryPrimitives.ReverseEndianness((uint)_config.Ttl)); // TTL

            // TXT RDATA
            var txtData = BuildTxtRdata();
            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)txtData.Length));
            writer.Write(txtData);

            return ms.ToArray();
        }

        private byte[] BuildSrvRdata()
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)0)); // Priority
            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)0)); // Weight
            writer.Write(BinaryPrimitives.ReverseEndianness((ushort)_port)); // Port

            // Target (hostname)
            WriteDnsName(writer, $"{_nodeId}.local");

            return ms.ToArray();
        }

        private byte[] BuildTxtRdata()
        {
            var txtRecords = new[]
            {
                $"nodeId={_nodeId}",
                $"address={_address}",
                $"port={_port}",
                $"version=3.0.0"
            };

            using var ms = new System.IO.MemoryStream();
            foreach (var record in txtRecords)
            {
                var bytes = Encoding.UTF8.GetBytes(record);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes);
            }
            return ms.ToArray();
        }

        private void WriteDnsName(System.IO.BinaryWriter writer, string name)
        {
            var labels = name.Split('.');
            foreach (var label in labels)
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                writer.Write((byte)bytes.Length);
                writer.Write(bytes);
            }
            writer.Write((byte)0); // Null terminator
        }

        private void ProcessIncomingPacket(byte[] buffer)
        {
            try
            {
                using var ms = new System.IO.MemoryStream(buffer);
                using var reader = new System.IO.BinaryReader(ms);

                // Parse DNS header
                var transactionId = reader.ReadUInt16();
                var flags = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                var qdCount = reader.ReadUInt16();
                var anCount = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                var nsCount = reader.ReadUInt16();
                var arCount = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());

                // Skip question section
                for (int i = 0; i < qdCount; i++)
                {
                    SkipDnsName(reader);
                    reader.ReadUInt32(); // Type + Class
                }

                // Parse answer section
                string? discoveredNodeId = null;
                string? discoveredAddress = null;
                int? discoveredPort = null;

                for (int i = 0; i < anCount + arCount; i++)
                {
                    var name = ReadDnsName(reader);
                    var type = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                    var dnsClass = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                    var ttl = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                    var rdLength = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                    var rdataStart = reader.BaseStream.Position;

                    if (name.Contains(ServiceType))
                    {
                        if (type == DnsTypeSrv)
                        {
                            reader.ReadUInt16(); // Priority
                            reader.ReadUInt16(); // Weight
                            discoveredPort = BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
                            var target = ReadDnsName(reader);
                        }
                        else if (type == DnsTypeTxt)
                        {
                            var txtData = reader.ReadBytes(rdLength);
                            var parsed = ParseTxtRecord(txtData);
                            discoveredNodeId = parsed.GetValueOrDefault("nodeId");
                            discoveredAddress = parsed.GetValueOrDefault("address");
                        }
                    }

                    // Ensure we're at the end of this record
                    reader.BaseStream.Position = rdataStart + rdLength;
                }

                // If we found a complete service announcement, add it
                if (!string.IsNullOrEmpty(discoveredNodeId) &&
                    !string.IsNullOrEmpty(discoveredAddress) &&
                    discoveredPort.HasValue &&
                    discoveredNodeId != _nodeId) // Don't discover ourselves
                {
                    AddDiscoveredService(new DiscoveredService
                    {
                        NodeId = discoveredNodeId,
                        Address = discoveredAddress,
                        Port = discoveredPort.Value,
                        Version = "3.0.0",
                        DiscoveredAt = DateTimeOffset.UtcNow
                    });
                }
            }
            catch
            {
                // Ignore malformed packets
            }
        }

        private string ReadDnsName(System.IO.BinaryReader reader)
        {
            var labels = new List<string>();
            while (true)
            {
                var length = reader.ReadByte();
                if (length == 0) break;

                // Handle compression pointers (not fully implemented - just skip)
                if ((length & 0xC0) == 0xC0)
                {
                    reader.ReadByte();
                    break;
                }

                var labelBytes = reader.ReadBytes(length);
                labels.Add(Encoding.ASCII.GetString(labelBytes));
            }
            return string.Join(".", labels);
        }

        private void SkipDnsName(System.IO.BinaryReader reader)
        {
            while (true)
            {
                var length = reader.ReadByte();
                if (length == 0) break;
                if ((length & 0xC0) == 0xC0)
                {
                    reader.ReadByte();
                    break;
                }
                reader.ReadBytes(length);
            }
        }

        private Dictionary<string, string> ParseTxtRecord(byte[] txtData)
        {
            var result = new Dictionary<string, string>();
            int offset = 0;
            while (offset < txtData.Length)
            {
                var length = txtData[offset++];
                if (offset + length > txtData.Length) break;

                var record = Encoding.UTF8.GetString(txtData, offset, length);
                offset += length;

                var parts = record.Split('=', 2);
                if (parts.Length == 2)
                    result[parts[0]] = parts[1];
            }
            return result;
        }

        private void AddDiscoveredService(DiscoveredService service)
        {
            // DIST-06: Enforce network prefix restrictions
            if (_config.AllowedNetworkPrefixes.Count > 0)
            {
                bool prefixAllowed = false;
                foreach (var prefix in _config.AllowedNetworkPrefixes)
                {
                    if (service.Address.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        prefixAllowed = true;
                        break;
                    }
                }
                if (!prefixAllowed)
                {
                    Console.WriteLine($"[MdnsServiceDiscovery] DIST-06: Rejected node {service.NodeId} at {service.Address} -- address not in allowed prefixes");
                    return;
                }
            }

            bool isNew = false;
            lock (_servicesLock)
            {
                if (!_discoveredServices.Any(s => s.NodeId == service.NodeId))
                {
                    // Enforce max entries (MEM-03)
                    while (_discoveredServices.Count >= _config.MaxDiscoveredServices)
                    {
                        var oldest = _discoveredServices.OrderBy(s => s.DiscoveredAt).First();
                        _discoveredServices.Remove(oldest);
                        Console.WriteLine($"[MdnsServiceDiscovery] Audit: Service lost (evicted) -- nodeId={oldest.NodeId}");
                        OnServiceLost?.Invoke(oldest.NodeId);
                    }

                    // DIST-06: Mark as unverified when RequireClusterVerification is enabled
                    var serviceToAdd = _config.RequireClusterVerification
                        ? service with { Verified = false }
                        : service with { Verified = true };

                    if (!_config.RequireClusterVerification)
                    {
                        Console.WriteLine($"[MdnsServiceDiscovery] WARNING: RequireClusterVerification is disabled -- node {service.NodeId} auto-trusted");
                    }

                    _discoveredServices.Add(serviceToAdd);
                    isNew = true;

                    // DIST-06: Audit trail for join events
                    Console.WriteLine($"[MdnsServiceDiscovery] Audit: Service discovered -- nodeId={service.NodeId}, address={service.Address}:{service.Port}, verified={serviceToAdd.Verified}");
                }
            }

            if (isNew)
            {
                OnServiceDiscovered?.Invoke(service);
            }
        }

        /// <summary>
        /// Marks a discovered service as verified after cluster credential validation (DIST-06).
        /// </summary>
        /// <param name="nodeId">The node ID to mark as verified.</param>
        /// <returns>True if the service was found and marked as verified.</returns>
        public bool MarkServiceVerified(string nodeId)
        {
            lock (_servicesLock)
            {
                var index = _discoveredServices.FindIndex(s => s.NodeId == nodeId);
                if (index >= 0)
                {
                    _discoveredServices[index] = _discoveredServices[index] with { Verified = true };
                    Console.WriteLine($"[MdnsServiceDiscovery] Audit: Service verified -- nodeId={nodeId}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Asynchronously disposes mDNS discovery resources.
        /// Preferred over <see cref="Dispose"/> to avoid sync-over-async.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        /// <summary>
        /// Synchronously disposes mDNS discovery resources. Prefer <see cref="DisposeAsync"/>.
        /// </summary>
        public void Dispose()
        {
            // Avoid deadlock under SynchronizationContext by offloading to thread pool
            Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Configuration for mDNS service discovery.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 40: Zero-config mDNS discovery")]
    public record MdnsConfiguration
    {
        /// <summary>
        /// Interval between announcement broadcasts (default 10 seconds).
        /// </summary>
        public int AnnounceIntervalMs { get; init; } = 10_000;

        /// <summary>
        /// Multicast address for mDNS (default 224.0.0.251).
        /// </summary>
        public string MulticastAddress { get; init; } = "224.0.0.251";

        /// <summary>
        /// Port for mDNS multicast (default 5353).
        /// </summary>
        public int Port { get; init; } = 5353;

        /// <summary>
        /// Time-to-live for mDNS records in seconds (default 120).
        /// </summary>
        public int Ttl { get; init; } = 120;

        /// <summary>
        /// Maximum number of discovered services to track (default 100, MEM-03 compliance).
        /// </summary>
        public int MaxDiscoveredServices { get; init; } = 100;

        /// <summary>
        /// When true, discovered nodes are added to a pending list and must be verified
        /// (e.g., via TLS handshake or cluster secret) before being auto-joined.
        /// When false (legacy mode), discovered nodes are immediately eligible for joining
        /// with a warning logged. (DIST-06 mitigation, default: true)
        /// </summary>
        public bool RequireClusterVerification { get; init; } = true;

        /// <summary>
        /// Allowed network prefixes for mDNS discovery (e.g., "192.168.1.", "10.0.0.").
        /// When set, only nodes announcing from addresses matching these prefixes are accepted.
        /// Empty list means all network addresses are accepted. (DIST-06 mitigation)
        /// </summary>
        public List<string> AllowedNetworkPrefixes { get; init; } = new();
    }

    /// <summary>
    /// Represents a discovered DataWarehouse service on the network.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 40: Zero-config mDNS discovery")]
    public record DiscoveredService
    {
        /// <summary>
        /// Unique identifier of the discovered node.
        /// </summary>
        public required string NodeId { get; init; }

        /// <summary>
        /// Network address of the discovered node.
        /// </summary>
        public required string Address { get; init; }

        /// <summary>
        /// Port the discovered node listens on.
        /// </summary>
        public required int Port { get; init; }

        /// <summary>
        /// Version of the discovered node.
        /// </summary>
        public required string Version { get; init; }

        /// <summary>
        /// When this service was discovered.
        /// </summary>
        public required DateTimeOffset DiscoveredAt { get; init; }

        /// <summary>
        /// Whether this discovered service has been verified (cluster credentials validated).
        /// Services with Verified=false should not be auto-joined when RequireClusterVerification is true (DIST-06).
        /// </summary>
        public bool Verified { get; init; }
    }
}
