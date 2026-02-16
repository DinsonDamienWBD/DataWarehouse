using DataWarehouse.SDK.Contracts;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Hardware.NVMe;

/// <summary>
/// NVMe admin commands (opcodes).
/// </summary>
/// <remarks>
/// <para>
/// Admin commands operate on the NVMe controller (namespace ID = 0) and provide management
/// operations like identify, firmware updates, log retrieval, and namespace formatting.
/// </para>
/// <para>
/// For complete NVMe command set, see NVMe Base Specification 2.0 or later.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NVMe admin commands (HW-07)")]
public enum NvmeAdminCommand : byte
{
    /// <summary>Identify controller or namespace.</summary>
    /// <remarks>
    /// Returns controller or namespace identification data structure (4096 bytes).
    /// Used to discover controller capabilities, namespace size, LBA format, etc.
    /// </remarks>
    Identify = 0x06,

    /// <summary>Get log page.</summary>
    /// <remarks>
    /// Retrieves log pages like SMART/health info, error log, firmware slot info, etc.
    /// Log page ID specified in command dwords (CDW10).
    /// </remarks>
    GetLogPage = 0x02,

    /// <summary>Firmware commit.</summary>
    /// <remarks>
    /// Activates a firmware image previously downloaded to a firmware slot.
    /// May require controller reset to take effect.
    /// </remarks>
    FirmwareCommit = 0x10,

    /// <summary>Firmware image download.</summary>
    /// <remarks>
    /// Downloads a firmware image to the controller. Image is transferred in chunks
    /// (typically 4KB per command). After download, use FirmwareCommit to activate.
    /// </remarks>
    FirmwareDownload = 0x11,

    /// <summary>Format NVM.</summary>
    /// <remarks>
    /// Low-level format of NVMe namespace. WARNING: Destroys all data on the namespace.
    /// Used to change LBA format, enable/disable metadata, or secure erase.
    /// </remarks>
    FormatNvm = 0x80
}

/// <summary>
/// NVMe I/O commands (opcodes).
/// </summary>
/// <remarks>
/// <para>
/// I/O commands operate on NVMe namespaces (namespace ID > 0) and provide data transfer
/// operations like read, write, TRIM, and compare.
/// </para>
/// <para>
/// For complete NVMe command set, see NVMe Base Specification 2.0 or later.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NVMe I/O commands (HW-07)")]
public enum NvmeIoCommand : byte
{
    /// <summary>Read data from namespace.</summary>
    /// <remarks>
    /// Reads logical blocks from the specified LBA range. Data is transferred from
    /// the device to host memory (DMA).
    /// </remarks>
    Read = 0x02,

    /// <summary>Write data to namespace.</summary>
    /// <remarks>
    /// Writes logical blocks to the specified LBA range. Data is transferred from
    /// host memory to the device (DMA).
    /// </remarks>
    Write = 0x01,

    /// <summary>Dataset management (TRIM).</summary>
    /// <remarks>
    /// Notifies the device that logical blocks are no longer in use (TRIM/UNMAP).
    /// Allows SSD to reclaim blocks for garbage collection, improving write performance.
    /// </remarks>
    DatasetManagement = 0x09,

    /// <summary>Write zeros.</summary>
    /// <remarks>
    /// Writes zeros to the specified LBA range without transferring data (device generates zeros).
    /// More efficient than writing zeros from host memory.
    /// </remarks>
    WriteZeroes = 0x08,

    /// <summary>Compare data.</summary>
    /// <remarks>
    /// Compares data on the device with data in host memory. Returns success if data matches,
    /// error if data differs. Used for data integrity verification.
    /// </remarks>
    Compare = 0x05
}

/// <summary>
/// NVMe command submission structure (simplified for Phase 35).
/// </summary>
/// <remarks>
/// <para>
/// Full NVMe command is 64 bytes with CDW0-CDW15 (16 dwords). Phase 35 provides
/// common fields only. Production implementation needs full 64-byte structure for
/// advanced commands (metadata, SGL, etc.).
/// </para>
/// <para>
/// For complete command structure, see NVMe Base Specification 2.0, Figure 87 (Command Format).
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SdkCompatibility("3.0.0", Notes = "Phase 35: NVMe command structure (HW-07)")]
public struct NvmeCommandPacket
{
    /// <summary>Command opcode (CDW0 bits 0-7).</summary>
    public byte Opcode;

