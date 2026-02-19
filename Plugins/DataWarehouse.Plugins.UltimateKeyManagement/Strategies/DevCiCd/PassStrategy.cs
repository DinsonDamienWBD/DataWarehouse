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
    /// Unix Pass (password-store) KeyStore strategy using the pass CLI.
    /// Implements IKeyStoreStrategy using pass for GPG-encrypted password/key management.
    ///
    /// Features:
    /// - Standard Unix password store integration
    /// - GPG-based encryption with multiple recipients
    /// - Git integration for version control and sync
    /// - Folder-based organization
    /// - Clipboard integration (optional)
    /// - OTP support via pass-otp extension
    /// - Tree-like hierarchical storage
    ///
    /// Prerequisites:
    /// - pass CLI installed (https://www.passwordstore.org/)
    /// - GPG with at least one key pair
    /// - Pass store initialized (pass init)
    ///
    /// Configuration:
    /// - PasswordStorePath: Path to password store (default: ~/.password-store)
    /// - PassPath: Path to pass binary (default: "pass")
    /// - GpgId: GPG key ID for encryption (or use existing store config)
    /// - SubPath: Sub-path within store for DataWarehouse keys (default: "datawarehouse")
    /// - AutoInit: Auto-initialize store if not exists (default: false)
    /// - GitEnabled: Enable git integration (default: true if store has .git)
    /// </summary>
    public sealed class PassStrategy : KeyStoreStrategyBase
    {
        private PassConfig _config = new();
        private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();
        private string? _currentKeyId;
        private readonly SemaphoreSlim _processLock = new(1, 1);
        private bool _isInitialized;
        private bool _gitEnabled;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,  // Unless GPG uses smartcard
            SupportsExpiration = false,
            SupportsReplication = true,  // Via git push/pull
            SupportsVersioning = true,   // Via git history
            SupportsPerKeyAcl = true,    // Multiple GPG IDs per subfolder
            SupportsAuditLogging = true, // Via git log
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "UnixPass",
                ["Backend"] = "pass CLI (password-store.org)",
                ["Platform"] = "Unix/Linux/macOS/WSL",
                ["Encryption"] = "GPG (OpenPGP)",
                ["Features"] = new[] { "GPG", "Git", "Multi-Key", "Hierarchical" },
                ["IdealFor"] = "Unix sysadmins, DevOps, Personal key management"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("pass.shutdown");
            _keyCache.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("pass.init");
            // Load configuration
            if (Configuration.TryGetValue("PasswordStorePath", out var storePathObj) && storePathObj is string storePath)
                _config.PasswordStorePath = storePath;
            if (Configuration.TryGetValue("PassPath", out var passPathObj) && passPathObj is string passPath)
                _config.PassPath = passPath;
            if (Configuration.TryGetValue("GpgId", out var gpgIdObj) && gpgIdObj is string gpgId)
                _config.GpgId = gpgId;
            if (Configuration.TryGetValue("SubPath", out var subPathObj) && subPathObj is string subPath)
                _config.SubPath = subPath;
            if (Configuration.TryGetValue("AutoInit", out var autoInitObj) && autoInitObj is bool autoInit)
                _config.AutoInit = autoInit;
            if (Configuration.TryGetValue("GitEnabled", out var gitEnabledObj) && gitEnabledObj is bool gitEnabled)
                _config.GitEnabled = gitEnabled;
            if (Configuration.TryGetValue("GpgProgramPath", out var gpgPathObj) && gpgPathObj is string gpgPath)
                _config.GpgProgramPath = gpgPath;

            // Expand ~ in path
            _config.PasswordStorePath = ExpandPath(_config.PasswordStorePath);

            // Set PASSWORD_STORE_DIR environment for pass commands
            Environment.SetEnvironmentVariable("PASSWORD_STORE_DIR", _config.PasswordStorePath);

            // Verify pass is installed
            await VerifyPassInstallationAsync(cancellationToken);

            // Check if store is initialized
            _isInitialized = await IsStoreInitializedAsync(cancellationToken);

            if (!_isInitialized)
            {
                if (_config.AutoInit && !string.IsNullOrEmpty(_config.GpgId))
                {
                    await InitializeStoreAsync(cancellationToken);
                    _isInitialized = true;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Password store not initialized at '{_config.PasswordStorePath}'. " +
                        "Run 'pass init <gpg-id>' or set AutoInit=true with GpgId in configuration.");
                }
            }

            // Check for git integration
            _gitEnabled = _config.GitEnabled ?? Directory.Exists(Path.Combine(_config.PasswordStorePath, ".git"));

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
                var result = await ExecutePassAsync("version", cancellationToken);
                if (result.ExitCode != 0)
                    return false;

                // Verify we can access the store
                result = await ExecutePassAsync("ls", cancellationToken);
                return result.ExitCode == 0;
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

            var passPath = GetPassPath(keyId);

            // Use pass show to decrypt and retrieve
            var result = await ExecutePassAsync($"show \"{passPath}\"");

            if (result.ExitCode != 0)
            {
                if (result.Error?.Contains("not in the password store") == true ||
                    result.Error?.Contains("is not a directory") == true)
                {
                    throw new KeyNotFoundException($"Key '{keyId}' not found in password store.");
                }
                throw new InvalidOperationException($"Failed to retrieve key '{keyId}': {result.Error}");
            }

            // Parse the output - first line should be the key (base64)
            var lines = result.Output?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines == null || lines.Length == 0)
            {
                throw new InvalidOperationException($"Empty key data for '{keyId}'.");
            }

            // Try to parse as JSON (our structured format)
            byte[] keyBytes;
            try
            {
                var json = string.Join("\n", lines);
                var keyData = JsonSerializer.Deserialize<PassKeyData>(json);
                if (keyData?.Key != null)
                {
                    keyBytes = Convert.FromBase64String(keyData.Key);
                }
                else
                {
                    // Fall back to treating first line as raw base64
                    keyBytes = Convert.FromBase64String(lines[0]);
                }
            }
            catch (JsonException)
            {
                // Not JSON, treat first line as raw base64
                keyBytes = Convert.FromBase64String(lines[0]);
            }

            _keyCache[keyId] = keyBytes;
            return keyBytes;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var passPath = GetPassPath(keyId);

            // Create structured key data
            var keyDataObj = new PassKeyData
            {
                Key = Convert.ToBase64String(keyData),
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                Algorithm = "AES-256"
            };

            var json = JsonSerializer.Serialize(keyDataObj, new JsonSerializerOptions { WriteIndented = true });

            // Use pass insert with multiline input
            var result = await ExecutePassWithInputAsync($"insert -m -f \"{passPath}\"", json);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to store key '{keyId}': {result.Error}");
            }

            // Update cache
            _keyCache[keyId] = keyData;
            _currentKeyId = keyId;

            // Git commit if enabled (pass does this automatically, but verify)
            if (_gitEnabled)
            {
                await ExecutePassAsync("git status");
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var subPath = _config.SubPath;
            var result = await ExecutePassAsync(string.IsNullOrEmpty(subPath) ? "ls" : $"ls \"{subPath}\"", cancellationToken);

            if (result.ExitCode != 0)
                return Array.Empty<string>();

            // Parse tree output from pass
            var keys = ParsePassTreeOutput(result.Output);
            return keys.AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var passPath = GetPassPath(keyId);

            // pass rm -f to force delete
            var result = await ExecutePassAsync($"rm -f \"{passPath}\"", cancellationToken);

            if (result.ExitCode != 0 && !result.Error?.Contains("is not in the password store") == true)
            {
                throw new InvalidOperationException($"Failed to delete key '{keyId}': {result.Error}");
            }

            _keyCache.TryRemove(keyId, out _);
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var passPath = GetPassPath(keyId);
            var fullPath = Path.Combine(_config.PasswordStorePath, $"{passPath}.gpg");

            if (!File.Exists(fullPath))
                return null;

            var fileInfo = new FileInfo(fullPath);

            // Try to get git log info
            string? lastCommit = null;
            DateTime? lastModified = null;

            if (_gitEnabled)
            {
                var gitResult = await ExecutePassAsync($"git log -1 --format=\"%H|%ai\" -- \"{passPath}.gpg\"", cancellationToken);
                if (gitResult.ExitCode == 0 && !string.IsNullOrEmpty(gitResult.Output))
                {
                    var parts = gitResult.Output.Split('|');
                    if (parts.Length >= 2)
                    {
                        lastCommit = parts[0].Trim();
                        if (DateTime.TryParse(parts[1].Trim(), out var dt))
                        {
                            lastModified = dt;
                        }
                    }
                }
            }

            return new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = fileInfo.CreationTimeUtc,
                LastRotatedAt = lastModified ?? fileInfo.LastWriteTimeUtc,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["FilePath"] = fullPath,
                    ["PassPath"] = passPath,
                    ["FileSize"] = fileInfo.Length,
                    ["Backend"] = "Unix Pass",
                    ["GitCommit"] = lastCommit ?? "N/A",
                    ["GitEnabled"] = _gitEnabled
                }
            };
        }

        #region Pass Store Operations

        private async Task VerifyPassInstallationAsync(CancellationToken cancellationToken)
        {
            var result = await ExecutePassAsync("version", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"pass is not installed or not accessible at '{_config.PassPath}'. " +
                    "Please install pass: https://www.passwordstore.org/");
            }
        }

        private async Task<bool> IsStoreInitializedAsync(CancellationToken cancellationToken)
        {
            // Check for .gpg-id file in store
            var gpgIdFile = Path.Combine(_config.PasswordStorePath, ".gpg-id");
            if (!File.Exists(gpgIdFile))
                return false;

            // Verify store is accessible
            var result = await ExecutePassAsync("ls", cancellationToken);
            return result.ExitCode == 0;
        }

        private async Task InitializeStoreAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_config.GpgId))
            {
                throw new InvalidOperationException("GPG ID required for store initialization.");
            }

            // Initialize with git if available
            var initCmd = _config.GitEnabled == true
                ? $"init -g \"{_config.GpgId}\""
                : $"init \"{_config.GpgId}\"";

            var result = await ExecutePassAsync(initCmd, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to initialize password store: {result.Error}");
            }
        }

        /// <summary>
        /// Syncs the password store with remote git repository.
        /// </summary>
        public async Task SyncAsync(CancellationToken cancellationToken = default)
        {
            if (!_gitEnabled)
            {
                throw new InvalidOperationException("Git is not enabled for this password store.");
            }

            // Pull then push
            var pullResult = await ExecutePassAsync("git pull --rebase", cancellationToken);
            if (pullResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git pull failed: {pullResult.Error}");
            }

            var pushResult = await ExecutePassAsync("git push", cancellationToken);
            if (pushResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git push failed: {pushResult.Error}");
            }
        }

        /// <summary>
        /// Gets the GPG IDs authorized for this store or subfolder.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetGpgIdsAsync(string? subPath = null, CancellationToken cancellationToken = default)
        {
            var gpgIdPath = string.IsNullOrEmpty(subPath)
                ? Path.Combine(_config.PasswordStorePath, ".gpg-id")
                : Path.Combine(_config.PasswordStorePath, subPath, ".gpg-id");

            if (!File.Exists(gpgIdPath))
            {
                // Fall back to root .gpg-id
                gpgIdPath = Path.Combine(_config.PasswordStorePath, ".gpg-id");
            }

            if (!File.Exists(gpgIdPath))
                return Array.Empty<string>();

            var gpgIds = await File.ReadAllLinesAsync(gpgIdPath, cancellationToken);
            return gpgIds
                .Where(id => !string.IsNullOrWhiteSpace(id) && !id.StartsWith("#"))
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Generates a random password using pass generate.
        /// </summary>
        public async Task<string> GeneratePasswordAsync(string path, int length = 32, bool noSymbols = false, CancellationToken cancellationToken = default)
        {
            var args = noSymbols ? $"-n {length}" : length.ToString();
            var result = await ExecutePassAsync($"generate -f {args} \"{path}\"", cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to generate password: {result.Error}");
            }

            // Get the generated password
            var showResult = await ExecutePassAsync($"show \"{path}\"", cancellationToken);
            var lines = showResult.Output?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines?.FirstOrDefault() ?? string.Empty;
        }

        private List<string> ParsePassTreeOutput(string? output)
        {
            if (string.IsNullOrEmpty(output))
                return new List<string>();

            var keys = new List<string>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Strip tree characters: ├── └── │
                var cleaned = Regex.Replace(line, @"^[│├└─\s]+", "").Trim();

                if (string.IsNullOrEmpty(cleaned))
                    continue;

                // Skip directory lines (those ending with /)
                if (cleaned.EndsWith("/"))
                    continue;

                // Skip the Password Store header
                if (cleaned.Equals("Password Store", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip subpath header if present
                if (cleaned == _config.SubPath)
                    continue;

                keys.Add(cleaned);
            }

            return keys;
        }

        #endregion

        #region Process Execution

        private async Task<ProcessResult> ExecutePassAsync(string arguments, CancellationToken cancellationToken = default)
        {
            return await ExecuteProcessAsync(_config.PassPath, arguments, null, cancellationToken);
        }

        private async Task<ProcessResult> ExecutePassWithInputAsync(string arguments, string input, CancellationToken cancellationToken = default)
        {
            return await ExecuteProcessAsync(_config.PassPath, arguments, input, cancellationToken);
        }

        private async Task<ProcessResult> ExecuteProcessAsync(string program, string arguments, string? stdin, CancellationToken cancellationToken)
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
                        WorkingDirectory = _config.PasswordStorePath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = stdin != null,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                // Set environment variables
                process.StartInfo.EnvironmentVariables["PASSWORD_STORE_DIR"] = _config.PasswordStorePath;
                if (!string.IsNullOrEmpty(_config.GpgProgramPath))
                {
                    process.StartInfo.EnvironmentVariables["PASSWORD_STORE_GPG_OPTS"] = $"--gpg-program {_config.GpgProgramPath}";
                }

                // Disable GPG TTY for non-interactive operation
                process.StartInfo.EnvironmentVariables["GPG_TTY"] = "";

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!string.IsNullOrEmpty(stdin))
                {
                    await process.StandardInput.WriteAsync(stdin);
                    process.StandardInput.Close();
                }

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

        private string GetPassPath(string keyId)
        {
            // Sanitize keyId to prevent path traversal
            var safeKeyId = Regex.Replace(keyId, @"[^a-zA-Z0-9_/-]", "_");

            if (string.IsNullOrEmpty(_config.SubPath))
            {
                return safeKeyId;
            }

            return $"{_config.SubPath}/{safeKeyId}";
        }

        private static string ExpandPath(string path)
        {
            if (path.StartsWith("~"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, path.Substring(1).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            return path;
        }

        public override void Dispose()
        {
            _processLock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for Unix Pass key store strategy.
    /// </summary>
    public class PassConfig
    {
        /// <summary>
        /// Path to the password store directory.
        /// </summary>
        public string PasswordStorePath { get; set; } = "~/.password-store";

        /// <summary>
        /// Path to the pass binary.
        /// </summary>
        public string PassPath { get; set; } = "pass";

        /// <summary>
        /// GPG key ID for encryption (used for initialization).
        /// </summary>
        public string? GpgId { get; set; }

        /// <summary>
        /// Sub-path within the store for DataWarehouse keys.
        /// </summary>
        public string SubPath { get; set; } = "datawarehouse";

        /// <summary>
        /// Auto-initialize store if not exists.
        /// </summary>
        public bool AutoInit { get; set; } = false;

        /// <summary>
        /// Enable git integration (null = auto-detect).
        /// </summary>
        public bool? GitEnabled { get; set; }

        /// <summary>
        /// Custom path to GPG binary.
        /// </summary>
        public string? GpgProgramPath { get; set; }
    }

    /// <summary>
    /// Key data structure stored in pass.
    /// </summary>
    internal class PassKeyData
    {
        public string Key { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string Algorithm { get; set; } = "AES-256";
    }

    #endregion
}
