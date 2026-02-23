using System.Collections.Frozen;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compatibility;

/// <summary>
/// Defines named presets for module selection during v1.0-to-v2.0 migration.
/// Each preset maps to a predefined set of <see cref="ModuleId"/> values.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 81: Migration module presets (MIGR-02)")]
public enum MigrationModulePreset : byte
{
    /// <summary>Standard profile: Security, Compression, Integrity, Snapshot (manifest 0x00001C01).</summary>
    Standard = 0,

    /// <summary>Minimal profile: no modules, bare v2.0 container.</summary>
    Minimal = 1,

    /// <summary>Analytics profile: Intelligence, Compression, Snapshot, Query (manifest 0x00003404).</summary>
    Analytics = 2,

    /// <summary>High-security profile: Security, Compliance, Integrity, Snapshot, Privacy (manifest 0x00004C03).</summary>
    HighSecurity = 3,

    /// <summary>Custom profile: caller provides explicit module list.</summary>
    Custom = 4,
}

/// <summary>
/// Provides module selection logic for v1.0-to-v2.0 VDE migration. Offers named presets
/// that map to predefined module sets, manifest building from arbitrary module lists,
/// and validation of user-selected modules.
/// </summary>
/// <remarks>
/// <para>
/// Presets align with the <see cref="VdeCreationProfile"/> factory methods:
/// <list type="bullet">
/// <item><see cref="MigrationModulePreset.Standard"/>: SEC + CMPR + INTG + SNAP (bits 0,10,11,12 = 0x00001C01)</item>
/// <item><see cref="MigrationModulePreset.Minimal"/>: no modules (manifest 0x00000000)</item>
/// <item><see cref="MigrationModulePreset.Analytics"/>: INTL + CMPR + SNAP + QURY (bits 2,10,12,13 = 0x00003404)</item>
/// <item><see cref="MigrationModulePreset.HighSecurity"/>: SEC + CMPL + INTG + SNAP + PRIV (bits 0,1,11,12,14 = 0x00004C03)</item>
/// <item><see cref="MigrationModulePreset.Custom"/>: empty list, caller provides explicit modules</item>
/// </list>
/// </para>
/// <para>
/// Uses <see cref="FrozenDictionary{TKey,TValue}"/> for zero-allocation preset-to-modules
/// mapping (same pattern as Phase 76-04 CheckClassificationTable).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 81: Migration module selector (MIGR-02)")]
public sealed class MigrationModuleSelector
{
    private static readonly FrozenDictionary<MigrationModulePreset, IReadOnlyList<ModuleId>> PresetModules =
        BuildPresetModules();

    private static readonly FrozenDictionary<MigrationModulePreset, string> PresetDescriptions =
        BuildPresetDescriptions();

    private static FrozenDictionary<MigrationModulePreset, IReadOnlyList<ModuleId>> BuildPresetModules() =>
        new Dictionary<MigrationModulePreset, IReadOnlyList<ModuleId>>
        {
            [MigrationModulePreset.Standard] = new[]
            {
                ModuleId.Security,
                ModuleId.Compression,
                ModuleId.Integrity,
                ModuleId.Snapshot,
            },
            [MigrationModulePreset.Minimal] = Array.Empty<ModuleId>(),
            [MigrationModulePreset.Analytics] = new[]
            {
                ModuleId.Intelligence,
                ModuleId.Compression,
                ModuleId.Snapshot,
                ModuleId.Query,
            },
            [MigrationModulePreset.HighSecurity] = new[]
            {
                ModuleId.Security,
                ModuleId.Compliance,
                ModuleId.Integrity,
                ModuleId.Snapshot,
                ModuleId.Privacy,
            },
            [MigrationModulePreset.Custom] = Array.Empty<ModuleId>(),
        }.ToFrozenDictionary();

    private static FrozenDictionary<MigrationModulePreset, string> BuildPresetDescriptions() =>
        new Dictionary<MigrationModulePreset, string>
        {
            [MigrationModulePreset.Standard] =
                "Standard migration: Security, Compression, Integrity, and Snapshot modules. " +
                "Provides a balanced set of features suitable for most workloads.",
            [MigrationModulePreset.Minimal] =
                "Minimal migration: bare v2.0 container with no modules enabled. " +
                "Smallest possible v2.0 VDE, modules can be added later via online module addition.",
            [MigrationModulePreset.Analytics] =
                "Analytics migration: Intelligence, Compression, Snapshot, and Query modules. " +
                "Optimized for analytical workloads with indexing and query acceleration.",
            [MigrationModulePreset.HighSecurity] =
                "High-security migration: Security, Compliance, Integrity, Snapshot, and Privacy modules. " +
                "Maximum data protection with compliance auditing and anonymization support.",
            [MigrationModulePreset.Custom] =
                "Custom migration: caller provides an explicit list of modules to enable. " +
                "Use ValidateModuleSelection to check the selection before migrating.",
        }.ToFrozenDictionary();

