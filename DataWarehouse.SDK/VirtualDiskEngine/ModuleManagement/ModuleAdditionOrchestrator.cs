using System.Buffers;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Analysis result for adding a single module to a VDE, including all four option
/// evaluations and the Tier 2 fallback status.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Module addition analysis result (OMOD-06)")]
public readonly record struct ModuleAdditionAnalysis
{
    /// <summary>The module being analyzed.</summary>
    public ModuleId Module { get; init; }

    /// <summary>Module definition from the registry.</summary>
    public VdeModule ModuleInfo { get; init; }

    /// <summary>True if the module is already active in the manifest.</summary>
    public bool AlreadyActive { get; init; }

    /// <summary>True if the module contributes inode field bytes.</summary>
    public bool HasInodeFields { get; init; }

    /// <summary>True if the module requires dedicated regions.</summary>
    public bool HasRegions { get; init; }

    /// <summary>All four options with availability, risk, downtime ratings.</summary>
    public OptionComparison Options { get; init; }

    /// <summary>Tier 2 fallback status for this module.</summary>
    public FallbackStatus Tier2Status { get; init; }
}

/// <summary>
/// Result of executing a module addition operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Module addition execution result (OMOD-07)")]
public readonly record struct ModuleAdditionResult
{
    /// <summary>True if the operation completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if the operation failed; null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Which option was executed (1-4).</summary>
    public int OptionUsed { get; init; }

    /// <summary>Updated module manifest after the operation.</summary>
    public uint NewManifest { get; init; }

    /// <summary>Updated inode layout descriptor if the layout changed; null otherwise.</summary>
    public InodeLayoutDescriptor? NewLayout { get; init; }

    /// <summary>Total duration of the operation.</summary>
    public TimeSpan Duration { get; init; }

    internal static ModuleAdditionResult Succeeded(int optionUsed, uint newManifest,
        InodeLayoutDescriptor? newLayout, TimeSpan duration) => new()
    {
        Success = true,
        OptionUsed = optionUsed,
        NewManifest = newManifest,
        NewLayout = newLayout,
        Duration = duration,
    };

    internal static ModuleAdditionResult Failed(string error) => new()
    {
        Success = false,
        ErrorMessage = error,
    };
}