    /// <summary>Flags (CDW0 bits 8-15): FUSE, PRINFO, etc.</summary>
    public byte Flags;

    /// <summary>Command identifier (CDW0 bits 16-31).</summary>
    /// <remarks>
    /// Unique identifier for this command submission. Used to match completion queue entries
    /// to submitted commands. Assigned by host.
    /// </remarks>
    public ushort CommandId;

    /// <summary>Namespace identifier (CDW1).</summary>
    /// <remarks>
    /// Identifies the namespace for I/O commands. 0 for admin commands or controller-level operations.
    /// </remarks>
    public uint NamespaceId;

    /// <summary>Reserved (CDW2-3).</summary>
    public ulong Reserved1;

    /// <summary>Metadata pointer (CDW4-5).</summary>
    /// <remarks>
    /// Physical address of metadata buffer (if namespace has metadata). Not used for most commands.
    /// </remarks>
    public ulong MetadataPointer;

    /// <summary>Data pointer (CDW6-7): physical address or PRP/SGL.</summary>
    /// <remarks>
    /// Physical memory address for data transfer. For simple transfers, this is the PRP entry.
    /// For complex transfers, this points to a PRP list or SGL.
    /// </remarks>
    public ulong DataPointer;

    /// <summary>Command dwords 10-15 (command-specific parameters).</summary>
    /// <remarks>
    /// Command-specific parameters. Layout varies by command opcode.
    /// Examples:
    /// - Read/Write: CDW10=start LBA low, CDW11=start LBA high, CDW12=block count
    /// - Identify: CDW10=controller/namespace structure (CNS field)
    /// </remarks>
    public unsafe fixed uint CommandDwords[6];
}

/// <summary>
/// NVMe command completion structure.
/// </summary>
/// <remarks>
/// <para>
/// Completion queue entry (16 bytes) returned by the device after command execution.
/// Provides command result, status, and submission queue head pointer.
/// </para>
/// <para>
/// For complete completion structure, see NVMe Base Specification 2.0, Figure 91 (Completion Queue Entry).
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SdkCompatibility("3.0.0", Notes = "Phase 35: NVMe completion structure (HW-07)")]
public struct NvmeCompletion
{
    /// <summary>Command-specific result (DW0).</summary>
    /// <remarks>
    /// Command-specific result value. For example, Identify command returns namespace ID count,
    /// Get Log Page returns log page data, etc.
    /// </remarks>
    public uint Result;

    /// <summary>Reserved (DW1).</summary>
    public uint Reserved;

    /// <summary>Submission queue head pointer (DW2 bits 0-15).</summary>
    /// <remarks>
    /// Updated head pointer for the submission queue. Used for flow control.
    /// </remarks>
    public ushort SqHead;

    /// <summary>Submission queue identifier (DW2 bits 16-31).</summary>
    /// <remarks>
    /// Identifies which submission queue this completion corresponds to.
    /// </remarks>
    public ushort SqId;

    /// <summary>Command identifier (DW3 bits 0-15).</summary>
    /// <remarks>
    /// Matches the CommandId from the submitted command. Used to correlate completions
    /// with commands.
    /// </remarks>
    public ushort CommandId;

    /// <summary>Status field (DW3 bits 16-31): phase, status code, status code type.</summary>
    /// <remarks>
    /// <para>
    /// Status field layout (16 bits):
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Bit 0: Phase Tag (P) — toggles for each completion queue wrap</description></item>
    ///   <item><description>Bits 1-15: Status (SC, SCT, CRD, M, DNR)</description></item>
    /// </list>
    /// <para>
    /// Status = 0x0000 indicates successful completion (no errors).
    /// Non-zero status indicates error (check status code type and status code).
    /// </para>
    /// </remarks>
    public ushort Status;
}
