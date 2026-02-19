using DataWarehouse.SDK.Security;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.DevCiCd
{
    /// <summary>
    /// git-crypt based KeyStore strategy for encrypting secrets in Git repositories.
    /// Implements IKeyStoreStrategy using git-crypt CLI for transparent encryption of files in Git repos.
    ///
    /// Features:
    /// - Transparent encryption/decryption of files in Git repositories
    /// - GPG-based key sharing with multiple users
    /// - Symmetric key export/import for CI/CD pipelines
    /// - Integration with .gitattributes for file pattern matching
    /// - Support for multiple key contexts per repository
    ///
    /// Prerequisites:
    /// - git-crypt CLI must be installed and accessible in PATH
    /// - Git repository must be initialized
    /// - GPG for multi-user key sharing (optional)
    ///
    /// Configuration:
    /// - RepositoryPath: Path to the Git repository (default: current directory)
    /// - KeyStorePath: Path within repo for encrypted key files (default: ".secrets")
    /// - GitCryptPath: Custom path to git-crypt binary (default: "git-crypt")
    /// - SymmetricKeyEnvVar: Environment variable containing base64 symmetric key for CI/CD
    /// - AutoInit: Automatically initialize git-crypt if not initialized (default: false)
    /// </summary>
    public sealed class GitCryptStrategy : KeyStoreStrategyBase
    {
        private GitCryptConfig _config = new();
        private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();
        private string? _currentKeyId;
        private bool _isInitialized;
        private readonly SemaphoreSlim _processLock = new(1, 1);

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false,  // Git handles replication via push/pull
            SupportsVersioning = true,    // Git provides versioning
            SupportsPerKeyAcl = true,     // GPG user keys control access
            SupportsAuditLogging = true,  // Git commit history provides audit trail
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "GitCrypt",
                ["Backend"] = "git-crypt CLI",
                ["Platform"] = "Cross-Platform",
                ["EncryptionType"] = "AES-256-CTR",
                ["KeySharing"] = "GPG",
                ["IdealFor"] = "Git-based CI/CD, Team collaboration, Secret versioning"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("gitcrypt.shutdown");
            _keyCache.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("gitcrypt.init");
            // Load configuration
            if (Configuration.TryGetValue("RepositoryPath", out var repoPathObj) && repoPathObj is string repoPath)
                _config.RepositoryPath = repoPath;
            if (Configuration.TryGetValue("KeyStorePath", out var keyStorePathObj) && keyStorePathObj is string keyStorePath)
                _config.KeyStorePath = keyStorePath;
            if (Configuration.TryGetValue("GitCryptPath", out var gitCryptPathObj) && gitCryptPathObj is string gitCryptPath)
                _config.GitCryptPath = gitCryptPath;
            if (Configuration.TryGetValue("SymmetricKeyEnvVar", out var symKeyEnvObj) && symKeyEnvObj is string symKeyEnv)
                _config.SymmetricKeyEnvVar = symKeyEnv;
            if (Configuration.TryGetValue("AutoInit", out var autoInitObj) && autoInitObj is bool autoInit)
                _config.AutoInit = autoInit;

            // Verify git-crypt is available
            await VerifyGitCryptInstallationAsync(cancellationToken);

            // Verify repository exists and is a Git repo
            if (!Directory.Exists(Path.Combine(_config.RepositoryPath, ".git")))
            {
                throw new InvalidOperationException($"Repository path '{_config.RepositoryPath}' is not a valid Git repository.");
            }

            // Check if git-crypt is initialized in the repository
            _isInitialized = await IsGitCryptInitializedAsync(cancellationToken);

            if (!_isInitialized && _config.AutoInit)
            {
                await InitializeGitCryptAsync(cancellationToken);
                _isInitialized = true;
            }

            // Try to unlock using environment variable symmetric key (for CI/CD)
            var symmetricKey = Environment.GetEnvironmentVariable(_config.SymmetricKeyEnvVar);
            if (!string.IsNullOrEmpty(symmetricKey) && _isInitialized)
            {
                await UnlockWithSymmetricKeyAsync(symmetricKey, cancellationToken);
            }

            // Ensure key store directory exists
            var fullKeyStorePath = Path.Combine(_config.RepositoryPath, _config.KeyStorePath);
            Directory.CreateDirectory(fullKeyStorePath);

            // Configure .gitattributes for the key store path
            await EnsureGitAttributesAsync(cancellationToken);

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
                // Check git-crypt is available
                var result = await ExecuteGitCryptAsync("version", cancellationToken);
                if (result.ExitCode != 0)
                    return false;

                // Check if repository is unlocked
                return await IsRepositoryUnlockedAsync(cancellationToken);
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

            var keyFilePath = GetKeyFilePath(keyId);

            if (!File.Exists(keyFilePath))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in git-crypt repository at '{keyFilePath}'.");
            }

            // git-crypt transparently decrypts when reading (if unlocked)
            var isUnlocked = await IsRepositoryUnlockedAsync();
            if (!isUnlocked)
            {
                throw new InvalidOperationException("Repository is locked. Unlock with git-crypt or provide symmetric key in environment variable.");
            }

            var content = await File.ReadAllTextAsync(keyFilePath);
            var keyData = JsonSerializer.Deserialize<GitCryptKeyData>(content);

            if (keyData?.Key == null)
            {
                throw new InvalidOperationException($"Invalid key format for key '{keyId}'.");
            }

            var keyBytes = Convert.FromBase64String(keyData.Key);
            _keyCache[keyId] = keyBytes;

            return keyBytes;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var keyFilePath = GetKeyFilePath(keyId);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(keyFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var keyDataObj = new GitCryptKeyData
            {
                Key = Convert.ToBase64String(keyData),
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                Algorithm = "AES-256"
            };

            var json = JsonSerializer.Serialize(keyDataObj, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(keyFilePath, json);

            // Update cache
            _keyCache[keyId] = keyData;
            _currentKeyId = keyId;

            // Stage the file for commit (but don't commit - leave that to the user)
            await ExecuteGitAsync($"add \"{keyFilePath}\"");
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var fullKeyStorePath = Path.Combine(_config.RepositoryPath, _config.KeyStorePath);
            if (!Directory.Exists(fullKeyStorePath))
                return Array.Empty<string>();

            var keyFiles = Directory.GetFiles(fullKeyStorePath, "*.key", SearchOption.AllDirectories);
            var keyIds = keyFiles
                .Select(f => Path.GetFileNameWithoutExtension(f))
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

            var keyFilePath = GetKeyFilePath(keyId);
            if (File.Exists(keyFilePath))
            {
                File.Delete(keyFilePath);
                _keyCache.TryRemove(keyId, out _);

                // Stage the deletion
                await ExecuteGitAsync($"rm --cached \"{keyFilePath}\"", cancellationToken);
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyFilePath = GetKeyFilePath(keyId);
            if (!File.Exists(keyFilePath))
                return null;

            try
            {
                var content = await File.ReadAllTextAsync(keyFilePath, cancellationToken);
                var keyData = JsonSerializer.Deserialize<GitCryptKeyData>(content);

                // Get git log for the file
                var logResult = await ExecuteGitAsync($"log -1 --format=\"%H|%ai|%an\" -- \"{keyFilePath}\"", cancellationToken);
                var logParts = logResult.Output?.Split('|') ?? Array.Empty<string>();

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = keyData?.CreatedAt ?? DateTime.UtcNow,
                    CreatedBy = keyData?.CreatedBy,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["FilePath"] = keyFilePath,
                        ["Algorithm"] = keyData?.Algorithm ?? "AES-256",
                        ["GitCommit"] = logParts.Length > 0 ? logParts[0] : "unknown",
                        ["Backend"] = "git-crypt"
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        #region git-crypt Operations

        private async Task VerifyGitCryptInstallationAsync(CancellationToken cancellationToken)
        {
            var result = await ExecuteGitCryptAsync("version", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git-crypt is not installed or not accessible at '{_config.GitCryptPath}'. " +
                    "Please install git-crypt: https://github.com/AGWA/git-crypt");
            }
        }

        private async Task<bool> IsGitCryptInitializedAsync(CancellationToken cancellationToken = default)
        {
            var gitCryptDir = Path.Combine(_config.RepositoryPath, ".git-crypt");
            if (!Directory.Exists(gitCryptDir))
                return false;

            // Check for key file
            var keyFile = Path.Combine(gitCryptDir, "keys", "default");
            return await Task.FromResult(Directory.Exists(keyFile));
        }

        private async Task<bool> IsRepositoryUnlockedAsync(CancellationToken cancellationToken = default)
        {
            // Check if .git-crypt/keys/default/0 exists and is accessible
            var result = await ExecuteGitCryptAsync("status", cancellationToken);

            // If status returns successfully and doesn't show "encrypted" for our key files,
            // the repository is unlocked
            return result.ExitCode == 0 && !result.Output?.Contains("encrypted:") == true;
        }

        private async Task InitializeGitCryptAsync(CancellationToken cancellationToken)
        {
            var result = await ExecuteGitCryptAsync("init", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to initialize git-crypt: {result.Error}");
            }
        }

        private async Task UnlockWithSymmetricKeyAsync(string base64Key, CancellationToken cancellationToken)
        {
            // Write symmetric key to temp file
            var tempKeyFile = Path.GetTempFileName();
            try
            {
                var keyBytes = Convert.FromBase64String(base64Key);
                await File.WriteAllBytesAsync(tempKeyFile, keyBytes, cancellationToken);

                var result = await ExecuteGitCryptAsync($"unlock \"{tempKeyFile}\"", cancellationToken);
                if (result.ExitCode != 0)
                {
                    // May already be unlocked or using GPG key
                    System.Diagnostics.Trace.TraceWarning($"git-crypt unlock warning: {result.Error}");
                }
            }
            finally
            {
                // Securely delete temp key file
                if (File.Exists(tempKeyFile))
                {
                    var zeros = new byte[new FileInfo(tempKeyFile).Length];
                    await File.WriteAllBytesAsync(tempKeyFile, zeros, cancellationToken);
                    File.Delete(tempKeyFile);
                }
            }
        }

        /// <summary>
        /// Exports the symmetric key for use in CI/CD environments.
        /// </summary>
        public async Task<string> ExportSymmetricKeyAsync(CancellationToken cancellationToken = default)
        {
            var tempKeyFile = Path.GetTempFileName();
            try
            {
                var result = await ExecuteGitCryptAsync($"export-key \"{tempKeyFile}\"", cancellationToken);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to export git-crypt key: {result.Error}");
                }

                var keyBytes = await File.ReadAllBytesAsync(tempKeyFile, cancellationToken);
                return Convert.ToBase64String(keyBytes);
            }
            finally
            {
                if (File.Exists(tempKeyFile))
                {
                    var zeros = new byte[new FileInfo(tempKeyFile).Length];
                    await File.WriteAllBytesAsync(tempKeyFile, zeros, cancellationToken);
                    File.Delete(tempKeyFile);
                }
            }
        }

        /// <summary>
        /// Adds a GPG user to the repository's git-crypt keyring.
        /// </summary>
        public async Task AddGpgUserAsync(string gpgUserId, CancellationToken cancellationToken = default)
        {
            var result = await ExecuteGitCryptAsync($"add-gpg-user {gpgUserId}", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to add GPG user '{gpgUserId}': {result.Error}");
            }
        }

        /// <summary>
        /// Lists GPG users with access to the repository.
        /// </summary>
        public async Task<IReadOnlyList<string>> ListGpgUsersAsync(CancellationToken cancellationToken = default)
        {
            var gpgKeysDir = Path.Combine(_config.RepositoryPath, ".git-crypt", "keys", "default", "0");
            if (!Directory.Exists(gpgKeysDir))
                return Array.Empty<string>();

            var gpgFiles = Directory.GetFiles(gpgKeysDir, "*.gpg");
            var users = gpgFiles
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToList()
                .AsReadOnly();

            return await Task.FromResult(users);
        }

        private async Task EnsureGitAttributesAsync(CancellationToken cancellationToken)
        {
            var gitAttributesPath = Path.Combine(_config.RepositoryPath, ".gitattributes");
            var keyPattern = $"{_config.KeyStorePath}/**/*.key";
            var requiredLine = $"{keyPattern} filter=git-crypt diff=git-crypt";

            string existingContent = "";
            if (File.Exists(gitAttributesPath))
            {
                existingContent = await File.ReadAllTextAsync(gitAttributesPath, cancellationToken);
                if (existingContent.Contains(keyPattern))
                    return; // Already configured
            }

            // Add the pattern
            var newContent = existingContent.TrimEnd() + Environment.NewLine + requiredLine + Environment.NewLine;
            await File.WriteAllTextAsync(gitAttributesPath, newContent, cancellationToken);

            // Stage .gitattributes
            await ExecuteGitAsync($"add \"{gitAttributesPath}\"", cancellationToken);
        }

        #endregion

        #region Process Execution

        private async Task<ProcessResult> ExecuteGitCryptAsync(string arguments, CancellationToken cancellationToken = default)
        {
            return await ExecuteProcessAsync(_config.GitCryptPath, arguments, _config.RepositoryPath, cancellationToken);
        }

        private async Task<ProcessResult> ExecuteGitAsync(string arguments, CancellationToken cancellationToken = default)
        {
            return await ExecuteProcessAsync("git", arguments, _config.RepositoryPath, cancellationToken);
        }

        private async Task<ProcessResult> ExecuteProcessAsync(string program, string arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            await _processLock.WaitAsync(cancellationToken);
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = program,
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw;
                }

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Output = outputBuilder.ToString().Trim(),
                    Error = errorBuilder.ToString().Trim()
                };
            }
            finally
            {
                _processLock.Release();
            }
        }

        #endregion

        private string GetKeyFilePath(string keyId)
        {
            // Sanitize keyId to prevent path traversal
            var safeKeyId = Regex.Replace(keyId, @"[^a-zA-Z0-9_-]", "_");
            return Path.Combine(_config.RepositoryPath, _config.KeyStorePath, $"{safeKeyId}.key");
        }

        public override void Dispose()
        {
            _processLock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for git-crypt key store strategy.
    /// </summary>
    public class GitCryptConfig
    {
        /// <summary>
        /// Path to the Git repository (default: current directory).
        /// </summary>
        public string RepositoryPath { get; set; } = Directory.GetCurrentDirectory();

        /// <summary>
        /// Path within the repository for storing encrypted key files.
        /// </summary>
        public string KeyStorePath { get; set; } = ".secrets";

        /// <summary>
        /// Path to the git-crypt binary (default: assumes it's in PATH).
        /// </summary>
        public string GitCryptPath { get; set; } = "git-crypt";

        /// <summary>
        /// Environment variable containing the base64-encoded symmetric key for CI/CD.
        /// </summary>
        public string SymmetricKeyEnvVar { get; set; } = "GIT_CRYPT_KEY";

        /// <summary>
        /// Automatically initialize git-crypt if not already initialized.
        /// </summary>
        public bool AutoInit { get; set; } = false;
    }

    /// <summary>
    /// Key data structure stored in git-crypt encrypted files.
    /// </summary>
    internal class GitCryptKeyData
    {
        public string Key { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string Algorithm { get; set; } = "AES-256";
    }

    /// <summary>
    /// Result of a process execution.
    /// </summary>
    internal class ProcessResult
    {
        public int ExitCode { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
    }

    #endregion
}
