// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIAgents.Models;
using DataWarehouse.Plugins.AIAgents.Registry;
using DataWarehouse.SDK.AI;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Semantics;

/// <summary>
/// Handles semantic analysis capabilities including categorization, entity extraction,
/// PII detection, sentiment analysis, language detection, duplicate detection,
/// relationship discovery, topic modeling, and keyword extraction.
/// </summary>
/// <remarks>
/// <para>
/// This handler coordinates between multiple semantic engines and routes requests
/// to the appropriate AI provider based on user configuration, with statistical
/// fallbacks when no AI provider is available.
/// </para>
/// <para>
/// The handler integrates with:
/// - <see cref="InstanceCapabilityRegistry"/> - Checks instance-level capability enablement
/// - <see cref="UserCapabilityMappings"/> - Resolves user-to-provider mappings
/// - <see cref="UserProviderRegistry"/> - Gets actual provider instances
/// </para>
/// <para>
/// Sub-capability checking behavior depends on the ChildrenMirrorParent setting:
/// - When true: If Semantics is enabled, all sub-capabilities are enabled
/// - When false: Each sub-capability must be explicitly enabled
/// </para>
/// </remarks>
public sealed class SemanticsCapabilityHandler : ICapabilityHandler, ICapabilityHandlerEx
{
    /// <summary>
    /// Capability name for content categorization.
    /// </summary>
    public const string CapabilityCategorization = "Categorization";

    /// <summary>
    /// Capability name for named entity recognition.
    /// </summary>
    public const string CapabilityEntityExtraction = "EntityExtraction";

    /// <summary>
    /// Capability name for PII detection and redaction.
    /// </summary>
    public const string CapabilityPIIDetection = "PIIDetection";

    /// <summary>
    /// Capability name for sentiment analysis.
    /// </summary>
    public const string CapabilitySentimentAnalysis = "SentimentAnalysis";

    /// <summary>
    /// Capability name for language detection.
    /// </summary>
    public const string CapabilityLanguageDetection = "LanguageDetection";

    /// <summary>
    /// Capability name for duplicate detection.
    /// </summary>
    public const string CapabilityDuplicateDetection = "DuplicateDetection";

    /// <summary>
    /// Capability name for relationship discovery.
    /// </summary>
    public const string CapabilityRelationshipDiscovery = "RelationshipDiscovery";

    /// <summary>
    /// Capability name for topic modeling.
    /// </summary>
    public const string CapabilityTopicModeling = "TopicModeling";

    /// <summary>
    /// Capability name for keyword extraction.
    /// </summary>
    public const string CapabilityKeywordExtraction = "KeywordExtraction";

    /// <summary>
    /// Capability name for content summarization.
    /// </summary>
    public const string CapabilitySummarization = "Summarization";

    private static readonly IReadOnlyList<string> _supportedCapabilities = new[]
    {
        CapabilityCategorization,
        CapabilityEntityExtraction,
        CapabilityPIIDetection,
        CapabilitySentimentAnalysis,
        CapabilityLanguageDetection,
        CapabilityDuplicateDetection,
        CapabilityRelationshipDiscovery,
        CapabilityTopicModeling,
        CapabilityKeywordExtraction,
        CapabilitySummarization
    };

    private static readonly IReadOnlySet<AISubCapability> _subCapabilities = new HashSet<AISubCapability>
    {
        AISubCapability.SemanticsCategorization,
        AISubCapability.SemanticsAutoTagging,
        AISubCapability.SemanticsSimilarity,
        AISubCapability.SemanticsDuplicateDetection,
        AISubCapability.SemanticsClustering,
        AISubCapability.SemanticsSearchEnhancement,
        AISubCapability.SemanticsRelationshipExtraction
    };

    /// <summary>
    /// Maps string capability names to AISubCapability enum values.
    /// </summary>
    private static readonly Dictionary<string, AISubCapability> _capabilityToSubCapability = new()
    {
        [CapabilityCategorization] = AISubCapability.SemanticsCategorization,
        [CapabilityEntityExtraction] = AISubCapability.SemanticsRelationshipExtraction,
        [CapabilityPIIDetection] = AISubCapability.SemanticsAutoTagging, // PII is a form of auto-tagging
        [CapabilitySentimentAnalysis] = AISubCapability.SemanticsCategorization,
        [CapabilityDuplicateDetection] = AISubCapability.SemanticsDuplicateDetection,
        [CapabilityRelationshipDiscovery] = AISubCapability.SemanticsRelationshipExtraction,
        [CapabilitySummarization] = AISubCapability.SemanticsCategorization,
        [CapabilityKeywordExtraction] = AISubCapability.SemanticsAutoTagging,
        [CapabilityTopicModeling] = AISubCapability.SemanticsClustering,
        [CapabilityLanguageDetection] = AISubCapability.SemanticsCategorization
    };

    private readonly Func<string, IExtendedAIProvider?>? _providerResolver;
    private readonly IVectorStore? _vectorStore;
    private readonly IKnowledgeGraph? _knowledgeGraph;
    private readonly InstanceCapabilityRegistry? _instanceRegistry;
    private readonly UserCapabilityMappings? _userMappings;

