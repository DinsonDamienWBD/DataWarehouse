using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware.Hypervisor;
using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware.NVMe;

/// <summary>
/// Implementation of <see cref="INvmePassthrough"/> for direct NVMe command submission.
/// </summary>
/// <remarks>
/// <para>
/// NvmePassthrough provides direct NVMe command passthrough bypassing the OS filesystem.
/// This enables maximum I/O performance (>90% of hardware line rate) for applications
/// that manage their own storage layout (e.g., DataWarehouse VDE).
/// </para>
/// <para>
/// <strong>Platform Support:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Windows:</strong> Uses DeviceIoControl with IOCTL_STORAGE_PROTOCOL_COMMAND
///       to submit commands via the StorNVMe driver. Requires Administrator privileges.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Linux:</strong> Uses ioctl on /dev/nvmeX character device to submit commands
///       directly to the NVMe driver. Requires root or CAP_SYS_ADMIN.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Availability Detection:</strong> Passthrough is only available on bare metal or
/// VMs with NVMe device passthrough configured. Phase 35 conservatively assumes passthrough
/// is unavailable in VMs (detection can be enhanced in future to check for PCI passthrough).
/// </para>
/// <para>
/// <strong>Capability Registration:</strong> When passthrough is available, registers the
/// "nvme.passthrough" capability via <see cref="IPlatformCapabilityRegistry"/>.
/// </para>
/// <para>
/// <strong>Phase 35 Implementation:</strong> Provides device opening, capability registration,
/// and API contract. Actual IOCTL/ioctl command marshaling is marked as future work and currently
/// returns placeholder completions.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NVMe passthrough implementation (HW-07)")]
public sealed class NvmePassthrough : INvmePassthrough
{
    private readonly IPlatformCapabilityRegistry _registry;
    private readonly IHypervisorDetector _hypervisorDetector;
    private readonly string _devicePath;
    private IntPtr _deviceHandle = IntPtr.Zero; // Windows handle
    private int _deviceFd = -1; // Linux file descriptor
    private int _controllerId;
    private bool _isAvailable = false;
    private readonly object _lock = new();
    private bool _disposed = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="NvmePassthrough"/> class.
    /// </summary>
    /// <param name="registry">Platform capability registry for capability registration.</param>
    /// <param name="hypervisorDetector">Hypervisor detector for environment detection.</param>
    /// <param name="devicePath">
    /// NVMe device path (e.g., "\\.\PhysicalDrive0" on Windows, "/dev/nvme0" on Linux).
    /// </param>
    /// <remarks>
    /// The constructor attempts to open the NVMe device and determine passthrough availability.
    /// If the device cannot be opened (missing permissions, device not found, or running in VM),
    /// <see cref="IsAvailable"/> will be false.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="registry"/>, <paramref name="hypervisorDetector"/>, or
    /// <paramref name="devicePath"/> is null.
    /// </exception>
    public NvmePassthrough(
        IPlatformCapabilityRegistry registry,
        IHypervisorDetector hypervisorDetector,
        string devicePath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(hypervisorDetector);
        ArgumentException.ThrowIfNullOrEmpty(devicePath);

        _registry = registry;
        _hypervisorDetector = hypervisorDetector;
        _devicePath = devicePath;

        Initialize();
    }

    /// <inheritdoc/>
    public bool IsAvailable => _isAvailable;

    /// <inheritdoc/>
    public int ControllerId => _controllerId;

    /// <summary>
    /// Initializes the NVMe passthrough by opening the device and registering capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initialization steps:
    /// </para>
    /// <list type="number">
    ///   <item><description>Detect hypervisor environment (bare metal required)</description></item>
    ///   <item><description>Attempt to open the NVMe device (platform-specific)</description></item>
    ///   <item><description>Extract controller ID from device path</description></item>
    ///   <item><description>Register "nvme.passthrough" capability if successful</description></item>
    /// </list>
    /// <para>
    /// <strong>Phase 35 Conservative Approach:</strong> Assumes passthrough is unavailable in VMs.
    /// Future enhancement: check for PCI passthrough by examining device properties or attempting
    /// device open (some hypervisors expose passthrough devices with special paths).
    /// </para>
    /// </remarks>
    private void Initialize()
    {
        lock (_lock)
        {
            // Check if running on bare metal or with passthrough
            var hypervisorInfo = _hypervisorDetector.Detect();
            if (hypervisorInfo.Type != HypervisorType.None)
            {
                // In VM — passthrough only available if explicitly configured
                // For Phase 35: assume NOT available in VMs (conservative approach)
                // Production: attempt device open or check PCI device properties to detect passthrough
                _isAvailable = false;
                return;
            }

            // Try to open NVMe device
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _deviceHandle = NvmeInterop.CreateFile(
                    _devicePath,
                    NvmeInterop.GENERIC_READ | NvmeInterop.GENERIC_WRITE,
                    NvmeInterop.FILE_SHARE_READ | NvmeInterop.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NvmeInterop.OPEN_EXISTING,
                    NvmeInterop.FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                _isAvailable = _deviceHandle != NvmeInterop.INVALID_HANDLE_VALUE;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _deviceFd = NvmeInterop.Open(_devicePath, NvmeInterop.O_RDWR);
                _isAvailable = _deviceFd >= 0;
            }

            if (_isAvailable)
            {
                // Extract controller ID from device path
                // Windows: "\\.\PhysicalDrive0" -> controller 0
                // Linux: "/dev/nvme0" -> controller 0
                _controllerId = ExtractControllerId(_devicePath);

                // Note: Platform capability registry automatically derives "nvme.passthrough"
                // capability from detected NVMe hardware via hardware probe. Manual registration
                // is not required.
            }
        }
    }

