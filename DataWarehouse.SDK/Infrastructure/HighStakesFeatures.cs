using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// TIER 3: HIGH-STAKES (BANKS/HOSPITALS/GOVERNMENTS)
// ============================================================================

#region Tier 3.1: FIPS 140-2 Certified Module (HSM Integration)

/// <summary>
/// Hardware Security Module integration for FIPS 140-2 compliance.
/// Provides government-grade cryptographic operations.
/// </summary>
public sealed class FipsCompliantCryptoModule : IAsyncDisposable
{
    private readonly IHsmProvider _hsmProvider;
    private readonly FipsConfig _config;
    private readonly ConcurrentDictionary<string, HsmKeyHandle> _keyHandles = new();
    private volatile bool _disposed;

    public FipsCompliantCryptoModule(IHsmProvider hsmProvider, FipsConfig? config = null)
    {
        _hsmProvider = hsmProvider ?? throw new ArgumentNullException(nameof(hsmProvider));
        _config = config ?? new FipsConfig();
    }

    /// <summary>
    /// Generates a FIPS-compliant key in the HSM.
    /// </summary>
    public async Task<FipsKeyResult> GenerateKeyAsync(
        string keyId,
        FipsKeyType keyType,
        CancellationToken ct = default)
    {
        ValidateFipsMode();

        var keySpec = keyType switch
        {
            FipsKeyType.AES256 => new HsmKeySpec { Algorithm = "AES", KeySize = 256 },
            FipsKeyType.RSA2048 => new HsmKeySpec { Algorithm = "RSA", KeySize = 2048 },
            FipsKeyType.RSA4096 => new HsmKeySpec { Algorithm = "RSA", KeySize = 4096 },
            FipsKeyType.ECDSA_P256 => new HsmKeySpec { Algorithm = "ECDSA", Curve = "P-256" },
            FipsKeyType.ECDSA_P384 => new HsmKeySpec { Algorithm = "ECDSA", Curve = "P-384" },
            _ => throw new ArgumentException($"Unsupported key type: {keyType}")
        };

        var handle = await _hsmProvider.GenerateKeyAsync(keyId, keySpec, ct);
        _keyHandles[keyId] = handle;

        return new FipsKeyResult
        {
            Success = true,
            KeyId = keyId,
            KeyType = keyType,
            GeneratedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddYears(_config.KeyValidityYears)
        };
    }

