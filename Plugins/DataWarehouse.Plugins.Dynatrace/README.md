# Dynatrace Observability Plugin

Production-ready Dynatrace observability plugin for DataWarehouse, providing comprehensive metrics and logs ingestion using Dynatrace APIs v2.

## Features

- **Metrics Ingestion**: Full support for Dynatrace Metrics API v2 using line protocol format
- **Logs Ingestion**: Structured logging via Dynatrace Logs API v2 with JSON payload
- **Metric Types**: Counter, gauge, and histogram/summary metrics
- **Multi-dimensional Metrics**: Support for custom dimensions (tags)
- **Batching**: Efficient API usage with configurable batch sizes
- **Background Flushing**: Automatic periodic flushing of batched data
- **Error Handling**: Robust error handling and retry logic
- **Duration Tracking**: Built-in support for tracking operation durations

## Configuration

```csharp
var config = new DynatraceConfiguration
{
    DynatraceUrl = "https://abc12345.live.dynatrace.com",
    ApiToken = "dt0c01.****",
    MetricPrefix = "datawarehouse",
    MetricsBatchSize = 100,
    LogsBatchSize = 100,
    FlushInterval = TimeSpan.FromSeconds(10),
    DefaultDimensions = new Dictionary<string, string>
    {
        ["environment"] = "production",
        ["service"] = "datawarehouse"
    }
};

var plugin = new DynatracePlugin(config);
```

## Required Configuration

- **DynatraceUrl**: Your Dynatrace environment URL (e.g., `https://{environment-id}.live.dynatrace.com`)
- **ApiToken**: Dynatrace API token with required scopes:
  - `metrics.ingest` - for metrics ingestion
  - `logs.ingest` - for logs ingestion

## API Endpoints

- **Metrics**: `POST /api/v2/metrics/ingest` (line protocol format)
- **Logs**: `POST /api/v2/logs/ingest` (JSON format)

## Usage Examples

### Counter Metrics

```csharp
// Simple counter increment
plugin.IncrementCounter("requests.total");

// Counter with custom increment
plugin.IncrementCounter("bytes.transferred", 1024);

// Counter with dimensions
plugin.IncrementCounter("requests.total", 1,
    DimensionSet.From(
        ("endpoint", "/api/data"),
        ("method", "GET")
    ));
```

### Gauge Metrics

```csharp
// Record a gauge value
plugin.RecordGauge("memory.usage", 1024.5);

// Gauge with dimensions
plugin.RecordGauge("cpu.usage", 45.2,
    DimensionSet.From(("core", "0")));
```

### Histogram Metrics

```csharp
// Record histogram observation
plugin.RecordHistogram("request.duration", 0.125);

// Histogram with dimensions
plugin.RecordHistogram("query.duration", 0.045,
    DimensionSet.From(("database", "users")));
```

### Duration Tracking

```csharp
// Automatically track operation duration
using (plugin.TrackDuration("operation.duration"))
{
    // Your operation here
    await ProcessDataAsync();
}

// With dimensions
using (plugin.TrackDuration("operation.duration",
    DimensionSet.From(("type", "batch"))))
{
    await ProcessBatchAsync();
}
```

### Logging

```csharp
// Simple log
plugin.Log("Application started", DynatraceLogLevel.INFO);

// Log with attributes
plugin.Log("Request processed", DynatraceLogLevel.INFO,
    new Dictionary<string, object>
    {
        ["requestId"] = "12345",
        ["duration"] = 125.5,
        ["status"] = 200
    });

// Error log
plugin.Log("Failed to process request", DynatraceLogLevel.ERROR,
    new Dictionary<string, object>
    {
        ["error"] = ex.Message,
        ["stackTrace"] = ex.StackTrace
    });
```

## Message Commands

The plugin supports the following message commands:

### dynatrace.increment
Increment a counter metric.

```csharp
var message = new PluginMessage
{
    Type = "dynatrace.increment",
    Payload = new Dictionary<string, object>
    {
        ["metric"] = "requests.count",
        ["amount"] = 1,
        ["dimensions"] = new Dictionary<string, string>
        {
            ["endpoint"] = "/api/data"
        }
    }
};
```

### dynatrace.record
Record a gauge metric value.

```csharp
var message = new PluginMessage
{
    Type = "dynatrace.record",
    Payload = new Dictionary<string, object>
    {
        ["metric"] = "memory.usage",
        ["value"] = 1024.5
    }
};
```

### dynatrace.histogram
Record a histogram observation.

```csharp
var message = new PluginMessage
{
    Type = "dynatrace.histogram",
    Payload = new Dictionary<string, object>
    {
        ["metric"] = "request.duration",
        ["value"] = 0.125
    }
};
```

### dynatrace.log
Send a log entry.

```csharp
var message = new PluginMessage
{
    Type = "dynatrace.log",
    Payload = new Dictionary<string, object>
    {
        ["message"] = "Application event",
        ["level"] = "INFO",
        ["attributes"] = new Dictionary<string, object>
        {
            ["userId"] = "123",
            ["action"] = "login"
        }
    }
};
```

### dynatrace.flush
Manually flush pending batches.

```csharp
var message = new PluginMessage
{
    Type = "dynatrace.flush"
};
```

### dynatrace.status
Get plugin status.

```csharp
var message = new PluginMessage
{
    Type = "dynatrace.status"
};
// Returns: isRunning, dynatraceUrl, metricsIngested, logsIngested, etc.
```

## Metric Naming

Metrics are automatically prefixed with the configured `MetricPrefix` value. For example:

- Configuration: `MetricPrefix = "datawarehouse"`
- Metric name: `requests.total`
- Actual metric: `datawarehouse.requests.total`

## Line Protocol Format

Metrics are sent to Dynatrace in line protocol format:

```
metric.name,dimension1=value1,dimension2=value2 42.5 1234567890000
```

Example:
```
datawarehouse.requests.total,endpoint=/api/data,method=GET 1 1640000000000
```

## Best Practices

1. **Use Dimensions Wisely**: Keep cardinality low to avoid performance issues
2. **Batch Size**: Adjust batch sizes based on your ingestion rate
3. **Flush Interval**: Balance between latency and API efficiency
4. **Error Handling**: Monitor `IngestErrors` property for failed ingestions
5. **API Token Security**: Store API tokens securely (environment variables, secrets management)

## Lifecycle Management

```csharp
// Start the plugin
await plugin.StartAsync(cancellationToken);

// Use the plugin
plugin.IncrementCounter("app.started");

// Stop the plugin (automatically flushes pending data)
await plugin.StopAsync();
```

## Monitoring

Track plugin health using the status properties:

```csharp
var metricsIngested = plugin.MetricsIngested;
var logsIngested = plugin.LogsIngested;
var ingestErrors = plugin.IngestErrors;
var isRunning = plugin.IsRunning;
```

## Dependencies

- .NET 10.0
- DataWarehouse.SDK

## Plugin Information

- **ID**: `com.datawarehouse.telemetry.dynatrace`
- **Name**: Dynatrace Observability Plugin
- **Version**: 1.0.0
- **Category**: MetricsProvider

## License

Part of the DataWarehouse project.
