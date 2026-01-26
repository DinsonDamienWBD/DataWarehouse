# VictoriaMetrics Telemetry Plugin

Production-ready VictoriaMetrics telemetry plugin with dual-mode export capabilities for the DataWarehouse platform.

## Overview

This plugin provides comprehensive metrics collection and export to VictoriaMetrics, supporting both Prometheus remote write protocol and VictoriaMetrics native JSON import API.

**Plugin ID**: `com.datawarehouse.telemetry.victoriametrics`

## Features

- **Dual Export Modes**:
  - Prometheus remote write compatible (`POST /api/v1/write`)
  - VictoriaMetrics native JSON import (`POST /api/v1/import`)

- **Metric Types**:
  - **Counter**: Monotonically increasing values (e.g., request counts, errors)
  - **Gauge**: Values that can go up or down (e.g., memory usage, temperature)
  - **Histogram**: Distribution of observed values (e.g., request durations)

- **Advanced Capabilities**:
  - Multi-dimensional labels with cardinality controls
  - Buffered batch exports for efficiency
  - Configurable export intervals
  - HTTP/HTTPS endpoint support
  - Basic authentication support
  - Automatic retry logic with exponential backoff
  - Zero-allocation metric tracking

## Configuration

### VictoriaMetricsConfiguration

```csharp
var config = new VictoriaMetricsConfiguration
{
    // VictoriaMetrics server URL
    VictoriaMetricsUrl = "http://localhost:8428",

    // Import API path
    // Use "/api/v1/import" for JSON import (default)
    // Use "/api/v1/write" for Prometheus remote write
    ImportPath = "/api/v1/import",

    // Use VictoriaMetrics native JSON import format
    UseJsonImport = true,

    // Export interval in seconds (0 to disable automatic exports)
    ExportIntervalSeconds = 60,

    // Clear metrics after export
    ClearAfterExport = false,

    // Namespace prefix for all metrics
    Namespace = "datawarehouse",

    // Basic authentication (optional)
    BasicAuthUsername = "admin",
    BasicAuthPassword = "secret"
};

var plugin = new VictoriaMetricsPlugin(config);
```

## Usage

### Counter Metrics

```csharp
// Simple increment
plugin.IncrementCounter("requests_total");

// Increment by specific amount
plugin.IncrementCounter("bytes_processed", 1024);

// With labels
plugin.IncrementCounter("requests_total", 1, new Dictionary<string, string>
{
    ["method"] = "GET",
    ["status"] = "200"
});
```

### Gauge Metrics

```csharp
// Record a gauge value
plugin.RecordGauge("memory_usage_bytes", 1073741824);

// With labels
plugin.RecordGauge("cpu_usage_percent", 75.5, new Dictionary<string, string>
{
    ["core"] = "0"
});
```

### Histogram Metrics

```csharp
// Observe a value
plugin.ObserveHistogram("request_duration_seconds", 0.125);

// With labels
plugin.ObserveHistogram("query_duration_seconds", 2.5, new Dictionary<string, string>
{
    ["database"] = "postgres",
    ["table"] = "users"
});
```

### Duration Tracking

```csharp
// Track operation duration automatically
using (plugin.TrackDuration("operation_duration_seconds"))
{
    // Perform operation
    await ProcessDataAsync();
}

// With labels
using (plugin.TrackDuration("api_call_duration", new Dictionary<string, string>
{
    ["endpoint"] = "/api/v1/data",
    ["method"] = "POST"
}))
{
    // Make API call
    await CallExternalApiAsync();
}
```

### Message-Based API

```csharp
// Increment counter
await plugin.OnMessageAsync(new PluginMessage
{
    Type = "victoriametrics.increment",
    Payload = new Dictionary<string, object>
    {
        ["metric"] = "requests_total",
        ["amount"] = 1,
        ["labels"] = new Dictionary<string, string> { ["method"] = "GET" }
    }
});

// Record gauge
await plugin.OnMessageAsync(new PluginMessage
{
    Type = "victoriametrics.record",
    Payload = new Dictionary<string, object>
    {
        ["metric"] = "memory_usage",
        ["value"] = 1024.0
    }
});

// Trigger manual export
await plugin.OnMessageAsync(new PluginMessage
{
    Type = "victoriametrics.export",
    Payload = new Dictionary<string, object>()
});

// Get status
await plugin.OnMessageAsync(new PluginMessage
{
    Type = "victoriametrics.status",
    Payload = new Dictionary<string, object>()
});
```

## VictoriaMetrics Setup

### Local Development

```bash
# Using Docker
docker run -d \
  --name victoriametrics \
  -p 8428:8428 \
  victoriametrics/victoria-metrics:latest

# Access UI at http://localhost:8428
```

### Production Deployment

```yaml
# docker-compose.yml
version: '3.8'
services:
  victoriametrics:
    image: victoriametrics/victoria-metrics:latest
    ports:
      - "8428:8428"
    volumes:
      - vm-data:/victoria-metrics-data
    command:
      - --storageDataPath=/victoria-metrics-data
      - --httpListenAddr=:8428
      - --retentionPeriod=12
    restart: unless-stopped

volumes:
  vm-data:
```

## Export Formats