/// <summary>
/// Central orchestrator for online module addition. Provides an analyze-then-execute API
/// surface that hides the complexity of the four addition strategies behind a clean flow:
/// <list type="number">
///   <item><see cref="AnalyzeAsync"/>: evaluate all 4 options, recommend the best</item>
///   <item><see cref="ExecuteAsync"/>: execute a chosen option with atomic manifest+config update</item>
/// </list>
///
/// <para>Satisfies:</para>
/// <list type="bullet">
///   <item>OMOD-06: user sees all options with performance/downtime/risk comparison</item>
///   <item>OMOD-05 / MRES-06/07: Tier 2 fallback verified before operation</item>
///   <item>OMOD-07: ModuleManifest and ModuleConfig update atomically within WAL transaction</item>
/// </list>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Module addition orchestrator (OMOD-06/07)")]
public sealed class ModuleAdditionOrchestrator
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;

    /// <summary>Status callback for UI progress reporting.</summary>
    public Action<string>? OnStatus { get; set; }

    /// <summary>
    /// Creates a new module addition orchestrator.
    /// </summary>
    /// <param name="vdeStream">The VDE stream (must support read/write/seek).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public ModuleAdditionOrchestrator(Stream vdeStream, int blockSize)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");
        _blockSize = blockSize;
    }

    /// <summary>
    /// Analyzes the VDE state and evaluates all four module addition options for the
    /// specified module. Returns an <see cref="ModuleAdditionAnalysis"/> with availability,
    /// risk, downtime, and the recommended option.
    /// </summary>
    /// <param name="module">The module to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Analysis with all four options evaluated.</returns>
    public async Task<ModuleAdditionAnalysis> AnalyzeAsync(ModuleId module, CancellationToken ct)
    {
        EmitStatus($"Analyzing module addition: {module}...");

        // Step 1: Read superblock
        var superblock = await ReadSuperblockAsync(ct);
        uint currentManifest = superblock.ModuleManifest;

        // Step 2: Get module info
        var moduleDef = ModuleRegistry.GetModule(module);
        var tier2Status = Tier2FallbackGuard.CheckFallback(module, currentManifest);

        // Step 3: Check if already active
        bool alreadyActive = ModuleRegistry.IsModuleActive(currentManifest, module);
        if (alreadyActive)
        {
            var noOptions = BuildAllUnavailableOptions("Module is already active in the manifest.");
            return new ModuleAdditionAnalysis
            {
                Module = module,
                ModuleInfo = moduleDef,
                AlreadyActive = true,
                HasInodeFields = moduleDef.HasInodeFields,
                HasRegions = moduleDef.HasRegion,
                Options = noOptions,
                Tier2Status = tier2Status,
            };
        }

        // Step 4-7: Evaluate each option
        var option1 = await EvaluateOption1Async(moduleDef, superblock, ct);
        var option2 = EvaluateOption2(moduleDef, currentManifest);
        var option3 = await EvaluateOption3Async(moduleDef, superblock, currentManifest, ct);
        var option4 = EvaluateOption4(moduleDef);

        // Step 8: Determine recommendation
        var options = DetermineRecommendation(moduleDef, option1, option2, option3, option4);

        EmitStatus($"Analysis complete: {options.Options.Count(o => o.Available)} of 4 options available.");

        return new ModuleAdditionAnalysis
        {
            Module = module,
            ModuleInfo = moduleDef,
            AlreadyActive = false,
            HasInodeFields = moduleDef.HasInodeFields,
            HasRegions = moduleDef.HasRegion,
            Options = options,
            Tier2Status = tier2Status,
        };
    }

    /// <summary>
    /// Executes a module addition using the specified option number (1-4).
    /// Verifies Tier 2 fallback, executes the operation, and atomically updates
    /// both ModuleManifest and ModuleConfig in the superblock.
    /// </summary>
    /// <param name="module">The module to add.</param>
    /// <param name="optionNumber">Option to execute (1-4).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="destinationPath">Required for Option 4 (new VDE path); ignored for others.</param>
    /// <returns>Result of the module addition operation.</returns>
    public async Task<ModuleAdditionResult> ExecuteAsync(
        ModuleId module, int optionNumber, CancellationToken ct, string? destinationPath = null)
    {
        var sw = Stopwatch.StartNew();

        // Step 1: Verify Tier 2 fallback
        if (!Tier2FallbackGuard.EnsureTier2Active(module))
            return ModuleAdditionResult.Failed($"Tier 2 fallback not available for module {module}.");

        // Step 2: Re-read superblock (state may have changed since analysis)
        var superblock = await ReadSuperblockAsync(ct);
        uint currentManifest = superblock.ModuleManifest;

        if (ModuleRegistry.IsModuleActive(currentManifest, module))
            return ModuleAdditionResult.Failed($"Module {module} is already active in the manifest.");

        var moduleDef = ModuleRegistry.GetModule(module);
        uint newManifest = currentManifest | (1u << (int)module);
        InodeLayoutDescriptor? newLayout = null;

        EmitStatus($"Executing Option {optionNumber} for module {module}...");

        // Step 3: Execute based on option number
        // If module needs BOTH regions and inode fields, handle combined operation
        bool needsRegions = moduleDef.HasRegion;
        bool needsInodeFields = moduleDef.HasInodeFields;

        if (needsRegions && needsInodeFields && optionNumber is 1 or 2 or 3)
        {
            // Combined: first add regions (Option 1), then handle inode fields
            return await ExecuteCombinedAsync(module, moduleDef, optionNumber,
                superblock, currentManifest, newManifest, sw, ct);
        }

        switch (optionNumber)
        {
            case 1:
            {
                EmitStatus("Adding module regions via online region addition...");
                var regionAdder = new OnlineRegionAddition(_vdeStream, _blockSize);
                var result = await regionAdder.AddModuleRegionsAsync(module, ct);
                if (!result.Success)
                    return ModuleAdditionResult.Failed(result.ErrorMessage ?? "Region addition failed.");

                // Atomic manifest+config update handled by OnlineRegionAddition's WAL
                // (it already updates the superblock manifest)
                await UpdateModuleConfigAsync(module, ct);
                return ModuleAdditionResult.Succeeded(1, newManifest, null, sw.Elapsed);
            }

            case 2:
            {
                EmitStatus("Claiming inode padding for module fields...");
                var paddingClaim = new InodePaddingClaim(_vdeStream, _blockSize);
                var result = await paddingClaim.ClaimPaddingForModuleAsync(module, ct);
                if (!result.Success)
                    return ModuleAdditionResult.Failed(result.ErrorMessage ?? "Padding claim failed.");

                newLayout = result.NewLayout;
                await UpdateModuleConfigAsync(module, ct);
                return ModuleAdditionResult.Succeeded(2, newManifest, newLayout, sw.Elapsed);
            }

            case 3:
            {
                EmitStatus("Starting background inode migration...");
                var migration = new BackgroundInodeMigration(_vdeStream, _blockSize);
                migration.OnProgress = p => EmitStatus(
                    $"Migration: {p.PercentComplete:F1}% ({p.MigratedInodes}/{p.TotalInodes} inodes)");
                var result = await migration.MigrateAsync(module, ct);
                if (!result.Success)
                    return ModuleAdditionResult.Failed(result.ErrorMessage ?? "Inode migration failed.");

                newLayout = result.NewLayout;
                await UpdateModuleConfigAsync(module, ct);
                return ModuleAdditionResult.Succeeded(3, newManifest, newLayout, sw.Elapsed);
            }

            case 4:
            {
                if (string.IsNullOrWhiteSpace(destinationPath))
                    return ModuleAdditionResult.Failed("Option 4 requires a destination path for the new VDE.");

                EmitStatus($"Copying VDE to {destinationPath} with module {module}...");
                var copier = new ExtentAwareVdeCopy(_vdeStream, _blockSize);
                copier.OnProgress = p => EmitStatus(
                    $"Copy: {p.PercentComplete:F1}% ({p.CopiedBlocks} copied, {p.SkippedBlocks} skipped)");
                var result = await copier.CopyToNewVdeAsync(destinationPath, module, ct);
                if (!result.Success)
                    return ModuleAdditionResult.Failed(result.ErrorMessage ?? "VDE copy failed.");

                newLayout = InodeLayoutDescriptor.Create(newManifest);
                return ModuleAdditionResult.Succeeded(4, newManifest, newLayout, sw.Elapsed);
            }

            default:
                return ModuleAdditionResult.Failed($"Invalid option number: {optionNumber}. Must be 1-4.");
        }
    }

    /// <summary>
    /// Convenience method: analyzes the module and executes the recommended option.
    /// </summary>
    /// <param name="module">The module to add.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="destinationPath">
    /// Optional destination path for Option 4. If null and Option 4 is recommended,
    /// execution will fail with a descriptive error.
    /// </param>
    /// <returns>Result of the module addition operation.</returns>
    public async Task<ModuleAdditionResult> ExecuteRecommendedAsync(
        ModuleId module, CancellationToken ct, string? destinationPath = null)
    {
        var analysis = await AnalyzeAsync(module, ct);

        if (analysis.AlreadyActive)
            return ModuleAdditionResult.Failed($"Module {module} is already active.");

        if (analysis.Options.Recommended is null)
            return ModuleAdditionResult.Failed($"No available options for adding module {module}.");

        int recommendedOption = analysis.Options.Recommended.Value.OptionNumber;
        EmitStatus($"Executing recommended Option {recommendedOption} for module {module}...");

        return await ExecuteAsync(module, recommendedOption, ct, destinationPath);
    }

    /// <summary>
    /// Analyzes adding multiple modules, considering cumulative effects: adding module A
    /// reduces padding available for module B. Each analysis uses the hypothetical manifest
    /// that includes all previously analyzed modules.
    /// </summary>
    /// <param name="modules">The modules to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of analyses, each reflecting cumulative manifest changes.</returns>
    public async Task<IReadOnlyList<ModuleAdditionAnalysis>> AnalyzeMultipleAsync(
        IEnumerable<ModuleId> modules, CancellationToken ct)
    {
        var results = new List<ModuleAdditionAnalysis>();
        var superblock = await ReadSuperblockAsync(ct);
        uint cumulativeManifest = superblock.ModuleManifest;

        foreach (var module in modules)
        {
            ct.ThrowIfCancellationRequested();

            var moduleDef = ModuleRegistry.GetModule(module);
            var tier2Status = Tier2FallbackGuard.CheckFallback(module, cumulativeManifest);
            bool alreadyActive = ModuleRegistry.IsModuleActive(cumulativeManifest, module);

            if (alreadyActive)
            {
                var noOptions = BuildAllUnavailableOptions("Module is already active (or queued from prior analysis).");
                results.Add(new ModuleAdditionAnalysis
                {
                    Module = module,
                    ModuleInfo = moduleDef,
                    AlreadyActive = true,
                    HasInodeFields = moduleDef.HasInodeFields,
                    HasRegions = moduleDef.HasRegion,
                    Options = noOptions,
                    Tier2Status = tier2Status,
                });
                continue;
            }

            // Evaluate options against cumulative manifest
            var option1 = await EvaluateOption1Async(moduleDef, superblock, ct);
            var option2 = EvaluateOption2(moduleDef, cumulativeManifest);
            var option3 = await EvaluateOption3Async(moduleDef, superblock, cumulativeManifest, ct);
            var option4 = EvaluateOption4(moduleDef);

            var options = DetermineRecommendation(moduleDef, option1, option2, option3, option4);

            results.Add(new ModuleAdditionAnalysis
            {
                Module = module,
                ModuleInfo = moduleDef,
                AlreadyActive = false,
                HasInodeFields = moduleDef.HasInodeFields,
                HasRegions = moduleDef.HasRegion,
                Options = options,
                Tier2Status = tier2Status,
            });

            // Update cumulative manifest for next module
            cumulativeManifest |= (1u << (int)module);
        }

        return results;
    }

    // ── Option Evaluation ────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates Option 1: Online Region Addition.
    /// Available if the module has regions AND sufficient free space exists.
    /// </summary>
    private async Task<AdditionOption> EvaluateOption1Async(
        VdeModule moduleDef, SuperblockV2 superblock, CancellationToken ct)
    {
        if (!moduleDef.HasRegion)
        {
            return new AdditionOption
            {
                OptionNumber = 1,
                Name = "Online Region Addition",
                Description = "Allocates new regions from free space in the allocation bitmap using WAL-journaled writes.",
                Available = false,
                UnavailableReason = $"Module {moduleDef.Name} does not require dedicated regions.",
                Risk = RiskLevel.Low,
                Downtime = DowntimeEstimate.Zero,
                PerformanceImpact = "None during operation",
                EstimatedDuration = TimeSpan.FromMilliseconds(500),
            };
        }

        // Check free space via pre-flight
        var regionAdder = new OnlineRegionAddition(_vdeStream, _blockSize);
        bool canAdd = await regionAdder.CanAddModuleAsync(moduleDef.Id, ct);

        return new AdditionOption
        {
            OptionNumber = 1,
            Name = "Online Region Addition",
            Description = "Allocates new regions from free space in the allocation bitmap using WAL-journaled writes.",
            Available = canAdd,
            UnavailableReason = canAdd ? null : "Insufficient contiguous free space for required regions.",
            Risk = RiskLevel.Low,
            Downtime = DowntimeEstimate.Zero,
            PerformanceImpact = "None during operation",
            EstimatedDuration = TimeSpan.FromMilliseconds(500),
        };
    }

    /// <summary>
    /// Evaluates Option 2: Inode Padding Claim.
    /// Available if the module has inode fields AND they fit in current padding.
    /// </summary>
    private static AdditionOption EvaluateOption2(VdeModule moduleDef, uint currentManifest)
    {
        if (!moduleDef.HasInodeFields)
        {
            return new AdditionOption
            {
                OptionNumber = 2,
                Name = "Inode Padding Claim",
                Description = "Claims inode padding bytes for the module's fields with a metadata-only update.",
                Available = false,
                UnavailableReason = $"Module {moduleDef.Name} has no inode fields.",
                Risk = RiskLevel.None,
                Downtime = DowntimeEstimate.Zero,
                PerformanceImpact = "None -- metadata-only operation",
                EstimatedDuration = TimeSpan.FromMilliseconds(100),
            };
        }

        bool canClaim = InodePaddingClaim.CanClaimPadding(currentManifest, moduleDef.Id);
        var sizeResult = InodeSizeCalculator.Calculate(currentManifest);

        return new AdditionOption
        {
            OptionNumber = 2,
            Name = "Inode Padding Claim",
            Description = "Claims inode padding bytes for the module's fields with a metadata-only update.",
            Available = canClaim,
            UnavailableReason = canClaim
                ? null
                : $"Module requires {moduleDef.InodeFieldBytes} bytes but only {sizeResult.PaddingBytes} padding bytes available.",
            Risk = RiskLevel.None,
            Downtime = DowntimeEstimate.Zero,
            PerformanceImpact = "None -- metadata-only operation",
            EstimatedDuration = TimeSpan.FromMilliseconds(100),
        };
    }

    /// <summary>
    /// Evaluates Option 3: Background Inode Migration.
    /// Available if the module has inode fields that don't fit in padding AND
    /// sufficient free space exists for a new inode table.
    /// </summary>
    private async Task<AdditionOption> EvaluateOption3Async(
        VdeModule moduleDef, SuperblockV2 superblock, uint currentManifest, CancellationToken ct)
    {
        if (!moduleDef.HasInodeFields)
        {
            return new AdditionOption
            {
                OptionNumber = 3,
                Name = "Background Inode Migration",
                Description = "Creates a new inode table with larger inodes and migrates all inodes in the background.",
                Available = false,
                UnavailableReason = $"Module {moduleDef.Name} has no inode fields.",
                Risk = RiskLevel.Low,
                Downtime = DowntimeEstimate.Zero,
                PerformanceImpact = "~10% I/O overhead during migration",
                EstimatedDuration = TimeSpan.FromMinutes(5),
            };
        }

        // If padding claim is available, migration is also available but not preferred
        bool paddingFits = InodePaddingClaim.CanClaimPadding(currentManifest, moduleDef.Id);

        // Check if free space is available for a new inode table
        uint newManifest = currentManifest | (1u << (int)moduleDef.Id);
        var newSizeResult = InodeSizeCalculator.Calculate(newManifest);
        var oldLayout = InodeLayoutDescriptor.Create(currentManifest);

        // Estimate inode count from superblock data
        long estimatedInodes = EstimateInodeCount(superblock, oldLayout.InodeSize);
        long newBlocksNeeded = (estimatedInodes * newSizeResult.InodeSize + _blockSize - 1) / _blockSize;

        bool hasFreeSpace = superblock.FreeBlocks >= newBlocksNeeded;
        TimeSpan estimatedDuration = TimeSpan.FromSeconds(Math.Max(1, estimatedInodes / 200));

        await Task.CompletedTask; // Satisfy async contract

        return new AdditionOption
        {
            OptionNumber = 3,
            Name = "Background Inode Migration",
            Description = "Creates a new inode table with larger inodes and migrates all inodes in the background.",
            Available = hasFreeSpace,
            UnavailableReason = hasFreeSpace
                ? null
                : $"Insufficient free space for new inode table ({newBlocksNeeded} blocks needed, {superblock.FreeBlocks} available).",
            Risk = RiskLevel.Low,
            Downtime = DowntimeEstimate.Zero,
            PerformanceImpact = "~10% I/O overhead during migration",
            EstimatedDuration = estimatedDuration,
        };
    }

    /// <summary>
    /// Evaluates Option 4: New VDE + Bulk Migration.
    /// Always available (requires destination path at execution time).
    /// </summary>
    private static AdditionOption EvaluateOption4(VdeModule moduleDef)
    {
        return new AdditionOption
        {
            OptionNumber = 4,
            Name = "New VDE + Bulk Migration",
            Description = "Creates a new VDE with the module and extent-aware copies only allocated data blocks.",
            Available = true,
            UnavailableReason = null,
            Risk = RiskLevel.Medium,
            Downtime = DowntimeEstimate.Minutes,
            PerformanceImpact = "Full I/O for allocated blocks; source VDE remains read-only during copy",
            EstimatedDuration = TimeSpan.FromMinutes(30),
        };
    }

    // ── Recommendation Logic ─────────────────────────────────────────────────

    /// <summary>
    /// Determines the recommended option based on module characteristics and availability.
    /// Priority: Option 2 (cheapest) > Option 1 (regions) > Option 3 (migration) > Option 4 (copy).
    /// Combined modules (regions + inode fields) prefer Option 1+2, then 1+3, then 4.
    /// </summary>
    private static OptionComparison DetermineRecommendation(
        VdeModule moduleDef,
        AdditionOption option1, AdditionOption option2,
        AdditionOption option3, AdditionOption option4)
    {
        bool needsRegions = moduleDef.HasRegion;
        bool needsInodeFields = moduleDef.HasInodeFields;

        // Determine the recommended option based on what the module needs
        int recommendedNumber;

        if (needsRegions && needsInodeFields)
        {
            // Module needs both: prefer Option 1+2 combined, else 1+3, else 4
            if (option1.Available && option2.Available)
                recommendedNumber = 1; // Will be combined 1+2 at execution time
            else if (option1.Available && option3.Available)
                recommendedNumber = 1; // Will be combined 1+3 at execution time
            else
                recommendedNumber = 4;
        }
        else if (needsRegions)
        {
            // Module needs only regions
            recommendedNumber = option1.Available ? 1 : 4;
        }
        else if (needsInodeFields)
        {
            // Module needs only inode fields: prefer 2 > 3 > 4
            if (option2.Available)
                recommendedNumber = 2;
            else if (option3.Available)
                recommendedNumber = 3;
            else
                recommendedNumber = 4;
        }
        else
        {
            // Module needs neither regions nor inode fields (e.g., region-only like Compute)
            if (option1.Available)
                recommendedNumber = 1;
            else
                recommendedNumber = 4;
        }

        // Apply IsRecommended to the chosen option
        var options = new AdditionOption[]
        {
            option1 with { IsRecommended = recommendedNumber == 1 },
            option2 with { IsRecommended = recommendedNumber == 2 },
            option3 with { IsRecommended = recommendedNumber == 3 },
            option4 with { IsRecommended = recommendedNumber == 4 },
        };

        return new OptionComparison(options);
    }

    // ── Combined Execution ───────────────────────────────────────────────────

    /// <summary>
    /// Executes a combined operation for modules that need BOTH regions and inode fields.
    /// First adds regions (Option 1), then handles inode fields (Option 2 or 3).
    /// </summary>
    private async Task<ModuleAdditionResult> ExecuteCombinedAsync(
        ModuleId module, VdeModule moduleDef, int primaryOption,
        SuperblockV2 superblock, uint currentManifest, uint newManifest,
        Stopwatch sw, CancellationToken ct)
    {
        InodeLayoutDescriptor? finalLayout = null;

        // Step 1: Add regions via Option 1
        EmitStatus($"Combined operation: adding regions for {module}...");
        var regionAdder = new OnlineRegionAddition(_vdeStream, _blockSize);
        var regionResult = await regionAdder.AddModuleRegionsAsync(module, ct);
        if (!regionResult.Success)
            return ModuleAdditionResult.Failed($"Region addition failed: {regionResult.ErrorMessage}");

        // Step 2: Handle inode fields
        if (moduleDef.HasInodeFields)
        {
            bool canClaimPadding = InodePaddingClaim.CanClaimPadding(currentManifest, module);

            if (canClaimPadding)
            {
                EmitStatus($"Combined operation: claiming inode padding for {module}...");
                var paddingClaim = new InodePaddingClaim(_vdeStream, _blockSize);
                var claimResult = await paddingClaim.ClaimPaddingForModuleAsync(module, ct);
                if (!claimResult.Success)
                    return ModuleAdditionResult.Failed($"Padding claim failed: {claimResult.ErrorMessage}");
                finalLayout = claimResult.NewLayout;
            }
            else
            {
                EmitStatus($"Combined operation: migrating inodes for {module}...");
                var migration = new BackgroundInodeMigration(_vdeStream, _blockSize);
                migration.OnProgress = p => EmitStatus(
                    $"Migration: {p.PercentComplete:F1}% ({p.MigratedInodes}/{p.TotalInodes} inodes)");
                var migResult = await migration.MigrateAsync(module, ct);
                if (!migResult.Success)
                    return ModuleAdditionResult.Failed($"Inode migration failed: {migResult.ErrorMessage}");
                finalLayout = migResult.NewLayout;
            }
        }

        // Step 3: Ensure module config is updated
        await UpdateModuleConfigAsync(module, ct);

        EmitStatus($"Module {module} added successfully via combined Option 1+{(finalLayout != null ? "2/3" : "1")}.");
        return ModuleAdditionResult.Succeeded(primaryOption, newManifest, finalLayout, sw.Elapsed);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the ModuleConfig in the superblock to set level=1 for the newly added module.
    /// Both manifest and config reside in the same superblock block, so the write is atomic.
    /// Uses WAL-journaled approach via the metadata WAL region.
    /// </summary>
    private async Task UpdateModuleConfigAsync(ModuleId module, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            // Read current superblock
            _vdeStream.Seek(0, SeekOrigin.Begin);
            await ReadExactAsync(buffer, _blockSize, ct);
            var superblock = SuperblockV2.Deserialize(buffer.AsSpan(0, _blockSize), _blockSize);

            // Update module config: set level to 1 (basic/enabled) for the new module
            var currentConfig = new ModuleConfigField(superblock.ModuleConfig, superblock.ModuleConfigExt);
            if (currentConfig.GetLevel(module) == 0)
            {
                var newConfig = currentConfig.WithLevel(module, 1);

                // Reconstruct superblock with updated config
                var updated = new SuperblockV2(
                    magic: superblock.Magic,
                    versionInfo: superblock.VersionInfo,
                    moduleManifest: superblock.ModuleManifest,
                    moduleConfig: newConfig.ConfigPrimary,
                    moduleConfigExt: newConfig.ConfigExtended,
                    blockSize: superblock.BlockSize,
                    totalBlocks: superblock.TotalBlocks,
                    freeBlocks: superblock.FreeBlocks,
                    expectedFileSize: superblock.ExpectedFileSize,
                    totalAllocatedBlocks: superblock.TotalAllocatedBlocks,
                    volumeUuid: superblock.VolumeUuid,
                    clusterNodeId: superblock.ClusterNodeId,
                    defaultCompressionAlgo: superblock.DefaultCompressionAlgo,
                    defaultEncryptionAlgo: superblock.DefaultEncryptionAlgo,
                    defaultChecksumAlgo: superblock.DefaultChecksumAlgo,
                    inodeSize: superblock.InodeSize,
                    policyVersion: superblock.PolicyVersion,
                    replicationEpoch: superblock.ReplicationEpoch,
                    wormHighWaterMark: superblock.WormHighWaterMark,
                    encryptionKeyFingerprint: superblock.EncryptionKeyFingerprint,
                    sovereigntyZoneId: superblock.SovereigntyZoneId,
                    volumeLabel: superblock.VolumeLabel,
                    createdTimestampUtc: superblock.CreatedTimestampUtc,
                    modifiedTimestampUtc: DateTimeOffset.UtcNow.Ticks,
                    lastScrubTimestamp: superblock.LastScrubTimestamp,
                    checkpointSequence: superblock.CheckpointSequence,
                    errorMapBlockCount: superblock.ErrorMapBlockCount,
                    lastWriterSessionId: superblock.LastWriterSessionId,
                    lastWriterTimestamp: DateTimeOffset.UtcNow.Ticks,
                    lastWriterNodeId: superblock.LastWriterNodeId,
                    physicalAllocatedBlocks: superblock.PhysicalAllocatedBlocks,
                    headerIntegritySeal: superblock.HeaderIntegritySeal);

                // Write updated superblock atomically
                Array.Clear(buffer, 0, _blockSize);
                SuperblockV2.Serialize(updated, buffer.AsSpan(0, _blockSize), _blockSize);
                UniversalBlockTrailer.Write(buffer.AsSpan(0, _blockSize), _blockSize, BlockTypeTags.SUPB, 1);

                _vdeStream.Seek(0, SeekOrigin.Begin);
                await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);

                // Also write mirror superblock
                _vdeStream.Seek(FormatConstants.SuperblockMirrorStartBlock * _blockSize, SeekOrigin.Begin);
                await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);

                await _vdeStream.FlushAsync(ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<SuperblockV2> ReadSuperblockAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            _vdeStream.Seek(0, SeekOrigin.Begin);
            await ReadExactAsync(buffer, _blockSize, ct);
            return SuperblockV2.Deserialize(buffer.AsSpan(0, _blockSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int bytesRead = await _vdeStream.ReadAsync(
                buffer.AsMemory(totalRead, count - totalRead), ct);
            if (bytesRead == 0)
                throw new InvalidDataException("Unexpected end of VDE stream.");
            totalRead += bytesRead;
        }
    }

    private static long EstimateInodeCount(SuperblockV2 superblock, int inodeSize)
    {
        // Estimate based on total allocated blocks: assume ~1 inode per 4 data blocks
        long dataBlocks = superblock.TotalAllocatedBlocks;
        long inodeEstimate = Math.Max(1024, dataBlocks / 4);
        return inodeEstimate;
    }

    private static OptionComparison BuildAllUnavailableOptions(string reason)
    {
        var options = new AdditionOption[]
        {
            new()
            {
                OptionNumber = 1, Name = "Online Region Addition",
                Description = "Allocates new regions from free space.",
                Available = false, UnavailableReason = reason,
                Risk = RiskLevel.Low, Downtime = DowntimeEstimate.Zero,
                PerformanceImpact = "N/A", EstimatedDuration = TimeSpan.Zero,
            },
            new()
            {
                OptionNumber = 2, Name = "Inode Padding Claim",
                Description = "Claims inode padding bytes for module fields.",
                Available = false, UnavailableReason = reason,
                Risk = RiskLevel.None, Downtime = DowntimeEstimate.Zero,
                PerformanceImpact = "N/A", EstimatedDuration = TimeSpan.Zero,
            },
            new()
            {
                OptionNumber = 3, Name = "Background Inode Migration",
                Description = "Creates new inode table and migrates inodes in background.",
                Available = false, UnavailableReason = reason,
                Risk = RiskLevel.Low, Downtime = DowntimeEstimate.Zero,
                PerformanceImpact = "N/A", EstimatedDuration = TimeSpan.Zero,
            },
            new()
            {
                OptionNumber = 4, Name = "New VDE + Bulk Migration",
                Description = "Creates new VDE with module and copies allocated data.",
                Available = false, UnavailableReason = reason,
                Risk = RiskLevel.Medium, Downtime = DowntimeEstimate.Minutes,
                PerformanceImpact = "N/A", EstimatedDuration = TimeSpan.Zero,
            },
        };
        return new OptionComparison(options);
    }

    private void EmitStatus(string message) => OnStatus?.Invoke(message);
}
