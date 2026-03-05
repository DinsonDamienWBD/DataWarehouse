using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Result of computing the preamble layout from a <see cref="PreambleCompositionProfile"/>.
/// Contains all byte offsets and sizes needed to write the preamble region.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-66 preamble layout result")]
public readonly struct PreambleLayoutResult : IEquatable<PreambleLayoutResult>
{
    /// <summary>Size of the BLAKE3 content hash in bytes.</summary>
    private const uint ContentHashSize = 32;

    /// <summary>Size of an Ed25519 signature in bytes.</summary>
    private const uint Ed25519SignatureSize = 64;

    /// <summary>Byte offset of the micro-kernel payload within the preamble.</summary>
    public uint KernelOffset { get; init; }

    /// <summary>Size of the micro-kernel payload in bytes.</summary>
    public uint KernelSize { get; init; }

    /// <summary>Byte offset of the SPDK driver pack within the preamble.</summary>
    public uint SpdkOffset { get; init; }

    /// <summary>Size of the SPDK driver pack in bytes (0 if SPDK not included).</summary>
    public uint SpdkSize { get; init; }

    /// <summary>Byte offset of the NativeAOT runtime within the preamble.</summary>
    public uint RuntimeOffset { get; init; }

    /// <summary>Size of the NativeAOT runtime in bytes.</summary>
    public uint RuntimeSize { get; init; }

    /// <summary>Byte offset of the 32-byte BLAKE3 content hash.</summary>
    public uint ContentHashOffset { get; init; }

    /// <summary>Total preamble size in bytes (before VDE alignment padding).</summary>
    public ulong PreambleTotalSize { get; init; }

    /// <summary>
    /// Byte offset of VDE Block 0, 4 KiB-aligned and >= <see cref="PreambleTotalSize"/>.
    /// </summary>
    public ulong VdeOffset { get; init; }

    /// <summary>Estimated total preamble size in megabytes for informational display.</summary>
    public double EstimatedTotalMB => PreambleTotalSize / (1024.0 * 1024.0);

    /// <summary>
    /// Creates a <see cref="PreambleHeader"/> from this layout result and the given profile.
    /// The header checksum field is set to zero; callers must compute and patch the checksum
    /// after serialization of bytes [0..55].
    /// </summary>
    /// <param name="profile">The composition profile used to determine flags and architecture.</param>
    /// <returns>A fully populated <see cref="PreambleHeader"/> ready for serialization.</returns>
    public PreambleHeader ToHeader(PreambleCompositionProfile profile)
    {
        var flags = PreambleCompositionEngine.ComputeFlags(profile);

        return new PreambleHeader(
            magic: PreambleHeader.ExpectedMagic,
            preambleVersion: 1,
            flags: flags,
            contentHashOffset: ContentHashOffset,
            preambleTotalSize: PreambleTotalSize,
            vdeOffset: VdeOffset,
            kernelOffset: KernelOffset,
            kernelSize: KernelSize,
            spdkOffset: SpdkOffset,
            spdkSize: SpdkSize,
            runtimeOffset: RuntimeOffset,
            runtimeSize: RuntimeSize,
            headerChecksum: 0); // Caller computes BLAKE3(bytes[0..55]) after serialization
    }

    /// <inheritdoc />
    public bool Equals(PreambleLayoutResult other) =>
        KernelOffset == other.KernelOffset
        && KernelSize == other.KernelSize
        && SpdkOffset == other.SpdkOffset
        && SpdkSize == other.SpdkSize
        && RuntimeOffset == other.RuntimeOffset
        && RuntimeSize == other.RuntimeSize
        && ContentHashOffset == other.ContentHashOffset
        && PreambleTotalSize == other.PreambleTotalSize
        && VdeOffset == other.VdeOffset;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PreambleLayoutResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(KernelOffset);
        h.Add(KernelSize);
        h.Add(SpdkOffset);
        h.Add(SpdkSize);
        h.Add(RuntimeOffset);
        h.Add(RuntimeSize);
        h.Add(ContentHashOffset);
        h.Add(PreambleTotalSize);
        h.Add(VdeOffset);
        return h.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(PreambleLayoutResult left, PreambleLayoutResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(PreambleLayoutResult left, PreambleLayoutResult right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        $"PreambleLayout(Kernel@{KernelOffset}+{KernelSize}, SPDK@{SpdkOffset}+{SpdkSize}, " +
        $"Runtime@{RuntimeOffset}+{RuntimeSize}, Hash@{ContentHashOffset}, " +
        $"Total={PreambleTotalSize}, VDE@0x{VdeOffset:X}, ~{EstimatedTotalMB:F1}MB)";
}

/// <summary>
/// Validates <see cref="PreambleCompositionProfile"/> instances for consistency and computes
/// the preamble byte layout including all payload offsets and the 4 KiB-aligned VDE start
/// position (VOPT-66).
/// </summary>
/// <remarks>
/// <para>The engine performs three main operations:</para>
/// <list type="bullet">
/// <item><description><see cref="ValidateProfile"/>: Checks for architecture mismatches, flag
/// inconsistencies, and SPDK/vfio-pci dependency violations.</description></item>
/// <item><description><see cref="ComputeLayout"/>: Calculates byte offsets for kernel, SPDK,
/// runtime, content hash, and optional signature, then aligns to 4 KiB.</description></item>
/// <item><description><see cref="ComputeFlags"/>: Builds the combined flags ushort from profile
/// settings including feature bits and architecture encoding.</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-66 preamble composition engine")]
public sealed class PreambleCompositionEngine
{
    /// <summary>4 KiB alignment boundary for VDE Block 0.</summary>
    private const ulong AlignmentBytes = 4096;

    /// <summary>Size of the BLAKE3 content hash in bytes.</summary>
    private const uint ContentHashSize = 32;

    /// <summary>Size of an Ed25519 signature in bytes.</summary>
    private const uint Ed25519SignatureSize = 64;

    /// <summary>
    /// Computes the preamble byte layout from a composition profile and payload sizes.
    /// </summary>
    /// <param name="profile">The composition profile defining which components are included.</param>
    /// <param name="kernelSizeBytes">Size of the micro-kernel bzImage in bytes.</param>
    /// <param name="spdkSizeBytes">Size of the SPDK driver pack in bytes (ignored if SPDK not included).</param>
    /// <param name="runtimeSizeBytes">Size of the NativeAOT runtime binary in bytes.</param>
    /// <returns>A <see cref="PreambleLayoutResult"/> with all computed offsets and sizes.</returns>
    /// <exception cref="OverflowException">
    /// Any uint32 offset exceeds <see cref="uint.MaxValue"/>.
    /// </exception>
    public PreambleLayoutResult ComputeLayout(
        PreambleCompositionProfile profile,
        uint kernelSizeBytes,
        uint spdkSizeBytes,
        uint runtimeSizeBytes)
    {
        // Kernel starts immediately after the 64-byte preamble header
        uint kernelOffset = (uint)PreambleHeader.SerializedSize;

        // SPDK follows the kernel (or overlaps kernel offset if not included)
        uint effectiveSpdkSize;
        uint spdkOffset;

        if (profile.IncludeSpdk)
        {
            spdkOffset = checked(kernelOffset + kernelSizeBytes);
            effectiveSpdkSize = spdkSizeBytes;
        }
        else
        {
            // When SPDK is not included, its offset equals kernel offset (no gap) and size is 0
            spdkOffset = kernelOffset;
            effectiveSpdkSize = 0;
        }

        // Runtime follows SPDK (or kernel if no SPDK)
        uint runtimeOffset = checked(spdkOffset + effectiveSpdkSize);

        // Content hash (32-byte BLAKE3) follows runtime
        uint contentHashOffset = checked(runtimeOffset + runtimeSizeBytes);

        // Total preamble size includes the hash
        ulong preambleTotalSize = (ulong)contentHashOffset + ContentHashSize;

        // Optional Ed25519 signature appended after the hash
        if (profile.SignWithEd25519)
        {
            preambleTotalSize += Ed25519SignatureSize;
        }

        // VDE Block 0 offset must be 4 KiB-aligned and >= preambleTotalSize
        ulong vdeOffset = AlignUp(preambleTotalSize, AlignmentBytes);

        // Validate all uint32 offsets didn't overflow (checked arithmetic handles this,
        // but also verify the aggregate doesn't exceed addressable range)
        if (vdeOffset > (ulong)uint.MaxValue * 2)
        {
            throw new OverflowException(
                $"Computed VDE offset ({vdeOffset}) is unreasonably large. " +
                "Check that payload sizes are within expected bounds.");
        }

        return new PreambleLayoutResult
        {
            KernelOffset = kernelOffset,
            KernelSize = kernelSizeBytes,
            SpdkOffset = spdkOffset,
            SpdkSize = effectiveSpdkSize,
            RuntimeOffset = runtimeOffset,
            RuntimeSize = runtimeSizeBytes,
            ContentHashOffset = contentHashOffset,
            PreambleTotalSize = preambleTotalSize,
            VdeOffset = vdeOffset,
        };
    }

    /// <summary>
    /// Validates a <see cref="PreambleCompositionProfile"/> for consistency.
    /// </summary>
    /// <param name="profile">The profile to validate.</param>
    /// <returns>
    /// A list of validation error/warning messages. An empty list indicates the profile is valid.
    /// </returns>
    public IReadOnlyList<string> ValidateProfile(PreambleCompositionProfile profile)
    {
        var errors = new List<string>();

        // Architecture consistency: kernel and runtime must target the same arch
        if (profile.KernelSpec.Architecture != profile.RuntimeSpec.Architecture)
        {
            errors.Add(
                $"Architecture mismatch: KernelSpec targets {profile.KernelSpec.Architecture} " +
                $"but RuntimeSpec targets {profile.RuntimeSpec.Architecture}. " +
                "Both must target the same CPU architecture.");
        }

        if (profile.Architecture != profile.KernelSpec.Architecture)
        {
            errors.Add(
                $"Profile architecture ({profile.Architecture}) does not match " +
                $"KernelSpec architecture ({profile.KernelSpec.Architecture}).");
        }

        // SPDK transport consistency
        if (!profile.IncludeSpdk
            && !string.Equals(profile.SpdkTransport, "stub", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"SPDK is not included but SpdkTransport is '{profile.SpdkTransport}' " +
                "instead of 'stub'. Set SpdkTransport to 'stub' when IncludeSpdk is false.");
        }

        // SPDK requires vfio-pci in the kernel
        if (profile.IncludeSpdk && !profile.KernelSpec.IncludeVfioPci)
        {
            errors.Add(
                "SPDK is included but KernelSpec.IncludeVfioPci is false. " +
                "SPDK requires the vfio-pci kernel driver for user-space device handoff.");
        }

        // Flag consistency: compression
        if (profile.CompressPreamble
            && (profile.PreambleFlags & PreambleFlags.CompressionActive) == 0)
        {
            errors.Add(
                "CompressPreamble is true but PreambleFlags does not include CompressionActive. " +
                "Set PreambleFlags |= PreambleFlags.CompressionActive.");
        }

        // Flag consistency: signature
        if (profile.SignWithEd25519
            && (profile.PreambleFlags & PreambleFlags.SignaturePresent) == 0)
        {
            errors.Add(
                "SignWithEd25519 is true but PreambleFlags does not include SignaturePresent. " +
                "Set PreambleFlags |= PreambleFlags.SignaturePresent.");
        }

        // Flag consistency: encryption
        if (profile.EncryptRuntime
            && (profile.PreambleFlags & PreambleFlags.EncryptedRuntime) == 0)
        {
            errors.Add(
                "EncryptRuntime is true but PreambleFlags does not include EncryptedRuntime. " +
                "Set PreambleFlags |= PreambleFlags.EncryptedRuntime.");
        }

        // Validate SPDK transport is a known value
        if (profile.IncludeSpdk)
        {
            var transport = profile.SpdkTransport;
            if (!string.Equals(transport, "nvme-local", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(transport, "nvme-of", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(transport, "stub", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"Unknown SpdkTransport '{transport}'. " +
                    "Expected 'nvme-local', 'nvme-of', or 'stub'.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Computes the combined flags ushort for a <see cref="PreambleHeader"/> from profile settings.
    /// Bits 0-2 encode <see cref="PreambleFlags"/>, bits 3-5 encode <see cref="TargetArchitecture"/>.
    /// </summary>
    /// <param name="profile">The composition profile.</param>
    /// <returns>The combined flags value for the preamble header.</returns>
    public static ushort ComputeFlags(PreambleCompositionProfile profile)
    {
        ushort featureBits = (ushort)profile.PreambleFlags; // bits 0-2
        ushort archBits = (ushort)((byte)profile.Architecture << 3); // bits 3-5

        return (ushort)(featureBits | archBits);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> up to the nearest multiple of <paramref name="alignment"/>.
    /// </summary>
    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }
}
