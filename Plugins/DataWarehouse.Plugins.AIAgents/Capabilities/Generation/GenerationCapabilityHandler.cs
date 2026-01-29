// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Generation;

/// <summary>
/// Handles content generation capabilities including text generation, summarization,
/// translation, paraphrasing, explanation, code generation, and data storytelling.
/// </summary>
public sealed class GenerationCapabilityHandler : ICapabilityHandler
{
    private readonly Func<string, IExtendedAIProvider?> _providerResolver;
    private readonly GenerationConfig _config;

    /// <summary>
    /// Text generation capability identifier.
    /// </summary>
    public const string TextGeneration = "TextGeneration";

    /// <summary>
    /// Summarization capability identifier.
    /// </summary>
    public const string Summarization = "Summarization";

    /// <summary>
    /// Translation capability identifier.
    /// </summary>
    public const string Translation = "Translation";

    /// <summary>
    /// Paraphrasing capability identifier.
    /// </summary>
    public const string Paraphrasing = "Paraphrasing";

    /// <summary>
    /// Explanation capability identifier.
    /// </summary>
    public const string Explanation = "Explanation";

    /// <summary>
    /// Code generation capability identifier.
    /// </summary>
    public const string CodeGeneration = "CodeGeneration";

    /// <summary>
    /// Data storytelling capability identifier.
    /// </summary>
    public const string DataStorytelling = "DataStorytelling";

    /// <inheritdoc/>
    public string CapabilityDomain => "Generation";

