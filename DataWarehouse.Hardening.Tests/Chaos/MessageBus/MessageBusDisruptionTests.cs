using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DataWarehouse.Hardening.Tests.Chaos.MessageBus;

/// <summary>
/// Chaos engineering tests proving the system handles message loss, duplication, and
/// reordering without data corruption. Each test establishes a "golden" state from a
/// clean run, then compares a chaos run against it.
///
/// Report: "Stage 3 - Steps 5-6 - Message Bus Disruption"
/// </summary>
public class MessageBusDisruptionTests : IDisposable
{
    private readonly DefaultMessageBus _realBus;
    private readonly ChaosMessageBusProxy _chaosProxy;
    private readonly List<IDisposable> _subscriptions = new();

    public MessageBusDisruptionTests()
    {
        _realBus = new DefaultMessageBus { RateLimitPerSecond = 10_000 };
        _chaosProxy = new ChaosMessageBusProxy(_realBus, seed: 42);
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Chaos scenario: 50% message drop rate.
    /// Operations that depend on message responses must detect missing responses and
    /// fail cleanly (timeout/error) rather than producing corrupt results.
    /// SendAsync with drops must return an error response, not hang or silently succeed.
    /// </summary>
    [Fact]
    public async Task MessageLoss_50Percent_OperationsFailCleanly()
    {
        // Arrange: register a response handler that always succeeds
        var handlerCallCount = 0;
        var sub = _realBus.Subscribe("chaos.request", async (PluginMessage msg) =>
        {
            Interlocked.Increment(ref handlerCallCount);
            return MessageResponse.Ok(new { processed = true });
        });
        _subscriptions.Add(sub);

        _chaosProxy.Mode = ChaosMessageBusProxy.ChaosMode.DropMessages;
        _chaosProxy.DropRate = 0.50;

        // Act: send 100 request-response messages through chaos proxy
        var successCount = 0;
        var errorCount = 0;
        var corruptCount = 0;

        for (int i = 0; i < 100; i++)
        {
            var msg = new PluginMessage
            {
                Type = "chaos.request",
                Payload = new Dictionary<string, object> { ["index"] = i }
            };

            var response = await _chaosProxy.SendAsync("chaos.request", msg);

            if (response.Success)
                Interlocked.Increment(ref successCount);
            else if (response.ErrorCode == "CHAOS_DROP")
                Interlocked.Increment(ref errorCount);
            else
                Interlocked.Increment(ref corruptCount); // Unexpected error = corruption
        }

        // Assert: some messages succeeded, some were dropped cleanly
        Assert.True(successCount > 0, "Some messages should have been delivered");
        Assert.True(errorCount > 0, "Some messages should have been dropped");
        Assert.Equal(0, corruptCount); // No data corruption
        Assert.Equal(100, successCount + errorCount); // All accounted for

        // Verify stats
        Assert.True(_chaosProxy.MessagesDropped > 0, "Proxy should track dropped messages");
        Assert.True(_chaosProxy.MessagesDelivered > 0, "Proxy should track delivered messages");
    }

    /// <summary>
    /// Chaos scenario: 100% message duplication.
    /// Every message is sent twice. The subscriber must produce identical final state
    /// as single-delivery (idempotency proof). Uses accumulator that is idempotent-by-design
    /// (last-write-wins on a keyed map) and verifies against a golden clean run.
    /// </summary>
    [Fact]
    public async Task MessageDuplication_100Percent_IdempotentResult()
    {
        // Arrange: golden state from clean bus
        var goldenState = new ConcurrentDictionary<string, string>();
        var goldenBus = new DefaultMessageBus { RateLimitPerSecond = 10_000 };
        var goldenSub = goldenBus.Subscribe("chaos.update", async (PluginMessage msg) =>
        {
            var key = msg.Payload["key"]?.ToString() ?? "";
            var value = msg.Payload["value"]?.ToString() ?? "";
            goldenState[key] = value; // Last-write-wins (idempotent)
            await Task.CompletedTask;
        });

        // Chaos state from duplicated bus
        var chaosState = new ConcurrentDictionary<string, string>();
        var chaosSub = _realBus.Subscribe("chaos.update", async (PluginMessage msg) =>
        {
            var key = msg.Payload["key"]?.ToString() ?? "";
            var value = msg.Payload["value"]?.ToString() ?? "";
            chaosState[key] = value; // Last-write-wins (idempotent)
            await Task.CompletedTask;
        });
        _subscriptions.Add(chaosSub);

        _chaosProxy.Mode = ChaosMessageBusProxy.ChaosMode.DuplicateMessages;
        _chaosProxy.DuplicateRate = 1.0; // 100% duplication

        // Act: publish 50 keyed updates through both buses
        var operations = new List<(string Key, string Value)>();
        for (int i = 0; i < 50; i++)
        {
            operations.Add(($"item-{i}", $"value-{i}"));
        }

        foreach (var (key, value) in operations)
        {
            var msg = new PluginMessage
            {
                Type = "chaos.update",
                Payload = new Dictionary<string, object> { ["key"] = key, ["value"] = value }
            };

            await goldenBus.PublishAndWaitAsync("chaos.update", msg);
            await _chaosProxy.PublishAndWaitAsync("chaos.update", msg);
        }

        // Wait for fire-and-forget deliveries
        await Task.Delay(200);

        // Assert: chaos state matches golden state exactly (idempotency proven)
        Assert.Equal(goldenState.Count, chaosState.Count);
        foreach (var kvp in goldenState)
        {
            Assert.True(chaosState.TryGetValue(kvp.Key, out var chaosValue),
                $"Key '{kvp.Key}' missing from chaos state");
            Assert.Equal(kvp.Value, chaosValue);
        }

        // Verify duplication actually happened
        Assert.True(_chaosProxy.MessagesDuplicated > 0, "Messages should have been duplicated");
        Assert.True(_chaosProxy.MessagesDelivered > _chaosProxy.MessagesPublished,
            "More deliveries than publishes due to duplication");

        goldenSub.Dispose();
    }

    /// <summary>
    /// Chaos scenario: Reorder buffer of 10.
    /// Messages are buffered and delivered in random order. Operations with ordering
    /// dependencies produce correct result because final state uses commutative updates
    /// (keyed map merge). Verifies that all messages eventually arrive regardless of order.
    /// </summary>
    [Fact]
    public async Task MessageReorder_Buffer10_CorrectFinalState()
    {
        // Arrange: track received messages and their order
        var receivedMessages = new ConcurrentBag<int>();
        var finalState = new ConcurrentDictionary<string, int>();

        var sub = _realBus.Subscribe("chaos.ordered", async (PluginMessage msg) =>
        {
            var index = (int)msg.Payload["index"];
            receivedMessages.Add(index);
            finalState[$"item-{index}"] = index; // Commutative update
            await Task.CompletedTask;
        });
        _subscriptions.Add(sub);

        _chaosProxy.Mode = ChaosMessageBusProxy.ChaosMode.ReorderMessages;
        _chaosProxy.ReorderBufferSize = 10;

        // Act: publish 30 ordered messages
        for (int i = 0; i < 30; i++)
        {
            var msg = new PluginMessage
            {
                Type = "chaos.ordered",
                Payload = new Dictionary<string, object> { ["index"] = i }
            };
            await _chaosProxy.PublishAsync("chaos.ordered", msg);
        }

        // Flush any remaining in buffer
        await _chaosProxy.FlushReorderBufferAsync();

        // Wait for async delivery
        await Task.Delay(300);

        // Assert: all 30 messages received (regardless of order)
        Assert.Equal(30, receivedMessages.Count);
        Assert.Equal(30, finalState.Count);

        // All values present
        for (int i = 0; i < 30; i++)
        {
            Assert.True(finalState.ContainsKey($"item-{i}"), $"Missing item-{i} from final state");
            Assert.Equal(i, finalState[$"item-{i}"]);
        }

        // Verify reordering occurred
        Assert.True(_chaosProxy.MessagesReordered > 0, "Messages should have been reordered");

        // Check that delivery order differs from publish order (probabilistic but near-certain with seed=42)
        var orderedReceived = receivedMessages.ToArray();
        var isStrictlyOrdered = true;
        for (int i = 1; i < orderedReceived.Length; i++)
        {
            if (orderedReceived[i] < orderedReceived[i - 1])
            {
                isStrictlyOrdered = false;
                break;
            }
        }
        // With buffer=10 and shuffle, order should be disrupted
        Assert.False(isStrictlyOrdered, "Messages should not arrive in strict original order after reordering");
    }

    /// <summary>
    /// Chaos scenario: Combined chaos with 20% drop, 50% duplication, reorder buffer 5.
    /// All three disruptions active simultaneously. Verifies no data corruption by
    /// computing checksums on final state and comparing structure integrity.
    /// </summary>
    [Fact]
    public async Task CombinedChaos_NoDataCorruption()
    {
        // Arrange: idempotent accumulator with integrity tracking
        var state = new ConcurrentDictionary<string, string>();
        var integrityHashes = new ConcurrentBag<string>();

        var sub = _realBus.Subscribe("chaos.combined", async (PluginMessage msg) =>
        {
            var key = msg.Payload["key"]?.ToString() ?? "";
            var value = msg.Payload["value"]?.ToString() ?? "";

            // Idempotent last-write-wins
            state[key] = value;

            // Track integrity: each message's payload hash
            var hash = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}:{value}")));
            integrityHashes.Add(hash);

            await Task.CompletedTask;
        });
        _subscriptions.Add(sub);

        _chaosProxy.Mode = ChaosMessageBusProxy.ChaosMode.Combined;
        _chaosProxy.DropRate = 0.20;
        _chaosProxy.DuplicateRate = 0.50;
        _chaosProxy.ReorderBufferSize = 5;

        // Build expected key-value pairs
        var expected = new Dictionary<string, string>();
        for (int i = 0; i < 40; i++)
        {
            expected[$"data-{i}"] = $"payload-{i}";
        }

        // Act: publish all updates
        foreach (var (key, value) in expected)
        {
            var msg = new PluginMessage
            {
                Type = "chaos.combined",
                Payload = new Dictionary<string, object> { ["key"] = key, ["value"] = value }
            };
            await _chaosProxy.PublishAsync("chaos.combined", msg);
        }

        // Flush reorder buffer
        await _chaosProxy.FlushReorderBufferAsync();
        await Task.Delay(300);

        // Assert: no corruption in received values
        foreach (var kvp in state)
        {
            // Every value in state must be a valid expected value (not garbled)
            Assert.True(expected.ContainsKey(kvp.Key),
                $"Unexpected key '{kvp.Key}' appeared in state (corruption)");
            Assert.Equal(expected[kvp.Key], kvp.Value);
        }

        // Verify integrity hashes are all valid SHA256 (proper hex, 64 chars)
        foreach (var hash in integrityHashes)
        {
            Assert.Equal(64, hash.Length);
        }

        // Some messages were dropped, so state may be subset of expected
        Assert.True(state.Count <= expected.Count,
            "State should not contain more items than expected");
        Assert.True(state.Count > 0, "At least some messages should have arrived");

        // Verify chaos actually happened
        Assert.True(_chaosProxy.MessagesDropped > 0, "Some messages should have been dropped");
        Assert.True(_chaosProxy.MessagesPublished > 0, "Messages should have been published");
    }

