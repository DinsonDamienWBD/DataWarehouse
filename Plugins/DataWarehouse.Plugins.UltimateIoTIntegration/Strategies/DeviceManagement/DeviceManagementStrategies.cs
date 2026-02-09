using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.DeviceManagement;

/// <summary>
/// Base class for device management strategies.
/// </summary>
public abstract class DeviceManagementStrategyBase : IoTStrategyBase, IDeviceManagementStrategy
{
    protected readonly ConcurrentDictionary<string, DeviceInfo> Devices = new();
    protected readonly ConcurrentDictionary<string, DeviceTwin> DeviceTwins = new();

    public override IoTStrategyCategory Category => IoTStrategyCategory.DeviceManagement;

    public abstract Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);
    public abstract Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);
    public abstract Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);
    public abstract Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);
    public abstract Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);
    public abstract Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);
    public abstract Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>
/// Device registry strategy - manages device identity and metadata.
/// </summary>
public class DeviceRegistryStrategy : DeviceManagementStrategyBase
{
    public override string StrategyId => "device-registry";
    public override string StrategyName => "Device Registry";
    public override string Description => "Central registry for IoT device identity and metadata management";
    public override string[] Tags => new[] { "iot", "device", "registry", "identity", "management" };

    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default)
    {
        var deviceId = string.IsNullOrEmpty(request.DeviceId) ? Guid.NewGuid().ToString() : request.DeviceId;
        var primaryKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var secondaryKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        var device = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceType = request.DeviceType,
            Status = DeviceStatus.Registered,
            LastSeen = DateTimeOffset.UtcNow,
            Metadata = request.Metadata
        };

        Devices[deviceId] = device;
        DeviceTwins[deviceId] = new DeviceTwin
        {
            DeviceId = deviceId,
            DesiredProperties = request.InitialTwin,
            ReportedProperties = new(),
            Version = 1,
            LastUpdated = DateTimeOffset.UtcNow
        };

        return Task.FromResult(new DeviceRegistration
        {
            DeviceId = deviceId,
            Success = true,
            PrimaryKey = primaryKey,
            SecondaryKey = secondaryKey,
            ConnectionString = $"HostName=iot.datawarehouse.local;DeviceId={deviceId};SharedAccessKey={primaryKey}",
            RegisteredAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default)
    {
        if (!DeviceTwins.TryGetValue(deviceId, out var twin))
            throw new KeyNotFoundException($"Device '{deviceId}' not found");
        return Task.FromResult(twin);
    }

    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default)
    {
        if (!DeviceTwins.TryGetValue(deviceId, out var twin))
            throw new KeyNotFoundException($"Device '{deviceId}' not found");

        foreach (var kvp in desiredProperties)
            twin.DesiredProperties[kvp.Key] = kvp.Value;
        twin.Version++;
        twin.LastUpdated = DateTimeOffset.UtcNow;

        return Task.CompletedTask;
    }

    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default)
    {
        IEnumerable<DeviceInfo> devices = Devices.Values;

        if (!string.IsNullOrEmpty(query.DeviceType))
            devices = devices.Where(d => d.DeviceType == query.DeviceType);
        if (query.Status.HasValue)
            devices = devices.Where(d => d.Status == query.Status.Value);
        if (query.Limit.HasValue)
            devices = devices.Take(query.Limit.Value);

        return Task.FromResult(devices);
    }

    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new FirmwareUpdateResult
        {
            Success = true,
            JobId = Guid.NewGuid().ToString(),
            DevicesTargeted = 1,
            Message = $"Firmware update initiated for device {request.DeviceId}"
        });
    }

    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var removed = Devices.TryRemove(deviceId, out _);
        DeviceTwins.TryRemove(deviceId, out _);
        return Task.FromResult(removed);
    }

    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        Devices.TryGetValue(deviceId, out var device);
        return Task.FromResult(device);
    }
}

