using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Messaging;

/// <summary>
/// Tests for IMessageBus contract and TestMessageBus implementation (replaces AdvancedMessageBusTests).
/// Validates publish/subscribe patterns, request/response, and message routing.
/// </summary>
[Trait("Category", "Unit")]
public class MessageBusContractTests
{
    [Fact]
    public void IMessageBus_ShouldDefineAllRequiredMethods()
    {
        var type = typeof(IMessageBus);
        var methods = type.GetMethods();

        methods.Should().Contain(m => m.Name == "PublishAsync");
        methods.Should().Contain(m => m.Name == "PublishAndWaitAsync");
        methods.Should().Contain(m => m.Name == "SendAsync");
        methods.Should().Contain(m => m.Name == "Subscribe");
        methods.Should().Contain(m => m.Name == "Unsubscribe");
        methods.Should().Contain(m => m.Name == "GetActiveTopics");
    }

    [Fact]
    public async Task TestMessageBus_Publish_ShouldNotifyMultipleSubscribers()
    {
        var bus = new TestMessageBus();
        var count1 = 0;
        var count2 = 0;

        bus.Subscribe("multi.topic", _ => { Interlocked.Increment(ref count1); return Task.CompletedTask; });
        bus.Subscribe("multi.topic", _ => { Interlocked.Increment(ref count2); return Task.CompletedTask; });

        await bus.PublishAsync("multi.topic", new PluginMessage());

        count1.Should().Be(1);
        count2.Should().Be(1);
    }

    [Fact]
    public async Task TestMessageBus_SendAsync_WithHandler_ShouldReturnHandlerResponse()
    {
        var bus = new TestMessageBus();
        bus.Subscribe("request.topic", msg =>
            Task.FromResult(MessageResponse.Ok("handler-result")));

        var response = await bus.SendAsync("request.topic", new PluginMessage());

        response.Success.Should().BeTrue();
        response.Payload.Should().Be("handler-result");
    }

    [Fact]
    public async Task TestMessageBus_SendAsync_WithoutHandler_ShouldReturnDefaultOk()
    {
        var bus = new TestMessageBus();

        var response = await bus.SendAsync("no.handler", new PluginMessage());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public void TestMessageBus_GetActiveTopics_ShouldReturnSubscribedTopics()
    {
        var bus = new TestMessageBus();
        bus.Subscribe("topic.a", _ => Task.CompletedTask);
        bus.Subscribe("topic.b", _ => Task.CompletedTask);

        var topics = bus.GetActiveTopics().ToList();

        topics.Should().Contain("topic.a");
        topics.Should().Contain("topic.b");
    }

    [Fact]
    public void TestMessageBus_Unsubscribe_ShouldRemoveTopic()
    {
        var bus = new TestMessageBus();
        bus.Subscribe("removable.topic", _ => Task.CompletedTask);

        bus.Unsubscribe("removable.topic");

        bus.GetActiveTopics().Should().NotContain("removable.topic");
    }

    [Fact]
    public async Task TestMessageBus_PublishedMessages_ShouldTrackHistory()
    {
        var bus = new TestMessageBus();

        await bus.PublishAsync("track.topic", new PluginMessage { Type = "event-1" });
        await bus.PublishAsync("track.topic", new PluginMessage { Type = "event-2" });

        bus.PublishedMessages.Should().HaveCount(2);
        bus.PublishedMessages[0].Topic.Should().Be("track.topic");
    }

    [Fact]
    public void TestMessageBus_Reset_ShouldClearEverything()
    {
        var bus = new TestMessageBus();
        bus.Subscribe("some.topic", _ => Task.CompletedTask);
        bus.SetupResponse("some.topic", MessageResponse.Ok());

        bus.Reset();

        bus.GetActiveTopics().Should().BeEmpty();
    }

    [Fact]
    public void PluginMessage_ShouldHaveDefaults()
    {
        var msg = new PluginMessage();
        msg.MessageId.Should().NotBeNullOrEmpty();
        msg.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}
