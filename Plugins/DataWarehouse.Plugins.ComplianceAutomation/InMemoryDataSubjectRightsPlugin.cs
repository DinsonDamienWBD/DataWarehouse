using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.ComplianceAutomation;

/// <summary>
/// In-memory Data Subject Rights (DSR) implementation for GDPR/CCPA/LGPD compliance.
/// Implements right to access, erasure, rectification, portability, and restriction.
/// </summary>
public class InMemoryDataSubjectRightsPlugin : DataSubjectRightsPluginBase
{
    private readonly ConcurrentDictionary<string, SubjectData> _subjectData = new();
    private readonly ConcurrentDictionary<string, ConsentRecord> _consents = new();
    private readonly ConcurrentDictionary<string, ProcessingRestriction> _restrictions = new();
    private readonly ConcurrentBag<DsrRequest> _requestLog = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.compliance.dsr.inmemory";

    /// <inheritdoc />
    public override string Name => "In-Memory Data Subject Rights";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Adds or updates personal data for a subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <param name="category">Data category (e.g., "profile", "transactions").</param>
    /// <param name="data">Data to store.</param>
    public void SetSubjectData(string subjectId, string category, Dictionary<string, object> data)
    {
        var subject = _subjectData.GetOrAdd(subjectId, _ => new SubjectData());
        subject.Categories[category] = data;
        subject.LastUpdated = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Gets all data categories for a subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <returns>List of data categories.</returns>
    public IReadOnlyList<string> GetSubjectCategories(string subjectId)
    {
        return _subjectData.TryGetValue(subjectId, out var data)
            ? data.Categories.Keys.ToList()
            : Array.Empty<string>();
    }

    /// <inheritdoc />
    protected override Task<byte[]> CollectSubjectDataAsync(string subjectId, DataExportOptions options)
    {
        LogRequest(subjectId, "Export", options.Format);

        if (!_subjectData.TryGetValue(subjectId, out var subject))
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        // Filter categories if specified
        var categoriesToExport = options.Categories.Length > 0
            ? subject.Categories.Where(c => options.Categories.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value)
            : subject.Categories.ToDictionary(c => c.Key, c => c.Value);

        // Add metadata if requested
        if (options.IncludeMetadata)
        {
            categoriesToExport["_metadata"] = new Dictionary<string, object>
            {
                ["subjectId"] = subjectId,
                ["exportedAt"] = DateTimeOffset.UtcNow.ToString("o"),
                ["categories"] = categoriesToExport.Keys.Where(k => k != "_metadata").ToArray(),
                ["consent"] = _consents.TryGetValue(subjectId, out var consent) ? consent : null!
            };
        }

        // Serialize based on format
        var data = options.Format.ToLowerInvariant() switch
        {
            "json" => SerializeToJson(categoriesToExport),
            "xml" => SerializeToXml(categoriesToExport),
            "csv" => SerializeToCsv(categoriesToExport),
            _ => SerializeToJson(categoriesToExport)
        };

        return Task.FromResult(data);
    }

    /// <inheritdoc />
    protected override Task<int> DeleteSubjectDataAsync(string subjectId, DeletionOptions options)
    {
        LogRequest(subjectId, "Delete", options.Cascade.ToString());

        var deleted = 0;

        // Remove subject data
        if (_subjectData.TryRemove(subjectId, out var data))
        {
            deleted += data.Categories.Count;
        }

        // Remove consents
        if (_consents.TryRemove(subjectId, out _))
        {
            deleted++;
        }

        // Remove restrictions
        if (_restrictions.TryRemove(subjectId, out _))
        {
            deleted++;
        }

        return Task.FromResult(deleted);
    }

    /// <inheritdoc />
    protected override Task<int> UpdateSubjectDataAsync(string subjectId, Dictionary<string, object> corrections)
    {
        LogRequest(subjectId, "Rectify", string.Join(",", corrections.Keys));

        if (!_subjectData.TryGetValue(subjectId, out var subject))
        {
            return Task.FromResult(0);
        }

        var corrected = 0;

        foreach (var (field, value) in corrections)
        {
            // Parse field path (e.g., "profile.email" or "profile:email")
            var parts = field.Split(new[] { '.', ':' }, 2);
            if (parts.Length == 2)
            {
                var category = parts[0];
                var fieldName = parts[1];

                if (subject.Categories.TryGetValue(category, out var categoryData))
                {
                    categoryData[fieldName] = value;
                    corrected++;
                }
            }
        }

        if (corrected > 0)
        {
            subject.LastUpdated = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(corrected);
    }

    /// <inheritdoc />
    protected override Task SetProcessingRestrictionAsync(string subjectId, bool restricted, string reason)
    {
        LogRequest(subjectId, restricted ? "Restrict" : "Unrestrict", reason);

        if (restricted)
        {
            _restrictions[subjectId] = new ProcessingRestriction(
                subjectId,
                reason,
                DateTimeOffset.UtcNow
            );
        }
        else
        {
            _restrictions.TryRemove(subjectId, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task StoreConsentAsync(ConsentRecord record)
    {
        LogRequest(record.SubjectId, "Consent", record.Details.Purpose);

        _consents[record.SubjectId] = record;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks if processing is restricted for a subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <returns>True if processing is restricted.</returns>
    public bool IsProcessingRestricted(string subjectId)
    {
        return _restrictions.ContainsKey(subjectId);
    }

    /// <summary>
    /// Gets the restriction details for a subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <returns>Restriction details or null.</returns>
    public ProcessingRestriction? GetRestriction(string subjectId)
    {
        return _restrictions.TryGetValue(subjectId, out var restriction) ? restriction : null;
    }

    /// <summary>
    /// Gets consent record for a subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <returns>Consent record or null.</returns>
    public ConsentRecord? GetConsent(string subjectId)
    {
        return _consents.TryGetValue(subjectId, out var consent) ? consent : null;
    }

    /// <summary>
    /// Gets all DSR requests for audit purposes.
    /// </summary>
    /// <returns>All logged requests.</returns>
    public IReadOnlyList<DsrRequest> GetRequestLog()
    {
        return _requestLog.OrderByDescending(r => r.Timestamp).ToList();
    }

    /// <summary>
    /// Gets DSR requests for a specific subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <returns>Requests for the subject.</returns>
    public IReadOnlyList<DsrRequest> GetRequestsForSubject(string subjectId)
    {
        return _requestLog
            .Where(r => r.SubjectId == subjectId)
            .OrderByDescending(r => r.Timestamp)
            .ToList();
    }

    private void LogRequest(string subjectId, string requestType, string details)
    {
        _requestLog.Add(new DsrRequest(
            $"DSR-{Guid.NewGuid():N}",
            subjectId,
            requestType,
            details,
            DateTimeOffset.UtcNow
        ));
    }

    private static byte[] SerializeToJson(Dictionary<string, Dictionary<string, object>> data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        return Encoding.UTF8.GetBytes(json);
    }

    private static byte[] SerializeToXml(Dictionary<string, Dictionary<string, object>> data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<SubjectData>");

        foreach (var (category, fields) in data)
        {
            sb.AppendLine($"  <{EscapeXmlTag(category)}>");
            foreach (var (field, value) in fields)
            {
                sb.AppendLine($"    <{EscapeXmlTag(field)}>{EscapeXmlValue(value?.ToString() ?? "")}</{EscapeXmlTag(field)}>");
            }
            sb.AppendLine($"  </{EscapeXmlTag(category)}>");
        }

        sb.AppendLine("</SubjectData>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] SerializeToCsv(Dictionary<string, Dictionary<string, object>> data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Category,Field,Value");

        foreach (var (category, fields) in data)
        {
            foreach (var (field, value) in fields)
            {
                sb.AppendLine($"\"{EscapeCsvValue(category)}\",\"{EscapeCsvValue(field)}\",\"{EscapeCsvValue(value?.ToString() ?? "")}\"");
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string EscapeXmlTag(string tag)
    {
        return new string(tag.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    private static string EscapeXmlValue(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string EscapeCsvValue(string value)
    {
        return value.Replace("\"", "\"\"");
    }

    private class SubjectData
    {
        public ConcurrentDictionary<string, Dictionary<string, object>> Categories { get; } = new();
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Processing restriction record.
    /// </summary>
    public record ProcessingRestriction(string SubjectId, string Reason, DateTimeOffset RestrictedAt);

    /// <summary>
    /// DSR request log entry.
    /// </summary>
    public record DsrRequest(string RequestId, string SubjectId, string RequestType, string Details, DateTimeOffset Timestamp);
}
