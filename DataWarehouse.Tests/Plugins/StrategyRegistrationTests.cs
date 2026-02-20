using DataWarehouse.Plugins.UltimateStorage;
using DataWarehouse.Plugins.UltimateEncryption;
using DataWarehouse.Plugins.UltimateConnector;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Strategy registration tests for the top large plugins.
/// Verifies strategy counts, uniqueness, and basic integrity.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Scope", "StrategyRegistration")]
public class StrategyRegistrationTests
{
    // ==================== UltimateStorage ====================

    [Fact]
    public void UltimateStorage_HasExpectedStrategyCount()
    {
        using var plugin = new UltimateStoragePlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        strategies.Should().NotBeNull();
        strategies.Count.Should().BeGreaterThanOrEqualTo(50,
            "UltimateStorage should have 50+ storage backend strategies");
    }

    [Fact]
    public void UltimateStorage_HasNoDuplicateStrategyIds()
    {
        using var plugin = new UltimateStoragePlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        var ids = strategies.Select(s => s.StrategyId).ToList();
        ids.Should().OnlyHaveUniqueItems("Strategy IDs within UltimateStorage must be unique");
    }

    [Fact]
    public void UltimateStorage_AllStrategiesHaveNonEmptyNames()
    {
        using var plugin = new UltimateStoragePlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        foreach (var strategy in strategies)
        {
            strategy.Name.Should().NotBeNullOrWhiteSpace(
                $"Strategy '{strategy.StrategyId}' in UltimateStorage must have a non-empty name");
        }
    }

    [Fact]
    public void UltimateStorage_AllStrategiesHaveNonEmptyIds()
    {
        using var plugin = new UltimateStoragePlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        foreach (var strategy in strategies)
        {
            strategy.StrategyId.Should().NotBeNullOrWhiteSpace(
                "All storage strategies must have a non-empty StrategyId");
        }
    }

    [Fact]
    public void UltimateStorage_StrategyIdsHaveNoSpaces()
    {
        using var plugin = new UltimateStoragePlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        foreach (var strategy in strategies)
        {
            strategy.StrategyId.Should().NotContain(" ",
                $"Strategy ID '{strategy.StrategyId}' in UltimateStorage should not contain spaces");
        }
    }

    // ==================== UltimateEncryption ====================

    [Fact]
    public void UltimateEncryption_HasExpectedStrategyCount()
    {
        using var plugin = new UltimateEncryptionPlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        strategies.Should().NotBeNull();
        strategies.Count.Should().BeGreaterThanOrEqualTo(30,
            "UltimateEncryption should have 30+ encryption strategies");
    }

    [Fact]
    public void UltimateEncryption_HasNoDuplicateStrategyIds()
    {
        using var plugin = new UltimateEncryptionPlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        var ids = strategies.Select(s => s.StrategyId).ToList();
        ids.Should().OnlyHaveUniqueItems("Strategy IDs within UltimateEncryption must be unique");
    }

    [Fact]
    public void UltimateEncryption_AllStrategiesHaveNonEmptyNames()
    {
        using var plugin = new UltimateEncryptionPlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        foreach (var strategy in strategies)
        {
            strategy.StrategyName.Should().NotBeNullOrWhiteSpace(
                $"Strategy '{strategy.StrategyId}' in UltimateEncryption must have a non-empty name");
        }
    }

    [Fact]
    public void UltimateEncryption_StrategyIdsHaveNoSpaces()
    {
        using var plugin = new UltimateEncryptionPlugin();
        var strategies = plugin.Registry.GetAllStrategies();

        foreach (var strategy in strategies)
        {
            strategy.StrategyId.Should().NotContain(" ",
                $"Strategy ID '{strategy.StrategyId}' in UltimateEncryption should not contain spaces");
        }
    }

    // ==================== UltimateConnector ====================

    [Fact]
    public void UltimateConnector_RegistryIsAccessible()
    {
        var plugin = new UltimateConnectorPlugin();
        var strategies = plugin.Registry.GetAll();

        // Connector strategies are registered lazily during initialization,
        // so the count may be 0 before initialization. We verify the registry is functional.
        strategies.Should().NotBeNull("Registry.GetAll() should return a non-null collection");
    }

    [Fact]
    public void UltimateConnector_HasNoDuplicateStrategyIds()
    {
        var plugin = new UltimateConnectorPlugin();
        var strategies = plugin.Registry.GetAll().ToList();

        var ids = strategies.Select(s => s.StrategyId).ToList();
        ids.Should().OnlyHaveUniqueItems("Strategy IDs within UltimateConnector must be unique");
    }

    [Fact]
    public void UltimateConnector_AllStrategiesHaveNonEmptyNames()
    {
        var plugin = new UltimateConnectorPlugin();
        var strategies = plugin.Registry.GetAll().ToList();

        foreach (var strategy in strategies)
        {
            strategy.DisplayName.Should().NotBeNullOrWhiteSpace(
                $"Strategy '{strategy.StrategyId}' in UltimateConnector must have a non-empty DisplayName");
        }
    }

    [Fact]
    public void UltimateConnector_StrategyIdsHaveNoSpaces()
    {
        var plugin = new UltimateConnectorPlugin();
        var strategies = plugin.Registry.GetAll().ToList();

        foreach (var strategy in strategies)
        {
            strategy.StrategyId.Should().NotContain(" ",
                $"Strategy ID '{strategy.StrategyId}' in UltimateConnector should not contain spaces");
        }
    }

    // ==================== Cross-Plugin Consistency ====================

    [Fact]
    public void AllTestedPlugins_StrategiesHaveReasonableIdLength()
    {
        using var storage = new UltimateStoragePlugin();
        using var encryption = new UltimateEncryptionPlugin();

        var allIds = new List<(string PluginName, string StrategyId)>();

        foreach (var s in storage.Registry.GetAllStrategies())
            allIds.Add(("Storage", s.StrategyId));
        foreach (var s in encryption.Registry.GetAllStrategies())
            allIds.Add(("Encryption", s.StrategyId));

        foreach (var (pluginName, strategyId) in allIds)
        {
            strategyId.Length.Should().BeLessThanOrEqualTo(100,
                $"Strategy ID '{strategyId}' in {pluginName} should not be excessively long");
            strategyId.Length.Should().BeGreaterThanOrEqualTo(2,
                $"Strategy ID '{strategyId}' in {pluginName} should not be too short");
        }
    }
}
