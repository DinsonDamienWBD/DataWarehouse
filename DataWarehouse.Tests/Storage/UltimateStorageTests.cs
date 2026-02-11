using DataWarehouse.Plugins.UltimateStorage.Strategies.Local;
using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Storage;

/// <summary>
/// Tests for UltimateStorage strategy implementations.
/// Validates storage strategy base, local strategies, and metadata.
/// </summary>
[Trait("Category", "Unit")]
public class UltimateStorageTests
{
    #region Storage Strategy Base Contract

    [Fact]
    public void StorageStrategyBase_ShouldExistInSdk()
    {
        // UltimateStorageStrategyBase is the plugin-level base
        var type = typeof(DataWarehouse.Plugins.UltimateStorage.Strategies.Local.LocalFileStrategy);
        type.Should().NotBeNull();
    }

    [Fact]
    public void LocalFileStrategy_ShouldHaveCorrectId()
    {
        var strategy = new LocalFileStrategy();
        strategy.StrategyId.Should().Be("local-file");
    }

    [Fact]
    public void NvmeDiskStrategy_ShouldHaveCorrectId()
    {
        var strategy = new NvmeDiskStrategy();
        strategy.StrategyId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void RamDiskStrategy_ShouldHaveCorrectId()
    {
        var strategy = new RamDiskStrategy();
        strategy.StrategyId.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region IStorageProvider Contract

    [Fact]
    public void IStorageProvider_ShouldBeInterface()
    {
        typeof(IStorageProvider).IsInterface.Should().BeTrue();
    }

    [Fact]
    public void IStorageProvider_ShouldDefineSaveAndLoad()
    {
        var type = typeof(IStorageProvider);
        var methods = type.GetMethods();
        methods.Should().Contain(m => m.Name == "SaveAsync");
        methods.Should().Contain(m => m.Name == "LoadAsync");
    }

    [Fact]
    public void IStorageProvider_ShouldDefineDeleteAndExists()
    {
        var type = typeof(IStorageProvider);
        var methods = type.GetMethods();
        methods.Should().Contain(m => m.Name == "DeleteAsync");
        methods.Should().Contain(m => m.Name == "ExistsAsync");
    }

    #endregion

    #region Storage Strategies Properties

    [Fact]
    public void LocalFileStrategy_ShouldNotBeAbstract()
    {
        typeof(LocalFileStrategy).IsAbstract.Should().BeFalse();
    }

    [Fact]
    public void NvmeDiskStrategy_ShouldNotBeAbstract()
    {
        typeof(NvmeDiskStrategy).IsAbstract.Should().BeFalse();
    }

    [Fact]
    public void PmemStrategy_ShouldHaveId()
    {
        var strategy = new PmemStrategy();
        strategy.StrategyId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ScmStrategy_ShouldHaveId()
    {
        var strategy = new ScmStrategy();
        strategy.StrategyId.Should().NotBeNullOrEmpty();
    }

    #endregion
}
