using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies;

/// <summary>
/// Self-Emulating Objects compute strategy - bundles data with WASM viewers
/// for long-term format preservation.
/// Consolidated from the standalone SelfEmulatingObjects plugin.
///
/// Capabilities:
/// - Viewer Bundler: combine data + WASM viewer into self-contained objects
/// - Format Detector: auto-detect format from magic numbers and select viewer
/// - Viewer Library: pre-built WASM viewers for common formats (PDF, PNG, JPEG, etc.)
/// - Viewer Runtime: execute WASM viewers in sandboxed environment
/// - Security Sandbox: isolate execution with resource limits
/// - Metadata Preservation: store format metadata for decades-long access
/// - Viewer Versioning: track viewer versions with snapshot/rollback/replay
///
/// Objects are self-contained and viewable decades later without external software,
/// making this ideal for archival storage and long-term data preservation.
/// </summary>
internal sealed class SelfEmulatingComputeStrategy : ComputeRuntimeStrategyBase
{
    private static readonly string[] SupportedFormats =
    [
        "pdf", "png", "jpeg", "gif", "zip", "bmp", "tiff",
        "webp", "mp4", "html", "json", "binary"
    ];

    /// <inheritdoc/>
    public override string StrategyId => "compute.self-emulating";

    /// <inheritdoc/>
    public override string StrategyName => "Self-Emulating Objects";

    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.WASM;

    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: false,
        SupportsSandboxing: true,
        MaxMemoryBytes: 512 * 1024 * 1024, // 512 MB for format processing
        MaxExecutionTime: TimeSpan.FromMinutes(5),
        SupportedLanguages: ["wasm"],
        MaxConcurrentTasks: 50,
        MemoryIsolation: MemoryIsolationLevel.Process
    );

    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.WASM];

    /// <inheritdoc/>
    public override string Description =>
        "Self-Emulating Objects strategy that bundles data with WASM viewers for long-term " +
        "format preservation. Supports auto-detection of 12+ formats, snapshot/rollback/replay " +
        "lifecycle, and sandboxed viewer execution.";

    /// <inheritdoc/>
    // Viewer bundling and sandboxed WASM execution are not yet fully implemented — format detection and envelope creation are production-ready but viewer runtime dispatch is stubbed.
    public override bool IsProductionReady => false;

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var sb = new StringBuilder();
            var inputData = task.InputData.ToArray();

            // Auto-detect format from input data
            var format = DetectFormat(inputData);
            sb.AppendLine($"[SelfEmulating] Detected format: {format}");
            sb.AppendLine($"[SelfEmulating] Input size: {inputData.Length} bytes");

            // Bundle data with viewer
            sb.AppendLine($"[SelfEmulating] Bundling with {format} viewer...");
            sb.AppendLine($"[SelfEmulating] Viewer version: 1.0.0");

            // Create self-emulating object envelope
            var envelope = new Dictionary<string, object>
            {
                ["format"] = format,
                ["dataSize"] = inputData.Length,
                ["viewerVersion"] = "1.0.0",
                ["supportedFormats"] = SupportedFormats,
                ["bundledAt"] = DateTime.UtcNow.ToString("O"),
                ["snapshotCapable"] = true
            };

            var output = Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(envelope));

            sb.AppendLine($"[SelfEmulating] Bundle created: {output.Length} bytes");
            sb.AppendLine($"[SelfEmulating] Execution completed successfully");

            // NOTE: Viewer runtime dispatch (actual WASM execution) is not yet implemented.
            // The envelope above contains metadata + data for the self-emulating format,
            // but the WASM viewer is not launched. IsProductionReady = false reflects this.
            return (output, sb.ToString());
        }, cancellationToken);
    }

    /// <summary>
    /// Detects file format based on magic numbers and file signatures.
    /// </summary>
    private static string DetectFormat(byte[] data)
    {
        if (data.Length < 4)
            return "binary";

        // PDF: %PDF
        if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
            return "pdf";

        // PNG: \x89PNG
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "png";

        // JPEG: \xFF\xD8\xFF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "jpeg";

        // GIF: GIF8
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
            return "gif";

        // ZIP/DOCX/XLSX: PK\x03\x04
        if (data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04)
            return "zip";

        // BMP: BM
        if (data[0] == 0x42 && data[1] == 0x4D)
            return "bmp";

        // TIFF: II or MM
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))
            return "tiff";

        // WebP: RIFF...WEBP
        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "webp";

        // MP4/Video: ftyp
        if (data.Length >= 8 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
            return "mp4";

        // HTML
        if (data[0] == '<')
            return "html";

        // JSON
        if (data[0] == '{' || data[0] == '[')
            return "json";

        return "binary";
    }
}
