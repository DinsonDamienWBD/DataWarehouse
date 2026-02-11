// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AIInterface.GUI;

/// <summary>
/// Provides AI-powered recommendations for file operations and security actions.
/// Displays context-aware suggestions like "This file looks like PII, encrypt?"
/// </summary>
/// <remarks>
/// <para>
/// The RecommendationEngine analyzes content and context to provide:
/// <list type="bullet">
/// <item>PII detection with encryption recommendations</item>
/// <item>Compliance suggestions based on content classification</item>
/// <item>Storage tier recommendations based on access patterns</item>
/// <item>Compression recommendations based on file type</item>
/// <item>Organization suggestions for related files</item>
/// <item>Security alerts for sensitive content</item>
/// </list>
/// </para>
/// <para>
/// When Intelligence is unavailable, basic rule-based recommendations are provided
/// using file metadata and extension patterns.
/// </para>
/// </remarks>
public sealed class RecommendationEngine : IDisposable
{
    private readonly IMessageBus? _messageBus;
    private readonly RecommendationEngineConfig _config;
    private readonly ConcurrentDictionary<string, RecommendationCache> _cache = new();
    private bool _intelligenceAvailable;
    private IntelligenceCapabilities _capabilities = IntelligenceCapabilities.None;
    private bool _disposed;

    /// <summary>
    /// Event raised when a high-priority recommendation is generated.
    /// </summary>
    public event EventHandler<RecommendationEventArgs>? RecommendationGenerated;

    /// <summary>
    /// Gets whether Intelligence is available for recommendations.
    /// </summary>
    public bool IsIntelligenceAvailable => _intelligenceAvailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationEngine"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for Intelligence communication.</param>
    /// <param name="config">Optional configuration.</param>
    public RecommendationEngine(IMessageBus? messageBus, RecommendationEngineConfig? config = null)
    {
        _messageBus = messageBus;
        _config = config ?? new RecommendationEngineConfig();
    }

