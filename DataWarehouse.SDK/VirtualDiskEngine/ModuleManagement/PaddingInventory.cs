using System.Numerics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Result of analyzing whether a single inactive module can fit within the
/// current inode padding bytes without requiring an inode table migration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online module addition - padding fit result (VDEF-09)")]
public readonly record struct ModuleFitResult
{
    /// <summary>The candidate module being evaluated.</summary>
    public ModuleId Module { get; init; }

    /// <summary>Number of inode field bytes this module requires.</summary>
    public int RequiredBytes { get; init; }

    /// <summary>True if RequiredBytes fits within currently available padding.</summary>
    public bool FitsInPadding { get; init; }

    /// <summary>
    /// Padding bytes remaining after claiming this module's fields.
    /// May be negative if the module does not fit.
    /// </summary>
    public int RemainingPaddingAfter { get; init; }
}

/// <summary>
/// Complete analysis of the current inode padding state for a given module manifest,
/// including which inactive modules can be added without inode table migration.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online module addition - padding analysis (VDEF-09)")]
public readonly record struct PaddingAnalysis
{
    /// <summary>Current inode size in bytes (aligned to 64-byte boundary).</summary>
    public int CurrentInodeSize { get; init; }

    /// <summary>Current padding bytes at the end of each inode.</summary>
    public int CurrentPaddingBytes { get; init; }

    /// <summary>Total module field bytes currently in use.</summary>
    public int CurrentModuleFieldBytes { get; init; }

    /// <summary>Modules currently active in the manifest.</summary>
    public IReadOnlyList<ModuleId> ActiveModules { get; init; }

    /// <summary>Fit results for each inactive module that has inode fields.</summary>
    public IReadOnlyList<ModuleFitResult> FitResults { get; init; }
}

/// <summary>
/// Analyzes the inode padding of a VDE volume to determine which modules can be
/// added online by claiming existing padding bytes, without requiring an inode
/// table migration. All calculations are pure (no I/O), operating on the module
/// manifest value and <see cref="ModuleRegistry"/> lookups.
/// </summary>
/// <remarks>
/// When a VDE is created with a set of active modules, the inode size is aligned
/// up to the next 64-byte boundary, producing padding bytes at the end of each inode.
/// If a new module's inode field bytes fit within this padding, the module can be
/// activated by simply updating the <see cref="InodeLayoutDescriptor"/> in the
/// superblock -- no inode data needs to be rewritten because the zero-filled padding
/// bytes are a valid initial state for all module fields.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online module addition - padding inventory (VDEF-09)")]
public sealed class PaddingInventory
{
    private readonly uint _moduleManifest;

    /// <summary>
    /// Creates a new padding inventory for the given module manifest.
    /// </summary>
    /// <param name="moduleManifest">Current 32-bit module manifest from the superblock.</param>
    public PaddingInventory(uint moduleManifest)
    {
        _moduleManifest = moduleManifest;
    }

    /// <summary>
    /// Performs a complete padding analysis: calculates the current inode layout and
    /// evaluates every inactive module that has inode fields for padding fit.
    /// </summary>
    /// <returns>A <see cref="PaddingAnalysis"/> with full details.</returns>
    public PaddingAnalysis Analyze()
    {
        var sizeResult = InodeSizeCalculator.Calculate(_moduleManifest);

        // Collect active module IDs
        var activeModules = new List<ModuleId>(BitOperations.PopCount(_moduleManifest));
        uint activeBits = _moduleManifest;
        while (activeBits != 0)
        {
            int bit = BitOperations.TrailingZeroCount(activeBits);
            if (bit < FormatConstants.DefinedModules)
                activeModules.Add((ModuleId)bit);
            activeBits &= activeBits - 1;
        }

        // Evaluate each inactive module with inode fields
        var fitResults = new List<ModuleFitResult>();
        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            var moduleId = (ModuleId)i;

            // Skip active modules
            if ((_moduleManifest & (1u << i)) != 0)
                continue;

            var moduleDef = ModuleRegistry.GetModule(moduleId);

            // Skip modules with no inode fields
            if (moduleDef.InodeFieldBytes == 0)
                continue;

            bool fits = InodeSizeCalculator.CanAddModuleWithoutMigration(
                sizeResult.PaddingBytes, moduleId);
            int remaining = sizeResult.PaddingBytes - moduleDef.InodeFieldBytes;

            fitResults.Add(new ModuleFitResult
            {
                Module = moduleId,
                RequiredBytes = moduleDef.InodeFieldBytes,
                FitsInPadding = fits,
                RemainingPaddingAfter = remaining,
            });
        }

        return new PaddingAnalysis
        {
            CurrentInodeSize = sizeResult.InodeSize,
            CurrentPaddingBytes = sizeResult.PaddingBytes,
            CurrentModuleFieldBytes = sizeResult.ModuleFieldBytes,
            ActiveModules = activeModules,
            FitResults = fitResults,
        };
    }

    /// <summary>
    /// Shortcut: checks whether the specified module can fit in the current inode padding.
    /// </summary>
    /// <param name="module">The module to check.</param>
    /// <returns>True if the module's inode field bytes fit within available padding.</returns>
    public bool CanFitModule(ModuleId module)
    {
        // Module already active -- technically "fits" since it's already there
        if (ModuleRegistry.IsModuleActive(_moduleManifest, module))
            return false;

        var moduleDef = ModuleRegistry.GetModule(module);
        if (moduleDef.InodeFieldBytes == 0)
            return true; // No inode fields needed -- always fits

        var sizeResult = InodeSizeCalculator.Calculate(_moduleManifest);
        return InodeSizeCalculator.CanAddModuleWithoutMigration(sizeResult.PaddingBytes, module);
    }

    /// <summary>
    /// Returns all inactive modules that have inode fields and fit within the current padding.
    /// Modules with zero inode field bytes are not included (they always fit trivially).
    /// </summary>
    /// <returns>List of module IDs that can be added via padding claim.</returns>
    public IReadOnlyList<ModuleId> GetAllFittingModules()
    {
        var sizeResult = InodeSizeCalculator.Calculate(_moduleManifest);
        var fitting = new List<ModuleId>();

        for (int i = 0; i < FormatConstants.DefinedModules; i++)
        {
            var moduleId = (ModuleId)i;

            // Skip active modules
            if ((_moduleManifest & (1u << i)) != 0)
                continue;

            var moduleDef = ModuleRegistry.GetModule(moduleId);

            // Skip modules with no inode fields
            if (moduleDef.InodeFieldBytes == 0)
                continue;

            if (InodeSizeCalculator.CanAddModuleWithoutMigration(sizeResult.PaddingBytes, moduleId))
                fitting.Add(moduleId);
        }

        return fitting;
    }

    /// <summary>
    /// Simulates what the padding analysis would look like AFTER adding the specified module.
    /// Useful for evaluating chained additions (e.g., can we add RAID then Compression?).
    /// </summary>
    /// <param name="module">The module to simulate adding.</param>
    /// <returns>
    /// A <see cref="PaddingAnalysis"/> for the hypothetical manifest with the module activated.
    /// </returns>
    /// <exception cref="ArgumentException">The module is already active in the current manifest.</exception>
    public PaddingAnalysis AnalyzeAfterAdding(ModuleId module)
    {
        if (ModuleRegistry.IsModuleActive(_moduleManifest, module))
            throw new ArgumentException(
                $"Module {module} is already active in the current manifest.", nameof(module));

        uint newManifest = _moduleManifest | (1u << (int)module);
        var projected = new PaddingInventory(newManifest);
        return projected.Analyze();
    }
}
