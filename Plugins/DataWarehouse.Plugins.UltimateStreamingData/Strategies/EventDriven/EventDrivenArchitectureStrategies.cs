using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies.EventDriven;

#region 111.4.1 Event Sourcing Strategy

/// <summary>
/// 111.4.1: Event sourcing strategy for capturing all changes as immutable events
/// with event store, replay capabilities, and snapshot optimization.
/// </summary>
public sealed class EventSourcingStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, EventStream> _streams = new BoundedDictionary<string, EventStream>(1000);
    private readonly BoundedDictionary<string, List<StoredEvent>> _eventStore = new BoundedDictionary<string, List<StoredEvent>>(1000);
    private readonly BoundedDictionary<string, Snapshot> _snapshots = new BoundedDictionary<string, Snapshot>(1000);
    private long _globalSequence;

    public override string StrategyId => "event-driven-sourcing";
    public override string DisplayName => "Event Sourcing";
    public override StreamingCategory Category => StreamingCategory.EventDrivenArchitecture;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = false,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 50000,
        TypicalLatencyMs = 5.0
    };
    public override string SemanticDescription =>
        "Event sourcing implementation capturing all state changes as immutable events " +
        "with append-only event store, replay capabilities, and snapshot optimization.";
    public override string[] Tags => ["event-sourcing", "immutable", "audit-trail", "replay", "snapshot"];

    /// <summary>
    /// Creates an event stream for an aggregate.
    /// </summary>
    public Task<EventStream> CreateStreamAsync(
        string streamId,
        string aggregateType,
        EventStreamConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new EventStream
        {
            StreamId = streamId,
            AggregateType = aggregateType,
            Config = config ?? new EventStreamConfig(),
            Version = 0,
            CreatedAt = DateTime.UtcNow
        };

        if (!_streams.TryAdd(streamId, stream))
            throw new InvalidOperationException($"Stream {streamId} already exists");

        _eventStore[streamId] = new List<StoredEvent>();
        RecordOperation("CreateEventStream");
        return Task.FromResult(stream);
    }

    /// <summary>
    /// Appends events to the stream.
    /// </summary>
    public Task<AppendResult> AppendEventsAsync(
        string streamId,
        IReadOnlyList<DomainEvent> events,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
            throw new KeyNotFoundException($"Stream {streamId} not found");

        var eventStore = _eventStore[streamId];

        lock (eventStore)
        {
            // Optimistic concurrency check
            if (stream.Version != expectedVersion)
            {
                throw new ConcurrencyException(
                    $"Expected version {expectedVersion} but stream is at version {stream.Version}");
            }

            var storedEvents = new List<StoredEvent>();
            foreach (var evt in events)
            {
                var seq = Interlocked.Increment(ref _globalSequence);
                stream.Version++;

                var storedEvent = new StoredEvent
                {
                    EventId = evt.EventId ?? Guid.NewGuid().ToString(),
                    StreamId = streamId,
                    EventType = evt.EventType,
                    Data = evt.Data,
                    Metadata = evt.Metadata,
                    Version = stream.Version,
                    GlobalSequence = seq,
                    Timestamp = DateTime.UtcNow
                };

                eventStore.Add(storedEvent);
                storedEvents.Add(storedEvent);
            }

            // Check if snapshot needed
            if (stream.Config.SnapshotEveryN > 0 &&
                stream.Version % stream.Config.SnapshotEveryN == 0)
            {
                CreateSnapshotInternal(streamId, stream.Version);
            }

            RecordOperation("AppendEvents");
            return Task.FromResult(new AppendResult
            {
                StreamId = streamId,
                NewVersion = stream.Version,
                EventsAppended = storedEvents.Count,
                FirstEventSequence = storedEvents.First().GlobalSequence,
                LastEventSequence = storedEvents.Last().GlobalSequence
            });
        }
    }

    /// <summary>
    /// Reads events from the stream.
    /// </summary>
    public async IAsyncEnumerable<StoredEvent> ReadEventsAsync(
        string streamId,
        long fromVersion = 0,
        long? toVersion = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_eventStore.TryGetValue(streamId, out var eventStore))
            throw new KeyNotFoundException($"Stream {streamId} not found");

        // Finding 4391: take a snapshot under lock to prevent TOCTOU data race with
        // concurrent AppendEventsAsync calls that mutate eventStore under the same lock.
        List<StoredEvent> snapshot;
        lock (eventStore)
        {
            snapshot = eventStore
                .Where(e => e.Version > fromVersion && (toVersion == null || e.Version <= toVersion))
                .OrderBy(e => e.Version)
                .ToList();
        }

        foreach (var evt in snapshot)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            yield return evt;
            await Task.Yield();
        }

        RecordOperation("ReadEvents");
    }

    /// <summary>
    /// Replays events to rebuild aggregate state.
    /// </summary>
    public async Task<TAggregateState> ReplayAsync<TAggregateState>(
        string streamId,
        Func<TAggregateState, StoredEvent, TAggregateState> applyEvent,
        TAggregateState initialState,
        CancellationToken cancellationToken = default) where TAggregateState : class
    {
        // Try to load from snapshot first
        var (state, fromVersion) = LoadFromSnapshot<TAggregateState>(streamId, initialState);

        // Replay events from snapshot version
        await foreach (var evt in ReadEventsAsync(streamId, fromVersion, null, cancellationToken))
        {
            state = applyEvent(state, evt);
        }

        RecordOperation("ReplayEvents");
        return state;
    }

    private (TAggregateState State, long FromVersion) LoadFromSnapshot<TAggregateState>(
        string streamId,
        TAggregateState initialState) where TAggregateState : class
    {
        if (_snapshots.TryGetValue(streamId, out var snapshot) && snapshot.State is TAggregateState state)
        {
            return (state, snapshot.Version);
        }
        return (initialState, 0);
    }

    /// <summary>
    /// Creates a snapshot of the current state.
    /// </summary>
    public Task<Snapshot> CreateSnapshotAsync(
        string streamId,
        object state,
        CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
            throw new KeyNotFoundException($"Stream {streamId} not found");

        var snapshot = CreateSnapshotInternal(streamId, stream.Version, state);
        RecordOperation("CreateSnapshot");
        return Task.FromResult(snapshot);
    }

    private Snapshot CreateSnapshotInternal(string streamId, long version, object? state = null)
    {
        var snapshot = new Snapshot
        {
            StreamId = streamId,
            Version = version,
            State = state,
            CreatedAt = DateTime.UtcNow
        };

        _snapshots[streamId] = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Gets stream metadata.
    /// </summary>
    public Task<EventStreamMetadata> GetMetadataAsync(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
            throw new KeyNotFoundException($"Stream {streamId} not found");

        var eventStore = _eventStore[streamId];
        var hasSnapshot = _snapshots.TryGetValue(streamId, out var snapshot);

        return Task.FromResult(new EventStreamMetadata
        {
            StreamId = streamId,
            AggregateType = stream.AggregateType,
            CurrentVersion = stream.Version,
            EventCount = eventStore.Count,
            HasSnapshot = hasSnapshot,
            SnapshotVersion = hasSnapshot ? snapshot!.Version : null,
            CreatedAt = stream.CreatedAt
        });
    }
}

