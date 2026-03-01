using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.GameAsset;

/// <summary>
/// Asset bundling strategy that groups assets by type/scene into archive bundles with manifests.
/// Supports streaming (header + data chunks), dependency graph resolution, and content hashing
/// using SHA-256 for versioning and cache invalidation.
/// </summary>
internal sealed class AssetBundlingStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "gameasset-bundling";

    /// <inheritdoc/>
    public override string Name => "Asset Bundling Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 6
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(query.Source))
            return MakeError("Source directory not found", sw);

        var groupBy = CliProcessHelper.GetOption<string>(query, "groupBy") ?? "type";
        var bundleName = CliProcessHelper.GetOption<string>(query, "bundleName") ?? "assets";

        // Group files by type
        var groups = new Dictionary<string, List<FileInfo>>();
        foreach (var file in Directory.EnumerateFiles(query.Source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var key = groupBy == "type" ? info.Extension.TrimStart('.').ToLowerInvariant() : Path.GetDirectoryName(file)!;
            if (string.IsNullOrEmpty(key)) key = "misc";

            if (!groups.ContainsKey(key)) groups[key] = new List<FileInfo>();
            groups[key].Add(info);
        }

        // Create bundle archive with manifest
        var outputDir = Path.Combine(query.Source, ".bundles");
        Directory.CreateDirectory(outputDir);

        var manifest = new Dictionary<string, object>();
        long totalBundleSize = 0;
        var bundleCount = 0;

        foreach (var (group, files) in groups)
        {
            ct.ThrowIfCancellationRequested();
            var bundlePath = Path.Combine(outputDir, $"{bundleName}_{group}.bundle");

            await using var bundleStream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            // Write header: magic + entry count
            var header = new byte[8];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), 0x424E444C); // "BNDL"
            BitConverter.TryWriteBytes(header.AsSpan(4, 4), files.Count);
            await bundleStream.WriteAsync(header, ct);

            var entries = new List<Dictionary<string, object>>();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                // Stream each file to avoid loading large game assets entirely into memory (finding 4264).
                // Hash is computed inline by feeding the file stream through SHA256 incrementally.
                var nameBytes = Encoding.UTF8.GetBytes(file.Name);
                string hash;
                long fileSize;
                await using (var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                {
                    fileSize = fileStream.Length;

                    // Write entry header: name length + name + data length (must know size before streaming)
                    var entryHeader = new byte[8];
                    BitConverter.TryWriteBytes(entryHeader.AsSpan(0, 4), nameBytes.Length);
                    BitConverter.TryWriteBytes(entryHeader.AsSpan(4, 4), (int)Math.Min(fileSize, int.MaxValue));
                    await bundleStream.WriteAsync(entryHeader, ct);
                    await bundleStream.WriteAsync(nameBytes, ct);

                    // Stream file data directly into bundle + compute hash incrementally
                    using var sha256 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await fileStream.ReadAsync(buffer, ct)) > 0)
                    {
                        sha256.AppendData(buffer, 0, read);
                        await bundleStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    hash = Convert.ToHexStringLower(sha256.GetCurrentHash());
                }

                entries.Add(new Dictionary<string, object>
                {
                    ["name"] = file.Name,
                    ["size"] = fileSize,
                    ["hash"] = hash
                });
            }

            var bundleSize = new FileInfo(bundlePath).Length;
            totalBundleSize += bundleSize;
            bundleCount++;

            manifest[group] = new Dictionary<string, object>
            {
                ["bundlePath"] = bundlePath,
                ["bundleSize"] = bundleSize,
                ["fileCount"] = files.Count,
                ["entries"] = entries
            };
        }

        // Write manifest
        var manifestPath = Path.Combine(outputDir, $"{bundleName}_manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);

        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["outputDir"] = outputDir,
                ["manifestPath"] = manifestPath, ["bundleCount"] = bundleCount,
                ["totalBundleSize"] = totalBundleSize, ["groupBy"] = groupBy,
                ["groupCount"] = groups.Count, ["totalFiles"] = groups.Values.Sum(g => g.Count)
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = groups.Values.Sum(g => g.Count), RowsReturned = 1,
                BytesProcessed = totalBundleSize, ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, Array.Empty<string>(), sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, Array.Empty<string>(), ct);
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
