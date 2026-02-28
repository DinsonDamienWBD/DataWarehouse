using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Media;

/// <summary>
/// ImageMagick strategy that invokes "magick" (ImageMagick 7) via Process for image operations.
/// Supports convert, resize, crop, format change, -quality setting, and batch mode with mogrify.
/// </summary>
internal sealed class ImageMagickStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "media-imagemagick";

    /// <inheritdoc/>
    public override string Name => "ImageMagick Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 5
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var operation = CliProcessHelper.GetOption<string>(query, "operation") ?? "convert";
        var resize = CliProcessHelper.GetOption<string>(query, "resize");
        var quality = CliProcessHelper.GetOption<int>(query, "quality");
        var outputFormat = CliProcessHelper.GetOption<string>(query, "outputFormat");
        var crop = CliProcessHelper.GetOption<string>(query, "crop");

        // Allowlist operations to prevent injection via the operation parameter
        var allowedOperations = new HashSet<string>(StringComparer.Ordinal) { "convert", "mogrify" };
        CliProcessHelper.ValidateAllowlist(operation, "operation", allowedOperations);

        // Validate geometry strings (resize, crop) — allow digits, x, +, -, %, ^, !
        static void ValidateGeometry(string? val, string name)
        {
            if (val == null) return;
            foreach (var c in val)
            {
                if (!char.IsDigit(c) && "xX+-.%^!@><".IndexOf(c) < 0)
                    throw new ArgumentException($"'{name}' contains invalid character '{c}'.", name);
            }
        }
        ValidateGeometry(resize, "resize");
        ValidateGeometry(crop, "crop");

        // Validate outputFormat as identifier
        if (outputFormat != null) CliProcessHelper.ValidateIdentifier(outputFormat, "outputFormat");

        // Validate quality range
        if (quality < 0 || quality > 100)
            throw new ArgumentException("'quality' must be between 0 and 100.", nameof(quality));

        string args;
        string outputPath;

        if (operation == "mogrify")
        {
            // Batch in-place operation
            args = "mogrify";
            if (resize != null) args += $" -resize {resize}";
            if (quality > 0) args += $" -quality {quality}";
            args += $" \"{query.Source}\"";
            outputPath = query.Source;
        }
        else
        {
            outputPath = outputFormat != null
                ? Path.ChangeExtension(query.Source, $".{outputFormat}")
                : query.Source + ".processed" + Path.GetExtension(query.Source);

            args = $"convert \"{query.Source}\"";
            if (resize != null) args += $" -resize {resize}";
            if (quality > 0) args += $" -quality {quality}";
            if (crop != null) args += $" -crop {crop}";
            args += $" \"{outputPath}\"";
        }

        var result = await CliProcessHelper.RunAsync("magick", args, Path.GetDirectoryName(query.Source), ct: ct);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "magick", new Dictionary<string, object?>
        {
            ["operation"] = operation, ["resize"] = resize, ["quality"] = quality,
            ["crop"] = crop, ["outputFormat"] = outputFormat, ["outputPath"] = outputPath,
            ["outputExists"] = File.Exists(outputPath),
            ["outputSize"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".svg" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }, ct);
    }
}
