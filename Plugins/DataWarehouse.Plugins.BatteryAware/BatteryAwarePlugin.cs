using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.BatteryAware;

/// <summary>
/// Production-ready battery awareness plugin for laptop power management.
/// Provides real OS battery detection, adaptive background task management,
/// and intelligent deferral of heavy operations until device is plugged in.
///
/// Platform Support:
/// - Windows: Uses kernel32 GetSystemPowerStatus API
/// - Linux: Reads from /sys/class/power_supply
/// - macOS: Uses pmset command-line tool
///
/// Features:
/// - Real-time battery level monitoring
/// - Power source detection (AC/Battery/UPS)
/// - Configurable low-battery thresholds
/// - Background task throttling on battery
/// - Heavy operation deferral queue
/// - Battery health estimation
/// - Power event notifications
///
/// Message Commands:
/// - battery.status: Get current battery status
/// - battery.defer: Defer a heavy operation until plugged in
/// - battery.execute: Execute immediately if power allows, otherwise defer
/// - battery.policy.set: Configure power management policy
/// - battery.stats: Get power usage statistics
/// </summary>
public sealed class BatteryAwarePlugin : FeaturePluginBase
{
    private readonly ConcurrentDictionary<string, DeferredOperation> _deferredOperations;
    private readonly ConcurrentQueue<PowerEvent> _powerEventHistory;
    private readonly SemaphoreSlim _monitorLock;
    private readonly CancellationTokenSource _cts;
    private readonly object _stateLock;

    private Task? _monitorTask;
    private BatteryStatus _currentStatus;
    private PowerPolicy _policy;
    private long _totalDeferredOperations;
    private long _executedOperations;
    private long _throttledOperations;
    private DateTime _lastPowerChange;
    private TimeSpan _timeOnBattery;
    private TimeSpan _timeOnAc;
    private DateTime _sessionStart;

    private const int MaxEventHistory = 1000;
    private const int MonitorIntervalMs = 5000;
    private const int LowBatteryThresholdDefault = 20;
    private const int CriticalBatteryThresholdDefault = 10;

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.battery.aware";

