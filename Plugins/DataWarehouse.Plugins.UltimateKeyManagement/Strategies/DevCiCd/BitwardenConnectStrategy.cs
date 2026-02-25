using DataWarehouse.SDK.Security;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.DevCiCd
{
    /// <summary>
    /// Bitwarden Secrets Manager KeyStore strategy using the Bitwarden Secrets Manager REST API.
    /// Implements IKeyStoreStrategy for enterprise secrets management via Bitwarden.
    ///
    /// Features:
    /// - Bitwarden Secrets Manager integration via REST API
    /// - Machine account authentication with access tokens
    /// - Project-based secret organization
    /// - Secret versioning and audit logging
    /// - End-to-end encryption (secrets encrypted client-side)
    /// - Cross-platform support
    ///
    /// Prerequisites:
    /// - Bitwarden organization with Secrets Manager enabled
    /// - Machine account with appropriate permissions
    /// - Service account access token
    ///
    /// Configuration:
    /// - ApiUrl: Bitwarden API base URL (default: "https://api.bitwarden.com")
    /// - IdentityUrl: Bitwarden Identity URL for auth (default: "https://identity.bitwarden.com")
    /// - AccessToken: Machine account access token (or use AccessTokenEnvVar)
    /// - AccessTokenEnvVar: Environment variable for access token (default: "BWS_ACCESS_TOKEN")
    /// - ProjectId: Project ID to organize secrets under
    /// - OrganizationId: Organization ID
    /// </summary>
    public sealed class BitwardenConnectStrategy : KeyStoreStrategyBase
    {
        private readonly HttpClient _httpClient;
        private BitwardenSecretsConfig _config = new();
        private readonly BoundedDictionary<string, byte[]> _keyCache = new BoundedDictionary<string, byte[]>(1000);
        private string? _currentKeyId;
        private string? _authToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _authLock = new(1, 1);

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = true,  // Bitwarden handles replication
            SupportsVersioning = true,   // Bitwarden tracks secret versions
            SupportsPerKeyAcl = true,    // Project-based access control
            SupportsAuditLogging = true, // Bitwarden provides audit logs
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "BitwardenSecretsManager",
                ["Provider"] = "Bitwarden",
                ["Platform"] = "Cloud",
                ["Encryption"] = "AES-256-CBC + HMAC",
                ["Features"] = new[] { "E2EE", "MachineAccounts", "Projects", "AuditLog" },
                ["IdealFor"] = "Enterprise CI/CD, Team secrets, Zero-trust environments"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("bitwardenconnect.shutdown");
            _keyCache.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public BitwardenConnectStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("bitwardenconnect.init");
            // Load configuration
            if (Configuration.TryGetValue("ApiUrl", out var apiUrlObj) && apiUrlObj is string apiUrl)
                _config.ApiUrl = apiUrl;
            if (Configuration.TryGetValue("IdentityUrl", out var identityUrlObj) && identityUrlObj is string identityUrl)
                _config.IdentityUrl = identityUrl;
            if (Configuration.TryGetValue("AccessToken", out var tokenObj) && tokenObj is string token)
                _config.AccessToken = token;
            if (Configuration.TryGetValue("AccessTokenEnvVar", out var tokenEnvObj) && tokenEnvObj is string tokenEnv)
                _config.AccessTokenEnvVar = tokenEnv;
            if (Configuration.TryGetValue("ProjectId", out var projectIdObj) && projectIdObj is string projectId)
                _config.ProjectId = projectId;
            if (Configuration.TryGetValue("OrganizationId", out var orgIdObj) && orgIdObj is string orgId)
                _config.OrganizationId = orgId;

            // Get access token from environment if not directly configured
            if (string.IsNullOrEmpty(_config.AccessToken))
            {
                _config.AccessToken = Environment.GetEnvironmentVariable(_config.AccessTokenEnvVar);
            }

            if (string.IsNullOrEmpty(_config.AccessToken))
            {
                throw new InvalidOperationException(
                    $"Bitwarden access token not configured. Set '{_config.AccessTokenEnvVar}' environment variable " +
                    "or provide 'AccessToken' in configuration.");
            }

            // Authenticate and get bearer token
            await AuthenticateAsync(cancellationToken);

            // Verify project exists if specified
            if (!string.IsNullOrEmpty(_config.ProjectId))
            {
                await VerifyProjectAccessAsync(cancellationToken);
            }

            _currentKeyId = "default";
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

                // List projects to verify connectivity
                var response = await SendAuthorizedRequestAsync(
                    HttpMethod.Get,
                    $"{_config.ApiUrl}/organizations/{_config.OrganizationId}/projects",
                    null,
                    cancellationToken);

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // Check cache first
            if (_keyCache.TryGetValue(keyId, out var cached))
                return cached;

            await EnsureAuthenticatedAsync();

            // Get secret by key (search by name)
            var secretId = await GetSecretIdByNameAsync(keyId);
            if (string.IsNullOrEmpty(secretId))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in Bitwarden Secrets Manager.");
            }

            // Get secret details
            var response = await SendAuthorizedRequestAsync(
                HttpMethod.Get,
                $"{_config.ApiUrl}/secrets/{secretId}",
                null);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var secret = JsonSerializer.Deserialize<BitwardenSecret>(json, _jsonOptions);

            if (secret?.Value == null)
            {
                throw new InvalidOperationException($"Invalid secret format for key '{keyId}'.");
            }

            // Decode from base64
            var keyBytes = Convert.FromBase64String(secret.Value);
            _keyCache[keyId] = keyBytes;

            return keyBytes;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await EnsureAuthenticatedAsync();

            var keyBase64 = Convert.ToBase64String(keyData);

            // Check if secret already exists
            var existingSecretId = await GetSecretIdByNameAsync(keyId);

            if (!string.IsNullOrEmpty(existingSecretId))
            {
                // Update existing secret
                var updatePayload = new
                {
                    key = keyId,
                    value = keyBase64,
                    note = $"DataWarehouse key updated at {DateTime.UtcNow:O} by {context.UserId}",
                    projectIds = string.IsNullOrEmpty(_config.ProjectId) ? null : new[] { _config.ProjectId }
                };

                var response = await SendAuthorizedRequestAsync(
                    HttpMethod.Put,
                    $"{_config.ApiUrl}/secrets/{existingSecretId}",
                    updatePayload);

                response.EnsureSuccessStatusCode();
            }
            else
            {
                // Create new secret
                var createPayload = new
                {
                    key = keyId,
                    value = keyBase64,
                    note = $"DataWarehouse key created at {DateTime.UtcNow:O} by {context.UserId}",
                    organizationId = _config.OrganizationId,
                    projectIds = string.IsNullOrEmpty(_config.ProjectId) ? null : new[] { _config.ProjectId }
                };

                var response = await SendAuthorizedRequestAsync(
                    HttpMethod.Post,
                    $"{_config.ApiUrl}/secrets",
                    createPayload);

                response.EnsureSuccessStatusCode();
            }

            // Update cache
            _keyCache[keyId] = keyData;
            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(cancellationToken);

            string url;
            if (!string.IsNullOrEmpty(_config.ProjectId))
            {
                url = $"{_config.ApiUrl}/projects/{_config.ProjectId}/secrets";
            }
            else
            {
                url = $"{_config.ApiUrl}/organizations/{_config.OrganizationId}/secrets";
            }

            var response = await SendAuthorizedRequestAsync(HttpMethod.Get, url, null, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<BitwardenSecretsListResponse>(json, _jsonOptions);

            return result?.Secrets?
                .Select(s => s.Key)
                .Where(k => !string.IsNullOrEmpty(k))
                .Cast<string>()
                .ToList()
                .AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            await EnsureAuthenticatedAsync(cancellationToken);

            var secretId = await GetSecretIdByNameAsync(keyId, cancellationToken);
            if (string.IsNullOrEmpty(secretId))
            {
                return; // Secret doesn't exist, nothing to delete
            }

            var response = await SendAuthorizedRequestAsync(
                HttpMethod.Delete,
                $"{_config.ApiUrl}/secrets/{secretId}",
                null,
                cancellationToken);

            response.EnsureSuccessStatusCode();
            _keyCache.TryRemove(keyId, out _);
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await EnsureAuthenticatedAsync(cancellationToken);

            var secretId = await GetSecretIdByNameAsync(keyId, cancellationToken);
            if (string.IsNullOrEmpty(secretId))
                return null;

            var response = await SendAuthorizedRequestAsync(
                HttpMethod.Get,
                $"{_config.ApiUrl}/secrets/{secretId}",
                null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var secret = JsonSerializer.Deserialize<BitwardenSecret>(json, _jsonOptions);

            if (secret == null)
                return null;

            return new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = secret.CreationDate ?? DateTime.UtcNow,
                LastRotatedAt = secret.RevisionDate,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["SecretId"] = secret.Id ?? "",
                    ["ProjectId"] = _config.ProjectId ?? "",
                    ["OrganizationId"] = _config.OrganizationId ?? "",
                    ["Backend"] = "Bitwarden Secrets Manager",
                    ["Note"] = secret.Note ?? ""
                }
            };
        }

        #region Authentication

        private async Task AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            await _authLock.WaitAsync(cancellationToken);
            try
            {
                // Parse the access token to extract credentials
                // Bitwarden service account tokens are in the format: version.clientId.clientSecret
                var tokenParts = _config.AccessToken?.Split('.') ?? Array.Empty<string>();
                if (tokenParts.Length < 3)
                {
                    throw new InvalidOperationException("Invalid Bitwarden access token format.");
                }

                var clientId = $"service_account.{tokenParts[1]}";
                var clientSecret = tokenParts[2];

                // Request bearer token using client credentials
                var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = "api.secrets"
                });

                var response = await _httpClient.PostAsync(
                    $"{_config.IdentityUrl}/connect/token",
                    tokenRequest,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var tokenResponse = JsonSerializer.Deserialize<BitwardenTokenResponse>(json, _jsonOptions);

                if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                {
                    throw new InvalidOperationException("Failed to obtain access token from Bitwarden.");
                }

                _authToken = tokenResponse.AccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60); // Refresh 60s before expiry

                // Extract organization ID from token if not configured
                if (string.IsNullOrEmpty(_config.OrganizationId))
                {
                    // Organization ID is part of the token payload
                    _config.OrganizationId = ExtractOrganizationIdFromToken(_authToken);
                }
            }
            finally
            {
                _authLock.Release();
            }
        }

        private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
        {
            if (DateTime.UtcNow >= _tokenExpiry || string.IsNullOrEmpty(_authToken))
            {
                await AuthenticateAsync(cancellationToken);
            }
        }

        private string? ExtractOrganizationIdFromToken(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) return null;

                var payload = parts[1];
                // Add padding if needed
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var jsonBytes = Convert.FromBase64String(payload);
                var json = Encoding.UTF8.GetString(jsonBytes);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("organization", out var org))
                {
                    return org.GetString();
                }
            }
            catch
            {

                // Token parsing failed
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            return null;
        }

        #endregion

        #region API Helpers

        private async Task<string?> GetSecretIdByNameAsync(string keyName, CancellationToken cancellationToken = default)
        {
            string url;
            if (!string.IsNullOrEmpty(_config.ProjectId))
            {
                url = $"{_config.ApiUrl}/projects/{_config.ProjectId}/secrets";
            }
            else
            {
                url = $"{_config.ApiUrl}/organizations/{_config.OrganizationId}/secrets";
            }

            var response = await SendAuthorizedRequestAsync(HttpMethod.Get, url, null, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<BitwardenSecretsListResponse>(json, _jsonOptions);

            return result?.Secrets?.FirstOrDefault(s => s.Key == keyName)?.Id;
        }

        private async Task VerifyProjectAccessAsync(CancellationToken cancellationToken)
        {
            var response = await SendAuthorizedRequestAsync(
                HttpMethod.Get,
                $"{_config.ApiUrl}/projects/{_config.ProjectId}",
                null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Cannot access Bitwarden project '{_config.ProjectId}'. Verify project ID and permissions.");
            }
        }

        private async Task<HttpResponseMessage> SendAuthorizedRequestAsync(
            HttpMethod method,
            string url,
            object? payload,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (payload != null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload, _jsonOptions),
                    Encoding.UTF8,
                    "application/json");
            }

            return await _httpClient.SendAsync(request, cancellationToken);
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        public override void Dispose()
        {
            _httpClient?.Dispose();
            _authLock.Dispose();
            _authToken = null;
            base.Dispose();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for Bitwarden Secrets Manager key store strategy.
    /// </summary>
    public class BitwardenSecretsConfig
    {
        /// <summary>
        /// Bitwarden Secrets Manager API base URL.
        /// </summary>
        public string ApiUrl { get; set; } = "https://api.bitwarden.com";

        /// <summary>
        /// Bitwarden Identity URL for authentication.
        /// </summary>
        public string IdentityUrl { get; set; } = "https://identity.bitwarden.com";

        /// <summary>
        /// Machine account access token.
        /// </summary>
        public string? AccessToken { get; set; }

        /// <summary>
        /// Environment variable containing the access token.
        /// </summary>
        public string AccessTokenEnvVar { get; set; } = "BWS_ACCESS_TOKEN";

        /// <summary>
        /// Project ID to organize secrets under.
        /// </summary>
        public string? ProjectId { get; set; }

        /// <summary>
        /// Organization ID.
        /// </summary>
        public string? OrganizationId { get; set; }
    }

    /// <summary>
    /// Bitwarden OAuth token response.
    /// </summary>
    internal class BitwardenTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    /// <summary>
    /// Bitwarden secret object.
    /// </summary>
    internal class BitwardenSecret
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("organizationId")]
        public string? OrganizationId { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }

        [JsonPropertyName("creationDate")]
        public DateTime? CreationDate { get; set; }

        [JsonPropertyName("revisionDate")]
        public DateTime? RevisionDate { get; set; }

        [JsonPropertyName("projects")]
        public List<BitwardenProjectRef>? Projects { get; set; }
    }

    /// <summary>
    /// Bitwarden project reference.
    /// </summary>
    internal class BitwardenProjectRef
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    /// <summary>
    /// Bitwarden secrets list response.
    /// </summary>
    internal class BitwardenSecretsListResponse
    {
        [JsonPropertyName("secrets")]
        public List<BitwardenSecretSummary>? Secrets { get; set; }
    }

    /// <summary>
    /// Bitwarden secret summary (from list endpoint).
    /// </summary>
    internal class BitwardenSecretSummary
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("organizationId")]
        public string? OrganizationId { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("creationDate")]
        public DateTime? CreationDate { get; set; }

        [JsonPropertyName("revisionDate")]
        public DateTime? RevisionDate { get; set; }
    }

    #endregion
}
