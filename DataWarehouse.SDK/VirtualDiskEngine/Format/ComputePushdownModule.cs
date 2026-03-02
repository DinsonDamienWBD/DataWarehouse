using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Execution-capability flags for a WASM predicate stored in a CPSH inode module.
/// </summary>
[Flags]
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: CPSH module -- Smart Extents WASM predicate flags (VOPT-37)")]
public enum ComputePushdownFlags : byte
{
    /// <summary>No special execution capability.</summary>
    None = 0,

    /// <summary>Bytecode is stored inline in the <see cref="ComputePushdownModule.InlinePredicate"/> field (max 32 bytes).</summary>
    InlinePredicate = 1,

    /// <summary>Predicate is safe for drive-side execution on a computational NVMe device (e.g. Samsung SmartSSD).</summary>
    ComputationalNvme = 2,

    /// <summary>Host-side io_uring filtered read: WASM executes after DMA, before user-space copy.</summary>
    IoUringFiltered = 4,

    /// <summary>Hint to JIT-compile (precompile) this predicate at mount time to amortise startup cost.</summary>
    PrecompileHint = 8,
}

/// <summary>
/// 48-byte inode Module Overflow Block entry for the Compute Pushdown (CPSH) module.
/// Stores a WASM predicate descriptor that enables the VDE engine to push filter logic
/// to the storage layer, eliminating unnecessary data movement.
/// </summary>
/// <remarks>
/// On-disk layout (all fields little-endian):
/// <code>
/// [WasmPredicateOffset : 8 bytes]
/// [PredicateLen        : 4 bytes]
/// [PredicateFlags      : 4 bytes]   (byte flag + 3 reserved bytes for alignment)
/// [InlinePredicate     : 32 bytes]
/// ─────────────────────────────────
/// Total                : 48 bytes
/// </code>
///
/// When <see cref="ComputePushdownFlags.InlinePredicate"/> is set and
/// <see cref="PredicateLen"/> is &lt;= 32, the WASM bytecode is stored directly in
/// <see cref="InlinePredicate"/> and <see cref="WasmPredicateOffset"/> is 0.
/// Otherwise <see cref="WasmPredicateOffset"/> addresses bytecode in the
/// <c>ComputeCodeCache</c> region.
///
/// Zero overhead when the CPSH bit is absent from the module manifest: the inode
/// simply contains no overflow block for this module.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: CPSH module data structure for Module Overflow Block (VOPT-37)")]
public readonly struct ComputePushdownModule
{
    // ── Layout constants ────────────────────────────────────────────────────

    /// <summary>Total serialized size in the Module Overflow Block: 48 bytes.</summary>
    public const int SerializedSize = 48;

    /// <summary>Bit position of CPSH in the module manifest (<see cref="ModuleId.ComputePushdown"/> = 19).</summary>
    public const byte ModuleBitPosition = 19;

    /// <summary>Maximum number of bytes that can be stored inline in <see cref="InlinePredicate"/>.</summary>
    public const int MaxInlinePredicateSize = 32;

    // ── Fields ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Block offset of the WASM bytecode in the <c>ComputeCodeCache</c> region.
    /// Zero when <see cref="ComputePushdownFlags.InlinePredicate"/> is set.
    /// </summary>
    public long WasmPredicateOffset { get; init; }

    /// <summary>Byte length of the WASM predicate bytecode.</summary>
    public int PredicateLen { get; init; }

    /// <summary>Execution-capability flags for this predicate.</summary>
    public ComputePushdownFlags PredicateFlags { get; init; }

    /// <summary>
    /// Up to 32 bytes of inline WASM bytecode.
    /// Valid only when <see cref="IsInline"/> is <see langword="true"/>.
    /// </summary>
    public byte[] InlinePredicate { get; init; }

    // ── Derived properties ──────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the predicate bytecode is stored in-line
    /// (i.e. <see cref="ComputePushdownFlags.InlinePredicate"/> is set and
    /// <see cref="PredicateLen"/> is within the 32-byte inline budget).
    /// </summary>
    public bool IsInline =>
        PredicateFlags.HasFlag(ComputePushdownFlags.InlinePredicate)
        && PredicateLen <= MaxInlinePredicateSize;

    // ── Serialization ───────────────────────────────────────────────────────

    /// <summary>
    /// Serializes this module to exactly <see cref="SerializedSize"/> bytes in
    /// <paramref name="buffer"/> using little-endian byte order.
    /// </summary>
    /// <param name="module">The module to serialize.</param>
    /// <param name="buffer">Target span; must be at least 48 bytes.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/>.
    /// </exception>
    public static void Serialize(in ComputePushdownModule module, Span<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes for ComputePushdownModule.",
                nameof(buffer));

        buffer.Slice(0, SerializedSize).Clear();

        // [0..7]  WasmPredicateOffset : int64 LE
        BinaryPrimitives.WriteInt64LittleEndian(buffer, module.WasmPredicateOffset);

        // [8..11] PredicateLen : int32 LE
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(8), module.PredicateLen);

        // [12]    PredicateFlags : byte  [13..15] reserved (zeroed)
        buffer[12] = (byte)module.PredicateFlags;

        // [16..47] InlinePredicate : 32 bytes (zeroed already; copy what we have)
        if (module.InlinePredicate is { Length: > 0 })
        {
            int copyLen = Math.Min(module.InlinePredicate.Length, MaxInlinePredicateSize);
            module.InlinePredicate.AsSpan(0, copyLen).CopyTo(buffer.Slice(16));
        }
    }

    /// <summary>
    /// Deserializes a <see cref="ComputePushdownModule"/> from exactly
    /// <see cref="SerializedSize"/> bytes using little-endian byte order.
    /// </summary>
    /// <param name="buffer">Source span; must be at least 48 bytes.</param>
    /// <returns>The deserialized module.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="SerializedSize"/>.
    /// </exception>
    public static ComputePushdownModule Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SerializedSize)
            throw new ArgumentException(
                $"Buffer must be at least {SerializedSize} bytes for ComputePushdownModule.",
                nameof(buffer));

        long wasmPredicateOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        int predicateLen         = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8));
        var predicateFlags       = (ComputePushdownFlags)buffer[12];
        byte[] inlinePredicate   = buffer.Slice(16, MaxInlinePredicateSize).ToArray();

        return new ComputePushdownModule
        {
            WasmPredicateOffset = wasmPredicateOffset,
            PredicateLen        = predicateLen,
            PredicateFlags      = predicateFlags,
            InlinePredicate     = inlinePredicate,
        };
    }
}
