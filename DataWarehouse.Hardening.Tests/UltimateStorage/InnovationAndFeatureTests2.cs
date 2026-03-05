// Hardening tests for UltimateStorage Innovation/Feature/Kubernetes/Local findings 285-287, 376-381, 425-440, 467-472
// GeoSovereignStrategy, InfiniteDeduplicationStrategy, InfiniteStorageStrategy,
// KubernetesCsiStorageStrategy, LatencyBasedSelectionFeature, LifecycleManagementFeature,
// LegacyBridgeStrategy, LocalFileStrategy, LsmTreeEngine

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class InnovationAndFeatureTests2
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string InnovationDir = Path.Combine(PluginRoot, "Strategies", "Innovation");
    private static readonly string FeaturesDir = Path.Combine(PluginRoot, "Features");
    private static readonly string K8sDir = Path.Combine(PluginRoot, "Strategies", "Kubernetes");
    private static readonly string LocalDir = Path.Combine(PluginRoot, "Strategies", "Local");
    private static readonly string ScaleDir = Path.Combine(PluginRoot, "Strategies", "Scale");

    // === GeoSovereignStrategy (findings 285-287) ===

    /// <summary>
    /// Finding 285 (LOW): _enforceGdpr exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding285_GeoSovereign_EnforceGdprExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "GeoSovereignStrategy.cs"));
        Assert.Contains("internal bool EnforceGdpr", code);
    }

    // === InfiniteDeduplicationStrategy (findings 376-377) ===

    /// <summary>
    /// Finding 376 (LOW): _enableCrosstenantDedup exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding376_InfiniteDedup_CrosstenantDedupExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteDeduplicationStrategy.cs"));
        Assert.Contains("internal bool EnableCrosstenantDedup", code);
    }

    /// <summary>
    /// Finding 377 (LOW): TenantRefs collection now queried in chunk deletion logic.
    /// </summary>
    [Fact]
    public void Finding377_InfiniteDedup_TenantRefsQueried()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteDeduplicationStrategy.cs"));
        Assert.Contains("globalChunk.TenantRefs.Count == 0", code);
    }

    // === InfiniteStorageStrategy (findings 378-381) ===

    /// <summary>
    /// Finding 378 (LOW): _enableAutoRebalancing exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding378_InfiniteStorage_AutoRebalancingExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteStorageStrategy.cs"));
        Assert.Contains("internal bool EnableAutoRebalancing", code);
    }

    /// <summary>
    /// Finding 380 (LOW): _healthCheckTimer exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding380_InfiniteStorage_HealthCheckTimerExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteStorageStrategy.cs"));
        Assert.Contains("internal Timer? HealthCheckTimer", code);
    }

    /// <summary>
    /// Finding 381 (HIGH): Async void lambda in Timer callback wrapped with try/catch.
    /// </summary>
    [Fact]
    public void Finding381_InfiniteStorage_AsyncTimerCallback_Safe()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteStorageStrategy.cs"));
        // Timer callback should have try/catch wrapping
        Assert.Contains("try { await MonitorProvidersHealthAsync()", code);
        Assert.Contains("catch (Exception ex)", code);
    }

    // === KubernetesCsiStorageStrategy (findings 425-436) ===

    /// <summary>
    /// Finding 425 (LOW): _socketPath exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding425_K8sCsi_SocketPathExposed()
    {
        var code = File.ReadAllText(Path.Combine(K8sDir, "KubernetesCsiStorageStrategy.cs"));
        Assert.Contains("internal string SocketPath", code);
    }

    /// <summary>
    /// Findings 426-428 (MEDIUM): Redundant null checks removed from TryGetValue results.
    /// </summary>
    [Fact]
    public void Findings426to428_K8sCsi_NullChecks_Removed()
    {
        var code = File.ReadAllText(Path.Combine(K8sDir, "KubernetesCsiStorageStrategy.cs"));
        // rb != null check should be removed since TryGetValue always gives non-null on Dictionary<string,object>
        Assert.DoesNotContain("&& rb != null", code);
        Assert.DoesNotContain("&& lb != null", code);
    }

    /// <summary>
    /// Finding 429 (MEDIUM): KeyValuePair struct equality replaced with explicit comparison.
    /// </summary>
    [Fact]
    public void Finding429_K8sCsi_StructEquality_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(K8sDir, "KubernetesCsiStorageStrategy.cs"));
        Assert.DoesNotContain("t.SequenceEqual(segDict)", code);
        Assert.Contains("t.Count == segDict.Count", code);
    }

    /// <summary>
    /// Findings 430-431 (LOW): CsiEncryptionType enum members PascalCase.
    /// </summary>
    [Fact]
    public void Findings430to431_K8sCsi_EncryptionType_PascalCase()
    {
        var code = File.ReadAllText(Path.Combine(K8sDir, "KubernetesCsiStorageStrategy.cs"));
        Assert.Contains("Aes256Gcm", code);
        Assert.Contains("Aes256Cbc", code);
        Assert.DoesNotContain("AES256GCM", code);
        Assert.DoesNotContain("AES256CBC", code);
    }

    // === LatencyBasedSelectionFeature (finding 437) ===

    /// <summary>
    /// Finding 437 (HIGH): Async void lambda in Timer callback wrapped with try/catch.
    /// </summary>
    [Fact]
    public void Finding437_LatencyBased_AsyncTimerCallback_Safe()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "LatencyBasedSelectionFeature.cs"));
        Assert.Contains("try { await RunHealthCheckCycleAsync()", code);
        Assert.Contains("catch (Exception ex)", code);
    }

    // === LegacyBridgeStrategy (findings 438-439) ===

    /// <summary>
    /// Findings 438-439 (LOW): EBCDIC method names renamed to PascalCase.
    /// </summary>
    [Fact]
    public void Findings438to439_LegacyBridge_EbcdicMethods_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "LegacyBridgeStrategy.cs"));
        Assert.Contains("ConvertToEbcdic", code);
        Assert.Contains("ConvertFromEbcdic", code);
        Assert.DoesNotContain("ConvertToEBCDIC", code);
        Assert.DoesNotContain("ConvertFromEBCDIC", code);
    }

    // === LocalFileStrategy (findings 467-471) ===

    /// <summary>
    /// Findings 467-471 (LOW): MediaType enum members PascalCase.
    /// </summary>
    [Theory]
    [InlineData("Usb")]
    [InlineData("SdCard")]
    public void Findings467to471_LocalFile_MediaType_PascalCase(string memberName)
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "LocalFileStrategy.cs"));
        Assert.Contains(memberName, code);
    }

    /// <summary>
    /// Findings 467-471: Old ALL_CAPS enum members removed from enum declaration.
    /// </summary>
    [Fact]
    public void Findings467to471_LocalFile_OldEnumValues_Removed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "LocalFileStrategy.cs"));
        // Check the enum declaration block does not have old names
        // Find the MediaType enum section
        var enumStart = code.IndexOf("public enum MediaType");
        var enumEnd = code.IndexOf("}", enumStart);
        var enumBlock = code.Substring(enumStart, enumEnd - enumStart);
        Assert.DoesNotContain("        USB,", enumBlock);
        Assert.DoesNotContain("        SDCard,", enumBlock);
    }

    // === LsmTreeEngine (findings 472-476) ===

    /// <summary>
    /// Finding 472 (LOW): LoadExistingSSTables renamed to LoadExistingSsTables.
    /// </summary>
    [Fact]
    public void Finding472_LsmTree_MethodRenamed()
    {
        var code = File.ReadAllText(Path.Combine(ScaleDir, "LsmTree", "LsmTreeEngine.cs"));
        Assert.Contains("LoadExistingSsTables", code);
        Assert.DoesNotContain("LoadExistingSSTables", code);
    }

    /// <summary>
    /// Finding 475 (LOW): Unused sstable assignment removed.
    /// </summary>
    [Fact]
    public void Finding475_LsmTree_SstableAssignment_Removed()
    {
        var code = File.ReadAllText(Path.Combine(ScaleDir, "LsmTree", "LsmTreeEngine.cs"));
        Assert.DoesNotContain("var sstable = await SSTableWriter", code);
        Assert.DoesNotContain("sstable = sstable with", code);
    }
}
