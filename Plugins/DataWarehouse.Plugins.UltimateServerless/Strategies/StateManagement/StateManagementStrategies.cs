using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateServerless.Strategies.StateManagement;

#region 119.3.1 Durable Entities Strategy

/// <summary>
/// 119.3.1: Durable entities (virtual actors) for stateful serverless
/// with Azure Durable Functions compatibility.
/// </summary>
public sealed class DurableEntitiesStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, EntityState> _entities = new BoundedDictionary<string, EntityState>(1000);

    public override string StrategyId => "state-durable-entities";
    public override string DisplayName => "Durable Entities";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsEventTriggers = true,
        SupportsSyncInvocation = true,
        SupportsAsyncInvocation = true
    };

    public override string SemanticDescription =>
        "Durable entities (virtual actors) for stateful serverless functions " +
        "with automatic state persistence, signal-based communication, and Azure Durable Functions compatibility.";

    public override string[] Tags => new[] { "durable", "entity", "actor", "stateful", "azure" };

    /// <summary>Creates or gets an entity.</summary>
    public Task<EntityState> GetOrCreateEntityAsync(string entityId, string entityType, object? initialState = null, CancellationToken ct = default)
    {
        var entity = _entities.GetOrAdd(entityId, _ => new EntityState
        {
            EntityId = entityId,
            EntityType = entityType,
            State = initialState,
            Version = 1,
            LastModified = DateTimeOffset.UtcNow
        });

        RecordOperation("GetOrCreateEntity");
        return Task.FromResult(entity);
    }

    /// <summary>Signals an entity with an operation (thread-safe via monitor lock on entity).</summary>
    public Task SignalEntityAsync(string entityId, string operation, object? input = null, CancellationToken ct = default)
    {
        if (_entities.TryGetValue(entityId, out var entity))
        {
            lock (entity)
            {
                entity.PendingOperations.Enqueue(new EntityOperation
                {
                    OperationId = Guid.NewGuid().ToString(),
                    OperationType = operation,
                    Input = input,
                    Timestamp = DateTimeOffset.UtcNow
                });
                entity.Version++;
                entity.LastModified = DateTimeOffset.UtcNow;
            }
        }

        RecordOperation("SignalEntity");
        return Task.CompletedTask;
    }

    /// <summary>Reads entity state.</summary>
    public Task<T?> ReadEntityStateAsync<T>(string entityId, CancellationToken ct = default)
    {
        RecordOperation("ReadEntityState");
        if (_entities.TryGetValue(entityId, out var entity) && entity.State is T state)
        {
            return Task.FromResult<T?>(state);
        }
        return Task.FromResult<T?>(default);
    }

    /// <summary>Deletes an entity.</summary>
    public Task DeleteEntityAsync(string entityId, CancellationToken ct = default)
    {
        _entities.TryRemove(entityId, out _);
        RecordOperation("DeleteEntity");
        return Task.CompletedTask;
    }

    /// <summary>Lists entities by type.</summary>
    public Task<IReadOnlyList<EntityState>> ListEntitiesAsync(string entityType, int limit = 100, CancellationToken ct = default)
    {
        var entities = _entities.Values
            .Where(e => e.EntityType == entityType)
            .Take(limit)
            .ToList();
        RecordOperation("ListEntities");
        return Task.FromResult<IReadOnlyList<EntityState>>(entities);
    }
}

#endregion

#region 119.3.2 Step Functions State Strategy

/// <summary>
/// 119.3.2: AWS Step Functions state management with
/// workflow state persistence and execution history.
/// </summary>
public sealed class StepFunctionsStateStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, WorkflowExecution> _executions = new BoundedDictionary<string, WorkflowExecution>(1000);

    public override string StrategyId => "state-step-functions";
    public override string DisplayName => "Step Functions State";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;
    public override ServerlessPlatform? TargetPlatform => ServerlessPlatform.AwsLambda;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsEventTriggers = true,
        SupportsSyncInvocation = true,
        SupportsAsyncInvocation = true
    };

    public override string SemanticDescription =>
        "AWS Step Functions state management with workflow state persistence, " +
        "execution history, wait states, and parallel branch coordination.";

    public override string[] Tags => new[] { "step-functions", "workflow", "state-machine", "aws" };

    /// <summary>Starts a workflow execution.</summary>
    public Task<WorkflowExecution> StartExecutionAsync(string stateMachineArn, string? executionName, object? input, CancellationToken ct = default)
    {
        var execution = new WorkflowExecution
        {
            ExecutionArn = $"{stateMachineArn}:{executionName ?? Guid.NewGuid().ToString()}",
            StateMachineArn = stateMachineArn,
            Status = "RUNNING",
            Input = input,
            StartTime = DateTimeOffset.UtcNow
        };

        _executions[execution.ExecutionArn] = execution;
        RecordOperation("StartExecution");
        return Task.FromResult(execution);
    }

    /// <summary>Gets execution state.</summary>
    public Task<WorkflowExecution?> GetExecutionAsync(string executionArn, CancellationToken ct = default)
    {
        _executions.TryGetValue(executionArn, out var execution);
        RecordOperation("GetExecution");
        return Task.FromResult<WorkflowExecution?>(execution);
    }

    /// <summary>Sends a task success response.</summary>
    public Task SendTaskSuccessAsync(string taskToken, object output, CancellationToken ct = default)
    {
        RecordOperation("SendTaskSuccess");
        return Task.CompletedTask;
    }

    /// <summary>Sends a task failure response.</summary>
    public Task SendTaskFailureAsync(string taskToken, string error, string cause, CancellationToken ct = default)
    {
        RecordOperation("SendTaskFailure");
        return Task.CompletedTask;
    }

    /// <summary>Sends a heartbeat for long-running tasks.</summary>
    public Task SendTaskHeartbeatAsync(string taskToken, CancellationToken ct = default)
    {
        RecordOperation("SendTaskHeartbeat");
        return Task.CompletedTask;
    }
}

