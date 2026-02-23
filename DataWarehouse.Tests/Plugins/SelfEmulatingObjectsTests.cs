using DataWarehouse.Plugins.UltimateCompute;
using DataWarehouse.SDK.Contracts.Compute;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for SelfEmulatingComputeStrategy (consolidated from SelfEmulatingObjects plugin).
/// Validates strategy identity, capabilities, and runtime type.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateCompute")]
public class SelfEmulatingObjectsTests
{
    [Fact]
    public void SelfEmulatingComputeStrategy_IsDiscoveredByRegistry()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();

        // Act
        var count = registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Assert
        Assert.True(count > 0);
        var strategy = registry.GetStrategy("compute.self-emulating");
        Assert.NotNull(strategy);
    }

    [Fact]
    public void SelfEmulatingComputeStrategy_HasCorrectIdentity()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Act
        var strategy = registry.GetStrategy("compute.self-emulating");

        // Assert
        Assert.NotNull(strategy);
        Assert.Equal(ComputeRuntime.WASM, strategy!.Runtime);
    }

    [Fact]
    public void SelfEmulatingComputeStrategy_HasSandboxingCapability()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Act
        var strategy = registry.GetStrategy("compute.self-emulating");

        // Assert
        Assert.NotNull(strategy);
        Assert.True(strategy!.Capabilities.SupportsSandboxing);
        Assert.True(strategy.Capabilities.SupportsCheckpointing);
    }

    [Fact]
    public void SelfEmulatingComputeStrategy_RuntimeIsWasm()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Act
        var strategies = registry.GetStrategiesByRuntime(ComputeRuntime.WASM);

        // Assert
        Assert.Contains(strategies, s => s is ComputeRuntimeStrategyBase b && b.StrategyId == "compute.self-emulating");
    }
}
