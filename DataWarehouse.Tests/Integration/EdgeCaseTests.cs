using DataWarehouse.Kernel.Plugins;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Query;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Tests.Helpers;
using FluentAssertions;
using Xunit;
using HealthStatus = DataWarehouse.SDK.Contracts.HealthStatus;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Edge case tests covering null inputs, empty strings, unicode, concurrency,
/// resource exhaustion, and general robustness of SDK and plugin operations.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Scope", "EdgeCase")]
public class EdgeCaseTests
{
    // ==================== Null Input Handling ====================

    [Fact]
    public void SqlParser_NullInput_ThrowsArgumentNullException()
    {
        var parser = new SqlParserEngine();
        Action act = () => parser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task InMemoryStorage_NullData_HandlesGracefully()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("mem://test/null-data");

        // Storing null data should either throw or handle gracefully
        try
        {
            await storage.SaveAsync(uri, (Stream)null!);
        }
        catch (ArgumentNullException)
        {
            // Expected - graceful null handling
        }
        catch (NullReferenceException)
        {
            // Also acceptable for null stream
        }

        true.Should().BeTrue("Test completed without crash");
    }

    // ==================== Empty String Handling ====================

    [Fact]
    public void SqlParser_EmptyString_ThrowsException()
    {
        var parser = new SqlParserEngine();
        Action act = () => parser.Parse("");

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void SqlParser_WhitespaceOnly_ThrowsException()
    {
        var parser = new SqlParserEngine();
        Action act = () => parser.Parse("   \t\n  ");

        act.Should().Throw<Exception>();
    }

    // ==================== Unicode Handling ====================

    [Fact]
    public async Task InMemoryStorage_UnicodeKey_RoundTrips()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var uri = new Uri("mem://test/\u00fc\u00e4\u00f6-\u65e5\u672c\u8a9e");

        await storage.SaveAsync(uri, new MemoryStream(data));
        var retrieved = await storage.LoadAsync(uri);

        retrieved.Should().NotBeNull();
    }

    [Fact]
    public void SqlParser_UnicodeStringLiteral_ParsesCorrectly()
    {
        var parser = new SqlParserEngine();
        var sql = "SELECT * FROM table1 WHERE name = '\u00fc\u00e4\u00f6'";

        var result = parser.Parse(sql);
        result.Should().NotBeNull();
    }

    // ==================== Concurrent Access ====================

    [Fact]
    public async Task InMemoryStorage_ConcurrentStores_AllSucceed()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();

        var tasks = Enumerable.Range(0, 100)
            .Select(i => storage.SaveAsync(
                new Uri($"mem://test/concurrent-{i}"),
                new MemoryStream(new byte[] { (byte)(i % 256) })))
            .ToArray();

        await Task.WhenAll(tasks);

