using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Analyzers
{
    /// <summary>
    /// Text content analyzer for plain text, markdown, and similar formats.
    /// </summary>
    public sealed class TextAnalyzer : IContentAnalyzer
    {
        private readonly Lazy<TextTokenizer> _tokenizer;
        private readonly Lazy<LanguageDetector> _languageDetector;
        private readonly Lazy<KeywordExtractor> _keywordExtractor;
        private readonly Lazy<SentenceExtractor> _sentenceExtractor;

        public string[] SupportedContentTypes => new[]
        {
            "text/plain",
            "text/markdown",
            "text/x-markdown",
            "text/rtf"
        };

        public TextAnalyzer()
        {
            _tokenizer = new Lazy<TextTokenizer>(() => new TextTokenizer());
            _languageDetector = new Lazy<LanguageDetector>(() => new LanguageDetector());
            _keywordExtractor = new Lazy<KeywordExtractor>(() => new KeywordExtractor());
            _sentenceExtractor = new Lazy<SentenceExtractor>(() => new SentenceExtractor());
        }

        public bool CanAnalyze(string contentType)
        {
            var normalizedType = NormalizeContentType(contentType);
            return SupportedContentTypes.Any(t =>
                normalizedType.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                normalizedType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<string> ExtractTextAsync(Stream content, string contentType, CancellationToken ct = default)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct);
        }

        public async Task<ContentAnalysisResult> AnalyzeAsync(
            Stream content,
            string contentType,
            ContentAnalysisOptions options,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ContentAnalysisResult
            {
                Success = true,
                Metadata = new Dictionary<string, object>()
            };

            try
            {
                // Extract text
                string text;
                if (options.ExtractText)
                {
                    text = await ExtractTextAsync(content, contentType, ct);
                    if (text.Length > options.MaxTextLength)
                    {
                        text = text[..options.MaxTextLength];
                        result.Metadata["truncated"] = true;
                    }
                }
                else
                {
                    using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    text = await reader.ReadToEndAsync(ct);
                }

                result = result with { Text = text };
                result.Metadata["textLength"] = text.Length;

                // Language detection
                if (options.DetectLanguage)
                {
                    ct.ThrowIfCancellationRequested();
                    var langResult = _languageDetector.Value.Detect(text);
                    result = result with { LanguageResult = langResult };
                }

                // Keyword extraction
                if (options.ExtractKeywords)
                {
                    ct.ThrowIfCancellationRequested();
                    var keywords = _keywordExtractor.Value.Extract(text, options.MaxKeywords);
                    result = result with { Keywords = keywords };
                }

                // Content hash
                if (options.ComputeHash)
                {
                    var hash = ComputeContentHash(text);
                    result = result with { ContentHash = hash };
                }

                sw.Stop();
                return result with { Duration = sw.Elapsed };
            }
            catch (OperationCanceledException)
            {
                return new ContentAnalysisResult
                {
                    Success = false,
                    ErrorMessage = "Analysis cancelled",
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                return new ContentAnalysisResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        private static string NormalizeContentType(string contentType)
        {
            var idx = contentType.IndexOf(';');
            return idx >= 0 ? contentType[..idx].Trim() : contentType.Trim();
        }

        private static string ComputeContentHash(string text)
        {
            // Normalize text for consistent hashing
            var normalized = text.ToLowerInvariant()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");

            // Remove extra whitespace
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

            var bytes = Encoding.UTF8.GetBytes(normalized);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Text tokenizer for natural language processing.
    /// </summary>
    internal sealed class TextTokenizer
    {
        private static readonly Regex TokenRegex = new(@"\b[a-zA-Z][a-zA-Z0-9]*\b", RegexOptions.Compiled);

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from",
            "has", "he", "in", "is", "it", "its", "of", "on", "that", "the",
            "to", "was", "were", "will", "with", "this", "but", "they",
            "have", "had", "what", "when", "where", "who", "which", "why", "how",
            "all", "each", "every", "both", "few", "more", "most", "other", "some",
            "such", "no", "nor", "not", "only", "own", "same", "so", "than", "too",
            "very", "just", "can", "should", "now", "i", "me", "my", "you", "your",
            "we", "our", "us", "him", "his", "her", "she", "them", "their", "been",
            "being", "do", "does", "did", "doing", "would", "could", "shall", "may",
            "might", "must", "also", "into", "over", "under", "about", "between",
            "through", "after", "before", "above", "below", "up", "down", "out"
        };

        public List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var tokens = new List<string>();
            var matches = TokenRegex.Matches(text.ToLowerInvariant());

            foreach (Match match in matches)
            {
                var token = match.Value;

                // Skip very short tokens and stopwords
                if (token.Length < 2 || StopWords.Contains(token))
                    continue;

                tokens.Add(token);
            }

            return tokens;
        }

        public List<string> TokenizeWithStopWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var matches = TokenRegex.Matches(text.ToLowerInvariant());
            return matches.Select(m => m.Value).ToList();
        }
    }

    /// <summary>
    /// Statistical language detector using character n-gram profiles.
    /// </summary>
    internal sealed class LanguageDetector
    {
        // Character trigram profiles for common languages
        private static readonly Dictionary<string, HashSet<string>> LanguageProfiles = new()
        {
            ["en"] = new HashSet<string> { "the", "and", "ing", "ion", "tio", "ent", "ati", "for", "her", "ter", "hat", "tha", "ere", "ate", "his", "con", "res", "ver", "all", "ons" },
            ["es"] = new HashSet<string> { "que", "los", "las", "del", "por", "con", "una", "est", "ent", "aci", "ion", "com", "tra", "par", "pro", "nte", "ado", "cia", "men", "ien" },
            ["fr"] = new HashSet<string> { "les", "que", "des", "ent", "ion", "ons", "ait", "our", "une", "est", "ant", "dan", "tio", "eur", "men", "eme", "par", "ais", "res", "con" },
            ["de"] = new HashSet<string> { "der", "die", "und", "sch", "ein", "ich", "den", "che", "ung", "gen", "ent", "ver", "ber", "aus", "ter", "ine", "ach", "eit", "hen", "uch" },
            ["it"] = new HashSet<string> { "che", "per", "del", "ell", "lla", "ion", "ent", "one", "ato", "nte", "ere", "con", "ita", "gli", "men", "sta", "zio", "azi", "ano", "ess" },
            ["pt"] = new HashSet<string> { "que", "ent", "ado", "com", "est", "par", "aca", "cao", "oes", "dos", "uma", "nto", "sta", "nte", "pro", "tra", "era", "ida", "cia", "men" },
            ["nl"] = new HashSet<string> { "het", "van", "een", "and", "den", "ing", "aar", "eer", "aan", "ver", "ter", "oor", "ijk", "met", "wor", "ond", "gen", "erd", "ede", "sch" },
            ["ru"] = new HashSet<string> { "ogo", "eni", "ova", "nie", "ski", "ost", "tel", "ija", "est", "pro", "sto", "noj", "ova", "ych", "omu", "nym", "kom", "ani", "nym", "ego" },
            ["zh"] = new HashSet<string> { }, // Chinese uses character detection instead
            ["ja"] = new HashSet<string> { }, // Japanese uses character detection instead
            ["ko"] = new HashSet<string> { }  // Korean uses character detection instead
        };

        // Script detection patterns
        private static readonly Regex CyrillicRegex = new(@"[\u0400-\u04FF]", RegexOptions.Compiled);
        private static readonly Regex ArabicRegex = new(@"[\u0600-\u06FF]", RegexOptions.Compiled);
        private static readonly Regex HebrewRegex = new(@"[\u0590-\u05FF]", RegexOptions.Compiled);
        private static readonly Regex CJKRegex = new(@"[\u4E00-\u9FFF\u3040-\u309F\u30A0-\u30FF\uAC00-\uD7AF]", RegexOptions.Compiled);
        private static readonly Regex DevanagariRegex = new(@"[\u0900-\u097F]", RegexOptions.Compiled);
        private static readonly Regex ThaiRegex = new(@"[\u0E00-\u0E7F]", RegexOptions.Compiled);
        private static readonly Regex GreekRegex = new(@"[\u0370-\u03FF]", RegexOptions.Compiled);

        public LanguageDetectionResult Detect(string text)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(text))
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "unknown",
                    Confidence = 0,
                    Duration = sw.Elapsed
                };
            }

            // Detect script first
            var script = DetectScript(text);

            // For CJK languages, use character detection
            if (script == ScriptType.CJK)
            {
                var cjkResult = DetectCJKLanguage(text);
                sw.Stop();
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = cjkResult.Language,
                    Confidence = cjkResult.Confidence,
                    Script = script,
                    LanguageScores = new Dictionary<string, float> { [cjkResult.Language] = cjkResult.Confidence },
                    Duration = sw.Elapsed
                };
            }

            // For Cyrillic, assume Russian (most common)
            if (script == ScriptType.Cyrillic)
            {
                sw.Stop();
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "ru",
                    Confidence = 0.8f,
                    Script = script,
                    LanguageScores = new Dictionary<string, float> { ["ru"] = 0.8f },
                    Duration = sw.Elapsed
                };
            }

            // For Latin script, use n-gram analysis
            var trigrams = ExtractTrigrams(text.ToLowerInvariant());
            var scores = new Dictionary<string, float>();

            foreach (var (lang, profile) in LanguageProfiles)
            {
                if (profile.Count == 0) continue;

                var matchCount = trigrams.Count(t => profile.Contains(t));
                var score = trigrams.Count > 0 ? (float)matchCount / Math.Max(profile.Count, trigrams.Count) : 0;
                scores[lang] = score;
            }

            var bestMatch = scores.OrderByDescending(s => s.Value).FirstOrDefault();
            var isMultilingual = scores.Values.Count(s => s > 0.3f) > 1;

            sw.Stop();
            return new LanguageDetectionResult
            {
                PrimaryLanguage = bestMatch.Key ?? "en",
                Confidence = bestMatch.Value,
                Script = script,
                LanguageScores = scores,
                IsMultilingual = isMultilingual,
                Duration = sw.Elapsed
            };
        }

        private ScriptType DetectScript(string text)
        {
            var counts = new Dictionary<ScriptType, int>
            {
                [ScriptType.Latin] = 0,
                [ScriptType.Cyrillic] = 0,
                [ScriptType.Arabic] = 0,
                [ScriptType.Hebrew] = 0,
                [ScriptType.CJK] = 0,
                [ScriptType.Devanagari] = 0,
                [ScriptType.Thai] = 0,
                [ScriptType.Greek] = 0
            };

            counts[ScriptType.Cyrillic] = CyrillicRegex.Matches(text).Count;
            counts[ScriptType.Arabic] = ArabicRegex.Matches(text).Count;
            counts[ScriptType.Hebrew] = HebrewRegex.Matches(text).Count;
            counts[ScriptType.CJK] = CJKRegex.Matches(text).Count;
            counts[ScriptType.Devanagari] = DevanagariRegex.Matches(text).Count;
            counts[ScriptType.Thai] = ThaiRegex.Matches(text).Count;
            counts[ScriptType.Greek] = GreekRegex.Matches(text).Count;

            // Count Latin characters
            var latinCount = text.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
            counts[ScriptType.Latin] = latinCount;

            var totalChars = counts.Values.Sum();
            if (totalChars == 0)
                return ScriptType.Unknown;

            var maxScript = counts.OrderByDescending(c => c.Value).First();

            // Check if mixed
            var significantScripts = counts.Count(c => c.Value > totalChars * 0.2);
            if (significantScripts > 1)
                return ScriptType.Mixed;

            return maxScript.Key;
        }

        private (string Language, float Confidence) DetectCJKLanguage(string text)
        {
            // Hiragana/Katakana = Japanese
            var hiragana = Regex.Matches(text, @"[\u3040-\u309F]").Count;
            var katakana = Regex.Matches(text, @"[\u30A0-\u30FF]").Count;

            // Hangul = Korean
            var hangul = Regex.Matches(text, @"[\uAC00-\uD7AF]").Count;

            // Han characters (shared by Chinese, Japanese, Korean)
            var han = Regex.Matches(text, @"[\u4E00-\u9FFF]").Count;

            if (hiragana + katakana > 0)
                return ("ja", 0.9f);

            if (hangul > 0)
                return ("ko", 0.9f);

            // Default to Chinese for Han-only text
            return ("zh", 0.8f);
        }

        private List<string> ExtractTrigrams(string text)
        {
            var trigrams = new List<string>();

            // Clean text - keep only letters and spaces
            var cleaned = Regex.Replace(text, @"[^a-z\s]", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            for (int i = 0; i < cleaned.Length - 2; i++)
            {
                var trigram = cleaned.Substring(i, 3);
                if (!trigram.Contains(' '))
                    trigrams.Add(trigram);
            }

            return trigrams.Distinct().ToList();
        }
    }

    /// <summary>
    /// Keyword extractor using TF-IDF and statistical methods.
    /// </summary>
    internal sealed class KeywordExtractor
    {
        private readonly TextTokenizer _tokenizer = new();

        public List<WeightedKeyword> Extract(string text, int maxKeywords)
        {
            var tokens = _tokenizer.Tokenize(text);

            if (tokens.Count == 0)
                return new List<WeightedKeyword>();

            // Calculate term frequencies
            var termFreqs = new Dictionary<string, int>();
            foreach (var token in tokens)
            {
                termFreqs.TryGetValue(token, out var count);
                termFreqs[token] = count + 1;
            }

            // Calculate TF-IDF-like scores
            // Since we don't have document corpus, use log-scaled frequency
            var keywords = termFreqs
                .Select(kv => new WeightedKeyword
                {
                    Keyword = kv.Key,
                    Frequency = kv.Value,
                    // TF: log(1 + freq)
                    TfIdf = (float)Math.Log(1 + kv.Value) * (kv.Key.Length > 5 ? 1.5f : 1.0f),
                    // Weight normalized to 0-1
                    Weight = 0
                })
                .OrderByDescending(k => k.TfIdf)
                .Take(maxKeywords * 2)
                .ToList();

            // Normalize weights
            var maxTfIdf = keywords.Max(k => k.TfIdf);
            if (maxTfIdf > 0)
            {
                keywords = keywords
                    .Select(k => new WeightedKeyword
                    {
                        Keyword = k.Keyword,
                        Frequency = k.Frequency,
                        TfIdf = k.TfIdf,
                        Weight = k.TfIdf / maxTfIdf
                    })
                    .Take(maxKeywords)
                    .ToList();
            }

            return keywords;
        }
    }

    /// <summary>
    /// Extracts sentences from text.
    /// </summary>
    internal sealed class SentenceExtractor
    {
        private static readonly Regex SentenceRegex = new(
            @"(?<=[.!?])\s+(?=[A-Z])|(?<=[.!?])$",
            RegexOptions.Compiled);

        public List<string> Extract(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var sentences = SentenceRegex.Split(text)
                .Select(s => s.Trim())
                .Where(s => s.Length > 10 && s.Length < 1000)
                .ToList();

            return sentences;
        }

        public List<(string Sentence, int Position)> ExtractWithPositions(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<(string, int)>();

            var results = new List<(string, int)>();
            var matches = SentenceRegex.Split(text);
            var position = 0;

            foreach (var match in matches)
            {
                var trimmed = match.Trim();
                if (trimmed.Length > 10 && trimmed.Length < 1000)
                {
                    results.Add((trimmed, position));
                }
                position += match.Length + 1; // +1 for split character
            }

            return results;
        }
    }
}
