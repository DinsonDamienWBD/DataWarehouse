using DataWarehouse.SDK.AI;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Psychometric indexing feature strategy (T88).
/// Analyzes content sentiment, emotional tone, psychological patterns, and deception detection for advanced indexing.
/// </summary>
/// <remarks>
/// Supports optional deception detection via EnableDeception configuration.
/// Deception analysis provides risk indicators only - not definitive lie detection.
/// All deception signals require human review for interpretation.
/// </remarks>
public sealed class PsychometricIndexingStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-psychometric-indexing";

    /// <inheritdoc/>
    public override string StrategyName => "Psychometric Indexing";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Psychometric Indexing",
        Description = "Advanced content analysis for sentiment, emotional tone, and psychological patterns",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "AnalysisDepth", Description = "Analysis depth (basic|standard|deep)", Required = false, DefaultValue = "standard" },
            new ConfigurationRequirement { Key = "EmotionModel", Description = "Emotion model (ekman|plutchik|panas)", Required = false, DefaultValue = "plutchik" },
            new ConfigurationRequirement { Key = "EnablePersonality", Description = "Enable Big Five personality inference", Required = false, DefaultValue = "false" },
            new ConfigurationRequirement { Key = "EnableDeception", Description = "Enable deception detection analysis", Required = false, DefaultValue = "false" }
        },
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        Tags = new[] { "psychometric", "sentiment", "emotion", "personality", "nlp", "t88" }
    };

    /// <summary>
    /// Analyzes content for psychometric properties.
    /// </summary>
    public async Task<PsychometricAnalysis> AnalyzeAsync(
        string content,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for psychometric analysis");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var depth = GetConfig("AnalysisDepth") ?? "standard";
            var emotionModel = GetConfig("EmotionModel") ?? "plutchik";
            var enablePersonality = bool.Parse(GetConfig("EnablePersonality") ?? "false");
            var enableDeception = bool.Parse(GetConfig("EnableDeception") ?? "false");

            var prompt = BuildAnalysisPrompt(content, depth, emotionModel, enablePersonality, enableDeception);

            var response = await AIProvider.CompleteAsync(new AIRequest
            {
                Prompt = prompt,
                MaxTokens = 800,
                Temperature = 0.3f
            }, ct);

            RecordTokens(response.Usage?.TotalTokens ?? 0);

            var analysis = ParsePsychometricAnalysis(response.Content, enableDeception);
            analysis.Content = content.Length > 200 ? content.Substring(0, 200) + "..." : content;
            analysis.AnalyzedAt = DateTime.UtcNow;

            // Store in vector store for searchability
            if (VectorStore != null)
            {
                var embedding = await AIProvider.GetEmbeddingsAsync(content, ct);
                RecordEmbeddings(1);

                await VectorStore.StoreAsync(
                    $"psychometric-{Guid.NewGuid():N}",
                    embedding,
                    new Dictionary<string, object>
                    {
                        ["content_hash"] = content.GetHashCode(),
                        ["sentiment"] = analysis.Sentiment.Label,
                        ["sentiment_score"] = analysis.Sentiment.Score,
                        ["dominant_emotion"] = analysis.Emotions.OrderByDescending(e => e.Intensity).FirstOrDefault()?.Name ?? "neutral",
                        ["deception_probability"] = analysis.DeceptionIndicators?.OverallDeceptionProbability ?? 0.0f,
                        ["deception_enabled"] = enableDeception,
                        ["analyzed_at"] = analysis.AnalyzedAt
                    },
                    ct
                );
                RecordVectorsStored(1);
            }

            return analysis;
        });
    }

    /// <summary>
    /// Analyzes multiple content items in batch.
    /// </summary>
    public async Task<IEnumerable<PsychometricAnalysis>> AnalyzeBatchAsync(
        IEnumerable<string> contents,
        CancellationToken ct = default)
    {
        var results = new List<PsychometricAnalysis>();
        foreach (var content in contents)
        {
            results.Add(await AnalyzeAsync(content, ct));
        }
        return results;
    }

    /// <summary>
    /// Searches for content by psychometric properties.
    /// </summary>
    public async Task<IEnumerable<PsychometricSearchResult>> SearchByEmotionAsync(
        string targetEmotion,
        float minIntensity = 0.5f,
        int topK = 10,
        CancellationToken ct = default)
    {
        if (VectorStore == null)
            throw new InvalidOperationException("Vector store required for psychometric search");
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for psychometric search");

        return await ExecuteWithTrackingAsync(async () =>
        {
            // Create query embedding based on emotion
            var emotionQuery = $"Content expressing {targetEmotion} emotion with high intensity";
            var queryEmbedding = await AIProvider.GetEmbeddingsAsync(emotionQuery, ct);
            RecordEmbeddings(1);

            var matches = await VectorStore.SearchAsync(
                queryEmbedding,
                topK * 2, // Fetch more for filtering
                0.5f,
                new Dictionary<string, object> { ["dominant_emotion"] = targetEmotion },
                ct
            );
            RecordSearch();

            return matches
                .Take(topK)
                .Select(m => new PsychometricSearchResult
                {
                    DocumentId = m.Entry.Id,
                    Score = m.Score,
                    Emotion = targetEmotion,
                    Intensity = m.Entry.Metadata.TryGetValue("sentiment_score", out var s) ? Convert.ToSingle(s) : 0.5f
                });
        });
    }

    /// <summary>
    /// Generates a psychometric profile from multiple content samples.
    /// </summary>
    public async Task<PsychometricProfile> GenerateProfileAsync(
        IEnumerable<string> contentSamples,
        string profileId,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var analyses = new List<PsychometricAnalysis>();
            foreach (var content in contentSamples)
            {
                analyses.Add(await AnalyzeAsync(content, ct));
            }

            // Aggregate results
            var avgSentiment = analyses.Average(a => a.Sentiment.Score);
            var emotionAggregates = analyses
                .SelectMany(a => a.Emotions)
                .GroupBy(e => e.Name)
                .Select(g => new EmotionScore
                {
                    Name = g.Key,
                    Intensity = g.Average(e => e.Intensity)
                })
                .OrderByDescending(e => e.Intensity)
                .ToList();

            // Use AI for personality inference if enabled
            BigFiveScores? personality = null;
            if (bool.Parse(GetConfig("EnablePersonality") ?? "false"))
            {
                var prompt = $@"Based on these aggregated content analyses, infer Big Five personality traits:

Sentiment: {avgSentiment:F2}
Dominant emotions: {string.Join(", ", emotionAggregates.Take(3).Select(e => $"{e.Name} ({e.Intensity:F2})"))}
Sample count: {analyses.Count}

Return JSON:
{{
  ""openness"": 0.0-1.0,
  ""conscientiousness"": 0.0-1.0,
  ""extraversion"": 0.0-1.0,
  ""agreeableness"": 0.0-1.0,
  ""neuroticism"": 0.0-1.0
}}";

                var response = await AIProvider.CompleteAsync(new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 200,
                    Temperature = 0.3f
                }, ct);

                RecordTokens(response.Usage?.TotalTokens ?? 0);
                personality = ParseBigFive(response.Content);
            }

            return new PsychometricProfile
            {
                ProfileId = profileId,
                SampleCount = analyses.Count,
                AverageSentiment = avgSentiment,
                EmotionalProfile = emotionAggregates,
                PersonalityTraits = personality,
                GeneratedAt = DateTime.UtcNow
            };
        });
    }

    private static string BuildAnalysisPrompt(string content, string depth, string emotionModel, bool includePersonality, bool includeDeception)
    {
        var emotionList = emotionModel switch
        {
            "ekman" => "anger, disgust, fear, happiness, sadness, surprise",
            "plutchik" => "joy, trust, fear, surprise, sadness, disgust, anger, anticipation",
            "panas" => "positive affect, negative affect",
            _ => "joy, trust, fear, surprise, sadness, disgust, anger, anticipation"
        };

        var prompt = $@"Perform psychometric analysis on this content:

Content:
{content}

Analyze for:
1. Sentiment (positive/negative/neutral with score -1 to 1)
2. Emotions ({emotionList}) with intensity 0-1
3. Writing style indicators";

        if (depth == "deep" || includePersonality)
        {
            prompt += @"
4. Communication style (formal/informal, assertive/passive, etc.)
5. Cognitive patterns (analytical, emotional, practical)";
        }

        if (includeDeception)
        {
            var sectionNumber = (depth == "deep" || includePersonality) ? 6 : 4;
            prompt += $@"
{sectionNumber}. Deception Detection Analysis:
   Analyze for deception signals:
   - Linguistic distance: unusual word choice or phrasing (0-1)
   - Temporal inconsistency: conflicting time references (0-1)
   - Emotional incongruence: sentiment mismatch with topic (0-1)
   - Overspecification: excessive unnecessary detail (0-1)
   - Hedging language: qualifiers (maybe, possibly, sort of) (0-1)
   Provide scores 0-1 for each signal, overall deception probability (0-1), confidence (0-1), and primary indicators.";
        }

        prompt += @"

Return JSON:
{
  ""sentiment"": {""label"": ""positive/negative/neutral"", ""score"": -1.0 to 1.0},
  ""emotions"": [{""name"": ""emotion"", ""intensity"": 0.0-1.0}],
  ""writing_style"": {""formality"": 0.0-1.0, ""complexity"": 0.0-1.0},
  ""cognitive_patterns"": [""pattern1"", ""pattern2""]";

        if (includeDeception)
        {
            prompt += @",
  ""deception"": {
    ""linguistic_distance"": 0.0-1.0,
    ""temporal_inconsistency"": 0.0-1.0,
    ""emotional_incongruence"": 0.0-1.0,
    ""overspecification"": 0.0-1.0,
    ""hedging_language"": 0.0-1.0,
    ""overall_probability"": 0.0-1.0,
    ""confidence"": 0.0-1.0,
    ""primary_indicators"": ""description""
  }";
        }

        prompt += @"
}";

        return prompt;
    }

    private static PsychometricAnalysis ParsePsychometricAnalysis(string response, bool includeDeception)
    {
        var analysis = new PsychometricAnalysis
        {
            Sentiment = new SentimentScore { Label = "neutral", Score = 0 },
            Emotions = new List<EmotionScore>(),
            WritingStyle = new WritingStyleMetrics(),
            CognitivePatterns = new List<string>(),
            DeceptionIndicators = null
        };

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("sentiment", out var sent))
                {
                    analysis.Sentiment = new SentimentScore
                    {
                        Label = sent.TryGetProperty("label", out var l) ? l.GetString() ?? "neutral" : "neutral",
                        Score = sent.TryGetProperty("score", out var s) ? s.GetSingle() : 0
                    };
                }

                if (doc.RootElement.TryGetProperty("emotions", out var emotions))
                {
                    foreach (var emo in emotions.EnumerateArray())
                    {
                        analysis.Emotions.Add(new EmotionScore
                        {
                            Name = emo.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Intensity = emo.TryGetProperty("intensity", out var i) ? i.GetSingle() : 0
                        });
                    }
                }

                if (doc.RootElement.TryGetProperty("writing_style", out var style))
                {
                    analysis.WritingStyle = new WritingStyleMetrics
                    {
                        Formality = style.TryGetProperty("formality", out var f) ? f.GetSingle() : 0.5f,
                        Complexity = style.TryGetProperty("complexity", out var c) ? c.GetSingle() : 0.5f
                    };
                }

                if (doc.RootElement.TryGetProperty("cognitive_patterns", out var patterns))
                {
                    analysis.CognitivePatterns = patterns.EnumerateArray()
                        .Select(p => p.GetString() ?? "")
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                }

                if (includeDeception && doc.RootElement.TryGetProperty("deception", out var deception))
                {
                    analysis.DeceptionIndicators = new DeceptionSignals
                    {
                        LinguisticDistanceScore = deception.TryGetProperty("linguistic_distance", out var ld) ? ld.GetSingle() : 0.0f,
                        TemporalInconsistencyScore = deception.TryGetProperty("temporal_inconsistency", out var ti) ? ti.GetSingle() : 0.0f,
                        EmotionalIncongruenceScore = deception.TryGetProperty("emotional_incongruence", out var ei) ? ei.GetSingle() : 0.0f,
                        OverspecificationScore = deception.TryGetProperty("overspecification", out var os) ? os.GetSingle() : 0.0f,
                        HedgingLanguageScore = deception.TryGetProperty("hedging_language", out var hl) ? hl.GetSingle() : 0.0f,
                        OverallDeceptionProbability = deception.TryGetProperty("overall_probability", out var op) ? op.GetSingle() : 0.0f,
                        AssessmentConfidence = deception.TryGetProperty("confidence", out var conf) ? conf.GetSingle() : 0.0f,
                        PrimaryIndicators = deception.TryGetProperty("primary_indicators", out var pi) ? pi.GetString() : null
                    };
                }
            }
        }
        catch { /* Parsing failure — return analysis with defaults */ }

        return analysis;
    }

    private static BigFiveScores? ParseBigFive(string response)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                return new BigFiveScores
                {
                    Openness = doc.RootElement.TryGetProperty("openness", out var o) ? o.GetSingle() : 0.5f,
                    Conscientiousness = doc.RootElement.TryGetProperty("conscientiousness", out var c) ? c.GetSingle() : 0.5f,
                    Extraversion = doc.RootElement.TryGetProperty("extraversion", out var e) ? e.GetSingle() : 0.5f,
                    Agreeableness = doc.RootElement.TryGetProperty("agreeableness", out var a) ? a.GetSingle() : 0.5f,
                    Neuroticism = doc.RootElement.TryGetProperty("neuroticism", out var n) ? n.GetSingle() : 0.5f
                };
            }
        }
        catch { /* Parsing failure — return null */ }

        return null;
    }
}

