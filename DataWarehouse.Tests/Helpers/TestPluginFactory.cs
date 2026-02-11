using DataWarehouse.Kernel.Plugins;

namespace DataWarehouse.Tests.Helpers;

/// <summary>
/// Static factory methods for creating plugin instances with default test configuration.
/// Centralizes common test setup patterns to reduce duplication across test classes.
/// </summary>
public static class TestPluginFactory
{
    /// <summary>
    /// Create a TestMessageBus with no pre-configured responses.
    /// </summary>
    public static TestMessageBus CreateTestMessageBus()
    {
        return new TestMessageBus();
    }

    /// <summary>
    /// Create an InMemoryTestStorage instance.
    /// </summary>
    public static InMemoryTestStorage CreateTestStorage()
    {
        return new InMemoryTestStorage();
    }

    /// <summary>
    /// Create a CancellationToken appropriate for test usage.
    /// Uses TestContext.Current.CancellationToken if available, otherwise CancellationToken.None.
    /// </summary>
    public static CancellationToken CreateCancellationToken()
    {
        try
        {
            return Xunit.TestContext.Current.CancellationToken;
        }
        catch
        {
            return CancellationToken.None;
        }
    }

    /// <summary>
    /// Create an InMemoryStoragePlugin with default unlimited configuration.
    /// </summary>
    public static InMemoryStoragePlugin CreateInMemoryStorage()
    {
        return new InMemoryStoragePlugin();
    }

    /// <summary>
    /// Create an InMemoryStoragePlugin with specified memory limit.
    /// </summary>
    public static InMemoryStoragePlugin CreateInMemoryStorage(long maxMemoryBytes)
    {
        return new InMemoryStoragePlugin(new InMemoryStorageConfig
        {
            MaxMemoryBytes = maxMemoryBytes
        });
    }

    /// <summary>
    /// Create an InMemoryStoragePlugin with specified item count limit.
    /// </summary>
    public static InMemoryStoragePlugin CreateInMemoryStorage(int maxItemCount)
    {
        return new InMemoryStoragePlugin(new InMemoryStorageConfig
        {
            MaxItemCount = maxItemCount
        });
    }

    /// <summary>
    /// Generate random test data of specified size.
    /// </summary>
    public static byte[] GenerateTestData(int sizeBytes)
    {
        var data = new byte[sizeBytes];
        Random.Shared.NextBytes(data);
        return data;
    }

    /// <summary>
    /// Create a MemoryStream from test data.
    /// </summary>
    public static MemoryStream CreateTestStream(int sizeBytes)
    {
        return new MemoryStream(GenerateTestData(sizeBytes));
    }

    /// <summary>
    /// Create a MemoryStream from a string.
    /// </summary>
    public static MemoryStream CreateTestStream(string content)
    {
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
    }
}
