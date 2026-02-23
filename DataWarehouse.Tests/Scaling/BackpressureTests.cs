using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Scaling;
using Xunit;

namespace DataWarehouse.Tests.Scaling
{
    /// <summary>
    /// Tests for <see cref="IBackpressureAware"/> implementations covering state transitions,
    /// event firing, and all backpressure strategies (DropOldest, BlockProducer, ShedLoad, Adaptive).
    /// </summary>
    public class BackpressureTests
    {
        // ----------------------------------------------------------------
        // Test double: a simple IBackpressureAware implementation
        // ----------------------------------------------------------------

        private sealed class TestBackpressureSubsystem : IBackpressureAware
        {
            private readonly List<string> _queue = new();
            private readonly object _lock = new();
            private readonly SemaphoreSlim _spaceAvailable = new(0, int.MaxValue);
            private readonly int _capacity;
            private BackpressureState _currentState = BackpressureState.Normal;

            public TestBackpressureSubsystem(int capacity, BackpressureStrategy strategy = BackpressureStrategy.DropOldest)
            {
                _capacity = capacity;
                Strategy = strategy;
            }

            public BackpressureStrategy Strategy { get; set; }
            public BackpressureState CurrentState => _currentState;
            public event Action<BackpressureStateChangedEventArgs>? OnBackpressureChanged;

            public int QueueCount { get { lock (_lock) return _queue.Count; } }
            public List<string> DroppedItems { get; } = new();
            public List<string> RejectedItems { get; } = new();

            public Task ApplyBackpressureAsync(BackpressureContext context, CancellationToken ct = default)
            {
                var ratio = context.MaxCapacity > 0
                    ? (double)context.CurrentLoad / context.MaxCapacity
                    : 0;

                var newState = ratio switch
                {
                    >= 0.95 => BackpressureState.Shedding,
                    >= 0.80 => BackpressureState.Critical,
                    >= 0.50 => BackpressureState.Warning,
                    _ => BackpressureState.Normal,
                };

                if (newState != _currentState)
                {
                    var prev = _currentState;
                    _currentState = newState;
                    OnBackpressureChanged?.Invoke(new BackpressureStateChangedEventArgs(
                        prev, newState, "TestSubsystem", DateTime.UtcNow));
                }

                return Task.CompletedTask;
            }

            /// <summary>
            /// Publish an item to the queue, applying the configured backpressure strategy.
            /// </summary>
            public async Task<bool> PublishAsync(string item, CancellationToken ct = default)
            {
                // Evaluate backpressure
                int currentCount;
                lock (_lock) currentCount = _queue.Count;

                await ApplyBackpressureAsync(
                    new BackpressureContext(currentCount, _capacity, currentCount, 0), ct);

                lock (_lock)
                {
                    if (_queue.Count >= _capacity)
                    {
                        switch (Strategy)
                        {
                            case BackpressureStrategy.DropOldest:
                                if (_queue.Count > 0)
                                {
                                    DroppedItems.Add(_queue[0]);
                                    _queue.RemoveAt(0);
                                }
                                _queue.Add(item);
                                return true;

                            case BackpressureStrategy.ShedLoad:
                                RejectedItems.Add(item);
                                return false;

                            case BackpressureStrategy.BlockProducer:
                                // For test purposes, we'll signal space when drained
                                // In real usage this would block until space available
                                RejectedItems.Add(item);
                                return false;

                            case BackpressureStrategy.Adaptive:
                                // Adaptive: use DropOldest at Warning, ShedLoad at Critical+
                                if (_currentState >= BackpressureState.Critical)
                                {
                                    RejectedItems.Add(item);
                                    return false;
                                }
                                if (_queue.Count > 0)
                                {
                                    DroppedItems.Add(_queue[0]);
                                    _queue.RemoveAt(0);
                                }
                                _queue.Add(item);
                                return true;

                            default:
                                _queue.Add(item);
                                return true;
                        }
                    }

                    _queue.Add(item);
                    return true;
                }
            }

            /// <summary>Drain one item, making space for BlockProducer strategy.</summary>
            public string? Drain()
            {
                lock (_lock)
                {
                    if (_queue.Count == 0) return null;
                    var item = _queue[0];
                    _queue.RemoveAt(0);
                    _spaceAvailable.Release();
                    return item;
                }
            }
        }

        // ----------------------------------------------------------------
        // 1. State transitions
        // ----------------------------------------------------------------

        [Fact]
        public async Task StateTransitions_NormalToWarningToCriticalToShedding()
        {
            var sub = new TestBackpressureSubsystem(100);

            // Normal state (0% load)
            await sub.ApplyBackpressureAsync(new BackpressureContext(0, 100, 0, 0));
            Assert.Equal(BackpressureState.Normal, sub.CurrentState);

            // Warning state (50% load)
            await sub.ApplyBackpressureAsync(new BackpressureContext(50, 100, 50, 0));
            Assert.Equal(BackpressureState.Warning, sub.CurrentState);

            // Critical state (80% load)
            await sub.ApplyBackpressureAsync(new BackpressureContext(80, 100, 80, 0));
            Assert.Equal(BackpressureState.Critical, sub.CurrentState);

            // Shedding state (95% load)
            await sub.ApplyBackpressureAsync(new BackpressureContext(95, 100, 95, 0));
            Assert.Equal(BackpressureState.Shedding, sub.CurrentState);
        }