    /// <summary>
    /// Initializes the engine and discovers Intelligence availability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _intelligenceAvailable = false;
            return;
        }

        try
        {
            var response = await _messageBus.RequestAsync(
                IntelligenceTopics.Discover,
                new Dictionary<string, object>
                {
                    ["requestorId"] = "gui.recommendations",
                    ["requestorName"] = "RecommendationEngine",
                    ["timestamp"] = DateTimeOffset.UtcNow
                },
                ct);

            if (response != null &&
                response.TryGetValue("available", out var available) && available is true)
            {
                _intelligenceAvailable = true;

                if (response.TryGetValue("capabilities", out var caps))
                {
                    if (caps is IntelligenceCapabilities ic)
                        _capabilities = ic;
                    else if (caps is long longVal)
                        _capabilities = (IntelligenceCapabilities)longVal;
                }
            }
        }
        catch
        {
            _intelligenceAvailable = false;
        }
    }

    /// <summary>
    /// Analyzes a file and generates recommendations.
    /// </summary>
    /// <param name="fileInfo">Information about the file.</param>
    /// <param name="content">Optional file content for deep analysis.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recommendations for the file.</returns>
    public async Task<IReadOnlyList<Recommendation>> AnalyzeFileAsync(
        FileAnalysisRequest fileInfo,
        string? content = null,
        CancellationToken ct = default)
    {
        var recommendations = new List<Recommendation>();

        // Check cache first
        var cacheKey = $"{fileInfo.FileId}:{fileInfo.LastModified?.Ticks ?? 0}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            DateTime.UtcNow - cached.GeneratedAt < TimeSpan.FromMinutes(_config.CacheDurationMinutes))
        {
            return cached.Recommendations.AsReadOnly();
        }

        // Generate basic recommendations based on file metadata
        var basicRecommendations = GenerateBasicRecommendations(fileInfo);
        recommendations.AddRange(basicRecommendations);

        // If Intelligence is available, get AI-powered recommendations
        if (_intelligenceAvailable && _messageBus != null)
        {
            try
            {
                var aiRecommendations = await GenerateAIRecommendationsAsync(fileInfo, content, ct);
                recommendations.AddRange(aiRecommendations);
            }
            catch
            {
                // Fall back to basic recommendations
            }
        }

        // Deduplicate and prioritize
        recommendations = DeduplicateRecommendations(recommendations);

        // Cache results
        _cache[cacheKey] = new RecommendationCache
        {
            Recommendations = recommendations,
            GeneratedAt = DateTime.UtcNow
        };

        // Notify for high-priority recommendations
        foreach (var rec in recommendations.Where(r => r.Priority == RecommendationPriority.High))
        {
            OnRecommendationGenerated(rec);
        }

        return recommendations.AsReadOnly();
    }

    /// <summary>
    /// Analyzes content for PII and sensitive data.
    /// </summary>
    /// <param name="content">The content to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PII analysis result with recommendations.</returns>
    public async Task<PIIAnalysisResult> AnalyzeForPIIAsync(
        string content,
        CancellationToken ct = default)
    {
        var result = new PIIAnalysisResult
        {
            Recommendations = new List<Recommendation>()
        };

        // Basic pattern matching for PII
        var basicPII = DetectBasicPII(content);
        if (basicPII.Count > 0)
        {
            result.ContainsPII = true;
            result.DetectedTypes = basicPII;
            result.Recommendations.Add(new Recommendation
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = RecommendationType.Encrypt,
                Title = "Encrypt sensitive content",
                Description = $"This content appears to contain {string.Join(", ", basicPII)}. Consider encrypting with strong encryption.",
                Priority = RecommendationPriority.High,
                Action = new RecommendationAction
                {
                    ActionType = "encrypt",
                    Label = "Encrypt with AES-256-GCM",
                    Parameters = new Dictionary<string, object>
                    {
                        ["cipher"] = "AES-256-GCM",
                        ["keySize"] = 256
                    }
                }
            });
        }

        // Enhanced PII detection with Intelligence
        if (_intelligenceAvailable && _messageBus != null && HasCapability(IntelligenceCapabilities.PIIDetection))
        {
            try
            {
                var response = await _messageBus.RequestAsync(
                    IntelligenceTopics.RequestPIIDetection,
                    new Dictionary<string, object>
                    {
                        ["text"] = content
                    },
                    ct);

                if (response != null)
                {
                    if (response.TryGetValue("containsPII", out var pii) && pii is true)
                    {
                        result.ContainsPII = true;
                    }

                    if (response.TryGetValue("piiItems", out var items) &&
                        items is IEnumerable<object> itemList)
                    {
                        foreach (var item in itemList.OfType<Dictionary<string, object>>())
                        {
                            var type = item.TryGetValue("type", out var t) ? t?.ToString() : "Unknown";
                            if (type != null && !result.DetectedTypes.Contains(type))
                            {
                                result.DetectedTypes.Add(type);
                            }
                        }
                    }

                    // Add compliance recommendation
                    if (result.ContainsPII)
                    {
                        result.Recommendations.Add(new Recommendation
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Type = RecommendationType.Compliance,
                            Title = "Review compliance requirements",
                            Description = "This file contains PII. Ensure it complies with GDPR, HIPAA, or other applicable regulations.",
                            Priority = RecommendationPriority.Medium,
                            Action = new RecommendationAction
                            {
                                ActionType = "audit",
                                Label = "Run compliance check"
                            }
                        });
                    }
                }
            }
            catch
            {
                // Use basic detection results
            }
        }

        return result;
    }

    /// <summary>
    /// Gets storage tier recommendations based on file characteristics.
    /// </summary>
    /// <param name="fileInfo">The file information.</param>
    /// <param name="accessPattern">Optional access pattern data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tiering recommendation.</returns>
    public async Task<TieringRecommendation> GetTieringRecommendationAsync(
        FileAnalysisRequest fileInfo,
        AccessPatternData? accessPattern = null,
        CancellationToken ct = default)
    {
        var recommendation = new TieringRecommendation
        {
            FileId = fileInfo.FileId
        };

        // Basic tiering based on access age
        var daysSinceAccess = fileInfo.LastAccessed.HasValue
            ? (DateTime.UtcNow - fileInfo.LastAccessed.Value).TotalDays
            : 365;

        if (daysSinceAccess > 365)
        {
            recommendation.RecommendedTier = StorageTier.Archive;
            recommendation.Reason = "File hasn't been accessed in over a year";
            recommendation.EstimatedSavings = fileInfo.SizeBytes * 0.8m / (1024 * 1024 * 1024); // Rough estimate
        }
        else if (daysSinceAccess > 90)
        {
            recommendation.RecommendedTier = StorageTier.Cool;
            recommendation.Reason = "File hasn't been accessed in over 90 days";
            recommendation.EstimatedSavings = fileInfo.SizeBytes * 0.5m / (1024 * 1024 * 1024);
        }
        else
        {
            recommendation.RecommendedTier = StorageTier.Hot;
            recommendation.Reason = "File is frequently accessed";
        }

        // Enhanced with AI predictions
        if (_intelligenceAvailable && _messageBus != null && HasCapability(IntelligenceCapabilities.TieringRecommendation))
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["fileId"] = fileInfo.FileId,
                    ["fileName"] = fileInfo.FileName,
                    ["fileType"] = fileInfo.ContentType ?? "application/octet-stream",
                    ["size"] = fileInfo.SizeBytes,
                    ["lastAccessed"] = fileInfo.LastAccessed ?? DateTime.UtcNow.AddDays(-30),
                    ["lastModified"] = fileInfo.LastModified ?? DateTime.UtcNow
                };

                if (accessPattern != null)
                {
                    payload["accessPattern"] = new Dictionary<string, object>
                    {
                        ["accessCount"] = accessPattern.AccessCount,
                        ["lastAccessDate"] = accessPattern.LastAccessDate,
                        ["averageAccessInterval"] = accessPattern.AverageAccessIntervalDays
                    };
                }

                var response = await _messageBus.RequestAsync(
                    IntelligenceTopics.RequestTieringRecommendation,
                    payload,
                    ct);

                if (response != null)
                {
                    if (response.TryGetValue("recommendedTier", out var tier))
                    {
                        recommendation.RecommendedTier = ParseStorageTier(tier?.ToString());
                    }

                    if (response.TryGetValue("reason", out var reason))
                    {
                        recommendation.Reason = reason?.ToString() ?? recommendation.Reason;
                    }

                    if (response.TryGetValue("estimatedSavings", out var savings) && savings is decimal s)
                    {
                        recommendation.EstimatedSavings = s;
                    }

                    if (response.TryGetValue("confidence", out var conf) && conf is double c)
                    {
                        recommendation.Confidence = c;
                    }

                    if (response.TryGetValue("predictedAccessDate", out var pad))
                    {
                        if (DateTime.TryParse(pad?.ToString(), out var predictedDate))
                        {
                            recommendation.PredictedNextAccess = predictedDate;
                        }
                    }
                }
            }
            catch
            {
                // Use basic recommendation
            }
        }

        return recommendation;
    }

    /// <summary>
    /// Gets compression recommendations for a file.
    /// </summary>
    /// <param name="fileInfo">The file information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compression recommendation.</returns>
    public async Task<CompressionRecommendation> GetCompressionRecommendationAsync(
        FileAnalysisRequest fileInfo,
        CancellationToken ct = default)
    {
        var recommendation = new CompressionRecommendation
        {
            FileId = fileInfo.FileId
        };

        // Basic recommendations based on file type
        var extension = System.IO.Path.GetExtension(fileInfo.FileName)?.ToLowerInvariant();
        var contentType = fileInfo.ContentType?.ToLowerInvariant() ?? string.Empty;

        // Already compressed formats
        if (IsCompressedFormat(extension, contentType))
        {
            recommendation.ShouldCompress = false;
            recommendation.Reason = "File is already in a compressed format";
            return recommendation;
        }

        // Text-based formats - high compression potential
        if (IsTextFormat(extension, contentType))
        {
            recommendation.ShouldCompress = true;
            recommendation.RecommendedAlgorithm = "zstd";
            recommendation.CompressionLevel = 6;
            recommendation.EstimatedRatio = 0.3; // ~70% compression
            recommendation.Reason = "Text files typically compress well";
        }
        // Binary data
        else
        {
            recommendation.ShouldCompress = fileInfo.SizeBytes > 1024 * 1024; // Only if > 1MB
            recommendation.RecommendedAlgorithm = "lz4";
            recommendation.CompressionLevel = 4;
            recommendation.EstimatedRatio = 0.7; // ~30% compression
            recommendation.Reason = "Binary data may have some compression potential";
        }

        // Enhanced with AI analysis
        if (_intelligenceAvailable && _messageBus != null && HasCapability(IntelligenceCapabilities.CompressionRecommendation))
        {
            try
            {
                var response = await _messageBus.RequestAsync(
                    IntelligenceTopics.RequestCompressionRecommendation,
                    new Dictionary<string, object>
                    {
                        ["fileId"] = fileInfo.FileId,
                        ["fileName"] = fileInfo.FileName,
                        ["contentType"] = fileInfo.ContentType ?? "application/octet-stream",
                        ["size"] = fileInfo.SizeBytes
                    },
                    ct);

                if (response != null)
                {
                    if (response.TryGetValue("shouldCompress", out var compress) && compress is bool c)
                    {
                        recommendation.ShouldCompress = c;
                    }

                    if (response.TryGetValue("algorithm", out var algo))
                    {
                        recommendation.RecommendedAlgorithm = algo?.ToString();
                    }

                    if (response.TryGetValue("level", out var level) && level is int l)
                    {
                        recommendation.CompressionLevel = l;
                    }

                    if (response.TryGetValue("estimatedRatio", out var ratio) && ratio is double r)
                    {
                        recommendation.EstimatedRatio = r;
                    }

                    if (response.TryGetValue("reason", out var reason))
                    {
                        recommendation.Reason = reason?.ToString();
                    }
                }
            }
            catch
            {
                // Use basic recommendation
            }
        }

        return recommendation;
    }

    /// <summary>
    /// Gets organization recommendations for a set of files.
    /// </summary>
    /// <param name="files">The files to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Organization recommendations.</returns>
    public async Task<IReadOnlyList<OrganizationRecommendation>> GetOrganizationRecommendationsAsync(
        IEnumerable<FileAnalysisRequest> files,
        CancellationToken ct = default)
    {
        var recommendations = new List<OrganizationRecommendation>();
        var fileList = files.ToList();

        if (fileList.Count == 0)
            return recommendations.AsReadOnly();

        // Group by extension
        var byExtension = fileList.GroupBy(f => System.IO.Path.GetExtension(f.FileName)?.ToLowerInvariant());
        foreach (var group in byExtension.Where(g => g.Count() > 5))
        {
            recommendations.Add(new OrganizationRecommendation
            {
                Type = OrganizationType.GroupByType,
                Title = $"Group {group.Key} files",
                Description = $"You have {group.Count()} {group.Key} files that could be organized into a folder.",
                AffectedFiles = group.Select(f => f.FileId).ToList(),
                SuggestedPath = $"/{group.Key?.TrimStart('.') ?? "misc"}"
            });
        }

        // Group by date
        var byMonth = fileList
            .Where(f => f.LastModified.HasValue)
            .GroupBy(f => new { f.LastModified!.Value.Year, f.LastModified.Value.Month });

        foreach (var group in byMonth.Where(g => g.Count() > 10))
        {
            recommendations.Add(new OrganizationRecommendation
            {
                Type = OrganizationType.GroupByDate,
                Title = $"Archive {group.Key.Year}-{group.Key.Month:00} files",
                Description = $"You have {group.Count()} files from {group.Key.Year}-{group.Key.Month:00} that could be archived.",
                AffectedFiles = group.Select(f => f.FileId).ToList(),
                SuggestedPath = $"/archive/{group.Key.Year}/{group.Key.Month:00}"
            });
        }

        // Enhanced with AI clustering
        if (_intelligenceAvailable && _messageBus != null && HasCapability(IntelligenceCapabilities.Clustering))
        {
            try
            {
                var response = await _messageBus.RequestAsync(
                    "intelligence.request.file-organization",
                    new Dictionary<string, object>
                    {
                        ["files"] = fileList.Select(f => new Dictionary<string, object>
                        {
                            ["id"] = f.FileId,
                            ["name"] = f.FileName,
                            ["type"] = f.ContentType ?? "application/octet-stream",
                            ["size"] = f.SizeBytes
                        }).ToList()
                    },
                    ct);

                if (response != null && response.TryGetValue("clusters", out var clusters) &&
                    clusters is IEnumerable<object> clusterList)
                {
                    foreach (var cluster in clusterList.OfType<Dictionary<string, object>>())
                    {
                        var name = cluster.TryGetValue("name", out var n) ? n?.ToString() : "Unnamed";
                        var fileIds = cluster.TryGetValue("files", out var fids) && fids is IEnumerable<object> ids
                            ? ids.Select(id => id?.ToString() ?? string.Empty).ToList()
                            : new List<string>();

                        if (fileIds.Count > 3)
                        {
                            recommendations.Add(new OrganizationRecommendation
                            {
                                Type = OrganizationType.SemanticGroup,
                                Title = $"Create '{name}' folder",
                                Description = $"These {fileIds.Count} files appear to be semantically related and could be grouped together.",
                                AffectedFiles = fileIds,
                                SuggestedPath = $"/{name?.ToLowerInvariant().Replace(" ", "-")}"
                            });
                        }
                    }
                }
            }
            catch
            {
                // Use basic recommendations
            }
        }

        return recommendations
            .OrderByDescending(r => r.AffectedFiles.Count)
            .Take(_config.MaxRecommendations)
            .ToList()
            .AsReadOnly();
    }

    private List<Recommendation> GenerateBasicRecommendations(FileAnalysisRequest fileInfo)
    {
        var recommendations = new List<Recommendation>();
        var extension = System.IO.Path.GetExtension(fileInfo.FileName)?.ToLowerInvariant();

        // Encryption recommendations for sensitive file types
        if (IsSensitiveFileType(extension))
        {
            recommendations.Add(new Recommendation
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = RecommendationType.Encrypt,
                Title = "Consider encryption",
                Description = $"{extension} files often contain sensitive data. Consider encrypting this file.",
                Priority = RecommendationPriority.Medium,
                Action = new RecommendationAction
                {
                    ActionType = "encrypt",
                    Label = "Encrypt file",
                    Parameters = new Dictionary<string, object>
                    {
                        ["cipher"] = "AES-256-GCM"
                    }
                }
            });
        }

        // Large file recommendations
        if (fileInfo.SizeBytes > 100 * 1024 * 1024) // > 100MB
        {
            recommendations.Add(new Recommendation
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = RecommendationType.Compress,
                Title = "Compress large file",
                Description = $"This file is {fileInfo.SizeBytes / (1024 * 1024):N0} MB. Compression could save storage space.",
                Priority = RecommendationPriority.Low,
                Action = new RecommendationAction
                {
                    ActionType = "compress",
                    Label = "Compress file"
                }
            });
        }

        // Old file recommendations
        if (fileInfo.LastAccessed.HasValue &&
            (DateTime.UtcNow - fileInfo.LastAccessed.Value).TotalDays > 180)
        {
            recommendations.Add(new Recommendation
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = RecommendationType.Tier,
                Title = "Move to cold storage",
                Description = "This file hasn't been accessed in 6+ months. Moving to cold storage could reduce costs.",
                Priority = RecommendationPriority.Low,
                Action = new RecommendationAction
                {
                    ActionType = "tier",
                    Label = "Move to archive tier",
                    Parameters = new Dictionary<string, object>
                    {
                        ["tier"] = "archive"
                    }
                }
            });
        }

        return recommendations;
    }

    private async Task<List<Recommendation>> GenerateAIRecommendationsAsync(
        FileAnalysisRequest fileInfo,
        string? content,
        CancellationToken ct)
    {
        var recommendations = new List<Recommendation>();

        // PII Detection
        if (!string.IsNullOrEmpty(content) && HasCapability(IntelligenceCapabilities.PIIDetection))
        {
            var piiResult = await AnalyzeForPIIAsync(content, ct);
            recommendations.AddRange(piiResult.Recommendations);
        }

        // Classification
        if (HasCapability(IntelligenceCapabilities.Classification))
        {
            try
            {
                var response = await _messageBus!.RequestAsync(
                    IntelligenceTopics.RequestClassification,
                    new Dictionary<string, object>
                    {
                        ["text"] = fileInfo.FileName + " " + (content ?? string.Empty),
                        ["categories"] = new[] { "confidential", "public", "internal", "restricted" },
                        ["multiLabel"] = false
                    },
                    ct);

                if (response != null &&
                    response.TryGetValue("classifications", out var classifications) &&
                    classifications is IEnumerable<object> classList)
                {
                    var topClass = classList
                        .OfType<Dictionary<string, object>>()
                        .OrderByDescending(c => c.TryGetValue("confidence", out var conf) && conf is double d ? d : 0)
                        .FirstOrDefault();

                    if (topClass != null)
                    {
                        var category = topClass.TryGetValue("category", out var cat) ? cat?.ToString() : null;
                        var confidence = topClass.TryGetValue("confidence", out var conf) && conf is double c ? c : 0;

                        if (category == "confidential" || category == "restricted")
                        {
                            recommendations.Add(new Recommendation
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Type = RecommendationType.Encrypt,
                                Title = $"File classified as {category}",
                                Description = $"AI analysis suggests this file is {category} (confidence: {confidence:P0}). Consider applying appropriate security controls.",
                                Priority = RecommendationPriority.High,
                                Confidence = confidence,
                                Action = new RecommendationAction
                                {
                                    ActionType = "encrypt",
                                    Label = "Apply security controls"
                                }
                            });
                        }
                    }
                }
            }
            catch
            {
                // Skip classification
            }
        }

        return recommendations;
    }

    private List<string> DetectBasicPII(string content)
    {
        var detected = new List<string>();

        // Email pattern
        if (System.Text.RegularExpressions.Regex.IsMatch(content, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"))
            detected.Add("Email addresses");

        // Phone pattern (various formats)
        if (System.Text.RegularExpressions.Regex.IsMatch(content, @"\b(\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b"))
            detected.Add("Phone numbers");

        // SSN pattern
        if (System.Text.RegularExpressions.Regex.IsMatch(content, @"\b\d{3}-\d{2}-\d{4}\b"))
            detected.Add("Social Security Numbers");

        // Credit card pattern (basic)
        if (System.Text.RegularExpressions.Regex.IsMatch(content, @"\b(?:\d{4}[-\s]?){3}\d{4}\b"))
            detected.Add("Credit card numbers");

        return detected;
    }

    private bool IsSensitiveFileType(string? extension)
    {
        var sensitiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xlsx", ".xls", ".csv", ".pdf", ".doc", ".docx",
            ".pem", ".key", ".crt", ".pfx", ".p12",
            ".bak", ".sql", ".mdb", ".accdb"
        };

        return extension != null && sensitiveExtensions.Contains(extension);
    }

    private bool IsCompressedFormat(string? extension, string contentType)
    {
        var compressedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".gz", ".bz2", ".xz", ".7z", ".rar", ".tar.gz", ".tgz",
            ".jpg", ".jpeg", ".png", ".gif", ".mp4", ".mp3", ".webp", ".avif"
        };

        return extension != null && compressedExtensions.Contains(extension);
    }

    private bool IsTextFormat(string? extension, string contentType)
    {
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".json", ".xml", ".html", ".css", ".js", ".ts",
            ".md", ".yaml", ".yml", ".csv", ".sql", ".py", ".cs", ".java"
        };

        return (extension != null && textExtensions.Contains(extension)) ||
               contentType.StartsWith("text/");
    }

    private StorageTier ParseStorageTier(string? tier)
    {
        return tier?.ToLowerInvariant() switch
        {
            "hot" => StorageTier.Hot,
            "warm" => StorageTier.Warm,
            "cool" => StorageTier.Cool,
            "cold" => StorageTier.Cold,
            "archive" => StorageTier.Archive,
            _ => StorageTier.Hot
        };
    }

    private List<Recommendation> DeduplicateRecommendations(List<Recommendation> recommendations)
    {
        return recommendations
            .GroupBy(r => r.Type)
            .Select(g => g.OrderByDescending(r => r.Priority).ThenByDescending(r => r.Confidence).First())
            .OrderByDescending(r => r.Priority)
            .Take(_config.MaxRecommendations)
            .ToList();
    }

    private bool HasCapability(IntelligenceCapabilities capability)
    {
        return (_capabilities & capability) == capability;
    }

    private void OnRecommendationGenerated(Recommendation recommendation)
    {
        RecommendationGenerated?.Invoke(this, new RecommendationEventArgs { Recommendation = recommendation });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cache.Clear();
    }
}

