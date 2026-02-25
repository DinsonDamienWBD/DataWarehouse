// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Service for sealing blocks and shards to prevent ANY modifications.
/// Implements cryptographic seal tokens that cannot be forged.
/// </summary>
public interface ISealService
{
    /// <summary>
    /// Seals an entire block, preventing any modifications to all its shards.
    /// </summary>
    /// <param name="blockId">The block ID to seal.</param>
    /// <param name="reason">Reason for sealing (audit trail).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the seal operation.</returns>
    Task<SealResult> SealBlockAsync(Guid blockId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Seals a specific shard within a block.
    /// </summary>
    /// <param name="blockId">The block ID containing the shard.</param>
    /// <param name="shardIndex">The shard index to seal.</param>
    /// <param name="reason">Reason for sealing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the seal operation.</returns>
    Task<SealResult> SealShardAsync(Guid blockId, int shardIndex, string reason, CancellationToken ct = default);

    /// <summary>
    /// Seals all blocks within a date range for regulatory compliance.
    /// </summary>
    /// <param name="from">Start of the date range.</param>
    /// <param name="to">End of the date range.</param>
    /// <param name="reason">Reason for sealing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the seal operation with count of sealed blocks.</returns>
    Task<SealResult> SealRangeAsync(DateTime from, DateTime to, string reason, CancellationToken ct = default);

    /// <summary>
    /// Checks if a block is sealed.
    /// </summary>
    /// <param name="blockId">The block ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the block is sealed.</returns>
    Task<bool> IsSealedAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a specific shard is sealed.
    /// </summary>
    /// <param name="blockId">The block ID containing the shard.</param>
    /// <param name="shardIndex">The shard index to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the shard is sealed.</returns>
    Task<bool> IsShardSealedAsync(Guid blockId, int shardIndex, CancellationToken ct = default);

    /// <summary>
    /// Gets seal information for a block.
    /// </summary>
    /// <param name="blockId">The block ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Seal information or null if not sealed.</returns>
    Task<SealInfo?> GetSealInfoAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Gets all active seals in the system.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of all seal records.</returns>
    Task<IReadOnlyList<SealInfo>> GetAllSealsAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifies a seal token is valid and not forged.
    /// </summary>
    /// <param name="blockId">The block ID.</param>
    /// <param name="sealToken">The seal token to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the seal token is valid.</returns>
    Task<bool> VerifySealTokenAsync(Guid blockId, string sealToken, CancellationToken ct = default);
}

/// <summary>
/// Result of a seal operation.
/// </summary>
/// <param name="Success">Whether the seal operation succeeded.</param>
/// <param name="SealToken">Cryptographic seal token (cannot be forged).</param>
/// <param name="SealedAt">Timestamp when the seal was applied.</param>
/// <param name="Reason">Reason for sealing.</param>
/// <param name="Error">Error message if the operation failed.</param>
public record SealResult(
    bool Success,
    string SealToken,
    DateTime SealedAt,
    string Reason,
    string? Error = null)
{
    /// <summary>
    /// Number of items sealed (for range operations).
    /// </summary>
    public int SealedCount { get; init; } = 1;

    /// <summary>
    /// Creates a successful seal result.
    /// </summary>
    public static SealResult CreateSuccess(string sealToken, string reason, int sealedCount = 1)
    {
        return new SealResult(true, sealToken, DateTime.UtcNow, reason)
        {
            SealedCount = sealedCount
        };
    }

    /// <summary>
    /// Creates a failed seal result.
    /// </summary>
    public static SealResult CreateFailure(string error, string reason)
    {
        return new SealResult(false, string.Empty, DateTime.UtcNow, reason, error);
    }
}

/// <summary>
/// Information about a seal on a block or shard.
/// </summary>
/// <param name="BlockId">The sealed block ID.</param>
/// <param name="ShardIndex">The sealed shard index (null for entire block).</param>
/// <param name="SealedAt">When the seal was applied.</param>
/// <param name="Reason">Reason for sealing.</param>
/// <param name="SealToken">Cryptographic seal token.</param>
/// <param name="SealedBy">Principal who applied the seal.</param>
public record SealInfo(
    Guid BlockId,
    int? ShardIndex,
    DateTime SealedAt,
    string Reason,
    string SealToken,
    string SealedBy)
{
    /// <summary>
    /// Date range start if this is a range seal.
    /// </summary>
    public DateTime? RangeStart { get; init; }

    /// <summary>
    /// Date range end if this is a range seal.
    /// </summary>
    public DateTime? RangeEnd { get; init; }

    /// <summary>
    /// Whether this seal covers an entire block (all shards).
    /// </summary>
    public bool IsBlockSeal => ShardIndex == null;

    /// <summary>
    /// Whether this is a date range seal.
    /// </summary>
    public bool IsRangeSeal => RangeStart.HasValue && RangeEnd.HasValue;
}

/// <summary>
/// Implementation of the seal service with cryptographic token generation.
/// </summary>
public class SealService : ISealService
{
    private readonly BoundedDictionary<Guid, SealRecord> _blockSeals = new BoundedDictionary<Guid, SealRecord>(1000);
    private readonly BoundedDictionary<(Guid BlockId, int ShardIndex), SealRecord> _shardSeals = new BoundedDictionary<(Guid BlockId, int ShardIndex), SealRecord>(1000);
    private readonly List<RangeSealRecord> _rangeSeals = new();
    private readonly object _rangeSealLock = new();
    private readonly IStorageProvider? _persistentStorage;
    private readonly ILogger<SealService> _logger;
    private readonly byte[] _sealingKey;
    private readonly List<SealAuditEntry> _auditLog = new();
    private readonly object _auditLock = new();