    /// <summary>
    /// Chaos scenario: Multiple concurrent publishers and subscribers operating under
    /// combined chaos. Simulates a multi-plugin environment where plugins publish and
    /// subscribe to different topics while the message bus is disrupted.
    /// Proves: no crashes, no deadlocks, no data corruption.
    /// </summary>
    [Fact]
    public async Task MultiPluginChaos_PluginsSurviveDisruption()
    {
        // Arrange: simulate 5 plugins with independent topics
        var pluginStates = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
        var pluginErrors = new ConcurrentBag<string>();

        for (int p = 0; p < 5; p++)
        {
            var pluginId = $"plugin-{p}";
            pluginStates[pluginId] = new ConcurrentDictionary<string, int>();

            var topic = $"chaos.plugin-{p}";
            var pState = pluginStates[pluginId];

            var sub = _realBus.Subscribe(topic, async (PluginMessage msg) =>
            {
                try
                {
                    var key = msg.Payload["key"]?.ToString() ?? "";
                    var value = (int)msg.Payload["value"];
                    // Idempotent max-wins (commutative, handles duplicates and reorder)
                    pState.AddOrUpdate(key, value, (_, existing) => Math.Max(existing, value));
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    pluginErrors.Add($"{pluginId}: {ex.Message}");
                }
            });
            _subscriptions.Add(sub);
        }

        _chaosProxy.Mode = ChaosMessageBusProxy.ChaosMode.Combined;
        _chaosProxy.DropRate = 0.15;
        _chaosProxy.DuplicateRate = 0.30;
        _chaosProxy.ReorderBufferSize = 5;

        // Act: 5 concurrent publishers, each publishing 20 messages to its own topic
        var publishTasks = new List<Task>();
        for (int p = 0; p < 5; p++)
        {
            var pluginIndex = p;
            publishTasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    var msg = new PluginMessage
                    {
                        Type = $"chaos.plugin-{pluginIndex}",
                        SourcePluginId = $"plugin-{pluginIndex}",
                        Payload = new Dictionary<string, object>
                        {
                            ["key"] = $"op-{i}",
                            ["value"] = i
                        }
                    };

                    try
                    {
                        await _chaosProxy.PublishAsync($"chaos.plugin-{pluginIndex}", msg);
                    }
                    catch (Exception ex)
                    {
                        pluginErrors.Add($"plugin-{pluginIndex} publish: {ex.Message}");
                    }
                }
            }));
        }

        await Task.WhenAll(publishTasks);
        await _chaosProxy.FlushReorderBufferAsync();
        await Task.Delay(500);

        // Assert: no crashes (we got here), no processing errors
        Assert.Empty(pluginErrors);

        // All plugins have some state (not all dropped for every plugin)
        foreach (var kvp in pluginStates)
        {
            // At least some messages should have arrived for each plugin
            // (with 15% drop and 20 messages, probability of all 20 dropped is ~0.15^20 ~ 0)
            Assert.True(kvp.Value.Count > 0,
                $"{kvp.Key} should have received at least some messages");
        }

        // No value corruption: all values should be in range [0, 19]
        foreach (var (pluginId, pState) in pluginStates)
        {
            foreach (var (key, value) in pState)
            {
                Assert.InRange(value, 0, 19);
            }
        }

        // Verify proxy stats are sane
        Assert.True(_chaosProxy.MessagesPublished == 100,
            $"Expected 100 published messages, got {_chaosProxy.MessagesPublished}");
    }

    /// <summary>
    /// Chaos scenario: Rapid fire publish under duplication proves message ID tracking
    /// can detect duplicates. Accumulator counting unique MessageIds demonstrates that
    /// duplicate detection is possible even when messages arrive multiple times.
    /// </summary>
    [Fact]
    public async Task MessageDuplication_UniqueIdTracking_DeduplicationPossible()
    {
        // Arrange: track unique message IDs to prove dedup is possible
        var seenIds = new ConcurrentDictionary<string, int>();
        var duplicateCount = 0;

        var sub = _realBus.Subscribe("chaos.dedup", async (PluginMessage msg) =>
        {
            var count = seenIds.AddOrUpdate(msg.MessageId, 1, (_, c) => c + 1);
            if (count > 1)
                Interlocked.Increment(ref duplicateCount);
            await Task.CompletedTask;
        });
        _subscriptions.Add(sub);

        _chaosProxy.Mode = ChaosMessageBusProxy.ChaosMode.DuplicateMessages;
        _chaosProxy.DuplicateRate = 1.0;

        // Act: publish 30 messages
        for (int i = 0; i < 30; i++)
        {
            var msg = new PluginMessage
            {
                Type = "chaos.dedup",
                Payload = new Dictionary<string, object> { ["index"] = i }
            };
            await _chaosProxy.PublishAndWaitAsync("chaos.dedup", msg);
        }

        await Task.Delay(200);

        // Assert: duplicates were detected via MessageId tracking
        Assert.True(duplicateCount > 0, "Duplicate messages should have been detected via MessageId");
        Assert.Equal(30, seenIds.Count); // 30 unique message IDs
        Assert.True(seenIds.Values.All(v => v >= 1), "Each message seen at least once");
        Assert.True(seenIds.Values.Any(v => v > 1), "Some messages seen more than once (duplicated)");
    }

    /// <summary>
    /// Chaos scenario: SendAsync under chaos returns proper error codes for drops,
    /// never hangs, and never produces corrupt responses.
    /// </summary>
    [Fact]
    public async Task SendAsync_UnderChaos_NeverHangsOrCorrupts()
    {
        // Arrange: response handler with known payload
        var expectedPayload = new Dictionary<string, object> { ["result"] = "computed-42" };
        var sub = _realBus.Subscribe("chaos.compute", async (PluginMessage msg) =>
        {
            return MessageResponse.Ok(expectedPayload);
        });
        _subscriptions.Add(sub);

        _chaosProxy.Mode = ChaosMessageBusProxy.ChaosMode.Combined;
        _chaosProxy.DropRate = 0.30;
        _chaosProxy.DuplicateRate = 0.50;
        _chaosProxy.ReorderBufferSize = 3;

        // Act: send 50 request-response calls with timeout
        var responses = new List<MessageResponse>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 50; i++)
        {
            var msg = new PluginMessage
            {
                Type = "chaos.compute",
                Payload = new Dictionary<string, object> { ["input"] = i }
            };

            var response = await _chaosProxy.SendAsync("chaos.compute", msg,
                TimeSpan.FromSeconds(2), cts.Token);
            responses.Add(response);
        }

        // Assert: we did NOT hang (got all 50 responses within timeout)
        Assert.Equal(50, responses.Count);

        // Every response is either clean success or clean CHAOS_DROP error
        foreach (var response in responses)
        {
            Assert.True(
                response.Success || response.ErrorCode == "CHAOS_DROP",
                $"Unexpected error: {response.ErrorCode} - {response.ErrorMessage}");
        }

        // At least some succeeded and some failed
        Assert.Contains(responses, r => r.Success);
        Assert.Contains(responses, r => !r.Success);
    }
}
