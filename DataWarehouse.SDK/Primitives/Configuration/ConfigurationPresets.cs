namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Factory methods for creating preset configurations.
/// Each preset represents a different security/performance tradeoff level.
/// </summary>
public static class ConfigurationPresets
{
    /// <summary>UNSAFE preset: Zero security, maximum performance, dev/testing ONLY.</summary>
    public static DataWarehouseConfiguration CreateUnsafe()
    {
        var config = new DataWarehouseConfiguration { PresetName = "unsafe" };

        config.Security.EncryptionEnabled.Value = false;
        config.Security.AuthEnabled.Value = false;
        config.Security.AuditEnabled.Value = false;
        config.Security.TlsRequired.Value = false;
        config.Security.RbacEnabled.Value = false;
        config.Security.PasswordMinLength.Value = 8;
        config.Security.SessionTimeoutMinutes.Value = 120;
        config.Security.DefaultAuthScheme.Value = "None";

        config.Encryption.Enabled.Value = false;
        config.Encryption.EncryptInTransit.Value = false;
        config.Encryption.EncryptAtRest.Value = false;

        config.Storage.EncryptAtRest.Value = false;
        config.Storage.EnableCompression.Value = false;
        config.Network.TlsEnabled.Value = false;

        return config;
    }

    /// <summary>MINIMAL preset: Basic auth, no encryption, no audit.</summary>
    public static DataWarehouseConfiguration CreateMinimal()
    {
        var config = new DataWarehouseConfiguration { PresetName = "minimal" };

        config.Security.EncryptionEnabled.Value = false;
        config.Security.AuthEnabled.Value = true;
        config.Security.AuditEnabled.Value = false;
        config.Security.TlsRequired.Value = false;
        config.Security.RbacEnabled.Value = true;
        config.Security.DefaultAuthScheme.Value = "Basic";

        config.Encryption.Enabled.Value = false;
        config.Storage.EncryptAtRest.Value = false;
        config.Network.TlsEnabled.Value = false;

        return config;
    }

    /// <summary>STANDARD preset: AES-256, RBAC, basic audit, TLS required.</summary>
    public static DataWarehouseConfiguration CreateStandard()
    {
        // Default values in DataWarehouseConfiguration are already "standard"
        var config = new DataWarehouseConfiguration { PresetName = "standard" };

        config.Security.EncryptionEnabled.Value = true;
        config.Security.AuthEnabled.Value = true;
        config.Security.AuditEnabled.Value = true;
        config.Security.TlsRequired.Value = true;
        config.Security.RbacEnabled.Value = true;

        config.Encryption.Enabled.Value = true;
        config.Encryption.DefaultAlgorithm.Value = "AES256-GCM";

        config.Storage.EncryptAtRest.Value = true;
        config.Network.TlsEnabled.Value = true;

        return config;
    }

    /// <summary>SECURE preset: Quantum-safe crypto, MFA, full audit, encrypted-at-rest, certificate pinning.</summary>
    public static DataWarehouseConfiguration CreateSecure()
    {
        var config = CreateStandard();
        config.PresetName = "secure";

        config.Security.QuantumSafeMode.Value = true;
        config.Security.MfaEnabled.Value = true;
        config.Security.CertificatePinning.Value = true;
        config.Security.PasswordMinLength.Value = 16;
        config.Security.SessionTimeoutMinutes.Value = 15;

        config.Encryption.KeyRotationEnabled.Value = true;
        config.Encryption.KeyRotationDays.Value = 30;

        config.Observability.TracingEnabled.Value = true;

        return config;
    }

    /// <summary>PARANOID preset: FIPS 140-3, HSM keys, air-gap ready, tamper-proof logging, zero-trust.</summary>
    public static DataWarehouseConfiguration CreateParanoid()
    {
        var config = CreateSecure();
        config.PresetName = "paranoid";

        config.Security.FipsMode.Value = true;
        config.Security.HsmRequired.Value = true;
        config.Security.ZeroTrustMode.Value = true;
        config.Security.TamperProofLogging.Value = true;
        config.Security.SessionTimeoutMinutes.Value = 10;

        // Lock critical security settings (cannot be overridden)
        config.Security.EncryptionEnabled.AllowUserToOverride = false;
        config.Security.EncryptionEnabled.LockedByPolicy = "Paranoid Preset";
        config.Security.AuthEnabled.AllowUserToOverride = false;
        config.Security.AuthEnabled.LockedByPolicy = "Paranoid Preset";
        config.Security.TlsRequired.AllowUserToOverride = false;
        config.Security.TlsRequired.LockedByPolicy = "Paranoid Preset";
        config.Security.FipsMode.AllowUserToOverride = false;
        config.Security.FipsMode.LockedByPolicy = "Paranoid Preset";

        config.Encryption.KeyStoreBackend.Value = "HSM";

        config.Deployment.AirGapMode.Value = true;

        return config;
    }

    /// <summary>GOD-TIER preset: Everything enabled, ML anomaly detection, adaptive security, self-healing, auto-rotation.</summary>
    public static DataWarehouseConfiguration CreateGodTier()
    {
        var config = CreateParanoid();
        config.PresetName = "god-tier";

        // Enable all advanced features
        config.Observability.AnomalyDetectionEnabled.Value = true;
        config.Resilience.SelfHealingEnabled.Value = true;

        config.Replication.Enabled.Value = true;
        config.Replication.MultiMasterEnabled.Value = true;
        config.Replication.CrdtEnabled.Value = true;
        config.Replication.ReplicationFactor.Value = 5;
        config.Replication.ConsistencyLevel.Value = 2; // QUORUM

        config.Storage.EnableDeduplication.Value = true;
        config.Storage.EnableVersioning.Value = true;

        config.DataManagement.GovernanceEnabled.Value = true;
        config.DataManagement.QualityEnabled.Value = true;
        config.DataManagement.LineageEnabled.Value = true;
        config.DataManagement.MultiTenancyEnabled.Value = true;

        config.Compute.GpuEnabled.Value = true;

        config.Deployment.MultiCloudEnabled.Value = true;
        config.Deployment.EdgeEnabled.Value = true;

        return config;
    }

    /// <summary>Get preset by name.</summary>
    public static DataWarehouseConfiguration CreateByName(string presetName)
    {
        return presetName.ToLowerInvariant() switch
        {
            "unsafe" => CreateUnsafe(),
            "minimal" => CreateMinimal(),
            "standard" => CreateStandard(),
            "secure" => CreateSecure(),
            "paranoid" => CreateParanoid(),
            "god-tier" or "godtier" => CreateGodTier(),
            _ => CreateStandard()
        };
    }
}
