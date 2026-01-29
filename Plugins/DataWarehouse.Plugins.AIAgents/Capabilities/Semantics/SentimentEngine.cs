// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Text.RegularExpressions;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Engine for sentiment analysis including emotion detection, aspect-based sentiment,
/// and sentence-level analysis. Supports both lexicon-based statistical methods
/// and AI-enhanced analysis.
/// </summary>
public sealed class SentimentEngine
{
    private IExtendedAIProvider? _aiProvider;
    private readonly SentimentLexicon _lexicon;

    /// <summary>
    /// Initializes a new instance of the <see cref="SentimentEngine"/> class.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced sentiment analysis.</param>
    public SentimentEngine(IExtendedAIProvider? aiProvider = null)
    {
        _aiProvider = aiProvider;
        _lexicon = new SentimentLexicon();
    }

    /// <summary>
    /// Sets or updates the AI provider.
    /// </summary>
    /// <param name="provider">The AI provider to use.</param>
    public void SetProvider(IExtendedAIProvider? provider)
    {
        _aiProvider = provider;
    }

    /// <summary>
    /// Gets whether an AI provider is available.
    /// </summary>
    public bool IsAIAvailable => _aiProvider?.IsAvailable ?? false;

    /// <summary>
    /// Analyzes sentiment of the given text.
    /// </summary>
    /// <param name="text">Text to analyze.</param>
    /// <param name="config">Analysis configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sentiment analysis result.</returns>
    public async Task<SentimentAnalysisResult> AnalyzeAsync(
        string text,
        SentimentAnalysisConfig? config = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new SentimentAnalysisConfig();

        if (string.IsNullOrWhiteSpace(text) || text.Length < config.MinTextLength)
        {
            return new SentimentAnalysisResult
            {
                OverallSentiment = SentimentClass.Neutral,
                OverallScore = 0,
                Confidence = 0,
                Duration = sw.Elapsed
            };
        }

        // Try AI analysis first if preferred
        if (config.PreferAI && IsAIAvailable)
        {
            try
            {
                var aiResult = await AnalyzeWithAIAsync(text, config, ct);
                if (aiResult != null)
                {
                    sw.Stop();
                    return aiResult with { Duration = sw.Elapsed };
                }
            }
            catch { /* Fall back to statistical */ }
        }

        // Statistical/lexicon-based analysis
        var result = AnalyzeStatistical(text, config);
        sw.Stop();

        return result with { Duration = sw.Elapsed, TextLength = text.Length };
    }

    /// <summary>
    /// Analyzes sentiment trends over time from a collection of documents.
    /// </summary>
    /// <param name="documents">Documents with timestamps.</param>
    /// <param name="periodSize">Time period for aggregation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of sentiment trends by period.</returns>
    public async Task<List<SentimentTrend>> AnalyzeTrendsAsync(
        IEnumerable<(DateTimeOffset Time, string Text)> documents,
        TimeSpan periodSize,
        CancellationToken ct = default)
    {
        var docList = documents.OrderBy(d => d.Time).ToList();

        if (docList.Count == 0)
            return new List<SentimentTrend>();

        var startTime = docList.First().Time;
        var endTime = docList.Last().Time;
        var trends = new List<SentimentTrend>();

        var currentPeriodStart = startTime;
        float? previousScore = null;

        while (currentPeriodStart < endTime)
        {
            ct.ThrowIfCancellationRequested();

            var periodEnd = currentPeriodStart + periodSize;
            var periodDocs = docList
                .Where(d => d.Time >= currentPeriodStart && d.Time < periodEnd)
                .ToList();

            if (periodDocs.Count > 0)
            {
                var periodResults = new List<SentimentAnalysisResult>();
                foreach (var doc in periodDocs)
                {
                    var result = await AnalyzeAsync(doc.Text, ct: ct);
                    periodResults.Add(result);
                }

                var avgScore = periodResults.Average(r => r.OverallScore);
                var distribution = periodResults
                    .GroupBy(r => r.OverallSentiment)
                    .ToDictionary(g => g.Key, g => g.Count());

                var dominantEmotion = periodResults
                    .SelectMany(r => r.Emotions)
                    .GroupBy(e => e.Emotion)
                    .OrderByDescending(g => g.Sum(e => e.Intensity))
                    .FirstOrDefault()?.Key;

                var trendDirection = TrendDirection.Stable;
                if (previousScore.HasValue)
                {
                    var diff = avgScore - previousScore.Value;
                    trendDirection = diff > 0.1f ? TrendDirection.Improving :
                                    diff < -0.1f ? TrendDirection.Declining :
                                    TrendDirection.Stable;
                }

                trends.Add(new SentimentTrend
                {
                    StartTime = currentPeriodStart,
                    EndTime = periodEnd,
                    AverageScore = avgScore,
                    Distribution = distribution,
                    DominantEmotion = dominantEmotion,
                    DocumentCount = periodDocs.Count,
                    Trend = trendDirection
                });

                previousScore = avgScore;
            }

            currentPeriodStart = periodEnd;
        }

        return trends;
    }

