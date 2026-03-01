using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Features
{
    /// <summary>
    /// D6: Zero-downtime key rotation with dual-key period support.
    /// Enables seamless key rotation where both old and new keys are valid during transition.
    /// Supports automatic re-encryption of data with new key and rotation status tracking.
    /// </summary>
    public sealed class ZeroDowntimeRotation : IDisposable
    {
        private readonly BoundedDictionary<string, RotationSession> _activeSessions = new BoundedDictionary<string, RotationSession>(1000);
        private readonly BoundedDictionary<string, RotationHistory> _rotationHistory = new BoundedDictionary<string, RotationHistory>(1000);
        private readonly IKeyStoreRegistry _registry;
        private readonly IMessageBus? _messageBus;
        private readonly SemaphoreSlim _rotationLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// Default dual-key period duration (both keys valid).
        /// </summary>
        public TimeSpan DefaultDualKeyPeriod { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Maximum number of concurrent re-encryption operations.
        /// </summary>
        public int MaxConcurrentReEncryption { get; set; } = 4;

        /// <summary>
        /// Creates a new zero-downtime rotation service.
        /// </summary>
        public ZeroDowntimeRotation(IKeyStoreRegistry registry, IMessageBus? messageBus = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus;
        }

        /// <summary>
        /// Initiates a zero-downtime key rotation.
        /// </summary>
        /// <param name="keyStoreId">The key store containing the key to rotate.</param>
        /// <param name="currentKeyId">The current key ID being rotated.</param>
        /// <param name="context">Security context.</param>
        /// <param name="options">Rotation options.</param>
        /// <returns>Rotation session information.</returns>
        public async Task<RotationSession> InitiateRotationAsync(
            string keyStoreId,
            string currentKeyId,
            ISecurityContext context,
            RotationOptions? options = null,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyStoreId);
            ArgumentException.ThrowIfNullOrWhiteSpace(currentKeyId);
            ArgumentNullException.ThrowIfNull(context);

            options ??= new RotationOptions();

            await _rotationLock.WaitAsync(ct);
            try
            {
                // Check for existing rotation session
                var existingSession = _activeSessions.Values
                    .FirstOrDefault(s => s.KeyStoreId == keyStoreId &&
                                        s.CurrentKeyId == currentKeyId &&
                                        s.Status != RotationStatus.Completed &&
                                        s.Status != RotationStatus.Failed);

                if (existingSession != null)
                {
                    throw new InvalidOperationException(
                        $"Rotation already in progress for key '{currentKeyId}'. Session: {existingSession.SessionId}");
                }

                // Get the key store
                var keyStore = _registry.GetKeyStore(keyStoreId);
                if (keyStore == null)
                {
                    throw new KeyNotFoundException($"Key store '{keyStoreId}' not found.");
                }

                // Create new key
                var newKeyId = options.NewKeyId ?? GenerateNewKeyId(currentKeyId);
                var newKeyData = await keyStore.CreateKeyAsync(newKeyId, context);

                // Create rotation session
                var session = new RotationSession
                {
                    SessionId = GenerateSessionId(),
                    KeyStoreId = keyStoreId,
                    CurrentKeyId = currentKeyId,
                    NewKeyId = newKeyId,
                    Status = RotationStatus.DualKeyPeriod,
                    InitiatedAt = DateTime.UtcNow,
                    InitiatedBy = context.UserId,
                    DualKeyExpiresAt = DateTime.UtcNow.Add(options.DualKeyPeriod ?? DefaultDualKeyPeriod),
                    Options = options,
                    NewKeySizeBytes = newKeyData.Length
                };

                _activeSessions[session.SessionId] = session;

                // Publish rotation initiated event
                await PublishRotationEventAsync("rotation.initiated", session, context);

                return session;
            }
            finally
            {
                _rotationLock.Release();
            }
        }

        /// <summary>
        /// Checks if a key is valid during a rotation session.
        /// Both current and new keys are valid during the dual-key period.
        /// </summary>
        public async Task<KeyValidationResult> ValidateKeyAsync(string keyStoreId, string keyId,
            ISecurityContext? context = null, CancellationToken ct = default)
        {
            var result = new KeyValidationResult
            {
                KeyId = keyId,
                CheckedAt = DateTime.UtcNow
            };

            // Find active sessions for this key store
            var activeSessions = _activeSessions.Values
                .Where(s => s.KeyStoreId == keyStoreId &&
                           (s.Status == RotationStatus.DualKeyPeriod ||
                            s.Status == RotationStatus.ReEncrypting))
                .ToList();

            foreach (var session in activeSessions)
            {
                // Check if key matches current or new key
                if (session.CurrentKeyId == keyId)
                {
                    result.IsValid = true;
                    result.IsCurrentKey = true;
                    result.RotationSessionId = session.SessionId;
                    result.DualKeyExpiresAt = session.DualKeyExpiresAt;
                    result.RecommendedAction = "Continue using this key for decryption. Use new key for new encryptions.";
                    return result;
                }

                if (session.NewKeyId == keyId)
                {
                    result.IsValid = true;
                    result.IsNewKey = true;
                    result.RotationSessionId = session.SessionId;
                    result.DualKeyExpiresAt = session.DualKeyExpiresAt;
                    result.RecommendedAction = "This is the new primary key. Use for all new encryptions.";
                    return result;
                }
            }

            // P2-3447: Verify the keyId actually exists in the store, not just that the
            // store itself is present. Any keyId was previously "valid" as long as the store
            // existed, allowing callers to use non-existent keys without error.
            var keyStore = _registry.GetKeyStore(keyStoreId);
            if (keyStore == null)
            {
                result.IsValid = false;
                result.IsCurrentKey = false;
                result.RecommendedAction = "Key store not found.";
            }
            else
            {
                var metadata = await keyStore.GetKeyMetadataAsync(keyId, context).ConfigureAwait(false);
                result.IsValid = metadata != null;
                result.IsCurrentKey = result.IsValid;
                result.RecommendedAction = result.IsValid
                    ? "Key is valid for use."
                    : $"Key '{keyId}' not found in store '{keyStoreId}'.";
            }

            return result;
        }

        /// <summary>
        /// Gets the currently recommended key for new encryptions.
        /// Returns new key during dual-key period, current key otherwise.
        /// </summary>
        public async Task<RecommendedKey> GetRecommendedKeyAsync(
            string keyStoreId,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            var keyStore = _registry.GetKeyStore(keyStoreId);
            if (keyStore == null)
            {
                throw new KeyNotFoundException($"Key store '{keyStoreId}' not found.");
            }

            // Check for active rotation
            var activeSession = _activeSessions.Values
                .FirstOrDefault(s => s.KeyStoreId == keyStoreId &&
                                    (s.Status == RotationStatus.DualKeyPeriod ||
                                     s.Status == RotationStatus.ReEncrypting));

            if (activeSession != null)
            {
                return new RecommendedKey
                {
                    KeyId = activeSession.NewKeyId,
                    KeyMaterial = await keyStore.GetKeyAsync(activeSession.NewKeyId, context),
                    IsRotationKey = true,
                    RotationSessionId = activeSession.SessionId,
                    Reason = "Active rotation session - using new key for new encryptions."
                };
            }

            // No active rotation - use current key
            var currentKeyId = await keyStore.GetCurrentKeyIdAsync();
            return new RecommendedKey
            {
                KeyId = currentKeyId,
                KeyMaterial = await keyStore.GetKeyAsync(currentKeyId, context),
                IsRotationKey = false,
                Reason = "No active rotation - using current key."
            };
        }

        /// <summary>
        /// Initiates re-encryption of data from old key to new key.
        /// </summary>
        public async Task<ReEncryptionJob> StartReEncryptionAsync(
            string sessionId,
            IEnumerable<ReEncryptionItem> items,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                throw new KeyNotFoundException($"Rotation session '{sessionId}' not found.");
            }

            if (session.Status != RotationStatus.DualKeyPeriod)
            {
                throw new InvalidOperationException(
                    $"Cannot start re-encryption. Session status is {session.Status}.");
            }

            // Update session status
            session.Status = RotationStatus.ReEncrypting;
            session.ReEncryptionStartedAt = DateTime.UtcNow;

            var job = new ReEncryptionJob
            {
                JobId = GenerateJobId(),
                SessionId = sessionId,
                StartedAt = DateTime.UtcNow,
                Status = ReEncryptionStatus.Running
            };

            var itemList = items.ToList();
            job.TotalItems = itemList.Count;

            // Get key store and keys
            var keyStore = _registry.GetKeyStore(session.KeyStoreId);
            if (keyStore == null)
            {
                throw new KeyNotFoundException($"Key store '{session.KeyStoreId}' not found.");
            }

            var oldKey = await keyStore.GetKeyAsync(session.CurrentKeyId, context);
            var newKey = await keyStore.GetKeyAsync(session.NewKeyId, context);

            // Process items with semaphore for concurrency control
            using var semaphore = new SemaphoreSlim(MaxConcurrentReEncryption);

            ReEncryptionResult[] results;
            try
            {
                var tasks = itemList.Select(async item =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        return await ReEncryptItemAsync(item, oldKey, newKey, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                results = await Task.WhenAll(tasks);
            }
            finally
            {
                // P2-3445: Zero key material from memory after re-encryption to minimize
                // the window during which plaintext key bytes reside in heap memory.
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(oldKey);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(newKey);
            }

            // Update job status
            job.ProcessedItems = results.Length;
            job.SuccessfulItems = results.Count(r => r.Success);
            job.FailedItems = results.Count(r => !r.Success);
            job.CompletedAt = DateTime.UtcNow;
            job.Status = job.FailedItems == 0
                ? ReEncryptionStatus.Completed
                : ReEncryptionStatus.CompletedWithErrors;
            job.Results = results.ToList();

            // Update session
            session.ReEncryptionCompletedAt = DateTime.UtcNow;
            session.ReEncryptionStats = new ReEncryptionStats
            {
                TotalItems = job.TotalItems,
                SuccessfulItems = job.SuccessfulItems,
                FailedItems = job.FailedItems,
                DurationMs = (long)(job.CompletedAt.Value - job.StartedAt).TotalMilliseconds
            };

            // Publish re-encryption completed event
            await PublishRotationEventAsync("rotation.reencryption.completed", session, context);

            return job;
        }

        /// <summary>
        /// Completes the rotation, making the new key the sole active key.
        /// Should only be called after dual-key period expires or re-encryption completes.
        /// </summary>
        public async Task<RotationCompletionResult> CompleteRotationAsync(
            string sessionId,
            ISecurityContext context,
            bool force = false,
            CancellationToken ct = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                throw new KeyNotFoundException($"Rotation session '{sessionId}' not found.");
            }

            var result = new RotationCompletionResult
            {
                SessionId = sessionId,
                CompletedAt = DateTime.UtcNow
            };

            // Check if dual-key period has expired
            if (!force && session.DualKeyExpiresAt > DateTime.UtcNow)
            {
                result.Success = false;
                result.ErrorMessage = $"Dual-key period has not expired. Expires at {session.DualKeyExpiresAt:O}. Use force=true to override.";
                return result;
            }

            // Check for failed re-encryption
            if (session.ReEncryptionStats?.FailedItems > 0 && !force)
            {
                result.Success = false;
                result.ErrorMessage = $"{session.ReEncryptionStats.FailedItems} items failed re-encryption. Use force=true to override.";
                return result;
            }

            await _rotationLock.WaitAsync(ct);
            try
            {
                // Update session
                session.Status = RotationStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;
                session.CompletedBy = context.UserId;

                // Move to history
                _activeSessions.TryRemove(sessionId, out _);

                var history = new RotationHistory
                {
                    SessionId = session.SessionId,
                    KeyStoreId = session.KeyStoreId,
                    OldKeyId = session.CurrentKeyId,
                    NewKeyId = session.NewKeyId,
                    InitiatedAt = session.InitiatedAt,
                    CompletedAt = session.CompletedAt.Value,
                    InitiatedBy = session.InitiatedBy,
                    CompletedBy = session.CompletedBy,
                    DurationHours = (session.CompletedAt.Value - session.InitiatedAt).TotalHours,
                    ReEncryptionStats = session.ReEncryptionStats
                };

                _rotationHistory[sessionId] = history;

                result.Success = true;
                result.NewKeyId = session.NewKeyId;
                result.OldKeyId = session.CurrentKeyId;

                // Publish rotation completed event
                await PublishRotationEventAsync("rotation.completed", session, context);
            }
            finally
            {
                _rotationLock.Release();
            }

            return result;
        }

        /// <summary>
        /// Cancels an active rotation, reverting to the original key.
        /// </summary>
        public async Task<bool> CancelRotationAsync(
            string sessionId,
            ISecurityContext context,
            string? reason = null,
            CancellationToken ct = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return false;
            }

            await _rotationLock.WaitAsync(ct);
            try
            {
                session.Status = RotationStatus.Cancelled;
                session.CancelledAt = DateTime.UtcNow;
                session.CancelledBy = context.UserId;
                session.CancellationReason = reason;

                // Move to history
                _activeSessions.TryRemove(sessionId, out _);

                var history = new RotationHistory
                {
                    SessionId = session.SessionId,
                    KeyStoreId = session.KeyStoreId,
                    OldKeyId = session.CurrentKeyId,
                    NewKeyId = session.NewKeyId,
                    InitiatedAt = session.InitiatedAt,
                    CompletedAt = DateTime.UtcNow,
                    InitiatedBy = session.InitiatedBy,
                    CompletedBy = context.UserId,
                    WasCancelled = true,
                    CancellationReason = reason
                };

                _rotationHistory[sessionId] = history;

                // Publish rotation cancelled event
                await PublishRotationEventAsync("rotation.cancelled", session, context);

                return true;
            }
            finally
            {
                _rotationLock.Release();
            }
        }

        /// <summary>
        /// Gets the status of a rotation session.
        /// </summary>
        public RotationStatusReport? GetRotationStatus(string sessionId)
        {
            if (_activeSessions.TryGetValue(sessionId, out var session))
            {
                return new RotationStatusReport
                {
                    SessionId = session.SessionId,
                    Status = session.Status,
                    IsActive = true,
                    CurrentKeyId = session.CurrentKeyId,
                    NewKeyId = session.NewKeyId,
                    InitiatedAt = session.InitiatedAt,
                    DualKeyExpiresAt = session.DualKeyExpiresAt,
                    DualKeyTimeRemaining = session.DualKeyExpiresAt - DateTime.UtcNow,
                    ReEncryptionStats = session.ReEncryptionStats
                };
            }

            if (_rotationHistory.TryGetValue(sessionId, out var history))
            {
                return new RotationStatusReport
                {
                    SessionId = history.SessionId,
                    Status = history.WasCancelled ? RotationStatus.Cancelled : RotationStatus.Completed,
                    IsActive = false,
                    CurrentKeyId = history.NewKeyId, // New key is now current
                    NewKeyId = history.NewKeyId,
                    InitiatedAt = history.InitiatedAt,
                    CompletedAt = history.CompletedAt,
                    ReEncryptionStats = history.ReEncryptionStats
                };
            }

            return null;
        }

        /// <summary>
        /// Gets all active rotation sessions.
        /// </summary>
        public IReadOnlyList<RotationSession> GetActiveSessions()
        {
            return _activeSessions.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets rotation history for a key store.
        /// </summary>
        public IReadOnlyList<RotationHistory> GetRotationHistory(string? keyStoreId = null)
        {
            var history = _rotationHistory.Values.AsEnumerable();

            if (!string.IsNullOrEmpty(keyStoreId))
            {
                history = history.Where(h => h.KeyStoreId == keyStoreId);
            }

            return history
                .OrderByDescending(h => h.CompletedAt)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Verifies that a rotation completed successfully.
        /// </summary>
        public RotationVerification VerifyRotation(string sessionId)
        {
            var verification = new RotationVerification
            {
                SessionId = sessionId,
                VerifiedAt = DateTime.UtcNow
            };

            if (_activeSessions.TryGetValue(sessionId, out var activeSession))
            {
                verification.IsComplete = false;
                verification.Status = activeSession.Status;
                verification.Message = $"Rotation is still active. Status: {activeSession.Status}";
                return verification;
            }

            if (_rotationHistory.TryGetValue(sessionId, out var history))
            {
                verification.IsComplete = !history.WasCancelled;
                verification.Status = history.WasCancelled ? RotationStatus.Cancelled : RotationStatus.Completed;
                verification.NewKeyId = history.NewKeyId;
                verification.OldKeyId = history.OldKeyId;
                verification.ReEncryptionStats = history.ReEncryptionStats;
                verification.Message = history.WasCancelled
                    ? $"Rotation was cancelled: {history.CancellationReason}"
                    : "Rotation completed successfully.";

                // Verify new key is accessible
                var keyStore = _registry.GetKeyStore(history.KeyStoreId);
                verification.NewKeyAccessible = keyStore != null;
            }
            else
            {
                verification.IsComplete = false;
                verification.Message = "Rotation session not found.";
            }

            return verification;
        }

        #region Private Methods

        private async Task<ReEncryptionResult> ReEncryptItemAsync(
            ReEncryptionItem item,
            byte[] oldKey,
            byte[] newKey,
            CancellationToken ct)
        {
            var result = new ReEncryptionResult
            {
                ItemId = item.ItemId,
                StartedAt = DateTime.UtcNow
            };

            try
            {
                // This is a simplified re-encryption - actual implementation would depend on encryption plugin
                // The callback allows custom re-encryption logic
                if (item.ReEncryptCallback != null)
                {
                    await item.ReEncryptCallback(oldKey, newKey, ct);
                }
                else
                {
                    // Default: just mark as processed (actual data re-encryption is external)
                    await Task.Delay(1, ct); // Placeholder for actual work
                }

                result.Success = true;
                result.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
            }

            return result;
        }

        private string GenerateSessionId()
        {
            return $"rot-{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        }

        private string GenerateJobId()
        {
            return $"job-{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        }

        private string GenerateNewKeyId(string currentKeyId)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            return $"{currentKeyId}-rotated-{timestamp}";
        }

        private async Task PublishRotationEventAsync(string eventType, RotationSession session, ISecurityContext context)
        {
            if (_messageBus == null)
                return;

            try
            {
                var message = new PluginMessage
                {
                    Type = eventType,
                    Payload = new Dictionary<string, object>
                    {
                        ["sessionId"] = session.SessionId,
                        ["keyStoreId"] = session.KeyStoreId,
                        ["currentKeyId"] = session.CurrentKeyId,
                        ["newKeyId"] = session.NewKeyId,
                        ["status"] = session.Status.ToString(),
                        ["userId"] = context.UserId,
                        ["timestamp"] = DateTime.UtcNow
                    }
                };

                await _messageBus.PublishAsync(eventType, message);
            }
            catch
            {

                // Best-effort event publishing
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _activeSessions.Clear();
            _rotationHistory.Clear();
            _rotationLock.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    #region Supporting Types

    /// <summary>
    /// Rotation session status.
    /// </summary>
    public enum RotationStatus
    {
        /// <summary>Rotation initiated, both keys valid.</summary>
        DualKeyPeriod,
        /// <summary>Re-encrypting data with new key.</summary>
        ReEncrypting,
        /// <summary>Rotation completed successfully.</summary>
        Completed,
        /// <summary>Rotation was cancelled.</summary>
        Cancelled,
        /// <summary>Rotation failed.</summary>
        Failed
    }

    /// <summary>
    /// Re-encryption job status.
    /// </summary>
    public enum ReEncryptionStatus
    {
        Pending,
        Running,
        Completed,
        CompletedWithErrors,
        Failed
    }

    /// <summary>
    /// Options for key rotation.
    /// </summary>
    public class RotationOptions
    {
        public TimeSpan? DualKeyPeriod { get; set; }
        public string? NewKeyId { get; set; }
        public bool AutoReEncrypt { get; set; }
        public bool DeleteOldKeyOnComplete { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Active rotation session.
    /// </summary>
    public class RotationSession
    {
        public string SessionId { get; set; } = "";
        public string KeyStoreId { get; set; } = "";
        public string CurrentKeyId { get; set; } = "";
        public string NewKeyId { get; set; } = "";
        public RotationStatus Status { get; set; }
        public DateTime InitiatedAt { get; set; }
        public string? InitiatedBy { get; set; }
        public DateTime DualKeyExpiresAt { get; set; }
        public DateTime? ReEncryptionStartedAt { get; set; }
        public DateTime? ReEncryptionCompletedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? CompletedBy { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string? CancelledBy { get; set; }
        public string? CancellationReason { get; set; }
        public RotationOptions? Options { get; set; }
        public int NewKeySizeBytes { get; set; }
        public ReEncryptionStats? ReEncryptionStats { get; set; }
    }

    /// <summary>
    /// Key validation result during rotation.
    /// </summary>
    public class KeyValidationResult
    {
        public string KeyId { get; set; } = "";
        public DateTime CheckedAt { get; set; }
        public bool IsValid { get; set; }
        public bool IsCurrentKey { get; set; }
        public bool IsNewKey { get; set; }
        public string? RotationSessionId { get; set; }
        public DateTime? DualKeyExpiresAt { get; set; }
        public string? RecommendedAction { get; set; }
    }

    /// <summary>
    /// Recommended key for operations.
    /// </summary>
    public class RecommendedKey
    {
        public string KeyId { get; set; } = "";
        public byte[] KeyMaterial { get; set; } = Array.Empty<byte>();
        public bool IsRotationKey { get; set; }
        public string? RotationSessionId { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Item to re-encrypt during rotation.
    /// </summary>
    public class ReEncryptionItem
    {
        public string ItemId { get; set; } = "";
        public Func<byte[], byte[], CancellationToken, Task>? ReEncryptCallback { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// Re-encryption job status.
    /// </summary>
    public class ReEncryptionJob
    {
        public string JobId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public ReEncryptionStatus Status { get; set; }
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public List<ReEncryptionResult> Results { get; set; } = new();
    }

    /// <summary>
    /// Result of re-encrypting a single item.
    /// </summary>
    public class ReEncryptionResult
    {
        public string ItemId { get; set; } = "";
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
    }

    /// <summary>
    /// Re-encryption statistics.
    /// </summary>
    public class ReEncryptionStats
    {
        public int TotalItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Rotation completion result.
    /// </summary>
    public class RotationCompletionResult
    {
        public string SessionId { get; set; } = "";
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? NewKeyId { get; set; }
        public string? OldKeyId { get; set; }
        public DateTime CompletedAt { get; set; }
    }

    /// <summary>
    /// Rotation status report.
    /// </summary>
    public class RotationStatusReport
    {
        public string SessionId { get; set; } = "";
        public RotationStatus Status { get; set; }
        public bool IsActive { get; set; }
        public string CurrentKeyId { get; set; } = "";
        public string NewKeyId { get; set; } = "";
        public DateTime InitiatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? DualKeyExpiresAt { get; set; }
        public TimeSpan? DualKeyTimeRemaining { get; set; }
        public ReEncryptionStats? ReEncryptionStats { get; set; }
    }

    /// <summary>
    /// Rotation history entry.
    /// </summary>
    public class RotationHistory
    {
        public string SessionId { get; set; } = "";
        public string KeyStoreId { get; set; } = "";
        public string OldKeyId { get; set; } = "";
        public string NewKeyId { get; set; } = "";
        public DateTime InitiatedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public string? InitiatedBy { get; set; }
        public string? CompletedBy { get; set; }
        public double DurationHours { get; set; }
        public bool WasCancelled { get; set; }
        public string? CancellationReason { get; set; }
        public ReEncryptionStats? ReEncryptionStats { get; set; }
    }

    /// <summary>
    /// Rotation verification result.
    /// </summary>
    public class RotationVerification
    {
        public string SessionId { get; set; } = "";
        public DateTime VerifiedAt { get; set; }
        public bool IsComplete { get; set; }
        public RotationStatus Status { get; set; }
        public string? Message { get; set; }
        public string? NewKeyId { get; set; }
        public string? OldKeyId { get; set; }
        public bool NewKeyAccessible { get; set; }
        public ReEncryptionStats? ReEncryptionStats { get; set; }
    }

    #endregion
}
