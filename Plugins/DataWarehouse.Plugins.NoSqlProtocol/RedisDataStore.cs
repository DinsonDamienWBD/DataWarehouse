using System.Collections.Concurrent;
using System.Text;

namespace DataWarehouse.Plugins.NoSqlProtocol;

/// <summary>
/// Redis data types.
/// </summary>
public enum RedisDataType
{
    /// <summary>String value.</summary>
    String,
    /// <summary>List value.</summary>
    List,
    /// <summary>Set value.</summary>
    Set,
    /// <summary>Hash value.</summary>
    Hash,
    /// <summary>Sorted set value.</summary>
    ZSet,
    /// <summary>Stream value.</summary>
    Stream
}

/// <summary>
/// Represents a Redis key-value entry with expiration.
/// </summary>
public sealed class RedisEntry
{
    /// <summary>The data type.</summary>
    public RedisDataType Type { get; set; }
    /// <summary>String value.</summary>
    public byte[]? StringValue { get; set; }
    /// <summary>List value.</summary>
    public LinkedList<byte[]>? ListValue { get; set; }
    /// <summary>Set value.</summary>
    public HashSet<string>? SetValue { get; set; }
    /// <summary>Hash value.</summary>
    public Dictionary<string, byte[]>? HashValue { get; set; }
    /// <summary>Sorted set value (member -> score).</summary>
    public SortedDictionary<double, HashSet<string>>? ZSetByScore { get; set; }
    /// <summary>Sorted set reverse lookup (member -> score).</summary>
    public Dictionary<string, double>? ZSetScores { get; set; }
    /// <summary>Expiration time (null = no expiry).</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Lock for thread-safe access.</summary>
    public readonly object Lock = new();
}

/// <summary>
/// In-memory Redis data store.
/// Thread-safe implementation supporting all major Redis data types.
/// </summary>
public sealed class RedisDataStore
{
    private readonly ConcurrentDictionary<string, RedisEntry> _data = new();
    private readonly Timer _expiryTimer;
    private readonly object _expiryLock = new();

