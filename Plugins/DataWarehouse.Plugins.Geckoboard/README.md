# DataWarehouse.Plugins.Geckoboard

Production-ready Geckoboard dashboard integration plugin for DataWarehouse. Provides seamless integration with Geckoboard's Datasets API for real-time dashboard updates.

## Features

- **Datasets API Integration**: Push data to Geckoboard datasets for visualization
- **API Key Authentication**: Secure authentication using Geckoboard API keys
- **Batch Data Updates**: Efficient batch processing with configurable batch sizes
- **Automatic Retry Logic**: Built-in retry mechanism with exponential backoff for transient failures
- **Dataset Schema Management**: Create and manage dataset schemas programmatically
- **Error Handling**: Comprehensive error handling with detailed logging
- **IMetricsProvider Implementation**: Standard metrics interface for counters, gauges, and duration tracking

## Installation

The plugin is automatically included when building the DataWarehouse solution.

## Configuration

### Environment Variables

```bash
GECKOBOARD_API_KEY=your-api-key-here
GECKOBOARD_DATASET_ID=datawarehouse_metrics
GECKOBOARD_API_URL=https://api.geckoboard.com
```

### Programmatic Configuration

```csharp
var config = new GeckoboardPluginConfig
{
    ApiKey = "your-api-key",
    DatasetId = "datawarehouse_metrics",
    ApiBaseUrl = "https://api.geckoboard.com",
    BatchSize = 500,
    TimeoutSeconds = 30,
    EnableRetries = true,
    MaxRetries = 3,
    ValidateOnStartup = true,
    EnableVerboseLogging = false
};

var plugin = new GeckoboardPlugin(config);
await plugin.StartAsync(CancellationToken.None);
```

## Usage

### Basic Metrics Tracking

```csharp
// Increment a counter
plugin.IncrementCounter("storage.writes");

// Record a gauge value
plugin.RecordMetric("storage.size_bytes", 1024000);

// Track operation duration
using (plugin.TrackDuration("storage.operation"))
{
    // Your operation here
}
```

### Push Custom Data

```csharp
var data = new[]
{
    new Dictionary<string, object>
    {
        ["metric"] = "cpu_usage",
        ["value"] = 75.5,
        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    }
};

await plugin.PushDataAsync("my_dataset", data);
```

### Create a Dataset

```csharp
var fields = new
{
    metric = new { type = "string", name = "Metric Name" },
    value = new { type = "number", name = "Value" },
    timestamp = new { type = "datetime", name = "Timestamp" }
};

await plugin.CreateDatasetAsync("my_dataset", fields);
```

### Delete a Dataset

```csharp
await plugin.DeleteDatasetAsync("my_dataset");
```

## Message Commands

The plugin supports the following message types:

### geckoboard.push

Push data to a dataset.

```json
{
  "type": "geckoboard.push",
  "payload": {
    "datasetId": "my_dataset",
    "data": [
      {
        "metric": "cpu_usage",
        "value": 75.5,
        "timestamp": 1234567890
      }
    ]
  }
}
```

### geckoboard.create

Create a new dataset with schema.

```json
{
  "type": "geckoboard.create",
  "payload": {
    "datasetId": "my_dataset",
    "fields": {
      "metric": { "type": "string", "name": "Metric Name" },
      "value": { "type": "number", "name": "Value" }
    }
  }
}
```

### geckoboard.delete

Delete a dataset.

```json
{
  "type": "geckoboard.delete",
  "payload": {
    "datasetId": "my_dataset"
  }
}
```

### geckoboard.status

Get plugin status and statistics.

```json
{
  "type": "geckoboard.status",
  "payload": {}
}
```

## API Reference

### GeckoboardPlugin

Main plugin class implementing `IMetricsProvider`.

**Properties:**
- `Id`: "com.datawarehouse.telemetry.geckoboard"
- `Name`: "Geckoboard Dashboard Plugin"
- `Version`: "1.0.0"
- `Category`: PluginCategory.MetricsProvider
- `PushCount`: Total number of successful data pushes
- `ErrorCount`: Total number of errors encountered

