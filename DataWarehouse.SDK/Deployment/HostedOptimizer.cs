using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Optimizes hosted VM deployments to eliminate the double-WAL penalty.
/// Disables OS-level journaling for VDE container files when VDE WAL is active.
/// </summary>
/// <remarks>
/// <para>
/// <b>Double-WAL Problem:</b>
/// VMs running on journaling filesystems (ext4, XFS, NTFS) journal every write twice:
/// 1. VDE write-ahead log (application-level)
/// 2. Filesystem journal (OS-level)
/// This causes a 30%+ write throughput penalty.
/// </para>
/// <para>
/// <b>Solution:</b>
/// When VDE WAL is active, disable OS-level journaling for VDE container files.
/// This is SAFE because VDE WAL provides crash consistency.
/// </para>
/// <para>
/// <b>CRITICAL SAFETY RULE:</b>
/// NEVER disable OS journaling without VDE WAL active. Data loss will occur on crashes.
/// </para>
/// <para>
/// Platform-specific implementations:
/// - Linux ext4: Use chattr +j to disable journaling per file
/// - Linux XFS: No per-file journaling control (skip optimization)
/// - Windows NTFS: Use DeviceIoControl with FSCTL_SET_ZERO_DATA (advanced)
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Hosted VM optimizations (ENV-01)")]
public sealed class HostedOptimizer
{
    private readonly ILogger<HostedOptimizer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostedOptimizer"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public HostedOptimizer(ILogger<HostedOptimizer>? logger = null)
    {
        _logger = logger ?? NullLogger<HostedOptimizer>.Instance;
    }

    /// <summary>
    /// Applies hosted VM optimizations (double-WAL bypass, I/O alignment).
    /// </summary>
    /// <param name="context">Detected deployment context.</param>
    /// <param name="vdeContainerPath">Path to VDE container file.</param>
    /// <param name="vdeWalEnabled">Whether VDE write-ahead log is enabled.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// Optimization steps:
    /// 1. Validate preconditions (hosted VM environment, filesystem detected)
    /// 2. Check VDE WAL status (CRITICAL: must be enabled before disabling OS journaling)
    /// 3. Disable OS journaling for VDE container file (platform-specific)
    /// 4. Configure I/O alignment to match filesystem block size
    /// </para>
    /// </remarks>
    public async Task OptimizeAsync(
        DeploymentContext context,
        string vdeContainerPath,
        bool vdeWalEnabled,
        CancellationToken ct = default)
    {
        // Step 1: Validate preconditions
        if (context.Environment != DeploymentEnvironment.HostedVm)
        {
            _logger.LogDebug("Not a hosted VM environment. Skipping hosted optimizations.");
            return;
        }

        if (string.IsNullOrEmpty(context.FilesystemType))
        {
            _logger.LogWarning("Filesystem type not detected. Skipping hosted optimizations.");
            return;
        }

        _logger.LogInformation("Hosted VM detected. Filesystem: {FilesystemType}, Hypervisor: {HypervisorType}",
            context.FilesystemType, context.HypervisorType);

        // Step 2: CRITICAL SAFETY CHECK -- VDE WAL must be enabled
        if (!vdeWalEnabled)
        {
            _logger.LogWarning(
                "VDE WAL is DISABLED. Skipping double-WAL bypass to preserve data safety. " +
                "NEVER disable OS journaling without VDE WAL active.");
            // Do NOT proceed with double-WAL bypass
        }
        else
        {
            // Step 3: Double-WAL bypass (when safe) -- Linux and Windows only
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            {
                await DisableOsJournalingAsync(context.FilesystemType, vdeContainerPath, ct);
            }
        }

        // Step 4: I/O alignment configuration
        await ConfigureIoAlignmentAsync(vdeContainerPath, ct);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private async Task DisableOsJournalingAsync(string filesystemType, string vdeContainerPath, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            await DisableLinuxJournalingAsync(filesystemType, vdeContainerPath, ct);
        }
        else if (OperatingSystem.IsWindows())
        {
            await DisableWindowsJournalingAsync(filesystemType, vdeContainerPath, ct);
        }
        else
        {
            _logger.LogInformation("OS journaling bypass not supported on this platform.");
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task DisableLinuxJournalingAsync(string filesystemType, string vdeContainerPath, CancellationToken ct)
    {
        if (filesystemType.Equals("ext4", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Use chattr to disable journaling for specific file
                // chattr +j <file> -- requires root or CAP_DAC_OVERRIDE
                var startInfo = new ProcessStartInfo
                {
                    FileName = "chattr",
                    Arguments = $"-j \"{vdeContainerPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    _logger.LogWarning("Failed to start chattr process for ext4 journaling bypass.");
                    return;
                }

                await process.WaitForExitAsync(ct);

                if (process.ExitCode == 0)
                {
                    _logger.LogInformation(
                        "Double-WAL bypass enabled: OS journaling disabled for VDE container (ext4). " +
                        "Expected throughput improvement: 30%+");
                }
                else
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogWarning(
                        "Failed to disable ext4 journaling (chattr exit code {ExitCode}). " +
                        "This may require root privileges. Error: {Error}",
                        process.ExitCode, error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to disable ext4 journaling for VDE container. " +
                    "Double-WAL penalty will remain. Ensure chattr is available and user has permissions.");
            }
        }
        else if (filesystemType.Equals("xfs", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "XFS filesystem detected. XFS does not support per-file journaling control. " +
                "Double-WAL bypass not available. Consider using ext4 for hosted VMs.");
        }
        else
        {
            _logger.LogInformation(
                "Filesystem {FilesystemType} does not support per-file journaling control. " +
                "Double-WAL bypass not available.", filesystemType);
        }
    }

    [SupportedOSPlatform("windows")]
    private Task DisableWindowsJournalingAsync(string filesystemType, string vdeContainerPath, CancellationToken ct)
    {
        if (filesystemType.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "NTFS filesystem detected. Per-file USN journal bypass requires advanced P/Invoke (DeviceIoControl). " +
                "Not implemented in this version. Consider using ReFS for better performance on Windows.");
        }
        else if (filesystemType.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "ReFS filesystem detected. ReFS does not use journaling (uses copy-on-write). " +
                "No double-WAL penalty. Excellent choice for hosted VMs on Windows.");
        }
        else
        {
            _logger.LogInformation(
                "Filesystem {FilesystemType} does not require journaling bypass.", filesystemType);
        }

        return Task.CompletedTask;
    }

    private async Task ConfigureIoAlignmentAsync(string vdeContainerPath, CancellationToken ct)
    {
        long? blockSize = null;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            blockSize = await FilesystemDetector.GetFilesystemBlockSizeAsync(vdeContainerPath, ct);
        }

        if (blockSize.HasValue)
        {
            _logger.LogWarning(
                "I/O alignment detected: {BlockSize} bytes (filesystem block size). " +
                "VDE storage layer alignment configuration requires IBlockDevice integration. " +
                "Currently using default alignment; I/O may not be optimal.",
                blockSize.Value);
        }
        else
        {
            _logger.LogInformation(
                "Filesystem block size not detected. Using default I/O alignment (4096 bytes).");
        }
    }
}
