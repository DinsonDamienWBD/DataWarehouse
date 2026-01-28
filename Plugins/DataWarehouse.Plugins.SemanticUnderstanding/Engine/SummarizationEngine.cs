using DataWarehouse.Plugins.SemanticUnderstanding.Analyzers;
using DataWarehouse.SDK.AI;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for content summarization using extractive and abstractive methods.
    /// </summary>
    public sealed class SummarizationEngine
    {
        private readonly IAIProvider? _aiProvider;

        public SummarizationEngine(IAIProvider? aiProvider = null)
        {
            _aiProvider = aiProvider;
        }

        /// <summary>
        /// Generates a summary of the text content.
        /// </summary>
        public async Task<SummarizationResult> SummarizeAsync(
            string text,
            SummarizationOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new SummarizationOptions();

            // Try abstractive (AI) first if preferred and available
            if (options.PreferAbstractive && _aiProvider != null && _aiProvider.IsAvailable)
            {
                var aiResult = await SummarizeWithAIAsync(text, options, ct);
                if (aiResult != null)
                {
                    sw.Stop();
                    return aiResult with { Duration = sw.Elapsed };
                }
            }

            // Fall back to extractive summarization
            var extractiveResult = SummarizeExtractive(text, options);
            sw.Stop();

            return extractiveResult with { Duration = sw.Elapsed };
        }

        /// <summary>
        /// Generates an abstractive summary using AI.
        /// </summary>
        private async Task<SummarizationResult?> SummarizeWithAIAsync(
            string text,
            SummarizationOptions options,
            CancellationToken ct)
        {
            if (_aiProvider == null || !_aiProvider.IsAvailable)
                return null;

            // Truncate text to fit context window
            var truncatedText = TruncateText(text, options.MaxInputLength);

            var prompt = options.FocusAspects.Count > 0
                ? $@"Summarize the following text in approximately {options.TargetLength} words.
Focus on these aspects: {string.Join(", ", options.FocusAspects)}

Text:
{truncatedText}

Provide a clear, coherent summary that captures the main points."
                : $@"Summarize the following text in approximately {options.TargetLength} words.

Text:
{truncatedText}

Provide a clear, coherent summary that captures the main points.";

            var request = new AIRequest
            {
                Prompt = prompt,
                SystemMessage = "You are a professional summarization system. Create concise, accurate summaries that preserve key information.",
                MaxTokens = options.TargetLength * 2 // Allow some flexibility
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (!response.Success)
                return null;

            var summary = response.Content.Trim();

            // Extract main topics from summary
            var topics = ExtractTopics(summary, 5);

            var compressionRatio = text.Length > 0 ? (float)summary.Length / text.Length : 0;

            return new SummarizationResult
            {
                Summary = summary,
                Type = SummarizationType.Abstractive,
                CompressionRatio = compressionRatio,
                MainTopics = topics,
                UsedAI = true
            };
        }

        /// <summary>
        /// Generates an extractive summary by selecting key sentences.
        /// </summary>
        private SummarizationResult SummarizeExtractive(
            string text,
            SummarizationOptions options)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new SummarizationResult
                {
                    Summary = "",
                    Type = SummarizationType.Extractive,
                    CompressionRatio = 1
                };
            }

            // Split into sentences
            var sentences = SplitSentences(text);

            if (sentences.Count == 0)
            {
                return new SummarizationResult
                {
                    Summary = text.Length > options.TargetLength * 5
                        ? text[..(options.TargetLength * 5)]
                        : text,
                    Type = SummarizationType.Extractive,
                    CompressionRatio = 1
                };
            }

            // Score sentences
            var scoredSentences = ScoreSentences(sentences, text, options);

            // Select top sentences based on target length
            var selectedSentences = SelectSentences(scoredSentences, options.TargetLength * 5);

            // Sort by original position for coherence
            selectedSentences = selectedSentences.OrderBy(s => s.Position).ToList();

            var summary = string.Join(" ", selectedSentences.Select(s => s.Text));
            var compressionRatio = text.Length > 0 ? (float)summary.Length / text.Length : 0;

            return new SummarizationResult
            {
                Summary = summary,
                Type = SummarizationType.Extractive,
                CompressionRatio = compressionRatio,
                KeySentences = selectedSentences.Select(s => s.Text).ToList(),
                MainTopics = ExtractTopics(summary, 5),
                UsedAI = false
            };
        }

        /// <summary>
        /// Summarizes multiple documents together.
        /// </summary>
        public async Task<SummarizationResult> SummarizeMultipleAsync(
            IEnumerable<string> documents,
            SummarizationOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new SummarizationOptions();

            var docList = documents.ToList();

            if (docList.Count == 0)
            {
                return new SummarizationResult
                {
                    Summary = "",
                    Type = SummarizationType.Extractive,
                    Duration = sw.Elapsed
                };
            }

            if (docList.Count == 1)
            {
                return await SummarizeAsync(docList[0], options, ct);
            }

            // Use AI for multi-document summarization if available
            if (_aiProvider != null && _aiProvider.IsAvailable)
            {
                var combinedLength = docList.Sum(d => d.Length);
                var truncatedDocs = new List<string>();
                var maxPerDoc = options.MaxInputLength / docList.Count;

                foreach (var doc in docList)
                {
                    truncatedDocs.Add(TruncateText(doc, maxPerDoc));
                }

                var combinedText = string.Join("\n\n---\n\n", truncatedDocs.Select((d, i) => $"Document {i + 1}:\n{d}"));

                var request = new AIRequest
                {
                    Prompt = $@"Synthesize a summary from the following {docList.Count} documents in approximately {options.TargetLength} words.
Identify common themes and key differences.

{combinedText}

Provide a unified summary that covers the main points from all documents.",
                    SystemMessage = "You are a multi-document summarization system. Create comprehensive summaries that synthesize information from multiple sources.",
                    MaxTokens = options.TargetLength * 2
                };

                var response = await _aiProvider.CompleteAsync(request, ct);

                if (response.Success)
                {
                    sw.Stop();
                    return new SummarizationResult
                    {
                        Summary = response.Content.Trim(),
                        Type = SummarizationType.Abstractive,
                        CompressionRatio = combinedLength > 0 ? (float)response.Content.Length / combinedLength : 0,
                        MainTopics = ExtractTopics(response.Content, 5),
                        UsedAI = true,
                        Duration = sw.Elapsed
                    };
                }
            }

            // Fallback: Summarize each document and combine
            var summaries = new List<string>();
            foreach (var doc in docList)
            {
                ct.ThrowIfCancellationRequested();
                var docSummary = await SummarizeAsync(doc,
                    options with { TargetLength = options.TargetLength / docList.Count }, ct);
                summaries.Add(docSummary.Summary);
            }

            var combinedSummary = string.Join(" ", summaries);
            sw.Stop();

            return new SummarizationResult
            {
                Summary = combinedSummary,
                Type = SummarizationType.Extractive,
                CompressionRatio = docList.Sum(d => d.Length) > 0
                    ? (float)combinedSummary.Length / docList.Sum(d => d.Length)
                    : 0,
                MainTopics = ExtractTopics(combinedSummary, 5),
                UsedAI = false,
                Duration = sw.Elapsed
            };
        }

        private List<string> SplitSentences(string text)
        {
            // Split on sentence boundaries
            var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
                .Select(s => s.Trim())
                .Where(s => s.Length >= 10 && s.Length < 500)
                .ToList();

            return sentences;
        }

        private List<ScoredSentence> ScoreSentences(
            List<string> sentences,
            string fullText,
            SummarizationOptions options)
        {
            var wordFreq = ComputeWordFrequencies(fullText);
            var scored = new List<ScoredSentence>();

            for (int i = 0; i < sentences.Count; i++)
            {
                var sentence = sentences[i];
                var score = 0.0f;

                // 1. Word frequency score
                var words = ExtractWords(sentence);
                if (words.Count > 0)
                {
                    var freqScore = words
                        .Where(w => wordFreq.ContainsKey(w))
                        .Sum(w => wordFreq[w]) / words.Count;
                    score += freqScore * 0.3f;
                }

                // 2. Position score (first and last sentences are important)
                var positionScore = i < sentences.Count / 5
                    ? 1.0f - (float)i / (sentences.Count / 5)
                    : i > sentences.Count * 4 / 5
                        ? (float)(i - sentences.Count * 4 / 5) / (sentences.Count / 5)
                        : 0.5f;
                score += positionScore * 0.2f;

                // 3. Length score (prefer medium-length sentences)
                var lengthScore = sentence.Length >= 50 && sentence.Length <= 200 ? 1.0f : 0.5f;
                score += lengthScore * 0.1f;

                // 4. Entity/proper noun presence
                var hasProperNoun = Regex.IsMatch(sentence, @"\b[A-Z][a-z]+\b");
                if (hasProperNoun)
                    score += 0.1f;

                // 5. Focus aspects matching
                if (options.FocusAspects.Count > 0)
                {
                    var aspectMatches = options.FocusAspects
                        .Count(a => sentence.Contains(a, StringComparison.OrdinalIgnoreCase));
                    score += (float)aspectMatches / options.FocusAspects.Count * 0.3f;
                }

                scored.Add(new ScoredSentence
                {
                    Text = sentence,
                    Position = i,
                    Score = score
                });
            }

            return scored.OrderByDescending(s => s.Score).ToList();
        }

        private List<ScoredSentence> SelectSentences(
            List<ScoredSentence> scoredSentences,
            int targetCharacterCount)
        {
            var selected = new List<ScoredSentence>();
            var currentLength = 0;

            foreach (var sentence in scoredSentences)
            {
                if (currentLength + sentence.Text.Length <= targetCharacterCount)
                {
                    selected.Add(sentence);
                    currentLength += sentence.Text.Length + 1; // +1 for space
                }

                if (currentLength >= targetCharacterCount * 0.8)
                    break;
            }

            // Ensure we have at least one sentence
            if (selected.Count == 0 && scoredSentences.Count > 0)
            {
                selected.Add(scoredSentences[0]);
            }

            return selected;
        }

        private Dictionary<string, float> ComputeWordFrequencies(string text)
        {
            var words = ExtractWords(text);
            var wordCounts = new Dictionary<string, int>();

            foreach (var word in words)
            {
                wordCounts.TryGetValue(word, out var count);
                wordCounts[word] = count + 1;
            }

            var maxCount = wordCounts.Values.Max();

            return wordCounts.ToDictionary(
                kv => kv.Key,
                kv => (float)kv.Value / maxCount
            );
        }

        private List<string> ExtractWords(string text)
        {
            return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z]+\b")
                .Select(m => m.Value)
                .Where(w => w.Length > 2)
                .ToList();
        }

        private List<string> ExtractTopics(string text, int count)
        {
            var words = ExtractWords(text);
            var wordCounts = new Dictionary<string, int>();

            // Common stop words
            var stopWords = new HashSet<string>
            {
                "the", "and", "for", "that", "this", "with", "from", "have", "are", "was",
                "were", "been", "being", "will", "would", "could", "should", "may", "might",
                "must", "shall", "can", "need", "has", "had", "does", "did", "into", "about"
            };

            foreach (var word in words)
            {
                if (word.Length > 3 && !stopWords.Contains(word))
                {
                    wordCounts.TryGetValue(word, out var c);
                    wordCounts[word] = c + 1;
                }
            }

            return wordCounts
                .OrderByDescending(kv => kv.Value)
                .Take(count)
                .Select(kv => kv.Key)
                .ToList();
        }

        private static string TruncateText(string text, int maxLength)
        {
            return text.Length <= maxLength ? text : text[..maxLength];
        }

        private sealed class ScoredSentence
        {
            public required string Text { get; init; }
            public int Position { get; init; }
            public float Score { get; init; }
        }
    }

    /// <summary>
    /// Options for summarization.
    /// </summary>
    public sealed record SummarizationOptions
    {
        /// <summary>Target summary length in words.</summary>
        public int TargetLength { get; init; } = 150;

        /// <summary>Maximum input text length.</summary>
        public int MaxInputLength { get; init; } = 10000;

        /// <summary>Prefer abstractive (AI) over extractive.</summary>
        public bool PreferAbstractive { get; init; } = true;

        /// <summary>Aspects to focus on in the summary.</summary>
        public List<string> FocusAspects { get; init; } = new();

        /// <summary>Include key sentences in result.</summary>
        public bool IncludeKeySentences { get; init; } = true;
    }
}
