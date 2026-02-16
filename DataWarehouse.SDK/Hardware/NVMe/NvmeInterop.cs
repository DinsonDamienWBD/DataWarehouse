using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Hardware.NVMe;

/// <summary>
/// Platform-specific interop for NVMe command passthrough.
/// </summary>
/// <remarks>
/// <para>
/// Provides P/Invoke declarations for NVMe passthrough on Windows (IOCTL_STORAGE_PROTOCOL_COMMAND)
/// and Linux (/dev/nvmeX ioctl).
/// </para>
/// <para>
/// <strong>Windows:</strong> Uses DeviceIoControl with IOCTL_STORAGE_PROTOCOL_COMMAND to submit
/// NVMe commands to the controller via the StorNVMe driver.
/// </para>
/// <para>
/// <strong>Linux:</strong> Uses ioctl on /dev/nvmeX character device to submit admin and I/O
/// commands directly to the NVMe driver.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NVMe platform interop (HW-07)")]
internal static partial class NvmeInterop
{
    #region Windows NVMe Passthrough (IOCTL_STORAGE_PROTOCOL_COMMAND)

    /// <summary>
    /// Windows IOCTL for NVMe command passthrough via StorNVMe driver.
    /// </summary>
    /// <remarks>
    /// IOCTL_STORAGE_PROTOCOL_COMMAND (0x004D4480) is the primary interface for NVMe passthrough
    /// on Windows 8+. Requires Administrator privileges.
    /// </remarks>
    internal const uint IOCTL_STORAGE_PROTOCOL_COMMAND = 0x004D4480;

    /// <summary>
    /// Windows storage protocol command structure for NVMe passthrough.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Structure is followed by variable-length data:
    /// </para>
    /// <list type="number">
    ///   <item><description>Command buffer (CommandLength bytes)</description></item>
    ///   <item><description>Error info buffer (ErrorInfoLength bytes)</description></item>
    ///   <item><description>Data to device buffer (DataToDeviceTransferLength bytes)</description></item>
    ///   <item><description>Data from device buffer (DataFromDeviceTransferLength bytes)</description></item>
    /// </list>
    /// <para>
    /// For NVMe, CommandLength is typically 64 bytes (sizeof(NvmeCommandPacket)).
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROTOCOL_COMMAND
    {
        /// <summary>Version = 1 (STORAGE_PROTOCOL_STRUCTURE_VERSION).</summary>
        public uint Version;

        /// <summary>Structure length = sizeof(STORAGE_PROTOCOL_COMMAND).</summary>
        public uint Length;

        /// <summary>Protocol type: ProtocolTypeNvme = 3.</summary>
        public uint ProtocolType;

        /// <summary>Flags (reserved, set to 0).</summary>
        public uint Flags;

        /// <summary>Return status code from the device.</summary>
        public uint ReturnStatus;

        /// <summary>Error code (device-specific).</summary>
        public uint ErrorCode;

        /// <summary>Length of the command buffer (typically 64 for NVMe).</summary>
        public uint CommandLength;

        /// <summary>Length of the error info buffer.</summary>
        public uint ErrorInfoLength;

        /// <summary>Length of data to transfer to the device (write).</summary>
        public uint DataToDeviceTransferLength;

        /// <summary>Length of data to transfer from the device (read).</summary>
        public uint DataFromDeviceTransferLength;

        /// <summary>Timeout value in seconds.</summary>
        public uint TimeOutValue;

        /// <summary>Offset to error info buffer (relative to structure start).</summary>
        public uint ErrorInfoOffset;

        /// <summary>Offset to data-to-device buffer (relative to structure start).</summary>
        public uint DataToDeviceBufferOffset;

        /// <summary>Offset to data-from-device buffer (relative to structure start).</summary>
        public uint DataFromDeviceBufferOffset;

        /// <summary>Command-specific parameter (NVMe namespace ID for I/O commands).</summary>
        public uint CommandSpecific;

        /// <summary>Reserved (set to 0).</summary>
        public uint Reserved0;
    }

    /// <summary>Protocol type constant for NVMe.</summary>
    internal const uint ProtocolTypeNvme = 3;

