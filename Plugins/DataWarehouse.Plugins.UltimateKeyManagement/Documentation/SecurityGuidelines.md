# UltimateKeyManagement Security Guidelines

This document provides comprehensive security guidelines, best practices, and recommendations for using the UltimateKeyManagement plugin in production environments.

## Table of Contents

1. [Security Overview](#security-overview)
2. [Strategy Selection Guide](#strategy-selection-guide)
3. [Best Practices](#best-practices)
4. [Security Considerations by Strategy Type](#security-considerations-by-strategy-type)
5. [Key Rotation](#key-rotation)
6. [Compliance Considerations](#compliance-considerations)
7. [Threat Model](#threat-model)
8. [Incident Response](#incident-response)
9. [Audit and Monitoring](#audit-and-monitoring)

---

## Security Overview

UltimateKeyManagement provides enterprise-grade key management with support for 50+ strategies across different security tiers:

| Tier | Description | Examples | Use Case |
|------|-------------|----------|----------|
| **Hardware** | Keys protected by dedicated hardware | HSMs, TPM, YubiKey, Ledger | High-security environments, regulatory compliance |
| **Cloud KMS** | Cloud-provider managed keys | AWS KMS, Azure Key Vault, GCP KMS | Cloud-native applications, managed security |
| **Secrets Management** | Enterprise secrets managers | HashiCorp Vault, CyberArk, Delinea | Enterprise environments, secret centralization |
| **Platform** | OS-level protection | Windows Credential Manager, macOS Keychain | Desktop applications, platform integration |
| **Local** | File-based with encryption | FileKeyStoreStrategy | Development, single-node deployments |
| **Threshold** | Distributed key control | Shamir Secret Sharing, MPC | High-value assets, multi-party control |

### Security Principles

1. **Least Privilege**: Grant minimum necessary permissions for key operations
2. **Defense in Depth**: Use multiple layers of protection
3. **Key Separation**: Use different keys for different purposes
4. **Audit Everything**: Log all key operations for forensics
5. **Rotate Regularly**: Implement automatic key rotation

---

## Strategy Selection Guide

### Decision Matrix

```
                        ┌─────────────────────────────────────────────────┐
                        │          What is your deployment model?          │
                        └───────────────────────┬─────────────────────────┘
                                                │
                    ┌───────────────────────────┼───────────────────────────┐
                    ▼                           ▼                           ▼
              Cloud-Native                Hybrid/On-Prem                  Local
                    │                           │                           │
                    ▼                           ▼                           ▼
        ┌───────────────────┐       ┌───────────────────┐       ┌───────────────────┐
        │ AWS? → AwsKms     │       │ Have HSM?         │       │ Development?      │
        │ Azure? → AzureKV  │       │ Yes → PKCS#11/HSM │       │ Yes → FileKeyStore│
        │ GCP? → GcpKms     │       │ No → Vault/Secrets│       │ No → See below    │
        └───────────────────┘       └───────────────────┘       └───────────────────┘
                    │                           │                           │
                    ▼                           ▼                           ▼
        ┌───────────────────┐       ┌───────────────────┐       ┌───────────────────┐
        │ Need envelope?    │       │ Which vendor?     │       │ Windows?          │
        │ Yes → Use KMS     │       │ Thales → LunaHSM  │       │ Yes → WinCredMgr  │
        │ No → Secrets Mgr  │       │ CyberArk → PAMSS  │       │ No → PBKDF2/Argon2│
        └───────────────────┘       └───────────────────┘       └───────────────────┘
```

### Recommendations by Environment

#### Production (High Security)

**Recommended Strategies:**
- `AwsKmsStrategy` / `AzureKeyVaultStrategy` / `GcpKmsStrategy` for cloud
- `ThalesLunaStrategy` / `Pkcs11HsmStrategy` for on-premises HSM
- `VaultKeyStoreStrategy` for HashiCorp Vault

**Configuration:**
```csharp
var config = new UltimateKeyManagementConfig
{
    AutoDiscoverStrategies = false,  // Explicit strategy registration
    EnableKeyRotation = true,
    DefaultRotationPolicy = new KeyRotationPolicy
    {
        Enabled = true,
        RotationInterval = TimeSpan.FromDays(90),  // PCI-DSS compliance
        GracePeriod = TimeSpan.FromDays(7),
        NotifyOnRotation = true
    },
    PublishKeyEvents = true  // Enable audit trail
};
```

#### Production (Standard Security)

**Recommended Strategies:**
- `VaultKeyStoreStrategy` (HashiCorp Vault)
- `DopplerStrategy` / `InfisicalStrategy` for modern secrets management
- `AkeylessStrategy` for zero-trust

**Configuration:**
```csharp
var config = new UltimateKeyManagementConfig
{
    AutoDiscoverStrategies = true,
    EnableKeyRotation = true,
    DefaultRotationPolicy = new KeyRotationPolicy
    {
        Enabled = true,
        RotationInterval = TimeSpan.FromDays(180),
        GracePeriod = TimeSpan.FromDays(14)
    }
};
```

#### Development/Testing

**Recommended Strategies:**
- `FileKeyStoreStrategy` for local development
- `EnvironmentKeyStoreStrategy` for CI/CD pipelines
- `DockerSecretsStrategy` for containerized development

**Configuration:**
```csharp
var config = new UltimateKeyManagementConfig
{
    AutoDiscoverStrategies = true,
    EnableKeyRotation = false,  // Not needed in dev
    StrategyConfigurations = new Dictionary<string, Dictionary<string, object>>
    {
        ["FileKeyStoreStrategy"] = new()
        {
            ["KeyStorePath"] = Path.Combine(Path.GetTempPath(), "dev-keys")
        }
    }
};
```

---

## Best Practices

### 1. Key Lifecycle Management

```
Key Lifecycle:
    ┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐
    │  Create  │───▶│  Active  │───▶│ Pending  │───▶│ Inactive │
    └──────────┘    └──────────┘    │Deactivate│    └──────────┘
                                    └──────────┘          │
                                                          ▼
                                                    ┌──────────┐
                                                    │ Destroyed│
                                                    └──────────┘
```

**Recommendations:**
- Set appropriate `MaxKeyAge` for compliance requirements
- Use `GracePeriod` to allow decryption during rotation
- Archive old keys securely before destruction
- Never delete keys without ensuring all data is re-encrypted

### 2. Access Control

```csharp
// Define strict security context
public class ProductionSecurityContext : ISecurityContext
{
    public string UserId { get; init; }
    public string? TenantId { get; init; }
    public IEnumerable<string> Roles { get; init; } = Array.Empty<string>();
    public bool IsSystemAdmin => false;  // Never default to admin!
}

// Use role-based access
var context = new ProductionSecurityContext
{
    UserId = authenticatedUser.Id,
    TenantId = authenticatedUser.TenantId,
    Roles = new[] { "key-reader" }  // Minimal required role
};

// Roles:
// - key-reader: Can read existing keys
// - key-operator: Can use keys for encryption/decryption
// - key-admin: Can create, rotate, and delete keys
```

### 3. Configuration Security

**DO:**
```csharp
// Use environment variables for secrets
var config = new UltimateKeyManagementConfig
{
    StrategyConfigurations = new Dictionary<string, Dictionary<string, object>>
    {
        ["VaultKeyStoreStrategy"] = new()
        {
            ["Address"] = Environment.GetEnvironmentVariable("VAULT_ADDR"),
            ["Token"] = Environment.GetEnvironmentVariable("VAULT_TOKEN")
        }
    }
};
```

**DON'T:**
```csharp
// Never hardcode credentials!
var config = new UltimateKeyManagementConfig
{
    StrategyConfigurations = new Dictionary<string, Dictionary<string, object>>
    {
        ["VaultKeyStoreStrategy"] = new()
        {
            ["Address"] = "http://vault:8200",
            ["Token"] = "hvs.super-secret-token"  // NEVER DO THIS!
        }
    }
};
```

### 4. Network Security

- Use TLS 1.2+ for all key store communications
- Implement certificate pinning for HSM connections
- Use private endpoints for cloud KMS services
- Configure firewall rules to restrict key store access

### 5. Memory Security

```csharp
// Clear sensitive data from memory
byte[] key = await keyStore.GetKeyAsync(keyId, context);
try
{
    // Use key for encryption/decryption
    var result = Encrypt(data, key);
}
finally
{
    // Zero memory immediately after use
    CryptographicOperations.ZeroMemory(key);
}
```

---

## Security Considerations by Strategy Type

### Cloud KMS Strategies

| Strategy | Security Level | Key Location | Compliance |
|----------|---------------|--------------|------------|
| `AwsKmsStrategy` | High | AWS HSM | SOC 2, PCI-DSS, HIPAA, FedRAMP |
| `AzureKeyVaultStrategy` | High | Azure HSM | SOC 2, PCI-DSS, HIPAA, FIPS 140-2 |
| `GcpKmsStrategy` | High | GCP HSM | SOC 2, PCI-DSS, HIPAA |
| `AlibabaKmsStrategy` | High | Alibaba HSM | ISO 27001, SOC 2 |
| `IbmKeyProtectStrategy` | High | IBM HSM | FIPS 140-2, SOC 2 |
| `OracleVaultStrategy` | High | Oracle HSM | PCI-DSS, FIPS 140-2 |

**Security Considerations:**
- Enable audit logging in cloud provider console
- Use IAM roles instead of static credentials where possible
- Enable automatic key rotation in the cloud provider
- Consider using Customer Managed Keys (CMK) for additional control
- Review data residency requirements for key storage location

### HSM Strategies

| Strategy | Hardware | FIPS Level | Use Case |
|----------|----------|------------|----------|
| `ThalesLunaStrategy` | Thales Luna | FIPS 140-2 Level 3 | Enterprise, Government |
| `Pkcs11HsmStrategy` | Any PKCS#11 | Varies | Generic HSM support |
| `AwsCloudHsmStrategy` | AWS CloudHSM | FIPS 140-2 Level 3 | Cloud HSM |
| `AzureDedicatedHsmStrategy` | Azure Dedicated HSM | FIPS 140-2 Level 3 | Azure HSM |
| `FortanixDsmStrategy` | Fortanix DSM | FIPS 140-2 Level 2/3 | Confidential computing |

**Security Considerations:**
- Keys never leave HSM boundary in plaintext
- Implement proper HSM partition management
- Use multi-factor authentication for HSM administration
- Regular firmware updates following vendor guidelines
- Maintain HSM cluster for high availability

### Secrets Management Strategies

| Strategy | Vendor | Key Features |
|----------|--------|--------------|
| `VaultKeyStoreStrategy` | HashiCorp | Transit engine, dynamic secrets |
| `CyberArkStrategy` | CyberArk | Privileged access, session recording |
| `DelineaStrategy` | Delinea | Secret Server, DevOps Secrets Vault |
| `AkeylessStrategy` | Akeyless | Zero-knowledge, SaaS |
| `DopplerStrategy` | Doppler | Developer-friendly, sync |
| `InfisicalStrategy` | Infisical | Open-source, E2E encrypted |

**Security Considerations:**
- Use short-lived tokens with automatic renewal
- Enable secret versioning for rollback capability
- Configure response wrapping for sensitive operations
- Implement namespace isolation for multi-tenant deployments
- Regular audit of access policies

### Hardware Token Strategies

| Strategy | Device | Security Level |
|----------|--------|---------------|
| `YubikeyStrategy` | YubiKey | FIDO2, PIV |
| `TpmStrategy` | TPM 2.0 | Platform-bound |
| `LedgerStrategy` | Ledger | Secure Element |
| `TrezorStrategy` | Trezor | Open-source firmware |
| `NitrokeyStrategy` | Nitrokey | Open-source hardware |

**Security Considerations:**
- Require user presence verification (touch)
- Implement PIN/password protection
- Store recovery keys securely offline
- Use device attestation to verify authenticity
- Plan for device loss/replacement procedures

### Local Strategies

| Strategy | Protection Method | Security Level |
|----------|------------------|----------------|
| `FileKeyStoreStrategy` | Multi-tier encryption | Medium |
| `WindowsCredManagerStrategy` | DPAPI | Medium |
| `MacOsKeychainStrategy` | Keychain | Medium |
| `LinuxSecretServiceStrategy` | Secret Service API | Medium |

**Security Considerations:**
- Only use for development or single-machine deployments
- Ensure proper file system permissions (600 for key files)
- Use strong master passwords for PBKDF2 fallback
- Enable full-disk encryption on the host
- Regular backup of encrypted key files

### Threshold/MPC Strategies

| Strategy | Scheme | Threshold |
|----------|--------|-----------|
| `ShamirSecretStrategy` | Shamir's Secret Sharing | (k, n) threshold |
| `SsssStrategy` | ssss implementation | (k, n) threshold |
| `FrostStrategy` | FROST | Schnorr threshold |
| `ThresholdEcdsaStrategy` | Threshold ECDSA | (t, n) threshold |
| `MultiPartyComputationStrategy` | MPC | Various |

**Security Considerations:**
- Distribute shares to geographically separated parties
- Use independent communication channels for share collection
- Implement secure share reconstruction ceremony
- Never store multiple shares together
- Regular share refresh to prevent compromise accumulation

---

## Key Rotation

### Rotation Strategy

```
Rotation Flow:
    ┌──────────────────────────────────────────────────────────────┐
    │  1. Generate new key (v2)                                    │
    │  2. Mark old key (v1) as "pending deactivation"              │
    │  3. New encryptions use v2                                   │
    │  4. Grace period: both v1 and v2 can decrypt                 │
    │  5. After grace period: v1 cannot encrypt, only decrypt      │
    │  6. Re-encrypt data: decrypt with v1, encrypt with v2        │
    │  7. After all data re-encrypted: destroy v1                  │
    └──────────────────────────────────────────────────────────────┘
```

### Recommended Rotation Intervals

| Compliance | Key Type | Rotation Interval |
|------------|----------|-------------------|
| PCI-DSS | KEK | Annual |
| PCI-DSS | DEK | Monthly to Quarterly |
| HIPAA | All keys | Annual minimum |
| SOX | Encryption keys | Annual |
| GDPR | Personal data keys | Risk-based |
| Internal Policy | General | 90 days |

### Configuration Example

```csharp
var config = new UltimateKeyManagementConfig
{
    EnableKeyRotation = true,
    RotationCheckInterval = TimeSpan.FromHours(1),
    DefaultRotationPolicy = new KeyRotationPolicy
    {
        Enabled = true,
        RotationInterval = TimeSpan.FromDays(90),
        GracePeriod = TimeSpan.FromDays(7),
        MaxKeyAge = TimeSpan.FromDays(365),  // Hard limit
        DeleteOldKeysAfterGrace = false,      // Archive instead
        NotifyOnRotation = true,
        TargetKeyIds = new List<string>(),    // All keys
        RetryPolicy = new RotationRetryPolicy
        {
            MaxRetries = 3,
            InitialDelay = TimeSpan.FromMinutes(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromHours(1)
        }
    },
    // Strategy-specific rotation policies
    StrategyRotationPolicies = new Dictionary<string, KeyRotationPolicy>
    {
        // More aggressive rotation for cloud KMS
        ["AwsKmsStrategy"] = new KeyRotationPolicy
        {
            Enabled = true,
            RotationInterval = TimeSpan.FromDays(30)
        },
        // Less frequent for HSM (hardware operation)
        ["ThalesLunaStrategy"] = new KeyRotationPolicy
        {
            Enabled = true,
            RotationInterval = TimeSpan.FromDays(365)
        }
    }
};
```

---

## Compliance Considerations

### PCI-DSS Requirements

| Requirement | Implementation |
|-------------|----------------|
| 3.5.1 - Cryptographic keys protected | Use HSM or cloud KMS strategies |
| 3.5.2 - Key storage in minimum locations | Centralized key management |
| 3.6.4 - Key changes for suspected compromise | Manual rotation via API |
| 3.6.5 - Retirement of compromised keys | `DeleteOldKeysAfterGrace` policy |
| 3.6.6 - Split knowledge and dual control | Threshold strategies |

### HIPAA Security Rule

| Safeguard | Implementation |
|-----------|----------------|
| Access controls | Role-based `ISecurityContext` |
| Audit controls | `PublishKeyEvents = true` |
| Integrity controls | HSM-backed strategies |
| Transmission security | TLS for all communications |

### GDPR Considerations

| Principle | Implementation |
|-----------|----------------|
| Data minimization | Key-per-tenant isolation |
| Storage limitation | Key expiration policies |
| Right to erasure | Key destruction procedures |
| Accountability | Comprehensive audit logging |

---

## Threat Model

### Assets

1. **Key Material**: Encryption keys themselves
2. **Key Metadata**: Key IDs, creation dates, rotation status
3. **Configuration**: Vault addresses, credentials
4. **Audit Logs**: Key operation history

### Threat Categories

| Threat | Impact | Mitigation |
|--------|--------|------------|
| Key extraction | Critical | HSM usage, memory protection |
| Key enumeration | High | Access control, rate limiting |
| Replay attacks | Medium | Nonce/timestamp validation |
| Side-channel attacks | Medium | Constant-time operations |
| Configuration exposure | High | Secrets management, encryption |
| Audit log tampering | Medium | Write-once logs, signing |
| Denial of service | Medium | Rate limiting, HA deployment |

### Attack Vectors

```
                    ┌─────────────────────────────────────────┐
                    │            Key Store Access             │
                    └───────────────────┬─────────────────────┘
                                        │
        ┌───────────────────────────────┼───────────────────────────────┐
        ▼                               ▼                               ▼
 ┌──────────────┐              ┌──────────────┐              ┌──────────────┐
 │   Network    │              │ Application  │              │   Insider    │
 │   Attack     │              │   Attack     │              │   Threat     │
 └──────────────┘              └──────────────┘              └──────────────┘
        │                               │                               │
   ┌────┴────┐                    ┌─────┴─────┐                   ┌─────┴─────┐
   │  MITM   │                    │ Injection │                   │ Privilege │
   │ Sniffing│                    │  Memory   │                   │  Abuse    │
   │  Replay │                    │  Timing   │                   │ Data Exfil│
   └─────────┘                    └───────────┘                   └───────────┘
```

### Mitigation Strategies

1. **Network**: TLS 1.3, certificate pinning, VPN/private endpoints
2. **Application**: Input validation, parameterized queries, memory wiping
3. **Insider**: Least privilege, audit logging, separation of duties

---

## Incident Response

### Key Compromise Procedure

```
┌─────────────────────────────────────────────────────────────────────────┐
│ 1. DETECT: Identify compromised key via audit logs or alert            │
│                                                                         │
│ 2. CONTAIN: Immediately disable compromised key                         │
│    await strategy.DeactivateKeyAsync(compromisedKeyId, adminContext);   │
│                                                                         │
│ 3. ASSESS: Determine scope of affected data                            │
│    var affectedData = await FindDataEncryptedWithKey(compromisedKeyId);│
│                                                                         │
│ 4. ROTATE: Generate new key and update references                      │
│    var newKeyId = await strategy.CreateKeyAsync("replacement", ctx);    │
│                                                                         │
│ 5. RE-ENCRYPT: Decrypt with old, encrypt with new                      │
│    foreach (var data in affectedData)                                   │
│        await ReEncryptData(data, compromisedKeyId, newKeyId);          │
│                                                                         │
│ 6. DESTROY: Securely destroy compromised key                           │
│    await strategy.DeleteKeyAsync(compromisedKeyId, adminContext);       │
│                                                                         │
│ 7. REPORT: Document incident and notify stakeholders                   │
└─────────────────────────────────────────────────────────────────────────┘
```

### Emergency Key Rotation

```csharp
public async Task EmergencyKeyRotationAsync(string compromisedKeyId)
{
    // 1. Create new key immediately
    var newKeyId = $"emergency-{DateTime.UtcNow:yyyyMMddHHmmss}";
    await _keyStore.CreateKeyAsync(newKeyId, _adminContext);

    // 2. Update rotation policy to skip grace period
    var emergencyPolicy = new KeyRotationPolicy
    {
        Enabled = true,
        GracePeriod = TimeSpan.Zero,  // No grace period
        DeleteOldKeysAfterGrace = false  // Keep for forensics
    };

    // 3. Trigger immediate re-encryption
    await _rotationScheduler.ForceRotationAsync(compromisedKeyId, emergencyPolicy);

    // 4. Notify security team
    await _alertService.SendSecurityAlertAsync(
        "Emergency Key Rotation",
        $"Key {compromisedKeyId} compromised. Rotated to {newKeyId}.");
}
```

---

## Audit and Monitoring

### Event Types

| Event | Priority | Retention |
|-------|----------|-----------|
| `keystore.key.created` | Medium | 1 year |
| `keystore.key.accessed` | Low | 90 days |
| `keystore.key.rotated` | Medium | 1 year |
| `keystore.key.deleted` | High | 7 years |
| `keystore.access.denied` | High | 1 year |
| `keystore.error` | High | 1 year |

### Monitoring Configuration

```csharp
// Subscribe to key events
var plugin = new UltimateKeyManagementPlugin();

// Via message bus
plugin.OnHandshakeAsync(new HandshakeRequest
{
    Config = new Dictionary<string, object>
    {
        ["MessageBus"] = myMessageBus
    }
});

// Custom event handler
myMessageBus.Subscribe("keystore.key.*", async (topic, message) =>
{
    await _auditLogger.LogAsync(new AuditEntry
    {
        Timestamp = DateTime.UtcNow,
        EventType = topic,
        KeyId = message.Payload["keyId"]?.ToString(),
        UserId = message.Payload["userId"]?.ToString(),
        Details = JsonSerializer.Serialize(message.Payload)
    });
});
```

### Alert Conditions

| Condition | Severity | Action |
|-----------|----------|--------|
| Multiple access denied | High | Investigate potential attack |
| Key accessed outside business hours | Medium | Verify legitimate use |
| Unusual key creation rate | Medium | Check for automation issues |
| Key rotation failure | High | Manual intervention required |
| HSM connectivity loss | Critical | Failover procedure |

---

## Summary

1. **Choose appropriate strategies** based on security requirements
2. **Enable key rotation** with compliant intervals
3. **Use HSM or cloud KMS** for production workloads
4. **Implement least privilege** access control
5. **Monitor and audit** all key operations
6. **Have incident response** procedures ready
7. **Regularly review** and update security configurations

For additional assistance, contact your security team or refer to vendor-specific documentation for your chosen key management strategy.