/// <summary>
/// A recommendation for file operations.
/// </summary>
public sealed class Recommendation
{
    /// <summary>Gets or sets the recommendation ID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the recommendation type.</summary>
    public RecommendationType Type { get; init; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets the priority.</summary>
    public RecommendationPriority Priority { get; init; }

    /// <summary>Gets or sets the confidence score.</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Gets or sets the action to take.</summary>
    public RecommendationAction? Action { get; init; }
}

/// <summary>
/// Recommendation type.
/// </summary>
public enum RecommendationType
{
    /// <summary>Encryption recommendation.</summary>
    Encrypt,

    /// <summary>Compression recommendation.</summary>
    Compress,

    /// <summary>Storage tier recommendation.</summary>
    Tier,

    /// <summary>Compliance recommendation.</summary>
    Compliance,

    /// <summary>Organization recommendation.</summary>
    Organize,

    /// <summary>Backup recommendation.</summary>
    Backup,

    /// <summary>Security recommendation.</summary>
    Security
}

/// <summary>
/// Recommendation priority.
/// </summary>
public enum RecommendationPriority
{
    /// <summary>Low priority.</summary>
    Low,

    /// <summary>Medium priority.</summary>
    Medium,

    /// <summary>High priority.</summary>
    High
}

/// <summary>
/// Action to take for a recommendation.
/// </summary>
public sealed class RecommendationAction
{
    /// <summary>Gets or sets the action type.</summary>
    public string ActionType { get; init; } = string.Empty;

