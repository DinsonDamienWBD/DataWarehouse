# DataWarehouse.Plugins.AccessLog

Production-ready access logging provider for tamper attribution and forensic analysis.

## Overview

The Default Access Log Plugin provides a robust, file-based implementation of access logging with:
- **Append-only log files** stored in date-based hierarchy
- **Automatic rotation** by date and hour
- **Thread-safe operations** with semaphore-based locking
- **Efficient querying** with time-range optimization
- **Tamper attribution analysis** with confidence scoring
- **Compliance-ready audit trail** in JSONL format

## Storage Layout

```
logs/access/
  2026/
    01/
      29/
        access_20260129_14.log  (entries for hour 14:00-14:59 UTC)
        access_20260129_15.log  (entries for hour 15:00-15:59 UTC)
```

## Features

### File-Based Storage
- **Format**: JSONL (JSON Lines) - one JSON object per line
- **Rotation**: Automatic hourly rotation
- **Structure**: Hierarchical date-based directories (YYYY/MM/DD)
- **Thread-Safety**: Semaphore-based write locking
- **Performance**: Buffered writes with manual flush control

### Tamper Attribution
- Comprehensive attribution analysis when tampering is detected
- Confidence-scored suspect identification
- Evidence collection and behavioral analysis
- Multi-factor likelihood scoring:
  - Write operations (strong indicator)
  - Administrative operations (moderate indicator)
  - Temporal proximity to detection
  - Access frequency patterns
  - Failed attempts followed by success

### Query Capabilities
- Object ID filtering
- Principal filtering (with wildcard support)
- Access type filtering
- Time range filtering (optimized file scanning)
- Session ID filtering
- Client IP filtering
- Success/failure filtering
- Hash presence filtering
- Flexible sorting and pagination

## Configuration

```csharp
var config = new Dictionary<string, object>
{
    ["LogBasePath"] = "logs/access"  // Default: "logs/access"
};

var plugin = new DefaultAccessLogPlugin();
await plugin.StartAsync(CancellationToken.None);
```

## Usage Examples

### Logging Access

```csharp
// Log a successful read operation
var entry = AccessLogEntry.CreateRead(
    objectId: objectGuid,
    principal: "user:john.doe",
    durationMs: 45,
    bytesTransferred: 1024000
);
await accessLogProvider.LogAccessAsync(entry);

// Log a successful write operation
var writeEntry = AccessLogEntry.CreateWrite(
    objectId: objectGuid,
    principal: "service:backup-agent",
    computedHash: "sha256:abc123...",
    durationMs: 120,
    bytesTransferred: 5242880
);
await accessLogProvider.LogAccessAsync(writeEntry);

// Log a failed operation
var failedEntry = AccessLogEntry.CreateFailed(
    objectId: objectGuid,
    accessType: AccessType.Read,
    principal: "user:jane.smith",
    errorMessage: "Access denied: insufficient permissions"
);
await accessLogProvider.LogAccessAsync(failedEntry);
```

### Querying Logs

```csharp
// Get all access history for an object
var history = await accessLogProvider.GetAccessHistoryAsync(
    objectId: objectGuid,
    from: DateTimeOffset.UtcNow.AddDays(-7),
    to: DateTimeOffset.UtcNow
);

// Query suspicious write operations
var suspiciousWrites = await accessLogProvider.QueryAsync(
    new AccessLogQuery
    {
        ObjectId = objectGuid,
        AccessTypes = new HashSet<AccessType> { AccessType.Write, AccessType.Correct },
        SucceededOnly = true,
        WithHashOnly = true,
        StartTime = DateTimeOffset.UtcNow.AddHours(-24)
    }
);

// Get summary statistics
var summary = await accessLogProvider.GetSummaryAsync(objectGuid);
Console.WriteLine($"Total accesses: {summary.TotalAccesses}");
Console.WriteLine($"Unique principals: {summary.UniquePrincipals}");
Console.WriteLine($"Suspicious patterns: {summary.HasSuspiciousPatterns}");
```

### Tamper Attribution Analysis

```csharp
// Analyze tampering when hash mismatch detected
var analysis = await accessLogProvider.AnalyzeForAttributionAsync(
    objectId: objectGuid,
    tamperDetectedAt: DateTimeOffset.UtcNow,
    lookbackWindow: TimeSpan.FromHours(72)
);

Console.WriteLine($"Confidence: {analysis.Confidence}");
Console.WriteLine($"Suspected principals: {analysis.SuspectedPrincipals.Count}");

if (analysis.SuspectedPrincipals.Any())
{
    var topSuspect = analysis.SuspectedPrincipals.First();
    Console.WriteLine($"Top suspect: {topSuspect.Principal}");
    Console.WriteLine($"Likelihood: {topSuspect.Likelihood:P}");
    Console.WriteLine($"Last access: {topSuspect.LastAccessTime}");

    foreach (var evidence in topSuspect.SpecificEvidence ?? [])
    {
        Console.WriteLine($"  - {evidence}");
    }
}

// Generate human-readable report
Console.WriteLine(analysis.GetSummary());
```

### Purging Old Logs

```csharp
// Delete logs older than 90 days
await accessLogProvider.PurgeAsync(
    olderThan: DateTimeOffset.UtcNow.AddDays(-90)
);
```

## Performance Characteristics

| Operation | Time Complexity | Notes |
|-----------|-----------------|-------|
| Append | O(1) | Constant time with buffered writes |
| Query by time range | O(n) | n = entries in relevant files; files outside range are skipped |
| Purge old logs | O(m) | m = number of old files to delete |
| Full scan | O(N) | N = total entries across all files |

## Thread Safety

All operations are thread-safe and can be called concurrently:
- **Write operations**: Protected by semaphore-based locking
- **Read operations**: Safe concurrent reads from multiple threads
- **File rotation**: Automatic with thread-safe writer management

## Compliance & Security

### Audit Trail Features
- **Immutable logs**: Append-only files prevent tampering
- **Tamper detection**: Hash chain verification via `ComputeEntryHash()`
- **Complete chain of custody**: Full access history with principals
- **Forensic analysis**: Attribution analysis with confidence scoring

### Compliance Frameworks
Suitable for:
- HIPAA (Health Insurance Portability and Accountability Act)
- SOX (Sarbanes-Oxley)
- GDPR (General Data Protection Regulation)
- PCI-DSS (Payment Card Industry Data Security Standard)

## Production Recommendations

### Storage Management
- Monitor log directory size regularly
- Implement automated purge policies based on retention requirements
- Consider log archival to cold storage before purging
- Use SSD storage for hot logs, HDD for archival

### Performance Tuning
- Adjust rotation frequency based on write volume
- Consider compression for archived logs
- Use asynchronous logging for high-throughput scenarios
- Implement log forwarding to SIEM for real-time analysis

### Monitoring
- Track log file count and total size
- Monitor write latency and throughput
- Set alerts for purge failures or disk space issues
- Verify log integrity periodically

## Plugin Metadata

```json
{
  "Id": "com.datawarehouse.accesslog.default",
  "Name": "Default Access Log Provider",
  "Version": "1.0.0",
  "Category": "FeatureProvider",
  "Description": "File-based access logging with date rotation, thread-safe operations, and tamper attribution analysis",
  "StorageType": "File-based",
  "RotationStrategy": "Hourly",
  "Format": "JSONL",
  "ThreadSafe": true,
  "SupportsRangeQueries": true,
  "SupportsPurge": true
}
```

## License

Licensed to the DataWarehouse under one or more agreements.
DataWarehouse licenses this file under the MIT license.
