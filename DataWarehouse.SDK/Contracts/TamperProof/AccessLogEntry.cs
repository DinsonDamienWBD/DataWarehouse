// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Represents a single access log entry for tamper attribution and audit trails.
/// Captures complete information about who accessed what, when, and how.
/// </summary>
public sealed class AccessLogEntry
{
    /// <summary>
    /// Gets the unique identifier for this log entry.
    /// </summary>
    public required Guid EntryId { get; init; }

    /// <summary>
    /// Gets the unique identifier of the object being accessed.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Gets the type of access operation performed.
    /// </summary>
    public required AccessType AccessType { get; init; }

    /// <summary>
    /// Gets the principal (user, service account, etc.) performing the access.
    /// Format: "type:identifier" (e.g., "user:john.doe", "service:etl-pipeline").
    /// </summary>
    public required string Principal { get; init; }

    /// <summary>
    /// Gets the timestamp when the access occurred (UTC).
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets the session identifier for correlating multiple accesses in the same session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the client IP address that initiated the access.
    /// </summary>
    public string? ClientIp { get; init; }

    /// <summary>
    /// Gets the user agent string of the client application.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Gets whether the access operation succeeded.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Gets the error message if the access failed.
    /// Null if Succeeded is true.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the computed hash of the object after the operation (for Write/Correct operations).
    /// Null for read operations or failed writes.
    /// </summary>
    public string? ComputedHash { get; init; }

    /// <summary>
    /// Gets the duration of the operation in milliseconds.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Gets the number of bytes transferred during the operation.
    /// </summary>
    public long? BytesTransferred { get; init; }

    /// <summary>
    /// Gets additional context information for the access.
    /// Can include custom metadata like application version, request ID, etc.
    /// </summary>
    public Dictionary<string, object>? Context { get; init; }