    /// <summary>
    /// Performs comparative sentiment analysis between two texts.
    /// </summary>
    /// <param name="text1">First text.</param>
    /// <param name="text2">Second text.</param>
    /// <param name="config">Analysis configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comparison result.</returns>
    public async Task<SentimentComparisonResult> CompareAsync(
        string text1,
        string text2,
        SentimentAnalysisConfig? config = null,
        CancellationToken ct = default)
    {
        var result1Task = AnalyzeAsync(text1, config, ct);
        var result2Task = AnalyzeAsync(text2, config, ct);

        await Task.WhenAll(result1Task, result2Task);

        var result1 = await result1Task;
        var result2 = await result2Task;

        return new SentimentComparisonResult
        {
            Result1 = result1,
            Result2 = result2,
            ScoreDifference = result1.OverallScore - result2.OverallScore,
            SentimentShift = DetermineSentimentShift(result1.OverallSentiment, result2.OverallSentiment),
            EmotionDifferences = CompareEmotions(result1.Emotions, result2.Emotions)
        };
    }

    /// <summary>
    /// Analyzes sentiment using AI.
    /// </summary>
    private async Task<SentimentAnalysisResult?> AnalyzeWithAIAsync(
        string text,
        SentimentAnalysisConfig config,
        CancellationToken ct)
    {
        if (_aiProvider == null || !_aiProvider.IsAvailable)
            return null;

        var truncatedText = text.Length > 3000 ? text[..3000] : text;

        var aspectPrompt = config.FocusAspects.Count > 0
            ? $"\nAlso analyze sentiment specifically for these aspects: {string.Join(", ", config.FocusAspects)}"
            : "";

        var request = new AIRequest
        {
            Prompt = $@"Analyze the sentiment of this text.{aspectPrompt}

Text:
{truncatedText}

Provide analysis in this format:
OVERALL: [VeryPositive/Positive/Neutral/Negative/VeryNegative/Mixed]
SCORE: [-1.0 to 1.0]
CONFIDENCE: [0.0 to 1.0]
EMOTIONS: [joy:0.X, sadness:0.X, anger:0.X, fear:0.X] (only include detected emotions)
ASPECTS: [aspect1:positive, aspect2:negative] (if any aspects analyzed)",
            SystemMessage = "You are a sentiment analysis system. Analyze text sentiment accurately and identify emotions.",
            MaxTokens = 300
        };

        var response = await _aiProvider.CompleteAsync(request, ct);

        if (!response.Success)
            return null;

        return ParseAIResponse(response.Content, text, config);
    }

