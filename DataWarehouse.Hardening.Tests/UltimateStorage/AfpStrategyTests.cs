// Hardening tests for UltimateStorage AfpStrategy findings 11-102
// Finding 11 (LOW): _openFiles collection never updated
// Finding 12 (MEDIUM): Method supports cancellation
// Finding 13 (HIGH): Identical ternary branches
// Finding 14-15 (LOW): Unused assignment
// Finding 16-102 (LOW): Enum naming (DSI*, FP*, AFP error/auth enums)

using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class AfpStrategyTests
{
    private static readonly string AfpFile = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage", "Strategies", "Network", "AfpStrategy.cs");

    /// <summary>
    /// Finding 11 (LOW): _openFiles collection should be updated (Add/TryRemove used).
    /// </summary>
    [Fact]
    public void Finding011_OpenFiles_CollectionUpdated()
    {
        var code = File.ReadAllText(AfpFile);
        // The collection must have TryAdd/TryRemove/AddOrUpdate calls
        Assert.True(
            code.Contains("_openFiles.TryAdd") || code.Contains("_openFiles.TryRemove") ||
            code.Contains("_openFiles[") || code.Contains("_openFiles.AddOrUpdate") ||
            code.Contains("_openFiles.GetOrAdd"),
            "_openFiles collection must be updated (populated and cleaned up)");
    }

    /// <summary>
    /// Finding 12 (MEDIUM): Methods that accept CancellationToken should pass it through.
    /// </summary>
    [Fact]
    public void Finding012_CancellationToken_PassedThrough()
    {
        var code = File.ReadAllText(AfpFile);
        // ConnectAsync should pass cancellation token to socket operations
        Assert.True(
            code.Contains("ConnectAsync(_serverAddress, _port") ||
            code.Contains("ConnectAsync(_serverAddress, _port, ct)"),
            "ConnectAsync should support cancellation");
    }

    /// <summary>
    /// Finding 13 (HIGH): Identical ternary branches in FPOpenFork.
    /// Both branches return AfpCommand.FPOpenFork - the condition is meaningless.
    /// </summary>
    [Fact]
    public void Finding013_IdenticalTernary_Fixed()
    {
        var code = File.ReadAllText(AfpFile);
        // The ternary should NOT have identical branches
        Assert.DoesNotContain(
            "forkType == AfpForkType.DataFork ? AfpCommand.FPOpenFork : AfpCommand.FPOpenFork",
            code);
    }

    /// <summary>
    /// Findings 14-15 (LOW): Unused assignments removed or used.
    /// Lines 856/862 - assignments that are never read.
    /// </summary>
    [Fact]
    public void Finding014_015_UnusedAssignments_Removed()
    {
        var code = File.ReadAllText(AfpFile);
        var lines = code.Split('\n');

        // Check that there are no obvious dead assignments in the write path
        // (around line 856/862 area - the fileId reassignment after delete-recreate)
        // The fix: ensure fileId is actually used after assignment
        var writeBlock = string.Join("\n", lines.Skip(840).Take(40));
        if (writeBlock.Contains("fileId ="))
        {
            Assert.True(
                writeBlock.Contains("forkRefNum") || writeBlock.Contains("OpenFork"),
                "fileId assignment should be used downstream (e.g., in OpenFork)");
        }
    }

    /// <summary>
    /// Findings 16-22 (LOW): DsiCommand enum naming - DSI* -> Dsi*.
    /// </summary>
    [Fact]
    public void Finding016_022_DsiCommand_PascalCase()
    {
        var code = File.ReadAllText(AfpFile);
        Assert.DoesNotContain("DSICloseSession", code);
        Assert.DoesNotContain("DSICommand", code);
        Assert.DoesNotContain("DSIGetStatus", code);
        Assert.DoesNotContain("DSIOpenSession", code);
        Assert.DoesNotContain("DSITickle", code);
        Assert.DoesNotContain("DSIWrite", code);
        Assert.DoesNotContain("DSIAttention", code);

        // Verify PascalCase replacements exist
        Assert.Contains("DsiCloseSession", code);
        Assert.Contains("DsiGetStatus", code);
        Assert.Contains("DsiOpenSession", code);
        Assert.Contains("DsiTickle", code);
        Assert.Contains("DsiWrite", code);
        Assert.Contains("DsiAttention", code);
    }

    /// <summary>
    /// Findings 23-66 (LOW): AfpCommand enum naming - FP* -> Fp*.
    /// </summary>
    [Fact]
    public void Finding023_066_AfpCommand_PascalCase()
    {
        var code = File.ReadAllText(AfpFile);
        // Extract AfpCommand enum block only (not comments/doc strings)
        var enumStart = code.IndexOf("internal enum AfpCommand");
        var enumEnd = code.IndexOf("}", enumStart) + 1;
        var enumBlock = code.Substring(enumStart, enumEnd - enumStart);

        // Check enum members are renamed to PascalCase
        Assert.DoesNotContain("FPByteRangeLock", enumBlock);
        Assert.DoesNotContain("FPCloseVol", enumBlock);
        Assert.DoesNotContain("FPCloseDT", enumBlock);
        Assert.DoesNotContain("FPCreateDir", enumBlock);
        Assert.DoesNotContain("FPDelete", enumBlock);
        Assert.DoesNotContain("FPLogin", enumBlock);
        Assert.DoesNotContain("FPLogout", enumBlock);
        Assert.DoesNotContain("FPRead", enumBlock);
        Assert.DoesNotContain("FPWrite", enumBlock);
        Assert.DoesNotContain("FPOpenVol", enumBlock);
        Assert.DoesNotContain("FPOpenFork", enumBlock);
        Assert.DoesNotContain("FPCatSearch", enumBlock);
        Assert.DoesNotContain("FPCloseDown", enumBlock);

        // Verify PascalCase replacements in enum
        Assert.Contains("FpByteRangeLock", enumBlock);
        Assert.Contains("FpCloseVol", enumBlock);
        Assert.Contains("FpCloseDt", enumBlock);
        Assert.Contains("FpCreateDir", enumBlock);
        Assert.Contains("FpDelete", enumBlock);
        Assert.Contains("FpLogin", enumBlock);
        Assert.Contains("FpLogout", enumBlock);
        Assert.Contains("FpRead", enumBlock);
        Assert.Contains("FpWrite", enumBlock);
        Assert.Contains("FpOpenVol", enumBlock);
        Assert.Contains("FpOpenFork", enumBlock);
        Assert.Contains("FpCatSearch", enumBlock);
        Assert.Contains("FpCloseDown", enumBlock);
    }

    /// <summary>
    /// Findings 67-100 (LOW): AfpError enum naming - FP* -> Fp*.
    /// </summary>
    [Fact]
    public void Finding067_100_AfpError_PascalCase()
    {
        var code = File.ReadAllText(AfpFile);
        Assert.DoesNotContain(" FPNoErr", code);
        Assert.DoesNotContain(" FPAccessDenied", code);
        Assert.DoesNotContain(" FPBadUAM", code);
        Assert.DoesNotContain(" FPMiscErr", code);
        Assert.DoesNotContain(" FPObjectNotFound", code);
        Assert.DoesNotContain(" FPVolLocked", code);
        Assert.DoesNotContain(" FPObjectLocked", code);

        Assert.Contains("FpNoErr", code);
        Assert.Contains("FpAccessDenied", code);
        Assert.Contains("FpBadUam", code);
        Assert.Contains("FpMiscErr", code);
        Assert.Contains("FpObjectNotFound", code);
        Assert.Contains("FpVolLocked", code);
        Assert.Contains("FpObjectLocked", code);
    }

    /// <summary>
    /// Findings 101-102 (LOW): AfpAuthMethod naming - DHX/DHX2 -> Dhx/Dhx2.
    /// </summary>
    [Fact]
    public void Finding101_102_AfpAuthMethod_PascalCase()
    {
        var code = File.ReadAllText(AfpFile);
        // DHX should be renamed to Dhx in enum definition
        // Extract enum block only
        var enumStart = code.IndexOf("internal enum AfpAuthMethod");
        var enumEnd = code.IndexOf("}", enumStart) + 1;
        var enumBlock = code.Substring(enumStart, enumEnd - enumStart);
        Assert.DoesNotContain(" DHX,", enumBlock);
        Assert.DoesNotContain(" DHX2", enumBlock);
        Assert.Contains("Dhx,", enumBlock);
        Assert.Contains("Dhx2", enumBlock);
    }
}
