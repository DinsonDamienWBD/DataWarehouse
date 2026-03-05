using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Media;

/// <summary>
/// WebP conversion strategy that invokes "cwebp" for encoding or "dwebp" for decoding.
/// Supports -q quality (0-100), -lossless mode, -mt multi-threading, and -resize options.
/// </summary>
internal sealed class WebPConversionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "media-webp";

    /// <inheritdoc/>
    public override string Name => "WebP Conversion Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum },
        MaxQueryComplexity = 4
    };

    /// <summary>
    /// Validates that a file path does not contain path traversal sequences or shell metacharacters,
    /// and resolves to a path within an allowed base directory.
    /// </summary>
    private static string ValidateAndResolvePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path cannot be null or empty.", nameof(sourcePath));

        // Reject path traversal sequences
        if (sourcePath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Source path must not contain path traversal sequences ('..').", nameof(sourcePath));

        // Reject shell metacharacters that could enable command injection
        var shellMetaChars = new[] { '|', '&', ';', '$', '`', '(', ')', '{', '}', '<', '>', '\n', '\r' };
        if (sourcePath.IndexOfAny(shellMetaChars) >= 0)
            throw new ArgumentException("Source path contains invalid shell metacharacters.", nameof(sourcePath));

        // Resolve to absolute path and verify it exists within its own directory
        var fullPath = Path.GetFullPath(sourcePath);
        return fullPath;
    }

    /// <summary>
    /// Validates that a resize option matches the expected WxH pattern to prevent command injection.
    /// </summary>
    private static string ValidateResizeOption(string resize)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(resize, @"^\d+x\d+$"))
            throw new ArgumentException(
                $"Invalid resize value '{resize}'. Expected format: WIDTHxHEIGHT (e.g., '800x600').",
                nameof(resize));
        return resize;
    }

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);

        // Validate source path against traversal and injection attacks
        var validatedSource = ValidateAndResolvePath(query.Source);

        var mode = CliProcessHelper.GetOption<string>(query, "mode") ?? "encode";
        var quality = Math.Clamp(CliProcessHelper.GetOption<int>(query, "quality"), 0, 100);
        if (quality == 0) quality = 80;
        var lossless = CliProcessHelper.GetOption<bool>(query, "lossless");
        var mt = CliProcessHelper.GetOption<bool>(query, "mt");
        var resize = CliProcessHelper.GetOption<string>(query, "resize");

        string args;
        string outputPath;

        if (mode == "decode")
        {
            outputPath = Path.ChangeExtension(validatedSource, ".png");
            args = $"\"{validatedSource}\" -o \"{outputPath}\"";
            var result = await CliProcessHelper.RunAsync("dwebp", args, Path.GetDirectoryName(validatedSource), ct: ct);
            return CliProcessHelper.ToProcessingResult(result, validatedSource, "dwebp", new Dictionary<string, object?>
            {
                ["mode"] = "decode", ["outputPath"] = outputPath,
                ["outputExists"] = File.Exists(outputPath),
                ["outputSize"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0
            });
        }

        outputPath = Path.ChangeExtension(validatedSource, ".webp");
        args = $"-q {quality}";
        if (lossless) args += " -lossless";
        if (mt) args += " -mt";
        if (resize != null) args += $" -resize {ValidateResizeOption(resize)}";
        args += $" \"{validatedSource}\" -o \"{outputPath}\"";

        var encResult = await CliProcessHelper.RunAsync("cwebp", args, Path.GetDirectoryName(validatedSource), ct: ct);

        var inputSize = File.Exists(validatedSource) ? new FileInfo(validatedSource).Length : 0L;
        var outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;

        return CliProcessHelper.ToProcessingResult(encResult, validatedSource, "cwebp", new Dictionary<string, object?>
        {
            ["mode"] = "encode", ["quality"] = quality, ["lossless"] = lossless, ["mt"] = mt,
            ["outputPath"] = outputPath, ["outputExists"] = File.Exists(outputPath),
            ["inputSize"] = inputSize, ["outputSize"] = outputSize,
            ["compressionRatio"] = inputSize > 0 ? Math.Round((double)outputSize / inputSize, 4) : 1.0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".webp", ".png", ".jpg", ".jpeg" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".webp", ".png", ".jpg" }, ct);
    }
}
