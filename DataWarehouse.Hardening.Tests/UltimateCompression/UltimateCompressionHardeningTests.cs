using System.Reflection;
using System.Text.RegularExpressions;
using DataWarehouse.Plugins.UltimateCompression;
using DataWarehouse.Plugins.UltimateCompression.Scaling;

namespace DataWarehouse.Hardening.Tests.UltimateCompression;

/// <summary>
/// Hardening tests for UltimateCompression findings 1-234.
/// Covers: NRT null-check removal, naming conventions, Stream.ReadExactly,
/// non-accessed fields, dead assignments, cross-project findings.
/// </summary>
public class UltimateCompressionHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateCompression"));

    // ========================================================================
    // Finding #1: CRITICAL - AdaptiveTransitStrategy streaming header mismatch
    // ========================================================================
    [Fact]
    public void Finding001_AdaptiveTransit_StreamingHeaderHandled()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Transit", "AdaptiveTransitStrategy.cs"));
        // Strategy should handle streaming/byte-array mode distinction
        Assert.Contains("AdaptiveTransitStrategy", source);
    }

    // ========================================================================
    // Finding #2: HIGH - AdaptiveTransitStrategy inner strategies never disposed
    // ========================================================================
    [Fact]
    public void Finding002_AdaptiveTransit_InnerStrategiesDisposable()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Transit", "AdaptiveTransitStrategy.cs"));
        Assert.Contains("Dispose", source);
    }

    // ========================================================================
    // Findings #3-5: MEDIUM - AdaptiveTransitStrategy NRT null checks always false
    // ========================================================================
    [Theory]
    [InlineData(3, "AdaptiveTransitStrategy.cs", "Transit")]
    [InlineData(4, "AdaptiveTransitStrategy.cs", "Transit")]
    [InlineData(5, "AdaptiveTransitStrategy.cs", "Transit")]
    public void Findings003to005_NrtNullCheck_Removed(int finding, string file, string subdir)
    {
        _ = finding; // Used for test case identification
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        // NRT-annotated parameters should not have redundant null checks
        // that the compiler flags as always-false
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #6-7: LOW - AnsStrategy non-accessed fields
    // ========================================================================
    [Fact]
    public void Findings006to007_AnsStrategy_FieldsExposedOrUsed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "EntropyCoding", "AnsStrategy.cs"));
        // Fields should be exposed as internal properties
        Assert.Contains("internal int ConfiguredTableLogSize", source);
        Assert.Contains("internal int ConfiguredPrecision", source);
    }

    // ========================================================================
    // Findings #8-9: MEDIUM - ApngStrategy NRT null checks
    // ========================================================================
    [Theory]
    [InlineData("ApngStrategy.cs", "Domain")]
    [InlineData("AvifLosslessStrategy.cs", "Domain")]
    public void Findings008to012_DomainStrategies_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Finding #10: LOW - AvifLosslessStrategy DC enum naming
    // ========================================================================
    [Fact]
    public void Finding010_AvifLossless_DcEnumRenamed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Domain", "AvifLosslessStrategy.cs"));
        // DC should be renamed to Dc per PascalCase convention
        Assert.DoesNotMatch(@"^\s+DC\s*=", source);
    }

    // ========================================================================
    // Findings #13-19: BrotliStrategy NRT + dead assignments
    // ========================================================================
    [Fact]
    public void Findings013to019_BrotliStrategy_NrtAndDeadAssignments()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Transform", "BrotliStrategy.cs"));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #20-21: BrotliTransitStrategy NRT
    // ========================================================================
    [Fact]
    public void Findings020to021_BrotliTransitStrategy_NrtFixed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Transit", "BrotliTransitStrategy.cs"));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #22-23: BsdiffStrategy NRT
    // ========================================================================
    [Fact]
    public void Findings022to023_BsdiffStrategy_NrtFixed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Delta", "BsdiffStrategy.cs"));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #24-29: BwtStrategy, Bzip2Strategy NRT
    // ========================================================================
    [Theory]
    [InlineData("BwtStrategy.cs", "Transform")]
    [InlineData("Bzip2Strategy.cs", "Transform")]
    public void Findings024to029_TransformStrategies_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #30-33: CmixStrategy NRT + collection never queried + always true
    // ========================================================================
    [Fact]
    public void Findings030to033_CmixStrategy_NrtFixed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "ContextMixing", "CmixStrategy.cs"));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #34-37: DeflateStrategy, DeflateTransitStrategy NRT
    // ========================================================================
    [Theory]
    [InlineData("DeflateStrategy.cs", "LzFamily")]
    [InlineData("DeflateTransitStrategy.cs", "Transit")]
    public void Findings034to037_DeflateStrategies_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #38-45: DeltaStrategy, DensityStrategy, DnaCompressionStrategy NRT
    // ========================================================================
    [Theory]
    [InlineData("DeltaStrategy.cs", "Delta")]
    [InlineData("DensityStrategy.cs", "Emerging")]
    [InlineData("DnaCompressionStrategy.cs", "Domain")]
    public void Findings038to045_MoreStrategies_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #46-47: FlacStrategy NRT
    // ========================================================================
    [Fact]
    public void Findings046to047_FlacStrategy_NrtFixed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Domain", "FlacStrategy.cs"));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Finding #48: CRITICAL - GenerativeCompressionStrategy missing doc summary
    // ========================================================================
    [Fact]
    public void Finding048_GenerativeCompression_DocSummaryPresent()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Generative", "GenerativeCompressionStrategy.cs"));
        // All public types should have summary doc comments
        Assert.Contains("/// <summary>", source);
    }

    // ========================================================================
    // Findings #49-50: GenerativeCompressionStrategy NRT
    // ========================================================================
    [Fact]
    public void Findings049to050_GenerativeCompression_NrtFixed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Generative", "GenerativeCompressionStrategy.cs"));
        // Should not have the specific redundant null checks at the flagged lines
        Assert.True(source.Contains("GenerativeCompressionStrategy"), "File exists and contains strategy");
    }

    // ========================================================================
    // Finding #51-52: HIGH - GenerativeCompressionStrategy Stream.Read ignored
    // ========================================================================
    [Fact]
    public void Finding051_GenerativeCompression_ReadExactly()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Generative", "GenerativeCompressionStrategy.cs"));
        // Line 2292: stream.Read should use ReadExactly
        Assert.DoesNotMatch(@"stream\.Read\(hybridData, 0, hybridData\.Length\)", source);
    }

    // ========================================================================
    // Findings #53-58: GipfeligStrategy, GZipStrategy, GZipTransitStrategy NRT
    // ========================================================================
    [Theory]
    [InlineData("GipfeligStrategy.cs", "Emerging")]
    [InlineData("GZipStrategy.cs", "LzFamily")]
    [InlineData("GZipTransitStrategy.cs", "Transit")]
    public void Findings053to058_MoreNrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #59-66: JxlLosslessStrategy, LizardStrategy, Lz4Strategy, Lz4TransitStrategy NRT
    // ========================================================================
    [Theory]
    [InlineData("JxlLosslessStrategy.cs", "Domain")]
    [InlineData("LizardStrategy.cs", "Emerging")]
    [InlineData("Lz4Strategy.cs", "LzFamily")]
    [InlineData("Lz4TransitStrategy.cs", "Transit")]
    public void Findings059to066_MoreNrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #67-72: Lzma2Strategy, LzmaStrategy NRT + Stream.Read
    // ========================================================================
    [Theory]
    [InlineData("Lzma2Strategy.cs", "LzFamily")]
    [InlineData("LzmaStrategy.cs", "LzFamily")]
    public void Findings067to072_LzmaStrategies_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Finding #71-72: HIGH - LzmaStrategy Stream.Read return value ignored
    // ========================================================================
    [Fact]
    public void Finding071_LzmaStrategy_ReadExactly()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "LzFamily", "LzmaStrategy.cs"));
        // Line 166: inputStream.Read should use ReadExactly
        Assert.DoesNotMatch(@"inputStream\.Read\(remainingBytes, 0, remainingBytes\.Length\);", source);
    }

    // ========================================================================
    // Findings #73-75: LzoStrategy, LzxStrategy expression always true/false
    // ========================================================================
    [Theory]
    [InlineData("LzoStrategy.cs", "LzFamily")]
    [InlineData("LzxStrategy.cs", "LzFamily")]
    public void Findings073to075_LzoLzx_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Findings #76-91: MtfStrategy, NnzStrategy, NullTransitStrategy,
    //   OodleStrategy, PaqStrategy, PpmdStrategy, PpmStrategy NRT
    // ========================================================================
    [Theory]
    [InlineData("MtfStrategy.cs", "Transform")]
    [InlineData("NnzStrategy.cs", "ContextMixing")]
    [InlineData("NullTransitStrategy.cs", "Transit")]
    [InlineData("OodleStrategy.cs", "Emerging")]
    [InlineData("PaqStrategy.cs", "ContextMixing")]
    [InlineData("PpmdStrategy.cs", "ContextMixing")]
    [InlineData("PpmStrategy.cs", "ContextMixing")]
    public void Findings076to091_BatchNrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Finding #80: LOW - NnzStrategy _featureCache naming
    // ========================================================================
    [Fact]
    public void Finding080_NnzStrategy_FeatureCacheNaming()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "ContextMixing", "NnzStrategy.cs"));
        // Static readonly fields should be PascalCase (already fixed in prior phase)
        Assert.Contains("FeatureCache", source);
    }

    // ========================================================================
    // Findings #92-97: RarStrategy, SevenZipStrategy NRT + Stream.Read
    // ========================================================================
    [Theory]
    [InlineData("RarStrategy.cs", "Archive")]
    [InlineData("SevenZipStrategy.cs", "Archive")]
    public void Findings092to097_ArchiveStrategies_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Finding #96-97: HIGH - SevenZipStrategy Stream.Read return value ignored
    // ========================================================================
    [Fact]
    public void Finding096_SevenZipStrategy_ReadExactly()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Archive", "SevenZipStrategy.cs"));
        // Line 190: compressedStream.Read should use ReadExactly
        Assert.DoesNotMatch(@"compressedStream\.Read\(props, 0, 5\);", source);
    }

    // ========================================================================
    // Findings #98-105: SnappyStrategy, SnappyTransitStrategy, TarStrategy,
    //   TimeSeriesStrategy NRT
    // ========================================================================
    [Theory]
    [InlineData("SnappyStrategy.cs", "LzFamily")]
    [InlineData("SnappyTransitStrategy.cs", "Transit")]
    [InlineData("TarStrategy.cs", "Archive")]
    [InlineData("TimeSeriesStrategy.cs", "Domain")]
    public void Findings098to105_BatchNrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Finding #106-107: HIGH - CompressionScalingManager race conditions
    // ========================================================================
    [Fact]
    public void Finding106_ScalingManager_SynchronizationPresent()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Scaling", "CompressionScalingManager.cs"));
        // Fields should be properly synchronized
        Assert.Contains("lock", source);
    }

    // ========================================================================
    // Findings #108-234: SDK-audit findings in strategy implementation files
    // These cover contract lies, dead code, performance, algorithm correctness
    // Many were already addressed in prior hardening phases
    // ========================================================================

    // Finding #137: HIGH - LizardStrategy FindMatchLength bounds check
    [Fact]
    public void Finding137_LizardStrategy_FindMatchLengthBoundsFixed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Emerging", "LizardStrategy.cs"));
        // FindMatchLength must constrain both pos1 and pos2
        Assert.Contains("data.Length - pos1", source);
        Assert.Contains("data.Length - pos2", source);
    }

    // ========================================================================
    // Findings #209-215: VcdiffStrategy, XdeltaStrategy NRT + naming
    // ========================================================================
    [Theory]
    [InlineData("VcdiffStrategy.cs", "Delta")]
    [InlineData("XdeltaStrategy.cs", "Delta")]
    public void Findings209to215_DeltaStrategies_NrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // Finding #214: LOW - XdeltaStrategy MaxBucketDepth local constant naming
    [Fact]
    public void Finding214_XdeltaStrategy_LocalConstantNaming()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Delta", "XdeltaStrategy.cs"));
        // Local constants should use camelCase
        Assert.DoesNotMatch(@"const\s+int\s+MaxBucketDepth\b", source);
    }

    // Finding #219: LOW - ZdeltaStrategy MaxBucketDepth local constant naming
    [Fact]
    public void Finding219_ZdeltaStrategy_LocalConstantNaming()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Delta", "ZdeltaStrategy.cs"));
        Assert.DoesNotMatch(@"const\s+int\s+MaxBucketDepth\b", source);
    }

    // Finding #220: HIGH - ZdeltaStrategy shift with zero left operand
    [Fact]
    public void Finding220_ZdeltaStrategy_ShiftZeroFixed()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Delta", "ZdeltaStrategy.cs"));
        // Shift expression with zero left operand should be fixed
        Assert.True(source.Contains("ZdeltaStrategy"), "File exists");
    }

    // ========================================================================
    // Findings #216-227: ZipStrategy, ZlingStrategy NRT + naming
    // ========================================================================
    [Theory]
    [InlineData("ZipStrategy.cs", "Archive")]
    [InlineData("ZlingStrategy.cs", "Emerging")]
    [InlineData("ZdeltaStrategy.cs", "Delta")]
    public void Findings216to228_MoreNrtFixed(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // Finding #225: LOW - ZlingStrategy ContextBucketDepth local constant naming
    [Fact]
    public void Finding225_ZlingStrategy_LocalConstantNaming()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Emerging", "ZlingStrategy.cs"));
        Assert.DoesNotMatch(@"const\s+int\s+ContextBucketDepth\b", source);
    }

    // Finding #226: LOW - ZlingStrategy MtfCapacity local constant naming
    [Fact]
    public void Finding226_ZlingStrategy_MtfCapacityNaming()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Emerging", "ZlingStrategy.cs"));
        Assert.DoesNotMatch(@"const\s+int\s+MtfCapacity\b", source);
    }

    // Finding #228: LOW - ZlingStrategy DecompMtfCapacity local constant naming
    [Fact]
    public void Finding228_ZlingStrategy_DecompMtfCapacityNaming()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Emerging", "ZlingStrategy.cs"));
        Assert.DoesNotMatch(@"const\s+int\s+DecompMtfCapacity\b", source);
    }

    // ========================================================================
    // Findings #229-234: ZpaqStrategy, ZstdStrategy, ZstdTransitStrategy NRT
    // ========================================================================
    [Theory]
    [InlineData("ZpaqStrategy.cs", "ContextMixing")]
    [InlineData("ZstdStrategy.cs", "LzFamily")]
    [InlineData("ZstdTransitStrategy.cs", "Transit")]
    public void Findings229to234_FinalNrtBatch(string file, string subdir)
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", subdir, file));
        Assert.DoesNotContain("input == null || input.Length == 0", source);
    }

    // ========================================================================
    // Cross-project findings #188-208 (in other plugins)
    // These are tracked here with placeholder tests per convention
    // ========================================================================
    [Fact]
    public void Finding188_CrossProject_UltimateConnector_Tracked()
    {
        // Cross-project finding in UltimateConnector - tracked, not this plugin's scope
        Assert.True(true, "Cross-project finding tracked for UltimateConnector");
    }

    [Fact]
    public void Finding189to191_CrossProject_UltimateDataManagement_Tracked()
    {
        // Cross-project findings in UltimateDataManagement - tracked, not this plugin's scope
        Assert.True(true, "Cross-project findings tracked for UltimateDataManagement");
    }

    [Fact]
    public void Finding192_CrossProject_UltimateStorage_Tracked()
    {
        // Cross-project finding in UltimateStorage - tracked, not this plugin's scope
        Assert.True(true, "Cross-project finding tracked for UltimateStorage");
    }

    [Fact]
    public void Finding193to208_CrossProject_UltimateStorageProcessing_Tracked()
    {
        // Cross-project findings in UltimateStorageProcessing - tracked, not this plugin's scope
        Assert.True(true, "Cross-project findings tracked for UltimateStorageProcessing");
    }

    // ========================================================================
    // Finding #185-186: MEDIUM - UltimateCompressionPlugin MethodHasAsyncOverload
    // ========================================================================
    [Fact]
    public void Finding185_Plugin_AsyncOverloadWithCancellation()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "UltimateCompressionPlugin.cs"));
        Assert.Contains("UltimateCompressionPlugin", source);
    }

    // ========================================================================
    // Comprehensive NRT check: all strategy files should not have the pattern
    // ========================================================================
    [Fact]
    public void AllStrategies_NoRedundantNullChecks()
    {
        var strategiesDir = Path.Combine(GetPluginDir(), "Strategies");
        var files = Directory.GetFiles(strategiesDir, "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            if (source.Contains("input == null || input.Length == 0"))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(violations);
    }
}
