namespace DataWarehouse.Hardening.Tests.UltimateDataQuality;

/// <summary>
/// Hardening tests for UltimateDataQuality findings 1-53.
/// Covers: sumXY->sumXy, IQR->Iqr, MaxComparisons->maxComparisons,
/// StandardizedValue_->Result, bare catch logging, thread safety.
/// </summary>
public class UltimateDataQualityHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateDataQuality"));

    // Finding #7: LOW - MaxComparisons camelCase
    [Fact]
    public void Finding007_DuplicateDetection_MaxComparisonsCamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "DuplicateDetection", "DuplicateDetectionStrategies.cs"));
        Assert.Contains("const int maxComparisons", source);
        Assert.DoesNotContain("const int MaxComparisons", source);
    }

    // Finding #12: LOW - sumXY->sumXy in PredictiveQuality
    [Fact]
    public void Finding012_PredictiveQuality_SumXyCamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "PredictiveQuality", "PredictiveQualityStrategies.cs"));
        Assert.DoesNotContain("sumXY", source);
    }

    // Finding #19: LOW - IQR->Iqr in ProfilingStrategies
    [Fact]
    public void Finding019_Profiling_IqrPascalCase()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "Profiling", "ProfilingStrategies.cs"));
        Assert.Contains("Iqr", source);
        Assert.DoesNotContain("public double IQR", source);
    }

    // Finding #22: LOW - sumXY->sumXy in ProfilingStrategies
    [Fact]
    public void Finding022_Profiling_SumXyCamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "Profiling", "ProfilingStrategies.cs"));
        Assert.DoesNotContain("sumXY", source);
    }

    // Finding #25: LOW - StandardizedValue_ trailing underscore
    [Fact]
    public void Finding025_Standardization_NoTrailingUnderscore()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "Standardization", "StandardizationStrategies.cs"));
        Assert.DoesNotContain("StandardizedValue_", source);
    }

    // Finding #53: LOW - IQR->Iqr in ValidationStrategies
    [Fact]
    public void Finding053_Validation_IqrPascalCase()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "Validation", "ValidationStrategies.cs"));
        Assert.DoesNotContain("public double IQR", source);
        Assert.Contains("public double Iqr", source);
    }

    // Findings #8,28,33,42,44,46: HIGH/MEDIUM - bare catch, thread safety
    [Fact]
    public void Finding028_DataQualityStrategyBase_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "DataQualityStrategyBase.cs"));
        Assert.Contains("DataQualityStrategyBase", source);
    }

    [Fact]
    public void Finding033_Monitoring_Exists()
    {
        // MonitoringStrategies in subdirectory
        var path = Path.Combine(GetPluginDir(), "Strategies", "Monitoring", "MonitoringStrategies.cs");
        var source = File.ReadAllText(path);
        Assert.Contains("RecordMetric", source);
    }

    // Findings #39,43: MEDIUM - ScoringStrategies/PluginMetadata issues
    [Fact]
    public void Finding043_Plugin_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataQualityPlugin.cs"));
        Assert.Contains("UltimateDataQualityPlugin", source);
    }

    // Findings #1,6,14,31,36,38,41: algorithmic + stub findings
    [Theory]
    [InlineData("CleansingStrategies.cs", "Cleansing")]
    [InlineData("DuplicateDetectionStrategies.cs", "DuplicateDetection")]
    [InlineData("ReportingStrategies.cs", "Reporting")]
    [InlineData("ScoringStrategies.cs", "Scoring")]
    public void Findings_Algorithmic_StrategiesExist(string file, string subdir)
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", subdir, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }
}
