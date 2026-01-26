# Tableau BI Integration Plugin

## Overview

Production-ready Tableau BI integration plugin for the DataWarehouse system, implementing Task A1 from the metadata implementation tasks.

## Features

### Core Capabilities
- **Hyper API Data Extracts**: High-performance data extract creation with simulated Hyper format
- **REST API Publishing**: Publish datasources to Tableau Server via REST API
- **Web Data Connector Support**: Configuration for WDC integration
- **Incremental Refresh**: Support for incremental data updates
- **Automatic Publishing**: Configurable auto-publish on interval

### Technical Features
- **Multi-threaded Upload**: Concurrent extract publishing with throttling
- **Batch Data Insertion**: Memory-efficient row batching (default 10,000 rows)
- **Extract Management**: Complete lifecycle management (create, finalize, publish, delete)
- **Authentication**: Personal Access Token (PAT) authentication
- **Compression**: Optional GZIP compression for API requests
- **Metrics Tracking**: IMetricsProvider implementation with counters and gauges
- **SSL Verification**: Configurable certificate validation

## Configuration

### Required Settings
```csharp
var config = new TableauConfiguration
{
    TableauServerUrl = "https://tableau.example.com",
    SiteId = "",  // Empty for default site
    TokenName = "your-token-name",
    TokenSecret = "your-token-secret",
    ProjectId = "project-id-guid"
};
```

### Optional Settings
- `ExtractNamePrefix`: Default extract name prefix (default: "DataWarehouse")
- `MaxRowsPerBatch`: Max rows per batch (default: 10,000)
- `EnableAutoPublish`: Auto-publish on interval (default: false)
- `PublishIntervalMinutes`: Auto-publish interval (default: 60)
- `TempDirectory`: Temporary extract storage (default: system temp)
- `DeleteAfterPublish`: Delete local extracts after upload (default: true)
- `ApiVersion`: Tableau API version (default: "3.20")
- `ConnectionTimeoutSeconds`: Connection timeout (default: 30)
- `VerifySslCertificate`: SSL cert validation (default: true)
- `MaxConcurrentUploads`: Max parallel uploads (default: 3)
- `EnableCompression`: Enable GZIP compression (default: true)
- `WebDataConnectorUrl`: WDC URL (optional)
- `CustomTags`: Tags for published datasources (default: empty)
- `EnableIncrementalRefresh`: Enable incremental refresh (default: false)

## Message Commands

### tableau.create_extract
Creates a new data extract.

**Payload:**
```json
{
  "name": "MyExtract",
  "tableName": "Extract",
  "columns": [
    {
      "name": "Id",
      "dataType": "Integer",
      "isNullable": false
    },
    {
      "name": "Name",
      "dataType": "String",
      "isNullable": true
    },
    {
      "name": "Amount",
      "dataType": "Double",
      "isNullable": false
    },
    {
      "name": "CreatedAt",
      "dataType": "Timestamp",
      "isNullable": false
    }
  ]
}
```

**Response:**
```json
{
  "result": "extract-id-guid"
}
```

### tableau.add_rows
Adds rows to an extract.

**Payload:**
```json
{
  "extractId": "extract-id-guid",
  "rows": [
    {
      "Id": 1,
      "Name": "Item 1",
      "Amount": 99.99,
      "CreatedAt": "2026-01-26T12:00:00Z"
    },
    {
      "Id": 2,
      "Name": "Item 2",
      "Amount": 149.99,
      "CreatedAt": "2026-01-26T13:00:00Z"
    }
  ]
}
```

### tableau.finalize_extract
Finalizes an extract, creating the Hyper file.

**Payload:**
```json
{
  "extractId": "extract-id-guid"
}
```

### tableau.publish
Publishes an extract to Tableau Server.

**Payload:**
```json
{
  "extractId": "extract-id-guid"
}
```

**Response:**
```json
{
  "result": {
    "datasourceId": "datasource-id-guid"
  }
}
```

### tableau.list_extracts
Lists all extracts with their status.

