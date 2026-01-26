using FluentAssertions;
using System.Reflection;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure;

/// <summary>
/// Tests to verify code cleanup tasks have been completed correctly.
/// These tests validate that obsolete code has been removed and
/// architectural fixes have been properly applied.
/// </summary>
public class CodeCleanupVerificationTests
{
    #region Task 1: DatabaseInfrastructure.cs Deletion Verification

    [Fact]
    public void DatabaseInfrastructure_FileDoesNotExist()
    {
        // Arrange
        var solutionRoot = FindSolutionRoot();
        var obsoleteFilePath = Path.Combine(solutionRoot, "DataWarehouse.SDK", "Database", "DatabaseInfrastructure.cs");

        // Assert
        File.Exists(obsoleteFilePath).Should().BeFalse(
            "DatabaseInfrastructure.cs should have been deleted as it was obsolete and replaced by " +
            "StorageConnectionRegistry and HybridDatabasePluginBase");
    }

    [Fact]
    public void ConnectionRegistry_TypeDoesNotExist_InLegacyNamespace()
    {
        // Arrange - Look for the old ConnectionRegistry class that should have been removed
        var sdkAssembly = typeof(DataWarehouse.SDK.Contracts.IPlugin).Assembly;

        // Act
        var legacyType = sdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "ConnectionRegistry" &&
                                  t.Namespace?.Contains("Database") == true &&
                                  !t.Name.Contains("Storage"));

        // Assert
        legacyType.Should().BeNull(
            "The legacy ConnectionRegistry class should have been removed. " +
            "Use StorageConnectionRegistry<TConfig> instead.");
    }

    [Fact]
    public void StorageConnectionRegistry_Exists_AsReplacement()
    {
        // Arrange
        var sdkAssembly = typeof(DataWarehouse.SDK.Contracts.IPlugin).Assembly;

        // Act
        var replacementType = sdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name.Contains("StorageConnectionRegistry"));

        // Assert
        replacementType.Should().NotBeNull(
            "StorageConnectionRegistry<TConfig> should exist as the replacement for the old ConnectionRegistry");
    }

    #endregion

    #region Task 4: VFS Base Class Fix Verification

    [Fact]
    public void CacheableStoragePluginBase_LoadAsync_IsAbstract()
    {
        // Arrange
        var sdkAssembly = typeof(DataWarehouse.SDK.Contracts.IPlugin).Assembly;
        var cacheableStorageType = sdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "CacheableStoragePluginBase");

        cacheableStorageType.Should().NotBeNull("CacheableStoragePluginBase should exist");

        // Act
        var loadAsyncMethod = cacheableStorageType!.GetMethod("LoadAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(Uri) },
            null);

        // Assert
        loadAsyncMethod.Should().NotBeNull("LoadAsync method should exist on CacheableStoragePluginBase");
        loadAsyncMethod!.IsAbstract.Should().BeTrue(
            "LoadAsync should be abstract to enforce compile-time implementation by derived classes");
    }

    [Fact]
    public void CacheableStoragePluginBase_LoadAsync_DoesNotThrowNotImplementedException()
    {
        // This test verifies that the old NotImplementedException pattern was removed.
        // The fix changed the method to abstract, which means there's no method body
        // that could throw NotImplementedException.

        // Arrange
        var sdkAssembly = typeof(DataWarehouse.SDK.Contracts.IPlugin).Assembly;
        var cacheableStorageType = sdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "CacheableStoragePluginBase");

        cacheableStorageType.Should().NotBeNull();

        // Act
        var loadAsyncMethod = cacheableStorageType!.GetMethod("LoadAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(Uri) },
            null);

        // Assert
        loadAsyncMethod.Should().NotBeNull();

        // An abstract method has no body, so it cannot throw NotImplementedException
        loadAsyncMethod!.IsAbstract.Should().BeTrue(
            "LoadAsync should be abstract (no method body) rather than throwing NotImplementedException");

        // Additional verification: abstract methods have no method body
        var methodBody = loadAsyncMethod.GetMethodBody();
        methodBody.Should().BeNull(
            "Abstract methods should have no method body - the old fire-and-forget TouchAsync bug is fixed");
    }

    [Fact]
    public void StorageProviderPluginBase_LoadAsync_IsAbstract()
    {
        // Arrange
        var sdkAssembly = typeof(DataWarehouse.SDK.Contracts.IPlugin).Assembly;
        var storageProviderType = sdkAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "StorageProviderPluginBase");

        storageProviderType.Should().NotBeNull("StorageProviderPluginBase should exist");

        // Act
        var loadAsyncMethod = storageProviderType!.GetMethod("LoadAsync",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null,
            new[] { typeof(Uri) },
            null);

        // Assert
        loadAsyncMethod.Should().NotBeNull("LoadAsync method should be declared on StorageProviderPluginBase");
        loadAsyncMethod!.IsAbstract.Should().BeTrue(
            "LoadAsync in StorageProviderPluginBase should be abstract");
    }

    #endregion

    #region InMemory Engine Removal Verification

    [Fact]
    public void EmbeddedEngine_DoesNotContainInMemory()
    {
        // Arrange
        var embeddedDbAssembly = typeof(DataWarehouse.Plugins.EmbeddedDatabaseStorage.EmbeddedDatabasePlugin).Assembly;
        var engineEnumType = embeddedDbAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "EmbeddedEngine" && t.IsEnum);

        engineEnumType.Should().NotBeNull("EmbeddedEngine enum should exist");

        // Act
        var enumValues = Enum.GetNames(engineEnumType!);

        // Assert
        enumValues.Should().NotContain("InMemory",
            "InMemory engine should have been removed from EmbeddedEngine enum");
    }

    [Fact]
    public void RelationalEngine_DoesNotContainInMemory()
    {
        // Arrange
        var relationalDbAssembly = typeof(DataWarehouse.Plugins.RelationalDatabaseStorage.RelationalDatabasePlugin).Assembly;
        var engineEnumType = relationalDbAssembly.GetTypes()
            .FirstOrDefault(t => t.Name == "RelationalEngine" && t.IsEnum);

        engineEnumType.Should().NotBeNull("RelationalEngine enum should exist");

        // Act
        var enumValues = Enum.GetNames(engineEnumType!);

        // Assert
        enumValues.Should().NotContain("InMemory",
            "InMemory engine should have been removed from RelationalEngine enum");
    }

    #endregion

    #region Helper Methods

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

        while (directory != null)
        {
            if (directory.GetFiles("*.slnx").Any() || directory.GetFiles("*.sln").Any())
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        // Fallback: try to find from test output path
        var testDir = Path.GetDirectoryName(typeof(CodeCleanupVerificationTests).Assembly.Location);
        while (!string.IsNullOrEmpty(testDir))
        {
            if (Directory.Exists(Path.Combine(testDir, "DataWarehouse.SDK")))
            {
                return testDir;
            }
            testDir = Path.GetDirectoryName(testDir);
        }

        throw new InvalidOperationException("Could not find solution root directory");
    }

    #endregion
}