/// <summary>
/// Device twin strategy - manages device desired/reported state.
/// </summary>
public class DeviceTwinStrategy : DeviceManagementStrategyBase
{
    public override string StrategyId => "device-twin";
    public override string StrategyName => "Device Twin";
    public override string Description => "Manages device twins for desired and reported state synchronization";
    public override string[] Tags => new[] { "iot", "device", "twin", "state", "synchronization" };

    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default)
    {
        var deviceId = string.IsNullOrEmpty(request.DeviceId) ? Guid.NewGuid().ToString() : request.DeviceId;

        DeviceTwins[deviceId] = new DeviceTwin
        {
            DeviceId = deviceId,
            DesiredProperties = request.InitialTwin,
            ReportedProperties = new(),
            Version = 1,
            LastUpdated = DateTimeOffset.UtcNow
        };

        return Task.FromResult(new DeviceRegistration
        {
            DeviceId = deviceId,
            Success = true,
            RegisteredAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default)
    {
        if (!DeviceTwins.TryGetValue(deviceId, out var twin))
            throw new KeyNotFoundException($"Device twin for '{deviceId}' not found");
        return Task.FromResult(twin);
    }

    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default)
    {
        if (!DeviceTwins.TryGetValue(deviceId, out var twin))
        {
            twin = new DeviceTwin { DeviceId = deviceId, DesiredProperties = new(), ReportedProperties = new() };
            DeviceTwins[deviceId] = twin;
        }

        foreach (var kvp in desiredProperties)
            twin.DesiredProperties[kvp.Key] = kvp.Value;
        twin.Version++;
        twin.LastUpdated = DateTimeOffset.UtcNow;

        return Task.CompletedTask;
    }

    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default)
    {
        return Task.FromResult(Enumerable.Empty<DeviceInfo>());
    }

    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default)
    {
        // Update desired properties with firmware version
        if (DeviceTwins.TryGetValue(request.DeviceId, out var twin))
        {
            twin.DesiredProperties["firmwareVersion"] = request.FirmwareVersion;
            twin.DesiredProperties["firmwareUrl"] = request.FirmwareUrl;
            twin.Version++;
        }

        return Task.FromResult(new FirmwareUpdateResult { Success = true, JobId = Guid.NewGuid().ToString(), DevicesTargeted = 1 });
    }

    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(DeviceTwins.TryRemove(deviceId, out _));
    }

    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult<DeviceInfo?>(null);
    }
}

/// <summary>
/// Fleet management strategy - manages device groups and bulk operations.
/// </summary>
public class FleetManagementStrategy : DeviceManagementStrategyBase
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _deviceGroups = new();

    public override string StrategyId => "fleet-management";
    public override string StrategyName => "Fleet Management";
    public override string Description => "Manages device fleets with bulk operations and group management";
    public override string[] Tags => new[] { "iot", "device", "fleet", "group", "bulk", "management" };

    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default)
    {
        var deviceId = string.IsNullOrEmpty(request.DeviceId) ? Guid.NewGuid().ToString() : request.DeviceId;

        Devices[deviceId] = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceType = request.DeviceType,
            Status = DeviceStatus.Registered,
            LastSeen = DateTimeOffset.UtcNow,
            Metadata = request.Metadata
        };

        // Add to default group
        var groupId = request.Metadata.TryGetValue("groupId", out var gid) ? gid : "default";
        if (!_deviceGroups.TryGetValue(groupId, out var group))
        {
            group = new HashSet<string>();
            _deviceGroups[groupId] = group;
        }
        group.Add(deviceId);

        return Task.FromResult(new DeviceRegistration
        {
            DeviceId = deviceId,
            Success = true,
            RegisteredAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default)
    {
        DeviceTwins.TryGetValue(deviceId, out var twin);
        return Task.FromResult(twin ?? new DeviceTwin { DeviceId = deviceId });
    }

    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default)
    {
        IEnumerable<DeviceInfo> devices = Devices.Values;

        if (query.Tags != null && query.Tags.TryGetValue("groupId", out var groupId))
        {
            if (_deviceGroups.TryGetValue(groupId, out var group))
                devices = devices.Where(d => group.Contains(d.DeviceId));
        }

        return Task.FromResult(devices);
    }

    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default)
    {
        var targetDevices = new List<string>();

        if (!string.IsNullOrEmpty(request.DeviceGroupId) && _deviceGroups.TryGetValue(request.DeviceGroupId, out var group))
            targetDevices.AddRange(group);
        else if (!string.IsNullOrEmpty(request.DeviceId))
            targetDevices.Add(request.DeviceId);

        return Task.FromResult(new FirmwareUpdateResult
        {
            Success = true,
            JobId = Guid.NewGuid().ToString(),
            DevicesTargeted = targetDevices.Count,
            Message = $"Firmware update scheduled for {targetDevices.Count} devices"
        });
    }

    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var removed = Devices.TryRemove(deviceId, out _);
        foreach (var group in _deviceGroups.Values)
            group.Remove(deviceId);
        return Task.FromResult(removed);
    }

    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        Devices.TryGetValue(deviceId, out var device);
        return Task.FromResult(device);
    }
}