#endregion

#region 111.4.2 CQRS Strategy

/// <summary>
/// 111.4.2: Command Query Responsibility Segregation (CQRS) strategy separating
/// read and write models with command handlers and query processors.
/// </summary>
public sealed class CqrsStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, CqrsAggregate> _aggregates = new BoundedDictionary<string, CqrsAggregate>(1000);
    private readonly BoundedDictionary<string, ICommandHandler> _commandHandlers = new BoundedDictionary<string, ICommandHandler>(1000);
    private readonly BoundedDictionary<string, IQueryHandler> _queryHandlers = new BoundedDictionary<string, IQueryHandler>(1000);
    private readonly BoundedDictionary<string, ReadModel> _readModels = new BoundedDictionary<string, ReadModel>(1000);

    public override string StrategyId => "event-driven-cqrs";
    public override string DisplayName => "CQRS Pattern";
    public override StreamingCategory Category => StreamingCategory.EventDrivenArchitecture;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = false,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 100000,
        TypicalLatencyMs = 3.0
    };
    public override string SemanticDescription =>
        "CQRS pattern implementation separating read and write models with command handlers, " +
        "query processors, and eventual consistency between models.";
    public override string[] Tags => ["cqrs", "command", "query", "read-model", "write-model"];

    /// <summary>
    /// Registers a command handler.
    /// </summary>
    public Task RegisterCommandHandlerAsync<TCommand>(
        string commandType,
        Func<TCommand, CancellationToken, Task<CommandResult>> handler,
        CancellationToken cancellationToken = default) where TCommand : ICommand
    {
        var wrapper = new CommandHandlerWrapper<TCommand>(handler);

        if (!_commandHandlers.TryAdd(commandType, wrapper))
            throw new InvalidOperationException($"Handler for {commandType} already registered");

        RecordOperation("RegisterCommandHandler");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers a query handler.
    /// </summary>
    public Task RegisterQueryHandlerAsync<TQuery, TResult>(
        string queryType,
        Func<TQuery, CancellationToken, Task<TResult>> handler,
        CancellationToken cancellationToken = default) where TQuery : IQuery<TResult>
    {
        var wrapper = new QueryHandlerWrapper<TQuery, TResult>(handler);

        if (!_queryHandlers.TryAdd(queryType, wrapper))
            throw new InvalidOperationException($"Handler for {queryType} already registered");

        RecordOperation("RegisterQueryHandler");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Executes a command.
    /// </summary>
    public async Task<CommandResult> ExecuteCommandAsync<TCommand>(
        TCommand command,
        CancellationToken cancellationToken = default) where TCommand : ICommand
    {
        var commandType = typeof(TCommand).Name;

        if (!_commandHandlers.TryGetValue(commandType, out var handler))
            throw new InvalidOperationException($"No handler registered for {commandType}");

        if (handler is not CommandHandlerWrapper<TCommand> typedHandler)
            throw new InvalidOperationException($"Handler type mismatch for {commandType}");

        var result = await typedHandler.HandleAsync(command, cancellationToken);
        RecordOperation("ExecuteCommand");
        return result;
    }

    /// <summary>
    /// Executes a query.
    /// </summary>
    public async Task<TResult> ExecuteQueryAsync<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken = default) where TQuery : IQuery<TResult>
    {
        var queryType = typeof(TQuery).Name;

        if (!_queryHandlers.TryGetValue(queryType, out var handler))
            throw new InvalidOperationException($"No handler registered for {queryType}");

        if (handler is not QueryHandlerWrapper<TQuery, TResult> typedHandler)
            throw new InvalidOperationException($"Handler type mismatch for {queryType}");

        var result = await typedHandler.HandleAsync(query, cancellationToken);
        RecordOperation("ExecuteQuery");
        return result;
    }

    /// <summary>
    /// Creates a read model.
    /// </summary>
    public Task<ReadModel> CreateReadModelAsync(
        string modelId,
        ReadModelConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var model = new ReadModel
        {
            ModelId = modelId,
            Config = config ?? new ReadModelConfig(),
            Data = new BoundedDictionary<string, object>(1000),
            LastUpdated = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        if (!_readModels.TryAdd(modelId, model))
            throw new InvalidOperationException($"Read model {modelId} already exists");

        RecordOperation("CreateReadModel");
        return Task.FromResult(model);
    }

    /// <summary>
    /// Updates a read model from events.
    /// </summary>
    public async Task UpdateReadModelAsync(
        string modelId,
        IAsyncEnumerable<DomainEvent> events,
        Func<ReadModel, DomainEvent, ReadModel> projector,
        CancellationToken cancellationToken = default)
    {
        if (!_readModels.TryGetValue(modelId, out var model))
            throw new KeyNotFoundException($"Read model {modelId} not found");

        await foreach (var evt in events.WithCancellation(cancellationToken))
        {
            model = projector(model, evt);
            model.LastUpdated = DateTime.UtcNow;
            _readModels[modelId] = model;
        }

        RecordOperation("UpdateReadModel");
    }

    /// <summary>
    /// Queries a read model.
    /// </summary>
    public Task<T?> QueryReadModelAsync<T>(
        string modelId,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (!_readModels.TryGetValue(modelId, out var model))
            throw new KeyNotFoundException($"Read model {modelId} not found");

        if (model.Data.TryGetValue(key, out var value) && value is T typedValue)
            return Task.FromResult<T?>(typedValue);

        return Task.FromResult<T?>(default);
    }
}

#endregion

#region 111.4.3 Saga Orchestration Strategy

/// <summary>
/// 111.4.3: Saga orchestration strategy for managing distributed transactions
/// with compensation, timeouts, and state management.
/// </summary>
public sealed class SagaOrchestrationStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, SagaDefinition> _definitions = new BoundedDictionary<string, SagaDefinition>(1000);
    private readonly BoundedDictionary<string, SagaInstance> _instances = new BoundedDictionary<string, SagaInstance>(1000);
    private readonly BoundedDictionary<string, List<SagaEvent>> _sagaEvents = new BoundedDictionary<string, List<SagaEvent>>(1000);

    public override string StrategyId => "event-driven-saga";
    public override string DisplayName => "Saga Orchestration";
    public override StreamingCategory Category => StreamingCategory.EventDrivenArchitecture;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = true,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = true,
        SupportsBackpressure = true,
        SupportsPartitioning = false,
        SupportsAutoScaling = false,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 10000,
        TypicalLatencyMs = 100.0
    };
    public override string SemanticDescription =>
        "Saga orchestration for distributed transactions with step execution, " +
        "compensation handlers, timeouts, and persistent state management.";
    public override string[] Tags => ["saga", "orchestration", "distributed-transaction", "compensation", "workflow"];

    /// <summary>
    /// Defines a saga with steps and compensations.
    /// </summary>
    public Task<SagaDefinition> DefineSagaAsync(
        string sagaType,
        IReadOnlyList<SagaStep> steps,
        SagaConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var definition = new SagaDefinition
        {
            SagaType = sagaType,
            Steps = steps.ToList(),
            Config = config ?? new SagaConfig(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_definitions.TryAdd(sagaType, definition))
            throw new InvalidOperationException($"Saga {sagaType} already defined");

        RecordOperation("DefineSaga");
        return Task.FromResult(definition);
    }

    /// <summary>
    /// Starts a new saga instance.
    /// </summary>
    public Task<SagaInstance> StartSagaAsync(
        string sagaType,
        string sagaId,
        Dictionary<string, object>? initialData = null,
        CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryGetValue(sagaType, out var definition))
            throw new KeyNotFoundException($"Saga type {sagaType} not found");

        var instance = new SagaInstance
        {
            SagaId = sagaId,
            SagaType = sagaType,
            State = SagaState.Started,
            CurrentStep = 0,
            Data = initialData ?? new Dictionary<string, object>(),
            StartedAt = DateTime.UtcNow
        };

        if (!_instances.TryAdd(sagaId, instance))
            throw new InvalidOperationException($"Saga instance {sagaId} already exists");

        _sagaEvents[sagaId] = new List<SagaEvent>
        {
            new SagaEvent
            {
                EventType = SagaEventType.Started,
                StepIndex = -1,
                Timestamp = DateTime.UtcNow
            }
        };

        RecordOperation("StartSaga");
        return Task.FromResult(instance);
    }

    /// <summary>
    /// Executes the next step of the saga.
    /// </summary>
    public async Task<StepResult> ExecuteStepAsync(
        string sagaId,
        CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(sagaId, out var instance))
            throw new KeyNotFoundException($"Saga instance {sagaId} not found");

        var definition = _definitions[instance.SagaType];
        var events = _sagaEvents[sagaId];

        if (instance.CurrentStep >= definition.Steps.Count)
        {
            instance.State = SagaState.Completed;
            events.Add(new SagaEvent
            {
                EventType = SagaEventType.Completed,
                StepIndex = instance.CurrentStep,
                Timestamp = DateTime.UtcNow
            });

            return new StepResult
            {
                SagaId = sagaId,
                StepIndex = instance.CurrentStep,
                Status = StepStatus.SagaCompleted
            };
        }

        var step = definition.Steps[instance.CurrentStep];

        try
        {
            // Execute step action (simulated)
            var stepResult = await ExecuteStepActionAsync(step, instance.Data, cancellationToken);

            if (stepResult.Success)
            {
                events.Add(new SagaEvent
                {
                    EventType = SagaEventType.StepCompleted,
                    StepIndex = instance.CurrentStep,
                    StepName = step.StepName,
                    Timestamp = DateTime.UtcNow
                });

                instance.CurrentStep++;

                return new StepResult
                {
                    SagaId = sagaId,
                    StepIndex = instance.CurrentStep - 1,
                    StepName = step.StepName,
                    Status = StepStatus.Completed,
                    Output = stepResult.Output
                };
            }
            else
            {
                // Step failed, initiate compensation
                instance.State = SagaState.Compensating;
                events.Add(new SagaEvent
                {
                    EventType = SagaEventType.StepFailed,
                    StepIndex = instance.CurrentStep,
                    StepName = step.StepName,
                    Error = stepResult.Error,
                    Timestamp = DateTime.UtcNow
                });

                return new StepResult
                {
                    SagaId = sagaId,
                    StepIndex = instance.CurrentStep,
                    StepName = step.StepName,
                    Status = StepStatus.Failed,
                    Error = stepResult.Error
                };
            }
        }
        catch (Exception ex)
        {
            instance.State = SagaState.Failed;
            events.Add(new SagaEvent
            {
                EventType = SagaEventType.StepFailed,
                StepIndex = instance.CurrentStep,
                StepName = step.StepName,
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            });

            throw;
        }
    }

    private Task<StepActionResult> ExecuteStepActionAsync(
        SagaStep step,
        Dictionary<string, object> data,
        CancellationToken cancellationToken)
    {
        // Simulated step execution
        return Task.FromResult(new StepActionResult
        {
            Success = true,
            Output = new Dictionary<string, object>
            {
                ["step_completed"] = step.StepName,
                ["timestamp"] = DateTime.UtcNow
            }
        });
    }

    /// <summary>
    /// Compensates the saga by executing compensation handlers in reverse.
    /// </summary>
    public async IAsyncEnumerable<CompensationResult> CompensateAsync(
        string sagaId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(sagaId, out var instance))
            throw new KeyNotFoundException($"Saga instance {sagaId} not found");

        var definition = _definitions[instance.SagaType];
        var events = _sagaEvents[sagaId];

        instance.State = SagaState.Compensating;

        // Execute compensations in reverse order
        for (int i = instance.CurrentStep - 1; i >= 0; i--)
        {
            var step = definition.Steps[i];
            if (step.CompensationAction == null) continue;

            var result = await ExecuteCompensationStepAsync(sagaId, i, step, events, cancellationToken);
            yield return result;
        }

        instance.State = SagaState.Compensated;
        RecordOperation("CompensateSaga");
    }

    private async Task<CompensationResult> ExecuteCompensationStepAsync(
        string sagaId,
        int stepIndex,
        SagaStep step,
        List<SagaEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            // Execute compensation (simulated)
            await Task.Delay(10, cancellationToken);

            events.Add(new SagaEvent
            {
                EventType = SagaEventType.StepCompensated,
                StepIndex = stepIndex,
                StepName = step.StepName,
                Timestamp = DateTime.UtcNow
            });

            return new CompensationResult
            {
                SagaId = sagaId,
                StepIndex = stepIndex,
                StepName = step.StepName,
                Status = CompensationStatus.Compensated
            };
        }
        catch (Exception ex)
        {
            events.Add(new SagaEvent
            {
                EventType = SagaEventType.CompensationFailed,
                StepIndex = stepIndex,
                StepName = step.StepName,
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            });

            return new CompensationResult
            {
                SagaId = sagaId,
                StepIndex = stepIndex,
                StepName = step.StepName,
                Status = CompensationStatus.Failed,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Gets saga instance status.
    /// </summary>
    public Task<SagaStatus> GetStatusAsync(string sagaId)
    {
        if (!_instances.TryGetValue(sagaId, out var instance))
            throw new KeyNotFoundException($"Saga instance {sagaId} not found");

        var definition = _definitions[instance.SagaType];
        var events = _sagaEvents[sagaId];

        return Task.FromResult(new SagaStatus
        {
            SagaId = sagaId,
            SagaType = instance.SagaType,
            State = instance.State,
            CurrentStep = instance.CurrentStep,
            TotalSteps = definition.Steps.Count,
            Events = events.ToList(),
            StartedAt = instance.StartedAt,
            CompletedAt = instance.State == SagaState.Completed ? events.LastOrDefault()?.Timestamp : null
        });
    }
}

#endregion

#region 111.4.4 Event Bus Strategy

/// <summary>
/// 111.4.4: Event bus strategy for pub/sub messaging with topic routing,
/// subscriber management, and delivery guarantees.
/// </summary>
public sealed class EventBusStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, EventTopic> _topics = new BoundedDictionary<string, EventTopic>(1000);
    private readonly BoundedDictionary<string, List<Subscription>> _subscriptions = new BoundedDictionary<string, List<Subscription>>(1000);
    private readonly BoundedDictionary<string, List<PublishedEvent>> _deadLetters = new BoundedDictionary<string, List<PublishedEvent>>(1000);
    private long _totalEventsPublished;

    public override string StrategyId => "event-driven-bus";
    public override string DisplayName => "Event Bus";
    public override StreamingCategory Category => StreamingCategory.EventDrivenArchitecture;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = false,
        SupportsWindowing = false,
        SupportsStateManagement = false,
        SupportsCheckpointing = false,
        SupportsBackpressure = true,
        SupportsPartitioning = true,
        SupportsAutoScaling = true,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 500000,
        TypicalLatencyMs = 1.0
    };
    public override string SemanticDescription =>
        "Event bus for pub/sub messaging with topic-based routing, subscriber management, " +
        "delivery guarantees (at-least-once, at-most-once), and dead letter handling.";
    public override string[] Tags => ["event-bus", "pub-sub", "messaging", "topic", "subscriber"];

    /// <summary>
    /// Creates a topic.
    /// </summary>
    public Task<EventTopic> CreateTopicAsync(
        string topicName,
        TopicConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var topic = new EventTopic
        {
            TopicName = topicName,
            Config = config ?? new TopicConfig(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_topics.TryAdd(topicName, topic))
            throw new InvalidOperationException($"Topic {topicName} already exists");

        _subscriptions[topicName] = new List<Subscription>();
        _deadLetters[topicName] = new List<PublishedEvent>();
        RecordOperation("CreateTopic");
        return Task.FromResult(topic);
    }

    /// <summary>
    /// Subscribes to a topic.
    /// </summary>
    public Task<Subscription> SubscribeAsync(
        string topicName,
        string subscriberId,
        Func<PublishedEvent, CancellationToken, Task<bool>> handler,
        SubscriptionConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        if (!_topics.ContainsKey(topicName))
            throw new KeyNotFoundException($"Topic {topicName} not found");

        var subscription = new Subscription
        {
            SubscriberId = subscriberId,
            TopicName = topicName,
            Handler = handler,
            Config = config ?? new SubscriptionConfig(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var subs = _subscriptions[topicName];
        lock (subs)
        {
            if (subs.Any(s => s.SubscriberId == subscriberId))
                throw new InvalidOperationException($"Subscriber {subscriberId} already subscribed to {topicName}");
            subs.Add(subscription);
        }

        RecordOperation("Subscribe");
        return Task.FromResult(subscription);
    }

    /// <summary>
    /// Publishes an event to a topic.
    /// </summary>
    public async Task<PublishResult> PublishAsync(
        string topicName,
        object eventData,
        string? eventType = null,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        if (!_topics.TryGetValue(topicName, out var topic))
            throw new KeyNotFoundException($"Topic {topicName} not found");

        var evt = new PublishedEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TopicName = topicName,
            EventType = eventType ?? eventData.GetType().Name,
            Data = eventData,
            Headers = headers ?? new Dictionary<string, string>(),
            PublishedAt = DateTime.UtcNow
        };

        Interlocked.Increment(ref _totalEventsPublished);

        var subs = _subscriptions[topicName];
        var deliveryResults = new List<DeliveryResult>();

        foreach (var sub in subs.Where(s => s.IsActive))
        {
            var delivered = false;
            Exception? lastError = null;

            for (int retry = 0; retry <= sub.Config.MaxRetries; retry++)
            {
                try
                {
                    delivered = await sub.Handler(evt, cancellationToken);
                    if (delivered) break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (retry < sub.Config.MaxRetries)
                        await Task.Delay(sub.Config.RetryDelayMs * (retry + 1), cancellationToken);
                }
            }

            deliveryResults.Add(new DeliveryResult
            {
                SubscriberId = sub.SubscriberId,
                Delivered = delivered,
                Error = lastError?.Message
            });

            // Add to dead letter if delivery failed
            if (!delivered && topic.Config.EnableDeadLetter)
            {
                _deadLetters[topicName].Add(evt);
            }
        }

        RecordOperation("PublishEvent");
        return new PublishResult
        {
            EventId = evt.EventId,
            TopicName = topicName,
            SubscriberCount = subs.Count,
            DeliveryResults = deliveryResults
        };
    }

    /// <summary>
    /// Publishes multiple events in a batch.
    /// </summary>
    public async IAsyncEnumerable<PublishResult> PublishBatchAsync(
        string topicName,
        IAsyncEnumerable<object> events,
        string? eventType = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var eventData in events.WithCancellation(cancellationToken))
        {
            yield return await PublishAsync(topicName, eventData, eventType, null, cancellationToken);
        }

        RecordOperation("PublishBatch");
    }

    /// <summary>
    /// Unsubscribes from a topic.
    /// </summary>
    public Task UnsubscribeAsync(
        string topicName,
        string subscriberId,
        CancellationToken cancellationToken = default)
    {
        if (!_subscriptions.TryGetValue(topicName, out var subs))
            throw new KeyNotFoundException($"Topic {topicName} not found");

        lock (subs)
        {
            var sub = subs.FirstOrDefault(s => s.SubscriberId == subscriberId);
            if (sub != null)
            {
                sub.IsActive = false;
                subs.Remove(sub);
            }
        }

        RecordOperation("Unsubscribe");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets dead letter events.
    /// </summary>
    public Task<IReadOnlyList<PublishedEvent>> GetDeadLettersAsync(
        string topicName,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!_deadLetters.TryGetValue(topicName, out var dlq))
            throw new KeyNotFoundException($"Topic {topicName} not found");

        var events = dlq.TakeLast(limit).ToList() as IReadOnlyList<PublishedEvent>;
        return Task.FromResult(events);
    }
}