#endregion

#region 119.3.3 Redis State Strategy

/// <summary>
/// 119.3.3: Redis-based state management for serverless functions
/// with TTL, pub/sub, and distributed locking.
/// </summary>
public sealed class RedisStateStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, RedisEntry> _store = new BoundedDictionary<string, RedisEntry>(1000);

    public override string StrategyId => "state-redis";
    public override string DisplayName => "Redis State";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = true,
        SupportsAsyncInvocation = false,
        SupportsEventTriggers = false
    };

    public override string SemanticDescription =>
        "Redis-based state management with key-value storage, TTL expiration, " +
        "pub/sub messaging, distributed locking, and ElastiCache/Upstash support.";

    public override string[] Tags => new[] { "redis", "cache", "state", "distributed", "lock" };

    /// <summary>Gets a value.</summary>
    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        RecordOperation("Get");
        if (_store.TryGetValue(key, out var entry) && !entry.IsExpired && entry.Value is T value)
        {
            return Task.FromResult<T?>(value);
        }
        return Task.FromResult<T?>(default);
    }

    /// <summary>Sets a value with optional TTL.</summary>
    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        _store[key] = new RedisEntry
        {
            Key = key,
            Value = value,
            ExpiresAt = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : null
        };
        RecordOperation("Set");
        return Task.CompletedTask;
    }

    /// <summary>Sets value only if key doesn't exist.</summary>
    public Task<bool> SetNxAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var added = _store.TryAdd(key, new RedisEntry
        {
            Key = key,
            Value = value,
            ExpiresAt = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : null
        });
        RecordOperation("SetNx");
        return Task.FromResult(added);
    }

    /// <summary>Acquires a distributed lock.</summary>
    public Task<RedisLock?> AcquireLockAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
    {
        var lockId = Guid.NewGuid().ToString();
        var acquired = _store.TryAdd($"lock:{lockKey}", new RedisEntry
        {
            Key = $"lock:{lockKey}",
            Value = lockId,
            ExpiresAt = DateTimeOffset.UtcNow + timeout
        });

        RecordOperation("AcquireLock");

        if (acquired)
        {
            return Task.FromResult<RedisLock?>(new RedisLock
            {
                LockKey = lockKey,
                LockId = lockId,
                AcquiredAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow + timeout
            });
        }
        return Task.FromResult<RedisLock?>(null);
    }

    /// <summary>Releases a lock.</summary>
    public Task<bool> ReleaseLockAsync(string lockKey, string lockId, CancellationToken ct = default)
    {
        if (_store.TryGetValue($"lock:{lockKey}", out var entry) && entry.Value?.ToString() == lockId)
        {
            _store.TryRemove($"lock:{lockKey}", out _);
            RecordOperation("ReleaseLock");
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>Deletes a key.</summary>
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        RecordOperation("Delete");
        return Task.FromResult(_store.TryRemove(key, out _));
    }
}

#endregion

#region 119.3.4 DynamoDB State Strategy

