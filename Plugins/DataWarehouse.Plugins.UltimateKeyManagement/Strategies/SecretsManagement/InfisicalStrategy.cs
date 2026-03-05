using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement
{
    /// <summary>
    /// Infisical KeyStore strategy supporting E2E encryption and workspace/environment hierarchy.
    /// Implements IKeyStoreStrategy for secure secrets management.
    ///
    /// Supported features:
    /// - End-to-end encryption
    /// - Workspace/environment hierarchy for secret organization
    /// - Universal Auth with ClientId/ClientSecret
    /// - Secret versioning and point-in-time recovery
    /// - Environment-specific configurations
    ///
    /// Configuration:
    /// - ApiUrl: Infisical API URL (e.g., "https://app.infisical.com")
    /// - ClientId: Universal Auth client ID
    /// - ClientSecret: Universal Auth client secret
    /// - WorkspaceId: Infisical workspace/project ID
    /// - Environment: Environment slug (e.g., "dev", "staging", "prod")
    /// </summary>
    public sealed class InfisicalStrategy : KeyStoreStrategyBase
    {
        private readonly HttpClient _httpClient;
        private InfisicalConfig _config = new();
        private string? _currentKeyId;
        private string? _accessToken;
        private DateTime _tokenExpiration;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 1,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Infisical",
                ["Backend"] = "Infisical Cloud",
                ["SupportsE2EE"] = true,
                ["SupportsEnvironments"] = true,
                ["AuthMethod"] = "Universal Auth"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("infisical.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public InfisicalStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("infisical.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("ApiUrl", out var apiUrlObj) && apiUrlObj is string apiUrl)
                _config.ApiUrl = apiUrl;
            if (Configuration.TryGetValue("ClientId", out var clientIdObj) && clientIdObj is string clientId)
                _config.ClientId = clientId;
            if (Configuration.TryGetValue("ClientSecret", out var clientSecretObj) && clientSecretObj is string clientSecret)
                _config.ClientSecret = clientSecret;
            if (Configuration.TryGetValue("WorkspaceId", out var workspaceIdObj) && workspaceIdObj is string workspaceId)
                _config.WorkspaceId = workspaceId;
            if (Configuration.TryGetValue("Environment", out var environmentObj) && environmentObj is string environment)
                _config.Environment = environment;

            // Authenticate and get access token
            await AuthenticateAsync(cancellationToken);

            // Validate connection
            var isHealthy = await IsHealthyAsync();
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to Infisical at {_config.ApiUrl}");
            }

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureAuthenticatedAsync(cancellationToken);

                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiUrl}/api/v3/workspaces/{_config.WorkspaceId}");
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(CancellationToken.None);

            // Retrieve specific secret by key
            var request = CreateAuthenticatedRequest(HttpMethod.Get,
                $"/api/v3/secrets/raw/{keyId}?workspaceId={_config.WorkspaceId}&environment={_config.Environment}");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Extract secret value
            var secretValue = doc.RootElement.GetProperty("secret").GetProperty("secretValue").GetString();
            return Encoding.UTF8.GetBytes(secretValue!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(CancellationToken.None);

            var secretValue = Encoding.UTF8.GetString(keyData);

            // Create or update secret
            var requestBody = new
            {
                workspaceId = _config.WorkspaceId,
                environment = _config.Environment,
                secretKey = keyId,
                secretValue = secretValue,
                type = "shared"
            };

            var content = JsonSerializer.Serialize(requestBody);
            var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/v3/secrets/raw");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(cancellationToken);

            var request = CreateAuthenticatedRequest(HttpMethod.Get,
                $"/api/v3/secrets/raw?workspaceId={_config.WorkspaceId}&environment={_config.Environment}");

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var secrets = new List<string>();
            foreach (var secret in doc.RootElement.GetProperty("secrets").EnumerateArray())
            {
                var secretKey = secret.GetProperty("secretKey").GetString();
                if (!string.IsNullOrEmpty(secretKey))
                {
                    secrets.Add(secretKey);
                }
            }

            return secrets.AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(cancellationToken);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete secrets.");
            }

            var requestBody = new
            {
                workspaceId = _config.WorkspaceId,
                environment = _config.Environment,
                secretKey = keyId
            };

            var content = JsonSerializer.Serialize(requestBody);
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, "/api/v3/secrets/raw");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(cancellationToken);

            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Get,
                    $"/api/v3/secrets/raw/{keyId}?workspaceId={_config.WorkspaceId}&environment={_config.Environment}");

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var secret = doc.RootElement.GetProperty("secret");

                var secretKey = secret.GetProperty("secretKey").GetString() ?? keyId;
                var createdAt = secret.TryGetProperty("createdAt", out var ca)
                    ? DateTime.Parse(ca.GetString()!)
                    : DateTime.UtcNow;

                var version = secret.TryGetProperty("version", out var v) ? v.GetInt32() : 1;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    Version = version,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SecretKey"] = secretKey,
                        ["WorkspaceId"] = _config.WorkspaceId,
                        ["Environment"] = _config.Environment,
                        ["Backend"] = "Infisical"
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
            var requestBody = new
            {
                clientId = _config.ClientId,
                clientSecret = _config.ClientSecret
            };

            var content = JsonSerializer.Serialize(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ApiUrl}/api/v1/auth/universal-auth/login");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            _accessToken = doc.RootElement.GetProperty("accessToken").GetString();
            var expiresIn = doc.RootElement.GetProperty("expiresIn").GetInt32();
            _tokenExpiration = DateTime.UtcNow.AddSeconds(expiresIn - 60); // Refresh 60 seconds before expiration
        }

        private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow >= _tokenExpiration)
            {
                await AuthenticateAsync(cancellationToken);
            }
        }

        private async Task<bool> IsHealthyAsync()
        {
            try
            {
                await EnsureAuthenticatedAsync(CancellationToken.None);

                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ApiUrl}/api/v3/workspaces/{_config.WorkspaceId}");
                request.Headers.Add("Authorization", $"Bearer {_accessToken}");
                using var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_config.ApiUrl}{path}");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
            return request;
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Infisical key store strategy.
    /// </summary>
    public class InfisicalConfig
    {
        public string ApiUrl { get; set; } = "https://app.infisical.com";
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string WorkspaceId { get; set; } = string.Empty;
        public string Environment { get; set; } = "dev";
    }
}
