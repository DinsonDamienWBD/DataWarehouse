// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Knowledge;

/// <summary>
/// Handles knowledge management capabilities including knowledge graphs, question answering,
/// reasoning chains, fact extraction, and contextual memory.
/// </summary>
/// <remarks>
/// <para>
/// This handler implements the Knowledge capability domain with the following sub-capabilities:
/// </para>
/// <list type="bullet">
/// <item><description>KnowledgeGraph - Build and query knowledge graphs from unstructured data</description></item>
/// <item><description>QuestionAnswering - Answer questions using knowledge base and reasoning</description></item>
/// <item><description>ReasoningChain - Chain-of-thought reasoning with explicit steps</description></item>
/// <item><description>FactExtraction - Extract structured facts from unstructured text</description></item>
/// <item><description>ContextualMemory - Maintain and recall contextual information across sessions</description></item>
/// </list>
/// <para>
/// This handler integrates with the SDK's IKnowledgeGraph for persistent knowledge storage.
/// </para>
/// </remarks>
public sealed class KnowledgeCapabilityHandler : ICapabilityHandler
{
    private readonly Func<string, IExtendedAIProvider?> _providerResolver;
    private readonly IKnowledgeGraphStore? _knowledgeStore;
    private readonly KnowledgeConfig _config;

    /// <summary>
    /// Knowledge graph capability identifier.
    /// </summary>
    public const string KnowledgeGraph = "KnowledgeGraph";

    /// <summary>
    /// Question answering capability identifier.
    /// </summary>
    public const string QuestionAnswering = "QuestionAnswering";

    /// <summary>
    /// Reasoning chain capability identifier.
    /// </summary>
    public const string ReasoningChain = "ReasoningChain";

    /// <summary>
    /// Fact extraction capability identifier.
    /// </summary>
    public const string FactExtraction = "FactExtraction";

    /// <summary>
    /// Contextual memory capability identifier.
    /// </summary>
    public const string ContextualMemory = "ContextualMemory";

    /// <inheritdoc/>
    public string CapabilityDomain => "Knowledge";