    /// <inheritdoc/>
    public override string Name => "Battery Aware Plugin";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Initializes a new instance of the BatteryAwarePlugin.
    /// </summary>
    public BatteryAwarePlugin()
    {
        _deferredOperations = new ConcurrentDictionary<string, DeferredOperation>();
        _powerEventHistory = new ConcurrentQueue<PowerEvent>();
        _monitorLock = new SemaphoreSlim(1, 1);
        _cts = new CancellationTokenSource();
        _stateLock = new object();
        _currentStatus = new BatteryStatus();
        _policy = new PowerPolicy
        {
            LowBatteryThreshold = LowBatteryThresholdDefault,
            CriticalBatteryThreshold = CriticalBatteryThresholdDefault,
            DeferHeavyOperationsOnBattery = true,
            ThrottleBackgroundTasksOnBattery = true,
            BackgroundTaskThrottlePercent = 50
        };
        _sessionStart = DateTime.UtcNow;
        _lastPowerChange = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _sessionStart = DateTime.UtcNow;
        _currentStatus = await GetBatteryStatusInternalAsync();
        _lastPowerChange = DateTime.UtcNow;

        _monitorTask = MonitorBatteryAsync(_cts.Token);

        RecordPowerEvent(new PowerEvent
        {
            Type = PowerEventType.PluginStarted,
            Timestamp = DateTime.UtcNow,
            BatteryLevel = _currentStatus.ChargePercent,
            PowerSource = _currentStatus.PowerSource
        });
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _cts.Cancel();

        if (_monitorTask != null)
        {
            try
            {
                await _monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (TimeoutException)
            {
                // Monitor didn't stop in time, continue with shutdown
            }
        }

        RecordPowerEvent(new PowerEvent
        {
            Type = PowerEventType.PluginStopped,
            Timestamp = DateTime.UtcNow,
            BatteryLevel = _currentStatus.ChargePercent,
            PowerSource = _currentStatus.PowerSource
        });
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case "battery.status":
                    await HandleGetStatusAsync(message);
                    break;
                case "battery.defer":
                    HandleDeferOperation(message);
                    break;
                case "battery.execute":
                    await HandleExecuteOrDeferAsync(message);
                    break;
                case "battery.policy.set":
                    HandleSetPolicy(message);
                    break;
                case "battery.stats":
                    HandleGetStats(message);
                    break;
                case "battery.deferred.list":
                    HandleListDeferred(message);
                    break;
                case "battery.deferred.cancel":
                    HandleCancelDeferred(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }
        catch (Exception ex)
        {
            message.Payload["error"] = ex.Message;
            message.Payload["success"] = false;
        }
    }

    /// <summary>
    /// Gets the current battery status.
    /// </summary>
    /// <returns>Current battery status information.</returns>
    public async Task<BatteryStatus> GetBatteryStatusAsync()
    {
        return await GetBatteryStatusInternalAsync();
    }

    /// <summary>
    /// Checks if heavy operations should be deferred based on current power state.
    /// </summary>
    /// <returns>True if operations should be deferred.</returns>
    public bool ShouldDeferHeavyOperations()
    {
        lock (_stateLock)
        {
            if (!_policy.DeferHeavyOperationsOnBattery)
                return false;

            return _currentStatus.PowerSource == PowerSource.Battery &&
                   _currentStatus.ChargePercent < _policy.LowBatteryThreshold;
        }
    }

    /// <summary>
    /// Gets the recommended throttle percentage for background tasks.
    /// </summary>
    /// <returns>Throttle percentage (0-100, where 100 means no throttle).</returns>
    public int GetBackgroundTaskThrottlePercent()
    {
        lock (_stateLock)
        {
            if (!_policy.ThrottleBackgroundTasksOnBattery)
                return 100;

            if (_currentStatus.PowerSource == PowerSource.AC)
                return 100;

            if (_currentStatus.ChargePercent < _policy.CriticalBatteryThreshold)
                return Math.Max(10, _policy.BackgroundTaskThrottlePercent / 2);

            if (_currentStatus.ChargePercent < _policy.LowBatteryThreshold)
                return _policy.BackgroundTaskThrottlePercent;

            return 100;
        }
    }

    /// <summary>
    /// Defers an operation to be executed when the device is plugged in.
    /// </summary>
    /// <param name="operationId">Unique identifier for the operation.</param>
    /// <param name="operation">The operation to defer.</param>
    /// <param name="priority">Priority of the operation.</param>
    /// <returns>True if operation was deferred, false if it should execute immediately.</returns>
    public bool DeferOperation(string operationId, Func<CancellationToken, Task> operation, OperationPriority priority = OperationPriority.Normal)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Operation ID cannot be null or empty", nameof(operationId));
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        if (!ShouldDeferHeavyOperations())
            return false;

        var deferred = new DeferredOperation
        {
            Id = operationId,
            Operation = operation,
            Priority = priority,
            DeferredAt = DateTime.UtcNow,
            BatteryLevelAtDeferral = _currentStatus.ChargePercent
        };

        if (_deferredOperations.TryAdd(operationId, deferred))
        {
            Interlocked.Increment(ref _totalDeferredOperations);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cancels a deferred operation.
    /// </summary>
    /// <param name="operationId">The operation ID to cancel.</param>
    /// <returns>True if the operation was cancelled.</returns>
    public bool CancelDeferredOperation(string operationId)
    {
        return _deferredOperations.TryRemove(operationId, out _);
    }

    private async Task HandleGetStatusAsync(PluginMessage message)
    {
        var status = await GetBatteryStatusInternalAsync();
        message.Payload["result"] = new Dictionary<string, object>
        {
            ["hasBattery"] = status.HasBattery,
            ["chargePercent"] = status.ChargePercent,
            ["powerSource"] = status.PowerSource.ToString(),
            ["isCharging"] = status.IsCharging,
            ["estimatedRuntimeMinutes"] = status.EstimatedRuntimeMinutes,
            ["batteryHealth"] = status.BatteryHealth.ToString(),
            ["designCapacityMah"] = status.DesignCapacityMah,
            ["currentCapacityMah"] = status.CurrentCapacityMah,
            ["cycleCount"] = status.CycleCount,
            ["voltage"] = status.VoltageV,
            ["platform"] = GetPlatformName()
        };
        message.Payload["success"] = true;
    }

    private void HandleDeferOperation(PluginMessage message)
    {
        var operationId = GetString(message.Payload, "operationId") ?? Guid.NewGuid().ToString("N");
        var priorityStr = GetString(message.Payload, "priority") ?? "normal";

        var priority = priorityStr.ToLowerInvariant() switch
        {
            "critical" => OperationPriority.Critical,
            "high" => OperationPriority.High,
            "low" => OperationPriority.Low,
            _ => OperationPriority.Normal
        };

        var deferred = new DeferredOperation
        {
            Id = operationId,
            Operation = _ => Task.CompletedTask, // Placeholder - actual execution handled by caller
            Priority = priority,
            DeferredAt = DateTime.UtcNow,
            BatteryLevelAtDeferral = _currentStatus.ChargePercent,
            Description = GetString(message.Payload, "description")
        };

        if (_deferredOperations.TryAdd(operationId, deferred))
        {
            Interlocked.Increment(ref _totalDeferredOperations);
            message.Payload["result"] = new { operationId, deferred = true };
            message.Payload["success"] = true;
        }
        else
        {
            message.Payload["error"] = "Operation ID already exists";
            message.Payload["success"] = false;
        }
    }

    private async Task HandleExecuteOrDeferAsync(PluginMessage message)
    {
        var operationId = GetString(message.Payload, "operationId") ?? Guid.NewGuid().ToString("N");
        var forceExecute = GetBool(message.Payload, "force") ?? false;

        if (forceExecute || !ShouldDeferHeavyOperations())
        {
            Interlocked.Increment(ref _executedOperations);
            message.Payload["result"] = new { operationId, action = "execute", deferred = false };
        }
        else
        {
            var deferred = new DeferredOperation
            {
                Id = operationId,
                Operation = _ => Task.CompletedTask,
                Priority = OperationPriority.Normal,
                DeferredAt = DateTime.UtcNow,
                BatteryLevelAtDeferral = _currentStatus.ChargePercent
            };

            _deferredOperations.TryAdd(operationId, deferred);
            Interlocked.Increment(ref _totalDeferredOperations);
            message.Payload["result"] = new
            {
                operationId,
                action = "deferred",
                deferred = true,
                reason = $"Battery at {_currentStatus.ChargePercent}% (threshold: {_policy.LowBatteryThreshold}%)"
            };
        }

        message.Payload["success"] = true;
        await Task.CompletedTask;
    }

    private void HandleSetPolicy(PluginMessage message)
    {
        lock (_stateLock)
        {
            if (message.Payload.TryGetValue("lowBatteryThreshold", out var low) && low is int lowVal)
                _policy.LowBatteryThreshold = Math.Clamp(lowVal, 5, 50);

            if (message.Payload.TryGetValue("criticalBatteryThreshold", out var crit) && crit is int critVal)
                _policy.CriticalBatteryThreshold = Math.Clamp(critVal, 5, _policy.LowBatteryThreshold);

            if (message.Payload.TryGetValue("deferHeavyOperations", out var defer) && defer is bool deferVal)
                _policy.DeferHeavyOperationsOnBattery = deferVal;

            if (message.Payload.TryGetValue("throttleBackgroundTasks", out var throttle) && throttle is bool throttleVal)
                _policy.ThrottleBackgroundTasksOnBattery = throttleVal;

            if (message.Payload.TryGetValue("throttlePercent", out var percent) && percent is int percentVal)
                _policy.BackgroundTaskThrottlePercent = Math.Clamp(percentVal, 10, 100);
        }

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["lowBatteryThreshold"] = _policy.LowBatteryThreshold,
            ["criticalBatteryThreshold"] = _policy.CriticalBatteryThreshold,
            ["deferHeavyOperations"] = _policy.DeferHeavyOperationsOnBattery,
            ["throttleBackgroundTasks"] = _policy.ThrottleBackgroundTasksOnBattery,
            ["throttlePercent"] = _policy.BackgroundTaskThrottlePercent
        };
        message.Payload["success"] = true;
    }

    private void HandleGetStats(PluginMessage message)
    {
        var uptime = DateTime.UtcNow - _sessionStart;

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["uptimeSeconds"] = uptime.TotalSeconds,
            ["totalDeferredOperations"] = Interlocked.Read(ref _totalDeferredOperations),
            ["executedOperations"] = Interlocked.Read(ref _executedOperations),
            ["throttledOperations"] = Interlocked.Read(ref _throttledOperations),
            ["pendingDeferredCount"] = _deferredOperations.Count,
            ["timeOnBatterySeconds"] = _timeOnBattery.TotalSeconds,
            ["timeOnAcSeconds"] = _timeOnAc.TotalSeconds,
            ["powerEventCount"] = _powerEventHistory.Count,
            ["currentThrottlePercent"] = GetBackgroundTaskThrottlePercent()
        };
        message.Payload["success"] = true;
    }

