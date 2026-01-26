using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.KeyRotation
{
    /// <summary>
    /// Automated key rotation plugin for DataWarehouse.
    /// Provides scheduled and on-demand key rotation with versioning and audit trails.
    ///
    /// Features:
    /// - Scheduled automatic key rotation (configurable intervals)
    /// - On-demand key rotation via message commands
    /// - Key versioning with full version history
    /// - Re-encryption of data using new keys
    /// - Comprehensive audit trail of all key operations
    /// - Support for multiple key stores
    /// - Grace period for old key deactivation
    /// - Compliance reporting (PCI-DSS, SOX, HIPAA)
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Message Commands:
    /// - keyrotation.rotate: Trigger immediate key rotation
    /// - keyrotation.schedule: Configure rotation schedule
    /// - keyrotation.reencrypt: Re-encrypt data with current key
    /// - keyrotation.audit: Get audit trail
    /// - keyrotation.versions: List key versions
    /// - keyrotation.stats: Get rotation statistics
    /// </summary>
    public sealed class KeyRotationPlugin : SecurityProviderPluginBase, IKeyStore, IDisposable
    {
        private readonly KeyRotationConfig _config;
        private readonly ConcurrentDictionary<string, KeyVersion> _keyVersions = new();
        private readonly ConcurrentDictionary<string, KeyMetadata> _keyMetadata = new();
        private readonly ConcurrentQueue<KeyAuditEntry> _auditLog = new();
        private readonly object _rotationLock = new();
        private readonly SemaphoreSlim _reencryptionSemaphore;
        private readonly Timer? _rotationTimer;
        private IKeyStore? _backingKeyStore;
        private string _currentKeyId = string.Empty;
        private DateTime _lastRotation = DateTime.MinValue;
        private DateTime _nextScheduledRotation = DateTime.MaxValue;
        private long _rotationCount;
        private long _reencryptionCount;
        private long _auditEntryCount;
        private bool _disposed;

        /// <summary>
        /// Maximum audit log entries to retain.
        /// </summary>
        private const int MaxAuditEntries = 100000;

        /// <summary>
        /// Default key size in bytes (256 bits for AES-256).
        /// </summary>
        private const int DefaultKeySizeBytes = 32;

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.security.keyrotation";

        /// <inheritdoc/>
        public override string Name => "Key Rotation Manager";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Initializes a new instance of the key rotation plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public KeyRotationPlugin(KeyRotationConfig? config = null)
        {
            _config = config ?? new KeyRotationConfig();
            _reencryptionSemaphore = new SemaphoreSlim(_config.MaxConcurrentReencryptions);

            if (_config.BackingKeyStore != null)
            {
                _backingKeyStore = _config.BackingKeyStore;
            }

            if (_config.EnableScheduledRotation && _config.RotationInterval > TimeSpan.Zero)
            {
                _rotationTimer = new Timer(
                    OnScheduledRotation,
                    null,
                    _config.RotationInterval,
                    _config.RotationInterval);

                _nextScheduledRotation = DateTime.UtcNow.Add(_config.RotationInterval);
            }

            // Initialize with a default key if none exists
            InitializeDefaultKeyAsync().GetAwaiter().GetResult();
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            response.Metadata["CurrentKeyId"] = _currentKeyId;
            response.Metadata["KeyVersionCount"] = _keyVersions.Count;
            response.Metadata["ScheduledRotation"] = _config.EnableScheduledRotation;
            response.Metadata["NextRotation"] = _nextScheduledRotation.ToString("O");

            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "keyrotation.rotate", DisplayName = "Rotate Key", Description = "Trigger immediate key rotation" },
                new() { Name = "keyrotation.schedule", DisplayName = "Schedule", Description = "Configure rotation schedule" },
                new() { Name = "keyrotation.reencrypt", DisplayName = "Re-encrypt", Description = "Re-encrypt data with current key" },
                new() { Name = "keyrotation.audit", DisplayName = "Audit", Description = "Get audit trail of key operations" },
                new() { Name = "keyrotation.versions", DisplayName = "Versions", Description = "List all key versions" },
                new() { Name = "keyrotation.stats", DisplayName = "Statistics", Description = "Get rotation statistics" },
                new() { Name = "keyrotation.deactivate", DisplayName = "Deactivate", Description = "Deactivate an old key version" },
                new() { Name = "keyrotation.export", DisplayName = "Export Audit", Description = "Export audit log for compliance" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "KeyRotation";
            metadata["SupportsScheduledRotation"] = true;
            metadata["SupportsVersioning"] = true;
            metadata["SupportsReencryption"] = true;
            metadata["SupportsAuditTrail"] = true;
            metadata["CurrentKeyId"] = _currentKeyId;
            metadata["KeyVersionCount"] = _keyVersions.Count;
            metadata["RotationInterval"] = _config.RotationInterval.TotalHours;
            metadata["ComplianceFrameworks"] = new[] { "PCI-DSS", "SOX", "HIPAA", "GDPR" };
            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "keyrotation.rotate" => HandleRotateAsync(message),
                "keyrotation.schedule" => HandleScheduleAsync(message),
                "keyrotation.reencrypt" => HandleReencryptAsync(message),
                "keyrotation.audit" => HandleAuditAsync(message),
                "keyrotation.versions" => HandleVersionsAsync(message),
                "keyrotation.stats" => HandleStatsAsync(message),
                "keyrotation.deactivate" => HandleDeactivateAsync(message),
                "keyrotation.export" => HandleExportAsync(message),
                "keyrotation.setBackingStore" => HandleSetBackingStoreAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        #region IKeyStore Implementation

        /// <summary>
        /// Gets the current active key ID.
        /// </summary>
        public Task<string> GetCurrentKeyIdAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(_currentKeyId))
            {
                throw new InvalidOperationException("No active key available. Initialize or rotate keys first.");
            }

            return Task.FromResult(_currentKeyId);
        }

        /// <summary>
        /// Gets a key by ID (synchronous version).
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <returns>The key bytes.</returns>
        public byte[] GetKey(string keyId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(keyId))
            {
                throw new ArgumentNullException(nameof(keyId));
            }

            // Try local cache first
            if (_keyVersions.TryGetValue(keyId, out var keyVersion))
            {
                if (keyVersion.State == KeyState.Deactivated)
                {
                    throw new InvalidOperationException($"Key '{keyId}' has been deactivated");
                }

                if (keyVersion.State == KeyState.Destroyed)
                {
                    throw new KeyNotFoundException($"Key '{keyId}' has been destroyed");
                }

                // Return a copy to prevent external modification
                var keyCopy = new byte[keyVersion.KeyMaterial.Length];
                Array.Copy(keyVersion.KeyMaterial, keyCopy, keyVersion.KeyMaterial.Length);
                return keyCopy;
            }

            // Try backing key store if available
            if (_backingKeyStore != null)
            {
                return _backingKeyStore.GetKey(keyId);
            }

            throw new KeyNotFoundException($"Key '{keyId}' not found");
        }

        /// <summary>
        /// Retrieves a key by ID for the given security context.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="context">The security context for authorization.</param>
        /// <returns>The key bytes.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the key doesn't exist.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown when the context lacks permission.</exception>
        public async Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(keyId))
            {
                throw new ArgumentNullException(nameof(keyId));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // Check authorization
            if (!IsAuthorized(context, "read", keyId))
            {
                LogAuditEntry(new KeyAuditEntry
                {
                    Operation = KeyOperation.AccessDenied,
                    KeyId = keyId,
                    UserId = context.UserId,
                    Timestamp = DateTime.UtcNow,
                    Details = "Unauthorized key access attempt"
                });

                throw new UnauthorizedAccessException($"User '{context.UserId}' is not authorized to access key '{keyId}'");
            }

            // Try local cache first
            if (_keyVersions.TryGetValue(keyId, out var keyVersion))
            {
                if (keyVersion.State == KeyState.Deactivated)
                {
                    throw new InvalidOperationException($"Key '{keyId}' has been deactivated");
                }

                if (keyVersion.State == KeyState.Destroyed)
                {
                    throw new KeyNotFoundException($"Key '{keyId}' has been destroyed");
                }

                LogAuditEntry(new KeyAuditEntry
                {
                    Operation = KeyOperation.Read,
                    KeyId = keyId,
                    UserId = context.UserId,
                    Timestamp = DateTime.UtcNow
                });

                // Return a copy to prevent external modification
                var keyCopy = new byte[keyVersion.KeyMaterial.Length];
                Array.Copy(keyVersion.KeyMaterial, keyCopy, keyVersion.KeyMaterial.Length);
                return keyCopy;
            }

            // Try backing key store
            if (_backingKeyStore != null)
            {
                var key = await _backingKeyStore.GetKeyAsync(keyId, context);

                LogAuditEntry(new KeyAuditEntry
                {
                    Operation = KeyOperation.Read,
                    KeyId = keyId,
                    UserId = context.UserId,
                    Timestamp = DateTime.UtcNow,
                    Details = "Retrieved from backing store"
                });

                return key;
            }

            throw new KeyNotFoundException($"Key '{keyId}' not found");
        }

        /// <summary>
        /// Creates a new key with the specified ID.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="context">The security context.</param>
        /// <returns>The created key bytes.</returns>
        public Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrEmpty(keyId))
            {
                throw new ArgumentNullException(nameof(keyId));
            }

            if (!IsAuthorized(context, "create", keyId))
            {
                throw new UnauthorizedAccessException($"User '{context.UserId}' is not authorized to create keys");
            }

            var keyMaterial = RandomNumberGenerator.GetBytes(DefaultKeySizeBytes);

            var keyVersion = new KeyVersion
            {
                KeyId = keyId,
                VersionNumber = 1,
                KeyMaterial = keyMaterial,
                State = KeyState.Active,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                Algorithm = "AES-256",
                KeySize = DefaultKeySizeBytes * 8
            };

            if (!_keyVersions.TryAdd(keyId, keyVersion))
            {
                CryptographicOperations.ZeroMemory(keyMaterial);
                throw new InvalidOperationException($"Key '{keyId}' already exists");
            }

            _keyMetadata[keyId] = new KeyMetadata
            {
                KeyId = keyId,
                CurrentVersion = 1,
                TotalVersions = 1,
                CreatedAt = DateTime.UtcNow,
                LastRotatedAt = DateTime.UtcNow
            };

            lock (_rotationLock)
            {
                _currentKeyId = keyId;
                _lastRotation = DateTime.UtcNow;
            }

            LogAuditEntry(new KeyAuditEntry
            {
                Operation = KeyOperation.Create,
                KeyId = keyId,
                UserId = context.UserId,
                Timestamp = DateTime.UtcNow,
                Details = $"Created new key (version 1)"
            });

            // Return a copy to prevent external modification
            var keyCopy = new byte[keyMaterial.Length];
            Array.Copy(keyMaterial, keyCopy, keyMaterial.Length);
            return Task.FromResult(keyCopy);
        }

        #endregion

        #region Key Rotation Operations

        /// <summary>
        /// Rotates the current key, creating a new version.
        /// </summary>
        /// <param name="context">The security context.</param>
        /// <param name="reason">Optional reason for rotation.</param>
        /// <returns>Information about the new key version.</returns>
        public async Task<KeyRotationResult> RotateKeyAsync(ISecurityContext context, string? reason = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsAuthorized(context, "rotate", _currentKeyId))
            {
                throw new UnauthorizedAccessException($"User '{context.UserId}' is not authorized to rotate keys");
            }

            lock (_rotationLock)
            {
                var oldKeyId = _currentKeyId;
                var newKeyId = GenerateKeyId();
                var newKeyMaterial = RandomNumberGenerator.GetBytes(DefaultKeySizeBytes);

                // Get version number
                var versionNumber = 1;
                if (_keyMetadata.TryGetValue(oldKeyId, out var metadata))
                {
                    versionNumber = metadata.CurrentVersion + 1;
                }

                // Create new key version
                var newKeyVersion = new KeyVersion
                {
                    KeyId = newKeyId,
                    VersionNumber = versionNumber,
                    KeyMaterial = newKeyMaterial,
                    State = KeyState.Active,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    PreviousKeyId = oldKeyId,
                    Algorithm = "AES-256",
                    KeySize = DefaultKeySizeBytes * 8
                };

                _keyVersions[newKeyId] = newKeyVersion;

                // Mark old key for deactivation after grace period
                if (_keyVersions.TryGetValue(oldKeyId, out var oldKeyVersion))
                {
                    oldKeyVersion.State = KeyState.PendingDeactivation;
                    oldKeyVersion.DeactivationScheduledAt = DateTime.UtcNow.Add(_config.GracePeriod);
                }

                // Update metadata
                _keyMetadata[newKeyId] = new KeyMetadata
                {
                    KeyId = newKeyId,
                    CurrentVersion = versionNumber,
                    TotalVersions = versionNumber,
                    CreatedAt = DateTime.UtcNow,
                    LastRotatedAt = DateTime.UtcNow,
                    PreviousKeyId = oldKeyId
                };

                _currentKeyId = newKeyId;
                _lastRotation = DateTime.UtcNow;
                _nextScheduledRotation = DateTime.UtcNow.Add(_config.RotationInterval);
                Interlocked.Increment(ref _rotationCount);

                LogAuditEntry(new KeyAuditEntry
                {
                    Operation = KeyOperation.Rotate,
                    KeyId = newKeyId,
                    UserId = context.UserId,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Rotated from {oldKeyId} to {newKeyId} (version {versionNumber}). Reason: {reason ?? "Scheduled"}"
                });

                return new KeyRotationResult
                {
                    Success = true,
                    OldKeyId = oldKeyId,
                    NewKeyId = newKeyId,
                    VersionNumber = versionNumber,
                    RotatedAt = DateTime.UtcNow,
                    GracePeriodEndsAt = DateTime.UtcNow.Add(_config.GracePeriod)
                };
            }
        }

        /// <summary>
        /// Re-encrypts data using the current key.
        /// </summary>
        /// <param name="dataId">The identifier of the data to re-encrypt.</param>
        /// <param name="encryptedData">The currently encrypted data.</param>
        /// <param name="oldKeyId">The key ID used for current encryption.</param>
        /// <param name="context">The security context.</param>
        /// <param name="encryptionPlugin">The encryption plugin to use.</param>
        /// <returns>Re-encrypted data with new key.</returns>
        public async Task<ReencryptionResult> ReencryptDataAsync(
            string dataId,
            Stream encryptedData,
            string oldKeyId,
            ISecurityContext context,
            IDataTransformation encryptionPlugin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!await _reencryptionSemaphore.WaitAsync(_config.ReencryptionTimeout))
            {
                throw new TimeoutException("Re-encryption operation timed out waiting for available slot");
            }

            try
            {
                if (!IsAuthorized(context, "reencrypt", dataId))
                {
                    throw new UnauthorizedAccessException($"User '{context.UserId}' is not authorized to re-encrypt data");
                }

                // Get old key
                var oldKey = await GetKeyAsync(oldKeyId, context);

                // Get new key
                var newKeyId = _currentKeyId;
                var newKey = await GetKeyAsync(newKeyId, context);

                try
                {
                    // Create mock kernel context for decryption
                    var decryptArgs = new Dictionary<string, object>
                    {
                        ["keyStore"] = this,
                        ["securityContext"] = context
                    };

                    // Decrypt with old key
                    using var decryptedStream = encryptionPlugin.OnRead(encryptedData, new MockKernelContext(this), decryptArgs);

                    // Re-encrypt with new key
                    var encryptArgs = new Dictionary<string, object>
                    {
                        ["keyStore"] = this,
                        ["securityContext"] = context,
                        ["keyId"] = newKeyId
                    };

                    var reencryptedStream = encryptionPlugin.OnWrite(decryptedStream, new MockKernelContext(this), encryptArgs);

                    Interlocked.Increment(ref _reencryptionCount);

                    LogAuditEntry(new KeyAuditEntry
                    {
                        Operation = KeyOperation.Reencrypt,
                        KeyId = newKeyId,
                        UserId = context.UserId,
                        Timestamp = DateTime.UtcNow,
                        Details = $"Re-encrypted data '{dataId}' from key '{oldKeyId}' to '{newKeyId}'"
                    });

                    return new ReencryptionResult
                    {
                        Success = true,
                        DataId = dataId,
                        OldKeyId = oldKeyId,
                        NewKeyId = newKeyId,
                        ReencryptedData = reencryptedStream,
                        ReencryptedAt = DateTime.UtcNow
                    };
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(oldKey);
                    CryptographicOperations.ZeroMemory(newKey);
                }
            }
            finally
            {
                _reencryptionSemaphore.Release();
            }
        }

        /// <summary>
        /// Deactivates an old key version.
        /// </summary>
        /// <param name="keyId">The key ID to deactivate.</param>
        /// <param name="context">The security context.</param>
        /// <returns>True if deactivation was successful.</returns>
        public Task<bool> DeactivateKeyAsync(string keyId, ISecurityContext context)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!IsAuthorized(context, "deactivate", keyId))
            {
                throw new UnauthorizedAccessException($"User '{context.UserId}' is not authorized to deactivate keys");
            }

            if (keyId == _currentKeyId)
            {
                throw new InvalidOperationException("Cannot deactivate the current active key. Rotate first.");
            }

            if (_keyVersions.TryGetValue(keyId, out var keyVersion))
            {
                keyVersion.State = KeyState.Deactivated;
                keyVersion.DeactivatedAt = DateTime.UtcNow;
                keyVersion.DeactivatedBy = context.UserId;

                LogAuditEntry(new KeyAuditEntry
                {
                    Operation = KeyOperation.Deactivate,
                    KeyId = keyId,
                    UserId = context.UserId,
                    Timestamp = DateTime.UtcNow
                });

                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        /// <summary>
        /// Gets all key versions.
        /// </summary>
        /// <returns>List of key version information.</returns>
        public Task<IReadOnlyList<KeyVersionInfo>> GetKeyVersionsAsync()
        {
            var versions = _keyVersions.Values
                .Select(kv => new KeyVersionInfo
                {
                    KeyId = kv.KeyId,
                    VersionNumber = kv.VersionNumber,
                    State = kv.State,
                    CreatedAt = kv.CreatedAt,
                    CreatedBy = kv.CreatedBy,
                    DeactivatedAt = kv.DeactivatedAt,
                    PreviousKeyId = kv.PreviousKeyId,
                    IsCurrent = kv.KeyId == _currentKeyId
                })
                .OrderByDescending(v => v.VersionNumber)
                .ToList();

            return Task.FromResult<IReadOnlyList<KeyVersionInfo>>(versions);
        }

        /// <summary>
        /// Gets the audit trail for key operations.
        /// </summary>
        /// <param name="filter">Optional filter criteria.</param>
        /// <returns>List of audit entries.</returns>
        public Task<IReadOnlyList<KeyAuditEntry>> GetAuditTrailAsync(AuditFilter? filter = null)
        {
            IEnumerable<KeyAuditEntry> entries = _auditLog.ToArray();

            if (filter != null)
            {
                if (filter.StartTime.HasValue)
                {
                    entries = entries.Where(e => e.Timestamp >= filter.StartTime.Value);
                }

                if (filter.EndTime.HasValue)
                {
                    entries = entries.Where(e => e.Timestamp <= filter.EndTime.Value);
                }

                if (!string.IsNullOrEmpty(filter.KeyId))
                {
                    entries = entries.Where(e => e.KeyId == filter.KeyId);
                }

                if (!string.IsNullOrEmpty(filter.UserId))
                {
                    entries = entries.Where(e => e.UserId == filter.UserId);
                }

                if (filter.Operations?.Any() == true)
                {
                    entries = entries.Where(e => filter.Operations.Contains(e.Operation));
                }

                if (filter.Limit > 0)
                {
                    entries = entries.Take(filter.Limit);
                }
            }

            return Task.FromResult<IReadOnlyList<KeyAuditEntry>>(entries.ToList());
        }

        #endregion

        #region Private Methods

        private async Task InitializeDefaultKeyAsync()
        {
            if (string.IsNullOrEmpty(_currentKeyId))
            {
                var context = new KeyRotationSecurityContext();
                var keyId = GenerateKeyId();
                await CreateKeyAsync(keyId, context);
            }
        }

        private void OnScheduledRotation(object? state)
        {
            try
            {
                var context = new KeyRotationSecurityContext { IsSystemAdmin = true };
                _ = RotateKeyAsync(context, "Scheduled rotation");
            }
            catch (Exception ex)
            {
                LogAuditEntry(new KeyAuditEntry
                {
                    Operation = KeyOperation.Error,
                    KeyId = _currentKeyId,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Scheduled rotation failed: {ex.Message}"
                });
            }
        }

        private static string GenerateKeyId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            return $"key-{timestamp}-{random}";
        }

        private bool IsAuthorized(ISecurityContext context, string operation, string resource)
        {
            if (context.IsSystemAdmin)
            {
                return true;
            }

            // Check for specific roles
            var requiredRole = operation switch
            {
                "create" => "key-admin",
                "rotate" => "key-admin",
                "deactivate" => "key-admin",
                "reencrypt" => "key-operator",
                "read" => "key-reader",
                _ => "key-admin"
            };

            return context.Roles.Any(r =>
                r.Equals(requiredRole, StringComparison.OrdinalIgnoreCase) ||
                r.Equals("key-admin", StringComparison.OrdinalIgnoreCase));
        }

        private void LogAuditEntry(KeyAuditEntry entry)
        {
            entry.EntryId = Guid.NewGuid().ToString("N");
            _auditLog.Enqueue(entry);
            Interlocked.Increment(ref _auditEntryCount);

            // Trim old entries if needed
            while (_auditLog.Count > MaxAuditEntries && _auditLog.TryDequeue(out _))
            {
            }
        }

        #endregion

        #region Message Handlers

        private async Task HandleRotateAsync(PluginMessage message)
        {
            var context = GetSecurityContext(message);
            var reason = message.Payload.TryGetValue("reason", out var r) ? r?.ToString() : null;

            var result = await RotateKeyAsync(context, reason);

            message.Payload["success"] = result.Success;
            message.Payload["oldKeyId"] = result.OldKeyId;
            message.Payload["newKeyId"] = result.NewKeyId;
            message.Payload["versionNumber"] = result.VersionNumber;
            message.Payload["rotatedAt"] = result.RotatedAt.ToString("O");
            message.Payload["gracePeriodEndsAt"] = result.GracePeriodEndsAt?.ToString("O") ?? string.Empty;
        }

        private Task HandleScheduleAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("intervalHours", out var intervalObj) && intervalObj is double hours)
            {
                _config.RotationInterval = TimeSpan.FromHours(hours);
                _nextScheduledRotation = DateTime.UtcNow.Add(_config.RotationInterval);

                LogAuditEntry(new KeyAuditEntry
                {
                    Operation = KeyOperation.ScheduleChange,
                    KeyId = _currentKeyId,
                    UserId = GetSecurityContext(message).UserId,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Rotation schedule changed to every {hours} hours"
                });
            }

            message.Payload["currentInterval"] = _config.RotationInterval.TotalHours;
            message.Payload["nextRotation"] = _nextScheduledRotation.ToString("O");

            return Task.CompletedTask;
        }

        private Task HandleReencryptAsync(PluginMessage message)
        {
            message.Payload["reencryptionCount"] = Interlocked.Read(ref _reencryptionCount);
            message.Payload["pendingReencryptions"] = _config.MaxConcurrentReencryptions - _reencryptionSemaphore.CurrentCount;
            return Task.CompletedTask;
        }

        private async Task HandleAuditAsync(PluginMessage message)
        {
            var filter = new AuditFilter();

            if (message.Payload.TryGetValue("limit", out var limitObj) && limitObj is int limit)
            {
                filter.Limit = limit;
            }

            if (message.Payload.TryGetValue("keyId", out var keyIdObj) && keyIdObj is string keyId)
            {
                filter.KeyId = keyId;
            }

            var entries = await GetAuditTrailAsync(filter);

            message.Payload["entries"] = entries.Select(e => new Dictionary<string, object>
            {
                ["entryId"] = e.EntryId ?? "",
                ["operation"] = e.Operation.ToString(),
                ["keyId"] = e.KeyId ?? "",
                ["userId"] = e.UserId ?? "",
                ["timestamp"] = e.Timestamp.ToString("O"),
                ["details"] = e.Details ?? ""
            }).ToList();

            message.Payload["totalEntries"] = Interlocked.Read(ref _auditEntryCount);
        }

        private async Task HandleVersionsAsync(PluginMessage message)
        {
            var versions = await GetKeyVersionsAsync();

            message.Payload["versions"] = versions.Select(v => new Dictionary<string, object>
            {
                ["keyId"] = v.KeyId,
                ["versionNumber"] = v.VersionNumber,
                ["state"] = v.State.ToString(),
                ["createdAt"] = v.CreatedAt.ToString("O"),
                ["createdBy"] = v.CreatedBy ?? "",
                ["isCurrent"] = v.IsCurrent
            }).ToList();

            message.Payload["currentKeyId"] = _currentKeyId;
            message.Payload["totalVersions"] = _keyVersions.Count;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            message.Payload["rotationCount"] = Interlocked.Read(ref _rotationCount);
            message.Payload["reencryptionCount"] = Interlocked.Read(ref _reencryptionCount);
            message.Payload["auditEntryCount"] = Interlocked.Read(ref _auditEntryCount);
            message.Payload["keyVersionCount"] = _keyVersions.Count;
            message.Payload["currentKeyId"] = _currentKeyId;
            message.Payload["lastRotation"] = _lastRotation.ToString("O");
            message.Payload["nextScheduledRotation"] = _nextScheduledRotation.ToString("O");
            message.Payload["scheduledRotationEnabled"] = _config.EnableScheduledRotation;
            message.Payload["rotationIntervalHours"] = _config.RotationInterval.TotalHours;
            message.Payload["gracePeriodHours"] = _config.GracePeriod.TotalHours;

            return Task.CompletedTask;
        }

        private async Task HandleDeactivateAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("keyId", out var keyIdObj) || keyIdObj is not string keyId)
            {
                throw new ArgumentException("Missing 'keyId' parameter");
            }

            var context = GetSecurityContext(message);
            var result = await DeactivateKeyAsync(keyId, context);

            message.Payload["success"] = result;
        }

        private async Task HandleExportAsync(PluginMessage message)
        {
            var filter = new AuditFilter();

            if (message.Payload.TryGetValue("startTime", out var startObj) && startObj is string startStr)
            {
                filter.StartTime = DateTime.Parse(startStr);
            }

            if (message.Payload.TryGetValue("endTime", out var endObj) && endObj is string endStr)
            {
                filter.EndTime = DateTime.Parse(endStr);
            }

            var entries = await GetAuditTrailAsync(filter);

            var exportData = new
            {
                ExportedAt = DateTime.UtcNow,
                TotalEntries = entries.Count,
                Entries = entries
            };

            message.Payload["exportData"] = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private Task HandleSetBackingStoreAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                _backingKeyStore = ks;
            }
            return Task.CompletedTask;
        }

        private ISecurityContext GetSecurityContext(PluginMessage message)
        {
            if (message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
            {
                return sc;
            }

            return new KeyRotationSecurityContext();
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _rotationTimer?.Dispose();
            _reencryptionSemaphore.Dispose();

            // Securely clear all key material
            foreach (var kv in _keyVersions.Values)
            {
                if (kv.KeyMaterial != null)
                {
                    CryptographicOperations.ZeroMemory(kv.KeyMaterial);
                }
            }

            _keyVersions.Clear();
            _keyMetadata.Clear();
        }
    }

    #region Data Models

    /// <summary>
    /// Configuration for key rotation plugin.
    /// </summary>
    public sealed class KeyRotationConfig
    {
        /// <summary>
        /// Backing key store for persistent key storage.
        /// </summary>
        public IKeyStore? BackingKeyStore { get; set; }

        /// <summary>
        /// Whether to enable scheduled automatic rotation.
        /// Default is true.
        /// </summary>
        public bool EnableScheduledRotation { get; set; } = true;

        /// <summary>
        /// Interval between automatic key rotations.
        /// Default is 90 days (PCI-DSS recommendation).
        /// </summary>
        public TimeSpan RotationInterval { get; set; } = TimeSpan.FromDays(90);

        /// <summary>
        /// Grace period before deactivating old keys.
        /// Default is 24 hours.
        /// </summary>
        public TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Maximum concurrent re-encryption operations.
        /// Default is 4.
        /// </summary>
        public int MaxConcurrentReencryptions { get; set; } = 4;

        /// <summary>
        /// Timeout for re-encryption operations.
        /// Default is 30 seconds.
        /// </summary>
        public TimeSpan ReencryptionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Key version information (internal).
    /// </summary>
    internal sealed class KeyVersion
    {
        public string KeyId { get; init; } = string.Empty;
        public int VersionNumber { get; init; }
        public byte[] KeyMaterial { get; init; } = Array.Empty<byte>();
        public KeyState State { get; set; }
        public DateTime CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
        public string? PreviousKeyId { get; init; }
        public DateTime? DeactivationScheduledAt { get; set; }
        public DateTime? DeactivatedAt { get; set; }
        public string? DeactivatedBy { get; set; }
        public string Algorithm { get; init; } = string.Empty;
        public int KeySize { get; init; }
    }

    /// <summary>
    /// Key metadata.
    /// </summary>
    internal sealed class KeyMetadata
    {
        public string KeyId { get; init; } = string.Empty;
        public int CurrentVersion { get; set; }
        public int TotalVersions { get; set; }
        public DateTime CreatedAt { get; init; }
        public DateTime LastRotatedAt { get; set; }
        public string? PreviousKeyId { get; init; }
    }

    /// <summary>
    /// Key state enumeration.
    /// </summary>
    public enum KeyState
    {
        Active,
        PendingDeactivation,
        Deactivated,
        Destroyed
    }

    /// <summary>
    /// Key operation types for audit.
    /// </summary>
    public enum KeyOperation
    {
        Create,
        Read,
        Rotate,
        Reencrypt,
        Deactivate,
        Destroy,
        AccessDenied,
        ScheduleChange,
        Error
    }

    /// <summary>
    /// Key version information (public DTO).
    /// </summary>
    public sealed class KeyVersionInfo
    {
        public string KeyId { get; init; } = string.Empty;
        public int VersionNumber { get; init; }
        public KeyState State { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
        public DateTime? DeactivatedAt { get; init; }
        public string? PreviousKeyId { get; init; }
        public bool IsCurrent { get; init; }
    }

    /// <summary>
    /// Audit log entry.
    /// </summary>
    public sealed class KeyAuditEntry
    {
        public string? EntryId { get; set; }
        public KeyOperation Operation { get; init; }
        public string? KeyId { get; init; }
        public string? UserId { get; init; }
        public DateTime Timestamp { get; init; }
        public string? Details { get; init; }
    }

    /// <summary>
    /// Filter for audit queries.
    /// </summary>
    public sealed class AuditFilter
    {
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string? KeyId { get; set; }
        public string? UserId { get; set; }
        public IReadOnlyList<KeyOperation>? Operations { get; set; }
        public int Limit { get; set; } = 1000;
    }

    /// <summary>
    /// Result of key rotation operation.
    /// </summary>
    public sealed class KeyRotationResult
    {
        public bool Success { get; init; }
        public string OldKeyId { get; init; } = string.Empty;
        public string NewKeyId { get; init; } = string.Empty;
        public int VersionNumber { get; init; }
        public DateTime RotatedAt { get; init; }
        public DateTime? GracePeriodEndsAt { get; init; }
    }

    /// <summary>
    /// Result of re-encryption operation.
    /// </summary>
    public sealed class ReencryptionResult
    {
        public bool Success { get; init; }
        public string DataId { get; init; } = string.Empty;
        public string OldKeyId { get; init; } = string.Empty;
        public string NewKeyId { get; init; } = string.Empty;
        public Stream? ReencryptedData { get; init; }
        public DateTime ReencryptedAt { get; init; }
    }

    /// <summary>
    /// Default security context for key rotation operations.
    /// </summary>
    internal sealed class KeyRotationSecurityContext : ISecurityContext
    {
        public string UserId => Environment.UserName;
        public string? TenantId => "keyrotation-local";
        public IEnumerable<string> Roles => new[] { "key-admin" };
        public bool IsSystemAdmin { get; set; }
    }

    /// <summary>
    /// Mock kernel context for re-encryption operations.
    /// </summary>
    internal sealed class MockKernelContext : IKernelContext
    {
        private readonly KeyRotationPlugin _plugin;

        public MockKernelContext(KeyRotationPlugin plugin)
        {
            _plugin = plugin;
        }

        public OperatingMode Mode => OperatingMode.Workstation;

        public string RootPath => Environment.CurrentDirectory;

        public IKernelStorageService Storage => new MockKernelStorageService();

        public T? GetPlugin<T>() where T : class, IPlugin
        {
            if (typeof(T) == typeof(IPlugin) || typeof(T).IsAssignableFrom(typeof(IKeyStore)))
            {
                return _plugin as T;
            }
            return null;
        }

        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin
        {
            if (typeof(T) == typeof(IPlugin) || typeof(T).IsAssignableFrom(typeof(IKeyStore)))
            {
                yield return (T)(object)_plugin;
            }
        }

        public void LogDebug(string message) { }
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }

    /// <summary>
    /// Mock kernel storage service (not used in this plugin).
    /// </summary>
    internal sealed class MockKernelStorageService : IKernelStorageService
    {
        public Task SaveAsync(string path, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("Mock storage service does not support save operations");
        }

        public Task SaveAsync(string path, byte[] data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            throw new NotSupportedException("Mock storage service does not support save operations");
        }

        public Task<Stream?> LoadAsync(string path, CancellationToken ct = default)
        {
            throw new NotSupportedException("Mock storage service does not support load operations");
        }

        public Task<byte[]?> LoadBytesAsync(string path, CancellationToken ct = default)
        {
            throw new NotSupportedException("Mock storage service does not support load operations");
        }

        public Task<bool> DeleteAsync(string path, CancellationToken ct = default)
        {
            throw new NotSupportedException("Mock storage service does not support delete operations");
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        {
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<StorageItemInfo>> ListAsync(string prefix, int limit = 100, int offset = 0, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<StorageItemInfo>>(Array.Empty<StorageItemInfo>());
        }

        public Task<IDictionary<string, string>?> GetMetadataAsync(string path, CancellationToken ct = default)
        {
            return Task.FromResult<IDictionary<string, string>?>(null);
        }
    }

    #endregion
}