    // Lazily initialized engines
    private CategorizationEngine? _categorizationEngine;
    private EntityExtractionEngine? _entityExtractionEngine;
    private PIIDetectionEngine? _piiDetectionEngine;
    private SentimentEngine? _sentimentEngine;
    private DuplicateDetectionEngine? _duplicateDetectionEngine;
    private SummarizationEngine? _summarizationEngine;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticsCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve AI providers by name.</param>
    /// <param name="vectorStore">Optional vector store for embedding operations.</param>
    /// <param name="knowledgeGraph">Optional knowledge graph for relationship operations.</param>
    /// <param name="instanceRegistry">Optional instance capability registry for capability checking.</param>
    /// <param name="userMappings">Optional user capability mappings for provider resolution.</param>
    public SemanticsCapabilityHandler(
        Func<string, IExtendedAIProvider?>? providerResolver = null,
        IVectorStore? vectorStore = null,
        IKnowledgeGraph? knowledgeGraph = null,
        InstanceCapabilityRegistry? instanceRegistry = null,
        UserCapabilityMappings? userMappings = null)
    {
        _providerResolver = providerResolver;
        _vectorStore = vectorStore;
        _knowledgeGraph = knowledgeGraph;
        _instanceRegistry = instanceRegistry;
        _userMappings = userMappings;
    }

    #region ICapabilityHandler Implementation

    /// <inheritdoc/>
    public string CapabilityDomain => "Semantics";