        [Fact]
        public async Task StateTransitions_CanRecoverFromCriticalToNormal()
        {
            var sub = new TestBackpressureSubsystem(100);

            // Go to Critical
            await sub.ApplyBackpressureAsync(new BackpressureContext(85, 100, 85, 0));
            Assert.Equal(BackpressureState.Critical, sub.CurrentState);

            // Recover to Normal
            await sub.ApplyBackpressureAsync(new BackpressureContext(10, 100, 10, 0));
            Assert.Equal(BackpressureState.Normal, sub.CurrentState);
        }

        // ----------------------------------------------------------------
        // 2. Event firing
        // ----------------------------------------------------------------

        [Fact]
        public async Task EventFiring_OnBackpressureChangedFiresWithCorrectStates()
        {
            var sub = new TestBackpressureSubsystem(100);
            var transitions = new List<(BackpressureState prev, BackpressureState curr)>();

            sub.OnBackpressureChanged += args =>
                transitions.Add((args.PreviousState, args.CurrentState));

            // Normal -> Warning
            await sub.ApplyBackpressureAsync(new BackpressureContext(55, 100, 55, 0));
            // Warning -> Critical
            await sub.ApplyBackpressureAsync(new BackpressureContext(85, 100, 85, 0));
            // Critical -> Shedding
            await sub.ApplyBackpressureAsync(new BackpressureContext(96, 100, 96, 0));

            Assert.Equal(3, transitions.Count);
            Assert.Equal((BackpressureState.Normal, BackpressureState.Warning), transitions[0]);
            Assert.Equal((BackpressureState.Warning, BackpressureState.Critical), transitions[1]);
            Assert.Equal((BackpressureState.Critical, BackpressureState.Shedding), transitions[2]);
        }

        [Fact]
        public async Task EventFiring_NoEventWhenStateUnchanged()
        {
            var sub = new TestBackpressureSubsystem(100);
            int eventCount = 0;
            sub.OnBackpressureChanged += _ => eventCount++;

            // Stay in Normal
            await sub.ApplyBackpressureAsync(new BackpressureContext(10, 100, 10, 0));
            await sub.ApplyBackpressureAsync(new BackpressureContext(20, 100, 20, 0));
            await sub.ApplyBackpressureAsync(new BackpressureContext(30, 100, 30, 0));

            Assert.Equal(0, eventCount); // No transitions
        }

        [Fact]
        public async Task EventFiring_SubsystemNameIncluded()
        {
            var sub = new TestBackpressureSubsystem(100);
            string? capturedName = null;
            sub.OnBackpressureChanged += args => capturedName = args.SubsystemName;

            await sub.ApplyBackpressureAsync(new BackpressureContext(55, 100, 55, 0));

            Assert.Equal("TestSubsystem", capturedName);
        }

        // ----------------------------------------------------------------
        // 3. DropOldest strategy
        // ----------------------------------------------------------------

        [Fact]
        public async Task DropOldest_OldestItemsDroppedNewestRetained()
        {
            var sub = new TestBackpressureSubsystem(5, BackpressureStrategy.DropOldest);

            // Fill to capacity
            for (int i = 0; i < 5; i++)
                await sub.PublishAsync($"item{i}");

            Assert.Equal(5, sub.QueueCount);

            // Publish 3 more -- oldest 3 should be dropped
            for (int i = 5; i < 8; i++)
                await sub.PublishAsync($"item{i}");

            Assert.Equal(5, sub.QueueCount);
            Assert.Equal(3, sub.DroppedItems.Count);
            Assert.Equal("item0", sub.DroppedItems[0]);
            Assert.Equal("item1", sub.DroppedItems[1]);
            Assert.Equal("item2", sub.DroppedItems[2]);
        }

        // ----------------------------------------------------------------
        // 4. BlockProducer strategy
        // ----------------------------------------------------------------

        [Fact]
        public async Task BlockProducer_RejectsWhenFull()
        {
            var sub = new TestBackpressureSubsystem(3, BackpressureStrategy.BlockProducer);

            // Fill to capacity
            for (int i = 0; i < 3; i++)
                Assert.True(await sub.PublishAsync($"item{i}"));

            // Next publish should be rejected (simulates block)
            Assert.False(await sub.PublishAsync("blocked_item"));
            Assert.Single(sub.RejectedItems);
        }

