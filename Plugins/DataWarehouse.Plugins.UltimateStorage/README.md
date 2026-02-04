# UltimateStorage Plugin

Comprehensive storage backend solution for DataWarehouse, providing 50+ storage strategies across multiple categories.

## Overview

The UltimateStorage plugin consolidates all storage backend strategies into a single, unified plugin with auto-discovery, strategy registry pattern, and advanced features like multi-region support, replication, tiered storage, and automatic failover.

## Features

- **50+ Storage Backends**: Support for local, cloud, distributed, network, database, and specialized storage
- **Strategy Pattern**: Extensible architecture for adding new storage backends
- **Auto-Discovery**: Automatic registration of storage strategies from assemblies
- **Unified API**: Consistent interface across all storage backends
- **Multi-Region Support**: Deploy across multiple geographic regions
- **Replication**: Data replication across backends for redundancy
- **Tiered Storage**: Hot/Warm/Cold/Archive tier management
- **Bandwidth Throttling**: Control data transfer rates
- **Cost Optimization**: Automatic tier selection based on cost/performance
- **Lifecycle Policies**: Automated data management based on age/access patterns
- **Versioning**: Object versioning support where available
- **Access Control**: Integration with ACL systems
- **Audit Logging**: Comprehensive logging of storage operations
- **Health Monitoring**: Real-time health checks and status tracking
- **Automatic Failover**: Switch to healthy backends on failure
- **Compression**: Optional compression for stored data
- **Deduplication**: Reduce storage costs with deduplication

## Storage Categories

### Local Storage
- **FileSystem**: Local disk storage
- **Memory**: In-memory storage
- **MemoryMapped**: Memory-mapped file storage
- **RAMDisk**: RAM disk storage

### Cloud Storage
- **AWS S3**: Amazon S3 object storage
- **Azure Blob**: Microsoft Azure Blob Storage
- **Google Cloud Storage (GCS)**: Google Cloud Storage
- **MinIO**: S3-compatible object storage
- **DigitalOcean Spaces**: DigitalOcean object storage
- **Wasabi**: Wasabi hot storage
- **Backblaze B2**: Backblaze cloud storage

### Distributed Storage
- **IPFS**: InterPlanetary File System
- **Arweave**: Permanent data storage
- **Storj**: Decentralized cloud storage
- **Sia**: Decentralized storage network
- **Filecoin**: Decentralized storage network

### Network Storage
- **NFS**: Network File System
- **SMB/CIFS**: Windows file sharing
- **FTP**: File Transfer Protocol
- **SFTP**: Secure File Transfer Protocol
- **WebDAV**: Web Distributed Authoring and Versioning

### Database Storage
- **MongoDB GridFS**: Large file storage in MongoDB
- **PostgreSQL Large Objects**: Binary large objects in PostgreSQL
- **SQL Server FileStream**: FILESTREAM storage in SQL Server

### Object Storage
- **OpenStack Swift**: OpenStack object storage
- **Ceph**: Distributed object storage
- **Redis**: In-memory data structure store

### Specialized Storage
- **Tape/LTO**: Linear Tape-Open storage
- **Optical**: CD/DVD/Blu-ray storage
- **Cold Storage**: Archival storage
- **Content Addressable Storage (CAS)**: Content-based addressing

## Project Structure

```
DataWarehouse.Plugins.UltimateStorage/
├── UltimateStoragePlugin.cs          # Main plugin class
├── StorageStrategyRegistry.cs         # Strategy registry and interfaces
├── Strategies/
│   ├── Local/                         # Local storage strategies
│   ├── Cloud/                         # Cloud storage strategies
│   ├── Network/                       # Network storage strategies
│   └── Specialized/                   # Specialized storage strategies
└── Features/                          # Advanced features
```

## Capabilities

The plugin provides the following capabilities:

- `storage.write` - Write data to storage backend
- `storage.read` - Read data from storage backend
- `storage.delete` - Delete data from storage backend
- `storage.list` - List objects in storage backend
- `storage.exists` - Check if object exists
- `storage.copy` - Copy object between locations
- `storage.move` - Move object to new location
- `storage.list-strategies` - List available storage strategies
- `storage.set-default` - Set default storage strategy
- `storage.stats` - Get storage statistics
- `storage.health` - Check health of storage backends
- `storage.replicate` - Replicate data across backends
- `storage.tier` - Move data between storage tiers
- `storage.get-metadata` - Get object metadata
- `storage.set-metadata` - Set object metadata

## Configuration

### Plugin Properties

- **AuditEnabled**: Enable/disable audit logging (default: true)
- **AutoFailoverEnabled**: Enable/disable automatic failover (default: true)
- **MaxRetries**: Maximum retry attempts on failure (default: 3)

