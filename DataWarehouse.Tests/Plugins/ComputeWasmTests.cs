using DataWarehouse.Plugins.UltimateCompute;
using DataWarehouse.SDK.Contracts.Compute;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for WasmInterpreterStrategy (consolidated from Compute.Wasm plugin).
/// Validates strategy identity, capabilities, and runtime type.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateCompute")]
public class ComputeWasmTests
{
    [Fact]
    public void WasmInterpreterStrategy_IsDiscoveredByRegistry()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();

        // Act
        var count = registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Assert
        Assert.True(count > 0);
        var strategy = registry.GetStrategy("compute.wasm.interpreter");
        Assert.NotNull(strategy);
    }

    [Fact]
    public void WasmInterpreterStrategy_HasCorrectIdentity()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Act
        var strategy = registry.GetStrategy("compute.wasm.interpreter");

        // Assert
        Assert.NotNull(strategy);
        Assert.Equal(ComputeRuntime.WASM, strategy!.Runtime);
    }

    [Fact]
    public void WasmInterpreterStrategy_HasSandboxingCapability()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Act
        var strategy = registry.GetStrategy("compute.wasm.interpreter");

        // Assert
        Assert.NotNull(strategy);
        Assert.True(strategy!.Capabilities.SupportsSandboxing);
    }

    [Fact]
    public void WasmInterpreterStrategy_RuntimeIsWasm()
    {
        // Arrange
        var registry = new ComputeRuntimeStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateComputePlugin).Assembly);

        // Act
        var strategies = registry.GetStrategiesByRuntime(ComputeRuntime.WASM);

        // Assert - should include interpreter among WASM strategies
        Assert.Contains(strategies, s => s is ComputeRuntimeStrategyBase b && b.StrategyId == "compute.wasm.interpreter");
    }
}
