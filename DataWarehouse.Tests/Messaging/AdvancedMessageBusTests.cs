using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Messaging;

/// <summary>
/// Tests for AdvancedMessageBus reliable publishing and message groups.
/// </summary>
public class AdvancedMessageBusTests : IDisposable
{
    private readonly AdvancedMessageBus _bus;

    public AdvancedMessageBusTests()
    {
        _bus = new AdvancedMessageBus(new TestKernelContext());
    }

    public void Dispose()
    {
        _bus.Dispose();
    }

    #region Basic Publish/Subscribe Tests

    [Fact]
    public async Task PublishAsync_ShouldDeliverToSubscribers()
    {
        // Arrange
        var received = new List<PluginMessage>();
        _bus.Subscribe("test.topic", msg =>
        {
            received.Add(msg);
            return Task.CompletedTask;
        });

        // Act
        await _bus.PublishAsync("test.topic", new PluginMessage { Type = "test" });

        // Assert
        received.Should().HaveCount(1);
        received[0].Type.Should().Be("test");
    }

    [Fact]
    public async Task PublishAsync_ShouldDeliverToMultipleSubscribers()
    {
        // Arrange
        var count = 0;

        _bus.Subscribe("multi.topic", _ => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        _bus.Subscribe("multi.topic", _ => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        _bus.Subscribe("multi.topic", _ => { Interlocked.Increment(ref count); return Task.CompletedTask; });

        // Act
        await _bus.PublishAsync("multi.topic", new PluginMessage { Type = "test" });

        // Assert
        count.Should().Be(3);
    }

    [Fact]
    public void Subscribe_ShouldReturnDisposable()
    {
        // Arrange
        var received = 0;
        var subscription = _bus.Subscribe("dispose.topic", _ => { received++; return Task.CompletedTask; });

        // Act
        _bus.PublishAsync("dispose.topic", new PluginMessage()).Wait();
        received.Should().Be(1);

        subscription.Dispose();
        _bus.PublishAsync("dispose.topic", new PluginMessage()).Wait();

        // Assert - Should not receive after dispose
        received.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_ShouldRemoveAllHandlers()
    {
        // Arrange
        var received = 0;
        _bus.Subscribe("unsub.topic", _ => { received++; return Task.CompletedTask; });
        _bus.Subscribe("unsub.topic", _ => { received++; return Task.CompletedTask; });

        // Act
        _bus.Unsubscribe("unsub.topic");
        _bus.PublishAsync("unsub.topic", new PluginMessage()).Wait();

        // Assert
        received.Should().Be(0);
    }

    [Fact]
    public void GetActiveTopics_ShouldReturnSubscribedTopics()
    {
        // Arrange
        _bus.Subscribe("topic1", _ => Task.CompletedTask);
        _bus.Subscribe("topic2", _ => Task.CompletedTask);
        _bus.Subscribe("topic3", _ => Task.CompletedTask);

        // Act
        var topics = _bus.GetActiveTopics().ToList();

        // Assert
        topics.Should().BeEquivalentTo(["topic1", "topic2", "topic3"]);
    }

    #endregion

    #region SendAsync Tests

    [Fact]
    public async Task SendAsync_ShouldReturnResponseFromHandler()
    {
        // Arrange
        _bus.Subscribe("request.topic", msg =>
        {
            return Task.CompletedTask;
        });

        // Act
        var response = await _bus.SendAsync("request.topic", new PluginMessage { Type = "request" });

        // Assert
        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnErrorWhenNoHandler()
    {
        // Act
        var response = await _bus.SendAsync("no.handler", new PluginMessage { Type = "request" });

        // Assert
        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("NO_HANDLER");
    }

    [Fact]
    public async Task SendAsync_WithTimeout_ShouldRespectTimeout()
    {
        // Arrange
        _bus.Subscribe("slow.topic", async msg =>
        {
            await Task.Delay(5000);
        });

        // Act
        var response = await _bus.SendAsync(
            "slow.topic",
            new PluginMessage { Type = "slow" },
            TimeSpan.FromMilliseconds(100));

        // Assert
        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be("TIMEOUT");
    }

    #endregion

    #region Reliable Publishing Tests

    [Fact]
    public async Task PublishReliableAsync_ShouldReturnSuccessWhenDelivered()
    {
        // Arrange
        _bus.Subscribe("reliable.topic", _ => Task.CompletedTask);

        // Act
        var result = await _bus.PublishReliableAsync("reliable.topic", new PluginMessage { Type = "reliable" });

        // Assert
        result.Success.Should().BeTrue();
        result.MessageId.Should().NotBeNullOrEmpty();
        result.State.Should().Be(MessageState.Delivered);
    }

    [Fact]
    public async Task PublishReliableAsync_ShouldTrackMessageState()
    {
        // Arrange
        _bus.Subscribe("track.topic", _ => Task.CompletedTask);

        // Act
        var result = await _bus.PublishReliableAsync("track.topic", new PluginMessage { Type = "track" });

        // Assert
        result.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AcknowledgeAsync_ShouldUpdateMessageState()
    {
        // Arrange
        _bus.Subscribe("ack.topic", _ => Task.CompletedTask);

        var result = await _bus.PublishReliableAsync(
            "ack.topic",
            new PluginMessage { Type = "ack" },
            new ReliablePublishOptions { RequireAcknowledgment = false });

        // Act
        await _bus.AcknowledgeAsync(result.MessageId);

        // Assert - The message state should be acknowledged
        var stats = _bus.GetStatistics();
        stats.TotalAcknowledged.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task PublishWithConfirmationAsync_ShouldReturnDetailedResult()
    {
        // Arrange
        _bus.Subscribe("confirm.topic", _ => Task.CompletedTask);

        // Act
        var result = await _bus.PublishWithConfirmationAsync(
            "confirm.topic",
            new PluginMessage { Type = "confirm" },
            new PublishOptions { CorrelationId = "test-correlation" });

        // Assert
        result.Success.Should().BeTrue();
        result.MessageId.Should().Be("test-correlation");
        result.SubscribersNotified.Should().Be(1);
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task PublishWithConfirmationAsync_ShouldReportNoSubscribers()
    {
        // Act
        var result = await _bus.PublishWithConfirmationAsync(
            "no.subscribers",
            new PluginMessage { Type = "orphan" });

        // Assert
        result.Success.Should().BeFalse();
        result.SubscribersNotified.Should().Be(0);
        result.Error.Should().Contain("No subscribers");
    }

    #endregion

    #region Filtered Subscription Tests

    [Fact]
    public async Task FilteredSubscribe_ShouldOnlyReceiveMatchingMessages()
    {
        // Arrange
        var received = new List<PluginMessage>();

        _bus.Subscribe(
            "filter.topic",
            msg => msg.Type == "important",
            msg => received.Add(msg));

        // Act
        await _bus.PublishAsync("filter.topic", new PluginMessage { Type = "important" });
        await _bus.PublishAsync("filter.topic", new PluginMessage { Type = "unimportant" });
        await _bus.PublishAsync("filter.topic", new PluginMessage { Type = "important" });

        // Assert
        received.Should().HaveCount(2);
        received.Should().AllSatisfy(m => m.Type.Should().Be("important"));
    }

    [Fact]
    public async Task FilteredSubscribe_ShouldBeDisposable()
    {
        // Arrange
        var received = 0;
        var subscription = _bus.Subscribe(
            "disposable.filter",
            _ => true,
            _ => received++);

        await _bus.PublishAsync("disposable.filter", new PluginMessage());
        received.Should().Be(1);

        // Act
        subscription.Dispose();
        await _bus.PublishAsync("disposable.filter", new PluginMessage());

        // Assert
        received.Should().Be(1);
    }

    #endregion

    #region Message Group Tests

    [Fact]
    public async Task MessageGroup_ShouldBatchMessages()
    {
        // Arrange
        var received = new List<string>();
        _bus.Subscribe("group.topic", msg =>
        {
            received.Add(msg.Type);
            return Task.CompletedTask;
        });

        // Act
        using (var group = _bus.CreateGroup("batch1"))
        {
            group.Add("group.topic", new PluginMessage { Type = "msg1" });
            group.Add("group.topic", new PluginMessage { Type = "msg2" });
            group.Add("group.topic", new PluginMessage { Type = "msg3" });

            var result = await group.CommitAsync();

            // Assert
            result.Success.Should().BeTrue();
            result.TotalMessages.Should().Be(3);
            result.SuccessfulMessages.Should().Be(3);
        }

        received.Should().BeEquivalentTo(["msg1", "msg2", "msg3"]);
    }

    [Fact]
    public void MessageGroup_ShouldThrowOnDuplicateGroupId()
    {
        // Arrange
        var group1 = _bus.CreateGroup("duplicate");

        // Act & Assert
        var act = () => _bus.CreateGroup("duplicate");
        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");

        group1.Dispose();
    }

    [Fact]
    public void MessageGroup_Rollback_ShouldDiscardMessages()
    {
        // Arrange
        var received = 0;
        _bus.Subscribe("rollback.topic", _ => { received++; return Task.CompletedTask; });

        // Act
        using (var group = _bus.CreateGroup("rollback"))
        {
            group.Add("rollback.topic", new PluginMessage { Type = "msg1" });
            group.Add("rollback.topic", new PluginMessage { Type = "msg2" });

            group.Rollback();
        }

        // Assert - Messages should not have been delivered
        received.Should().Be(0);
    }

    [Fact]
    public async Task MessageGroup_ShouldReportPartialFailures()
    {
        // Arrange
        var failOnSecond = 0;
        _bus.Subscribe("partial.topic", msg =>
        {
            if (++failOnSecond == 2)
                throw new Exception("Simulated failure");
            return Task.CompletedTask;
        });

        // Act
        using (var group = _bus.CreateGroup("partial"))
        {
            group.Add("partial.topic", new PluginMessage { Type = "msg1" });
            group.Add("partial.topic", new PluginMessage { Type = "msg2" });
            group.Add("partial.topic", new PluginMessage { Type = "msg3" });

            var result = await group.CommitAsync();

            // Assert
            result.TotalMessages.Should().Be(3);
            result.FailedMessages.Should().Be(1);
            result.Errors.Should().NotBeEmpty();
        }
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public async Task GetStatistics_ShouldTrackPublishedMessages()
    {
        // Arrange
        _bus.Subscribe("stats.topic", _ => Task.CompletedTask);

        // Act
        await _bus.PublishReliableAsync("stats.topic", new PluginMessage());
        await _bus.PublishReliableAsync("stats.topic", new PluginMessage());

        // Assert
        var stats = _bus.GetStatistics();
        stats.TotalPublished.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task GetStatistics_ShouldTrackSubscriptions()
    {
        // Arrange
        _bus.Subscribe("sub1", _ => Task.CompletedTask);
        _bus.Subscribe("sub2", _ => Task.CompletedTask);

        // Assert
        var stats = _bus.GetStatistics();
        stats.ActiveSubscriptions.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void ResetStatistics_ShouldClearCounters()
    {
        // Arrange
        _bus.Subscribe("reset.topic", _ => Task.CompletedTask);
        _bus.PublishReliableAsync("reset.topic", new PluginMessage()).Wait();

        var before = _bus.GetStatistics();
        before.TotalPublished.Should().BeGreaterThan(0);

        // Act
        _bus.ResetStatistics();

        // Assert
        var after = _bus.GetStatistics();
        after.TotalPublished.Should().Be(0);
        after.TotalDelivered.Should().Be(0);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentPublish_ShouldBeThreadSafe()
    {
        // Arrange
        var received = 0;
        _bus.Subscribe("concurrent.topic", _ =>
        {
            Interlocked.Increment(ref received);
            return Task.CompletedTask;
        });

        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(_bus.PublishAsync("concurrent.topic", new PluginMessage { Type = $"msg{i}" }));
        }

        await Task.WhenAll(tasks);

        // Assert
        received.Should().Be(100);
    }

    [Fact]
    public async Task ConcurrentSubscribeAndPublish_ShouldBeThreadSafe()
    {
        // Arrange
        var received = 0;
        var tasks = new List<Task>();

        // Act - Concurrent subscriptions and publishes
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                _bus.Subscribe("dynamic.topic", _ =>
                {
                    Interlocked.Increment(ref received);
                    return Task.CompletedTask;
                });
            }));

            tasks.Add(_bus.PublishAsync("dynamic.topic", new PluginMessage { Type = "msg" }));
        }

        await Task.WhenAll(tasks);

        // Assert - Should not throw
        received.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region Test Helpers

    private class TestKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.OnPrem;
        public string RootPath => Environment.CurrentDirectory;
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => [];
    }

    #endregion
}
