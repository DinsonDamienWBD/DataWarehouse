using DataWarehouse.SDK.Security;
using k8s;
using k8s.Models;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Container
{
    /// <summary>
    /// Bitnami Sealed Secrets-based KeyStore strategy for GitOps-friendly secret management.
    /// Implements IKeyStoreStrategy using kubeseal CLI and Sealed Secrets controller.
    ///
    /// Features:
    /// - Encrypt secrets using kubeseal CLI and controller's public certificate
    /// - Store encrypted SealedSecrets in cluster (safe to commit to Git)
    /// - Automatic certificate fetching from controller
    /// - Support for cluster-wide, namespace-wide, and strict scopes
    /// - Certificate validation and rotation detection
    ///
    /// Configuration:
    /// - Namespace: Kubernetes namespace (default: "default")
    /// - ControllerNamespace: Sealed Secrets controller namespace (default: "kube-system")
    /// - ControllerName: Controller deployment name (default: "sealed-secrets-controller")
    /// - KubesealPath: Path to kubeseal CLI (default: auto-detect)
    /// - CertificatePath: Path to controller's public certificate (optional)
    /// - Scope: Sealing scope - strict, namespace-wide, cluster-wide (default: "strict")
    ///
    /// Requirements:
    /// - Sealed Secrets controller installed in cluster
    /// - kubeseal CLI available in PATH or specified via KubesealPath
    /// - Kubernetes cluster access (in-cluster or via kubeconfig)
    /// </summary>
    public sealed class SealedSecretsStrategy : KeyStoreStrategyBase
    {
        private SealedSecretsConfig _config = new();
        private IKubernetes? _client;
        private string? _kubesealPath;
        private X509Certificate2? _controllerCertificate;
        private string? _currentKeyId;

        private const string SealedSecretApiVersion = "bitnami.com/v1alpha1";
        private const string SealedSecretKind = "SealedSecret";
        private const string KeyDataField = "key";
        private const string AnnotationPrefix = "datawarehouse.io/";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,  // Certificate-based expiration
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,   // Via K8s RBAC
            SupportsAuditLogging = true, // Via K8s audit logging
            MaxKeySizeBytes = 1024 * 1024,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "SealedSecrets",
                ["Platform"] = "Kubernetes",
                ["Encryption"] = "RSA-OAEP + AES-256-GCM",
                ["GitOpsCompatible"] = true,
                ["Controller"] = "bitnami/sealed-secrets"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sealedsecrets.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("sealedsecrets.init");
            LoadConfiguration();

            // Find kubeseal CLI
            _kubesealPath = FindKubesealPath();
            if (string.IsNullOrEmpty(_kubesealPath))
            {
                throw new InvalidOperationException(
                    "kubeseal CLI not found. Install it via: " +
                    "brew install kubeseal (macOS), " +
                    "choco install kubeseal (Windows), " +
                    "or download from https://github.com/bitnami-labs/sealed-secrets/releases");
            }

            // Initialize Kubernetes client
            _client = await CreateKubernetesClientAsync();

            // Fetch and verify controller certificate
            await FetchControllerCertificateAsync(cancellationToken);

            // Initialize current key ID
            await InitializeCurrentKeyIdAsync(cancellationToken);
        }

        private void LoadConfiguration()
        {
            if (Configuration.TryGetValue("Namespace", out var nsObj) && nsObj is string ns)
                _config.Namespace = ns;
            if (Configuration.TryGetValue("ControllerNamespace", out var ctrlNsObj) && ctrlNsObj is string ctrlNs)
                _config.ControllerNamespace = ctrlNs;
            if (Configuration.TryGetValue("ControllerName", out var ctrlNameObj) && ctrlNameObj is string ctrlName)
                _config.ControllerName = ctrlName;
            if (Configuration.TryGetValue("KubesealPath", out var kubesealObj) && kubesealObj is string kubeseal)
                _config.KubesealPath = kubeseal;
            if (Configuration.TryGetValue("CertificatePath", out var certObj) && certObj is string cert)
                _config.CertificatePath = cert;
            if (Configuration.TryGetValue("Scope", out var scopeObj) && scopeObj is string scope)
                _config.Scope = scope;
            if (Configuration.TryGetValue("KubeconfigPath", out var kubeconfigObj) && kubeconfigObj is string kubeconfig)
                _config.KubeconfigPath = kubeconfig;
        }

        private string? FindKubesealPath()
        {
            // Check configured path first
            if (!string.IsNullOrEmpty(_config.KubesealPath) && File.Exists(_config.KubesealPath))
            {
                return _config.KubesealPath;
            }

            // Check common locations
            var commonPaths = new[]
            {
                "kubeseal",
                "/usr/local/bin/kubeseal",
                "/usr/bin/kubeseal",
                "/opt/homebrew/bin/kubeseal",
                Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\scoop\shims\kubeseal.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramData%\chocolatey\bin\kubeseal.exe"),
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WinGet\Packages\kubeseal.exe")
            };

            foreach (var path in commonPaths)
            {
                try
                {
                    var fullPath = path.Contains(Path.DirectorySeparatorChar) ? path : FindInPath(path);
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return null;
        }

        private static string? FindInPath(string executable)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            var paths = pathEnv.Split(Path.PathSeparator);

            var extensions = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new[] { ".exe", ".cmd", ".bat", "" }
                : new[] { "" };

            foreach (var path in paths)
            {
                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(path, executable + ext);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }

            return null;
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

        private async Task FetchControllerCertificateAsync(CancellationToken cancellationToken)
        {
            // Try to load from file first
            if (!string.IsNullOrEmpty(_config.CertificatePath) && File.Exists(_config.CertificatePath))
            {
                var certPem = await File.ReadAllTextAsync(_config.CertificatePath, cancellationToken);
                _controllerCertificate = X509Certificate2.CreateFromPem(certPem);
                ValidateCertificate(_controllerCertificate);
                return;
            }

            // Fetch from controller using kubeseal
            var certPemContent = await FetchCertificateViaKubesealAsync(cancellationToken);
            _controllerCertificate = X509Certificate2.CreateFromPem(certPemContent);
            ValidateCertificate(_controllerCertificate);
        }

        private async Task<string> FetchCertificateViaKubesealAsync(CancellationToken cancellationToken)
        {
            var args = new StringBuilder();
            args.Append("--fetch-cert");
            args.Append($" --controller-namespace {_config.ControllerNamespace}");
            args.Append($" --controller-name {_config.ControllerName}");

            if (!string.IsNullOrEmpty(_config.KubeconfigPath))
            {
                args.Append($" --kubeconfig \"{_config.KubeconfigPath}\"");
            }

            var (exitCode, stdout, stderr) = await RunKubesealAsync(args.ToString(), null, cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to fetch Sealed Secrets certificate: {stderr}. " +
                    $"Ensure the sealed-secrets-controller is installed in namespace '{_config.ControllerNamespace}'.");
            }

            return stdout;
        }

        private void ValidateCertificate(X509Certificate2 certificate)
        {
            // Check expiration
            if (certificate.NotAfter < DateTime.UtcNow)
            {
                throw new InvalidOperationException(
                    $"Sealed Secrets certificate expired on {certificate.NotAfter}. " +
                    "Rotate the certificate using: kubeseal --rotate-keys");
            }

            // Warn if expiring soon
            var daysUntilExpiry = (certificate.NotAfter - DateTime.UtcNow).TotalDays;
            if (daysUntilExpiry < 30)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Sealed Secrets certificate expires in {daysUntilExpiry:F0} days. " +
                    "Consider rotating the certificate.");
            }
        }

        private async Task InitializeCurrentKeyIdAsync(CancellationToken cancellationToken)
        {
            try
            {
                // List SealedSecrets using custom resource
                var sealedSecrets = await ListSealedSecretsAsync(cancellationToken);

                var latestSecret = sealedSecrets
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
            if (_client == null || _controllerCertificate == null)
                return false;

            try
            {
                // Verify we can reach the controller
                await _client.CoreV1.ReadNamespacedServiceAsync(
                    _config.ControllerName,
                    _config.ControllerNamespace,
                    cancellationToken: cancellationToken);

                // Verify certificate is still valid
                return _controllerCertificate.NotAfter > DateTime.UtcNow;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // SealedSecrets decrypt to regular Secrets - read the Secret
            var secretName = GetSecretName(keyId);

            try
            {
                var secret = await _client!.CoreV1.ReadNamespacedSecretAsync(
                    secretName,
                    _config.Namespace);

                if (secret.Data == null || !secret.Data.TryGetValue(KeyDataField, out var keyData))
                {
                    throw new KeyNotFoundException($"Secret '{secretName}' does not contain key data.");
                }

                return keyData;
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found. The SealedSecret may not have been processed yet.", ex);
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var secretName = GetSecretName(keyId);

            // Create a regular Secret manifest
            var secretManifest = new V1Secret
            {
                ApiVersion = "v1",
                Kind = "Secret",
                Type = "Opaque",
                Metadata = new V1ObjectMeta
                {
                    Name = secretName,
                    NamespaceProperty = _config.Namespace,
                    Annotations = new Dictionary<string, string>
                    {
                        [$"{AnnotationPrefix}key-id"] = keyId,
                        [$"{AnnotationPrefix}created-at"] = DateTime.UtcNow.ToString("O"),
                        [$"{AnnotationPrefix}created-by"] = context.UserId
                    },
                    Labels = new Dictionary<string, string>
                    {
                        ["app"] = "datawarehouse",
                        ["type"] = "encryption-key",
                        ["managed-by"] = "sealed-secrets"
                    }
                },
                Data = new Dictionary<string, byte[]>
                {
                    [KeyDataField] = keyData
                }
            };

            // Serialize to JSON
            var secretJson = KubernetesJson.Serialize(secretManifest);

            // Seal the secret using kubeseal
            var sealedSecretJson = await SealSecretAsync(secretJson, CancellationToken.None);

            // Apply the SealedSecret to the cluster
            await ApplySealedSecretAsync(sealedSecretJson, CancellationToken.None);

            _currentKeyId = keyId;
        }

        private async Task<string> SealSecretAsync(string secretJson, CancellationToken cancellationToken)
        {
            var args = new StringBuilder();
            args.Append("--format json");
            args.Append($" --controller-namespace {_config.ControllerNamespace}");
            args.Append($" --controller-name {_config.ControllerName}");

            // Add scope
            switch (_config.Scope.ToLowerInvariant())
            {
                case "cluster-wide":
                    args.Append(" --scope cluster-wide");
                    break;
                case "namespace-wide":
                    args.Append(" --scope namespace-wide");
                    break;
                default:
                    args.Append(" --scope strict");
                    break;
            }

            if (!string.IsNullOrEmpty(_config.KubeconfigPath))
            {
                args.Append($" --kubeconfig \"{_config.KubeconfigPath}\"");
            }

            var (exitCode, stdout, stderr) = await RunKubesealAsync(args.ToString(), secretJson, cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Failed to seal secret: {stderr}");
            }

            return stdout;
        }

        private async Task ApplySealedSecretAsync(string sealedSecretJson, CancellationToken cancellationToken)
        {
            // Parse the SealedSecret
            var sealedSecret = JsonSerializer.Deserialize<JsonElement>(sealedSecretJson);

            // Use the Kubernetes client to create/update the SealedSecret custom resource
            var body = JsonSerializer.Deserialize<Dictionary<string, object>>(sealedSecretJson);

            try
            {
                // Try to create
                await _client!.CustomObjects.CreateNamespacedCustomObjectAsync(
                    body: body,
                    group: "bitnami.com",
                    version: "v1alpha1",
                    namespaceParameter: _config.Namespace,
                    plural: "sealedsecrets",
                    cancellationToken: cancellationToken);
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Already exists, update it
                var name = sealedSecret.GetProperty("metadata").GetProperty("name").GetString()
                    ?? throw new InvalidOperationException("SealedSecret name cannot be null");

                await _client!.CustomObjects.ReplaceNamespacedCustomObjectAsync(
                    body: body,
                    group: "bitnami.com",
                    version: "v1alpha1",
                    namespaceParameter: _config.Namespace,
                    plural: "sealedsecrets",
                    name: name,
                    cancellationToken: cancellationToken);
            }
        }

        private async Task<(int exitCode, string stdout, string stderr)> RunKubesealAsync(
            string arguments,
            string? stdin,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _kubesealPath,
                    Arguments = arguments,
                    RedirectStandardInput = stdin != null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (stdin != null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken);

            return (process.ExitCode, stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim());
        }

        private async Task<List<SealedSecretResource>> ListSealedSecretsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await _client!.CustomObjects.ListNamespacedCustomObjectAsync(
                    group: "bitnami.com",
                    version: "v1alpha1",
                    namespaceParameter: _config.Namespace,
                    plural: "sealedsecrets",
                    labelSelector: "app=datawarehouse,type=encryption-key",
                    cancellationToken: cancellationToken);

                var json = JsonSerializer.Serialize(result);
                var list = JsonSerializer.Deserialize<SealedSecretList>(json);

                return list?.Items ?? new List<SealedSecretResource>();
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // CRD not installed
                return new List<SealedSecretResource>();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var sealedSecrets = await ListSealedSecretsAsync(cancellationToken);

            var keyIds = sealedSecrets
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
                // Delete the SealedSecret
                await _client!.CustomObjects.DeleteNamespacedCustomObjectAsync(
                    group: "bitnami.com",
                    version: "v1alpha1",
                    namespaceParameter: _config.Namespace,
                    plural: "sealedsecrets",
                    name: secretName,
                    cancellationToken: cancellationToken);

                // The controller will delete the corresponding Secret
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already deleted
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var secretName = GetSecretName(keyId);

            try
            {
                // Get the decrypted Secret
                var secret = await _client!.CoreV1.ReadNamespacedSecretAsync(
                    secretName,
                    _config.Namespace,
                    cancellationToken: cancellationToken);

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
                        ["Scope"] = _config.Scope,
                        ["CertificateExpiry"] = _controllerCertificate?.NotAfter.ToString("O") ?? "Unknown"
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
            _controllerCertificate?.Dispose();
            base.Dispose();
        }

        #region SealedSecret CRD Types

        private class SealedSecretList
        {
            [JsonPropertyName("items")]
            public List<SealedSecretResource>? Items { get; set; }
        }

        private class SealedSecretResource
        {
            [JsonPropertyName("metadata")]
            public V1ObjectMeta? Metadata { get; set; }

            [JsonPropertyName("spec")]
            public SealedSecretSpec? Spec { get; set; }
        }

        private class SealedSecretSpec
        {
            [JsonPropertyName("encryptedData")]
            public Dictionary<string, string>? EncryptedData { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Configuration for Sealed Secrets-based key store strategy.
    /// </summary>
    public class SealedSecretsConfig
    {
        /// <summary>
        /// Kubernetes namespace for secrets.
        /// Default: "default"
        /// </summary>
        public string Namespace { get; set; } = "default";

        /// <summary>
        /// Namespace where sealed-secrets-controller is installed.
        /// Default: "kube-system"
        /// </summary>
        public string ControllerNamespace { get; set; } = "kube-system";

        /// <summary>
        /// Name of the sealed-secrets-controller service.
        /// Default: "sealed-secrets-controller"
        /// </summary>
        public string ControllerName { get; set; } = "sealed-secrets-controller";

        /// <summary>
        /// Path to kubeseal CLI. If not specified, searches PATH.
        /// </summary>
        public string? KubesealPath { get; set; }

        /// <summary>
        /// Path to controller's public certificate file.
        /// If not specified, fetches from controller.
        /// </summary>
        public string? CertificatePath { get; set; }

        /// <summary>
        /// Sealing scope:
        /// - "strict": Secret is bound to exact name and namespace
        /// - "namespace-wide": Secret is bound to namespace only
        /// - "cluster-wide": Secret can be used anywhere in cluster
        /// Default: "strict"
        /// </summary>
        public string Scope { get; set; } = "strict";

        /// <summary>
        /// Path to kubeconfig file (optional).
        /// </summary>
        public string? KubeconfigPath { get; set; }

        /// <summary>
        /// Prefix for secret names.
        /// Default: "datawarehouse-key-"
        /// </summary>
        public string SecretNamePrefix { get; set; } = "datawarehouse-key-";
    }
}