    /// <summary>
    /// Analyzes sentiment using lexicon-based approach.
    /// </summary>
    private SentimentAnalysisResult AnalyzeStatistical(string text, SentimentAnalysisConfig config)
    {
        var sentences = SplitSentences(text);
        var sentenceSentiments = new List<SentenceSentiment>();
        var totalScore = 0.0f;
        var wordCount = 0;

        // Emotion counters
        var emotionScores = new Dictionary<EmotionType, float>
        {
            [EmotionType.Joy] = 0,
            [EmotionType.Sadness] = 0,
            [EmotionType.Anger] = 0,
            [EmotionType.Fear] = 0,
            [EmotionType.Surprise] = 0,
            [EmotionType.Disgust] = 0,
            [EmotionType.Trust] = 0,
            [EmotionType.Anticipation] = 0
        };

        var position = 0;
        foreach (var sentence in sentences.Take(config.MaxSentences))
        {
            var sentenceScore = AnalyzeSentence(sentence, emotionScores, out var words);
            wordCount += words;
            totalScore += sentenceScore * words;

            var sentenceClass = ClassifySentiment(sentenceScore);
            sentenceSentiments.Add(new SentenceSentiment
            {
                Text = sentence,
                StartPosition = position,
                Sentiment = sentenceClass,
                Score = sentenceScore,
                Confidence = Math.Min(0.8f, words / 10.0f) // More words = more confident
            });

            position = text.IndexOf(sentence, position, StringComparison.Ordinal);
            if (position >= 0)
                position += sentence.Length;
            else
                position = 0;
        }

        var overallScore = wordCount > 0 ? totalScore / wordCount : 0;
        var overallClass = ClassifySentiment(overallScore);

        // Normalize emotion scores
        var maxEmotion = emotionScores.Values.Max();
        if (maxEmotion > 0)
        {
            foreach (var key in emotionScores.Keys.ToList())
            {
                emotionScores[key] /= maxEmotion;
            }
        }

        // Build emotion list
        var emotions = emotionScores
            .Where(e => e.Value > 0.2f)
            .Select(e => new DetectedEmotion
            {
                Emotion = e.Key,
                Intensity = e.Value,
                Confidence = Math.Min(0.8f, e.Value)
            })
            .OrderByDescending(e => e.Intensity)
            .ToList();

        // Aspect-based analysis
        var aspectSentiments = new List<AspectSentiment>();
        if (config.EnableAspectBased && config.FocusAspects.Count > 0)
        {
            aspectSentiments = AnalyzeAspects(text, config.FocusAspects);
        }

        return new SentimentAnalysisResult
        {
            OverallSentiment = overallClass,
            OverallScore = overallScore,
            Confidence = Math.Min(0.8f, wordCount / 50.0f),
            Emotions = config.EnableEmotionDetection ? emotions : new List<DetectedEmotion>(),
            AspectSentiments = aspectSentiments,
            SentenceSentiments = config.EnableSentenceLevel ? sentenceSentiments : new List<SentenceSentiment>(),
            UsedAI = false
        };
    }

    private float AnalyzeSentence(
        string sentence,
        Dictionary<EmotionType, float> emotionScores,
        out int wordCount)
    {
        var words = ExtractWords(sentence);
        wordCount = words.Count;

        if (wordCount == 0)
            return 0;

        var totalScore = 0.0f;
        var negationActive = false;
        var intensifier = 1.0f;

        foreach (var word in words)
        {
            // Check for negation
            if (_lexicon.NegationWords.Contains(word))
            {
                negationActive = true;
                continue;
            }

            // Check for intensifiers
            if (_lexicon.IntensifierWords.TryGetValue(word, out var intValue))
            {
                intensifier = intValue;
                continue;
            }

            // Get sentiment score
            if (_lexicon.SentimentWords.TryGetValue(word, out var score))
            {
                var adjustedScore = negationActive ? -score : score;
                adjustedScore *= intensifier;
                totalScore += adjustedScore;
                negationActive = false;
                intensifier = 1.0f;
            }

            // Track emotions
            if (_lexicon.EmotionWords.TryGetValue(word, out var emotions))
            {
                foreach (var emotion in emotions)
                {
                    var emotionScore = negationActive ? 0 : 1.0f;
                    emotionScores[emotion] += emotionScore;
                }
            }
        }

        return totalScore / wordCount;
    }

