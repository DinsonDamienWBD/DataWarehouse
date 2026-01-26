# DataWarehouse.Plugins.LogicMonitor

Production-ready LogicMonitor metrics plugin implementing REST API v3 metric ingestion for the DataWarehouse system.

## Features

- **REST API v3 Integration**: Push metrics via LogicMonitor's `/metric/ingest` endpoint
- **Dual Authentication**: Supports both LMv1 HMAC-SHA256 signature and Bearer token authentication
- **Metric Types**: Counter, Gauge, and Histogram metrics
- **Automatic Push**: Configurable periodic metric push to LogicMonitor
- **Batch Processing**: Efficient batch ingestion with configurable batch size
- **Resource Metadata**: Customizable resource properties and identification
- **Production Ready**: Full error handling, retry logic, and monitoring

## Configuration

### Basic Configuration

```csharp
var config = new LogicMonitorConfiguration
{
    AccountName = "your-company",           // LogicMonitor subdomain
    AccessId = "your-access-id",            // API Access ID
    AccessKey = "your-access-key",          // API Access Key
    ResourceId = "datawarehouse_metrics",   // Resource identifier
    ResourceName = "DataWarehouse",         // Display name
    PushIntervalSeconds = 60                // Push every 60 seconds
};

var plugin = new LogicMonitorPlugin(config);
```

### Bearer Token Authentication

```csharp
var config = new LogicMonitorConfiguration
{
    AccountName = "your-company",
    UseBearerAuth = true,
    BearerToken = "your-bearer-token",
    ResourceId = "datawarehouse_metrics",
    PushIntervalSeconds = 60
};
```

### Advanced Configuration

```csharp
var config = new LogicMonitorConfiguration
{
    AccountName = "your-company",
    AccessId = "your-access-id",
    AccessKey = "your-access-key",
    ResourceId = "datawarehouse_metrics",
    ResourceName = "DataWarehouse Production",
    ResourceDescription = "Production DataWarehouse telemetry",
    PushIntervalSeconds = 30,               // More frequent updates
    MaxBatchSize = 200,                     // Larger batches
    TimeoutSeconds = 60,                    // Longer timeout
    ResourceProperties = new Dictionary<string, string>
    {
        ["environment"] = "production",
        ["region"] = "us-west-2",
        ["cluster"] = "primary"
    }
};
```

## Usage

### Basic Usage

```csharp
// Initialize plugin
var plugin = new LogicMonitorPlugin(config);
await plugin.StartAsync(CancellationToken.None);

// Increment counters
plugin.IncrementCounter("requests_total");
plugin.IncrementCounter("requests_total", 5);

// Record gauge values
plugin.RecordMetric("cpu_usage_percent", 45.2);
plugin.RecordMetric("memory_used_bytes", 1024 * 1024 * 512);

// Track operation duration
using (plugin.TrackDuration("operation_duration_seconds"))
{
    // Your operation here
}

// Manual push (automatic push happens based on PushIntervalSeconds)
await plugin.PushMetricsAsync();

// Stop plugin
await plugin.StopAsync();
```

### Message-Based API

```csharp
// Increment counter
var msg = new PluginMessage
{
    Type = "logicmonitor.increment",
    Payload = new Dictionary<string, object>
    {
        ["metric"] = "requests_total",
        ["amount"] = 1.0
    }
};
await plugin.OnMessageAsync(msg);

// Record gauge
var msg = new PluginMessage
{
    Type = "logicmonitor.record",
    Payload = new Dictionary<string, object>
    {
        ["metric"] = "temperature_celsius",
        ["value"] = 23.5
    }
};
await plugin.OnMessageAsync(msg);

// Get status
var statusMsg = new PluginMessage
{
    Type = "logicmonitor.status",
    Payload = new Dictionary<string, object>()
};
await plugin.OnMessageAsync(statusMsg);
var status = statusMsg.Payload["result"];
```

## Authentication

### LMv1 Signature (Default)

LMv1 uses HMAC-SHA256 signature authentication:

1. Set `UseBearerAuth = false` (default)
2. Provide `AccessId` and `AccessKey`
3. Plugin automatically generates signatures for each request

**Signature Format:**
```
LMv1 {AccessId}:{Base64(HMAC-SHA256(AccessKey, StringToSign))}:{EpochMillis}
```

### Bearer Token

