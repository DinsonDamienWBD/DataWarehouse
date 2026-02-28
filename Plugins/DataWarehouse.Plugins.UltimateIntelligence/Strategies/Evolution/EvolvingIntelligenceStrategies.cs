using System.Text.Json;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Evolution;

#region Helper Types

/// <summary>
/// Expertise level classification for evolved intelligence.
/// </summary>
public enum ExpertiseLevel
{
    /// <summary>Beginner level (0-100 interactions).</summary>
    Novice = 0,

    /// <summary>Learning basic patterns (100-500 interactions).</summary>
    Apprentice = 1,

    /// <summary>Competent with established patterns (500-2000 interactions).</summary>
    Competent = 2,

    /// <summary>Proficient with deep understanding (2000-5000 interactions).</summary>
    Proficient = 3,

    /// <summary>Expert with mastery (5000-10000 interactions).</summary>
    Expert = 4,

    /// <summary>Master level (10000+ interactions).</summary>
    Master = 5
}

/// <summary>
/// Metrics tracking evolution and learning progress.
/// </summary>
public sealed record EvolutionMetrics
{
    /// <summary>Total number of interactions processed.</summary>
    public long TotalInteractions { get; init; }

    /// <summary>Number of successful predictions/responses.</summary>
    public long SuccessfulPredictions { get; init; }

    /// <summary>Number of failed or corrected predictions.</summary>
    public long FailedPredictions { get; init; }

    /// <summary>Current accuracy rate (0.0-1.0).</summary>
    public double AccuracyRate => TotalInteractions > 0
        ? (double)SuccessfulPredictions / TotalInteractions
        : 0.0;

    /// <summary>Current expertise level.</summary>
    public ExpertiseLevel ExpertiseLevel { get; init; }

    /// <summary>Learning rate (how quickly improving).</summary>
    public double LearningRate { get; init; }

    /// <summary>Knowledge retention score (0.0-1.0).</summary>
    public double KnowledgeRetention { get; init; }

    /// <summary>Number of distinct domains mastered.</summary>
    public int DomainsCount { get; init; }

    /// <summary>Last evolution/consolidation timestamp.</summary>
    public DateTime LastEvolutionTime { get; init; }

    /// <summary>Version number for learned model.</summary>
    public int ModelVersion { get; init; }
}

/// <summary>
/// Package for transferring knowledge between agents.
/// </summary>
public sealed record KnowledgePackage
{
    /// <summary>Unique identifier for this knowledge package.</summary>
    public string PackageId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Source agent that created this knowledge.</summary>
    public string SourceAgentId { get; init; } = "";

    /// <summary>Domain or topic this knowledge applies to.</summary>
    public string Domain { get; init; } = "";

    /// <summary>Knowledge type (patterns, rules, examples, etc.).</summary>
    public string KnowledgeType { get; init; } = "";

    /// <summary>Serialized knowledge data.</summary>
    public string SerializedData { get; init; } = "";

    /// <summary>Confidence score for this knowledge (0.0-1.0).</summary>
    public double ConfidenceScore { get; init; }

    /// <summary>Number of validations/confirmations.</summary>
    public int ValidationCount { get; init; }

    /// <summary>Timestamp when knowledge was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Metadata about the knowledge.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Feedback record for learning from interactions.
/// </summary>
public sealed record LearningFeedback
{
    /// <summary>Unique identifier for this feedback.</summary>
    public string FeedbackId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Interaction that this feedback refers to.</summary>
    public string InteractionId { get; init; } = "";

    /// <summary>Feedback type (Positive, Negative, Correction, etc.).</summary>
    public FeedbackType Type { get; init; }

    /// <summary>Rating score if applicable (1-5).</summary>
    public int? Rating { get; init; }

    /// <summary>Textual feedback or correction.</summary>
    public string? Comment { get; init; }

    /// <summary>Corrected response if this was a correction.</summary>
    public string? CorrectedResponse { get; init; }

    /// <summary>Timestamp of feedback.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Type of learning feedback.
/// </summary>
public enum FeedbackType
{
    /// <summary>Positive feedback (good response).</summary>
    Positive,

    /// <summary>Negative feedback (poor response).</summary>
    Negative,

    /// <summary>Correction provided.</summary>
    Correction,