    /// <summary>
    /// Creates a new seal service instance.
    /// </summary>
    /// <param name="persistentStorage">Optional persistent storage for seal backup.</param>
    /// <param name="sealingKeyBase64">Base64-encoded sealing key (32 bytes for HMAC-SHA256).</param>
    /// <param name="logger">Logger instance.</param>
    public SealService(
        IStorageProvider? persistentStorage,
        string? sealingKeyBase64,
        ILogger<SealService> logger)
    {
        _persistentStorage = persistentStorage;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Use provided key or generate a new one
        if (!string.IsNullOrEmpty(sealingKeyBase64))
        {
            _sealingKey = Convert.FromBase64String(sealingKeyBase64);
        }
        else
        {
            _sealingKey = RandomNumberGenerator.GetBytes(32);
            _logger.LogWarning(
                "No sealing key provided, generated ephemeral key. " +
                "Seals will not survive service restart without persistent storage.");
        }
    }

    /// <summary>
    /// Creates a new seal service with default configuration.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public SealService(ILogger<SealService> logger)
        : this(null, null, logger)
    {
    }

    /// <inheritdoc/>
    public async Task<SealResult> SealBlockAsync(Guid blockId, string reason, CancellationToken ct = default)
    {
        _logger.LogInformation("Sealing block {BlockId}: {Reason}", blockId, reason);

        try
        {
            // Check if already sealed
            if (_blockSeals.ContainsKey(blockId))
            {
                return SealResult.CreateFailure(
                    $"Block {blockId} is already sealed",
                    reason);
            }

            // Generate cryptographic seal token
            var sealToken = GenerateSealToken(blockId, null, reason);
            var sealedAt = DateTime.UtcNow;
            var sealedBy = GetCurrentPrincipal();

            var sealRecord = new SealRecord
            {
                BlockId = blockId,
                ShardIndex = null,
                SealToken = sealToken,
                SealedAt = sealedAt,
                Reason = reason,
                SealedBy = sealedBy
            };

            if (!_blockSeals.TryAdd(blockId, sealRecord))
            {
                return SealResult.CreateFailure(
                    $"Failed to add seal for block {blockId} (concurrent modification)",
                    reason);
            }

            // Persist to backup storage
            await PersistSealAsync(sealRecord, ct);

            // Log audit entry
            LogAuditEntry(SealOperation.SealBlock, blockId, null, reason, sealedBy);

            _logger.LogInformation(
                "Block {BlockId} sealed successfully. Token: {TokenPrefix}...",
                blockId, sealToken[..Math.Min(16, sealToken.Length)]);

            return SealResult.CreateSuccess(sealToken, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seal block {BlockId}", blockId);
            return SealResult.CreateFailure(ex.Message, reason);
        }
    }

    /// <inheritdoc/>
    public async Task<SealResult> SealShardAsync(Guid blockId, int shardIndex, string reason, CancellationToken ct = default)
    {
        _logger.LogInformation("Sealing shard {ShardIndex} of block {BlockId}: {Reason}",
            shardIndex, blockId, reason);

        try
        {
            // Check if block is already sealed (covers all shards)
            if (_blockSeals.ContainsKey(blockId))
            {
                return SealResult.CreateFailure(
                    $"Block {blockId} is already sealed (entire block)",
                    reason);
            }

            // Check if this specific shard is already sealed
            var key = (blockId, shardIndex);
            if (_shardSeals.ContainsKey(key))
            {
                return SealResult.CreateFailure(
                    $"Shard {shardIndex} of block {blockId} is already sealed",
                    reason);
            }

            // Generate cryptographic seal token
            var sealToken = GenerateSealToken(blockId, shardIndex, reason);
            var sealedAt = DateTime.UtcNow;
            var sealedBy = GetCurrentPrincipal();

            var sealRecord = new SealRecord
            {
                BlockId = blockId,
                ShardIndex = shardIndex,
                SealToken = sealToken,
                SealedAt = sealedAt,
                Reason = reason,
                SealedBy = sealedBy
            };

            if (!_shardSeals.TryAdd(key, sealRecord))
            {
                return SealResult.CreateFailure(
                    $"Failed to add seal for shard {shardIndex} of block {blockId}",
                    reason);
            }

            // Persist to backup storage
            await PersistSealAsync(sealRecord, ct);

            // Log audit entry
            LogAuditEntry(SealOperation.SealShard, blockId, shardIndex, reason, sealedBy);

            _logger.LogInformation(
                "Shard {ShardIndex} of block {BlockId} sealed successfully",
                shardIndex, blockId);

            return SealResult.CreateSuccess(sealToken, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seal shard {ShardIndex} of block {BlockId}",
                shardIndex, blockId);
            return SealResult.CreateFailure(ex.Message, reason);
        }
    }

    /// <inheritdoc/>
    public async Task<SealResult> SealRangeAsync(DateTime from, DateTime to, string reason, CancellationToken ct = default)
    {
        _logger.LogInformation("Sealing date range {From} to {To}: {Reason}", from, to, reason);

        try
        {
            if (from >= to)
            {
                return SealResult.CreateFailure("'from' date must be before 'to' date", reason);
            }

            var sealedBy = GetCurrentPrincipal();
            var sealToken = GenerateRangeSealToken(from, to, reason);
            var sealedAt = DateTime.UtcNow;

            var rangeSeal = new RangeSealRecord
            {
                RangeStart = from,
                RangeEnd = to,
                SealToken = sealToken,
                SealedAt = sealedAt,
                Reason = reason,
                SealedBy = sealedBy
            };

            lock (_rangeSealLock)
            {
                // Check for overlapping range seals
                var overlapping = _rangeSeals.FirstOrDefault(r =>
                    (from >= r.RangeStart && from <= r.RangeEnd) ||
                    (to >= r.RangeStart && to <= r.RangeEnd) ||
                    (from <= r.RangeStart && to >= r.RangeEnd));

                if (overlapping != null)
                {
                    return SealResult.CreateFailure(
                        $"Range overlaps with existing seal from {overlapping.RangeStart} to {overlapping.RangeEnd}",
                        reason);
                }

                _rangeSeals.Add(rangeSeal);
            }

            // Persist to backup storage
            await PersistRangeSealAsync(rangeSeal, ct);

            // Log audit entry
            LogAuditEntry(SealOperation.SealRange, Guid.Empty, null, reason, sealedBy,
                $"Range: {from:O} to {to:O}");

            _logger.LogInformation(
                "Date range {From} to {To} sealed successfully",
                from, to);

            // Note: SealedCount would need to be determined by querying actual blocks in range
            // For now, we return 1 to indicate the range seal itself
            return SealResult.CreateSuccess(sealToken, reason, sealedCount: 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seal date range {From} to {To}", from, to);
            return SealResult.CreateFailure(ex.Message, reason);
        }
    }

    /// <inheritdoc/>
    public Task<bool> IsSealedAsync(Guid blockId, CancellationToken ct = default)
    {
        // Check direct block seal
        if (_blockSeals.ContainsKey(blockId))
        {
            return Task.FromResult(true);
        }

        // Check range seals (would need block creation date to check properly)
        // For now, we only check direct seals
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task<bool> IsShardSealedAsync(Guid blockId, int shardIndex, CancellationToken ct = default)
    {
        // Check if entire block is sealed
        if (_blockSeals.ContainsKey(blockId))
        {
            return Task.FromResult(true);
        }

        // Check specific shard seal
        return Task.FromResult(_shardSeals.ContainsKey((blockId, shardIndex)));
    }

    /// <inheritdoc/>
    public Task<SealInfo?> GetSealInfoAsync(Guid blockId, CancellationToken ct = default)
    {
        if (_blockSeals.TryGetValue(blockId, out var sealRecord))
        {
            return Task.FromResult<SealInfo?>(new SealInfo(
                sealRecord.BlockId,
                sealRecord.ShardIndex,
                sealRecord.SealedAt,
                sealRecord.Reason,
                sealRecord.SealToken,
                sealRecord.SealedBy));
        }

        return Task.FromResult<SealInfo?>(null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SealInfo>> GetAllSealsAsync(CancellationToken ct = default)
    {
        var seals = new List<SealInfo>();

        // Add block seals
        foreach (var record in _blockSeals.Values)
        {
            seals.Add(new SealInfo(
                record.BlockId,
                record.ShardIndex,
                record.SealedAt,
                record.Reason,
                record.SealToken,
                record.SealedBy));
        }

        // Add shard seals
        foreach (var record in _shardSeals.Values)
        {
            seals.Add(new SealInfo(
                record.BlockId,
                record.ShardIndex,
                record.SealedAt,
                record.Reason,
                record.SealToken,
                record.SealedBy));
        }

        // Add range seals
        lock (_rangeSealLock)
        {
            foreach (var record in _rangeSeals)
            {
                seals.Add(new SealInfo(
                    Guid.Empty,
                    null,
                    record.SealedAt,
                    record.Reason,
                    record.SealToken,
                    record.SealedBy)
                {
                    RangeStart = record.RangeStart,
                    RangeEnd = record.RangeEnd
                });
            }
        }

        return Task.FromResult<IReadOnlyList<SealInfo>>(seals.OrderByDescending(s => s.SealedAt).ToList());
    }

    /// <inheritdoc/>
    public Task<bool> VerifySealTokenAsync(Guid blockId, string sealToken, CancellationToken ct = default)
    {
        // Look up the seal record
        if (_blockSeals.TryGetValue(blockId, out var record))
        {
            // Verify using constant-time comparison to prevent timing attacks
            return Task.FromResult(CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(record.SealToken),
                Encoding.UTF8.GetBytes(sealToken)));
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Checks if a block or shard is sealed for write operations.
    /// Throws BlockSealedException if sealed.
    /// </summary>
    /// <param name="blockId">The block ID to check.</param>
    /// <param name="shardIndex">Optional shard index to check.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ThrowIfSealedAsync(Guid blockId, int? shardIndex, CancellationToken ct = default)
    {
        // Check block seal
        if (_blockSeals.TryGetValue(blockId, out var blockSeal))
        {
            throw new BlockSealedException(blockId, blockSeal.SealedAt, blockSeal.Reason);
        }

        // Check shard seal if specified
        if (shardIndex.HasValue && _shardSeals.TryGetValue((blockId, shardIndex.Value), out var shardSeal))
        {
            throw new BlockSealedException(blockId, shardSeal.SealedAt, shardSeal.Reason)
            {
                ShardIndex = shardIndex.Value
            };
        }

        // Check range seals
        // Note: Would need block creation timestamp to properly check range seals
        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets the audit log for seal operations.
    /// </summary>
    /// <param name="from">Start of time range (optional).</param>
    /// <param name="to">End of time range (optional).</param>
    /// <returns>List of audit entries.</returns>
    public IReadOnlyList<SealAuditEntry> GetAuditLog(DateTime? from = null, DateTime? to = null)
    {
        lock (_auditLock)
        {
            var entries = _auditLog.AsEnumerable();

            if (from.HasValue)
            {
                entries = entries.Where(e => e.Timestamp >= from.Value);
            }

            if (to.HasValue)
            {
                entries = entries.Where(e => e.Timestamp <= to.Value);
            }

            return entries.OrderByDescending(e => e.Timestamp).ToList();
        }
    }

    /// <summary>
    /// Generates a cryptographic seal token that cannot be forged.
    /// </summary>
    private string GenerateSealToken(Guid blockId, int? shardIndex, string reason)
    {
        var timestamp = DateTime.UtcNow.Ticks;
        var nonce = RandomNumberGenerator.GetBytes(16);

        // Build data to sign
        var dataToSign = new StringBuilder();
        dataToSign.Append(blockId.ToString("N"));
        dataToSign.Append('|');
        dataToSign.Append(shardIndex?.ToString() ?? "BLOCK");
        dataToSign.Append('|');
        dataToSign.Append(timestamp);
        dataToSign.Append('|');
        dataToSign.Append(Convert.ToBase64String(nonce));
        dataToSign.Append('|');
        dataToSign.Append(reason);

        // Compute HMAC
        // HMAC computed inline; bus delegation to UltimateEncryption available for centralized policy enforcement
        using var hmac = new HMACSHA256(_sealingKey);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign.ToString()));

        // Build token: base64(nonce) + "." + base64(timestamp) + "." + base64(signature)
        return $"{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(BitConverter.GetBytes(timestamp))}.{Convert.ToBase64String(signature)}";
    }

    /// <summary>
    /// Generates a cryptographic seal token for date range seals.
    /// </summary>
    private string GenerateRangeSealToken(DateTime from, DateTime to, string reason)
    {
        var timestamp = DateTime.UtcNow.Ticks;
        var nonce = RandomNumberGenerator.GetBytes(16);

        var dataToSign = new StringBuilder();
        dataToSign.Append("RANGE");
        dataToSign.Append('|');
        dataToSign.Append(from.Ticks);
        dataToSign.Append('|');
        dataToSign.Append(to.Ticks);
        dataToSign.Append('|');
        dataToSign.Append(timestamp);
        dataToSign.Append('|');
        dataToSign.Append(Convert.ToBase64String(nonce));
        dataToSign.Append('|');
        dataToSign.Append(reason);

        // HMAC computed inline; bus delegation to UltimateEncryption available for centralized policy enforcement
        using var hmac = new HMACSHA256(_sealingKey);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign.ToString()));

        return $"R.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(BitConverter.GetBytes(timestamp))}.{Convert.ToBase64String(signature)}";
    }

    /// <summary>
    /// Persists a seal record to backup storage.
    /// </summary>
    private async Task PersistSealAsync(SealRecord record, CancellationToken ct)
    {
        if (_persistentStorage == null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(record);
            var bytes = Encoding.UTF8.GetBytes(json);
            var uri = new Uri($"seals://blocks/{record.BlockId}_{record.ShardIndex?.ToString() ?? "block"}.json");

            using var stream = new MemoryStream(bytes);
            await _persistentStorage.SaveAsync(uri, stream);

            _logger.LogDebug("Seal persisted for block {BlockId}", record.BlockId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist seal for block {BlockId}", record.BlockId);
        }
    }

    /// <summary>
    /// Persists a range seal record to backup storage.
    /// </summary>
    private async Task PersistRangeSealAsync(RangeSealRecord record, CancellationToken ct)
    {
        if (_persistentStorage == null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(record);
            var bytes = Encoding.UTF8.GetBytes(json);
            var id = Guid.NewGuid();
            var uri = new Uri($"seals://ranges/{id}.json");

            using var stream = new MemoryStream(bytes);
            await _persistentStorage.SaveAsync(uri, stream);

            _logger.LogDebug("Range seal persisted: {From} to {To}", record.RangeStart, record.RangeEnd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist range seal");
        }
    }

    /// <summary>
    /// Logs an audit entry for a seal operation.
    /// </summary>
    private void LogAuditEntry(
        SealOperation operation,
        Guid blockId,
        int? shardIndex,
        string reason,
        string principal,
        string? additionalInfo = null)
    {
        var entry = new SealAuditEntry
        {
            EntryId = Guid.NewGuid(),
            Operation = operation,
            BlockId = blockId,
            ShardIndex = shardIndex,
            Reason = reason,
            Principal = principal,
            Timestamp = DateTime.UtcNow,
            AdditionalInfo = additionalInfo
        };

        lock (_auditLock)
        {
            _auditLog.Add(entry);
        }

        _logger.LogDebug(
            "Seal audit: {Operation} by {Principal} on block {BlockId}",
            operation, principal, blockId);
    }

    /// <summary>
    /// Gets the current principal from thread context.
    /// </summary>
    private static string GetCurrentPrincipal()
    {
        try
        {
            var principal = System.Threading.Thread.CurrentPrincipal;
            if (principal?.Identity?.Name != null && !string.IsNullOrWhiteSpace(principal.Identity.Name))
            {
                return principal.Identity.Name;
            }
        }
        catch
        {

            // Ignore
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return "system";
    }

    /// <summary>
    /// Internal record for storing seal information.
    /// </summary>
    private class SealRecord
    {
        public required Guid BlockId { get; init; }
        public int? ShardIndex { get; init; }
        public required string SealToken { get; init; }
        public required DateTime SealedAt { get; init; }
        public required string Reason { get; init; }
        public required string SealedBy { get; init; }
    }

    /// <summary>
    /// Internal record for storing range seal information.
    /// </summary>
    private class RangeSealRecord
    {
        public required DateTime RangeStart { get; init; }
        public required DateTime RangeEnd { get; init; }
        public required string SealToken { get; init; }
        public required DateTime SealedAt { get; init; }
        public required string Reason { get; init; }
        public required string SealedBy { get; init; }
    }
}

/// <summary>
/// Types of seal operations for audit logging.
/// </summary>
public enum SealOperation
{
    /// <summary>Seal an entire block.</summary>
    SealBlock,

    /// <summary>Seal a specific shard.</summary>
    SealShard,

    /// <summary>Seal a date range.</summary>
    SealRange,

    /// <summary>Verify a seal token.</summary>
    VerifyToken
}

/// <summary>
/// Audit log entry for seal operations.
/// </summary>
public class SealAuditEntry
{
    /// <summary>Unique entry ID.</summary>
    public required Guid EntryId { get; init; }

    /// <summary>Type of operation.</summary>
    public required SealOperation Operation { get; init; }

    /// <summary>Block ID (empty for range seals).</summary>
    public required Guid BlockId { get; init; }

    /// <summary>Shard index if applicable.</summary>
    public int? ShardIndex { get; init; }

    /// <summary>Reason for the operation.</summary>
    public required string Reason { get; init; }

    /// <summary>Principal who performed the operation.</summary>
    public required string Principal { get; init; }

    /// <summary>When the operation occurred.</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>Additional information (e.g., date range).</summary>
    public string? AdditionalInfo { get; init; }
}
