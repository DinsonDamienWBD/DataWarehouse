# Envelope Encryption Documentation (D3)

This document provides comprehensive guidance on implementing and using envelope encryption in the DataWarehouse UltimateKeyManagement plugin.

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Key Concepts](#key-concepts)
4. [Implementation Guide](#implementation-guide)
5. [API Reference](#api-reference)
6. [Best Practices](#best-practices)
7. [Security Considerations](#security-considerations)
8. [Troubleshooting](#troubleshooting)
9. [Examples](#examples)

---

## Overview

Envelope encryption is a two-tier encryption strategy that combines the security of Hardware Security Modules (HSMs) with the performance of symmetric encryption. This approach is widely used by cloud providers (AWS KMS, Azure Key Vault, Google Cloud KMS) and is the recommended pattern for encrypting large volumes of data.

### Why Envelope Encryption?

| Aspect | Direct Encryption | Envelope Encryption |
|--------|-------------------|---------------------|
| Key Security | Key exists in application memory | Master key (KEK) never leaves HSM |
| Performance | Single encryption pass | Two-tier: fast DEK for data, HSM for DEK |
| Scalability | Same key for all operations | Unique DEK per object |
| Compromise Impact | All data at risk | Only affected objects at risk |
| Rotation | Re-encrypt all data | Rotate KEK, DEKs auto-updated |

### Mode Selection

Use `KeyManagementMode.Envelope` when:
- Data requires HSM-grade protection
- Compliance mandates key separation (HIPAA, PCI-DSS, SOC 2)
- You need per-object key isolation
- Key rotation must not require data re-encryption

Use `KeyManagementMode.Direct` when:
- Simpler key management is acceptable
- Performance is critical and data volume is small
- HSM infrastructure is not available

---

## Architecture

### Two-Tier Key Hierarchy

```
+------------------+
|       HSM        |
|  +------------+  |
|  |    KEK     |  |  <-- Key Encryption Key (never leaves HSM)
|  +------------+  |
+--------|---------+
         |
         v
+--------|---------+
|   Wrap/Unwrap    |  <-- HSM operation
+--------|---------+
         |
         v
+------------------+
|   Wrapped DEK    |  <-- Stored with encrypted data
+------------------+
         |
         v
+------------------+
|   Plaintext DEK  |  <-- In memory only, used for data encryption
+------------------+
         |
         v
+------------------+
|  Encrypted Data  |
+------------------+
```

### Components

1. **Key Encryption Key (KEK)**
   - Stored in HSM (never exported in plaintext)
   - Used only to wrap/unwrap DEKs
   - Typically RSA or AES-256 key
   - Long-lived, rotated annually or per policy

2. **Data Encryption Key (DEK)**
   - Generated per object/record
   - Used for actual data encryption (AES-256-GCM)
   - Wrapped by KEK and stored with ciphertext
   - Short-lived in memory

3. **Envelope Header**
   - Stored at the beginning of encrypted data
   - Contains: wrapped DEK, KEK ID, algorithm info, metadata
   - Self-describing: enables decryption without external metadata

---

## Key Concepts

### IEnvelopeKeyStore Interface

The `IEnvelopeKeyStore` interface extends `IKeyStore` with envelope-specific operations:

```csharp
public interface IEnvelopeKeyStore : IKeyStore
{
    // Wrap a DEK with KEK (encryption happens inside HSM)
    Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);

    // Unwrap a DEK with KEK (decryption happens inside HSM)
    Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);

    // Supported wrapping algorithms
    IReadOnlyList<string> SupportedWrappingAlgorithms { get; }

    // Whether keys can be generated inside HSM
    bool SupportsHsmKeyGeneration { get; }
}
```

### Envelope Header Format

The envelope header uses a binary format with JSON payload:

```
+------------------+------------------+------------------+------------------+
| Magic (5 bytes)  | Version (4 bytes)| Length (4 bytes) | JSON Payload     |
| 0x44 0x57 0x45   |                  |                  | (variable)       |
| 0x4E 0x56        |                  |                  |                  |
| "DWENV"          |                  |                  |                  |
+------------------+------------------+------------------+------------------+
```

JSON Payload structure:
```json
{
  "Version": 1,
  "KekId": "kek-production-001",
  "KeyStorePluginId": "azure-keyvault",
  "WrappedDek": "base64-encoded-wrapped-dek",
  "WrappingAlgorithm": "RSA-OAEP-256",
  "Iv": "base64-encoded-iv",
  "EncryptionAlgorithm": "AES-256-GCM",
  "EncryptionPluginId": "aes256gcm",
  "EncryptedAtTicks": 638123456789012345,
  "EncryptedBy": "user@example.com",
  "Metadata": {}
}
```

---

## Implementation Guide

### Step 1: Configure Envelope Key Store

Register an envelope-capable key store:

```csharp
// Example: Azure Key Vault
var config = new Dictionary<string, object>
{
    ["VaultUrl"] = "https://myvault.vault.azure.net/",
    ["KeyName"] = "production-kek",
    ["ClientId"] = "your-client-id",
    ["ClientSecret"] = "your-client-secret"
};

var strategy = new AzureKeyVaultStrategy();
await strategy.InitializeAsync(config);

registry.RegisterEnvelope("azure-keyvault", strategy);
```

### Step 2: Configure User Key Management

Set user preference for envelope mode:

```csharp
var userConfig = new KeyManagementConfig
{
    Mode = KeyManagementMode.Envelope,
    EnvelopeKeyStorePluginId = "azure-keyvault",
    KekKeyId = "production-kek"
};

await configProvider.SaveConfigAsync(context, userConfig);
```

### Step 3: Encrypt Data

The encryption plugin handles envelope encryption automatically:

```csharp
// Data is encrypted with unique DEK, DEK is wrapped by KEK
var ciphertext = await encryptionPlugin.EncryptAsync(plaintext, options);

// Ciphertext includes envelope header:
// [EnvelopeHeader][EncryptedData]
```

### Step 4: Decrypt Data

Decryption automatically detects and processes envelope headers:

```csharp
// Plugin detects envelope header, unwraps DEK via HSM, decrypts data
var plaintext = await encryptionPlugin.DecryptAsync(ciphertext, options);
```

---

## API Reference

### EnvelopeVerification Service (D1)

Verifies that registered envelope key stores are properly configured.

```csharp
public class EnvelopeVerification
{
    // Verify all registered envelope key stores
    Task<EnvelopeVerificationReport> VerifyAllEnvelopeKeyStoresAsync(CancellationToken ct);

    // Verify a specific envelope key store
    Task<EnvelopeStoreVerificationResult> VerifyEnvelopeKeyStoreAsync(
        string storeId,
        IEnvelopeKeyStore keyStore,
        CancellationToken ct);

    // Check if a store supports envelope encryption
    bool SupportsEnvelopeEncryption(string storeId);

    // Get capabilities for a store
    Task<EnvelopeCapabilityReport> GetEnvelopeCapabilitiesAsync(string storeId, CancellationToken ct);

    // Check readiness for envelope operations
    EnvelopeReadinessCheck CheckEnvelopeReadiness(string storeId, string kekId);
}
```

### Key Derivation Hierarchy (D5)

HKDF-based key derivation for creating key hierarchies.

```csharp
public class KeyDerivationHierarchy
{
    // Register a master key for derivation
    Task RegisterMasterKeyAsync(string masterId, string keyId, ISecurityContext context, CancellationToken ct);

    // Derive a key from master using HKDF
    Task<DerivedKeyResult> DeriveKeyAsync(
        string masterId,
        string purpose,
        ISecurityContext context,
        byte[]? salt,
        int keySizeBytes,
        CancellationToken ct);

    // Derive child key from derived key (hierarchical)
    Task<DerivedKeyResult> DeriveChildKeyAsync(
        string parentDerivedKeyId,
        string childPurpose,
        ISecurityContext context,
        byte[]? salt,
        int keySizeBytes,
        CancellationToken ct);

    // Re-derive a key (for recovery)
    Task<byte[]> RederiveKeyAsync(string derivedKeyId, ISecurityContext context, CancellationToken ct);
}
```

Standard key purposes:
- `KeyPurpose.Encryption` - General encryption
- `KeyPurpose.Signing` - Digital signatures
- `KeyPurpose.KeyWrapping` - Wrapping other keys
- `KeyPurpose.DataEncryption` - DEK derivation
- `KeyPurpose.MacGeneration` - Message authentication
- `KeyPurpose.SessionKey` - Temporary session keys

### Zero-Downtime Rotation (D6)

Enables key rotation without service interruption.

```csharp
public class ZeroDowntimeRotation
{
    // Initiate rotation with dual-key period
    Task<RotationSession> InitiateRotationAsync(
        string keyStoreId,
        string currentKeyId,
        ISecurityContext context,
        RotationOptions? options,
        CancellationToken ct);

    // Validate a key during rotation
    KeyValidationResult ValidateKey(string keyStoreId, string keyId);

    // Get recommended key for new encryptions
    Task<RecommendedKey> GetRecommendedKeyAsync(string keyStoreId, ISecurityContext context, CancellationToken ct);

    // Start re-encryption of existing data
    Task<ReEncryptionJob> StartReEncryptionAsync(
        string sessionId,
        IEnumerable<ReEncryptionItem> items,
        ISecurityContext context,
        CancellationToken ct);

    // Complete the rotation
    Task<RotationCompletionResult> CompleteRotationAsync(
        string sessionId,
        ISecurityContext context,
        bool force,
        CancellationToken ct);
}
```

Rotation workflow:
1. **Initiate**: Create new key, enter dual-key period
2. **Dual-Key Period**: Both old and new keys valid
3. **Re-Encrypt** (optional): Migrate data to new key
4. **Complete**: New key becomes sole active key

### Key Escrow Recovery (D7)

M-of-N threshold scheme for key recovery.

```csharp
public class KeyEscrowRecovery
{
    // Register an escrow agent
    Task<EscrowAgent> RegisterAgentAsync(string agentId, EscrowAgentRegistration registration, ISecurityContext context, CancellationToken ct);

    // Create escrow configuration (M-of-N)
    Task<EscrowConfiguration> CreateEscrowConfigurationAsync(string configId, EscrowConfigurationRequest request, ISecurityContext context, CancellationToken ct);

    // Place a key in escrow
    Task<EscrowedKeyInfo> EscrowKeyAsync(string keyId, string configId, ISecurityContext context, CancellationToken ct);

    // Request recovery
    Task<RecoveryRequest> RequestRecoveryAsync(string escrowId, RecoveryRequestDetails details, ISecurityContext context, CancellationToken ct);

    // Approve recovery (by each agent)
    Task<ApprovalResult> ApproveRecoveryAsync(string requestId, string agentId, ApprovalDetails details, ISecurityContext context, CancellationToken ct);

    // Execute recovery (after threshold met)
    Task<RecoveryResult> ExecuteRecoveryAsync(string requestId, ISecurityContext context, CancellationToken ct);
}
```

### Break-Glass Access (D8)

Emergency key access with comprehensive auditing.

```csharp
public class BreakGlassAccess
{
    // Register emergency responder
    Task<AuthorizedResponder> RegisterResponderAsync(string responderId, ResponderRegistration registration, ISecurityContext context, CancellationToken ct);

    // Create emergency access policy
    Task<EmergencyAccessPolicy> CreatePolicyAsync(string policyId, EmergencyAccessPolicyRequest request, ISecurityContext context, CancellationToken ct);

    // Initiate break-glass access
    Task<BreakGlassSession> InitiateBreakGlassAsync(string policyId, BreakGlassRequest request, ISecurityContext context, CancellationToken ct);

    // Authorize session (by responders)
    Task<AuthorizationResult> AuthorizeSessionAsync(string sessionId, string responderId, AuthorizationDetails details, ISecurityContext context, CancellationToken ct);

    // Access key via break-glass
    Task<BreakGlassKeyAccessResult> AccessKeyAsync(string sessionId, string keyId, string accessReason, ISecurityContext context, CancellationToken ct);

    // End session
    Task<bool> EndSessionAsync(string sessionId, string reason, ISecurityContext context, CancellationToken ct);

    // Generate compliance report
    BreakGlassComplianceReport GenerateComplianceReport(DateTime fromDate, DateTime toDate);
}
```

Emergency types supported:
- `DisasterRecovery` - DR scenarios
- `LegalHold` - eDiscovery requirements
- `SecurityIncident` - Active incident response
- `ComplianceAudit` - Regulatory audit
- `BusinessContinuity` - BC emergency
- `SystemFailure` - System recovery

---

## Best Practices

### Key Management

1. **Separate KEKs by Environment**
   ```
   production-kek-001  -> Production data
   staging-kek-001     -> Staging data
   development-kek-001 -> Development data
   ```

2. **Rotate KEKs Periodically**
   - Annual rotation for compliance
   - Use ZeroDowntimeRotation for seamless transition
   - Keep previous KEK active for dual-key period

3. **Use Key Derivation for Related Keys**
   ```
   master-key
     -> derived-encryption-key
     -> derived-signing-key
     -> derived-session-key
   ```

### Performance Optimization

1. **Cache Unwrapped DEKs** (with caution)
   - Cache for short periods (5 minutes max)
   - Clear cache on application restart
   - Never persist unwrapped DEKs to disk

2. **Batch HSM Operations**
   - Group wrap/unwrap operations when possible
   - Use async/parallel processing for multiple objects

3. **Connection Pooling**
   - Reuse HSM connections
   - Configure appropriate pool sizes

### Security

1. **Never Log Key Material**
   - Log key IDs, not key data
   - Mask wrapped DEKs in logs

2. **Secure Memory Handling**
   - Clear key arrays after use
   - Use `CryptographicOperations.ZeroMemory()`

3. **Audit Everything**
   - Log all key operations
   - Include context (user, timestamp, operation)

---

## Security Considerations

### Threat Model

| Threat | Mitigation |
|--------|------------|
| KEK compromise | HSM isolation, access controls, rotation |
| DEK in memory | Short-lived, cleared after use |
| Wrapped DEK theft | Useless without KEK access |
| Replay attack | Unique DEK per object, nonce/IV |
| Key exhaustion | Rate limiting, monitoring |

### Compliance Mapping

| Requirement | Feature |
|-------------|---------|
| HIPAA - Encryption | Envelope encryption with HSM |
| PCI-DSS - Key Management | Key rotation, escrow, audit |
| SOC 2 - Access Control | Break-glass with multi-party auth |
| GDPR - Data Protection | Per-object encryption, key deletion |

### HSM Requirements

Supported HSM backends:
- **Azure Key Vault** (Premium tier for HSM-backed keys)
- **AWS KMS** (With HSM-backed CMKs)
- **Google Cloud KMS** (HSM protection level)
- **HashiCorp Vault** (With Transit secrets engine)
- **Thales Luna HSM** (Network HSM)
- **nCipher nShield** (Network HSM)

---

## Troubleshooting

### Common Issues

#### "KEK not found"
- Verify KEK ID is correct
- Check HSM connectivity
- Confirm user has access to the key

#### "Unwrap failed"
- KEK may have been rotated
- Check wrapped DEK integrity
- Verify algorithm compatibility

#### "Envelope header invalid"
- Data may be corrupted
- Check magic bytes match "DWENV"
- Verify header length field

#### "Performance degradation"
- Reduce HSM calls via caching
- Check HSM connection pool
- Consider batch operations

### Diagnostic Commands

```csharp
// Verify envelope key store
var verification = await envelopeVerification.VerifyAllEnvelopeKeyStoresAsync(ct);
Console.WriteLine($"Valid stores: {verification.ValidStores}/{verification.TotalStores}");

// Check readiness
var readiness = envelopeVerification.CheckEnvelopeReadiness("store-id", "kek-id");
foreach (var issue in readiness.Issues)
{
    Console.WriteLine($"Issue: {issue}");
}

// Get capabilities
var capabilities = await envelopeVerification.GetEnvelopeCapabilitiesAsync("store-id", ct);
Console.WriteLine($"Algorithms: {string.Join(", ", capabilities.SupportedAlgorithms)}");
```

---

## Examples

### Complete Encryption Flow

```csharp
// 1. Setup
var registry = new DefaultKeyStoreRegistry();
var azureStrategy = new AzureKeyVaultStrategy();
await azureStrategy.InitializeAsync(config);
registry.RegisterEnvelope("azure-kv", azureStrategy);

// 2. Configure user
var userConfig = new KeyManagementConfig
{
    Mode = KeyManagementMode.Envelope,
    EnvelopeKeyStorePluginId = "azure-kv",
    KekKeyId = "production-kek"
};

// 3. Encrypt
var plaintext = Encoding.UTF8.GetBytes("Sensitive data");
var ciphertext = await encryptionPlugin.EncryptAsync(plaintext, new EncryptOptions
{
    KeyManagement = userConfig
});

// 4. Decrypt
var decrypted = await encryptionPlugin.DecryptAsync(ciphertext, new DecryptOptions
{
    KeyStoreRegistry = registry
});
```

### Key Rotation Example

```csharp
var rotation = new ZeroDowntimeRotation(registry);

// Start rotation
var session = await rotation.InitiateRotationAsync(
    "azure-kv",
    "production-kek",
    context,
    new RotationOptions { DualKeyPeriod = TimeSpan.FromDays(7) });

// During dual-key period, both keys work
var recommended = await rotation.GetRecommendedKeyAsync("azure-kv", context);
Console.WriteLine($"Use key: {recommended.KeyId} for new encryptions");

// After dual-key period
var result = await rotation.CompleteRotationAsync(session.SessionId, context, false);
Console.WriteLine($"Rotation complete. New key: {result.NewKeyId}");
```

### Break-Glass Access Example

```csharp
var breakGlass = new BreakGlassAccess(keyStore, messageBus);

// Register responders
await breakGlass.RegisterResponderAsync("resp-001", new ResponderRegistration
{
    Name = "John Security",
    Email = "john@example.com",
    Role = ResponderRole.SecurityOfficer,
    CanInitiateBreakGlass = true,
    CanAuthorizeBreakGlass = true
}, adminContext);

// Create policy (2-of-3 authorization)
await breakGlass.CreatePolicyAsync("emergency-policy", new EmergencyAccessPolicyRequest
{
    Name = "Production Emergency Access",
    RequiredAuthorizationCount = 2,
    AuthorizedResponderIds = new List<string> { "resp-001", "resp-002", "resp-003" },
    AllowedEmergencyTypes = new List<EmergencyType> { EmergencyType.DisasterRecovery },
    RequiresJustification = true,
    RequiresIncidentTicket = true
}, adminContext);

// Initiate break-glass
var session = await breakGlass.InitiateBreakGlassAsync("emergency-policy", new BreakGlassRequest
{
    EmergencyType = EmergencyType.DisasterRecovery,
    Justification = "Primary datacenter offline, need to recover encryption keys",
    IncidentTicketId = "INC-2024-001234"
}, responderContext);

// Authorize (by 2 responders)
await breakGlass.AuthorizeSessionAsync(session.SessionId, "resp-001", new AuthorizationDetails(), responder1Context);
await breakGlass.AuthorizeSessionAsync(session.SessionId, "resp-002", new AuthorizationDetails(), responder2Context);

// Access key
var keyResult = await breakGlass.AccessKeyAsync(
    session.SessionId,
    "critical-data-key",
    "Recovering encrypted backup files",
    responderContext);

// End session
await breakGlass.EndSessionAsync(session.SessionId, "Recovery complete", responderContext);
```

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2025-02 | Initial envelope encryption documentation |

## Related Documentation

- [MigrationGuide.md](./MigrationGuide.md) - Migration from legacy plugins
- [SecurityGuidelines.md](./SecurityGuidelines.md) - Security best practices
- DataWarehouse SDK IKeyStore documentation
