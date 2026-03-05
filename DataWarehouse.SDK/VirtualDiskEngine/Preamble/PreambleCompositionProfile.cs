using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Identifies the type of a <see cref="PreambleCompositionProfile"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-66 preamble composition profiles")]
public enum PreambleProfileType : byte
{
    /// <summary>Full server deployment with all drivers, plugins, and SPDK.</summary>
    ServerFull = 0,

    /// <summary>Minimal embedded/headless deployment with core plugins only.</summary>
    EmbeddedMinimal = 1,

    /// <summary>Air-gapped read-only deployment with no NIC and Ed25519 signing.</summary>
    AirgapReadonly = 2,

    /// <summary>User-defined custom profile.</summary>
    Custom = 3,
}

/// <summary>
/// A named composition profile that defines which kernel, SPDK, and runtime components
/// to include in a DWVD bootable preamble (VOPT-66). Three built-in profiles cover
/// standard deployment scenarios; custom profiles support arbitrary compositions.
/// </summary>
/// <remarks>
/// <para>Profiles are consumed by <see cref="PreambleCompositionEngine"/> to validate
/// consistency, compute layout offsets, and produce a <see cref="PreambleHeader"/>.</para>
/// <para>Built-in profiles:</para>
/// <list type="bullet">
/// <item><description><see cref="ServerFull"/>: All drivers, full plugin set, SPDK nvme-local, x86_64.</description></item>
/// <item><description><see cref="EmbeddedMinimal"/>: Minimal drivers (no display), core plugins only, arm64.</description></item>
/// <item><description><see cref="AirgapReadonly"/>: No NIC, read-only plugins, Ed25519-signed, x86_64.</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-66 preamble composition profile")]
public sealed class PreambleCompositionProfile
{
    /// <summary>The profile type identifier.</summary>
    public PreambleProfileType ProfileType { get; init; }

    /// <summary>Human-readable profile name (e.g., "server-full").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Description of what this profile targets and its trade-offs.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Kernel configuration for this profile (driver selection, architecture).</summary>
    public StrippedKernelBuildSpec KernelSpec { get; init; } = new();

    /// <summary>NativeAOT runtime publish configuration for this profile.</summary>
    public NativeAotPublishSpec RuntimeSpec { get; init; } = new();

    /// <summary>Whether the SPDK driver pack is included in the preamble.</summary>
    public bool IncludeSpdk { get; init; } = true;

    /// <summary>
    /// SPDK transport mode. Valid values: <c>"nvme-local"</c>, <c>"nvme-of"</c>, <c>"stub"</c>.
    /// </summary>
    public string SpdkTransport { get; init; } = "nvme-local";

    /// <summary>Preamble feature flags (compression, signature, encryption).</summary>
    public PreambleFlags PreambleFlags { get; init; }

    /// <summary>Target CPU architecture for all preamble payloads.</summary>
    public TargetArchitecture Architecture { get; init; } = TargetArchitecture.X8664;

    /// <summary>Whether to append an Ed25519 digital signature to the preamble.</summary>
    public bool SignWithEd25519 { get; init; }

    /// <summary>Whether to LZ4-frame compress the preamble content sections.</summary>
    public bool CompressPreamble { get; init; }

    /// <summary>Whether the runtime payload is TPM-encrypted at rest.</summary>
    public bool EncryptRuntime { get; init; }

    /// <summary>
    /// Creates the <c>server-full</c> profile: all drivers, full plugin set, SPDK nvme-local,
    /// x86_64 architecture. This is the standard bare-metal server deployment.
    /// </summary>
    public static PreambleCompositionProfile ServerFull() => new()
    {
        ProfileType = PreambleProfileType.ServerFull,
        Name = "server-full",
        Description = "Full server deployment with all drivers, complete plugin set, " +
                      "SPDK nvme-local transport, targeting x86_64 bare-metal servers.",
        KernelSpec = StrippedKernelBuildSpec.ServerFull(),
        RuntimeSpec = NativeAotPublishSpec.ServerFull(),
        IncludeSpdk = true,
        SpdkTransport = "nvme-local",
        PreambleFlags = Preamble.PreambleFlags.None,
        Architecture = TargetArchitecture.X8664,
        SignWithEd25519 = false,
        CompressPreamble = false,
        EncryptRuntime = false,
    };

    /// <summary>
    /// Creates the <c>embedded-minimal</c> profile: minimal drivers (no display), core plugins
    /// only (Storage, Encryption, RAID, Filesystem), SPDK nvme-local, arm64 architecture.
    /// </summary>
    public static PreambleCompositionProfile EmbeddedMinimal() => new()
    {
        ProfileType = PreambleProfileType.EmbeddedMinimal,
        Name = "embedded-minimal",
        Description = "Minimal embedded deployment for edge/IoT: no display drivers, " +
                      "core plugins only, SPDK nvme-local, targeting ARM64.",
        KernelSpec = StrippedKernelBuildSpec.EmbeddedMinimal(),
        RuntimeSpec = NativeAotPublishSpec.EmbeddedMinimal(),
        IncludeSpdk = true,
        SpdkTransport = "nvme-local",
        PreambleFlags = Preamble.PreambleFlags.None,
        Architecture = TargetArchitecture.Aarch64,
        SignWithEd25519 = false,
        CompressPreamble = false,
        EncryptRuntime = false,
    };

    /// <summary>
    /// Creates the <c>airgap-readonly</c> profile: no NIC, read-only plugin subset,
    /// SPDK nvme-local, x86_64, Ed25519-signed for secure boot verification.
    /// </summary>
    public static PreambleCompositionProfile AirgapReadonly() => new()
    {
        ProfileType = PreambleProfileType.AirgapReadonly,
        Name = "airgap-readonly",
        Description = "Air-gapped read-only deployment: no NIC drivers, read-only plugin subset, " +
                      "Ed25519-signed preamble for integrity verification, targeting x86_64.",
        KernelSpec = StrippedKernelBuildSpec.AirgapReadonly(),
        RuntimeSpec = NativeAotPublishSpec.RecoveryReadonly(),
        IncludeSpdk = true,
        SpdkTransport = "nvme-local",
        PreambleFlags = Preamble.PreambleFlags.SignaturePresent,
        Architecture = TargetArchitecture.X8664,
        SignWithEd25519 = true,
        CompressPreamble = false,
        EncryptRuntime = false,
    };
}