// Supporting types
public sealed class PsychometricAnalysis
{
    public string Content { get; set; } = "";
    public SentimentScore Sentiment { get; set; } = new();
    public List<EmotionScore> Emotions { get; set; } = new();
    public WritingStyleMetrics WritingStyle { get; set; } = new();
    public List<string> CognitivePatterns { get; set; } = new();
    /// <summary>
    /// Deception detection signals (null if not requested in analysis).
    /// </summary>
    public DeceptionSignals? DeceptionIndicators { get; set; }
    public DateTime AnalyzedAt { get; set; }
}

public sealed class SentimentScore
{
    public string Label { get; init; } = "neutral";
    public float Score { get; init; }
}

public sealed class EmotionScore
{
    public string Name { get; init; } = "";
    public float Intensity { get; init; }
}

public sealed class WritingStyleMetrics
{
    public float Formality { get; init; } = 0.5f;
    public float Complexity { get; init; } = 0.5f;
}

public sealed class PsychometricSearchResult
{
    public string DocumentId { get; init; } = "";
    public float Score { get; init; }
    public string Emotion { get; init; } = "";
    public float Intensity { get; init; }
}

public sealed class PsychometricProfile
{
    public string ProfileId { get; init; } = "";
    public int SampleCount { get; init; }
    public float AverageSentiment { get; init; }
    public List<EmotionScore> EmotionalProfile { get; init; } = new();
    public BigFiveScores? PersonalityTraits { get; init; }
    public DateTime GeneratedAt { get; init; }
}