    private List<AspectSentiment> AnalyzeAspects(string text, List<string> aspects)
    {
        var results = new List<AspectSentiment>();

        foreach (var aspect in aspects)
        {
            var mentions = new List<string>();
            var opinionTerms = new List<string>();
            var totalScore = 0.0f;
            var count = 0;

            // Find sentences containing the aspect
            var sentences = SplitSentences(text);
            foreach (var sentence in sentences)
            {
                if (sentence.Contains(aspect, StringComparison.OrdinalIgnoreCase))
                {
                    mentions.Add(sentence);

                    // Analyze sentiment of words near the aspect
                    var words = ExtractWords(sentence);
                    foreach (var word in words)
                    {
                        if (_lexicon.SentimentWords.TryGetValue(word, out var score))
                        {
                            totalScore += score;
                            opinionTerms.Add(word);
                            count++;
                        }
                    }
                }
            }

            if (count > 0)
            {
                var avgScore = totalScore / count;
                results.Add(new AspectSentiment
                {
                    Aspect = aspect,
                    Sentiment = ClassifySentiment(avgScore),
                    Score = avgScore,
                    Confidence = Math.Min(0.8f, count / 5.0f),
                    Mentions = mentions.Take(3).ToList(),
                    OpinionTerms = opinionTerms.Distinct().Take(5).ToList()
                });
            }
        }

        return results;
    }

    private SentimentClass ClassifySentiment(float score)
    {
        return score switch
        {
            >= 0.5f => SentimentClass.VeryPositive,
            >= 0.2f => SentimentClass.Positive,
            >= -0.2f => SentimentClass.Neutral,
            >= -0.5f => SentimentClass.Negative,
            _ => SentimentClass.VeryNegative
        };
    }

    private List<string> SplitSentences(string text)
    {
        return Regex.Split(text, @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length >= 5)
            .ToList();
    }

    private List<string> ExtractWords(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z]+\b")
            .Select(m => m.Value)
            .Where(w => w.Length > 2)
            .ToList();
    }

    private SentimentAnalysisResult ParseAIResponse(
        string response,
        string originalText,
        SentimentAnalysisConfig config)
    {
        var overallMatch = Regex.Match(response, @"OVERALL:\s*(\w+)");
        var scoreMatch = Regex.Match(response, @"SCORE:\s*([-0-9.]+)");
        var confidenceMatch = Regex.Match(response, @"CONFIDENCE:\s*([0-9.]+)");
        var emotionsMatch = Regex.Match(response, @"EMOTIONS:\s*(.+?)(?:ASPECTS:|$)", RegexOptions.Singleline);
        var aspectsMatch = Regex.Match(response, @"ASPECTS:\s*(.+?)$", RegexOptions.Singleline);

        var overallClass = SentimentClass.Neutral;
        if (overallMatch.Success)
        {
            overallClass = overallMatch.Groups[1].Value.ToLowerInvariant() switch
            {
                "verypositive" => SentimentClass.VeryPositive,
                "positive" => SentimentClass.Positive,
                "neutral" => SentimentClass.Neutral,
                "negative" => SentimentClass.Negative,
                "verynegative" => SentimentClass.VeryNegative,
                "mixed" => SentimentClass.Mixed,
                _ => SentimentClass.Neutral
            };
        }

        var score = scoreMatch.Success && float.TryParse(scoreMatch.Groups[1].Value, out var s)
            ? Math.Clamp(s, -1, 1) : 0;

        var confidence = confidenceMatch.Success && float.TryParse(confidenceMatch.Groups[1].Value, out var c)
            ? Math.Clamp(c, 0, 1) : 0.8f;

        var emotions = new List<DetectedEmotion>();
        if (emotionsMatch.Success)
        {
            var emotionPairs = Regex.Matches(emotionsMatch.Groups[1].Value, @"(\w+):([0-9.]+)");
            foreach (Match em in emotionPairs)
            {
                if (Enum.TryParse<EmotionType>(em.Groups[1].Value, true, out var emotionType) &&
                    float.TryParse(em.Groups[2].Value, out var intensity))
                {
                    emotions.Add(new DetectedEmotion
                    {
                        Emotion = emotionType,
                        Intensity = Math.Clamp(intensity, 0, 1),
                        Confidence = 0.8f
                    });
                }
            }
        }

        var aspectSentiments = new List<AspectSentiment>();
        if (aspectsMatch.Success)
        {
            var aspectPairs = Regex.Matches(aspectsMatch.Groups[1].Value, @"(\w+):(\w+)");
            foreach (Match ap in aspectPairs)
            {
                var aspectSentiment = ap.Groups[2].Value.ToLowerInvariant() switch
                {
                    "positive" or "verypositive" => SentimentClass.Positive,
                    "negative" or "verynegative" => SentimentClass.Negative,
                    _ => SentimentClass.Neutral
                };

                aspectSentiments.Add(new AspectSentiment
                {
                    Aspect = ap.Groups[1].Value,
                    Sentiment = aspectSentiment,
                    Score = aspectSentiment == SentimentClass.Positive ? 0.5f :
                            aspectSentiment == SentimentClass.Negative ? -0.5f : 0,
                    Confidence = 0.8f
                });
            }
        }

        return new SentimentAnalysisResult
        {
            OverallSentiment = overallClass,
            OverallScore = score,
            Confidence = confidence,
            Emotions = emotions,
            AspectSentiments = aspectSentiments,
            UsedAI = true
        };
    }

    private SentimentShift DetermineSentimentShift(SentimentClass from, SentimentClass to)
    {
        var fromValue = (int)from;
        var toValue = (int)to;
        var diff = toValue - fromValue;

        return diff switch
        {
            > 1 => SentimentShift.SignificantImprovement,
            1 => SentimentShift.SlightImprovement,
            0 => SentimentShift.NoChange,
            -1 => SentimentShift.SlightDecline,
            _ => SentimentShift.SignificantDecline
        };
    }

    private Dictionary<EmotionType, float> CompareEmotions(
        List<DetectedEmotion> emotions1,
        List<DetectedEmotion> emotions2)
    {
        var differences = new Dictionary<EmotionType, float>();

        var dict1 = emotions1.ToDictionary(e => e.Emotion, e => e.Intensity);
        var dict2 = emotions2.ToDictionary(e => e.Emotion, e => e.Intensity);

        var allEmotions = dict1.Keys.Union(dict2.Keys);

        foreach (var emotion in allEmotions)
        {
            var val1 = dict1.GetValueOrDefault(emotion, 0);
            var val2 = dict2.GetValueOrDefault(emotion, 0);
            differences[emotion] = val1 - val2;
        }

        return differences;
    }
}

