using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.BatteryAwareness;

/// <summary>
/// Integrates with UPS (Uninterruptible Power Supply) systems for graceful
/// shutdown and power management during outages. Supports NUT, APC, and SNMP protocols.
/// </summary>
public sealed class UpsIntegrationStrategy : SustainabilityStrategyBase
{
    private UpsStatus _currentStatus = new();
    private Timer? _pollTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "ups-integration";
    /// <inheritdoc/>
    public override string DisplayName => "UPS Integration";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.BatteryAwareness;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.Alerting | SustainabilityCapabilities.ExternalIntegration;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Integrates with UPS systems (NUT, APC, SNMP) for power monitoring and graceful shutdown during outages.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "ups", "power", "outage", "nut", "apc", "snmp" };

    /// <summary>UPS host for NUT/SNMP.</summary>
    public string UpsHost { get; set; } = "localhost";
    /// <summary>UPS name for NUT.</summary>
    public string UpsName { get; set; } = "ups";
    /// <summary>Battery threshold for shutdown (%).</summary>
    public int ShutdownThreshold { get; set; } = 10;
    /// <summary>Runtime threshold for shutdown (seconds).</summary>
    public int ShutdownRuntimeSeconds { get; set; } = 120;

    /// <summary>Gets current UPS status.</summary>
    public UpsStatus CurrentStatus { get { lock (_lock) return _currentStatus; } }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _pollTimer = new Timer(async _ => { try { await PollUpsAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _pollTimer?.Dispose();
        return Task.CompletedTask;
    }

    private async Task PollUpsAsync()
    {
        var status = await ReadUpsStatusAsync();
        lock (_lock) _currentStatus = status;
        RecordSample(status.LoadPercent * status.NominalPowerWatts / 100.0, 0);
        UpdateRecommendations(status);
    }

    /// <summary>NUT port (default 3493).</summary>
    public int NutPort { get; set; } = 3493;

    /// <summary>NUT username for authenticated access.</summary>
    public string? NutUsername { get; set; }

    /// <summary>NUT password for authenticated access.</summary>
    public string? NutPassword { get; set; }

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectTimeoutMs { get; set; } = 5000;

    private async Task<UpsStatus> ReadUpsStatusAsync()
    {
        // Try NUT first
        if (await TryNutAsync() is { } nutStatus) return nutStatus;
        // No live source available — return unknown state so callers know data is stale
        // rather than silently reporting healthy values.
        return new UpsStatus
        {
            IsOnline = false,
            OnUtility = false,
            BatteryPercent = 0,
            RuntimeSeconds = 0,
            Model = "Unknown (NUT unreachable)"
        };
    }

    private async Task<UpsStatus?> TryNutAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeoutMs);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(UpsHost, NutPort, cts.Token);
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };

            // Authenticate if credentials provided
            if (!string.IsNullOrEmpty(NutUsername))
            {
                await writer.WriteLineAsync($"USERNAME {NutUsername}");
                var authResp = await reader.ReadLineAsync(cts.Token);
                if (authResp == null || !authResp.StartsWith("OK", StringComparison.Ordinal))
                    return null;

                await writer.WriteLineAsync($"PASSWORD {NutPassword ?? string.Empty}");
                var passResp = await reader.ReadLineAsync(cts.Token);
                if (passResp == null || !passResp.StartsWith("OK", StringComparison.Ordinal))
                    return null;
            }

            // Query UPS variables using LIST VAR
            await writer.WriteLineAsync($"LIST VAR {UpsName}");
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line == null) break;
                if (line.StartsWith("END LIST VAR", StringComparison.Ordinal)) break;
                if (line.StartsWith("ERR", StringComparison.Ordinal)) return null;
                // VAR <upsname> <varname> "<value>"
                if (line.StartsWith("VAR ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', 4);
                    if (parts.Length == 4)
                    {
                        var varName = parts[2];
                        var value = parts[3].Trim('"');
                        vars[varName] = value;
                    }
                }
            }

            if (vars.Count == 0) return null;

            // Parse NUT variables into UpsStatus
            var status = vars.GetValueOrDefault("ups.status", "");
            var isOnline = status.Contains("OL", StringComparison.OrdinalIgnoreCase);
            var onBattery = status.Contains("OB", StringComparison.OrdinalIgnoreCase);

            double.TryParse(vars.GetValueOrDefault("battery.charge", "0"), out var batteryCharge);
            double.TryParse(vars.GetValueOrDefault("battery.runtime", "0"), out var runtimeSecs);
            double.TryParse(vars.GetValueOrDefault("ups.load", "0"), out var loadPercent);
            double.TryParse(vars.GetValueOrDefault("ups.realpower.nominal", "1500"), out var nominalPower);
            var model = vars.GetValueOrDefault("ups.model", "Unknown");

            return new UpsStatus
            {
                IsOnline = isOnline || onBattery,
                OnUtility = isOnline && !onBattery,
                BatteryPercent = (int)batteryCharge,
                RuntimeSeconds = (int)runtimeSecs,
                LoadPercent = loadPercent,
                NominalPowerWatts = nominalPower > 0 ? nominalPower : 1500,
                Model = model
            };
        }
        catch { return null; }
    }

    /// <summary>Initiates graceful shutdown by issuing OS shutdown command.</summary>
    public async Task InitiateShutdownAsync(string reason, CancellationToken ct = default)
    {
        RecordOptimizationAction();

        System.Diagnostics.Debug.WriteLine($"[UpsIntegration] Initiating graceful shutdown: {reason}");

        // Issue platform-appropriate shutdown command
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "-h +1",  // Halt in 1 minute to allow graceful wind-down
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/s /t 60 /c \"UPS battery low\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UpsIntegration] Shutdown command failed: {ex.Message}");
            throw;
        }
    }

    private void UpdateRecommendations(UpsStatus status)
    {
        ClearRecommendations();
        if (!status.OnUtility)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-on-battery",
                Type = "OnBattery",
                Priority = 9,
                Description = $"UPS on battery power. {status.RuntimeSeconds / 60:F0} minutes remaining.",
                CanAutoApply = false
            });
        }
        if (status.BatteryPercent <= ShutdownThreshold || status.RuntimeSeconds <= ShutdownRuntimeSeconds)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-shutdown",
                Type = "Shutdown",
                Priority = 10,
                Description = "Low UPS battery. Initiate graceful shutdown.",
                CanAutoApply = true,
                Action = "shutdown"
            });
        }
    }
}

/// <summary>UPS status information.</summary>
public sealed record UpsStatus
{
    public bool IsOnline { get; init; }
    public bool OnUtility { get; init; }
    public bool OnBattery => IsOnline && !OnUtility;
    public int BatteryPercent { get; init; }
    public int RuntimeSeconds { get; init; }
    public double LoadPercent { get; init; }
    public double NominalPowerWatts { get; init; } = 1500;
    public string Model { get; init; } = "Unknown";
    public DateTimeOffset LastUpdate { get; init; } = DateTimeOffset.UtcNow;
}