public sealed class BigFiveScores
{
    public float Openness { get; init; }
    public float Conscientiousness { get; init; }
    public float Extraversion { get; init; }
    public float Agreeableness { get; init; }
    public float Neuroticism { get; init; }
}

/// <summary>
/// Deception detection signals extracted from content analysis.
/// </summary>
/// <remarks>
/// Based on linguistic and psychometric research into deception indicators.
/// Scores are 0-1 normalized values where higher = stronger signal.
/// NOT a definitive lie detector - use as risk indicator requiring human review.
/// </remarks>
public sealed class DeceptionSignals
{
    /// <summary>
    /// Linguistic distance score - unusual word choice, unnatural phrasing.
    /// </summary>
    /// <remarks>
    /// Measures deviation from author's typical writing style.
    /// Range: 0 (normal) to 1 (highly unusual).
    /// </remarks>
    public float LinguisticDistanceScore { get; init; }

    /// <summary>
    /// Temporal inconsistency score - conflicting time references, timeline gaps.
    /// </summary>
    /// <remarks>
    /// Detects contradictions in when events occurred.
    /// Range: 0 (consistent) to 1 (conflicting).
    /// </remarks>
    public float TemporalInconsistencyScore { get; init; }

    /// <summary>
    /// Emotional incongruence score - sentiment mismatch with topic.
    /// </summary>
    /// <remarks>
    /// E.g., positive sentiment when discussing negative event.
    /// Range: 0 (congruent) to 1 (incongruent).
    /// </remarks>
    public float EmotionalIncongruenceScore { get; init; }