#region Supporting Types

/// <summary>
/// Sentiment lexicon for lexicon-based analysis.
/// </summary>
internal sealed class SentimentLexicon
{
    public Dictionary<string, float> SentimentWords { get; } = new()
    {
        // Positive words
        ["good"] = 0.6f, ["great"] = 0.8f, ["excellent"] = 0.9f, ["amazing"] = 0.9f,
        ["wonderful"] = 0.85f, ["fantastic"] = 0.85f, ["awesome"] = 0.8f, ["best"] = 0.9f,
        ["love"] = 0.8f, ["happy"] = 0.7f, ["pleased"] = 0.6f, ["satisfied"] = 0.6f,
        ["perfect"] = 0.95f, ["beautiful"] = 0.7f, ["brilliant"] = 0.8f, ["superb"] = 0.85f,
        ["outstanding"] = 0.85f, ["impressive"] = 0.7f, ["positive"] = 0.6f, ["success"] = 0.7f,
        ["successful"] = 0.7f, ["helpful"] = 0.6f, ["useful"] = 0.5f, ["valuable"] = 0.6f,
        ["recommend"] = 0.6f, ["enjoy"] = 0.6f, ["delight"] = 0.7f, ["pleasant"] = 0.6f,
        ["favorable"] = 0.6f, ["exceptional"] = 0.85f, ["remarkable"] = 0.75f, ["terrific"] = 0.8f,
        ["thrilled"] = 0.8f, ["wonderful"] = 0.8f, ["marvelous"] = 0.8f, ["splendid"] = 0.75f,

        // Negative words
        ["bad"] = -0.6f, ["terrible"] = -0.85f, ["awful"] = -0.85f, ["horrible"] = -0.9f,
        ["poor"] = -0.5f, ["worst"] = -0.95f, ["hate"] = -0.8f, ["disappointed"] = -0.6f,
        ["disappointing"] = -0.6f, ["frustrating"] = -0.7f, ["annoying"] = -0.6f, ["angry"] = -0.7f,
        ["upset"] = -0.6f, ["sad"] = -0.5f, ["unhappy"] = -0.6f, ["fail"] = -0.7f,
        ["failed"] = -0.7f, ["failure"] = -0.7f, ["problem"] = -0.4f, ["issue"] = -0.3f,
        ["broken"] = -0.6f, ["useless"] = -0.7f, ["waste"] = -0.6f, ["regret"] = -0.6f,
        ["ugly"] = -0.6f, ["boring"] = -0.5f, ["slow"] = -0.3f, ["difficult"] = -0.3f,
        ["dreadful"] = -0.8f, ["dismal"] = -0.7f, ["pathetic"] = -0.75f, ["miserable"] = -0.75f,
        ["inferior"] = -0.6f, ["defective"] = -0.7f, ["flawed"] = -0.5f, ["inadequate"] = -0.6f
    };