    /// <summary>Neutral/informational feedback.</summary>
    Neutral
}

/// <summary>
/// User profile for adaptive personalization.
/// </summary>
public sealed record UserProfile
{
    /// <summary>User identifier.</summary>
    public string UserId { get; init; } = "";

    /// <summary>User preferences extracted from history.</summary>
    public Dictionary<string, object> Preferences { get; init; } = new();

    /// <summary>Common query patterns.</summary>
    public List<string> QueryPatterns { get; init; } = new();

    /// <summary>Interaction count with this user.</summary>
    public int InteractionCount { get; init; }

    /// <summary>Average satisfaction rating.</summary>
    public double AverageSatisfaction { get; init; }

    /// <summary>Preferred response style.</summary>
    public string? PreferredStyle { get; init; }

    /// <summary>Last interaction timestamp.</summary>
    public DateTime LastInteraction { get; init; }
}

#endregion

#region 1. Evolving Expert Strategy

/// <summary>
/// Self-improving domain expert that learns from experience.
/// Combines Long-Term Memory for experience storage with Tabular Models for pattern learning.
/// Tracks successful/failed predictions and continuously improves.
/// </summary>
public sealed class EvolvingExpertStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, EvolutionMetrics> _domainMetrics = new BoundedDictionary<string, EvolutionMetrics>(1000);
    private readonly BoundedDictionary<string, List<(string Query, string Response, bool Success)>> _experienceBuffer = new BoundedDictionary<string, List<(string Query, string Response, bool Success)>>(1000);
    private string? _storagePath;

    /// <inheritdoc/>
    public override string StrategyId => "evolution-expert";

