using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for sentiment analysis including emotion detection and aspect-based sentiment.
    /// </summary>
    public sealed class SentimentEngine
    {
        private readonly IAIProvider? _aiProvider;
        private readonly SentimentLexicon _lexicon;

        public SentimentEngine(IAIProvider? aiProvider = null)
        {
            _aiProvider = aiProvider;
            _lexicon = new SentimentLexicon();
        }

        /// <summary>
        /// Analyzes sentiment of the text.
        /// </summary>
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
            if (config.PreferAI && _aiProvider != null && _aiProvider.IsAvailable)
            {
                var aiResult = await AnalyzeWithAIAsync(text, config, ct);
                if (aiResult != null)
                {
                    sw.Stop();
                    return aiResult with { Duration = sw.Elapsed };
                }
            }

            // Statistical/lexicon-based analysis
            var result = AnalyzeStatistical(text, config);
            sw.Stop();

            return result with { Duration = sw.Elapsed, TextLength = text.Length };
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

                position = text.IndexOf(sentence, position) + sentence.Length;
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
                Emotions = emotions,
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

            foreach (var word in words)
            {
                // Check for negation
                if (_lexicon.NegationWords.Contains(word))
                {
                    negationActive = true;
                    continue;
                }

                // Check for intensifiers
                var intensifier = 1.0f;
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
            var lowerText = text.ToLowerInvariant();

            foreach (var aspect in aspects)
            {
                var lowerAspect = aspect.ToLowerInvariant();
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

        /// <summary>
        /// Analyzes sentiment trends over time.
        /// </summary>
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
    }

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

            // Negative words
            ["bad"] = -0.6f, ["terrible"] = -0.85f, ["awful"] = -0.85f, ["horrible"] = -0.9f,
            ["poor"] = -0.5f, ["worst"] = -0.95f, ["hate"] = -0.8f, ["disappointed"] = -0.6f,
            ["disappointing"] = -0.6f, ["frustrating"] = -0.7f, ["annoying"] = -0.6f, ["angry"] = -0.7f,
            ["upset"] = -0.6f, ["sad"] = -0.5f, ["unhappy"] = -0.6f, ["fail"] = -0.7f,
            ["failed"] = -0.7f, ["failure"] = -0.7f, ["problem"] = -0.4f, ["issue"] = -0.3f,
            ["broken"] = -0.6f, ["useless"] = -0.7f, ["waste"] = -0.6f, ["regret"] = -0.6f,
            ["ugly"] = -0.6f, ["boring"] = -0.5f, ["slow"] = -0.3f, ["difficult"] = -0.3f
        };

        public Dictionary<string, List<EmotionType>> EmotionWords { get; } = new()
        {
            // Joy
            ["happy"] = new() { EmotionType.Joy }, ["joy"] = new() { EmotionType.Joy },
            ["delighted"] = new() { EmotionType.Joy }, ["cheerful"] = new() { EmotionType.Joy },
            ["excited"] = new() { EmotionType.Joy, EmotionType.Anticipation },

            // Sadness
            ["sad"] = new() { EmotionType.Sadness }, ["depressed"] = new() { EmotionType.Sadness },
            ["unhappy"] = new() { EmotionType.Sadness }, ["miserable"] = new() { EmotionType.Sadness },

            // Anger
            ["angry"] = new() { EmotionType.Anger }, ["furious"] = new() { EmotionType.Anger },
            ["outraged"] = new() { EmotionType.Anger }, ["annoyed"] = new() { EmotionType.Anger },

            // Fear
            ["scared"] = new() { EmotionType.Fear }, ["afraid"] = new() { EmotionType.Fear },
            ["worried"] = new() { EmotionType.Fear }, ["anxious"] = new() { EmotionType.Fear },

            // Surprise
            ["surprised"] = new() { EmotionType.Surprise }, ["amazed"] = new() { EmotionType.Surprise },
            ["shocked"] = new() { EmotionType.Surprise }, ["astonished"] = new() { EmotionType.Surprise },

            // Disgust
            ["disgusted"] = new() { EmotionType.Disgust }, ["repulsed"] = new() { EmotionType.Disgust },

            // Trust
            ["trust"] = new() { EmotionType.Trust }, ["confident"] = new() { EmotionType.Trust },
            ["reliable"] = new() { EmotionType.Trust },

            // Anticipation
            ["expecting"] = new() { EmotionType.Anticipation }, ["hopeful"] = new() { EmotionType.Anticipation }
        };

        public HashSet<string> NegationWords { get; } = new()
        {
            "not", "no", "never", "neither", "nobody", "nothing", "nowhere",
            "hardly", "barely", "scarcely", "without", "dont", "doesn", "didnt",
            "wont", "wouldnt", "cant", "cannot", "isnt", "arent", "wasnt", "werent"
        };

        public Dictionary<string, float> IntensifierWords { get; } = new()
        {
            ["very"] = 1.5f, ["extremely"] = 2.0f, ["really"] = 1.3f, ["absolutely"] = 1.8f,
            ["totally"] = 1.5f, ["completely"] = 1.6f, ["highly"] = 1.4f, ["incredibly"] = 1.7f,
            ["somewhat"] = 0.7f, ["slightly"] = 0.5f, ["barely"] = 0.3f, ["hardly"] = 0.3f
        };
    }
}