    /// <inheritdoc/>
    public string DisplayName => "Content Generation";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => new[]
    {
        TextGeneration,
        Summarization,
        Translation,
        Paraphrasing,
        Explanation,
        CodeGeneration,
        DataStorytelling
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerationCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve providers by name.</param>
    /// <param name="config">Optional configuration settings.</param>
    public GenerationCapabilityHandler(
        Func<string, IExtendedAIProvider?> providerResolver,
        GenerationConfig? config = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _config = config ?? new GenerationConfig();
    }

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability))
            return false;

        // Check quota tier restrictions
        var limits = UsageLimits.GetDefaultLimits(instanceConfig.QuotaTier);

        // Code generation might be restricted on lower tiers
        if (capability == CodeGeneration && instanceConfig.QuotaTier == QuotaTier.Free)
            return false;

        return true;
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var provider))
            return provider;

        // Fall back to domain-level mapping
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out provider))
            return provider;

        // Default provider based on capability
        return capability switch
        {
            CodeGeneration => "openai", // GPT-4 is strong at code
            DataStorytelling => "anthropic", // Claude excels at narrative
            _ => _config.DefaultProvider
        };
    }

    /// <summary>
    /// Generates text based on a prompt.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The generation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generation result.</returns>
    public async Task<CapabilityResult<TextGenerationResult>> GenerateTextAsync(
        InstanceCapabilityConfig instanceConfig,
        TextGenerationRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, TextGeneration))
            return CapabilityResult<TextGenerationResult>.Disabled(TextGeneration);

        var providerName = GetProviderForCapability(instanceConfig, TextGeneration);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<TextGenerationResult>.NoProvider(TextGeneration);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<TextGenerationResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = BuildMessages(request.SystemPrompt, request.Prompt, request.Context),
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
                Temperature = request.Temperature ?? _config.DefaultTemperature,
                StopSequences = request.StopSequences
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            var duration = DateTime.UtcNow - startTime;

            return CapabilityResult<TextGenerationResult>.Ok(
                new TextGenerationResult
                {
                    GeneratedText = response.Content,
                    FinishReason = response.FinishReason
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<TextGenerationResult>.Fail(ex.Message, "GENERATION_ERROR");
        }
    }

    /// <summary>
    /// Summarizes the provided content.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The summarization request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The summarization result.</returns>
    public async Task<CapabilityResult<SummarizationResult>> SummarizeAsync(
        InstanceCapabilityConfig instanceConfig,
        SummarizationRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, Summarization))
            return CapabilityResult<SummarizationResult>.Disabled(Summarization);

        var providerName = GetProviderForCapability(instanceConfig, Summarization);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<SummarizationResult>.NoProvider(Summarization);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<SummarizationResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            var systemPrompt = BuildSummarizationSystemPrompt(request);
            var userPrompt = BuildSummarizationUserPrompt(request);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = userPrompt }
                },
                MaxTokens = CalculateSummaryMaxTokens(request),
                Temperature = 0.3 // Lower temperature for more focused summaries
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            var duration = DateTime.UtcNow - startTime;

            // Extract key points if requested
            List<string>? keyPoints = null;
            if (request.ExtractKeyPoints)
            {
                keyPoints = ExtractKeyPointsFromSummary(response.Content);
            }

            return CapabilityResult<SummarizationResult>.Ok(
                new SummarizationResult
                {
                    Summary = response.Content,
                    KeyPoints = keyPoints,
                    OriginalLength = request.Content.Length,
                    SummaryLength = response.Content.Length,
                    CompressionRatio = request.Content.Length > 0
                        ? (double)response.Content.Length / request.Content.Length
                        : 0
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<SummarizationResult>.Fail(ex.Message, "SUMMARIZATION_ERROR");
        }
    }

    /// <summary>
    /// Translates content to the target language.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The translation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The translation result.</returns>
    public async Task<CapabilityResult<TranslationResult>> TranslateAsync(
        InstanceCapabilityConfig instanceConfig,
        TranslationRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, Translation))
            return CapabilityResult<TranslationResult>.Disabled(Translation);

        var providerName = GetProviderForCapability(instanceConfig, Translation);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<TranslationResult>.NoProvider(Translation);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<TranslationResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            var systemPrompt = $"""
                You are an expert translator. Translate the following text to {request.TargetLanguage}.
                {(request.PreserveFormatting ? "Preserve all formatting, including line breaks, bullet points, and structure." : "")}
                {(request.FormalityLevel != null ? $"Use a {request.FormalityLevel} tone." : "")}
                {(request.Domain != null ? $"This is {request.Domain} content, use appropriate terminology." : "")}
                Only output the translation, no explanations.
                """;

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = request.Content }
                },
                MaxTokens = request.Content.Length * 2, // Translations might be longer
                Temperature = 0.2 // Low temperature for accuracy
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            var duration = DateTime.UtcNow - startTime;

            return CapabilityResult<TranslationResult>.Ok(
                new TranslationResult
                {
                    TranslatedText = response.Content,
                    SourceLanguage = request.SourceLanguage,
                    TargetLanguage = request.TargetLanguage
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<TranslationResult>.Fail(ex.Message, "TRANSLATION_ERROR");
        }
    }

    /// <summary>
    /// Paraphrases the provided content.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The paraphrasing request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The paraphrasing result.</returns>
    public async Task<CapabilityResult<ParaphrasingResult>> ParaphraseAsync(
        InstanceCapabilityConfig instanceConfig,
        ParaphrasingRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, Paraphrasing))
            return CapabilityResult<ParaphrasingResult>.Disabled(Paraphrasing);

        var providerName = GetProviderForCapability(instanceConfig, Paraphrasing);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<ParaphrasingResult>.NoProvider(Paraphrasing);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<ParaphrasingResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            var styleInstruction = request.Style switch
            {
                ParaphrasingStyle.Formal => "Use formal, professional language.",
                ParaphrasingStyle.Casual => "Use casual, conversational language.",
                ParaphrasingStyle.Academic => "Use academic, scholarly language.",
                ParaphrasingStyle.Simple => "Use simple, easy-to-understand language.",
                ParaphrasingStyle.Creative => "Use creative, engaging language.",
                _ => ""
            };

            var systemPrompt = $"""
                You are an expert at paraphrasing text. Rewrite the following text while:
                - Preserving the original meaning
                - Using different words and sentence structures
                {styleInstruction}
                {(request.PreserveTone ? "Maintain the original tone and emphasis." : "")}
                Only output the paraphrased text.
                """;

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = request.Content }
                },
                MaxTokens = request.Content.Length * 2,
                Temperature = request.Style == ParaphrasingStyle.Creative ? 0.8 : 0.5
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            var duration = DateTime.UtcNow - startTime;

            return CapabilityResult<ParaphrasingResult>.Ok(
                new ParaphrasingResult
                {
                    ParaphrasedText = response.Content,
                    Style = request.Style
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<ParaphrasingResult>.Fail(ex.Message, "PARAPHRASING_ERROR");
        }
    }

    /// <summary>
    /// Generates an explanation for the provided topic or concept.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The explanation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The explanation result.</returns>
    public async Task<CapabilityResult<ExplanationResult>> ExplainAsync(
        InstanceCapabilityConfig instanceConfig,
        ExplanationRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, Explanation))
            return CapabilityResult<ExplanationResult>.Disabled(Explanation);

        var providerName = GetProviderForCapability(instanceConfig, Explanation);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<ExplanationResult>.NoProvider(Explanation);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<ExplanationResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            var audienceLevel = request.AudienceLevel switch
            {
                AudienceLevel.Beginner => "Explain like I'm a complete beginner with no technical background.",
                AudienceLevel.Intermediate => "Explain for someone with basic knowledge of the subject.",
                AudienceLevel.Expert => "Explain in technical detail for an expert audience.",
                AudienceLevel.Child => "Explain in very simple terms, as if to a 10-year-old.",
                _ => ""
            };

            var systemPrompt = $"""
                You are an expert at explaining complex topics clearly.
                {audienceLevel}
                {(request.IncludeExamples ? "Include practical examples to illustrate key points." : "")}
                {(request.IncludeAnalogies ? "Use analogies to make concepts more relatable." : "")}
                Structure your explanation clearly with logical flow.
                """;

            var userPrompt = request.Context != null
                ? $"Context: {request.Context}\n\nExplain: {request.Topic}"
                : $"Explain: {request.Topic}";

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = userPrompt }
                },
                MaxTokens = request.MaxLength ?? 2000,
                Temperature = 0.5
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            var duration = DateTime.UtcNow - startTime;

            return CapabilityResult<ExplanationResult>.Ok(
                new ExplanationResult
                {
                    Explanation = response.Content,
                    Topic = request.Topic,
                    AudienceLevel = request.AudienceLevel
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<ExplanationResult>.Fail(ex.Message, "EXPLANATION_ERROR");
        }
    }

    /// <summary>
    /// Generates code based on the provided specification.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The code generation request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The code generation result.</returns>
    public async Task<CapabilityResult<CodeGenerationResult>> GenerateCodeAsync(
        InstanceCapabilityConfig instanceConfig,
        CodeGenerationRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CodeGeneration))
            return CapabilityResult<CodeGenerationResult>.Disabled(CodeGeneration);

        var providerName = GetProviderForCapability(instanceConfig, CodeGeneration);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<CodeGenerationResult>.NoProvider(CodeGeneration);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<CodeGenerationResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            var languageInstruction = !string.IsNullOrEmpty(request.Language)
                ? $"Write code in {request.Language}."
                : "";

            var frameworkInstruction = !string.IsNullOrEmpty(request.Framework)
                ? $"Use the {request.Framework} framework."
                : "";

            var systemPrompt = $"""
                You are an expert software developer.
                {languageInstruction}
                {frameworkInstruction}
                Write clean, well-documented, production-ready code.
                {(request.IncludeTests ? "Include unit tests." : "")}
                {(request.IncludeComments ? "Include detailed comments explaining the code." : "")}
                Follow best practices and design patterns appropriate for the language.
                Only output the code, wrapped in appropriate markdown code blocks.
                """;

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"Task: {request.Description}");

            if (request.Requirements?.Count > 0)
            {
                userPrompt.AppendLine("\nRequirements:");
                foreach (var req in request.Requirements)
                {
                    userPrompt.AppendLine($"- {req}");
                }
            }

            if (!string.IsNullOrEmpty(request.ExistingCode))
            {
                userPrompt.AppendLine($"\nExisting code to extend/modify:\n```\n{request.ExistingCode}\n```");
            }

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = userPrompt.ToString() }
                },
                MaxTokens = request.MaxTokens ?? 4000,
                Temperature = 0.2 // Low temperature for more deterministic code
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            var duration = DateTime.UtcNow - startTime;

            // Extract code blocks from response
            var (code, explanation) = ExtractCodeAndExplanation(response.Content);

            return CapabilityResult<CodeGenerationResult>.Ok(
                new CodeGenerationResult
                {
                    Code = code,
                    Language = request.Language,
                    Explanation = explanation,
                    FullResponse = response.Content
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<CodeGenerationResult>.Fail(ex.Message, "CODE_GENERATION_ERROR");
        }
    }

    /// <summary>
    /// Generates a data narrative/story from the provided data.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The data storytelling request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The data storytelling result.</returns>
    public async Task<CapabilityResult<DataStoryResult>> GenerateDataStoryAsync(
        InstanceCapabilityConfig instanceConfig,
        DataStoryRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, DataStorytelling))
            return CapabilityResult<DataStoryResult>.Disabled(DataStorytelling);

        var providerName = GetProviderForCapability(instanceConfig, DataStorytelling);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<DataStoryResult>.NoProvider(DataStorytelling);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<DataStoryResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var startTime = DateTime.UtcNow;

            var narrativeStyle = request.Style switch
            {
                NarrativeStyle.Executive => "Write an executive summary suitable for C-level stakeholders. Be concise and focus on business impact.",
                NarrativeStyle.Technical => "Write a technical analysis with detailed metrics and statistical insights.",
                NarrativeStyle.Casual => "Write an engaging, easy-to-understand narrative for a general audience.",
                NarrativeStyle.Journalistic => "Write in a journalistic style, leading with the most important findings.",
                _ => "Write a clear, professional data narrative."
            };

            var systemPrompt = $"""
                You are a data analyst expert at turning data into compelling narratives.
                {narrativeStyle}
                {(request.IncludeTrends ? "Identify and highlight trends in the data." : "")}
                {(request.IncludeInsights ? "Provide actionable insights based on the data." : "")}
                {(request.IncludeRecommendations ? "Include specific recommendations based on your analysis." : "")}
                Structure the narrative with clear sections.
                """;

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"Data Title: {request.Title}");

            if (!string.IsNullOrEmpty(request.TimeRange))
            {
                userPrompt.AppendLine($"Time Period: {request.TimeRange}");
            }

            userPrompt.AppendLine($"\nData:\n{JsonSerializer.Serialize(request.Data, new JsonSerializerOptions { WriteIndented = true })}");

            if (request.Metrics?.Count > 0)
            {
                userPrompt.AppendLine("\nKey Metrics:");
                foreach (var metric in request.Metrics)
                {
                    userPrompt.AppendLine($"- {metric.Key}: {metric.Value}");
                }
            }

            if (!string.IsNullOrEmpty(request.Context))
            {
                userPrompt.AppendLine($"\nAdditional Context: {request.Context}");
            }

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = userPrompt.ToString() }
                },
                MaxTokens = request.MaxLength ?? 2000,
                Temperature = 0.6 // Balanced for creativity while maintaining accuracy
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            var duration = DateTime.UtcNow - startTime;

            // Parse sections from the narrative
            var sections = ParseNarrativeSections(response.Content);

            return CapabilityResult<DataStoryResult>.Ok(
                new DataStoryResult
                {
                    Narrative = response.Content,
                    Title = request.Title,
                    Sections = sections,
                    Style = request.Style
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                duration);
        }
        catch (Exception ex)
        {
            return CapabilityResult<DataStoryResult>.Fail(ex.Message, "DATA_STORYTELLING_ERROR");
        }
    }

    #region Private Helpers

    private static List<ChatMessage> BuildMessages(string? systemPrompt, string userPrompt, string? context)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
        }

        var fullUserPrompt = !string.IsNullOrEmpty(context)
            ? $"Context: {context}\n\n{userPrompt}"
            : userPrompt;

        messages.Add(new ChatMessage { Role = "user", Content = fullUserPrompt });

        return messages;
    }

    private static string BuildSummarizationSystemPrompt(SummarizationRequest request)
    {
        var lengthInstruction = request.TargetLength switch
        {
            SummaryLength.Brief => "Create a very brief summary of 1-2 sentences.",
            SummaryLength.Short => "Create a short summary of 3-5 sentences.",
            SummaryLength.Medium => "Create a medium-length summary of 1-2 paragraphs.",
            SummaryLength.Long => "Create a comprehensive summary that covers all key points.",
            _ => "Create a concise summary."
        };

        var formatInstruction = request.Format switch
        {
            SummaryFormat.Paragraph => "Write in paragraph form.",
            SummaryFormat.BulletPoints => "Use bullet points for each key point.",
            SummaryFormat.NumberedList => "Use a numbered list format.",
            SummaryFormat.Structured => "Use a structured format with clear sections.",
            _ => ""
        };

        return $"""
            You are an expert at summarizing content accurately and concisely.
            {lengthInstruction}
            {formatInstruction}
            {(request.PreserveKeyDetails ? "Ensure all critical details and numbers are preserved." : "")}
            {(request.FocusArea != null ? $"Focus especially on: {request.FocusArea}" : "")}
            Only output the summary.
            """;
    }

    private static string BuildSummarizationUserPrompt(SummarizationRequest request)
    {
        var prompt = new StringBuilder();

        if (!string.IsNullOrEmpty(request.Title))
        {
            prompt.AppendLine($"Title: {request.Title}");
        }

        prompt.AppendLine($"\nContent to summarize:\n{request.Content}");

        return prompt.ToString();
    }

    private static int CalculateSummaryMaxTokens(SummarizationRequest request)
    {
        return request.TargetLength switch
        {
            SummaryLength.Brief => 100,
            SummaryLength.Short => 250,
            SummaryLength.Medium => 500,
            SummaryLength.Long => 1000,
            _ => 300
        };
    }

    private static List<string>? ExtractKeyPointsFromSummary(string summary)
    {
        // Simple extraction - split by bullet points or numbered items
        var lines = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var keyPoints = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") ||
                (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.'))
            {
                var point = trimmed.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ');
                if (!string.IsNullOrWhiteSpace(point))
                {
                    keyPoints.Add(point);
                }
            }
        }

        return keyPoints.Count > 0 ? keyPoints : null;
    }

    private static (string code, string? explanation) ExtractCodeAndExplanation(string response)
    {
        var codeBlockPattern = "```";
        var startIndex = response.IndexOf(codeBlockPattern);

        if (startIndex < 0)
        {
            return (response, null);
        }

        var beforeCode = startIndex > 0 ? response[..startIndex].Trim() : null;

        // Find end of first code block
        var codeStart = response.IndexOf('\n', startIndex) + 1;
        var codeEnd = response.IndexOf(codeBlockPattern, codeStart);

        if (codeEnd < 0)
        {
            codeEnd = response.Length;
        }

        var code = response[codeStart..codeEnd].Trim();
        var afterCode = codeEnd + codeBlockPattern.Length < response.Length
            ? response[(codeEnd + codeBlockPattern.Length)..].Trim()
            : null;

        var explanation = !string.IsNullOrEmpty(beforeCode)
            ? beforeCode
            : afterCode;

        return (code, explanation);
    }

    private static List<NarrativeSection> ParseNarrativeSections(string narrative)
    {
        var sections = new List<NarrativeSection>();
        var lines = narrative.Split('\n');
        var currentSection = new NarrativeSection { Title = "Overview", Order = 1 };
        var contentBuilder = new StringBuilder();
        var sectionOrder = 1;

        foreach (var line in lines)
        {
            // Check for section headers (## Header or **Header**)
            if (line.StartsWith("## ") || line.StartsWith("# "))
            {
                if (contentBuilder.Length > 0)
                {
                    currentSection.Content = contentBuilder.ToString().Trim();
                    sections.Add(currentSection);
                    contentBuilder.Clear();
                }

                sectionOrder++;
                currentSection = new NarrativeSection
                {
                    Title = line.TrimStart('#', ' '),
                    Order = sectionOrder
                };
            }
            else
            {
                contentBuilder.AppendLine(line);
            }
        }

        // Add final section
        if (contentBuilder.Length > 0)
        {
            currentSection.Content = contentBuilder.ToString().Trim();
            sections.Add(currentSection);
        }

        return sections;
    }

    #endregion
}