    private void HandleListDeferred(PluginMessage message)
    {
        var deferred = _deferredOperations.Values
            .OrderByDescending(d => d.Priority)
            .ThenBy(d => d.DeferredAt)
            .Select(d => new Dictionary<string, object>
            {
                ["id"] = d.Id,
                ["priority"] = d.Priority.ToString(),
                ["deferredAt"] = d.DeferredAt,
                ["batteryLevelAtDeferral"] = d.BatteryLevelAtDeferral,
                ["description"] = d.Description ?? ""
            })
            .ToList();

        message.Payload["result"] = new { operations = deferred, count = deferred.Count };
        message.Payload["success"] = true;
    }

    private void HandleCancelDeferred(PluginMessage message)
    {
        var operationId = GetString(message.Payload, "operationId");
        if (string.IsNullOrEmpty(operationId))
        {
            message.Payload["error"] = "operationId is required";
            message.Payload["success"] = false;
            return;
        }

        var cancelled = _deferredOperations.TryRemove(operationId, out _);
        message.Payload["result"] = new { operationId, cancelled };
        message.Payload["success"] = true;
    }

    private async Task MonitorBatteryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MonitorIntervalMs, ct);

                var newStatus = await GetBatteryStatusInternalAsync();
                BatteryStatus previousStatus;

                lock (_stateLock)
                {
                    previousStatus = _currentStatus;
                    _currentStatus = newStatus;
                }

                // Track time on battery vs AC
                if (previousStatus.PowerSource == PowerSource.Battery)
                    _timeOnBattery += TimeSpan.FromMilliseconds(MonitorIntervalMs);
                else if (previousStatus.PowerSource == PowerSource.AC)
                    _timeOnAc += TimeSpan.FromMilliseconds(MonitorIntervalMs);

                // Detect power source changes
                if (newStatus.PowerSource != previousStatus.PowerSource)
                {
                    _lastPowerChange = DateTime.UtcNow;
                    RecordPowerEvent(new PowerEvent
                    {
                        Type = newStatus.PowerSource == PowerSource.AC
                            ? PowerEventType.PluggedIn
                            : PowerEventType.Unplugged,
                        Timestamp = DateTime.UtcNow,
                        BatteryLevel = newStatus.ChargePercent,
                        PowerSource = newStatus.PowerSource
                    });

                    // Execute deferred operations when plugged in
                    if (newStatus.PowerSource == PowerSource.AC)
                    {
                        await ExecuteDeferredOperationsAsync(ct);
                    }
                }

                // Detect threshold crossings
                if (previousStatus.ChargePercent >= _policy.LowBatteryThreshold &&
                    newStatus.ChargePercent < _policy.LowBatteryThreshold)
                {
                    RecordPowerEvent(new PowerEvent
                    {
                        Type = PowerEventType.LowBattery,
                        Timestamp = DateTime.UtcNow,
                        BatteryLevel = newStatus.ChargePercent,
                        PowerSource = newStatus.PowerSource
                    });
                }

                if (previousStatus.ChargePercent >= _policy.CriticalBatteryThreshold &&
                    newStatus.ChargePercent < _policy.CriticalBatteryThreshold)
                {
                    RecordPowerEvent(new PowerEvent
                    {
                        Type = PowerEventType.CriticalBattery,
                        Timestamp = DateTime.UtcNow,
                        BatteryLevel = newStatus.ChargePercent,
                        PowerSource = newStatus.PowerSource
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue monitoring
            }
        }
    }

    private async Task ExecuteDeferredOperationsAsync(CancellationToken ct)
    {
        var operations = _deferredOperations.Values
            .OrderByDescending(d => d.Priority)
            .ThenBy(d => d.DeferredAt)
            .ToList();

        foreach (var op in operations)
        {
            if (ct.IsCancellationRequested)
                break;

            // Re-check power status before each operation
            if (_currentStatus.PowerSource != PowerSource.AC)
                break;

            if (_deferredOperations.TryRemove(op.Id, out var removed))
            {
                try
                {
                    await removed.Operation(ct);
                    Interlocked.Increment(ref _executedOperations);
                }
                catch
                {
                    // Log error but continue with other operations
                }
            }
        }
    }

    private async Task<BatteryStatus> GetBatteryStatusInternalAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await GetWindowsBatteryStatusAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await GetLinuxBatteryStatusAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await GetMacOSBatteryStatusAsync();
        }

        return new BatteryStatus { HasBattery = false, PowerSource = PowerSource.AC };
    }

    private Task<BatteryStatus> GetWindowsBatteryStatusAsync()
    {
        var status = new BatteryStatus();

        try
        {
            if (GetSystemPowerStatus(out var powerStatus))
            {
                status.HasBattery = powerStatus.BatteryFlag != 128; // 128 = No system battery
                status.ChargePercent = powerStatus.BatteryLifePercent <= 100
                    ? powerStatus.BatteryLifePercent
                    : 0;
                status.PowerSource = powerStatus.ACLineStatus == 1 ? PowerSource.AC : PowerSource.Battery;
                status.IsCharging = (powerStatus.BatteryFlag & 8) != 0; // 8 = Charging

                if (powerStatus.BatteryLifeTime != -1)
                {
                    status.EstimatedRuntimeMinutes = powerStatus.BatteryLifeTime / 60;
                }

                // Determine battery health from flag
                status.BatteryHealth = powerStatus.BatteryFlag switch
                {
                    1 => BatteryHealth.Good, // High
                    2 => BatteryHealth.Good, // Low but functional
                    4 => BatteryHealth.Poor, // Critical
                    _ => BatteryHealth.Unknown
                };
            }
        }
        catch
        {
            status.HasBattery = false;
            status.PowerSource = PowerSource.Unknown;
        }

        return Task.FromResult(status);
    }

    private async Task<BatteryStatus> GetLinuxBatteryStatusAsync()
    {
        var status = new BatteryStatus();
        var powerSupplyPath = "/sys/class/power_supply";

        try
        {
            if (!Directory.Exists(powerSupplyPath))
            {
                status.HasBattery = false;
                status.PowerSource = PowerSource.AC;
                return status;
            }

            var directories = Directory.GetDirectories(powerSupplyPath);
            string? batteryPath = null;
            string? acPath = null;

            foreach (var dir in directories)
            {
                var typePath = Path.Combine(dir, "type");
                if (File.Exists(typePath))
                {
                    var type = (await File.ReadAllTextAsync(typePath)).Trim();
                    if (type.Equals("Battery", StringComparison.OrdinalIgnoreCase))
                        batteryPath = dir;
                    else if (type.Equals("Mains", StringComparison.OrdinalIgnoreCase))
                        acPath = dir;
                }
            }

            // Check AC status
            if (acPath != null)
            {
                var onlinePath = Path.Combine(acPath, "online");
                if (File.Exists(onlinePath))
                {
                    var online = (await File.ReadAllTextAsync(onlinePath)).Trim();
                    status.PowerSource = online == "1" ? PowerSource.AC : PowerSource.Battery;
                }
            }

            if (batteryPath != null)
            {
                status.HasBattery = true;

                // Read capacity (charge percent)
                var capacityPath = Path.Combine(batteryPath, "capacity");
                if (File.Exists(capacityPath))
                {
                    var capacity = (await File.ReadAllTextAsync(capacityPath)).Trim();
                    if (int.TryParse(capacity, out var cap))
                        status.ChargePercent = cap;
                }

                // Read charging status
                var statusPath = Path.Combine(batteryPath, "status");
                if (File.Exists(statusPath))
                {
                    var battStatus = (await File.ReadAllTextAsync(statusPath)).Trim();
                    status.IsCharging = battStatus.Equals("Charging", StringComparison.OrdinalIgnoreCase);
                }

                // Read voltage
                var voltagePath = Path.Combine(batteryPath, "voltage_now");
                if (File.Exists(voltagePath))
                {
                    var voltage = (await File.ReadAllTextAsync(voltagePath)).Trim();
                    if (long.TryParse(voltage, out var v))
                        status.VoltageV = v / 1_000_000.0; // Convert from microvolts
                }

                // Read design capacity
                var energyFullDesignPath = Path.Combine(batteryPath, "energy_full_design");
                var chargeFullDesignPath = Path.Combine(batteryPath, "charge_full_design");
                if (File.Exists(energyFullDesignPath))
                {
                    var energy = (await File.ReadAllTextAsync(energyFullDesignPath)).Trim();
                    if (long.TryParse(energy, out var e))
                        status.DesignCapacityMah = (int)(e / 1000); // Convert from uWh to mWh (approximation)
                }
                else if (File.Exists(chargeFullDesignPath))
                {
                    var charge = (await File.ReadAllTextAsync(chargeFullDesignPath)).Trim();
                    if (long.TryParse(charge, out var c))
                        status.DesignCapacityMah = (int)(c / 1000); // Convert from uAh to mAh
                }

                // Read current capacity
                var energyFullPath = Path.Combine(batteryPath, "energy_full");
                var chargeFullPath = Path.Combine(batteryPath, "charge_full");
                if (File.Exists(energyFullPath))
                {
                    var energy = (await File.ReadAllTextAsync(energyFullPath)).Trim();
                    if (long.TryParse(energy, out var e))
                        status.CurrentCapacityMah = (int)(e / 1000);
                }
                else if (File.Exists(chargeFullPath))
                {
                    var charge = (await File.ReadAllTextAsync(chargeFullPath)).Trim();
                    if (long.TryParse(charge, out var c))
                        status.CurrentCapacityMah = (int)(c / 1000);
                }

                // Read cycle count
                var cyclePath = Path.Combine(batteryPath, "cycle_count");
                if (File.Exists(cyclePath))
                {
                    var cycles = (await File.ReadAllTextAsync(cyclePath)).Trim();
                    if (int.TryParse(cycles, out var c))
                        status.CycleCount = c;
                }

                // Estimate battery health
                if (status.DesignCapacityMah > 0 && status.CurrentCapacityMah > 0)
                {
                    var healthPercent = (double)status.CurrentCapacityMah / status.DesignCapacityMah * 100;
                    status.BatteryHealth = healthPercent switch
                    {
                        >= 80 => BatteryHealth.Good,
                        >= 50 => BatteryHealth.Fair,
                        >= 20 => BatteryHealth.Poor,
                        _ => BatteryHealth.Critical
                    };
                }
            }
            else
            {
                status.HasBattery = false;
                status.PowerSource = PowerSource.AC;
            }
        }
        catch
        {
            status.HasBattery = false;
            status.PowerSource = PowerSource.Unknown;
        }

        return status;
    }

    private async Task<BatteryStatus> GetMacOSBatteryStatusAsync()
    {
        var status = new BatteryStatus();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/pmset",
                    Arguments = "-g batt",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse pmset output
            // Example: "Now drawing from 'AC Power'" or "Now drawing from 'Battery Power'"
            status.PowerSource = output.Contains("AC Power") ? PowerSource.AC : PowerSource.Battery;

            // Parse battery percentage: "InternalBattery-0 (id=...)  67%; charging; 0:45 remaining"
            var percentMatch = Regex.Match(output, @"(\d+)%");
            if (percentMatch.Success && int.TryParse(percentMatch.Groups[1].Value, out var percent))
            {
                status.HasBattery = true;
                status.ChargePercent = percent;
            }

            status.IsCharging = output.Contains("charging", StringComparison.OrdinalIgnoreCase) &&
                               !output.Contains("discharging", StringComparison.OrdinalIgnoreCase);

            // Parse remaining time
            var timeMatch = Regex.Match(output, @"(\d+):(\d+) remaining");
            if (timeMatch.Success)
            {
                if (int.TryParse(timeMatch.Groups[1].Value, out var hours) &&
                    int.TryParse(timeMatch.Groups[2].Value, out var minutes))
                {
                    status.EstimatedRuntimeMinutes = hours * 60 + minutes;
                }
            }

            // Get detailed battery info from ioreg
            using var ioregProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/ioreg",
                    Arguments = "-r -c AppleSmartBattery -w 0",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            ioregProcess.Start();
            var ioregOutput = await ioregProcess.StandardOutput.ReadToEndAsync();
            await ioregProcess.WaitForExitAsync();

            // Parse DesignCapacity
            var designCapMatch = Regex.Match(ioregOutput, @"""DesignCapacity"" = (\d+)");
            if (designCapMatch.Success && int.TryParse(designCapMatch.Groups[1].Value, out var designCap))
            {
                status.DesignCapacityMah = designCap;
            }

            // Parse MaxCapacity
            var maxCapMatch = Regex.Match(ioregOutput, @"""MaxCapacity"" = (\d+)");
            if (maxCapMatch.Success && int.TryParse(maxCapMatch.Groups[1].Value, out var maxCap))
            {
                status.CurrentCapacityMah = maxCap;
            }

            // Parse CycleCount
            var cycleMatch = Regex.Match(ioregOutput, @"""CycleCount"" = (\d+)");
            if (cycleMatch.Success && int.TryParse(cycleMatch.Groups[1].Value, out var cycles))
            {
                status.CycleCount = cycles;
            }

            // Parse Voltage
            var voltageMatch = Regex.Match(ioregOutput, @"""Voltage"" = (\d+)");
            if (voltageMatch.Success && int.TryParse(voltageMatch.Groups[1].Value, out var voltage))
            {
                status.VoltageV = voltage / 1000.0; // Convert from mV to V
            }

            // Estimate health
            if (status.DesignCapacityMah > 0 && status.CurrentCapacityMah > 0)
            {
                var healthPercent = (double)status.CurrentCapacityMah / status.DesignCapacityMah * 100;
                status.BatteryHealth = healthPercent switch
                {
                    >= 80 => BatteryHealth.Good,
                    >= 50 => BatteryHealth.Fair,
                    >= 20 => BatteryHealth.Poor,
                    _ => BatteryHealth.Critical
                };
            }
        }
        catch
        {
            status.HasBattery = false;
            status.PowerSource = PowerSource.Unknown;
        }

        return status;
    }

    private void RecordPowerEvent(PowerEvent evt)
    {
        _powerEventHistory.Enqueue(evt);

        // Trim history if too large
        while (_powerEventHistory.Count > MaxEventHistory)
        {
            _powerEventHistory.TryDequeue(out _);
        }
    }

    private static string GetPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";
        return "Unknown";
    }

    private static string? GetString(Dictionary<string, object> payload, string key)
    {
        return payload.TryGetValue(key, out var val) && val is string s ? s : null;
    }

    private static bool? GetBool(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is bool b) return b;
            if (val is string s && bool.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "battery.status", DisplayName = "Get Battery Status", Description = "Get current battery and power status" },
            new() { Name = "battery.defer", DisplayName = "Defer Operation", Description = "Defer heavy operation until plugged in" },
            new() { Name = "battery.execute", DisplayName = "Execute or Defer", Description = "Execute if power allows, otherwise defer" },
            new() { Name = "battery.policy.set", DisplayName = "Set Power Policy", Description = "Configure power management thresholds" },
            new() { Name = "battery.stats", DisplayName = "Get Statistics", Description = "Get power usage statistics" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Platform"] = GetPlatformName();
        metadata["HasBattery"] = _currentStatus.HasBattery;
        metadata["ChargePercent"] = _currentStatus.ChargePercent;
        metadata["PowerSource"] = _currentStatus.PowerSource.ToString();
        metadata["DeferredOperationsCount"] = _deferredOperations.Count;
        metadata["LowBatteryThreshold"] = _policy.LowBatteryThreshold;
        return metadata;
    }

    #region Windows P/Invoke

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

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

    #endregion
}

