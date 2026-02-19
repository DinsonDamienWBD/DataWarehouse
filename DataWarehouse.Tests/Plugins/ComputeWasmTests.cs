using DataWarehouse.Plugins.Compute.Wasm;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for WASM Compute plugin.
/// Validates plugin identity, supported features, and WASM module structure.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "ComputeWasm")]
public class ComputeWasmTests
{
    [Fact]
    public void WasmComputePlugin_CanBeConstructed()
    {
        // Act
        var plugin = new WasmComputePlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void WasmComputePlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new WasmComputePlugin();

        // Assert
        Assert.Equal("com.datawarehouse.compute.wasm", plugin.Id);
        Assert.Equal("WASM Compute-on-Storage", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void WasmComputePlugin_CategoryIsDataTransformationProvider()
    {
        // Arrange
        var plugin = new WasmComputePlugin();

        // Assert
        Assert.Equal(PluginCategory.DataTransformationProvider, plugin.Category);
    }

    [Fact]
    public void WasmComputePlugin_SupportedFeatures_ContainsExpectedCapabilities()
    {
        // Arrange
        var plugin = new WasmComputePlugin();

        // Assert
        Assert.NotNull(plugin.SupportedFeatures);
        Assert.Contains("bulk-memory", plugin.SupportedFeatures);
        Assert.Contains("simd", plugin.SupportedFeatures);
        Assert.Contains("multi-value", plugin.SupportedFeatures);
    }

    [Fact]
    public void WasmComputePlugin_MaxConcurrentExecutions_IsPositive()
    {
        // Arrange
        var plugin = new WasmComputePlugin();

        // Assert
        Assert.True(plugin.MaxConcurrentExecutions > 0);
        Assert.Equal(100, plugin.MaxConcurrentExecutions);
    }

    [Fact]
    public void WasmModule_CanBeCreated_WithDefaults()
    {
        // Act
        var module = new WasmModule();

        // Assert
        Assert.NotNull(module);
        Assert.Equal(string.Empty, module.FunctionId);
        Assert.Empty(module.RawBytes);
        Assert.Empty(module.Types);
        Assert.Empty(module.Imports);
        Assert.Empty(module.Exports);
        Assert.Empty(module.Code);
    }
}