    /// <inheritdoc/>
    public override string StrategyName => "Evolving Expert";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Evolving Expert",
        Description = "Self-improving domain expert that learns from feedback and grows expertise over time",
        Capabilities = IntelligenceCapabilities.Prediction |
                      IntelligenceCapabilities.MemoryStorage |
                      IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.TabularClassification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "StoragePath", Description = "Path to persist learned models", Required = false, DefaultValue = "./evolution-data/expert" },
            new ConfigurationRequirement { Key = "ConsolidationThreshold", Description = "Number of experiences before consolidation", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "MinConfidence", Description = "Minimum confidence for predictions", Required = false, DefaultValue = "0.7" }
        },
        CostTier = 2,
        LatencyTier = 2,
        SupportsOfflineMode = true,
        RequiresNetworkAccess = false,
        Tags = new[] { "learning", "expertise", "evolution", "domain-expert", "adaptive" }
    };

    /// <summary>
    /// Learns from an interaction with feedback.
    /// </summary>
    public async Task LearnFromInteractionAsync(
        string query,
        string response,
        LearningFeedback feedback,
        string domain,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Store experience in LTM if available
            if (VectorStore != null && AIProvider != null)
            {
                var embedding = await AIProvider.GetEmbeddingsAsync($"{query} {response}", ct);
                var metadata = new Dictionary<string, object>
                {
                    ["domain"] = domain,
                    ["query"] = query,
                    ["response"] = response,
                    ["feedback_type"] = feedback.Type.ToString(),
                    ["success"] = feedback.Type == FeedbackType.Positive,
                    ["timestamp"] = DateTime.UtcNow
                };

                await VectorStore.StoreAsync($"exp_{domain}_{Guid.NewGuid()}", embedding, metadata, ct);
            }

            // Update domain metrics
            var metrics = _domainMetrics.GetOrAdd(domain, _ => new EvolutionMetrics
            {
                ModelVersion = 1,
                LastEvolutionTime = DateTime.UtcNow
            });

            var success = feedback.Type == FeedbackType.Positive;
            var updatedMetrics = metrics with
            {
                TotalInteractions = metrics.TotalInteractions + 1,
                SuccessfulPredictions = success ? metrics.SuccessfulPredictions + 1 : metrics.SuccessfulPredictions,
                FailedPredictions = success ? metrics.FailedPredictions : metrics.FailedPredictions + 1,
                ExpertiseLevel = CalculateExpertiseLevel(metrics.TotalInteractions + 1),
                LearningRate = CalculateLearningRate(metrics.TotalInteractions + 1, success)
            };

            _domainMetrics[domain] = updatedMetrics;

            // Buffer experience for batch learning
            _experienceBuffer.AddOrUpdate(domain,
                _ => new List<(string, string, bool)> { (query, response, success) },
                (_, list) => { list.Add((query, response, success)); return list; });

            // Trigger consolidation if threshold reached
            var threshold = int.Parse(GetConfig("ConsolidationThreshold") ?? "100");
            if (_experienceBuffer[domain].Count >= threshold)
            {
                await EvolveExpertiseAsync(domain, ct);
            }
        });
    }

    /// <summary>
    /// Gets the current expertise score for a domain.
    /// </summary>
    public Task<EvolutionMetrics> GetExpertiseScoreAsync(string domain, CancellationToken ct = default)
    {
        var metrics = _domainMetrics.GetOrAdd(domain, _ => new EvolutionMetrics
        {
            ModelVersion = 1,
            LastEvolutionTime = DateTime.UtcNow
        });

        return Task.FromResult(metrics);
    }

    /// <summary>
    /// Recalls relevant experiences for a query.
    /// </summary>
    public async Task<IEnumerable<(string Query, string Response, double Relevance)>> RecallRelevantExperienceAsync(
        string query,
        string domain,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (VectorStore == null || AIProvider == null)
            return Array.Empty<(string, string, double)>();

        return await ExecuteWithTrackingAsync(async () =>
        {
            // Get query embedding
            var queryEmbedding = await AIProvider.GetEmbeddingsAsync(query, ct);

            // Search for similar experiences
            var filter = new Dictionary<string, object> { ["domain"] = domain };
            var matches = await VectorStore.SearchAsync(queryEmbedding, topK, 0.5f, filter, ct);

            return matches.Select(m => (
                Query: m.Entry.Metadata.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "",
                Response: m.Entry.Metadata.TryGetValue("response", out var r) ? r?.ToString() ?? "" : "",
                Relevance: (double)m.Score
            )).ToList();
        });
    }

    /// <summary>
    /// Consolidates learning and evolves expertise.
    /// </summary>
    public async Task EvolveExpertiseAsync(string? domain = null, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var domains = domain != null ? new[] { domain } : _experienceBuffer.Keys.ToArray();

            foreach (var dom in domains)
            {
                if (!_experienceBuffer.TryRemove(dom, out var experiences))
                    continue;

                // Train tabular model on experiences (simplified)
                var successRate = experiences.Count(e => e.Success) / (double)experiences.Count;

                // Update metrics
                if (_domainMetrics.TryGetValue(dom, out var metrics))
                {
                    var evolved = metrics with
                    {
                        ModelVersion = metrics.ModelVersion + 1,
                        LastEvolutionTime = DateTime.UtcNow,
                        KnowledgeRetention = CalculateKnowledgeRetention(successRate, metrics.KnowledgeRetention)
                    };

                    _domainMetrics[dom] = evolved;

                    // Persist to storage
                    await PersistMetricsAsync(dom, evolved, ct);
                }
            }
        });
    }

    private ExpertiseLevel CalculateExpertiseLevel(long interactions)
    {
        return interactions switch
        {
            < 100 => ExpertiseLevel.Novice,
            < 500 => ExpertiseLevel.Apprentice,
            < 2000 => ExpertiseLevel.Competent,
            < 5000 => ExpertiseLevel.Proficient,
            < 10000 => ExpertiseLevel.Expert,
            _ => ExpertiseLevel.Master
        };
    }

    private double CalculateLearningRate(long interactions, bool success)
    {
        // Learning rate decreases as expertise grows, increases with failures
        var baseRate = 1.0 / Math.Log(interactions + 10);
        return success ? baseRate * 0.9 : baseRate * 1.2;
    }

    private double CalculateKnowledgeRetention(double recentSuccess, double previousRetention)
    {
        // Exponential moving average
        return 0.7 * recentSuccess + 0.3 * previousRetention;
    }

    private async Task PersistMetricsAsync(string domain, EvolutionMetrics metrics, CancellationToken ct)
    {
        _storagePath ??= GetConfig("StoragePath") ?? "./evolution-data/expert";
        Directory.CreateDirectory(_storagePath);

        var filePath = Path.Combine(_storagePath, $"{domain}.json");
        var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, ct);
    }
}

#endregion

#region 2. Adaptive Model Strategy

