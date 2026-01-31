# DataWarehouse.Plugins.TamperProof

Tamper-proof storage plugin for DataWarehouse providing immutable versioning, blockchain anchoring, RAID redundancy, and WORM compliance.

## Overview

The TamperProof plugin implements a comprehensive tamper-proof storage solution with:

- **5-Phase Write Pipeline**: User transformations → Integrity hashing → RAID sharding → 4-tier writes → Blockchain anchoring
- **5-Phase Read Pipeline**: Manifest loading → Shard reconstruction → Integrity verification → Recovery (if needed) → Transformation reversal
- **4-Tier Storage Architecture**: Data (RAID shards), Metadata (manifests), WORM (immutable backups), Blockchain (anchors)
- **Automatic Tamper Detection**: Hash-based verification on every read with configurable recovery behavior
- **WORM Compliance**: Write-once-read-many storage with retention policies and legal hold support
- **Blockchain Anchoring**: Cryptographic proof of existence with batch optimization
- **Access Logging**: Complete audit trail for all operations with tamper attribution

## Features

### Write Operations

1. **User Transformations** (Phase 1)
   - Compression (optional)
   - Encryption (optional)
   - Custom pipeline stages

2. **Integrity Hash** (Phase 2)
   - Configurable algorithm (SHA256, SHA384, SHA512, Blake3)
   - Covers original and final content

3. **RAID Sharding** (Phase 3)
   - Configurable data + parity shards
   - Shard-level integrity hashes
   - Optional shard padding for size obfuscation

4. **Transactional 4-Tier Write** (Phase 4)
   - Parallel writes with transaction semantics
   - Automatic rollback on failure (except WORM)
   - Orphaned WORM tracking for cleanup

5. **Blockchain Anchoring** (Phase 5)
   - Single or batched anchoring
   - Merkle tree optimization
   - Configurable confirmation requirements

### Read Operations

1. **Manifest Loading** (Phase 1)
   - Version-aware retrieval
   - Manifest integrity validation

2. **Shard Reconstruction** (Phase 2)
   - Parallel shard loading
   - Parity-based recovery for missing shards
   - Handles up to N parity shards missing

3. **Integrity Verification** (Phase 3)
   - Fast, Verified, or Audit modes
   - Blockchain anchor verification (Audit mode)
   - Automatic tamper detection

4. **Recovery** (Phase 3b, if needed)
   - Automatic recovery from WORM backup
   - Configurable recovery behavior
   - Tamper incident logging and alerting

5. **Transformation Reversal** (Phase 4)
   - Reverses pipeline stages in opposite order
   - Content padding removal
   - Access logging

## Configuration

### Storage Instances

```csharp
var config = new TamperProofConfiguration
{
    StorageInstances = new StorageInstancesConfig
    {
        Data = new StorageInstanceConfig
        {
            InstanceId = "data",
            PluginId = "com.datawarehouse.storage.local",
            ConnectionString = "path/to/data"
        },
        Metadata = new StorageInstanceConfig
        {
            InstanceId = "metadata",
            PluginId = "com.datawarehouse.storage.local",
            ConnectionString = "path/to/metadata"
        },
        Worm = new StorageInstanceConfig
        {
            InstanceId = "worm",
            PluginId = "com.datawarehouse.storage.s3",
            ConnectionString = "s3://worm-bucket"
        },
        Blockchain = new StorageInstanceConfig
        {
            InstanceId = "blockchain",
            PluginId = "com.datawarehouse.blockchain.local",
            ConnectionString = "path/to/blockchain"
        }
    }
};
```

### RAID Configuration

```csharp
config.Raid = new RaidConfig
{
    DataShards = 4,      // Split data into 4 pieces
    ParityShards = 2,    // 2 parity shards for redundancy
    MinShardSize = 64 * 1024,    // 64 KB minimum
    MaxShardSize = 64 * 1024 * 1024  // 64 MB maximum
};
```

### Recovery Behavior