    /// <inheritdoc/>
    public string DisplayName => "Knowledge Management";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => new[]
    {
        KnowledgeGraph,
        QuestionAnswering,
        ReasoningChain,
        FactExtraction,
        ContextualMemory
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve providers by name.</param>
    /// <param name="knowledgeStore">Optional knowledge graph store for persistence.</param>
    /// <param name="config">Optional configuration settings.</param>
    public KnowledgeCapabilityHandler(
        Func<string, IExtendedAIProvider?> providerResolver,
        IKnowledgeGraphStore? knowledgeStore = null,
        KnowledgeConfig? config = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _knowledgeStore = knowledgeStore;
        _config = config ?? new KnowledgeConfig();
    }

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability))
            return false;

        // Check full domain.capability
        var fullCapability = $"{CapabilityDomain}.{capability}";
        if (instanceConfig.DisabledCapabilities.Contains(fullCapability))
            return false;

        // Knowledge capabilities are available for Basic tier and above
        var limits = UsageLimits.GetDefaultLimits(instanceConfig.QuotaTier);

        // Reasoning chains and knowledge graphs require at least embedding support
        if ((capability == KnowledgeGraph || capability == ReasoningChain) && !limits.EmbeddingsEnabled)
            return false;

        return true;
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var provider))
            return provider;

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out provider))
            return provider;

        // Default providers based on capability
        return capability switch
        {
            ReasoningChain => "anthropic", // Claude excels at reasoning
            QuestionAnswering => "perplexity", // Good at factual Q&A with citations
            _ => _config.DefaultProvider
        };
    }

    /// <summary>
    /// Extracts entities and relationships from text to build a knowledge graph.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The knowledge graph extraction request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The extracted knowledge graph.</returns>
    public async Task<CapabilityResult<KnowledgeGraphResult>> ExtractKnowledgeGraphAsync(
        InstanceCapabilityConfig instanceConfig,
        KnowledgeGraphRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, KnowledgeGraph))
            return CapabilityResult<KnowledgeGraphResult>.Disabled(KnowledgeGraph);

        if (string.IsNullOrWhiteSpace(request.Text))
            return CapabilityResult<KnowledgeGraphResult>.Fail("Text cannot be empty.", "NO_TEXT");

        var providerName = GetProviderForCapability(instanceConfig, KnowledgeGraph);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<KnowledgeGraphResult>.NoProvider(KnowledgeGraph);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<KnowledgeGraphResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var sw = Stopwatch.StartNew();

            var prompt = BuildKnowledgeGraphPrompt(request);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = GetKnowledgeGraphSystemPrompt(request.EntityTypes, request.RelationshipTypes) },
                    new() { Role = "user", Content = prompt }
                },
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
                Temperature = 0.1 // Low temperature for consistent extraction
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            var graphResult = ParseKnowledgeGraphResponse(response.Content);

            // Store in knowledge graph if persistence is enabled
            if (_knowledgeStore != null && request.Persist)
            {
                await StoreKnowledgeGraphAsync(graphResult, request.Namespace, ct);
            }

            return CapabilityResult<KnowledgeGraphResult>.Ok(
                graphResult,
                providerName,
                chatRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<KnowledgeGraphResult>.Fail(ex.Message, "KNOWLEDGE_GRAPH_ERROR");
        }
    }

    /// <summary>
    /// Answers a question using knowledge base and reasoning.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The question answering request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The answer with supporting evidence.</returns>
    public async Task<CapabilityResult<QuestionAnswerResult>> AnswerQuestionAsync(
        InstanceCapabilityConfig instanceConfig,
        QuestionAnswerRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, QuestionAnswering))
            return CapabilityResult<QuestionAnswerResult>.Disabled(QuestionAnswering);

        if (string.IsNullOrWhiteSpace(request.Question))
            return CapabilityResult<QuestionAnswerResult>.Fail("Question cannot be empty.", "NO_QUESTION");

        var providerName = GetProviderForCapability(instanceConfig, QuestionAnswering);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<QuestionAnswerResult>.NoProvider(QuestionAnswering);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<QuestionAnswerResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var sw = Stopwatch.StartNew();

            // Build context from provided documents and/or knowledge store
            var context = await BuildQuestionContextAsync(request, ct);

            var prompt = BuildQuestionAnswerPrompt(request, context);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = GetQuestionAnswerSystemPrompt(request.AnswerStyle) },
                    new() { Role = "user", Content = prompt }
                },
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
                Temperature = request.Temperature ?? 0.3
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            var answerResult = ParseQuestionAnswerResponse(response.Content);

            return CapabilityResult<QuestionAnswerResult>.Ok(
                answerResult,
                providerName,
                chatRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<QuestionAnswerResult>.Fail(ex.Message, "QUESTION_ANSWER_ERROR");
        }
    }

    /// <summary>
    /// Performs chain-of-thought reasoning to solve a problem.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The reasoning request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reasoning chain with conclusion.</returns>
    public async Task<CapabilityResult<ReasoningResult>> ReasonAsync(
        InstanceCapabilityConfig instanceConfig,
        ReasoningRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, ReasoningChain))
            return CapabilityResult<ReasoningResult>.Disabled(ReasoningChain);

        if (string.IsNullOrWhiteSpace(request.Problem))
            return CapabilityResult<ReasoningResult>.Fail("Problem cannot be empty.", "NO_PROBLEM");

        var providerName = GetProviderForCapability(instanceConfig, ReasoningChain);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<ReasoningResult>.NoProvider(ReasoningChain);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<ReasoningResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var sw = Stopwatch.StartNew();

            var prompt = BuildReasoningPrompt(request);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = GetReasoningSystemPrompt(request.ReasoningStyle) },
                    new() { Role = "user", Content = prompt }
                },
                MaxTokens = request.MaxTokens ?? _config.DefaultReasoningMaxTokens,
                Temperature = 0.2 // Low temperature for consistent reasoning
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            var reasoningResult = ParseReasoningResponse(response.Content);

            return CapabilityResult<ReasoningResult>.Ok(
                reasoningResult,
                providerName,
                chatRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<ReasoningResult>.Fail(ex.Message, "REASONING_ERROR");
        }
    }

    /// <summary>
    /// Extracts structured facts from unstructured text.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The fact extraction request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The extracted facts.</returns>
    public async Task<CapabilityResult<FactExtractionResult>> ExtractFactsAsync(
        InstanceCapabilityConfig instanceConfig,
        FactExtractionRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, FactExtraction))
            return CapabilityResult<FactExtractionResult>.Disabled(FactExtraction);

        if (string.IsNullOrWhiteSpace(request.Text))
            return CapabilityResult<FactExtractionResult>.Fail("Text cannot be empty.", "NO_TEXT");

        var providerName = GetProviderForCapability(instanceConfig, FactExtraction);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<FactExtractionResult>.NoProvider(FactExtraction);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<FactExtractionResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var sw = Stopwatch.StartNew();

            var prompt = BuildFactExtractionPrompt(request);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = GetFactExtractionSystemPrompt(request.FactTypes) },
                    new() { Role = "user", Content = prompt }
                },
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
                Temperature = 0.1 // Very low temperature for factual extraction
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            var factsResult = ParseFactExtractionResponse(response.Content);

            return CapabilityResult<FactExtractionResult>.Ok(
                factsResult,
                providerName,
                chatRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<FactExtractionResult>.Fail(ex.Message, "FACT_EXTRACTION_ERROR");
        }
    }

    /// <summary>
    /// Stores information in contextual memory for later recall.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The memory store request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The memory storage result.</returns>
    public async Task<CapabilityResult<MemoryStoreResult>> StoreMemoryAsync(
        InstanceCapabilityConfig instanceConfig,
        MemoryStoreRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, ContextualMemory))
            return CapabilityResult<MemoryStoreResult>.Disabled(ContextualMemory);

        if (string.IsNullOrWhiteSpace(request.Content))
            return CapabilityResult<MemoryStoreResult>.Fail("Content cannot be empty.", "NO_CONTENT");

        if (_knowledgeStore == null)
            return CapabilityResult<MemoryStoreResult>.Fail("Knowledge store not configured.", "NO_STORE");

        try
        {
            var sw = Stopwatch.StartNew();

            var memoryId = Guid.NewGuid().ToString("N");

            // Generate embedding for semantic search if provider available
            float[]? embedding = null;
            var providerName = GetProviderForCapability(instanceConfig, ContextualMemory);
            if (!string.IsNullOrEmpty(providerName))
            {
                var provider = _providerResolver(providerName);
                if (provider != null && provider.SupportsEmbeddings)
                {
                    var embeddings = await provider.EmbedAsync(new[] { request.Content }, null, ct);
                    embedding = embeddings[0].Select(d => (float)d).ToArray();
                }
            }

            var memory = new MemoryEntry
            {
                Id = memoryId,
                Content = request.Content,
                Type = request.MemoryType,
                Tags = request.Tags ?? new List<string>(),
                Metadata = request.Metadata ?? new Dictionary<string, object>(),
                Namespace = request.Namespace,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = request.TTL.HasValue ? DateTime.UtcNow.Add(request.TTL.Value) : null,
                Embedding = embedding
            };

            await _knowledgeStore.StoreMemoryAsync(memory, ct);
            sw.Stop();

            return CapabilityResult<MemoryStoreResult>.Ok(
                new MemoryStoreResult
                {
                    MemoryId = memoryId,
                    Success = true
                },
                providerName,
                null,
                duration: sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<MemoryStoreResult>.Fail(ex.Message, "MEMORY_STORE_ERROR");
        }
    }

    /// <summary>
    /// Recalls information from contextual memory.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The memory recall request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recalled memories.</returns>
    public async Task<CapabilityResult<MemoryRecallResult>> RecallMemoryAsync(
        InstanceCapabilityConfig instanceConfig,
        MemoryRecallRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, ContextualMemory))
            return CapabilityResult<MemoryRecallResult>.Disabled(ContextualMemory);

        if (_knowledgeStore == null)
            return CapabilityResult<MemoryRecallResult>.Fail("Knowledge store not configured.", "NO_STORE");

        try
        {
            var sw = Stopwatch.StartNew();
            var memories = new List<MemoryEntry>();

            // Semantic search if query provided
            if (!string.IsNullOrEmpty(request.Query))
            {
                var providerName = GetProviderForCapability(instanceConfig, ContextualMemory);
                if (!string.IsNullOrEmpty(providerName))
                {
                    var provider = _providerResolver(providerName);
                    if (provider != null && provider.SupportsEmbeddings)
                    {
                        var embeddings = await provider.EmbedAsync(new[] { request.Query }, null, ct);
                        var queryVector = embeddings[0].Select(d => (float)d).ToArray();

                        memories = await _knowledgeStore.SearchMemoriesAsync(
                            queryVector,
                            request.Namespace,
                            request.MaxResults,
                            request.MemoryTypes,
                            request.Tags,
                            ct);
                    }
                }
            }
            else
            {
                // Direct lookup by filters
                memories = await _knowledgeStore.GetMemoriesAsync(
                    request.Namespace,
                    request.MaxResults,
                    request.MemoryTypes,
                    request.Tags,
                    ct);
            }

            sw.Stop();

            return CapabilityResult<MemoryRecallResult>.Ok(
                new MemoryRecallResult
                {
                    Memories = memories,
                    Count = memories.Count
                },
                null,
                null,
                duration: sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<MemoryRecallResult>.Fail(ex.Message, "MEMORY_RECALL_ERROR");
        }
    }

    #region Private Helpers

    private static string GetKnowledgeGraphSystemPrompt(List<string>? entityTypes, List<string>? relationshipTypes)
    {
        var prompt = @"You are a knowledge graph extraction specialist. Extract entities and relationships from the provided text.

Respond in JSON format:
{
  ""entities"": [
    { ""id"": ""e1"", ""name"": ""Entity Name"", ""type"": ""EntityType"", ""properties"": {} }
  ],
  ""relationships"": [
    { ""source"": ""e1"", ""target"": ""e2"", ""type"": ""RelationType"", ""properties"": {} }
  ]
}";

        if (entityTypes != null && entityTypes.Count > 0)
        {
            prompt += $"\n\nFocus on these entity types: {string.Join(", ", entityTypes)}";
        }

        if (relationshipTypes != null && relationshipTypes.Count > 0)
        {
            prompt += $"\n\nFocus on these relationship types: {string.Join(", ", relationshipTypes)}";
        }

        return prompt;
    }

    private static string BuildKnowledgeGraphPrompt(KnowledgeGraphRequest request)
    {
        var prompt = $"Extract a knowledge graph from the following text:\n\n{request.Text}";

        if (request.Focus != null)
        {
            prompt += $"\n\nFocus specifically on: {request.Focus}";
        }

        return prompt;
    }

    private static KnowledgeGraphResult ParseKnowledgeGraphResponse(string response)
    {
        try
        {
            return JsonSerializer.Deserialize<KnowledgeGraphResult>(response)
                ?? new KnowledgeGraphResult();
        }
        catch
        {
            return new KnowledgeGraphResult { RawResponse = response };
        }
    }

    private async Task StoreKnowledgeGraphAsync(KnowledgeGraphResult graph, string? ns, CancellationToken ct)
    {
        if (_knowledgeStore == null || graph.Entities == null) return;

        foreach (var entity in graph.Entities)
        {
            await _knowledgeStore.StoreEntityAsync(entity, ns, ct);
        }

        if (graph.Relationships != null)
        {
            foreach (var relationship in graph.Relationships)
            {
                await _knowledgeStore.StoreRelationshipAsync(relationship, ns, ct);
            }
        }
    }

    private async Task<string> BuildQuestionContextAsync(QuestionAnswerRequest request, CancellationToken ct)
    {
        var context = new StringBuilder();

        // Add provided context documents
        if (request.ContextDocuments != null && request.ContextDocuments.Count > 0)
        {
            context.AppendLine("Context Documents:");
            foreach (var doc in request.ContextDocuments)
            {
                context.AppendLine($"\n--- {doc.Title ?? "Document"} ---");
                context.AppendLine(doc.Content);
            }
        }

        // Query knowledge store if available
        if (_knowledgeStore != null && request.UseKnowledgeBase)
        {
            var relatedEntities = await _knowledgeStore.SearchEntitiesAsync(
                request.Question,
                request.Namespace,
                5,
                ct);

            if (relatedEntities.Count > 0)
            {
                context.AppendLine("\nRelevant Knowledge:");
                foreach (var entity in relatedEntities)
                {
                    context.AppendLine($"- {entity.Name} ({entity.Type}): {JsonSerializer.Serialize(entity.Properties)}");
                }
            }
        }

        return context.ToString();
    }

    private static string GetQuestionAnswerSystemPrompt(AnswerStyle style)
    {
        var basePrompt = @"You are a knowledgeable assistant that answers questions accurately based on the provided context.

Respond in JSON format:
{
  ""answer"": ""The direct answer"",
  ""confidence"": 0.95,
  ""sources"": [""source1"", ""source2""],
  ""reasoning"": ""Brief explanation of how you arrived at the answer"",
  ""caveats"": [""Any limitations or uncertainties""]
}

If you cannot answer from the provided context, say so clearly.";

        return style switch
        {
            AnswerStyle.Concise => basePrompt + "\n\nProvide brief, direct answers.",
            AnswerStyle.Detailed => basePrompt + "\n\nProvide comprehensive, detailed answers with full explanations.",
            AnswerStyle.Academic => basePrompt + "\n\nProvide scholarly answers with citations and nuanced analysis.",
            AnswerStyle.Simple => basePrompt + "\n\nProvide simple answers that anyone can understand. Avoid jargon.",
            _ => basePrompt
        };
    }

    private static string BuildQuestionAnswerPrompt(QuestionAnswerRequest request, string context)
    {
        var prompt = new StringBuilder();

        if (!string.IsNullOrEmpty(context))
        {
            prompt.AppendLine(context);
            prompt.AppendLine();
        }

        prompt.AppendLine($"Question: {request.Question}");

        if (request.FollowUp != null && request.FollowUp.Count > 0)
        {
            prompt.AppendLine("\nPrevious Q&A for context:");
            foreach (var qa in request.FollowUp)
            {
                prompt.AppendLine($"Q: {qa.Question}");
                prompt.AppendLine($"A: {qa.Answer}");
            }
        }

        return prompt.ToString();
    }

    private static QuestionAnswerResult ParseQuestionAnswerResponse(string response)
    {
        try
        {
            return JsonSerializer.Deserialize<QuestionAnswerResult>(response)
                ?? new QuestionAnswerResult { Answer = response };
        }
        catch
        {
            return new QuestionAnswerResult
            {
                Answer = response,
                RawResponse = response
            };
        }
    }

    private static string GetReasoningSystemPrompt(ReasoningStyle style)
    {
        var basePrompt = @"You are a logical reasoning specialist. Think through problems step by step.

Respond in JSON format:
{
  ""steps"": [
    { ""stepNumber"": 1, ""description"": ""Step description"", ""reasoning"": ""Why this step"", ""result"": ""Intermediate result"" }
  ],
  ""conclusion"": ""Final answer/conclusion"",
  ""confidence"": 0.95,
  ""assumptions"": [""Any assumptions made""],
  ""alternatives"": [""Alternative approaches considered""]
}";

        return style switch
        {
            ReasoningStyle.Deductive => basePrompt + "\n\nUse deductive reasoning: start from general principles and derive specific conclusions.",
            ReasoningStyle.Inductive => basePrompt + "\n\nUse inductive reasoning: start from specific observations and form general conclusions.",
            ReasoningStyle.Abductive => basePrompt + "\n\nUse abductive reasoning: find the most likely explanation for the observations.",
            ReasoningStyle.Analytical => basePrompt + "\n\nUse analytical reasoning: break down the problem into components and analyze each.",
            ReasoningStyle.Critical => basePrompt + "\n\nUse critical reasoning: evaluate evidence, identify biases, and question assumptions.",
            _ => basePrompt
        };
    }

    private static string BuildReasoningPrompt(ReasoningRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Problem: {request.Problem}");

        if (request.Context != null && request.Context.Count > 0)
        {
            prompt.AppendLine("\nGiven information:");
            foreach (var item in request.Context)
            {
                prompt.AppendLine($"- {item}");
            }
        }

        if (request.Constraints != null && request.Constraints.Count > 0)
        {
            prompt.AppendLine("\nConstraints:");
            foreach (var constraint in request.Constraints)
            {
                prompt.AppendLine($"- {constraint}");
            }
        }

        return prompt.ToString();
    }

    private static ReasoningResult ParseReasoningResponse(string response)
    {
        try
        {
            return JsonSerializer.Deserialize<ReasoningResult>(response)
                ?? new ReasoningResult { Conclusion = response };
        }
        catch
        {
            return new ReasoningResult
            {
                Conclusion = response,
                RawResponse = response
            };
        }
    }

    private static string GetFactExtractionSystemPrompt(List<string>? factTypes)
    {
        var prompt = @"You are a fact extraction specialist. Extract verifiable facts from text.

Respond in JSON format:
{
  ""facts"": [
    {
      ""statement"": ""The extracted fact"",
      ""type"": ""FactType"",
      ""confidence"": 0.95,
      ""source"": ""Quote or reference from the text"",
      ""entities"": [""Entity1"", ""Entity2""],
      ""temporalInfo"": ""Any date/time information"",
      ""spatialInfo"": ""Any location information""
    }
  ],
  ""summary"": ""Brief summary of key facts""
}

Only extract facts that are explicitly stated or strongly implied. Do not infer or speculate.";

        if (factTypes != null && factTypes.Count > 0)
        {
            prompt += $"\n\nFocus on these types of facts: {string.Join(", ", factTypes)}";
        }

        return prompt;
    }

    private static string BuildFactExtractionPrompt(FactExtractionRequest request)
    {
        var prompt = $"Extract facts from the following text:\n\n{request.Text}";

        if (request.Focus != null)
        {
            prompt += $"\n\nFocus specifically on: {request.Focus}";
        }

        return prompt;
    }

    private static FactExtractionResult ParseFactExtractionResponse(string response)
    {
        try
        {
            return JsonSerializer.Deserialize<FactExtractionResult>(response)
                ?? new FactExtractionResult();
        }
        catch
        {
            return new FactExtractionResult { RawResponse = response };
        }
    }

    #endregion
}