    /// <summary>Gets or sets the button label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets or sets action parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// File analysis request.
/// </summary>
public sealed class FileAnalysisRequest
{
    /// <summary>Gets or sets the file ID.</summary>
    public string FileId { get; init; } = string.Empty;

    /// <summary>Gets or sets the file name.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Gets or sets the content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Gets or sets the last modified date.</summary>
    public DateTime? LastModified { get; init; }

    /// <summary>Gets or sets the last accessed date.</summary>
    public DateTime? LastAccessed { get; init; }

    /// <summary>Gets or sets custom metadata.</summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// PII analysis result.
/// </summary>
public sealed class PIIAnalysisResult
{
    /// <summary>Gets or sets whether PII was detected.</summary>
    public bool ContainsPII { get; set; }

    /// <summary>Gets or sets detected PII types.</summary>
    public List<string> DetectedTypes { get; set; } = new();

    /// <summary>Gets or sets recommendations.</summary>
    public List<Recommendation> Recommendations { get; init; } = new();
}

/// <summary>
/// Tiering recommendation.
/// </summary>
public sealed class TieringRecommendation
{
    /// <summary>Gets or sets the file ID.</summary>
    public string FileId { get; init; } = string.Empty;

    /// <summary>Gets or sets the recommended tier.</summary>
    public StorageTier RecommendedTier { get; set; }