/// <summary>
/// Models that adapt to individual user patterns and preferences.
/// Personalizes responses based on interaction history and feedback.
/// </summary>
public sealed class AdaptiveModelStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, UserProfile> _userProfiles = new BoundedDictionary<string, UserProfile>(1000);
    private readonly BoundedDictionary<string, List<(string Query, string Response, int? Rating)>> _userHistory = new BoundedDictionary<string, List<(string Query, string Response, int? Rating)>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "evolution-adaptive";

    /// <inheritdoc/>
    public override string StrategyName => "Adaptive Model";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Adaptive Model",
        Description = "AI that adapts to individual user patterns and preferences over time",
        Capabilities = IntelligenceCapabilities.Prediction |
                      IntelligenceCapabilities.MemoryStorage |
                      IntelligenceCapabilities.MemoryRetrieval,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "StoragePath", Description = "Path to persist user profiles", Required = false, DefaultValue = "./evolution-data/adaptive" },
            new ConfigurationRequirement { Key = "MinInteractions", Description = "Minimum interactions before adaptation", Required = false, DefaultValue = "10" }
        },
        CostTier = 2,
        LatencyTier = 2,
        SupportsOfflineMode = true,
        RequiresNetworkAccess = false,
        Tags = new[] { "adaptive", "personalization", "user-modeling", "preferences" }
    };

    /// <summary>
    /// Tracks a user interaction for learning.
    /// </summary>
    public async Task TrackUserInteractionAsync(
        string userId,
        string query,
        string response,
        int? rating = null,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Update history
            _userHistory.AddOrUpdate(userId,
                _ => new List<(string, string, int?)> { (query, response, rating) },
                (_, list) => { list.Add((query, response, rating)); return list; });

            // Update profile
            var profile = _userProfiles.GetOrAdd(userId, _ => new UserProfile
            {
                UserId = userId,
                LastInteraction = DateTime.UtcNow
            });

            var history = _userHistory[userId];
            var avgSatisfaction = history.Where(h => h.Rating.HasValue)
                .Select(h => h.Rating!.Value)
                .DefaultIfEmpty(0)
                .Average();

            var updatedProfile = profile with
            {
                InteractionCount = history.Count,
                AverageSatisfaction = avgSatisfaction,
                LastInteraction = DateTime.UtcNow
            };

            _userProfiles[userId] = updatedProfile;

            // Store in LTM
            if (VectorStore != null && AIProvider != null)
            {
                var embedding = await AIProvider.GetEmbeddingsAsync(query, ct);
                var metadata = new Dictionary<string, object>
                {
                    ["user_id"] = userId,
                    ["query"] = query,
                    ["response"] = response,
                    ["rating"] = rating ?? 0,
                    ["timestamp"] = DateTime.UtcNow
                };

                await VectorStore.StoreAsync($"user_{userId}_{Guid.NewGuid()}", embedding, metadata, ct);
            }
        });
    }

    /// <summary>
    /// Gets the user profile.
    /// </summary>
    public Task<UserProfile> GetUserProfileAsync(string userId, CancellationToken ct = default)
    {
        var profile = _userProfiles.GetOrAdd(userId, _ => new UserProfile
        {
            UserId = userId,
            LastInteraction = DateTime.UtcNow
        });

        return Task.FromResult(profile);
    }

    /// <summary>
    /// Personalizes a response based on user history.
    /// </summary>
    public async Task<string> PersonalizeResponseAsync(
        string baseResponse,
        string userId,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            return baseResponse;

        return await ExecuteWithTrackingAsync(async () =>
        {
            var profile = await GetUserProfileAsync(userId, ct);
            var minInteractions = int.Parse(GetConfig("MinInteractions") ?? "10");

            // Not enough data yet
            if (profile.InteractionCount < minInteractions)
                return baseResponse;

            // Use AI to personalize
            var prompt = $"Personalize this response for a user with these preferences:\n" +
                        $"Average satisfaction: {profile.AverageSatisfaction:F2}/5\n" +
                        $"Interaction count: {profile.InteractionCount}\n" +
                        $"Preferred style: {profile.PreferredStyle ?? "unknown"}\n\n" +
                        $"Base response: {baseResponse}\n\n" +
                        $"Personalized response:";

            var response = await AIProvider.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 500 }, ct);
            return response.Content;
        });
    }

    /// <summary>
    /// Adapts model based on explicit feedback.
    /// </summary>
    public async Task AdaptToFeedbackAsync(
        string interactionId,
        LearningFeedback feedback,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Extract patterns from feedback and update preferences
            // This would involve more sophisticated learning in production
            await Task.CompletedTask;
        });
    }
}