### Storage Options

- **Timeout**: Operation timeout (default: 30 seconds)
- **BufferSize**: Buffer size for streaming (default: 81920 bytes)
- **EnableCompression**: Enable compression (default: false)
- **Metadata**: Custom metadata dictionary

## Usage Examples

### Writing Data

```csharp
var message = new PluginMessage
{
    Type = "storage.write",
    Payload = new Dictionary<string, object>
    {
        ["data"] = byteArray,
        ["strategyId"] = "s3",
        ["path"] = "documents/file.txt",
        ["metadata"] = new Dictionary<string, string>
        {
            ["ContentType"] = "text/plain"
        }
    }
};
await plugin.OnMessageAsync(message);
string path = (string)message.Payload["path"];
```

### Reading Data

```csharp
var message = new PluginMessage
{
    Type = "storage.read",
    Payload = new Dictionary<string, object>
    {
        ["path"] = "documents/file.txt",
        ["strategyId"] = "s3"
    }
};
await plugin.OnMessageAsync(message);
byte[] data = (byte[])message.Payload["data"];
```

### Listing Strategies

```csharp
var message = new PluginMessage
{
    Type = "storage.list-strategies",
    Payload = new Dictionary<string, object>()
};
await plugin.OnMessageAsync(message);
var strategies = (List<Dictionary<string, object>>)message.Payload["strategies"];
```

### Health Check

```csharp
var message = new PluginMessage
{
    Type = "storage.health",
    Payload = new Dictionary<string, object>()
};
await plugin.OnMessageAsync(message);
var health = (Dictionary<string, object>)message.Payload["health"];
```

### Data Replication

```csharp
var message = new PluginMessage
{
    Type = "storage.replicate",
    Payload = new Dictionary<string, object>
    {
        ["path"] = "documents/file.txt",
        ["sourceStrategy"] = "s3",
        ["targetStrategies"] = new[] { "azure-blob", "gcs" }
    }
};
await plugin.OnMessageAsync(message);
```

### Tiering Data

```csharp
var message = new PluginMessage
{
    Type = "storage.tier",
    Payload = new Dictionary<string, object>
    {
        ["path"] = "documents/file.txt",
        ["sourceStrategy"] = "s3-hot",
        ["targetStrategy"] = "s3-glacier",
        ["deleteSource"] = true
    }
};
await plugin.OnMessageAsync(message);
```

## NuGet Packages

### Cloud Providers
- **AWSSDK.S3**: AWS S3 integration
- **Azure.Storage.Blobs**: Azure Blob Storage
- **Azure.Identity**: Azure authentication
- **Google.Cloud.Storage.V1**: Google Cloud Storage
- **Minio**: MinIO (S3-compatible)

### Database Storage
- **MongoDB.Driver**: MongoDB integration
- **MongoDB.Driver.GridFS**: MongoDB GridFS
- **Npgsql**: PostgreSQL client

### Distributed Storage
- **Ipfs.Http.Client**: IPFS HTTP client

### Network Storage
- **SMBLibrary**: SMB/CIFS support
- **FluentFTP**: FTP/FTPS client

### Key-Value Stores
- **StackExchange.Redis**: Redis client

## Statistics

The plugin tracks the following statistics:

- Total writes/reads/deletes
- Total bytes written/read
- Total failures
- Usage by strategy
- Operation latencies

## Health Monitoring

Each storage strategy supports health checks to monitor:
- Connectivity
- Latency
- Capacity
- Error rates

## Failover

When automatic failover is enabled:
1. Plugin monitors health status of all backends
2. On failure, switches to healthy alternative in same category
3. Retries operations with exponential backoff
4. Logs all failover events

## Extending with New Strategies

To add a new storage strategy:

1. Implement `IStorageStrategyExtended` interface
2. Place in appropriate `Strategies/` subdirectory
3. Strategy will be auto-discovered on plugin initialization

Example:

```csharp
public class MyCustomStorageStrategy : StorageStrategyBase, IStorageStrategyExtended
{
    public override string StrategyId => "my-custom";
    public override string Name => "My Custom Storage";
    public string StrategyName => Name;
    public string Category => "custom";
    public bool IsAvailable => true;
    public bool SupportsReplication => true;
    public override StorageTier Tier => StorageTier.Hot;
    public override StorageCapabilities Capabilities => new()
    {
        SupportsVersioning = true,
        SupportsMetadata = true,
        SupportsStreaming = true
    };

    // Implement required methods...
}
```

## License

Part of the DataWarehouse project.

## Version

1.0.0
