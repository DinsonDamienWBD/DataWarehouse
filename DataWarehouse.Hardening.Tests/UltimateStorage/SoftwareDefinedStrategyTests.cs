// Hardening tests for UltimateStorage SoftwareDefined strategy findings 273-321, 410-495
// FoundationDbStrategy, GlusterFsStrategy, GpfsStrategy, JuiceFsStrategy,
// LizardFsStrategy, LustreStrategy

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class SoftwareDefinedStrategyTests
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string SdDir = Path.Combine(PluginRoot, "Strategies", "SoftwareDefined");
    private static readonly string SpecializedDir = Path.Combine(PluginRoot, "Strategies", "Specialized");

    // === FoundationDbStrategy (findings 273-276) ===

    /// <summary>
    /// Finding 273 (LOW): _transactionTimeout exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding273_FoundationDb_TransactionTimeoutExposed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "FoundationDbStrategy.cs"));
        Assert.Contains("internal TimeSpan TransactionTimeout", code);
    }

    /// <summary>
    /// Finding 275 (LOW): Assignment not used - meta declaration fixed.
    /// </summary>
    [Fact]
    public void Finding275_FoundationDb_MetaAssignment_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "FoundationDbStrategy.cs"));
        Assert.Contains("StorageObjectMetadata meta;", code);
        Assert.DoesNotContain("StorageObjectMetadata? meta = null;", code);
    }

    /// <summary>
    /// Finding 276 (MEDIUM): Condition always true removed - meta != null check eliminated.
    /// </summary>
    [Fact]
    public void Finding276_FoundationDb_AlwaysTrueCondition_Removed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "FoundationDbStrategy.cs"));
        // StorageObjectMetadata (non-nullable) used directly, no null check needed
        Assert.Contains("yield return meta;", code);
    }

    // === GlusterFsStrategy (findings 288-321) ===

    /// <summary>
    /// Finding 288 (LOW): _arbiterCount exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding288_GlusterFs_ArbiterCountExposed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GlusterFsStrategy.cs"));
        Assert.Contains("internal int ArbiterCount", code);
    }

    /// <summary>
    /// Finding 318 (LOW): _fileLocks collection exposed for diagnostics.
    /// </summary>
    [Fact]
    public void Finding318_GlusterFs_FileLocksExposed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GlusterFsStrategy.cs"));
        Assert.Contains("internal int FileLockCount", code);
    }

    /// <summary>
    /// Finding 319 (LOW): bytesWritten initial assignment removed.
    /// </summary>
    [Fact]
    public void Finding319_GlusterFs_BytesWritten_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GlusterFsStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
        Assert.DoesNotContain("long bytesWritten = 0;", code);
    }

    /// <summary>
    /// Finding 320 (MEDIUM): Redundant null check removed from metadata parameter.
    /// </summary>
    [Fact]
    public void Finding320_GlusterFs_NullCheck_Removed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GlusterFsStrategy.cs"));
        // Should check count only, not null
        Assert.DoesNotContain("metadata == null || metadata.Count == 0", code);
        Assert.Contains("metadata.Count == 0", code);
    }

    /// <summary>
    /// Finding 321 (MEDIUM): Object initializer separated from using variable.
    /// </summary>
    [Fact]
    public void Finding321_GlusterFs_UsingVarInitializer_Separated()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GlusterFsStrategy.cs"));
        Assert.Contains("using var process = new System.Diagnostics.Process();", code);
        Assert.Contains("process.StartInfo =", code);
    }

    // === GpfsStrategy (findings 322-359) ===

    /// <summary>
    /// Finding 322 (LOW): _managementApiUser exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding322_Gpfs_ManagementApiUserExposed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GpfsStrategy.cs"));
        Assert.Contains("internal string? ManagementApiUser", code);
    }

    /// <summary>
    /// Finding 324 (LOW): _useManagementApi exposed as internal property.
    /// </summary>
    [Fact]
    public void Finding324_Gpfs_UseManagementApiExposed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GpfsStrategy.cs"));
        Assert.Contains("internal bool UseManagementApi", code);
    }

    /// <summary>
    /// Finding 356 (LOW): _fileLocks exposed for diagnostics.
    /// </summary>
    [Fact]
    public void Finding356_Gpfs_FileLocksExposed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GpfsStrategy.cs"));
        Assert.Contains("internal int FileLockCount", code);
    }

    /// <summary>
    /// Finding 357 (LOW): bytesWritten initial assignment removed.
    /// </summary>
    [Fact]
    public void Finding357_Gpfs_BytesWritten_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GpfsStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
    }

    /// <summary>
    /// Finding 358 (MEDIUM): Redundant null check removed.
    /// </summary>
    [Fact]
    public void Finding358_Gpfs_NullCheck_Removed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GpfsStrategy.cs"));
        Assert.DoesNotContain("metadata == null || metadata.Count == 0", code);
    }

    /// <summary>
    /// Finding 359 (MEDIUM): Object initializer separated from using variable.
    /// </summary>
    [Fact]
    public void Finding359_Gpfs_UsingVarInitializer_Separated()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "GpfsStrategy.cs"));
        Assert.Contains("using var process = new System.Diagnostics.Process();", code);
    }

    // === JuiceFsStrategy (findings 410-424) ===

    /// <summary>
    /// Finding 420 (LOW): bytesWritten initial assignment removed.
    /// </summary>
    [Fact]
    public void Finding420_JuiceFs_BytesWritten_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "JuiceFsStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
        Assert.DoesNotContain("long bytesWritten = 0;", code);
    }

    /// <summary>
    /// Findings 421-424 (LOW): Response/xml/json initial null assignments removed where unused.
    /// Note: SendWithRetryAsync legitimately needs = null for loop scope.
    /// </summary>
    [Fact]
    public void Findings421to424_JuiceFs_Assignments_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "JuiceFsStrategy.cs"));
        // The WebDAV and list method declarations should not have = null
        Assert.Contains("HttpResponseMessage? response;", code);
        Assert.Contains("string? xml;", code);
        Assert.Contains("string? json;", code);
    }

    // === LizardFsStrategy (findings 446-466) ===

    /// <summary>
    /// Finding 465 (LOW): bytesWritten initial assignment removed.
    /// </summary>
    [Fact]
    public void Finding465_LizardFs_BytesWritten_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LizardFsStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
    }

    /// <summary>
    /// Finding 466 (MEDIUM): Redundant null check removed from metadata parameter.
    /// </summary>
    [Fact]
    public void Finding466_LizardFs_NullCheck_Removed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LizardFsStrategy.cs"));
        Assert.DoesNotContain("metadata == null || metadata.Count == 0", code);
    }

    // === LustreStrategy (findings 477-495) ===

    /// <summary>
    /// Finding 477 (LOW): s_shellInjectionChars renamed to ShellInjectionChars.
    /// </summary>
    [Fact]
    public void Finding477_Lustre_ShellInjectionChars_Renamed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LustreStrategy.cs"));
        Assert.Contains("ShellInjectionChars", code);
        Assert.DoesNotContain("s_shellInjectionChars", code);
    }

    /// <summary>
    /// Finding 480 (LOW): _availableOstPools collection exposed for querying.
    /// </summary>
    [Fact]
    public void Finding480_Lustre_OstPoolsExposed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LustreStrategy.cs"));
        Assert.Contains("internal IReadOnlyList<string> AvailableOstPools", code);
    }

    /// <summary>
    /// Finding 487 (LOW): _keyToFidCache collection exposed for diagnostics.
    /// </summary>
    [Fact]
    public void Finding487_Lustre_FidCacheExposed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LustreStrategy.cs"));
        Assert.Contains("internal int FidCacheCount", code);
    }

    /// <summary>
    /// Finding 491 (LOW): bytesWritten initial assignment removed.
    /// </summary>
    [Fact]
    public void Finding491_Lustre_BytesWritten_Fixed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LustreStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
    }

    /// <summary>
    /// Finding 492 (MEDIUM): Redundant null check removed.
    /// </summary>
    [Fact]
    public void Finding492_Lustre_NullCheck_Removed()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LustreStrategy.cs"));
        Assert.DoesNotContain("metadata == null || metadata.Count == 0", code);
    }

    /// <summary>
    /// Finding 493 (MEDIUM): Object initializer separated from using variable.
    /// </summary>
    [Fact]
    public void Finding493_Lustre_UsingVarInitializer_Separated()
    {
        var code = File.ReadAllText(Path.Combine(SdDir, "LustreStrategy.cs"));
        Assert.Contains("using var process = new Process();", code);
        Assert.Contains("process.StartInfo = startInfo;", code);
    }
}
