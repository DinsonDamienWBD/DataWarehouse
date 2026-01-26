using DataWarehouse.Plugins.RelationalDatabaseStorage;
using FluentAssertions;
using System.Text;
using Xunit;

namespace DataWarehouse.Tests.Database;

/// <summary>
/// Unit tests for RelationalDatabasePlugin using SQLite as the test database.
/// SQLite is used because it doesn't require a server and supports the same ADO.NET patterns.
/// </summary>
public class RelationalDatabasePluginTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly List<IDisposable> _disposables = new();

    public RelationalDatabasePluginTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "RelationalDbTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); } catch { }
        }

        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch { }
    }

    #region SQLite Engine Tests (using RelationalDatabasePlugin)

    [Fact]
    public async Task SQLite_SaveAndLoad_RoundTripsData()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("relational:///testdb/records/rec1");
        var testData = """{"name": "Test Record", "amount": 99.99}""";
        var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(testData));

        // Act
        await plugin.SaveAsync(uri, dataStream);
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("Test Record");
        result.Should().Contain("99.99");
    }

    [Fact]
    public async Task SQLite_Delete_RemovesRecord()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational_delete.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("relational:///testdb/records/delete_me");
        var testData = """{"status": "pending_delete"}""";
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(testData)));

        // Act
        await plugin.DeleteAsync(uri);

        // Assert
        var exists = await plugin.ExistsAsync(uri);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task SQLite_Exists_ReturnsTrueForExistingRecord()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational_exists.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("relational:///testdb/items/existing");
        var testData = """{"exists": true}""";
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(testData)));

        // Act
        var exists = await plugin.ExistsAsync(uri);

        // Assert
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task SQLite_Exists_ReturnsFalseForNonExistingRecord()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational_notexists.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("relational:///testdb/items/nonexistent");

        // Act
        var exists = await plugin.ExistsAsync(uri);

        // Assert
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task SQLite_Update_OverwritesExistingRecord()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational_update.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("relational:///testdb/config/app_settings");
        var originalData = """{"version": "1.0.0"}""";
        var updatedData = """{"version": "2.0.0"}""";

        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(originalData)));

        // Act
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(updatedData)));
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("2.0.0");
    }

    [Fact]
    public async Task SQLite_LoadNonExistent_ThrowsFileNotFoundException()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational_notfound.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("relational:///testdb/records/does_not_exist");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => plugin.LoadAsync(uri));
    }

    [Fact]
    public async Task SQLite_MultipleRecords_IndependentStorage()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational_multi.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri1 = new Uri("relational:///testdb/users/user1");
        var uri2 = new Uri("relational:///testdb/users/user2");
        var data1 = """{"name": "Alice"}""";
        var data2 = """{"name": "Bob"}""";

        // Act
        await plugin.SaveAsync(uri1, new MemoryStream(Encoding.UTF8.GetBytes(data1)));
        await plugin.SaveAsync(uri2, new MemoryStream(Encoding.UTF8.GetBytes(data2)));

        using var stream1 = await plugin.LoadAsync(uri1);
        using var stream2 = await plugin.LoadAsync(uri2);
        using var reader1 = new StreamReader(stream1);
        using var reader2 = new StreamReader(stream2);
        var result1 = await reader1.ReadToEndAsync();
        var result2 = await reader2.ReadToEndAsync();

        // Assert
        result1.Should().Contain("Alice");
        result2.Should().Contain("Bob");
    }

    [Fact]
    public async Task SQLite_MultipleTables_IndependentStorage()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_relational_tables.db");
        var config = new RelationalDbConfig
        {
            Engine = RelationalEngine.SQLite,
            ConnectionString = $"Data Source={dbPath}"
        };
        var plugin = new RelationalDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri1 = new Uri("relational:///testdb/orders/order1");
        var uri2 = new Uri("relational:///testdb/products/prod1");
        var orderData = """{"orderId": "ORD-001", "total": 150.00}""";
        var productData = """{"productId": "PROD-001", "price": 29.99}""";

        // Act
        await plugin.SaveAsync(uri1, new MemoryStream(Encoding.UTF8.GetBytes(orderData)));
        await plugin.SaveAsync(uri2, new MemoryStream(Encoding.UTF8.GetBytes(productData)));

        using var stream1 = await plugin.LoadAsync(uri1);
        using var stream2 = await plugin.LoadAsync(uri2);
        using var reader1 = new StreamReader(stream1);
        using var reader2 = new StreamReader(stream2);
        var result1 = await reader1.ReadToEndAsync();
        var result2 = await reader2.ReadToEndAsync();

        // Assert
        result1.Should().Contain("ORD-001");
        result2.Should().Contain("PROD-001");
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void RelationalDbConfig_DefaultEngine_IsMySQL()
    {
        // Arrange & Act
        var config = new RelationalDbConfig();

        // Assert
        config.Engine.Should().Be(RelationalEngine.MySQL);
    }

    [Fact]
    public void RelationalDbConfig_ForSQLite_SetsCorrectEngine()
    {
        // Arrange & Act
        var config = RelationalDbConfig.ForSQLite("Data Source=test.db");

        // Assert
        config.Engine.Should().Be(RelationalEngine.SQLite);
        config.ConnectionString.Should().Be("Data Source=test.db");
    }

    [Fact]
    public void RelationalDbConfig_ForPostgreSQL_SetsCorrectEngine()
    {
        // Arrange & Act
        var config = RelationalDbConfig.ForPostgreSQL("Host=localhost;Database=test");

        // Assert
        config.Engine.Should().Be(RelationalEngine.PostgreSQL);
        config.ConnectionString.Should().Be("Host=localhost;Database=test");
    }

    [Fact]
    public void RelationalDbConfig_ForMySQL_SetsCorrectEngine()
    {
        // Arrange & Act
        var config = RelationalDbConfig.ForMySQL("Server=localhost;Database=test");

        // Assert
        config.Engine.Should().Be(RelationalEngine.MySQL);
        config.ConnectionString.Should().Be("Server=localhost;Database=test");
    }

    [Fact]
    public void RelationalDbConfig_ForSQLServer_SetsCorrectEngine()
    {
        // Arrange & Act
        var config = RelationalDbConfig.ForSQLServer("Server=localhost;Database=test");

        // Assert
        config.Engine.Should().Be(RelationalEngine.SQLServer);
        config.ConnectionString.Should().Be("Server=localhost;Database=test");
    }

    #endregion

    #region Engine Enum Tests

    [Theory]
    [InlineData(RelationalEngine.MySQL)]
    [InlineData(RelationalEngine.PostgreSQL)]
    [InlineData(RelationalEngine.SQLServer)]
    [InlineData(RelationalEngine.SQLite)]
    [InlineData(RelationalEngine.MariaDB)]
    [InlineData(RelationalEngine.CockroachDB)]
    public void RelationalEngine_AllSupportedEngines_AreDefined(RelationalEngine engine)
    {
        // Assert
        Enum.IsDefined(typeof(RelationalEngine), engine).Should().BeTrue();
    }

    [Fact]
    public void RelationalEngine_DoesNotContainInMemory()
    {
        // Assert - InMemory was removed
        var engineNames = Enum.GetNames(typeof(RelationalEngine));
        engineNames.Should().NotContain("InMemory");
    }

    #endregion
}