#region Request/Result Types

/// <summary>
/// Configuration for the generation capability handler.
/// </summary>
public sealed class GenerationConfig
{
    /// <summary>Default provider for generation capabilities.</summary>
    public string DefaultProvider { get; init; } = "openai";

    /// <summary>Default maximum tokens for generation.</summary>
    public int DefaultMaxTokens { get; init; } = 2048;

    /// <summary>Default temperature for generation.</summary>
    public double DefaultTemperature { get; init; } = 0.7;
}

/// <summary>
/// Request for text generation.
/// </summary>
public sealed class TextGenerationRequest
{
    /// <summary>The main prompt for generation.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Optional system prompt to guide the generation.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Optional context to include.</summary>
    public string? Context { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens to generate.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature for randomness (0.0-2.0).</summary>
    public double? Temperature { get; init; }

    /// <summary>Sequences that stop generation.</summary>
    public string[]? StopSequences { get; init; }
}

/// <summary>
/// Result of text generation.
/// </summary>
public sealed class TextGenerationResult
{
    /// <summary>The generated text.</summary>
    public string GeneratedText { get; init; } = string.Empty;

    /// <summary>Reason generation stopped.</summary>
    public string? FinishReason { get; init; }
}

/// <summary>
/// Request for summarization.
/// </summary>
public sealed class SummarizationRequest
{
    /// <summary>Content to summarize.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Optional title of the content.</summary>
    public string? Title { get; init; }

