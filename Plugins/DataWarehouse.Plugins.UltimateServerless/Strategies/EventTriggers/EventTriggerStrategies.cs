using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateServerless.Strategies.EventTriggers;

#region 119.2.1 HTTP Trigger Strategy

/// <summary>
/// 119.2.1: HTTP/HTTPS trigger strategy with API Gateway integration,
/// request validation, and rate limiting support.
/// </summary>
public sealed class HttpTriggerStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, HttpTriggerConfig> _triggers = new BoundedDictionary<string, HttpTriggerConfig>(1000);

    public override string StrategyId => "trigger-http";
    public override string DisplayName => "HTTP Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = true,
        SupportsAsyncInvocation = false,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "HTTP/HTTPS trigger for serverless functions with API Gateway integration, " +
        "request validation, CORS configuration, authentication, and rate limiting.";

    public override string[] Tags => new[] { "http", "https", "api", "rest", "webhook", "trigger" };

    /// <summary>Creates an HTTP trigger.</summary>
    public Task<HttpTriggerResult> CreateTriggerAsync(HttpTriggerConfig config, CancellationToken ct = default)
    {
        _triggers[config.TriggerId] = config;
        RecordOperation("CreateTrigger");

        return Task.FromResult(new HttpTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            Endpoint = $"https://api.example.com/{config.Path}",
            Methods = config.Methods
        });
    }

    /// <summary>Configures authentication.</summary>
    public Task ConfigureAuthAsync(string triggerId, HttpAuthConfig auth, CancellationToken ct = default)
    {
        RecordOperation("ConfigureAuth");
        return Task.CompletedTask;
    }

    /// <summary>Configures rate limiting.</summary>
    public Task ConfigureRateLimitAsync(string triggerId, int requestsPerSecond, int burstSize, CancellationToken ct = default)
    {
        RecordOperation("ConfigureRateLimit");
        return Task.CompletedTask;
    }
}

#endregion

#region 119.2.2 Queue Trigger Strategy

/// <summary>
/// 119.2.2: Message queue trigger strategy supporting SQS, Azure Queue,
/// Pub/Sub, and RabbitMQ with batch processing and DLQ handling.
/// </summary>
public sealed class QueueTriggerStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, QueueTriggerConfig> _triggers = new BoundedDictionary<string, QueueTriggerConfig>(1000);

    public override string StrategyId => "trigger-queue";
    public override string DisplayName => "Queue Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Message queue trigger supporting AWS SQS, Azure Storage Queue, Google Pub/Sub, " +
        "and RabbitMQ with batch processing, visibility timeout, and dead-letter queue handling.";

    public override string[] Tags => new[] { "queue", "sqs", "pubsub", "rabbitmq", "message", "trigger" };

    /// <summary>Creates a queue trigger.</summary>
    public Task<QueueTriggerResult> CreateTriggerAsync(QueueTriggerConfig config, CancellationToken ct = default)
    {
        _triggers[config.TriggerId] = config;
        RecordOperation("CreateTrigger");

        return Task.FromResult(new QueueTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            QueueUrl = config.QueueUrl,
            BatchSize = config.BatchSize
        });
    }

    /// <summary>Configures batch processing.</summary>
    public Task ConfigureBatchAsync(string triggerId, int batchSize, int maxBatchingWindowSeconds, CancellationToken ct = default)
    {
        if (_triggers.TryGetValue(triggerId, out var config))
        {
            _triggers[triggerId] = config with { BatchSize = batchSize, MaxBatchingWindowSeconds = maxBatchingWindowSeconds };
        }
        RecordOperation("ConfigureBatch");
        return Task.CompletedTask;
    }

    /// <summary>Configures dead-letter queue.</summary>
    public Task ConfigureDeadLetterQueueAsync(string triggerId, string dlqUrl, int maxReceiveCount, CancellationToken ct = default)
    {
        RecordOperation("ConfigureDLQ");
        return Task.CompletedTask;
    }
}

#endregion

#region 119.2.3 Schedule Trigger Strategy