    /// <summary>
    /// Validates the access log entry for correctness.
    /// </summary>
    /// <returns>True if the entry is valid; otherwise, false.</returns>
    public bool Validate()
    {
        if (EntryId == Guid.Empty) return false;
        if (ObjectId == Guid.Empty) return false;
        if (string.IsNullOrWhiteSpace(Principal)) return false;
        if (Timestamp == default) return false;
        if (!Succeeded && string.IsNullOrWhiteSpace(ErrorMessage)) return false;
        if (Succeeded && !string.IsNullOrWhiteSpace(ErrorMessage)) return false;

        // Write/Correct operations should have ComputedHash if successful
        if ((AccessType is AccessType.Write or AccessType.Correct) &&
            Succeeded &&
            string.IsNullOrWhiteSpace(ComputedHash))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Computes a cryptographic hash of this entry for integrity verification.
    /// The hash includes all fields except Context to ensure reproducibility.
    /// </summary>
    /// <param name="algorithm">Hash algorithm to use (default: SHA256).</param>
    /// <returns>Base64-encoded hash of the entry.</returns>
    public string ComputeEntryHash(HashAlgorithmType algorithm = HashAlgorithmType.SHA256)
    {
        var sb = new StringBuilder();
        sb.Append(EntryId.ToString("N"));
        sb.Append('|');
        sb.Append(ObjectId.ToString("N"));
        sb.Append('|');
        sb.Append((int)AccessType);
        sb.Append('|');
        sb.Append(Principal);
        sb.Append('|');
        sb.Append(Timestamp.ToUnixTimeMilliseconds());
        sb.Append('|');
        sb.Append(SessionId ?? string.Empty);
        sb.Append('|');
        sb.Append(ClientIp ?? string.Empty);
        sb.Append('|');
        sb.Append(UserAgent ?? string.Empty);
        sb.Append('|');
        sb.Append(Succeeded ? "1" : "0");
        sb.Append('|');
        sb.Append(ErrorMessage ?? string.Empty);
        sb.Append('|');
        sb.Append(ComputedHash ?? string.Empty);
        sb.Append('|');
        sb.Append(DurationMs?.ToString() ?? string.Empty);
        sb.Append('|');
        sb.Append(BytesTransferred?.ToString() ?? string.Empty);

        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());

        return algorithm switch
        {
            HashAlgorithmType.SHA256 => Convert.ToBase64String(SHA256.HashData(data)),
            HashAlgorithmType.SHA384 => Convert.ToBase64String(SHA384.HashData(data)),
            HashAlgorithmType.SHA512 => Convert.ToBase64String(SHA512.HashData(data)),
            HashAlgorithmType.Blake3 => ComputeBlake3Hash(data),
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}", nameof(algorithm))
        };
    }

    /// <summary>
    /// Creates an access log entry for a successful read operation.
    /// </summary>
    public static AccessLogEntry CreateRead(Guid objectId, string principal, long durationMs, long bytesTransferred)
    {
        return new AccessLogEntry
        {
            EntryId = Guid.NewGuid(),
            ObjectId = objectId,
            AccessType = AccessType.Read,
            Principal = principal,
            Timestamp = DateTimeOffset.UtcNow,
            Succeeded = true,
            DurationMs = durationMs,
            BytesTransferred = bytesTransferred
        };
    }

    /// <summary>
    /// Creates an access log entry for a successful write operation.
    /// </summary>
    public static AccessLogEntry CreateWrite(Guid objectId, string principal, string computedHash, long durationMs, long bytesTransferred)
    {
        return new AccessLogEntry
        {
            EntryId = Guid.NewGuid(),
            ObjectId = objectId,
            AccessType = AccessType.Write,
            Principal = principal,
            Timestamp = DateTimeOffset.UtcNow,
            Succeeded = true,
            ComputedHash = computedHash,
            DurationMs = durationMs,
            BytesTransferred = bytesTransferred
        };
    }

    /// <summary>
    /// Creates an access log entry for a failed operation.
    /// </summary>
    public static AccessLogEntry CreateFailed(Guid objectId, AccessType accessType, string principal, string errorMessage)
    {
        return new AccessLogEntry
        {
            EntryId = Guid.NewGuid(),
            ObjectId = objectId,
            AccessType = accessType,
            Principal = principal,
            Timestamp = DateTimeOffset.UtcNow,
            Succeeded = false,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Creates an access log entry for an administrative operation.
    /// </summary>
    public static AccessLogEntry CreateAdminOperation(Guid objectId, string principal, string operationDetails)
    {
        return new AccessLogEntry
        {
            EntryId = Guid.NewGuid(),
            ObjectId = objectId,
            AccessType = AccessType.AdminOperation,
            Principal = principal,
            Timestamp = DateTimeOffset.UtcNow,
            Succeeded = true,
            Context = new Dictionary<string, object> { ["Operation"] = operationDetails }
        };
    }

    private static string ComputeBlake3Hash(byte[] data)
    {
        // Blake3 is not available in standard .NET - fall back to SHA512 for now
        // In production, add Blake3.NET NuGet package and implement proper Blake3 hashing
        // For now, use SHA512 as a secure alternative
        return Convert.ToBase64String(SHA512.HashData(data));
    }
}

/// <summary>
/// Query parameters for searching and filtering access logs.
/// Supports complex queries across multiple dimensions.
/// </summary>
public sealed class AccessLogQuery
{
    /// <summary>
    /// Gets or sets the object ID to filter by (optional).
    /// </summary>
    public Guid? ObjectId { get; set; }

    /// <summary>
    /// Gets or sets the principal to filter by (optional).
    /// Supports wildcards (e.g., "user:*" for all users).
    /// </summary>
    public string? Principal { get; set; }

    /// <summary>
    /// Gets or sets the access types to filter by (optional).
    /// If empty or null, all access types are included.
    /// </summary>
    public HashSet<AccessType>? AccessTypes { get; set; }

    /// <summary>
    /// Gets or sets the start of the time range to query (inclusive).
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end of the time range to query (inclusive).
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>
    /// Gets or sets the session ID to filter by (optional).
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the client IP to filter by (optional).
    /// Supports CIDR notation (e.g., "192.168.1.0/24").
    /// </summary>
    public string? ClientIp { get; set; }

    /// <summary>
    /// Gets or sets whether to include only successful operations.
    /// Null includes both successful and failed operations.
    /// </summary>
    public bool? SucceededOnly { get; set; }

    /// <summary>
    /// Gets or sets whether to include only operations with computed hashes.
    /// Useful for filtering write/correct operations.
    /// </summary>
    public bool? WithHashOnly { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of results to return.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets the number of results to skip (for pagination).
    /// </summary>
    public int? Offset { get; set; }

    /// <summary>
    /// Gets or sets the sort order for results.
    /// Default is descending by timestamp (newest first).
    /// </summary>
    public AccessLogSortOrder SortOrder { get; set; } = AccessLogSortOrder.TimestampDescending;

    /// <summary>
    /// Creates a query for all accesses to a specific object.
    /// </summary>
    public static AccessLogQuery ForObject(Guid objectId)
    {
        return new AccessLogQuery { ObjectId = objectId };
    }

    /// <summary>
    /// Creates a query for all accesses by a specific principal.
    /// </summary>
    public static AccessLogQuery ForPrincipal(string principal)
    {
        return new AccessLogQuery { Principal = principal };
    }

    /// <summary>
    /// Creates a query for all accesses within a time range.
    /// </summary>
    public static AccessLogQuery ForTimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        return new AccessLogQuery
        {
            StartTime = start,
            EndTime = end
        };
    }

    /// <summary>
    /// Creates a query for suspicious write operations (successful writes with hashes).
    /// </summary>
    public static AccessLogQuery ForSuspiciousWrites()
    {
        return new AccessLogQuery
        {
            AccessTypes = new HashSet<AccessType> { AccessType.Write, AccessType.Correct },
            SucceededOnly = true,
            WithHashOnly = true
        };
    }

    /// <summary>
    /// Validates the query parameters for correctness.
    /// </summary>
    /// <returns>True if the query is valid; otherwise, false.</returns>
    public bool Validate()
    {
        if (StartTime.HasValue && EndTime.HasValue && StartTime > EndTime)
            return false;

        if (Limit.HasValue && Limit.Value <= 0)
            return false;

        if (Offset.HasValue && Offset.Value < 0)
            return false;

        return true;
    }
}

/// <summary>
/// Sort order for access log query results.
/// </summary>
public enum AccessLogSortOrder
{
    /// <summary>Sort by timestamp, newest first (default).</summary>
    TimestampDescending,

    /// <summary>Sort by timestamp, oldest first.</summary>
    TimestampAscending,

    /// <summary>Sort by principal name alphabetically.</summary>
    PrincipalAscending,

    /// <summary>Sort by access type.</summary>
    AccessTypeAscending,

    /// <summary>Sort by duration, longest first.</summary>
    DurationDescending
}

/// <summary>
/// Summarized access statistics for a specific object.
/// Provides aggregate view of access patterns.
/// </summary>
public sealed class AccessLogSummary
{
    /// <summary>
    /// Gets the object ID this summary is for.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Gets the total number of access operations.
    /// </summary>
    public required int TotalAccesses { get; init; }

    /// <summary>
    /// Gets the number of successful accesses.
    /// </summary>
    public required int SuccessfulAccesses { get; init; }

    /// <summary>
    /// Gets the number of failed accesses.
    /// </summary>
    public required int FailedAccesses { get; init; }

    /// <summary>
    /// Gets the breakdown of accesses by type.
    /// </summary>
    public required Dictionary<AccessType, int> AccessesByType { get; init; }

    /// <summary>
    /// Gets the number of unique principals who accessed the object.
    /// </summary>
    public required int UniquePrincipals { get; init; }

    /// <summary>
    /// Gets the top principals by access count (principal -> count).
    /// </summary>
    public required Dictionary<string, int> TopPrincipals { get; init; }

    /// <summary>
    /// Gets the timestamp of the first recorded access.
    /// </summary>
    public required DateTimeOffset FirstAccess { get; init; }

    /// <summary>
    /// Gets the timestamp of the most recent access.
    /// </summary>
    public required DateTimeOffset LastAccess { get; init; }

    /// <summary>
    /// Gets the total bytes transferred across all operations.
    /// </summary>
    public long TotalBytesTransferred { get; init; }

    /// <summary>
    /// Gets the average operation duration in milliseconds.
    /// </summary>
    public double AverageDurationMs { get; init; }

    /// <summary>
    /// Gets the number of write operations that included a computed hash.
    /// </summary>
    public int WritesWithHash { get; init; }

    /// <summary>
    /// Gets whether any suspicious patterns were detected.
    /// </summary>
    public bool HasSuspiciousPatterns { get; init; }

    /// <summary>
    /// Gets the list of suspicious patterns detected (if any).
    /// </summary>
    public List<string>? SuspiciousPatterns { get; init; }

    /// <summary>
    /// Creates an empty access log summary for a new object.
    /// </summary>
    public static AccessLogSummary CreateEmpty(Guid objectId)
    {
        return new AccessLogSummary
        {
            ObjectId = objectId,
            TotalAccesses = 0,
            SuccessfulAccesses = 0,
            FailedAccesses = 0,
            AccessesByType = new Dictionary<AccessType, int>(),
            UniquePrincipals = 0,
            TopPrincipals = new Dictionary<string, int>(),
            FirstAccess = DateTimeOffset.MinValue,
            LastAccess = DateTimeOffset.MinValue
        };
    }

    /// <summary>
    /// Computes a summary from a collection of access log entries.
    /// </summary>
    public static AccessLogSummary FromEntries(Guid objectId, IEnumerable<AccessLogEntry> entries)
    {
        var entriesList = entries.ToList();

        if (entriesList.Count == 0)
            return CreateEmpty(objectId);

        var accessesByType = new Dictionary<AccessType, int>();
        var principalCounts = new Dictionary<string, int>();
        long totalBytes = 0;
        double totalDuration = 0;
        int durationCount = 0;
        int writesWithHash = 0;
        var suspiciousPatterns = new List<string>();

        foreach (var entry in entriesList)
        {
            // Count by type
            if (!accessesByType.ContainsKey(entry.AccessType))
                accessesByType[entry.AccessType] = 0;
            accessesByType[entry.AccessType]++;

            // Count by principal
            if (!principalCounts.ContainsKey(entry.Principal))
                principalCounts[entry.Principal] = 0;
            principalCounts[entry.Principal]++;

            // Aggregate metrics
            if (entry.BytesTransferred.HasValue)
                totalBytes += entry.BytesTransferred.Value;

            if (entry.DurationMs.HasValue)
            {
                totalDuration += entry.DurationMs.Value;
                durationCount++;
            }

            if ((entry.AccessType is AccessType.Write or AccessType.Correct) &&
                !string.IsNullOrWhiteSpace(entry.ComputedHash))
            {
                writesWithHash++;
            }
        }

        // Detect suspicious patterns
        DetectSuspiciousPatterns(entriesList, principalCounts, suspiciousPatterns);

        var topPrincipals = principalCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(10)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return new AccessLogSummary
        {
            ObjectId = objectId,
            TotalAccesses = entriesList.Count,
            SuccessfulAccesses = entriesList.Count(e => e.Succeeded),
            FailedAccesses = entriesList.Count(e => !e.Succeeded),
            AccessesByType = accessesByType,
            UniquePrincipals = principalCounts.Count,
            TopPrincipals = topPrincipals,
            FirstAccess = entriesList.Min(e => e.Timestamp),
            LastAccess = entriesList.Max(e => e.Timestamp),
            TotalBytesTransferred = totalBytes,
            AverageDurationMs = durationCount > 0 ? totalDuration / durationCount : 0,
            WritesWithHash = writesWithHash,
            HasSuspiciousPatterns = suspiciousPatterns.Count > 0,
            SuspiciousPatterns = suspiciousPatterns.Count > 0 ? suspiciousPatterns : null
        };
    }

    private static void DetectSuspiciousPatterns(
        List<AccessLogEntry> entries,
        Dictionary<string, int> principalCounts,
        List<string> patterns)
    {
        // Pattern: Single principal responsible for all failed accesses
        var failedEntries = entries.Where(e => !e.Succeeded).ToList();
        if (failedEntries.Count > 5)
        {
            var failedByPrincipal = failedEntries.GroupBy(e => e.Principal).ToList();
            var singlePrincipalFailed = failedByPrincipal.FirstOrDefault(g => g.Count() == failedEntries.Count);
            if (singlePrincipalFailed != null)
            {
                patterns.Add($"All {failedEntries.Count} failed accesses by single principal: {singlePrincipalFailed.Key}");
            }
        }

        // Pattern: Unusual access frequency (more than 100 accesses by one principal)
        var highFrequencyPrincipals = principalCounts.Where(kvp => kvp.Value > 100).ToList();
        foreach (var kvp in highFrequencyPrincipals)
        {
            patterns.Add($"High access frequency: {kvp.Key} accessed {kvp.Value} times");
        }

        // Pattern: Access from multiple IPs by same principal
        var entriesByPrincipal = entries.GroupBy(e => e.Principal);
        foreach (var group in entriesByPrincipal)
        {
            var uniqueIps = group.Where(e => !string.IsNullOrWhiteSpace(e.ClientIp))
                                 .Select(e => e.ClientIp)
                                 .Distinct()
                                 .Count();
            if (uniqueIps > 5)
            {
                patterns.Add($"Principal {group.Key} accessed from {uniqueIps} different IPs");
            }
        }

        // Pattern: Administrative operations outside business hours
        var adminOps = entries.Where(e => e.AccessType == AccessType.AdminOperation).ToList();
        var afterHoursAdmin = adminOps.Where(e =>
        {
            var hour = e.Timestamp.Hour;
            return hour < 6 || hour > 22; // Before 6 AM or after 10 PM
        }).ToList();

        if (afterHoursAdmin.Count > 0)
        {
            patterns.Add($"{afterHoursAdmin.Count} administrative operations performed outside business hours");
        }
    }
}

/// <summary>
/// Analysis result for suspicious access patterns related to potential tampering.
/// Provides attribution confidence and evidence for security investigation.
/// </summary>
public sealed class SuspiciousAccessAnalysis
{
    /// <summary>
    /// Gets the object ID that was potentially tampered with.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Gets the expected hash of the object (from manifest or WORM).
    /// </summary>
    public required string ExpectedHash { get; init; }

    /// <summary>
    /// Gets the actual hash found (indicating tampering).
    /// </summary>
    public required string ActualHash { get; init; }

    /// <summary>
    /// Gets the timestamp when the tampering was detected.
    /// </summary>
    public required DateTimeOffset DetectionTime { get; init; }

    /// <summary>
    /// Gets the confidence level of the attribution analysis.
    /// </summary>
    public required AttributionConfidence Confidence { get; init; }

    /// <summary>
    /// Gets the list of suspected principals, ordered by likelihood.
    /// Empty if Confidence is Unknown.
    /// </summary>
    public required List<SuspectedPrincipal> SuspectedPrincipals { get; init; }

    /// <summary>
    /// Gets the time window during which tampering likely occurred.
    /// </summary>
    public required TimeWindow TamperingWindow { get; init; }

    /// <summary>
    /// Gets the list of corroborating evidence for the analysis.
    /// </summary>
    public required List<string> Evidence { get; init; }

    /// <summary>
    /// Gets the list of access log entries analyzed.
    /// </summary>
    public required List<AccessLogEntry> AnalyzedEntries { get; init; }

    /// <summary>
    /// Gets recommended actions based on the analysis.
    /// </summary>
    public List<string>? RecommendedActions { get; init; }

    /// <summary>
    /// Validates the analysis for correctness.
    /// </summary>
    public bool Validate()
    {
        if (ObjectId == Guid.Empty) return false;
        if (string.IsNullOrWhiteSpace(ExpectedHash)) return false;
        if (string.IsNullOrWhiteSpace(ActualHash)) return false;
        if (ExpectedHash == ActualHash) return false; // Not tampering if hashes match
        if (DetectionTime == default) return false;
        if (Evidence == null || Evidence.Count == 0) return false;
        if (AnalyzedEntries == null) return false;

        // If confidence is not Unknown, should have suspects
        if (Confidence != AttributionConfidence.Unknown &&
            (SuspectedPrincipals == null || SuspectedPrincipals.Count == 0))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates an analysis result indicating unknown attribution.
    /// </summary>
    public static SuspiciousAccessAnalysis CreateUnknown(
        Guid objectId,
        string expectedHash,
        string actualHash,
        DateTimeOffset detectionTime,
        string reason)
    {
        return new SuspiciousAccessAnalysis
        {
            ObjectId = objectId,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            DetectionTime = detectionTime,
            Confidence = AttributionConfidence.Unknown,
            SuspectedPrincipals = new List<SuspectedPrincipal>(),
            TamperingWindow = new TimeWindow
            {
                Start = DateTimeOffset.MinValue,
                End = detectionTime
            },
            Evidence = new List<string> { reason },
            AnalyzedEntries = new List<AccessLogEntry>(),
            RecommendedActions = new List<string>
            {
                "Review physical access logs",
                "Check system integrity",
                "Investigate network security"
            }
        };
    }

    /// <summary>
    /// Gets a human-readable summary of the analysis.
    /// </summary>
    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tampering Detection Analysis for Object {ObjectId}");
        sb.AppendLine($"Detection Time: {DetectionTime:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"Confidence: {Confidence}");
        sb.AppendLine();
        sb.AppendLine($"Expected Hash: {ExpectedHash}");
        sb.AppendLine($"Actual Hash:   {ActualHash}");
        sb.AppendLine();

        if (SuspectedPrincipals.Count > 0)
        {
            sb.AppendLine("Suspected Principals:");
            foreach (var suspect in SuspectedPrincipals)
            {
                sb.AppendLine($"  - {suspect.Principal} (Likelihood: {suspect.Likelihood:P})");
                sb.AppendLine($"    Last Access: {suspect.LastAccessTime:yyyy-MM-dd HH:mm:ss UTC}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Tampering Window: {TamperingWindow.Start:yyyy-MM-dd HH:mm:ss} to {TamperingWindow.End:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        if (Evidence.Count > 0)
        {
            sb.AppendLine("Evidence:");
            foreach (var item in Evidence)
            {
                sb.AppendLine($"  - {item}");
            }
            sb.AppendLine();
        }

        if (RecommendedActions != null && RecommendedActions.Count > 0)
        {
            sb.AppendLine("Recommended Actions:");
            foreach (var action in RecommendedActions)
            {
                sb.AppendLine($"  - {action}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Represents a suspected principal in tampering attribution analysis.
/// </summary>
public sealed class SuspectedPrincipal
{
    /// <summary>
    /// Gets the principal identifier.
    /// </summary>
    public required string Principal { get; init; }

    /// <summary>
    /// Gets the likelihood that this principal is responsible (0.0 to 1.0).
    /// </summary>
    public required double Likelihood { get; init; }

    /// <summary>
    /// Gets the timestamp of the last access by this principal.
    /// </summary>
    public required DateTimeOffset LastAccessTime { get; init; }

    /// <summary>
    /// Gets the access types performed by this principal.
    /// </summary>
    public required List<AccessType> AccessTypes { get; init; }

    /// <summary>
    /// Gets corroborating evidence specific to this principal.
    /// </summary>
    public List<string>? SpecificEvidence { get; init; }
}

/// <summary>
/// Represents a time window for analysis purposes.
/// </summary>
public sealed class TimeWindow
{
    /// <summary>
    /// Gets the start of the time window (inclusive).
    /// </summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>
    /// Gets the end of the time window (inclusive).
    /// </summary>
    public required DateTimeOffset End { get; init; }

    /// <summary>
    /// Gets the duration of the time window.
    /// </summary>
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// Determines if a timestamp falls within this window.
    /// </summary>
    public bool Contains(DateTimeOffset timestamp)
    {
        return timestamp >= Start && timestamp <= End;
    }
}
