using System.Numerics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Result of an inode size calculation, including the final aligned size, breakdown
/// of core/module/padding bytes, and the per-module field layout within the inode.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 inode size result (VDE2-05)")]
public readonly record struct InodeSizeResult
{
    /// <summary>Final aligned inode size in bytes (always a multiple of 64).</summary>
    public int InodeSize { get; init; }

    /// <summary>Core inode bytes before module extension area (always 304).</summary>
    public int CoreBytes { get; init; }

    /// <summary>Sum of inode field bytes from all active modules.</summary>
    public int ModuleFieldBytes { get; init; }

    /// <summary>Padding bytes appended to reach alignment (InodeSize - CoreBytes - ModuleFieldBytes).</summary>
    public int PaddingBytes { get; init; }

    /// <summary>
    /// Per-module field layout: module identifier, byte offset within the inode, and field size.
    /// Sorted by module bit position (ascending).
    /// </summary>
    public IReadOnlyList<(ModuleId Module, int Offset, int Size)> ModuleFieldLayout { get; init; }
}

/// <summary>
/// Calculates the variable inode size for any combination of active modules.
/// The inode core is always <see cref="FormatConstants.InodeCoreSize"/> (304) bytes.
/// Active modules contribute additional inode field bytes. The final size is rounded
/// up to the next multiple of <see cref="FormatConstants.InodeAlignmentMultiple"/> (64).
/// </summary>
/// <remarks>
/// Spec size examples:
/// <list type="bullet">
///   <item>No modules: 304 raw -> 320 aligned (16 padding)</item>
///   <item>SEC+TAGS+REPL+INTL: 304+180=484 raw -> 512 aligned (28 padding)</item>
///   <item>SEC+CMPL+RAID+CMPR+QURY: 304+48=352 raw -> 384 aligned (32 padding)</item>
///   <item>All 19 modules: 304+219=523 raw -> 576 aligned (53 padding)</item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 inode size calculator (VDE2-05)")]
public static class InodeSizeCalculator
{
    /// <summary>
    /// Calculates the inode size and field layout for the given module manifest.
    /// </summary>
    /// <param name="moduleManifest">32-bit module manifest (each set bit activates a module).</param>
    /// <returns>Complete size result with layout details.</returns>
    public static InodeSizeResult Calculate(uint moduleManifest)
    {
        int coreBytes = FormatConstants.InodeCoreSize;
        int runningOffset = coreBytes;
        int moduleFieldBytes = 0;
        var layout = new List<(ModuleId Module, int Offset, int Size)>(BitOperations.PopCount(moduleManifest));

        // Iterate active modules in ModuleId order (bit position ascending)
        uint bits = moduleManifest;
        while (bits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(bits);
            if (bit < FormatConstants.DefinedModules)
            {
                var module = ModuleRegistry.GetModule((ModuleId)bit);
                if (module.InodeFieldBytes > 0)
                {
                    layout.Add(((ModuleId)bit, runningOffset, module.InodeFieldBytes));
                    runningOffset += module.InodeFieldBytes;
                    moduleFieldBytes += module.InodeFieldBytes;
                }
            }
            bits &= bits - 1; // clear lowest set bit
        }

        int raw = coreBytes + moduleFieldBytes;
        int aligned = AlignUp(raw, FormatConstants.InodeAlignmentMultiple);
        int padding = aligned - raw;

        return new InodeSizeResult
        {
            InodeSize = aligned,
            CoreBytes = coreBytes,
            ModuleFieldBytes = moduleFieldBytes,
            PaddingBytes = padding,
            ModuleFieldLayout = layout,
        };
    }

    /// <summary>
    /// Calculates the minimal inode size (no modules active). Returns 320 bytes.
    /// </summary>
    public static InodeSizeResult CalculateMinimal() => Calculate(0x00000000u);

    /// <summary>
    /// Calculates the maximal inode size (all 19 modules active). Returns 576 bytes.
    /// </summary>
    public static InodeSizeResult CalculateMaximal() => Calculate(0x0007FFFFu);

    /// <summary>
    /// Checks whether a module's inode fields can fit in the current padding without
    /// requiring an inode migration (size change).
    /// </summary>
    /// <param name="currentPadding">Current padding bytes at the end of the inode.</param>
    /// <param name="module">The module to check.</param>
    /// <returns>True if the module's inode bytes fit within the current padding.</returns>
    public static bool CanAddModuleWithoutMigration(int currentPadding, ModuleId module)
    {
        var moduleDef = ModuleRegistry.GetModule(module);
        return moduleDef.InodeFieldBytes <= currentPadding;
    }

    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of <paramref name="alignment"/>.
    /// </summary>
    private static int AlignUp(int value, int alignment)
        => ((value + alignment - 1) / alignment) * alignment;
}