```csharp
// Auto-recover silently
config.RecoveryBehavior = TamperRecoveryBehavior.AutoRecoverSilent;

// Auto-recover with admin notification
config.RecoveryBehavior = TamperRecoveryBehavior.AutoRecoverWithReport;

// Block access and wait for manual intervention
config.RecoveryBehavior = TamperRecoveryBehavior.AlertAndWait;
```

## Usage

### Write Data

```csharp
var writeContext = new WriteContext
{
    Author = "user@example.com",
    Comment = "Initial upload of customer data"
};

using var dataStream = File.OpenRead("customer-data.csv");

var result = await tamperProofPlugin.ExecuteWritePipelineAsync(
    dataStream,
    writeContext,
    cancellationToken);

Console.WriteLine($"Object ID: {result.ObjectId}");
Console.WriteLine($"Manifest ID: {result.ManifestId}");
Console.WriteLine($"WORM Record ID: {result.WormRecordId}");
```

### Read Data

```csharp
var result = await tamperProofPlugin.ExecuteReadPipelineAsync(
    objectId: myObjectId,
    version: null,  // Latest version
    readMode: ReadMode.Verified,
    cancellationToken);

if (result.Success)
{
    var data = result.Data;

    if (result.RecoveryPerformed)
    {
        Console.WriteLine("Warning: Data was recovered from WORM backup");
        Console.WriteLine($"Recovery details: {result.RecoveryDetails}");
    }
}
```

## Architecture

### Data Flow - Write

```
User Data
   ↓
Phase 1: User Transformations (compression, encryption)
   ↓
Phase 2: Compute Integrity Hash (SHA256)
   ↓
Phase 3: RAID Sharding (4 data + 2 parity shards)
   ↓
Phase 4: Parallel 4-Tier Write
   ├─→ Data Tier (RAID shards)
   ├─→ Metadata Tier (manifest)
   ├─→ WORM Tier (immutable backup)
   └─→ Blockchain Tier (pending anchor)
   ↓
Phase 5: Blockchain Anchoring (batch or immediate)
```

### Data Flow - Read

```
Request (ObjectId + Version)
   ↓
Phase 1: Load Manifest (metadata tier)
   ↓
Phase 2: Load & Reconstruct Shards (data tier + parity)
   ↓
Phase 3: Verify Integrity (hash comparison)
   ├─→ Valid: Continue
   └─→ Tampered: Recover from WORM
   ↓
Phase 4: Reverse Transformations (decrypt, decompress)
   ↓
Original Data
```

## Security Considerations

1. **Integrity Protection**
   - All data hashed with cryptographic algorithms
   - Shard-level and object-level integrity checks
   - Blockchain provides non-repudiable proof of existence

2. **WORM Immutability**
   - Write-once guarantees prevent tampering
   - Hardware integration available (S3 Object Lock, Azure Immutable Blob)
   - Orphaned WORM tracking for failed transactions

3. **Access Control**
   - All operations logged with principal attribution
   - Tamper incidents tracked for forensics
   - Configurable alerting for security events

4. **Recovery**
   - Automatic recovery from WORM backups
   - Configurable behavior (silent, report, block)
   - Maintains audit trail of all recovery operations

## Performance

- **Write Throughput**: Parallel tier writes for optimal performance
- **Read Throughput**: Parallel shard loading with optional fast mode
- **Blockchain Batching**: Reduces per-object anchoring overhead
- **RAID Redundancy**: Tolerates shard failures without data loss

## Dependencies

- **DataWarehouse.SDK**: Core SDK with TamperProof contracts
- **IIntegrityProvider**: Hash computation (SHA256, SHA384, SHA512, Blake3)
- **IBlockchainProvider**: Blockchain anchoring (local, Ethereum, etc.)
- **IWormStorageProvider**: WORM storage (local, S3, Azure)
- **IAccessLogProvider**: Access logging and audit trails
- **IPipelineOrchestrator**: User transformation pipeline

## Compliance

- **WORM**: Supports SEC 17a-4, FINRA 4511, HIPAA, and other regulations
- **Blockchain**: Provides cryptographic proof for legal requirements
- **Audit Trail**: Complete access history for compliance audits
- **Retention**: Configurable retention policies with legal hold support

## License

MIT License - See LICENSE file for details.