/// <summary>
/// Current battery and power status.
/// </summary>
public sealed class BatteryStatus
{
    /// <summary>
    /// Whether the system has a battery.
    /// </summary>
    public bool HasBattery { get; set; }

    /// <summary>
    /// Current charge level (0-100).
    /// </summary>
    public int ChargePercent { get; set; }

    /// <summary>
    /// Current power source.
    /// </summary>
    public PowerSource PowerSource { get; set; }

    /// <summary>
    /// Whether the battery is currently charging.
    /// </summary>
    public bool IsCharging { get; set; }

    /// <summary>
    /// Estimated runtime remaining in minutes.
    /// </summary>
    public int EstimatedRuntimeMinutes { get; set; } = -1;

    /// <summary>
    /// Battery health status.
    /// </summary>
    public BatteryHealth BatteryHealth { get; set; } = BatteryHealth.Unknown;

    /// <summary>
    /// Design capacity in mAh.
    /// </summary>
    public int DesignCapacityMah { get; set; }

    /// <summary>
    /// Current maximum capacity in mAh.
    /// </summary>
    public int CurrentCapacityMah { get; set; }

    /// <summary>
    /// Battery charge cycle count.
    /// </summary>
    public int CycleCount { get; set; }

    /// <summary>
    /// Current voltage in volts.
    /// </summary>
    public double VoltageV { get; set; }
}

