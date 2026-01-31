# DataWarehouse Software WORM Plugin

Software-enforced Write-Once-Read-Many (WORM) storage provider for DataWarehouse.

## Overview

This plugin implements WORM storage using file system with JSON metadata sidecars for retention policy and legal hold enforcement. It provides immutable storage capabilities without requiring specialized hardware.

## Features

- **Write-Once Guarantee**: Objects cannot be overwritten once written
- **Retention Period Enforcement**: Objects cannot be deleted before retention expires
- **Legal Holds**: Prevent deletion even after retention expires
- **Retention Extension**: Can extend retention periods (never shorten)
- **Content Integrity**: SHA-256 hash verification
- **Audit Trail**: Author and comment tracking for all operations

## Storage Structure

```
{basePath}/
├── {objectId}.dat          # Actual data file (read-only)
└── {objectId}.meta.json    # Metadata sidecar (read-only)
```

## Usage

```csharp
// Initialize plugin
var wormPlugin = new SoftwareWormPlugin("./worm-storage");
await wormPlugin.StartAsync(CancellationToken.None);

// Write data with 7-year retention
var objectId = Guid.NewGuid();
var data = new MemoryStream(Encoding.UTF8.GetBytes("Immutable data"));
var retention = WormRetentionPolicy.Standard(TimeSpan.FromDays(7 * 365));
var context = new WriteContext
{
    Author = "system",
    Comment = "Initial write"
};

var result = await wormPlugin.WriteAsync(objectId, data, retention, context);

// Read data
var readStream = await wormPlugin.ReadAsync(objectId);

// Check status
var status = await wormPlugin.GetStatusAsync(objectId);
Console.WriteLine($"Can delete: {status.CanDelete}");

// Place legal hold
await wormPlugin.PlaceLegalHoldAsync(objectId, "HOLD-001", "Litigation hold");

// Extend retention
var newExpiry = DateTimeOffset.UtcNow.AddYears(10);
await wormPlugin.ExtendRetentionAsync(objectId, newExpiry);

// Remove legal hold
await wormPlugin.RemoveLegalHoldAsync(objectId, "HOLD-001");
```

## Configuration

Constructor parameters:

- `basePath` (optional): Base directory for WORM storage. Defaults to `./worm-storage` if not specified.

## Metadata Structure

Each object has a JSON metadata sidecar file containing:

```json
{
  "ObjectId": "guid",
  "WrittenAt": "timestamp",
  "RetentionExpiry": "timestamp",
  "ContentHash": "hex-string",
  "HashAlgorithm": "SHA256",
  "SizeBytes": 1024,
  "Author": "username",
  "Comment": "operation comment",
  "LegalHolds": [
    {
      "HoldId": "HOLD-001",
      "Reason": "Litigation hold",
      "PlacedAt": "timestamp",
      "PlacedBy": "admin"
    }
  ],
  "DataFilePath": "path/to/data.dat",
  "ProviderMetadata": {}
}
```

## Security Considerations

### Software Enforcement Limitations

This plugin uses **software-enforced** immutability, which means:

- ✅ Protects against application-level overwrites and deletions
- ✅ Enforces retention policies at the API level
- ✅ Sets files to read-only for additional protection
- ❌ Can be bypassed with direct filesystem access
- ❌ No protection against admin/root users
- ❌ Not suitable for regulatory compliance requiring hardware WORM

### Additional Protection

To enhance security:

1. **File System Permissions**: Restrict access to WORM storage directory
2. **Audit Logging**: Monitor file system access to WORM files
3. **Backup**: Replicate to hardware WORM or immutable cloud storage
4. **Network Isolation**: Store WORM data on separate, access-controlled volumes

## Compliance Use Cases

Suitable for:
- Development and testing environments
- Internal data retention policies
- Backup protection (logical immutability)
- Cost-effective WORM implementation

Not suitable for:
- SEC 17a-4 compliance (requires hardware WORM)
- FINRA record retention (hardware immutability required)
- Medical records under strict regulations
- Legal evidence chain of custody

## API Reference

### IWormStorageProvider Methods

- `WriteAsync()`: Write data with retention policy
- `ReadAsync()`: Read immutable data
- `GetStatusAsync()`: Get object status including retention and holds
- `ExistsAsync()`: Check if object exists
- `ExtendRetentionAsync()`: Extend retention period
- `PlaceLegalHoldAsync()`: Place legal hold on object
- `RemoveLegalHoldAsync()`: Remove legal hold from object
- `GetLegalHoldsAsync()`: Get all legal holds for object

### Properties

- `EnforcementMode`: Returns `WormEnforcementMode.Software`
- `Id`: `com.datawarehouse.worm.software`
- `Name`: "Software WORM Provider"
- `Version`: "1.0.0"

## Performance

- **Write**: O(n) where n is data size (single pass for hash + write)
- **Read**: O(1) file system read
- **Status**: O(1) metadata read
- **Legal Hold Operations**: O(1) metadata read/write

## Thread Safety

- Write operations are protected with locks to prevent concurrent writes
- Metadata updates are atomic (read-only file attribute + overwrite)
- Read operations are thread-safe (read-only access)

## Error Handling

- `InvalidOperationException`: Attempted overwrite of existing WORM object
- `KeyNotFoundException`: Object does not exist
- `ArgumentException`: Invalid parameters (empty GUID, negative retention, etc.)

## License

Licensed under the MIT License. See LICENSE file for details.
