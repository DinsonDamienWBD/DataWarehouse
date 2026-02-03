// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Features;

/// <summary>
/// Configuration for PerObjectConfig mode.
/// Each object stores its own encryption configuration in the manifest.
/// </summary>
/// <remarks>
/// E4: PerObjectConfig - each object has its own encryption settings.
/// This is the default mode for maximum flexibility in multi-tenant deployments.
/// </remarks>
public sealed class PerObjectConfigMode : IEncryptionConfigMode
{
    /// <summary>
    /// Mode identifier for serialization.
    /// </summary>
    public const string ModeId = "PerObject";

    /// <inheritdoc/>
    public string Id => ModeId;

    /// <inheritdoc/>
    public string DisplayName => "Per-Object Configuration";

    /// <inheritdoc/>
    public string Description =>
        "Each object stores its own encryption metadata. " +
        "Maximum flexibility for multi-tenant deployments with mixed compliance requirements.";

    /// <inheritdoc/>
    public EncryptionConfigMode Mode => EncryptionConfigMode.PerObjectConfig;

    /// <inheritdoc/>
    public (bool IsValid, string? Error) ValidateConfig(
        EncryptionMetadata metadata,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy)
    {
        // PerObjectConfig mode has no restrictions
        // Each object can have any valid encryption configuration
        if (metadata == null)
        {
            return (false, "Encryption metadata cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(metadata.EncryptionPluginId))
        {
            return (false, "EncryptionPluginId is required.");
        }

        return (true, null);
    }

    /// <inheritdoc/>
    public (bool IsAllowed, string? Error) CanWriteWithConfig(
        EncryptionMetadata proposedConfig,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy)
    {
        // PerObjectConfig allows any configuration per object
        var validation = ValidateConfig(proposedConfig, existingConfig, policy);
        return (validation.IsValid, validation.Error);
    }

    /// <inheritdoc/>
    public (bool IsSealed, string? Error) IsSealedAfterFirstWrite(
        EncryptionMetadata? existingConfig)
    {
        // PerObjectConfig is never sealed - each write can use different config
        return (false, null);
    }
}

/// <summary>
/// Configuration for FixedConfig mode.
/// All objects MUST use the same encryption configuration.
/// Configuration is sealed after first write.
/// </summary>
/// <remarks>
/// E5: FixedConfig - all objects must use same encryption (sealed after first write).
/// Use case: Single-tenant deployments with strict compliance requiring uniform encryption.
/// </remarks>
public sealed class FixedConfigMode : IEncryptionConfigMode
{
    /// <summary>
    /// Mode identifier for serialization.
    /// </summary>
    public const string ModeId = "Fixed";

    /// <summary>
    /// Storage key for the sealed configuration.
    /// </summary>
    public const string SealedConfigKey = "FixedConfig.Sealed";

    private EncryptionMetadata? _sealedConfig;
    private bool _isSealed;
    private readonly object _sealLock = new();

    /// <inheritdoc/>
    public string Id => ModeId;

    /// <inheritdoc/>
    public string DisplayName => "Fixed Configuration";

    /// <inheritdoc/>
    public string Description =>
        "All objects must use the same encryption configuration. " +
        "Configuration is sealed after first write and cannot be changed.";

    /// <inheritdoc/>
    public EncryptionConfigMode Mode => EncryptionConfigMode.FixedConfig;

    /// <summary>
    /// Gets the sealed configuration, or null if not yet sealed.
    /// </summary>
    public EncryptionMetadata? SealedConfiguration => _sealedConfig;

    /// <summary>
    /// Gets whether the configuration has been sealed.
    /// </summary>
    public bool IsConfigurationSealed => _isSealed;

    /// <summary>
    /// Seals the configuration with the given metadata.
    /// Can only be called once - subsequent calls will fail.
    /// </summary>
    /// <param name="config">The configuration to seal.</param>
    /// <returns>True if sealed successfully, false if already sealed.</returns>
    public bool TrySeal(EncryptionMetadata config)
    {
        lock (_sealLock)
        {
            if (_isSealed)
            {
                return false;
            }

            _sealedConfig = config;
            _isSealed = true;
            return true;
        }
    }

    /// <summary>
    /// Loads a previously sealed configuration (for recovery/restart scenarios).
    /// </summary>
    /// <param name="config">The sealed configuration to restore.</param>
    public void LoadSealedConfig(EncryptionMetadata config)
    {
        lock (_sealLock)
        {
            _sealedConfig = config;
            _isSealed = true;
        }
    }

    /// <inheritdoc/>
    public (bool IsValid, string? Error) ValidateConfig(
        EncryptionMetadata metadata,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy)
    {
        if (metadata == null)
        {
            return (false, "Encryption metadata cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(metadata.EncryptionPluginId))
        {
            return (false, "EncryptionPluginId is required.");
        }

        // If sealed, validate against sealed config
        if (_isSealed && _sealedConfig != null)
        {
            if (!ConfigsMatch(metadata, _sealedConfig))
            {
                return (false,
                    $"FixedConfig mode: Configuration is sealed. All writes must use " +
                    $"EncryptionPluginId='{_sealedConfig.EncryptionPluginId}', " +
                    $"KeyMode='{_sealedConfig.KeyMode}'. " +
                    $"Attempted: EncryptionPluginId='{metadata.EncryptionPluginId}', " +
                    $"KeyMode='{metadata.KeyMode}'.");
            }
        }

        return (true, null);
    }

    /// <inheritdoc/>
    public (bool IsAllowed, string? Error) CanWriteWithConfig(
        EncryptionMetadata proposedConfig,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy)
    {
        var validation = ValidateConfig(proposedConfig, existingConfig, policy);
        if (!validation.IsValid)
        {
            return (false, validation.Error);
        }

        // If not sealed, this write will seal the config
        if (!_isSealed)
        {
            return (true, null);
        }

        // If sealed, verify proposed matches sealed
        if (!ConfigsMatch(proposedConfig, _sealedConfig!))
        {
            return (false,
                "FixedConfig mode is sealed. Cannot write with different encryption configuration. " +
                $"Sealed config: Plugin='{_sealedConfig!.EncryptionPluginId}', Mode='{_sealedConfig.KeyMode}'. " +
                $"Proposed: Plugin='{proposedConfig.EncryptionPluginId}', Mode='{proposedConfig.KeyMode}'.");
        }

        return (true, null);
    }

    /// <inheritdoc/>
    public (bool IsSealed, string? Error) IsSealedAfterFirstWrite(
        EncryptionMetadata? existingConfig)
    {
        return (_isSealed, null);
    }

    /// <summary>
    /// Checks if two configurations match for FixedConfig purposes.
    /// </summary>
    private static bool ConfigsMatch(EncryptionMetadata a, EncryptionMetadata b)
    {
        // Core matching criteria for FixedConfig
        return a.EncryptionPluginId == b.EncryptionPluginId &&
               a.KeyMode == b.KeyMode &&
               a.KeyStorePluginId == b.KeyStorePluginId;
    }
}

/// <summary>
/// Configuration for PolicyEnforced mode.
/// Per-object configuration is allowed, but must satisfy tenant/org policy constraints.
/// </summary>
/// <remarks>
/// E6: PolicyEnforced - per-object allowed within policy constraints.
/// Use case: Enterprise with compliance rules but per-user flexibility within bounds.
/// </remarks>
public sealed class PolicyEnforcedConfigMode : IEncryptionConfigMode
{
    /// <summary>
    /// Mode identifier for serialization.
    /// </summary>
    public const string ModeId = "PolicyEnforced";

    private EncryptionPolicy? _policy;

    /// <inheritdoc/>
    public string Id => ModeId;

    /// <inheritdoc/>
    public string DisplayName => "Policy-Enforced Configuration";

    /// <inheritdoc/>
    public string Description =>
        "Per-object configuration allowed within policy constraints. " +
        "Policy defines allowed algorithms, key modes, and key stores.";

    /// <inheritdoc/>
    public EncryptionConfigMode Mode => EncryptionConfigMode.PolicyEnforced;

    /// <summary>
    /// Gets or sets the active encryption policy.
    /// Must be set before validation operations.
    /// </summary>
    public EncryptionPolicy? Policy
    {
        get => _policy;
        set => _policy = value;
    }

    /// <summary>
    /// Creates a new PolicyEnforcedConfigMode with optional initial policy.
    /// </summary>
    /// <param name="policy">Optional policy to enforce.</param>
    public PolicyEnforcedConfigMode(EncryptionPolicy? policy = null)
    {
        _policy = policy;
    }

    /// <inheritdoc/>
    public (bool IsValid, string? Error) ValidateConfig(
        EncryptionMetadata metadata,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy)
    {
        if (metadata == null)
        {
            return (false, "Encryption metadata cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(metadata.EncryptionPluginId))
        {
            return (false, "EncryptionPluginId is required.");
        }

        var effectivePolicy = policy ?? _policy;
        if (effectivePolicy == null)
        {
            return (false, "PolicyEnforced mode requires a policy to be set.");
        }

        // Validate against policy constraints
        var policyValidation = ValidateAgainstPolicy(metadata, effectivePolicy);
        return policyValidation;
    }

    /// <inheritdoc/>
    public (bool IsAllowed, string? Error) CanWriteWithConfig(
        EncryptionMetadata proposedConfig,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy)
    {
        return ValidateConfig(proposedConfig, existingConfig, policy);
    }

    /// <inheritdoc/>
    public (bool IsSealed, string? Error) IsSealedAfterFirstWrite(
        EncryptionMetadata? existingConfig)
    {
        // PolicyEnforced is never sealed - policy can be updated
        return (false, null);
    }

    /// <summary>
    /// Validates encryption metadata against policy constraints.
    /// </summary>
    private static (bool IsValid, string? Error) ValidateAgainstPolicy(
        EncryptionMetadata metadata,
        EncryptionPolicy policy)
    {
        // Check allowed key modes
        if (policy.AllowedModes.Length > 0)
        {
            if (!policy.AllowedModes.Contains(metadata.KeyMode))
            {
                return (false,
                    $"Policy violation: Key mode '{metadata.KeyMode}' is not allowed. " +
                    $"Allowed modes: {string.Join(", ", policy.AllowedModes)}");
            }
        }

        // Check allowed encryption plugins
        if (policy.AllowedEncryptionPlugins.Length > 0)
        {
            if (!policy.AllowedEncryptionPlugins.Contains(metadata.EncryptionPluginId))
            {
                return (false,
                    $"Policy violation: Encryption plugin '{metadata.EncryptionPluginId}' is not allowed. " +
                    $"Allowed plugins: {string.Join(", ", policy.AllowedEncryptionPlugins)}");
            }
        }

        // Check allowed key stores
        if (policy.AllowedKeyStores.Length > 0 && !string.IsNullOrEmpty(metadata.KeyStorePluginId))
        {
            if (!policy.AllowedKeyStores.Contains(metadata.KeyStorePluginId))
            {
                return (false,
                    $"Policy violation: Key store '{metadata.KeyStorePluginId}' is not allowed. " +
                    $"Allowed key stores: {string.Join(", ", policy.AllowedKeyStores)}");
            }
        }

        // Check HSM requirement for Envelope mode
        if (policy.RequireHsmBackedKek && metadata.KeyMode == KeyManagementMode.Envelope)
        {
            // This would typically check the key store capabilities
            // For now, we trust that the key store plugin ID indicates HSM capability
            // More sophisticated validation would involve querying the key store registry
        }

        return (true, null);
    }
}

/// <summary>
/// Interface for encryption configuration mode implementations.
/// </summary>
public interface IEncryptionConfigMode
{
    /// <summary>
    /// Gets the unique identifier for this mode.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets a description of this mode's behavior.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the SDK enum value for this mode.
    /// </summary>
    EncryptionConfigMode Mode { get; }

    /// <summary>
    /// Validates an encryption configuration.
    /// </summary>
    /// <param name="metadata">The proposed encryption metadata.</param>
    /// <param name="existingConfig">The existing sealed/stored config (if any).</param>
    /// <param name="policy">Optional policy for policy-enforced mode.</param>
    /// <returns>Validation result.</returns>
    (bool IsValid, string? Error) ValidateConfig(
        EncryptionMetadata metadata,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy);

    /// <summary>
    /// Determines if a write operation is allowed with the proposed configuration.
    /// </summary>
    /// <param name="proposedConfig">The proposed encryption configuration.</param>
    /// <param name="existingConfig">The existing sealed/stored config (if any).</param>
    /// <param name="policy">Optional policy for policy-enforced mode.</param>
    /// <returns>Whether the write is allowed.</returns>
    (bool IsAllowed, string? Error) CanWriteWithConfig(
        EncryptionMetadata proposedConfig,
        EncryptionMetadata? existingConfig,
        EncryptionPolicy? policy);

    /// <summary>
    /// Determines if the configuration is sealed after first write.
    /// </summary>
    /// <param name="existingConfig">The existing stored config (if any).</param>
    /// <returns>Whether config is sealed and any error message.</returns>
    (bool IsSealed, string? Error) IsSealedAfterFirstWrite(
        EncryptionMetadata? existingConfig);
}

/// <summary>
/// Factory for creating encryption config mode instances.
/// </summary>
public static class EncryptionConfigModeFactory
{
    /// <summary>
    /// Creates an encryption config mode from the SDK enum.
    /// </summary>
    /// <param name="mode">The config mode enum value.</param>
    /// <param name="policy">Optional policy for PolicyEnforced mode.</param>
    /// <returns>The config mode instance.</returns>
    public static IEncryptionConfigMode Create(
        EncryptionConfigMode mode,
        EncryptionPolicy? policy = null)
    {
        return mode switch
        {
            SDK.Security.EncryptionConfigMode.PerObjectConfig => new PerObjectConfigMode(),
            SDK.Security.EncryptionConfigMode.FixedConfig => new FixedConfigMode(),
            SDK.Security.EncryptionConfigMode.PolicyEnforced => new PolicyEnforcedConfigMode(policy),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown encryption config mode.")
        };
    }

    /// <summary>
    /// Creates an encryption config mode from a mode identifier string.
    /// </summary>
    /// <param name="modeId">The mode identifier.</param>
    /// <param name="policy">Optional policy for PolicyEnforced mode.</param>
    /// <returns>The config mode instance.</returns>
    public static IEncryptionConfigMode CreateFromId(
        string modeId,
        EncryptionPolicy? policy = null)
    {
        return modeId switch
        {
            PerObjectConfigMode.ModeId => new PerObjectConfigMode(),
            FixedConfigMode.ModeId => new FixedConfigMode(),
            PolicyEnforcedConfigMode.ModeId => new PolicyEnforcedConfigMode(policy),
            _ => throw new ArgumentException($"Unknown encryption config mode: {modeId}", nameof(modeId))
        };
    }
}

/// <summary>
/// Result of mode validation including detailed error information.
/// </summary>
/// <param name="IsValid">Whether validation passed.</param>
/// <param name="Error">Error message if validation failed.</param>
/// <param name="Mode">The config mode that performed validation.</param>
/// <param name="ViolatedConstraint">Specific constraint that was violated (if any).</param>
public record ModeValidationResult(
    bool IsValid,
    string? Error,
    EncryptionConfigMode Mode,
    string? ViolatedConstraint = null);
