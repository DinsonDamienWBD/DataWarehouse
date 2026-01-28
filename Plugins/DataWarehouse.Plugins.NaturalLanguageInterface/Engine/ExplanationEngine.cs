// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using DataWarehouse.SDK.AI;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Engine;

/// <summary>
/// Engine for generating explanations of system decisions and data analysis.
/// Supports decision explanation, reasoning chains, and anomaly root cause analysis.
/// </summary>
public sealed class ExplanationEngine
{
    private readonly IAIProviderRegistry? _aiRegistry;
    private readonly ExplanationEngineConfig _config;

    public ExplanationEngine(IAIProviderRegistry? aiRegistry = null, ExplanationEngineConfig? config = null)
    {
        _aiRegistry = aiRegistry;
        _config = config ?? new ExplanationEngineConfig();
    }

    /// <summary>
    /// Explain why a file was tiered to a specific storage tier.
    /// </summary>
    public async Task<ExplanationResult> ExplainTieringDecisionAsync(
        TieringDecisionContext context,
        CancellationToken ct = default)
    {
        var reasons = new List<ReasoningStep>();

        // Build reasoning chain from decision factors
        if (context.LastAccessDate.HasValue)
        {
            var daysSinceAccess = (DateTime.UtcNow - context.LastAccessDate.Value).TotalDays;
            reasons.Add(new ReasoningStep
            {
                Factor = "Last Access Time",
                Description = $"File was last accessed {daysSinceAccess:F0} days ago",
                Impact = daysSinceAccess > 90 ? "High" : daysSinceAccess > 30 ? "Medium" : "Low",
                ContributesToDecision = daysSinceAccess > 30
            });
        }

        if (context.FileSize.HasValue)
        {
            var sizeGB = context.FileSize.Value / (1024.0 * 1024 * 1024);
            reasons.Add(new ReasoningStep
            {
                Factor = "File Size",
                Description = $"File is {sizeGB:F2} GB",
                Impact = sizeGB > 1 ? "High" : sizeGB > 0.1 ? "Medium" : "Low",
                ContributesToDecision = sizeGB > 0.5
            });
        }

        if (context.AccessFrequency.HasValue)
        {
            reasons.Add(new ReasoningStep
            {
                Factor = "Access Frequency",
                Description = $"File is accessed {context.AccessFrequency} times per month on average",
                Impact = context.AccessFrequency < 1 ? "High" : context.AccessFrequency < 5 ? "Medium" : "Low",
                ContributesToDecision = context.AccessFrequency < 3
            });
        }

        if (!string.IsNullOrEmpty(context.ContentType))
        {
            var isArchiveCandidate = context.ContentType.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                                     context.ContentType.Contains("archive", StringComparison.OrdinalIgnoreCase) ||
                                     context.ContentType.Contains("log", StringComparison.OrdinalIgnoreCase);
            reasons.Add(new ReasoningStep
            {
                Factor = "Content Type",
                Description = $"Content type is '{context.ContentType}'",
                Impact = isArchiveCandidate ? "Medium" : "Low",
                ContributesToDecision = isArchiveCandidate
            });
        }

        if (context.PolicyRules?.Count > 0)
        {
            foreach (var rule in context.PolicyRules)
            {
                reasons.Add(new ReasoningStep
                {
                    Factor = "Policy Rule",
                    Description = rule,
                    Impact = "High",
                    ContributesToDecision = true
                });
            }
        }

        // Generate human-readable explanation
        var summary = await GenerateExplanationSummaryAsync(
            "tiering decision",
            context.TargetTier,
            reasons,
            ct);

        return new ExplanationResult
        {
            Success = true,
            Question = $"Why was '{context.FileName}' tiered to {context.TargetTier}?",
            Summary = summary,
            ReasoningChain = reasons,
            Confidence = CalculateConfidence(reasons),
            Recommendations = GenerateTieringRecommendations(context, reasons)
        };
    }