    /// <inheritdoc/>
    public string DisplayName => "Semantic Analysis";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => _supportedCapabilities;

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        if (instanceConfig == null)
            return false;

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability) ||
            instanceConfig.DisabledCapabilities.Contains(CapabilityDomain))
            return false;

        // Check if the capability domain is configured
        return instanceConfig.CapabilityProviderMappings.ContainsKey(capability) ||
               instanceConfig.CapabilityProviderMappings.ContainsKey(CapabilityDomain);
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        if (instanceConfig == null)
            return null;

        // Check for specific capability mapping first
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var specificProvider))
            return specificProvider;

        // Fall back to domain-level mapping
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out var domainProvider))
            return domainProvider;

        return null;
    }

    #endregion

    #region ICapabilityHandlerEx Implementation

    /// <inheritdoc/>
    public AICapability Capability => AICapability.Semantics;

    /// <inheritdoc/>
    public IReadOnlySet<AISubCapability> SubCapabilities => _subCapabilities;

    /// <inheritdoc/>
    public Task<CapabilityHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        // Check if we have any provider configured
        var hasProvider = _providerResolver != null ||
                          (_userMappings != null && _instanceRegistry != null);

        if (!hasProvider)
        {
            return Task.FromResult(CapabilityHealthResult.Unhealthy(
                "No provider configuration available"));
        }

        // Engines have statistical fallbacks, so always considered healthy
        return Task.FromResult(CapabilityHealthResult.Healthy("Semantics"));
    }

    /// <inheritdoc/>
    public decimal EstimateCost(AISubCapability subCapability, int estimatedInputTokens, int estimatedOutputTokens)
    {
        // Semantic operations typically use input tokens only for embedding
        // Cost per 1K tokens (approximate, varies by provider)
        const decimal costPer1KInputTokens = 0.0001m;
        const decimal costPer1KOutputTokens = 0.0003m;

        return (estimatedInputTokens / 1000m * costPer1KInputTokens) +
               (estimatedOutputTokens / 1000m * costPer1KOutputTokens);
    }

    /// <inheritdoc/>
    public string? GetRecommendedProvider(AISubCapability subCapability)
    {
        // Semantic operations work well with most providers
        // OpenAI has strong embeddings, Anthropic has strong analysis
        return subCapability switch
        {
            AISubCapability.SemanticsDuplicateDetection or
            AISubCapability.SemanticsSimilarity => "openai", // Best embeddings
            AISubCapability.SemanticsRelationshipExtraction or
            AISubCapability.SemanticsCategorization => "anthropic", // Best analysis
            _ => null // No specific recommendation
        };
    }

    #endregion

    #region Registry-Based Capability Checking

    /// <summary>
    /// Checks if the Semantics capability is enabled at the instance level using the registry.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>True if Semantics capability is enabled.</returns>
    public bool IsCapabilityEnabledViaRegistry(string? instanceId = null)
    {
        if (_instanceRegistry == null)
            return true; // Default to enabled if no registry

        return _instanceRegistry.IsCapabilityEnabled(AICapability.Semantics, instanceId);
    }

    /// <summary>
    /// Checks if a specific sub-capability is enabled at the instance level.
    /// Respects the ChildrenMirrorParent setting.
    /// </summary>
    /// <param name="subCapability">The sub-capability to check.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>True if the sub-capability is enabled.</returns>
    public bool IsSubCapabilityEnabled(AISubCapability subCapability, string? instanceId = null)
    {
        if (_instanceRegistry == null)
            return true; // Default to enabled if no registry

        // First check if parent Semantics capability is enabled
        if (!_instanceRegistry.IsCapabilityEnabled(AICapability.Semantics, instanceId))
            return false;

        // If ChildrenMirrorParent is true, all sub-capabilities are enabled when parent is enabled
        if (_instanceRegistry.GetChildrenMirrorParent(instanceId))
            return true;

        // Otherwise check the specific sub-capability
        return _instanceRegistry.IsSubCapabilityEnabled(subCapability, instanceId);
    }

    /// <summary>
    /// Checks if a capability (by string name) is enabled, converting to the appropriate sub-capability.
    /// </summary>
    /// <param name="capabilityName">The capability name (e.g., "Categorization").</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>True if the capability is enabled.</returns>
    public bool IsCapabilityNameEnabled(string capabilityName, string? instanceId = null)
    {
        if (_instanceRegistry == null)
            return true;

        if (!IsCapabilityEnabledViaRegistry(instanceId))
            return false;

        // If ChildrenMirrorParent is true, all capabilities are enabled
        if (_instanceRegistry.GetChildrenMirrorParent(instanceId))
            return true;

        // Map string capability to sub-capability and check
        if (_capabilityToSubCapability.TryGetValue(capabilityName, out var subCapability))
        {
            return _instanceRegistry.IsSubCapabilityEnabled(subCapability, instanceId);
        }

        // Unknown capability - default to enabled if parent is enabled
        return true;
    }

    /// <summary>
    /// Resolves the provider for a user and capability using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="subCapability">The sub-capability for provider resolution.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>Resolution result with provider or error.</returns>
    public ProviderResolutionResult ResolveProviderViaRegistry(
        string userId,
        AISubCapability? subCapability = null,
        string? instanceId = null)
    {
        if (_userMappings == null)
        {
            return ProviderResolutionResult.Failed(
                ProviderResolutionError.NoProviderConfigured,
                "User capability mappings not configured");
        }

        return _userMappings.ResolveProvider(
            userId,
            AICapability.Semantics,
            subCapability,
            instanceId);
    }

    /// <summary>
    /// Gets the AI provider for a user via the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="capabilityName">The capability name for resolution.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>The AI provider, or null if not available.</returns>
    private IExtendedAIProvider? GetProviderViaRegistry(string userId, string capabilityName, string? instanceId = null)
    {
        if (_userMappings == null || _providerResolver == null)
            return null;

        // Map capability name to sub-capability
        AISubCapability? subCapability = _capabilityToSubCapability.TryGetValue(capabilityName, out var sc) ? sc : null;

        var resolution = ResolveProviderViaRegistry(userId, subCapability, instanceId);
        if (!resolution.Success || resolution.Provider == null)
            return null;

        return _providerResolver(resolution.Provider.Name);
    }

    #endregion

    /// <summary>
    /// Gets the AI provider for a specific capability and instance configuration.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="capability">The specific capability.</param>
    /// <returns>The AI provider, or null if not available.</returns>
    private IExtendedAIProvider? GetProvider(InstanceCapabilityConfig instanceConfig, string capability)
    {
        if (_providerResolver == null)
            return null;

        var providerName = GetProviderForCapability(instanceConfig, capability);
        return providerName != null ? _providerResolver(providerName) : null;
    }

    #region Categorization

    /// <summary>
    /// Categorizes content using AI or keyword-based methods.
    /// Uses the registry system for capability checking and provider resolution.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text">Text to categorize.</param>
    /// <param name="options">Categorization options.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Categorization result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<CategorizationResult>> CategorizeAsync(
        string userId,
        string text,
        CategorizationOptions? options = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        // Check capability enabled via registry
        if (!IsCapabilityNameEnabled(CapabilityCategorization, instanceId))
            return CapabilityResult<CategorizationResult>.Disabled(CapabilityCategorization);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new CategorizationOptions();

        try
        {
            // Resolve provider via registry
            var provider = GetProviderViaRegistry(userId, CapabilityCategorization, instanceId);
            var engine = GetCategorizationEngine(provider);

            var result = await engine.CategorizeAsync(text, options, ct);
            sw.Stop();

            return CapabilityResult<CategorizationResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to statistical method
            try
            {
                var engine = GetCategorizationEngine(null);
                var result = await engine.CategorizeAsync(text, options with { PreferAI = false }, ct);
                return CapabilityResult<CategorizationResult>.Ok(
                    result,
                    "StatisticalFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<CategorizationResult>.Fail(
                    $"Categorization failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Categorizes content using AI or keyword-based methods.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text">Text to categorize.</param>
    /// <param name="options">Categorization options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Categorization result.</returns>
    public async Task<CapabilityResult<CategorizationResult>> CategorizeAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        CategorizationOptions? options = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilityCategorization))
            return CapabilityResult<CategorizationResult>.Disabled(CapabilityCategorization);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new CategorizationOptions();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilityCategorization);
            var engine = GetCategorizationEngine(provider);

            var result = await engine.CategorizeAsync(text, options, ct);
            sw.Stop();

            return CapabilityResult<CategorizationResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<CategorizationResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Trains a category with example documents.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="categoryId">Category ID to train.</param>
    /// <param name="examples">Example documents.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success or failure.</returns>
    public async Task<CapabilityResult<bool>> TrainCategoryAsync(
        InstanceCapabilityConfig instanceConfig,
        string categoryId,
        IEnumerable<string> examples,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilityCategorization))
            return CapabilityResult<bool>.Disabled(CapabilityCategorization);

        try
        {
            var provider = GetProvider(instanceConfig, CapabilityCategorization);
            var engine = GetCategorizationEngine(provider);

            await engine.TrainCategoryAsync(categoryId, examples, ct);
            return CapabilityResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return CapabilityResult<bool>.Fail(ex.Message);
        }
    }

    private CategorizationEngine GetCategorizationEngine(IExtendedAIProvider? provider)
    {
        // Create new engine if provider changed or not initialized
        if (_categorizationEngine == null)
        {
            _categorizationEngine = new CategorizationEngine(provider, _vectorStore);
        }
        else if (provider != null)
        {
            _categorizationEngine.SetProvider(provider);
        }

        return _categorizationEngine;
    }

    #endregion

    #region Entity Extraction

    /// <summary>
    /// Extracts named entities from text using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="options">Extraction options.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Entity extraction result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<EntityExtractionResult>> ExtractEntitiesAsync(
        string userId,
        string text,
        EntityExtractionOptions? options = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilityEntityExtraction, instanceId))
            return CapabilityResult<EntityExtractionResult>.Disabled(CapabilityEntityExtraction);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new EntityExtractionOptions();

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilityEntityExtraction, instanceId);
            var engine = GetEntityExtractionEngine(provider);

            var result = await engine.ExtractAsync(text, options, ct);
            sw.Stop();

            return CapabilityResult<EntityExtractionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to pattern-based extraction
            try
            {
                var engine = GetEntityExtractionEngine(null);
                var result = await engine.ExtractAsync(text, options with { PreferAI = false }, ct);
                return CapabilityResult<EntityExtractionResult>.Ok(
                    result,
                    "PatternFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<EntityExtractionResult>.Fail(
                    $"Entity extraction failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Extracts named entities from text.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="options">Extraction options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Entity extraction result.</returns>
    public async Task<CapabilityResult<EntityExtractionResult>> ExtractEntitiesAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        EntityExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilityEntityExtraction))
            return CapabilityResult<EntityExtractionResult>.Disabled(CapabilityEntityExtraction);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new EntityExtractionOptions();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilityEntityExtraction);
            var engine = GetEntityExtractionEngine(provider);

            var result = await engine.ExtractAsync(text, options, ct);
            sw.Stop();

            return CapabilityResult<EntityExtractionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<EntityExtractionResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Registers a custom entity type.
    /// </summary>
    /// <param name="entityType">Custom entity type definition.</param>
    public void RegisterCustomEntityType(CustomEntityType entityType)
    {
        var engine = GetEntityExtractionEngine(null);
        engine.RegisterCustomType(entityType);
    }

    private EntityExtractionEngine GetEntityExtractionEngine(IExtendedAIProvider? provider)
    {
        if (_entityExtractionEngine == null)
        {
            _entityExtractionEngine = new EntityExtractionEngine(provider);
        }
        else if (provider != null)
        {
            _entityExtractionEngine.SetProvider(provider);
        }

        return _entityExtractionEngine;
    }

    #endregion

    #region PII Detection

    /// <summary>
    /// Detects personally identifiable information (PII) in text using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="config">Detection configuration.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PII detection result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<PIIDetectionResult>> DetectPIIAsync(
        string userId,
        string text,
        PIIDetectionConfig? config = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilityPIIDetection, instanceId))
            return CapabilityResult<PIIDetectionResult>.Disabled(CapabilityPIIDetection);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new PIIDetectionConfig();

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilityPIIDetection, instanceId);
            var engine = GetPIIDetectionEngine(provider);

            var result = await engine.DetectAsync(text, config, ct);
            sw.Stop();

            return CapabilityResult<PIIDetectionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to pattern-based detection (always available)
            try
            {
                var engine = GetPIIDetectionEngine(null);
                var result = await engine.DetectAsync(text, config with { EnableAI = false }, ct);
                return CapabilityResult<PIIDetectionResult>.Ok(
                    result,
                    "PatternFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<PIIDetectionResult>.Fail(
                    $"PII detection failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Detects personally identifiable information (PII) in text.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="config">Detection configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PII detection result.</returns>
    public async Task<CapabilityResult<PIIDetectionResult>> DetectPIIAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        PIIDetectionConfig? config = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilityPIIDetection))
            return CapabilityResult<PIIDetectionResult>.Disabled(CapabilityPIIDetection);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new PIIDetectionConfig();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilityPIIDetection);
            var engine = GetPIIDetectionEngine(provider);

            var result = await engine.DetectAsync(text, config, ct);
            sw.Stop();

            return CapabilityResult<PIIDetectionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<PIIDetectionResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Redacts PII from text using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text">Text to redact.</param>
    /// <param name="config">Detection configuration.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Redacted text result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<PIIRedactionResult>> RedactPIIAsync(
        string userId,
        string text,
        PIIDetectionConfig? config = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilityPIIDetection, instanceId))
            return CapabilityResult<PIIRedactionResult>.Disabled(CapabilityPIIDetection);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new PIIDetectionConfig { GenerateRedactions = true };

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilityPIIDetection, instanceId);
            var engine = GetPIIDetectionEngine(provider);

            var result = await engine.RedactAsync(text, config, ct);
            sw.Stop();

            return CapabilityResult<PIIRedactionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to pattern-based redaction
            try
            {
                var engine = GetPIIDetectionEngine(null);
                var result = await engine.RedactAsync(text, config with { EnableAI = false }, ct);
                return CapabilityResult<PIIRedactionResult>.Ok(
                    result,
                    "PatternFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<PIIRedactionResult>.Fail(
                    $"PII redaction failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Redacts PII from text.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text">Text to redact.</param>
    /// <param name="config">Detection configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Redacted text result.</returns>
    public async Task<CapabilityResult<PIIRedactionResult>> RedactPIIAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        PIIDetectionConfig? config = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilityPIIDetection))
            return CapabilityResult<PIIRedactionResult>.Disabled(CapabilityPIIDetection);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new PIIDetectionConfig();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilityPIIDetection);
            var engine = GetPIIDetectionEngine(provider);

            var result = await engine.RedactAsync(text, config, ct);
            sw.Stop();

            return CapabilityResult<PIIRedactionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<PIIRedactionResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Registers a custom PII pattern.
    /// </summary>
    /// <param name="pattern">Custom PII pattern definition.</param>
    public void RegisterCustomPIIPattern(CustomPIIPattern pattern)
    {
        var engine = GetPIIDetectionEngine(null);
        engine.RegisterCustomPattern(pattern);
    }

    private PIIDetectionEngine GetPIIDetectionEngine(IExtendedAIProvider? provider)
    {
        if (_piiDetectionEngine == null)
        {
            _piiDetectionEngine = new PIIDetectionEngine(provider);
        }
        else if (provider != null)
        {
            _piiDetectionEngine.SetProvider(provider);
        }

        return _piiDetectionEngine;
    }

    #endregion

    #region Sentiment Analysis

    /// <summary>
    /// Analyzes sentiment of text using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="config">Analysis configuration.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sentiment analysis result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<SentimentAnalysisResult>> AnalyzeSentimentAsync(
        string userId,
        string text,
        SentimentAnalysisConfig? config = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilitySentimentAnalysis, instanceId))
            return CapabilityResult<SentimentAnalysisResult>.Disabled(CapabilitySentimentAnalysis);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new SentimentAnalysisConfig();

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilitySentimentAnalysis, instanceId);
            var engine = GetSentimentEngine(provider);

            var result = await engine.AnalyzeAsync(text, config, ct);
            sw.Stop();

            return CapabilityResult<SentimentAnalysisResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to lexicon-based analysis
            try
            {
                var engine = GetSentimentEngine(null);
                var result = await engine.AnalyzeAsync(text, config with { PreferAI = false }, ct);
                return CapabilityResult<SentimentAnalysisResult>.Ok(
                    result,
                    "LexiconFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<SentimentAnalysisResult>.Fail(
                    $"Sentiment analysis failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Analyzes sentiment of text.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="config">Analysis configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sentiment analysis result.</returns>
    public async Task<CapabilityResult<SentimentAnalysisResult>> AnalyzeSentimentAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        SentimentAnalysisConfig? config = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilitySentimentAnalysis))
            return CapabilityResult<SentimentAnalysisResult>.Disabled(CapabilitySentimentAnalysis);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        config ??= new SentimentAnalysisConfig();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilitySentimentAnalysis);
            var engine = GetSentimentEngine(provider);

            var result = await engine.AnalyzeAsync(text, config, ct);
            sw.Stop();

            return CapabilityResult<SentimentAnalysisResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<SentimentAnalysisResult>.Fail(ex.Message);
        }
    }

    private SentimentEngine GetSentimentEngine(IExtendedAIProvider? provider)
    {
        if (_sentimentEngine == null)
        {
            _sentimentEngine = new SentimentEngine(provider);
        }
        else if (provider != null)
        {
            _sentimentEngine.SetProvider(provider);
        }

        return _sentimentEngine;
    }

    #endregion

    #region Duplicate Detection

    /// <summary>
    /// Detects duplicate or near-duplicate content using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="text">Text to check.</param>
    /// <param name="corpus">Corpus to check against.</param>
    /// <param name="options">Detection options.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Duplicate detection result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<DuplicateDetectionResult>> DetectDuplicatesAsync(
        string userId,
        string documentId,
        string text,
        IEnumerable<(string Id, string Text, float[]? Embedding)> corpus,
        DuplicateDetectionOptions? options = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilityDuplicateDetection, instanceId))
            return CapabilityResult<DuplicateDetectionResult>.Disabled(CapabilityDuplicateDetection);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new DuplicateDetectionOptions();

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilityDuplicateDetection, instanceId);
            var engine = GetDuplicateDetectionEngine(provider);

            var resultEx = await engine.DetectDuplicatesAsync(documentId, text, corpus, options, ct);
            sw.Stop();

            var result = new DuplicateDetectionResult
            {
                DocumentId = resultEx.DocumentId,
                HasDuplicates = resultEx.HasDuplicates,
                Duplicates = resultEx.Duplicates,
                Duration = resultEx.Duration
            };

            return CapabilityResult<DuplicateDetectionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to hash-based detection
            try
            {
                var engine = GetDuplicateDetectionEngine(null);
                var resultEx = await engine.DetectDuplicatesAsync(
                    documentId, text, corpus,
                    options with { EnableSemanticSimilarity = false },
                    ct);
                var result = new DuplicateDetectionResult
                {
                    DocumentId = resultEx.DocumentId,
                    HasDuplicates = resultEx.HasDuplicates,
                    Duplicates = resultEx.Duplicates,
                    Duration = resultEx.Duration
                };
                return CapabilityResult<DuplicateDetectionResult>.Ok(
                    result,
                    "HashFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<DuplicateDetectionResult>.Fail(
                    $"Duplicate detection failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Detects duplicate or near-duplicate content.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="text">Text to check.</param>
    /// <param name="corpus">Corpus to check against.</param>
    /// <param name="options">Detection options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Duplicate detection result.</returns>
    public async Task<CapabilityResult<DuplicateDetectionResult>> DetectDuplicatesAsync(
        InstanceCapabilityConfig instanceConfig,
        string documentId,
        string text,
        IEnumerable<(string Id, string Text, float[]? Embedding)> corpus,
        DuplicateDetectionOptions? options = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilityDuplicateDetection))
            return CapabilityResult<DuplicateDetectionResult>.Disabled(CapabilityDuplicateDetection);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new DuplicateDetectionOptions();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilityDuplicateDetection);
            var engine = GetDuplicateDetectionEngine(provider);

            var resultEx = await engine.DetectDuplicatesAsync(documentId, text, corpus, options, ct);
            sw.Stop();

            var result = new DuplicateDetectionResult
            {
                DocumentId = resultEx.DocumentId,
                HasDuplicates = resultEx.HasDuplicates,
                Duplicates = resultEx.Duplicates,
                Duration = resultEx.Duration
            };

            return CapabilityResult<DuplicateDetectionResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<DuplicateDetectionResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Computes similarity between two texts using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text1">First text.</param>
    /// <param name="text2">Second text.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Similarity score (0-1) wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<float>> ComputeSimilarityAsync(
        string userId,
        string text1,
        string text2,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilityDuplicateDetection, instanceId))
            return CapabilityResult<float>.Disabled(CapabilityDuplicateDetection);

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilityDuplicateDetection, instanceId);
            var engine = GetDuplicateDetectionEngine(provider);

            var similarity = await engine.ComputeSimilarityAsync(text1, text2, ct);
            return CapabilityResult<float>.Ok(similarity, provider?.GetType().Name);
        }
        catch (Exception ex)
        {
            // Fall back to hash-based similarity (no embeddings)
            try
            {
                var engine = GetDuplicateDetectionEngine(null);
                var similarity = await engine.ComputeSimilarityAsync(text1, text2, ct);
                return CapabilityResult<float>.Ok(similarity, "HashFallback");
            }
            catch
            {
                return CapabilityResult<float>.Fail(
                    $"Similarity computation failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Computes similarity between two texts.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text1">First text.</param>
    /// <param name="text2">Second text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Similarity score (0-1).</returns>
    public async Task<CapabilityResult<float>> ComputeSimilarityAsync(
        InstanceCapabilityConfig instanceConfig,
        string text1,
        string text2,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilityDuplicateDetection))
            return CapabilityResult<float>.Disabled(CapabilityDuplicateDetection);

        try
        {
            var provider = GetProvider(instanceConfig, CapabilityDuplicateDetection);
            var engine = GetDuplicateDetectionEngine(provider);

            var similarity = await engine.ComputeSimilarityAsync(text1, text2, ct);
            return CapabilityResult<float>.Ok(similarity, provider?.GetType().Name);
        }
        catch (Exception ex)
        {
            return CapabilityResult<float>.Fail(ex.Message);
        }
    }

    private DuplicateDetectionEngine GetDuplicateDetectionEngine(IExtendedAIProvider? provider)
    {
        if (_duplicateDetectionEngine == null)
        {
            _duplicateDetectionEngine = new DuplicateDetectionEngine(provider, null);
        }
        else if (provider != null)
        {
            _duplicateDetectionEngine.SetProvider(provider);
        }

        return _duplicateDetectionEngine;
    }

    #endregion

    #region Summarization

    /// <summary>
    /// Generates a summary of text content using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text">Text to summarize.</param>
    /// <param name="options">Summarization options.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summarization result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<SummarizationResult>> SummarizeAsync(
        string userId,
        string text,
        SummarizationOptions? options = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilitySummarization, instanceId))
            return CapabilityResult<SummarizationResult>.Disabled(CapabilitySummarization);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new SummarizationOptions();

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilitySummarization, instanceId);
            var engine = GetSummarizationEngine(provider);

            var result = await engine.SummarizeAsync(text, options, ct);
            sw.Stop();

            return CapabilityResult<SummarizationResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to extractive summarization
            try
            {
                var engine = GetSummarizationEngine(null);
                var result = await engine.SummarizeAsync(
                    text,
                    options with { PreferAbstractive = false },
                    ct);
                return CapabilityResult<SummarizationResult>.Ok(
                    result,
                    "ExtractiveFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<SummarizationResult>.Fail(
                    $"Summarization failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Generates a summary of text content.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text">Text to summarize.</param>
    /// <param name="options">Summarization options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summarization result.</returns>
    public async Task<CapabilityResult<SummarizationResult>> SummarizeAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        SummarizationOptions? options = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilitySummarization))
            return CapabilityResult<SummarizationResult>.Disabled(CapabilitySummarization);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new SummarizationOptions();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilitySummarization);
            var engine = GetSummarizationEngine(provider);

            var result = await engine.SummarizeAsync(text, options, ct);
            sw.Stop();

            return CapabilityResult<SummarizationResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<SummarizationResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Summarizes multiple documents together using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="documents">Documents to summarize.</param>
    /// <param name="options">Summarization options.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Combined summarization result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<SummarizationResult>> SummarizeMultipleAsync(
        string userId,
        IEnumerable<string> documents,
        SummarizationOptions? options = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityNameEnabled(CapabilitySummarization, instanceId))
            return CapabilityResult<SummarizationResult>.Disabled(CapabilitySummarization);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new SummarizationOptions();

        try
        {
            var provider = GetProviderViaRegistry(userId, CapabilitySummarization, instanceId);
            var engine = GetSummarizationEngine(provider);

            var documentList = documents.Select((text, index) => new DocumentForSummarization
            {
                Id = $"doc_{index}",
                Content = text
            }).ToList();

            var multiDocResult = await engine.SummarizeMultipleAsync(documentList, options, ct);
            sw.Stop();

            var result = new SummarizationResult
            {
                Summary = multiDocResult.OverallSummary,
                Type = SummarizationType.MultiDocument,
                OriginalLength = multiDocResult.TotalOriginalLength,
                SummaryLength = multiDocResult.OverallSummary.Length,
                CompressionRatio = multiDocResult.TotalOriginalLength > 0
                    ? (float)multiDocResult.OverallSummary.Length / multiDocResult.TotalOriginalLength
                    : 0f,
                MainTopics = multiDocResult.CommonThemes,
                UsedAI = provider != null,
                Duration = multiDocResult.Duration
            };

            return CapabilityResult<SummarizationResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Fall back to extractive multi-doc summarization
            try
            {
                var engine = GetSummarizationEngine(null);
                var documentList = documents.Select((text, index) => new DocumentForSummarization
                {
                    Id = $"doc_{index}",
                    Content = text
                }).ToList();
                var multiDocResult = await engine.SummarizeMultipleAsync(
                    documentList,
                    options with { PreferAbstractive = false },
                    ct);
                var result = new SummarizationResult
                {
                    Summary = multiDocResult.OverallSummary,
                    Type = SummarizationType.MultiDocument,
                    OriginalLength = multiDocResult.TotalOriginalLength,
                    SummaryLength = multiDocResult.OverallSummary.Length,
                    CompressionRatio = multiDocResult.TotalOriginalLength > 0
                        ? (float)multiDocResult.OverallSummary.Length / multiDocResult.TotalOriginalLength
                        : 0f,
                    MainTopics = multiDocResult.CommonThemes,
                    UsedAI = false,
                    Duration = multiDocResult.Duration
                };
                return CapabilityResult<SummarizationResult>.Ok(
                    result,
                    "ExtractiveFallback",
                    duration: sw.Elapsed);
            }
            catch
            {
                return CapabilityResult<SummarizationResult>.Fail(
                    $"Multi-document summarization failed: {ex.Message}",
                    "OPERATION_FAILED");
            }
        }
    }

    /// <summary>
    /// Summarizes multiple documents together.
    /// Uses the legacy InstanceCapabilityConfig approach.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="documents">Documents to summarize.</param>
    /// <param name="options">Summarization options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Combined summarization result.</returns>
    public async Task<CapabilityResult<SummarizationResult>> SummarizeMultipleAsync(
        InstanceCapabilityConfig instanceConfig,
        IEnumerable<string> documents,
        SummarizationOptions? options = null,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, CapabilitySummarization))
            return CapabilityResult<SummarizationResult>.Disabled(CapabilitySummarization);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new SummarizationOptions();

        try
        {
            var provider = GetProvider(instanceConfig, CapabilitySummarization);
            var engine = GetSummarizationEngine(provider);

            var documentList = documents.Select((text, index) => new DocumentForSummarization
            {
                Id = $"doc_{index}",
                Content = text
            }).ToList();

            var multiDocResult = await engine.SummarizeMultipleAsync(documentList, options, ct);
            sw.Stop();

            var result = new SummarizationResult
            {
                Summary = multiDocResult.OverallSummary,
                Type = SummarizationType.MultiDocument,
                OriginalLength = multiDocResult.TotalOriginalLength,
                SummaryLength = multiDocResult.OverallSummary.Length,
                CompressionRatio = multiDocResult.TotalOriginalLength > 0
                    ? (float)multiDocResult.OverallSummary.Length / multiDocResult.TotalOriginalLength
                    : 0f,
                MainTopics = multiDocResult.CommonThemes,
                UsedAI = provider != null,
                Duration = multiDocResult.Duration
            };

            return CapabilityResult<SummarizationResult>.Ok(
                result,
                provider?.GetType().Name,
                duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CapabilityResult<SummarizationResult>.Fail(ex.Message);
        }
    }

    private SummarizationEngine GetSummarizationEngine(IExtendedAIProvider? provider)
    {
        if (_summarizationEngine == null)
        {
            _summarizationEngine = new SummarizationEngine(provider);
        }
        else if (provider != null)
        {
            _summarizationEngine.SetProvider(provider);
        }

        return _summarizationEngine;
    }

    #endregion

    #region Comprehensive Analysis

    /// <summary>
    /// Performs comprehensive semantic analysis including multiple capabilities.
    /// </summary>
    /// <param name="instanceConfig">Instance configuration.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="options">Analysis options specifying which capabilities to include.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comprehensive semantic analysis result.</returns>
    public async Task<CapabilityResult<ComprehensiveSemanticResult>> AnalyzeComprehensiveAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        ComprehensiveAnalysisOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new ComprehensiveAnalysisOptions();

        var result = new ComprehensiveSemanticResult { OriginalText = text };
        var tasks = new List<Task>();

        // Run enabled analyses in parallel
        if (options.IncludeCategorization && IsCapabilityEnabled(instanceConfig, CapabilityCategorization))
        {
            tasks.Add(Task.Run(async () =>
            {
                var catResult = await CategorizeAsync(instanceConfig, text, options.CategorizationOptions, ct);
                if (catResult.Success && catResult.Data != null)
                    result.Categorization = catResult.Data;
            }, ct));
        }

        if (options.IncludeEntities && IsCapabilityEnabled(instanceConfig, CapabilityEntityExtraction))
        {
            tasks.Add(Task.Run(async () =>
            {
                var entResult = await ExtractEntitiesAsync(instanceConfig, text, options.EntityExtractionOptions, ct);
                if (entResult.Success && entResult.Data != null)
                    result.Entities = entResult.Data;
            }, ct));
        }

        if (options.IncludePII && IsCapabilityEnabled(instanceConfig, CapabilityPIIDetection))
        {
            tasks.Add(Task.Run(async () =>
            {
                var piiResult = await DetectPIIAsync(instanceConfig, text, options.PIIDetectionConfig, ct);
                if (piiResult.Success && piiResult.Data != null)
                    result.PII = piiResult.Data;
            }, ct));
        }

        if (options.IncludeSentiment && IsCapabilityEnabled(instanceConfig, CapabilitySentimentAnalysis))
        {
            tasks.Add(Task.Run(async () =>
            {
                var sentResult = await AnalyzeSentimentAsync(instanceConfig, text, options.SentimentConfig, ct);
                if (sentResult.Success && sentResult.Data != null)
                    result.Sentiment = sentResult.Data;
            }, ct));
        }

        if (options.IncludeSummary && IsCapabilityEnabled(instanceConfig, CapabilitySummarization))
        {
            tasks.Add(Task.Run(async () =>
            {
                var sumResult = await SummarizeAsync(instanceConfig, text, options.SummarizationOptions, ct);
                if (sumResult.Success && sumResult.Data != null)
                    result.Summary = sumResult.Data;
            }, ct));
        }

        await Task.WhenAll(tasks);
        sw.Stop();
        result.Duration = sw.Elapsed;

        return CapabilityResult<ComprehensiveSemanticResult>.Ok(result, duration: sw.Elapsed);
    }

    /// <summary>
    /// Performs comprehensive semantic analysis using the registry system.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="text">Text to analyze.</param>
    /// <param name="options">Analysis options specifying which capabilities to include.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comprehensive semantic analysis result wrapped in CapabilityResult.</returns>
    public async Task<CapabilityResult<ComprehensiveSemanticResult>> AnalyzeComprehensiveAsync(
        string userId,
        string text,
        ComprehensiveAnalysisOptions? options = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        // Check if Semantics capability is enabled via registry
        if (!IsCapabilityEnabledViaRegistry(instanceId))
            return CapabilityResult<ComprehensiveSemanticResult>.Disabled("Semantics");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        options ??= new ComprehensiveAnalysisOptions();

        var result = new ComprehensiveSemanticResult { OriginalText = text };
        var tasks = new List<Task>();

        // Run enabled analyses in parallel, checking sub-capabilities via registry
        if (options.IncludeCategorization && IsCapabilityNameEnabled(CapabilityCategorization, instanceId))
        {
            tasks.Add(Task.Run(async () =>
            {
                var catResult = await CategorizeAsync(userId, text, options.CategorizationOptions, instanceId, ct);
                if (catResult.Success && catResult.Data != null)
                    result.Categorization = catResult.Data;
            }, ct));
        }

        if (options.IncludeEntities && IsCapabilityNameEnabled(CapabilityEntityExtraction, instanceId))
        {
            tasks.Add(Task.Run(async () =>
            {
                var entResult = await ExtractEntitiesAsync(userId, text, options.EntityExtractionOptions, instanceId, ct);
                if (entResult.Success && entResult.Data != null)
                    result.Entities = entResult.Data;
            }, ct));
        }

        if (options.IncludePII && IsCapabilityNameEnabled(CapabilityPIIDetection, instanceId))
        {
            tasks.Add(Task.Run(async () =>
            {
                var piiResult = await DetectPIIAsync(userId, text, options.PIIDetectionConfig, instanceId, ct);
                if (piiResult.Success && piiResult.Data != null)
                    result.PII = piiResult.Data;
            }, ct));
        }

        if (options.IncludeSentiment && IsCapabilityNameEnabled(CapabilitySentimentAnalysis, instanceId))
        {
            tasks.Add(Task.Run(async () =>
            {
                var sentResult = await AnalyzeSentimentAsync(userId, text, options.SentimentConfig, instanceId, ct);
                if (sentResult.Success && sentResult.Data != null)
                    result.Sentiment = sentResult.Data;
            }, ct));
        }

        if (options.IncludeSummary && IsCapabilityNameEnabled(CapabilitySummarization, instanceId))
        {
            tasks.Add(Task.Run(async () =>
            {
                var sumResult = await SummarizeAsync(userId, text, options.SummarizationOptions, instanceId, ct);
                if (sumResult.Success && sumResult.Data != null)
                    result.Summary = sumResult.Data;
            }, ct));
        }

        await Task.WhenAll(tasks);
        sw.Stop();
        result.Duration = sw.Elapsed;

        return CapabilityResult<ComprehensiveSemanticResult>.Ok(result, duration: sw.Elapsed);
    }

    #endregion
}

