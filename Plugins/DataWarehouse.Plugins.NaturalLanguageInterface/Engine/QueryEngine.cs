// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using DataWarehouse.SDK.AI;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Engine;

/// <summary>
/// Query engine for natural language search and parsing.
/// Uses AI providers for intelligent query understanding and entity extraction.
/// </summary>
public sealed class QueryEngine
{
    private readonly IAIProviderRegistry? _aiRegistry;
    private readonly string _queryParsingProvider;
    private readonly QueryEngineConfig _config;

    // Intent patterns for rule-based fallback
    private static readonly List<(Regex Pattern, IntentType Intent, string? SubType)> IntentPatterns = new()
    {
        // Search intents
        (new Regex(@"(?:find|search|look\s+for|locate|get|show|list)\s+(?:all\s+)?(?:files?|documents?|data|items?)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.FindFiles, null),
        (new Regex(@"(?:find|search)\s+.*(?:with|having|containing)\s+(?:tag|label|metadata)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.FindByMetadata, null),
        (new Regex(@"(?:find|search)\s+.*(?:containing|with\s+content|that\s+(?:has|contains))", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.FindByContent, null),
        (new Regex(@"(?:find|search|show)\s+.*(?:from|since|before|after|in|during)\s+(?:\d|last|this|yesterday|today)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.FindByDate, null),
        (new Regex(@"(?:find|search|show)\s+.*(?:larger|smaller|bigger|greater|less)\s+than", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.FindBySize, null),
        (new Regex(@"(?:find|search|show)\s+(?:all\s+)?(?:\.?\w+)\s+files", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.FindByType, null),

        // Information intents
        (new Regex(@"(?:get|show|what\s+is|tell\s+me\s+about)\s+(?:info|information|details|status)\s+(?:of|about|for)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.GetInfo, null),
        (new Regex(@"(?:what\s+is|show|check|get)\s+(?:the\s+)?status", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.GetStatus, null),
        (new Regex(@"(?:show|get|what\s+are)\s+(?:the\s+)?(?:stats|statistics|metrics)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.GetStats, null),
        (new Regex(@"how\s+much\s+(?:storage|space|disk|quota)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.GetUsage, null),
        (new Regex(@"(?:show|get|what\s+is)\s+(?:the\s+)?(?:history|activity|log)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.GetHistory, null),

        // Action intents
        (new Regex(@"(?:create|make|run|start)\s+(?:a\s+)?backup", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Backup, null),
        (new Regex(@"(?:restore|recover|rollback)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Restore, null),
        (new Regex(@"(?:delete|remove|erase)\s+(?:the\s+)?(?:file|document|data|backup)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Delete, null),
        (new Regex(@"(?:archive|move\s+to\s+archive)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Archive, null),
        (new Regex(@"(?:tier|move\s+to\s+(?:cold|hot|archive|glacier))", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Tier, null),

        // Explanation intents
        (new Regex(@"(?:explain|why\s+(?:was|is|did|does))", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Explain, null),
        (new Regex(@"why\s+(?:is|was|did|does|has|have)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.WhyQuestion, null),
        (new Regex(@"how\s+(?:do|does|can|should|to)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.HowQuestion, null),
        (new Regex(@"compare\s+|what\s+(?:is|are)\s+the\s+difference", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Compare, null),

        // Storytelling intents
        (new Regex(@"(?:summarize|summary\s+of|give\s+me\s+a\s+summary)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Summarize, null),
        (new Regex(@"(?:generate|create|give\s+me)\s+(?:a\s+)?report", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Report, null),
        (new Regex(@"(?:show|what\s+(?:is|are))\s+(?:the\s+)?trend", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Trend, null),
        (new Regex(@"(?:predict|forecast|project)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Forecast, null),

        // System intents
        (new Regex(@"(?:help|what\s+can\s+you\s+do|how\s+do\s+I)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Help, null),
        (new Regex(@"(?:configure|setup|set|change)\s+(?:the\s+)?(?:settings?|config|options?)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Configure, null),

        // Conversational
        (new Regex(@"^(?:hi|hello|hey|greetings)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Greeting, null),
        (new Regex(@"^(?:bye|goodbye|see\s+you|later)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Goodbye, null),
        (new Regex(@"^(?:thanks|thank\s+you|thx)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Thanks, null),
        (new Regex(@"^(?:yes|no|okay|ok|sure|nope|yep|yeah)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Confirmation, null),
        (new Regex(@"^(?:cancel|stop|abort|nevermind)", RegexOptions.IgnoreCase | RegexOptions.Compiled), IntentType.Cancellation, null),
    };

    // Entity extraction patterns
    private static readonly List<(Regex Pattern, EntityType Type, int ValueGroup)> EntityPatterns = new()
    {
        // Date patterns
        (new Regex(@"(?:from|since|after)\s+(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.DateTime, 1),
        (new Regex(@"(?:to|until|before)\s+(\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.DateTime, 1),
        (new Regex(@"(last\s+(?:week|month|year|day|\d+\s+days?))", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.RelativeTime, 1),
        (new Regex(@"(yesterday|today|this\s+(?:week|month|year))", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.RelativeTime, 1),

        // File patterns - use [""'] for quotes in verbatim strings
        (new Regex(@"(?:file(?:s)?(?:\s+(?:named?|called))?\s+)[""']?([^""'\s,]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.FileName, 1),
        (new Regex(@"(?:in\s+(?:folder|directory|path)\s+)[""']?([^\s""']+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.FilePath, 1),
        (new Regex(@"\.(\w{2,5})\s+files?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.FileExtension, 1),
        (new Regex(@"(?:type\s+(?:of|is)\s+)(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.FileType, 1),

        // Size patterns
        (new Regex(@"(\d+(?:\.\d+)?)\s*(?:GB|MB|KB|TB|bytes?)", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.FileSize, 0),
        (new Regex(@"(?:larger|bigger|greater)\s+than\s+(\d+(?:\.\d+)?)\s*(?:GB|MB|KB|TB|bytes?)?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.SizeComparison, 0),
        (new Regex(@"(?:smaller|less)\s+than\s+(\d+(?:\.\d+)?)\s*(?:GB|MB|KB|TB|bytes?)?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.SizeComparison, 0),

        // Metadata patterns - use [""'] for quotes in verbatim strings
        (new Regex(@"(?:tag(?:ged)?(?:\s+(?:with|as))?\s+)[""']?([^""'\s,]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Tag, 1),
        (new Regex(@"(?:label(?:ed)?(?:\s+(?:as|with))?\s+)[""']?([^""'\s,]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Label, 1),
        (new Regex(@"(?:owned?\s+by|owner\s+(?:is)?)\s+[""']?(\w+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Owner, 1),
        (new Regex(@"(?:created?\s+by|creator\s+(?:is)?)\s+[""']?(\w+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Creator, 1),

        // Quantity patterns
        (new Regex(@"(?:top|first|last)\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Count, 1),
        (new Regex(@"(\d+)\s+(?:files?|items?|results?)", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Count, 1),

        // Location patterns - use [""'] for quotes in verbatim strings
        (new Regex(@"(?:in\s+(?:pool|storage)\s+)[""']?([^""'\s]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.StoragePool, 1),
        (new Regex(@"(?:in\s+bucket\s+)[""']?([^""'\s]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Bucket, 1),
        (new Regex(@"(?:in\s+(?:region|zone)\s+)[""']?([^""'\s]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled), EntityType.Region, 1),
    };

    // Language detection patterns
    private static readonly Dictionary<string, Regex[]> LanguagePatterns = new()
    {
        ["en"] = new[] { new Regex(@"\b(the|is|are|was|were|find|show|get|what|how|why)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
        ["es"] = new[] { new Regex(@"\b(el|la|los|las|buscar|mostrar|encontrar|que|como|por)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
        ["fr"] = new[] { new Regex(@"\b(le|la|les|trouver|montrer|chercher|que|comment|pourquoi)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
        ["de"] = new[] { new Regex(@"\b(der|die|das|finden|zeigen|suchen|was|wie|warum)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
        ["zh"] = new[] { new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled) },
        ["ja"] = new[] { new Regex(@"[\u3040-\u309f\u30a0-\u30ff]", RegexOptions.Compiled) },
    };

    public QueryEngine(IAIProviderRegistry? aiRegistry = null, QueryEngineConfig? config = null)
    {
        _aiRegistry = aiRegistry;
        _config = config ?? new QueryEngineConfig();
        _queryParsingProvider = _config.ProviderRouting.QueryParsingProvider;
    }

    /// <summary>
    /// Parse natural language query and extract intent and entities.
    /// </summary>
    public async Task<QueryIntent> ParseQueryAsync(string query, ConversationContext? context = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new QueryIntent
            {
                Type = IntentType.Unknown,
                Confidence = 0,
                OriginalQuery = query ?? string.Empty
            };
        }

        var normalizedQuery = NormalizeQuery(query);
        var language = DetectLanguage(query);

        // Try AI-powered parsing first if available
        if (_aiRegistry != null && _config.UseAIForParsing)
        {
            var aiProvider = _aiRegistry.GetProvider(_queryParsingProvider) ?? _aiRegistry.GetDefaultProvider();
            if (aiProvider != null && aiProvider.IsAvailable)
            {
                try
                {
                    var aiIntent = await ParseWithAIAsync(aiProvider, query, normalizedQuery, context, ct);
                    if (aiIntent != null && aiIntent.Confidence >= _config.MinAIConfidence)
                    {
                        // Return new QueryIntent with updated language
                        return new QueryIntent
                        {
                            Type = aiIntent.Type,
                            SubType = aiIntent.SubType,
                            Confidence = aiIntent.Confidence,
                            Entities = aiIntent.Entities,
                            Parameters = aiIntent.Parameters,
                            OriginalQuery = aiIntent.OriginalQuery,
                            NormalizedQuery = aiIntent.NormalizedQuery,
                            Language = language.LanguageCode,
                            Keywords = aiIntent.Keywords,
                            SuggestedCommand = aiIntent.SuggestedCommand,
                            IsFollowUp = aiIntent.IsFollowUp
                        };
                    }
                }
                catch
                {
                    // Fall through to rule-based parsing
                }
            }
        }

        // Fall back to rule-based parsing
        return ParseWithRules(query, normalizedQuery, language, context);
    }

    /// <summary>
    /// Translate parsed intent to DataWarehouse command.
    /// </summary>
    public QueryTranslation TranslateToCommand(QueryIntent intent, ConversationContext? context = null)
    {
        var parameters = new Dictionary<string, object?>();
        var warnings = new List<string>();

        // Build command based on intent type
        var (command, filterExpression) = intent.Type switch
        {
            IntentType.FindFiles or IntentType.Search => BuildSearchCommand(intent, context, parameters),
            IntentType.FindByMetadata => BuildMetadataSearchCommand(intent, context, parameters),
            IntentType.FindByContent => BuildContentSearchCommand(intent, context, parameters),
            IntentType.FindByDate => BuildDateSearchCommand(intent, context, parameters),
            IntentType.FindBySize => BuildSizeSearchCommand(intent, context, parameters),
            IntentType.FindByType => BuildTypeSearchCommand(intent, context, parameters),
            IntentType.GetInfo => ("storage.info", null as string),
            IntentType.GetStatus => ("health.status", null),
            IntentType.GetStats => ("storage.stats", null),
            IntentType.GetUsage => ("storage.stats", null),
            IntentType.GetHistory => ("audit.list", null),
            IntentType.Backup => BuildBackupCommand(intent, parameters),
            IntentType.Restore => BuildRestoreCommand(intent, parameters),
            IntentType.Delete => BuildDeleteCommand(intent, parameters, warnings),
            IntentType.Help => ("help", null),
            _ => ("search", null)
        };

        // Add common parameters
        if (intent.Entities.Any(e => e.Type == EntityType.Count))
        {
            var countEntity = intent.Entities.First(e => e.Type == EntityType.Count);
            if (int.TryParse(countEntity.Value, out var limit))
            {
                parameters["limit"] = limit;
            }
        }

        var explanation = GenerateExplanation(intent, command, parameters);

        return new QueryTranslation
        {
            Success = true,
            Command = command,
            Parameters = parameters,
            FilterExpression = filterExpression,
            Explanation = explanation,
            Confidence = intent.Confidence,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Execute a natural language search.
    /// </summary>
    public async Task<NLSearchResponse> SearchAsync(NLSearchRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Parse the query
            var context = request.ConversationId != null ? new ConversationContext() : null;
            var intent = await ParseQueryAsync(request.Query, context, ct);

            // Check if clarification is needed
            if (intent.Confidence < _config.ClarificationThreshold)
            {
                return new NLSearchResponse
                {
                    Success = false,
                    Response = "I'm not sure what you're looking for. Could you be more specific?",
                    Intent = intent,
                    Clarification = GenerateClarification(intent),
                    ExecutionTimeMs = sw.ElapsedMilliseconds
                };
            }

            // Translate to command
            var translation = TranslateToCommand(intent, context);

            // Generate response
            var response = new NLSearchResponse
            {
                Success = true,
                Response = translation.Explanation,
                Intent = intent,
                Translation = translation,
                Results = new List<NLSearchResult>(), // Would be populated by actual search execution
                SuggestedFollowUps = GenerateSuggestedFollowUps(intent),
                ExecutionTimeMs = sw.ElapsedMilliseconds
            };

            return response;
        }
        catch (Exception ex)
        {
            return new NLSearchResponse
            {
                Success = false,
                Response = "An error occurred while processing your query.",
                Error = ex.Message,
                ExecutionTimeMs = sw.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// Extract entities from text.
    /// </summary>
    public List<ExtractedEntity> ExtractEntities(string text)
    {
        var entities = new List<ExtractedEntity>();

        foreach (var (pattern, type, valueGroup) in EntityPatterns)
        {
            var matches = pattern.Matches(text);
            foreach (Match match in matches)
            {
                var value = valueGroup > 0 && match.Groups.Count > valueGroup
                    ? match.Groups[valueGroup].Value
                    : match.Value;

                entities.Add(new ExtractedEntity
                {
                    Type = type,
                    Value = value,
                    NormalizedValue = NormalizeEntityValue(value, type),
                    Confidence = 0.85,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length
                });
            }
        }

        return entities;
    }

    /// <summary>
    /// Detect language of the query.
    /// </summary>
    public LanguageDetectionResult DetectLanguage(string text)
    {
        var scores = new Dictionary<string, int>();

        foreach (var (lang, patterns) in LanguagePatterns)
        {
            var score = patterns.Sum(p => p.Matches(text).Count);
            if (score > 0) scores[lang] = score;
        }

        if (scores.Count == 0)
        {
            return new LanguageDetectionResult
            {
                LanguageCode = "en",
                LanguageName = "English",
                Confidence = 0.5
            };
        }

        var topLang = scores.OrderByDescending(kv => kv.Value).First();
        var total = scores.Values.Sum();

        return new LanguageDetectionResult
        {
            LanguageCode = topLang.Key,
            LanguageName = GetLanguageName(topLang.Key),
            Confidence = (double)topLang.Value / total,
            Alternatives = scores
                .Where(kv => kv.Key != topLang.Key)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => (kv.Key, (double)kv.Value / total))
                .ToList()
        };
    }

    #region Private Methods

    private async Task<QueryIntent?> ParseWithAIAsync(IAIProvider provider, string originalQuery, string normalizedQuery, ConversationContext? context, CancellationToken ct)
    {
        var systemPrompt = """
            You are a query parser for a data warehouse system. Parse the user's natural language query and extract:
            1. Intent (search, find_files, get_info, get_status, backup, restore, explain, summarize, etc.)
            2. Entities (file names, dates, sizes, tags, paths, etc.)
            3. Suggested DataWarehouse command

            Respond in JSON format:
            {
              "intent": "find_files",
              "subtype": "by_date",
              "confidence": 0.95,
              "entities": [{"type": "RelativeTime", "value": "last week", "normalized": "7d"}],
              "parameters": {"timeRange": "7d"},
              "keywords": ["files", "modified"],
              "suggestedCommand": "storage.list"
            }
            """;

        var contextInfo = context != null ? $"\nCurrent context: {JsonSerializer.Serialize(context)}" : "";
        var prompt = $"Parse this query: \"{originalQuery}\"{contextInfo}";

        var request = new AIRequest
        {
            SystemMessage = systemPrompt,
            Prompt = prompt,
            Temperature = 0.1f,
            MaxTokens = 500
        };

        var response = await provider.CompleteAsync(request, ct);

        if (!response.Success || string.IsNullOrEmpty(response.Content))
            return null;

        try
        {
            var parsed = JsonDocument.Parse(response.Content);
            var root = parsed.RootElement;

            var intentStr = root.GetProperty("intent").GetString() ?? "unknown";
            var intent = ParseIntentType(intentStr);
            var subType = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
            var confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.8;

            var entities = new List<ExtractedEntity>();
            if (root.TryGetProperty("entities", out var entitiesArray))
            {
                foreach (var entity in entitiesArray.EnumerateArray())
                {
                    entities.Add(new ExtractedEntity
                    {
                        Type = ParseEntityType(entity.GetProperty("type").GetString() ?? "Unknown"),
                        Value = entity.GetProperty("value").GetString() ?? "",
                        NormalizedValue = entity.TryGetProperty("normalized", out var norm) ? norm.GetString() ?? "" : "",
                        Confidence = 0.9
                    });
                }
            }

            var parameters = new Dictionary<string, object>();
            if (root.TryGetProperty("parameters", out var paramsObj))
            {
                foreach (var prop in paramsObj.EnumerateObject())
                {
                    parameters[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString()!,
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.GetRawText()
                    };
                }
            }

            var keywords = new List<string>();
            if (root.TryGetProperty("keywords", out var keywordsArray))
            {
                keywords.AddRange(keywordsArray.EnumerateArray().Select(k => k.GetString() ?? ""));
            }

            var suggestedCommand = root.TryGetProperty("suggestedCommand", out var cmd) ? cmd.GetString() : null;

            return new QueryIntent
            {
                Type = intent,
                SubType = subType,
                Confidence = confidence,
                Entities = entities,
                Parameters = parameters,
                OriginalQuery = originalQuery,
                NormalizedQuery = normalizedQuery,
                Keywords = keywords,
                SuggestedCommand = suggestedCommand
            };
        }
        catch
        {
            return null;
        }
    }

    private QueryIntent ParseWithRules(string originalQuery, string normalizedQuery, LanguageDetectionResult language, ConversationContext? context)
    {
        var (intent, subType, confidence) = MatchIntent(normalizedQuery);
        var entities = ExtractEntities(originalQuery);
        var keywords = ExtractKeywords(normalizedQuery);

        // Check for follow-up indicators
        var isFollowUp = Regex.IsMatch(normalizedQuery, @"^(and|also|or|more|show\s+me|what\s+about)", RegexOptions.IgnoreCase);

        // Apply context for follow-ups
        if (isFollowUp && context != null)
        {
            // Inherit entities from context if not specified
            if (!entities.Any(e => e.Type == EntityType.RelativeTime || e.Type == EntityType.DateTime) && context.TimeRange != null)
            {
                entities.Add(new ExtractedEntity
                {
                    Type = EntityType.RelativeTime,
                    Value = context.TimeRange.RelativeDescription ?? "inherited",
                    NormalizedValue = context.TimeRange.RelativeDescription ?? "",
                    Confidence = 0.7
                });
            }
        }

        var suggestedCommand = intent switch
        {
            IntentType.FindFiles or IntentType.Search => "storage.list",
            IntentType.GetStatus => "health.status",
            IntentType.GetStats => "storage.stats",
            IntentType.Backup => "backup.create",
            IntentType.Help => "help",
            _ => "search"
        };

        return new QueryIntent
        {
            Type = intent,
            SubType = subType,
            Confidence = confidence,
            Entities = entities,
            Parameters = new Dictionary<string, object>(),
            OriginalQuery = originalQuery,
            NormalizedQuery = normalizedQuery,
            Language = language.LanguageCode,
            Keywords = keywords,
            SuggestedCommand = suggestedCommand,
            IsFollowUp = isFollowUp
        };
    }

    private (IntentType Intent, string? SubType, double Confidence) MatchIntent(string query)
    {
        foreach (var (pattern, intent, subType) in IntentPatterns)
        {
            var match = pattern.Match(query);
            if (match.Success)
            {
                var confidence = Math.Min(0.95, 0.6 + (match.Length / (double)query.Length) * 0.35);
                return (intent, subType, confidence);
            }
        }

        return (IntentType.Search, null, 0.5);
    }

    private string NormalizeQuery(string query)
    {
        // Remove extra whitespace
        var normalized = Regex.Replace(query.Trim(), @"\s+", " ");

        // Expand common contractions
        normalized = Regex.Replace(normalized, @"what's", "what is", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"where's", "where is", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"how's", "how is", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"I'm", "I am", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"don't", "do not", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"can't", "cannot", RegexOptions.IgnoreCase);

        return normalized;
    }

    private string NormalizeEntityValue(string value, EntityType type)
    {
        return type switch
        {
            EntityType.FileSize => NormalizeSize(value),
            EntityType.RelativeTime => NormalizeRelativeTime(value),
            EntityType.FileExtension => value.TrimStart('.').ToLowerInvariant(),
            _ => value.Trim().ToLowerInvariant()
        };
    }

    private string NormalizeSize(string size)
    {
        var match = Regex.Match(size, @"(\d+(?:\.\d+)?)\s*(GB|MB|KB|TB|bytes?)?", RegexOptions.IgnoreCase);
        if (!match.Success) return size;

        if (!double.TryParse(match.Groups[1].Value, out var number)) return size;

        var unit = match.Groups[2].Value.ToUpperInvariant();
        var bytes = unit switch
        {
            "TB" => number * 1024L * 1024 * 1024 * 1024,
            "GB" => number * 1024L * 1024 * 1024,
            "MB" => number * 1024L * 1024,
            "KB" => number * 1024L,
            _ => number
        };

        return ((long)bytes).ToString();
    }

    private string NormalizeRelativeTime(string relativeTime)
    {
        var lower = relativeTime.ToLowerInvariant();
        return lower switch
        {
            "yesterday" => "1d",
            "today" => "0d",
            "this week" => "7d",
            "this month" => "30d",
            "this year" => "365d",
            _ when lower.Contains("week") => Regex.Match(lower, @"\d+").Success
                ? $"{int.Parse(Regex.Match(lower, @"\d+").Value) * 7}d"
                : "7d",
            _ when lower.Contains("month") => Regex.Match(lower, @"\d+").Success
                ? $"{int.Parse(Regex.Match(lower, @"\d+").Value) * 30}d"
                : "30d",
            _ when lower.Contains("year") => Regex.Match(lower, @"\d+").Success
                ? $"{int.Parse(Regex.Match(lower, @"\d+").Value) * 365}d"
                : "365d",
            _ when lower.Contains("day") => Regex.Match(lower, @"\d+").Success
                ? $"{Regex.Match(lower, @"\d+").Value}d"
                : "1d",
            _ => relativeTime
        };
    }

    private List<string> ExtractKeywords(string query)
    {
        // Remove common stop words and extract keywords
        var stopWords = new HashSet<string> { "the", "a", "an", "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did", "will", "would", "could", "should", "may", "might", "must", "shall", "can", "need", "dare", "ought", "used", "to", "of", "in", "for", "on", "with", "at", "by", "from", "as", "into", "through", "during", "before", "after", "above", "below", "between", "under", "again", "further", "then", "once", "here", "there", "when", "where", "why", "how", "all", "each", "few", "more", "most", "other", "some", "such", "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very", "just", "and", "but", "if", "or", "because", "until", "while", "although", "though", "since", "unless", "me", "my", "i", "you", "your", "he", "him", "his", "she", "her", "it", "its", "we", "our", "they", "them", "their" };

        var words = Regex.Split(query.ToLowerInvariant(), @"[\s,;.!?]+")
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();

        return words;
    }

    private IntentType ParseIntentType(string intent)
    {
        return intent.ToLowerInvariant().Replace("_", "") switch
        {
            "search" => IntentType.Search,
            "findfiles" => IntentType.FindFiles,
            "findbymetadata" => IntentType.FindByMetadata,
            "findbycontent" => IntentType.FindByContent,
            "findbydate" => IntentType.FindByDate,
            "findbysize" => IntentType.FindBySize,
            "findbytype" => IntentType.FindByType,
            "getinfo" => IntentType.GetInfo,
            "getstatus" => IntentType.GetStatus,
            "getstats" => IntentType.GetStats,
            "getusage" => IntentType.GetUsage,
            "gethistory" => IntentType.GetHistory,
            "backup" => IntentType.Backup,
            "restore" => IntentType.Restore,
            "delete" => IntentType.Delete,
            "archive" => IntentType.Archive,
            "tier" => IntentType.Tier,
            "explain" => IntentType.Explain,
            "summarize" => IntentType.Summarize,
            "report" => IntentType.Report,
            "help" => IntentType.Help,
            _ => IntentType.Unknown
        };
    }

    private EntityType ParseEntityType(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "datetime" => EntityType.DateTime,
            "daterange" => EntityType.DateRange,
            "relativetime" => EntityType.RelativeTime,
            "filename" => EntityType.FileName,
            "filepath" => EntityType.FilePath,
            "fileextension" => EntityType.FileExtension,
            "filetype" => EntityType.FileType,
            "filesize" => EntityType.FileSize,
            "tag" => EntityType.Tag,
            "owner" => EntityType.Owner,
            "storagepool" => EntityType.StoragePool,
            "bucket" => EntityType.Bucket,
            "region" => EntityType.Region,
            _ => EntityType.Unknown
        };
    }

    private string GetLanguageName(string code)
    {
        return code switch
        {
            "en" => "English",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "zh" => "Chinese",
            "ja" => "Japanese",
            _ => "Unknown"
        };
    }

    private (string Command, string? Filter) BuildSearchCommand(QueryIntent intent, ConversationContext? context, Dictionary<string, object?> parameters)
    {
        var filters = new List<string>();

        foreach (var entity in intent.Entities)
        {
            switch (entity.Type)
            {
                case EntityType.FileName:
                    filters.Add($"name LIKE '%{entity.Value}%'");
                    parameters["name"] = entity.Value;
                    break;
                case EntityType.FileExtension:
                    filters.Add($"extension = '{entity.NormalizedValue}'");
                    parameters["extension"] = entity.NormalizedValue;
                    break;
                case EntityType.Tag:
                    filters.Add($"tags CONTAINS '{entity.Value}'");
                    parameters["tag"] = entity.Value;
                    break;
            }
        }

        if (intent.Keywords.Count > 0)
        {
            parameters["query"] = string.Join(" ", intent.Keywords);
        }

        return ("storage.list", filters.Count > 0 ? string.Join(" AND ", filters) : null);
    }

    private (string Command, string? Filter) BuildMetadataSearchCommand(QueryIntent intent, ConversationContext? context, Dictionary<string, object?> parameters)
    {
        var filters = new List<string>();

        foreach (var entity in intent.Entities.Where(e => e.Type == EntityType.Tag || e.Type == EntityType.Label || e.Type == EntityType.Owner))
        {
            filters.Add($"{entity.Type.ToString().ToLower()} = '{entity.Value}'");
            parameters[entity.Type.ToString().ToLower()] = entity.Value;
        }

        return ("storage.list", filters.Count > 0 ? string.Join(" AND ", filters) : null);
    }

    private (string Command, string? Filter) BuildContentSearchCommand(QueryIntent intent, ConversationContext? context, Dictionary<string, object?> parameters)
    {
        var keywords = intent.Keywords.Count > 0 ? string.Join(" ", intent.Keywords) : intent.OriginalQuery;
        parameters["content"] = keywords;
        return ("storage.search", $"content CONTAINS '{keywords}'");
    }

    private (string Command, string? Filter) BuildDateSearchCommand(QueryIntent intent, ConversationContext? context, Dictionary<string, object?> parameters)
    {
        var filters = new List<string>();

        foreach (var entity in intent.Entities.Where(e => e.Type == EntityType.RelativeTime || e.Type == EntityType.DateTime))
        {
            if (entity.Type == EntityType.RelativeTime)
            {
                parameters["since"] = entity.NormalizedValue;
                filters.Add($"modified >= NOW() - {entity.NormalizedValue}");
            }
            else
            {
                parameters["date"] = entity.Value;
                filters.Add($"modified = '{entity.Value}'");
            }
        }

        return ("storage.list", filters.Count > 0 ? string.Join(" AND ", filters) : null);
    }

    private (string Command, string? Filter) BuildSizeSearchCommand(QueryIntent intent, ConversationContext? context, Dictionary<string, object?> parameters)
    {
        var filters = new List<string>();

        foreach (var entity in intent.Entities.Where(e => e.Type == EntityType.FileSize || e.Type == EntityType.SizeComparison))
        {
            if (intent.OriginalQuery.ToLower().Contains("larger") || intent.OriginalQuery.ToLower().Contains("bigger"))
            {
                filters.Add($"size > {entity.NormalizedValue}");
                parameters["minSize"] = entity.NormalizedValue;
            }
            else if (intent.OriginalQuery.ToLower().Contains("smaller") || intent.OriginalQuery.ToLower().Contains("less"))
            {
                filters.Add($"size < {entity.NormalizedValue}");
                parameters["maxSize"] = entity.NormalizedValue;
            }
        }

        return ("storage.list", filters.Count > 0 ? string.Join(" AND ", filters) : null);
    }

    private (string Command, string? Filter) BuildTypeSearchCommand(QueryIntent intent, ConversationContext? context, Dictionary<string, object?> parameters)
    {
        var extensions = intent.Entities.Where(e => e.Type == EntityType.FileExtension).Select(e => e.NormalizedValue).ToList();

        if (extensions.Count > 0)
        {
            parameters["extensions"] = extensions;
            return ("storage.list", $"extension IN ({string.Join(", ", extensions.Select(e => $"'{e}'"))})");
        }

        return ("storage.list", null);
    }

    private (string Command, string? Filter) BuildBackupCommand(QueryIntent intent, Dictionary<string, object?> parameters)
    {
        var nameEntity = intent.Entities.FirstOrDefault(e => e.Type == EntityType.FileName);
        if (nameEntity != null)
        {
            parameters["name"] = nameEntity.Value;
        }
        else
        {
            parameters["name"] = $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        }

        return ("backup.create", null);
    }

    private (string Command, string? Filter) BuildRestoreCommand(QueryIntent intent, Dictionary<string, object?> parameters)
    {
        var idEntity = intent.Entities.FirstOrDefault(e => e.Type == EntityType.FileName);
        if (idEntity != null)
        {
            parameters["id"] = idEntity.Value;
        }

        return ("backup.restore", null);
    }

    private (string Command, string? Filter) BuildDeleteCommand(QueryIntent intent, Dictionary<string, object?> parameters, List<string> warnings)
    {
        warnings.Add("Delete operations are destructive. Please confirm this action.");

        var nameEntity = intent.Entities.FirstOrDefault(e => e.Type == EntityType.FileName);
        if (nameEntity != null)
        {
            parameters["id"] = nameEntity.Value;
        }

        return ("storage.delete", null);
    }

    private string GenerateExplanation(QueryIntent intent, string command, Dictionary<string, object?> parameters)
    {
        var parts = new List<string> { $"I'll execute '{command}'" };

        if (parameters.Count > 0)
        {
            var paramDesc = string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}"));
            parts.Add($"with parameters: {paramDesc}");
        }

        return string.Join(" ", parts) + ".";
    }

    private ClarificationRequest GenerateClarification(QueryIntent intent)
    {
        return new ClarificationRequest
        {
            Type = ClarificationType.AmbiguousIntent,
            Question = "I'm not sure what you're looking for. What would you like to do?",
            Options = new List<ClarificationOption>
            {
                new() { Label = "Search files", Value = "search", Description = "Find files by name, content, or metadata" },
                new() { Label = "Check status", Value = "status", Description = "View system health and statistics" },
                new() { Label = "Manage backups", Value = "backup", Description = "Create, restore, or list backups" },
                new() { Label = "Get help", Value = "help", Description = "See available commands" }
            },
            Reason = $"Your query had low confidence ({intent.Confidence:P0})"
        };
    }

    private List<string> GenerateSuggestedFollowUps(QueryIntent intent)
    {
        return intent.Type switch
        {
            IntentType.FindFiles or IntentType.Search => new List<string>
            {
                "Show only files larger than 100MB",
                "Filter by last modified date",
                "Include only specific file types"
            },
            IntentType.GetStats => new List<string>
            {
                "Show storage by region",
                "Compare to last month",
                "Break down by file type"
            },
            IntentType.Backup => new List<string>
            {
                "List all backups",
                "Verify the backup",
                "Schedule automatic backups"
            },
            _ => new List<string>()
        };
    }

    #endregion
}

/// <summary>
/// Configuration for the query engine.
/// </summary>
public sealed class QueryEngineConfig
{
    /// <summary>Whether to use AI for query parsing.</summary>
    public bool UseAIForParsing { get; init; } = true;

    /// <summary>Minimum AI confidence to accept.</summary>
    public double MinAIConfidence { get; init; } = 0.6;

    /// <summary>Threshold below which to request clarification.</summary>
    public double ClarificationThreshold { get; init; } = 0.4;

    /// <summary>AI provider routing configuration.</summary>
    public AIProviderRouting ProviderRouting { get; init; } = new();
}
