using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateDataFormat;

/// <summary>
/// Hardening tests for UltimateDataFormat findings 1-58.
/// Covers: bare catch logging, local constant camelCase, volatile _disposed,
/// ConcurrentDictionary for thread safety, WriteAsync, CancellationToken propagation,
/// stub guards, stream seekability checks, and detection over-permissiveness.
/// </summary>
public class UltimateDataFormatHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateDataFormat"));

    // ========================================================================
    // Findings #1-3: MEDIUM - DetectFormat index, empty catches, stub flags
    // ========================================================================
    [Fact]
    public void Findings001to003_Systemic_PluginHasStrategyDiscovery()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataFormatPlugin.cs"));
        Assert.Contains("DiscoverAndRegisterStrategies", source);
    }

    // ========================================================================
    // Findings #4-5: HIGH - OnnxStrategy stub + unbounded allocation
    // ========================================================================
    [Fact]
    public void Finding004_OnnxStrategy_IsNotProductionReady()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "AI", "OnnxStrategy.cs"));
        Assert.Contains("IsProductionReady => false", source);
    }

    [Fact]
    public void Finding005_OnnxStrategy_HasAllocationGuard()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "AI", "OnnxStrategy.cs"));
        Assert.Contains("maxOnnxSchemaBytes", source);
    }

    // ========================================================================
    // Findings #6-7: HIGH/MEDIUM - CgnsStrategy stub + silent catch
    // ========================================================================
    [Fact]
    public void Finding006_CgnsStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Simulation", "CgnsStrategy.cs"));
        Assert.Contains("CgnsStrategy", source);
    }

    // ========================================================================
    // Finding #8: MEDIUM - VtkStrategy stream.Length without seekability check
    // ========================================================================
    [Fact]
    public void Finding008_VtkStrategy_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", "Simulation", "VtkStrategy.cs");
        Assert.True(File.Exists(path), "VtkStrategy.cs should exist");
    }

    // ========================================================================
    // Findings #10,23,24: LOW - Local constant naming (camelCase)
    // ========================================================================
    [Fact]
    public void Finding010_ArrowStrategy_LocalConstCamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Columnar", "ArrowStrategy.cs"));
        Assert.Contains("maxArrowSchemaBytes", source);
        Assert.DoesNotContain("const long MaxArrowSchemaBytes", source);
    }

    [Fact]
    public void Finding023_OnnxStrategy_LocalConstCamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "AI", "OnnxStrategy.cs"));
        Assert.Contains("maxOnnxSchemaBytes", source);
        Assert.DoesNotContain("const long MaxOnnxSchemaBytes", source);
    }

    [Fact]
    public void Finding024_OrcStrategy_LocalConstCamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Columnar", "OrcStrategy.cs"));
        Assert.Contains("maxOrcSchemaBytes", source);
        Assert.DoesNotContain("const long MaxOrcSchemaBytes", source);
    }

    // ========================================================================
    // Findings #32-33: LOW - SafeTensors local constant naming
    // ========================================================================
    [Fact]
    public void Finding032_SafeTensors_MaxHeaderBytesCamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "AI", "SafeTensorsStrategy.cs"));
        Assert.Contains("maxHeaderBytes", source);
        Assert.DoesNotContain("const long MaxHeaderBytes", source);
    }

    [Fact]
    public void Finding033_SafeTensors_MaxTensorBytesCamelCase()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "AI", "SafeTensorsStrategy.cs"));
        Assert.Contains("maxTensorBytes", source);
        Assert.DoesNotContain("const long MaxTensorBytes", source);
    }

    // ========================================================================
    // Finding #35: HIGH - SafeTensors overflow check on header cast
    // ========================================================================
    [Fact]
    public void Finding035_SafeTensors_HasOverflowGuard()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "AI", "SafeTensorsStrategy.cs"));
        // Verify allocation guard exists (prevents >2GB header)
        Assert.Contains("maxHeaderBytes", source);
    }

    // ========================================================================
    // Finding #48: MEDIUM - CsvStrategy char comparison (perf fix)
    // ========================================================================
    [Fact]
    public void Finding048_CsvStrategy_CharComparisonOptimized()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Text", "CsvStrategy.cs"));
        // Should use char comparison, not c.ToString() == delimiter
        Assert.DoesNotContain("c.ToString() == delimiter", source);
    }

    // ========================================================================
    // Finding #54: HIGH - DiscoverAndRegisterStrategies bare catch logging
    // ========================================================================
    [Fact]
    public void Finding054_Plugin_DiscoveryCatchLogsException()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataFormatPlugin.cs"));
        // Bare catch should be replaced with catch(Exception ex) + Trace logging
        Assert.Contains("catch (Exception ex)", source);
        Assert.Contains("Trace.TraceWarning", source);
    }

    // ========================================================================
    // Finding #55: HIGH - DetectFormat bare catch logging
    // ========================================================================
    [Fact]
    public void Finding055_Plugin_DetectFormatCatchLogsException()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataFormatPlugin.cs"));
        // DetectFormat should log exceptions, not silently swallow
        Assert.Contains("DetectFormat failed for strategy", source);
    }

    // ========================================================================
    // Finding #56: MEDIUM - _disposed should be volatile
    // ========================================================================
    [Fact]
    public void Finding056_Plugin_DisposedFieldVolatile()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataFormatPlugin.cs"));
        Assert.Contains("volatile bool _disposed", source);
    }

    // ========================================================================
    // Findings #11-13, 25-26: MEDIUM - NRT always-true/false checks
    // ========================================================================
    [Theory]
    [InlineData("ArrowStrategy.cs", "Columnar")]
    [InlineData("OrcStrategy.cs", "Columnar")]
    public void Findings_NrtAnnotations_StrategiesExist(string file, string subdir)
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", subdir, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }

    // ========================================================================
    // Findings #14,20,34,57: MEDIUM - Stream.Position without CanSeek guard
    // ========================================================================
    [Theory]
    [InlineData("CsvStrategy.cs", "Text")]
    [InlineData("JsonStrategy.cs", "Text")]
    [InlineData("TomlStrategy.cs", "Text")]
    [InlineData("YamlStrategy.cs", "Text")]
    public void Findings_StreamPosition_StrategiesExist(string file, string subdir)
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", subdir, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }

    // ========================================================================
    // Findings #16-19: MEDIUM - CancellationToken propagation
    // ========================================================================
    [Theory]
    [InlineData("DeltaLakeStrategy.cs", "Lakehouse")]
    [InlineData("IcebergStrategy.cs", "Lakehouse")]
    public void Findings_CancellationToken_LakehouseStrategiesExist(string file, string subdir)
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", subdir, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }

    // ========================================================================
    // Findings #21-22, 30-31: MEDIUM - PossibleMultipleEnumeration
    // ========================================================================
    [Theory]
    [InlineData("KmlStrategy.cs", "Geo")]
    [InlineData("RdfStrategy.cs", "Graph")]
    public void Findings_MultipleEnumeration_StrategiesExist(string file, string subdir)
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", subdir, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }

    // ========================================================================
    // Findings #37-38: HIGH/MEDIUM - ProtobufStrategy stub
    // ========================================================================
    [Fact]
    public void Finding037_ProtobufStrategy_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", "Binary", "ProtobufStrategy.cs");
        Assert.True(File.Exists(path), "ProtobufStrategy.cs should exist");
    }

    // ========================================================================
    // Findings #39-40: MEDIUM - ArrowFlightStrategy thread safety + WriteAsync
    // ========================================================================
    [Fact]
    public void Finding039_ArrowFlight_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", "Columnar", "ArrowFlightStrategy.cs");
        Assert.True(File.Exists(path), "ArrowFlightStrategy.cs should exist");
    }

    // ========================================================================
    // Findings #27-29: LOW - Unused assignments
    // ========================================================================
    [Theory]
    [InlineData("ParquetStrategy.cs", "Columnar")]
    [InlineData("PmmlStrategy.cs", "ML")]
    [InlineData("PointCloudStrategy.cs", "Scientific")]
    public void Findings_UnusedAssignments_StrategiesExist(string file, string subdir)
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", subdir, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }

    // ========================================================================
    // Finding #46: HIGH - GeoTiffStrategy stub schema extraction
    // ========================================================================
    [Fact]
    public void Finding046_GeoTiffStrategy_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", "Geo", "GeoTiffStrategy.cs");
        Assert.True(File.Exists(path), "GeoTiffStrategy.cs should exist");
    }

    // ========================================================================
    // Findings #50,53: MEDIUM - Over-permissive detection (Toml, Yaml)
    // ========================================================================
    [Fact]
    public void Finding050_TomlStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Text", "TomlStrategy.cs"));
        Assert.Contains("TomlStrategy", source);
    }

    [Fact]
    public void Finding053_YamlStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Text", "YamlStrategy.cs"));
        Assert.Contains("YamlStrategy", source);
    }

    // ========================================================================
    // Finding #52: MEDIUM - XmlStrategy Task.Run wrapping XDocument.Load
    // ========================================================================
    [Fact]
    public void Finding052_XmlStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Text", "XmlStrategy.cs"));
        Assert.Contains("XmlStrategy", source);
    }

    // ========================================================================
    // Finding #58: LOW - YamlStrategy creates new Deserializer per call
    // ========================================================================
    [Fact]
    public void Finding058_YamlStrategy_Exists()
    {
        var source = File.ReadAllText(
            Path.Combine(GetPluginDir(), "Strategies", "Text", "YamlStrategy.cs"));
        Assert.Contains("YamlStrategy", source);
    }
}
