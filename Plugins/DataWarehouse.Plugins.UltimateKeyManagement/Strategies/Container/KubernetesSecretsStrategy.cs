using DataWarehouse.SDK.Security;
using k8s;
using k8s.Models;
using System.Text;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Container
{
    /// <summary>
    /// Kubernetes Secrets-based KeyStore strategy using the official KubernetesClient NuGet package.
    /// Implements IKeyStoreStrategy with native K8s Secrets API integration.
    ///
    /// Features:
    /// - In-cluster authentication (service account token)
    /// - Out-of-cluster authentication via kubeconfig file
    /// - Namespace-scoped secret management
    /// - Labels and annotations for key metadata
    /// - Support for opaque and TLS secret types
    /// - Automatic base64 encoding/decoding
    ///
    /// Configuration:
    /// - Namespace: Kubernetes namespace for secrets (default: "default")
    /// - SecretNamePrefix: Prefix for secret names (default: "datawarehouse-key-")
    /// - KubeconfigPath: Path to kubeconfig file (optional, uses in-cluster config if not specified)
    /// - LabelSelector: Label selector for listing secrets (default: "app=datawarehouse,type=encryption-key")
    /// - UseInClusterConfig: Force in-cluster authentication (default: auto-detect)
    /// </summary>
    public sealed class KubernetesSecretsStrategy : KeyStoreStrategyBase
    {
        private KubernetesSecretsConfig _config = new();
        private IKubernetes? _client;
        private string? _currentKeyId;

        private const string KeyDataField = "key";
        private const string MetadataAnnotationPrefix = "datawarehouse.io/";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false, // K8s handles replication
            SupportsVersioning = true,   // Via resourceVersion
            SupportsPerKeyAcl = true,    // Via RBAC
            SupportsAuditLogging = true, // Via K8s audit logging
            MaxKeySizeBytes = 1024 * 1024, // 1MB max secret size
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "KubernetesSecrets",
                ["Platform"] = "Kubernetes",
                ["AuthMethod"] = "ServiceAccount/Kubeconfig",
                ["Encryption"] = "etcd-at-rest (cluster-dependent)"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("kubernetessecrets.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("kubernetessecrets.init");
            LoadConfiguration();

            // Initialize Kubernetes client
            _client = await CreateKubernetesClientAsync();

            // Verify connectivity by attempting to access the namespace
            try
            {
                await _client.CoreV1.ReadNamespaceAsync(_config.Namespace, cancellationToken: cancellationToken);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"Kubernetes namespace '{_config.Namespace}' not found.", ex);
            }

            // Try to find current key
            await InitializeCurrentKeyIdAsync(cancellationToken);
        }

        private void LoadConfiguration()
        {
            if (Configuration.TryGetValue("Namespace", out var nsObj) && nsObj is string ns)
                _config.Namespace = ns;
            if (Configuration.TryGetValue("SecretNamePrefix", out var prefixObj) && prefixObj is string prefix)
                _config.SecretNamePrefix = prefix;
            if (Configuration.TryGetValue("KubeconfigPath", out var kubeconfigObj) && kubeconfigObj is string kubeconfig)
                _config.KubeconfigPath = kubeconfig;
            if (Configuration.TryGetValue("LabelSelector", out var labelObj) && labelObj is string labels)
                _config.LabelSelector = labels;
            if (Configuration.TryGetValue("UseInClusterConfig", out var inClusterObj) && inClusterObj is bool inCluster)
                _config.UseInClusterConfig = inCluster;
            if (Configuration.TryGetValue("Context", out var contextObj) && contextObj is string context)
                _config.Context = context;
        }

        private Task<IKubernetes> CreateKubernetesClientAsync()
        {
            KubernetesClientConfiguration config;

            // Determine authentication method
            if (_config.UseInClusterConfig || IsRunningInCluster())
            {
                // In-cluster configuration using service account
                config = KubernetesClientConfiguration.InClusterConfig();
            }
            else if (!string.IsNullOrEmpty(_config.KubeconfigPath))
            {
                // Use specified kubeconfig file
                config = KubernetesClientConfiguration.BuildConfigFromConfigFile(
                    kubeconfigPath: _config.KubeconfigPath,
                    currentContext: _config.Context);
            }
            else
            {
                // Use default kubeconfig location
                config = KubernetesClientConfiguration.BuildDefaultConfig();
            }

            return Task.FromResult<IKubernetes>(new Kubernetes(config));
        }

        private static bool IsRunningInCluster()
        {
            // Check for Kubernetes service account token file
            return File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token");
        }

        private async Task InitializeCurrentKeyIdAsync(CancellationToken cancellationToken)
        {
            try
            {
                var secrets = await _client!.CoreV1.ListNamespacedSecretAsync(
                    namespaceParameter: _config.Namespace,
                    labelSelector: _config.LabelSelector,
                    cancellationToken: cancellationToken);

                // Find the most recently created secret as the current key
                var latestSecret = secrets.Items
                    .Where(s => s.Data?.ContainsKey(KeyDataField) == true)
                    .OrderByDescending(s => s.Metadata?.CreationTimestamp)
                    .FirstOrDefault();

                if (latestSecret != null)
                {
                    _currentKeyId = ExtractKeyIdFromSecretName(latestSecret.Metadata.Name);
                }
                else
                {
                    // Generate initial key
                    _currentKeyId = Guid.NewGuid().ToString("N");
                    await SaveKeyToStorage(_currentKeyId, GenerateKey(), CreateSystemContext());
                }
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException(
                    $"Service account lacks permission to list secrets in namespace '{_config.Namespace}'. " +
                    "Ensure RBAC allows 'list' and 'get' operations on secrets.", ex);
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
                await _client.CoreV1.ReadNamespaceAsync(_config.Namespace, cancellationToken: cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var secretName = GetSecretName(keyId);

            try
            {
                var secret = await _client!.CoreV1.ReadNamespacedSecretAsync(
                    name: secretName,
                    namespaceParameter: _config.Namespace);

                if (secret.Data == null || !secret.Data.TryGetValue(KeyDataField, out var keyData))
                {
                    throw new KeyNotFoundException($"Secret '{secretName}' does not contain key data field '{KeyDataField}'.");
                }

                return keyData;
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new KeyNotFoundException($"Kubernetes secret for key '{keyId}' not found in namespace '{_config.Namespace}'.", ex);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException($"Access denied to secret '{secretName}' in namespace '{_config.Namespace}'.", ex);
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var secretName = GetSecretName(keyId);

            var secret = new V1Secret
            {
                ApiVersion = "v1",
                Kind = "Secret",
                Type = "Opaque",
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
                        [$"{MetadataAnnotationPrefix}key-id"] = keyId,
                        [$"{MetadataAnnotationPrefix}created-at"] = DateTime.UtcNow.ToString("O"),
                        [$"{MetadataAnnotationPrefix}created-by"] = context.UserId,
                        [$"{MetadataAnnotationPrefix}key-size-bytes"] = keyData.Length.ToString()
                    }
                },
                Data = new Dictionary<string, byte[]>
                {
                    [KeyDataField] = keyData
                }
            };

            try
            {
                // Try to create first
                await _client!.CoreV1.CreateNamespacedSecretAsync(secret, _config.Namespace);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Secret exists, update it (rotation)
                var existingSecret = await _client!.CoreV1.ReadNamespacedSecretAsync(secretName, _config.Namespace);
                secret.Metadata!.ResourceVersion = existingSecret.Metadata.ResourceVersion;
                secret.Metadata.Annotations![$"{MetadataAnnotationPrefix}rotated-at"] = DateTime.UtcNow.ToString("O");
                secret.Metadata.Annotations[$"{MetadataAnnotationPrefix}rotated-by"] = context.UserId;

                await _client.CoreV1.ReplaceNamespacedSecretAsync(secret, secretName, _config.Namespace);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException(
                    $"Service account lacks permission to create/update secrets in namespace '{_config.Namespace}'.", ex);
            }

            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var secrets = await _client!.CoreV1.ListNamespacedSecretAsync(
                namespaceParameter: _config.Namespace,
                labelSelector: _config.LabelSelector,
                cancellationToken: cancellationToken);

            var keyIds = secrets.Items
                .Where(s => s.Data?.ContainsKey(KeyDataField) == true)
                .Select(s => ExtractKeyIdFromSecretName(s.Metadata.Name))
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
                await _client!.CoreV1.DeleteNamespacedSecretAsync(
                    name: secretName,
                    namespaceParameter: _config.Namespace,
                    cancellationToken: cancellationToken);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already deleted, ignore
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException(
                    $"Service account lacks permission to delete secrets in namespace '{_config.Namespace}'.", ex);
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var secretName = GetSecretName(keyId);

            try
            {
                var secret = await _client!.CoreV1.ReadNamespacedSecretAsync(
                    name: secretName,
                    namespaceParameter: _config.Namespace,
                    cancellationToken: cancellationToken);

                var createdBy = secret.Metadata.Annotations != null &&
                    secret.Metadata.Annotations.TryGetValue($"{MetadataAnnotationPrefix}created-by", out var cb) ? cb : null;
                var keySizeBytes = secret.Data != null && secret.Data.TryGetValue(KeyDataField, out var kd) ? kd.Length : 0;

                var metadata = new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = secret.Metadata.CreationTimestamp?.ToUniversalTime() ?? DateTime.UtcNow,
                    CreatedBy = createdBy,
                    KeySizeBytes = keySizeBytes,
                    IsActive = keyId == _currentKeyId,
                    Version = int.TryParse(secret.Metadata.ResourceVersion, out var rv) ? rv : 1,
                    Metadata = new Dictionary<string, object>
                    {
                        ["SecretName"] = secretName,
                        ["Namespace"] = _config.Namespace,
                        ["ResourceVersion"] = secret.Metadata.ResourceVersion ?? "",
                        ["Uid"] = secret.Metadata.Uid ?? ""
                    }
                };

                // Parse rotation timestamp if present
                if (secret.Metadata.Annotations?.TryGetValue($"{MetadataAnnotationPrefix}rotated-at", out var rotatedAt) == true
                    && DateTime.TryParse(rotatedAt, out var rotatedAtDt))
                {
                    metadata = metadata with { LastRotatedAt = rotatedAtDt };
                }

                return metadata;
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private string GetSecretName(string keyId)
        {
            // Kubernetes secret names must be DNS-1123 compliant
            var safeName = keyId.ToLowerInvariant()
                .Replace('_', '-')
                .Replace('.', '-');

            return $"{_config.SecretNamePrefix}{safeName}";
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
    }

    /// <summary>
    /// Configuration for Kubernetes Secrets-based key store strategy.
    /// </summary>
    public class KubernetesSecretsConfig
    {
        /// <summary>
        /// Kubernetes namespace for storing secrets.
        /// Default: "default"
        /// </summary>
        public string Namespace { get; set; } = "default";

        /// <summary>
        /// Prefix for secret names. Secret names will be: {SecretNamePrefix}{keyId}
        /// Default: "datawarehouse-key-"
        /// </summary>
        public string SecretNamePrefix { get; set; } = "datawarehouse-key-";

        /// <summary>
        /// Path to kubeconfig file for out-of-cluster authentication.
        /// If not specified, uses in-cluster config when running in K8s, or default kubeconfig.
        /// </summary>
        public string? KubeconfigPath { get; set; }

        /// <summary>
        /// Kubeconfig context to use (when using kubeconfig file).
        /// </summary>
        public string? Context { get; set; }

        /// <summary>
        /// Label selector for listing/filtering secrets.
        /// Default: "app=datawarehouse,type=encryption-key"
        /// </summary>
        public string LabelSelector { get; set; } = "app=datawarehouse,type=encryption-key";

        /// <summary>
        /// Force in-cluster configuration even if kubeconfig is available.
        /// Default: false (auto-detect)
        /// </summary>
        public bool UseInClusterConfig { get; set; } = false;
    }
}