    /// <summary>
    /// Explain why storage usage spiked.
    /// </summary>
    public async Task<ExplanationResult> ExplainStorageSpikeAsync(
        StorageSpikeContext context,
        CancellationToken ct = default)
    {
        var reasons = new List<ReasoningStep>();
        var timeline = new List<TimelineEvent>();

        // Analyze contributing factors
        if (context.LargeFileUploads?.Count > 0)
        {
            var totalSize = context.LargeFileUploads.Sum(f => f.Size);
            reasons.Add(new ReasoningStep
            {
                Factor = "Large File Uploads",
                Description = $"{context.LargeFileUploads.Count} large files uploaded totaling {FormatSize(totalSize)}",
                Impact = "High",
                ContributesToDecision = true,
                Details = context.LargeFileUploads.Select(f => $"{f.Name}: {FormatSize(f.Size)}").ToList()
            });

            timeline.AddRange(context.LargeFileUploads.Select(f => new TimelineEvent
            {
                Timestamp = f.UploadTime,
                Event = $"Uploaded {f.Name}",
                SizeChange = f.Size,
                Category = "Upload"
            }));
        }

        if (context.BackupOperations?.Count > 0)
        {
            var totalBackupSize = context.BackupOperations.Sum(b => b.Size);
            reasons.Add(new ReasoningStep
            {
                Factor = "Backup Operations",
                Description = $"{context.BackupOperations.Count} backup operations created totaling {FormatSize(totalBackupSize)}",
                Impact = "High",
                ContributesToDecision = true
            });

            timeline.AddRange(context.BackupOperations.Select(b => new TimelineEvent
            {
                Timestamp = b.CreatedAt,
                Event = $"Backup created: {b.Name}",
                SizeChange = b.Size,
                Category = "Backup"
            }));
        }

        if (context.DeduplicationDisabled)
        {
            reasons.Add(new ReasoningStep
            {
                Factor = "Deduplication Disabled",
                Description = "Deduplication was disabled, preventing space savings",
                Impact = "Medium",
                ContributesToDecision = true
            });
        }

        if (context.CompressionRatioChange.HasValue && context.CompressionRatioChange > 0)
        {
            reasons.Add(new ReasoningStep
            {
                Factor = "Compression Efficiency",
                Description = $"Compression ratio decreased by {context.CompressionRatioChange:P0}",
                Impact = "Medium",
                ContributesToDecision = true
            });
        }

        if (context.FailedCleanups?.Count > 0)
        {
            reasons.Add(new ReasoningStep
            {
                Factor = "Failed Cleanup Operations",
                Description = $"{context.FailedCleanups.Count} cleanup operations failed to complete",
                Impact = "Medium",
                ContributesToDecision = true,
                Details = context.FailedCleanups
            });
        }

        // Sort timeline
        timeline = timeline.OrderBy(t => t.Timestamp).ToList();

        // Generate summary
        var summary = await GenerateExplanationSummaryAsync(
            "storage spike",
            $"{FormatSize(context.SizeIncrease)} increase",
            reasons,
            ct);

        return new ExplanationResult
        {
            Success = true,
            Question = $"Why did storage usage spike by {FormatSize(context.SizeIncrease)} on {context.SpikeDate:yyyy-MM-dd}?",
            Summary = summary,
            ReasoningChain = reasons,
            Timeline = timeline,
            Confidence = CalculateConfidence(reasons),
            Recommendations = GenerateSpikeRecommendations(context, reasons),
            ImpactAssessment = new ImpactAssessment
            {
                Severity = context.SizeIncrease > context.QuotaLimit * 0.1 ? "High" :
                           context.SizeIncrease > context.QuotaLimit * 0.05 ? "Medium" : "Low",
                AffectedQuota = context.QuotaLimit > 0 ? (double)context.SizeIncrease / context.QuotaLimit : 0,
                EstimatedCostImpact = context.CostPerGB * (context.SizeIncrease / (1024.0 * 1024 * 1024)),
                Description = GenerateImpactDescription(context)
            }
        };
    }

    /// <summary>
    /// Explain a query result - why these files matched.
    /// </summary>
    public async Task<ExplanationResult> ExplainQueryResultAsync(
        QueryResultContext context,
        CancellationToken ct = default)
    {
        var reasons = new List<ReasoningStep>();

        // Explain query interpretation
        if (context.ParsedIntent != null)
        {
            reasons.Add(new ReasoningStep
            {
                Factor = "Query Interpretation",
                Description = $"Your query was interpreted as a {context.ParsedIntent.Type} operation",
                Impact = "Info",
                ContributesToDecision = true
            });
        }

        // Explain filter application
        if (context.AppliedFilters?.Count > 0)
        {
            foreach (var filter in context.AppliedFilters)
            {
                reasons.Add(new ReasoningStep
                {
                    Factor = "Filter Applied",
                    Description = $"{filter.Key}: {filter.Value}",
                    Impact = "Info",
                    ContributesToDecision = true
                });
            }
        }

        // Explain why specific results matched
        if (context.TopResults?.Count > 0)
        {
            var matchDetails = new List<string>();
            foreach (var result in context.TopResults.Take(5))
            {
                matchDetails.Add($"{result.Name}: {result.MatchReason}");
            }

            reasons.Add(new ReasoningStep
            {
                Factor = "Match Reasons",
                Description = $"Top {matchDetails.Count} results matched because:",
                Impact = "Info",
                ContributesToDecision = true,
                Details = matchDetails
            });
        }

        // Explain ranking
        if (context.RankingFactors?.Count > 0)
        {
            reasons.Add(new ReasoningStep
            {
                Factor = "Result Ranking",
                Description = "Results were ranked based on:",
                Impact = "Info",
                ContributesToDecision = true,
                Details = context.RankingFactors
            });
        }

        var summary = await GenerateExplanationSummaryAsync(
            "search result",
            $"{context.TotalResults} matches",
            reasons,
            ct);

        return new ExplanationResult
        {
            Success = true,
            Question = $"Why did '{context.OriginalQuery}' return these results?",
            Summary = summary,
            ReasoningChain = reasons,
            Confidence = 0.9
        };
    }

