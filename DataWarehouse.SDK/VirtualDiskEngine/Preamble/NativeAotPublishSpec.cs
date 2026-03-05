using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Defines the NativeAOT publish configuration for building a single-binary DW Runtime
/// suitable for embedding in the DWVD bootable preamble (VOPT-65).
/// </summary>
/// <remarks>
/// <para>The preamble bundles the DW Runtime (kernel + selected plugins + CLI) as a NativeAOT
/// single binary for bare-metal boot. This class defines the publish parameters; the actual
/// script generation is performed by <see cref="NativeAotScriptGenerator"/>.</para>
/// <para>Three factory profiles cover common deployment scenarios:</para>
/// <list type="bullet">
/// <item><description><see cref="ServerFull"/>: All plugins, CLI, no GUI, linux-x64.</description></item>
/// <item><description><see cref="EmbeddedMinimal"/>: Core plugins only, CLI, no GUI, linux-arm64.</description></item>
/// <item><description><see cref="RecoveryReadonly"/>: Read-only subset, CLI only.</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-65 NativeAOT publish specification")]
public sealed class NativeAotPublishSpec
{
    /// <summary>Default maximum binary size in bytes (25 MiB).</summary>
    private const long DefaultMaxBinarySizeBytes = 25L * 1024 * 1024;

    /// <summary>
    /// Target Runtime Identifier for the <c>dotnet publish</c> command (e.g., <c>"linux-x64"</c>).
    /// </summary>
    public string RuntimeIdentifier { get; init; } = "linux-x64";

    /// <summary>Target CPU architecture. Must be consistent with <see cref="RuntimeIdentifier"/>.</summary>
    public TargetArchitecture Architecture { get; init; } = TargetArchitecture.X86_64;

    /// <summary>
    /// Maximum acceptable size of the output NativeAOT binary in bytes.
    /// The generated publish script will fail if the binary exceeds this limit.
    /// The value must fit within the <see cref="PreambleHeader.RuntimeSize"/> field (uint32).
    /// </summary>
    public long MaxBinarySizeBytes { get; init; } = DefaultMaxBinarySizeBytes;

    /// <summary>
    /// Path to the DW Runtime <c>.csproj</c> file that serves as the publish entry point.
    /// </summary>
    public string ProjectPath { get; init; } = string.Empty;

    /// <summary>Enable IL trimming to reduce binary size by removing unused code.</summary>
    public bool EnableTrimming { get; init; } = true;

    /// <summary>Publish as a single-file executable.</summary>
    public bool EnableSingleFile { get; init; } = true;

    /// <summary>Compress embedded assemblies within the single-file binary.</summary>
    public bool EnableCompression { get; init; } = true;

    /// <summary>Include CLI command handlers in the published binary.</summary>
    public bool IncludeCli { get; init; } = true;

    /// <summary>Include GUI components. Defaults to <c>false</c> for headless bare-metal boot.</summary>
    public bool IncludeGui { get; init; }

    /// <summary>
    /// Explicit list of plugin assembly names to include in the composition.
    /// Mutually exclusive with <see cref="ExcludedPlugins"/>.
    /// When empty, all plugins are included (unless <see cref="ExcludedPlugins"/> is specified).
    /// </summary>
    public IReadOnlyList<string> IncludedPlugins { get; init; } = Array.Empty<string>();

    /// <summary>
    /// List of plugin assembly names to exclude from the composition.
    /// Mutually exclusive with <see cref="IncludedPlugins"/>.
    /// </summary>
    public IReadOnlyList<string> ExcludedPlugins { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Additional MSBuild properties to pass as <c>-p:Key=Value</c> arguments.
    /// </summary>
    public IReadOnlyDictionary<string, string> AdditionalMsBuildProperties { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Creates a full server profile: all plugins, CLI, no GUI, targeting linux-x64.
    /// This is the standard deployment configuration for bare-metal servers with full I/O.
    /// </summary>
    public static NativeAotPublishSpec ServerFull() => new()
    {
        RuntimeIdentifier = "linux-x64",
        Architecture = TargetArchitecture.X86_64,
        IncludeCli = true,
        IncludeGui = false,
    };

    /// <summary>
    /// Creates an embedded/minimal profile: core plugins only (Storage, Encryption, RAID,
    /// Filesystem), CLI, no GUI, targeting linux-arm64 for edge and IoT deployments.
    /// </summary>
    public static NativeAotPublishSpec EmbeddedMinimal() => new()
    {
        RuntimeIdentifier = "linux-arm64",
        Architecture = TargetArchitecture.Aarch64,
        IncludeCli = true,
        IncludeGui = false,
        IncludedPlugins = new[]
        {
            "DataWarehouse.Plugins.UltimateStorage",
            "DataWarehouse.Plugins.UltimateEncryption",
            "DataWarehouse.Plugins.UltimateRAID",
            "DataWarehouse.Plugins.UltimateFilesystem",
        },
    };

    /// <summary>
    /// Creates a recovery/read-only profile: read-only subset (Storage, Filesystem, Integrity),
    /// CLI only, no GUI. Used for disaster-recovery and forensic access scenarios.
    /// </summary>
    public static NativeAotPublishSpec RecoveryReadonly() => new()
    {
        RuntimeIdentifier = "linux-x64",
        Architecture = TargetArchitecture.X86_64,
        IncludeCli = true,
        IncludeGui = false,
        IncludedPlugins = new[]
        {
            "DataWarehouse.Plugins.UltimateStorage",
            "DataWarehouse.Plugins.UltimateFilesystem",
            "DataWarehouse.Plugins.UltimateIntegrity",
        },
        AdditionalMsBuildProperties = new Dictionary<string, string>
        {
            ["DwReadOnlyMode"] = "true",
        },
    };

    /// <summary>
    /// Validates this publish specification for correctness.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ProjectPath"/> is null or empty, architecture does not match the runtime
    /// identifier, or both <see cref="IncludedPlugins"/> and <see cref="ExcludedPlugins"/>
    /// contain entries (they are mutually exclusive).
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
            throw new InvalidOperationException(
                "ProjectPath must be specified. Provide the path to the DW Runtime .csproj file.");

        // Validate Architecture <-> RuntimeIdentifier consistency
        var expectedRidPrefix = Architecture switch
        {
            TargetArchitecture.X86_64 => "linux-x64",
            TargetArchitecture.Aarch64 => "linux-arm64",
            TargetArchitecture.RiscV64 => "linux-riscv64",
            _ => throw new InvalidOperationException(
                $"Unsupported target architecture: {Architecture}."),
        };

        if (!RuntimeIdentifier.Equals(expectedRidPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Architecture {Architecture} requires RuntimeIdentifier '{expectedRidPrefix}', " +
                $"but got '{RuntimeIdentifier}'.");

        // IncludedPlugins and ExcludedPlugins are mutually exclusive
        if (IncludedPlugins.Count > 0 && ExcludedPlugins.Count > 0)
            throw new InvalidOperationException(
                "IncludedPlugins and ExcludedPlugins are mutually exclusive. " +
                "Specify one or the other, not both.");

        // MaxBinarySizeBytes must fit in uint32 (PreambleHeader.RuntimeSize limit)
        if (MaxBinarySizeBytes <= 0)
            throw new InvalidOperationException(
                "MaxBinarySizeBytes must be a positive value.");

        if (MaxBinarySizeBytes > uint.MaxValue)
            throw new InvalidOperationException(
                $"MaxBinarySizeBytes ({MaxBinarySizeBytes}) exceeds the uint32 limit " +
                $"({uint.MaxValue}) imposed by PreambleHeader.RuntimeSize.");
    }
}
