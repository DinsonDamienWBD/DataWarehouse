using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Pipeline;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Per-module handler contract for populating (write path) and extracting (read path)
/// inode extension fields. Each implementation handles exactly one <see cref="ModuleId"/>
/// and knows how to serialize/deserialize its module-specific bytes within the inode
/// extension area.
/// </summary>
/// <remarks>
/// <para>
/// On the write path, <see cref="PopulateAsync"/> is called by <see cref="ModulePopulatorStage"/>
/// with a pre-allocated buffer of exactly <see cref="FieldSizeBytes"/> bytes. The handler
/// reads from <see cref="VdePipelineContext.Properties"/> or <see cref="VdePipelineContext.DataBuffer"/>
/// to derive the field values and writes them into the buffer.
/// </para>
/// <para>
/// On the read path, <see cref="ExtractAsync"/> is called by <see cref="ModuleExtractorStage"/>
/// with a read-only slice of the inode extension area. The handler parses the bytes and
/// stores the results in <see cref="VdePipelineContext.Properties"/> and/or
/// <see cref="VdePipelineContext.ModuleFields"/> for downstream consumption.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Per-module field handler interface (VOPT-89)")]
public interface IModuleFieldHandler
{
    /// <summary>
    /// The module identifier this handler services. Must match exactly one
    /// <see cref="ModuleId"/> and correspond to a module definition in <see cref="ModuleRegistry"/>.
    /// </summary>
    ModuleId HandledModule { get; }

    /// <summary>
    /// Number of bytes this handler contributes to the inode extension area.
    /// Must match <see cref="VdeModule.InodeFieldBytes"/> for the handled module.
    /// A value of 0 indicates the module has no inode extension fields.
    /// </summary>
    int FieldSizeBytes { get; }

    /// <summary>
    /// Write path: populates this module's inode extension bytes into <paramref name="fieldBuffer"/>.
    /// The buffer is pre-allocated to exactly <see cref="FieldSizeBytes"/> bytes. The handler
    /// reads from <paramref name="context"/> to derive the field values (e.g., EKEY handler
    /// derives DEK and writes KeySlotId, QOS handler reads tenant policy and writes QoS class).
    /// </summary>
    /// <param name="context">The mutable pipeline context containing inode state, data buffer, and properties.</param>
    /// <param name="fieldBuffer">Pre-allocated buffer of exactly <see cref="FieldSizeBytes"/> bytes to write into.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>A task representing the asynchronous populate operation.</returns>
    Task PopulateAsync(VdePipelineContext context, Memory<byte> fieldBuffer, CancellationToken ct);

    /// <summary>
    /// Read path: extracts this module's inode extension bytes from <paramref name="fieldBuffer"/>
    /// into <see cref="VdePipelineContext.ModuleFields"/> and/or <see cref="VdePipelineContext.Properties"/>.
    /// The buffer contains exactly <see cref="FieldSizeBytes"/> bytes as stored on disk.
    /// </summary>
    /// <param name="context">The mutable pipeline context to store extracted values into.</param>
    /// <param name="fieldBuffer">Read-only buffer containing exactly <see cref="FieldSizeBytes"/> bytes from disk.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>A task representing the asynchronous extract operation.</returns>
    Task ExtractAsync(VdePipelineContext context, ReadOnlyMemory<byte> fieldBuffer, CancellationToken ct);
}

/// <summary>
/// Result of a per-module field handler operation (populate or extract).
/// Used for structured error reporting without exceptions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Module field handler result type (VOPT-89)")]
public readonly record struct ModuleFieldResult
{
    /// <summary>The module that was processed.</summary>
    public ModuleId Module { get; init; }

    /// <summary>Whether the operation completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if <see cref="Success"/> is false; null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result for the specified module.</summary>
    /// <param name="module">The module that was successfully processed.</param>
    /// <returns>A successful <see cref="ModuleFieldResult"/>.</returns>
    public static ModuleFieldResult Ok(ModuleId module) => new() { Module = module, Success = true };

    /// <summary>Creates a failure result for the specified module with an error message.</summary>
    /// <param name="module">The module that failed processing.</param>
    /// <param name="error">Description of what went wrong.</param>
    /// <returns>A failed <see cref="ModuleFieldResult"/>.</returns>
    public static ModuleFieldResult Fail(ModuleId module, string error) => new() { Module = module, Success = false, Error = error };
}