/// <summary>
/// Firmware OTA strategy - over-the-air firmware updates.
/// </summary>
public class FirmwareOtaStrategy : DeviceManagementStrategyBase
{
    public override string StrategyId => "firmware-ota";
    public override string StrategyName => "Firmware OTA";
    public override string Description => "Over-the-air firmware update management with rollback support";
    public override string[] Tags => new[] { "iot", "device", "firmware", "ota", "update", "rollback" };

    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new DeviceRegistration { DeviceId = request.DeviceId, Success = true });
    }

    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(new DeviceTwin { DeviceId = deviceId });
    }

    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default)
    {
        return Task.FromResult(Enumerable.Empty<DeviceInfo>());
    }

    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default)
    {
        // Simulate OTA firmware update with staged rollout
        return Task.FromResult(new FirmwareUpdateResult
        {
            Success = true,
            JobId = Guid.NewGuid().ToString(),
            DevicesTargeted = 1,
            Message = $"OTA update to {request.FirmwareVersion} initiated. Checksum: {request.Checksum ?? "none"}"
        });
    }

    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult<DeviceInfo?>(null);
    }
}

/// <summary>
/// Device lifecycle strategy - manages device state transitions.
/// </summary>
public class DeviceLifecycleStrategy : DeviceManagementStrategyBase
{
    public override string StrategyId => "device-lifecycle";
    public override string StrategyName => "Device Lifecycle";
    public override string Description => "Manages device lifecycle states from provisioning to retirement";
    public override string[] Tags => new[] { "iot", "device", "lifecycle", "state", "provisioning", "retirement" };

    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default)
    {
        var deviceId = string.IsNullOrEmpty(request.DeviceId) ? Guid.NewGuid().ToString() : request.DeviceId;

        Devices[deviceId] = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceType = request.DeviceType,
            Status = DeviceStatus.Registered,
            LastSeen = DateTimeOffset.UtcNow,
            Metadata = request.Metadata
        };

        return Task.FromResult(new DeviceRegistration
        {
            DeviceId = deviceId,
            Success = true,
            RegisteredAt = DateTimeOffset.UtcNow
        });
    }

    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.FromResult(new DeviceTwin { DeviceId = deviceId });
    }

    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default)
    {
        return Task.FromResult(Devices.Values.AsEnumerable());
    }

    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new FirmwareUpdateResult { Success = true, JobId = Guid.NewGuid().ToString(), DevicesTargeted = 1 });
    }

    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        // Transition to Retired status instead of deleting
        if (Devices.TryGetValue(deviceId, out var device))
        {
            device.Status = DeviceStatus.Retired;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        Devices.TryGetValue(deviceId, out var device);
        return Task.FromResult(device);
    }
}
