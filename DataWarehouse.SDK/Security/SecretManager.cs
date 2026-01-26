using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Security;

#region Secret Manager Interfaces

/// <summary>
/// Centralized secret management interface.
/// All credentials, API keys, and sensitive configuration MUST use this interface.
/// Plain-text credentials are PROHIBITED in production.
/// </summary>
public interface ISecretManager
{
    /// <summary>
    /// Retrieves a secret by reference.
    /// </summary>
    Task<string> GetSecretAsync(SecretReference reference, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a secret as bytes.
    /// </summary>
    Task<byte[]> GetSecretBytesAsync(SecretReference reference, CancellationToken ct = default);

    /// <summary>
    /// Stores a secret securely.
    /// </summary>
    Task SetSecretAsync(SecretReference reference, string value, SecretMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Rotates a secret, keeping the old version available for a grace period.
    /// </summary>
    Task<SecretRotationResult> RotateSecretAsync(SecretReference reference, string newValue, TimeSpan gracePeriod, CancellationToken ct = default);

    /// <summary>
    /// Deletes a secret permanently.
    /// </summary>
    Task DeleteSecretAsync(SecretReference reference, CancellationToken ct = default);

    /// <summary>
    /// Checks if a secret exists.
    /// </summary>
    Task<bool> ExistsAsync(SecretReference reference, CancellationToken ct = default);

    /// <summary>
    /// Lists all secret references (not values) matching a pattern.
    /// </summary>
    Task<IEnumerable<SecretReference>> ListSecretsAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>
    /// Validates that a configuration does not contain plain-text secrets.
    /// Throws if violations found.
    /// </summary>
    void ValidateNoPlainTextSecrets(object config);

    /// <summary>
    /// Resolves all secret references in a configuration object.
    /// </summary>
    Task<T> ResolveSecretsAsync<T>(T config, CancellationToken ct = default) where T : class;
}

/// <summary>
/// Reference to a secret stored in the secret manager.
/// Use this instead of plain-text credentials in configuration.
/// </summary>
public class SecretReference
{
    /// <summary>
    /// The secret provider (vault, env, file, keystore).
    /// </summary>
    public SecretProvider Provider { get; set; } = SecretProvider.Vault;

    /// <summary>
    /// The secret path/key.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Optional specific version (for rotation support).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Optional field within a structured secret (e.g., JSON).
    /// </summary>
    public string? Field { get; set; }

    /// <summary>
    /// Cache TTL for this secret. Null = use default.
    /// </summary>
    public TimeSpan? CacheTtl { get; set; }

    /// <summary>
    /// Creates a reference from a URI string.
    /// Format: provider://path[?version=v1&field=password]
    /// Examples:
    ///   vault://secrets/database/prod?field=password
    ///   env://DATABASE_PASSWORD
    ///   keystore://encryption/master-key
    /// </summary>
    public static SecretReference Parse(string uri)
    {
        var match = Regex.Match(uri, @"^(?<provider>\w+)://(?<path>[^?]+)(?:\?(?<query>.+))?$");
        if (!match.Success)
            throw new ArgumentException($"Invalid secret reference URI: {uri}");

        var reference = new SecretReference
        {
            Provider = Enum.Parse<SecretProvider>(match.Groups["provider"].Value, ignoreCase: true),
            Path = match.Groups["path"].Value
        };

        var queryString = match.Groups["query"].Value;
        if (!string.IsNullOrEmpty(queryString))
        {
            var queryParams = queryString.Split('&')
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1]);

            if (queryParams.TryGetValue("version", out var version))
                reference.Version = version;
            if (queryParams.TryGetValue("field", out var field))
                reference.Field = field;
            if (queryParams.TryGetValue("ttl", out var ttl) && int.TryParse(ttl, out var ttlSeconds))
                reference.CacheTtl = TimeSpan.FromSeconds(ttlSeconds);
        }

        return reference;
    }

    /// <summary>
    /// Converts to URI string.
    /// </summary>
    public override string ToString()
    {
        var uri = $"{Provider.ToString().ToLower()}://{Path}";
        var queryParams = new List<string>();

        if (!string.IsNullOrEmpty(Version))
            queryParams.Add($"version={Version}");
        if (!string.IsNullOrEmpty(Field))
            queryParams.Add($"field={Field}");
        if (CacheTtl.HasValue)
            queryParams.Add($"ttl={(int)CacheTtl.Value.TotalSeconds}");

        if (queryParams.Count > 0)
            uri += "?" + string.Join("&", queryParams);

        return uri;
    }

    /// <summary>
    /// Creates a vault reference.
    /// </summary>
    public static SecretReference Vault(string path, string? field = null)
        => new() { Provider = SecretProvider.Vault, Path = path, Field = field };

    /// <summary>
    /// Creates an environment variable reference.
    /// </summary>
    public static SecretReference Env(string variableName)
        => new() { Provider = SecretProvider.Environment, Path = variableName };

    /// <summary>
    /// Creates a keystore reference.
    /// </summary>
    public static SecretReference KeyStore(string keyId)
        => new() { Provider = SecretProvider.KeyStore, Path = keyId };
}

/// <summary>
/// Secret provider types.
/// </summary>
public enum SecretProvider
{
    /// <summary>HashiCorp Vault or similar secret vault.</summary>
    Vault,
    /// <summary>Environment variable.</summary>
    Environment,
    /// <summary>Local keystore (DPAPI, Keychain, etc.).</summary>
    KeyStore,
    /// <summary>Azure Key Vault.</summary>
    AzureKeyVault,
    /// <summary>AWS Secrets Manager.</summary>
    AwsSecretsManager,
    /// <summary>Google Cloud Secret Manager.</summary>
    GcpSecretManager,
    /// <summary>Kubernetes secrets.</summary>
    Kubernetes,
    /// <summary>Encrypted file.</summary>
    EncryptedFile
}

/// <summary>
/// Metadata for a secret.
/// </summary>
public class SecretMetadata
{
    public DateTime? ExpiresAt { get; set; }
    public string? Description { get; set; }
    public string? CreatedBy { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
    public bool RotationEnabled { get; set; }
    public TimeSpan? RotationInterval { get; set; }
}

/// <summary>
/// Result of a secret rotation operation.
/// </summary>
public class SecretRotationResult
{
    public bool Success { get; set; }
    public string? NewVersion { get; set; }
    public string? OldVersion { get; set; }
    public DateTime GracePeriodEndsAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Configuration for the secret manager.
/// </summary>
public class SecretManagerConfig
{
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan CacheCleanupInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RotationCheckInterval { get; set; } = TimeSpan.FromHours(1);
    public bool EnforceNoPlainTextSecrets { get; set; } = true;
}

#endregion

#region Secret Provider Interface

/// <summary>
/// Interface for secret storage backends.
/// </summary>
public interface ISecretProvider
{
    Task<string> GetSecretAsync(string path, string? version = null, CancellationToken ct = default);
    Task SetSecretAsync(string path, string value, SecretMetadata? metadata = null, CancellationToken ct = default);
    Task SetSecretVersionAsync(string path, string version, string value, SecretMetadata? metadata = null, CancellationToken ct = default);
    Task<string?> GetCurrentVersionAsync(string path, CancellationToken ct = default);
    Task DeleteSecretAsync(string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    Task<IEnumerable<string>> ListSecretsAsync(string? prefix = null, CancellationToken ct = default);
}

#endregion
