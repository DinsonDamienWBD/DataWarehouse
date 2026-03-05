// Hardening tests for UltimateStorage Network strategy findings 262-267, 277-279, 407-409
// EdgeCascade, FcStrategy, FtpStrategy, IscsiStrategy

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class NetworkStrategyTests
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string NetworkDir = Path.Combine(PluginRoot, "Strategies", "Network");
    private static readonly string InnovationDir = Path.Combine(PluginRoot, "Strategies", "Innovation");

    // === EdgeCascadeStrategy (findings 262-264) ===

    /// <summary>
    /// Finding 263 (LOW): _edgeCacheMaxBytes exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding263_EdgeCascade_CacheMaxBytesExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "EdgeCascadeStrategy.cs"));
        Assert.Contains("internal long EdgeCacheMaxBytes", code);
    }

    /// <summary>
    /// Finding 264 (LOW): _enableCacheWarming exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding264_EdgeCascade_CacheWarmingExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "EdgeCascadeStrategy.cs"));
        Assert.Contains("internal bool EnableCacheWarming", code);
    }

    // === FcStrategy (findings 265-267) ===

    /// <summary>
    /// Finding 265 (LOW): _multipathStates collection exposed for querying.
    /// </summary>
    [Fact]
    public void Finding265_Fc_MultipathStatesExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FcStrategy.cs"));
        Assert.Contains("internal IReadOnlyDictionary<string, MultipathState> MultipathStates", code);
    }

    /// <summary>
    /// Finding 266 (MEDIUM): Useless subtraction of 0 removed.
    /// </summary>
    [Fact]
    public void Finding266_Fc_UselessSubtractionRemoved()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FcStrategy.cs"));
        Assert.DoesNotContain("long usedCapacity = 0;", code);
        Assert.DoesNotContain("totalCapacity - usedCapacity", code);
    }

    /// <summary>
    /// Finding 267 (LOW): Assignment not used - removed initial null assignment.
    /// </summary>
    [Fact]
    public void Finding267_Fc_AssignmentNotUsed_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FcStrategy.cs"));
        // Should use declaration without initializer
        Assert.Contains("object? data;", code);
        Assert.DoesNotContain("object? data = null;", code);
    }

    // === FtpStrategy (findings 277-279) ===

    /// <summary>
    /// Finding 277 (LOW): _useCompression exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding277_Ftp_UseCompressionExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FtpStrategy.cs"));
        Assert.Contains("internal bool UseCompression", code);
    }

    /// <summary>
    /// Finding 279 (MEDIUM): MethodHasAsyncOverload - use DisposeAsync.
    /// </summary>
    [Fact]
    public void Finding279_Ftp_DisposeAsync()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FtpStrategy.cs"));
        Assert.Contains("await memoryStream.DisposeAsync()", code);
    }

    // === IscsiStrategy (findings 407-409) ===

    /// <summary>
    /// Finding 407 (LOW): _dataPDUInOrder renamed to _dataPduInOrder.
    /// </summary>
    [Fact]
    public void Finding407_Iscsi_DataPduInOrder_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "IscsiStrategy.cs"));
        Assert.Contains("_dataPduInOrder", code);
        Assert.DoesNotContain("_dataPDUInOrder", code);
    }

    /// <summary>
    /// Finding 408 (MEDIUM): MethodHasAsyncOverload - stream DisposeAsync.
    /// </summary>
    [Fact]
    public void Finding408_Iscsi_StreamDisposeAsync()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "IscsiStrategy.cs"));
        Assert.Contains("await _stream.DisposeAsync()", code);
    }

    /// <summary>
    /// Finding 409 (LOW): StartLBA renamed to StartLba.
    /// </summary>
    [Fact]
    public void Finding409_Iscsi_StartLba_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "IscsiStrategy.cs"));
        Assert.Contains("StartLba", code);
        Assert.DoesNotContain("StartLBA", code);
    }
}