    public Dictionary<string, List<EmotionType>> EmotionWords { get; } = new()
    {
        // Joy
        ["happy"] = new() { EmotionType.Joy }, ["joy"] = new() { EmotionType.Joy },
        ["delighted"] = new() { EmotionType.Joy }, ["cheerful"] = new() { EmotionType.Joy },
        ["excited"] = new() { EmotionType.Joy, EmotionType.Anticipation },
        ["pleased"] = new() { EmotionType.Joy }, ["thrilled"] = new() { EmotionType.Joy },
        ["ecstatic"] = new() { EmotionType.Joy }, ["elated"] = new() { EmotionType.Joy },

        // Sadness
        ["sad"] = new() { EmotionType.Sadness }, ["depressed"] = new() { EmotionType.Sadness },
        ["unhappy"] = new() { EmotionType.Sadness }, ["miserable"] = new() { EmotionType.Sadness },
        ["gloomy"] = new() { EmotionType.Sadness }, ["melancholy"] = new() { EmotionType.Sadness },
        ["sorrowful"] = new() { EmotionType.Sadness }, ["heartbroken"] = new() { EmotionType.Sadness },

        // Anger
        ["angry"] = new() { EmotionType.Anger }, ["furious"] = new() { EmotionType.Anger },
        ["outraged"] = new() { EmotionType.Anger }, ["annoyed"] = new() { EmotionType.Anger },
        ["irritated"] = new() { EmotionType.Anger }, ["enraged"] = new() { EmotionType.Anger },
        ["frustrated"] = new() { EmotionType.Anger }, ["hostile"] = new() { EmotionType.Anger },

        // Fear
        ["scared"] = new() { EmotionType.Fear }, ["afraid"] = new() { EmotionType.Fear },
        ["worried"] = new() { EmotionType.Fear }, ["anxious"] = new() { EmotionType.Fear },
        ["terrified"] = new() { EmotionType.Fear }, ["nervous"] = new() { EmotionType.Fear },
        ["panicked"] = new() { EmotionType.Fear }, ["frightened"] = new() { EmotionType.Fear },

        // Surprise
        ["surprised"] = new() { EmotionType.Surprise }, ["amazed"] = new() { EmotionType.Surprise },
        ["shocked"] = new() { EmotionType.Surprise }, ["astonished"] = new() { EmotionType.Surprise },
        ["stunned"] = new() { EmotionType.Surprise }, ["startled"] = new() { EmotionType.Surprise },

        // Disgust
        ["disgusted"] = new() { EmotionType.Disgust }, ["repulsed"] = new() { EmotionType.Disgust },
        ["revolted"] = new() { EmotionType.Disgust }, ["appalled"] = new() { EmotionType.Disgust },
        ["nauseated"] = new() { EmotionType.Disgust },

        // Trust
        ["trust"] = new() { EmotionType.Trust }, ["confident"] = new() { EmotionType.Trust },
        ["reliable"] = new() { EmotionType.Trust }, ["trustworthy"] = new() { EmotionType.Trust },
        ["dependable"] = new() { EmotionType.Trust }, ["faithful"] = new() { EmotionType.Trust },

        // Anticipation
        ["expecting"] = new() { EmotionType.Anticipation }, ["hopeful"] = new() { EmotionType.Anticipation },
        ["eager"] = new() { EmotionType.Anticipation }, ["awaiting"] = new() { EmotionType.Anticipation },
        ["looking forward"] = new() { EmotionType.Anticipation }
    };

    public HashSet<string> NegationWords { get; } = new()
    {
        "not", "no", "never", "neither", "nobody", "nothing", "nowhere",
        "hardly", "barely", "scarcely", "without", "dont", "doesn", "didnt",
        "wont", "wouldnt", "cant", "cannot", "isnt", "arent", "wasnt", "werent",
        "shouldnt", "couldnt", "havent", "hasnt", "hadnt", "nor", "aint"
    };