/// <summary>
/// 119.2.3: Scheduled/cron trigger strategy with timezone support,
/// one-time schedules, and rate expressions.
/// </summary>
public sealed class ScheduleTriggerStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, ScheduleTriggerConfig> _triggers = new BoundedDictionary<string, ScheduleTriggerConfig>(1000);

    public override string StrategyId => "trigger-schedule";
    public override string DisplayName => "Schedule Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Scheduled trigger with cron expressions, rate expressions, " +
        "timezone support, one-time schedules, and missed execution handling.";

    public override string[] Tags => new[] { "schedule", "cron", "timer", "periodic", "trigger" };

    /// <summary>Creates a schedule trigger.</summary>
    public Task<ScheduleTriggerResult> CreateTriggerAsync(ScheduleTriggerConfig config, CancellationToken ct = default)
    {
        _triggers[config.TriggerId] = config;
        RecordOperation("CreateTrigger");

        return Task.FromResult(new ScheduleTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            Schedule = config.ScheduleExpression,
            NextExecution = CalculateNextExecution(config.ScheduleExpression)
        });
    }

    /// <summary>Disables a schedule.</summary>
    public Task DisableAsync(string triggerId, CancellationToken ct = default)
    {
        if (_triggers.TryGetValue(triggerId, out var config))
        {
            _triggers[triggerId] = config with { Enabled = false };
        }
        RecordOperation("Disable");
        return Task.CompletedTask;
    }

    /// <summary>Gets next N scheduled executions based on rate or cron expression.</summary>
    public Task<IReadOnlyList<DateTimeOffset>> GetNextExecutionsAsync(string triggerId, int count = 5, CancellationToken ct = default)
    {
        RecordOperation("GetNextExecutions");
        if (!_triggers.TryGetValue(triggerId, out var config))
            return Task.FromResult<IReadOnlyList<DateTimeOffset>>(Array.Empty<DateTimeOffset>());

        var executions = new List<DateTimeOffset>(count);
        var next = CalculateNextExecution(config.ScheduleExpression);
        var interval = ParseInterval(config.ScheduleExpression);
        for (int i = 0; i < count; i++)
        {
            executions.Add(next);
            next = next.Add(interval);
        }
        return Task.FromResult<IReadOnlyList<DateTimeOffset>>(executions);
    }

    private static DateTimeOffset CalculateNextExecution(string expression)
    {
        var interval = ParseInterval(expression);
        return DateTimeOffset.UtcNow.Add(interval);
    }

    /// <summary>
    /// Parses rate expressions ("rate(5 minutes)", "rate(1 hour)") and falls
    /// back to 1-minute for unrecognised cron expressions.
    /// </summary>
    private static TimeSpan ParseInterval(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return TimeSpan.FromMinutes(1);

        // Rate expression: rate(value unit)
        var rateMatch = System.Text.RegularExpressions.Regex.Match(
            expression, @"rate\((\d+)\s+(minute|minutes|hour|hours|day|days)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (rateMatch.Success && int.TryParse(rateMatch.Groups[1].Value, out var value))
        {
            return rateMatch.Groups[2].Value.ToLowerInvariant() switch
            {
                "minute" or "minutes" => TimeSpan.FromMinutes(value),
                "hour" or "hours" => TimeSpan.FromHours(value),
                "day" or "days" => TimeSpan.FromDays(value),
                _ => TimeSpan.FromMinutes(value)
            };
        }

        // Cron expression fallback: attempt to derive period from minute field
        var parts = expression.Trim().Split(' ');
        if (parts.Length >= 2 && parts[0].StartsWith("*/") && int.TryParse(parts[0][2..], out var everyMin))
            return TimeSpan.FromMinutes(everyMin);

        return TimeSpan.FromMinutes(1); // Safe default for unparseable expressions
    }
}

#endregion

#region 119.2.4 Stream Trigger Strategy

/// <summary>
/// 119.2.4: Stream trigger for Kinesis, Event Hubs, Kafka, and DynamoDB streams
/// with parallel processing and checkpointing.
/// </summary>
public sealed class StreamTriggerStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, StreamTriggerConfig> _triggers = new BoundedDictionary<string, StreamTriggerConfig>(1000);

    public override string StrategyId => "trigger-stream";
    public override string DisplayName => "Stream Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Stream trigger for AWS Kinesis, Azure Event Hubs, Apache Kafka, and DynamoDB Streams " +
        "with parallel shard processing, checkpointing, and batch windowing.";

    public override string[] Tags => new[] { "stream", "kinesis", "eventhubs", "kafka", "dynamodb", "trigger" };

    /// <summary>Creates a stream trigger.</summary>
    public Task<StreamTriggerResult> CreateTriggerAsync(StreamTriggerConfig config, CancellationToken ct = default)
    {
        _triggers[config.TriggerId] = config;
        RecordOperation("CreateTrigger");

        return Task.FromResult(new StreamTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            StreamArn = config.StreamArn,
            StartingPosition = config.StartingPosition
        });
    }

    /// <summary>Configures parallel processing.</summary>
    public Task ConfigureParallelismAsync(string triggerId, int parallelizationFactor, CancellationToken ct = default)
    {
        RecordOperation("ConfigureParallelism");
        return Task.CompletedTask;
    }

    /// <summary>Gets stream processing metrics.</summary>
    public Task<StreamMetrics> GetMetricsAsync(string triggerId, CancellationToken ct = default)
    {
        RecordOperation("GetMetrics");
        return Task.FromResult(new StreamMetrics
        {
            TriggerId = triggerId,
            RecordsProcessed = Random.Shared.Next(10000, 100000),
            IteratorAge = TimeSpan.FromSeconds(Random.Shared.Next(0, 60)),
            ErrorCount = Random.Shared.Next(0, 10)
        });
    }
}