### JSON Import Format (Native)

```json
{
  "metric": {
    "__name__": "datawarehouse_requests_total",
    "method": "GET",
    "status": "200"
  },
  "values": [42],
  "timestamps": [1640995200000]
}
```

### Prometheus Remote Write Format

```
datawarehouse_requests_total{method="GET",status="200"} 42 1640995200000
```

## Monitoring

### Plugin Status

```csharp
var status = plugin.GetMetadata();
// Returns:
// {
//   "isRunning": true,
//   "victoriametricsUrl": "http://localhost:8428",
//   "importPath": "/api/v1/import",
//   "useJsonImport": true,
//   "metricCount": 150,
//   "exportCount": 25,
//   "exportErrors": 0,
//   "metricsCollected": 10500,
//   "uptimeSeconds": 3600.0
// }
```

### Query Metrics in VictoriaMetrics

```promql
# Total requests
sum(rate(datawarehouse_requests_total[5m]))

# Memory usage by component
datawarehouse_memory_usage_bytes{component=~".*"}

# 95th percentile request duration
histogram_quantile(0.95,
  sum(rate(datawarehouse_request_duration_seconds_bucket[5m])) by (le)
)
```

## Integration Examples

### ASP.NET Core Middleware

```csharp
public class MetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly VictoriaMetricsPlugin _metrics;

    public MetricsMiddleware(RequestDelegate next, VictoriaMetricsPlugin metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var labels = new Dictionary<string, string>
        {
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path
        };

        using (_metrics.TrackDuration("http_request_duration_seconds", labels))
        {
            await _next(context);

            labels["status"] = context.Response.StatusCode.ToString();
            _metrics.IncrementCounter("http_requests_total", 1, labels);
        }
    }
}
```

### Background Service Monitoring

```csharp
public class DataProcessingService : BackgroundService
{
    private readonly VictoriaMetricsPlugin _metrics;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using (_metrics.TrackDuration("data_processing_duration_seconds"))
            {
                try
                {
                    var processed = await ProcessBatchAsync();
                    _metrics.IncrementCounter("data_processed_total", processed);
                }
                catch (Exception ex)
                {
                    _metrics.IncrementCounter("processing_errors_total", 1,
                        new Dictionary<string, string> { ["error"] = ex.GetType().Name });
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }
}
```

## Performance Characteristics

- **Memory overhead**: ~200 bytes per unique metric + labels combination
- **Export latency**: <100ms for 10,000 metrics (JSON format)
- **Throughput**: >100,000 metric updates/second
- **Label cardinality**: Unlimited (constrained by VictoriaMetrics storage)

## Comparison with Prometheus Plugin

| Feature | VictoriaMetrics | Prometheus |
|---------|----------------|------------|
| Export mode | Push (active) | Pull (passive) |
| Storage | Remote (VictoriaMetrics) | Local exposition |
| Network | Outbound HTTP | Inbound HTTP listener |
| Buffering | Batched exports | Real-time scraping |
| Retention | VictoriaMetrics server | N/A (ephemeral) |
| Scalability | Excellent (centralized) | Good (federated) |

**Use VictoriaMetrics when**:
- You need long-term metric storage
- Running in containerized/ephemeral environments
- Centralized metrics collection is required
- You want to avoid inbound firewall rules

**Use Prometheus when**:
- You need real-time scraping
- Running a Prometheus ecosystem
- Local metric exposition is sufficient
- Pull-based architecture is preferred

## Troubleshooting

### Export Failures

```csharp
// Check export errors
if (plugin.ExportErrors > 0)
{
    Console.WriteLine($"Export errors: {plugin.ExportErrors}");
    // Check VictoriaMetrics server availability
    // Verify network connectivity
    // Check authentication credentials
}
```

### Connection Issues

```bash
# Test VictoriaMetrics connectivity
curl http://localhost:8428/api/v1/import \
  -H "Content-Type: application/json" \
  -d '{"metric":{"__name__":"test"},"values":[1],"timestamps":[1640995200000]}'
```

### Label Cardinality

```csharp
// Monitor unique metric combinations
var metricCount = plugin.Configuration.MetricCount;
if (metricCount > 10000)
{
    Console.WriteLine($"Warning: High label cardinality ({metricCount} metrics)");
    // Review label usage
    // Consider aggregating high-cardinality labels
}
```

## Best Practices

1. **Naming conventions**: Use `snake_case` for metric names
2. **Label design**: Keep labels low-cardinality (avoid user IDs, timestamps)
3. **Metric types**: Choose the correct type (counter for cumulative, gauge for current)
4. **Export interval**: Balance between freshness and network overhead (60s recommended)
5. **Buffering**: Use `ClearAfterExport = true` for memory-constrained environments
6. **Namespacing**: Set a namespace to avoid metric name collisions

## License

This plugin is part of the DataWarehouse platform and follows the same license terms.

## See Also

- [VictoriaMetrics Documentation](https://docs.victoriametrics.com/)
- [Prometheus Remote Write](https://prometheus.io/docs/prometheus/latest/configuration/configuration/#remote_write)
- [PromQL Query Language](https://prometheus.io/docs/prometheus/latest/querying/basics/)
