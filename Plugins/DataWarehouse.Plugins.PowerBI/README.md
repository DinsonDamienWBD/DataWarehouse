# DataWarehouse.Plugins.PowerBI

Production-ready Microsoft Power BI telemetry plugin for real-time data ingestion.

## Features

- **OAuth2 Authentication**: Azure AD authentication with automatic token refresh
- **Push Datasets API**: Batch data ingestion for historical data
- **Streaming Datasets API**: Real-time data streaming for live dashboards
- **Automatic Retry Logic**: Exponential backoff for failed requests
- **Batch Processing**: Configurable batch sizes for optimal performance
- **Metric Tracking**: Counter and gauge metrics with automatic buffering

## Configuration

```csharp
var config = new PowerBIConfiguration
{
    WorkspaceId = "your-workspace-id",
    DatasetId = "your-dataset-id",
    ClientId = "your-azure-ad-client-id",
    ClientSecret = "your-azure-ad-client-secret",
    TenantId = "your-azure-ad-tenant-id",
    UseStreamingDataset = false, // true for streaming, false for push
    BatchSize = 100,
    TimeoutSeconds = 30,
    MaxRetries = 3
};

var plugin = new PowerBIPlugin(config);
```

## Usage

### Increment Counter
```csharp
plugin.IncrementCounter("request_count");
```

### Record Gauge
```csharp
plugin.RecordMetric("memory_usage_mb", 512.5);
```

### Track Duration
```csharp
using (plugin.TrackDuration("operation_duration"))
{
    // Your operation here
}
```

### Push Custom Data
```csharp
var message = PluginMessage.Create("powerbi.push", new Dictionary<string, object>
{
    ["data"] = new Dictionary<string, object>
    {
        ["timestamp"] = DateTime.UtcNow.ToString("o"),
        ["metric"] = "custom_metric",
        ["value"] = 42.0
    }
});

await plugin.OnMessageAsync(message);
```

## Message Commands

- `powerbi.push`: Push data rows to Power BI dataset
- `powerbi.increment`: Increment a counter metric
- `powerbi.record`: Record a gauge metric value
- `powerbi.flush`: Flush buffered data immediately
- `powerbi.status`: Get plugin status
- `powerbi.test`: Test connection and authentication

## Azure AD Setup

1. Register an application in Azure AD
2. Create a client secret
3. Grant the following API permissions:
   - Power BI Service → Dataset.ReadWrite.All
4. Add the service principal to your Power BI workspace

## Power BI Dataset Setup

### For Push Datasets
1. Create a dataset in Power BI
2. Define the table schema with required columns
3. Use the dataset ID and table name in configuration

### For Streaming Datasets
1. Create a streaming dataset via the Power BI API or portal
2. Use the dataset ID in configuration
3. Data is pushed directly without historical storage

## Plugin ID

`com.datawarehouse.telemetry.powerbi`

## Requirements

- .NET 10.0
- Azure AD application with Power BI permissions
- Power BI workspace and dataset

## Production Considerations

- **Token Management**: Tokens are automatically refreshed 5 minutes before expiry
- **Rate Limiting**: Automatic exponential backoff on 429 responses
- **Buffer Management**: Data is batched and flushed every 5 seconds or when batch size is reached
- **Error Handling**: Failed requests are retried up to MaxRetries times
- **Connection Testing**: Use `powerbi.test` command to verify setup before production use

## Monitoring

Use the `powerbi.status` command to monitor:
- Push count
- Error count
- Total rows pushed
- Buffer size
- Token validity

## References

- [Power BI REST API Documentation](https://learn.microsoft.com/en-us/rest/api/power-bi/)
- [Azure AD Authentication](https://learn.microsoft.com/en-us/azure/active-directory/develop/)
- [Push Datasets API](https://learn.microsoft.com/en-us/rest/api/power-bi/push-datasets)
- [Streaming Datasets](https://learn.microsoft.com/en-us/power-bi/connect-data/service-real-time-streaming)
