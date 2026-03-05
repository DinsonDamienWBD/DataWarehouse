using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Read-path pipeline stage that dispatches to registered per-module handlers
/// to extract inode extension fields into rich metadata. Implements Read Stage 5
/// (Module Extractor) from the VDE v2.1 specification (AD-53).
/// </summary>
/// <remarks>
/// <para>
/// For each active module in the manifest, the extractor calculates the cumulative
/// byte offset within the inode extension area (modules are packed in bit order),
/// slices the corresponding bytes, and dispatches to the registered handler for
/// parsing. If no handler is registered, the raw bytes are still stored in
/// <see cref="VdePipelineContext.ModuleFields"/> for downstream access.
/// </para>
/// <para>
/// Modules whose bit is OFF in the manifest are never dispatched to, achieving
/// zero-overhead for disabled modules. The inode extension area is obtained from
/// <c>context.Properties["InodeExtensionBytes"]</c>, set by a prior InodeLookup stage.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Module Extractor read stage (VOPT-89)")]
public sealed class ModuleExtractorStage : IVdeReadStage
{
    private readonly IReadOnlyDictionary<ModuleId, IModuleFieldHandler> _handlers;

    /// <summary>
    /// Initializes a new <see cref="ModuleExtractorStage"/> with the given per-module handlers.
    /// </summary>
    /// <param name="handlers">Registered handlers keyed by module identifier. Modules without
    /// a registered handler will have their raw bytes stored in the context but not parsed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is null.</exception>
    public ModuleExtractorStage(IReadOnlyDictionary<ModuleId, IModuleFieldHandler> handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    /// <inheritdoc />
    public string StageName => "ModuleExtractor";

    /// <inheritdoc />
    /// <remarks>
    /// Always null -- this stage always executes because it internally iterates the
    /// manifest and dispatches per-module. Module gating is handled within
    /// <see cref="ExecuteAsync"/>.
    /// </remarks>
    public ModuleId? ModuleGate => null;

    /// <summary>
    /// The registered per-module handlers available to this stage.
    /// </summary>
    public IReadOnlyDictionary<ModuleId, IModuleFieldHandler> Handlers => _handlers;

    /// <summary>
    /// Well-known property key for the inode extension area bytes, expected to be set
    /// by the InodeLookup stage before this stage executes.
    /// </summary>
    public const string InodeExtensionBytesKey = "InodeExtensionBytes";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Calculates cumulative field offsets for each active module in bit order
    /// (the on-disk layout packs module fields sequentially by ascending module bit).
    /// For each active module:
    /// </para>
    /// <list type="number">
    /// <item>Slices <c>InodeExtensionBytes</c> at the computed offset for
    /// <see cref="VdeModule.InodeFieldBytes"/> bytes.</item>
    /// <item>Stores the raw bytes in <see cref="VdePipelineContext.ModuleFields"/>.</item>
    /// <item>If a handler is registered, calls <see cref="IModuleFieldHandler.ExtractAsync"/>
    /// to parse the bytes and populate <see cref="VdePipelineContext.Properties"/>.</item>
    /// </list>
    /// </remarks>
    public async Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        var activeModules = ModuleRegistry.GetActiveModules(context.Manifest);

        // Get the inode extension area from a prior stage (InodeLookup).
        // If not available, use an empty buffer -- modules with fields will get empty slices.
        ReadOnlyMemory<byte> extensionArea = context.TryGetProperty<ReadOnlyMemory<byte>>(InodeExtensionBytesKey, out var area)
            ? area
            : ReadOnlyMemory<byte>.Empty;

        int offset = 0;

        for (int i = 0; i < activeModules.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var module = activeModules[i];
            int fieldBytes = module.InodeFieldBytes;

            // Slice the inode extension area for this module's bytes
            ReadOnlyMemory<byte> fieldBuffer;
            if (fieldBytes > 0 && offset + fieldBytes <= extensionArea.Length)
            {
                fieldBuffer = extensionArea.Slice(offset, fieldBytes);
            }
            else if (fieldBytes > 0)
            {
                // Extension area is shorter than expected -- use zero-filled bytes
                // (defensive: handles truncated or legacy inodes gracefully)
                fieldBuffer = new byte[fieldBytes];
            }
            else
            {
                fieldBuffer = ReadOnlyMemory<byte>.Empty;
            }

            // Always store raw bytes for downstream access
            context.ModuleFields[module.Id] = fieldBuffer;

            if (_handlers.TryGetValue(module.Id, out var handler))
            {
                await handler.ExtractAsync(context, fieldBuffer, ct).ConfigureAwait(false);
                context.StagesExecuted.Add($"ModuleExtractor:{module.Name}");
            }
            else
            {
                // No handler -- raw bytes are still available in ModuleFields
                context.StagesSkipped.Add($"ModuleExtractor:{module.Name}(no handler)");
            }

            offset += fieldBytes;
        }

        context.StagesExecuted.Add(StageName);
    }
}