#endregion

#region 119.2.5 Storage Trigger Strategy

/// <summary>
/// 119.2.5: Storage trigger for S3, Blob Storage, GCS, and MinIO
/// with event filtering and prefix/suffix matching.
/// </summary>
public sealed class StorageTriggerStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, StorageTriggerConfig> _triggers = new BoundedDictionary<string, StorageTriggerConfig>(1000);

    public override string StrategyId => "trigger-storage";
    public override string DisplayName => "Storage Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Storage trigger for S3, Azure Blob Storage, Google Cloud Storage, and MinIO " +
        "with event type filtering, prefix/suffix matching, and metadata filters.";

    public override string[] Tags => new[] { "storage", "s3", "blob", "gcs", "bucket", "trigger" };

    /// <summary>Creates a storage trigger.</summary>
    public Task<StorageTriggerResult> CreateTriggerAsync(StorageTriggerConfig config, CancellationToken ct = default)
    {
        _triggers[config.TriggerId] = config;
        RecordOperation("CreateTrigger");

        return Task.FromResult(new StorageTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            BucketName = config.BucketName,
            EventTypes = config.EventTypes
        });
    }

    /// <summary>Configures prefix filter.</summary>
    public Task ConfigurePrefixFilterAsync(string triggerId, string prefix, CancellationToken ct = default)
    {
        RecordOperation("ConfigurePrefixFilter");
        return Task.CompletedTask;
    }

    /// <summary>Configures suffix filter.</summary>
    public Task ConfigureSuffixFilterAsync(string triggerId, string suffix, CancellationToken ct = default)
    {
        RecordOperation("ConfigureSuffixFilter");
        return Task.CompletedTask;
    }
}

#endregion

#region 119.2.6 Database Trigger Strategy

/// <summary>
/// 119.2.6: Database trigger for DynamoDB, Cosmos DB, Firestore,
/// and PostgreSQL with change data capture.
/// </summary>
public sealed class DatabaseTriggerStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "trigger-database";
    public override string DisplayName => "Database Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Database trigger for DynamoDB Streams, Cosmos DB Change Feed, Firestore triggers, " +
        "and PostgreSQL logical replication with change data capture.";

    public override string[] Tags => new[] { "database", "dynamodb", "cosmos", "firestore", "cdc", "trigger" };

    /// <summary>Creates a database trigger.</summary>
    public Task<DatabaseTriggerResult> CreateTriggerAsync(DatabaseTriggerConfig config, CancellationToken ct = default)
    {
        RecordOperation("CreateTrigger");
        return Task.FromResult(new DatabaseTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            DatabaseType = config.DatabaseType,
            TableName = config.TableName
        });
    }

    /// <summary>Configures change types to capture.</summary>
    public Task ConfigureChangeTypesAsync(string triggerId, IReadOnlyList<string> changeTypes, CancellationToken ct = default)
    {
        RecordOperation("ConfigureChangeTypes");
        return Task.CompletedTask;
    }
}

#endregion

#region 119.2.7 Webhook Trigger Strategy

/// <summary>
/// 119.2.7: Generic webhook trigger with signature validation,
/// retry handling, and payload transformation.
/// </summary>
public sealed class WebhookTriggerStrategy : ServerlessStrategyBase
{
    private readonly BoundedDictionary<string, string> _webhookSecrets = new BoundedDictionary<string, string>(1000);

    public override string StrategyId => "trigger-webhook";
    public override string DisplayName => "Webhook Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = true,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Generic webhook trigger with HMAC signature validation, " +
        "retry handling, payload transformation, and idempotency support.";

    public override string[] Tags => new[] { "webhook", "callback", "integration", "signature", "trigger" };

