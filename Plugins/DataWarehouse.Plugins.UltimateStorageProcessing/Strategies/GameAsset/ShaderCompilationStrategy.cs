using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.GameAsset;

/// <summary>
/// Shader compilation strategy supporting multiple shader languages and targets.
/// Invokes glslangValidator for GLSL-to-SPIR-V, dxc for HLSL-to-DXIL/SPIR-V,
/// and metal for MSL compilation. Outputs shader reflection information.
/// </summary>
internal sealed class ShaderCompilationStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "gameasset-shader";

    /// <inheritdoc/>
    public override string Name => "Shader Compilation Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsProjection = true, SupportsAggregation = true, SupportsLimiting = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum },
        MaxQueryComplexity = 5
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var ext = Path.GetExtension(query.Source).ToLowerInvariant();
        var target = CliProcessHelper.GetOption<string>(query, "target") ?? "spirv";
        var stage = CliProcessHelper.GetOption<string>(query, "stage");
        var entryPoint = CliProcessHelper.GetOption<string>(query, "entryPoint") ?? "main";

        // Validate stage and entryPoint to prevent command injection
        // entryPoint must be a valid C identifier: letters, digits, underscores only
        var allowedTargets = new HashSet<string>(StringComparer.Ordinal) { "spirv", "dxil", "msl", "glsl" };
        CliProcessHelper.ValidateAllowlist(target, "target", allowedTargets);
        if (stage != null) CliProcessHelper.ValidateIdentifier(stage, "stage");
        // entryPoint: only word characters (letters/digits/underscore)
        foreach (var c in entryPoint)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                throw new ArgumentException($"'entryPoint' contains invalid character '{c}'. Only letters, digits, and underscores are allowed.", nameof(entryPoint));
        }

        CliOutput result;
        string outputPath;
        string tool;

        if (ext is ".hlsl" or ".fx")
        {
            // HLSL via DXC
            tool = "dxc";
            var targetProfile = target == "spirv" ? "-spirv" : $"-T {stage ?? "vs_6_0"}";
            outputPath = target == "spirv"
                ? Path.ChangeExtension(query.Source, ".spv")
                : Path.ChangeExtension(query.Source, ".dxil");
            var args = $"{targetProfile} -E {entryPoint} -Fo \"{outputPath}\" \"{query.Source}\"";
            result = await CliProcessHelper.RunAsync("dxc", args, Path.GetDirectoryName(query.Source), ct: ct);
        }
        else if (ext == ".metal")
        {
            // MSL via Apple metal compiler
            tool = "metal";
            outputPath = Path.ChangeExtension(query.Source, ".air");
            var args = $"-c \"{query.Source}\" -o \"{outputPath}\"";
            result = await CliProcessHelper.RunAsync("xcrun", $"-sdk macosx metal {args}", Path.GetDirectoryName(query.Source), ct: ct);
        }
        else
        {
            // GLSL via glslangValidator
            tool = "glslangValidator";
            outputPath = Path.ChangeExtension(query.Source, ".spv");
            var stageFlag = stage ?? DetectShaderStage(ext);
            var args = $"-V -S {stageFlag} -o \"{outputPath}\" \"{query.Source}\"";
            result = await CliProcessHelper.RunAsync("glslangValidator", args, Path.GetDirectoryName(query.Source), ct: ct);
        }

        return CliProcessHelper.ToProcessingResult(result, query.Source, tool, new Dictionary<string, object?>
        {
            ["target"] = target, ["stage"] = stage, ["entryPoint"] = entryPoint,
            ["outputPath"] = outputPath, ["outputExists"] = File.Exists(outputPath),
            ["outputSize"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0
        });
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".glsl", ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese", ".hlsl", ".fx", ".metal", ".spv" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".glsl", ".hlsl", ".metal", ".spv" }, ct);
    }

    private static string DetectShaderStage(string ext) => ext switch
    {
        ".vert" => "vert", ".frag" => "frag", ".comp" => "comp",
        ".geom" => "geom", ".tesc" => "tesc", ".tese" => "tese",
        _ => "frag"
    };
}
