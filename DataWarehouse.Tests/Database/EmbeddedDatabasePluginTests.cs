using DataWarehouse.Plugins.EmbeddedDatabaseStorage;
using FluentAssertions;
using System.Text;
using Xunit;

namespace DataWarehouse.Tests.Database;

/// <summary>
/// Unit tests for EmbeddedDatabasePlugin covering SQLite, LiteDB, and RocksDB engines.
/// </summary>
public class EmbeddedDatabasePluginTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly List<IDisposable> _disposables = new();

    public EmbeddedDatabasePluginTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "EmbeddedDbTests_" + Guid.NewGuid().ToString("N"));
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

    #region SQLite Tests

    [Fact]
    public async Task SQLite_SaveAndLoad_RoundTripsData()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_sqlite.db");
        var config = EmbeddedDbConfig.SQLite(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///documents/doc1");
        var testData = """{"name": "Test Document", "value": 42}""";
        var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(testData));

        // Act
        await plugin.SaveAsync(uri, dataStream);
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("Test Document");
        result.Should().Contain("42");
    }

    [Fact]
    public async Task SQLite_Delete_RemovesRecord()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_sqlite_delete.db");
        var config = EmbeddedDbConfig.SQLite(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///documents/doc_to_delete");
        var testData = """{"name": "Delete Me"}""";
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
        var dbPath = Path.Combine(_testDirectory, "test_sqlite_exists.db");
        var config = EmbeddedDbConfig.SQLite(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///documents/existing_doc");
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
        var dbPath = Path.Combine(_testDirectory, "test_sqlite_not_exists.db");
        var config = EmbeddedDbConfig.SQLite(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///documents/nonexistent");

        // Act
        var exists = await plugin.ExistsAsync(uri);

        // Assert
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task SQLite_Update_OverwritesExistingRecord()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_sqlite_update.db");
        var config = EmbeddedDbConfig.SQLite(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///documents/update_doc");
        var originalData = """{"version": 1}""";
        var updatedData = """{"version": 2}""";

        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(originalData)));

        // Act
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(updatedData)));
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("\"version\": 2");
    }

    [Fact]
    public async Task SQLite_LoadNonExistent_ThrowsFileNotFoundException()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_sqlite_notfound.db");
        var config = EmbeddedDbConfig.SQLite(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///documents/does_not_exist");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => plugin.LoadAsync(uri));
    }

    #endregion

    #region LiteDB Tests

    [Fact]
    public async Task LiteDB_SaveAndLoad_RoundTripsData()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_litedb.db");
        var config = EmbeddedDbConfig.LiteDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///users/user1");
        var testData = """{"username": "testuser", "email": "test@example.com"}""";
        var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(testData));

        // Act
        await plugin.SaveAsync(uri, dataStream);
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("testuser");
        result.Should().Contain("test@example.com");
    }

    [Fact]
    public async Task LiteDB_Delete_RemovesDocument()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_litedb_delete.db");
        var config = EmbeddedDbConfig.LiteDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///users/user_to_delete");
        var testData = """{"name": "Delete Me"}""";
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(testData)));

        // Act
        await plugin.DeleteAsync(uri);

        // Assert
        var exists = await plugin.ExistsAsync(uri);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task LiteDB_Update_OverwritesExistingDocument()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_litedb_update.db");
        var config = EmbeddedDbConfig.LiteDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///config/settings");
        var originalData = """{"theme": "light"}""";
        var updatedData = """{"theme": "dark"}""";

        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(originalData)));

        // Act
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(updatedData)));
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("dark");
    }

    [Fact]
    public async Task LiteDB_LoadNonExistent_ThrowsFileNotFoundException()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_litedb_notfound.db");
        var config = EmbeddedDbConfig.LiteDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///users/nonexistent");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => plugin.LoadAsync(uri));
    }

    #endregion

    #region RocksDB Tests

    [Fact]
    public async Task RocksDB_SaveAndLoad_RoundTripsData()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_rocksdb");
        var config = EmbeddedDbConfig.RocksDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///cache/item1");
        var testData = """{"key": "value", "timestamp": 1234567890}""";
        var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(testData));

        // Act
        await plugin.SaveAsync(uri, dataStream);
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("value");
        result.Should().Contain("1234567890");
    }

    [Fact]
    public async Task RocksDB_Delete_RemovesKey()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_rocksdb_delete");
        var config = EmbeddedDbConfig.RocksDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///cache/delete_me");
        var testData = """{"temp": true}""";
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(testData)));

        // Act
        await plugin.DeleteAsync(uri);

        // Assert
        var exists = await plugin.ExistsAsync(uri);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task RocksDB_Update_OverwritesExistingKey()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_rocksdb_update");
        var config = EmbeddedDbConfig.RocksDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///cache/counter");
        var originalData = """{"count": 1}""";
        var updatedData = """{"count": 100}""";

        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(originalData)));

        // Act
        await plugin.SaveAsync(uri, new MemoryStream(Encoding.UTF8.GetBytes(updatedData)));
        using var resultStream = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(resultStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        result.Should().Contain("100");
    }

    [Fact]
    public async Task RocksDB_LoadNonExistent_ThrowsFileNotFoundException()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_rocksdb_notfound");
        var config = EmbeddedDbConfig.RocksDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri = new Uri("embedded:///cache/nonexistent");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => plugin.LoadAsync(uri));
    }

    [Fact]
    public async Task RocksDB_MultipleKeys_IndependentStorage()
    {
        // Arrange
        var dbPath = Path.Combine(_testDirectory, "test_rocksdb_multi");
        var config = EmbeddedDbConfig.RocksDB(dbPath);
        var plugin = new EmbeddedDatabasePlugin(config);
        _disposables.Add(plugin);

        var uri1 = new Uri("embedded:///data/key1");
        var uri2 = new Uri("embedded:///data/key2");
        var data1 = """{"id": 1}""";
        var data2 = """{"id": 2}""";

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
        result1.Should().Contain("\"id\": 1");
        result2.Should().Contain("\"id\": 2");
    }

    #endregion

    #region Engine Configuration Tests

    [Fact]
    public void EmbeddedDbConfig_SQLiteFactory_SetsCorrectEngine()
    {
        // Arrange & Act
        var config = EmbeddedDbConfig.SQLite("/path/to/db.sqlite");

        // Assert
        config.Engine.Should().Be(EmbeddedEngine.SQLite);
        config.FilePath.Should().Be("/path/to/db.sqlite");
    }

    [Fact]
    public void EmbeddedDbConfig_LiteDBFactory_SetsCorrectEngine()
    {
        // Arrange & Act
        var config = EmbeddedDbConfig.LiteDB("/path/to/db.litedb", "password123");

        // Assert
        config.Engine.Should().Be(EmbeddedEngine.LiteDB);
        config.FilePath.Should().Be("/path/to/db.litedb");
        config.Password.Should().Be("password123");
    }

    [Fact]
    public void EmbeddedDbConfig_RocksDBFactory_SetsCorrectEngine()
    {
        // Arrange & Act
        var config = EmbeddedDbConfig.RocksDB("/path/to/rocksdb");

        // Assert
        config.Engine.Should().Be(EmbeddedEngine.RocksDB);
        config.FilePath.Should().Be("/path/to/rocksdb");
    }

    #endregion
}
