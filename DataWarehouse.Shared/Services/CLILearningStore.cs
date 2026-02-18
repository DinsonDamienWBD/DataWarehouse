// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Represents a learned pattern from user corrections or successful interpretations.
/// </summary>
public sealed record LearnedPattern
{
    /// <summary>Unique pattern identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The input phrase or pattern.</summary>
    public required string InputPhrase { get; init; }

    /// <summary>The normalized/canonical form of the phrase.</summary>
    public required string NormalizedPhrase { get; init; }

    /// <summary>The correct command for this phrase.</summary>
    public required string CommandName { get; init; }

    /// <summary>Parameters extracted from the phrase.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>How many times this pattern has been used successfully.</summary>
    public int SuccessCount { get; set; }

    /// <summary>How many times this pattern failed after being learned.</summary>
    public int FailureCount { get; set; }

    /// <summary>When this pattern was first learned.</summary>
    public DateTime LearnedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When this pattern was last used.</summary>
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this was from a user correction.</summary>
    public bool IsCorrection { get; init; }

    /// <summary>Original misinterpretation (if this was a correction).</summary>
    public string? OriginalMisinterpretation { get; init; }

    /// <summary>Confidence score based on success rate.</summary>
    public double Confidence => SuccessCount + FailureCount == 0
        ? 0.5
        : (double)SuccessCount / (SuccessCount + FailureCount);
}

/// <summary>
/// Represents a synonym mapping for semantic understanding.
/// </summary>
public sealed record SynonymMapping
{
    /// <summary>The synonym word or phrase.</summary>
    public required string Synonym { get; init; }

    /// <summary>The canonical term it maps to.</summary>
    public required string CanonicalTerm { get; init; }

    /// <summary>Context where this synonym applies (e.g., "backup", "storage").</summary>
    public string? Context { get; init; }

    /// <summary>Confidence in this mapping.</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>How many times this mapping was used.</summary>
    public int UseCount { get; set; }
}

/// <summary>
/// Represents a user preference for CLI behavior.
/// </summary>
public sealed record UserPreference
{
    /// <summary>Preference key.</summary>
    public required string Key { get; init; }

    /// <summary>Preference value.</summary>
    public required object Value { get; init; }

    /// <summary>When this preference was set.</summary>
    public DateTime SetAt { get; init; } = DateTime.UtcNow;

    /// <summary>How many times this preference was applied.</summary>
    public int ApplyCount { get; set; }
}

/// <summary>
/// Stores and retrieves learned patterns, corrections, and user preferences.
/// Enables the CLI to improve over time based on user interactions.
/// </summary>
public sealed class CLILearningStore : IDisposable
{
    private readonly ConcurrentDictionary<string, LearnedPattern> _patterns = new();
    private readonly ConcurrentDictionary<string, SynonymMapping> _synonyms = new();
    private readonly ConcurrentDictionary<string, UserPreference> _preferences = new();
    private readonly string? _persistencePath;
    private readonly Timer? _persistenceTimer;
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private bool _disposed;
    private bool _isDirty;

