using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.DevCiCd
{
    /// <summary>
    /// Environment variable-based KeyStore strategy for development, CI/CD, and containerized deployments.
    /// Implements IKeyStoreStrategy with simple, stateless key storage via environment variables.
    ///
    /// Features:
    /// - Zero-configuration key storage (no files, databases, or external services)
    /// - Ideal for Docker containers, Kubernetes secrets, CI/CD pipelines
    /// - Automatic key derivation from environment variables
    /// - Support for multiple key IDs via prefixed environment variables
    /// - Stateless operation (no persistent storage)
    ///
    /// Configuration:
    /// - KeyPrefix: Environment variable prefix (default: "DATAWAREHOUSE_KEY_")
    /// - DefaultKeyEnvVar: Environment variable for default key (default: "DATAWAREHOUSE_MASTER_KEY")
    /// - AutoGenerateIfMissing: Generate keys if environment variable not found (default: false)
    /// - KeyDerivationSalt: Salt for key derivation (default: "DataWarehouse.Env.Salt.v1")
    ///
    /// Usage:
    /// - Set environment variables like: DATAWAREHOUSE_KEY_mykey=base64_encoded_key
    /// - Or use default: DATAWAREHOUSE_MASTER_KEY=base64_encoded_key
    /// </summary>
    public sealed class EnvironmentKeyStoreStrategy : KeyStoreStrategyBase
    {
        private EnvironmentKeyStoreConfig _config = new();
        private readonly BoundedDictionary<string, byte[]> _generatedKeys = new BoundedDictionary<string, byte[]>(1000);
        private string? _currentKeyId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "EnvironmentVariables",
                ["Stateless"] = true,
                ["Platform"] = "Cross-Platform",
                ["IdealFor"] = "Docker, Kubernetes, CI/CD"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("environmentkeystore.shutdown");
            _generatedKeys.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("KeyPrefix", out var prefixObj) && prefixObj is string prefix)
                _config.KeyPrefix = prefix;
            if (Configuration.TryGetValue("DefaultKeyEnvVar", out var envVarObj) && envVarObj is string envVar)
                _config.DefaultKeyEnvVar = envVar;
            if (Configuration.TryGetValue("AutoGenerateIfMissing", out var autoGenObj) && autoGenObj is bool autoGen)
                _config.AutoGenerateIfMissing = autoGen;
            if (Configuration.TryGetValue("KeyDerivationSalt", out var saltObj) && saltObj is string salt)
                _config.KeyDerivationSalt = salt;

            _currentKeyId = "default";

            return Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            // Check if at least the default key environment variable exists or auto-generation is enabled
            var defaultKeyExists = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(_config.DefaultKeyEnvVar));
            return Task.FromResult(defaultKeyExists || _config.AutoGenerateIfMissing);
        }

        protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // Try to get key from environment variable
            var envVarName = GetEnvironmentVariableName(keyId);
            var keyValue = Environment.GetEnvironmentVariable(envVarName);

            if (!string.IsNullOrEmpty(keyValue))
            {
                try
                {
                    // Attempt to decode as base64
                    return Task.FromResult(Convert.FromBase64String(keyValue));
                }
                catch
                {
                    // If not base64, derive key from the value
                    return Task.FromResult(DeriveKeyFromString(keyValue));
                }
            }

            // Check if key was previously auto-generated
            if (_generatedKeys.TryGetValue(keyId, out var generatedKey))
            {
                return Task.FromResult(generatedKey);
            }

            // Auto-generate key if configured
            if (_config.AutoGenerateIfMissing)
            {
                var newKey = GenerateKey();
                _generatedKeys[keyId] = newKey;
                return Task.FromResult(newKey);
            }

            throw new KeyNotFoundException($"Environment variable '{envVarName}' not found and auto-generation is disabled.");
        }

        protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // Environment variables are typically read-only in production
            // We store generated keys in memory for the session
            _generatedKeys[keyId] = keyData;
            _currentKeyId = keyId;

            // Optionally, we could suggest setting the environment variable
            var envVarName = GetEnvironmentVariableName(keyId);
            var keyBase64 = Convert.ToBase64String(keyData);

            System.Diagnostics.Trace.TraceInformation(
                $"Key '{keyId}' generated. To persist, set environment variable: {envVarName}={keyBase64}");

            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keys = new List<string>();

            // Find all environment variables with the configured prefix
            var allEnvVars = Environment.GetEnvironmentVariables();
            foreach (var key in allEnvVars.Keys)
            {
                var keyStr = key?.ToString() ?? "";
                if (keyStr.StartsWith(_config.KeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var keyId = keyStr.Substring(_config.KeyPrefix.Length);
                    if (!string.IsNullOrEmpty(keyId))
                    {
                        keys.Add(keyId);
                    }
                }
            }

            // Add the default key if it exists
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(_config.DefaultKeyEnvVar)))
            {
                keys.Add("default");
            }

            // Add auto-generated keys
            keys.AddRange(_generatedKeys.Keys);

            return Task.FromResult<IReadOnlyList<string>>(keys.Distinct().ToList().AsReadOnly());
        }

        public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            // Remove from generated keys cache
            _generatedKeys.TryRemove(keyId, out _);

            // Note: Cannot delete actual environment variables at runtime
            System.Diagnostics.Trace.TraceWarning(
                $"Key '{keyId}' removed from cache. To permanently delete, remove environment variable: {GetEnvironmentVariableName(keyId)}");

            return Task.CompletedTask;
        }

        public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var envVarName = GetEnvironmentVariableName(keyId);
            var keyValue = Environment.GetEnvironmentVariable(envVarName);
            var isGenerated = _generatedKeys.ContainsKey(keyId);

            if (string.IsNullOrEmpty(keyValue) && !isGenerated)
                return Task.FromResult<KeyMetadata?>(null);

            return Task.FromResult<KeyMetadata?>(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow, // Unknown for env vars
                IsActive = keyId == _currentKeyId,
                KeySizeBytes = 32, // Assuming default
                Metadata = new Dictionary<string, object>
                {
                    ["Source"] = isGenerated ? "AutoGenerated" : "EnvironmentVariable",
                    ["EnvironmentVariable"] = envVarName,
                    ["IsEphemeral"] = isGenerated
                }
            });
        }

        private string GetEnvironmentVariableName(string keyId)
        {
            if (keyId.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                return _config.DefaultKeyEnvVar;
            }

            return $"{_config.KeyPrefix}{keyId}";
        }

        private byte[] DeriveKeyFromString(string value)
        {
            // Derive a cryptographically strong key from a string value
            var salt = SHA256.HashData(Encoding.UTF8.GetBytes(_config.KeyDerivationSalt));
            return Rfc2898DeriveBytes.Pbkdf2(value, salt, 100000, HashAlgorithmName.SHA256, 32); // 256-bit key
        }
    }

    /// <summary>
    /// Configuration for environment variable-based key store strategy.
    /// </summary>
    public class EnvironmentKeyStoreConfig
    {
        /// <summary>
        /// Prefix for environment variables containing keys.
        /// Keys will be read from: {KeyPrefix}{keyId}
        /// </summary>
        public string KeyPrefix { get; set; } = "DATAWAREHOUSE_KEY_";

        /// <summary>
        /// Environment variable name for the default key.
        /// </summary>
        public string DefaultKeyEnvVar { get; set; } = "DATAWAREHOUSE_MASTER_KEY";

        /// <summary>
        /// If true, automatically generate keys if environment variable not found.
        /// Generated keys are ephemeral and exist only for the process lifetime.
        /// </summary>
        public bool AutoGenerateIfMissing { get; set; } = false;

        /// <summary>
        /// Salt used for deriving keys from non-base64 environment variable values.
        /// </summary>
        public string KeyDerivationSalt { get; set; } = "DataWarehouse.Env.Salt.v1";
    }
}