    /// <summary>
    /// Creates a new Redis data store.
    /// </summary>
    public RedisDataStore()
    {
        _expiryTimer = new Timer(CleanupExpired, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Gets or creates an entry for a key.
    /// </summary>
    public RedisEntry GetOrCreate(string key, RedisDataType type)
    {
        return _data.GetOrAdd(key, _ => CreateEntry(type));
    }

    /// <summary>
    /// Gets an entry if it exists and is not expired.
    /// </summary>
    public RedisEntry? Get(string key)
    {
        if (_data.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= DateTime.UtcNow)
            {
                _data.TryRemove(key, out _);
                return null;
            }
            return entry;
        }
        return null;
    }

    /// <summary>
    /// Checks if a key exists and is not expired.
    /// </summary>
    public bool Exists(string key)
    {
        return Get(key) != null;
    }

    /// <summary>
    /// Deletes one or more keys.
    /// </summary>
    public int Delete(params string[] keys)
    {
        int count = 0;
        foreach (var key in keys)
        {
            if (_data.TryRemove(key, out _))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Gets the type of a key.
    /// </summary>
    public RedisDataType? GetType(string key)
    {
        var entry = Get(key);
        return entry?.Type;
    }

    /// <summary>
    /// Sets expiration on a key.
    /// </summary>
    public bool Expire(string key, TimeSpan ttl)
    {
        var entry = Get(key);
        if (entry == null) return false;

        lock (entry.Lock)
        {
            entry.ExpiresAt = DateTime.UtcNow.Add(ttl);
        }
        return true;
    }

    /// <summary>
    /// Sets expiration on a key at a specific time.
    /// </summary>
    public bool ExpireAt(string key, DateTime time)
    {
        var entry = Get(key);
        if (entry == null) return false;

        lock (entry.Lock)
        {
            entry.ExpiresAt = time;
        }
        return true;
    }

    /// <summary>
    /// Removes expiration from a key.
    /// </summary>
    public bool Persist(string key)
    {
        var entry = Get(key);
        if (entry == null) return false;

        lock (entry.Lock)
        {
            var hadExpiry = entry.ExpiresAt.HasValue;
            entry.ExpiresAt = null;
            return hadExpiry;
        }
    }

    /// <summary>
    /// Gets the TTL of a key.
    /// </summary>
    public TimeSpan? GetTtl(string key)
    {
        var entry = Get(key);
        if (entry?.ExpiresAt == null) return null;
        var remaining = entry.ExpiresAt.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Renames a key.
    /// </summary>
    public bool Rename(string oldKey, string newKey)
    {
        if (_data.TryRemove(oldKey, out var entry))
        {
            _data[newKey] = entry;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets all keys matching a pattern.
    /// </summary>
    public IEnumerable<string> Keys(string pattern)
    {
        var regex = PatternToRegex(pattern);
        return _data.Keys.Where(k => regex.IsMatch(k) && Get(k) != null);
    }

    /// <summary>
    /// Gets a random key.
    /// </summary>
    public string? RandomKey()
    {
        var keys = _data.Keys.ToArray();
        if (keys.Length == 0) return null;
        return keys[Random.Shared.Next(keys.Length)];
    }

    /// <summary>
    /// Flushes all data.
    /// </summary>
    public void FlushAll()
    {
        _data.Clear();
    }

    /// <summary>
    /// Gets database statistics.
    /// </summary>
    public (long keys, long expires) DbSize()
    {
        var keys = _data.Count;
        var expires = _data.Values.Count(e => e.ExpiresAt.HasValue);
        return (keys, expires);
    }

    #region String Operations

    /// <summary>
    /// Sets a string value.
    /// </summary>
    public void Set(string key, byte[] value, TimeSpan? ttl = null)
    {
        var entry = GetOrCreate(key, RedisDataType.String);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.String;
            entry.StringValue = value;
            entry.ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : null;
            ClearOtherTypes(entry, RedisDataType.String);
        }
    }

    /// <summary>
    /// Gets a string value.
    /// </summary>
    public byte[]? GetString(string key)
    {
        var entry = Get(key);
        if (entry?.Type != RedisDataType.String) return null;
        return entry.StringValue;
    }

    /// <summary>
    /// Sets a string value only if the key does not exist.
    /// </summary>
    public bool SetNx(string key, byte[] value)
    {
        var entry = _data.GetOrAdd(key, _ => new RedisEntry { Type = RedisDataType.String, StringValue = value });
        return entry.StringValue == value && entry.Type == RedisDataType.String;
    }

    /// <summary>
    /// Increments a numeric string value.
    /// </summary>
    public long Incr(string key, long delta = 1)
    {
        var entry = GetOrCreate(key, RedisDataType.String);
        lock (entry.Lock)
        {
            var current = entry.StringValue != null
                ? long.Parse(Encoding.UTF8.GetString(entry.StringValue))
                : 0;
            var newValue = current + delta;
            entry.StringValue = Encoding.UTF8.GetBytes(newValue.ToString());
            entry.Type = RedisDataType.String;
            return newValue;
        }
    }

    /// <summary>
    /// Appends to a string value.
    /// </summary>
    public long Append(string key, byte[] value)
    {
        var entry = GetOrCreate(key, RedisDataType.String);
        lock (entry.Lock)
        {
            if (entry.StringValue == null)
            {
                entry.StringValue = value;
            }
            else
            {
                var newValue = new byte[entry.StringValue.Length + value.Length];
                entry.StringValue.CopyTo(newValue, 0);
                value.CopyTo(newValue, entry.StringValue.Length);
                entry.StringValue = newValue;
            }
            entry.Type = RedisDataType.String;
            return entry.StringValue.Length;
        }
    }

    /// <summary>
    /// Gets multiple string values.
    /// </summary>
    public byte[]?[] MGet(params string[] keys)
    {
        var result = new byte[]?[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            result[i] = GetString(keys[i]);
        }
        return result;
    }

    #endregion

    #region List Operations

    /// <summary>
    /// Pushes values to the left of a list.
    /// </summary>
    public long LPush(string key, params byte[][] values)
    {
        var entry = GetOrCreate(key, RedisDataType.List);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.List;
            entry.ListValue ??= new LinkedList<byte[]>();
            foreach (var value in values)
            {
                entry.ListValue.AddFirst(value);
            }
            return entry.ListValue.Count;
        }
    }

    /// <summary>
    /// Pushes values to the right of a list.
    /// </summary>
    public long RPush(string key, params byte[][] values)
    {
        var entry = GetOrCreate(key, RedisDataType.List);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.List;
            entry.ListValue ??= new LinkedList<byte[]>();
            foreach (var value in values)
            {
                entry.ListValue.AddLast(value);
            }
            return entry.ListValue.Count;
        }
    }

    /// <summary>
    /// Pops a value from the left of a list.
    /// </summary>
    public byte[]? LPop(string key)
    {
        var entry = Get(key);
        if (entry?.ListValue == null) return null;

        lock (entry.Lock)
        {
            if (entry.ListValue.Count == 0) return null;
            var value = entry.ListValue.First!.Value;
            entry.ListValue.RemoveFirst();
            if (entry.ListValue.Count == 0) _data.TryRemove(key, out _);
            return value;
        }
    }

    /// <summary>
    /// Pops a value from the right of a list.
    /// </summary>
    public byte[]? RPop(string key)
    {
        var entry = Get(key);
        if (entry?.ListValue == null) return null;

        lock (entry.Lock)
        {
            if (entry.ListValue.Count == 0) return null;
            var value = entry.ListValue.Last!.Value;
            entry.ListValue.RemoveLast();
            if (entry.ListValue.Count == 0) _data.TryRemove(key, out _);
            return value;
        }
    }

    /// <summary>
    /// Gets a range of elements from a list.
    /// </summary>
    public byte[][] LRange(string key, long start, long stop)
    {
        var entry = Get(key);
        if (entry?.ListValue == null) return Array.Empty<byte[]>();

        lock (entry.Lock)
        {
            var count = entry.ListValue.Count;
            if (start < 0) start = count + start;
            if (stop < 0) stop = count + stop;
            if (start < 0) start = 0;
            if (stop >= count) stop = count - 1;
            if (start > stop) return Array.Empty<byte[]>();

            return entry.ListValue.Skip((int)start).Take((int)(stop - start + 1)).ToArray();
        }
    }

    /// <summary>
    /// Gets the length of a list.
    /// </summary>
    public long LLen(string key)
    {
        var entry = Get(key);
        return entry?.ListValue?.Count ?? 0;
    }

    #endregion

    #region Set Operations

    /// <summary>
    /// Adds members to a set.
    /// </summary>
    public long SAdd(string key, params string[] members)
    {
        var entry = GetOrCreate(key, RedisDataType.Set);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.Set;
            entry.SetValue ??= new HashSet<string>();
            int added = 0;
            foreach (var member in members)
            {
                if (entry.SetValue.Add(member)) added++;
            }
            return added;
        }
    }

    /// <summary>
    /// Removes members from a set.
    /// </summary>
    public long SRem(string key, params string[] members)
    {
        var entry = Get(key);
        if (entry?.SetValue == null) return 0;

        lock (entry.Lock)
        {
            int removed = 0;
            foreach (var member in members)
            {
                if (entry.SetValue.Remove(member)) removed++;
            }
            if (entry.SetValue.Count == 0) _data.TryRemove(key, out _);
            return removed;
        }
    }

    /// <summary>
    /// Checks if a member is in a set.
    /// </summary>
    public bool SIsMember(string key, string member)
    {
        var entry = Get(key);
        if (entry?.SetValue == null) return false;
        lock (entry.Lock)
        {
            return entry.SetValue.Contains(member);
        }
    }

    /// <summary>
    /// Gets all members of a set.
    /// </summary>
    public string[] SMembers(string key)
    {
        var entry = Get(key);
        if (entry?.SetValue == null) return Array.Empty<string>();
        lock (entry.Lock)
        {
            return entry.SetValue.ToArray();
        }
    }

    /// <summary>
    /// Gets the cardinality of a set.
    /// </summary>
    public long SCard(string key)
    {
        var entry = Get(key);
        return entry?.SetValue?.Count ?? 0;
    }

    #endregion

    #region Hash Operations

    /// <summary>
    /// Sets a hash field.
    /// </summary>
    public bool HSet(string key, string field, byte[] value)
    {
        var entry = GetOrCreate(key, RedisDataType.Hash);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.Hash;
            entry.HashValue ??= new Dictionary<string, byte[]>();
            var isNew = !entry.HashValue.ContainsKey(field);
            entry.HashValue[field] = value;
            return isNew;
        }
    }

    /// <summary>
    /// Gets a hash field.
    /// </summary>
    public byte[]? HGet(string key, string field)
    {
        var entry = Get(key);
        if (entry?.HashValue == null) return null;
        lock (entry.Lock)
        {
            return entry.HashValue.TryGetValue(field, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Deletes hash fields.
    /// </summary>
    public long HDel(string key, params string[] fields)
    {
        var entry = Get(key);
        if (entry?.HashValue == null) return 0;

        lock (entry.Lock)
        {
            int removed = 0;
            foreach (var field in fields)
            {
                if (entry.HashValue.Remove(field)) removed++;
            }
            if (entry.HashValue.Count == 0) _data.TryRemove(key, out _);
            return removed;
        }
    }

    /// <summary>
    /// Gets all fields and values of a hash.
    /// </summary>
    public Dictionary<string, byte[]> HGetAll(string key)
    {
        var entry = Get(key);
        if (entry?.HashValue == null) return new Dictionary<string, byte[]>();
        lock (entry.Lock)
        {
            return new Dictionary<string, byte[]>(entry.HashValue);
        }
    }

    /// <summary>
    /// Checks if a hash field exists.
    /// </summary>
    public bool HExists(string key, string field)
    {
        var entry = Get(key);
        if (entry?.HashValue == null) return false;
        lock (entry.Lock)
        {
            return entry.HashValue.ContainsKey(field);
        }
    }

    /// <summary>
    /// Gets all hash fields.
    /// </summary>
    public string[] HKeys(string key)
    {
        var entry = Get(key);
        if (entry?.HashValue == null) return Array.Empty<string>();
        lock (entry.Lock)
        {
            return entry.HashValue.Keys.ToArray();
        }
    }

    /// <summary>
    /// Gets the number of fields in a hash.
    /// </summary>
    public long HLen(string key)
    {
        var entry = Get(key);
        return entry?.HashValue?.Count ?? 0;
    }

    /// <summary>
    /// Increments a hash field by an integer.
    /// </summary>
    public long HIncrBy(string key, string field, long delta)
    {
        var entry = GetOrCreate(key, RedisDataType.Hash);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.Hash;
            entry.HashValue ??= new Dictionary<string, byte[]>();

            long current = 0;
            if (entry.HashValue.TryGetValue(field, out var existing))
            {
                current = long.Parse(Encoding.UTF8.GetString(existing));
            }

            var newValue = current + delta;
            entry.HashValue[field] = Encoding.UTF8.GetBytes(newValue.ToString());
            return newValue;
        }
    }

    #endregion

    #region Sorted Set Operations

    /// <summary>
    /// Adds members to a sorted set.
    /// </summary>
    public long ZAdd(string key, params (double score, string member)[] items)
    {
        var entry = GetOrCreate(key, RedisDataType.ZSet);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.ZSet;
            entry.ZSetByScore ??= new SortedDictionary<double, HashSet<string>>();
            entry.ZSetScores ??= new Dictionary<string, double>();

            int added = 0;
            foreach (var (score, member) in items)
            {
                // Remove old score if exists
                if (entry.ZSetScores.TryGetValue(member, out var oldScore))
                {
                    if (entry.ZSetByScore.TryGetValue(oldScore, out var oldSet))
                    {
                        oldSet.Remove(member);
                        if (oldSet.Count == 0) entry.ZSetByScore.Remove(oldScore);
                    }
                }
                else
                {
                    added++;
                }

                // Add with new score
                if (!entry.ZSetByScore.TryGetValue(score, out var set))
                {
                    set = new HashSet<string>();
                    entry.ZSetByScore[score] = set;
                }
                set.Add(member);
                entry.ZSetScores[member] = score;
            }

            return added;
        }
    }

    /// <summary>
    /// Gets the score of a member.
    /// </summary>
    public double? ZScore(string key, string member)
    {
        var entry = Get(key);
        if (entry?.ZSetScores == null) return null;
        lock (entry.Lock)
        {
            return entry.ZSetScores.TryGetValue(member, out var score) ? score : null;
        }
    }

    /// <summary>
    /// Gets members by rank range.
    /// </summary>
    public (string member, double score)[] ZRange(string key, long start, long stop, bool withScores = false)
    {
        var entry = Get(key);
        if (entry?.ZSetByScore == null) return Array.Empty<(string, double)>();

        lock (entry.Lock)
        {
            var all = entry.ZSetByScore
                .SelectMany(kvp => kvp.Value.Select(m => (member: m, score: kvp.Key)))
                .ToList();

            var count = all.Count;
            if (start < 0) start = count + start;
            if (stop < 0) stop = count + stop;
            if (start < 0) start = 0;
            if (stop >= count) stop = count - 1;
            if (start > stop) return Array.Empty<(string, double)>();

            return all.Skip((int)start).Take((int)(stop - start + 1)).ToArray();
        }
    }

    /// <summary>
    /// Removes members from a sorted set.
    /// </summary>
    public long ZRem(string key, params string[] members)
    {
        var entry = Get(key);
        if (entry?.ZSetScores == null) return 0;

        lock (entry.Lock)
        {
            int removed = 0;
            foreach (var member in members)
            {
                if (entry.ZSetScores.TryGetValue(member, out var score))
                {
                    entry.ZSetScores.Remove(member);
                    if (entry.ZSetByScore != null && entry.ZSetByScore.TryGetValue(score, out var set))
                    {
                        set.Remove(member);
                        if (set.Count == 0) entry.ZSetByScore.Remove(score);
                    }
                    removed++;
                }
            }

            if (entry.ZSetScores.Count == 0) _data.TryRemove(key, out _);
            return removed;
        }
    }

    /// <summary>
    /// Gets the cardinality of a sorted set.
    /// </summary>
    public long ZCard(string key)
    {
        var entry = Get(key);
        return entry?.ZSetScores?.Count ?? 0;
    }

    /// <summary>
    /// Increments the score of a member.
    /// </summary>
    public double ZIncrBy(string key, double delta, string member)
    {
        var entry = GetOrCreate(key, RedisDataType.ZSet);
        lock (entry.Lock)
        {
            entry.Type = RedisDataType.ZSet;
            entry.ZSetByScore ??= new SortedDictionary<double, HashSet<string>>();
            entry.ZSetScores ??= new Dictionary<string, double>();

            double oldScore = 0;
            if (entry.ZSetScores.TryGetValue(member, out oldScore))
            {
                if (entry.ZSetByScore.TryGetValue(oldScore, out var oldSet))
                {
                    oldSet.Remove(member);
                    if (oldSet.Count == 0) entry.ZSetByScore.Remove(oldScore);
                }
            }

            var newScore = oldScore + delta;
            if (!entry.ZSetByScore.TryGetValue(newScore, out var set))
            {
                set = new HashSet<string>();
                entry.ZSetByScore[newScore] = set;
            }
            set.Add(member);
            entry.ZSetScores[member] = newScore;

            return newScore;
        }
    }

    #endregion

    private static RedisEntry CreateEntry(RedisDataType type)
    {
        return new RedisEntry { Type = type };
    }

    private static void ClearOtherTypes(RedisEntry entry, RedisDataType keep)
    {
        if (keep != RedisDataType.String) entry.StringValue = null;
        if (keep != RedisDataType.List) entry.ListValue = null;
        if (keep != RedisDataType.Set) entry.SetValue = null;
        if (keep != RedisDataType.Hash) entry.HashValue = null;
        if (keep != RedisDataType.ZSet)
        {
            entry.ZSetByScore = null;
            entry.ZSetScores = null;
        }
    }

    private void CleanupExpired(object? state)
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _data
            .Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _data.TryRemove(key, out _);
        }
    }

    private static System.Text.RegularExpressions.Regex PatternToRegex(string pattern)
    {
        var escaped = System.Text.RegularExpressions.Regex.Escape(pattern);
        var regexPattern = escaped
            .Replace("\\*", ".*")
            .Replace("\\?", ".")
            .Replace("\\[", "[")
            .Replace("\\]", "]");
        return new System.Text.RegularExpressions.Regex($"^{regexPattern}$");
    }

    /// <summary>
    /// Disposes the data store.
    /// </summary>
    public void Dispose()
    {
        _expiryTimer.Dispose();
    }
}