    /// <summary>
    /// Creates a new CLILearningStore.
    /// </summary>
    /// <param name="persistencePath">Optional file path to persist learned patterns.</param>
    public CLILearningStore(string? persistencePath = null)
    {
        _persistencePath = persistencePath;

        // Initialize default synonyms
        InitializeDefaultSynonyms();

        // Load from persistence if available
        if (!string.IsNullOrEmpty(_persistencePath) && File.Exists(_persistencePath))
        {
            _ = LoadAsync();
        }

        // Auto-save every 5 minutes if persistence is enabled
        if (!string.IsNullOrEmpty(_persistencePath))
        {
            _persistenceTimer = new Timer(
                _ => { if (_isDirty) _ = SaveAsync(); },
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }
    }

    #region Pattern Learning

    /// <summary>
    /// Records a successful interpretation for learning.
    /// </summary>
    public void RecordSuccess(string inputPhrase, string commandName, Dictionary<string, object?>? parameters = null)
    {
        var normalized = NormalizePhrase(inputPhrase);
        var key = $"{normalized}:{commandName}";

        var pattern = _patterns.AddOrUpdate(
            key,
            _ => new LearnedPattern
            {
                InputPhrase = inputPhrase,
                NormalizedPhrase = normalized,
                CommandName = commandName,
                Parameters = parameters ?? new(),
                SuccessCount = 1,
                IsCorrection = false
            },
            (_, existing) =>
            {
                existing.SuccessCount++;
                existing.LastUsedAt = DateTime.UtcNow;
                return existing;
            });

        _isDirty = true;
    }

    /// <summary>
    /// Records a user correction (when the AI got it wrong).
    /// </summary>
    public void RecordCorrection(
        string inputPhrase,
        string incorrectCommand,
        string correctCommand,
        Dictionary<string, object?>? correctParameters = null)
    {
        var normalized = NormalizePhrase(inputPhrase);
        var key = $"{normalized}:{correctCommand}";

        // Add the correction as a high-priority pattern
        var pattern = new LearnedPattern
        {
            InputPhrase = inputPhrase,
            NormalizedPhrase = normalized,
            CommandName = correctCommand,
            Parameters = correctParameters ?? new(),
            SuccessCount = 3, // Start with higher weight for corrections
            IsCorrection = true,
            OriginalMisinterpretation = incorrectCommand
        };

        _patterns[key] = pattern;

        // Record failure for the incorrect pattern if it exists
        var incorrectKey = $"{normalized}:{incorrectCommand}";
        if (_patterns.TryGetValue(incorrectKey, out var incorrectPattern))
        {
            incorrectPattern.FailureCount++;
        }

        _isDirty = true;
    }

    /// <summary>
    /// Finds the best matching learned pattern for an input.
    /// </summary>
    public LearnedPattern? FindBestMatch(string inputPhrase, double minConfidence = 0.6)
    {
        var normalized = NormalizePhrase(inputPhrase);

        // Exact match first
        var exactMatches = _patterns.Values
            .Where(p => p.NormalizedPhrase.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Confidence >= minConfidence)
            .OrderByDescending(p => p.Confidence)
            .ThenByDescending(p => p.SuccessCount)
            .ThenBy(p => p.IsCorrection ? 0 : 1) // Prefer corrections
            .ToList();

        if (exactMatches.Count > 0)
        {
            return exactMatches[0];
        }

        // Fuzzy match - find patterns with significant word overlap
        var inputWords = GetSignificantWords(normalized);
        if (inputWords.Length == 0) return null;

        var fuzzyMatches = _patterns.Values
            .Select(p => new
            {
                Pattern = p,
                Similarity = CalculateSimilarity(inputWords, GetSignificantWords(p.NormalizedPhrase))
            })
            .Where(x => x.Similarity > 0.7 && x.Pattern.Confidence >= minConfidence)
            .OrderByDescending(x => x.Similarity)
            .ThenByDescending(x => x.Pattern.Confidence)
            .FirstOrDefault();

        return fuzzyMatches?.Pattern;
    }

    /// <summary>
    /// Gets patterns that might help interpret ambiguous input.
    /// </summary>
    public IEnumerable<LearnedPattern> GetSimilarPatterns(string inputPhrase, int maxResults = 5)
    {
        var normalized = NormalizePhrase(inputPhrase);
        var inputWords = GetSignificantWords(normalized);

        return _patterns.Values
            .Select(p => new
            {
                Pattern = p,
                Similarity = CalculateSimilarity(inputWords, GetSignificantWords(p.NormalizedPhrase))
            })
            .Where(x => x.Similarity > 0.3)
            .OrderByDescending(x => x.Similarity)
            .ThenByDescending(x => x.Pattern.Confidence)
            .Take(maxResults)
            .Select(x => x.Pattern);
    }

    #endregion

    #region Synonym Handling

    /// <summary>
    /// Adds a synonym mapping.
    /// </summary>
    public void AddSynonym(string synonym, string canonicalTerm, string? context = null)
    {
        var key = $"{synonym.ToLowerInvariant()}:{context ?? "*"}";
        _synonyms[key] = new SynonymMapping
        {
            Synonym = synonym.ToLowerInvariant(),
            CanonicalTerm = canonicalTerm.ToLowerInvariant(),
            Context = context
        };
        _isDirty = true;
    }

    /// <summary>
    /// Resolves synonyms in an input phrase.
    /// </summary>
    public string ResolveSynonyms(string input, string? context = null)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        foreach (var word in words)
        {
            var normalizedWord = word.ToLowerInvariant();

            // Try context-specific synonym first
            if (context != null)
            {
                var contextKey = $"{normalizedWord}:{context}";
                if (_synonyms.TryGetValue(contextKey, out var contextMapping))
                {
                    contextMapping.UseCount++;
                    result.Add(contextMapping.CanonicalTerm);
                    continue;
                }
            }

            // Try global synonym
            var globalKey = $"{normalizedWord}:*";
            if (_synonyms.TryGetValue(globalKey, out var globalMapping))
            {
                globalMapping.UseCount++;
                result.Add(globalMapping.CanonicalTerm);
                continue;
            }

            result.Add(word);
        }

        return string.Join(" ", result);
    }