    /// <summary>Target summary length.</summary>
    public SummaryLength TargetLength { get; init; } = SummaryLength.Medium;

    /// <summary>Output format for the summary.</summary>
    public SummaryFormat Format { get; init; } = SummaryFormat.Paragraph;

    /// <summary>Whether to preserve key numerical details.</summary>
    public bool PreserveKeyDetails { get; init; } = true;

    /// <summary>Specific area to focus on.</summary>
    public string? FocusArea { get; init; }

    /// <summary>Whether to extract key points as a list.</summary>
    public bool ExtractKeyPoints { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Target length for summaries.
/// </summary>
public enum SummaryLength { Brief, Short, Medium, Long }

/// <summary>
/// Output format for summaries.
/// </summary>
public enum SummaryFormat { Paragraph, BulletPoints, NumberedList, Structured }

/// <summary>
/// Result of summarization.
/// </summary>
public sealed class SummarizationResult
{
    /// <summary>The generated summary.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Extracted key points if requested.</summary>
    public List<string>? KeyPoints { get; init; }

    /// <summary>Original content length in characters.</summary>
    public int OriginalLength { get; init; }

    /// <summary>Summary length in characters.</summary>
    public int SummaryLength { get; init; }

    /// <summary>Compression ratio (summary/original).</summary>
    public double CompressionRatio { get; init; }
}

/// <summary>
/// Request for translation.
/// </summary>
public sealed class TranslationRequest
{
    /// <summary>Content to translate.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Source language (auto-detect if null).</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>Target language.</summary>
    public string TargetLanguage { get; init; } = string.Empty;

    /// <summary>Whether to preserve formatting.</summary>
    public bool PreserveFormatting { get; init; } = true;

    /// <summary>Formality level (formal, informal, neutral).</summary>
    public string? FormalityLevel { get; init; }

    /// <summary>Domain for specialized terminology.</summary>
    public string? Domain { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Result of translation.
/// </summary>
public sealed class TranslationResult
{
    /// <summary>The translated text.</summary>
    public string TranslatedText { get; init; } = string.Empty;

    /// <summary>Detected or specified source language.</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>Target language.</summary>
    public string TargetLanguage { get; init; } = string.Empty;
}

/// <summary>
/// Request for paraphrasing.
/// </summary>
public sealed class ParaphrasingRequest
{
    /// <summary>Content to paraphrase.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Style of paraphrasing.</summary>
    public ParaphrasingStyle Style { get; init; } = ParaphrasingStyle.Standard;

    /// <summary>Whether to preserve the original tone.</summary>
    public bool PreserveTone { get; init; } = true;

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Style options for paraphrasing.
/// </summary>
public enum ParaphrasingStyle { Standard, Formal, Casual, Academic, Simple, Creative }

/// <summary>
/// Result of paraphrasing.
/// </summary>
public sealed class ParaphrasingResult
{
    /// <summary>The paraphrased text.</summary>
    public string ParaphrasedText { get; init; } = string.Empty;

    /// <summary>Style used for paraphrasing.</summary>
    public ParaphrasingStyle Style { get; init; }
}

/// <summary>
/// Request for explanation generation.
/// </summary>
public sealed class ExplanationRequest
{
    /// <summary>Topic or concept to explain.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Additional context for the explanation.</summary>
    public string? Context { get; init; }

    /// <summary>Target audience level.</summary>
    public AudienceLevel AudienceLevel { get; init; } = AudienceLevel.Intermediate;

    /// <summary>Whether to include examples.</summary>
    public bool IncludeExamples { get; init; } = true;

    /// <summary>Whether to include analogies.</summary>
    public bool IncludeAnalogies { get; init; }

    /// <summary>Maximum length of explanation.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Target audience level for explanations.
/// </summary>
public enum AudienceLevel { Child, Beginner, Intermediate, Expert }

/// <summary>
/// Result of explanation generation.
/// </summary>
public sealed class ExplanationResult
{
    /// <summary>The generated explanation.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>The topic that was explained.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Audience level used.</summary>
    public AudienceLevel AudienceLevel { get; init; }
}

/// <summary>
/// Request for code generation.
/// </summary>
public sealed class CodeGenerationRequest
{
    /// <summary>Description of what the code should do.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Target programming language.</summary>
    public string? Language { get; init; }

    /// <summary>Framework or library to use.</summary>
    public string? Framework { get; init; }

    /// <summary>Specific requirements for the code.</summary>
    public List<string>? Requirements { get; init; }

    /// <summary>Existing code to extend or modify.</summary>
    public string? ExistingCode { get; init; }

    /// <summary>Whether to include unit tests.</summary>
    public bool IncludeTests { get; init; }

    /// <summary>Whether to include detailed comments.</summary>
    public bool IncludeComments { get; init; } = true;

    /// <summary>Maximum tokens for generation.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Result of code generation.
/// </summary>
public sealed class CodeGenerationResult
{
    /// <summary>The generated code.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Programming language of the code.</summary>
    public string? Language { get; init; }

    /// <summary>Explanation of the code.</summary>
    public string? Explanation { get; init; }

    /// <summary>Full response including any additional text.</summary>
    public string FullResponse { get; init; } = string.Empty;
}

/// <summary>
/// Request for data storytelling.
/// </summary>
public sealed class DataStoryRequest
{
    /// <summary>Title for the data story.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The data to create a narrative from.</summary>
    public object Data { get; init; } = new();

    /// <summary>Key metrics to highlight.</summary>
    public Dictionary<string, object>? Metrics { get; init; }

    /// <summary>Time range the data covers.</summary>
    public string? TimeRange { get; init; }

    /// <summary>Additional context for the narrative.</summary>
    public string? Context { get; init; }

    /// <summary>Narrative style.</summary>
    public NarrativeStyle Style { get; init; } = NarrativeStyle.Professional;

    /// <summary>Whether to include trend analysis.</summary>
    public bool IncludeTrends { get; init; } = true;

    /// <summary>Whether to include insights.</summary>
    public bool IncludeInsights { get; init; } = true;

    /// <summary>Whether to include recommendations.</summary>
    public bool IncludeRecommendations { get; init; }

    /// <summary>Maximum length of the narrative.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Narrative style options.
/// </summary>
public enum NarrativeStyle { Professional, Executive, Technical, Casual, Journalistic }

/// <summary>
/// Result of data storytelling.
/// </summary>
public sealed class DataStoryResult
{
    /// <summary>The full narrative.</summary>
    public string Narrative { get; init; } = string.Empty;

    /// <summary>Title of the story.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Parsed sections of the narrative.</summary>
    public List<NarrativeSection> Sections { get; init; } = new();

    /// <summary>Style used for the narrative.</summary>
    public NarrativeStyle Style { get; init; }
}

/// <summary>
/// A section within a data narrative.
/// </summary>
public sealed class NarrativeSection
{
    /// <summary>Section title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Section content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Order of the section in the narrative.</summary>
    public int Order { get; set; }
}

#endregion