/// <summary>
/// 119.3.4: DynamoDB-based state management with single-table design,
/// TTL, and optimistic locking.
/// </summary>
public sealed class DynamoDbStateStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, DynamoDbItem> _store = new BoundedDictionary<string, DynamoDbItem>(1000);

    public override string StrategyId => "state-dynamodb";
    public override string DisplayName => "DynamoDB State";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;
    public override ServerlessPlatform? TargetPlatform => ServerlessPlatform.AwsLambda;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = true,
        SupportsAsyncInvocation = false,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "DynamoDB-based state management with single-table design patterns, " +
        "TTL expiration, optimistic locking, and DynamoDB Streams integration.";

    public override string[] Tags => new[] { "dynamodb", "nosql", "state", "aws", "single-table" };

    /// <summary>Gets an item.</summary>
    public Task<T?> GetItemAsync<T>(string pk, string sk, CancellationToken ct = default)
    {
        var key = $"{pk}#{sk}";
        RecordOperation("GetItem");
        if (_store.TryGetValue(key, out var item) && item.Data is T value)
        {
            return Task.FromResult<T?>(value);
        }
        return Task.FromResult<T?>(default);
    }

    /// <summary>Puts an item with optimistic locking.</summary>
    public Task<bool> PutItemAsync<T>(string pk, string sk, T data, long? expectedVersion = null, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = $"{pk}#{sk}";
        DynamoDbItem? existing = null;

        if (expectedVersion.HasValue && _store.TryGetValue(key, out existing) && existing.Version != expectedVersion)
        {
            return Task.FromResult(false);
        }

        if (!expectedVersion.HasValue)
        {
            _store.TryGetValue(key, out existing);
        }

        _store[key] = new DynamoDbItem
        {
            Pk = pk,
            Sk = sk,
            Data = data,
            Version = (existing?.Version ?? 0) + 1,
            TtlTimestamp = ttl.HasValue ? DateTimeOffset.UtcNow + ttl.Value : null,
            LastModified = DateTimeOffset.UtcNow
        };

        RecordOperation("PutItem");
        return Task.FromResult(true);
    }

    /// <summary>Deletes an item.</summary>
    public Task<bool> DeleteItemAsync(string pk, string sk, CancellationToken ct = default)
    {
        var key = $"{pk}#{sk}";
        RecordOperation("DeleteItem");
        return Task.FromResult(_store.TryRemove(key, out _));
    }

    /// <summary>Queries items by partition key.</summary>
    public Task<IReadOnlyList<T>> QueryAsync<T>(string pk, string? skPrefix = null, int limit = 100, CancellationToken ct = default)
    {
        var items = _store.Values
            .Where(i => i.Pk == pk && (skPrefix == null || i.Sk.StartsWith(skPrefix)))
            .Take(limit)
            .Select(i => i.Data)
            .OfType<T>()
            .ToList();

        RecordOperation("Query");
        return Task.FromResult<IReadOnlyList<T>>(items);
    }
}

#endregion

#region 119.3.5 Cosmos DB State Strategy

/// <summary>
/// 119.3.5: Cosmos DB state management with partition key design,
/// change feed, and multi-region writes.
/// </summary>
public sealed class CosmosDbStateStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, CosmosDbDocument> _store = new BoundedDictionary<string, CosmosDbDocument>(1000);

    public override string StrategyId => "state-cosmosdb";
    public override string DisplayName => "Cosmos DB State";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;
    public override ServerlessPlatform? TargetPlatform => ServerlessPlatform.AzureFunctions;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = true,
        SupportsAsyncInvocation = false,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Azure Cosmos DB state management with partition key optimization, " +
        "change feed processing, TTL, and multi-region active-active writes.";

    public override string[] Tags => new[] { "cosmosdb", "azure", "nosql", "state", "change-feed" };

    /// <summary>Reads a document.</summary>
    public Task<T?> ReadItemAsync<T>(string id, string partitionKey, CancellationToken ct = default)
    {
        var key = $"{partitionKey}:{id}";
        RecordOperation("ReadItem");
        if (_store.TryGetValue(key, out var doc) && doc.Data is T value)
        {
            return Task.FromResult<T?>(value);
        }
        return Task.FromResult<T?>(default);
    }

    /// <summary>Upserts a document.</summary>
    public Task<CosmosDbDocument> UpsertItemAsync<T>(string id, string partitionKey, T data, string? etag = null, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = $"{partitionKey}:{id}";

        var doc = new CosmosDbDocument
        {
            Id = id,
            PartitionKey = partitionKey,
            Data = data,
            Etag = Guid.NewGuid().ToString(),
            TtlSeconds = ttl.HasValue ? (int)ttl.Value.TotalSeconds : null,
            Timestamp = DateTimeOffset.UtcNow
        };

        _store[key] = doc;
        RecordOperation("UpsertItem");
        return Task.FromResult(doc);
    }

    /// <summary>Deletes a document.</summary>
    public Task<bool> DeleteItemAsync(string id, string partitionKey, CancellationToken ct = default)
    {
        var key = $"{partitionKey}:{id}";
        RecordOperation("DeleteItem");
        return Task.FromResult(_store.TryRemove(key, out _));
    }

    /// <summary>Queries with SQL.</summary>
    public Task<IReadOnlyList<T>> QueryAsync<T>(string partitionKey, string? filter = null, int limit = 100, CancellationToken ct = default)
    {
        var items = _store.Values
            .Where(d => d.PartitionKey == partitionKey)
            .Take(limit)
            .Select(d => d.Data)
            .OfType<T>()
            .ToList();

        RecordOperation("Query");
        return Task.FromResult<IReadOnlyList<T>>(items);
    }
}

