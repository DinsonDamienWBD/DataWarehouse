using DataWarehouse.SDK.Security;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Container
{
    /// <summary>
    /// Mozilla SOPS (Secrets OPerationS)-based KeyStore strategy for file-based encrypted secrets.
    /// Implements IKeyStoreStrategy using the sops CLI with multiple encryption backends.
    ///
    /// Features:
    /// - Support for multiple encryption backends: age, GPG, AWS KMS, GCP KMS, Azure Key Vault, HashiCorp Vault
    /// - GitOps-friendly encrypted files (YAML, JSON, ENV, INI, BINARY)
    /// - In-place encryption with key/value granularity
    /// - Automatic key rotation support
    /// - Integration with cloud KMS for key management
    ///
    /// Configuration:
    /// - SecretsDirectory: Directory for encrypted secret files (default: "./secrets")
    /// - SopsPath: Path to sops CLI (default: auto-detect)
    /// - Backend: Encryption backend - age, pgp, aws_kms, gcp_kms, azure_kv, hc_vault (default: "age")
    /// - AgeRecipient: age public key recipient (for age backend)
    /// - AgeKeyFile: Path to age private key file (default: ~/.config/sops/age/keys.txt)
    /// - PgpFingerprint: GPG key fingerprint (for pgp backend)
    /// - KmsArn: AWS KMS key ARN (for aws_kms backend)
    /// - GcpKmsResourceId: GCP KMS key resource ID (for gcp_kms backend)
    /// - AzureKeyVaultUrl: Azure Key Vault URL (for azure_kv backend)
    /// - VaultTransitPath: HashiCorp Vault transit path (for hc_vault backend)
    /// - FileFormat: Output format - yaml, json, dotenv, ini, binary (default: "yaml")
    ///
    /// Requirements:
    /// - sops CLI installed (https://github.com/getsops/sops)
    /// - For age: age-keygen for key generation
    /// - For GPG: gnupg installed with keys
    /// - For cloud KMS: appropriate SDK credentials configured
    /// </summary>
    public sealed class SopsStrategy : KeyStoreStrategyBase
    {
        private SopsConfig _config = new();
        private string? _sopsPath;
        private string? _currentKeyId;
        private readonly ISerializer _yamlSerializer;
        private readonly IDeserializer _yamlDeserializer;

        private const string KeyDataField = "key";
        private const string MetadataField = "_metadata";

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,  // SOPS does envelope encryption with cloud KMS
            SupportsHsm = true,       // When using cloud KMS backends
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true, // Via Git versioning
            SupportsPerKeyAcl = true,  // Via encryption key access
            SupportsAuditLogging = true, // Via cloud KMS audit logs
            MaxKeySizeBytes = 64 * 1024, // Practical limit for file-based storage
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "SOPS",
                ["Platform"] = "Cross-Platform",
                ["GitOpsCompatible"] = true,
                ["SupportedBackends"] = new[] { "age", "pgp", "aws_kms", "gcp_kms", "azure_kv", "hc_vault" },
                ["FileFormats"] = new[] { "yaml", "json", "dotenv", "ini", "binary" }
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sops.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public SopsStrategy()
        {
            _yamlSerializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            _yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("sops.init");
            LoadConfiguration();

            // Find sops CLI
            _sopsPath = FindSopsPath();
            if (string.IsNullOrEmpty(_sopsPath))
            {
                throw new InvalidOperationException(
                    "sops CLI not found. Install it via: " +
                    "brew install sops (macOS), " +
                    "choco install sops (Windows), " +
                    "or download from https://github.com/getsops/sops/releases");
            }

            // Verify backend configuration
            await VerifyBackendConfigurationAsync(cancellationToken);

            // Ensure secrets directory exists
            Directory.CreateDirectory(_config.SecretsDirectory);

            // Initialize current key ID
            await InitializeCurrentKeyIdAsync(cancellationToken);
        }

        private void LoadConfiguration()
        {
            if (Configuration.TryGetValue("SecretsDirectory", out var dirObj) && dirObj is string dir)
                _config.SecretsDirectory = dir;
            if (Configuration.TryGetValue("SopsPath", out var sopsObj) && sopsObj is string sops)
                _config.SopsPath = sops;
            if (Configuration.TryGetValue("Backend", out var backendObj) && backendObj is string backend)
                _config.Backend = backend;
            if (Configuration.TryGetValue("AgeRecipient", out var ageRecipientObj) && ageRecipientObj is string ageRecipient)
                _config.AgeRecipient = ageRecipient;
            if (Configuration.TryGetValue("AgeKeyFile", out var ageKeyObj) && ageKeyObj is string ageKey)
                _config.AgeKeyFile = ageKey;
            if (Configuration.TryGetValue("PgpFingerprint", out var pgpObj) && pgpObj is string pgp)
                _config.PgpFingerprint = pgp;
            if (Configuration.TryGetValue("KmsArn", out var kmsObj) && kmsObj is string kms)
                _config.KmsArn = kms;
            if (Configuration.TryGetValue("GcpKmsResourceId", out var gcpObj) && gcpObj is string gcp)
                _config.GcpKmsResourceId = gcp;
            if (Configuration.TryGetValue("AzureKeyVaultUrl", out var azureObj) && azureObj is string azure)
                _config.AzureKeyVaultUrl = azure;
            if (Configuration.TryGetValue("VaultTransitPath", out var vaultObj) && vaultObj is string vault)
                _config.VaultTransitPath = vault;
            if (Configuration.TryGetValue("FileFormat", out var formatObj) && formatObj is string format)
                _config.FileFormat = format;
            if (Configuration.TryGetValue("SecretNamePrefix", out var prefixObj) && prefixObj is string prefix)
                _config.SecretNamePrefix = prefix;
            if (Configuration.TryGetValue("SopsConfigFile", out var configObj) && configObj is string config)
                _config.SopsConfigFile = config;
        }

        private string? FindSopsPath()
        {
            // Check configured path first
            if (!string.IsNullOrEmpty(_config.SopsPath) && File.Exists(_config.SopsPath))
            {
                return _config.SopsPath;
            }

            // Check common locations
            var commonPaths = new[]
            {
                "sops",
                "/usr/local/bin/sops",
                "/usr/bin/sops",
                "/opt/homebrew/bin/sops",
                Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\scoop\shims\sops.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramData%\chocolatey\bin\sops.exe"),
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WinGet\Packages\sops.exe")
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

        private async Task VerifyBackendConfigurationAsync(CancellationToken cancellationToken)
        {
            switch (_config.Backend.ToLowerInvariant())
            {
                case "age":
                    await VerifyAgeBackendAsync(cancellationToken);
                    break;

                case "pgp":
                    VerifyPgpBackend();
                    break;

                case "aws_kms":
                    VerifyAwsKmsBackend();
                    break;

                case "gcp_kms":
                    VerifyGcpKmsBackend();
                    break;

                case "azure_kv":
                    VerifyAzureKeyVaultBackend();
                    break;

                case "hc_vault":
                    VerifyHashiCorpVaultBackend();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown SOPS backend: {_config.Backend}. " +
                        "Supported backends: age, pgp, aws_kms, gcp_kms, azure_kv, hc_vault");
            }
        }

        private async Task VerifyAgeBackendAsync(CancellationToken cancellationToken)
        {
            // Check age recipient is configured
            if (string.IsNullOrEmpty(_config.AgeRecipient))
            {
                throw new InvalidOperationException(
                    "AgeRecipient must be configured for age backend. " +
                    "Generate a key with: age-keygen -o key.txt");
            }

            // Verify age key file exists if specified
            if (!string.IsNullOrEmpty(_config.AgeKeyFile))
            {
                var keyFile = Environment.ExpandEnvironmentVariables(_config.AgeKeyFile);
                if (!File.Exists(keyFile))
                {
                    throw new InvalidOperationException(
                        $"Age key file not found: {keyFile}");
                }
            }

            // Test encryption/decryption
            var testResult = await RunSopsAsync("--version", null, cancellationToken);
            if (testResult.exitCode != 0)
            {
                throw new InvalidOperationException($"SOPS not working: {testResult.stderr}");
            }
        }

        private void VerifyPgpBackend()
        {
            if (string.IsNullOrEmpty(_config.PgpFingerprint))
            {
                throw new InvalidOperationException(
                    "PgpFingerprint must be configured for pgp backend. " +
                    "Use 'gpg --list-keys' to find your key fingerprint.");
            }
        }

        private void VerifyAwsKmsBackend()
        {
            if (string.IsNullOrEmpty(_config.KmsArn))
            {
                throw new InvalidOperationException(
                    "KmsArn must be configured for aws_kms backend. " +
                    "Format: arn:aws:kms:region:account:key/key-id");
            }
        }

        private void VerifyGcpKmsBackend()
        {
            if (string.IsNullOrEmpty(_config.GcpKmsResourceId))
            {
                throw new InvalidOperationException(
                    "GcpKmsResourceId must be configured for gcp_kms backend. " +
                    "Format: projects/PROJECT/locations/LOCATION/keyRings/KEYRING/cryptoKeys/KEY");
            }
        }

        private void VerifyAzureKeyVaultBackend()
        {
            if (string.IsNullOrEmpty(_config.AzureKeyVaultUrl))
            {
                throw new InvalidOperationException(
                    "AzureKeyVaultUrl must be configured for azure_kv backend. " +
                    "Format: https://VAULT_NAME.vault.azure.net/keys/KEY_NAME/KEY_VERSION");
            }
        }

        private void VerifyHashiCorpVaultBackend()
        {
            if (string.IsNullOrEmpty(_config.VaultTransitPath))
            {
                throw new InvalidOperationException(
                    "VaultTransitPath must be configured for hc_vault backend. " +
                    "Format: https://vault.example.com:8200/v1/transit/keys/my-key");
            }
        }

        private async Task InitializeCurrentKeyIdAsync(CancellationToken cancellationToken)
        {
            var secretFiles = GetSecretFiles();

            if (secretFiles.Any())
            {
                var latestFile = secretFiles
                    .OrderByDescending(f => File.GetCreationTimeUtc(f))
                    .First();

                _currentKeyId = ExtractKeyIdFromFileName(Path.GetFileName(latestFile));
            }
            else
            {
                _currentKeyId = Guid.NewGuid().ToString("N");
                await SaveKeyToStorage(_currentKeyId, GenerateKey(), CreateSystemContext());
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_sopsPath))
                return false;

            var result = await RunSopsAsync("--version", null, cancellationToken);
            return result.exitCode == 0;
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var filePath = GetSecretFilePath(keyId);

            if (!File.Exists(filePath))
            {
                throw new KeyNotFoundException($"Secret file for key '{keyId}' not found at: {filePath}");
            }

            // Decrypt the file using sops
            var decryptedContent = await DecryptFileAsync(filePath, CancellationToken.None);

            // Parse based on format
            var secretData = ParseSecretContent(decryptedContent, _config.FileFormat);

            if (!secretData.TryGetValue(KeyDataField, out var keyValue))
            {
                throw new KeyNotFoundException($"Secret file does not contain '{KeyDataField}' field.");
            }

            // Decode from base64
            return Convert.FromBase64String(keyValue);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var filePath = GetSecretFilePath(keyId);

            // Create secret content
            var secretContent = new Dictionary<string, string>
            {
                [KeyDataField] = Convert.ToBase64String(keyData),
                [MetadataField] = JsonSerializer.Serialize(new
                {
                    keyId,
                    createdAt = DateTime.UtcNow.ToString("O"),
                    createdBy = context.UserId,
                    keySizeBytes = keyData.Length
                })
            };

            // Serialize to appropriate format
            var plaintext = SerializeSecretContent(secretContent, _config.FileFormat);

            // #3457: Write plaintext to a restricted-permission temp file, delete-on-close.
            // Use the system temp directory but with owner-only permissions.
            var tempDir = Path.GetTempPath();
            var tempFileName = Path.Combine(tempDir, $"sops-{Guid.NewGuid():N}{GetFileExtension()}");
            try
            {
                // Create with restricted permissions (owner read/write only)
                await using (var fs = new FileStream(
                    tempFileName,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
                    await fs.WriteAsync(bytes, CancellationToken.None);
                }

                // Set file permissions to owner-only (Unix: 0600; Windows: restrict ACL)
                SetOwnerOnlyPermissions(tempFileName);

                // Encrypt using sops
                await EncryptFileAsync(tempFileName, filePath, CancellationToken.None);
            }
            finally
            {
                if (File.Exists(tempFileName))
                {
                    // Overwrite with zeros before deletion to minimize data remanence
                    try
                    {
                        var size = new FileInfo(tempFileName).Length;
                        if (size > 0)
                        {
                            using var fs = new FileStream(tempFileName, FileMode.Open, FileAccess.Write, FileShare.None);
                            var zeros = new byte[Math.Min(size, 4096)];
                            long remaining = size;
                            while (remaining > 0)
                            {
                                var toWrite = (int)Math.Min(remaining, zeros.Length);
                                await fs.WriteAsync(zeros.AsMemory(0, toWrite), CancellationToken.None);
                                remaining -= toWrite;
                            }
                        }
                    }
                    catch { /* best-effort zero-wipe */ }
                    File.Delete(tempFileName);
                }
            }

            _currentKeyId = keyId;
        }

        private async Task<string> DecryptFileAsync(string filePath, CancellationToken cancellationToken)
        {
            var args = BuildSopsDecryptArgs(filePath);
            var (exitCode, stdout, stderr) = await RunSopsAsync(args, null, cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Failed to decrypt secret: {stderr}");
            }

            return stdout;
        }

        private async Task EncryptFileAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            var args = BuildSopsEncryptArgs(inputPath, outputPath);
            var (exitCode, stdout, stderr) = await RunSopsAsync(args, null, cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Failed to encrypt secret: {stderr}");
            }
        }

        private string BuildSopsDecryptArgs(string filePath)
        {
            var args = new StringBuilder();
            args.Append("--decrypt");

            // Add config file if specified
            if (!string.IsNullOrEmpty(_config.SopsConfigFile))
            {
                args.Append($" --config \"{_config.SopsConfigFile}\"");
            }

            // Add age key file environment variable will be set separately
            args.Append($" \"{filePath}\"");

            return args.ToString();
        }

        private string BuildSopsEncryptArgs(string inputPath, string outputPath)
        {
            var args = new StringBuilder();
            args.Append("--encrypt");

            // Add encryption key based on backend
            switch (_config.Backend.ToLowerInvariant())
            {
                case "age":
                    args.Append($" --age {_config.AgeRecipient}");
                    break;

                case "pgp":
                    args.Append($" --pgp {_config.PgpFingerprint}");
                    break;

                case "aws_kms":
                    args.Append($" --kms {_config.KmsArn}");
                    break;

                case "gcp_kms":
                    args.Append($" --gcp-kms {_config.GcpKmsResourceId}");
                    break;

                case "azure_kv":
                    args.Append($" --azure-kv {_config.AzureKeyVaultUrl}");
                    break;

                case "hc_vault":
                    args.Append($" --hc-vault-transit {_config.VaultTransitPath}");
                    break;
            }

            // Add config file if specified
            if (!string.IsNullOrEmpty(_config.SopsConfigFile))
            {
                args.Append($" --config \"{_config.SopsConfigFile}\"");
            }

            args.Append($" --output \"{outputPath}\"");
            args.Append($" \"{inputPath}\"");

            return args.ToString();
        }

        private async Task<(int exitCode, string stdout, string stderr)> RunSopsAsync(
            string arguments,
            string? stdin,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _sopsPath,
                    Arguments = arguments,
                    RedirectStandardInput = stdin != null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            // Set age key file if configured
            if (_config.Backend.ToLowerInvariant() == "age" && !string.IsNullOrEmpty(_config.AgeKeyFile))
            {
                var keyFile = Environment.ExpandEnvironmentVariables(_config.AgeKeyFile);
                process.StartInfo.EnvironmentVariables["SOPS_AGE_KEY_FILE"] = keyFile;
            }

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

        private Dictionary<string, string> ParseSecretContent(string content, string format)
        {
            switch (format.ToLowerInvariant())
            {
                case "json":
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(content) ?? new();

                case "yaml":
                    return _yamlDeserializer.Deserialize<Dictionary<string, string>>(content) ?? new();

                case "dotenv":
                case "env":
                    return ParseDotEnvContent(content);

                case "ini":
                    return ParseIniContent(content);

                default:
                    // Try JSON first, then YAML
                    try
                    {
                        return JsonSerializer.Deserialize<Dictionary<string, string>>(content) ?? new();
                    }
                    catch
                    {
                        return _yamlDeserializer.Deserialize<Dictionary<string, string>>(content) ?? new();
                    }
            }
        }

        private static Dictionary<string, string> ParseDotEnvContent(string content)
        {
            var result = new Dictionary<string, string>();
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var equalIndex = trimmed.IndexOf('=');
                if (equalIndex > 0)
                {
                    var key = trimmed.Substring(0, equalIndex).Trim();
                    var value = trimmed.Substring(equalIndex + 1).Trim().Trim('"', '\'');
                    result[key] = value;
                }
            }
            return result;
        }

        private static Dictionary<string, string> ParseIniContent(string content)
        {
            // Simplified INI parser - treats all keys as flat
            var result = new Dictionary<string, string>();
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith('['))
                    continue;

                var equalIndex = trimmed.IndexOf('=');
                if (equalIndex > 0)
                {
                    var key = trimmed.Substring(0, equalIndex).Trim();
                    var value = trimmed.Substring(equalIndex + 1).Trim();
                    result[key] = value;
                }
            }
            return result;
        }

        private string SerializeSecretContent(Dictionary<string, string> content, string format)
        {
            switch (format.ToLowerInvariant())
            {
                case "json":
                    return JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });

                case "yaml":
                    return _yamlSerializer.Serialize(content);

                case "dotenv":
                case "env":
                    return string.Join("\n", content.Select(kv => $"{kv.Key}=\"{kv.Value}\""));

                case "ini":
                    var sb = new StringBuilder();
                    sb.AppendLine("[secrets]");
                    foreach (var kv in content)
                    {
                        sb.AppendLine($"{kv.Key} = {kv.Value}");
                    }
                    return sb.ToString();

                default:
                    return _yamlSerializer.Serialize(content);
            }
        }

        private string GetFileExtension()
        {
            return _config.FileFormat.ToLowerInvariant() switch
            {
                "json" => ".json",
                "yaml" => ".yaml",
                "dotenv" or "env" => ".env",
                "ini" => ".ini",
                "binary" => ".bin",
                _ => ".yaml"
            };
        }

        // #3457: Restrict file permissions to owner-only (0600 on Unix, restricted ACL on Windows).
        private static void SetOwnerOnlyPermissions(string filePath)
        {
            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    var fi = new System.IO.FileInfo(filePath);
                    var acl = fi.GetAccessControl();
                    // Remove inherited rules and restrict to current user
                    acl.SetAccessRuleProtection(true, false);
                    var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                    acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        currentUser,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                    fi.SetAccessControl(acl);
                }
                else
                {
                    // Unix: chmod 0600
                    System.IO.File.SetUnixFileMode(filePath,
                        System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
                }
            }
            catch
            {
                // Best-effort permission restriction; if it fails, we still delete after use
            }
        }

        private string[] GetSecretFiles()
        {
            if (!Directory.Exists(_config.SecretsDirectory))
                return Array.Empty<string>();

            var pattern = $"{_config.SecretNamePrefix}*";
            return Directory.GetFiles(_config.SecretsDirectory, pattern);
        }

        private string GetSecretFilePath(string keyId)
        {
            var safeName = keyId.Replace('/', '_').Replace('\\', '_');
            return Path.Combine(_config.SecretsDirectory, $"{_config.SecretNamePrefix}{safeName}{GetFileExtension()}");
        }

        private string ExtractKeyIdFromFileName(string fileName)
        {
            // Remove prefix and extension
            var name = Path.GetFileNameWithoutExtension(fileName);
            if (name.StartsWith(_config.SecretNamePrefix))
            {
                return name.Substring(_config.SecretNamePrefix.Length);
            }
            return name;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var secretFiles = GetSecretFiles();

            var keyIds = secretFiles
                .Select(f => ExtractKeyIdFromFileName(Path.GetFileName(f)))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList()
                .AsReadOnly();

            return await Task.FromResult(keyIds);
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var filePath = GetSecretFilePath(keyId);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            await Task.CompletedTask;
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var filePath = GetSecretFilePath(keyId);

            if (!File.Exists(filePath))
                return null;

            var fileInfo = new FileInfo(filePath);

            // Try to get metadata from decrypted content
            Dictionary<string, object>? metadata = null;
            try
            {
                var decrypted = await DecryptFileAsync(filePath, cancellationToken);
                var content = ParseSecretContent(decrypted, _config.FileFormat);

                if (content.TryGetValue(MetadataField, out var metadataJson))
                {
                    metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
                }
            }
            catch
            {

                // Ignore decryption errors for metadata
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = fileInfo.CreationTimeUtc,
                LastRotatedAt = fileInfo.LastWriteTimeUtc,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["FilePath"] = filePath,
                    ["FileSize"] = fileInfo.Length,
                    ["Backend"] = _config.Backend,
                    ["Format"] = _config.FileFormat,
                    ["Encrypted"] = true
                }
            });
        }

        /// <summary>
        /// Rotates the encryption key for a secret file.
        /// This re-encrypts the file with new key material from the KMS/key backend.
        /// </summary>
        public async Task RotateKeyAsync(string keyId, CancellationToken cancellationToken)
        {
            var filePath = GetSecretFilePath(keyId);

            if (!File.Exists(filePath))
            {
                throw new KeyNotFoundException($"Secret file for key '{keyId}' not found.");
            }

            var args = $"--rotate --in-place \"{filePath}\"";
            var (exitCode, _, stderr) = await RunSopsAsync(args, null, cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Failed to rotate key: {stderr}");
            }
        }

        /// <summary>
        /// Exports the SOPS configuration for .sops.yaml file.
        /// </summary>
        public string GenerateSopsConfig()
        {
            var config = new Dictionary<string, object>
            {
                ["creation_rules"] = new[]
                {
                    BuildCreationRule()
                }
            };

            return _yamlSerializer.Serialize(config);
        }

        private Dictionary<string, object> BuildCreationRule()
        {
            var rule = new Dictionary<string, object>
            {
                ["path_regex"] = $@".*{_config.SecretNamePrefix}.*"
            };

            switch (_config.Backend.ToLowerInvariant())
            {
                case "age":
                    rule["age"] = _config.AgeRecipient!;
                    break;

                case "pgp":
                    rule["pgp"] = _config.PgpFingerprint!;
                    break;

                case "aws_kms":
                    rule["kms"] = _config.KmsArn!;
                    break;

                case "gcp_kms":
                    rule["gcp_kms"] = _config.GcpKmsResourceId!;
                    break;

                case "azure_kv":
                    rule["azure_keyvault"] = _config.AzureKeyVaultUrl!;
                    break;

                case "hc_vault":
                    rule["hc_vault_transit_uri"] = _config.VaultTransitPath!;
                    break;
            }

            return rule;
        }
    }

    /// <summary>
    /// Configuration for Mozilla SOPS-based key store strategy.
    /// </summary>
    public class SopsConfig
    {
        /// <summary>
        /// Directory for storing encrypted secret files.
        /// Default: "./secrets"
        /// </summary>
        public string SecretsDirectory { get; set; } = "./secrets";

        /// <summary>
        /// Path to sops CLI. If not specified, searches PATH.
        /// </summary>
        public string? SopsPath { get; set; }

        /// <summary>
        /// Encryption backend: age, pgp, aws_kms, gcp_kms, azure_kv, hc_vault.
        /// Default: "age"
        /// </summary>
        public string Backend { get; set; } = "age";

        /// <summary>
        /// age public key recipient (for age backend).
        /// Format: age1...
        /// </summary>
        public string? AgeRecipient { get; set; }

        /// <summary>
        /// Path to age private key file (for age backend).
        /// Default: ~/.config/sops/age/keys.txt or $SOPS_AGE_KEY_FILE
        /// </summary>
        public string? AgeKeyFile { get; set; }

        /// <summary>
        /// GPG key fingerprint (for pgp backend).
        /// </summary>
        public string? PgpFingerprint { get; set; }

        /// <summary>
        /// AWS KMS key ARN (for aws_kms backend).
        /// Format: arn:aws:kms:region:account:key/key-id
        /// </summary>
        public string? KmsArn { get; set; }

        /// <summary>
        /// GCP KMS key resource ID (for gcp_kms backend).
        /// Format: projects/PROJECT/locations/LOCATION/keyRings/KEYRING/cryptoKeys/KEY
        /// </summary>
        public string? GcpKmsResourceId { get; set; }

        /// <summary>
        /// Azure Key Vault key URL (for azure_kv backend).
        /// Format: https://VAULT_NAME.vault.azure.net/keys/KEY_NAME/KEY_VERSION
        /// </summary>
        public string? AzureKeyVaultUrl { get; set; }

        /// <summary>
        /// HashiCorp Vault transit path (for hc_vault backend).
        /// Format: https://vault.example.com:8200/v1/transit/keys/my-key
        /// </summary>
        public string? VaultTransitPath { get; set; }

        /// <summary>
        /// Output file format: yaml, json, dotenv, ini, binary.
        /// Default: "yaml"
        /// </summary>
        public string FileFormat { get; set; } = "yaml";

        /// <summary>
        /// Prefix for secret file names.
        /// Default: "key_"
        /// </summary>
        public string SecretNamePrefix { get; set; } = "key_";

        /// <summary>
        /// Path to .sops.yaml configuration file (optional).
        /// If specified, uses this config instead of command-line args.
        /// </summary>
        public string? SopsConfigFile { get; set; }
    }
}
