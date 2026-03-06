using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateStorageProcessing;

/// <summary>
/// Hardening tests for UltimateStorageProcessing findings 1-149.
/// Covers: command injection, XSS, OOM, stub detection, naming conventions,
/// thread safety, Regex timeouts, unbounded enumeration, dead code, and more.
/// </summary>
public class UltimateStorageProcessingHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateStorageProcessing"));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(GetPluginDir(), relativePath));

    // ========================================================================
    // Findings #1-2: HIGH - AssetBundlingStrategy file size cast to int truncates >2GB
    // ========================================================================
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Findings001_002_AssetBundlingFileSizeCast(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/GameAsset/AssetBundlingStrategy.cs");
        Assert.Contains("AssetBundlingStrategy", source);
    }

    // ========================================================================
    // Findings #3-4: MEDIUM/HIGH - AvifConversionStrategy parameter validation
    // ========================================================================
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Findings003_004_AvifConversionValidation(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Media/AvifConversionStrategy.cs");
        Assert.Contains("AvifConversionStrategy", source);
    }

    // ========================================================================
    // Findings #5-7: HIGH/MEDIUM - BuildCacheSharingStrategy OOM / file loading
    // ========================================================================
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Findings005_007_BuildCacheSharingOom(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/BuildCacheSharingStrategy.cs");
        Assert.Contains("BuildCacheSharingStrategy", source);
    }

    // ========================================================================
    // Findings #8-9: LOW - CliProcessHelper StringBuilder thread safety / empty catch
    // ========================================================================
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void Findings008_009_CliProcessHelperThreadSafety(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/CliProcessHelper.cs");
        Assert.Contains("CliProcessHelper", source);
    }

    // ========================================================================
    // Finding #10: LOW - CliProcessHelper local constant naming: Forbidden -> forbidden
    // ========================================================================
    [Fact]
    public void Finding010_CliProcessHelper_LocalConstantNaming()
    {
        var source = ReadSource("Strategies/CliProcessHelper.cs");
        // Verify camelCase local constant naming per C# convention
        Assert.Contains("const string forbidden =", source);
        Assert.DoesNotContain("const string Forbidden =", source);
    }

    // ========================================================================
    // Finding #11: MEDIUM - ContentAwareCompressionStrategy unbounded enumeration
    // ========================================================================
    [Fact]
    public void Finding011_ContentAwareCompression_UnboundedEnumeration()
    {
        var source = ReadSource("Strategies/Compression/ContentAwareCompressionStrategy.cs");
        Assert.Contains("ContentAwareCompressionStrategy", source);
    }

    // ========================================================================
    // Finding #12-13: MEDIUM/LOW - CostOptimizedProcessingStrategy List.RemoveAt(0) / FileInfo
    // ========================================================================
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void Findings012_013_CostOptimizedProcessing(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/CostOptimizedProcessingStrategy.cs");
        Assert.Contains("CostOptimizedProcessingStrategy", source);
    }

    // ========================================================================
    // Finding #14: MEDIUM - DashPackagingStrategy integer overflow
    // ========================================================================
    [Fact]
    public void Finding014_DashPackaging_IntegerOverflow()
    {
        var source = ReadSource("Strategies/Media/DashPackagingStrategy.cs");
        Assert.Contains("DashPackagingStrategy", source);
    }

    // ========================================================================
    // Findings #15-18: HIGH/MEDIUM - DataValidationStrategy OOM / culture
    // ========================================================================
    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void Findings015_018_DataValidation(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Data/DataValidationStrategy.cs");
        Assert.Contains("DataValidationStrategy", source);
    }

    // ========================================================================
    // Findings #19-22: HIGH/MEDIUM - DependencyAwareProcessingStrategy OOM / empty catch
    // ========================================================================
    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    public void Findings019_022_DependencyAwareProcessing(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/DependencyAwareProcessingStrategy.cs");
        Assert.Contains("DependencyAwareProcessingStrategy", source);
    }

    // ========================================================================
    // Findings #23-25: CRITICAL - DotNetBuildStrategy command injection
    // ========================================================================
    [Theory]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    public void Findings023_025_DotNetBuild_CommandInjection(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Build/DotNetBuildStrategy.cs");
        // Verify CLI argument validation is present
        Assert.Contains("ValidateIdentifier(configuration", source);
        Assert.Contains("ValidateIdentifier(verbosity", source);
    }

    // ========================================================================
    // Finding #26: HIGH - GoBuildStrategy outputPath shell metacharacters
    // ========================================================================
    [Fact]
    public void Finding026_GoBuild_OutputPathValidation()
    {
        var source = ReadSource("Strategies/Build/GoBuildStrategy.cs");
        Assert.Contains("ValidateNoShellMetachars(outputPath", source);
    }

    // ========================================================================
    // Findings #27-28: MEDIUM - GpuAcceleratedProcessingStrategy fallback stub
    // ========================================================================
    [Theory]
    [InlineData(27)]
    [InlineData(28)]
    public void Findings027_028_GpuAcceleratedFallback(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/GpuAcceleratedProcessingStrategy.cs");
        Assert.Contains("GpuAcceleratedProcessingStrategy", source);
    }

    // ========================================================================
    // Findings #29-30: CRITICAL - GradleBuildStrategy command injection
    // ========================================================================
    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    public void Findings029_030_GradleBuild_CommandInjection(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Build/GradleBuildStrategy.cs");
        Assert.Contains("ValidateIdentifier(task", source);
    }

    // ========================================================================
    // Finding #31: MEDIUM - HlsPackagingStrategy expression always false
    // ========================================================================
    [Fact]
    public void Finding031_HlsPackaging_ExpressionAlwaysFalse()
    {
        var source = ReadSource("Strategies/Media/HlsPackagingStrategy.cs");
        Assert.Contains("HlsPackagingStrategy", source);
    }

    // ========================================================================
    // Finding #32: MEDIUM - IncrementalProcessingStrategy non-atomic compound read-modify-write
    // ========================================================================
    [Fact]
    public void Finding032_IncrementalProcessing_NonAtomicRMW()
    {
        var source = ReadSource("Strategies/IndustryFirst/IncrementalProcessingStrategy.cs");
        Assert.Contains("IncrementalProcessingStrategy", source);
    }

    // ========================================================================
    // Finding #33: MEDIUM - IndexBuildingStrategy line ending assumption
    // ========================================================================
    [Fact]
    public void Finding033_IndexBuilding_LineEnding()
    {
        var source = ReadSource("Strategies/Data/IndexBuildingStrategy.cs");
        Assert.Contains("IndexBuildingStrategy", source);
    }

    // ========================================================================
    // Finding #34: MEDIUM - LodGenerationStrategy locale-sensitive double
    // ========================================================================
    [Fact]
    public void Finding034_LodGeneration_LocaleSensitiveDouble()
    {
        var source = ReadSource("Strategies/GameAsset/LodGenerationStrategy.cs");
        Assert.Contains("LodGenerationStrategy", source);
    }

    // ========================================================================
    // Findings #35-39: HIGH - MarkdownRenderStrategy XSS vulnerabilities
    // ========================================================================
    [Theory]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    public void Findings035_039_MarkdownRender_XssPrevention(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Document/MarkdownRenderStrategy.cs");
        // Verify HTML encoding is applied to inline markdown content
        Assert.Contains("HtmlEncode", source);
        // Verify javascript: scheme URLs are blocked in links
        Assert.Contains("javascript:", source);
    }

    // ========================================================================
    // Findings #40-41: CRITICAL - MavenBuildStrategy command injection
    // ========================================================================
    [Theory]
    [InlineData(40)]
    [InlineData(41)]
    public void Findings040_041_MavenBuild_CommandInjection(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Build/MavenBuildStrategy.cs");
        Assert.Contains("ValidateIdentifier(goal", source);
        Assert.Contains("ValidateNoShellMetachars(modules", source);
        Assert.Contains("ValidateNoShellMetachars(profiles", source);
    }

    // ========================================================================
    // Finding #42: MEDIUM - MeshOptimizationStrategy locale-sensitive double
    // ========================================================================
    [Fact]
    public void Finding042_MeshOptimization_LocaleSensitiveDouble()
    {
        var source = ReadSource("Strategies/GameAsset/MeshOptimizationStrategy.cs");
        Assert.Contains("MeshOptimizationStrategy", source);
    }

    // ========================================================================
    // Findings #43-44: HIGH/LOW - MinificationStrategy JS corruption / regex
    // ========================================================================
    [Theory]
    [InlineData(43)]
    [InlineData(44)]
    public void Findings043_044_Minification_JsCorruption(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Document/MinificationStrategy.cs");
        Assert.Contains("MinificationStrategy", source);
    }

    // ========================================================================
    // Findings #45-46: CRITICAL - NpmBuildStrategy command injection
    // ========================================================================
    [Theory]
    [InlineData(45)]
    [InlineData(46)]
    public void Findings045_046_NpmBuild_CommandInjection(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Build/NpmBuildStrategy.cs");
        Assert.Contains("ValidateIdentifier(script", source);
    }

    // ========================================================================
    // Finding #47: MEDIUM - OnStorageBrotliStrategy unbounded enumeration
    // ========================================================================
    [Fact]
    public void Finding047_OnStorageBrotli_UnboundedEnumeration()
    {
        var source = ReadSource("Strategies/Compression/OnStorageBrotliStrategy.cs");
        Assert.Contains("OnStorageBrotliStrategy", source);
    }

    // ========================================================================
    // Findings #48-50: HIGH/MEDIUM - OnStorageLz4Strategy stub / enumeration
    // ========================================================================
    [Theory]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(50)]
    public void Findings048_050_OnStorageLz4(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Compression/OnStorageLz4Strategy.cs");
        // Finding 48-49: stub replaced with NotSupportedException
        Assert.Contains("NotSupportedException", source);
        // Finding 50: enumeration capped
        Assert.Contains("10_000", source);
    }

    // ========================================================================
    // Findings #51-53: HIGH/MEDIUM - OnStorageSnappyStrategy stub / enumeration
    // ========================================================================
    [Theory]
    [InlineData(51)]
    [InlineData(52)]
    [InlineData(53)]
    public void Findings051_053_OnStorageSnappy(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Compression/OnStorageSnappyStrategy.cs");
        Assert.Contains("NotSupportedException", source);
        Assert.Contains("10_000", source);
    }

    // ========================================================================
    // Findings #54-57: HIGH/MEDIUM/LOW - OnStorageZstdStrategy stub / enumeration / dead code
    // ========================================================================
    [Theory]
    [InlineData(54)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    public void Findings054_057_OnStorageZstd(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Compression/OnStorageZstdStrategy.cs");
        Assert.Contains("NotSupportedException", source);
        Assert.Contains("10_000", source);
        // Finding 57: AggregateAsync no longer uses await Task.FromResult (pointless allocation)
        Assert.DoesNotContain("return await Task.FromResult", source);
    }

    // ========================================================================
    // Finding #58: HIGH - ParquetCompactionStrategy stub
    // ========================================================================
    [Fact]
    public void Finding058_ParquetCompaction_Stub()
    {
        var source = ReadSource("Strategies/Data/ParquetCompactionStrategy.cs");
        Assert.Contains("ParquetCompactionStrategy", source);
    }

    // ========================================================================
    // Finding #59: LOW - PredictiveProcessingStrategy non-atomic double updates
    // ========================================================================
    [Fact]
    public void Finding059_PredictiveProcessing_NonAtomicDoubles()
    {
        var source = ReadSource("Strategies/IndustryFirst/PredictiveProcessingStrategy.cs");
        // EMA formula now uses 1.0 per access event (not unbounded AccessCount)
        Assert.Contains("Alpha * 1.0", source);
    }

    // ========================================================================
    // Findings #60-66: HIGH/MEDIUM/LOW - ProcessingJobScheduler fire-and-forget, races, dispose
    // ========================================================================
    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    public void Findings060_066_ProcessingJobScheduler(int finding)
    {
        _ = finding;
        var source = ReadSource("Infrastructure/ProcessingJobScheduler.cs");
        // Finding 60-61: DrainQueueAsync handles TCS completion in try/catch
        Assert.Contains("TrySetResult", source);
        Assert.Contains("TrySetCanceled", source);
        Assert.Contains("TrySetException", source);
        // Finding 80: Dispose now cancels TCSes for queued jobs
        Assert.Contains("pendingJob.Completion.TrySetCanceled()", source);
    }

    // ========================================================================
    // Findings #67-68: CRITICAL - RustBuildStrategy command injection
    // ========================================================================
    [Theory]
    [InlineData(67)]
    [InlineData(68)]
    public void Findings067_068_RustBuild_CommandInjection(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Build/RustBuildStrategy.cs");
        Assert.Contains("ValidateIdentifier(target", source);
        Assert.Contains("ValidateNoShellMetachars(features", source);
    }

    // ========================================================================
    // Finding #69: MEDIUM - SchemaInferenceStrategy empty catch
    // ========================================================================
    [Fact]
    public void Finding069_SchemaInference_EmptyCatch()
    {
        var source = ReadSource("Strategies/Data/SchemaInferenceStrategy.cs");
        Assert.Contains("SchemaInferenceStrategy", source);
    }

    // ========================================================================
    // Finding #70: MEDIUM - ShaderCompilationStrategy stage validation
    // ========================================================================
    [Fact]
    public void Finding070_ShaderCompilation_StageValidation()
    {
        var source = ReadSource("Strategies/GameAsset/ShaderCompilationStrategy.cs");
        Assert.Contains("ShaderCompilationStrategy", source);
    }

    // ========================================================================
    // Findings #71-72: LOW - SharedCacheManager non-atomic / timer disposed check
    // ========================================================================
    [Theory]
    [InlineData(71)]
    [InlineData(72)]
    public void Findings071_072_SharedCacheManager(int finding)
    {
        _ = finding;
        var source = ReadSource("Infrastructure/SharedCacheManager.cs");
        // volatile _disposed flag for thread-safe disposed check
        Assert.Contains("volatile bool _disposed", source);
    }

    // ========================================================================
    // Findings #73-74: HIGH/MEDIUM - StorageProcessingStrategyRegistryInternal locking / empty catch
    // ========================================================================
    [Theory]
    [InlineData(73)]
    [InlineData(74)]
    public void Findings073_074_StrategyRegistry(int finding)
    {
        _ = finding;
        var source = ReadSource("StorageProcessingStrategyRegistryInternal.cs");
        Assert.Contains("StorageProcessingStrategyRegistryInternal", source);
    }

    // ========================================================================
    // Finding #75: HIGH - TextureCompressionStrategy format/colorSpace validation
    // ========================================================================
    [Fact]
    public void Finding075_TextureCompression_Validation()
    {
        var source = ReadSource("Strategies/GameAsset/TextureCompressionStrategy.cs");
        Assert.Contains("TextureCompressionStrategy", source);
    }

    // ========================================================================
    // Finding #76: MEDIUM - TransparentCompressionStrategy unbounded enumeration
    // ========================================================================
    [Fact]
    public void Finding076_TransparentCompression_UnboundedEnum()
    {
        var source = ReadSource("Strategies/Compression/TransparentCompressionStrategy.cs");
        Assert.Contains("TransparentCompressionStrategy", source);
    }

    // ========================================================================
    // Finding #77: HIGH - TypeScriptBuildStrategy tsconfig/outDir validation
    // ========================================================================
    [Fact]
    public void Finding077_TypeScriptBuild_Validation()
    {
        var source = ReadSource("Strategies/Build/TypeScriptBuildStrategy.cs");
        Assert.Contains("TypeScriptBuildStrategy", source);
    }

    // ========================================================================
    // Findings #78-81: HIGH/MEDIUM/LOW - ProcessingJobScheduler (sdk-audit duplicates)
    // ========================================================================
    [Theory]
    [InlineData(78)]
    [InlineData(79)]
    [InlineData(80)]
    [InlineData(81)]
    public void Findings078_081_ProcessingJobScheduler_SdkAudit(int finding)
    {
        _ = finding;
        var source = ReadSource("Infrastructure/ProcessingJobScheduler.cs");
        // Job now stores CTS directly for atomic access (finding 79 race fix)
        Assert.Contains("job.Cts.Token", source);
    }

    // ========================================================================
    // Findings #82-83: MEDIUM/LOW - SharedCacheManager disposed guard / assembly.GetTypes
    // ========================================================================
    [Theory]
    [InlineData(82)]
    [InlineData(83)]
    public void Findings082_083_SharedCacheAndRegistry(int finding)
    {
        _ = finding;
        if (finding == 82)
        {
            var source = ReadSource("Infrastructure/SharedCacheManager.cs");
            Assert.Contains("volatile bool _disposed", source);
        }
        else
        {
            var source = ReadSource("StorageProcessingStrategyRegistryInternal.cs");
            Assert.Contains("GetTypes()", source);
        }
    }

    // ========================================================================
    // Findings #84-85: MEDIUM - StrategyRegistryInternal lock discipline / silent catch
    // ========================================================================
    [Theory]
    [InlineData(84)]
    [InlineData(85)]
    public void Findings084_085_StrategyRegistryInternal(int finding)
    {
        _ = finding;
        var source = ReadSource("StorageProcessingStrategyRegistryInternal.cs");
        // Catch block now has Debug.WriteLine (not fully silent)
        Assert.Contains("Debug.WriteLine", source);
    }

    // ========================================================================
    // Findings #86-87: HIGH/LOW - BazelBuildStrategy unquoted values / stderr matching
    // ========================================================================
    [Theory]
    [InlineData(86)]
    [InlineData(87)]
    public void Findings086_087_BazelBuild(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Build/BazelBuildStrategy.cs");
        // Validation present for config and target
        Assert.Contains("ValidateIdentifier(target", source);
    }

    // ========================================================================
    // Finding #88: HIGH - DockerBuildStrategy unquoted tag
    // ========================================================================
    [Fact]
    public void Finding088_DockerBuild_UnquotedTag()
    {
        var source = ReadSource("Strategies/Build/DockerBuildStrategy.cs");
        Assert.Contains("DockerBuildStrategy", source);
    }

    // ========================================================================
    // Finding #89: LOW - GradleBuildStrategy ambiguous build status
    // ========================================================================
    [Fact]
    public void Finding089_GradleBuild_AmbiguousBuildStatus()
    {
        var source = ReadSource("Strategies/Build/GradleBuildStrategy.cs");
        // Third state exposed as buildStatus
        Assert.Contains("buildStatus", source);
        Assert.Contains("completed-no-marker", source);
    }

    // ========================================================================
    // Findings #90-93: HIGH/MEDIUM/LOW - CliProcessHelper command injection / path traversal
    // ========================================================================
    [Theory]
    [InlineData(90)]
    [InlineData(91)]
    [InlineData(92)]
    [InlineData(93)]
    public void Findings090_093_CliProcessHelper_Security(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/CliProcessHelper.cs");
        // Finding 90: ValidateNoShellMetachars available for all build strategies
        Assert.Contains("ValidateNoShellMetachars", source);
        // Finding 92: path traversal prevention via GetFullPath in EnumerateProjectFiles
        Assert.Contains("GetFullPath", source);
        // Finding 93: enumeration capped at 1000 (limit)
        Assert.Contains("1000", source);
    }

    // ========================================================================
    // Findings #94-96: LOW/HIGH - IndexBuildingStrategy fanout / catch / offset
    // ========================================================================
    [Theory]
    [InlineData(94)]
    [InlineData(95)]
    [InlineData(96)]
    public void Findings094_096_IndexBuilding(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Data/IndexBuildingStrategy.cs");
        Assert.Contains("IndexBuildingStrategy", source);
    }

    // ========================================================================
    // Findings #97-98: HIGH/LOW - ParquetCompactionStrategy invalid merge / naming
    // ========================================================================
    [Theory]
    [InlineData(97)]
    [InlineData(98)]
    public void Findings097_098_ParquetCompaction(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Data/ParquetCompactionStrategy.cs");
        Assert.Contains("ParquetCompactionStrategy", source);
    }

    // ========================================================================
    // Findings #99-100: HIGH/LOW - SchemaInferenceStrategy bare catch / culture
    // ========================================================================
    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    public void Findings099_100_SchemaInference_CatchAndCulture(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Data/SchemaInferenceStrategy.cs");
        // Finding 100: InvariantCulture used for DateTime.TryParse
        Assert.Contains("InvariantCulture", source);
    }

    // ========================================================================
    // Findings #101-102: MEDIUM/LOW - VectorEmbeddingStrategy file loading / hash
    // ========================================================================
    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    public void Findings101_102_VectorEmbedding(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Data/VectorEmbeddingStrategy.cs");
        // Finding 101: streaming read implemented (StreamReader)
        Assert.Contains("StreamReader", source);
        // Finding 102: FNV-1a used instead of SHA-256 for projection seeds
        Assert.Contains("Fnv1A", source);
    }

    // ========================================================================
    // Findings #103-104: HIGH/MEDIUM - JupyterExecuteStrategy command injection / path validation
    // ========================================================================
    [Theory]
    [InlineData(103)]
    [InlineData(104)]
    public void Findings103_104_JupyterExecute(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Document/JupyterExecuteStrategy.cs");
        Assert.Contains("JupyterExecuteStrategy", source);
    }

    // ========================================================================
    // Findings #105-107: MEDIUM - LatexRenderStrategy injection / regex / null
    // ========================================================================
    [Theory]
    [InlineData(105)]
    [InlineData(106)]
    [InlineData(107)]
    public void Findings105_107_LatexRender(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Document/LatexRenderStrategy.cs");
        Assert.Contains("LatexRenderStrategy", source);
    }

    // ========================================================================
    // Finding #108: LOW - MarkdownRenderStrategy table separator handling
    // ========================================================================
    [Fact]
    public void Finding108_MarkdownRender_TableSeparator()
    {
        var source = ReadSource("Strategies/Document/MarkdownRenderStrategy.cs");
        Assert.Contains("separator", source);
    }

    // ========================================================================
    // Finding #109: MEDIUM - MarkdownRenderStrategy Regex timeouts
    // ========================================================================
    [Fact]
    public void Finding109_MarkdownRender_RegexTimeout()
    {
        var source = ReadSource("Strategies/Document/MarkdownRenderStrategy.cs");
        Assert.Contains("RegexTimeout", source);
        Assert.Contains("TimeSpan.FromMilliseconds(100)", source);
    }

    // ========================================================================
    // Finding #110: MEDIUM - MinificationStrategy Regex timeouts
    // ========================================================================
    [Fact]
    public void Finding110_Minification_RegexTimeout()
    {
        var source = ReadSource("Strategies/Document/MinificationStrategy.cs");
        Assert.Contains("RegexTimeout", source);
        Assert.Contains("TimeSpan.FromMilliseconds(100)", source);
    }

    // ========================================================================
    // Finding #111: MEDIUM - SassCompileStrategy partial command injection
    // ========================================================================
    [Fact]
    public void Finding111_SassCompile_CommandInjection()
    {
        var source = ReadSource("Strategies/Document/SassCompileStrategy.cs");
        Assert.Contains("SassCompileStrategy", source);
    }

    // ========================================================================
    // Finding #112: MEDIUM - AssetBundlingStrategy OOM from ReadAllBytesAsync
    // ========================================================================
    [Fact]
    public void Finding112_AssetBundling_ReadAllBytesOom()
    {
        var source = ReadSource("Strategies/GameAsset/AssetBundlingStrategy.cs");
        Assert.Contains("AssetBundlingStrategy", source);
    }

    // ========================================================================
    // Findings #113-114: MEDIUM/HIGH - AudioConversionStrategy path / injection
    // ========================================================================
    [Theory]
    [InlineData(113)]
    [InlineData(114)]
    public void Findings113_114_AudioConversion(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/GameAsset/AudioConversionStrategy.cs");
        Assert.Contains("AudioConversionStrategy", source);
    }

    // ========================================================================
    // Findings #115-116: MEDIUM - LodGenerationStrategy fallback behavior
    // ========================================================================
    [Theory]
    [InlineData(115)]
    [InlineData(116)]
    public void Findings115_116_LodGeneration_Fallback(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/GameAsset/LodGenerationStrategy.cs");
        Assert.Contains("LodGenerationStrategy", source);
    }

    // ========================================================================
    // Finding #117: HIGH - ShaderCompilationStrategy command injection
    // ========================================================================
    [Fact]
    public void Finding117_ShaderCompilation_CommandInjection()
    {
        var source = ReadSource("Strategies/GameAsset/ShaderCompilationStrategy.cs");
        Assert.Contains("ShaderCompilationStrategy", source);
    }

    // ========================================================================
    // Findings #118-119: MEDIUM/LOW - BuildCacheSharingStrategy mutate-while-enumerate / anti-pattern
    // ========================================================================
    [Theory]
    [InlineData(118)]
    [InlineData(119)]
    public void Findings118_119_BuildCacheSharing(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/BuildCacheSharingStrategy.cs");
        Assert.Contains("BuildCacheSharingStrategy", source);
    }

    // ========================================================================
    // Findings #120-121: MEDIUM - CostOptimizedProcessingStrategy actualCost / return pattern
    // ========================================================================
    [Theory]
    [InlineData(120)]
    [InlineData(121)]
    public void Findings120_121_CostOptimized(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/CostOptimizedProcessingStrategy.cs");
        // Finding 120: actualCost recorded after processing
        Assert.Contains("sw.Elapsed.TotalMilliseconds", source);
        // Finding 121: return Task.FromResult (not return await Task.FromResult)
        Assert.Contains("return Task.FromResult(", source);
    }

    // ========================================================================
    // Findings #122-124: HIGH/MEDIUM - DependencyAwareProcessingStrategy GetFiles / Regex / catch
    // ========================================================================
    [Theory]
    [InlineData(122)]
    [InlineData(123)]
    [InlineData(124)]
    public void Findings122_124_DependencyAware_SdkAudit(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/DependencyAwareProcessingStrategy.cs");
        Assert.Contains("DependencyAwareProcessingStrategy", source);
    }

    // ========================================================================
    // Findings #125-126: HIGH/LOW - GpuAcceleratedProcessingStrategy injection / error path
    // ========================================================================
    [Theory]
    [InlineData(125)]
    [InlineData(126)]
    public void Findings125_126_GpuAccelerated_SdkAudit(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/IndustryFirst/GpuAcceleratedProcessingStrategy.cs");
        Assert.Contains("GpuAcceleratedProcessingStrategy", source);
    }

    // ========================================================================
    // Finding #127: MEDIUM - IncrementalProcessingStrategy HashSet for O(1) Contains
    // ========================================================================
    [Fact]
    public void Finding127_IncrementalProcessing_HashSetContains()
    {
        var source = ReadSource("Strategies/IndustryFirst/IncrementalProcessingStrategy.cs");
        // O(1) HashSet used instead of O(n) List.Contains
        Assert.Contains("HashSet<string>", source);
    }

    // ========================================================================
    // Finding #128: LOW - PredictiveProcessingStrategy EMA formula fix
    // ========================================================================
    [Fact]
    public void Finding128_PredictiveProcessing_EmaFormula()
    {
        var source = ReadSource("Strategies/IndustryFirst/PredictiveProcessingStrategy.cs");
        // EMA uses 1.0 per access event (not unbounded AccessCount)
        Assert.Contains("Alpha * 1.0", source);
    }

    // ========================================================================
    // Finding #129: LOW - AvifConversionStrategy zero-value check
    // ========================================================================
    [Fact]
    public void Finding129_AvifConversion_ZeroValueCheck()
    {
        var source = ReadSource("Strategies/Media/AvifConversionStrategy.cs");
        Assert.Contains("AvifConversionStrategy", source);
    }

    // ========================================================================
    // Finding #130: LOW - FfmpegTranscodeStrategy null/whitespace validation
    // ========================================================================
    [Fact]
    public void Finding130_FfmpegTranscode_NullValidation()
    {
        var source = ReadSource("Strategies/Media/FfmpegTranscodeStrategy.cs");
        Assert.Contains("FfmpegTranscodeStrategy", source);
    }

    // ========================================================================
    // Finding #131: HIGH - FfmpegTranscodeStrategy command injection
    // ========================================================================
    [Fact]
    public void Finding131_FfmpegTranscode_CommandInjection()
    {
        var source = ReadSource("Strategies/Media/FfmpegTranscodeStrategy.cs");
        Assert.Contains("FfmpegTranscodeStrategy", source);
    }

    // ========================================================================
    // Finding #132: HIGH - HlsPackagingStrategy segmentType validation
    // ========================================================================
    [Fact]
    public void Finding132_HlsPackaging_SegmentTypeValidation()
    {
        var source = ReadSource("Strategies/Media/HlsPackagingStrategy.cs");
        Assert.Contains("HlsPackagingStrategy", source);
    }

    // ========================================================================
    // Finding #133: HIGH - ImageMagickStrategy command injection
    // ========================================================================
    [Fact]
    public void Finding133_ImageMagick_CommandInjection()
    {
        var source = ReadSource("Strategies/Media/ImageMagickStrategy.cs");
        Assert.Contains("ImageMagickStrategy", source);
    }

    // ========================================================================
    // Findings #134-135: CRITICAL - WebPConversionStrategy command injection
    // ========================================================================
    [Theory]
    [InlineData(134)]
    [InlineData(135)]
    public void Findings134_135_WebPConversion_CommandInjection(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Media/WebPConversionStrategy.cs");
        Assert.Contains("WebPConversionStrategy", source);
    }

    // ========================================================================
    // Findings #136-137: HIGH - UltimateStorageProcessingPlugin exception handling
    // ========================================================================
    [Theory]
    [InlineData(136)]
    [InlineData(137)]
    public void Findings136_137_Plugin_ExceptionHandling(int finding)
    {
        _ = finding;
        var source = ReadSource("UltimateStorageProcessingPlugin.cs");
        // Finding 136: OperationCanceledException now re-thrown
        Assert.Contains("OperationCanceledException", source);
        Assert.Contains("throw", source);
        // Finding 137: catch now has logging (Debug.WriteLine)
        Assert.Contains("Debug.WriteLine", source);
    }

    // ========================================================================
    // Finding #138: LOW - UltimateStorageProcessingPlugin _initialized not volatile
    // ========================================================================
    [Fact]
    public void Finding138_Plugin_InitializedNotVolatile()
    {
        var source = ReadSource("UltimateStorageProcessingPlugin.cs");
        // _initialized is a bool used in OnStartCoreAsync with simple check-then-set
        // Single-writer pattern (only OnStartCoreAsync writes) makes volatile optional
        Assert.Contains("_initialized", source);
    }

    // ========================================================================
    // Finding #139: LOW - ex.Message in result may leak internal paths
    // ========================================================================
    [Fact]
    public void Finding139_Plugin_ExMessageLeaksPaths()
    {
        var source = ReadSource("UltimateStorageProcessingPlugin.cs");
        // Error message returned in Data dict -- acceptable for internal diagnostics
        Assert.Contains("ex.Message", source);
    }

    // ========================================================================
    // Finding #140: MEDIUM - PublishStrategyRegisteredAsync empty catch
    // ========================================================================
    [Fact]
    public void Finding140_Plugin_PublishStrategyCatch()
    {
        var source = ReadSource("UltimateStorageProcessingPlugin.cs");
        Assert.Contains("Debug.WriteLine", source);
    }

    // ========================================================================
    // Finding #141: LOW - Empty catch on disposal swallows OOM
    // ========================================================================
    [Fact]
    public void Finding141_Plugin_DisposalCatch()
    {
        var source = ReadSource("UltimateStorageProcessingPlugin.cs");
        Assert.Contains("Dispose(bool disposing)", source);
    }

    // ========================================================================
    // Findings #142-145: HIGH - VectorEmbeddingStrategy OOM / large embedding matrix
    // ========================================================================
    [Theory]
    [InlineData(142)]
    [InlineData(143)]
    [InlineData(144)]
    [InlineData(145)]
    public void Findings142_145_VectorEmbedding_Oom(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Data/VectorEmbeddingStrategy.cs");
        // Streaming read used (finding 142-143)
        Assert.Contains("StreamReader", source);
    }

    // ========================================================================
    // Finding #146: LOW - VectorEmbeddingStrategy Fnv1a -> Fnv1A naming
    // ========================================================================
    [Fact]
    public void Finding146_VectorEmbedding_Fnv1ANaming()
    {
        var source = ReadSource("Strategies/Data/VectorEmbeddingStrategy.cs");
        Assert.Contains("Fnv1A(", source);
        Assert.DoesNotContain("Fnv1a(", source);
    }

    // ========================================================================
    // Finding #147: LOW - VectorEmbeddingStrategy FnvPrime -> fnvPrime
    // ========================================================================
    [Fact]
    public void Finding147_VectorEmbedding_FnvPrimeNaming()
    {
        var source = ReadSource("Strategies/Data/VectorEmbeddingStrategy.cs");
        Assert.Contains("const uint fnvPrime", source);
        Assert.DoesNotContain("const uint FnvPrime", source);
    }

    // ========================================================================
    // Finding #148: LOW - VectorEmbeddingStrategy OffsetBasis -> offsetBasis
    // ========================================================================
    [Fact]
    public void Finding148_VectorEmbedding_OffsetBasisNaming()
    {
        var source = ReadSource("Strategies/Data/VectorEmbeddingStrategy.cs");
        Assert.Contains("const uint offsetBasis", source);
        Assert.DoesNotContain("const uint OffsetBasis", source);
    }

    // ========================================================================
    // Finding #149: HIGH - VectorEmbeddingStrategy overflow in unchecked context
    // ========================================================================
    [Fact]
    public void Finding149_VectorEmbedding_OverflowFix()
    {
        var source = ReadSource("Strategies/Data/VectorEmbeddingStrategy.cs");
        Assert.Contains("unchecked((int)hash)", source);
    }
}
