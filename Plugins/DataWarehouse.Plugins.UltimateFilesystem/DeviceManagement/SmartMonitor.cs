using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Platform-specific SMART attribute reader that extracts health metrics from
/// physical block devices on Linux (via sysfs/hwmon) and Windows (via WMI).
/// All read operations are best-effort with safe defaults on failure.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: SMART health monitoring (BMDV-03)")]
public sealed class SmartMonitor
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new SmartMonitor with an optional logger.
    /// </summary>
    /// <param name="logger">Logger for diagnostics. Uses NullLogger if null.</param>
    public SmartMonitor(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Reads SMART health attributes from the specified device using platform-appropriate methods.
    /// Returns best-effort data with safe defaults for any unreadable attributes.
    /// </summary>
    /// <param name="devicePath">OS-specific device path (e.g., /dev/nvme0n1 or \\.\PhysicalDrive0).</param>
    /// <param name="busType">Bus type of the device for selecting the appropriate read strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health snapshot with available SMART data; fields default to safe values when unreadable.</returns>
    public async Task<PhysicalDeviceHealth> ReadSmartAttributesAsync(
        string devicePath, BusType busType, CancellationToken ct = default)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return await ReadLinuxSmartAsync(devicePath, busType, ct).ConfigureAwait(false);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return await ReadWindowsSmartAsync(devicePath, ct).ConfigureAwait(false);
            }

            _logger.LogWarning("SMART monitoring not supported on platform {Platform}.",
                RuntimeInformation.OSDescription);
            return CreateDefaultHealth();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read SMART attributes for {DevicePath}. Returning defaults.", devicePath);
            return CreateDefaultHealth();
        }
    }

    // ========================================================================
    // Linux SMART reading via sysfs
    // ========================================================================

    private async Task<PhysicalDeviceHealth> ReadLinuxSmartAsync(
        string devicePath, BusType busType, CancellationToken ct)
    {
        var devName = Path.GetFileName(devicePath); // e.g., "nvme0n1" or "sda"
        var sysBlockPath = $"/sys/block/{devName}";
        var rawAttributes = new Dictionary<string, string>();

        double temperature = -1;
        double wearLevelPercent = 0;
        long totalBytesWritten = 0;
        long totalBytesRead = 0;
        long uncorrectableErrors = 0;
        long reallocatedSectors = 0;
        int powerOnHours = 0;
        bool isHealthy = true;

        if (busType == BusType.NVMe)
        {
            // NVMe: read temperature from hwmon
            temperature = await ReadNvmeTemperatureAsync(sysBlockPath, ct).ConfigureAwait(false);

            // NVMe: percentage_used for wear level
            var percentUsed = await ReadSysfsValueAsync(
                $"{sysBlockPath}/device/percentage_used", ct).ConfigureAwait(false);
            if (percentUsed >= 0)
            {
                wearLevelPercent = percentUsed;
                rawAttributes["percentage_used"] = percentUsed.ToString("F0");
            }

            // NVMe: error_count
            var errorCount = await ReadSysfsLongAsync(
                $"{sysBlockPath}/device/error_count", ct).ConfigureAwait(false);
            if (errorCount >= 0)
            {
                uncorrectableErrors = errorCount;
                rawAttributes["error_count"] = errorCount.ToString();
            }
        }
        else
        {
            // SATA/SCSI: temperature from hwmon
            temperature = await ReadHwmonTemperatureAsync(sysBlockPath, ct).ConfigureAwait(false);

            // SATA/SCSI: error counts
            var errorCnt = await ReadSysfsLongAsync(
                $"{sysBlockPath}/device/error_cnt", ct).ConfigureAwait(false);
            if (errorCnt >= 0)
            {
                uncorrectableErrors = errorCnt;
                rawAttributes["error_cnt"] = errorCnt.ToString();
            }

            var timeoutCnt = await ReadSysfsLongAsync(
                $"{sysBlockPath}/device/timeout_cnt", ct).ConfigureAwait(false);
            if (timeoutCnt >= 0)
            {
                rawAttributes["timeout_cnt"] = timeoutCnt.ToString();
            }
        }

        // Read I/O stats from /sys/block/{dev}/stat
        // Format: reads_completed reads_merged sectors_read time_reading writes_completed writes_merged sectors_written time_writing ...
        var statContent = await ReadSysfsFileAsync($"{sysBlockPath}/stat", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(statContent))
        {
            var parts = statContent.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 7)
            {
                if (long.TryParse(parts[2], out var sectorsRead))
                {
                    totalBytesRead = sectorsRead * 512; // sectors are always 512 bytes in /sys/block/*/stat
                    rawAttributes["sectors_read"] = sectorsRead.ToString();
                }

                if (long.TryParse(parts[6], out var sectorsWritten))
                {
                    totalBytesWritten = sectorsWritten * 512;
                    rawAttributes["sectors_written"] = sectorsWritten.ToString();
                }
            }
        }

        if (temperature >= 0)
        {
            rawAttributes["temperature_celsius"] = temperature.ToString("F1");
        }

        // Health assessment: healthy if no extreme values
        isHealthy = temperature < 85 && wearLevelPercent < 100 && uncorrectableErrors < 100;

        TimeSpan? estimatedLife = null;
        if (wearLevelPercent > 0 && wearLevelPercent < 100)
        {
            // Very rough estimate: if wear is X%, remaining is (100-X)% of current life
            // This is a placeholder; FailurePredictionEngine does proper EWMA analysis
            estimatedLife = null; // Defer to FailurePredictionEngine
        }

        return new PhysicalDeviceHealth(
            IsHealthy: isHealthy,
            TemperatureCelsius: temperature,
            WearLevelPercent: wearLevelPercent,
            TotalBytesWritten: totalBytesWritten,
            TotalBytesRead: totalBytesRead,
            UncorrectableErrors: uncorrectableErrors,
            ReallocatedSectors: reallocatedSectors,
            PowerOnHours: powerOnHours,
            EstimatedRemainingLife: estimatedLife,
            RawSmartAttributes: rawAttributes);
    }

    private async Task<double> ReadNvmeTemperatureAsync(string sysBlockPath, CancellationToken ct)
    {
        // Try direct hwmon under the NVMe device
        var hwmonPaths = new[]
        {
            $"{sysBlockPath}/device/hwmon",
            $"{sysBlockPath}/device"
        };

        foreach (var basePath in hwmonPaths)
        {
            try
            {
                if (!Directory.Exists(basePath)) continue;

                var hwmonDirs = Directory.GetDirectories(basePath, "hwmon*");
                foreach (var hwmonDir in hwmonDirs)
                {
                    var tempFile = Path.Combine(hwmonDir, "temp1_input");
                    var value = await ReadSysfsValueAsync(tempFile, ct).ConfigureAwait(false);
                    if (value >= 0)
                    {
                        return value / 1000.0; // millidegrees to degrees
                    }
                }
            }
            catch
            {

                // Continue trying other paths
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        return -1;
    }

    private async Task<double> ReadHwmonTemperatureAsync(string sysBlockPath, CancellationToken ct)
    {
        // Try hwmon under the device path
        try
        {
            var deviceHwmon = $"{sysBlockPath}/device/hwmon";
            if (Directory.Exists(deviceHwmon))
            {
                var hwmonDirs = Directory.GetDirectories(deviceHwmon, "hwmon*");
                foreach (var hwmonDir in hwmonDirs)
                {
                    // Try temp*_input files
                    var tempFiles = Directory.GetFiles(hwmonDir, "temp*_input");
                    foreach (var tempFile in tempFiles)
                    {
                        var value = await ReadSysfsValueAsync(tempFile, ct).ConfigureAwait(false);
                        if (value >= 0)
                        {
                            return value / 1000.0;
                        }
                    }
                }
            }
        }
        catch
        {

            // Temperature is optional
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        // Also try /sys/class/hwmon/ as fallback
        try
        {
            const string hwmonClass = "/sys/class/hwmon";
            if (Directory.Exists(hwmonClass))
            {
                foreach (var hwmonDir in Directory.GetDirectories(hwmonClass))
                {
                    var nameFile = Path.Combine(hwmonDir, "name");
                    var name = await ReadSysfsFileAsync(nameFile, ct).ConfigureAwait(false);
                    if (name != null && name.Trim().Contains("drivetemp", StringComparison.OrdinalIgnoreCase))
                    {
                        var tempFile = Path.Combine(hwmonDir, "temp1_input");
                        var value = await ReadSysfsValueAsync(tempFile, ct).ConfigureAwait(false);
                        if (value >= 0)
                        {
                            return value / 1000.0;
                        }
                    }
                }
            }
        }
        catch
        {

            // Temperature is optional
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return -1;
    }

    // ========================================================================
    // Windows SMART reading via WMI
    // ========================================================================

    private async Task<PhysicalDeviceHealth> ReadWindowsSmartAsync(
        string devicePath, CancellationToken ct)
    {
        var rawAttributes = new Dictionary<string, string>();
        bool isHealthy = true;
        double temperature = -1;
        double wearLevelPercent = 0;
        long totalBytesWritten = 0;
        long totalBytesRead = 0;
        long uncorrectableErrors = 0;
        long reallocatedSectors = 0;
        int powerOnHours = 0;

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // Read MSStorageDriver_FailurePredictStatus for overall health
            isHealthy = ReadWmiFailurePredictStatus(devicePath, rawAttributes);

            // Read MSStorageDriver_FailurePredictData for SMART attributes
            ReadWmiFailurePredictData(devicePath, rawAttributes,
                out temperature, out wearLevelPercent, out totalBytesWritten,
                out totalBytesRead, out uncorrectableErrors, out reallocatedSectors,
                out powerOnHours);

        }, ct).ConfigureAwait(false);

        return new PhysicalDeviceHealth(
            IsHealthy: isHealthy,
            TemperatureCelsius: temperature,
            WearLevelPercent: wearLevelPercent,
            TotalBytesWritten: totalBytesWritten,
            TotalBytesRead: totalBytesRead,
            UncorrectableErrors: uncorrectableErrors,
            ReallocatedSectors: reallocatedSectors,
            PowerOnHours: powerOnHours,
            EstimatedRemainingLife: null,
            RawSmartAttributes: rawAttributes);
    }

    private bool ReadWmiFailurePredictStatus(string devicePath, Dictionary<string, string> rawAttributes)
    {
        try
        {
            var managementType = Type.GetType("System.Management.ManagementObjectSearcher, System.Management");
            if (managementType == null) return true;

            // Extract drive index from device path (\\.\PhysicalDriveN -> N)
            var driveIndex = ExtractDriveIndex(devicePath);

            using var searcher = (IDisposable?)Activator.CreateInstance(managementType,
                @"root\WMI",
                "SELECT PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus");
            if (searcher == null) return true;

            var getMethod = managementType.GetMethod("Get", Type.EmptyTypes);
            var results = getMethod?.Invoke(searcher, null);
            if (results is System.Collections.IEnumerable enumerable)
            {
                foreach (var obj in enumerable)
                {
                    try
                    {
                        var objType = obj.GetType();
                        var indexer = objType.GetProperty("Item", new[] { typeof(string) });
                        if (indexer == null) continue;

                        var predictFailure = indexer.GetValue(obj, new object[] { "PredictFailure" });
                        if (predictFailure is bool failing)
                        {
                            rawAttributes["PredictFailure"] = failing.ToString();
                            if (failing) return false; // Device predicts failure
                        }
                    }
                    catch
                    {

                        // Skip individual WMI object errors
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                    }
                    finally
                    {
                        (obj as IDisposable)?.Dispose();
                    }
                }
            }

            (results as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read WMI FailurePredictStatus for {DevicePath}.", devicePath);
        }

        return true;
    }

    private void ReadWmiFailurePredictData(string devicePath, Dictionary<string, string> rawAttributes,
        out double temperature, out double wearLevelPercent, out long totalBytesWritten,
        out long totalBytesRead, out long uncorrectableErrors, out long reallocatedSectors,
        out int powerOnHours)
    {
        temperature = -1;
        wearLevelPercent = 0;
        totalBytesWritten = 0;
        totalBytesRead = 0;
        uncorrectableErrors = 0;
        reallocatedSectors = 0;
        powerOnHours = 0;

        try
        {
            var managementType = Type.GetType("System.Management.ManagementObjectSearcher, System.Management");
            if (managementType == null) return;

            using var searcher = (IDisposable?)Activator.CreateInstance(managementType,
                @"root\WMI",
                "SELECT VendorSpecific FROM MSStorageDriver_FailurePredictData");
            if (searcher == null) return;

            var getMethod = managementType.GetMethod("Get", Type.EmptyTypes);
            var results = getMethod?.Invoke(searcher, null);
            if (results is System.Collections.IEnumerable enumerable)
            {
                foreach (var obj in enumerable)
                {
                    try
                    {
                        var objType = obj.GetType();
                        var indexer = objType.GetProperty("Item", new[] { typeof(string) });
                        if (indexer == null) continue;

                        var vendorSpecific = indexer.GetValue(obj, new object[] { "VendorSpecific" });
                        if (vendorSpecific is byte[] smartData)
                        {
                            ParseAtaSmartData(smartData, rawAttributes,
                                out temperature, out wearLevelPercent, out totalBytesWritten,
                                out totalBytesRead, out uncorrectableErrors, out reallocatedSectors,
                                out powerOnHours);
                        }
                    }
                    catch
                    {

                        // Skip individual parse errors
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                    }
                    finally
                    {
                        (obj as IDisposable)?.Dispose();
                    }
                }
            }

            (results as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read WMI FailurePredictData for {DevicePath}.", devicePath);
        }
    }

    /// <summary>
    /// Parses standard ATA SMART attribute format from the VendorSpecific byte array.
    /// Each attribute is 12 bytes: [ID:1][Flags:2][Current:1][Worst:1][Raw:6][Reserved:1]
    /// Standard attributes parsed: 1 (Read Error Rate), 5 (Reallocated Sectors), 9 (Power-On Hours),
    /// 12 (Power Cycle Count), 187 (Reported Uncorrectable), 188 (Command Timeout),
    /// 194 (Temperature), 197 (Current Pending Sector), 198 (Offline Uncorrectable),
    /// 199 (UDMA CRC Error), 241 (Total LBAs Written), 242 (Total LBAs Read).
    /// </summary>
    private static void ParseAtaSmartData(byte[] smartData, Dictionary<string, string> rawAttributes,
        out double temperature, out double wearLevelPercent, out long totalBytesWritten,
        out long totalBytesRead, out long uncorrectableErrors, out long reallocatedSectors,
        out int powerOnHours)
    {
        temperature = -1;
        wearLevelPercent = 0;
        totalBytesWritten = 0;
        totalBytesRead = 0;
        uncorrectableErrors = 0;
        reallocatedSectors = 0;
        powerOnHours = 0;

        if (smartData == null || smartData.Length < 2) return;

        // SMART data starts at offset 2, each attribute is 12 bytes
        const int attributeSize = 12;
        int offset = 2;

        while (offset + attributeSize <= smartData.Length)
        {
            byte attrId = smartData[offset];
            if (attrId == 0) break; // End of attributes

            byte currentValue = smartData[offset + 3];
            // Raw value is 6 bytes starting at offset+5 (little-endian)
            long rawValue = 0;
            for (int i = 0; i < 6 && (offset + 5 + i) < smartData.Length; i++)
            {
                rawValue |= (long)smartData[offset + 5 + i] << (i * 8);
            }

            rawAttributes[$"smart_{attrId}_current"] = currentValue.ToString();
            rawAttributes[$"smart_{attrId}_raw"] = rawValue.ToString();

            switch (attrId)
            {
                case 5: // Reallocated Sectors Count
                    reallocatedSectors = rawValue;
                    break;
                case 9: // Power-On Hours
                    powerOnHours = (int)Math.Min(rawValue, int.MaxValue);
                    break;
                case 187: // Reported Uncorrectable Errors
                    uncorrectableErrors = rawValue;
                    break;
                case 194: // Temperature Celsius
                    temperature = rawValue & 0xFF; // Lower byte is current temp
                    break;
                case 197: // Current Pending Sector Count
                    rawAttributes["pending_sectors"] = rawValue.ToString();
                    break;
                case 198: // Offline Uncorrectable Sector Count
                    uncorrectableErrors = Math.Max(uncorrectableErrors, rawValue);
                    break;
                case 241: // Total LBAs Written (multiply by 512 for bytes)
                    totalBytesWritten = rawValue * 512;
                    break;
                case 242: // Total LBAs Read (multiply by 512 for bytes)
                    totalBytesRead = rawValue * 512;
                    break;
            }

            offset += attributeSize;
        }
    }

    // ========================================================================
    // Utility methods
    // ========================================================================

    private static PhysicalDeviceHealth CreateDefaultHealth()
    {
        return new PhysicalDeviceHealth(
            IsHealthy: true,
            TemperatureCelsius: -1,
            WearLevelPercent: 0,
            TotalBytesWritten: 0,
            TotalBytesRead: 0,
            UncorrectableErrors: 0,
            ReallocatedSectors: 0,
            PowerOnHours: 0,
            EstimatedRemainingLife: null,
            RawSmartAttributes: new Dictionary<string, string>());
    }

    private static string ExtractDriveIndex(string devicePath)
    {
        // \\.\PhysicalDriveN -> N
        const string prefix = @"\\.\PhysicalDrive";
        if (devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return devicePath.Substring(prefix.Length);
        }
        return "0";
    }

    private static async Task<double> ReadSysfsValueAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return -1;
            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (double.TryParse(content.Trim(), out var value))
            {
                return value;
            }
        }
        catch
        {

            // Sysfs reads are best-effort
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return -1;
    }

    private static async Task<long> ReadSysfsLongAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return -1;
            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (long.TryParse(content.Trim(), out var value))
            {
                return value;
            }
        }
        catch
        {

            // Sysfs reads are best-effort
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return -1;
    }

    private static async Task<string?> ReadSysfsFileAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