    /// <summary>
    /// Explain an anomaly detected in the system.
    /// </summary>
    public async Task<ExplanationResult> ExplainAnomalyAsync(
        AnomalyContext context,
        CancellationToken ct = default)
    {
        var reasons = new List<ReasoningStep>();
        var timeline = new List<TimelineEvent>();

        // Describe the anomaly
        reasons.Add(new ReasoningStep
        {
            Factor = "Anomaly Detection",
            Description = $"{context.AnomalyType} anomaly detected: {context.Description}",
            Impact = context.Severity,
            ContributesToDecision = true
        });

        // Root cause analysis
        if (context.PossibleCauses?.Count > 0)
        {
            foreach (var (cause, probability) in context.PossibleCauses.OrderByDescending(c => c.Probability))
            {
                reasons.Add(new ReasoningStep
                {
                    Factor = "Possible Cause",
                    Description = cause,
                    Impact = probability > 0.7 ? "High" : probability > 0.4 ? "Medium" : "Low",
                    ContributesToDecision = probability > 0.3,
                    Details = new List<string> { $"Probability: {probability:P0}" }
                });
            }
        }

        // Related events
        if (context.RelatedEvents?.Count > 0)
        {
            timeline.AddRange(context.RelatedEvents.Select(e => new TimelineEvent
            {
                Timestamp = e.Timestamp,
                Event = e.Description,
                Category = e.Category
            }));

            reasons.Add(new ReasoningStep
            {
                Factor = "Related Events",
                Description = $"{context.RelatedEvents.Count} related events found around the time of the anomaly",
                Impact = "Info",
                ContributesToDecision = false
            });
        }

        // Baseline comparison
        if (context.BaselineValue.HasValue && context.AnomalousValue.HasValue)
        {
            var deviation = Math.Abs(context.AnomalousValue.Value - context.BaselineValue.Value) / context.BaselineValue.Value;
            reasons.Add(new ReasoningStep
            {
                Factor = "Baseline Comparison",
                Description = $"Value deviated {deviation:P0} from the baseline ({context.BaselineValue:F2} -> {context.AnomalousValue:F2})",
                Impact = deviation > 0.5 ? "High" : deviation > 0.2 ? "Medium" : "Low",
                ContributesToDecision = true
            });
        }

        var summary = await GenerateExplanationSummaryAsync(
            "anomaly",
            context.Description,
            reasons,
            ct);

        return new ExplanationResult
        {
            Success = true,
            Question = $"Why was this {context.AnomalyType} anomaly detected?",
            Summary = summary,
            ReasoningChain = reasons,
            Timeline = timeline.OrderBy(t => t.Timestamp).ToList(),
            Confidence = context.PossibleCauses?.Count > 0 ?
                context.PossibleCauses.Max(c => c.Probability) : 0.5,
            Recommendations = GenerateAnomalyRecommendations(context, reasons),
            ImpactAssessment = new ImpactAssessment
            {
                Severity = context.Severity,
                Description = context.Description
            }
        };
    }

    #region Private Methods