    /// <summary>
    /// Gets all synonyms for a canonical term.
    /// </summary>
    public IEnumerable<string> GetSynonymsFor(string canonicalTerm)
    {
        return _synonyms.Values
            .Where(s => s.CanonicalTerm.Equals(canonicalTerm, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Synonym);
    }

    private void InitializeDefaultSynonyms()
    {
        // Backup synonyms
        AddSynonym("save", "backup", "backup");
        AddSynonym("archive", "backup", "backup");
        AddSynonym("snapshot", "backup", "backup");
        AddSynonym("copy", "backup", "backup");

        // Show/List synonyms
        AddSynonym("show", "list");
        AddSynonym("get", "list");
        AddSynonym("display", "list");
        AddSynonym("view", "list");

        // Create synonyms
        AddSynonym("make", "create");
        AddSynonym("add", "create");
        AddSynonym("new", "create");

        // Delete synonyms
        AddSynonym("remove", "delete");
        AddSynonym("destroy", "delete");
        AddSynonym("drop", "delete");

        // Storage synonyms
        AddSynonym("disk", "storage");
        AddSynonym("drive", "storage");
        AddSynonym("volume", "storage");

        // Status synonyms
        AddSynonym("health", "status");
        AddSynonym("state", "status");
        AddSynonym("condition", "status");

        // Start/Stop synonyms
        AddSynonym("begin", "start");
        AddSynonym("launch", "start");
        AddSynonym("halt", "stop");
        AddSynonym("end", "stop");
        AddSynonym("terminate", "stop");
    }

    #endregion

    #region User Preferences

    /// <summary>
    /// Sets a user preference.
    /// </summary>
    public void SetPreference(string key, object value)
    {
        _preferences[key] = new UserPreference
        {
            Key = key,
            Value = value
        };
        _isDirty = true;
    }

    /// <summary>
    /// Gets a user preference.
    /// </summary>
    public T? GetPreference<T>(string key, T? defaultValue = default)
    {
        if (_preferences.TryGetValue(key, out var pref))
        {
            pref.ApplyCount++;
            try
            {
                if (pref.Value is T typed)
                    return typed;
                if (pref.Value is JsonElement element)
                    return JsonSerializer.Deserialize<T>(element.GetRawText());
                return (T)Convert.ChangeType(pref.Value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    /// <summary>
    /// Gets all preferences.
    /// </summary>
    public IReadOnlyDictionary<string, object> GetAllPreferences()
    {
        return _preferences.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value);
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets learning statistics.
    /// </summary>
    public LearningStatistics GetStatistics()
    {
        var patterns = _patterns.Values.ToList();
        var corrections = patterns.Where(p => p.IsCorrection).ToList();

        return new LearningStatistics
        {
            TotalPatterns = patterns.Count,
            CorrectionsLearned = corrections.Count,
            TotalSuccesses = patterns.Sum(p => p.SuccessCount),
            TotalFailures = patterns.Sum(p => p.FailureCount),
            AverageConfidence = patterns.Count > 0 ? patterns.Average(p => p.Confidence) : 0,
            SynonymCount = _synonyms.Count,
            PreferenceCount = _preferences.Count,
            OldestPattern = patterns.Count > 0 ? patterns.Min(p => p.LearnedAt) : null,
            MostUsedPattern = patterns.OrderByDescending(p => p.SuccessCount).FirstOrDefault()?.InputPhrase
        };
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Saves learned data to persistence.
    /// </summary>
    public async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(_persistencePath)) return;

        await _persistenceLock.WaitAsync();
        try
        {
            var data = new LearningData
            {
                Patterns = _patterns.Values.ToList(),
                Synonyms = _synonyms.Values.ToList(),
                Preferences = _preferences.Values.ToList(),
                SavedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var directory = Path.GetDirectoryName(_persistencePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(_persistencePath, json);
            _isDirty = false;
        }
        finally
        {
            _persistenceLock.Release();
        }
    }

    /// <summary>
    /// Loads learned data from persistence.
    /// </summary>
    public async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(_persistencePath) || !File.Exists(_persistencePath)) return;

        await _persistenceLock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_persistencePath);
            var data = JsonSerializer.Deserialize<LearningData>(json);

            if (data != null)
            {
                _patterns.Clear();
                foreach (var pattern in data.Patterns)
                {
                    var key = $"{pattern.NormalizedPhrase}:{pattern.CommandName}";
                    _patterns[key] = pattern;
                }

                foreach (var synonym in data.Synonyms)
                {
                    var key = $"{synonym.Synonym}:{synonym.Context ?? "*"}";
                    _synonyms[key] = synonym;
                }

                foreach (var pref in data.Preferences)
                {
                    _preferences[pref.Key] = pref;
                }
            }

            _isDirty = false;
        }
        catch
        {
            // Failed to load - continue with empty store
        }
        finally
        {
            _persistenceLock.Release();
        }
    }

    #endregion

    #region Helpers

    private static string NormalizePhrase(string phrase)
    {
        // Remove punctuation, extra spaces, and convert to lowercase
        var normalized = new string(phrase
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            .ToArray());

        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] GetSignificantWords(string phrase)
    {
        var stopWords = new HashSet<string> { "a", "an", "the", "my", "to", "for", "with", "on", "in", "of", "is", "are", "was", "be", "i", "me" };
        return phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToArray();
    }

    private static double CalculateSimilarity(string[] words1, string[] words2)
    {
        if (words1.Length == 0 || words2.Length == 0) return 0;

        var set1 = new HashSet<string>(words1);
        var set2 = new HashSet<string>(words2);

        var intersection = set1.Intersect(set2).Count();
        var union = set1.Union(set2).Count();

        return union > 0 ? (double)intersection / union : 0;
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isDirty && !string.IsNullOrEmpty(_persistencePath))
        {
            // Sync bridge: Dispose cannot be async without IAsyncDisposable
            Task.Run(() => SaveAsync()).GetAwaiter().GetResult();
        }

        _persistenceTimer?.Dispose();
        _persistenceLock.Dispose();
    }

    private sealed class LearningData
    {
        public List<LearnedPattern> Patterns { get; set; } = new();
        public List<SynonymMapping> Synonyms { get; set; } = new();
        public List<UserPreference> Preferences { get; set; } = new();
        public DateTime SavedAt { get; set; }
    }
}

/// <summary>
/// Statistics about the learning store.
/// </summary>
public sealed record LearningStatistics
{
    public int TotalPatterns { get; init; }
    public int CorrectionsLearned { get; init; }
    public int TotalSuccesses { get; init; }
    public int TotalFailures { get; init; }
    public double AverageConfidence { get; init; }
    public int SynonymCount { get; init; }
    public int PreferenceCount { get; init; }
    public DateTime? OldestPattern { get; init; }
    public string? MostUsedPattern { get; init; }
}