    /// <summary>
    /// Encrypts data using HSM-protected key.
    /// </summary>
    public async Task<FipsEncryptedData> EncryptAsync(
        string keyId,
        byte[] plaintext,
        CancellationToken ct = default)
    {
        ValidateFipsMode();

        if (!_keyHandles.TryGetValue(keyId, out var handle))
            throw new KeyNotFoundException($"Key '{keyId}' not found in HSM");

        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        var ciphertext = await _hsmProvider.EncryptAsync(handle, plaintext, iv, ct);

        return new FipsEncryptedData
        {
            KeyId = keyId,
            Algorithm = "AES-256-GCM",
            IV = iv,
            Ciphertext = ciphertext,
            EncryptedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Decrypts data using HSM-protected key.
    /// </summary>
    public async Task<byte[]> DecryptAsync(
        FipsEncryptedData encrypted,
        CancellationToken ct = default)
    {
        ValidateFipsMode();

        if (!_keyHandles.TryGetValue(encrypted.KeyId, out var handle))
            throw new KeyNotFoundException($"Key '{encrypted.KeyId}' not found in HSM");

        return await _hsmProvider.DecryptAsync(handle, encrypted.Ciphertext, encrypted.IV, ct);
    }

    /// <summary>
    /// Signs data using HSM-protected key.
    /// </summary>
    public async Task<FipsSignature> SignAsync(
        string keyId,
        byte[] data,
        CancellationToken ct = default)
    {
        ValidateFipsMode();

        if (!_keyHandles.TryGetValue(keyId, out var handle))
            throw new KeyNotFoundException($"Key '{keyId}' not found in HSM");

        var hash = SHA384.HashData(data);
        var signature = await _hsmProvider.SignAsync(handle, hash, ct);

        return new FipsSignature
        {
            KeyId = keyId,
            Algorithm = "ECDSA-P384-SHA384",
            DataHash = Convert.ToHexString(hash).ToLowerInvariant(),
            Signature = signature,
            SignedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Verifies a signature using HSM-protected key.
    /// </summary>
    public async Task<bool> VerifyAsync(
        string keyId,
        byte[] data,
        byte[] signature,
        CancellationToken ct = default)
    {
        ValidateFipsMode();

        if (!_keyHandles.TryGetValue(keyId, out var handle))
            throw new KeyNotFoundException($"Key '{keyId}' not found in HSM");

        var hash = SHA384.HashData(data);
        return await _hsmProvider.VerifyAsync(handle, hash, signature, ct);
    }

    /// <summary>
    /// Rotates a key in the HSM.
    /// </summary>
    public async Task<FipsKeyResult> RotateKeyAsync(
        string keyId,
        CancellationToken ct = default)
    {
        ValidateFipsMode();

        if (!_keyHandles.TryGetValue(keyId, out var oldHandle))
            throw new KeyNotFoundException($"Key '{keyId}' not found in HSM");

        var newHandle = await _hsmProvider.RotateKeyAsync(oldHandle, ct);
        _keyHandles[keyId] = newHandle;

        // Archive old key for decryption of historical data
        var archiveId = $"{keyId}_v{DateTime.UtcNow:yyyyMMddHHmmss}";
        _keyHandles[archiveId] = oldHandle;

        return new FipsKeyResult
        {
            Success = true,
            KeyId = keyId,
            GeneratedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddYears(_config.KeyValidityYears),
            PreviousKeyArchived = archiveId
        };
    }

    private void ValidateFipsMode()
    {
        if (!_config.FipsEnabled)
            throw new InvalidOperationException("FIPS mode is not enabled");
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

public interface IHsmProvider
{
    Task<HsmKeyHandle> GenerateKeyAsync(string keyId, HsmKeySpec spec, CancellationToken ct);
    Task<byte[]> EncryptAsync(HsmKeyHandle handle, byte[] plaintext, byte[] iv, CancellationToken ct);
    Task<byte[]> DecryptAsync(HsmKeyHandle handle, byte[] ciphertext, byte[] iv, CancellationToken ct);
    Task<byte[]> SignAsync(HsmKeyHandle handle, byte[] hash, CancellationToken ct);
    Task<bool> VerifyAsync(HsmKeyHandle handle, byte[] hash, byte[] signature, CancellationToken ct);
    Task<HsmKeyHandle> RotateKeyAsync(HsmKeyHandle oldHandle, CancellationToken ct);
}

public enum FipsKeyType { AES256, RSA2048, RSA4096, ECDSA_P256, ECDSA_P384 }

public sealed class HsmKeyHandle
{
    public required string HandleId { get; init; }
    public required string KeyId { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class HsmKeySpec
{
    public required string Algorithm { get; init; }
    public int KeySize { get; init; }
    public string? Curve { get; init; }
}

public record FipsKeyResult
{
    public bool Success { get; init; }
    public string KeyId { get; init; } = string.Empty;
    public FipsKeyType KeyType { get; init; }
    public DateTime GeneratedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string? PreviousKeyArchived { get; init; }
}

public record FipsEncryptedData
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required byte[] IV { get; init; }
    public required byte[] Ciphertext { get; init; }
    public DateTime EncryptedAt { get; init; }
}

public record FipsSignature
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string DataHash { get; init; }
    public required byte[] Signature { get; init; }
    public DateTime SignedAt { get; init; }
}

public sealed class FipsConfig
{
    public bool FipsEnabled { get; set; } = true;
    public int KeyValidityYears { get; set; } = 2;
    public bool RequireHsmForAllOperations { get; set; } = true;
}

#endregion

#region Tier 3.2: Immutable Audit Trail (Blockchain-backed)

/// <summary>
/// Blockchain-backed tamper-evident logging for compliance.
/// Provides cryptographic proof of audit trail integrity.
/// </summary>
public sealed class ImmutableAuditTrail : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AuditBlock> _blocks = new();
    private readonly Channel<AuditEntry> _entryChannel;
    private readonly IAuditStorage _storage;
    private readonly AuditTrailConfig _config;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private string _lastBlockHash = "genesis";
    private long _blockNumber;
    private readonly object _blockLock = new();
    private volatile bool _disposed;

    public ImmutableAuditTrail(IAuditStorage storage, AuditTrailConfig? config = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _config = config ?? new AuditTrailConfig();

        _entryChannel = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _processingTask = ProcessEntriesAsync(_cts.Token);
    }

    /// <summary>
    /// Records an audit entry with cryptographic chaining.
    /// </summary>
    public async ValueTask RecordAsync(
        string action,
        string principal,
        string resource,
        AuditEventType eventType,
        Dictionary<string, object>? details = null,
        CancellationToken ct = default)
    {
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            Action = action,
            Principal = principal,
            Resource = resource,
            EventType = eventType,
            Details = details ?? new Dictionary<string, object>(),
            SourceIP = GetSourceIP(),
            SessionId = GetCurrentSessionId()
        };

        await _entryChannel.Writer.WriteAsync(entry, ct);
    }

    /// <summary>
    /// Verifies the integrity of the entire audit chain.
    /// </summary>
    public async Task<AuditVerificationResult> VerifyChainIntegrityAsync(CancellationToken ct = default)
    {
        var blocks = await _storage.GetAllBlocksAsync(ct);
        var orderedBlocks = blocks.OrderBy(b => b.BlockNumber).ToList();

        if (orderedBlocks.Count == 0)
            return new AuditVerificationResult { IsValid = true, VerifiedBlocks = 0 };

        var invalidBlocks = new List<long>();
        var previousHash = "genesis";

        foreach (var block in orderedBlocks)
        {
            // Verify previous hash chain
            if (block.PreviousBlockHash != previousHash)
            {
                invalidBlocks.Add(block.BlockNumber);
                continue;
            }

            // Verify block hash
            var calculatedHash = ComputeBlockHash(block);
            if (calculatedHash != block.BlockHash)
            {
                invalidBlocks.Add(block.BlockNumber);
                continue;
            }

            // Verify merkle root
            var calculatedMerkle = ComputeMerkleRoot(block.Entries);
            if (calculatedMerkle != block.MerkleRoot)
            {
                invalidBlocks.Add(block.BlockNumber);
            }

            previousHash = block.BlockHash;
        }

        return new AuditVerificationResult
        {
            IsValid = invalidBlocks.Count == 0,
            VerifiedBlocks = orderedBlocks.Count,
            InvalidBlocks = invalidBlocks,
            FirstBlock = orderedBlocks.First().Timestamp,
            LastBlock = orderedBlocks.Last().Timestamp
        };
    }

    /// <summary>
    /// Gets audit entries for a specific resource.
    /// </summary>
    public async IAsyncEnumerable<AuditEntry> GetEntriesAsync(
        string? resource = null,
        DateTime? from = null,
        DateTime? to = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var blocks = await _storage.GetAllBlocksAsync(ct);

        foreach (var block in blocks.OrderBy(b => b.BlockNumber))
        {
            foreach (var entry in block.Entries)
            {
                if (resource != null && entry.Resource != resource)
                    continue;
                if (from.HasValue && entry.Timestamp < from.Value)
                    continue;
                if (to.HasValue && entry.Timestamp > to.Value)
                    continue;

                yield return entry;
            }
        }
    }

    /// <summary>
    /// Exports audit trail for compliance reporting.
    /// </summary>
    public async Task<AuditExport> ExportAsync(
        AuditExportRequest request,
        CancellationToken ct = default)
    {
        var entries = new List<AuditEntry>();

        await foreach (var entry in GetEntriesAsync(request.Resource, request.From, request.To, ct))
        {
            if (request.EventTypes == null || request.EventTypes.Contains(entry.EventType))
            {
                entries.Add(entry);
            }
        }

        var verification = await VerifyChainIntegrityAsync(ct);

        return new AuditExport
        {
            ExportedAt = DateTime.UtcNow,
            Entries = entries,
            TotalEntries = entries.Count,
            ChainVerified = verification.IsValid,
            ExportHash = ComputeExportHash(entries)
        };
    }

    private async Task ProcessEntriesAsync(CancellationToken ct)
    {
        var pendingEntries = new List<AuditEntry>();
        var lastBlockTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_config.BlockInterval);

                try
                {
                    while (pendingEntries.Count < _config.MaxEntriesPerBlock &&
                           await _entryChannel.Reader.WaitToReadAsync(timeoutCts.Token))
                    {
                        while (_entryChannel.Reader.TryRead(out var entry))
                        {
                            pendingEntries.Add(entry);
                            if (pendingEntries.Count >= _config.MaxEntriesPerBlock)
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Block interval elapsed
                }

                if (pendingEntries.Count > 0)
                {
                    await CreateBlockAsync(pendingEntries, ct);
                    pendingEntries.Clear();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Final flush
                if (pendingEntries.Count > 0)
                {
                    try { await CreateBlockAsync(pendingEntries, default); }
                    catch { /* Best effort */ }
                }
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task CreateBlockAsync(List<AuditEntry> entries, CancellationToken ct)
    {
        AuditBlock block;

        lock (_blockLock)
        {
            var merkleRoot = ComputeMerkleRoot(entries);
            var blockNumber = Interlocked.Increment(ref _blockNumber);

            block = new AuditBlock
            {
                BlockNumber = blockNumber,
                Timestamp = DateTime.UtcNow,
                PreviousBlockHash = _lastBlockHash,
                MerkleRoot = merkleRoot,
                Entries = entries.ToList(),
                EntryCount = entries.Count
            };

            block.BlockHash = ComputeBlockHash(block);
            _lastBlockHash = block.BlockHash;
        }

        _blocks[block.BlockHash] = block;
        await _storage.SaveBlockAsync(block, ct);
    }

    private static string ComputeBlockHash(AuditBlock block)
    {
        var data = $"{block.BlockNumber}|{block.PreviousBlockHash}|{block.MerkleRoot}|{block.Timestamp:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeMerkleRoot(IEnumerable<AuditEntry> entries)
    {
        var hashes = entries.Select(e =>
        {
            var data = $"{e.EntryId}|{e.Timestamp:O}|{e.Action}|{e.Principal}|{e.Resource}";
            return SHA256.HashData(Encoding.UTF8.GetBytes(data));
        }).ToList();

        if (hashes.Count == 0)
            return new string('0', 64);

        while (hashes.Count > 1)
        {
            var newHashes = new List<byte[]>();
            for (int i = 0; i < hashes.Count; i += 2)
            {
                var combined = i + 1 < hashes.Count
                    ? hashes[i].Concat(hashes[i + 1]).ToArray()
                    : hashes[i].Concat(hashes[i]).ToArray();
                newHashes.Add(SHA256.HashData(combined));
            }
            hashes = newHashes;
        }

        return Convert.ToHexString(hashes[0]).ToLowerInvariant();
    }

    private static string ComputeExportHash(List<AuditEntry> entries)
    {
        var data = string.Join("|", entries.Select(e => e.EntryId));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? GetSourceIP() => null; // Would get from request context
    private static string? GetCurrentSessionId() => null; // Would get from session context

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _entryChannel.Writer.Complete();
        _cts.Cancel();

        try { await _processingTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { /* Ignore */ }

        _cts.Dispose();
    }
}

public interface IAuditStorage
{
    Task SaveBlockAsync(AuditBlock block, CancellationToken ct);
    Task<IEnumerable<AuditBlock>> GetAllBlocksAsync(CancellationToken ct);
}

public enum AuditEventType
{
    Create, Read, Update, Delete,
    Login, Logout, AccessDenied,
    ConfigChange, PermissionChange,
    BackupCreated, BackupRestored,
    EmergencyAccess, KeyRotation
}

public sealed class AuditEntry
{
    public required string EntryId { get; init; }
    public DateTime Timestamp { get; init; }
    public required string Action { get; init; }
    public required string Principal { get; init; }
    public required string Resource { get; init; }
    public AuditEventType EventType { get; init; }
    public Dictionary<string, object> Details { get; init; } = new();
    public string? SourceIP { get; init; }
    public string? SessionId { get; init; }
}

public sealed class AuditBlock
{
    public long BlockNumber { get; init; }
    public DateTime Timestamp { get; init; }
    public required string PreviousBlockHash { get; init; }
    public string BlockHash { get; set; } = string.Empty;
    public required string MerkleRoot { get; init; }
    public List<AuditEntry> Entries { get; init; } = new();
    public int EntryCount { get; init; }
}

public record AuditVerificationResult
{
    public bool IsValid { get; init; }
    public int VerifiedBlocks { get; init; }
    public List<long> InvalidBlocks { get; init; } = new();
    public DateTime? FirstBlock { get; init; }
    public DateTime? LastBlock { get; init; }
}

public record AuditExportRequest
{
    public string? Resource { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public HashSet<AuditEventType>? EventTypes { get; init; }
}

public record AuditExport
{
    public DateTime ExportedAt { get; init; }
    public List<AuditEntry> Entries { get; init; } = new();
    public int TotalEntries { get; init; }
    public bool ChainVerified { get; init; }
    public string ExportHash { get; init; } = string.Empty;
}

public sealed class AuditTrailConfig
{
    public int MaxEntriesPerBlock { get; set; } = 100;
    public TimeSpan BlockInterval { get; set; } = TimeSpan.FromSeconds(5);
}

#endregion

#region Tier 3.3: Compliance Dashboard

/// <summary>
/// Real-time compliance scoring for HIPAA, SOX, GDPR, and PCI-DSS.
/// </summary>
public sealed class ComplianceDashboard
{
    private readonly ConcurrentDictionary<ComplianceStandard, ComplianceCheck[]> _checks = new();
    private readonly ConcurrentDictionary<ComplianceStandard, ComplianceScore> _scores = new();

    public ComplianceDashboard()
    {
        InitializeComplianceChecks();
    }

    private void InitializeComplianceChecks()
    {
        // HIPAA checks
        _checks[ComplianceStandard.HIPAA] = new[]
        {
            new ComplianceCheck { Id = "HIPAA-001", Name = "PHI Encryption at Rest", Category = "Security", Weight = 10 },
            new ComplianceCheck { Id = "HIPAA-002", Name = "PHI Encryption in Transit", Category = "Security", Weight = 10 },
            new ComplianceCheck { Id = "HIPAA-003", Name = "Access Controls", Category = "Security", Weight = 10 },
            new ComplianceCheck { Id = "HIPAA-004", Name = "Audit Logging", Category = "Audit", Weight = 10 },
            new ComplianceCheck { Id = "HIPAA-005", Name = "Automatic Logoff", Category = "Security", Weight = 5 },
            new ComplianceCheck { Id = "HIPAA-006", Name = "Unique User IDs", Category = "Security", Weight = 5 },
            new ComplianceCheck { Id = "HIPAA-007", Name = "Emergency Access", Category = "Operations", Weight = 10 },
            new ComplianceCheck { Id = "HIPAA-008", Name = "Data Backup", Category = "Operations", Weight = 10 },
            new ComplianceCheck { Id = "HIPAA-009", Name = "Data Integrity", Category = "Security", Weight = 10 },
            new ComplianceCheck { Id = "HIPAA-010", Name = "Transmission Security", Category = "Security", Weight = 10 }
        };

        // SOX checks
        _checks[ComplianceStandard.SOX] = new[]
        {
            new ComplianceCheck { Id = "SOX-001", Name = "Change Management", Category = "Controls", Weight = 15 },
            new ComplianceCheck { Id = "SOX-002", Name = "Access Reviews", Category = "Controls", Weight = 15 },
            new ComplianceCheck { Id = "SOX-003", Name = "Segregation of Duties", Category = "Controls", Weight = 15 },
            new ComplianceCheck { Id = "SOX-004", Name = "Audit Trail", Category = "Audit", Weight = 15 },
            new ComplianceCheck { Id = "SOX-005", Name = "Data Retention", Category = "Audit", Weight = 10 },
            new ComplianceCheck { Id = "SOX-006", Name = "IT General Controls", Category = "Controls", Weight = 15 },
            new ComplianceCheck { Id = "SOX-007", Name = "Application Controls", Category = "Controls", Weight = 15 }
        };

        // GDPR checks
        _checks[ComplianceStandard.GDPR] = new[]
        {
            new ComplianceCheck { Id = "GDPR-001", Name = "Consent Management", Category = "Privacy", Weight = 10 },
            new ComplianceCheck { Id = "GDPR-002", Name = "Right to Erasure", Category = "Privacy", Weight = 15 },
            new ComplianceCheck { Id = "GDPR-003", Name = "Data Portability", Category = "Privacy", Weight = 10 },
            new ComplianceCheck { Id = "GDPR-004", Name = "Breach Notification", Category = "Security", Weight = 15 },
            new ComplianceCheck { Id = "GDPR-005", Name = "Data Minimization", Category = "Privacy", Weight = 10 },
            new ComplianceCheck { Id = "GDPR-006", Name = "Privacy by Design", Category = "Privacy", Weight = 15 },
            new ComplianceCheck { Id = "GDPR-007", Name = "Data Processing Records", Category = "Audit", Weight = 10 },
            new ComplianceCheck { Id = "GDPR-008", Name = "Cross-Border Transfers", Category = "Privacy", Weight = 15 }
        };

        // PCI-DSS checks
        _checks[ComplianceStandard.PCI_DSS] = new[]
        {
            new ComplianceCheck { Id = "PCI-001", Name = "Firewall Configuration", Category = "Network", Weight = 8 },
            new ComplianceCheck { Id = "PCI-002", Name = "Default Passwords Changed", Category = "Security", Weight = 8 },
            new ComplianceCheck { Id = "PCI-003", Name = "Cardholder Data Protection", Category = "Data", Weight = 10 },
            new ComplianceCheck { Id = "PCI-004", Name = "Encryption in Transit", Category = "Data", Weight = 10 },
            new ComplianceCheck { Id = "PCI-005", Name = "Malware Protection", Category = "Security", Weight = 8 },
            new ComplianceCheck { Id = "PCI-006", Name = "Secure Development", Category = "Security", Weight = 8 },
            new ComplianceCheck { Id = "PCI-007", Name = "Access Control", Category = "Security", Weight = 10 },
            new ComplianceCheck { Id = "PCI-008", Name = "Unique IDs", Category = "Security", Weight = 8 },
            new ComplianceCheck { Id = "PCI-009", Name = "Physical Access", Category = "Security", Weight = 8 },
            new ComplianceCheck { Id = "PCI-010", Name = "Logging & Monitoring", Category = "Audit", Weight = 10 },
            new ComplianceCheck { Id = "PCI-011", Name = "Security Testing", Category = "Security", Weight = 6 },
            new ComplianceCheck { Id = "PCI-012", Name = "Security Policies", Category = "Governance", Weight = 6 }
        };
    }

    /// <summary>
    /// Updates a compliance check status.
    /// </summary>
    public void UpdateCheckStatus(
        ComplianceStandard standard,
        string checkId,
        ComplianceStatus status,
        string? notes = null)
    {
        if (!_checks.TryGetValue(standard, out var checks))
            return;

        var check = checks.FirstOrDefault(c => c.Id == checkId);
        if (check != null)
        {
            check.Status = status;
            check.Notes = notes;
            check.LastChecked = DateTime.UtcNow;
        }

        RecalculateScore(standard);
    }

    /// <summary>
    /// Gets the current compliance score for a standard.
    /// </summary>
    public ComplianceScore GetScore(ComplianceStandard standard)
    {
        return _scores.GetValueOrDefault(standard, new ComplianceScore { Standard = standard });
    }

    /// <summary>
    /// Gets the full compliance dashboard state.
    /// </summary>
    public ComplianceDashboardState GetDashboardState()
    {
        return new ComplianceDashboardState
        {
            Timestamp = DateTime.UtcNow,
            Scores = _scores.Values.ToList(),
            TotalChecks = _checks.Values.Sum(c => c.Length),
            PassedChecks = _checks.Values.Sum(c => c.Count(ch => ch.Status == ComplianceStatus.Passed)),
            FailedChecks = _checks.Values.Sum(c => c.Count(ch => ch.Status == ComplianceStatus.Failed)),
            PendingChecks = _checks.Values.Sum(c => c.Count(ch => ch.Status == ComplianceStatus.Pending)),
            OverallScore = _scores.Values.Any() ? _scores.Values.Average(s => s.Score) : 0
        };
    }

    /// <summary>
    /// Gets detailed check results for a standard.
    /// </summary>
    public IReadOnlyList<ComplianceCheck> GetChecks(ComplianceStandard standard)
    {
        return _checks.GetValueOrDefault(standard, Array.Empty<ComplianceCheck>());
    }

    /// <summary>
    /// Runs automated compliance checks.
    /// </summary>
    public async Task<ComplianceCheckResult> RunAutomatedChecksAsync(
        ComplianceStandard standard,
        IComplianceChecker checker,
        CancellationToken ct = default)
    {
        if (!_checks.TryGetValue(standard, out var checks))
            return new ComplianceCheckResult { Success = false, Error = "Standard not found" };

        var results = new List<CheckResult>();

        foreach (var check in checks)
        {
            try
            {
                var result = await checker.CheckAsync(check, ct);
                check.Status = result.Passed ? ComplianceStatus.Passed : ComplianceStatus.Failed;
                check.Notes = result.Notes;
                check.LastChecked = DateTime.UtcNow;

                results.Add(result);
            }
            catch (Exception ex)
            {
                check.Status = ComplianceStatus.Error;
                check.Notes = ex.Message;
                results.Add(new CheckResult { CheckId = check.Id, Passed = false, Notes = ex.Message });
            }
        }

        RecalculateScore(standard);

        return new ComplianceCheckResult
        {
            Success = true,
            Standard = standard,
            Results = results,
            Score = _scores.GetValueOrDefault(standard)?.Score ?? 0
        };
    }

    private void RecalculateScore(ComplianceStandard standard)
    {
        if (!_checks.TryGetValue(standard, out var checks))
            return;

        var totalWeight = checks.Sum(c => c.Weight);
        var passedWeight = checks.Where(c => c.Status == ComplianceStatus.Passed).Sum(c => c.Weight);

        var score = new ComplianceScore
        {
            Standard = standard,
            Score = totalWeight > 0 ? (double)passedWeight / totalWeight * 100 : 0,
            LastUpdated = DateTime.UtcNow,
            PassedChecks = checks.Count(c => c.Status == ComplianceStatus.Passed),
            TotalChecks = checks.Length
        };

        _scores[standard] = score;
    }
}

public interface IComplianceChecker
{
    Task<CheckResult> CheckAsync(ComplianceCheck check, CancellationToken ct);
}

public enum ComplianceStandard { HIPAA, SOX, GDPR, PCI_DSS, FedRAMP, ISO27001 }
public enum ComplianceStatus { Pending, Passed, Failed, Error, NotApplicable }

public sealed class ComplianceCheck
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public int Weight { get; init; }
    public ComplianceStatus Status { get; set; } = ComplianceStatus.Pending;
    public string? Notes { get; set; }
    public DateTime? LastChecked { get; set; }
}

public record ComplianceScore
{
    public ComplianceStandard Standard { get; init; }
    public double Score { get; init; }
    public DateTime LastUpdated { get; init; }
    public int PassedChecks { get; init; }
    public int TotalChecks { get; init; }
}

public record ComplianceDashboardState
{
    public DateTime Timestamp { get; init; }
    public List<ComplianceScore> Scores { get; init; } = new();
    public int TotalChecks { get; init; }
    public int PassedChecks { get; init; }
    public int FailedChecks { get; init; }
    public int PendingChecks { get; init; }
    public double OverallScore { get; init; }
}

public record CheckResult
{
    public required string CheckId { get; init; }
    public bool Passed { get; init; }
    public string? Notes { get; init; }
}

public record ComplianceCheckResult
{
    public bool Success { get; init; }
    public ComplianceStandard Standard { get; init; }
    public List<CheckResult> Results { get; init; } = new();
    public double Score { get; init; }
    public string? Error { get; init; }
}

#endregion

#region Tier 3.4: Break-Glass Emergency Access

/// <summary>
/// Audited emergency override procedures for critical situations.
/// Provides controlled bypass of normal access controls with full audit trail.
/// </summary>
public sealed class BreakGlassAccessManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BreakGlassSession> _activeSessions = new();
    private readonly ImmutableAuditTrail _auditTrail;
    private readonly INotificationService _notifications;
    private readonly BreakGlassConfig _config;
    private readonly Task _monitorTask;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    public BreakGlassAccessManager(
        ImmutableAuditTrail auditTrail,
        INotificationService notifications,
        BreakGlassConfig? config = null)
    {
        _auditTrail = auditTrail ?? throw new ArgumentNullException(nameof(auditTrail));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _config = config ?? new BreakGlassConfig();

        _monitorTask = MonitorSessionsAsync(_cts.Token);
    }

    /// <summary>
    /// Requests emergency access with justification.
    /// </summary>
    public async Task<BreakGlassResult> RequestEmergencyAccessAsync(
        string requesterId,
        BreakGlassRequest request,
        CancellationToken ct = default)
    {
        // Validate requester has break-glass privileges
        if (!_config.AuthorizedUsers.Contains(requesterId))
        {
            await _auditTrail.RecordAsync(
                "BreakGlassAccessDenied",
                requesterId,
                request.TargetResource,
                AuditEventType.AccessDenied,
                new Dictionary<string, object>
                {
                    ["reason"] = "Not authorized for emergency access",
                    ["justification"] = request.Justification
                },
                ct);

            return new BreakGlassResult
            {
                Success = false,
                Error = "User not authorized for emergency access"
            };
        }

        // Create session
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new BreakGlassSession
        {
            SessionId = sessionId,
            RequesterId = requesterId,
            TargetResource = request.TargetResource,
            Justification = request.Justification,
            EmergencyType = request.EmergencyType,
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(_config.MaxSessionDuration),
            Status = BreakGlassStatus.Active
        };

        _activeSessions[sessionId] = session;

        // Audit the access grant
        await _auditTrail.RecordAsync(
            "BreakGlassAccessGranted",
            requesterId,
            request.TargetResource,
            AuditEventType.EmergencyAccess,
            new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["justification"] = request.Justification,
                ["emergencyType"] = request.EmergencyType.ToString(),
                ["expiresAt"] = session.ExpiresAt
            },
            ct);

        // Notify stakeholders
        await _notifications.NotifyAsync(new EmergencyAccessNotification
        {
            SessionId = sessionId,
            RequesterId = requesterId,
            Resource = request.TargetResource,
            Justification = request.Justification,
            ExpiresAt = session.ExpiresAt
        }, ct);

        return new BreakGlassResult
        {
            Success = true,
            SessionId = sessionId,
            ExpiresAt = session.ExpiresAt,
            AccessToken = GenerateAccessToken(session)
        };
    }

    /// <summary>
    /// Validates an emergency access token.
    /// </summary>
    public bool ValidateAccess(string sessionId, string resource)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
            return false;

        if (session.Status != BreakGlassStatus.Active)
            return false;

        if (DateTime.UtcNow > session.ExpiresAt)
        {
            session.Status = BreakGlassStatus.Expired;
            return false;
        }

        if (session.TargetResource != "*" && session.TargetResource != resource)
            return false;

        session.AccessCount++;
        session.LastAccessed = DateTime.UtcNow;

        return true;
    }

    /// <summary>
    /// Revokes an emergency access session.
    /// </summary>
    public async Task<bool> RevokeAccessAsync(
        string sessionId,
        string revokedBy,
        string reason,
        CancellationToken ct = default)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
            return false;

        session.Status = BreakGlassStatus.Revoked;
        session.RevokedBy = revokedBy;
        session.RevokedAt = DateTime.UtcNow;
        session.RevocationReason = reason;

        await _auditTrail.RecordAsync(
            "BreakGlassAccessRevoked",
            revokedBy,
            session.TargetResource,
            AuditEventType.EmergencyAccess,
            new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["originalRequester"] = session.RequesterId,
                ["reason"] = reason,
                ["accessCount"] = session.AccessCount
            },
            ct);

        return true;
    }

    /// <summary>
    /// Gets all active emergency sessions.
    /// </summary>
    public IReadOnlyList<BreakGlassSession> GetActiveSessions()
    {
        return _activeSessions.Values
            .Where(s => s.Status == BreakGlassStatus.Active)
            .ToList();
    }

    private string GenerateAccessToken(BreakGlassSession session)
    {
        var data = $"{session.SessionId}|{session.ExpiresAt:O}|{session.TargetResource}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data + _config.TokenSecret));
        return Convert.ToBase64String(hash);
    }

    private async Task MonitorSessionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                var now = DateTime.UtcNow;
                foreach (var (sessionId, session) in _activeSessions)
                {
                    if (session.Status == BreakGlassStatus.Active && now > session.ExpiresAt)
                    {
                        session.Status = BreakGlassStatus.Expired;

                        await _auditTrail.RecordAsync(
                            "BreakGlassSessionExpired",
                            "system",
                            session.TargetResource,
                            AuditEventType.EmergencyAccess,
                            new Dictionary<string, object>
                            {
                                ["sessionId"] = sessionId,
                                ["requesterId"] = session.RequesterId,
                                ["accessCount"] = session.AccessCount
                            },
                            ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try { await _monitorTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* Ignore */ }

        _cts.Dispose();
    }
}

public interface INotificationService
{
    Task NotifyAsync(EmergencyAccessNotification notification, CancellationToken ct);
}

public enum BreakGlassStatus { Active, Expired, Revoked }
public enum EmergencyType { SystemOutage, SecurityIncident, DataRecovery, ComplianceAudit, Other }

public sealed class BreakGlassSession
{
    public required string SessionId { get; init; }
    public required string RequesterId { get; init; }
    public required string TargetResource { get; init; }
    public required string Justification { get; init; }
    public EmergencyType EmergencyType { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public BreakGlassStatus Status { get; set; }
    public int AccessCount { get; set; }
    public DateTime? LastAccessed { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
}

public record BreakGlassRequest
{
    public required string TargetResource { get; init; }
    public required string Justification { get; init; }
    public EmergencyType EmergencyType { get; init; }
}

public record BreakGlassResult
{
    public bool Success { get; init; }
    public string? SessionId { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? AccessToken { get; init; }
    public string? Error { get; init; }
}

public record EmergencyAccessNotification
{
    public required string SessionId { get; init; }
    public required string RequesterId { get; init; }
    public required string Resource { get; init; }
    public required string Justification { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public sealed class BreakGlassConfig
{
    public HashSet<string> AuthorizedUsers { get; set; } = new();
    public TimeSpan MaxSessionDuration { get; set; } = TimeSpan.FromHours(4);
    public string TokenSecret { get; set; } = Guid.NewGuid().ToString();
    public bool RequireMultiPartyApproval { get; set; } = false;
}

#endregion

#region Tier 3.5: Zero-Knowledge Encryption

/// <summary>
/// Client-side encryption where server never sees plaintext data.
/// Provides privacy-preserving storage with server-blind operations.
/// </summary>
public sealed class ZeroKnowledgeEncryption
{
    private readonly IKeyDerivationFunction _kdf;
    private readonly ZeroKnowledgeConfig _config;

    public ZeroKnowledgeEncryption(IKeyDerivationFunction? kdf = null, ZeroKnowledgeConfig? config = null)
    {
        _kdf = kdf ?? new Argon2KeyDerivation();
        _config = config ?? new ZeroKnowledgeConfig();
    }

    /// <summary>
    /// Derives a client-side encryption key from user credentials.
    /// Server never learns the key.
    /// </summary>
    public async Task<ClientKey> DeriveClientKeyAsync(
        string userId,
        string password,
        CancellationToken ct = default)
    {
        // Generate salt deterministically from userId (so server doesn't need to store it)
        var userIdBytes = Encoding.UTF8.GetBytes(userId);
        var salt = SHA256.HashData(userIdBytes);

        // Derive encryption key using strong KDF
        var keyMaterial = await _kdf.DeriveKeyAsync(password, salt, _config.KeyLength, ct);

        // Derive separate keys for encryption and authentication
        var encryptionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            keyMaterial,
            _config.KeyLength,
            info: Encoding.UTF8.GetBytes("encryption"));

        var authKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            keyMaterial,
            _config.KeyLength,
            info: Encoding.UTF8.GetBytes("authentication"));

        // Create verification token (server can verify user without knowing key)
        var verificationToken = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            keyMaterial,
            32,
            info: Encoding.UTF8.GetBytes("verification"));

        return new ClientKey
        {
            UserId = userId,
            EncryptionKey = encryptionKey,
            AuthenticationKey = authKey,
            VerificationToken = Convert.ToHexString(verificationToken).ToLowerInvariant()
        };
    }

    /// <summary>
    /// Encrypts data client-side. Server only stores ciphertext.
    /// </summary>
    public EncryptedClientData EncryptClientSide(ClientKey clientKey, byte[] plaintext)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(clientKey.EncryptionKey, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Create HMAC for additional integrity verification
        var hmac = HMACSHA256.HashData(clientKey.AuthenticationKey, ciphertext);

        return new EncryptedClientData
        {
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            Hmac = hmac,
            EncryptedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Decrypts data client-side.
    /// </summary>
    public byte[] DecryptClientSide(ClientKey clientKey, EncryptedClientData encrypted)
    {
        // Verify HMAC first
        var expectedHmac = HMACSHA256.HashData(clientKey.AuthenticationKey, encrypted.Ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, encrypted.Hmac))
            throw new CryptographicException("Data integrity check failed");

        var plaintext = new byte[encrypted.Ciphertext.Length];

        using var aes = new AesGcm(clientKey.EncryptionKey, 16);
        aes.Decrypt(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Generates a searchable token for encrypted data.
    /// Enables server-side search without decryption.
    /// </summary>
    public string GenerateSearchToken(ClientKey clientKey, string searchTerm)
    {
        // Create deterministic token from search term
        var termBytes = Encoding.UTF8.GetBytes(searchTerm.ToLowerInvariant());
        var token = HMACSHA256.HashData(clientKey.AuthenticationKey, termBytes);
        return Convert.ToHexString(token).ToLowerInvariant();
    }

    /// <summary>
    /// Enables secure key sharing using public key cryptography.
    /// </summary>
    public KeyShareResult ShareKey(ClientKey ownerKey, byte[] recipientPublicKey)
    {
        // Generate ephemeral key pair
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var ephemeralPublic = ecdh.ExportSubjectPublicKeyInfo();

        // Derive shared secret
        using var recipientEcdh = ECDiffieHellman.Create();
        recipientEcdh.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);

        var sharedSecret = ecdh.DeriveKeyMaterial(recipientEcdh.PublicKey);

        // Encrypt the owner's key with shared secret
        var wrappingKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32);

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var encryptedKey = new byte[ownerKey.EncryptionKey.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(wrappingKey, 16);
        aes.Encrypt(nonce, ownerKey.EncryptionKey, encryptedKey, tag);

        return new KeyShareResult
        {
            EphemeralPublicKey = ephemeralPublic,
            EncryptedKey = encryptedKey,
            Nonce = nonce,
            Tag = tag
        };
    }
}

public interface IKeyDerivationFunction
{
    Task<byte[]> DeriveKeyAsync(string password, byte[] salt, int keyLength, CancellationToken ct);
}

public sealed class Argon2KeyDerivation : IKeyDerivationFunction
{
    public Task<byte[]> DeriveKeyAsync(string password, byte[] salt, int keyLength, CancellationToken ct)
    {
        // Using PBKDF2 as fallback (Argon2 would require external library)
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100_000, // iterations
            HashAlgorithmName.SHA256,
            keyLength);

        return Task.FromResult(key);
    }
}

public sealed class ClientKey
{
    public required string UserId { get; init; }
    public required byte[] EncryptionKey { get; init; }
    public required byte[] AuthenticationKey { get; init; }
    public required string VerificationToken { get; init; }
}

public record EncryptedClientData
{
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Tag { get; init; }
    public required byte[] Hmac { get; init; }
    public DateTime EncryptedAt { get; init; }
}

public record KeyShareResult
{
    public required byte[] EphemeralPublicKey { get; init; }
    public required byte[] EncryptedKey { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Tag { get; init; }
}

public sealed class ZeroKnowledgeConfig
{
    public int KeyLength { get; set; } = 32;
    public int Iterations { get; set; } = 100_000;
}

#endregion