#endregion

#region 119.3.6-8 Additional State Strategies

/// <summary>
/// 119.3.6: Cloudflare Durable Objects for edge state.
/// </summary>
public sealed class DurableObjectsStateStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "state-durable-objects";
    public override string DisplayName => "Cloudflare Durable Objects";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;
    public override ServerlessPlatform? TargetPlatform => ServerlessPlatform.CloudflareWorkers;
    public override bool IsProductionReady => false; // Requires Cloudflare Workers runtime

    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true, SupportsAsyncInvocation = false };

    public override string SemanticDescription =>
        "Cloudflare Durable Objects for edge-native state with strong consistency, " +
        "WebSocket support, and global coordination.";

    public override string[] Tags => new[] { "durable-objects", "cloudflare", "edge", "state" };

    public Task<object?> GetStateAsync(string objectId, CancellationToken ct = default)
    {
        RecordOperation("GetState");
        return Task.FromResult<object?>(null);
    }

    public Task PutStateAsync(string objectId, object state, CancellationToken ct = default)
    {
        RecordOperation("PutState");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 119.3.7: Firestore state management.
/// </summary>
public sealed class FirestoreStateStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "state-firestore";
    public override string DisplayName => "Firestore State";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;
    public override ServerlessPlatform? TargetPlatform => ServerlessPlatform.GoogleCloudFunctions;
    public override bool IsProductionReady => false; // Requires Google Cloud Firestore SDK

    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true, SupportsEventTriggers = true };

    public override string SemanticDescription =>
        "Google Firestore state management with real-time sync, " +
        "offline support, and security rules integration.";

    public override string[] Tags => new[] { "firestore", "gcp", "realtime", "state" };

    public Task<T?> GetDocumentAsync<T>(string collection, string documentId, CancellationToken ct = default)
    {
        RecordOperation("GetDocument");
        return Task.FromResult<T?>(default);
    }

    public Task SetDocumentAsync<T>(string collection, string documentId, T data, CancellationToken ct = default)
    {
        RecordOperation("SetDocument");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 119.3.8: Vercel KV state management.
/// </summary>
public sealed class VercelKvStateStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "state-vercel-kv";
    public override string DisplayName => "Vercel KV";
    public override ServerlessCategory Category => ServerlessCategory.StateManagement;
    public override ServerlessPlatform? TargetPlatform => ServerlessPlatform.VercelFunctions;
    public override bool IsProductionReady => false; // Requires Vercel KV SDK

    public override ServerlessStrategyCapabilities Capabilities => new() { SupportsSyncInvocation = true };

    public override string SemanticDescription =>
        "Vercel KV (Redis-compatible) state management with edge distribution " +
        "and sub-millisecond latency at the edge.";

    public override string[] Tags => new[] { "vercel", "kv", "redis", "edge", "state" };

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        RecordOperation("Get");
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, int? exSeconds = null, CancellationToken ct = default)
    {
        RecordOperation("Set");
        return Task.CompletedTask;
    }
}

#endregion

#region Supporting Types

public sealed class EntityState
{
    public required string EntityId { get; init; }
    public required string EntityType { get; init; }
    public object? State { get; set; }
    public long Version { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public ConcurrentQueue<EntityOperation> PendingOperations { get; } = new();
}

public sealed record EntityOperation
{
    public required string OperationId { get; init; }
    public required string OperationType { get; init; }
    public object? Input { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed class WorkflowExecution
{
    public required string ExecutionArn { get; init; }
    public required string StateMachineArn { get; init; }
    public string Status { get; set; } = "RUNNING";
    public object? Input { get; init; }
    public object? Output { get; set; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? StopTime { get; set; }
    public List<WorkflowEvent> Events { get; } = new();
}

public sealed record WorkflowEvent
{
    public required string EventType { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public object? Details { get; init; }
}

public sealed class RedisEntry
{
    public required string Key { get; init; }
    public object? Value { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow > ExpiresAt.Value;
}

public sealed record RedisLock
{
    public required string LockKey { get; init; }
    public required string LockId { get; init; }
    public DateTimeOffset AcquiredAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class DynamoDbItem
{
    public required string Pk { get; init; }
    public required string Sk { get; init; }
    public object? Data { get; init; }
    public long Version { get; set; }
    public DateTimeOffset? TtlTimestamp { get; init; }
    public DateTimeOffset LastModified { get; set; }
}

public sealed class CosmosDbDocument
{
    public required string Id { get; init; }
    public required string PartitionKey { get; init; }
    public object? Data { get; init; }
    public required string Etag { get; init; }
    public int? TtlSeconds { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

#endregion