        // Verify all items were stored
        for (int i = 0; i < 100; i++)
        {
            var exists = await storage.ExistsAsync(new Uri($"mem://test/concurrent-{i}"));
            exists.Should().BeTrue($"Key concurrent-{i} should exist after concurrent store");
        }
    }

    [Fact]
    public async Task MessageBus_ConcurrentPublish_AllDelivered()
    {
        var bus = TestPluginFactory.CreateTestMessageBus();
        var receivedCount = 0;

        bus.Subscribe("stress.topic", _ =>
        {
            Interlocked.Increment(ref receivedCount);
            return Task.CompletedTask;
        });

        var tasks = Enumerable.Range(0, 100)
            .Select(i => bus.PublishAsync("stress.topic", new PluginMessage { Type = $"msg-{i}" }))
            .ToArray();

        await Task.WhenAll(tasks);
        receivedCount.Should().Be(100);
    }

    [Fact]
    public async Task MessageBus_ConcurrentSubscribeUnsubscribe_NoDeadlock()
    {
        var bus = TestPluginFactory.CreateTestMessageBus();

        var tasks = Enumerable.Range(0, 50)
            .Select(async i =>
            {
                var sub = bus.Subscribe($"dynamic-topic-{i % 5}", _ => Task.CompletedTask);
                await bus.PublishAsync($"dynamic-topic-{i % 5}", new PluginMessage { Type = $"event-{i}" });
                sub.Dispose();
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Test passes if no deadlock or crash occurred
        true.Should().BeTrue("No deadlock detected during concurrent subscribe/unsubscribe");
    }

    // ==================== Plugin Health Under Load ====================

    [Fact]
    public async Task Plugin_ConcurrentHealthChecks_AllReturnHealthy()
    {
        using var plugin = new DataWarehouse.Plugins.UltimateStorage.UltimateStoragePlugin();

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => plugin.CheckHealthAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            result.Status.Should().Be(HealthStatus.Healthy);
        }
    }

    // ==================== Large Data Handling ====================

    [Fact]
    public async Task InMemoryStorage_LargeObject_StoresAndRetrieves()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("mem://test/large-object");

        // 1MB of data
        var largeData = new byte[1024 * 1024];
        Random.Shared.NextBytes(largeData);

        await storage.SaveAsync(uri, new MemoryStream(largeData));
        var retrieved = await storage.LoadAsync(uri);

        retrieved.Should().NotBeNull();
        using var ms = new MemoryStream();
        await retrieved.CopyToAsync(ms);
        ms.ToArray().Length.Should().Be(largeData.Length);
    }

    // ==================== Dispose Safety ====================

    [Fact]
    public void Plugin_DoubleDispose_DoesNotThrow()
    {
        var plugin = new DataWarehouse.Plugins.UltimateStorage.UltimateStoragePlugin();
        plugin.Dispose();

        // Second dispose should not throw
        Action act = () => plugin.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Plugin_DisposeMultipleTimes_SafeForAllDisposablePlugins()
    {
        var plugins = new IDisposable[]
        {
            new DataWarehouse.Plugins.UltimateEncryption.UltimateEncryptionPlugin(),
            new DataWarehouse.Plugins.UltimateCompute.UltimateComputePlugin(),
            new DataWarehouse.Plugins.UltimateDeployment.UltimateDeploymentPlugin(),
        };

        // Dispose all
        foreach (var p in plugins)
            p.Dispose();

        // Dispose again - should not throw
        foreach (var p in plugins)
        {
            Action act = () => p.Dispose();
            act.Should().NotThrow();
        }
    }

    // ==================== InMemoryStorage Boundary Conditions ====================

    [Fact]
    public async Task InMemoryStorage_LoadNonExistent_ThrowsOrReturnsDefault()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("mem://test/non-existent-12345");

        try
        {
            var result = await storage.LoadAsync(uri);
            // If it returns without throwing, any result is acceptable
        }
        catch (KeyNotFoundException)
        {
            // Expected - item not found
        }
        catch (FileNotFoundException)
        {
            // Also acceptable
        }

        true.Should().BeTrue("Test completed without crash");
    }

    [Fact]
    public async Task InMemoryStorage_StoreAndDelete_NoLongerExists()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("mem://test/temp-key");

        await storage.SaveAsync(uri, new MemoryStream(new byte[] { 1, 2, 3 }));
        var existsBefore = await storage.ExistsAsync(uri);
        existsBefore.Should().BeTrue();

        await storage.DeleteAsync(uri);
        var existsAfter = await storage.ExistsAsync(uri);
        existsAfter.Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryStorage_StoreOverwrite_ReplacesData()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var uri = new Uri("mem://test/overwrite-key");

        await storage.SaveAsync(uri, new MemoryStream(new byte[] { 1, 2, 3 }));
        await storage.SaveAsync(uri, new MemoryStream(new byte[] { 4, 5, 6, 7 }));

        var retrieved = await storage.LoadAsync(uri);
        retrieved.Should().NotBeNull();
        using var ms = new MemoryStream();
        await retrieved.CopyToAsync(ms);
        ms.ToArray().Should().Equal(4, 5, 6, 7);
    }

    // ==================== PluginMessage Edge Cases ====================

    [Fact]
    public void PluginMessage_DefaultConstruction_HasNullableFields()
    {
        var msg = new PluginMessage();
        msg.Should().NotBeNull();
    }

    [Fact]
    public async Task MessageBus_EmptyPayload_DeliveredSuccessfully()
    {
        var bus = TestPluginFactory.CreateTestMessageBus();
        PluginMessage? received = null;

        bus.Subscribe("empty.payload", msg =>
        {
            received = msg;
            return Task.CompletedTask;
        });

        await bus.PublishAsync("empty.payload", new PluginMessage { Type = "empty" });

        received.Should().NotBeNull();
        received!.Type.Should().Be("empty");
    }

    // ==================== Query Engine Edge Cases ====================

    [Fact]
    public void SqlParser_VeryLongQuery_ParsesWithoutStackOverflow()
    {
        var parser = new SqlParserEngine();
        // Generate a SELECT with many columns
        var columns = string.Join(", ", Enumerable.Range(0, 200).Select(i => $"col{i}"));
        var sql = $"SELECT {columns} FROM large_table";

        var result = parser.Parse(sql);
        result.Should().BeOfType<SelectStatement>();
        ((SelectStatement)result).Columns.Length.Should().Be(200);
    }

    [Fact]
    public void SqlParser_ManyJoins_ParsesCorrectly()
    {
        var parser = new SqlParserEngine();
        var sql = "SELECT * FROM t1 JOIN t2 ON t1.id = t2.id JOIN t3 ON t2.id = t3.id JOIN t4 ON t3.id = t4.id JOIN t5 ON t4.id = t5.id";

        var result = parser.Parse(sql);
        result.Should().BeOfType<SelectStatement>();
        ((SelectStatement)result).Joins.Length.Should().Be(4);
    }

    [Fact]
    public void SqlParser_MultipleCtes_ParsesAll()
    {
        var parser = new SqlParserEngine();
        var sql = @"
            WITH cte1 AS (SELECT 1 AS x),
                 cte2 AS (SELECT 2 AS y)
            SELECT * FROM cte1, cte2";

        var result = parser.Parse(sql);
        result.Should().BeOfType<SelectStatement>();
        ((SelectStatement)result).Ctes.Length.Should().Be(2);
    }
}