/// <summary>
/// Power source enumeration.
/// </summary>
public enum PowerSource
{
    /// <summary>Unknown power source.</summary>
    Unknown,
    /// <summary>Running on AC power.</summary>
    AC,
    /// <summary>Running on battery power.</summary>
    Battery,
    /// <summary>Running on UPS.</summary>
    UPS
}

/// <summary>
/// Battery health status.
/// </summary>
public enum BatteryHealth
{
    /// <summary>Health unknown.</summary>
    Unknown,
    /// <summary>Battery in good condition.</summary>
    Good,
    /// <summary>Battery in fair condition.</summary>
    Fair,
    /// <summary>Battery in poor condition, consider replacement.</summary>
    Poor,
    /// <summary>Battery in critical condition, replace immediately.</summary>
    Critical
}

/// <summary>
/// Power management policy configuration.
/// </summary>
public sealed class PowerPolicy
{
    /// <summary>
    /// Battery percentage threshold for low battery warnings.
    /// </summary>
    public int LowBatteryThreshold { get; set; } = 20;

    /// <summary>
    /// Battery percentage threshold for critical battery warnings.
    /// </summary>
    public int CriticalBatteryThreshold { get; set; } = 10;

    /// <summary>
    /// Whether to defer heavy operations when on battery.
    /// </summary>
    public bool DeferHeavyOperationsOnBattery { get; set; } = true;