**Methods:**
- `StartAsync(CancellationToken)`: Initialize and start the plugin
- `StopAsync()`: Stop the plugin and cleanup resources
- `IncrementCounter(string)`: Increment a counter metric
- `RecordMetric(string, double)`: Record a gauge value
- `TrackDuration(string)`: Track operation duration
- `PushDataAsync(string, object, CancellationToken)`: Push data to a dataset
- `CreateDatasetAsync(string, object, CancellationToken)`: Create a new dataset
- `DeleteDatasetAsync(string, CancellationToken)`: Delete a dataset

### GeckoboardPluginConfig

Configuration class for the plugin.

**Properties:**
- `ApiKey`: Geckoboard API key (required)
- `DatasetId`: Default dataset ID (required)
- `ApiBaseUrl`: API base URL (default: "https://api.geckoboard.com")
- `BatchSize`: Maximum batch size (default: 500)
- `TimeoutSeconds`: Request timeout (default: 30)
- `EnableRetries`: Enable automatic retries (default: true)
- `MaxRetries`: Maximum retry attempts (default: 3)
- `ValidateOnStartup`: Validate API connection on startup (default: true)
- `EnableVerboseLogging`: Enable detailed logging (default: false)

## Error Handling

The plugin implements comprehensive error handling:

1. **Transient Errors**: Automatically retries on timeout, rate limiting, and server errors
2. **Exponential Backoff**: Retry delays increase exponentially (1s, 2s, 4s, 8s...)
3. **Error Logging**: All errors are logged to stderr with context
4. **Error Counting**: Maintains error count statistics

## Performance Considerations

- **Batch Processing**: Configure `BatchSize` to optimize data transfer
- **Async Operations**: All API calls are async for non-blocking execution
- **Connection Pooling**: Uses HttpClient for efficient connection management
- **Timeout Control**: Configurable timeouts prevent hanging requests

## Security

- **API Key Authentication**: Uses Basic authentication with API key
- **HTTPS Only**: All API communication over secure HTTPS
- **Environment Variables**: Supports secure credential storage via environment variables

## Geckoboard Integration

This plugin integrates with Geckoboard's Datasets API:

- **Endpoint**: `POST /datasets/{id}/data`
- **Authentication**: Basic auth with API key
- **Format**: JSON payload
- **Documentation**: https://docs.geckoboard.com/

## Example Dashboards

### System Metrics Dashboard

```csharp
// Track system metrics
plugin.RecordMetric("system.cpu_percent", 45.2);
plugin.RecordMetric("system.memory_percent", 62.8);
plugin.RecordMetric("system.disk_percent", 78.5);
plugin.IncrementCounter("system.requests_total");
```

### Storage Operations Dashboard

```csharp
// Track storage operations
using (plugin.TrackDuration("storage.write_duration_ms"))
{
    await WriteDataAsync();
}
plugin.IncrementCounter("storage.writes_total");
plugin.RecordMetric("storage.size_gb", 1024.5);
```

## Troubleshooting

### Connection Errors

If you encounter connection errors:
1. Verify your API key is correct
2. Check network connectivity to api.geckoboard.com
3. Ensure firewall allows HTTPS traffic
4. Enable verbose logging for detailed diagnostics

### Dataset Not Found

If you get "dataset not found" errors:
1. Create the dataset first using `CreateDatasetAsync`
2. Verify the dataset ID matches exactly
3. Check dataset exists in Geckoboard dashboard

### Rate Limiting

If you hit rate limits:
1. Reduce the frequency of data pushes
2. Increase batch sizes to send more data per request
3. Enable retries to handle temporary rate limits

## License

Part of the DataWarehouse project.

## Related Plugins

- **Prometheus**: Metrics exposition in Prometheus format
- **OpenTelemetry**: Distributed tracing and metrics
- **Grafana Loki**: Log aggregation and querying
- **Kibana**: Elasticsearch-based analytics