#endregion

#region 3. Continual Learning Strategy

/// <summary>
/// Never-forgetting incremental learner using Elastic Weight Consolidation.
/// Learns new tasks while preserving knowledge from previous tasks.
/// </summary>
public sealed class ContinualLearningStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, TaskMetrics> _taskMetrics = new BoundedDictionary<string, TaskMetrics>(1000);
    // Use ConcurrentBag for _learnedTasks to allow thread-safe concurrent enumeration and add (finding 3093).
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _learnedTasks = new();

    /// <inheritdoc/>
    public override string StrategyId => "evolution-continual";

    /// <inheritdoc/>
    // EWC model training, Fisher information matrix, and knowledge consolidation are not yet
    // integrated with an ML runtime. Mark not production-ready until T118 (ML runtime) lands.
    public override bool IsProductionReady => false;

    /// <inheritdoc/>
    public override string StrategyName => "Continual Learning";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Continual Learning",
        Description = "Never-forgetting incremental learner that preserves old knowledge while learning new tasks",
        Capabilities = IntelligenceCapabilities.TabularClassification |
                      IntelligenceCapabilities.TabularRegression |
                      IntelligenceCapabilities.MemoryConsolidation,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "StoragePath", Description = "Path to persist task models", Required = false, DefaultValue = "./evolution-data/continual" },
            new ConfigurationRequirement { Key = "ConsolidationWeight", Description = "EWC consolidation weight", Required = false, DefaultValue = "0.5" }
        },
        CostTier = 3,
        LatencyTier = 3,
        SupportsOfflineMode = true,
        RequiresNetworkAccess = false,
        Tags = new[] { "continual-learning", "ewc", "no-forgetting", "incremental" }
    };

    /// <summary>
    /// Learns a new task from examples.
    /// </summary>
    public async Task LearnNewTaskAsync(
        string taskName,
        IEnumerable<(object[] Features, object Label)> examples,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var exampleList = examples.ToList();

            // Record task
            if (!_learnedTasks.Contains(taskName))
                _learnedTasks.Add(taskName);

            // Create/update task metrics
            var metrics = _taskMetrics.GetOrAdd(taskName, _ => new TaskMetrics
            {
                TaskName = taskName,
                LearnedAt = DateTime.UtcNow
            });

            var updated = metrics with
            {
                ExampleCount = metrics.ExampleCount + exampleList.Count,
                LastTrainingTime = DateTime.UtcNow
            };

            _taskMetrics[taskName] = updated;

            // In production: Train model with EWC to prevent catastrophic forgetting
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Evaluates performance on all learned tasks.
    /// </summary>
    public async Task<Dictionary<string, double>> EvaluateOnAllTasksAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var results = new Dictionary<string, double>();

            foreach (var task in _learnedTasks)
            {
                // Actual evaluation requires running the EWC model against a held-out test set.
                // Return per-task metric from stored training statistics until ML runtime is integrated.
                var metrics = _taskMetrics.TryGetValue(task, out var m) ? m : null;
                results[task] = metrics != null && metrics.ExampleCount > 0
                    ? Math.Min(1.0, 0.5 + metrics.ExampleCount * 0.001) // rough convergence heuristic
                    : 0.0;
            }

            await Task.CompletedTask;
            return results;
        });
    }

    /// <summary>
    /// Gets knowledge retention score across all tasks.
    /// </summary>
    public async Task<double> GetKnowledgeRetentionScoreAsync(CancellationToken ct = default)
    {
        var evaluations = await EvaluateOnAllTasksAsync(ct);
        return evaluations.Any() ? evaluations.Values.Average() : 0.0;
    }

    /// <summary>
    /// Consolidates knowledge to strengthen important weights.
    /// </summary>
    public async Task ConsolidateKnowledgeAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // In production: Apply EWC consolidation
            // Calculate Fisher information matrix for important parameters
            // Apply consolidation weight to prevent forgetting
            await Task.CompletedTask;
        });
    }

    private sealed record TaskMetrics
    {
        public string TaskName { get; init; } = "";
        public int ExampleCount { get; init; }
        public DateTime LearnedAt { get; init; }
        public DateTime LastTrainingTime { get; init; }
        public double LastAccuracy { get; init; }
    }
}

#endregion

#region 4. Collective Intelligence Strategy

