using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Services;

/// <summary>
/// Smart Folder service providing dynamic folder views based on metadata queries.
/// Smart folders don't contain files directly but display files matching criteria.
/// </summary>
public sealed class SmartFolderService : IDisposable
{
    private readonly ConcurrentDictionary<string, SmartFolder> _folders = new();
    private readonly ConcurrentDictionary<string, SmartFolderIndex> _indices = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a new smart folder with the specified criteria.
    /// </summary>
    /// <param name="name">Unique name for the smart folder.</param>
    /// <param name="criteria">Search criteria for matching files.</param>
    /// <returns>The created smart folder.</returns>
    public SmartFolder CreateFolder(string name, SmartFolderCriteria criteria)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Folder name cannot be empty", nameof(name));

        var folder = new SmartFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Criteria = criteria ?? throw new ArgumentNullException(nameof(criteria)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (!_folders.TryAdd(folder.Id, folder))
            throw new InvalidOperationException("Failed to create smart folder");

        return folder;
    }

    /// <summary>
    /// Gets a smart folder by ID.
    /// </summary>
    public SmartFolder? GetFolder(string folderId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _folders.TryGetValue(folderId, out var folder) ? folder : null;
    }

    /// <summary>
    /// Lists all smart folders.
    /// </summary>
    public IEnumerable<SmartFolder> ListFolders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _folders.Values.ToList();
    }

    /// <summary>
    /// Deletes a smart folder.
    /// </summary>
    public bool DeleteFolder(string folderId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _folders.TryRemove(folderId, out _);
    }

    /// <summary>
    /// Updates the criteria for a smart folder.
    /// </summary>
    public bool UpdateCriteria(string folderId, SmartFolderCriteria newCriteria)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_folders.TryGetValue(folderId, out var folder))
            return false;

        folder.Criteria = newCriteria;
        folder.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Indexes a file for smart folder matching.
    /// </summary>
    public async Task IndexFileAsync(string fileId, Dictionary<string, object> metadata, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _indexLock.WaitAsync(ct);
        try
        {
            _indices[fileId] = new SmartFolderIndex
            {
                FileId = fileId,
                Metadata = metadata,
                IndexedAt = DateTime.UtcNow
            };
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Removes a file from the index.
    /// </summary>
    public void RemoveFromIndex(string fileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _indices.TryRemove(fileId, out _);
    }

    /// <summary>
    /// Evaluates which files match a smart folder's criteria.
    /// </summary>
    public IEnumerable<string> GetMatchingFiles(string folderId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_folders.TryGetValue(folderId, out var folder))
            return Enumerable.Empty<string>();

        return _indices.Values
            .Where(idx => MatchesCriteria(idx.Metadata, folder.Criteria))
            .Select(idx => idx.FileId)
            .ToList();
    }

    /// <summary>
    /// Gets all smart folders that a file belongs to.
    /// </summary>
    public IEnumerable<SmartFolder> GetFoldersForFile(string fileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_indices.TryGetValue(fileId, out var index))
            return Enumerable.Empty<SmartFolder>();

        return _folders.Values
            .Where(f => MatchesCriteria(index.Metadata, f.Criteria))
            .ToList();
    }

    private static bool MatchesCriteria(Dictionary<string, object> metadata, SmartFolderCriteria criteria)
    {
        // Check file type filter
        if (criteria.FileTypes != null && criteria.FileTypes.Length > 0)
        {
            if (!metadata.TryGetValue("fileType", out var fileType) ||
                !criteria.FileTypes.Contains(fileType?.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Check date range
        if (criteria.CreatedAfter.HasValue || criteria.CreatedBefore.HasValue)
        {
            if (!metadata.TryGetValue("createdAt", out var createdAtObj) ||
                createdAtObj is not DateTime createdAt)
            {
                return false;
            }

            if (criteria.CreatedAfter.HasValue && createdAt < criteria.CreatedAfter.Value)
                return false;
            if (criteria.CreatedBefore.HasValue && createdAt > criteria.CreatedBefore.Value)
                return false;
        }

        // Check size range
        if (criteria.MinSizeBytes.HasValue || criteria.MaxSizeBytes.HasValue)
        {
            if (!metadata.TryGetValue("size", out var sizeObj) ||
                !long.TryParse(sizeObj?.ToString(), out var size))
            {
                return false;
            }

            if (criteria.MinSizeBytes.HasValue && size < criteria.MinSizeBytes.Value)
                return false;
            if (criteria.MaxSizeBytes.HasValue && size > criteria.MaxSizeBytes.Value)
                return false;
        }

        // Check tags
        if (criteria.Tags != null && criteria.Tags.Length > 0)
        {
            if (!metadata.TryGetValue("tags", out var tagsObj))
                return false;

            var fileTags = tagsObj switch
            {
                string[] arr => arr,
                IEnumerable<string> enumerable => enumerable.ToArray(),
                string str => str.Split(',', StringSplitOptions.RemoveEmptyEntries),
                _ => Array.Empty<string>()
            };

            if (criteria.TagMatchMode == TagMatchMode.All)
            {
                if (!criteria.Tags.All(t => fileTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    return false;
            }
            else
            {
                if (!criteria.Tags.Any(t => fileTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    return false;
            }
        }

        // Check custom metadata conditions
        if (criteria.MetadataConditions != null)
        {
            foreach (var condition in criteria.MetadataConditions)
            {
                if (!metadata.TryGetValue(condition.Key, out var value))
                {
                    if (condition.Operator != ConditionOperator.NotExists)
                        return false;
                    continue;
                }

                if (!EvaluateCondition(value, condition))
                    return false;
            }
        }

        // Check name pattern
        if (!string.IsNullOrEmpty(criteria.NamePattern))
        {
            if (!metadata.TryGetValue("name", out var nameObj))
                return false;

            var name = nameObj?.ToString() ?? "";
            var regex = new Regex(
                "^" + Regex.Escape(criteria.NamePattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase);

            if (!regex.IsMatch(name))
                return false;
        }

        return true;
    }

    private static bool EvaluateCondition(object value, MetadataCondition condition)
    {
        var stringValue = value?.ToString() ?? "";

        return condition.Operator switch
        {
            ConditionOperator.Equals => stringValue.Equals(condition.Value?.ToString(), StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !stringValue.Equals(condition.Value?.ToString(), StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => stringValue.Contains(condition.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
            ConditionOperator.StartsWith => stringValue.StartsWith(condition.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
            ConditionOperator.EndsWith => stringValue.EndsWith(condition.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase),
            ConditionOperator.GreaterThan => CompareNumeric(value, condition.Value) > 0,
            ConditionOperator.LessThan => CompareNumeric(value, condition.Value) < 0,
            ConditionOperator.Exists => true,
            ConditionOperator.NotExists => false,
            ConditionOperator.Regex => Regex.IsMatch(stringValue, condition.Value?.ToString() ?? ""),
            _ => false
        };
    }

    private static int CompareNumeric(object? a, object? b)
    {
        if (double.TryParse(a?.ToString(), out var numA) && double.TryParse(b?.ToString(), out var numB))
            return numA.CompareTo(numB);
        return string.Compare(a?.ToString(), b?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _indexLock.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Represents a smart folder that dynamically groups files based on criteria.
/// </summary>
public class SmartFolder
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SmartFolderCriteria Criteria { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Criteria for matching files to a smart folder.
/// </summary>
public class SmartFolderCriteria
{
    public string? NamePattern { get; set; }
    public string[]? FileTypes { get; set; }
    public string[]? Tags { get; set; }
    public TagMatchMode TagMatchMode { get; set; } = TagMatchMode.Any;
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public DateTime? ModifiedAfter { get; set; }
    public DateTime? ModifiedBefore { get; set; }
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }
    public List<MetadataCondition>? MetadataConditions { get; set; }
}

/// <summary>
/// Condition for matching metadata values.
/// </summary>
public class MetadataCondition
{
    public string Key { get; set; } = string.Empty;
    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;
    public object? Value { get; set; }
}

/// <summary>
/// Operators for metadata conditions.
/// </summary>
public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    LessThan,
    Exists,
    NotExists,
    Regex
}

/// <summary>
/// How tags should be matched.
/// </summary>
public enum TagMatchMode
{
    Any,
    All
}

internal class SmartFolderIndex
{
    public string FileId { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime IndexedAt { get; set; }
}