    /// <summary>
    /// Overspecification score - excessive unnecessary detail.
    /// </summary>
    /// <remarks>
    /// Deceptive content often includes excessive detail to appear credible.
    /// Range: 0 (appropriate) to 1 (excessive).
    /// </remarks>
    public float OverspecificationScore { get; init; }

    /// <summary>
    /// Hedging language score - use of qualifiers ("maybe", "possibly", "sort of").
    /// </summary>
    /// <remarks>
    /// Frequent hedging may indicate uncertainty or deception.
    /// Range: 0 (direct) to 1 (heavily hedged).
    /// </remarks>
    public float HedgingLanguageScore { get; init; }

    /// <summary>
    /// Overall deception probability - composite score.
    /// </summary>
    /// <remarks>
    /// Weighted combination of all signals.
    /// Range: 0 (likely truthful) to 1 (likely deceptive).
    /// Threshold recommendation: 0.7+ warrants human review.
    /// </remarks>
    public float OverallDeceptionProbability { get; init; }

    /// <summary>
    /// Confidence in deception assessment.
    /// </summary>
    /// <remarks>
    /// Affected by content length, clarity, language complexity.
    /// Range: 0 (low confidence) to 1 (high confidence).
    /// </remarks>
    public float AssessmentConfidence { get; init; }

    /// <summary>
    /// Explanation of primary deception indicators detected.
    /// </summary>
    public string? PrimaryIndicators { get; init; }
}
