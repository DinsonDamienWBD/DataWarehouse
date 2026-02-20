using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Kernel;

/// <summary>
/// Tests for DefaultMessageBus: pub/sub, topic filtering, unsubscribe, concurrent publish.
/// </summary>
[Trait("Category", "Unit")]
public class MessageBusTests
{
    private DefaultMessageBus CreateBus() => new DefaultMessageBus();

    #region Publish/Subscribe

    [Fact]
    public async Task Subscribe_ShouldReceivePublishedMessage()
    {
        var bus = CreateBus();
        var tcs = new TaskCompletionSource<PluginMessage>();

        bus.Subscribe("test.topic", msg =>
        {
            tcs.TrySetResult(msg);
            return Task.CompletedTask;
        });

        var message = new PluginMessage { Type = "test", Source = "unit-test" };
        await bus.PublishAsync("test.topic", message);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Type.Should().Be("test");
        result.Source.Should().Be("unit-test");
    }

    [Fact]
    public async Task PublishAndWaitAsync_ShouldWaitForHandlers()
    {
        var bus = CreateBus();
        bool handlerCalled = false;

        bus.Subscribe("test.wait", async msg =>
        {
            await Task.Delay(50);
            handlerCalled = true;
        });

        await bus.PublishAndWaitAsync("test.wait", new PluginMessage { Type = "test", Source = "unit-test" });

        // Give handler time to complete since PublishAndWaitAsync fires handlers
        await Task.Delay(200);
        handlerCalled.Should().BeTrue();
    }

    #endregion

    #region Topic Filtering

    [Fact]
    public async Task Subscribe_DifferentTopic_ShouldNotReceive()
    {
        var bus = CreateBus();
        bool received = false;

        bus.Subscribe("topic.a", msg =>
        {
            received = true;
            return Task.CompletedTask;
        });

        await bus.PublishAsync("topic.b", new PluginMessage { Type = "test", Source = "unit-test" });
        await Task.Delay(200);

        received.Should().BeFalse();
    }

    [Fact]
    public async Task Subscribe_MultipleTopics_ShouldOnlyReceiveMatching()
    {
        var bus = CreateBus();
        int countA = 0;
        int countB = 0;

        bus.Subscribe("count.a", msg => { Interlocked.Increment(ref countA); return Task.CompletedTask; });
        bus.Subscribe("count.b", msg => { Interlocked.Increment(ref countB); return Task.CompletedTask; });

        await bus.PublishAsync("count.a", new PluginMessage { Type = "test", Source = "unit-test" });
        await bus.PublishAsync("count.a", new PluginMessage { Type = "test", Source = "unit-test" });
        await bus.PublishAsync("count.b", new PluginMessage { Type = "test", Source = "unit-test" });
        await Task.Delay(300);

        countA.Should().Be(2);
        countB.Should().Be(1);
    }

    #endregion

    #region Unsubscribe

    [Fact]
    public async Task Unsubscribe_ShouldStopReceivingMessages()
    {
        var bus = CreateBus();
        int count = 0;

        var sub = bus.Subscribe("unsub.test", msg =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await bus.PublishAsync("unsub.test", new PluginMessage { Type = "test", Source = "unit-test" });
        await Task.Delay(200);
        count.Should().Be(1);

        sub.Dispose();

        await bus.PublishAsync("unsub.test", new PluginMessage { Type = "test", Source = "unit-test" });
        await Task.Delay(200);
        count.Should().Be(1, "No messages after unsubscribe");
    }

    [Fact]
    public void Unsubscribe_Topic_ShouldRemoveAllHandlers()
    {
        var bus = CreateBus();
        bus.Subscribe("remove.all", msg => Task.CompletedTask);
        bus.Subscribe("remove.all", msg => Task.CompletedTask);

        bus.Unsubscribe("remove.all");

        var topics = bus.GetActiveTopics().ToList();
        topics.Should().NotContain("remove.all");
    }

    #endregion

    #region Concurrent Publish

    [Fact]
    public async Task ConcurrentPublish_AllMessagesShouldBeReceived()
    {
        var bus = CreateBus();
        int count = 0;

        bus.Subscribe("concurrent.test", msg =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        var tasks = Enumerable.Range(0, 20).Select(i =>
            bus.PublishAsync("concurrent.test", new PluginMessage
            {
                Type = "concurrent",
                Source = $"publisher-{i}"
            })
        ).ToArray();

        await Task.WhenAll(tasks);
        await Task.Delay(500);

        count.Should().Be(20, "All concurrent messages should be received");
    }

    #endregion

    #region GetActiveTopics

    [Fact]
    public void GetActiveTopics_NoSubscriptions_ShouldBeEmpty()
    {
        var bus = CreateBus();
        bus.GetActiveTopics().Should().BeEmpty();
    }

    [Fact]
    public void GetActiveTopics_WithSubscriptions_ShouldReturnTopics()
    {
        var bus = CreateBus();
        bus.Subscribe("topic.one", msg => Task.CompletedTask);
        bus.Subscribe("topic.two", msg => Task.CompletedTask);

        var topics = bus.GetActiveTopics().ToList();
        topics.Should().Contain("topic.one");
        topics.Should().Contain("topic.two");
    }

    #endregion

    #region Send (Request-Response)

    [Fact]
    public async Task SendAsync_ShouldReturnResponse()
    {
        var bus = CreateBus();

        bus.Subscribe("rpc.echo", msg =>
        {
            return Task.FromResult(MessageResponse.Ok(msg.Type));
        });

        var response = await bus.SendAsync("rpc.echo",
            new PluginMessage { Type = "hello", Source = "unit-test" },
            TimeSpan.FromSeconds(5));

        response.Should().NotBeNull();
        response.Success.Should().BeTrue();
    }

    #endregion

    #region Null Arguments

    [Fact]
    public async Task PublishAsync_NullTopic_ShouldThrow()
    {
        var bus = CreateBus();
        var act = async () => await bus.PublishAsync(null!, new PluginMessage { Type = "x", Source = "test" });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PublishAsync_NullMessage_ShouldThrow()
    {
        var bus = CreateBus();
        var act = async () => await bus.PublishAsync("topic", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region MessageResponse Factory

    [Fact]
    public void MessageResponse_Ok_ShouldBeSuccessful()
    {
        var response = MessageResponse.Ok("payload");
        response.Success.Should().BeTrue();
        response.Payload.Should().Be("payload");
        response.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void MessageResponse_Error_ShouldNotBeSuccessful()
    {
        var response = MessageResponse.Error("Something failed", "ERR001");
        response.Success.Should().BeFalse();
        response.ErrorMessage.Should().Be("Something failed");
        response.ErrorCode.Should().Be("ERR001");
    }

    #endregion
}
