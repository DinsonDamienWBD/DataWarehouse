using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Write-path pipeline stage that dispatches to registered per-module handlers
/// to populate inode extension fields. Implements Write Stage 2 (Module Populator)
/// from the VDE v2.1 specification (AD-53).
/// </summary>
/// <remarks>
/// <para>
/// For each active module in the manifest, the populator looks up a registered
/// <see cref="IModuleFieldHandler"/> and calls <see cref="IModuleFieldHandler.PopulateAsync"/>
/// with a pre-allocated buffer. If no handler is registered for a module, the stage
/// stores zero-filled bytes of the correct size as a safe default -- modules not yet
/// implemented get zeroed inode extension fields.
/// </para>
/// <para>
/// Modules whose bit is OFF in the manifest are never dispatched to, achieving
/// zero-overhead for disabled modules. The stage always executes (no module gate)
/// because it internally iterates the manifest and gates per-module.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Module Populator write stage (VOPT-89)")]
public sealed class ModulePopulatorStage : IVdeWriteStage
{
    private readonly IReadOnlyDictionary<ModuleId, IModuleFieldHandler> _handlers;

    /// <summary>
    /// Initializes a new <see cref="ModulePopulatorStage"/> with the given per-module handlers.
    /// </summary>
    /// <param name="handlers">Registered handlers keyed by module identifier. Only modules
    /// with a registered handler will have their fields populated via the handler; others
    /// receive zero-filled defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is null.</exception>
    public ModulePopulatorStage(IReadOnlyDictionary<ModuleId, IModuleFieldHandler> handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    /// <inheritdoc />
    public string StageName => "ModulePopulator";

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

    /// <inheritdoc />
    /// <remarks>
    /// Iterates all active modules in the manifest. For each module:
    /// <list type="bullet">
    /// <item>If a handler is registered, allocates a buffer of <see cref="IModuleFieldHandler.FieldSizeBytes"/>
    /// bytes and calls <see cref="IModuleFieldHandler.PopulateAsync"/>.</item>
    /// <item>If no handler is registered, stores zero-filled bytes of <see cref="VdeModule.InodeFieldBytes"/>
    /// size as a safe default.</item>
    /// </list>
    /// Results are stored in <see cref="VdePipelineContext.ModuleFields"/> keyed by <see cref="ModuleId"/>.
    /// </remarks>
    public async Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        var activeModules = ModuleRegistry.GetActiveModules(context.Manifest);

        for (int i = 0; i < activeModules.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var module = activeModules[i];

            if (_handlers.TryGetValue(module.Id, out var handler))
            {
                if (handler.FieldSizeBytes > 0)
                {
                    var buffer = new byte[handler.FieldSizeBytes];
                    await handler.PopulateAsync(context, buffer.AsMemory(), ct).ConfigureAwait(false);
                    context.ModuleFields[module.Id] = buffer;
                }
                else
                {
                    // Module has a handler but contributes zero inode bytes -- still record it
                    context.ModuleFields[module.Id] = ReadOnlyMemory<byte>.Empty;
                }

                context.StagesExecuted.Add($"ModulePopulator:{module.Name}");
            }
            else
            {
                // No handler registered -- store zero-filled bytes as safe default
                if (module.InodeFieldBytes > 0)
                {
                    context.ModuleFields[module.Id] = new byte[module.InodeFieldBytes];
                }
                else
                {
                    context.ModuleFields[module.Id] = ReadOnlyMemory<byte>.Empty;
                }

                context.StagesSkipped.Add($"ModulePopulator:{module.Name}(no handler)");
            }
        }

        context.StagesExecuted.Add(StageName);
    }
}