    /// <summary>
    /// Returns the predefined modules for the specified preset.
    /// </summary>
    /// <param name="preset">The migration preset.</param>
    /// <returns>
    /// The list of <see cref="ModuleId"/> values for the preset.
    /// For <see cref="MigrationModulePreset.Custom"/>, returns an empty list (caller provides modules).
    /// </returns>
    public IReadOnlyList<ModuleId> GetPresetModules(MigrationModulePreset preset)
    {
        return PresetModules.TryGetValue(preset, out var modules)
            ? modules
            : Array.Empty<ModuleId>();
    }

    /// <summary>
    /// Returns a human-readable description of the specified preset.
    /// </summary>
    /// <param name="preset">The migration preset.</param>
    /// <returns>A descriptive string explaining what the preset enables and its purpose.</returns>
    public string GetPresetDescription(MigrationModulePreset preset)
    {
        return PresetDescriptions.TryGetValue(preset, out var description)
            ? description
            : $"Unknown preset: {preset}";
    }

    /// <summary>
    /// Builds a 32-bit module manifest bitmask from a list of module identifiers.
    /// Each module contributes its bit (1u &lt;&lt; (int)moduleId) to the result.
    /// </summary>
    /// <param name="modules">The modules to include in the manifest.</param>
    /// <returns>The combined 32-bit module manifest value.</returns>
    public uint BuildManifest(IEnumerable<ModuleId> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        uint manifest = 0u;
        foreach (var module in modules)
        {
            manifest |= 1u << (int)module;
        }
        return manifest;
    }

    /// <summary>
    /// Validates a module selection for migration. Checks for duplicates, undefined enum values,
    /// and warns (without error) if Integrity is selected without Security.
    /// </summary>
    /// <param name="modules">The proposed module selection to validate.</param>
    /// <returns>
    /// A tuple of (Valid, Error) where Valid is true if the selection is acceptable,
    /// and Error contains a description of the issue if validation fails.
    /// A warning about Integrity without Security does NOT cause validation failure.
    /// </returns>
    public (bool Valid, string? Error) ValidateModuleSelection(IReadOnlyList<ModuleId> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        // Check for duplicates
        var seen = new HashSet<ModuleId>();
        foreach (var module in modules)
        {
            if (!seen.Add(module))
            {
                return (false, $"Duplicate module in selection: {module}");
            }
        }

        // Check all values are defined enum values
        foreach (var module in modules)
        {
            if (!Enum.IsDefined(module))
            {
                return (false, $"Undefined module identifier: {(byte)module}");
            }
        }

        // Warn (not error) if Integrity without Security
        bool hasIntegrity = seen.Contains(ModuleId.Integrity);
        bool hasSecurity = seen.Contains(ModuleId.Security);
        if (hasIntegrity && !hasSecurity)
        {
            // This is a warning, not an error -- return valid with a warning message
            return (true, "Warning: Integrity module selected without Security module. " +
                          "Integrity checksums will be computed but data will not be encrypted.");
        }

        return (true, null);
    }

    /// <summary>
    /// Lists all available presets with their descriptions and module counts,
    /// suitable for building interactive prompts.
    /// </summary>
    /// <returns>
    /// A list of (Preset, Description, ModuleCount) tuples for each defined preset.
    /// </returns>
    public IReadOnlyList<(MigrationModulePreset Preset, string Description, int ModuleCount)> GetAvailablePresets()
    {
        var presets = new (MigrationModulePreset Preset, string Description, int ModuleCount)[]
        {
            (MigrationModulePreset.Standard,
             GetPresetDescription(MigrationModulePreset.Standard),
             GetPresetModules(MigrationModulePreset.Standard).Count),

            (MigrationModulePreset.Minimal,
             GetPresetDescription(MigrationModulePreset.Minimal),
             GetPresetModules(MigrationModulePreset.Minimal).Count),

            (MigrationModulePreset.Analytics,
             GetPresetDescription(MigrationModulePreset.Analytics),
             GetPresetModules(MigrationModulePreset.Analytics).Count),

            (MigrationModulePreset.HighSecurity,
             GetPresetDescription(MigrationModulePreset.HighSecurity),
             GetPresetModules(MigrationModulePreset.HighSecurity).Count),

            (MigrationModulePreset.Custom,
             GetPresetDescription(MigrationModulePreset.Custom),
             GetPresetModules(MigrationModulePreset.Custom).Count),
        };

        return presets;
    }
}
