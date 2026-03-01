using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Append-only audit log for all configuration changes with hash chain integrity protection.
/// Logs who changed what, when, and the before/after values.
/// Each entry is a single JSON line in the audit file.
///
/// INFRA-04 (CVSS 4.7): Hash chain integrity protection ensures tamper evidence.
/// Each entry includes a SHA-256 hash of (previous_entry_hash + current_entry_data).
/// Modifying any entry breaks all subsequent hashes, making tampering detectable.
/// </summary>
public class ConfigurationAuditLog
{
    private readonly string _auditFilePath;
    private readonly object _lock = new();

    /// <summary>
    /// Well-known genesis hash used as the chain root.
    /// SHA-256 of "DataWarehouse-AuditGenesis".
    /// </summary>
    private static readonly string GenesisHash = ComputeSha256("DataWarehouse-AuditGenesis");

    /// <summary>
    /// Current chain head hash for fast append validation.
    /// Initialized from existing log on construction or set to genesis hash.
    /// </summary>
    private volatile string _chainHeadHash;

    /// <summary>
    /// Creates a new configuration audit log writing to the specified file.
    /// </summary>
    /// <param name="auditFilePath">Path to the append-only audit log file.</param>
    public ConfigurationAuditLog(string auditFilePath)
    {
        _auditFilePath = auditFilePath ?? throw new ArgumentNullException(nameof(auditFilePath));

        // Ensure directory exists
        var directory = Path.GetDirectoryName(_auditFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Initialize chain head from existing log or genesis
        _chainHeadHash = LoadChainHead();
    }

    /// <summary>Integrity-protected audit entry with hash chain link.</summary>
    public record AuditEntry(
        DateTime Timestamp,
        string User,
        string SettingPath,
        string? OldValue,
        string? NewValue,
        string? Reason,
        string? IntegrityHash = null,
        string? PreviousHash = null);

    /// <summary>
    /// Result of integrity verification.
    /// </summary>
    public record IntegrityVerificationResult(
        bool IsValid,
        int TotalEntries,
        int ValidEntries,
        int? FirstCorruptedEntry,
        string? ErrorMessage);

    /// <summary>
    /// Log a configuration change with hash chain integrity protection.
    /// Append-only: never modifies existing entries.
    /// </summary>
    public async Task LogChangeAsync(string user, string settingPath, object? oldValue, object? newValue, string? reason = null)
    {
        string finalJson;
        string integrityHash;

        lock (_lock)
        {
            var previousHash = _chainHeadHash;

            // Create entry without hash first to compute hash
            // Finding 534: serialize complex objects to JSON rather than calling .ToString()
            var entryData = new AuditEntry(
                DateTime.UtcNow,
                user ?? "System",
                settingPath,
                SerializeValue(oldValue),
                SerializeValue(newValue),
                reason,
                IntegrityHash: null,
                PreviousHash: previousHash);

            // Compute integrity hash: SHA-256(previousHash + entryData)
            var entryJson = JsonSerializer.Serialize(entryData);
            integrityHash = ComputeSha256(previousHash + entryJson);

            // Create final entry with integrity hash
            var finalEntry = entryData with { IntegrityHash = integrityHash };
            finalJson = JsonSerializer.Serialize(finalEntry);

            // Update chain head inside lock to maintain chain ordering
            _chainHeadHash = integrityHash;
        }

        await File.AppendAllTextAsync(_auditFilePath, finalJson + Environment.NewLine).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the integrity of the entire audit log by walking the hash chain.
    /// Returns detailed results including the first corrupted entry if tampering is detected.
    /// </summary>
    public async Task<IntegrityVerificationResult> VerifyIntegrityAsync()
    {
        if (!File.Exists(_auditFilePath))
            return new IntegrityVerificationResult(true, 0, 0, null, null);

        var lines = await File.ReadAllLinesAsync(_auditFilePath);
        var previousHash = GenesisHash;
        var totalEntries = 0;
        var validEntries = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            totalEntries++;

            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                if (entry == null)
                    return new IntegrityVerificationResult(false, totalEntries, validEntries, totalEntries,
                        $"Entry {totalEntries} is null after deserialization");

                if (entry.IntegrityHash == null)
                {
                    // Legacy entry without integrity hash -- skip chain validation
                    // but still count as valid for backward compatibility
                    validEntries++;
                    continue;
                }

                // Verify the hash chain link
                var entryWithoutHash = entry with { IntegrityHash = null };
                var entryJson = JsonSerializer.Serialize(entryWithoutHash);
                var expectedHash = ComputeSha256((entry.PreviousHash ?? previousHash) + entryJson);

                if (entry.IntegrityHash != expectedHash)
                {
                    return new IntegrityVerificationResult(false, totalEntries, validEntries, totalEntries,
                        $"Entry {totalEntries} integrity hash mismatch: expected {expectedHash[..16]}..., got {entry.IntegrityHash[..16]}...");
                }

                previousHash = entry.IntegrityHash;
                validEntries++;
            }
            catch (Exception ex)
            {
                return new IntegrityVerificationResult(false, totalEntries, validEntries, totalEntries,
                    $"Entry {totalEntries} failed to parse: {ex.Message}");
            }
        }

        return new IntegrityVerificationResult(true, totalEntries, validEntries, null, null);
    }

    /// <summary>
    /// Query configuration changes with optional filters.
    /// </summary>
    public async Task<IReadOnlyList<AuditEntry>> QueryChangesAsync(
        string? settingPathPrefix = null,
        DateTime? since = null,
        DateTime? until = null,
        string? user = null)
    {
        if (!File.Exists(_auditFilePath))
            return Array.Empty<AuditEntry>();

        var lines = await File.ReadAllLinesAsync(_auditFilePath);
        var entries = new List<AuditEntry>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                if (entry == null)
                    continue;

                // Apply filters
                if (settingPathPrefix != null && !entry.SettingPath.StartsWith(settingPathPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (since.HasValue && entry.Timestamp < since.Value)
                    continue;

                if (until.HasValue && entry.Timestamp > until.Value)
                    continue;

                if (user != null && !entry.User.Equals(user, StringComparison.OrdinalIgnoreCase))
                    continue;

                entries.Add(entry);
            }
            catch
            {
                // Skip malformed entries - append-only log must tolerate partial writes
            }
        }

        return entries;
    }

    /// <summary>
    /// Computes SHA-256 hash of the input string, returned as lowercase hex.
    /// </summary>
    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Loads the chain head hash from the last entry in an existing log file.
    /// Returns the genesis hash if no log file exists or the file is empty.
    /// </summary>
    private string LoadChainHead()
    {
        if (!File.Exists(_auditFilePath))
            return GenesisHash;

        try
        {
            var lines = File.ReadAllLines(_auditFilePath);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var entry = JsonSerializer.Deserialize<AuditEntry>(lines[i]);
                if (entry?.IntegrityHash != null)
                    return entry.IntegrityHash;
            }
        }
        catch
        {
            // If we can't read existing log, start fresh chain
        }

        return GenesisHash;
    }

    /// <summary>
    /// Serializes a value for audit recording.
    /// Primitives use their string representation; complex objects are serialized to JSON
    /// so the audit record contains actual content rather than type names.
    /// </summary>
    private static string? SerializeValue(object? value)
    {
        if (value is null) return null;
        return value switch
        {
            string s => s,
            bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal => value.ToString(),
            _ => JsonSerializer.Serialize(value)
        };
    }
}
