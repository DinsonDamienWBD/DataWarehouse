# UltimateKeyManagement Migration Guide

This guide provides step-by-step instructions for migrating from individual key management plugins (`FileKeyStorePlugin`, `VaultKeyStorePlugin`, `KeyRotationPlugin`) to the unified `UltimateKeyManagementPlugin`.

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Migration Paths](#migration-paths)
   - [FileKeyStorePlugin Migration](#filekeystoreplugin-migration)
   - [VaultKeyStorePlugin Migration](#vaultkeystoreplugin-migration)
   - [KeyRotationPlugin Migration](#keyrotationplugin-migration)
4. [Configuration Migration](#configuration-migration)
5. [Code Migration](#code-migration)
6. [Automated Migration Tools](#automated-migration-tools)
7. [Testing Your Migration](#testing-your-migration)
8. [Troubleshooting](#troubleshooting)
9. [FAQ](#faq)

---

## Overview

The `UltimateKeyManagementPlugin` consolidates all key management functionality into a single, unified plugin with a strategy-based architecture. This provides:

- **Unified API**: One plugin for all key management needs
- **50+ Strategies**: Support for cloud KMS, HSMs, secrets managers, and more
- **Built-in Rotation**: Integrated key rotation with configurable policies
- **Auto-Discovery**: Automatic strategy discovery and registration
- **Backward Compatibility**: Compatibility shims for gradual migration

### Deprecation Timeline

| Plugin | Deprecated | Removed |
|--------|------------|---------|
| `FileKeyStorePlugin` | v2.0.0 (Jan 2025) | v3.0.0 (Jan 2026) |
| `VaultKeyStorePlugin` | v2.0.0 (Jan 2025) | v3.0.0 (Jan 2026) |
| `KeyRotationPlugin` | v2.0.0 (Jan 2025) | v3.0.0 (Jan 2026) |

---

## Prerequisites

Before starting migration:

1. **Backup your existing configuration files**
2. **Document current key store usage** in your application
3. **Install the UltimateKeyManagement package**:
   ```bash
   dotnet add package DataWarehouse.Plugins.UltimateKeyManagement
   ```
4. **Verify existing keys are accessible** (migration preserves keys, not key data)

---

## Migration Paths

### FileKeyStorePlugin Migration

#### Before (Deprecated)

```csharp
// Old imports
using DataWarehouse.Plugins.FileKeyStore;

// Old configuration
var config = new FileKeyStoreConfig
{
    KeyStorePath = @"C:\Keys",
    KeySizeBytes = 32,
    RequireAuthentication = true,
    DpapiEntropy = "my-entropy"
};

// Old plugin instantiation
var plugin = new FileKeyStorePlugin(config);
```

#### After (Recommended)

```csharp
// New imports
using DataWarehouse.Plugins.UltimateKeyManagement;
using DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local;

// New configuration
var config = new UltimateKeyManagementConfig
{
    AutoDiscoverStrategies = false,
    EnableKeyRotation = true,
    StrategyConfigurations = new Dictionary<string, Dictionary<string, object>>
    {
        ["DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy"] = new()
        {
            ["KeyStorePath"] = @"C:\Keys",
            ["KeySizeBytes"] = 32,
            ["RequireAuthentication"] = true,
            ["DpapiEntropy"] = "my-entropy"
        }
    }
};

// New plugin instantiation
var plugin = new UltimateKeyManagementPlugin();
await plugin.StartAsync(CancellationToken.None);

// Get the strategy
var fileKeyStore = plugin.GetKeyStore(
    "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy");
```

#### Configuration Mapping

| FileKeyStoreConfig | UltimateKeyManagement Strategy Config |
|-------------------|--------------------------------------|
| `KeyStorePath` | `KeyStorePath` |
| `KeySizeBytes` | `KeySizeBytes` |
| `MasterKeyEnvVar` | `MasterKeyEnvVar` |
| `RequireAuthentication` | `RequireAuthentication` |
| `RequireAdminForCreate` | `RequireAdminForCreate` |
| `DpapiScope` | `DpapiScope` |
| `DpapiEntropy` | `DpapiEntropy` |
| `CredentialManagerTarget` | `CredentialManagerTarget` |
| `FallbackPassword` | `FallbackPassword` |

---

### VaultKeyStorePlugin Migration

#### Before (Deprecated)

```csharp
using DataWarehouse.Plugins.VaultKeyStore;

var config = new VaultConfig
{
    HashiCorpVault = new HashiCorpVaultConfig
    {
        Address = "http://vault:8200",
        Token = "my-token",
        MountPath = "secret",
        DefaultKeyName = "app-key"
    },
    AzureKeyVault = new AzureKeyVaultConfig
    {
        VaultUrl = "https://myvault.vault.azure.net",
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "secret"
    }
};

var plugin = new VaultKeyStorePlugin(config);
```

#### After (Recommended)

```csharp
using DataWarehouse.Plugins.UltimateKeyManagement;
using DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement;
using DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms;

var config = new UltimateKeyManagementConfig
{
    AutoDiscoverStrategies = false,
    EnableKeyRotation = true,
    StrategyConfigurations = new Dictionary<string, Dictionary<string, object>>
    {
        // HashiCorp Vault
        ["DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy"] = new()
        {
            ["Address"] = "http://vault:8200",
            ["Token"] = "my-token",
            ["MountPath"] = "secret",
            ["DefaultKeyName"] = "app-key"
        },
        // Azure Key Vault
        ["DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms.AzureKeyVaultStrategy"] = new()
        {
            ["VaultUrl"] = "https://myvault.vault.azure.net",
            ["TenantId"] = "tenant-id",
            ["ClientId"] = "client-id",
            ["ClientSecret"] = "secret"
        }
    }
};

var plugin = new UltimateKeyManagementPlugin();
await plugin.StartAsync(CancellationToken.None);

// Get HashiCorp Vault strategy
var vaultKeyStore = plugin.GetKeyStore(
    "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy");

// Get Azure Key Vault strategy (also supports envelope encryption)
var azureKeyStore = plugin.GetEnvelopeKeyStore(
    "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.CloudKms.AzureKeyVaultStrategy");
```

#### Available Vault/KMS Strategies

| Old Backend | New Strategy |
|------------|--------------|
| HashiCorpVault | `VaultKeyStoreStrategy` |
| AzureKeyVault | `AzureKeyVaultStrategy` |
| AwsKms | `AwsKmsStrategy` |
| (new) | `GcpKmsStrategy` |
| (new) | `AlibabaKmsStrategy` |
| (new) | `IbmKeyProtectStrategy` |
| (new) | `OracleVaultStrategy` |
| (new) | `DigitalOceanVaultStrategy` |

---

### KeyRotationPlugin Migration

#### Before (Deprecated)

```csharp
using DataWarehouse.Plugins.KeyRotation;

var config = new KeyRotationConfig
{
    EnableScheduledRotation = true,
    RotationInterval = TimeSpan.FromDays(90),
    GracePeriod = TimeSpan.FromHours(24),
    MaxConcurrentReencryptions = 4
};

var plugin = new KeyRotationPlugin(config);

// Manual rotation
await plugin.RotateKeyAsync(securityContext, "Scheduled rotation");
```

#### After (Recommended)

```csharp
using DataWarehouse.Plugins.UltimateKeyManagement;

var config = new UltimateKeyManagementConfig
{
    AutoDiscoverStrategies = true,
    EnableKeyRotation = true,  // Built-in rotation!
    DefaultRotationPolicy = new KeyRotationPolicy
    {
        Enabled = true,
        RotationInterval = TimeSpan.FromDays(90),
        GracePeriod = TimeSpan.FromDays(7),
        DeleteOldKeysAfterGrace = false,
        NotifyOnRotation = true,
        RetryPolicy = new RotationRetryPolicy
        {
            MaxRetries = 3,
            InitialDelay = TimeSpan.FromMinutes(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromHours(1)
        }
    },
    RotationCheckInterval = TimeSpan.FromHours(1)
};

var plugin = new UltimateKeyManagementPlugin();
await plugin.StartAsync(CancellationToken.None);

// Rotation happens automatically via KeyRotationScheduler
// No manual calls needed!
```

#### Rotation Policy Mapping

| KeyRotationConfig | UltimateKeyManagement |
|------------------|----------------------|
| `EnableScheduledRotation` | `EnableKeyRotation` + `DefaultRotationPolicy.Enabled` |
| `RotationInterval` | `DefaultRotationPolicy.RotationInterval` |
| `GracePeriod` | `DefaultRotationPolicy.GracePeriod` |
| `MaxConcurrentReencryptions` | Handled internally |
| `ReencryptionTimeout` | Handled internally |

---

## Configuration Migration

### Using the ConfigurationMigrator

The `ConfigurationMigrator` class automates configuration file migration:

```csharp
using DataWarehouse.Plugins.UltimateKeyManagement.Migration;

var migrator = new ConfigurationMigrator(new ConfigurationMigratorOptions
{
    CreateBackups = true,
    BackupDirectory = @"C:\Backups",
    ValidateAfterMigration = true
});

// Migrate a single file
var result = await migrator.MigrateConfigurationFileAsync(
    sourceConfigPath: @"C:\Config\keystore.json",
    outputConfigPath: @"C:\Config\keystore.ultimate.json"
);

if (result.Success)
{
    Console.WriteLine($"Migrated to: {result.OutputPath}");
    Console.WriteLine($"Backup at: {result.BackupPath}");
}
else
{
    Console.WriteLine($"Migration failed: {result.ErrorMessage}");
}

// Migrate all files in a directory
var batchResult = await migrator.MigrateDirectoryAsync(
    sourceDirectory: @"C:\Config",
    outputDirectory: @"C:\Config\Migrated"
);

Console.WriteLine($"Migrated {batchResult.SuccessfulMigrations} of {batchResult.TotalFilesFound} files");
```

### Manual JSON Migration

#### Before (FileKeyStoreConfig)

```json
{
  "keyStorePath": "C:\\Keys",
  "keySizeBytes": 32,
  "masterKeyEnvVar": "MASTER_KEY",
  "requireAuthentication": true,
  "dpapiEntropy": "my-entropy"
}
```

#### After (UltimateKeyManagementConfig)

```json
{
  "autoDiscoverStrategies": false,
  "enableKeyRotation": true,
  "publishKeyEvents": true,
  "strategyConfigurations": {
    "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy": {
      "KeyStorePath": "C:\\Keys",
      "KeySizeBytes": 32,
      "MasterKeyEnvVar": "MASTER_KEY",
      "RequireAuthentication": true,
      "DpapiEntropy": "my-entropy"
    }
  },
  "defaultRotationPolicy": {
    "enabled": true,
    "rotationInterval": "90.00:00:00",
    "gracePeriod": "7.00:00:00",
    "notifyOnRotation": true
  }
}
```

---

## Code Migration

### Using the PluginMigrationHelper

The `PluginMigrationHelper` scans your codebase for deprecated references:

```csharp
using DataWarehouse.Plugins.UltimateKeyManagement.Migration;

var helper = new PluginMigrationHelper();

// Scan your project
var scanResult = await helper.ScanForDeprecatedReferencesAsync(
    rootDirectory: @"C:\MyProject",
    filePatterns: new[] { "*.cs", "*.json", "*.csproj" }
);

Console.WriteLine($"Found {scanResult.TotalReferencesFound} deprecated references");

foreach (var file in scanResult.FilesWithReferences)
{
    Console.WriteLine($"\n{file.FilePath}:");
    foreach (var reference in file.References)
    {
        Console.WriteLine($"  Line {reference.LineNumber}: {reference.OldReference}");
        Console.WriteLine($"    -> Migrate to: {reference.NewReference}");
    }
}

// Get suggestions
var suggestions = helper.GetSuggestions();
foreach (var suggestion in suggestions)
{
    Console.WriteLine($"\n[{suggestion.Severity}] {suggestion.Description}");
    Console.WriteLine($"  Recommended: {suggestion.RecommendedAction}");
    Console.WriteLine($"  Auto-fix available: {suggestion.AutoFixAvailable}");
}

// Apply auto-fixes
var fixResult = await helper.ApplyAllAutoFixesAsync(createBackups: true);
Console.WriteLine($"Applied {fixResult.SuccessfulFixes} fixes");
```

### Compatibility Shim for Gradual Migration

If you need to migrate gradually, use the compatibility shim:

```csharp
using DataWarehouse.Plugins.UltimateKeyManagement.Migration;

var ultimatePlugin = new UltimateKeyManagementPlugin();
await ultimatePlugin.StartAsync(CancellationToken.None);

var helper = new PluginMigrationHelper();
var shim = helper.CreateCompatibilityShim(ultimatePlugin);

// Old code continues to work!
var keyStore = shim.GetLegacyKeyStore("datawarehouse.plugins.keystore.file");
var key = await keyStore.GetKeyAsync("my-key", securityContext);
```

---

## Testing Your Migration

### 1. Verify Key Retrieval

```csharp
[Fact]
public async Task MigratedKeyStore_CanRetrieveExistingKeys()
{
    // Arrange
    var plugin = new UltimateKeyManagementPlugin();
    await plugin.StartAsync(CancellationToken.None);

    var keyStore = plugin.GetKeyStore(
        "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy");

    // Act
    var key = await keyStore.GetKeyAsync("existing-key-id", _securityContext);

    // Assert
    Assert.NotNull(key);
    Assert.Equal(32, key.Length);
}
```

### 2. Verify Key Creation

```csharp
[Fact]
public async Task MigratedKeyStore_CanCreateNewKeys()
{
    var plugin = new UltimateKeyManagementPlugin();
    await plugin.StartAsync(CancellationToken.None);

    var keyStore = plugin.GetKeyStore(
        "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy");

    var newKey = await keyStore.CreateKeyAsync("new-key-id", _securityContext);

    Assert.NotNull(newKey);
    Assert.Equal(32, newKey.Length);
}
```

### 3. Verify Rotation

```csharp
[Fact]
public async Task MigratedKeyStore_RotationWorks()
{
    var config = new UltimateKeyManagementConfig
    {
        EnableKeyRotation = true,
        DefaultRotationPolicy = new KeyRotationPolicy
        {
            Enabled = true,
            RotationInterval = TimeSpan.FromSeconds(1) // Short for testing
        }
    };

    var plugin = new UltimateKeyManagementPlugin();
    await plugin.StartAsync(CancellationToken.None);

    // Wait for rotation
    await Task.Delay(2000);

    // Verify rotation occurred via events or logs
}
```

---

## Troubleshooting

### Common Issues

#### Issue: "Key not found" after migration

**Cause**: Keys are stored with hashed IDs based on configuration path.

**Solution**: Ensure `KeyStorePath` matches the original configuration exactly.

#### Issue: "Unable to decrypt key with any available protection tier"

**Cause**: Protection tier mismatch between old and new configuration.

**Solution**:
1. Ensure `DpapiEntropy` matches if set
2. Ensure `MasterKeyEnvVar` points to same environment variable
3. Run migration on same machine/user context

#### Issue: Configuration validation errors

**Cause**: Required fields missing in migrated configuration.

**Solution**: Check `ConfigurationValidationResult` for specific errors:
```csharp
var result = await migrator.MigrateConfigurationFileAsync(path);
foreach (var error in result.ValidationResult?.Errors ?? new())
{
    Console.WriteLine($"Error: {error}");
}
```

#### Issue: Deprecation warnings in production

**Solution**: Suppress warnings by configuring DeprecationManager:
```csharp
var manager = new DeprecationManager(new DeprecationManagerOptions
{
    EmitToConsole = false,
    SuppressDuplicateWarnings = true
});
```

### Getting Help

1. Check the [Security Guidelines](SecurityGuidelines.md) for best practices
2. Review the [API documentation](https://docs.datawarehouse.io/api/ultimate-key-management)
3. File issues at [GitHub Issues](https://github.com/datawarehouse/issues)

---

## FAQ

### Q: Do I need to re-encrypt existing data?

**A**: No. The migration only affects the key management layer. Your encrypted data remains unchanged and will decrypt correctly with the migrated key stores.

### Q: Can I use multiple strategies simultaneously?

**A**: Yes! UltimateKeyManagement supports registering multiple strategies. Use `AutoDiscoverStrategies = true` or manually configure each strategy in `StrategyConfigurations`.

### Q: What happens to my existing keys?

**A**: Keys stored in file-based stores remain in place. Keys in external vaults (HashiCorp, Azure, AWS) are unaffected. The migration only changes how your application accesses them.

### Q: How do I handle envelope encryption migration?

**A**: If you were using `VaultKeyStorePlugin` with envelope encryption:
1. Use `AzureKeyVaultStrategy`, `AwsKmsStrategy`, or similar that implements `IEnvelopeKeyStore`
2. Call `plugin.GetEnvelopeKeyStore()` instead of `GetKeyStore()`
3. The `WrapKeyAsync`/`UnwrapKeyAsync` methods work identically

### Q: Can I rollback the migration?

**A**: Yes, if you:
1. Kept backups (enabled by default in migration tools)
2. Didn't delete old plugin packages
3. Restore configuration from `.backup` files

### Q: When will deprecated plugins stop working?

**A**: Deprecated plugins will continue working until v3.0.0 (estimated Jan 2026). However, they will:
- Emit deprecation warnings
- Not receive new features
- Only receive critical security fixes

---

## Next Steps

1. Review [Security Guidelines](SecurityGuidelines.md) for production deployment
2. Test migration in a staging environment
3. Plan phased rollout for production
4. Remove deprecated package references after successful migration
