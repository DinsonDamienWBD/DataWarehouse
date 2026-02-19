using DataWarehouse.SDK.Security;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.DevCiCd
{
    /// <summary>
    /// 1Password Connect KeyStore strategy using the 1Password Connect REST API.
    /// Implements IKeyStoreStrategy for enterprise secrets management via 1Password Connect Server.
    ///
    /// Features:
    /// - 1Password Connect Server API integration
    /// - Self-hosted secrets access for servers and CI/CD
    /// - Vault-based secret organization
    /// - Field-level encryption
    /// - Cross-platform support
    /// - Automatic token refresh
    ///
    /// Prerequisites:
    /// - 1Password Connect Server deployed (self-hosted or cloud)
    /// - 1Password Connect token with appropriate vault access
    /// - Vault with permissions for the token
    ///
    /// Configuration:
    /// - ConnectHost: 1Password Connect Server URL (e.g., "http://localhost:8080")
    /// - ConnectToken: 1Password Connect API token (or use ConnectTokenEnvVar)
    /// - ConnectTokenEnvVar: Environment variable for token (default: "OP_CONNECT_TOKEN")
    /// - VaultId: Vault ID to store secrets in
    /// - VaultName: Vault name (alternative to VaultId - will be resolved to ID)
    /// - ItemCategory: Category for created items (default: "API_CREDENTIAL")
    /// </summary>
    public sealed class OnePasswordConnectStrategy : KeyStoreStrategyBase
    {
        private readonly HttpClient _httpClient;
        private OnePasswordConnectConfig _config = new();
        private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();
        private readonly ConcurrentDictionary<string, string> _itemIdCache = new(); // keyId -> itemId
        private string? _currentKeyId;
        private string? _resolvedVaultId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = true,  // 1Password handles sync
            SupportsVersioning = true,   // 1Password tracks item history
            SupportsPerKeyAcl = true,    // Vault-based access control
            SupportsAuditLogging = true, // 1Password provides audit logs
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "OnePasswordConnect",
                ["Provider"] = "1Password",
                ["Platform"] = "Self-Hosted / Cloud",
                ["Encryption"] = "AES-256-GCM",
                ["Features"] = new[] { "E2EE", "VaultAccess", "ItemHistory", "ConnectAPI" },
                ["IdealFor"] = "Enterprise CI/CD, Self-hosted secrets, Infrastructure automation"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("onepasswordconnect.shutdown");
            _keyCache.Clear();
            _itemIdCache.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public OnePasswordConnectStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("onepasswordconnect.init");
            // Load configuration
            if (Configuration.TryGetValue("ConnectHost", out var hostObj) && hostObj is string host)
                _config.ConnectHost = host.TrimEnd('/');
            if (Configuration.TryGetValue("ConnectToken", out var tokenObj) && tokenObj is string token)
                _config.ConnectToken = token;
            if (Configuration.TryGetValue("ConnectTokenEnvVar", out var tokenEnvObj) && tokenEnvObj is string tokenEnv)
                _config.ConnectTokenEnvVar = tokenEnv;
            if (Configuration.TryGetValue("VaultId", out var vaultIdObj) && vaultIdObj is string vaultId)
                _config.VaultId = vaultId;
            if (Configuration.TryGetValue("VaultName", out var vaultNameObj) && vaultNameObj is string vaultName)
                _config.VaultName = vaultName;
            if (Configuration.TryGetValue("ItemCategory", out var categoryObj) && categoryObj is string category)
                _config.ItemCategory = category;

            // Get token from environment if not directly configured
            if (string.IsNullOrEmpty(_config.ConnectToken))
            {
                _config.ConnectToken = Environment.GetEnvironmentVariable(_config.ConnectTokenEnvVar);
            }

            if (string.IsNullOrEmpty(_config.ConnectToken))
            {
                throw new InvalidOperationException(
                    $"1Password Connect token not configured. Set '{_config.ConnectTokenEnvVar}' environment variable " +
                    "or provide 'ConnectToken' in configuration.");
            }

            // Set authorization header
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.ConnectToken);

            // Verify connection
            await VerifyConnectionAsync(cancellationToken);

            // Resolve vault ID from name if needed
            await ResolveVaultIdAsync(cancellationToken);

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
                var response = await _httpClient.GetAsync($"{_config.ConnectHost}/health", cancellationToken);
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

            // Find item by title
            var item = await GetItemByTitleAsync(keyId);
            if (item == null)
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in 1Password vault.");
            }

            // Get the key field value
            var keyField = item.Fields?.FirstOrDefault(f =>
                f.Id == "key" || f.Label == "key" || f.Purpose == "CONCEALED");

            if (keyField?.Value == null)
            {
                throw new InvalidOperationException($"Key field not found in item '{keyId}'.");
            }

            var keyBytes = Convert.FromBase64String(keyField.Value);
            _keyCache[keyId] = keyBytes;

            // Cache item ID for future operations
            if (!string.IsNullOrEmpty(item.Id))
            {
                _itemIdCache[keyId] = item.Id;
            }

            return keyBytes;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var keyBase64 = Convert.ToBase64String(keyData);

            // Check if item already exists
            var existingItem = await GetItemByTitleAsync(keyId);

            if (existingItem != null && !string.IsNullOrEmpty(existingItem.Id))
            {
                // Update existing item
                await UpdateItemAsync(existingItem.Id, keyId, keyBase64, context);
            }
            else
            {
                // Create new item
                var newItemId = await CreateItemAsync(keyId, keyBase64, context);
                _itemIdCache[keyId] = newItemId;
            }

            // Update cache
            _keyCache[keyId] = keyData;
            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var response = await _httpClient.GetAsync(
                $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var items = JsonSerializer.Deserialize<List<OpItem>>(json, _jsonOptions);

            return items?
                .Where(i => i.Category == _config.ItemCategory)
                .Select(i => i.Title)
                .Where(t => !string.IsNullOrEmpty(t))
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

            // Get item ID from cache or lookup
            if (!_itemIdCache.TryGetValue(keyId, out var itemId))
            {
                var item = await GetItemByTitleAsync(keyId, cancellationToken);
                if (item == null)
                    return; // Item doesn't exist

                itemId = item.Id;
            }

            if (string.IsNullOrEmpty(itemId))
                return;

            var response = await _httpClient.DeleteAsync(
                $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items/{itemId}",
                cancellationToken);

            response.EnsureSuccessStatusCode();

            _keyCache.TryRemove(keyId, out _);
            _itemIdCache.TryRemove(keyId, out _);
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var item = await GetItemByTitleAsync(keyId, cancellationToken);
            if (item == null)
                return null;

            return new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = item.CreatedAt ?? DateTime.UtcNow,
                LastRotatedAt = item.UpdatedAt,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["ItemId"] = item.Id ?? "",
                    ["VaultId"] = _resolvedVaultId ?? "",
                    ["Category"] = item.Category ?? "",
                    ["Backend"] = "1Password Connect",
                    ["Version"] = item.Version ?? 0
                }
            };
        }

        #region 1Password Connect API Operations

        private async Task VerifyConnectionAsync(CancellationToken cancellationToken)
        {
            var response = await _httpClient.GetAsync($"{_config.ConnectHost}/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Cannot connect to 1Password Connect Server at '{_config.ConnectHost}'. " +
                    $"Status: {response.StatusCode}");
            }
        }

        private async Task ResolveVaultIdAsync(CancellationToken cancellationToken)
        {
            // If VaultId is already set, use it
            if (!string.IsNullOrEmpty(_config.VaultId))
            {
                _resolvedVaultId = _config.VaultId;
                return;
            }

            // Resolve vault by name
            if (string.IsNullOrEmpty(_config.VaultName))
            {
                throw new InvalidOperationException("Either VaultId or VaultName must be configured.");
            }

            var response = await _httpClient.GetAsync($"{_config.ConnectHost}/v1/vaults", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var vaults = JsonSerializer.Deserialize<List<OpVault>>(json, _jsonOptions);

            var vault = vaults?.FirstOrDefault(v =>
                string.Equals(v.Name, _config.VaultName, StringComparison.OrdinalIgnoreCase));

            if (vault == null || string.IsNullOrEmpty(vault.Id))
            {
                throw new InvalidOperationException($"Vault '{_config.VaultName}' not found.");
            }

            _resolvedVaultId = vault.Id;
        }

        private async Task<OpItem?> GetItemByTitleAsync(string title, CancellationToken cancellationToken = default)
        {
            // 1Password Connect doesn't have a direct "get by title" - we list and filter
            var response = await _httpClient.GetAsync(
                $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items?filter=title eq \"{title}\"",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Fallback: list all and filter manually
                response = await _httpClient.GetAsync(
                    $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items",
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                    return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var items = JsonSerializer.Deserialize<List<OpItem>>(json, _jsonOptions);

            var itemSummary = items?.FirstOrDefault(i =>
                string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase));

            if (itemSummary == null || string.IsNullOrEmpty(itemSummary.Id))
                return null;

            // Get full item details
            response = await _httpClient.GetAsync(
                $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items/{itemSummary.Id}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return itemSummary;

            json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<OpItem>(json, _jsonOptions);
        }

        private async Task<string> CreateItemAsync(string keyId, string keyBase64, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            var item = new OpItem
            {
                Vault = new OpVaultRef { Id = _resolvedVaultId },
                Title = keyId,
                Category = _config.ItemCategory,
                Fields = new List<OpField>
                {
                    new OpField
                    {
                        Id = "key",
                        Type = "CONCEALED",
                        Purpose = "CONCEALED",
                        Label = "key",
                        Value = keyBase64
                    },
                    new OpField
                    {
                        Id = "createdBy",
                        Type = "STRING",
                        Label = "Created By",
                        Value = context.UserId
                    },
                    new OpField
                    {
                        Id = "createdAt",
                        Type = "STRING",
                        Label = "Created At",
                        Value = DateTime.UtcNow.ToString("O")
                    },
                    new OpField
                    {
                        Id = "algorithm",
                        Type = "STRING",
                        Label = "Algorithm",
                        Value = "AES-256"
                    }
                },
                Tags = new List<string> { "datawarehouse", "encryption-key" }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(item, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items",
                content,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var createdItem = JsonSerializer.Deserialize<OpItem>(json, _jsonOptions);

            return createdItem?.Id ?? throw new InvalidOperationException("Failed to get created item ID.");
        }

        private async Task UpdateItemAsync(string itemId, string keyId, string keyBase64, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            // Get existing item first
            var response = await _httpClient.GetAsync(
                $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items/{itemId}",
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var item = JsonSerializer.Deserialize<OpItem>(json, _jsonOptions);

            if (item == null)
            {
                throw new InvalidOperationException($"Failed to retrieve item '{itemId}'.");
            }

            // Update key field
            var keyField = item.Fields?.FirstOrDefault(f => f.Id == "key" || f.Label == "key");
            if (keyField != null)
            {
                keyField.Value = keyBase64;
            }
            else
            {
                item.Fields ??= new List<OpField>();
                item.Fields.Add(new OpField
                {
                    Id = "key",
                    Type = "CONCEALED",
                    Purpose = "CONCEALED",
                    Label = "key",
                    Value = keyBase64
                });
            }

            // Update metadata fields
            var updatedByField = item.Fields?.FirstOrDefault(f => f.Id == "lastUpdatedBy");
            if (updatedByField != null)
            {
                updatedByField.Value = context.UserId;
            }
            else
            {
                item.Fields?.Add(new OpField
                {
                    Id = "lastUpdatedBy",
                    Type = "STRING",
                    Label = "Last Updated By",
                    Value = context.UserId
                });
            }

            var updatedAtField = item.Fields?.FirstOrDefault(f => f.Id == "lastUpdatedAt");
            if (updatedAtField != null)
            {
                updatedAtField.Value = DateTime.UtcNow.ToString("O");
            }
            else
            {
                item.Fields?.Add(new OpField
                {
                    Id = "lastUpdatedAt",
                    Type = "STRING",
                    Label = "Last Updated At",
                    Value = DateTime.UtcNow.ToString("O")
                });
            }

            var content = new StringContent(
                JsonSerializer.Serialize(item, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            response = await _httpClient.PutAsync(
                $"{_config.ConnectHost}/v1/vaults/{_resolvedVaultId}/items/{itemId}",
                content,
                cancellationToken);

            response.EnsureSuccessStatusCode();
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        #endregion

        public override void Dispose()
        {
            _httpClient?.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for 1Password Connect key store strategy.
    /// </summary>
    public class OnePasswordConnectConfig
    {
        /// <summary>
        /// 1Password Connect Server URL.
        /// </summary>
        public string ConnectHost { get; set; } = "http://localhost:8080";

        /// <summary>
        /// 1Password Connect API token.
        /// </summary>
        public string? ConnectToken { get; set; }

        /// <summary>
        /// Environment variable containing the Connect token.
        /// </summary>
        public string ConnectTokenEnvVar { get; set; } = "OP_CONNECT_TOKEN";

        /// <summary>
        /// Vault ID to store secrets in.
        /// </summary>
        public string? VaultId { get; set; }

        /// <summary>
        /// Vault name (alternative to VaultId - will be resolved).
        /// </summary>
        public string? VaultName { get; set; }

        /// <summary>
        /// Category for created items.
        /// </summary>
        public string ItemCategory { get; set; } = "API_CREDENTIAL";
    }

    /// <summary>
    /// 1Password vault representation.
    /// </summary>
    internal class OpVault
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("attributeVersion")]
        public int? AttributeVersion { get; set; }

        [JsonPropertyName("contentVersion")]
        public int? ContentVersion { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// 1Password vault reference.
    /// </summary>
    internal class OpVaultRef
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    /// <summary>
    /// 1Password item representation.
    /// </summary>
    internal class OpItem
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("vault")]
        public OpVaultRef? Vault { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        [JsonPropertyName("version")]
        public int? Version { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        [JsonPropertyName("lastEditedBy")]
        public string? LastEditedBy { get; set; }

        [JsonPropertyName("fields")]
        public List<OpField>? Fields { get; set; }

        [JsonPropertyName("sections")]
        public List<OpSection>? Sections { get; set; }
    }

    /// <summary>
    /// 1Password item field.
    /// </summary>
    internal class OpField
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("purpose")]
        public string? Purpose { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("section")]
        public OpSectionRef? Section { get; set; }
    }

    /// <summary>
    /// 1Password item section.
    /// </summary>
    internal class OpSection
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }
    }

    /// <summary>
    /// 1Password section reference.
    /// </summary>
    internal class OpSectionRef
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    #endregion
}