#region Knowledge Store Interface

/// <summary>
/// Interface for persistent knowledge graph storage.
/// </summary>
public interface IKnowledgeGraphStore
{
    /// <summary>Stores an entity in the knowledge graph.</summary>
    Task StoreEntityAsync(KnowledgeEntity entity, string? ns, CancellationToken ct);

    /// <summary>Stores a relationship in the knowledge graph.</summary>
    Task StoreRelationshipAsync(KnowledgeRelationship relationship, string? ns, CancellationToken ct);

    /// <summary>Searches for entities by query.</summary>
    Task<List<KnowledgeEntity>> SearchEntitiesAsync(string query, string? ns, int maxResults, CancellationToken ct);

    /// <summary>Stores a memory entry.</summary>
    Task StoreMemoryAsync(MemoryEntry memory, CancellationToken ct);

    /// <summary>Searches memories by vector similarity.</summary>
    Task<List<MemoryEntry>> SearchMemoriesAsync(float[] queryVector, string? ns, int maxResults, List<string>? types, List<string>? tags, CancellationToken ct);

    /// <summary>Gets memories by filters.</summary>
    Task<List<MemoryEntry>> GetMemoriesAsync(string? ns, int maxResults, List<string>? types, List<string>? tags, CancellationToken ct);
}

