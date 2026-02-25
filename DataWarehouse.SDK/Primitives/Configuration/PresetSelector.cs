using DataWarehouse.SDK.Hardware;

namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Selects the best-fit configuration preset based on detected hardware capabilities.
/// Uses IHardwareProbe to detect available resources and security modules.
/// </summary>
public static class PresetSelector
{
    /// <summary>
    /// Selects the best configuration preset based on hardware probe results.
    /// </summary>
    /// <param name="probe">Hardware probe to detect resources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recommended preset name and configuration.</returns>
    public static async Task<(string PresetName, DataWarehouseConfiguration Config)> SelectPresetAsync(
        IHardwareProbe probe,
        CancellationToken ct = default)
    {
        var devices = await probe.DiscoverAsync(null, ct);

        // Analyze resources
        var cpuCores = Environment.ProcessorCount;
        var hasHsm = HasHsm(devices);
        var hasTpm = HasTpm(devices);
        var hasGpu = HasGpu(devices);
        var ramGb = EstimateRamGb(devices);

        // Decision logic based on detected hardware
        if (hasHsm || hasTpm)
        {
            // HSM/TPM detected - paranoid preset for security-critical environments
            return ("paranoid", ConfigurationPresets.CreateParanoid());
        }

        if (hasGpu && ramGb >= 128)
        {
            // GPU + large RAM (128GB+) - god-tier preset for high-end systems
            return ("god-tier", ConfigurationPresets.CreateGodTier());
        }

        if (cpuCores >= 16 && ramGb >= 32)
        {
            // High resources (16+ cores, 32+ GB RAM) - secure preset
            return ("secure", ConfigurationPresets.CreateSecure());
        }

        if (cpuCores >= 4 && ramGb >= 8)
        {
            // Standard resources (4-16 cores, 8-32GB RAM) - standard preset
            return ("standard", ConfigurationPresets.CreateStandard());
        }

        // Low resources (< 4 cores, < 8GB RAM) - minimal preset
        return ("minimal", ConfigurationPresets.CreateMinimal());
    }

    private static bool HasHsm(IReadOnlyList<HardwareDevice> devices)
        => devices.Any(d => d.Type.HasFlag(HardwareDeviceType.HsmDevice));

    private static bool HasTpm(IReadOnlyList<HardwareDevice> devices)
        => devices.Any(d => d.Type.HasFlag(HardwareDeviceType.TpmDevice));

    private static bool HasGpu(IReadOnlyList<HardwareDevice> devices)
        => devices.Any(d => d.Type.HasFlag(HardwareDeviceType.GpuAccelerator));

    /// <summary>
    /// Estimate RAM in GB from hardware probe data.
    /// Falls back to GC reported memory if hardware probe does not provide memory info.
    /// </summary>
    private static int EstimateRamGb(IReadOnlyList<HardwareDevice> devices)
    {
        // Check for memory-related properties in any device
        foreach (var device in devices)
        {
            if (device.Properties.TryGetValue("TotalBytes", out var bytesValue) &&
                long.TryParse(bytesValue, out var bytes))
            {
                return (int)(bytes / (1024L * 1024 * 1024));
            }

            if (device.Properties.TryGetValue("TotalMemoryMB", out var mbValue) &&
                long.TryParse(mbValue, out var mb))
            {
                return (int)(mb / 1024);
            }
        }

        // Fallback: Use GC total available memory as rough proxy
        var gcMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (gcMemory > 0)
        {
            return (int)(gcMemory / (1024L * 1024 * 1024));
        }

        return 8; // Conservative fallback: assume 8GB
    }
}