    public Dictionary<string, float> IntensifierWords { get; } = new()
    {
        ["very"] = 1.5f, ["extremely"] = 2.0f, ["really"] = 1.3f, ["absolutely"] = 1.8f,
        ["totally"] = 1.5f, ["completely"] = 1.6f, ["highly"] = 1.4f, ["incredibly"] = 1.7f,
        ["somewhat"] = 0.7f, ["slightly"] = 0.5f, ["barely"] = 0.3f, ["hardly"] = 0.3f,
        ["quite"] = 1.2f, ["fairly"] = 0.8f, ["rather"] = 0.9f, ["pretty"] = 1.1f,
        ["exceptionally"] = 1.9f, ["remarkably"] = 1.6f, ["tremendously"] = 1.8f
    };
}

/// <summary>
/// Result of sentiment analysis.
/// </summary>
public sealed record SentimentAnalysisResult
{
    /// <summary>Overall sentiment classification.</summary>
    public SentimentClass OverallSentiment { get; init; }

    /// <summary>Overall sentiment score (-1 to 1).</summary>
    public float OverallScore { get; init; }

    /// <summary>Confidence in overall sentiment (0-1).</summary>
    public float Confidence { get; init; }

    /// <summary>Detected emotions.</summary>
    public List<DetectedEmotion> Emotions { get; init; } = new();

    /// <summary>Aspect-based sentiments.</summary>
    public List<AspectSentiment> AspectSentiments { get; init; } = new();

    /// <summary>Sentence-level sentiments.</summary>
    public List<SentenceSentiment> SentenceSentiments { get; init; } = new();

    /// <summary>Whether AI was used for analysis.</summary>
    public bool UsedAI { get; init; }

    /// <summary>Processing duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Text length analyzed.</summary>
    public int TextLength { get; init; }
}

/// <summary>
/// Sentiment classification.
/// </summary>
public enum SentimentClass
{
    /// <summary>Strongly positive sentiment.</summary>
    VeryPositive = 2,
    /// <summary>Positive sentiment.</summary>
    Positive = 1,
    /// <summary>Neutral sentiment.</summary>
    Neutral = 0,
    /// <summary>Negative sentiment.</summary>
    Negative = -1,
    /// <summary>Strongly negative sentiment.</summary>
    VeryNegative = -2,
    /// <summary>Mixed positive and negative.</summary>
    Mixed = 3
}

/// <summary>
/// A detected emotion in text.
/// </summary>
public sealed class DetectedEmotion
{
    /// <summary>Emotion type.</summary>
    public EmotionType Emotion { get; init; }

    /// <summary>Intensity score (0-1).</summary>
    public float Intensity { get; init; }

    /// <summary>Confidence in detection (0-1).</summary>
    public float Confidence { get; init; }

    /// <summary>Text spans showing this emotion.</summary>
    public List<string> EvidenceSpans { get; init; } = new();
}

/// <summary>
/// Types of emotions (Plutchik's wheel).
/// </summary>
public enum EmotionType
{
    /// <summary>Joy, happiness.</summary>
    Joy,
    /// <summary>Sadness, grief.</summary>
    Sadness,
    /// <summary>Anger, frustration.</summary>
    Anger,
    /// <summary>Fear, anxiety.</summary>
    Fear,
    /// <summary>Surprise, amazement.</summary>
    Surprise,
    /// <summary>Disgust, contempt.</summary>
    Disgust,
    /// <summary>Trust, confidence.</summary>
    Trust,
    /// <summary>Anticipation, expectation.</summary>
    Anticipation
}

/// <summary>
/// Sentiment for a specific aspect/topic.
/// </summary>
public sealed class AspectSentiment
{
    /// <summary>The aspect/topic being evaluated.</summary>
    public required string Aspect { get; init; }

    /// <summary>Sentiment class for this aspect.</summary>
    public SentimentClass Sentiment { get; init; }

    /// <summary>Sentiment score (-1 to 1).</summary>
    public float Score { get; init; }

    /// <summary>Confidence (0-1).</summary>
    public float Confidence { get; init; }

