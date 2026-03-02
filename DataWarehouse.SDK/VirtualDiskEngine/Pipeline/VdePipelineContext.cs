using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

/// <summary>
/// Mutable context bag carried through all stages of a VDE I/O pipeline.
/// Each stage reads from and writes to this context, propagating inode metadata,
/// module manifest, data buffers, extent lists, and transformation flags across
/// the entire pipeline chain.
/// </summary>
/// <remarks>
/// <para>
/// The context is created once at pipeline entry and flows through every stage
/// sequentially. Stages add their results (e.g., resolved inode, allocated extents,
/// computed integrity hashes) so that downstream stages can consume them.
/// </para>
/// <para>
/// The <see cref="Properties"/> dictionary provides an open-ended extension point
/// for cross-stage communication without requiring context schema changes
/// (e.g., compression algorithm name, encryption IV, QoS class).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE I/O pipeline context (VOPT-88)")]
public sealed class VdePipelineContext
{
    /// <summary>
    /// Initializes a new pipeline context with all collections pre-allocated
    /// and <see cref="StartedAt"/> set to the current UTC time.
    /// </summary>
    public VdePipelineContext()
    {
        ModuleFields = new Dictionary<ModuleId, ReadOnlyMemory<byte>>();
        Extents = new List<InodeExtent>();
        Properties = new Dictionary<string, object>(StringComparer.Ordinal);
        StagesExecuted = new List<string>();
        StagesSkipped = new List<string>();
        StartedAt = DateTimeOffset.UtcNow;
    }

    // ── Pipeline identity ───────────────────────────────────────────────

    /// <summary>Whether this pipeline is executing a write or read operation.</summary>
    public PipelineDirection Direction { get; set; }

    /// <summary>UTC timestamp when this pipeline execution started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Propagated cancellation token for cooperative cancellation.</summary>
    public CancellationToken CancellationToken { get; init; }

    // ── Module manifest and config ──────────────────────────────────────

    /// <summary>
    /// Active modules for this operation. Used by the pipeline orchestrator
    /// to gate stage execution: stages whose <see cref="IVdePipelineStage.ModuleGate"/>
    /// bit is OFF in this manifest are skipped.
    /// </summary>
    public ModuleManifestField Manifest { get; set; }

    /// <summary>Per-module configuration levels.</summary>
    public ModuleConfigField ModuleConfig { get; set; }

    // ── Inode state ─────────────────────────────────────────────────────

    /// <summary>Target inode number for this I/O operation.</summary>
    public long InodeNumber { get; set; }

    /// <summary>
    /// Resolved inode structure. Set by InodeResolver (write) or InodeLookup (read).
    /// Null until the resolver stage executes.
    /// </summary>
    public ExtendedInode512? Inode { get; set; }

    // ── Data payload ────────────────────────────────────────────────────

    /// <summary>
    /// Data buffer being written or read. On write, this is the user-supplied
    /// payload; on read, this is populated by the BlockReader stage.
    /// </summary>
    public Memory<byte> DataBuffer { get; set; }

    /// <summary>VDE block size in bytes (immutable per-volume).</summary>
    public int BlockSize { get; init; }

    // ── Module extension fields ─────────────────────────────────────────

    /// <summary>
    /// Per-module inode extension fields. Populated by ModulePopulator (write)
    /// or ModuleExtractor (read). Keyed by <see cref="ModuleId"/>.
    /// </summary>
    public Dictionary<ModuleId, ReadOnlyMemory<byte>> ModuleFields { get; }

    // ── Extent management ───────────────────────────────────────────────

    /// <summary>
    /// Extent list for this operation. Populated by ExtentAllocator (write)
    /// or ExtentResolver (read).
    /// </summary>
    public List<InodeExtent> Extents { get; }

    // ── Transformation flags ────────────────────────────────────────────

    /// <summary>Whether the data buffer has been compressed by a DataTransformer stage.</summary>
    public bool IsCompressed { get; set; }

    /// <summary>Whether the data buffer has been encrypted by a DataTransformer stage.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Whether this operation uses delta (sub-block) extents.</summary>
    public bool IsDelta { get; set; }

    // ── Open-ended property bag ─────────────────────────────────────────

    /// <summary>
    /// Open-ended property bag for cross-stage communication. Stages can store
    /// arbitrary keyed values (e.g., compression algorithm, encryption IV, QoS class,
    /// RAID parity buffers) without requiring changes to the context schema.
    /// </summary>
    public Dictionary<string, object> Properties { get; }

    /// <summary>
    /// Gets a typed property from the property bag.
    /// </summary>
    /// <typeparam name="T">The expected type of the property value.</typeparam>
    /// <param name="key">The property key.</param>
    /// <returns>The property value cast to <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The key does not exist in the property bag.
    /// </exception>
    /// <exception cref="InvalidCastException">
    /// The value cannot be cast to <typeparamref name="T"/>.
    /// </exception>
    public T GetProperty<T>(string key)
    {
        if (!Properties.TryGetValue(key, out var value))
            throw new InvalidOperationException(
                $"Pipeline property '{key}' not found. Ensure the producing stage has executed before the consuming stage.");

        return (T)value;
    }

    /// <summary>
    /// Sets a typed property in the property bag, overwriting any existing value.
    /// </summary>
    /// <typeparam name="T">The type of the property value.</typeparam>
    /// <param name="key">The property key.</param>
    /// <param name="value">The property value.</param>
    public void SetProperty<T>(string key, T value)
    {
        Properties[key] = value!;
    }

    /// <summary>
    /// Attempts to get a typed property from the property bag.
    /// </summary>
    /// <typeparam name="T">The expected type of the property value.</typeparam>
    /// <param name="key">The property key.</param>
    /// <param name="value">When this method returns, contains the property value if found and castable; otherwise, the default value.</param>
    /// <returns>True if the property was found and successfully cast; otherwise, false.</returns>
    public bool TryGetProperty<T>(string key, out T value)
    {
        if (Properties.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    // ── Audit trail ─────────────────────────────────────────────────────

    /// <summary>
    /// Names of stages that executed successfully, in execution order.
    /// </summary>
    public List<string> StagesExecuted { get; }

    /// <summary>
    /// Names of stages that were skipped because their module gate bit was OFF.
    /// </summary>
    public List<string> StagesSkipped { get; }
}
