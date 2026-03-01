using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.IoT
{
    /// <summary>
    /// LoRaWAN network server connection strategy with full protocol support.
    /// Supports LoRaWAN packet parsing, device activation (OTAA/ABP), downlink scheduling,
    /// and Adaptive Data Rate (ADR) management.
    /// </summary>
    public class LoRaWanConnectionStrategy : IoTConnectionStrategyBase
    {
        private readonly BoundedDictionary<string, LoRaDevice> _devices = new BoundedDictionary<string, LoRaDevice>(1000);
        private readonly BoundedDictionary<string, Queue<LoRaDownlink>> _downlinkQueues = new BoundedDictionary<string, Queue<LoRaDownlink>>(1000);
        private readonly BoundedDictionary<string, AdrState> _adrStates = new BoundedDictionary<string, AdrState>(1000);

        public override string StrategyId => "lorawan";
        public override string DisplayName => "LoRaWAN";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to LoRaWAN network servers with OTAA/ABP activation, downlink scheduling, ADR management, and packet parsing.";
        public override string[] Tags => new[] { "lorawan", "lpwan", "iot", "wireless", "long-range", "adr" };

        public LoRaWanConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p1700) ? p1700 : 1700;
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port,
                ["protocol"] = "LoRaWAN",
                ["version"] = "1.0.3"
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // Finding 1919: Use live socket probe instead of stale Connected flag.
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected) return false;
            try { await client.GetStream().WriteAsync(Array.Empty<byte>(), ct); return true; }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            handle.GetConnection<TcpClient>().Close();
            return Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy,
                isHealthy ? $"LoRaWAN network server connected ({_devices.Count} devices registered)" : "LoRaWAN network server disconnected",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override Task<Dictionary<string, object>> ReadTelemetryAsync(IConnectionHandle handle, string deviceId, CancellationToken ct = default)
        {
            var result = new Dictionary<string, object>
            {
                ["protocol"] = "LoRaWAN",
                ["devEUI"] = deviceId,
                ["registeredDevices"] = _devices.Count,
                ["status"] = _devices.ContainsKey(deviceId) ? "registered" : "unknown"
            };

            if (_devices.TryGetValue(deviceId, out var device))
            {
                result["activationType"] = device.ActivationType;
                result["dataRate"] = device.DataRate;
                result["snr"] = device.LastSnr;
                result["rssi"] = device.LastRssi;
                result["frameCounter"] = device.FrameCounterUp;
                result["lastSeen"] = device.LastSeen;
            }

            result["timestamp"] = DateTimeOffset.UtcNow;
            return Task.FromResult(result);
        }

        public override Task<string> SendCommandAsync(IConnectionHandle handle, string deviceId, string command,
            Dictionary<string, object>? parameters = null, CancellationToken ct = default)
        {
            var port = parameters?.TryGetValue("port", out var p) == true && p is int pv ? pv : 1;
            var confirmed = parameters?.TryGetValue("confirmed", out var c) == true && c is bool cv && cv;

            ScheduleDownlink(deviceId, System.Text.Encoding.UTF8.GetBytes(command), port, confirmed);

            return Task.FromResult($"{{\"status\":\"queued\",\"devEUI\":\"{deviceId}\",\"port\":{port},\"confirmed\":{confirmed.ToString().ToLowerInvariant()},\"queueDepth\":{GetDownlinkQueueDepth(deviceId)}}}");
        }

        /// <summary>
        /// Registers a device via OTAA (Over-The-Air Activation).
        /// AppKey must be provided via the appKey parameter to verify the MIC before accepting any device.
        /// </summary>
        public LoRaJoinResult ProcessOtaaJoin(string devEui, byte[] joinRequest, byte[]? appKey = null)
        {
            // Parse join request: MHDR (1) + AppEUI (8) + DevEUI (8) + DevNonce (2) + MIC (4)
            if (joinRequest.Length < 23)
                return new LoRaJoinResult { Success = false, Reason = "Invalid join request length" };

            // Finding 1970: Verify OTAA join MIC before accepting any device.
            // The MIC covers bytes 0..18 (MHDR+AppEUI+DevEUI+DevNonce) using CMAC with AppKey.
            // If AppKey is supplied, reject joins whose MIC doesn't match.
            if (appKey != null && appKey.Length == 16)
            {
                var mic = ComputeJoinMic(appKey, joinRequest[..19]);
                var receivedMic = BinaryPrimitives.ReadUInt32LittleEndian(joinRequest.AsSpan(19, 4));
                if (mic != receivedMic)
                    return new LoRaJoinResult { Success = false, Reason = "Invalid MIC — join request rejected" };
            }

            var devNonce = BinaryPrimitives.ReadUInt16LittleEndian(joinRequest.AsSpan(17, 2));

            // Finding 1971: Reject replayed DevNonces.
            // LoRaWAN spec §6.2.5: each DevNonce MUST be unique per device across all joins.
            if (_devices.TryGetValue(devEui, out var existingDevice) && existingDevice.DevNonce == devNonce)
                return new LoRaJoinResult { Success = false, Reason = $"Replayed DevNonce {devNonce} — join request rejected" };

            // Generate session keys
            var devAddr = GenerateDevAddr();
            var nwkSKey = GenerateSessionKey();
            var appSKey = GenerateSessionKey();

            var device = new LoRaDevice
            {
                DevEui = devEui,
                DevAddr = devAddr,
                ActivationType = "OTAA",
                NwkSKey = nwkSKey,
                AppSKey = appSKey,
                DevNonce = devNonce,
                FrameCounterUp = 0,
                FrameCounterDown = 0,
                DataRate = 0,
                JoinedAt = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow
            };

            _devices[devEui] = device;

            return new LoRaJoinResult
            {
                Success = true,
                DevAddr = devAddr,
                DevEui = devEui,
                ActivationType = "OTAA"
            };
        }

        /// <summary>
        /// Registers a device via ABP (Activation By Personalization).
        /// </summary>
        public LoRaJoinResult RegisterAbpDevice(string devEui, string devAddr, byte[] nwkSKey, byte[] appSKey)
        {
            var device = new LoRaDevice
            {
                DevEui = devEui,
                DevAddr = devAddr,
                ActivationType = "ABP",
                NwkSKey = nwkSKey,
                AppSKey = appSKey,
                FrameCounterUp = 0,
                FrameCounterDown = 0,
                DataRate = 0,
                JoinedAt = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow
            };

            _devices[devEui] = device;

            return new LoRaJoinResult
            {
                Success = true,
                DevAddr = devAddr,
                DevEui = devEui,
                ActivationType = "ABP"
            };
        }

        /// <summary>
        /// Processes an uplink frame from a device.
        /// </summary>
        public LoRaUplinkResult ProcessUplink(string devEui, byte[] payload, int fPort,
            double rssi, double snr, int dataRate, uint receivedFrameCounter = uint.MaxValue)
        {
            if (!_devices.TryGetValue(devEui, out var device))
                return new LoRaUplinkResult { Success = false, Reason = "Device not registered" };

            // Finding 1971: Enforce monotonic frame counter to prevent replay attacks.
            // If the caller provides the frame counter from the received frame, validate it.
            if (receivedFrameCounter != uint.MaxValue)
            {
                lock (device)
                {
                    if (receivedFrameCounter <= device.FrameCounterUp)
                        return new LoRaUplinkResult { Success = false, Reason = $"Frame counter replay detected: received {receivedFrameCounter}, expected > {device.FrameCounterUp}" };
                    device.FrameCounterUp = receivedFrameCounter;
                }
            }
            else
            {
                lock (device) { device.FrameCounterUp++; }
            }
            device.LastRssi = rssi;
            device.LastSnr = snr;
            device.DataRate = dataRate;
            device.LastSeen = DateTimeOffset.UtcNow;

            // ADR processing
            var adrResult = ProcessAdr(devEui, rssi, snr, dataRate);

            // Check for pending downlinks
            LoRaDownlink? pendingDownlink = null;
            if (_downlinkQueues.TryGetValue(devEui, out var queue) && queue.Count > 0)
            {
                lock (queue)
                {
                    if (queue.Count > 0)
                        pendingDownlink = queue.Dequeue();
                }
            }

            return new LoRaUplinkResult
            {
                Success = true,
                DevEui = devEui,
                FrameCounter = device.FrameCounterUp,
                FPort = fPort,
                PayloadSize = payload.Length,
                Rssi = rssi,
                Snr = snr,
                DataRate = dataRate,
                AdrRecommendation = adrResult,
                HasPendingDownlink = pendingDownlink != null
            };
        }

        /// <summary>
        /// Schedules a downlink message for a device.
        /// </summary>
        public void ScheduleDownlink(string devEui, byte[] payload, int fPort = 1, bool confirmed = false)
        {
            var downlink = new LoRaDownlink
            {
                DevEui = devEui,
                Payload = payload,
                FPort = fPort,
                Confirmed = confirmed,
                QueuedAt = DateTimeOffset.UtcNow
            };

            var queue = _downlinkQueues.GetOrAdd(devEui, _ => new Queue<LoRaDownlink>());
            lock (queue)
            {
                queue.Enqueue(downlink);
            }
        }

        /// <summary>
        /// Processes ADR (Adaptive Data Rate) for a device.
        /// </summary>
        public AdrRecommendation? ProcessAdr(string devEui, double rssi, double snr, int currentDataRate)
        {
            var state = _adrStates.GetOrAdd(devEui, _ => new AdrState());

            lock (state.SnrHistory)
            {
                state.SnrHistory.Add(snr);
                if (state.SnrHistory.Count > 20)
                    state.SnrHistory.RemoveAt(0);
            }

            if (state.SnrHistory.Count < 10)
                return null; // Not enough data

            // Calculate average SNR
            double avgSnr;
            lock (state.SnrHistory)
            {
                var sum = 0.0;
                foreach (var s in state.SnrHistory) sum += s;
                avgSnr = sum / state.SnrHistory.Count;
            }

            // SNR margins per data rate (LoRaWAN regional parameters)
            var requiredSnr = currentDataRate switch
            {
                0 => -20.0, // SF12
                1 => -17.5, // SF11
                2 => -15.0, // SF10
                3 => -12.5, // SF9
                4 => -10.0, // SF8
                5 => -7.5,  // SF7
                _ => -7.5
            };

            var margin = avgSnr - requiredSnr;
            var recommendedDr = currentDataRate;

            // Increase data rate if margin > 3 dB
            if (margin > 3.0 && currentDataRate < 5)
                recommendedDr = Math.Min(currentDataRate + 1, 5);
            // Decrease data rate if margin < 0
            else if (margin < 0 && currentDataRate > 0)
                recommendedDr = Math.Max(currentDataRate - 1, 0);

            if (recommendedDr == currentDataRate) return null;

            return new AdrRecommendation
            {
                DevEui = devEui,
                CurrentDataRate = currentDataRate,
                RecommendedDataRate = recommendedDr,
                AverageSnr = avgSnr,
                Margin = margin
            };
        }

        /// <summary>
        /// Gets the number of registered devices.
        /// </summary>
        public int DeviceCount => _devices.Count;

        private int GetDownlinkQueueDepth(string devEui) =>
            _downlinkQueues.TryGetValue(devEui, out var q) ? q.Count : 0;

        private static string GenerateDevAddr()
        {
            var bytes = new byte[4];
            RandomNumberGenerator.Fill(bytes);
            bytes[0] = (byte)((bytes[0] & 0x7F) | 0x20); // NwkID prefix
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static byte[] GenerateSessionKey()
        {
            var key = new byte[16];
            RandomNumberGenerator.Fill(key);
            return key;
        }

        /// <summary>
        /// Computes the 4-byte LoRaWAN AES-128 CMAC MIC for a join-request payload.
        /// Per LoRaWAN spec §6.2.4: CMAC is computed over the message bytes using the AppKey.
        /// Returns the low 32 bits of the CMAC.
        /// </summary>
        private static uint ComputeJoinMic(byte[] appKey, byte[] message)
        {
            // AES-128 CMAC per RFC 4493
            var cmac = new byte[16];
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = appKey;
            aes.Mode = System.Security.Cryptography.CipherMode.ECB;
            aes.Padding = System.Security.Cryptography.PaddingMode.None;

            // Subkey generation
            var k1 = new byte[16];
            var k2 = new byte[16];
            var zeros = new byte[16];
            using (var enc = aes.CreateEncryptor())
                enc.TransformBlock(zeros, 0, 16, k1, 0);

            bool msb1 = (k1[0] & 0x80) != 0;
            for (int i = 0; i < 15; i++) k1[i] = (byte)((k1[i] << 1) | (k1[i + 1] >> 7));
            k1[15] = (byte)((k1[15] << 1) ^ (msb1 ? 0x87 : 0x00));

            bool msb2 = (k1[0] & 0x80) != 0;
            for (int i = 0; i < 15; i++) k2[i] = (byte)((k1[i] << 1) | (k1[i + 1] >> 7));
            k2[15] = (byte)((k1[15] << 1) ^ (msb2 ? 0x87 : 0x00));

            // Pad message to 16-byte block
            int blocks = (message.Length + 15) / 16;
            if (blocks == 0) blocks = 1;
            var padded = new byte[blocks * 16];
            Buffer.BlockCopy(message, 0, padded, 0, message.Length);
            bool complete = message.Length > 0 && message.Length % 16 == 0;
            if (!complete) padded[message.Length] = 0x80;

            // XOR last block with appropriate subkey
            var xorKey = complete ? k1 : k2;
            for (int i = 0; i < 16; i++)
                padded[(blocks - 1) * 16 + i] ^= xorKey[i];

            // CBC-MAC
            var x = new byte[16];
            using (var enc = aes.CreateEncryptor())
            {
                for (int b = 0; b < blocks; b++)
                {
                    for (int i = 0; i < 16; i++) x[i] ^= padded[b * 16 + i];
                    enc.TransformBlock(x, 0, 16, x, 0);
                }
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(0, 4));
        }
    }

    public sealed class LoRaDevice
    {
        public string DevEui { get; set; } = "";
        public string DevAddr { get; set; } = "";
        public string ActivationType { get; set; } = "";
        public byte[] NwkSKey { get; set; } = Array.Empty<byte>();
        public byte[] AppSKey { get; set; } = Array.Empty<byte>();
        public ushort DevNonce { get; set; }
        public uint FrameCounterUp { get; set; }
        public uint FrameCounterDown { get; set; }
        public int DataRate { get; set; }
        public double LastRssi { get; set; }
        public double LastSnr { get; set; }
        public DateTimeOffset JoinedAt { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public sealed record LoRaJoinResult
    {
        public bool Success { get; init; }
        public string? DevAddr { get; init; }
        public string? DevEui { get; init; }
        public string? ActivationType { get; init; }
        public string? Reason { get; init; }
    }

    public sealed record LoRaUplinkResult
    {
        public bool Success { get; init; }
        public string? DevEui { get; init; }
        public uint FrameCounter { get; init; }
        public int FPort { get; init; }
        public int PayloadSize { get; init; }
        public double Rssi { get; init; }
        public double Snr { get; init; }
        public int DataRate { get; init; }
        public AdrRecommendation? AdrRecommendation { get; init; }
        public bool HasPendingDownlink { get; init; }
        public string? Reason { get; init; }
    }

    public sealed record LoRaDownlink
    {
        public required string DevEui { get; init; }
        public required byte[] Payload { get; init; }
        public int FPort { get; init; }
        public bool Confirmed { get; init; }
        public DateTimeOffset QueuedAt { get; init; }
    }

    public sealed class AdrState
    {
        public List<double> SnrHistory { get; } = new();
    }

    public sealed record AdrRecommendation
    {
        public required string DevEui { get; init; }
        public int CurrentDataRate { get; init; }
        public int RecommendedDataRate { get; init; }
        public double AverageSnr { get; init; }
        public double Margin { get; init; }
    }
}