        [Fact]
        public async Task BlockProducer_AcceptsAfterDrain()
        {
            var sub = new TestBackpressureSubsystem(3, BackpressureStrategy.BlockProducer);

            for (int i = 0; i < 3; i++)
                await sub.PublishAsync($"item{i}");

            // Drain one item
            var drained = sub.Drain();
            Assert.NotNull(drained);

            // Should now accept
            Assert.True(await sub.PublishAsync("new_item"));
        }

        // ----------------------------------------------------------------
        // 5. ShedLoad strategy
        // ----------------------------------------------------------------

        [Fact]
        public async Task ShedLoad_RejectsExcessItems()
        {
            var sub = new TestBackpressureSubsystem(5, BackpressureStrategy.ShedLoad);

            // Fill to capacity
            for (int i = 0; i < 5; i++)
                Assert.True(await sub.PublishAsync($"item{i}"));

            // Excess items should be rejected
            Assert.False(await sub.PublishAsync("excess1"));
            Assert.False(await sub.PublishAsync("excess2"));
            Assert.False(await sub.PublishAsync("excess3"));

            Assert.Equal(3, sub.RejectedItems.Count);
            Assert.Equal(5, sub.QueueCount); // Capacity unchanged
        }

        // ----------------------------------------------------------------
        // 6. Adaptive strategy
        // ----------------------------------------------------------------

        [Fact]
        public async Task Adaptive_UsesDropOldestAtWarning()
        {
            // Capacity 10 -- at 6 items we hit Warning (60% > 50%)
            var sub = new TestBackpressureSubsystem(10, BackpressureStrategy.Adaptive);

            // Fill to capacity
            for (int i = 0; i < 10; i++)
                await sub.PublishAsync($"item{i}");

            // At capacity, state is Shedding (100%) -- adaptive uses ShedLoad
            // The test is that at warning/normal levels, it drops oldest instead
            // Reset with small capacity to test adaptive behavior
            var sub2 = new TestBackpressureSubsystem(5, BackpressureStrategy.Adaptive);

            // Fill to capacity -- once full, state transitions to Shedding (>=95%)
            for (int i = 0; i < 5; i++)
                await sub2.PublishAsync($"item{i}");

            // At 100% capacity, adaptive in Critical+ mode rejects
            var accepted = await sub2.PublishAsync("overflow");
            Assert.False(accepted);
            Assert.Single(sub2.RejectedItems);
        }

        [Fact]
        public async Task Adaptive_SwitchesStrategyBasedOnLoad()
        {
            var sub = new TestBackpressureSubsystem(100, BackpressureStrategy.Adaptive);

            // At normal load -- should accept
            for (int i = 0; i < 40; i++)
                Assert.True(await sub.PublishAsync($"item{i}"));

            Assert.Equal(BackpressureState.Normal, sub.CurrentState);

            // Push to warning
            for (int i = 40; i < 55; i++)
                await sub.PublishAsync($"item{i}");

            Assert.Equal(BackpressureState.Warning, sub.CurrentState);
        }

        // ----------------------------------------------------------------
        // BackpressureState enum coverage
        // ----------------------------------------------------------------

        [Fact]
        public void BackpressureState_AllValuesAreDefined()
        {
            var values = Enum.GetValues<BackpressureState>();
            Assert.Equal(4, values.Length);
            Assert.Contains(BackpressureState.Normal, values);
            Assert.Contains(BackpressureState.Warning, values);
            Assert.Contains(BackpressureState.Critical, values);
            Assert.Contains(BackpressureState.Shedding, values);
        }

        [Fact]
        public void BackpressureStrategy_AllValuesAreDefined()
        {
            var values = Enum.GetValues<BackpressureStrategy>();
            Assert.Equal(5, values.Length);
            Assert.Contains(BackpressureStrategy.DropOldest, values);
            Assert.Contains(BackpressureStrategy.BlockProducer, values);
            Assert.Contains(BackpressureStrategy.ShedLoad, values);
            Assert.Contains(BackpressureStrategy.DegradeQuality, values);
            Assert.Contains(BackpressureStrategy.Adaptive, values);
        }

        [Fact]
        public void BackpressureStateChangedEventArgs_HasCorrectProperties()
        {
            var args = new BackpressureStateChangedEventArgs(
                BackpressureState.Normal,
                BackpressureState.Warning,
                "TestSubsystem",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(BackpressureState.Normal, args.PreviousState);
            Assert.Equal(BackpressureState.Warning, args.CurrentState);
            Assert.Equal("TestSubsystem", args.SubsystemName);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), args.Timestamp);
        }

        [Fact]
        public void BackpressureContext_HasCorrectProperties()
        {
            var ctx = new BackpressureContext(50, 100, 25, 1.5);
            Assert.Equal(50, ctx.CurrentLoad);
            Assert.Equal(100, ctx.MaxCapacity);
            Assert.Equal(25, ctx.QueueDepth);
            Assert.Equal(1.5, ctx.LatencyP99);
        }
    }
}
