using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using System.Text;
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

#endregion

#region Secret Manager Implementation

/// <summary>
/// Production-ready secret manager with multi-provider support.
/// </summary>
public class SecretManager : ISecretManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CachedSecret> _cache = new();
    private readonly ConcurrentDictionary<SecretProvider, ISecretProvider> _providers = new();
    private readonly SemaphoreSlim _rotationLock = new(1, 1);
    private readonly SecretManagerConfig _config;
    private readonly Timer? _cacheCleanupTimer;
    private readonly Timer? _rotationCheckTimer;
    private bool _disposed;

    // Patterns that indicate plain-text secrets
    private static readonly Regex[] PlainTextSecretPatterns = new[]
    {
        new Regex(@"password\s*[:=]\s*[""']?[^""'\s]{8,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"secret\s*[:=]\s*[""']?[^""'\s]{8,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"api[_-]?key\s*[:=]\s*[""']?[^""'\s]{16,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"access[_-]?key\s*[:=]\s*[""']?[A-Z0-9]{16,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"private[_-]?key\s*[:=]\s*[""']?-----BEGIN", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"connection[_-]?string\s*[:=]\s*[""']?[^""'\s]*password=[^""'\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"bearer\s+[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", RegexOptions.Compiled), // JWT
    };

    public SecretManager(SecretManagerConfig? config = null)
    {
        _config = config ?? new SecretManagerConfig();

        // Start cache cleanup timer
        if (_config.CacheCleanupInterval > TimeSpan.Zero)
        {
            _cacheCleanupTimer = new Timer(
                _ => CleanupExpiredCache(),
                null,
                _config.CacheCleanupInterval,
                _config.CacheCleanupInterval);
        }

        // Start rotation check timer
        if (_config.RotationCheckInterval > TimeSpan.Zero)
        {
            _rotationCheckTimer = new Timer(
                async _ => await CheckRotationsAsync(),
                null,
                _config.RotationCheckInterval,
                _config.RotationCheckInterval);
        }
    }

    /// <summary>
    /// Registers a secret provider.
    /// </summary>
    public void RegisterProvider(SecretProvider type, ISecretProvider provider)
    {
        _providers[type] = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<string> GetSecretAsync(SecretReference reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var cacheKey = GetCacheKey(reference);
        var ttl = reference.CacheTtl ?? _config.DefaultCacheTtl;

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Value;
        }

        // Get from provider
        var provider = GetProvider(reference.Provider);
        var value = await provider.GetSecretAsync(reference.Path, reference.Version, ct);

        // Extract field if specified
        if (!string.IsNullOrEmpty(reference.Field) && value.StartsWith('{'))
        {
            value = ExtractField(value, reference.Field);
        }

        // Cache the result
        _cache[cacheKey] = new CachedSecret
        {
            Value = value,
            ExpiresAt = DateTime.UtcNow.Add(ttl),
            Reference = reference
        };

        return value;
    }

    public async Task<byte[]> GetSecretBytesAsync(SecretReference reference, CancellationToken ct = default)
    {
        var value = await GetSecretAsync(reference, ct);
        return Convert.FromBase64String(value);
    }

    public async Task SetSecretAsync(SecretReference reference, string value, SecretMetadata? metadata = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(value);

        var provider = GetProvider(reference.Provider);
        await provider.SetSecretAsync(reference.Path, value, metadata, ct);

        // Invalidate cache
        var cacheKey = GetCacheKey(reference);
        _cache.TryRemove(cacheKey, out _);
    }

    public async Task<SecretRotationResult> RotateSecretAsync(SecretReference reference, string newValue, TimeSpan gracePeriod, CancellationToken ct = default)
    {
        await _rotationLock.WaitAsync(ct);
        try
        {
            var provider = GetProvider(reference.Provider);

            // Get current version
            string? oldVersion = null;
            try
            {
                oldVersion = await provider.GetCurrentVersionAsync(reference.Path, ct);
            }
            catch { /* No previous version */ }

            // Set new secret with version
            var newVersion = $"v{DateTime.UtcNow:yyyyMMddHHmmss}";
            var metadata = new SecretMetadata
            {
                CreatedBy = "SecretManager.Rotate",
                Tags = new Dictionary<string, string>
                {
                    ["rotation"] = "true",
                    ["previousVersion"] = oldVersion ?? "none",
                    ["gracePeriodEnds"] = DateTime.UtcNow.Add(gracePeriod).ToString("O")
                }
            };

            await provider.SetSecretVersionAsync(reference.Path, newVersion, newValue, metadata, ct);

            // Invalidate cache
            var cacheKey = GetCacheKey(reference);
            _cache.TryRemove(cacheKey, out _);

            return new SecretRotationResult
            {
                Success = true,
                NewVersion = newVersion,
                OldVersion = oldVersion,
                GracePeriodEndsAt = DateTime.UtcNow.Add(gracePeriod)
            };
        }
        catch (Exception ex)
        {
            return new SecretRotationResult
            {
                Success = false,
                Error = ex.Message
            };
        }
        finally
        {
            _rotationLock.Release();
        }
    }

    public async Task DeleteSecretAsync(SecretReference reference, CancellationToken ct = default)
    {
        var provider = GetProvider(reference.Provider);
        await provider.DeleteSecretAsync(reference.Path, ct);

        // Invalidate cache
        var cacheKey = GetCacheKey(reference);
        _cache.TryRemove(cacheKey, out _);
    }

    public async Task<bool> ExistsAsync(SecretReference reference, CancellationToken ct = default)
    {
        var provider = GetProvider(reference.Provider);
        return await provider.ExistsAsync(reference.Path, ct);
    }

    public async Task<IEnumerable<SecretReference>> ListSecretsAsync(string? prefix = null, CancellationToken ct = default)
    {
        var results = new List<SecretReference>();

        foreach (var (providerType, provider) in _providers)
        {
            var paths = await provider.ListSecretsAsync(prefix, ct);
            results.AddRange(paths.Select(p => new SecretReference { Provider = providerType, Path = p }));
        }

        return results;
    }

    public void ValidateNoPlainTextSecrets(object config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var violations = new List<string>();

        foreach (var pattern in PlainTextSecretPatterns)
        {
            var matches = pattern.Matches(json);
            foreach (Match match in matches)
            {
                // Check if it's actually a SecretReference URI
                if (match.Value.Contains("://") && Regex.IsMatch(match.Value, @"^(vault|env|keystore|azure|aws|gcp)://"))
                    continue;

                violations.Add($"Potential plain-text secret found: {match.Value[..Math.Min(30, match.Value.Length)]}...");
            }
        }

        if (violations.Count > 0)
        {
            throw new SecurityException(
                $"Plain-text secrets detected in configuration. Use SecretReference instead.\n" +
                $"Violations:\n- {string.Join("\n- ", violations)}");
        }
    }

    public async Task<T> ResolveSecretsAsync<T>(T config, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(config);

        var configType = typeof(T);
        var properties = configType.GetProperties()
            .Where(p => p.PropertyType == typeof(SecretReference) || p.PropertyType == typeof(string));

        foreach (var prop in properties)
        {
            if (prop.PropertyType == typeof(SecretReference))
            {
                var reference = prop.GetValue(config) as SecretReference;
                if (reference != null)
                {
                    // Find corresponding string property (e.g., PasswordReference -> Password)
                    var resolvedPropName = prop.Name.Replace("Reference", "");
                    var resolvedProp = configType.GetProperty(resolvedPropName);
                    if (resolvedProp?.PropertyType == typeof(string))
                    {
                        var value = await GetSecretAsync(reference, ct);
                        resolvedProp.SetValue(config, value);
                    }
                }
            }
            else if (prop.PropertyType == typeof(string))
            {
                var value = prop.GetValue(config) as string;
                // Check if it's a secret reference URI
                if (!string.IsNullOrEmpty(value) && Regex.IsMatch(value, @"^(vault|env|keystore|azure|aws|gcp)://"))
                {
                    var reference = SecretReference.Parse(value);
                    var resolvedValue = await GetSecretAsync(reference, ct);
                    prop.SetValue(config, resolvedValue);
                }
            }
        }

        return config;
    }

    private ISecretProvider GetProvider(SecretProvider type)
    {
        if (_providers.TryGetValue(type, out var provider))
            return provider;

        // Fall back to default providers
        return type switch
        {
            SecretProvider.Environment => new EnvironmentSecretProvider(),
            _ => throw new InvalidOperationException($"No provider registered for {type}")
        };
    }

    private static string GetCacheKey(SecretReference reference)
    {
        return $"{reference.Provider}://{reference.Path}:{reference.Version ?? "latest"}:{reference.Field ?? ""}";
    }

    private static string ExtractField(string json, string field)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(field, out var value))
            {
                return value.GetString() ?? throw new KeyNotFoundException($"Field '{field}' is null");
            }
            throw new KeyNotFoundException($"Field '{field}' not found in secret");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("Secret value is not valid JSON, cannot extract field");
        }
    }

    private void CleanupExpiredCache()
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _cache
            .Where(kv => kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private async Task CheckRotationsAsync()
    {
        // Check for secrets that need rotation based on their metadata
        // This would integrate with the registered providers
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cacheCleanupTimer?.Dispose();
        _rotationCheckTimer?.Dispose();

        // Clear sensitive data from cache
        foreach (var cached in _cache.Values)
        {
            // Zero out the string if possible (defensive)
            try
            {
                unsafe
                {
                    fixed (char* ptr = cached.Value)
                    {
                        for (int i = 0; i < cached.Value.Length; i++)
                            ptr[i] = '\0';
                    }
                }
            }
            catch { /* Best effort */ }
        }
        _cache.Clear();

        foreach (var provider in _providers.Values)
        {
            if (provider is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (provider is IDisposable disposable)
                disposable.Dispose();
        }

        _rotationLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private class CachedSecret
    {
        public string Value { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public SecretReference Reference { get; set; } = null!;
    }
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

/// <summary>
/// Environment variable secret provider.
/// </summary>
public class EnvironmentSecretProvider : ISecretProvider
{
    public Task<string> GetSecretAsync(string path, string? version = null, CancellationToken ct = default)
    {
        var value = Environment.GetEnvironmentVariable(path);
        if (value == null)
            throw new KeyNotFoundException($"Environment variable '{path}' not found");
        return Task.FromResult(value);
    }

    public Task SetSecretAsync(string path, string value, SecretMetadata? metadata = null, CancellationToken ct = default)
    {
        Environment.SetEnvironmentVariable(path, value);
        return Task.CompletedTask;
    }

    public Task SetSecretVersionAsync(string path, string version, string value, SecretMetadata? metadata = null, CancellationToken ct = default)
    {
        // Environment variables don't support versioning
        return SetSecretAsync(path, value, metadata, ct);
    }

    public Task<string?> GetCurrentVersionAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null); // No versioning support
    }

    public Task DeleteSecretAsync(string path, CancellationToken ct = default)
    {
        Environment.SetEnvironmentVariable(path, null);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(Environment.GetEnvironmentVariable(path) != null);
    }

    public Task<IEnumerable<string>> ListSecretsAsync(string? prefix = null, CancellationToken ct = default)
    {
        var vars = Environment.GetEnvironmentVariables();
        var keys = vars.Keys.Cast<string>();

        if (!string.IsNullOrEmpty(prefix))
            keys = keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(keys);
    }
}

#endregion