Bearer token authentication is simpler but requires managing tokens:

1. Set `UseBearerAuth = true`
2. Provide `BearerToken`
3. Ensure token has appropriate permissions

## Metric Types

### Counter
Monotonically increasing values (e.g., request counts, errors)

```csharp
plugin.IncrementCounter("http_requests_total");
plugin.IncrementCounter("bytes_sent_total", 1024);
```

### Gauge
Current value that can go up or down (e.g., CPU usage, temperature)

```csharp
plugin.RecordMetric("cpu_percent", 67.5);
plugin.RecordMetric("active_connections", 142);
```

### Histogram
Distribution of values with automatic averaging (e.g., response times)

```csharp
plugin.ObserveHistogram("request_duration_seconds", 0.245);
plugin.ObserveHistogram("payload_size_bytes", 4096);
```

## API Endpoint

The plugin pushes metrics to:
```
POST https://{AccountName}.logicmonitor.com/rest/metric/ingest
```

**Payload Structure:**
```json
{
  "resource": {
    "system.displayName": "DataWarehouse",
    "system.hostname": "datawarehouse_metrics",
    "properties": {
      "environment": "production"
    }
  },
  "dataPoints": [
    {
      "dataSource": "datawarehouse_telemetry",
      "dataSourceDisplayName": "DataWarehouse Telemetry",
      "dataPointName": "requests_total",
      "dataPointAggregationType": "sum",
      "instanceName": "datawarehouse_metrics",
      "values": {
        "1706234567": "1234.5"
      }
    }
  ]
}
```

## Monitoring

### Plugin Status

```csharp
var status = new PluginMessage { Type = "logicmonitor.status" };
await plugin.OnMessageAsync(status);
var result = status.Payload["result"] as Dictionary<string, object>;

Console.WriteLine($"Running: {result["isRunning"]}");
Console.WriteLine($"Push Count: {result["pushCount"]}");
Console.WriteLine($"Errors: {result["pushErrors"]}");
Console.WriteLine($"DataPoints Pushed: {result["dataPointsPushed"]}");
```

### Properties

- `IsRunning`: Whether the plugin is active
- `PushCount`: Total number of successful metric pushes
- `PushErrors`: Total number of push errors
- `DataPointsPushed`: Total datapoints sent to LogicMonitor

## Error Handling

The plugin includes comprehensive error handling:

- **Connection Errors**: Logged to stderr, retry on next interval
- **Authentication Failures**: Logged with details
- **Batch Failures**: Individual batches fail independently
- **Shutdown Errors**: Gracefully ignored during cleanup

## Best Practices

1. **Push Interval**: Use 60-300 seconds for production (LogicMonitor recommendation)
2. **Batch Size**: 100-500 datapoints per batch for optimal performance
3. **Resource Properties**: Add environment, region, cluster for filtering
4. **Metric Naming**: Use snake_case with units (e.g., `requests_total`, `duration_seconds`)
5. **Authentication**: Prefer LMv1 signature for security, rotate keys regularly

## Integration Example

```csharp
// Program.cs
var config = new LogicMonitorConfiguration
{
    AccountName = Environment.GetEnvironmentVariable("LM_ACCOUNT") ?? "default",
    AccessId = Environment.GetEnvironmentVariable("LM_ACCESS_ID") ?? "",
    AccessKey = Environment.GetEnvironmentVariable("LM_ACCESS_KEY") ?? "",
    ResourceId = "datawarehouse_prod",
    ResourceName = "DataWarehouse Production",
    PushIntervalSeconds = 60,
    ResourceProperties = new Dictionary<string, string>
    {
        ["app"] = "datawarehouse",
        ["env"] = "production",
        ["version"] = "1.0.0"
    }
};

var metricsPlugin = new LogicMonitorPlugin(config);
await metricsPlugin.StartAsync(CancellationToken.None);

// Use throughout application
metricsPlugin.IncrementCounter("app_started_total");
metricsPlugin.RecordMetric("startup_duration_seconds", 2.34);

// On shutdown
await metricsPlugin.StopAsync();
```

## Plugin ID

```
com.datawarehouse.telemetry.logicmonitor
```

## Version

1.0.0

## Dependencies

- DataWarehouse.SDK
- .NET 10.0
- System.Net.Http
- System.Text.Json
- System.Security.Cryptography

## License

Same as DataWarehouse project.