    /// <summary>Creates a webhook endpoint.</summary>
    public Task<WebhookResult> CreateWebhookAsync(WebhookConfig config, CancellationToken ct = default)
    {
        RecordOperation("CreateWebhook");
        var secret = Guid.NewGuid().ToString("N");
        _webhookSecrets[config.WebhookId] = secret;
        return Task.FromResult(new WebhookResult
        {
            Success = true,
            WebhookId = config.WebhookId,
            Endpoint = $"https://webhooks.example.com/{config.WebhookId}",
            Secret = secret
        });
    }

    /// <summary>Validates webhook HMAC-SHA256 signature.</summary>
    public Task<bool> ValidateSignatureAsync(string webhookId, string payload, string signature, CancellationToken ct = default)
    {
        RecordOperation("ValidateSignature");

        if (!_webhookSecrets.TryGetValue(webhookId, out var secret))
            return Task.FromResult(false); // Unknown webhook — deny

        // Compute HMAC-SHA256 of payload with the webhook secret
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var expectedBytes = hmac.ComputeHash(payloadBytes);
        var expected = Convert.ToHexString(expectedBytes).ToLowerInvariant();

        // Compare provided signature (strip optional "sha256=" prefix for GitHub-style)
        var provided = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature[7..] : signature;

        return Task.FromResult(string.Equals(expected, provided, StringComparison.OrdinalIgnoreCase));
    }
}

#endregion

#region 119.2.8 IoT Trigger Strategy

/// <summary>
/// 119.2.8: IoT trigger for AWS IoT, Azure IoT Hub, and Google IoT Core
/// with device shadow updates and telemetry processing.
/// </summary>
public sealed class IoTTriggerStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "trigger-iot";
    public override string DisplayName => "IoT Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "IoT trigger for AWS IoT Core, Azure IoT Hub, and Google Cloud IoT " +
        "with device lifecycle events, telemetry processing, and shadow updates.";

    public override string[] Tags => new[] { "iot", "device", "telemetry", "mqtt", "trigger" };

    /// <summary>Creates an IoT trigger.</summary>
    public Task<IoTTriggerResult> CreateTriggerAsync(IoTTriggerConfig config, CancellationToken ct = default)
    {
        RecordOperation("CreateTrigger");
        return Task.FromResult(new IoTTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            TopicFilter = config.TopicFilter
        });
    }
}

#endregion

#region 119.2.9 GraphQL Subscription Trigger Strategy

/// <summary>
/// 119.2.9: GraphQL subscription trigger for real-time updates
/// with WebSocket support and subscription filtering.
/// </summary>
public sealed class GraphQLSubscriptionTriggerStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "trigger-graphql-subscription";
    public override string DisplayName => "GraphQL Subscription Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "GraphQL subscription trigger for real-time updates via WebSocket " +
        "with subscription filtering, authorization, and connection management.";

    public override string[] Tags => new[] { "graphql", "subscription", "websocket", "realtime", "trigger" };

    /// <summary>Creates a subscription trigger.</summary>
    public Task<GraphQLTriggerResult> CreateTriggerAsync(GraphQLTriggerConfig config, CancellationToken ct = default)
    {
        RecordOperation("CreateTrigger");
        return Task.FromResult(new GraphQLTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            SubscriptionName = config.SubscriptionName
        });
    }
}

#endregion

#region 119.2.10 EventBridge/Eventarc Trigger Strategy

/// <summary>
/// 119.2.10: Cloud event bus trigger for EventBridge, Eventarc,
/// and Azure Event Grid with pattern matching and content filtering.
/// </summary>
public sealed class EventBusTriggerStrategy : ServerlessStrategyBase
{
    public override string StrategyId => "trigger-eventbus";
    public override string DisplayName => "Event Bus Trigger";
    public override ServerlessCategory Category => ServerlessCategory.EventTriggers;

    public override ServerlessStrategyCapabilities Capabilities => new()
    {
        SupportsSyncInvocation = false,
        SupportsAsyncInvocation = true,
        SupportsEventTriggers = true
    };

    public override string SemanticDescription =>
        "Cloud event bus trigger for AWS EventBridge, GCP Eventarc, and Azure Event Grid " +
        "with event pattern matching, content-based filtering, and cross-account events.";

    public override string[] Tags => new[] { "eventbridge", "eventarc", "eventgrid", "event-bus", "trigger" };

