using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement
{
    /// <summary>
    /// CyberArk Conjur/PAM KeyStore strategy supporting secure secrets storage.
    /// Implements IKeyStoreStrategy for enterprise-grade secrets management.
    ///
    /// Supported features:
    /// - Policy-based access control
    /// - REST API authentication (username/API key or token)
    /// - Secure secrets storage in Conjur vault
    /// - Key rotation support
    /// - Audit logging
    ///
    /// Configuration:
    /// - ApplianceUrl: CyberArk appliance URL (e.g., "https://conjur.example.com")
    /// - Account: Conjur account name
    /// - Username: Authentication username (optional if using token)
    /// - ApiKey: API key for authentication (optional if using token)
    /// - Token: Bearer token for authentication (optional if using username/API key)
    /// - PolicyPath: Base path for secrets (default: "datawarehouse/keys")
    /// </summary>
    public sealed class CyberArkStrategy : KeyStoreStrategyBase
    {
        private readonly HttpClient _httpClient;
        private CyberArkConfig _config = new();
        private string? _currentKeyId;
        private string? _authToken;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "CyberArk",
                ["Backend"] = "Conjur/PAM",
                ["AuthMethod"] = "API Key or Token",
                ["PolicyBased"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("cyberark.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public CyberArkStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("cyberark.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("ApplianceUrl", out var urlObj) && urlObj is string url)
                _config.ApplianceUrl = url;
            if (Configuration.TryGetValue("Account", out var accountObj) && accountObj is string account)
                _config.Account = account;
            if (Configuration.TryGetValue("Username", out var usernameObj) && usernameObj is string username)
                _config.Username = username;
            if (Configuration.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is string apiKey)
                _config.ApiKey = apiKey;
            if (Configuration.TryGetValue("Token", out var tokenObj) && tokenObj is string token)
                _config.Token = token;
            if (Configuration.TryGetValue("PolicyPath", out var pathObj) && pathObj is string path)
                _config.PolicyPath = path;

            // Authenticate
            await AuthenticateAsync(cancellationToken);

            // Validate connection
            var isHealthy = await IsHealthyAsync(cancellationToken);
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to CyberArk at {_config.ApplianceUrl}");
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
            var secretPath = $"{_config.PolicyPath}/{keyId}";
            var request = CreateRequest(HttpMethod.Get, $"/secrets/{_config.Account}/variable/{Uri.EscapeDataString(secretPath)}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var keyBase64 = await response.Content.ReadAsStringAsync();
            return Convert.FromBase64String(keyBase64);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var keyBase64 = Convert.ToBase64String(keyData);
            var secretPath = $"{_config.PolicyPath}/{keyId}";

            var request = CreateRequest(HttpMethod.Post, $"/secrets/{_config.Account}/variable/{Uri.EscapeDataString(secretPath)}");
            request.Content = new StringContent(keyBase64, Encoding.UTF8, "application/octet-stream");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var request = CreateRequest(HttpMethod.Get, $"/resources/{_config.Account}/variable?search={Uri.EscapeDataString(_config.PolicyPath)}");
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return Array.Empty<string>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var resources = JsonSerializer.Deserialize<JsonElement>(json);

                var keys = new List<string>();
                if (resources.ValueKind == JsonValueKind.Array)
                {
                    foreach (var resource in resources.EnumerateArray())
                    {
                        if (resource.TryGetProperty("id", out var id))
                        {
                            var idStr = id.GetString() ?? "";
                            var prefix = $"{_config.Account}:variable:{_config.PolicyPath}/";
                            if (idStr.StartsWith(prefix))
                            {
                                keys.Add(idStr.Substring(prefix.Length));
                            }
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

            var secretPath = $"{_config.PolicyPath}/{keyId}";
            var resourceId = $"{_config.Account}:variable:{secretPath}";

            var request = CreateRequest(HttpMethod.Delete, $"/resources/{_config.Account}/{Uri.EscapeDataString(resourceId)}");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var secretPath = $"{_config.PolicyPath}/{keyId}";
                var resourceId = $"{_config.Account}:variable:{secretPath}";

                var request = CreateRequest(HttpMethod.Get, $"/resources/{_config.Account}/{Uri.EscapeDataString(resourceId)}");
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var resource = JsonSerializer.Deserialize<JsonElement>(json);

                var createdAt = resource.TryGetProperty("created_at", out var ct)
                    ? DateTime.Parse(ct.GetString()!)
                    : DateTime.UtcNow;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    Version = 1,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SecretPath"] = secretPath,
                        ["Backend"] = "CyberArk Conjur",
                        ["Account"] = _config.Account
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(_config.Token))
            {
                _authToken = _config.Token;
                return;
            }

            if (string.IsNullOrEmpty(_config.Username) || string.IsNullOrEmpty(_config.ApiKey))
            {
                throw new InvalidOperationException("Either Token or Username/ApiKey must be provided for authentication.");
            }

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_config.ApplianceUrl}/authn/{_config.Account}/{Uri.EscapeDataString(_config.Username)}/authenticate");
            request.Content = new StringContent(_config.ApiKey, Encoding.UTF8, "text/plain");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            _authToken = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApplianceUrl}/health");
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
            var request = new HttpRequestMessage(method, $"{_config.ApplianceUrl}{path}");
            if (!string.IsNullOrEmpty(_authToken))
            {
                request.Headers.Add("Authorization", $"Token token=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(_authToken))}\"");
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
    /// Configuration for CyberArk Conjur/PAM key store strategy.
    /// </summary>
    public class CyberArkConfig
    {
        public string ApplianceUrl { get; set; } = "https://conjur.example.com";
        public string Account { get; set; } = "default";
        public string Username { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string PolicyPath { get; set; } = "datawarehouse/keys";
    }
}