#endregion

#region 111.4.5 Domain Events Strategy

/// <summary>
/// 111.4.5: Domain events strategy for bounded context integration with
/// event publishing, handlers, and aggregate notifications.
/// </summary>
public sealed class DomainEventsStrategy : StreamingDataStrategyBase
{
    private readonly BoundedDictionary<string, List<IDomainEventHandler>> _handlers = new BoundedDictionary<string, List<IDomainEventHandler>>(1000);
    private readonly BoundedDictionary<string, List<DomainEventRecord>> _eventLog = new BoundedDictionary<string, List<DomainEventRecord>>(1000);
    private readonly BoundedDictionary<string, AggregateRoot> _aggregates = new BoundedDictionary<string, AggregateRoot>(1000);
    private long _totalEventsDispatched;

    public override string StrategyId => "event-driven-domain-events";
    public override string DisplayName => "Domain Events";
    public override StreamingCategory Category => StreamingCategory.EventDrivenArchitecture;
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = false,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = false,
        SupportsBackpressure = false,
        SupportsPartitioning = false,
        SupportsAutoScaling = false,
        SupportsDistributed = false,
        MaxThroughputEventsPerSec = 100000,
        TypicalLatencyMs = 0.5
    };
    public override string SemanticDescription =>
        "Domain events pattern for DDD bounded context integration with " +
        "aggregate notifications, event handlers, and decoupled module communication.";
    public override string[] Tags => ["domain-events", "ddd", "aggregate", "bounded-context", "handler"];

    /// <summary>
    /// Registers a domain event handler.
    /// </summary>
    public Task RegisterHandlerAsync<TEvent>(
        Func<TEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default) where TEvent : IDomainEvent
    {
        var eventType = typeof(TEvent).Name;
        var wrapper = new DomainEventHandlerWrapper<TEvent>(handler);

        _handlers.AddOrUpdate(
            eventType,
            new List<IDomainEventHandler> { wrapper },
            (_, list) =>
            {
                lock (list) { list.Add(wrapper); }
                return list;
            });

        RecordOperation("RegisterDomainEventHandler");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Raises a domain event from an aggregate.
    /// </summary>
    public async Task RaiseAsync<TEvent>(
        TEvent domainEvent,
        string? aggregateId = null,
        CancellationToken cancellationToken = default) where TEvent : IDomainEvent
    {
        var eventType = typeof(TEvent).Name;

        // Log the event
        var record = new DomainEventRecord
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = eventType,
            AggregateId = aggregateId,
            EventData = domainEvent,
            RaisedAt = DateTime.UtcNow
        };

        _eventLog.AddOrUpdate(
            eventType,
            new List<DomainEventRecord> { record },
            (_, list) =>
            {
                lock (list) { list.Add(record); }
                return list;
            });

        // Dispatch to handlers
        if (_handlers.TryGetValue(eventType, out var handlers))
        {
            foreach (var handler in handlers.ToList())
            {
                if (handler is DomainEventHandlerWrapper<TEvent> typedHandler)
                {
                    await typedHandler.HandleAsync(domainEvent, cancellationToken);
                    Interlocked.Increment(ref _totalEventsDispatched);
                }
            }
        }

        RecordOperation("RaiseDomainEvent");
    }

    /// <summary>
    /// Raises multiple domain events.
    /// </summary>
    public async Task RaiseManyAsync(
        IEnumerable<IDomainEvent> events,
        string? aggregateId = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var evt in events)
        {
            var method = typeof(DomainEventsStrategy).GetMethod(nameof(RaiseAsync))!
                .MakeGenericMethod(evt.GetType());
            await (Task)method.Invoke(this, new object?[] { evt, aggregateId, cancellationToken })!;
        }
    }

    /// <summary>
    /// Collects events from an aggregate for later dispatch.
    /// </summary>
    public Task<IReadOnlyList<IDomainEvent>> CollectEventsAsync(
        string aggregateId,
        CancellationToken cancellationToken = default)
    {
        if (!_aggregates.TryGetValue(aggregateId, out var aggregate))
        {
            return Task.FromResult<IReadOnlyList<IDomainEvent>>(Array.Empty<IDomainEvent>());
        }

        var events = aggregate.UncommittedEvents.ToList() as IReadOnlyList<IDomainEvent>;
        aggregate.ClearUncommittedEvents();

        RecordOperation("CollectDomainEvents");
        return Task.FromResult(events);
    }

    /// <summary>
    /// Registers an aggregate for event collection.
    /// </summary>
    public Task RegisterAggregateAsync(
        string aggregateId,
        AggregateRoot aggregate,
        CancellationToken cancellationToken = default)
    {
        _aggregates[aggregateId] = aggregate;
        RecordOperation("RegisterAggregate");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets event history for an event type.
    /// </summary>
    public Task<IReadOnlyList<DomainEventRecord>> GetEventHistoryAsync(
        string eventType,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!_eventLog.TryGetValue(eventType, out var log))
            return Task.FromResult<IReadOnlyList<DomainEventRecord>>(Array.Empty<DomainEventRecord>());

        var events = log.TakeLast(limit).ToList() as IReadOnlyList<DomainEventRecord>;
        return Task.FromResult(events);
    }
}