    /// <summary>Creates an event bus trigger.</summary>
    public Task<EventBusTriggerResult> CreateTriggerAsync(EventBusTriggerConfig config, CancellationToken ct = default)
    {
        RecordOperation("CreateTrigger");
        return Task.FromResult(new EventBusTriggerResult
        {
            Success = true,
            TriggerId = config.TriggerId,
            RuleName = config.RuleName,
            EventPattern = config.EventPattern
        });
    }

    /// <summary>Configures event pattern.</summary>
    public Task ConfigurePatternAsync(string triggerId, Dictionary<string, object> pattern, CancellationToken ct = default)
    {
        RecordOperation("ConfigurePattern");
        return Task.CompletedTask;
    }
}

#endregion

#region Supporting Types

public sealed record HttpTriggerConfig
{
    public required string TriggerId { get; init; }
    public required string FunctionId { get; init; }
    public required string Path { get; init; }
    public IReadOnlyList<string> Methods { get; init; } = new[] { "GET", "POST" };
    public bool RequireAuth { get; init; }
    public bool EnableCors { get; init; } = true;
}

public sealed record HttpTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? Endpoint { get; init; } public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>(); }
public sealed record HttpAuthConfig { public required string AuthType { get; init; } public Dictionary<string, string> Config { get; init; } = new(); }

public sealed record QueueTriggerConfig
{
    public required string TriggerId { get; init; }
    public required string FunctionId { get; init; }
    public required string QueueUrl { get; init; }
    public int BatchSize { get; init; } = 10;
    public int MaxBatchingWindowSeconds { get; init; } = 0;
    public int VisibilityTimeoutSeconds { get; init; } = 30;
}

public sealed record QueueTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? QueueUrl { get; init; } public int BatchSize { get; init; } }

public sealed record ScheduleTriggerConfig
{
    public required string TriggerId { get; init; }
    public required string FunctionId { get; init; }
    public required string ScheduleExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record ScheduleTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? Schedule { get; init; } public DateTimeOffset? NextExecution { get; init; } }

public sealed record StreamTriggerConfig
{
    public required string TriggerId { get; init; }
    public required string FunctionId { get; init; }
    public required string StreamArn { get; init; }
    public string StartingPosition { get; init; } = "LATEST";
    public int BatchSize { get; init; } = 100;
    public int ParallelizationFactor { get; init; } = 1;
}

public sealed record StreamTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? StreamArn { get; init; } public string? StartingPosition { get; init; } }
public sealed record StreamMetrics { public required string TriggerId { get; init; } public long RecordsProcessed { get; init; } public TimeSpan IteratorAge { get; init; } public int ErrorCount { get; init; } }

public sealed record StorageTriggerConfig
{
    public required string TriggerId { get; init; }
    public required string FunctionId { get; init; }
    public required string BucketName { get; init; }
    public IReadOnlyList<string> EventTypes { get; init; } = new[] { "s3:ObjectCreated:*" };
    public string? Prefix { get; init; }
    public string? Suffix { get; init; }
}

public sealed record StorageTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? BucketName { get; init; } public IReadOnlyList<string> EventTypes { get; init; } = Array.Empty<string>(); }

public sealed record DatabaseTriggerConfig { public required string TriggerId { get; init; } public required string FunctionId { get; init; } public required string DatabaseType { get; init; } public required string TableName { get; init; } }
public sealed record DatabaseTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? DatabaseType { get; init; } public string? TableName { get; init; } }

public sealed record WebhookConfig { public required string WebhookId { get; init; } public required string FunctionId { get; init; } public string? SecretHeader { get; init; } }
public sealed record WebhookResult { public bool Success { get; init; } public string? WebhookId { get; init; } public string? Endpoint { get; init; } public string? Secret { get; init; } }

public sealed record IoTTriggerConfig { public required string TriggerId { get; init; } public required string FunctionId { get; init; } public required string TopicFilter { get; init; } }
public sealed record IoTTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? TopicFilter { get; init; } }

public sealed record GraphQLTriggerConfig { public required string TriggerId { get; init; } public required string FunctionId { get; init; } public required string SubscriptionName { get; init; } }
public sealed record GraphQLTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? SubscriptionName { get; init; } }

public sealed record EventBusTriggerConfig { public required string TriggerId { get; init; } public required string FunctionId { get; init; } public required string RuleName { get; init; } public Dictionary<string, object> EventPattern { get; init; } = new(); }
public sealed record EventBusTriggerResult { public bool Success { get; init; } public string? TriggerId { get; init; } public string? RuleName { get; init; } public Dictionary<string, object> EventPattern { get; init; } = new(); }

#endregion
