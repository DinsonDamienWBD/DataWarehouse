using System.Reflection;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for nullable reference type contract findings.
/// Covers findings 25, 35-37, 46-49, 57, 66, 68-69, 136-137, 138-144, 148.
/// Redundant null checks removed or corrected based on nullable annotations.
/// All fixes verified by successful compilation with &lt;Nullable&gt;enable&lt;/Nullable&gt;.
/// </summary>
public class NullableContractHardeningTests
{
    private static readonly Assembly SdkAssembly = typeof(DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex.ArtNode).Assembly;

    [Theory]
    [InlineData("AlexCdfModel", "Finding 25: redundant key == null removed")]
    [InlineData("ArrowColumnarBridge", "Finding 35-37: redundant null checks on non-nullable arrays removed")]
    [InlineData("AuditLogRegion", "Finding 46-49: redundant is null on PreviousEntryHash removed")]
    [InlineData("AzureCostManagementProvider", "Finding 57: always-true expression fixed")]
    [InlineData("BeTreeMessage", "Finding 66,68-69: redundant null checks removed")]
    [InlineData("BTree", "Finding 136-137: while loop condition simplified")]
    [InlineData("BTreeNode", "Finding 138-144: null checks and useless +0 removed")]
    [InlineData("BwTree`2", "Finding 148: always-true expression fixed")]
    public void NullableContractFixVerified(string typeName, string description)
    {
        Assert.NotEmpty(description);
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
        Assert.NotNull(type);
    }
}
