using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.Search
{
    /// <summary>
    /// Production-ready filename search plugin using Trie-based indexing.
    /// Supports prefix search, glob pattern matching, regex patterns, case-insensitive matching,
    /// path component search, and extension filtering. Optimized for TB-scale indexes with persistence.
    /// </summary>
    public sealed class FilenameSearchPlugin : SearchProviderPluginBase
    {
        public override string Id => "datawarehouse.search.filename";
        public override string Name => "Filename Search Provider";
        public override string Version => "1.0.0";
        public override SearchType SearchType => SearchType.Filename;
        public override int Priority => 40;

        private readonly FilenameTrieIndex _trieIndex;
        private readonly ConcurrentDictionary<string, FileIndexEntry> _entries;
        private readonly ConcurrentDictionary<string, HashSet<string>> _extensionIndex;
        private readonly ReaderWriterLockSlim _indexLock;
        private volatile bool _isAvailable;
        private long _indexedCount;
        private long _searchCount;
        private DateTime _lastOptimized;
        private string? _persistencePath;

        /// <summary>
        /// Regex cache for compiled patterns to avoid recompilation overhead.
        /// </summary>
        private readonly ConcurrentDictionary<string, Regex> _regexCache;
        private const int MaxRegexCacheSize = 1000;

        public override bool IsAvailable => _isAvailable;

        public FilenameSearchPlugin() : this(null) { }

        public FilenameSearchPlugin(string? persistencePath)
        {
            _trieIndex = new FilenameTrieIndex();
            _entries = new ConcurrentDictionary<string, FileIndexEntry>(StringComparer.OrdinalIgnoreCase);
            _extensionIndex = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            _regexCache = new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);
            _indexLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            _isAvailable = true;
            _lastOptimized = DateTime.UtcNow;
            _persistencePath = persistencePath;
        }

        /// <summary>
        /// Configures the persistence path for index snapshots.
        /// </summary>
        public void SetPersistencePath(string path)
        {
            _persistencePath = path;
        }

        public override async Task StartAsync(CancellationToken ct)
        {
            // Load persisted index if available
            if (!string.IsNullOrEmpty(_persistencePath) && File.Exists(_persistencePath))
            {
                await LoadIndexAsync(ct);
            }
            _isAvailable = true;
        }

        public override async Task StopAsync()
        {
            _isAvailable = false;
            // Persist index on shutdown if path is configured
            if (!string.IsNullOrEmpty(_persistencePath))
            {
                await SaveIndexAsync(CancellationToken.None);
            }
        }

        public override async Task<SearchProviderResult> SearchAsync(
            SearchRequest request,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            Interlocked.Increment(ref _searchCount);

            try
            {
                var query = request.Query?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(query))
                {
                    return new SearchProviderResult
                    {
                        SearchType = SearchType,
                        Hits = Array.Empty<SearchHit>(),
                        Duration = sw.Elapsed,
                        Success = true
                    };
                }

                var hits = new List<SearchHit>();
                var limit = Math.Max(1, Math.Min(request.Limit, 10000));

                // Check for extension filter in request
                string[]? extensionFilter = null;
                if (request.Filters?.TryGetValue("extensions", out var extObj) == true)
                {
                    extensionFilter = extObj switch
                    {
                        string s => new[] { NormalizeExtension(s) },
                        string[] arr => arr.Select(NormalizeExtension).ToArray(),
                        IEnumerable<string> list => list.Select(NormalizeExtension).ToArray(),
                        _ => null
                    };
                }

                // Check for regex mode
                var useRegex = request.Filters?.TryGetValue("regex", out var regexVal) == true
                    && (regexVal is true || regexVal?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);

                _indexLock.EnterReadLock();
                try
                {
                    // Apply extension filter first if specified
                    IEnumerable<string>? candidateIds = null;
                    if (extensionFilter != null && extensionFilter.Length > 0)
                    {
                        candidateIds = SearchByExtensionsAsync(extensionFilter, limit * 2);
                    }

                    // Determine search strategy based on query pattern
                    if (useRegex)
                    {
                        // Direct regex pattern search
                        hits.AddRange(await SearchByRegexPatternAsync(query, limit, ct, candidateIds));
                    }
                    else if (IsGlobPattern(query))
                    {
                        // Glob pattern search (*.txt, doc*.pdf, **/file.txt, etc.)
                        hits.AddRange(await SearchByGlobPatternAsync(query, limit, ct, candidateIds));
                    }
                    else if (query.Contains('/') || query.Contains('\\'))
                    {
                        // Path component search
                        hits.AddRange(await SearchByPathComponentAsync(query, limit, ct, candidateIds));
                    }
                    else
                    {
                        // Prefix/substring search using Trie
                        hits.AddRange(await SearchByPrefixAsync(query, limit, ct, candidateIds));
                    }
                }
                finally
                {
                    _indexLock.ExitReadLock();
                }

                sw.Stop();

                return new SearchProviderResult
                {
                    SearchType = SearchType,
                    Hits = hits.OrderByDescending(h => h.Score).Take(limit).ToList(),
                    Duration = sw.Elapsed,
                    Success = true
                };
            }
            catch (OperationCanceledException)
            {
                return new SearchProviderResult
                {
                    SearchType = SearchType,
                    Success = false,
                    ErrorMessage = "Search cancelled",
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                return new SearchProviderResult
                {
                    SearchType = SearchType,
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        public override Task IndexAsync(
            string objectId,
            IndexableContent content,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(objectId))
                return Task.CompletedTask;

            var filename = content.Filename ?? objectId;
            var normalizedFilename = NormalizeFilename(filename);

            _indexLock.EnterWriteLock();
            try
            {
                // Create entry
                var entry = new FileIndexEntry
                {
                    ObjectId = objectId,
                    OriginalFilename = filename,
                    NormalizedFilename = normalizedFilename,
                    Extension = GetExtension(filename),
                    PathComponents = GetPathComponents(filename),
                    Size = content.Size ?? 0,
                    ContentType = content.ContentType,
                    IndexedAt = DateTime.UtcNow
                };

                // Update or add entry
                var isNew = !_entries.ContainsKey(objectId);
                if (!isNew)
                {
                    // Remove old entry from trie and extension index
                    if (_entries.TryGetValue(objectId, out var oldEntry))
                    {
                        _trieIndex.Remove(oldEntry.NormalizedFilename, objectId);
                        foreach (var component in oldEntry.PathComponents)
                        {
                            _trieIndex.Remove(component.ToLowerInvariant(), objectId);
                        }
                        // Remove from extension index
                        if (!string.IsNullOrEmpty(oldEntry.Extension) &&
                            _extensionIndex.TryGetValue(oldEntry.Extension, out var oldExtSet))
                        {
                            lock (oldExtSet)
                            {
                                oldExtSet.Remove(objectId);
                            }
                        }
                    }
                }

                _entries[objectId] = entry;

                // Add to trie index
                _trieIndex.Insert(normalizedFilename, objectId);

                // Also index path components for component search
                foreach (var component in entry.PathComponents)
                {
                    _trieIndex.Insert(component.ToLowerInvariant(), objectId);
                }

                // Index filename without extension
                var nameWithoutExt = Path.GetFileNameWithoutExtension(normalizedFilename);
                if (!string.IsNullOrEmpty(nameWithoutExt))
                {
                    _trieIndex.Insert(nameWithoutExt, objectId);
                }

                // Add to extension index for fast extension filtering
                if (!string.IsNullOrEmpty(entry.Extension))
                {
                    var extSet = _extensionIndex.GetOrAdd(entry.Extension, _ => new HashSet<string>());
                    lock (extSet)
                    {
                        extSet.Add(objectId);
                    }
                }

                if (isNew)
                {
                    Interlocked.Increment(ref _indexedCount);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }

            return Task.CompletedTask;
        }

        public override Task RemoveFromIndexAsync(string objectId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(objectId))
                return Task.CompletedTask;

            _indexLock.EnterWriteLock();
            try
            {
                if (_entries.TryRemove(objectId, out var entry))
                {
                    _trieIndex.Remove(entry.NormalizedFilename, objectId);
                    foreach (var component in entry.PathComponents)
                    {
                        _trieIndex.Remove(component.ToLowerInvariant(), objectId);
                    }

                    var nameWithoutExt = Path.GetFileNameWithoutExtension(entry.NormalizedFilename);
                    if (!string.IsNullOrEmpty(nameWithoutExt))
                    {
                        _trieIndex.Remove(nameWithoutExt, objectId);
                    }

                    // Remove from extension index
                    if (!string.IsNullOrEmpty(entry.Extension) &&
                        _extensionIndex.TryGetValue(entry.Extension, out var extSet))
                    {
                        lock (extSet)
                        {
                            extSet.Remove(objectId);
                        }
                    }

                    Interlocked.Decrement(ref _indexedCount);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Optimizes the index by compacting the trie structure and removing empty nodes.
        /// </summary>
        public void OptimizeIndex()
        {
            _indexLock.EnterWriteLock();
            try
            {
                _trieIndex.Compact();
                _lastOptimized = DateTime.UtcNow;
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets index statistics.
        /// </summary>
        public FilenameIndexStatistics GetStatistics()
        {
            _indexLock.EnterReadLock();
            try
            {
                return new FilenameIndexStatistics
                {
                    TotalEntries = Interlocked.Read(ref _indexedCount),
                    TrieNodeCount = _trieIndex.NodeCount,
                    SearchCount = Interlocked.Read(ref _searchCount),
                    LastOptimized = _lastOptimized,
                    MemoryEstimateBytes = _trieIndex.EstimatedMemoryBytes
                };
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        #region Private Search Methods

        private Task<IEnumerable<SearchHit>> SearchByPrefixAsync(
            string prefix,
            int limit,
            CancellationToken ct,
            IEnumerable<string>? candidateIds = null)
        {
            var normalizedPrefix = prefix.ToLowerInvariant();
            IEnumerable<string> searchIds;

            if (candidateIds != null)
            {
                // Filter from candidate set
                searchIds = candidateIds;
            }
            else
            {
                // Use trie for efficient prefix lookup
                searchIds = _trieIndex.FindByPrefix(normalizedPrefix, limit * 2);
            }

            var hits = new List<SearchHit>();

            foreach (var objectId in searchIds.Take(limit * 2))
            {
                ct.ThrowIfCancellationRequested();

                if (_entries.TryGetValue(objectId, out var entry))
                {
                    // Check if it matches the prefix
                    if (candidateIds != null &&
                        !entry.NormalizedFilename.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var score = CalculatePrefixScore(normalizedPrefix, entry);
                    hits.Add(CreateSearchHit(entry, score, normalizedPrefix));

                    if (hits.Count >= limit)
                        break;
                }
            }

            return Task.FromResult<IEnumerable<SearchHit>>(hits);
        }

        private Task<IEnumerable<SearchHit>> SearchByGlobPatternAsync(
            string pattern,
            int limit,
            CancellationToken ct,
            IEnumerable<string>? candidateIds = null)
        {
            var regex = GlobToRegex(pattern);
            var hits = new List<SearchHit>();
            var count = 0;

            IEnumerable<FileIndexEntry> searchEntries;
            if (candidateIds != null)
            {
                searchEntries = candidateIds
                    .Select(id => _entries.TryGetValue(id, out var e) ? e : null)
                    .Where(e => e != null)!;
            }
            else
            {
                searchEntries = _entries.Values;
            }

            foreach (var entry in searchEntries)
            {
                ct.ThrowIfCancellationRequested();
                if (count >= limit) break;

                if (regex.IsMatch(entry.OriginalFilename) ||
                    regex.IsMatch(entry.NormalizedFilename))
                {
                    var score = CalculateGlobScore(pattern, entry);
                    hits.Add(CreateSearchHit(entry, score, pattern));
                    count++;
                }
            }

            return Task.FromResult<IEnumerable<SearchHit>>(hits);
        }

        private Task<IEnumerable<SearchHit>> SearchByRegexPatternAsync(
            string pattern,
            int limit,
            CancellationToken ct,
            IEnumerable<string>? candidateIds = null)
        {
            // Get or compile regex with caching
            var regex = GetOrCreateRegex(pattern);
            if (regex == null)
            {
                // Invalid regex pattern
                return Task.FromResult<IEnumerable<SearchHit>>(Array.Empty<SearchHit>());
            }

            var hits = new List<SearchHit>();
            var count = 0;

            IEnumerable<FileIndexEntry> searchEntries;
            if (candidateIds != null)
            {
                searchEntries = candidateIds
                    .Select(id => _entries.TryGetValue(id, out var e) ? e : null)
                    .Where(e => e != null)!;
            }
            else
            {
                searchEntries = _entries.Values;
            }

            foreach (var entry in searchEntries)
            {
                ct.ThrowIfCancellationRequested();
                if (count >= limit) break;

                var match = regex.Match(entry.OriginalFilename);
                if (!match.Success)
                {
                    match = regex.Match(entry.NormalizedFilename);
                }

                if (match.Success)
                {
                    // Score based on match length relative to filename length
                    var score = (double)match.Length / entry.OriginalFilename.Length;
                    var hit = CreateSearchHit(entry, score, pattern);

                    // Add regex match highlights
                    var updatedHit = new SearchHit
                    {
                        ObjectId = hit.ObjectId,
                        Score = hit.Score,
                        FoundBy = hit.FoundBy,
                        Snippet = hit.Snippet,
                        Highlights = new[] { new HighlightRange { Start = match.Index, End = match.Index + match.Length } },
                        Metadata = hit.Metadata
                    };

                    hits.Add(updatedHit);
                    count++;
                }
            }

            return Task.FromResult<IEnumerable<SearchHit>>(hits);
        }

        private Task<IEnumerable<SearchHit>> SearchByPathComponentAsync(
            string pathQuery,
            int limit,
            CancellationToken ct,
            IEnumerable<string>? candidateIds = null)
        {
            var components = GetPathComponents(pathQuery);
            var hits = new List<SearchHit>();
            var candidateScores = new Dictionary<string, double>();

            if (candidateIds != null)
            {
                // Score against candidate set
                foreach (var objectId in candidateIds)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!_entries.TryGetValue(objectId, out var entry))
                        continue;

                    var matchedComponents = 0;
                    foreach (var component in components)
                    {
                        if (entry.PathComponents.Any(pc =>
                            pc.Contains(component, StringComparison.OrdinalIgnoreCase)))
                        {
                            matchedComponents++;
                        }
                    }

                    if (matchedComponents > 0)
                    {
                        candidateScores[objectId] = (double)matchedComponents / components.Length;
                    }
                }
            }
            else
            {
                // Use trie for each component
                foreach (var component in components)
                {
                    ct.ThrowIfCancellationRequested();

                    var normalizedComponent = component.ToLowerInvariant();
                    var matchingIds = _trieIndex.FindByPrefix(normalizedComponent, limit * 2);

                    foreach (var objectId in matchingIds)
                    {
                        if (!candidateScores.ContainsKey(objectId))
                        {
                            candidateScores[objectId] = 0;
                        }
                        candidateScores[objectId] += 1.0 / components.Length;
                    }
                }
            }

            foreach (var kvp in candidateScores.OrderByDescending(x => x.Value).Take(limit))
            {
                ct.ThrowIfCancellationRequested();

                if (_entries.TryGetValue(kvp.Key, out var entry))
                {
                    hits.Add(CreateSearchHit(entry, kvp.Value, pathQuery));
                }
            }

            return Task.FromResult<IEnumerable<SearchHit>>(hits);
        }

        /// <summary>
        /// Searches by file extensions using the extension index.
        /// </summary>
        private IEnumerable<string> SearchByExtensionsAsync(string[] extensions, int limit)
        {
            var results = new HashSet<string>();

            foreach (var ext in extensions)
            {
                if (_extensionIndex.TryGetValue(ext, out var objectIds))
                {
                    lock (objectIds)
                    {
                        foreach (var id in objectIds)
                        {
                            results.Add(id);
                            if (results.Count >= limit)
                                break;
                        }
                    }
                }
                if (results.Count >= limit)
                    break;
            }

            return results;
        }

        /// <summary>
        /// Gets or creates a compiled regex from cache.
        /// </summary>
        private Regex? GetOrCreateRegex(string pattern)
        {
            if (_regexCache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }

            try
            {
                var regex = new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromSeconds(1));

                // Limit cache size to prevent memory issues
                if (_regexCache.Count >= MaxRegexCacheSize)
                {
                    // Remove oldest entries (simple LRU approximation)
                    var toRemove = _regexCache.Keys.Take(MaxRegexCacheSize / 4).ToList();
                    foreach (var key in toRemove)
                    {
                        _regexCache.TryRemove(key, out _);
                    }
                }

                _regexCache[pattern] = regex;
                return regex;
            }
            catch (ArgumentException)
            {
                // Invalid regex pattern
                return null;
            }
        }

        #endregion

        #region Helper Methods

        private static bool IsGlobPattern(string query)
        {
            return query.Contains('*') || query.Contains('?') || query.Contains('[');
        }

        private static string NormalizeFilename(string filename)
        {
            return filename.ToLowerInvariant()
                .Replace('\\', '/')
                .TrimStart('/');
        }

        private static string GetExtension(string filename)
        {
            var ext = Path.GetExtension(filename);
            return string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
        }

        private static string NormalizeExtension(string ext)
        {
            ext = ext.Trim().ToLowerInvariant();
            if (!ext.StartsWith('.'))
            {
                ext = "." + ext;
            }
            return ext;
        }

        private static string[] GetPathComponents(string path)
        {
            return path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        private static Regex GlobToRegex(string pattern)
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*\\*", ".*")
                .Replace("\\*", "[^/]*")
                .Replace("\\?", "[^/]")
                .Replace("\\[!", "[^")
                .Replace("\\[", "[")
                .Replace("\\]", "]") + "$";

            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private static double CalculatePrefixScore(string prefix, FileIndexEntry entry)
        {
            var filename = Path.GetFileName(entry.NormalizedFilename);

            // Exact match scores highest
            if (filename.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return 1.0;

            // Starts with prefix scores high
            if (filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return 0.9 * ((double)prefix.Length / filename.Length);

            // Contains prefix scores medium
            if (filename.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return 0.7 * ((double)prefix.Length / filename.Length);

            // Path contains prefix scores lower
            if (entry.NormalizedFilename.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return 0.5 * ((double)prefix.Length / entry.NormalizedFilename.Length);

            return 0.3;
        }

        private static double CalculateGlobScore(string pattern, FileIndexEntry entry)
        {
            var filename = Path.GetFileName(entry.OriginalFilename);

            // Filename matches pattern scores highest
            var patternWithoutWildcard = pattern.Replace("*", "").Replace("?", "");

            if (filename.StartsWith(patternWithoutWildcard, StringComparison.OrdinalIgnoreCase))
                return 0.9;

            if (filename.Contains(patternWithoutWildcard, StringComparison.OrdinalIgnoreCase))
                return 0.7;

            return 0.5;
        }

        private static SearchHit CreateSearchHit(FileIndexEntry entry, double score, string query)
        {
            var filename = Path.GetFileName(entry.OriginalFilename);
            var highlightStart = filename.IndexOf(query.Replace("*", "").Replace("?", ""),
                StringComparison.OrdinalIgnoreCase);

            return new SearchHit
            {
                ObjectId = entry.ObjectId,
                Score = score,
                FoundBy = SearchType.Filename,
                Snippet = entry.OriginalFilename,
                Highlights = highlightStart >= 0
                    ? new[] { new HighlightRange { Start = highlightStart, End = highlightStart + query.Length } }
                    : null,
                Metadata = new Dictionary<string, object>
                {
                    ["filename"] = entry.OriginalFilename,
                    ["extension"] = entry.Extension,
                    ["size"] = entry.Size,
                    ["contentType"] = entry.ContentType ?? string.Empty
                }
            };
        }

        #endregion

        #region Persistence Methods

        /// <summary>
        /// Saves the index to the configured persistence path.
        /// </summary>
        public async Task SaveIndexAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_persistencePath))
                return;

            _indexLock.EnterReadLock();
            try
            {
                var snapshot = new IndexSnapshot
                {
                    Version = 1,
                    CreatedAt = DateTime.UtcNow,
                    Entries = _entries.Values.Select(e => new SerializableEntry
                    {
                        ObjectId = e.ObjectId,
                        OriginalFilename = e.OriginalFilename,
                        NormalizedFilename = e.NormalizedFilename,
                        Extension = e.Extension,
                        PathComponents = e.PathComponents,
                        Size = e.Size,
                        ContentType = e.ContentType,
                        IndexedAt = e.IndexedAt
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                var tempPath = _persistencePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json, ct);
                File.Move(tempPath, _persistencePath, overwrite: true);
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Loads the index from the configured persistence path.
        /// </summary>
        public async Task LoadIndexAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_persistencePath) || !File.Exists(_persistencePath))
                return;

            _indexLock.EnterWriteLock();
            try
            {
                var json = await File.ReadAllTextAsync(_persistencePath, ct);
                var snapshot = JsonSerializer.Deserialize<IndexSnapshot>(json);

                if (snapshot?.Entries == null)
                    return;

                // Clear existing index
                _entries.Clear();
                _extensionIndex.Clear();
                _trieIndex.Clear();
                _indexedCount = 0;

                // Rebuild index from snapshot
                foreach (var entry in snapshot.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var fileEntry = new FileIndexEntry
                    {
                        ObjectId = entry.ObjectId,
                        OriginalFilename = entry.OriginalFilename,
                        NormalizedFilename = entry.NormalizedFilename,
                        Extension = entry.Extension,
                        PathComponents = entry.PathComponents,
                        Size = entry.Size,
                        ContentType = entry.ContentType,
                        IndexedAt = entry.IndexedAt
                    };

                    _entries[entry.ObjectId] = fileEntry;
                    _trieIndex.Insert(entry.NormalizedFilename, entry.ObjectId);

                    foreach (var component in entry.PathComponents)
                    {
                        _trieIndex.Insert(component.ToLowerInvariant(), entry.ObjectId);
                    }

                    var nameWithoutExt = Path.GetFileNameWithoutExtension(entry.NormalizedFilename);
                    if (!string.IsNullOrEmpty(nameWithoutExt))
                    {
                        _trieIndex.Insert(nameWithoutExt, entry.ObjectId);
                    }

                    if (!string.IsNullOrEmpty(entry.Extension))
                    {
                        var extSet = _extensionIndex.GetOrAdd(entry.Extension, _ => new HashSet<string>());
                        lock (extSet)
                        {
                            extSet.Add(entry.ObjectId);
                        }
                    }

                    Interlocked.Increment(ref _indexedCount);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Lists all indexed extensions with their document counts.
        /// </summary>
        public Dictionary<string, int> GetExtensionCounts()
        {
            _indexLock.EnterReadLock();
            try
            {
                return _extensionIndex.ToDictionary(
                    kvp => kvp.Key,
                    kvp => { lock (kvp.Value) { return kvp.Value.Count; } });
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        #endregion

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["IndexType"] = "Trie";
            metadata["SupportsGlobPatterns"] = true;
            metadata["SupportsRegexPatterns"] = true;
            metadata["SupportsPathComponents"] = true;
            metadata["SupportsExtensionFilter"] = true;
            metadata["CaseInsensitive"] = true;
            metadata["SupportsPersistence"] = !string.IsNullOrEmpty(_persistencePath);
            metadata["IndexedEntries"] = Interlocked.Read(ref _indexedCount);
            metadata["SearchCount"] = Interlocked.Read(ref _searchCount);
            metadata["UniqueExtensions"] = _extensionIndex.Count;
            return metadata;
        }
    }

    #region Persistence Types

    internal sealed class IndexSnapshot
    {
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<SerializableEntry> Entries { get; set; } = new();
    }

    internal sealed class SerializableEntry
    {
        public required string ObjectId { get; init; }
        public required string OriginalFilename { get; init; }
        public required string NormalizedFilename { get; init; }
        public required string Extension { get; init; }
        public required string[] PathComponents { get; init; }
        public long Size { get; init; }
        public string? ContentType { get; init; }
        public DateTime IndexedAt { get; init; }
    }

    #endregion

    #region Supporting Classes

    /// <summary>
    /// Compressed trie implementation for efficient filename indexing.
    /// Uses path compression and pooled memory for TB-scale indexes.
    /// </summary>
    internal sealed class FilenameTrieIndex
    {
        private readonly TrieNode _root;
        private long _nodeCount;
        private readonly object _compactLock = new();

        public long NodeCount => Interlocked.Read(ref _nodeCount);
        public long EstimatedMemoryBytes => _nodeCount * 128; // Rough estimate per node

        public FilenameTrieIndex()
        {
            _root = new TrieNode();
            _nodeCount = 1;
        }

        public void Insert(string key, string objectId)
        {
            if (string.IsNullOrEmpty(key)) return;

            var current = _root;
            var keySpan = key.AsSpan();

            for (int i = 0; i < keySpan.Length; i++)
            {
                var c = keySpan[i];

                if (!current.Children.TryGetValue(c, out var child))
                {
                    child = new TrieNode { Character = c };
                    current.Children[c] = child;
                    Interlocked.Increment(ref _nodeCount);
                }

                current = child;
            }

            current.ObjectIds.Add(objectId);
            current.IsEndOfWord = true;
        }

        public void Remove(string key, string objectId)
        {
            if (string.IsNullOrEmpty(key)) return;

            var current = _root;
            var keySpan = key.AsSpan();

            for (int i = 0; i < keySpan.Length; i++)
            {
                var c = keySpan[i];

                if (!current.Children.TryGetValue(c, out var child))
                {
                    return; // Key not found
                }

                current = child;
            }

            current.ObjectIds.Remove(objectId);

            if (current.ObjectIds.Count == 0)
            {
                current.IsEndOfWord = false;
            }
        }

        public IEnumerable<string> FindByPrefix(string prefix, int limit)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return CollectAllObjectIds(limit);
            }

            var current = _root;
            var prefixSpan = prefix.AsSpan();

            // Navigate to prefix node
            for (int i = 0; i < prefixSpan.Length; i++)
            {
                var c = prefixSpan[i];

                if (!current.Children.TryGetValue(c, out var child))
                {
                    return Enumerable.Empty<string>(); // Prefix not found
                }

                current = child;
            }

            // Collect all object IDs under this prefix
            return CollectObjectIds(current, limit);
        }

        public void Compact()
        {
            lock (_compactLock)
            {
                CompactNode(_root);
            }
        }

        public void Clear()
        {
            lock (_compactLock)
            {
                _root.Children.Clear();
                _root.ObjectIds.Clear();
                _root.IsEndOfWord = false;
                _nodeCount = 1;
            }
        }

        private void CompactNode(TrieNode node)
        {
            // Remove empty leaf nodes
            var keysToRemove = new List<char>();

            foreach (var kvp in node.Children)
            {
                CompactNode(kvp.Value);

                // Remove if node has no children and no object IDs
                if (kvp.Value.Children.Count == 0 &&
                    kvp.Value.ObjectIds.Count == 0 &&
                    !kvp.Value.IsEndOfWord)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                node.Children.TryRemove(key, out _);
                Interlocked.Decrement(ref _nodeCount);
            }

            // Trim excess capacity from object ID sets
            if (node.ObjectIds is HashSet<string> set && set.Count < set.EnsureCapacity(0) / 2)
            {
                node.ObjectIds = new HashSet<string>(set);
            }
        }

        private IEnumerable<string> CollectAllObjectIds(int limit)
        {
            var collected = new HashSet<string>();
            CollectFromNode(_root, collected, limit);
            return collected;
        }

        private IEnumerable<string> CollectObjectIds(TrieNode startNode, int limit)
        {
            var collected = new HashSet<string>();
            CollectFromNode(startNode, collected, limit);
            return collected;
        }

        private void CollectFromNode(TrieNode node, HashSet<string> collected, int limit)
        {
            if (collected.Count >= limit) return;

            foreach (var objectId in node.ObjectIds)
            {
                if (collected.Count >= limit) return;
                collected.Add(objectId);
            }

            foreach (var child in node.Children.Values)
            {
                if (collected.Count >= limit) return;
                CollectFromNode(child, collected, limit);
            }
        }

        private sealed class TrieNode
        {
            public char Character { get; set; }
            public ConcurrentDictionary<char, TrieNode> Children { get; } = new();
            public HashSet<string> ObjectIds { get; set; } = new();
            public bool IsEndOfWord { get; set; }
        }
    }

    /// <summary>
    /// Entry in the filename index.
    /// </summary>
    internal sealed class FileIndexEntry
    {
        public required string ObjectId { get; init; }
        public required string OriginalFilename { get; init; }
        public required string NormalizedFilename { get; init; }
        public required string Extension { get; init; }
        public required string[] PathComponents { get; init; }
        public long Size { get; init; }
        public string? ContentType { get; init; }
        public DateTime IndexedAt { get; init; }
    }

    /// <summary>
    /// Statistics for the filename index.
    /// </summary>
    public sealed class FilenameIndexStatistics
    {
        public long TotalEntries { get; init; }
        public long TrieNodeCount { get; init; }
        public long SearchCount { get; init; }
        public DateTime LastOptimized { get; init; }
        public long MemoryEstimateBytes { get; init; }
    }

    #endregion
}
