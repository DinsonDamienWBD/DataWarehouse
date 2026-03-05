using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// Generates deterministic shell scripts and Dockerfiles for building a stripped Linux kernel
/// suitable for the DWVD bootable preamble (VOPT-64).
/// </summary>
/// <remarks>
/// <para>This is a C# helper that emits build scripts — it does NOT execute any build pipeline.
/// The generated scripts are consumed by DevOps tooling to produce a bzImage that is then
/// embedded in the preamble region by <see cref="PreambleWriter"/>.</para>
/// <para>Key properties of generated scripts:</para>
/// <list type="bullet">
/// <item><description>Deterministic: same <see cref="StrippedKernelBuildSpec"/> always produces identical output.</description></item>
/// <item><description>Validated: output image size is checked against <see cref="StrippedKernelBuildSpec.MaxBzImageBytes"/>.</description></item>
/// <item><description>Portable: Docker variant enables reproducible builds on any CI host.</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-64 kernel build script generator")]
public sealed class KernelBuildScriptGenerator
{
    /// <summary>
    /// Validates a <see cref="StrippedKernelBuildSpec"/> for correctness and returns any warnings.
    /// </summary>
    /// <param name="spec">The build spec to validate.</param>
    /// <returns>A list of non-fatal warnings. An empty list indicates no warnings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// The spec is invalid: unsupported architecture, missing vfio-pci, or non-positive size limit.
    /// </exception>
    public IReadOnlyList<string> ValidateSpec(StrippedKernelBuildSpec spec)
    {
        if (spec is null)
            throw new ArgumentNullException(nameof(spec));

        // Architecture must be a defined enum value
        if (!Enum.IsDefined(typeof(TargetArchitecture), spec.Architecture))
            throw new ArgumentException(
                $"Unsupported target architecture: {spec.Architecture}.", nameof(spec));

        // vfio-pci is required for SPDK handoff — preamble boot is pointless without it
        if (!spec.IncludeVfioPci)
            throw new ArgumentException(
                "IncludeVfioPci must be enabled. VFIO-PCI is required for SPDK user-space " +
                "driver handoff during preamble boot.", nameof(spec));

        // MaxBzImageBytes must be positive and fit in uint32 (PreambleHeader.KernelSize is uint32)
        if (spec.MaxBzImageBytes <= 0)
            throw new ArgumentException(
                "MaxBzImageBytes must be a positive value.", nameof(spec));

        if (spec.MaxBzImageBytes > uint.MaxValue)
            throw new ArgumentException(
                $"MaxBzImageBytes ({spec.MaxBzImageBytes}) exceeds the uint32 KernelSize field " +
                $"in PreambleHeader (max {uint.MaxValue} bytes).", nameof(spec));

        // Kernel version must be non-empty
        if (string.IsNullOrWhiteSpace(spec.KernelVersion))
            throw new ArgumentException(
                "KernelVersion must be a non-empty version string (e.g., \"6.6\").", nameof(spec));

        var warnings = new List<string>();

        // Warn if both NIC and USB are disabled — no I/O path to the host
        if (!spec.IncludeNicDrivers && !spec.IncludeUsbDrivers)
        {
            warnings.Add(
                "Both NIC and USB drivers are disabled. The kernel will have no external I/O " +
                "path — only serial console and vfio-pci will be available.");
        }

        return warnings;
    }