/// <summary>
/// Options for comprehensive semantic analysis.
/// </summary>
public class ComprehensiveAnalysisOptions
{
    /// <summary>Include categorization in analysis.</summary>
    public bool IncludeCategorization { get; init; } = true;

    /// <summary>Include entity extraction in analysis.</summary>
    public bool IncludeEntities { get; init; } = true;

    /// <summary>Include PII detection in analysis.</summary>
    public bool IncludePII { get; init; } = true;

    /// <summary>Include sentiment analysis.</summary>
    public bool IncludeSentiment { get; init; } = true;

    /// <summary>Include summarization.</summary>
    public bool IncludeSummary { get; init; } = true;

    /// <summary>Options for categorization.</summary>
    public CategorizationOptions? CategorizationOptions { get; init; }

    /// <summary>Options for entity extraction.</summary>
    public EntityExtractionOptions? EntityExtractionOptions { get; init; }

    /// <summary>Configuration for PII detection.</summary>
    public PIIDetectionConfig? PIIDetectionConfig { get; init; }

    /// <summary>Configuration for sentiment analysis.</summary>
    public SentimentAnalysisConfig? SentimentConfig { get; init; }

    /// <summary>Options for summarization.</summary>
    public SummarizationOptions? SummarizationOptions { get; init; }
}

/// <summary>
/// Result of comprehensive semantic analysis.
/// </summary>
public class ComprehensiveSemanticResult
{
    /// <summary>Original text analyzed.</summary>
    public string OriginalText { get; init; } = string.Empty;

    /// <summary>Categorization result.</summary>
    public CategorizationResult? Categorization { get; set; }

    /// <summary>Entity extraction result.</summary>
    public EntityExtractionResult? Entities { get; set; }

    /// <summary>PII detection result.</summary>
    public PIIDetectionResult? PII { get; set; }

    /// <summary>Sentiment analysis result.</summary>
    public SentimentAnalysisResult? Sentiment { get; set; }

    /// <summary>Summarization result.</summary>
    public SummarizationResult? Summary { get; set; }

    /// <summary>Total processing duration.</summary>
    public TimeSpan Duration { get; set; }
}