    /// <summary>Storage protocol structure version.</summary>
    internal const uint STORAGE_PROTOCOL_STRUCTURE_VERSION = 1;

    /// <summary>
    /// Windows DeviceIoControl for device I/O operations.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// Opens a device handle for NVMe controller access.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>
    /// Closes a device handle.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    /// <summary>Generic read access.</summary>
    internal const uint GENERIC_READ = 0x80000000;

    /// <summary>Generic write access.</summary>
    internal const uint GENERIC_WRITE = 0x40000000;

    /// <summary>Share read access.</summary>
    internal const uint FILE_SHARE_READ = 0x00000001;

    /// <summary>Share write access.</summary>
    internal const uint FILE_SHARE_WRITE = 0x00000002;

    /// <summary>Open existing file/device.</summary>
    internal const uint OPEN_EXISTING = 3;

    /// <summary>Normal file attributes.</summary>
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>Invalid handle value.</summary>
    internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    #endregion

    #region Linux NVMe Passthrough (/dev/nvmeX ioctl)

    /// <summary>
    /// Linux ioctl command code for NVMe admin command passthrough.
    /// </summary>
    /// <remarks>
    /// NVME_IOCTL_ADMIN_CMD is defined in linux/nvme_ioctl.h. Used with ioctl() on /dev/nvme0, /dev/nvme1, etc.
    /// </remarks>
    internal const int NVME_IOCTL_ADMIN_CMD = unchecked((int)0xC0484E41);

    /// <summary>
    /// Linux ioctl command code for NVMe I/O command passthrough.
    /// </summary>
    /// <remarks>
    /// NVME_IOCTL_IO_CMD is defined in linux/nvme_ioctl.h. Used with ioctl() on /dev/nvme0n1, /dev/nvme0n2, etc.
    /// </remarks>
    internal const int NVME_IOCTL_IO_CMD = unchecked((int)0xC0484E43);

    /// <summary>
    /// Linux NVMe admin command structure for ioctl passthrough.
    /// </summary>
    /// <remarks>
    /// Matches the structure defined in linux/nvme_ioctl.h (struct nvme_admin_cmd).
    /// Total size: 72 bytes.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct nvme_admin_cmd
    {
        /// <summary>Opcode (admin command).</summary>
        public byte opcode;

        /// <summary>Flags (FUSE, PRINFO).</summary>
        public byte flags;

        /// <summary>Reserved.</summary>
        public ushort rsvd1;

        /// <summary>Namespace ID (0 for controller-level commands).</summary>
        public uint nsid;

        /// <summary>Reserved (CDW2).</summary>
        public uint cdw2;

        /// <summary>Reserved (CDW3).</summary>
        public uint cdw3;

        /// <summary>Metadata pointer (physical address).</summary>
        public ulong metadata;

        /// <summary>Data pointer (physical address or virtual if kernel maps it).</summary>
        public ulong addr;

        /// <summary>Metadata length in bytes.</summary>
        public uint metadata_len;

        /// <summary>Data length in bytes.</summary>
        public uint data_len;

        /// <summary>Command dword 10.</summary>
        public uint cdw10;

        /// <summary>Command dword 11.</summary>
        public uint cdw11;

        /// <summary>Command dword 12.</summary>
        public uint cdw12;

        /// <summary>Command dword 13.</summary>
        public uint cdw13;

        /// <summary>Command dword 14.</summary>
        public uint cdw14;

        /// <summary>Command dword 15.</summary>
        public uint cdw15;

        /// <summary>Timeout in milliseconds.</summary>
        public uint timeout_ms;

        /// <summary>Result (completion queue DW0, returned by kernel).</summary>
        public uint result;
    }

    /// <summary>
    /// Opens a file descriptor for /dev/nvmeX device.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string pathname, int flags);

    /// <summary>
    /// Closes a file descriptor.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    /// <summary>
    /// Executes an ioctl command on a file descriptor.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, int request, ref nvme_admin_cmd arg);

    /// <summary>Open for read/write access.</summary>
    internal const int O_RDWR = 2;

    #endregion
}
