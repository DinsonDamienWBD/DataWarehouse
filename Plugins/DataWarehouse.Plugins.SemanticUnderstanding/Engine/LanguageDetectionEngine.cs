using DataWarehouse.Plugins.SemanticUnderstanding.Analyzers;
using DataWarehouse.SDK.AI;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for detecting document languages using statistical and AI methods.
    /// </summary>
    public sealed class LanguageDetectionEngine
    {
        private readonly IAIProvider? _aiProvider;
        private readonly LanguageProfiles _profiles;

        public LanguageDetectionEngine(IAIProvider? aiProvider = null)
        {
            _aiProvider = aiProvider;
            _profiles = new LanguageProfiles();
        }

        /// <summary>
        /// Detects the language(s) of the text.
        /// </summary>
        public async Task<LanguageDetectionResult> DetectAsync(
            string text,
            LanguageDetectionOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new LanguageDetectionOptions();

            if (string.IsNullOrWhiteSpace(text))
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "unknown",
                    Confidence = 0,
                    Duration = sw.Elapsed
                };
            }

            // Try AI detection first if preferred
            if (options.UseAI && _aiProvider != null && _aiProvider.IsAvailable)
            {
                var aiResult = await DetectWithAIAsync(text, options, ct);
                if (aiResult != null)
                {
                    sw.Stop();
                    return aiResult with { Duration = sw.Elapsed };
                }
            }

            // Statistical detection
            var result = DetectStatistical(text, options);
            sw.Stop();

            return result with { Duration = sw.Elapsed };
        }

        /// <summary>
        /// Detects language using AI.
        /// </summary>
        private async Task<LanguageDetectionResult?> DetectWithAIAsync(
            string text,
            LanguageDetectionOptions options,
            CancellationToken ct)
        {
            if (_aiProvider == null || !_aiProvider.IsAvailable)
                return null;

            var truncatedText = text.Length > 1000 ? text[..1000] : text;

            var request = new AIRequest
            {
                Prompt = $@"Identify the language(s) of this text. If multilingual, list all languages.

Text:
{truncatedText}

Format response as:
PRIMARY: [ISO 639-1 code, e.g., en, fr, de, es]
CONFIDENCE: [0.0-1.0]
SECONDARY: [comma-separated additional language codes if multilingual]
SCRIPT: [Latin, Cyrillic, Arabic, CJK, etc.]",
                SystemMessage = "You are a language identification system. Identify languages accurately using ISO 639-1 codes.",
                MaxTokens = 100
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (!response.Success)
                return null;

            return ParseAIResponse(response.Content);
        }

        /// <summary>
        /// Detects language using statistical methods.
        /// </summary>
        private LanguageDetectionResult DetectStatistical(string text, LanguageDetectionOptions options)
        {
            // Detect script first
            var script = DetectScript(text);

            // For non-Latin scripts, use character-based detection
            if (script == ScriptType.CJK)
            {
                return DetectCJK(text);
            }
            else if (script == ScriptType.Cyrillic)
            {
                return DetectCyrillic(text);
            }
            else if (script == ScriptType.Arabic)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "ar",
                    Confidence = 0.8f,
                    Script = ScriptType.Arabic,
                    LanguageScores = new Dictionary<string, float> { ["ar"] = 0.8f }
                };
            }
            else if (script == ScriptType.Hebrew)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "he",
                    Confidence = 0.85f,
                    Script = ScriptType.Hebrew,
                    LanguageScores = new Dictionary<string, float> { ["he"] = 0.85f }
                };
            }
            else if (script == ScriptType.Devanagari)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "hi",
                    Confidence = 0.75f,
                    Script = ScriptType.Devanagari,
                    LanguageScores = new Dictionary<string, float> { ["hi"] = 0.75f }
                };
            }
            else if (script == ScriptType.Greek)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "el",
                    Confidence = 0.9f,
                    Script = ScriptType.Greek,
                    LanguageScores = new Dictionary<string, float> { ["el"] = 0.9f }
                };
            }
            else if (script == ScriptType.Thai)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "th",
                    Confidence = 0.9f,
                    Script = ScriptType.Thai,
                    LanguageScores = new Dictionary<string, float> { ["th"] = 0.9f }
                };
            }

            // Latin script - use n-gram analysis
            return DetectLatinScript(text);
        }

        private ScriptType DetectScript(string text)
        {
            var latinCount = Regex.Matches(text, @"[a-zA-Z]").Count;
            var cyrillicCount = Regex.Matches(text, @"[\u0400-\u04FF]").Count;
            var arabicCount = Regex.Matches(text, @"[\u0600-\u06FF]").Count;
            var hebrewCount = Regex.Matches(text, @"[\u0590-\u05FF]").Count;
            var cjkCount = Regex.Matches(text, @"[\u4E00-\u9FFF\u3040-\u309F\u30A0-\u30FF\uAC00-\uD7AF]").Count;
            var devanagariCount = Regex.Matches(text, @"[\u0900-\u097F]").Count;
            var greekCount = Regex.Matches(text, @"[\u0370-\u03FF]").Count;
            var thaiCount = Regex.Matches(text, @"[\u0E00-\u0E7F]").Count;

            var counts = new Dictionary<ScriptType, int>
            {
                [ScriptType.Latin] = latinCount,
                [ScriptType.Cyrillic] = cyrillicCount,
                [ScriptType.Arabic] = arabicCount,
                [ScriptType.Hebrew] = hebrewCount,
                [ScriptType.CJK] = cjkCount,
                [ScriptType.Devanagari] = devanagariCount,
                [ScriptType.Greek] = greekCount,
                [ScriptType.Thai] = thaiCount
            };

            var totalChars = counts.Values.Sum();
            if (totalChars == 0)
                return ScriptType.Unknown;

            var dominant = counts.OrderByDescending(c => c.Value).First();

            // Check if mixed
            var significantScripts = counts.Count(c => c.Value > totalChars * 0.15);
            if (significantScripts > 1)
                return ScriptType.Mixed;

            return dominant.Key;
        }

        private LanguageDetectionResult DetectCJK(string text)
        {
            var hiragana = Regex.Matches(text, @"[\u3040-\u309F]").Count;
            var katakana = Regex.Matches(text, @"[\u30A0-\u30FF]").Count;
            var hangul = Regex.Matches(text, @"[\uAC00-\uD7AF]").Count;
            var han = Regex.Matches(text, @"[\u4E00-\u9FFF]").Count;

            if (hiragana + katakana > 0)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "ja",
                    Confidence = 0.9f,
                    Script = ScriptType.CJK,
                    LanguageScores = new Dictionary<string, float> { ["ja"] = 0.9f }
                };
            }

            if (hangul > 0)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "ko",
                    Confidence = 0.9f,
                    Script = ScriptType.CJK,
                    LanguageScores = new Dictionary<string, float> { ["ko"] = 0.9f }
                };
            }

            return new LanguageDetectionResult
            {
                PrimaryLanguage = "zh",
                Confidence = 0.8f,
                Script = ScriptType.CJK,
                LanguageScores = new Dictionary<string, float> { ["zh"] = 0.8f }
            };
        }

        private LanguageDetectionResult DetectCyrillic(string text)
        {
            // Different Cyrillic languages have characteristic letters
            var ukrainianIndicators = Regex.Matches(text, @"[\u0404\u0406\u0407\u0454\u0456\u0457\u0490\u0491]").Count;
            var bulgarianIndicators = Regex.Matches(text, @"[\u044A\u042A]").Count; // Hard sign usage
            var serbianIndicators = Regex.Matches(text, @"[\u0402\u0403\u040B\u040F\u0452\u0453\u045B\u045F]").Count;

            if (ukrainianIndicators > 0)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "uk",
                    Confidence = 0.85f,
                    Script = ScriptType.Cyrillic,
                    LanguageScores = new Dictionary<string, float>
                    {
                        ["uk"] = 0.85f,
                        ["ru"] = 0.3f
                    }
                };
            }

            if (serbianIndicators > 0)
            {
                return new LanguageDetectionResult
                {
                    PrimaryLanguage = "sr",
                    Confidence = 0.8f,
                    Script = ScriptType.Cyrillic,
                    LanguageScores = new Dictionary<string, float> { ["sr"] = 0.8f }
                };
            }

            // Default to Russian (most common Cyrillic)
            return new LanguageDetectionResult
            {
                PrimaryLanguage = "ru",
                Confidence = 0.75f,
                Script = ScriptType.Cyrillic,
                LanguageScores = new Dictionary<string, float>
                {
                    ["ru"] = 0.75f,
                    ["uk"] = 0.2f,
                    ["bg"] = 0.1f
                }
            };
        }

        private LanguageDetectionResult DetectLatinScript(string text)
        {
            var lowerText = text.ToLowerInvariant();
            var trigrams = ExtractTrigrams(lowerText);

            var scores = new Dictionary<string, float>();

            foreach (var (lang, profile) in _profiles.TrigramProfiles)
            {
                var matchCount = trigrams.Count(t => profile.Contains(t));
                var score = trigrams.Count > 0
                    ? (float)matchCount / Math.Max(profile.Count, trigrams.Count)
                    : 0;
                scores[lang] = score;
            }

            // Also check for language-specific characters
            var accentedChars = Regex.Matches(text, @"[\u00C0-\u00FF]").Count;

            // French indicators
            if (Regex.IsMatch(text, @"\b(le|la|les|un|une|des|et|est|sont|avec|pour|dans|sur|que|qui)\b", RegexOptions.IgnoreCase))
            {
                scores["fr"] = scores.GetValueOrDefault("fr", 0) + 0.2f;
            }

            // Spanish indicators
            if (Regex.IsMatch(text, @"[��]", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\b(el|la|los|las|un|una|es|son|con|para|en|que)\b", RegexOptions.IgnoreCase))
            {
                scores["es"] = scores.GetValueOrDefault("es", 0) + 0.2f;
            }

            // German indicators
            if (Regex.IsMatch(text, @"[��]", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\b(der|die|das|und|ist|sind|mit|f�r|auf|den|dem)\b", RegexOptions.IgnoreCase))
            {
                scores["de"] = scores.GetValueOrDefault("de", 0) + 0.2f;
            }

            // Portuguese indicators
            if (Regex.IsMatch(text, @"[����]", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\b(o|a|os|as|um|uma|e|�|s�o|com|para|em|que|de)\b", RegexOptions.IgnoreCase))
            {
                scores["pt"] = scores.GetValueOrDefault("pt", 0) + 0.2f;
            }

            // Italian indicators
            if (Regex.IsMatch(text, @"\b(il|lo|la|i|gli|le|un|uno|una|e|�|sono|con|per|in|che|di)\b", RegexOptions.IgnoreCase))
            {
                scores["it"] = scores.GetValueOrDefault("it", 0) + 0.2f;
            }

            // Dutch indicators
            if (Regex.IsMatch(text, @"\b(de|het|een|en|is|zijn|met|voor|op|aan|van)\b", RegexOptions.IgnoreCase))
            {
                scores["nl"] = scores.GetValueOrDefault("nl", 0) + 0.2f;
            }

            var best = scores.OrderByDescending(s => s.Value).FirstOrDefault();
            var isMultilingual = scores.Values.Count(s => s > 0.3f) > 1;

            return new LanguageDetectionResult
            {
                PrimaryLanguage = !string.IsNullOrEmpty(best.Key) ? best.Key : "en",
                Confidence = Math.Clamp(best.Value, 0, 1),
                Script = ScriptType.Latin,
                LanguageScores = scores,
                IsMultilingual = isMultilingual
            };
        }

        private List<string> ExtractTrigrams(string text)
        {
            var cleaned = Regex.Replace(text, @"[^a-z\s]", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            var trigrams = new List<string>();
            for (int i = 0; i < cleaned.Length - 2; i++)
            {
                var trigram = cleaned.Substring(i, 3);
                if (!trigram.Contains(' '))
                    trigrams.Add(trigram);
            }

            return trigrams.Distinct().ToList();
        }

        private LanguageDetectionResult ParseAIResponse(string response)
        {
            var primaryMatch = Regex.Match(response, @"PRIMARY:\s*(\w{2})");
            var confidenceMatch = Regex.Match(response, @"CONFIDENCE:\s*([0-9.]+)");
            var secondaryMatch = Regex.Match(response, @"SECONDARY:\s*(.+?)(?:SCRIPT:|$)", RegexOptions.Singleline);
            var scriptMatch = Regex.Match(response, @"SCRIPT:\s*(\w+)");

            var primary = primaryMatch.Success ? primaryMatch.Groups[1].Value.ToLowerInvariant() : "en";
            var confidence = confidenceMatch.Success && float.TryParse(confidenceMatch.Groups[1].Value, out var c)
                ? Math.Clamp(c, 0, 1)
                : 0.8f;

            var scores = new Dictionary<string, float> { [primary] = confidence };
            var isMultilingual = false;

            if (secondaryMatch.Success)
            {
                var secondary = secondaryMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToLowerInvariant())
                    .Where(s => s.Length == 2);

                foreach (var lang in secondary)
                {
                    scores[lang] = 0.5f;
                    isMultilingual = true;
                }
            }

            var script = ScriptType.Unknown;
            if (scriptMatch.Success)
            {
                script = scriptMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "latin" => ScriptType.Latin,
                    "cyrillic" => ScriptType.Cyrillic,
                    "arabic" => ScriptType.Arabic,
                    "hebrew" => ScriptType.Hebrew,
                    "cjk" => ScriptType.CJK,
                    "devanagari" => ScriptType.Devanagari,
                    "greek" => ScriptType.Greek,
                    "thai" => ScriptType.Thai,
                    _ => ScriptType.Unknown
                };
            }

            return new LanguageDetectionResult
            {
                PrimaryLanguage = primary,
                Confidence = confidence,
                LanguageScores = scores,
                IsMultilingual = isMultilingual,
                Script = script
            };
        }
    }

    /// <summary>
    /// Language trigram profiles for detection.
    /// </summary>
    internal sealed class LanguageProfiles
    {
        public Dictionary<string, HashSet<string>> TrigramProfiles { get; } = new()
        {
            ["en"] = new HashSet<string> { "the", "and", "ing", "ion", "tio", "ent", "ati", "for", "her", "ter", "hat", "tha", "ere", "ate", "his", "con", "res", "ver", "all", "ons" },
            ["es"] = new HashSet<string> { "que", "los", "las", "del", "por", "con", "una", "est", "ent", "aci", "ion", "com", "tra", "par", "pro", "nte", "ado", "cia", "men", "ien" },
            ["fr"] = new HashSet<string> { "les", "que", "des", "ent", "ion", "ons", "ait", "our", "une", "est", "ant", "dan", "tio", "eur", "men", "eme", "par", "ais", "res", "con" },
            ["de"] = new HashSet<string> { "der", "die", "und", "sch", "ein", "ich", "den", "che", "ung", "gen", "ent", "ver", "ber", "aus", "ter", "ine", "ach", "eit", "hen", "uch" },
            ["it"] = new HashSet<string> { "che", "per", "del", "ell", "lla", "ion", "ent", "one", "ato", "nte", "ere", "con", "ita", "gli", "men", "sta", "zio", "azi", "ano", "ess" },
            ["pt"] = new HashSet<string> { "que", "ent", "ado", "com", "est", "par", "aca", "cao", "oes", "dos", "uma", "nto", "sta", "nte", "pro", "tra", "era", "ida", "cia", "men" },
            ["nl"] = new HashSet<string> { "het", "van", "een", "and", "den", "ing", "aar", "eer", "aan", "ver", "ter", "oor", "ijk", "met", "wor", "ond", "gen", "erd", "ede", "sch" }
        };
    }

    /// <summary>
    /// Options for language detection.
    /// </summary>
    public sealed class LanguageDetectionOptions
    {
        /// <summary>Use AI for detection.</summary>
        public bool UseAI { get; init; } = true;

        /// <summary>Minimum text length for reliable detection.</summary>
        public int MinTextLength { get; init; } = 20;

        /// <summary>Return multiple languages if detected.</summary>
        public bool DetectMultiple { get; init; } = true;
    }
}
