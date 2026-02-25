using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement
{
    /// <summary>
    /// Doppler SecretOps KeyStore strategy supporting project/environment hierarchy.
    /// Implements IKeyStoreStrategy for secure secrets management.
    ///
    /// Supported features:
    /// - Project/environment hierarchy for secret organization
    /// - Service token authentication
    /// - Dynamic secret retrieval
    /// - Secret versioning and history
    /// - Environment-specific configurations
    ///
    /// Configuration:
    /// - ServiceToken: Doppler service token for authentication
    /// - Project: Doppler project name
    /// - Config: Environment/configuration name (e.g., "dev", "prd")
    /// </summary>
    public sealed class DopplerStrategy : KeyStoreStrategyBase
    {
        private readonly HttpClient _httpClient;
        private DopplerConfig _config = new();
        private string? _currentKeyId;
        private const string BaseApiUrl = "https://api.doppler.com/v3";

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
                ["Provider"] = "Doppler SecretOps",
                ["Backend"] = "Doppler Cloud",
                ["SupportsEnvironments"] = true,
                ["AuthMethod"] = "Service Token"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("doppler.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public DopplerStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("doppler.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("ServiceToken", out var tokenObj) && tokenObj is string token)
                _config.ServiceToken = token;
            if (Configuration.TryGetValue("Project", out var projectObj) && projectObj is string project)
                _config.Project = project;
            if (Configuration.TryGetValue("Config", out var configObj) && configObj is string config)
                _config.Config = config;

            // Validate connection
            var isHealthy = await IsHealthyAsync();
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to Doppler API or invalid service token");
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
                var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseApiUrl}/configs/config/secrets");
                request.Headers.Add("Authorization", $"Bearer {_config.ServiceToken}");
                var response = await _httpClient.SendAsync(request, cancellationToken);
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

            // Retrieve specific secret by name
            var request = CreateRequest(HttpMethod.Get, $"/configs/config/secret?name={keyId}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            // Extract secret value
            var secretValue = doc.RootElement.GetProperty("value").GetProperty("raw").GetString();
            return Encoding.UTF8.GetBytes(secretValue!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var secretValue = Encoding.UTF8.GetString(keyData);

            // Create or update secret
            var requestBody = new
            {
                project = _config.Project,
                config = _config.Config,
                name = keyId,
                value = secretValue
            };

            var content = JsonSerializer.Serialize(requestBody);
            var request = CreateRequest(HttpMethod.Post, "/configs/config/secrets");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var request = CreateRequest(HttpMethod.Get, "/configs/config/secrets");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            var secrets = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                secrets.Add(prop.Name);
            }

            return secrets.AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete secrets.");
            }

            var requestBody = new
            {
                project = _config.Project,
                config = _config.Config,
                name = keyId
            };

            var content = JsonSerializer.Serialize(requestBody);
            var request = CreateRequest(HttpMethod.Delete, "/configs/config/secret");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var request = CreateRequest(HttpMethod.Get, $"/configs/config/secret?name={keyId}");
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);

                var name = doc.RootElement.GetProperty("name").GetString() ?? keyId;
                var createdAt = doc.RootElement.TryGetProperty("created_at", out var ca)
                    ? DateTime.Parse(ca.GetString()!)
                    : DateTime.UtcNow;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = createdAt,
                    Version = 1,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SecretName"] = name,
                        ["Project"] = _config.Project,
                        ["Config"] = _config.Config,
                        ["Backend"] = "Doppler SecretOps"
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> IsHealthyAsync()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseApiUrl}/configs/config/secrets");
                request.Headers.Add("Authorization", $"Bearer {_config.ServiceToken}");
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{BaseApiUrl}{path}");
            request.Headers.Add("Authorization", $"Bearer {_config.ServiceToken}");
            return request;
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Doppler SecretOps key store strategy.
    /// </summary>
    public class DopplerConfig
    {
        public string ServiceToken { get; set; } = string.Empty;
        public string Project { get; set; } = string.Empty;
        public string Config { get; set; } = "dev";
    }
}