/// <summary>
/// Multi-agent shared learning system with federated learning capabilities.
/// Enables knowledge sharing and collective wisdom across agent instances.
/// </summary>
public sealed class CollectiveIntelligenceStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, KnowledgePackage> _sharedKnowledge = new BoundedDictionary<string, KnowledgePackage>(1000);
    private readonly string _agentId = Guid.NewGuid().ToString();

    /// <inheritdoc/>
    public override string StrategyId => "evolution-collective";

    /// <inheritdoc/>
    public override string StrategyName => "Collective Intelligence";

    /// <inheritdoc/>
    // Federated learning aggregation and knowledge distillation require an ML runtime.
    public override bool IsProductionReady => false;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Collective Intelligence",
        Description = "Multi-agent shared learning with federated learning and knowledge distillation",
        Capabilities = IntelligenceCapabilities.MemoryStorage |
                      IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.MultiAgentCollaboration,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "StoragePath", Description = "Path to shared knowledge store", Required = false, DefaultValue = "./evolution-data/collective" },
            new ConfigurationRequirement { Key = "MinConfidence", Description = "Minimum confidence to share knowledge", Required = false, DefaultValue = "0.8" }
        },
        CostTier = 3,
        LatencyTier = 2,
        SupportsOfflineMode = true,
        RequiresNetworkAccess = false,
        Tags = new[] { "collective", "federated-learning", "knowledge-sharing", "multi-agent" }
    };

    /// <summary>
    /// Shares knowledge with the collective.
    /// </summary>
    public async Task ShareKnowledgeAsync(
        KnowledgePackage knowledgePackage,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var minConfidence = double.Parse(GetConfig("MinConfidence") ?? "0.8");

            // Only share high-confidence knowledge
            if (knowledgePackage.ConfidenceScore < minConfidence)
                return;

            // Store in shared knowledge base
            _sharedKnowledge[knowledgePackage.PackageId] = knowledgePackage;

            // Persist to storage for other agents
            var storagePath = GetConfig("StoragePath") ?? "./evolution-data/collective";
            Directory.CreateDirectory(storagePath);

            var filePath = Path.Combine(storagePath, $"{knowledgePackage.PackageId}.json");
            var json = JsonSerializer.Serialize(knowledgePackage, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, ct);

            // Store in vector database for semantic search
            if (VectorStore != null && AIProvider != null)
            {
                var embedding = await AIProvider.GetEmbeddingsAsync(
                    $"{knowledgePackage.Domain} {knowledgePackage.KnowledgeType}", ct);

                await VectorStore.StoreAsync(
                    knowledgePackage.PackageId,
                    embedding,
                    new Dictionary<string, object>
                    {
                        ["domain"] = knowledgePackage.Domain,
                        ["type"] = knowledgePackage.KnowledgeType,
                        ["source"] = knowledgePackage.SourceAgentId,
                        ["confidence"] = knowledgePackage.ConfidenceScore
                    },
                    ct);
            }
        });
    }

    /// <summary>
    /// Receives knowledge from another agent.
    /// </summary>
    public async Task ReceiveKnowledgeAsync(
        string fromAgentId,
        KnowledgePackage knowledge,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Validate and integrate knowledge
            _sharedKnowledge[knowledge.PackageId] = knowledge;

            // In production: Merge with local model using federated learning
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Distills expertise from an expert agent.
    /// </summary>
    public async Task DistillExpertiseAsync(
        string expertAgentId,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Get all knowledge from expert agent
            var expertKnowledge = _sharedKnowledge.Values
                .Where(k => k.SourceAgentId == expertAgentId)
                .ToList();

            // In production: Apply knowledge distillation
            // Transfer knowledge from complex expert model to simpler student model
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Gets collective wisdom on a topic.
    /// </summary>
    public async Task<IEnumerable<KnowledgePackage>> GetCollectiveWisdomAsync(
        string topic,
        int topK = 10,
        CancellationToken ct = default)
    {
        if (VectorStore == null || AIProvider == null)
            return Array.Empty<KnowledgePackage>();

        return await ExecuteWithTrackingAsync(async () =>
        {
            // Search for relevant knowledge
            var embedding = await AIProvider.GetEmbeddingsAsync(topic, ct);
            var matches = await VectorStore.SearchAsync(embedding, topK, 0.6f, null, ct);

            // Retrieve knowledge packages
            var packages = matches
                .Select(m => _sharedKnowledge.TryGetValue(m.Entry.Id, out var pkg) ? pkg : null)
                .Where(pkg => pkg != null)
                .Cast<KnowledgePackage>()
                .OrderByDescending(pkg => pkg.ConfidenceScore * pkg.ValidationCount)
                .ToList();

            return packages;
        });
    }
}

#endregion

#region 5. Meta-Learning Strategy

/// <summary>
/// Learning to learn faster through meta-learning and few-shot learning.
/// Optimizes learning strategies and enables rapid adaptation to new tasks.
/// </summary>
public sealed class MetaLearningStrategy : FeatureStrategyBase
{
    private readonly BoundedDictionary<string, MetaKnowledge> _metaKnowledge = new BoundedDictionary<string, MetaKnowledge>(1000);
    private double _learningEfficiency = 0.5;

    /// <inheritdoc/>
    public override string StrategyId => "evolution-metalearning";

    /// <inheritdoc/>
    public override string StrategyName => "Meta-Learning";

    /// <inheritdoc/>
    // MAML and transfer learning require an ML runtime integration.
    public override bool IsProductionReady => false;

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Meta-Learning",
        Description = "Learning to learn faster through few-shot learning and task-agnostic knowledge transfer",
        Capabilities = IntelligenceCapabilities.TabularClassification |
                      IntelligenceCapabilities.TabularRegression |
                      IntelligenceCapabilities.TaskPlanning,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "StoragePath", Description = "Path to persist meta-knowledge", Required = false, DefaultValue = "./evolution-data/meta" },
            new ConfigurationRequirement { Key = "MinExamplesForFewShot", Description = "Minimum examples for few-shot learning", Required = false, DefaultValue = "5" }
        },
        CostTier = 4,
        LatencyTier = 3,
        SupportsOfflineMode = true,
        RequiresNetworkAccess = false,
        Tags = new[] { "meta-learning", "few-shot", "transfer-learning", "maml" }
    };

    /// <summary>
    /// Learns from few examples using meta-learning.
    /// </summary>
    public async Task<string> LearnFromFewExamplesAsync(
        IEnumerable<(object Input, object Output)> examples,
        string taskType,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var exampleList = examples.ToList();
            var minExamples = int.Parse(GetConfig("MinExamplesForFewShot") ?? "5");

            if (exampleList.Count < minExamples)
                throw new InvalidOperationException($"Need at least {minExamples} examples for few-shot learning");

            // Use meta-knowledge to quickly adapt
            var modelId = $"fewshot_{taskType}_{Guid.NewGuid()}";

            // In production: Apply MAML (Model-Agnostic Meta-Learning)
            // Use learned initialization that quickly adapts to new tasks

            // Update meta-knowledge
            var meta = _metaKnowledge.GetOrAdd(taskType, _ => new MetaKnowledge
            {
                TaskType = taskType,
                AdaptationCount = 0
            });

            var updated = meta with
            {
                AdaptationCount = meta.AdaptationCount + 1,
                LastAdaptation = DateTime.UtcNow,
                AverageExamplesNeeded = (meta.AverageExamplesNeeded * meta.AdaptationCount + exampleList.Count) / (meta.AdaptationCount + 1)
            };

            _metaKnowledge[taskType] = updated;

            // Improve learning efficiency
            _learningEfficiency = Math.Min(0.95, _learningEfficiency + 0.01);

            await Task.CompletedTask;
            return modelId;
        });
    }

    /// <summary>
    /// Transfers knowledge from source task to target task.
    /// </summary>
    public async Task TransferKnowledgeAsync(
        string sourceTask,
        string targetTask,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // In production: Apply transfer learning
            // Identify common patterns between tasks
            // Transfer learned representations
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Optimizes the learning strategy itself.
    /// </summary>
    public async Task OptimizeLearningStrategyAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Analyze which learning strategies work best
            // Optimize hyperparameters for learning
            // Improve meta-learning initialization
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Gets the learning efficiency score.
    /// </summary>
    public Task<double> GetLearningEfficiencyScoreAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_learningEfficiency);
    }

    private sealed record MetaKnowledge
    {
        public string TaskType { get; init; } = "";
        public int AdaptationCount { get; init; }
        public double AverageExamplesNeeded { get; init; }
        public DateTime LastAdaptation { get; init; }
    }
}

#endregion
