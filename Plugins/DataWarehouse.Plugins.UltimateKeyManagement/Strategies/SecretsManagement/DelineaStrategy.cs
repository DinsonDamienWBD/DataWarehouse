using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement
{
    /// <summary>
    /// Delinea (Thycotic) Secret Server KeyStore strategy supporting template-based secrets storage.
    /// Implements IKeyStoreStrategy for enterprise secrets management.
    ///
    /// Supported features:
    /// - Secret template-based storage
    /// - REST API authentication (username/password or API key)
    /// - Folder-based organization
    /// - Key rotation support
    /// - Audit logging
    ///
    /// Configuration:
    /// - ServerUrl: Secret Server URL (e.g., "https://secretserver.example.com")
    /// - Username: Authentication username (optional if using ApiKey)
    /// - Password: Authentication password (optional if using ApiKey)
    /// - ApiKey: API key for authentication (optional if using username/password)
    /// - FolderId: Folder ID for key storage (default: -1 for root)
    /// - SecretTemplateId: Template ID for secrets (default: 6000 for generic secrets)
    /// </summary>
    public sealed class DelineaStrategy : KeyStoreStrategyBase
    {
        private readonly HttpClient _httpClient;
        private DelineaConfig _config = new();
        private string? _currentKeyId;
        private string? _accessToken;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Delinea",
                ["Backend"] = "Secret Server",
                ["AuthMethod"] = "Username/Password or API Key",
                ["TemplateBased"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("delinea.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public DelineaStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("delinea.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("ServerUrl", out var urlObj) && urlObj is string url)
                _config.ServerUrl = url;
            if (Configuration.TryGetValue("Username", out var usernameObj) && usernameObj is string username)
                _config.Username = username;
            if (Configuration.TryGetValue("Password", out var passwordObj) && passwordObj is string password)
                _config.Password = password;
            if (Configuration.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is string apiKey)
                _config.ApiKey = apiKey;
            if (Configuration.TryGetValue("FolderId", out var folderObj) && folderObj is int folderId)
                _config.FolderId = folderId;
            if (Configuration.TryGetValue("SecretTemplateId", out var templateObj) && templateObj is int templateId)
                _config.SecretTemplateId = templateId;

            // Authenticate
            await AuthenticateAsync(cancellationToken);

            // Validate connection
            var isHealthy = await IsHealthyAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to Delinea Secret Server at {_config.ServerUrl}");
            }

            _currentKeyId = "datawarehouse-master";

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "datawarehouse-master");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            return await IsHealthyAsync(cancellationToken);
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // First, search for the secret by name
            var secretId = await FindSecretByNameAsync(keyId);
            if (!secretId.HasValue)
            {
                throw new KeyNotFoundException($"Secret with name '{keyId}' not found.");
            }

            var request = CreateRequest(HttpMethod.Get, $"/api/v1/secrets/{secretId.Value}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var secret = JsonSerializer.Deserialize<JsonElement>(json);

            // Find the field containing the key data (typically named "Key" or "Password")
            var items = secret.GetProperty("items");
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("fieldName", out var fieldName) &&
                    (fieldName.GetString() == "Key" || fieldName.GetString() == "Password"))
                {
                    var itemValue = item.GetProperty("itemValue").GetString();
                    return Convert.FromBase64String(itemValue!);
                }
            }

            throw new InvalidOperationException($"Key data not found in secret '{keyId}'.");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var keyBase64 = Convert.ToBase64String(keyData);

            // Create a new secret using the template
            var secretPayload = new
            {
                name = keyId,
                secretTemplateId = _config.SecretTemplateId,
                folderId = _config.FolderId,
                items = new[]
                {
                    new { fieldName = "Key", itemValue = keyBase64 }
                }
            };

            var content = JsonSerializer.Serialize(secretPayload);
            var request = CreateRequest(HttpMethod.Post, "/api/v1/secrets");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var request = CreateRequest(HttpMethod.Get, $"/api/v1/secrets?filter.folderId={_config.FolderId}");
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return Array.Empty<string>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<JsonElement>(json);

                var keys = new List<string>();
                if (result.TryGetProperty("records", out var records))
                {
                    foreach (var record in records.EnumerateArray())
                    {
                        if (record.TryGetProperty("name", out var name))
                        {
                            keys.Add(name.GetString() ?? "");
                        }
                    }
                }

                return keys.AsReadOnly();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var secretId = await FindSecretByNameAsync(keyId);
            if (!secretId.HasValue)
            {
                throw new KeyNotFoundException($"Secret with name '{keyId}' not found.");
            }

            var request = CreateRequest(HttpMethod.Delete, $"/api/v1/secrets/{secretId.Value}");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var secretId = await FindSecretByNameAsync(keyId);
                if (!secretId.HasValue)
                    return null;

                var request = CreateRequest(HttpMethod.Get, $"/api/v1/secrets/{secretId.Value}");
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var secret = JsonSerializer.Deserialize<JsonElement>(json);

                var createdAt = secret.TryGetProperty("createDate", out var cd)
                    ? DateTime.Parse(cd.GetString()!)
                    : DateTime.UtcNow;

                var version = secret.TryGetProperty("secretVersion", out var sv)
                    ? sv.GetInt32()
                    : 1;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    Version = version,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SecretId"] = secretId.Value,
                        ["Backend"] = "Delinea Secret Server",
                        ["FolderId"] = _config.FolderId
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<int?> FindSecretByNameAsync(string keyId)
        {
            var request = CreateRequest(HttpMethod.Get, $"/api/v1/secrets?filter.searchText={Uri.EscapeDataString(keyId)}&filter.folderId={_config.FolderId}");
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (result.TryGetProperty("records", out var records))
            {
                foreach (var record in records.EnumerateArray())
                {
                    if (record.TryGetProperty("name", out var name) &&
                        name.GetString() == keyId &&
                        record.TryGetProperty("id", out var id))
                    {
                        return id.GetInt32();
                    }
                }
            }

            return null;
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                _accessToken = _config.ApiKey;
                return;
            }

            if (string.IsNullOrEmpty(_config.Username) || string.IsNullOrEmpty(_config.Password))
            {
                throw new InvalidOperationException("Either ApiKey or Username/Password must be provided for authentication.");
            }

            var authPayload = new
            {
                username = _config.Username,
                password = _config.Password,
                grant_type = "password"
            };

            var content = JsonSerializer.Serialize(authPayload);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ServerUrl}/oauth2/token");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            _accessToken = result.GetProperty("access_token").GetString();
        }

        private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            try
            {
                var request = CreateRequest(HttpMethod.Get, "/api/v1/version");
                var response = await _httpClient.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.ServerUrl}{path}");
            if (!string.IsNullOrEmpty(_accessToken))
            {
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            }
            return request;
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Delinea Secret Server key store strategy.
    /// </summary>
    public class DelineaConfig
    {
        public string ServerUrl { get; set; } = "https://secretserver.example.com";
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public int FolderId { get; set; } = -1;
        public int SecretTemplateId { get; set; } = 6000;
    }
}
