using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;
using DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

/// <summary>
/// Assembles complete VDE write and read pipelines with all stages wired in the
/// correct spec order (AD-53). The builder accepts the core VDE dependencies
/// (block device, allocator, WAL, module handlers) and constructs pre-ordered
/// stage lists for <see cref="VdeWritePipeline"/> and <see cref="VdeReadPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// Write pipeline order (7 stages):
/// <list type="number">
///   <item>InodeResolver — allocate or lookup the target inode</item>
///   <item>ModulePopulator — populate per-module inode extension fields</item>
///   <item>TagIndexer — update tag indexes</item>
///   <item>DataTransformer — compress, encrypt, or delta-encode</item>
///   <item>ExtentAllocator — allocate physical extents</item>
///   <item>IntegrityCalculator — compute integrity hashes</item>
///   <item>WalBlockWriter — journal to WAL and write blocks</item>
/// </list>
/// </para>
/// <para>
/// Read pipeline order (6 stages):
/// <list type="number">
///   <item>InodeLookup — look up inode from ARC cache or device</item>
///   <item>AccessControl — verify security permissions</item>
///   <item>ExtentResolver — resolve extent tree to physical blocks</item>
///   <item>BlockReaderIntegrity — read blocks and verify integrity</item>
///   <item>ModuleExtractor — extract per-module inode extension fields</item>
///   <item>PostReadUpdates — update caches and statistics</item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE pipeline builder for E2E I/O path (VOPT-90)")]
public sealed class VdePipelineBuilder
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IWriteAheadLog _wal;
    private readonly IReadOnlyDictionary<ModuleId, IModuleFieldHandler> _moduleHandlers;

    /// <summary>
    /// Initializes a new <see cref="VdePipelineBuilder"/> with all required VDE dependencies.
    /// </summary>
    /// <param name="device">Block device for I/O operations.</param>
    /// <param name="allocator">Block allocator for inode and extent allocation.</param>
    /// <param name="wal">Write-ahead log for crash-consistent writes.</param>
    /// <param name="moduleHandlers">Per-module field handlers keyed by module identifier.</param>
    /// <exception cref="ArgumentNullException">Any parameter is null.</exception>
    public VdePipelineBuilder(
        IBlockDevice device,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        IReadOnlyDictionary<ModuleId, IModuleFieldHandler> moduleHandlers)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _wal = wal ?? throw new ArgumentNullException(nameof(wal));
        _moduleHandlers = moduleHandlers ?? throw new ArgumentNullException(nameof(moduleHandlers));
    }

    /// <summary>
    /// Builds a complete write pipeline with all 7 stages in AD-53 spec order.
    /// </summary>
    /// <returns>A new <see cref="VdeWritePipeline"/> with all stages wired.</returns>
    public VdeWritePipeline BuildWritePipeline()
    {
        var stages = new IVdeWriteStage[]
        {
            new InodeResolverStage(_allocator),         // Stage 1: Inode resolution
            new ModulePopulatorStage(_moduleHandlers),  // Stage 2: Module field population
            new TagIndexerStage(),                      // Stage 3: Tag indexing
            new DataTransformerStage(),                 // Stage 4: Data transformation
            new ExtentAllocatorStage(_allocator),       // Stage 5: Extent allocation
            new IntegrityCalculatorStage(),             // Stage 6: Integrity calculation
            new WalBlockWriterStage(_device, _wal),     // Stage 7: WAL journaling + block write
        };

        return new VdeWritePipeline(stages);
    }

    /// <summary>
    /// Builds a complete read pipeline with all 6 stages in AD-53 spec order.
    /// </summary>
    /// <returns>A new <see cref="VdeReadPipeline"/> with all stages wired.</returns>
    public VdeReadPipeline BuildReadPipeline()
    {
        var stages = new IVdeReadStage[]
        {
            new InodeLookupStage(_device),              // Stage 1: Inode lookup
            new AccessControlStage(),                   // Stage 2: Access control
            new ExtentResolverStage(_device),           // Stage 3: Extent resolution
            new BlockReaderIntegrityStage(_device),     // Stage 4: Block read + integrity
            new ModuleExtractorStage(_moduleHandlers),  // Stage 5: Module field extraction
            new PostReadUpdatesStage(),                 // Stage 6: Post-read updates
        };

        return new VdeReadPipeline(stages);
    }

    /// <summary>
    /// Factory method that creates a <see cref="VdePipelineBuilder"/> with an optional
    /// empty handler dictionary when no module handlers are registered.
    /// </summary>
    /// <param name="device">Block device for I/O operations.</param>
    /// <param name="allocator">Block allocator for inode and extent allocation.</param>
    /// <param name="wal">Write-ahead log for crash-consistent writes.</param>
    /// <param name="moduleHandlers">Per-module field handlers, or null for empty handler set.</param>
    /// <returns>A new <see cref="VdePipelineBuilder"/>.</returns>
    public static VdePipelineBuilder Create(
        IBlockDevice device,
        IBlockAllocator allocator,
        IWriteAheadLog wal,
        IReadOnlyDictionary<ModuleId, IModuleFieldHandler>? moduleHandlers = null)
    {
        return new VdePipelineBuilder(
            device,
            allocator,
            wal,
            moduleHandlers ?? new Dictionary<ModuleId, IModuleFieldHandler>());
    }
}
