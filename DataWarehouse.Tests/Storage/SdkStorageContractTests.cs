using DataWarehouse.SDK.Contracts;
using DataWarehouse.Kernel.Plugins;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Storage;

/// <summary>
/// Tests for SDK storage contracts (replaces DurableStateTests).
/// Validates storage provider interfaces, StoragePoolBase, and InMemoryStoragePlugin integration.
/// </summary>
[Trait("Category", "Unit")]
public class SdkStorageContractTests
{
    [Fact]
    public void IStorageProvider_ShouldExistInSdk()
    {
        var type = typeof(IStorageProvider);
        type.Should().NotBeNull();
        type.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void IStorageProvider_ShouldDefineCoreMethods()
    {
        var type = typeof(IStorageProvider);
        var methods = type.GetMethods();
        methods.Should().Contain(m => m.Name == "SaveAsync");
        methods.Should().Contain(m => m.Name == "LoadAsync");
        methods.Should().Contain(m => m.Name == "DeleteAsync");
        methods.Should().Contain(m => m.Name == "ExistsAsync");
    }

    [Fact]
    public void StoragePoolBase_ShouldBeAbstract()
    {
        var type = typeof(StoragePoolBase);
        type.IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void StoragePoolBase_ShouldSupportStrategies()
    {
        var type = typeof(StoragePoolBase);
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        methods.Should().Contain(m => m.Name == "SetStrategy");
        methods.Should().Contain(m => m.Name == "AddProvider");
        methods.Should().Contain(m => m.Name == "RemoveProvider");
    }

    [Fact]
    public void SimpleStrategy_ShouldHaveCorrectId()
    {
        var strategy = new SimpleStrategy();
        strategy.StrategyId.Should().Be("simple");
        strategy.Name.Should().Be("Simple");
    }

    [Fact]
    public void MirroredStrategy_ShouldHaveCorrectId()
    {
        var strategy = new MirroredStrategy();
        strategy.StrategyId.Should().Be("mirrored");
        strategy.Name.Should().Contain("Mirrored");
    }

    [Fact]
    public void StorageRole_ShouldContainExpectedValues()
    {
        Enum.GetValues<StorageRole>().Should().Contain(StorageRole.Primary);
        Enum.GetValues<StorageRole>().Should().Contain(StorageRole.Mirror);
        Enum.GetValues<StorageRole>().Should().Contain(StorageRole.Cache);
    }

    [Fact]
    public async Task InMemoryStoragePlugin_ShouldImplementSaveLoadDelete()
    {
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///contract-test.txt");
        var data = "contract test data"u8.ToArray();

        await plugin.SaveAsync(uri, new MemoryStream(data));
        (await plugin.ExistsAsync(uri)).Should().BeTrue();

        var loaded = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(loaded);
        (await reader.ReadToEndAsync()).Should().Be("contract test data");

        await plugin.DeleteAsync(uri);
        (await plugin.ExistsAsync(uri)).Should().BeFalse();
    }

    [Fact]
    public void InMemoryStoragePlugin_ShouldHaveCorrectMetadata()
    {
        var plugin = new InMemoryStoragePlugin();
        plugin.Id.Should().NotBeNullOrEmpty();
        plugin.Name.Should().Be("In-Memory Storage");
        plugin.Scheme.Should().Be("memory");
    }
}