    private async Task<string> GenerateExplanationSummaryAsync(
        string decisionType,
        string outcome,
        List<ReasoningStep> reasons,
        CancellationToken ct)
    {
        // Try AI-generated summary if available
        if (_aiRegistry != null && _config.UseAIForSummaries)
        {
            var provider = _aiRegistry.GetProvider(_config.ProviderRouting.ExplanationProvider)
                ?? _aiRegistry.GetDefaultProvider();

            if (provider != null && provider.IsAvailable)
            {
                try
                {
                    var systemPrompt = """
                        You are explaining a data warehouse system decision to a user.
                        Generate a clear, concise explanation (2-3 sentences) that:
                        1. Summarizes the main reasons for the decision
                        2. Uses plain language, avoiding technical jargon
                        3. Is helpful and informative
                        """;

                    var reasonsSummary = string.Join("\n", reasons
                        .Where(r => r.ContributesToDecision)
                        .Select(r => $"- {r.Factor}: {r.Description}"));

                    var prompt = $"""
                        Decision type: {decisionType}
                        Outcome: {outcome}
                        Contributing factors:
                        {reasonsSummary}

                        Generate a human-friendly explanation.
                        """;

                    var request = new AIRequest
                    {
                        SystemMessage = systemPrompt,
                        Prompt = prompt,
                        Temperature = 0.5f,
                        MaxTokens = 150
                    };

                    var response = await provider.CompleteAsync(request, ct);
                    if (response.Success && !string.IsNullOrEmpty(response.Content))
                    {
                        return response.Content;
                    }
                }
                catch
                {
                    // Fall through to template-based summary
                }
            }
        }

        // Template-based summary
        var contributingReasons = reasons.Where(r => r.ContributesToDecision).ToList();

        if (contributingReasons.Count == 0)
        {
            return $"The {decisionType} resulted in {outcome}, but no specific factors were identified.";
        }

        var mainReason = contributingReasons.FirstOrDefault(r => r.Impact == "High")
            ?? contributingReasons.First();

        return contributingReasons.Count == 1
            ? $"The {decisionType} resulted in {outcome} primarily because {mainReason.Description.ToLower()}."
            : $"The {decisionType} resulted in {outcome} due to {contributingReasons.Count} factors. " +
              $"The main factor was: {mainReason.Description.ToLower()}.";
    }

    private double CalculateConfidence(List<ReasoningStep> reasons)
    {
        if (reasons.Count == 0) return 0.5;

        var contributingReasons = reasons.Where(r => r.ContributesToDecision).ToList();
        if (contributingReasons.Count == 0) return 0.5;

        // Higher confidence with more high-impact contributing factors
        var highImpact = contributingReasons.Count(r => r.Impact == "High");
        var mediumImpact = contributingReasons.Count(r => r.Impact == "Medium");

        var confidence = 0.5 + (highImpact * 0.15) + (mediumImpact * 0.08);
        return Math.Min(0.95, confidence);
    }

    private List<string> GenerateTieringRecommendations(TieringDecisionContext context, List<ReasoningStep> reasons)
    {
        var recommendations = new List<string>();

        if (context.LastAccessDate.HasValue &&
            (DateTime.UtcNow - context.LastAccessDate.Value).TotalDays > 180)
        {
            recommendations.Add("Consider archiving this file if it's no longer needed for regular access.");
        }

        if (context.AccessFrequency > 10)
        {
            recommendations.Add("This file is accessed frequently. Consider keeping it in a hot tier for performance.");
        }

        if (string.IsNullOrEmpty(recommendations.FirstOrDefault()))
        {
            recommendations.Add("Review your tiering policies to ensure they align with your access patterns.");
        }

        return recommendations;
    }

    private List<string> GenerateSpikeRecommendations(StorageSpikeContext context, List<ReasoningStep> reasons)
    {
        var recommendations = new List<string>();

        if (context.LargeFileUploads?.Count > 5)
        {
            recommendations.Add("Consider implementing upload quotas or approval workflows for large files.");
        }

        if (context.DeduplicationDisabled)
        {
            recommendations.Add("Enable deduplication to save storage space on duplicate content.");
        }

        if (context.FailedCleanups?.Count > 0)
        {
            recommendations.Add("Investigate and resolve failed cleanup operations to reclaim space.");
        }

        if (context.SizeIncrease > context.QuotaLimit * 0.2)
        {
            recommendations.Add("Consider increasing your storage quota or archiving older data.");
        }

        return recommendations;
    }

    private List<string> GenerateAnomalyRecommendations(AnomalyContext context, List<ReasoningStep> reasons)
    {
        var recommendations = new List<string>();

        switch (context.AnomalyType.ToLower())
        {
            case "performance":
                recommendations.Add("Review recent changes that might have affected performance.");
                recommendations.Add("Check resource utilization and consider scaling if needed.");
                break;

            case "security":
                recommendations.Add("Review access logs for suspicious activity.");
                recommendations.Add("Consider implementing additional authentication controls.");
                break;

            case "capacity":
                recommendations.Add("Implement data lifecycle policies to manage growth.");
                recommendations.Add("Set up alerts for capacity thresholds.");
                break;

            default:
                recommendations.Add("Monitor for recurrence and set up alerts.");
                break;
        }

        return recommendations;
    }

