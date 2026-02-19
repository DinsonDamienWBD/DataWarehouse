using DataWarehouse.SDK.Security;
using k8s;
using k8s.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Container
{
    /// <summary>
    /// External Secrets Operator-based KeyStore strategy for multi-provider secret synchronization.
    /// Implements IKeyStoreStrategy using the External Secrets Operator CRDs.
    ///
    /// Features:
    /// - Synchronize secrets from external providers (AWS, Azure, GCP, Vault, etc.)
    /// - Create ExternalSecret resources that pull from SecretStores
    /// - Support for multiple secret store backends
    /// - Automatic refresh and rotation via ESO
    /// - Template-based secret transformation
    ///
    /// Configuration:
    /// - Namespace: Kubernetes namespace (default: "default")
    /// - SecretStoreName: Name of the SecretStore/ClusterSecretStore to use
    /// - SecretStoreKind: "SecretStore" or "ClusterSecretStore" (default: "ClusterSecretStore")
    /// - RefreshInterval: How often ESO syncs secrets (default: "1h")
    /// - Provider: Backend provider type - aws, azure, gcp, vault, etc.
    /// - RemoteKeyPrefix: Prefix for keys in the remote store
    ///
    /// Requirements:
    /// - External Secrets Operator installed in cluster
    /// - SecretStore or ClusterSecretStore configured for your provider
    /// - Kubernetes cluster access (in-cluster or via kubeconfig)
    /// </summary>
    public sealed class ExternalSecretsStrategy : KeyStoreStrategyBase
    {
        private ExternalSecretsConfig _config = new();
        private IKubernetes? _client;
        private string? _currentKeyId;

        private const string EsoApiGroup = "external-secrets.io";
        private const string EsoApiVersion = "v1beta1";
        private const string ExternalSecretKind = "ExternalSecret";
        private const string KeyDataField = "key";
        private const string AnnotationPrefix = "datawarehouse.io/";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = true,  // Backend may be HSM-backed
            SupportsExpiration = true,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 1024 * 1024,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "ExternalSecrets",
                ["Platform"] = "Kubernetes",
                ["Operator"] = "external-secrets.io",
                ["MultiProvider"] = true,
                ["SyncBased"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("externalsecrets.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("externalsecrets.init");
            LoadConfiguration();

            // Initialize Kubernetes client
            _client = await CreateKubernetesClientAsync();

            // Verify ESO is installed and SecretStore exists
            await VerifySecretStoreAsync(cancellationToken);

            // Initialize current key ID
            await InitializeCurrentKeyIdAsync(cancellationToken);
        }

        private void LoadConfiguration()
        {
            if (Configuration.TryGetValue("Namespace", out var nsObj) && nsObj is string ns)
                _config.Namespace = ns;
            if (Configuration.TryGetValue("SecretStoreName", out var storeObj) && storeObj is string store)
                _config.SecretStoreName = store;
            if (Configuration.TryGetValue("SecretStoreKind", out var kindObj) && kindObj is string kind)
                _config.SecretStoreKind = kind;
            if (Configuration.TryGetValue("RefreshInterval", out var refreshObj) && refreshObj is string refresh)
                _config.RefreshInterval = refresh;
            if (Configuration.TryGetValue("Provider", out var providerObj) && providerObj is string provider)
                _config.Provider = provider;
            if (Configuration.TryGetValue("RemoteKeyPrefix", out var prefixObj) && prefixObj is string prefix)
                _config.RemoteKeyPrefix = prefix;
            if (Configuration.TryGetValue("KubeconfigPath", out var kubeconfigObj) && kubeconfigObj is string kubeconfig)
                _config.KubeconfigPath = kubeconfig;
            if (Configuration.TryGetValue("SecretNamePrefix", out var secretPrefixObj) && secretPrefixObj is string secretPrefix)
                _config.SecretNamePrefix = secretPrefix;
            if (Configuration.TryGetValue("CreationPolicy", out var creationObj) && creationObj is string creation)
                _config.CreationPolicy = creation;
            if (Configuration.TryGetValue("DeletionPolicy", out var deletionObj) && deletionObj is string deletion)
                _config.DeletionPolicy = deletion;
        }

        private Task<IKubernetes> CreateKubernetesClientAsync()
        {
            KubernetesClientConfiguration config;

            if (IsRunningInCluster())
            {
                config = KubernetesClientConfiguration.InClusterConfig();
            }
            else if (!string.IsNullOrEmpty(_config.KubeconfigPath))
            {
                config = KubernetesClientConfiguration.BuildConfigFromConfigFile(_config.KubeconfigPath);
            }
            else
            {
                config = KubernetesClientConfiguration.BuildDefaultConfig();
            }

            return Task.FromResult<IKubernetes>(new Kubernetes(config));
        }

        private static bool IsRunningInCluster()
        {
            return File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token");
        }

        private async Task VerifySecretStoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                var plural = _config.SecretStoreKind.ToLowerInvariant() == "clustersecretstore"
                    ? "clustersecretstores"
                    : "secretstores";

                if (_config.SecretStoreKind.ToLowerInvariant() == "clustersecretstore")
                {
                    await _client!.CustomObjects.GetClusterCustomObjectAsync(
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        plural: plural,
                        name: _config.SecretStoreName,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _client!.CustomObjects.GetNamespacedCustomObjectAsync(
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        namespaceParameter: _config.Namespace,
                        plural: plural,
                        name: _config.SecretStoreName,
                        cancellationToken: cancellationToken);
                }
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"{_config.SecretStoreKind} '{_config.SecretStoreName}' not found. " +
                    "Ensure External Secrets Operator is installed and the SecretStore is configured.");
            }
        }

        private async Task InitializeCurrentKeyIdAsync(CancellationToken cancellationToken)
        {
            try
            {
                var externalSecrets = await ListExternalSecretsAsync(cancellationToken);

                var latestSecret = externalSecrets
                    .OrderByDescending(s => s.Metadata?.CreationTimestamp)
                    .FirstOrDefault();

                if (latestSecret != null)
                {
                    _currentKeyId = ExtractKeyIdFromSecretName(latestSecret.Metadata?.Name ?? "");
                }
                else
                {
                    _currentKeyId = Guid.NewGuid().ToString("N");
                    await SaveKeyToStorage(_currentKeyId, GenerateKey(), CreateSystemContext());
                }
            }
            catch
            {
                _currentKeyId = "default";
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            if (_client == null)
                return false;

            try
            {
                await VerifySecretStoreAsync(cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // ExternalSecrets sync to regular Secrets - read the Secret
            var secretName = GetSecretName(keyId);

            try
            {
                var secret = await _client!.CoreV1.ReadNamespacedSecretAsync(
                    secretName,
                    _config.Namespace);

                if (secret.Data == null || !secret.Data.TryGetValue(KeyDataField, out var keyData))
                {
                    throw new KeyNotFoundException(
                        $"Secret '{secretName}' does not contain key data. " +
                        "The ExternalSecret may not have synced yet.");
                }

                return keyData;
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new KeyNotFoundException(
                    $"Key '{keyId}' not found. The ExternalSecret may not have synced yet.", ex);
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var secretName = GetSecretName(keyId);
            var remoteKey = GetRemoteKey(keyId);

            // First, we need to push the secret to the remote store
            // This depends on the provider - for now, we create a PushSecret or use the provider's API
            // For simplicity, we'll create an ExternalSecret that references an existing remote key
            // The actual push would need to be done via the provider's SDK

            // Create ExternalSecret resource
            var externalSecret = new ExternalSecretResource
            {
                ApiVersion = $"{EsoApiGroup}/{EsoApiVersion}",
                Kind = ExternalSecretKind,
                Metadata = new V1ObjectMeta
                {
                    Name = secretName,
                    NamespaceProperty = _config.Namespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["app"] = "datawarehouse",
                        ["type"] = "encryption-key",
                        ["managed-by"] = "UltimateKeyManagement"
                    },
                    Annotations = new Dictionary<string, string>
                    {
                        [$"{AnnotationPrefix}key-id"] = keyId,
                        [$"{AnnotationPrefix}created-at"] = DateTime.UtcNow.ToString("O"),
                        [$"{AnnotationPrefix}created-by"] = context.UserId,
                        [$"{AnnotationPrefix}remote-key"] = remoteKey
                    }
                },
                Spec = new ExternalSecretSpec
                {
                    RefreshInterval = _config.RefreshInterval,
                    SecretStoreRef = new SecretStoreRef
                    {
                        Name = _config.SecretStoreName,
                        Kind = _config.SecretStoreKind
                    },
                    Target = new ExternalSecretTarget
                    {
                        Name = secretName,
                        CreationPolicy = _config.CreationPolicy,
                        DeletionPolicy = _config.DeletionPolicy
                    },
                    Data = new List<ExternalSecretData>
                    {
                        new ExternalSecretData
                        {
                            SecretKey = KeyDataField,
                            RemoteRef = new RemoteRef
                            {
                                Key = remoteKey,
                                Property = "value"
                            }
                        }
                    }
                }
            };

            // Also create a PushSecret to push the key to the remote store
            var pushSecret = CreatePushSecretResource(secretName, keyId, keyData, remoteKey, context);

            try
            {
                // First, create a temporary Secret with the key data for PushSecret to sync
                var tempSecret = new V1Secret
                {
                    ApiVersion = "v1",
                    Kind = "Secret",
                    Metadata = new V1ObjectMeta
                    {
                        Name = $"{secretName}-source",
                        NamespaceProperty = _config.Namespace,
                        Labels = new Dictionary<string, string>
                        {
                            ["app"] = "datawarehouse",
                            ["type"] = "encryption-key-source"
                        }
                    },
                    Data = new Dictionary<string, byte[]>
                    {
                        [KeyDataField] = keyData
                    }
                };

                try
                {
                    await _client!.CoreV1.CreateNamespacedSecretAsync(tempSecret, _config.Namespace);
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Update existing
                    var existing = await _client!.CoreV1.ReadNamespacedSecretAsync($"{secretName}-source", _config.Namespace);
                    tempSecret.Metadata!.ResourceVersion = existing.Metadata.ResourceVersion;
                    await _client.CoreV1.ReplaceNamespacedSecretAsync(tempSecret, $"{secretName}-source", _config.Namespace);
                }

                // Create PushSecret to sync to remote store
                var pushSecretBody = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(pushSecret));

                try
                {
                    await _client.CustomObjects.CreateNamespacedCustomObjectAsync(
                        body: pushSecretBody,
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        namespaceParameter: _config.Namespace,
                        plural: "pushsecrets");
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Update existing
                    await _client.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                        body: pushSecretBody,
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        namespaceParameter: _config.Namespace,
                        plural: "pushsecrets",
                        name: secretName);
                }

                // Create ExternalSecret to sync back (for reading)
                var externalSecretBody = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(externalSecret));

                try
                {
                    await _client.CustomObjects.CreateNamespacedCustomObjectAsync(
                        body: externalSecretBody,
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        namespaceParameter: _config.Namespace,
                        plural: "externalsecrets");
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Update existing
                    await _client.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                        body: externalSecretBody,
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        namespaceParameter: _config.Namespace,
                        plural: "externalsecrets",
                        name: secretName);
                }

                _currentKeyId = keyId;
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    "External Secrets Operator CRDs not found. Ensure ESO is installed.", ex);
            }
        }

        private PushSecretResource CreatePushSecretResource(string secretName, string keyId, byte[] keyData, string remoteKey, ISecurityContext context)
        {
            return new PushSecretResource
            {
                ApiVersion = $"{EsoApiGroup}/{EsoApiVersion}",
                Kind = "PushSecret",
                Metadata = new V1ObjectMeta
                {
                    Name = secretName,
                    NamespaceProperty = _config.Namespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["app"] = "datawarehouse",
                        ["type"] = "encryption-key-push"
                    }
                },
                Spec = new PushSecretSpec
                {
                    RefreshInterval = _config.RefreshInterval,
                    SecretStoreRefs = new List<PushSecretStoreRef>
                    {
                        new PushSecretStoreRef
                        {
                            Name = _config.SecretStoreName,
                            Kind = _config.SecretStoreKind
                        }
                    },
                    Selector = new PushSecretSelector
                    {
                        Secret = new SecretSelector
                        {
                            Name = $"{secretName}-source"
                        }
                    },
                    Data = new List<PushSecretData>
                    {
                        new PushSecretData
                        {
                            Match = new PushSecretMatch
                            {
                                SecretKey = KeyDataField,
                                RemoteRef = new PushRemoteRef
                                {
                                    RemoteKey = remoteKey,
                                    Property = "value"
                                }
                            }
                        }
                    }
                }
            };
        }

        private async Task<List<ExternalSecretResource>> ListExternalSecretsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await _client!.CustomObjects.ListNamespacedCustomObjectAsync(
                    group: EsoApiGroup,
                    version: EsoApiVersion,
                    namespaceParameter: _config.Namespace,
                    plural: "externalsecrets",
                    labelSelector: "app=datawarehouse,type=encryption-key",
                    cancellationToken: cancellationToken);

                var json = JsonSerializer.Serialize(result);
                var list = JsonSerializer.Deserialize<ExternalSecretList>(json);

                return list?.Items ?? new List<ExternalSecretResource>();
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new List<ExternalSecretResource>();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var externalSecrets = await ListExternalSecretsAsync(cancellationToken);

            var keyIds = externalSecrets
                .Where(s => s.Metadata?.Name?.StartsWith(_config.SecretNamePrefix) == true)
                .Select(s => ExtractKeyIdFromSecretName(s.Metadata!.Name!))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList()
                .AsReadOnly();

            return keyIds;
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var secretName = GetSecretName(keyId);

            try
            {
                // Delete PushSecret
                try
                {
                    await _client!.CustomObjects.DeleteNamespacedCustomObjectAsync(
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        namespaceParameter: _config.Namespace,
                        plural: "pushsecrets",
                        name: secretName,
                        cancellationToken: cancellationToken);
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already deleted
                }

                // Delete ExternalSecret
                try
                {
                    await _client!.CustomObjects.DeleteNamespacedCustomObjectAsync(
                        group: EsoApiGroup,
                        version: EsoApiVersion,
                        namespaceParameter: _config.Namespace,
                        plural: "externalsecrets",
                        name: secretName,
                        cancellationToken: cancellationToken);
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already deleted
                }

                // Delete source secret
                try
                {
                    await _client!.CoreV1.DeleteNamespacedSecretAsync(
                        $"{secretName}-source",
                        _config.Namespace,
                        cancellationToken: cancellationToken);
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already deleted
                }
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException(
                    "Insufficient permissions to delete ExternalSecret resources.", ex);
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var secretName = GetSecretName(keyId);

            try
            {
                // Get the synced Secret
                var secret = await _client!.CoreV1.ReadNamespacedSecretAsync(
                    secretName,
                    _config.Namespace,
                    cancellationToken: cancellationToken);

                // Get ExternalSecret status
                var esResult = await _client.CustomObjects.GetNamespacedCustomObjectAsync(
                    group: EsoApiGroup,
                    version: EsoApiVersion,
                    namespaceParameter: _config.Namespace,
                    plural: "externalsecrets",
                    name: secretName,
                    cancellationToken: cancellationToken);

                var esJson = JsonSerializer.Serialize(esResult);
                var externalSecret = JsonSerializer.Deserialize<ExternalSecretResource>(esJson);

                var createdBy = secret.Metadata.Annotations != null &&
                    secret.Metadata.Annotations.TryGetValue($"{AnnotationPrefix}created-by", out var cb) ? cb : null;
                var keySizeBytes = secret.Data != null && secret.Data.TryGetValue(KeyDataField, out var kd) ? kd.Length : 0;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = secret.Metadata.CreationTimestamp?.ToUniversalTime() ?? DateTime.UtcNow,
                    CreatedBy = createdBy,
                    KeySizeBytes = keySizeBytes,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SecretName"] = secretName,
                        ["Namespace"] = _config.Namespace,
                        ["SecretStore"] = _config.SecretStoreName,
                        ["SecretStoreKind"] = _config.SecretStoreKind,
                        ["Provider"] = _config.Provider,
                        ["RemoteKey"] = GetRemoteKey(keyId),
                        ["RefreshInterval"] = _config.RefreshInterval,
                        ["SyncStatus"] = externalSecret?.Status?.Conditions?.LastOrDefault()?.Status ?? "Unknown"
                    }
                };
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private string GetSecretName(string keyId)
        {
            var safeName = keyId.ToLowerInvariant()
                .Replace('_', '-')
                .Replace('.', '-');

            return $"{_config.SecretNamePrefix}{safeName}";
        }

        private string GetRemoteKey(string keyId)
        {
            return $"{_config.RemoteKeyPrefix}{keyId}";
        }

        private string ExtractKeyIdFromSecretName(string secretName)
        {
            if (secretName.StartsWith(_config.SecretNamePrefix))
            {
                return secretName.Substring(_config.SecretNamePrefix.Length);
            }
            return secretName;
        }

        public override void Dispose()
        {
            _client?.Dispose();
            base.Dispose();
        }

        #region ExternalSecret CRD Types

        private class ExternalSecretList
        {
            [JsonPropertyName("items")]
            public List<ExternalSecretResource>? Items { get; set; }
        }

        private class ExternalSecretResource
        {
            [JsonPropertyName("apiVersion")]
            public string? ApiVersion { get; set; }

            [JsonPropertyName("kind")]
            public string? Kind { get; set; }

            [JsonPropertyName("metadata")]
            public V1ObjectMeta? Metadata { get; set; }

            [JsonPropertyName("spec")]
            public ExternalSecretSpec? Spec { get; set; }

            [JsonPropertyName("status")]
            public ExternalSecretStatus? Status { get; set; }
        }

        private class ExternalSecretSpec
        {
            [JsonPropertyName("refreshInterval")]
            public string? RefreshInterval { get; set; }

            [JsonPropertyName("secretStoreRef")]
            public SecretStoreRef? SecretStoreRef { get; set; }

            [JsonPropertyName("target")]
            public ExternalSecretTarget? Target { get; set; }

            [JsonPropertyName("data")]
            public List<ExternalSecretData>? Data { get; set; }
        }

        private class SecretStoreRef
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("kind")]
            public string? Kind { get; set; }
        }

        private class ExternalSecretTarget
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("creationPolicy")]
            public string? CreationPolicy { get; set; }

            [JsonPropertyName("deletionPolicy")]
            public string? DeletionPolicy { get; set; }
        }

        private class ExternalSecretData
        {
            [JsonPropertyName("secretKey")]
            public string? SecretKey { get; set; }

            [JsonPropertyName("remoteRef")]
            public RemoteRef? RemoteRef { get; set; }
        }

        private class RemoteRef
        {
            [JsonPropertyName("key")]
            public string? Key { get; set; }

            [JsonPropertyName("property")]
            public string? Property { get; set; }
        }

        private class ExternalSecretStatus
        {
            [JsonPropertyName("conditions")]
            public List<ExternalSecretCondition>? Conditions { get; set; }
        }

        private class ExternalSecretCondition
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }
        }

        private class PushSecretResource
        {
            [JsonPropertyName("apiVersion")]
            public string? ApiVersion { get; set; }

            [JsonPropertyName("kind")]
            public string? Kind { get; set; }

            [JsonPropertyName("metadata")]
            public V1ObjectMeta? Metadata { get; set; }

            [JsonPropertyName("spec")]
            public PushSecretSpec? Spec { get; set; }
        }

        private class PushSecretSpec
        {
            [JsonPropertyName("refreshInterval")]
            public string? RefreshInterval { get; set; }

            [JsonPropertyName("secretStoreRefs")]
            public List<PushSecretStoreRef>? SecretStoreRefs { get; set; }

            [JsonPropertyName("selector")]
            public PushSecretSelector? Selector { get; set; }

            [JsonPropertyName("data")]
            public List<PushSecretData>? Data { get; set; }
        }

        private class PushSecretStoreRef
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("kind")]
            public string? Kind { get; set; }
        }

        private class PushSecretSelector
        {
            [JsonPropertyName("secret")]
            public SecretSelector? Secret { get; set; }
        }

        private class SecretSelector
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        private class PushSecretData
        {
            [JsonPropertyName("match")]
            public PushSecretMatch? Match { get; set; }
        }

        private class PushSecretMatch
        {
            [JsonPropertyName("secretKey")]
            public string? SecretKey { get; set; }

            [JsonPropertyName("remoteRef")]
            public PushRemoteRef? RemoteRef { get; set; }
        }

        private class PushRemoteRef
        {
            [JsonPropertyName("remoteKey")]
            public string? RemoteKey { get; set; }

            [JsonPropertyName("property")]
            public string? Property { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Configuration for External Secrets Operator-based key store strategy.
    /// </summary>
    public class ExternalSecretsConfig
    {
        /// <summary>
        /// Kubernetes namespace.
        /// Default: "default"
        /// </summary>
        public string Namespace { get; set; } = "default";

        /// <summary>
        /// Name of the SecretStore or ClusterSecretStore to use.
        /// Required.
        /// </summary>
        public string SecretStoreName { get; set; } = "default-secret-store";

        /// <summary>
        /// Kind of secret store: "SecretStore" or "ClusterSecretStore".
        /// Default: "ClusterSecretStore"
        /// </summary>
        public string SecretStoreKind { get; set; } = "ClusterSecretStore";

        /// <summary>
        /// How often ESO syncs secrets from the remote store.
        /// Default: "1h"
        /// </summary>
        public string RefreshInterval { get; set; } = "1h";

        /// <summary>
        /// Backend provider type: aws, azure, gcp, vault, etc.
        /// Used for metadata purposes.
        /// </summary>
        public string Provider { get; set; } = "unknown";

        /// <summary>
        /// Prefix for keys in the remote secret store.
        /// Default: "datawarehouse/keys/"
        /// </summary>
        public string RemoteKeyPrefix { get; set; } = "datawarehouse/keys/";

        /// <summary>
        /// Path to kubeconfig file (optional).
        /// </summary>
        public string? KubeconfigPath { get; set; }

        /// <summary>
        /// Prefix for Kubernetes secret names.
        /// Default: "datawarehouse-key-"
        /// </summary>
        public string SecretNamePrefix { get; set; } = "datawarehouse-key-";

        /// <summary>
        /// Secret creation policy: Owner, Orphan, Merge, None.
        /// Default: "Owner"
        /// </summary>
        public string CreationPolicy { get; set; } = "Owner";

        /// <summary>
        /// Secret deletion policy: Delete, Retain.
        /// Default: "Retain"
        /// </summary>
        public string DeletionPolicy { get; set; } = "Retain";
    }
}
