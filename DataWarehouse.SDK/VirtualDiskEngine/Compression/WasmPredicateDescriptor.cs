using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compression;

/// <summary>
/// Preferred execution mode for a WASM predicate during a filtered read.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: CPSH filter execution mode (VOPT-37)")]
public enum FilterExecutionMode : byte
{
    /// <summary>
    /// Host-side io_uring filter: WASM executes after DMA but before the user-space copy,
    /// saving memory bandwidth on standard NVMe drives.
    /// </summary>
    HostSidePostDma = 0,

    /// <summary>
    /// Drive-side execution: the computational NVMe device's ARM processor executes WASM
    /// directly on flash (e.g. Samsung SmartSSD), eliminating host-side DMA entirely.
    /// </summary>
    DriveSideExecution = 1,

    /// <summary>
    /// Predicate execution is disabled (e.g. flags not set or hardware unavailable).
    /// Reads fall back to the non-filtered path with zero overhead.
    /// </summary>
    Disabled = 2,
}

/// <summary>
/// Resolves and manages WASM predicate bytecode for Smart Extent filtered reads.
/// Wraps a <see cref="ComputePushdownModule"/> to provide typed access to either
/// inline bytecode or an external <c>ComputeCodeCache</c> block reference.
/// </summary>
/// <remarks>
/// Use the static factory methods <see cref="CreateInline"/> and
/// <see cref="CreateExternal"/> to create <see cref="ComputePushdownModule"/> values
/// that this descriptor can then resolve at read time.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: WASM predicate descriptor for io_uring and computational NVMe (VOPT-37)")]
public sealed class WasmPredicateDescriptor
{
    private readonly ComputePushdownModule _module;

    /// <summary>
    /// Initialises a new descriptor wrapping <paramref name="module"/>.
    /// </summary>
    /// <param name="module">The CPSH inode module to wrap.</param>
    public WasmPredicateDescriptor(ComputePushdownModule module)
    {
        _module = module;
    }

    // ── Read-side properties ─────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the WASM bytecode is stored inline in the inode
    /// (i.e. the predicate fits within the 32-byte inline budget).
    /// </summary>
    public bool IsInline => _module.IsInline;

    /// <summary>
    /// Returns <see langword="true"/> when the predicate is declared safe for execution on a
    /// computational NVMe device (e.g. Samsung SmartSSD).
    /// </summary>
    public bool IsComputationalNvmeCapable =>
        _module.PredicateFlags.HasFlag(ComputePushdownFlags.ComputationalNvme);

    /// <summary>Byte length of the WASM predicate bytecode.</summary>
    public int BytecodeLength => _module.PredicateLen;

    /// <summary>
    /// Returns the inline WASM bytecode as a <see cref="ReadOnlyMemory{T}"/> view.
    /// </summary>
    /// <returns>The inline bytecode slice (length equals <see cref="BytecodeLength"/>).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="IsInline"/> is <see langword="false"/>;
    /// call <see cref="GetExternalBlockOffset"/> instead.
    /// </exception>
    public ReadOnlyMemory<byte> GetInlineBytecode()
    {
        if (!IsInline)
            throw new InvalidOperationException(
                "Predicate is not inline. Use GetExternalBlockOffset() to locate bytecode in the ComputeCodeCache region.");

        int len = Math.Min(_module.PredicateLen, ComputePushdownModule.MaxInlinePredicateSize);
        return _module.InlinePredicate.AsMemory(0, len);
    }

    /// <summary>
    /// Returns the block offset of the WASM bytecode in the <c>ComputeCodeCache</c> region.
    /// </summary>
    /// <returns>Block offset (absolute, within the VDE).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="IsInline"/> is <see langword="true"/>;
    /// call <see cref="GetInlineBytecode"/> instead.
    /// </exception>
    public long GetExternalBlockOffset()
    {
        if (IsInline)
            throw new InvalidOperationException(
                "Predicate is inline. Use GetInlineBytecode() to access the bytecode directly.");

        return _module.WasmPredicateOffset;
    }