#endregion

#region Configuration

/// <summary>
/// Configuration for the knowledge capability handler.
/// </summary>
public sealed class KnowledgeConfig
{
    /// <summary>Default provider for knowledge operations.</summary>
    public string DefaultProvider { get; init; } = "anthropic";

    /// <summary>Default maximum tokens for responses.</summary>
    public int DefaultMaxTokens { get; init; } = 4096;

    /// <summary>Default maximum tokens for reasoning chains.</summary>
    public int DefaultReasoningMaxTokens { get; init; } = 8192;
}

/// <summary>
/// Style of answer to provide.
/// </summary>
public enum AnswerStyle
{
    /// <summary>Standard balanced answers.</summary>
    Standard,
    /// <summary>Brief, direct answers.</summary>
    Concise,
    /// <summary>Comprehensive, detailed answers.</summary>
    Detailed,
    /// <summary>Academic style with citations.</summary>
    Academic,
    /// <summary>Simple, easy-to-understand answers.</summary>
    Simple
}

/// <summary>
/// Style of reasoning to use.
/// </summary>
public enum ReasoningStyle
{
    /// <summary>Standard reasoning.</summary>
    Standard,
    /// <summary>Deductive: general to specific.</summary>
    Deductive,
    /// <summary>Inductive: specific to general.</summary>
    Inductive,
    /// <summary>Abductive: best explanation.</summary>
    Abductive,
    /// <summary>Analytical: break down and analyze.</summary>
    Analytical,
    /// <summary>Critical: evaluate and question.</summary>
    Critical
}