    /// <summary>
    /// Generates a complete bash script that downloads, configures, and builds a stripped
    /// Linux kernel from source. The script is self-contained and deterministic given the
    /// same <paramref name="spec"/>.
    /// </summary>
    /// <param name="spec">The kernel build specification.</param>
    /// <returns>A UTF-8 bash script string ready to be written to a <c>.sh</c> file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The spec fails validation.</exception>
    public string GenerateBuildScript(StrippedKernelBuildSpec spec)
    {
        ValidateSpec(spec);

        var sb = new StringBuilder(4096);
        var majorMinor = GetMajorVersion(spec.KernelVersion);
        var imageTarget = GetImageTarget(spec.Architecture);
        var imagePath = GetImagePath(spec.Architecture, spec.KernelVersion);
        var outputName = GetOutputFileName(spec.Architecture);

        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# =============================================================================");
        sb.AppendLine("# DWVD Preamble — Stripped Linux Kernel Build Script (VOPT-64)");
        sb.AppendLine($"# Generated by KernelBuildScriptGenerator at {{timestamp}}");
        sb.AppendLine($"# Kernel: {spec.KernelVersion} | Arch: {spec.Architecture} | Max: {spec.MaxBzImageBytes} bytes");
        sb.AppendLine("# =============================================================================");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();

        // Variables
        sb.AppendLine($"KERNEL_VERSION=\"{spec.KernelVersion}\"");
        sb.AppendLine($"KERNEL_MAJOR=\"{majorMinor}\"");
        sb.AppendLine($"MAX_IMAGE_BYTES={spec.MaxBzImageBytes}");
        sb.AppendLine($"BUILD_DIR=\"$(pwd)/kernel-build\"");
        sb.AppendLine($"OUTPUT_DIR=\"$(pwd)/kernel-output\"");
        sb.AppendLine($"TARBALL_URL=\"https://cdn.kernel.org/pub/linux/kernel/v${{KERNEL_MAJOR}}.x/linux-${{KERNEL_VERSION}}.tar.xz\"");
        sb.AppendLine();

        // Step 1: Download
        sb.AppendLine("echo \"[1/6] Downloading kernel source...\"");
        sb.AppendLine("mkdir -p \"$BUILD_DIR\" \"$OUTPUT_DIR\"");
        sb.AppendLine("if [ ! -f \"$BUILD_DIR/linux-${KERNEL_VERSION}.tar.xz\" ]; then");
        sb.AppendLine("    curl -fSL \"$TARBALL_URL\" -o \"$BUILD_DIR/linux-${KERNEL_VERSION}.tar.xz\"");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Step 2: Extract
        sb.AppendLine("echo \"[2/6] Extracting source...\"");
        sb.AppendLine("cd \"$BUILD_DIR\"");
        sb.AppendLine("if [ ! -d \"linux-${KERNEL_VERSION}\" ]; then");
        sb.AppendLine("    tar xf \"linux-${KERNEL_VERSION}.tar.xz\"");
        sb.AppendLine("fi");
        sb.AppendLine("cd \"linux-${KERNEL_VERSION}\"");
        sb.AppendLine();

        // Step 3: Write .config via heredoc
        sb.AppendLine("echo \"[3/6] Writing kernel config...\"");
        sb.AppendLine("cat > .config << 'KCONFIG_EOF'");
        foreach (var entry in spec.GenerateKconfigEntries())
        {
            sb.AppendLine(entry);
        }
        sb.AppendLine("KCONFIG_EOF");
        sb.AppendLine();

        // Step 4: Resolve dependencies
        sb.AppendLine("echo \"[4/6] Resolving config dependencies...\"");
        sb.AppendLine("make olddefconfig");
        sb.AppendLine();

        // Step 5: Build
        sb.AppendLine($"echo \"[5/6] Building {imageTarget}...\"");
        sb.AppendLine($"make -j\"$(nproc)\" {imageTarget}");
        sb.AppendLine();

        // Step 6: Validate and copy
        sb.AppendLine("echo \"[6/6] Validating output...\"");
        sb.AppendLine($"IMAGE_PATH=\"{imagePath}\"");
        sb.AppendLine("IMAGE_SIZE=$(stat -c%s \"$IMAGE_PATH\" 2>/dev/null || stat -f%z \"$IMAGE_PATH\")");
        sb.AppendLine();
        sb.AppendLine("if [ \"$IMAGE_SIZE\" -gt \"$MAX_IMAGE_BYTES\" ]; then");
        sb.AppendLine("    echo \"ERROR: Kernel image $IMAGE_SIZE bytes exceeds limit of $MAX_IMAGE_BYTES bytes\" >&2");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine($"cp \"$IMAGE_PATH\" \"$OUTPUT_DIR/{outputName}\"");
        sb.AppendLine($"SHA256=$(sha256sum \"$OUTPUT_DIR/{outputName}\" | awk '{{print $1}}')");
        sb.AppendLine();
        sb.AppendLine($"echo \"SUCCESS: {outputName} ($IMAGE_SIZE bytes)\"");
        sb.AppendLine("echo \"SHA256: $SHA256\"");
        sb.AppendLine($"echo \"Output: $OUTPUT_DIR/{outputName}\"");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a Dockerfile and associated build script for reproducible kernel builds
    /// in a containerized environment. The Docker build produces only the kernel image
    /// as output via multi-stage build.
    /// </summary>
    /// <param name="spec">The kernel build specification.</param>
    /// <returns>
    /// A string containing the Dockerfile content. The file uses multi-stage build to
    /// minimize the output layer to just the kernel image.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The spec fails validation.</exception>
    public string GenerateDockerBuildScript(StrippedKernelBuildSpec spec)
    {
        ValidateSpec(spec);

        var sb = new StringBuilder(4096);
        var majorMinor = GetMajorVersion(spec.KernelVersion);
        var imageTarget = GetImageTarget(spec.Architecture);
        var imagePath = GetImagePath(spec.Architecture, spec.KernelVersion);
        var outputName = GetOutputFileName(spec.Architecture);

        sb.AppendLine("# =============================================================================");
        sb.AppendLine("# DWVD Preamble — Stripped Linux Kernel Dockerfile (VOPT-64)");
        sb.AppendLine($"# Kernel: {spec.KernelVersion} | Arch: {spec.Architecture} | Max: {spec.MaxBzImageBytes} bytes");
        sb.AppendLine("# Usage: docker build -t dwvd-kernel . && docker cp $(docker create dwvd-kernel):/output/ .");
        sb.AppendLine("# =============================================================================");
        sb.AppendLine();

        // Stage 1: Build environment
        sb.AppendLine("# Stage 1: Build environment with all toolchain dependencies");
        sb.AppendLine("FROM ubuntu:22.04 AS builder");
        sb.AppendLine();
        sb.AppendLine("ENV DEBIAN_FRONTEND=noninteractive");
        sb.AppendLine("RUN apt-get update && apt-get install -y --no-install-recommends \\");
        sb.AppendLine("    build-essential \\");
        sb.AppendLine("    flex \\");
        sb.AppendLine("    bison \\");
        sb.AppendLine("    libelf-dev \\");
        sb.AppendLine("    bc \\");
        sb.AppendLine("    libssl-dev \\");
        sb.AppendLine("    curl \\");
        sb.AppendLine("    xz-utils \\");
        sb.AppendLine("    cpio \\");
        sb.AppendLine("    && rm -rf /var/lib/apt/lists/*");
        sb.AppendLine();

        // Download and extract
        sb.AppendLine($"ARG KERNEL_VERSION={spec.KernelVersion}");
        sb.AppendLine($"ARG KERNEL_MAJOR={majorMinor}");
        sb.AppendLine("WORKDIR /build");
        sb.AppendLine("RUN curl -fSL \"https://cdn.kernel.org/pub/linux/kernel/v${KERNEL_MAJOR}.x/linux-${KERNEL_VERSION}.tar.xz\" \\");
        sb.AppendLine("    -o linux.tar.xz && tar xf linux.tar.xz && rm linux.tar.xz");
        sb.AppendLine();

        // Write kconfig
        sb.AppendLine($"WORKDIR /build/linux-${{KERNEL_VERSION}}");
        sb.AppendLine("COPY <<-'KCONFIG_EOF' .config");
        foreach (var entry in spec.GenerateKconfigEntries())
        {
            sb.AppendLine(entry);
        }
        sb.AppendLine("KCONFIG_EOF");
        sb.AppendLine();

        // Build
        sb.AppendLine("RUN make olddefconfig");
        sb.AppendLine($"RUN make -j\"$(nproc)\" {imageTarget}");
        sb.AppendLine();

        // Validate size
        sb.AppendLine($"RUN IMAGE_SIZE=$(stat -c%s \"{imagePath}\") && \\");
        sb.AppendLine($"    if [ \"$IMAGE_SIZE\" -gt {spec.MaxBzImageBytes} ]; then \\");
        sb.AppendLine("        echo \"ERROR: Image $IMAGE_SIZE bytes exceeds limit\" >&2 && exit 1; \\");
        sb.AppendLine("    fi");
        sb.AppendLine();

        // Stage 2: Output only the kernel image
        sb.AppendLine("# Stage 2: Minimal output — only the kernel image");
        sb.AppendLine("FROM scratch AS output");
        sb.AppendLine($"COPY --from=builder /build/linux-{spec.KernelVersion}/{imagePath.TrimStart('.')} /output/{outputName}");

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the major version number from a kernel version string for use in download URLs.
    /// </summary>
    private static string GetMajorVersion(string kernelVersion)
    {
        var dotIndex = kernelVersion.IndexOf('.', StringComparison.Ordinal);
        return dotIndex > 0
            ? kernelVersion.Substring(0, dotIndex)
            : kernelVersion;
    }

    /// <summary>
    /// Returns the <c>make</c> target for the kernel image based on target architecture.
    /// </summary>
    private static string GetImageTarget(TargetArchitecture arch) => arch switch
    {
        TargetArchitecture.X8664 => "bzImage",
        TargetArchitecture.Aarch64 => "Image.gz",
        TargetArchitecture.RiscV64 => "Image.gz",
        _ => "bzImage",
    };

    /// <summary>
    /// Returns the relative path to the built kernel image within the source tree.
    /// </summary>
    private static string GetImagePath(TargetArchitecture arch, string kernelVersion) => arch switch
    {
        TargetArchitecture.X8664 => $"arch/x86/boot/bzImage",
        TargetArchitecture.Aarch64 => $"arch/arm64/boot/Image.gz",
        TargetArchitecture.RiscV64 => $"arch/riscv/boot/Image.gz",
        _ => $"arch/x86/boot/bzImage",
    };

    /// <summary>
    /// Returns a descriptive output file name for the kernel image.
    /// </summary>
    private static string GetOutputFileName(TargetArchitecture arch) => arch switch
    {
        TargetArchitecture.X8664 => "dwvd-kernel-x86_64.bzImage",
        TargetArchitecture.Aarch64 => "dwvd-kernel-aarch64.Image.gz",
        TargetArchitecture.RiscV64 => "dwvd-kernel-riscv64.Image.gz",
        _ => "dwvd-kernel.bzImage",
    };
}
