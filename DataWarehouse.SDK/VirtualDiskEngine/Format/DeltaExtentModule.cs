using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Compaction policy controlling when delta chains are flattened into fresh base blocks.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-23: DELT compaction policy (VOPT-36)")]
public enum DeltaCompactionPolicy : uint
{
    /// <summary>
    /// Compact the delta chain when <see cref="DeltaExtentModule.CurrentDepth"/> exceeds
    /// <see cref="DeltaExtentModule.MaxDeltaDepth"/>. Minimises read amplification at the
    /// cost of earlier write amplification.
    /// </summary>
    Eager = 0,

    /// <summary>
    /// Compact when depth reaches 2x <see cref="DeltaExtentModule.MaxDeltaDepth"/>.
    /// Defers write amplification at the cost of deeper read chains.
    /// </summary>
    Lazy = 1,

    /// <summary>
    /// Trigger compaction on the next read that traverses a chain exceeding
    /// <see cref="DeltaExtentModule.MaxDeltaDepth"/>. Zero-background-I/O policy.
    /// </summary>
    OnRead = 2,

    /// <summary>
    /// Never compact automatically. Chains must be flattened by an explicit
    /// administrator or Vacuum command. Useful for audit trails that must preserve
    /// every delta.
    /// </summary>
    Manual = 3,
}

/// <summary>
/// An 8-byte inode module entry stored at fixed offset 0x1F2 (byte 498) inside the
/// DataOsModuleArea of an <see cref="ExtendedInode512"/>. Tracks the current depth
/// of a sub-block delta chain and the policy that governs compaction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>On-disk layout (8 bytes, little-endian):</strong>
/// <code>
///   [0..2)  MaxDeltaDepth     — ushort: maximum chain depth before compaction
///   [2..4)  CurrentDepth      — ushort: current delta chain depth for this inode
///   [4..8)  CompactionPolicy  — uint: DeltaCompactionPolicy
/// </code>
/// </para>
/// <para>
/// <strong>Module registration:</strong> DELT, bit 22 in the ModuleManifest.
/// Fixed inode offset <c>0x1F2</c> = byte 498.
/// </para>
/// <para>
/// <strong>Purpose:</strong> When a write modifies less than 10% of a 4 KB block, the
/// VDE generates a VCDIFF binary patch via <see cref="DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite.DeltaExtentPatcher"/>
/// instead of allocating a full 4 KB copy-on-write block. This module tracks the chain
/// depth so the background Vacuum knows when to flatten patches back into a clean base block.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-23: DELT inode module (VOPT-36, Module bit 22)")]
public readonly struct DeltaExtentModule
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Serialized size of this module entry in bytes.</summary>
    public const int SerializedSize = 8;

    /// <summary>
    /// Bit position in the ModuleManifest for the DELT module.
    /// The corresponding manifest bit is <c>1u &lt;&lt; ModuleBitPosition</c> = 0x00400000.
    /// </summary>
    public const byte ModuleBitPosition = 22;

    /// <summary>
    /// Fixed byte offset within the 512-byte inode DataOsModuleArea where this module
    /// is stored. <c>0x1F2</c> = 498 decimal.
    /// </summary>
    public const int InodeOffset = 0x1F2;

    // ── Fields ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum depth of the delta chain for this inode before the active compaction
    /// policy triggers flattening. Default is 8.
    /// </summary>
    public ushort MaxDeltaDepth { get; init; }

    /// <summary>
    /// Current number of pending delta patches in the chain. Incremented by the
    /// CoW write path; reset to zero after successful compaction.
    /// </summary>
    public ushort CurrentDepth { get; init; }

    /// <summary>Compaction policy applied to this inode's delta chain.</summary>
    public DeltaCompactionPolicy CompactionPolicy { get; init; }

    // ── Computed properties ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <see cref="CurrentDepth"/> exceeds
    /// <see cref="MaxDeltaDepth"/> and the policy is <see cref="DeltaCompactionPolicy.Eager"/>,
    /// indicating the background Vacuum should flatten the chain immediately.
    /// </summary>
    public bool NeedsCompaction =>
        CompactionPolicy == DeltaCompactionPolicy.Eager && CurrentDepth > MaxDeltaDepth;

    // ── Mutation helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns a new <see cref="DeltaExtentModule"/> with <see cref="CurrentDepth"/> incremented
    /// by one, representing the addition of a new delta patch to the chain.
    /// </summary>
    public DeltaExtentModule WithIncrementedDepth() => this with { CurrentDepth = (ushort)(CurrentDepth + 1) };

    /// <summary>
    /// Returns a new <see cref="DeltaExtentModule"/> with <see cref="CurrentDepth"/> reset to zero,
    /// representing a successful chain compaction where all patches were flattened into a base block.
    /// </summary>
    public DeltaExtentModule WithResetDepth() => this with { CurrentDepth = 0 };

    // ── Default instance ──────────────────────────────────────────────────────

    /// <summary>
    /// Default module state: depth limit of 8, no pending deltas, Eager compaction policy.
    /// </summary>
    public static DeltaExtentModule Default { get; } = new DeltaExtentModule
    {
        MaxDeltaDepth = 8,
        CurrentDepth = 0,
        CompactionPolicy = DeltaCompactionPolicy.Eager,
    };

    // ── Serialization ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="module"/> into <paramref name="buffer"/> using
    /// little-endian byte order.
    /// </summary>
    /// <param name="module">The module state to serialize.</param>
    /// <param name="buffer">
    /// Output span of at least <see cref="SerializedSize"/> bytes.
    /// Layout: [MaxDeltaDepth:2][CurrentDepth:2][CompactionPolicy:4].
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static void Serialize(in DeltaExtentModule module, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], module.MaxDeltaDepth);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], module.CurrentDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], (uint)module.CompactionPolicy);
    }

    /// <summary>
    /// Deserializes a <see cref="DeltaExtentModule"/> from <paramref name="buffer"/> using
    /// little-endian byte order.
    /// </summary>
    /// <param name="buffer">
    /// Input span of at least <see cref="SerializedSize"/> bytes.
    /// Layout: [MaxDeltaDepth:2][CurrentDepth:2][CompactionPolicy:4].
    /// </param>
    /// <returns>The deserialized module state.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/> bytes.
    /// </exception>
    public static DeltaExtentModule Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes.", nameof(buffer));

        ushort maxDeltaDepth = BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]);
        ushort currentDepth  = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]);
        var policy           = (DeltaCompactionPolicy)BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);

        return new DeltaExtentModule
        {
            MaxDeltaDepth    = maxDeltaDepth,
            CurrentDepth     = currentDepth,
            CompactionPolicy = policy,
        };
    }
}