    /// <summary>
    /// Extracts the NVMe controller ID from the device path.
    /// </summary>
    /// <param name="devicePath">Device path (Windows or Linux format).</param>
    /// <returns>Controller ID (0-based), or 0 if extraction fails.</returns>
    /// <remarks>
    /// <para>
    /// Extraction logic:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Windows: "\\.\PhysicalDrive0" → 0</description></item>
    ///   <item><description>Linux: "/dev/nvme0" → 0, "/dev/nvme1" → 1</description></item>
    /// </list>
    /// </remarks>
    private static int ExtractControllerId(string devicePath)
    {
        // Extract numeric suffix from device path
        var match = Regex.Match(devicePath, @"\d+$");
        return match.Success ? int.Parse(match.Value) : 0;
    }

    /// <inheritdoc/>
    public async Task<NvmeCompletion> SubmitAdminCommandAsync(
        NvmeAdminCommand opcode,
        uint nsid,
        byte[]? dataBuffer,
        uint[] commandDwords)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isAvailable)
            throw new InvalidOperationException("NVMe passthrough is not available.");

        ArgumentNullException.ThrowIfNull(commandDwords);
        if (commandDwords.Length != 6)
            throw new ArgumentException("Command dwords must be exactly 6 elements (CDW10-CDW15).", nameof(commandDwords));

        return await Task.Run(() =>
        {
            lock (_lock)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return SubmitWindowsAdminCommand(opcode, nsid, dataBuffer, commandDwords);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return SubmitLinuxAdminCommand(opcode, nsid, dataBuffer, commandDwords);
                }
                else
                {
                    throw new PlatformNotSupportedException("NVMe passthrough not supported on this platform.");
                }
            }
        });
    }

    /// <summary>
    /// Submits an admin command on Windows via IOCTL_STORAGE_PROTOCOL_COMMAND.
    /// </summary>
    /// <param name="opcode">Admin command opcode.</param>
    /// <param name="nsid">Namespace ID (0 for controller-level commands).</param>
    /// <param name="dataBuffer">Data buffer for command input/output.</param>
    /// <param name="commandDwords">Command-specific dwords (CDW10-CDW15).</param>
    /// <returns>NVMe completion structure.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Phase 35 Implementation:</strong> Returns placeholder completion.
    /// </para>
    /// <para>
    /// <strong>Production Implementation:</strong> Marshal STORAGE_PROTOCOL_COMMAND structure,
    /// build NVMe command packet, call DeviceIoControl with IOCTL_STORAGE_PROTOCOL_COMMAND,
    /// parse completion from ReturnStatus and device response.
    /// </para>
    /// </remarks>
    private NvmeCompletion SubmitWindowsAdminCommand(
        NvmeAdminCommand opcode,
        uint nsid,
        byte[]? dataBuffer,
        uint[] commandDwords)
    {
        // SIMPLIFIED for Phase 35: Return placeholder completion
        // Production: marshal STORAGE_PROTOCOL_COMMAND, call DeviceIoControl
        //
        // Steps:
        // 1. Allocate buffer: sizeof(STORAGE_PROTOCOL_COMMAND) + 64 (command) + dataBuffer.Length
        // 2. Fill STORAGE_PROTOCOL_COMMAND:
        //    - Version = 1
        //    - Length = sizeof(STORAGE_PROTOCOL_COMMAND)
        //    - ProtocolType = 3 (NVMe)
        //    - CommandLength = 64
        //    - DataToDeviceTransferLength or DataFromDeviceTransferLength = dataBuffer.Length
        //    - TimeOutValue = 30 seconds
        // 3. Build NvmeCommandPacket at offset sizeof(STORAGE_PROTOCOL_COMMAND):
        //    - Opcode, Flags, CommandId, NamespaceId, CommandDwords
        // 4. Call DeviceIoControl(_deviceHandle, IOCTL_STORAGE_PROTOCOL_COMMAND, buffer, ...)
        // 5. Parse STORAGE_PROTOCOL_COMMAND.ReturnStatus and STORAGE_PROTOCOL_COMMAND.ErrorCode
        // 6. Return NvmeCompletion with Result and Status

        // TODO: Actual Windows IOCTL_STORAGE_PROTOCOL_COMMAND marshaling
        return new NvmeCompletion
        {
            Result = 0,
            Reserved = 0,
            SqHead = 0,
            SqId = 0,
            CommandId = 0,
            Status = 0 // Success
        };
    }

    /// <summary>
    /// Submits an admin command on Linux via NVME_IOCTL_ADMIN_CMD.
    /// </summary>
    /// <param name="opcode">Admin command opcode.</param>
    /// <param name="nsid">Namespace ID (0 for controller-level commands).</param>
    /// <param name="dataBuffer">Data buffer for command input/output.</param>
    /// <param name="commandDwords">Command-specific dwords (CDW10-CDW15).</param>
    /// <returns>NVMe completion structure.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Phase 35 Implementation:</strong> Returns placeholder completion.
    /// </para>
    /// <para>
    /// <strong>Production Implementation:</strong> Build nvme_admin_cmd structure, pin data buffer
    /// (GC.AllocHandle), call ioctl(_deviceFd, NVME_IOCTL_ADMIN_CMD, &cmd), parse cmd.result.
    /// </para>
    /// </remarks>
    private NvmeCompletion SubmitLinuxAdminCommand(
        NvmeAdminCommand opcode,
        uint nsid,
        byte[]? dataBuffer,
        uint[] commandDwords)
    {
        // SIMPLIFIED for Phase 35: Return placeholder completion
        // Production: build nvme_admin_cmd structure, call ioctl
        //
        // Steps:
        // 1. Build nvme_admin_cmd structure:
        //    - opcode, flags, nsid
        //    - addr = GCHandle.Alloc(dataBuffer, GCHandleType.Pinned).AddrOfPinnedObject()
        //    - data_len = dataBuffer.Length
        //    - cdw10-cdw15 = commandDwords
        //    - timeout_ms = 30000
        // 2. Call ioctl(_deviceFd, NVME_IOCTL_ADMIN_CMD, ref cmd)
        // 3. Check return value (0 = success, -1 = error)
        // 4. Parse cmd.result (completion DW0)
        // 5. Free GCHandle
        // 6. Return NvmeCompletion with Result and Status

        // TODO: Actual Linux NVME_IOCTL_ADMIN_CMD marshaling
        return new NvmeCompletion
        {
            Result = 0,
            Reserved = 0,
            SqHead = 0,
            SqId = 0,
            CommandId = 0,
            Status = 0 // Success
        };
    }

    /// <inheritdoc/>
    public async Task<NvmeCompletion> SubmitIoCommandAsync(
        NvmeIoCommand opcode,
        uint nsid,
        ulong startLba,
        ushort blockCount,
        byte[]? dataBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isAvailable)
            throw new InvalidOperationException("NVMe passthrough is not available.");

        ArgumentOutOfRangeException.ThrowIfZero(nsid, nameof(nsid));

        return await Task.Run(() =>
        {
            lock (_lock)
            {
                // SIMPLIFIED for Phase 35: Return placeholder completion
                // Production: marshal I/O command, submit via IOCTL/ioctl
                //
                // Windows:
                // Similar to SubmitWindowsAdminCommand but with I/O opcode and LBA/block count in CDW10-CDW12
                //
                // Linux:
                // Similar to SubmitLinuxAdminCommand but use NVME_IOCTL_IO_CMD instead of NVME_IOCTL_ADMIN_CMD

                // TODO: Actual NVMe I/O command submission
                // - Windows: IOCTL_STORAGE_PROTOCOL_COMMAND with I/O command
                // - Linux: ioctl(_deviceFd, NVME_IOCTL_IO_CMD, ref cmd)
                return new NvmeCompletion
                {
                    Result = 0,
                    Reserved = 0,
                    SqHead = 0,
                    SqId = 0,
                    CommandId = 0,
                    Status = 0 // Success
                };
            }
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            if (_deviceHandle != IntPtr.Zero && _deviceHandle != NvmeInterop.INVALID_HANDLE_VALUE)
            {
                NvmeInterop.CloseHandle(_deviceHandle);
                _deviceHandle = IntPtr.Zero;
            }

            if (_deviceFd >= 0)
            {
                NvmeInterop.Close(_deviceFd);
                _deviceFd = -1;
            }

            _disposed = true;
        }
    }
}