    /// <summary>Gets or sets the reason.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets estimated savings in GB-months.</summary>
    public decimal EstimatedSavings { get; set; }

    /// <summary>Gets or sets the confidence score.</summary>
    public double Confidence { get; set; } = 0.8;

    /// <summary>Gets or sets predicted next access date.</summary>
    public DateTime? PredictedNextAccess { get; set; }
}

/// <summary>
/// Storage tier.
/// </summary>
public enum StorageTier
{
    /// <summary>Hot storage for frequently accessed data.</summary>
    Hot,

    /// <summary>Warm storage for occasionally accessed data.</summary>
    Warm,

    /// <summary>Cool storage for infrequently accessed data.</summary>
    Cool,

    /// <summary>Cold storage for rarely accessed data.</summary>
    Cold,

    /// <summary>Archive storage for long-term retention.</summary>
    Archive
}

/// <summary>
/// Access pattern data.
/// </summary>
public sealed class AccessPatternData
{
    /// <summary>Gets or sets the access count.</summary>
    public int AccessCount { get; init; }

    /// <summary>Gets or sets the last access date.</summary>
    public DateTime LastAccessDate { get; init; }

    /// <summary>Gets or sets the average access interval in days.</summary>
    public double AverageAccessIntervalDays { get; init; }
}

/// <summary>
/// Compression recommendation.
/// </summary>
public sealed class CompressionRecommendation
{
    /// <summary>Gets or sets the file ID.</summary>
    public string FileId { get; init; } = string.Empty;

