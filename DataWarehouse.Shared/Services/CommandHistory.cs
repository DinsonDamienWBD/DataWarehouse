// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Represents a command history entry.
/// </summary>
public sealed record HistoryEntry
{
    /// <summary>Entry unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The command that was executed.</summary>
    public required string Command { get; init; }

    /// <summary>Command parameters.</summary>
    public Dictionary<string, object?>? Parameters { get; init; }

    /// <summary>When the command was executed.</summary>
    public DateTime ExecutedAt { get; init; }

    /// <summary>Whether the command succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Optional tags for categorization.</summary>
    public List<string> Tags { get; init; } = new();
}

/// <summary>
/// Manages command history with fuzzy search and persistence.
/// Enables features like: command history search, replay, analytics.
/// </summary>
public sealed class CommandHistory : IDisposable
{
    private readonly string _historyPath;
    private readonly List<HistoryEntry> _entries = new();
    private readonly object _lock = new();
    private readonly int _maxEntries;
    private bool _disposed;

    /// <summary>
    /// Creates a new CommandHistory instance.
    /// </summary>
    /// <param name="historyPath">Path to history file. Uses default if null.</param>
    /// <param name="maxEntries">Maximum entries to keep (default 10000).</param>
    public CommandHistory(string? historyPath = null, int maxEntries = 10000)
    {
        _maxEntries = maxEntries;
        _historyPath = historyPath ?? GetDefaultHistoryPath();
        LoadHistory();
    }

    /// <summary>
    /// Gets all history entries.
    /// </summary>
    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Gets the count of history entries.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Adds a command to history.
    /// </summary>
    public void Add(string command, Dictionary<string, object?>? parameters = null, bool success = true, double durationMs = 0)
    {
        var entry = new HistoryEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Command = command,
            Parameters = parameters,
            ExecutedAt = DateTime.UtcNow,
            Success = success,
            DurationMs = durationMs
        };

        lock (_lock)
        {
            _entries.Add(entry);

            // Trim if over max
            if (_entries.Count > _maxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - _maxEntries);
            }
        }

        _ = Task.Run(async () => await SaveHistoryAsync());
    }

    /// <summary>
    /// Searches history using fuzzy matching.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="maxResults">Maximum results to return.</param>
    /// <returns>Matching history entries.</returns>
    public IEnumerable<HistoryEntry> Search(string query, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            lock (_lock)
            {
                return _entries.TakeLast(maxResults).Reverse().ToList();
            }
        }

        var normalizedQuery = query.ToLowerInvariant();

        lock (_lock)
        {
            return _entries
                .Where(e => FuzzyMatch(e.Command.ToLowerInvariant(), normalizedQuery))
                .OrderByDescending(e => e.ExecutedAt)
                .Take(maxResults)
                .ToList();
        }
    }

    /// <summary>
    /// Gets recent commands.
    /// </summary>
    /// <param name="count">Number of recent commands to return.</param>
    /// <returns>Recent history entries.</returns>
    public IEnumerable<HistoryEntry> GetRecent(int count = 10)
    {
        lock (_lock)
        {
            return _entries.TakeLast(count).Reverse().ToList();
        }
    }

    /// <summary>
    /// Gets command frequency statistics.
    /// </summary>
    /// <returns>Dictionary of command to execution count.</returns>
    public Dictionary<string, int> GetCommandFrequency()
    {
        lock (_lock)
        {
            return _entries
                .GroupBy(e => e.Command)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>
    /// Gets commands executed in a time range.
    /// </summary>
    public IEnumerable<HistoryEntry> GetInRange(DateTime start, DateTime end)
    {
        lock (_lock)
        {
            return _entries
                .Where(e => e.ExecutedAt >= start && e.ExecutedAt <= end)
                .OrderByDescending(e => e.ExecutedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Clears all history.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        _ = Task.Run(async () => await SaveHistoryAsync());
    }

    /// <summary>
    /// Gets command suggestions based on history.
    /// </summary>
    /// <param name="prefix">Command prefix to match.</param>
    /// <param name="maxSuggestions">Maximum suggestions.</param>
    /// <returns>List of suggested commands.</returns>
    public IEnumerable<string> GetSuggestions(string prefix, int maxSuggestions = 5)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            // Return most frequent commands
            return GetCommandFrequency()
                .Take(maxSuggestions)
                .Select(kv => kv.Key);
        }

        var normalizedPrefix = prefix.ToLowerInvariant();

        lock (_lock)
        {
            return _entries
                .Select(e => e.Command)
                .Where(c => c.ToLowerInvariant().StartsWith(normalizedPrefix))
                .Distinct()
                .OrderByDescending(c => _entries.Count(e => e.Command == c))
                .Take(maxSuggestions);
        }
    }

    private static bool FuzzyMatch(string text, string pattern)
    {
        // Simple fuzzy match: all pattern chars must appear in order
        var patternIndex = 0;

        foreach (var c in text)
        {
            if (patternIndex < pattern.Length && c == pattern[patternIndex])
            {
                patternIndex++;
            }
        }

        return patternIndex == pattern.Length;
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json);

                if (entries != null)
                {
                    lock (_lock)
                    {
                        _entries.Clear();
                        _entries.AddRange(entries.TakeLast(_maxEntries));
                    }
                }
            }
        }
        catch
        {
            // Ignore load errors - start fresh
        }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            List<HistoryEntry> snapshot;
            lock (_lock)
            {
                snapshot = _entries.ToList();
            }

            var dir = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_historyPath, json);
        }
        catch (Exception ex)
        {
            // Log save errors but don't throw
            System.Diagnostics.Debug.WriteLine($"CommandHistory save failed: {ex.Message}");
        }
    }

    private static string GetDefaultHistoryPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "cli_history.json");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Save on dispose
        List<HistoryEntry> snapshot;
        lock (_lock)
        {
            snapshot = _entries.ToList();
        }

        try
        {
            var dir = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyPath, json);
        }
        catch
        {
            // Ignore save errors on dispose
        }
    }
}
