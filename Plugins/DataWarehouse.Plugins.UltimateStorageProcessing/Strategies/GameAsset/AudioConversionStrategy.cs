using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.GameAsset;

/// <summary>
/// Game audio conversion strategy using ffmpeg for audio format transcoding.
/// Supports PCM to OGG/MP3/AAC/Opus conversion, sample rate conversion, channel mixing,
/// and loudness normalization via the -loudnorm filter.
/// </summary>
internal sealed class AudioConversionStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "gameasset-audio";

    /// <inheritdoc/>
    public override string Name => "Game Audio Conversion Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum },
        MaxQueryComplexity = 4
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var outputFormat = CliProcessHelper.GetOption<string>(query, "outputFormat") ?? "ogg";
        var sampleRate = CliProcessHelper.GetOption<int>(query, "sampleRate");
        var channels = CliProcessHelper.GetOption<int>(query, "channels");
        var normalize = CliProcessHelper.GetOption<bool>(query, "normalize");
        var bitrate = CliProcessHelper.GetOption<string>(query, "bitrate") ?? "128k";

        var codec = outputFormat switch
        {
            "ogg" => "libvorbis",
            "mp3" => "libmp3lame",
            "aac" => "aac",
            "opus" => "libopus",
            "flac" => "flac",
            _ => "libvorbis"
        };

        var outputPath = Path.ChangeExtension(query.Source, $".{outputFormat}");
        var args = $"-i \"{query.Source}\" -c:a {codec} -b:a {bitrate}";
        if (sampleRate > 0) args += $" -ar {sampleRate}";
        if (channels > 0) args += $" -ac {channels}";
        if (normalize) args += " -af loudnorm=I=-16:TP=-1.5:LRA=11";
        args += $" -y \"{outputPath}\"";

        var result = await CliProcessHelper.RunAsync("ffmpeg", args, Path.GetDirectoryName(query.Source), ct: ct);

        return CliProcessHelper.ToProcessingResult(result, query.Source, "ffmpeg audio", new Dictionary<string, object?>
        {
            ["outputFormat"] = outputFormat, ["codec"] = codec, ["bitrate"] = bitrate,
            ["sampleRate"] = sampleRate, ["channels"] = channels, ["normalize"] = normalize,
            ["outputPath"] = outputPath, ["outputExists"] = File.Exists(outputPath),
            ["outputSize"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".wav", ".ogg", ".mp3", ".aac", ".opus", ".flac" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".wav", ".ogg", ".mp3", ".flac" }, ct);
    }
}