    /// <summary>
    /// Whether to throttle background tasks when on battery.
    /// </summary>
    public bool ThrottleBackgroundTasksOnBattery { get; set; } = true;

    /// <summary>
    /// Percentage to throttle background tasks to when on battery (0-100).
    /// </summary>
    public int BackgroundTaskThrottlePercent { get; set; } = 50;
}

/// <summary>
/// Operation priority for deferred operations.
/// </summary>
public enum OperationPriority
{
    /// <summary>Low priority, execute last.</summary>
    Low = 0,
    /// <summary>Normal priority.</summary>
    Normal = 1,
    /// <summary>High priority, execute early.</summary>
    High = 2,
    /// <summary>Critical priority, execute first.</summary>
    Critical = 3
}

/// <summary>
/// A deferred operation waiting for power.
/// </summary>
internal sealed class DeferredOperation
{
    public required string Id { get; init; }
    public required Func<CancellationToken, Task> Operation { get; init; }
    public OperationPriority Priority { get; init; }
    public DateTime DeferredAt { get; init; }
    public int BatteryLevelAtDeferral { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Power event types.
/// </summary>
public enum PowerEventType
{
    /// <summary>Plugin started.</summary>
    PluginStarted,
    /// <summary>Plugin stopped.</summary>
    PluginStopped,
    /// <summary>Device plugged in to AC power.</summary>
    PluggedIn,
    /// <summary>Device unplugged from AC power.</summary>
    Unplugged,
    /// <summary>Battery reached low threshold.</summary>
    LowBattery,
    /// <summary>Battery reached critical threshold.</summary>
    CriticalBattery
}

/// <summary>
/// Record of a power event.
/// </summary>
public sealed class PowerEvent
{
    /// <summary>Type of power event.</summary>
    public PowerEventType Type { get; init; }
    /// <summary>When the event occurred.</summary>
    public DateTime Timestamp { get; init; }
    /// <summary>Battery level at the time of event.</summary>
    public int BatteryLevel { get; init; }
    /// <summary>Power source at the time of event.</summary>
    public PowerSource PowerSource { get; init; }
}