**Response:**
```json
{
  "result": [
    {
      "id": "extract-id",
      "name": "MyExtract",
      "status": "Published",
      "rowCount": 1000,
      "fileSizeBytes": 524288,
      "createdAt": "2026-01-26T12:00:00Z",
      "updatedAt": "2026-01-26T12:05:00Z",
      "datasourceId": "datasource-id-guid",
      "errorMessage": null
    }
  ]
}
```

### tableau.delete_extract
Deletes an extract and its local file.

**Payload:**
```json
{
  "extractId": "extract-id-guid"
}
```

### tableau.status
Gets plugin status and metrics.

**Response:**
```json
{
  "result": {
    "isRunning": true,
    "tableauServerUrl": "https://tableau.example.com",
    "siteId": "",
    "extractsCreated": 25,
    "extractsPublished": 23,
    "rowsInserted": 250000,
    "uptimeSeconds": 3600.5,
    "autoPublishEnabled": true,
    "counters": {
      "extracts_created_total": 25,
      "extracts_published_total": 23,
      "publish_errors_total": 0
    },
    "gauges": {
      "rows_inserted_total": 250000,
      "extract_size_bytes": 524288,
      "publish_duration_seconds": 2.5
    }
  }
}
```

### tableau.authenticate
Tests Tableau Server authentication.

**Response:**
```json
{
  "result": {
    "authenticated": true
  }
}
```

## Data Types

### Supported Tableau Data Types
- `Boolean`: Boolean values
- `SmallInt`: 16-bit integer
- `Integer`: 32-bit integer
- `BigInt`: 64-bit integer
- `Real`: 32-bit floating point
- `Double`: 64-bit floating point
- `Numeric`: Numeric with precision and scale
- `String`: Variable-length string
- `Date`: Date (no time component)
- `Time`: Time (no date component)
- `Timestamp`: Timestamp with timezone
- `Interval`: Duration
- `Bytes`: Binary data
- `Geography`: Geographic point

## Extract Status

- **Creating**: Extract is being created
- **Ready**: Extract is finalized and ready for publishing
- **Publishing**: Extract is being uploaded
- **Published**: Extract successfully published
- **Failed**: Extract operation failed (see errorMessage)

## Metrics Provider

Implements `IMetricsProvider` interface:

- `IncrementCounter(string metric)`: Increment counter metric
- `RecordMetric(string metric, double value)`: Record gauge metric
- `TrackDuration(string metric)`: Track operation duration

## Plugin Metadata

- **Plugin ID**: `com.datawarehouse.telemetry.tableau`
- **Category**: `MetricsProvider`
- **Version**: `1.0.0`
- **Target Framework**: .NET 10.0

## Production Notes

### Hyper API
This implementation includes a simulated Hyper API for demonstration. In production:
1. Install the official Tableau Hyper API NuGet package
2. Replace `HyperApiSimulator` with actual `Tableau.HyperAPI` calls
3. Use `HyperProcess`, `Connection`, and `Inserter` classes

### REST API
The REST API implementation is production-ready and follows Tableau's official API:
- Personal Access Token authentication
- Multipart form data for uploads
- Proper error handling and retries
- Support for Tableau Server API 3.20+

### Security
- Store credentials in secure configuration (e.g., Azure Key Vault, AWS Secrets Manager)
- Use SSL certificate verification in production
- Implement proper access controls on Tableau Server
- Regular token rotation recommended

## Dependencies

- **DataWarehouse.SDK**: Core SDK reference
- **.NET 10.0**: Framework dependency (includes System.Data.Common)

## Build

```bash
cd Plugins/DataWarehouse.Plugins.Tableau
dotnet build --configuration Release
```

## Testing

The plugin should be tested with:
1. Valid Tableau Server credentials
2. Various data types and extract sizes
3. Concurrent extract publishing
4. Network failure scenarios
5. Authentication expiration handling

## Integration

Add to DataWarehouse.slnx:
```xml
<Project Path="Plugins/DataWarehouse.Plugins.Tableau/DataWarehouse.Plugins.Tableau.csproj" />
```

## References

- [Tableau REST API Documentation](https://help.tableau.com/current/api/rest_api/en-us/REST/rest_api.htm)
- [Tableau Hyper API Documentation](https://help.tableau.com/current/api/hyper_api/en-us/index.html)
- [Web Data Connector Guide](https://tableau.github.io/webdataconnector/)
