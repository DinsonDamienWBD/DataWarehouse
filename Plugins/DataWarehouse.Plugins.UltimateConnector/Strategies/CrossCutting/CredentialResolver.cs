using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Cross-cutting credential resolver that integrates with the UltimateKeyManagement
    /// plugin (T94) via the message bus to retrieve connection credentials at runtime.
    /// Falls back to <see cref="ConnectionConfig.AuthCredential"/> when the key management
    /// service is unavailable.
    /// </summary>
    /// <remarks>
    /// Credential resolution follows a priority chain:
    /// 1. Message bus request to "keystore.get" topic (T94 UltimateKeyManagement)
    /// 2. ConnectionConfig.AuthCredential as static fallback
    /// 3. Environment variable lookup as last resort
    ///
    /// All resolved credentials are short-lived and never cached beyond the configured TTL.
    /// </remarks>
    public sealed class CredentialResolver
    {
        private readonly IMessageBus? _messageBus;
        private readonly ILogger? _logger;
        private readonly TimeSpan _requestTimeout;

        /// <summary>
        /// Topic used to request credentials from the UltimateKeyManagement plugin.
        /// </summary>
        public const string KeystoreGetTopic = "keystore.get";

        /// <summary>
        /// Topic used to request credential rotation status.
        /// </summary>
        public const string KeystoreStatusTopic = "keystore.status";

        /// <summary>
        /// Initializes a new instance of <see cref="CredentialResolver"/>.
        /// </summary>
        /// <param name="messageBus">Message bus for inter-plugin communication. May be null if unavailable.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        /// <param name="requestTimeout">Timeout for keystore requests. Defaults to 5 seconds.</param>
        public CredentialResolver(IMessageBus? messageBus, ILogger? logger = null, TimeSpan? requestTimeout = null)
        {
            _messageBus = messageBus;
            _logger = logger;
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Resolves the credential for a connection, attempting the keystore first,
        /// then falling back to config and environment variables.
        /// </summary>
        /// <param name="config">Connection configuration containing the static credential fallback.</param>
        /// <param name="credentialKey">Logical key identifying the credential in the keystore.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The resolved credential information.</returns>
        public async Task<ResolvedCredential> ResolveCredentialAsync(
            ConnectionConfig config,
            string credentialKey,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrWhiteSpace(credentialKey);

            var fromKeystore = await TryResolveFromKeystoreAsync(credentialKey, ct);
            if (fromKeystore != null)
            {
                _logger?.LogInformation(
                    "Credential '{Key}' resolved from keystore via message bus",
                    credentialKey);
                return fromKeystore;
            }

            if (!string.IsNullOrEmpty(config.AuthCredential))
            {
                _logger?.LogInformation(
                    "Credential '{Key}' resolved from ConnectionConfig fallback",
                    credentialKey);
                return new ResolvedCredential(
                    Value: config.AuthCredential,
                    Source: CredentialSource.Config,
                    SecondaryValue: config.AuthSecondary,
                    AuthMethod: config.AuthMethod,
                    ExpiresAt: null);
            }

            var envValue = ResolveFromEnvironment(credentialKey);
            if (!string.IsNullOrEmpty(envValue))
            {
                _logger?.LogInformation(
                    "Credential '{Key}' resolved from environment variable",
                    credentialKey);
                return new ResolvedCredential(
                    Value: envValue,
                    Source: CredentialSource.Environment,
                    SecondaryValue: null,
                    AuthMethod: config.AuthMethod,
                    ExpiresAt: null);
            }

            _logger?.LogWarning(
                "Credential '{Key}' could not be resolved from any source",
                credentialKey);
            throw new InvalidOperationException(
                $"Unable to resolve credential '{credentialKey}' from keystore, config, or environment.");
        }

        /// <summary>
        /// Checks whether the keystore service is reachable via the message bus.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the keystore responded to a status check.</returns>
        public async Task<bool> IsKeystoreAvailableAsync(CancellationToken ct = default)
        {
            if (_messageBus == null) return false;

            try
            {
                var message = PluginMessage.Create(
                    "keystore.health",
                    new Dictionary<string, object> { ["check"] = "ping" });

                var response = await _messageBus.SendAsync(KeystoreStatusTopic, message, _requestTimeout, ct);
                return response.Success;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Attempts to resolve a credential from the T94 UltimateKeyManagement keystore.
        /// </summary>
        private async Task<ResolvedCredential?> TryResolveFromKeystoreAsync(
            string credentialKey, CancellationToken ct)
        {
            if (_messageBus == null) return null;

            try
            {
                var message = PluginMessage.Create(
                    "keystore.get",
                    new Dictionary<string, object>
                    {
                        ["key"] = credentialKey,
                        ["purpose"] = "connection-credential",
                        ["requestor"] = "UltimateConnector"
                    });

                var response = await _messageBus.SendAsync(KeystoreGetTopic, message, _requestTimeout, ct);

                if (!response.Success)
                {
                    _logger?.LogDebug(
                        "Keystore returned failure for key '{Key}': {Error}",
                        credentialKey, response.ErrorMessage);
                    return null;
                }

                if (response.Payload is not Dictionary<string, object> payload)
                    return null;

                var value = payload.GetValueOrDefault("credential")?.ToString();
                if (string.IsNullOrEmpty(value)) return null;

                var secondary = payload.GetValueOrDefault("secondary")?.ToString();
                var authMethod = payload.GetValueOrDefault("auth_method")?.ToString() ?? "bearer";
                var expiresAtStr = payload.GetValueOrDefault("expires_at")?.ToString();

                DateTimeOffset? expiresAt = null;
                if (DateTimeOffset.TryParse(expiresAtStr, out var parsed))
                    expiresAt = parsed;

                return new ResolvedCredential(
                    Value: value,
                    Source: CredentialSource.Keystore,
                    SecondaryValue: secondary,
                    AuthMethod: authMethod,
                    ExpiresAt: expiresAt);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to resolve credential '{Key}' from keystore, falling back",
                    credentialKey);
                return null;
            }
        }

        /// <summary>
        /// Attempts to resolve a credential from environment variables using common naming patterns.
        /// </summary>
        private static string? ResolveFromEnvironment(string credentialKey)
        {
            var normalizedKey = credentialKey.Replace('.', '_').Replace('-', '_').ToUpperInvariant();

            var candidates = new[]
            {
                $"DW_CREDENTIAL_{normalizedKey}",
                $"DATAWAREHOUSE_{normalizedKey}",
                normalizedKey
            };

            foreach (var candidate in candidates)
            {
                var value = Environment.GetEnvironmentVariable(candidate);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            return null;
        }
    }

    /// <summary>
    /// A resolved credential with metadata about its source and expiration.
    /// </summary>
    /// <param name="Value">The credential value (token, password, API key).</param>
    /// <param name="Source">Where the credential was resolved from.</param>
    /// <param name="SecondaryValue">Optional secondary value (username for basic auth).</param>
    /// <param name="AuthMethod">Authentication method to use with this credential.</param>
    /// <param name="ExpiresAt">When this credential expires, if known.</param>
    public sealed record ResolvedCredential(
        string Value,
        CredentialSource Source,
        string? SecondaryValue,
        string AuthMethod,
        DateTimeOffset? ExpiresAt)
    {
        /// <summary>
        /// Whether this credential has expired.
        /// </summary>
        public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;
    }

    /// <summary>
    /// Source of a resolved credential.
    /// </summary>
    public enum CredentialSource
    {
        /// <summary>Resolved from the T94 UltimateKeyManagement keystore via message bus.</summary>
        Keystore,
        /// <summary>Resolved from the ConnectionConfig.AuthCredential property.</summary>
        Config,
        /// <summary>Resolved from an environment variable.</summary>
        Environment
    }
}
