using DataWarehouse.Kernel;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Kernel;

/// <summary>
/// Tests for Kernel contracts and initialization (replaces DataWarehouseKernelTests).
/// Validates kernel type structure and public API via reflection.
/// </summary>
[Trait("Category", "Unit")]
public class KernelContractTests
{
    [Fact]
    public void DataWarehouseKernel_ShouldExist()
    {
        var type = typeof(DataWarehouseKernel);
        type.Should().NotBeNull();
    }

    [Fact]
    public void DataWarehouseKernel_ShouldHaveConstructors()
    {
        var constructors = typeof(DataWarehouseKernel).GetConstructors(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        constructors.Should().NotBeEmpty();
    }

    [Fact]
    public void IPlugin_ShouldDefineIdAndName()
    {
        var type = typeof(IPlugin);
        type.GetProperty("Id").Should().NotBeNull();
        type.GetProperty("Name").Should().NotBeNull();
    }

    [Fact]
    public void PluginBase_ShouldBeAbstract()
    {
        var type = typeof(PluginBase);
        type.IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void FeaturePluginBase_ShouldExtendPluginBase()
    {
        var type = typeof(FeaturePluginBase);
        type.IsAbstract.Should().BeTrue();
        type.IsSubclassOf(typeof(PluginBase)).Should().BeTrue();
    }

    [Fact]
    public void StorageProviderPluginBase_ShouldImplementIStorageProvider()
    {
        var type = typeof(StorageProviderPluginBase);
        type.GetInterfaces().Should().Contain(typeof(IStorageProvider));
    }

    [Fact]
    public void PluginCategory_ShouldHaveMultipleValues()
    {
        var values = Enum.GetValues<PluginCategory>();
        values.Length.Should().BeGreaterThan(5, "should have multiple plugin categories");
    }
}
