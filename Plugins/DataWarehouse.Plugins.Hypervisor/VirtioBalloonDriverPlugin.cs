using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Virtualization;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Virtio balloon memory driver plugin for KVM/QEMU guests.
/// Provides dynamic memory management through balloon inflation/deflation.
/// Supports Linux virtio_balloon and Windows vioscsi/balloon drivers.
/// </summary>
public class VirtioBalloonDriverPlugin : BalloonDriverPluginBase
{
    private const string LinuxBalloonPath = "/sys/devices/system/memory/balloon";
    private const string LinuxBalloonDriverPath = "/sys/module/virtio_balloon";
    private const string LinuxMemInfoPath = "/proc/meminfo";

    private long _targetMemory;
    private long _inflatedBytes;

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.virtio-balloon";

    /// <inheritdoc />
    public override string Name => "Virtio Balloon Driver";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override bool IsAvailable => CheckBalloonDriverAvailable();

    /// <inheritdoc />
    protected override async Task<long> GetCurrentTargetAsync()
    {
        if (!IsAvailable)
            return 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await GetLinuxBalloonTargetAsync();
        }

        return _targetMemory;
    }

    /// <inheritdoc />
    protected override async Task SetTargetAsync(long bytes)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Balloon driver is not available");

        _targetMemory = bytes;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await SetLinuxBalloonTargetAsync(bytes);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await SetWindowsBalloonTargetAsync(bytes);
        }
    }

    /// <inheritdoc />
    protected override async Task<BalloonStatistics> GetStatsAsync()
    {
        if (!IsAvailable)
        {
            return new BalloonStatistics(
                CurrentMemoryBytes: 0,
                MaxMemoryBytes: 0,
                SwapInBytes: 0,
                SwapOutBytes: 0
            );
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await GetLinuxBalloonStatsAsync();
        }

        return new BalloonStatistics(
            CurrentMemoryBytes: _inflatedBytes,
            MaxMemoryBytes: GetTotalPhysicalMemory(),
            SwapInBytes: 0,
            SwapOutBytes: 0
        );
    }

    private bool CheckBalloonDriverAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check for virtio_balloon module
            return Directory.Exists(LinuxBalloonDriverPath) ||
                   File.Exists("/sys/bus/virtio/drivers/virtio_balloon");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Check for Windows virtio balloon service
            return CheckWindowsBalloonService();
        }

        return false;
    }

    private bool CheckWindowsBalloonService()
    {
        try
        {
            // Check for BLNSVR (Balloon Service)
            var balloonPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "balloon.sys"
            );

            return File.Exists(balloonPath);
        }
        catch
        {
            return false;
        }
    }

    private async Task<long> GetLinuxBalloonTargetAsync()
    {
        try
        {
            // Read balloon stats from virtio device
            var statsPath = "/sys/bus/virtio/drivers/virtio_balloon/virtio*/actual";
            var actualFiles = Directory.GetFiles("/sys/bus/virtio/drivers/virtio_balloon", "virtio*", SearchOption.TopDirectoryOnly);

            if (actualFiles.Length > 0)
            {
                var actualPath = Path.Combine(actualFiles[0], "actual");
                if (File.Exists(actualPath))
                {
                    var value = await File.ReadAllTextAsync(actualPath);
                    // Value is in 4KB pages
                    return long.Parse(value.Trim()) * 4096;
                }
            }

            // Fallback: calculate from available memory
            var memInfo = await GetLinuxMemInfoAsync();
            return memInfo.TotalMemory - memInfo.AvailableMemory;
        }
        catch
        {
            return _targetMemory;
        }
    }

    private async Task SetLinuxBalloonTargetAsync(long bytes)
    {
        try
        {
            // Find the virtio balloon device
            var virtioDevices = Directory.GetDirectories("/sys/bus/virtio/drivers/virtio_balloon");
            foreach (var device in virtioDevices)
            {
                if (Path.GetFileName(device).StartsWith("virtio"))
                {
                    var numPagesPath = Path.Combine(device, "num_pages");
                    if (File.Exists(numPagesPath))
                    {
                        // Convert bytes to 4KB pages
                        var pages = bytes / 4096;
                        await File.WriteAllTextAsync(numPagesPath, pages.ToString());
                        _inflatedBytes = bytes;
                        return;
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Insufficient permissions to control balloon driver. Root/Administrator required.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to set balloon target: {ex.Message}", ex);
        }
    }

    private async Task SetWindowsBalloonTargetAsync(long bytes)
    {
        // Windows balloon is controlled via virtio device
        // This requires the BLNSVR service to be running
        _targetMemory = bytes;

        await Task.CompletedTask;
        throw new NotSupportedException("Direct balloon control on Windows requires administrative access through virtio device");
    }

    private async Task<BalloonStatistics> GetLinuxBalloonStatsAsync()
    {
        var memInfo = await GetLinuxMemInfoAsync();

        return new BalloonStatistics(
            CurrentMemoryBytes: memInfo.TotalMemory - memInfo.AvailableMemory,
            MaxMemoryBytes: memInfo.TotalMemory,
            SwapInBytes: memInfo.SwapIn,
            SwapOutBytes: memInfo.SwapOut
        );
    }

    private async Task<LinuxMemInfo> GetLinuxMemInfoAsync()
    {
        var result = new LinuxMemInfo();

        try
        {
            var lines = await File.ReadAllLinesAsync(LinuxMemInfoPath);

            foreach (var line in lines)
            {
                var parts = line.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length != 2) continue;

                var key = parts[0];
                var valueStr = parts[1].Split(' ')[0];
                if (!long.TryParse(valueStr, out var value)) continue;

                // Values in /proc/meminfo are in KB
                value *= 1024;

                switch (key)
                {
                    case "MemTotal":
                        result.TotalMemory = value;
                        break;
                    case "MemAvailable":
                        result.AvailableMemory = value;
                        break;
                    case "SwapTotal":
                        result.SwapTotal = value;
                        break;
                    case "SwapFree":
                        result.SwapFree = value;
                        break;
                }
            }

            // Read swap activity from /proc/vmstat
            if (File.Exists("/proc/vmstat"))
            {
                var vmstatLines = await File.ReadAllLinesAsync("/proc/vmstat");
                foreach (var line in vmstatLines)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) continue;

                    if (parts[0] == "pswpin" && long.TryParse(parts[1], out var pswpin))
                    {
                        result.SwapIn = pswpin * 4096; // Pages to bytes
                    }
                    else if (parts[0] == "pswpout" && long.TryParse(parts[1], out var pswpout))
                    {
                        result.SwapOut = pswpout * 4096;
                    }
                }
            }
        }
        catch
        {
            // Return zeros
        }

        return result;
    }

    private long GetTotalPhysicalMemory()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return gcInfo.TotalAvailableMemoryBytes;
        }
        catch
        {
            return 0;
        }
    }

    private struct LinuxMemInfo
    {
        public long TotalMemory;
        public long AvailableMemory;
        public long SwapTotal;
        public long SwapFree;
        public long SwapIn;
        public long SwapOut;
    }
}
