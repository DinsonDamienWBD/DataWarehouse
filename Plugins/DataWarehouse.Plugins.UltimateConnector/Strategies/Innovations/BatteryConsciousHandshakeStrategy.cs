using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Negotiates data transfer protocols and compression modes based on the hardware
    /// energy state of the host machine. On battery power with low charge, the strategy
    /// selects lightweight transfer modes with aggressive compression to reduce CPU cycles
    /// and radio time. On AC power, it selects high-throughput modes with minimal compression.
    /// </summary>
    /// <remarks>
    /// This strategy is designed for edge computing, mobile data collection, and IoT gateway
    /// scenarios where battery life directly impacts data collection windows. The handshake
    /// includes the current power state, allowing the remote endpoint to cooperate by
    /// adjusting server-side buffering, batch sizes, and acknowledgment frequency.
    /// </remarks>
    public class BatteryConsciousHandshakeStrategy : ConnectionStrategyBase
    {
        /// <summary>
        /// Transfer mode thresholds based on battery percentage.
        /// </summary>
        private static readonly (int Threshold, string Mode, string Compression)[] PowerProfiles =
        [
            (10, "ultra-low-power", "lz4-minimal"),
            (25, "low-power", "lz4"),
            (50, "balanced", "zstd-fast"),
            (75, "performance", "zstd"),
            (100, "full-throughput", "none")
        ];

        /// <inheritdoc/>
        public override string StrategyId => "innovation-battery-handshake";

        /// <inheritdoc/>
        public override string DisplayName => "Battery-Conscious Handshake";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            SupportsReconnection: true,
            SupportedAuthMethods: ["bearer", "apikey", "none"],
            MaxConcurrentConnections: 25
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Negotiates data transfer protocols based on hardware energy state, selecting " +
            "lightweight modes on battery and high-throughput modes on AC power";

        /// <inheritdoc/>
        public override string[] Tags => ["battery", "power-aware", "energy", "edge", "iot", "mobile", "adaptive"];

        /// <summary>
        /// Initializes a new instance of <see cref="BatteryConsciousHandshakeStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public BatteryConsciousHandshakeStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var targetEndpoint = config.ConnectionString
                ?? throw new ArgumentException("ConnectionString must specify the target endpoint.");

            var forceMode = GetConfiguration<string>(config, "force_transfer_mode", "");
            var adaptiveInterval = GetConfiguration<int>(config, "adaptive_interval_seconds", 60);
            var reportPowerState = GetConfiguration<bool>(config, "report_power_state", true);

            var powerState = QueryPowerState();
            var (transferMode, compression) = SelectTransferProfile(powerState, forceMode);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(
                    powerState.IsOnBattery ? 1 : 5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(
                    powerState.IsOnBattery ? 120 : 60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(
                    powerState.IsOnBattery ? 60 : 30),
                MaxConnectionsPerServer = powerState.IsOnBattery
                    ? Math.Max(1, config.PoolSize / 4)
                    : config.PoolSize
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(targetEndpoint),
                Timeout = powerState.IsOnBattery
                    ? TimeSpan.FromSeconds(Math.Min(config.Timeout.TotalSeconds, 10))
                    : config.Timeout
            };

            var handshakePayload = new Dictionary<string, object>
            {
                ["transfer_mode"] = transferMode,
                ["compression"] = compression,
                ["battery_percent"] = powerState.ChargePercent,
                ["is_on_battery"] = powerState.IsOnBattery,
                ["is_charging"] = powerState.IsCharging,
                ["adaptive_interval_seconds"] = adaptiveInterval,
                ["client_platform"] = RuntimeInformation.OSDescription,
                ["client_architecture"] = RuntimeInformation.OSArchitecture.ToString()
            };

            var content = new StringContent(
                JsonSerializer.Serialize(handshakePayload),
                Encoding.UTF8,
                "application/json");

            client.DefaultRequestHeaders.Add("X-Transfer-Mode", transferMode);
            client.DefaultRequestHeaders.Add("X-Compression", compression);
            client.DefaultRequestHeaders.Add("X-Battery-Percent", powerState.ChargePercent.ToString());

            var response = await client.PostAsync("/api/v1/handshake/negotiate", content, ct);
            response.EnsureSuccessStatusCode();

            var info = new Dictionary<string, object>
            {
                ["target_endpoint"] = targetEndpoint,
                ["transfer_mode"] = transferMode,
                ["compression"] = compression,
                ["battery_percent"] = powerState.ChargePercent,
                ["is_on_battery"] = powerState.IsOnBattery,
                ["is_charging"] = powerState.IsCharging,
                ["adaptive_interval_seconds"] = adaptiveInterval,
                ["connected_at"] = DateTimeOffset.UtcNow,
                ["platform"] = RuntimeInformation.OSDescription
            };

            return new DefaultConnectionHandle(client, info, $"battery-{transferMode}-{powerState.ChargePercent}pct");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var response = await client.GetAsync("/api/v1/handshake/ping", ct);
            return response.IsSuccessStatusCode;
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            var currentPower = QueryPowerState();
            var originalMode = handle.ConnectionInfo.GetValueOrDefault("transfer_mode", "unknown")!;

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy
                    ? $"Battery-conscious connection healthy, mode: {originalMode}, battery: {currentPower.ChargePercent}%"
                    : "Battery-conscious endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["current_battery_percent"] = currentPower.ChargePercent,
                    ["current_is_on_battery"] = currentPower.IsOnBattery,
                    ["current_is_charging"] = currentPower.IsCharging,
                    ["negotiated_transfer_mode"] = originalMode,
                    ["negotiated_compression"] = handle.ConnectionInfo.GetValueOrDefault("compression", "unknown")!
                });
        }

        /// <summary>
        /// Queries the current power/battery state using platform-specific APIs.
        /// </summary>
        private static PowerStateInfo QueryPowerState()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return QueryWindowsPowerState();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return QueryLinuxPowerState();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return QueryMacOsPowerState();

            return new PowerStateInfo(ChargePercent: 100, IsOnBattery: false, IsCharging: false);
        }

        /// <summary>
        /// Queries Windows battery state via GetSystemPowerStatus.
        /// </summary>
        private static PowerStateInfo QueryWindowsPowerState()
        {
            try
            {
                var status = new SystemPowerStatus();
                if (GetSystemPowerStatus(ref status))
                {
                    var percent = status.BatteryLifePercent == 255 ? 100 : (int)status.BatteryLifePercent;
                    var isOnBattery = status.ACLineStatus == 0;
                    var isCharging = (status.BatteryFlag & 8) != 0;
                    return new PowerStateInfo(percent, isOnBattery, isCharging);
                }
            }
            catch { /* P/Invoke may not be available in all environments */ }

            return new PowerStateInfo(100, false, false);
        }

        /// <summary>
        /// Reads Linux battery state from /sys/class/power_supply.
        /// </summary>
        private static PowerStateInfo QueryLinuxPowerState()
        {
            try
            {
                const string basePath = "/sys/class/power_supply/BAT0";
                if (!System.IO.Directory.Exists(basePath))
                    return new PowerStateInfo(100, false, false);

                var capacityStr = System.IO.File.ReadAllText($"{basePath}/capacity").Trim();
                var statusStr = System.IO.File.ReadAllText($"{basePath}/status").Trim();

                var percent = int.TryParse(capacityStr, out var cap) ? cap : 100;
                var isCharging = statusStr.Equals("Charging", StringComparison.OrdinalIgnoreCase);
                var isOnBattery = statusStr.Equals("Discharging", StringComparison.OrdinalIgnoreCase);

                return new PowerStateInfo(percent, isOnBattery, isCharging);
            }
            catch
            {
                return new PowerStateInfo(100, false, false);
            }
        }

        /// <summary>
        /// Queries macOS battery state via /usr/bin/pmset.
        /// </summary>
        private static PowerStateInfo QueryMacOsPowerState()
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/pmset", "-g batt")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return new PowerStateInfo(100, false, false);

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                var isOnBattery = output.Contains("Battery Power", StringComparison.OrdinalIgnoreCase);
                var isCharging = output.Contains("charging", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("discharging", StringComparison.OrdinalIgnoreCase);

                var percent = 100;
                var percentIdx = output.IndexOf('%');
                if (percentIdx > 0)
                {
                    var start = percentIdx - 1;
                    while (start > 0 && char.IsDigit(output[start - 1])) start--;
                    if (int.TryParse(output[start..percentIdx], out var parsed))
                        percent = parsed;
                }

                return new PowerStateInfo(percent, isOnBattery, isCharging);
            }
            catch
            {
                return new PowerStateInfo(100, false, false);
            }
        }

        /// <summary>
        /// Selects the appropriate transfer profile based on power state and optional override.
        /// </summary>
        private static (string TransferMode, string Compression) SelectTransferProfile(
            PowerStateInfo powerState, string forceMode)
        {
            if (!string.IsNullOrEmpty(forceMode))
            {
                foreach (var profile in PowerProfiles)
                {
                    if (profile.Mode.Equals(forceMode, StringComparison.OrdinalIgnoreCase))
                        return (profile.Mode, profile.Compression);
                }
            }

            if (!powerState.IsOnBattery)
                return ("full-throughput", "none");

            foreach (var (threshold, mode, compression) in PowerProfiles)
            {
                if (powerState.ChargePercent <= threshold)
                    return (mode, compression);
            }

            return ("full-throughput", "none");
        }

        /// <summary>
        /// Power state information for the host machine.
        /// </summary>
        private sealed record PowerStateInfo(int ChargePercent, bool IsOnBattery, bool IsCharging);

        #region Windows P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(ref SystemPowerStatus lpSystemPowerStatus);

        #endregion
    }
}
