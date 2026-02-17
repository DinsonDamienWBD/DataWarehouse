using System.Text.Json;

namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Append-only audit log for all configuration changes.
/// Logs who changed what, when, and the before/after values.
/// Each entry is a single JSON line in the audit file.
/// </summary>
public class ConfigurationAuditLog
{
    private readonly string _auditFilePath;
    private readonly object _lock = new();

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
    }

    /// <summary>Audit entry record capturing who changed what, when, and why.</summary>
    public record AuditEntry(
        DateTime Timestamp,
        string User,
        string SettingPath,
        string? OldValue,
        string? NewValue,
        string? Reason);

    /// <summary>
    /// Log a configuration change.
    /// Append-only: never modifies existing entries.
    /// </summary>
    public Task LogChangeAsync(string user, string settingPath, object? oldValue, object? newValue, string? reason = null)
    {
        var entry = new AuditEntry(
            DateTime.UtcNow,
            user ?? "System",
            settingPath,
            oldValue?.ToString(),
            newValue?.ToString(),
            reason);

        var json = JsonSerializer.Serialize(entry);

        lock (_lock)
        {
            File.AppendAllText(_auditFilePath, json + Environment.NewLine);
        }

        return Task.CompletedTask;
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
}
