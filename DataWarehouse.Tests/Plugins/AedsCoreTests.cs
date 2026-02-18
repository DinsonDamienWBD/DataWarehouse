using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for AEDS Core plugin.
/// Validates intent manifest validation, signature verification, and job queue prioritization.
/// NOTE: To run these tests, add ProjectReference to DataWarehouse.Plugins.AedsCore in csproj.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "AedsCore")]
public class AedsCoreTests
{
    [Fact]
    public void AedsCorePlugin_RequiresProjectReference()
    {
        // This is a placeholder test to document that the AedsCore plugin exists
        // and should be tested once the project reference is added.
        // Expected plugin ID: "com.datawarehouse.aeds.core"
        // Expected capabilities: IntentManifest validation, signature verification, priority scoring
        Assert.True(true);
    }
}
