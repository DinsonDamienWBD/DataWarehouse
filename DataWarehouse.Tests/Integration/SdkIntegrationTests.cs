using DataWarehouse.Kernel.Plugins;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for SDK components working together (replaces KernelIntegrationTests).
/// Tests cross-cutting concerns like message bus + storage provider interaction.
/// </summary>
[Trait("Category", "Unit")]
public class SdkIntegrationTests
{
    [Fact]
    public async Task TestMessageBus_PublishSubscribe_ShouldDeliverMessages()
    {
        // Arrange
        var bus = TestPluginFactory.CreateTestMessageBus();
        var received = new List<string>();
        bus.Subscribe("test.topic", msg =>
        {
            received.Add(msg.Type);
            return Task.CompletedTask;
        });

        // Act
        await bus.PublishAsync("test.topic", new SDK.Utilities.PluginMessage { Type = "test-event" });

        // Assert
        received.Should().ContainSingle("test-event");
    }

    [Fact]
    public async Task TestMessageBus_RequestResponse_ShouldReturnConfiguredResponse()
    {
        var bus = TestPluginFactory.CreateTestMessageBus();
        bus.SetupResponse("query.topic", MessageResponse.Ok("result-data"));

        var response = await bus.SendAsync("query.topic", new SDK.Utilities.PluginMessage { Type = "query" });

        response.Success.Should().BeTrue();
        response.Payload.Should().Be("result-data");
    }

    [Fact]
    public async Task InMemoryStorage_WithTestMessageBus_ShouldWorkIndependently()
    {
        var storage = TestPluginFactory.CreateInMemoryStorage();
        var bus = TestPluginFactory.CreateTestMessageBus();

        var uri = new Uri("memory:///integration/test.dat");
        await storage.SaveAsync(uri, new MemoryStream(new byte[100]));

        (await storage.ExistsAsync(uri)).Should().BeTrue();

        await bus.PublishAsync("storage.saved", new SDK.Utilities.PluginMessage
        {
            Type = "storage.saved",
            Payload = new Dictionary<string, object> { ["uri"] = uri.ToString() }
        });

        bus.PublishedMessages.Should().ContainSingle();
    }

    [Fact]
    public async Task StoragePool_WithInMemoryProvider_ShouldSaveAndLoad()
    {
        var plugin = new InMemoryStoragePlugin();
        var uri = new Uri("memory:///integration/roundtrip.txt");
        var content = "integration test data"u8.ToArray();

        await plugin.SaveAsync(uri, new MemoryStream(content));

        var loaded = await plugin.LoadAsync(uri);
        using var reader = new StreamReader(loaded);
        var result = await reader.ReadToEndAsync();

        result.Should().Be("integration test data");
    }

    [Fact]
    public async Task TestMessageBus_Unsubscribe_ShouldStopDelivery()
    {
        var bus = TestPluginFactory.CreateTestMessageBus();
        var count = 0;
        var sub = bus.Subscribe("count.topic", _ =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await bus.PublishAsync("count.topic", new SDK.Utilities.PluginMessage());
        count.Should().Be(1);

        sub.Dispose();
        await bus.PublishAsync("count.topic", new SDK.Utilities.PluginMessage());
        count.Should().Be(1, "handler was unsubscribed");
    }

    [Fact]
    public async Task InMemoryTestStorage_BasicOperations_ShouldWork()
    {
        var store = TestPluginFactory.CreateTestStorage();

        await store.SaveAsync("key1", new byte[] { 1, 2, 3 });
        (await store.ExistsAsync("key1")).Should().BeTrue();
        store.Count.Should().Be(1);

        var loaded = await store.LoadAsync("key1");
        loaded.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });

        (await store.DeleteAsync("key1")).Should().BeTrue();
        store.Count.Should().Be(0);
    }
}