    /// <summary>
    /// Returns the recommended <see cref="FilterExecutionMode"/> based on the predicate's
    /// capability flags and the availability of a computational NVMe device.
    /// </summary>
    /// <param name="hasComputationalNvme">
    /// <see langword="true"/> if the underlying storage device supports drive-side
    /// WASM execution (e.g. Samsung SmartSSD with ARM co-processor).
    /// </param>
    /// <returns>
    /// <see cref="FilterExecutionMode.DriveSideExecution"/> when both the predicate flags
    /// and the hardware support it; <see cref="FilterExecutionMode.HostSidePostDma"/> when
    /// <see cref="ComputePushdownFlags.IoUringFiltered"/> is set; otherwise
    /// <see cref="FilterExecutionMode.Disabled"/>.
    /// </returns>
    public FilterExecutionMode GetRecommendedMode(bool hasComputationalNvme)
    {
        if (hasComputationalNvme && IsComputationalNvmeCapable)
            return FilterExecutionMode.DriveSideExecution;

        if (_module.PredicateFlags.HasFlag(ComputePushdownFlags.IoUringFiltered))
            return FilterExecutionMode.HostSidePostDma;

        return FilterExecutionMode.Disabled;
    }

    // ── Static factory methods ───────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="ComputePushdownModule"/> with bytecode stored inline in the inode.
    /// </summary>
    /// <param name="wasmBytecode">
    /// The WASM filter bytecode. Must be &lt;= <see cref="ComputePushdownModule.MaxInlinePredicateSize"/> (32) bytes.
    /// </param>
    /// <param name="flags">
    /// Execution flags. <see cref="ComputePushdownFlags.InlinePredicate"/> is always OR-ed in.
    /// </param>
    /// <returns>A new <see cref="ComputePushdownModule"/> with the bytecode stored inline.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="wasmBytecode"/> exceeds 32 bytes.
    /// </exception>
    public static ComputePushdownModule CreateInline(
        ReadOnlySpan<byte> wasmBytecode,
        ComputePushdownFlags flags)
    {
        if (wasmBytecode.Length > ComputePushdownModule.MaxInlinePredicateSize)
            throw new ArgumentException(
                $"Inline WASM bytecode must be <= {ComputePushdownModule.MaxInlinePredicateSize} bytes. " +
                $"Got {wasmBytecode.Length} bytes. Use CreateExternal() for larger predicates.",
                nameof(wasmBytecode));

        byte[] inline = new byte[ComputePushdownModule.MaxInlinePredicateSize];
        wasmBytecode.CopyTo(inline);

        return new ComputePushdownModule
        {
            WasmPredicateOffset = 0L,
            PredicateLen        = wasmBytecode.Length,
            PredicateFlags      = flags | ComputePushdownFlags.InlinePredicate,
            InlinePredicate     = inline,
        };
    }

    /// <summary>
    /// Creates a <see cref="ComputePushdownModule"/> that references WASM bytecode stored in
    /// the <c>ComputeCodeCache</c> region at <paramref name="blockOffset"/>.
    /// </summary>
    /// <param name="blockOffset">Absolute block offset of the bytecode in the ComputeCodeCache region.</param>
    /// <param name="bytecodeLen">Byte length of the WASM predicate.</param>
    /// <param name="flags">
    /// Execution flags. <see cref="ComputePushdownFlags.InlinePredicate"/> must NOT be set;
    /// it is cleared automatically.
    /// </param>
    /// <returns>A new <see cref="ComputePushdownModule"/> referencing external bytecode.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockOffset"/> is negative or <paramref name="bytecodeLen"/> is &lt;= 0.
    /// </exception>
    public static ComputePushdownModule CreateExternal(
        long blockOffset,
        int bytecodeLen,
        ComputePushdownFlags flags)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockOffset);
        if (bytecodeLen <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(bytecodeLen), bytecodeLen,
                "Bytecode length must be positive.");

        // External predicates must never have the InlinePredicate flag set.
        ComputePushdownFlags externalFlags = flags & ~ComputePushdownFlags.InlinePredicate;

        return new ComputePushdownModule
        {
            WasmPredicateOffset = blockOffset,
            PredicateLen        = bytecodeLen,
            PredicateFlags      = externalFlags,
            InlinePredicate     = new byte[ComputePushdownModule.MaxInlinePredicateSize],
        };
    }
}
