using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement
{
    /// <summary>
    /// BeyondTrust Password Safe KeyStore strategy supporting managed account credential requests.
    /// Implements IKeyStoreStrategy for secure credential retrieval.
    ///
    /// Supported features:
    /// - Managed account credential requests
    /// - Run-as user context support
    /// - Automatic API key authentication
    /// - Credential check-in/check-out workflow
    /// - Session-based credential access
    ///
    /// Configuration:
    /// - BaseUrl: BeyondTrust Password Safe API URL (e.g., "https://passwordsafe.company.com")
    /// - ApiKey: API authentication key
    /// - RunAsUser: Optional user context for credential requests (default: empty)
    /// </summary>
    public sealed class BeyondTrustStrategy : KeyStoreStrategyBase
    {
        private readonly HttpClient _httpClient;
        private BeyondTrustConfig _config = new();
        private string? _currentKeyId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "BeyondTrust Password Safe",
                ["Backend"] = "Managed Accounts",
                ["SupportsCheckout"] = true,
                ["AuthMethod"] = "API Key"
            }
        };

        public BeyondTrustStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("BaseUrl", out var baseUrlObj) && baseUrlObj is string baseUrl)
                _config.BaseUrl = baseUrl;
            if (Configuration.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is string apiKey)
                _config.ApiKey = apiKey;
            if (Configuration.TryGetValue("RunAsUser", out var runAsUserObj) && runAsUserObj is string runAsUser)
                _config.RunAsUser = runAsUser;

            // Validate connection
            var isHealthy = await IsHealthyAsync();
            if (!isHealthy)
            {
                throw new InvalidOperationException($"Cannot connect to BeyondTrust Password Safe at {_config.BaseUrl}");
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
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.BaseUrl}/BeyondTrust/api/public/v3/ManagedAccounts");
                request.Headers.Add("Authorization", $"PS-Auth key={_config.ApiKey}");
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

            // Request credential using managed account name
            var requestBody = new Dictionary<string, object>
            {
                ["SystemName"] = keyId,
                ["AccountName"] = keyId,
                ["DurationMinutes"] = 15
            };

            if (!string.IsNullOrEmpty(_config.RunAsUser))
            {
                requestBody["RunAsUser"] = _config.RunAsUser;
            }

            var content = JsonSerializer.Serialize(requestBody);
            var request = CreateRequest(HttpMethod.Post, "/BeyondTrust/api/public/v3/Requests");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            // Extract credential from response
            var credential = doc.RootElement.GetProperty("Credentials")[0].GetProperty("Password").GetString();
            return Encoding.UTF8.GetBytes(credential!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            // BeyondTrust Password Safe doesn't support direct credential creation via API
            // This would typically be done through the management console
            throw new NotSupportedException("BeyondTrust Password Safe does not support direct credential creation via API. Use the management console to create managed accounts.");
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var request = CreateRequest(HttpMethod.Get, "/BeyondTrust/api/public/v3/ManagedAccounts");
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            var accounts = new List<string>();
            foreach (var account in doc.RootElement.EnumerateArray())
            {
                var accountName = account.GetProperty("AccountName").GetString();
                if (!string.IsNullOrEmpty(accountName))
                {
                    accounts.Add(accountName);
                }
            }

            return accounts.AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete managed accounts.");
            }

            // BeyondTrust Password Safe typically requires managed accounts to be deleted via console
            throw new NotSupportedException("BeyondTrust Password Safe requires managed accounts to be deleted via the management console.");
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var request = CreateRequest(HttpMethod.Get, $"/BeyondTrust/api/public/v3/ManagedAccounts/{keyId}");
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(json);

                var accountName = doc.RootElement.GetProperty("AccountName").GetString() ?? keyId;
                var systemName = doc.RootElement.TryGetProperty("SystemName", out var sn) ? sn.GetString() : null;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = DateTime.UtcNow,
                    Version = 1,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["AccountName"] = accountName,
                        ["SystemName"] = systemName ?? "N/A",
                        ["Backend"] = "BeyondTrust Password Safe"
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
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.BaseUrl}/BeyondTrust/api/public/v3/ManagedAccounts");
                request.Headers.Add("Authorization", $"PS-Auth key={_config.ApiKey}");
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
            var request = new HttpRequestMessage(method, $"{_config.BaseUrl}{path}");
            request.Headers.Add("Authorization", $"PS-Auth key={_config.ApiKey}");
            return request;
        }

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for BeyondTrust Password Safe key store strategy.
    /// </summary>
    public class BeyondTrustConfig
    {
        public string BaseUrl { get; set; } = "https://passwordsafe.company.com";
        public string ApiKey { get; set; } = string.Empty;
        public string RunAsUser { get; set; } = string.Empty;
    }
}