    private string GenerateImpactDescription(StorageSpikeContext context)
    {
        var parts = new List<string>();

        if (context.QuotaLimit > 0)
        {
            var percentOfQuota = (double)context.SizeIncrease / context.QuotaLimit * 100;
            parts.Add($"Used {percentOfQuota:F1}% of quota");
        }

        if (context.CostPerGB > 0)
        {
            var cost = context.CostPerGB * (context.SizeIncrease / (1024.0 * 1024 * 1024));
            parts.Add($"estimated additional cost: ${cost:F2}");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "Impact assessment not available";
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:F2} {sizes[order]}";
    }

    #endregion
}

#region Explanation Models

/// <summary>
/// Result of an explanation request.
/// </summary>
public sealed class ExplanationResult
{
    public bool Success { get; init; }
    public string Question { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public List<ReasoningStep> ReasoningChain { get; init; } = new();
    public List<TimelineEvent>? Timeline { get; init; }
    public double Confidence { get; init; }
    public List<string>? Recommendations { get; init; }
    public ImpactAssessment? ImpactAssessment { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// A step in the reasoning chain.
/// </summary>
public sealed class ReasoningStep
{
    public string Factor { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Impact { get; init; } = "Low"; // Low, Medium, High, Info
    public bool ContributesToDecision { get; init; }
    public List<string>? Details { get; init; }
}

/// <summary>
/// An event in a timeline reconstruction.
/// </summary>
public sealed class TimelineEvent
{
    public DateTime Timestamp { get; init; }
    public string Event { get; init; } = string.Empty;
    public string? Category { get; init; }
    public long? SizeChange { get; init; }
}

/// <summary>
/// Impact assessment of a decision or anomaly.
/// </summary>
public sealed class ImpactAssessment
{
    public string Severity { get; init; } = "Low";
    public double? AffectedQuota { get; init; }
    public double? EstimatedCostImpact { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Context for explaining a tiering decision.
/// </summary>
public sealed class TieringDecisionContext
{
    public string FileName { get; init; } = string.Empty;
    public string TargetTier { get; init; } = string.Empty;
    public string? SourceTier { get; init; }
    public DateTime? LastAccessDate { get; init; }
    public long? FileSize { get; init; }
    public double? AccessFrequency { get; init; }
    public string? ContentType { get; init; }
    public List<string>? PolicyRules { get; init; }
}

/// <summary>
/// Context for explaining a storage spike.
/// </summary>
public sealed class StorageSpikeContext
{
    public DateTime SpikeDate { get; init; }
    public long SizeIncrease { get; init; }
    public long QuotaLimit { get; init; }
    public double CostPerGB { get; init; }
    public List<LargeFileUpload>? LargeFileUploads { get; init; }
    public List<BackupOperation>? BackupOperations { get; init; }
    public bool DeduplicationDisabled { get; init; }
    public double? CompressionRatioChange { get; init; }
    public List<string>? FailedCleanups { get; init; }
}

/// <summary>
/// Information about a large file upload.
/// </summary>
public sealed class LargeFileUpload
{
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTime UploadTime { get; init; }
}

/// <summary>
/// Information about a backup operation.
/// </summary>
public sealed class BackupOperation
{
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Context for explaining query results.
/// </summary>
public sealed class QueryResultContext
{
    public string OriginalQuery { get; init; } = string.Empty;
    public QueryIntent? ParsedIntent { get; init; }
    public Dictionary<string, string>? AppliedFilters { get; init; }
    public List<NLSearchResult>? TopResults { get; init; }
    public List<string>? RankingFactors { get; init; }
    public int TotalResults { get; init; }
}

/// <summary>
/// Context for explaining an anomaly.
/// </summary>
public sealed class AnomalyContext
{
    public string AnomalyType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Severity { get; init; } = "Medium";
    public DateTime DetectedAt { get; init; }
    public double? BaselineValue { get; init; }
    public double? AnomalousValue { get; init; }
    public List<(string Cause, double Probability)>? PossibleCauses { get; init; }
    public List<RelatedEvent>? RelatedEvents { get; init; }
}

/// <summary>
/// An event related to an anomaly.
/// </summary>
public sealed class RelatedEvent
{
    public DateTime Timestamp { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
}

#endregion

/// <summary>
/// Configuration for the explanation engine.
/// </summary>
public sealed class ExplanationEngineConfig
{
    /// <summary>Whether to use AI for generating summaries.</summary>
    public bool UseAIForSummaries { get; init; } = true;

    /// <summary>AI provider routing configuration.</summary>
    public AIProviderRouting ProviderRouting { get; init; } = new();
}