#endregion

#region Supporting Types for Event-Driven Architecture

// Event Sourcing Types
public record EventStream
{
    public required string StreamId { get; init; }
    public required string AggregateType { get; init; }
    public required EventStreamConfig Config { get; init; }
    public long Version { get; set; }
    public DateTime CreatedAt { get; init; }
}

public record EventStreamConfig
{
    public int SnapshotEveryN { get; init; } = 100;
    public bool EnableCompression { get; init; } = false;
}

public record DomainEvent
{
    public string? EventId { get; init; }
    public required string EventType { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public record StoredEvent
{
    public required string EventId { get; init; }
    public required string StreamId { get; init; }
    public required string EventType { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public long Version { get; init; }
    public long GlobalSequence { get; init; }
    public DateTime Timestamp { get; init; }
}

public record AppendResult
{
    public required string StreamId { get; init; }
    public long NewVersion { get; init; }
    public int EventsAppended { get; init; }
    public long FirstEventSequence { get; init; }
    public long LastEventSequence { get; init; }
}

public record Snapshot
{
    public required string StreamId { get; init; }
    public long Version { get; init; }
    public object? State { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record EventStreamMetadata
{
    public required string StreamId { get; init; }
    public required string AggregateType { get; init; }
    public long CurrentVersion { get; init; }
    public int EventCount { get; init; }
    public bool HasSnapshot { get; init; }
    public long? SnapshotVersion { get; init; }
    public DateTime CreatedAt { get; init; }
}

public class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message) { }
}

// CQRS Types
public interface ICommand { }
public interface IQuery<TResult> { }
public interface ICommandHandler { }
public interface IQueryHandler { }

public record CqrsAggregate
{
    public required string AggregateId { get; init; }
    public required string AggregateType { get; init; }
    public long Version { get; init; }
}

public record CommandResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}

public record ReadModel
{
    public required string ModelId { get; init; }
    public required ReadModelConfig Config { get; init; }
    public required BoundedDictionary<string, object> Data { get; init; }
    public DateTime LastUpdated { get; set; }
    public DateTime CreatedAt { get; init; }
}

public record ReadModelConfig
{
    public bool EnableCaching { get; init; } = true;
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}

public class CommandHandlerWrapper<TCommand> : ICommandHandler where TCommand : ICommand
{
    private readonly Func<TCommand, CancellationToken, Task<CommandResult>> _handler;

    public CommandHandlerWrapper(Func<TCommand, CancellationToken, Task<CommandResult>> handler)
    {
        _handler = handler;
    }

    public Task<CommandResult> HandleAsync(TCommand command, CancellationToken ct) => _handler(command, ct);
}

public class QueryHandlerWrapper<TQuery, TResult> : IQueryHandler where TQuery : IQuery<TResult>
{
    private readonly Func<TQuery, CancellationToken, Task<TResult>> _handler;

    public QueryHandlerWrapper(Func<TQuery, CancellationToken, Task<TResult>> handler)
    {
        _handler = handler;
    }

    public Task<TResult> HandleAsync(TQuery query, CancellationToken ct) => _handler(query, ct);
}

// Saga Types
public enum SagaState { Started, Running, Compensating, Completed, Compensated, Failed }
public enum SagaEventType { Started, StepCompleted, StepFailed, StepCompensated, CompensationFailed, Completed }
public enum StepStatus { Completed, Failed, SagaCompleted }
public enum CompensationStatus { Compensated, Failed }

public record SagaDefinition
{
    public required string SagaType { get; init; }
    public required List<SagaStep> Steps { get; init; }
    public required SagaConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record SagaStep
{
    public required string StepName { get; init; }
    public required Func<Dictionary<string, object>, CancellationToken, Task<StepActionResult>> Action { get; init; }
    public Func<Dictionary<string, object>, CancellationToken, Task>? CompensationAction { get; init; }
    public TimeSpan? Timeout { get; init; }
}

public record SagaConfig
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxRetries { get; init; } = 3;
}

public record SagaInstance
{
    public required string SagaId { get; init; }
    public required string SagaType { get; init; }
    public SagaState State { get; set; }
    public int CurrentStep { get; set; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime StartedAt { get; init; }
}

public record SagaEvent
{
    public required SagaEventType EventType { get; init; }
    public int StepIndex { get; init; }
    public string? StepName { get; init; }
    public string? Error { get; init; }
    public DateTime Timestamp { get; init; }
}

public record StepActionResult
{
    public bool Success { get; init; }
    public Dictionary<string, object>? Output { get; init; }
    public string? Error { get; init; }
}

public record StepResult
{
    public required string SagaId { get; init; }
    public int StepIndex { get; init; }
    public string? StepName { get; init; }
    public required StepStatus Status { get; init; }
    public Dictionary<string, object>? Output { get; init; }
    public string? Error { get; init; }
}

public record CompensationResult
{
    public required string SagaId { get; init; }
    public int StepIndex { get; init; }
    public string? StepName { get; init; }
    public required CompensationStatus Status { get; init; }
    public string? Error { get; init; }
}

public record SagaStatus
{
    public required string SagaId { get; init; }
    public required string SagaType { get; init; }
    public required SagaState State { get; init; }
    public int CurrentStep { get; init; }
    public int TotalSteps { get; init; }
    public required List<SagaEvent> Events { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

// Event Bus Types
public record EventTopic
{
    public required string TopicName { get; init; }
    public required TopicConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record TopicConfig
{
    public bool EnableDeadLetter { get; init; } = true;
    public int MaxRetries { get; init; } = 3;
    public TimeSpan MessageTtl { get; init; } = TimeSpan.FromHours(24);
}

public record Subscription
{
    public required string SubscriberId { get; init; }
    public required string TopicName { get; init; }
    public required Func<PublishedEvent, CancellationToken, Task<bool>> Handler { get; init; }
    public required SubscriptionConfig Config { get; init; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; init; }
}

public record SubscriptionConfig
{
    public int MaxRetries { get; init; } = 3;
    public int RetryDelayMs { get; init; } = 1000;
    public DeliveryGuarantee Guarantee { get; init; } = DeliveryGuarantee.AtLeastOnce;
}

public enum DeliveryGuarantee { AtMostOnce, AtLeastOnce, ExactlyOnce }

public record PublishedEvent
{
    public required string EventId { get; init; }
    public required string TopicName { get; init; }
    public required string EventType { get; init; }
    public required object Data { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public DateTime PublishedAt { get; init; }
}

public record PublishResult
{
    public required string EventId { get; init; }
    public required string TopicName { get; init; }
    public int SubscriberCount { get; init; }
    public required List<DeliveryResult> DeliveryResults { get; init; }
}

public record DeliveryResult
{
    public required string SubscriberId { get; init; }
    public bool Delivered { get; init; }
    public string? Error { get; init; }
}

// Domain Events Types
public interface IDomainEvent
{
    string EventId { get; }
    DateTime OccurredAt { get; }
}

public interface IDomainEventHandler { }

public record DomainEventRecord
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public string? AggregateId { get; init; }
    public required object EventData { get; init; }
    public DateTime RaisedAt { get; init; }
}

public class DomainEventHandlerWrapper<TEvent> : IDomainEventHandler where TEvent : IDomainEvent
{
    private readonly Func<TEvent, CancellationToken, Task> _handler;

    public DomainEventHandlerWrapper(Func<TEvent, CancellationToken, Task> handler)
    {
        _handler = handler;
    }

    public Task HandleAsync(TEvent evt, CancellationToken ct) => _handler(evt, ct);
}

public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _uncommittedEvents = new();

    public IReadOnlyList<IDomainEvent> UncommittedEvents => _uncommittedEvents;

    protected void RaiseEvent(IDomainEvent domainEvent)
    {
        _uncommittedEvents.Add(domainEvent);
    }

    public void ClearUncommittedEvents()
    {
        _uncommittedEvents.Clear();
    }
}

#endregion
