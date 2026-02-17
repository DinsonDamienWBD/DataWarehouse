# FIX-12 Encryption Key Management Extraction

## Source: Legacy EncryptionPluginBase (PluginBase.cs lines 2112-2924)

### 1. Configuration Infrastructure

**Fields (lines 2114-2145):**
- `IKeyStore? DefaultKeyStore` - default key store (fallback)
- `KeyManagementMode DefaultKeyManagementMode` - default mode (Direct)
- `IEnvelopeKeyStore? DefaultEnvelopeKeyStore` - default envelope key store
- `string? DefaultKekKeyId` - default KEK key ID
- `IKeyManagementConfigProvider? ConfigProvider` - per-user config provider (multi-tenant)
- `IKeyStoreRegistry? KeyStoreRegistry` - registry for resolving plugin IDs

### 2. Statistics Tracking (lines 2148-2180)

**Fields:**
- `object StatsLock` - thread-safe stats lock
- `long EncryptionCount` - total encryptions
- `long DecryptionCount` - total decryptions
- `long TotalBytesEncrypted` / `TotalBytesDecrypted`
- `ConcurrentDictionary<string, DateTime> KeyAccessLog` - bounded key access audit (10K max)

**Methods:**
- `void RecordKeyAccess(string keyId)` - bounded key access logging with LRU eviction
- `void UpdateEncryptionStats(long bytesProcessed)` - thread-safe encryption counter
- `void UpdateDecryptionStats(long bytesProcessed)` - thread-safe decryption counter
- `EncryptionStatistics GetStatistics()` - snapshot of all stats

### 3. Configuration Resolution (lines 2233-2343)

**Methods:**
- `Task<ResolvedKeyManagementConfig> ResolveConfigAsync(Dictionary<string, object> args, ISecurityContext context)` - 3-tier resolution: args > user prefs > defaults
- `bool TryGetConfigFromArgs(Dictionary<string, object> args, out ResolvedKeyManagementConfig config)` - extract config from operation args
- `ResolvedKeyManagementConfig ResolveFromUserConfig(KeyManagementConfig userConfig)` - resolve from user preferences

### 4. Key Management (lines 2346-2467)

**Methods:**
- `Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetKeyForEncryptionAsync(ResolvedKeyManagementConfig config, ISecurityContext context)` - dispatch to Direct or Envelope mode
- `Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetDirectKeyForEncryptionAsync(...)` - Direct mode: retrieve key from store
- `Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetEnvelopeKeyForEncryptionAsync(...)` - Envelope mode: generate random DEK, wrap with KEK
- `Task<byte[]> GetKeyForDecryptionAsync(EnvelopeHeader? envelope, string? keyId, ResolvedKeyManagementConfig config, ISecurityContext context)` - unwrap DEK or get direct key

**Dependencies:**
- `IKeyStore` - key storage interface
- `IEnvelopeKeyStore` - envelope key wrapping interface
- `IKeyStoreRegistry` - registry for resolving key stores by plugin ID
- `ISecurityContext` - security context (user ID, tenant, roles)
- `IKeyManagementConfigProvider` - per-user config provider
- `KeyManagementMode` enum (Direct, Envelope)
- `ResolvedKeyManagementConfig` - resolved configuration DTO
- `EnvelopeHeader` - envelope encryption header
- `EncryptionMetadata` - metadata for storage

### 5. Security Context Resolution (lines 2470-2498)

**Methods:**
- `ISecurityContext GetSecurityContext(Dictionary<string, object> args, IKernelContext context)` - resolve security context from args or kernel
- `DefaultSecurityContext` inner class - system fallback context

### 6. OnWrite/OnRead Implementation (lines 2501-2639)

**Methods:**
- `Task<Stream> OnWriteAsync(...)` - full encryption pipeline: resolve config -> get key -> encrypt -> store metadata -> zero memory
- `Task<Stream> OnReadAsync(...)` - full decryption pipeline: parse metadata/envelope -> get key -> decrypt -> zero memory

### 7. Abstract Algorithm Methods (lines 2643-2676)

- `abstract Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context)`
- `abstract Task<(Stream data, byte[]? tag)> DecryptCoreAsync(Stream input, byte[] key, byte[]? iv, IKernelContext context)`
- `virtual byte[] GenerateIv()` - random IV generation

### 8. Configuration Methods (lines 2727-2768)

- `void SetDefaultKeyStore(IKeyStore keyStore)`
- `void SetDefaultEnvelopeKeyStore(IEnvelopeKeyStore envelopeKeyStore, string kekKeyId)`
- `void SetDefaultMode(KeyManagementMode mode)`
- `void SetConfigProvider(IKeyManagementConfigProvider provider)`
- `void SetKeyStoreRegistry(IKeyStoreRegistry registry)`

### 9. Strategy Registry Support (lines 2772-2883)

- `IEncryptionStrategyRegistry? StrategyRegistry` - virtual property
- `DeclaredCapabilities` - auto-generates capabilities from strategies
- `GetStaticKnowledge()` - generates knowledge objects for strategies

## Port Strategy

Port to new `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/EncryptionPluginBase.cs` as protected virtual/abstract methods:

1. **Key Management Core**: `GetKeyForEncryptionAsync`, `GetKeyForDecryptionAsync`, `GetDirectKeyForEncryptionAsync`, `GetEnvelopeKeyForEncryptionAsync` - protected virtual
2. **Configuration**: `ResolveConfigAsync`, `TryGetConfigFromArgs`, `ResolveFromUserConfig` - protected virtual
3. **Statistics**: `RecordKeyAccess`, `UpdateEncryptionStats`, `UpdateDecryptionStats`, `GetStatistics` - protected virtual
4. **Config Methods**: `SetDefaultKeyStore`, `SetDefaultEnvelopeKeyStore`, `SetDefaultMode`, `SetConfigProvider`, `SetKeyStoreRegistry` - protected virtual
5. **Security Context**: `GetSecurityContext`, `DefaultSecurityContext` - protected virtual
6. **Algorithm**: `EncryptCoreAsync`, `DecryptCoreAsync`, `GenerateIv` - protected abstract/virtual
7. **Pipeline**: `OnWriteAsync`, `OnReadAsync` overrides with full encryption pipeline
8. **Dispose**: Clean up KeyAccessLog

All features are opt-in: plugins that don't need key management can ignore these methods.
