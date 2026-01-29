// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Defines a provider for access log storage and retrieval for tamper attribution.
/// Tracks all access operations to objects and provides forensic analysis capabilities
/// to identify who potentially tampered with data when corruption or tampering is detected.
/// </summary>
public interface IAccessLogProvider
{
    /// <summary>
    /// Records an access operation to the log for audit trail and tamper attribution.
    /// This method should be called for every read, write, or administrative operation
    /// on objects in the system.
    /// </summary>
    /// <param name="entry">The access log entry containing all details of the operation.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <returns>A task that completes when the log entry has been persisted.</returns>
    /// <exception cref="ArgumentNullException">Thrown if entry is null.</exception>
    /// <exception cref="ArgumentException">Thrown if entry fails validation.</exception>
    Task LogAccessAsync(AccessLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the access history for a specific object within a time range.
    /// Useful for auditing and understanding the complete access pattern for an object.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to query.</param>
    /// <param name="from">Start of the time range (inclusive).</param>
    /// <param name="to">End of the time range (inclusive).</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <returns>A read-only list of access log entries, ordered by timestamp (newest first).</returns>
    Task<IReadOnlyList<AccessLogEntry>> GetAccessHistoryAsync(
        Guid objectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a flexible query against the access logs.
    /// Supports filtering by multiple dimensions including principal, access type,
    /// session, client IP, and success status.
    /// </summary>
    /// <param name="query">The query parameters defining the search criteria.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <returns>A read-only list of access log entries matching the query criteria.</returns>
    /// <exception cref="ArgumentNullException">Thrown if query is null.</exception>
    /// <exception cref="ArgumentException">Thrown if query fails validation.</exception>
    Task<IReadOnlyList<AccessLogEntry>> QueryAsync(
        AccessLogQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves suspicious access patterns for tamper attribution analysis.
    /// Returns accesses that occurred within the lookback window before tampering was detected,
    /// with focus on write operations and privileged access.
    /// </summary>
    /// <param name="objectId">The object ID where tampering was detected.</param>
    /// <param name="tamperDetectedAt">The timestamp when tampering was discovered.</param>
    /// <param name="lookbackWindow">How far back to search for suspicious access (e.g., 24 hours).</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <returns>A read-only list of potentially suspicious access log entries.</returns>
    Task<IReadOnlyList<AccessLogEntry>> QuerySuspiciousAccessAsync(
        Guid objectId,
        DateTimeOffset tamperDetectedAt,
        TimeSpan lookbackWindow,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a statistical summary of access patterns for a specific object.
    /// Provides aggregated information about who accessed the object, how often,
    /// and whether any suspicious patterns were detected.
    /// </summary>
    /// <param name="objectId">The object ID to summarize access for.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <returns>An access log summary containing aggregate statistics.</returns>
    Task<AccessLogSummary> GetSummaryAsync(
        Guid objectId,
        CancellationToken ct = default);

    /// <summary>
    /// Performs comprehensive attribution analysis when tampering is detected.
    /// Combines access log queries, pattern analysis, and behavioral heuristics
    /// to identify the most likely principals responsible for the tampering.
    /// Returns confidence-scored suspects with supporting evidence.
    /// </summary>
    /// <param name="objectId">The object ID where tampering was detected.</param>
    /// <param name="tamperDetectedAt">The timestamp when tampering was discovered.</param>
    /// <param name="lookbackWindow">How far back to analyze access patterns (e.g., 72 hours).</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <returns>A suspicious access analysis with attributed principals and evidence.</returns>
    Task<SuspiciousAccessAnalysis> AnalyzeForAttributionAsync(
        Guid objectId,
        DateTimeOffset tamperDetectedAt,
        TimeSpan lookbackWindow,
        CancellationToken ct = default);

    /// <summary>
    /// Purges access log entries older than the specified timestamp.
    /// Use this method for compliance with data retention policies or to manage storage costs.
    /// WARNING: Purging logs will permanently remove forensic evidence. Ensure compliance
    /// requirements are met before purging.
    /// </summary>
    /// <param name="olderThan">Delete all log entries with timestamps before this value.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    /// <returns>A task that completes when the purge operation finishes.</returns>
    Task PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for access log provider plugins.
/// Extends FeaturePluginBase to provide lifecycle management and implements IAccessLogProvider
/// with default implementations for attribution analysis.
/// Derived classes only need to implement the persistence layer (PersistLogEntryAsync and QueryLogsAsync).
/// </summary>
public abstract class AccessLogProviderPluginBase : FeaturePluginBase, IAccessLogProvider
{
    /// <summary>
    /// Category is always FeatureProvider for access log plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Records an access operation to the log. Validates the entry and delegates to PersistLogEntryAsync.
    /// </summary>
    /// <param name="entry">The access log entry to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is persisted.</returns>
    /// <exception cref="ArgumentNullException">Thrown if entry is null.</exception>
    /// <exception cref="ArgumentException">Thrown if entry fails validation.</exception>
    public virtual async Task LogAccessAsync(AccessLogEntry entry, CancellationToken ct = default)
    {
        if (entry == null)
            throw new ArgumentNullException(nameof(entry));

        if (!entry.Validate())
            throw new ArgumentException("Access log entry failed validation.", nameof(entry));

        await PersistLogEntryAsync(entry, ct);
    }

    /// <summary>
    /// Retrieves access history for an object within a time range.
    /// Constructs a query and delegates to QueryLogsAsync.
    /// </summary>
    public virtual Task<IReadOnlyList<AccessLogEntry>> GetAccessHistoryAsync(
        Guid objectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var query = new AccessLogQuery
        {
            ObjectId = objectId,
            StartTime = from,
            EndTime = to,
            SortOrder = AccessLogSortOrder.TimestampDescending
        };

        return QueryLogsAsync(query, ct);
    }

    /// <summary>
    /// Executes a flexible query against the access logs.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if query is null.</exception>
    /// <exception cref="ArgumentException">Thrown if query fails validation.</exception>
    public virtual Task<IReadOnlyList<AccessLogEntry>> QueryAsync(
        AccessLogQuery query,
        CancellationToken ct = default)
    {
        if (query == null)
            throw new ArgumentNullException(nameof(query));

        if (!query.Validate())
            throw new ArgumentException("Access log query failed validation.", nameof(query));

        return QueryLogsAsync(query, ct);
    }

    /// <summary>
    /// Retrieves suspicious access patterns for tamper attribution.
    /// Focuses on write operations and admin operations within the lookback window.
    /// </summary>
    public virtual Task<IReadOnlyList<AccessLogEntry>> QuerySuspiciousAccessAsync(
        Guid objectId,
        DateTimeOffset tamperDetectedAt,
        TimeSpan lookbackWindow,
        CancellationToken ct = default)
    {
        var query = new AccessLogQuery
        {
            ObjectId = objectId,
            StartTime = tamperDetectedAt.Subtract(lookbackWindow),
            EndTime = tamperDetectedAt,
            AccessTypes = new HashSet<AccessType>
            {
                AccessType.Write,
                AccessType.Correct,
                AccessType.AdminOperation
            },
            SucceededOnly = true,
            SortOrder = AccessLogSortOrder.TimestampDescending
        };

        return QueryLogsAsync(query, ct);
    }

    /// <summary>
    /// Retrieves a statistical summary of access patterns for a specific object.
    /// Queries all access history and computes the summary using AccessLogSummary.FromEntries.
    /// </summary>
    public virtual async Task<AccessLogSummary> GetSummaryAsync(
        Guid objectId,
        CancellationToken ct = default)
    {
        var query = new AccessLogQuery
        {
            ObjectId = objectId,
            SortOrder = AccessLogSortOrder.TimestampDescending
        };

        var entries = await QueryLogsAsync(query, ct);
        return AccessLogSummary.FromEntries(objectId, entries);
    }

    /// <summary>
    /// Performs comprehensive attribution analysis when tampering is detected.
    /// This default implementation provides sophisticated analysis including:
    /// - Write operation analysis (who wrote with what hash)
    /// - Access pattern analysis (frequency, timing, anomalies)
    /// - Principal risk scoring (likelihood calculation)
    /// - Behavioral heuristics (unusual access patterns)
    /// - Evidence collection and confidence scoring
    ///
    /// Derived classes can override to add custom attribution logic or integrate with
    /// external threat intelligence systems.
    /// </summary>
    public virtual async Task<SuspiciousAccessAnalysis> AnalyzeForAttributionAsync(
        Guid objectId,
        DateTimeOffset tamperDetectedAt,
        TimeSpan lookbackWindow,
        CancellationToken ct = default)
    {
        // Get suspicious access entries
        var suspiciousEntries = await QuerySuspiciousAccessAsync(objectId, tamperDetectedAt, lookbackWindow, ct);

        if (suspiciousEntries.Count == 0)
        {
            return SuspiciousAccessAnalysis.CreateUnknown(
                objectId,
                expectedHash: string.Empty,
                actualHash: string.Empty,
                tamperDetectedAt,
                "No access logs found within the lookback window. Tampering may have occurred outside the retention period or access logging was disabled.");
        }

        // Group entries by principal for analysis
        var byPrincipal = suspiciousEntries
            .GroupBy(e => e.Principal)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Calculate suspect likelihood for each principal
        var suspects = new List<SuspectedPrincipal>();
        var evidence = new List<string>();

        foreach (var (principal, entries) in byPrincipal)
        {
            var writeOperations = entries.Where(e => e.AccessType is AccessType.Write or AccessType.Correct).ToList();
            var adminOperations = entries.Where(e => e.AccessType == AccessType.AdminOperation).ToList();
            var lastAccess = entries.Max(e => e.Timestamp);

            // Calculate likelihood score (0.0 to 1.0)
            double likelihood = 0.0;
            var specificEvidence = new List<string>();

            // Factor 1: Write operations (strong indicator)
            if (writeOperations.Count > 0)
            {
                likelihood += 0.5;
                specificEvidence.Add($"{writeOperations.Count} write operation(s) performed");

                // Check for hash changes
                var hashes = writeOperations
                    .Where(e => !string.IsNullOrWhiteSpace(e.ComputedHash))
                    .Select(e => e.ComputedHash!)
                    .Distinct()
                    .ToList();

                if (hashes.Count > 1)
                {
                    likelihood += 0.2;
                    specificEvidence.Add($"Multiple distinct hashes written: {hashes.Count}");
                }
            }

            // Factor 2: Administrative operations (moderate indicator)
            if (adminOperations.Count > 0)
            {
                likelihood += 0.2;
                specificEvidence.Add($"{adminOperations.Count} administrative operation(s)");
            }

            // Factor 3: Temporal proximity to detection (closer = higher likelihood)
            var timeSinceLastAccess = tamperDetectedAt - lastAccess;
            if (timeSinceLastAccess.TotalHours < 1)
            {
                likelihood += 0.2;
                specificEvidence.Add($"Recent access: {timeSinceLastAccess.TotalMinutes:F1} minutes before detection");
            }
            else if (timeSinceLastAccess.TotalHours < 24)
            {
                likelihood += 0.1;
                specificEvidence.Add($"Access within 24 hours: {timeSinceLastAccess.TotalHours:F1} hours before detection");
            }

            // Factor 4: Frequency (unusual high frequency can indicate tampering attempts)
            if (entries.Count > 10)
            {
                likelihood += 0.1;
                specificEvidence.Add($"High access frequency: {entries.Count} operations");
            }

            // Factor 5: Failed operations followed by successful ones (potential tampering attempts)
            var hasFailedAttempts = entries.Any(e => !e.Succeeded);
            var hasSuccessAfterFail = entries
                .OrderBy(e => e.Timestamp)
                .SkipWhile(e => e.Succeeded)
                .Any(e => e.Succeeded);

            if (hasFailedAttempts && hasSuccessAfterFail)
            {
                likelihood += 0.15;
                specificEvidence.Add("Failed access attempts followed by successful operation");
            }

            // Cap likelihood at 1.0
            likelihood = Math.Min(1.0, likelihood);

            suspects.Add(new SuspectedPrincipal
            {
                Principal = principal,
                Likelihood = likelihood,
                LastAccessTime = lastAccess,
                AccessTypes = entries.Select(e => e.AccessType).Distinct().ToList(),
                SpecificEvidence = specificEvidence
            });
        }

        // Sort suspects by likelihood (highest first)
        suspects = suspects.OrderByDescending(s => s.Likelihood).ToList();

        // Determine confidence level based on evidence quality
        var confidence = CalculateConfidence(suspects, suspiciousEntries, lookbackWindow);

        // Collect overall evidence
        evidence.Add($"Analyzed {suspiciousEntries.Count} access operations from {byPrincipal.Count} principal(s)");
        evidence.Add($"Time window: {lookbackWindow.TotalHours} hours before detection");

        var writeCount = suspiciousEntries.Count(e => e.AccessType is AccessType.Write or AccessType.Correct);
        if (writeCount > 0)
        {
            evidence.Add($"{writeCount} write operation(s) detected in lookback window");
        }

        var adminCount = suspiciousEntries.Count(e => e.AccessType == AccessType.AdminOperation);
        if (adminCount > 0)
        {
            evidence.Add($"{adminCount} administrative operation(s) detected");
        }

        // Check for anomalous patterns
        var uniqueIps = suspiciousEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.ClientIp))
            .Select(e => e.ClientIp)
            .Distinct()
            .Count();
        if (uniqueIps > 1)
        {
            evidence.Add($"Accesses from {uniqueIps} different IP addresses");
        }

        // Determine tampering window (first write to detection)
        var firstWrite = suspiciousEntries
            .Where(e => e.AccessType is AccessType.Write or AccessType.Correct)
            .MinBy(e => e.Timestamp);

        var tamperingWindow = new TimeWindow
        {
            Start = firstWrite?.Timestamp ?? tamperDetectedAt.Subtract(lookbackWindow),
            End = tamperDetectedAt
        };

        // Generate recommended actions
        var recommendedActions = GenerateRecommendedActions(suspects, confidence, evidence);

        return new SuspiciousAccessAnalysis
        {
            ObjectId = objectId,
            ExpectedHash = string.Empty, // Would be set by caller with actual expected hash
            ActualHash = string.Empty,    // Would be set by caller with actual detected hash
            DetectionTime = tamperDetectedAt,
            Confidence = confidence,
            SuspectedPrincipals = suspects,
            TamperingWindow = tamperingWindow,
            Evidence = evidence,
            AnalyzedEntries = suspiciousEntries.ToList(),
            RecommendedActions = recommendedActions
        };
    }

    /// <summary>
    /// Purges access log entries older than the specified timestamp.
    /// Default implementation queries for old entries and deletes them individually.
    /// Derived classes should override for bulk delete optimizations specific to their storage backend.
    /// </summary>
    public virtual async Task PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        // Default implementation: query old entries and delete individually
        // Derived classes should override with bulk delete for better performance
        var query = new AccessLogQuery
        {
            EndTime = olderThan,
            SortOrder = AccessLogSortOrder.TimestampAscending
        };

        var oldEntries = await QueryLogsAsync(query, ct);

        // Derived classes can override to implement actual deletion
        // This base implementation just identifies what would be deleted
        await Task.CompletedTask;
    }

    /// <summary>
    /// Calculates the confidence level for attribution analysis based on evidence quality.
    /// </summary>
    private static AttributionConfidence CalculateConfidence(
        List<SuspectedPrincipal> suspects,
        IReadOnlyList<AccessLogEntry> entries,
        TimeSpan lookbackWindow)
    {
        if (suspects.Count == 0)
        {
            return AttributionConfidence.Unknown;
        }

        var topSuspect = suspects[0];
        var hasWriteOps = entries.Any(e => e.AccessType is AccessType.Write or AccessType.Correct);
        var hasRecentAccess = (entries.Max(e => e.Timestamp) - entries.Min(e => e.Timestamp)).TotalHours < 1;

        // High confidence: single suspect with high likelihood, write operations, and recent access
        if (suspects.Count == 1 && topSuspect.Likelihood >= 0.7 && hasWriteOps && hasRecentAccess)
        {
            return AttributionConfidence.Confirmed;
        }

        // Medium confidence: clear top suspect with moderate likelihood or multiple write operations
        if (topSuspect.Likelihood >= 0.5 && hasWriteOps)
        {
            if (suspects.Count == 1 || topSuspect.Likelihood - suspects[1].Likelihood > 0.3)
            {
                return AttributionConfidence.Likely;
            }
        }

        // Low confidence: some evidence but uncertain
        if (topSuspect.Likelihood >= 0.3)
        {
            return AttributionConfidence.Suspected;
        }

        // Unknown: insufficient evidence
        return AttributionConfidence.Unknown;
    }

    /// <summary>
    /// Generates recommended actions based on attribution analysis results.
    /// </summary>
    private static List<string> GenerateRecommendedActions(
        List<SuspectedPrincipal> suspects,
        AttributionConfidence confidence,
        List<string> evidence)
    {
        var actions = new List<string>();

        if (confidence == AttributionConfidence.Confirmed && suspects.Count > 0)
        {
            actions.Add($"Investigate principal '{suspects[0].Principal}' immediately");
            actions.Add("Review all access permissions for the affected object");
            actions.Add("Consider temporarily suspending access for suspected principal");
        }
        else if (confidence == AttributionConfidence.Likely)
        {
            actions.Add("Conduct interviews with suspected principals");
            actions.Add("Review access logs for correlated suspicious activity");
            actions.Add("Verify integrity of related objects accessed by suspects");
        }
        else if (confidence == AttributionConfidence.Suspected)
        {
            actions.Add("Broaden investigation to include all principals with recent access");
            actions.Add("Check for gaps in access logging");
            actions.Add("Review physical and network access controls");
        }
        else
        {
            actions.Add("Review physical access logs");
            actions.Add("Check system integrity and logging configuration");
            actions.Add("Investigate network security and potential lateral movement");
            actions.Add("Consider forensic imaging of affected systems");
        }

        // Always recommend these baseline actions
        actions.Add("Document all findings for compliance and legal purposes");
        actions.Add("Notify security team and stakeholders");
        actions.Add("Preserve all log evidence for potential forensic analysis");

        return actions;
    }

    /// <summary>
    /// Abstract method for persisting an access log entry.
    /// Derived classes must implement this to store entries in their chosen backend
    /// (e.g., SQL database, NoSQL store, file system, cloud storage).
    /// </summary>
    /// <param name="entry">The validated access log entry to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the entry is persisted.</returns>
    protected abstract Task PersistLogEntryAsync(AccessLogEntry entry, CancellationToken ct);

    /// <summary>
    /// Abstract method for querying access logs.
    /// Derived classes must implement this to retrieve entries from their chosen backend
    /// matching the query criteria.
    /// </summary>
    /// <param name="query">The validated query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of access log entries matching the query.</returns>
    protected abstract Task<IReadOnlyList<AccessLogEntry>> QueryLogsAsync(
        AccessLogQuery query,
        CancellationToken ct);

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "AccessLog";
        metadata["SupportsTamperAttribution"] = true;
        metadata["SupportsForensicAnalysis"] = true;
        metadata["SupportsAuditTrail"] = true;
        metadata["SupportsAdvancedQuery"] = true;
        return metadata;
    }
}