    /// <summary>Text spans mentioning this aspect.</summary>
    public List<string> Mentions { get; init; } = new();

    /// <summary>Opinion words/phrases associated with this aspect.</summary>
    public List<string> OpinionTerms { get; init; } = new();
}

/// <summary>
/// Sentence-level sentiment.
/// </summary>
public sealed class SentenceSentiment
{
    /// <summary>The sentence text.</summary>
    public required string Text { get; init; }

    /// <summary>Start position in original text.</summary>
    public int StartPosition { get; init; }

    /// <summary>Sentiment class.</summary>
    public SentimentClass Sentiment { get; init; }

    /// <summary>Sentiment score (-1 to 1).</summary>
    public float Score { get; init; }

    /// <summary>Confidence (0-1).</summary>
    public float Confidence { get; init; }
}

/// <summary>
/// Sentiment trend over time.
/// </summary>
public sealed class SentimentTrend
{
    /// <summary>Period start time.</summary>
    public DateTimeOffset StartTime { get; init; }

    /// <summary>Period end time.</summary>
    public DateTimeOffset EndTime { get; init; }

    /// <summary>Average sentiment score for this period.</summary>
    public float AverageScore { get; init; }

    /// <summary>Sentiment distribution.</summary>
    public Dictionary<SentimentClass, int> Distribution { get; init; } = new();

    /// <summary>Dominant emotion in this period.</summary>
    public EmotionType? DominantEmotion { get; init; }

    /// <summary>Number of documents analyzed.</summary>
    public int DocumentCount { get; init; }

    /// <summary>Trend direction compared to previous period.</summary>
    public TrendDirection Trend { get; init; }

    /// <summary>Significant topics in this period.</summary>
    public List<string> SignificantTopics { get; init; } = new();
}

/// <summary>
/// Trend direction.
/// </summary>
public enum TrendDirection
{
    /// <summary>Improving sentiment.</summary>
    Improving,
    /// <summary>Stable sentiment.</summary>
    Stable,
    /// <summary>Declining sentiment.</summary>
    Declining
}

/// <summary>
/// Result of comparing two texts' sentiments.
/// </summary>
public sealed class SentimentComparisonResult
{
    /// <summary>First text analysis result.</summary>
    public required SentimentAnalysisResult Result1 { get; init; }

    /// <summary>Second text analysis result.</summary>
    public required SentimentAnalysisResult Result2 { get; init; }

    /// <summary>Score difference (Result1 - Result2).</summary>
    public float ScoreDifference { get; init; }

    /// <summary>Sentiment shift from text1 to text2.</summary>
    public SentimentShift SentimentShift { get; init; }

    /// <summary>Emotion intensity differences.</summary>
    public Dictionary<EmotionType, float> EmotionDifferences { get; init; } = new();
}

/// <summary>
/// Sentiment shift classification.
/// </summary>
public enum SentimentShift
{
    /// <summary>Large positive shift.</summary>
    SignificantImprovement,
    /// <summary>Small positive shift.</summary>
    SlightImprovement,
    /// <summary>No change.</summary>
    NoChange,
    /// <summary>Small negative shift.</summary>
    SlightDecline,
    /// <summary>Large negative shift.</summary>
    SignificantDecline
}

/// <summary>
/// Configuration for sentiment analysis.
/// </summary>
public sealed record SentimentAnalysisConfig
{
    /// <summary>Enable sentence-level analysis.</summary>
    public bool EnableSentenceLevel { get; init; } = true;

    /// <summary>Enable aspect-based analysis.</summary>
    public bool EnableAspectBased { get; init; } = true;

    /// <summary>Enable emotion detection.</summary>
    public bool EnableEmotionDetection { get; init; } = true;

    /// <summary>Aspects to focus on (empty = auto-detect).</summary>
    public List<string> FocusAspects { get; init; } = new();

    /// <summary>Prefer AI over statistical methods.</summary>
    public bool PreferAI { get; init; } = true;

    /// <summary>Minimum text length for reliable analysis.</summary>
    public int MinTextLength { get; init; } = 10;

    /// <summary>Maximum sentences to analyze per document.</summary>
    public int MaxSentences { get; init; } = 1000;
}

#endregion