#endregion

#region Request Types

/// <summary>
/// Request for knowledge graph extraction.
/// </summary>
public sealed class KnowledgeGraphRequest
{
    /// <summary>Text to extract knowledge from.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Specific focus area for extraction.</summary>
    public string? Focus { get; init; }

    /// <summary>Entity types to look for.</summary>
    public List<string>? EntityTypes { get; init; }

    /// <summary>Relationship types to look for.</summary>
    public List<string>? RelationshipTypes { get; init; }

    /// <summary>Whether to persist the graph.</summary>
    public bool Persist { get; init; }

    /// <summary>Namespace for storage.</summary>
    public string? Namespace { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Request for question answering.
/// </summary>
public sealed class QuestionAnswerRequest
{
    /// <summary>The question to answer.</summary>
    public string Question { get; init; } = string.Empty;

    /// <summary>Context documents to use.</summary>
    public List<ContextDocument>? ContextDocuments { get; init; }

    /// <summary>Whether to query the knowledge base.</summary>
    public bool UseKnowledgeBase { get; init; } = true;

    /// <summary>Namespace in knowledge base.</summary>
    public string? Namespace { get; init; }

    /// <summary>Previous Q&A for follow-up context.</summary>
    public List<QAPair>? FollowUp { get; init; }

    /// <summary>Answer style preference.</summary>
    public AnswerStyle AnswerStyle { get; init; } = AnswerStyle.Standard;

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature for response generation.</summary>
    public double? Temperature { get; init; }
}

/// <summary>
/// A context document for question answering.
/// </summary>
public sealed class ContextDocument
{
    /// <summary>Document title.</summary>
    public string? Title { get; init; }

    /// <summary>Document content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Source reference.</summary>
    public string? Source { get; init; }
}

/// <summary>
/// A question-answer pair for conversation context.
/// </summary>
public sealed class QAPair
{
    /// <summary>The question.</summary>
    public string Question { get; init; } = string.Empty;

    /// <summary>The answer.</summary>
    public string Answer { get; init; } = string.Empty;
}

/// <summary>
/// Request for reasoning chain.
/// </summary>
public sealed class ReasoningRequest
{
    /// <summary>The problem to reason about.</summary>
    public string Problem { get; init; } = string.Empty;

    /// <summary>Given information/facts.</summary>
    public List<string>? Context { get; init; }

    /// <summary>Constraints to consider.</summary>
    public List<string>? Constraints { get; init; }

    /// <summary>Reasoning style to use.</summary>
    public ReasoningStyle ReasoningStyle { get; init; } = ReasoningStyle.Standard;

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Request for fact extraction.
/// </summary>
public sealed class FactExtractionRequest
{
    /// <summary>Text to extract facts from.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Types of facts to focus on.</summary>
    public List<string>? FactTypes { get; init; }

    /// <summary>Specific focus area.</summary>
    public string? Focus { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Request to store memory.
/// </summary>
public sealed class MemoryStoreRequest
{
    /// <summary>Content to store.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Type of memory.</summary>
    public string MemoryType { get; init; } = "general";

    /// <summary>Tags for categorization.</summary>
    public List<string>? Tags { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>Namespace for organization.</summary>
    public string? Namespace { get; init; }

    /// <summary>Time-to-live for the memory.</summary>
    public TimeSpan? TTL { get; init; }
}

/// <summary>
/// Request to recall memories.
/// </summary>
public sealed class MemoryRecallRequest
{
    /// <summary>Query for semantic search.</summary>
    public string? Query { get; init; }

    /// <summary>Namespace to search in.</summary>
    public string? Namespace { get; init; }

    /// <summary>Filter by memory types.</summary>
    public List<string>? MemoryTypes { get; init; }

    /// <summary>Filter by tags.</summary>
    public List<string>? Tags { get; init; }

    /// <summary>Maximum results to return.</summary>
    public int MaxResults { get; init; } = 10;
}

#endregion

#region Result Types

/// <summary>
/// Result of knowledge graph extraction.
/// </summary>
public sealed class KnowledgeGraphResult
{
    /// <summary>Extracted entities.</summary>
    public List<KnowledgeEntity>? Entities { get; init; }

    /// <summary>Extracted relationships.</summary>
    public List<KnowledgeRelationship>? Relationships { get; init; }

    /// <summary>Raw response from provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// An entity in the knowledge graph.
/// </summary>
public sealed class KnowledgeEntity
{
    /// <summary>Entity identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Entity name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Entity type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Entity properties.</summary>
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// A relationship in the knowledge graph.
/// </summary>
public sealed class KnowledgeRelationship
{
    /// <summary>Source entity ID.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Target entity ID.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Relationship type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Relationship properties.</summary>
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// Result of question answering.
/// </summary>
public sealed class QuestionAnswerResult
{
    /// <summary>The answer.</summary>
    public string Answer { get; init; } = string.Empty;

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Sources used for the answer.</summary>
    public List<string>? Sources { get; init; }

    /// <summary>Reasoning behind the answer.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Caveats or limitations.</summary>
    public List<string>? Caveats { get; init; }

    /// <summary>Raw response from provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// Result of reasoning chain.
/// </summary>
public sealed class ReasoningResult
{
    /// <summary>Reasoning steps.</summary>
    public List<ReasoningStep>? Steps { get; init; }

    /// <summary>Final conclusion.</summary>
    public string Conclusion { get; init; } = string.Empty;

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Assumptions made.</summary>
    public List<string>? Assumptions { get; init; }

    /// <summary>Alternative approaches considered.</summary>
    public List<string>? Alternatives { get; init; }

    /// <summary>Raw response from provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// A step in the reasoning chain.
/// </summary>
public sealed class ReasoningStep
{
    /// <summary>Step number.</summary>
    public int StepNumber { get; init; }

    /// <summary>Step description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Reasoning for this step.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Intermediate result.</summary>
    public string? Result { get; init; }
}

/// <summary>
/// Result of fact extraction.
/// </summary>
public sealed class FactExtractionResult
{
    /// <summary>Extracted facts.</summary>
    public List<ExtractedFact>? Facts { get; init; }

    /// <summary>Summary of key facts.</summary>
    public string? Summary { get; init; }

    /// <summary>Raw response from provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// An extracted fact.
/// </summary>
public sealed class ExtractedFact
{
    /// <summary>The fact statement.</summary>
    public string Statement { get; init; } = string.Empty;

    /// <summary>Type of fact.</summary>
    public string? Type { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Source quote from text.</summary>
    public string? Source { get; init; }

    /// <summary>Entities mentioned in the fact.</summary>
    public List<string>? Entities { get; init; }

    /// <summary>Temporal information.</summary>
    public string? TemporalInfo { get; init; }

    /// <summary>Spatial/location information.</summary>
    public string? SpatialInfo { get; init; }
}

/// <summary>
/// Result of memory storage.
/// </summary>
public sealed class MemoryStoreResult
{
    /// <summary>ID of the stored memory.</summary>
    public string MemoryId { get; init; } = string.Empty;

    /// <summary>Whether storage succeeded.</summary>
    public bool Success { get; init; }
}

/// <summary>
/// Result of memory recall.
/// </summary>
public sealed class MemoryRecallResult
{
    /// <summary>Recalled memories.</summary>
    public List<MemoryEntry> Memories { get; init; } = new();

    /// <summary>Number of memories returned.</summary>
    public int Count { get; init; }
}

/// <summary>
/// A memory entry.
/// </summary>
public sealed class MemoryEntry
{
    /// <summary>Memory identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Memory content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Type of memory.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Tags for categorization.</summary>
    public List<string> Tags { get; init; } = new();

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();

    /// <summary>Namespace.</summary>
    public string? Namespace { get; init; }

    /// <summary>Embedding vector for semantic search.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>When the memory was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When the memory expires.</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Similarity score (set during search).</summary>
    public double? SimilarityScore { get; set; }
}

#endregion