    /// <summary>Gets or sets whether compression is recommended.</summary>
    public bool ShouldCompress { get; set; }

    /// <summary>Gets or sets the recommended algorithm.</summary>
    public string? RecommendedAlgorithm { get; set; }

    /// <summary>Gets or sets the compression level.</summary>
    public int CompressionLevel { get; set; }

    /// <summary>Gets or sets the estimated compression ratio (0-1).</summary>
    public double EstimatedRatio { get; set; }

    /// <summary>Gets or sets the reason.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Organization recommendation.
/// </summary>
public sealed class OrganizationRecommendation
{
    /// <summary>Gets or sets the organization type.</summary>
    public OrganizationType Type { get; init; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets affected file IDs.</summary>
    public List<string> AffectedFiles { get; init; } = new();

    /// <summary>Gets or sets the suggested path.</summary>
    public string SuggestedPath { get; init; } = string.Empty;
}

/// <summary>
/// Organization type.
/// </summary>
public enum OrganizationType
{
    /// <summary>Group by file type.</summary>
    GroupByType,

    /// <summary>Group by date.</summary>
    GroupByDate,

    /// <summary>Semantic grouping.</summary>
    SemanticGroup,

    /// <summary>Project-based grouping.</summary>
    ProjectGroup
}

/// <summary>
/// Recommendation engine configuration.
/// </summary>
public sealed class RecommendationEngineConfig
{
    /// <summary>Maximum recommendations to return.</summary>
    public int MaxRecommendations { get; init; } = 5;

    /// <summary>Cache duration in minutes.</summary>
    public int CacheDurationMinutes { get; init; } = 10;

    /// <summary>Enable PII detection.</summary>
    public bool EnablePIIDetection { get; init; } = true;

    /// <summary>Enable tiering recommendations.</summary>
    public bool EnableTieringRecommendations { get; init; } = true;
}

/// <summary>
/// Event args for recommendation generated.
/// </summary>
public sealed class RecommendationEventArgs : EventArgs
{
    /// <summary>Gets or sets the recommendation.</summary>
    public required Recommendation Recommendation { get; init; }
}

/// <summary>
/// Internal recommendation cache.
/// </summary>
internal sealed class RecommendationCache
{
    public List<Recommendation> Recommendations { get; init; } = new();
    public DateTime GeneratedAt { get; init; }
}
