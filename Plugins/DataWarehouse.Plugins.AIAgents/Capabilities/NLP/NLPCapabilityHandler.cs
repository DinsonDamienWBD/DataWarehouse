// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Diagnostics;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.NLP;

/// <summary>
/// Main NLP capability handler implementing ICapabilityHandler.
/// Provides natural language processing capabilities including query parsing,
/// intent detection, command extraction, and follow-up handling.
/// </summary>
/// <remarks>
/// <para>
/// This handler integrates with the AIAgents plugin to provide NLP capabilities
/// that can be enabled/disabled per instance. It supports:
/// </para>
/// <list type="bullet">
/// <item><description>QueryParsing - Convert natural language to structured queries</description></item>
/// <item><description>IntentDetection - Identify user intent with confidence scoring</description></item>
/// <item><description>CommandExtraction - Extract commands from text</description></item>
/// <item><description>FollowUpHandling - Handle contextual follow-ups</description></item>
/// </list>
/// <para>
/// The handler checks if NLP capability is enabled on the instance (via InstanceCapabilityConfig),
/// gets the user's mapped provider for NLP, and falls back to pattern-based processing
/// if no provider is available.
/// </para>
/// </remarks>
public sealed class NLPCapabilityHandler : ICapabilityHandler, IDisposable
{
    /// <summary>
    /// The capability domain identifier for NLP.
    /// </summary>
    public const string Domain = "NLP";

    /// <summary>
    /// Capability identifier for query parsing operations.
    /// </summary>
    public const string CapabilityQueryParsing = "QueryParsing";

    /// <summary>
    /// Capability identifier for intent detection operations.
    /// </summary>
    public const string CapabilityIntentDetection = "IntentDetection";

    /// <summary>
    /// Capability identifier for command extraction operations.
    /// </summary>
    public const string CapabilityCommandExtraction = "CommandExtraction";

    /// <summary>
    /// Capability identifier for follow-up handling operations.
    /// </summary>
    public const string CapabilityFollowUpHandling = "FollowUpHandling";

    private static readonly IReadOnlyList<string> _supportedCapabilities = new[]
    {
        CapabilityQueryParsing,
        CapabilityIntentDetection,
        CapabilityCommandExtraction,
        CapabilityFollowUpHandling
    };

    private readonly ConcurrentDictionary<string, IExtendedAIProvider> _providers;
    private readonly QueryParsingEngine _queryParser;
    private readonly ConversationContextEngine _conversationEngine;
    private readonly NLPCapabilityConfig _config;
    private bool _disposed;

    /// <inheritdoc/>
    public string CapabilityDomain => Domain;

    /// <inheritdoc/>
    public string DisplayName => "Natural Language Processing";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => _supportedCapabilities;

    /// <summary>
    /// Initializes a new instance of the <see cref="NLPCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providers">The concurrent dictionary of available AI providers.</param>
    /// <param name="config">Optional NLP capability configuration.</param>
    public NLPCapabilityHandler(
        ConcurrentDictionary<string, IExtendedAIProvider> providers,
        NLPCapabilityConfig? config = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _config = config ?? new NLPCapabilityConfig();
        _queryParser = new QueryParsingEngine(_config.QueryParsing);
        _conversationEngine = new ConversationContextEngine(_config.Conversation);
    }

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        // Check if capability is in the disabled set
        if (instanceConfig.DisabledCapabilities.Contains(capability))
        {
            return false;
        }

        // Check if the full domain.capability is disabled
        var fullCapability = $"{Domain}.{capability}";
        if (instanceConfig.DisabledCapabilities.Contains(fullCapability))
        {
            return false;
        }

        // Check quota tier restrictions
        return IsCapabilityAllowedForTier(instanceConfig.QuotaTier, capability);
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        // Try specific capability mapping first
        var fullCapability = $"{Domain}.{capability}";
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(fullCapability, out var specificProvider))
        {
            return specificProvider;
        }

        // Try domain-level mapping
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(Domain, out var domainProvider))
        {
            return domainProvider;
        }

        // Return default provider from config
        return _config.DefaultProvider;
    }

    /// <summary>
    /// Parses a natural language query into a structured command intent.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="query">The natural language query to parse.</param>
    /// <param name="sessionId">Optional session ID for context tracking.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A capability result containing the parsed command intent.</returns>
    public async Task<CapabilityResult<NLPCommandIntent>> ParseQueryAsync(
        InstanceCapabilityConfig instanceConfig,
        string query,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, CapabilityQueryParsing))
        {
            return CapabilityResult<NLPCommandIntent>.Disabled(CapabilityQueryParsing);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return CapabilityResult<NLPCommandIntent>.Fail(
                "Query cannot be empty.",
                "INVALID_INPUT");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            // Get session context if available
            var session = sessionId != null ? _conversationEngine.GetSession(sessionId) : null;
            var carryOverContext = session?.GetCarryOverContext() ?? new Dictionary<string, object?>();

            // Try to get AI provider
            var providerName = GetProviderForCapability(instanceConfig, CapabilityQueryParsing);
            var provider = GetProvider(providerName, instanceConfig);

            NLPCommandIntent result;
            TokenUsage? usage = null;

            if (provider != null && provider.IsAvailable)
            {
                // Use AI-enhanced parsing
                var (aiResult, aiUsage) = await _queryParser.ParseWithAIAsync(
                    query,
                    provider,
                    carryOverContext,
                    ct);
                result = aiResult;
                usage = aiUsage;
            }
            else
            {
                // Fall back to pattern-based parsing
                result = _queryParser.ParseWithPatterns(query, carryOverContext);
            }

            // Update session if tracking
            if (session != null)
            {
                session.AddTurn(query, result.CommandName, result.Parameters, true);
            }

            sw.Stop();
            return CapabilityResult<NLPCommandIntent>.Ok(
                result,
                providerName,
                provider?.ProviderId,
                usage,
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<NLPCommandIntent>.Fail(
                $"Query parsing failed: {ex.Message}",
                "PARSING_ERROR");
        }
    }

    /// <summary>
    /// Detects the intent from a natural language input.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="input">The natural language input to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A capability result containing the detected intent.</returns>
    public async Task<CapabilityResult<NLPDetectedIntent>> DetectIntentAsync(
        InstanceCapabilityConfig instanceConfig,
        string input,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, CapabilityIntentDetection))
        {
            return CapabilityResult<NLPDetectedIntent>.Disabled(CapabilityIntentDetection);
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return CapabilityResult<NLPDetectedIntent>.Fail(
                "Input cannot be empty.",
                "INVALID_INPUT");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var providerName = GetProviderForCapability(instanceConfig, CapabilityIntentDetection);
            var provider = GetProvider(providerName, instanceConfig);

            NLPDetectedIntent result;
            TokenUsage? usage = null;

            if (provider != null)
            {
                var (aiResult, aiUsage) = await _queryParser.DetectIntentWithAIAsync(input, provider, ct);
                result = aiResult;
                usage = aiUsage;
            }
            else
            {
                result = _queryParser.DetectIntentWithPatterns(input);
            }

            sw.Stop();
            return CapabilityResult<NLPDetectedIntent>.Ok(
                result,
                providerName,
                provider?.ProviderId,
                usage,
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<NLPDetectedIntent>.Fail(
                $"Intent detection failed: {ex.Message}",
                "DETECTION_ERROR");
        }
    }

    /// <summary>
    /// Extracts commands from natural language text.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="text">The text to extract commands from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A capability result containing the extracted commands.</returns>
    public async Task<CapabilityResult<NLPExtractedCommands>> ExtractCommandsAsync(
        InstanceCapabilityConfig instanceConfig,
        string text,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, CapabilityCommandExtraction))
        {
            return CapabilityResult<NLPExtractedCommands>.Disabled(CapabilityCommandExtraction);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return CapabilityResult<NLPExtractedCommands>.Fail(
                "Text cannot be empty.",
                "INVALID_INPUT");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var providerName = GetProviderForCapability(instanceConfig, CapabilityCommandExtraction);
            var provider = GetProvider(providerName, instanceConfig);

            NLPExtractedCommands result;
            TokenUsage? usage = null;

            if (provider != null)
            {
                var (aiResult, aiUsage) = await _queryParser.ExtractCommandsWithAIAsync(text, provider, ct);
                result = aiResult;
                usage = aiUsage;
            }
            else
            {
                result = _queryParser.ExtractCommandsWithPatterns(text);
            }

            sw.Stop();
            return CapabilityResult<NLPExtractedCommands>.Ok(
                result,
                providerName,
                provider?.ProviderId,
                usage,
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<NLPExtractedCommands>.Fail(
                $"Command extraction failed: {ex.Message}",
                "EXTRACTION_ERROR");
        }
    }

    /// <summary>
    /// Handles a follow-up query in a conversational context.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="sessionId">The session ID for context tracking.</param>
    /// <param name="followUpQuery">The follow-up query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A capability result containing the resolved query.</returns>
    public async Task<CapabilityResult<NLPFollowUpResult>> HandleFollowUpAsync(
        InstanceCapabilityConfig instanceConfig,
        string sessionId,
        string followUpQuery,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, CapabilityFollowUpHandling))
        {
            return CapabilityResult<NLPFollowUpResult>.Disabled(CapabilityFollowUpHandling);
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return CapabilityResult<NLPFollowUpResult>.Fail(
                "Session ID is required for follow-up handling.",
                "INVALID_SESSION");
        }

        if (string.IsNullOrWhiteSpace(followUpQuery))
        {
            return CapabilityResult<NLPFollowUpResult>.Fail(
                "Follow-up query cannot be empty.",
                "INVALID_INPUT");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var session = _conversationEngine.GetSession(sessionId);
            if (session == null)
            {
                return CapabilityResult<NLPFollowUpResult>.Fail(
                    "Session not found. Start a new conversation.",
                    "SESSION_NOT_FOUND");
            }

            var providerName = GetProviderForCapability(instanceConfig, CapabilityFollowUpHandling);
            var provider = GetProvider(providerName, instanceConfig);

            var result = await _conversationEngine.ProcessFollowUpAsync(
                session,
                followUpQuery,
                provider,
                ct);

            sw.Stop();
            return CapabilityResult<NLPFollowUpResult>.Ok(
                result,
                providerName,
                provider?.ProviderId,
                result.TokenUsage,
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<NLPFollowUpResult>.Fail(
                $"Follow-up handling failed: {ex.Message}",
                "FOLLOWUP_ERROR");
        }
    }

    /// <summary>
    /// Creates or retrieves a conversation session.
    /// </summary>
    /// <param name="sessionId">Optional session ID. If null, a new session is created.</param>
    /// <returns>The session information.</returns>
    public NLPSessionInfo GetOrCreateSession(string? sessionId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var session = _conversationEngine.GetOrCreateSession(sessionId);
        return new NLPSessionInfo
        {
            SessionId = session.SessionId,
            CreatedAt = session.CreatedAt,
            TurnCount = session.Turns.Count,
            LastCommandName = session.LastCommandName,
            IsActive = true
        };
    }

    /// <summary>
    /// Clears a session's context.
    /// </summary>
    /// <param name="sessionId">The session ID to clear.</param>
    /// <returns>True if the session was found and cleared.</returns>
    public bool ClearSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _conversationEngine.ClearSessionContext(sessionId);
    }

    /// <summary>
    /// Ends a session.
    /// </summary>
    /// <param name="sessionId">The session ID to end.</param>
    /// <returns>True if the session was found and ended.</returns>
    public bool EndSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _conversationEngine.EndSession(sessionId);
    }

    /// <summary>
    /// Gets completions/suggestions for partial input.
    /// </summary>
    /// <param name="partialInput">The partial input to complete.</param>
    /// <param name="maxSuggestions">Maximum number of suggestions to return.</param>
    /// <returns>List of completion suggestions.</returns>
    public IEnumerable<string> GetCompletions(string partialInput, int maxSuggestions = 10)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _queryParser.GetCompletions(partialInput, maxSuggestions);
    }

    /// <summary>
    /// Gets statistics about the NLP handler's learning store.
    /// </summary>
    /// <returns>Learning statistics.</returns>
    public NLPLearningStats GetLearningStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _queryParser.GetLearningStats();
    }

    /// <summary>
    /// Records a user correction for learning.
    /// </summary>
    /// <param name="input">The original input.</param>
    /// <param name="incorrectCommand">What was incorrectly interpreted.</param>
    /// <param name="correctCommand">What it should have been.</param>
    /// <param name="correctParameters">Correct parameters if any.</param>
    public void RecordCorrection(
        string input,
        string incorrectCommand,
        string correctCommand,
        Dictionary<string, object?>? correctParameters = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _queryParser.RecordCorrection(input, incorrectCommand, correctCommand, correctParameters);
    }

    private IExtendedAIProvider? GetProvider(string? providerName, InstanceCapabilityConfig instanceConfig)
    {
        if (string.IsNullOrEmpty(providerName))
        {
            return null;
        }

        if (!_providers.TryGetValue(providerName, out var provider))
        {
            return null;
        }

        // For BYOK, check if user has provided their own API key
        // This would need to be handled at a higher level when the provider is created
        return provider;
    }

    private static bool IsCapabilityAllowedForTier(QuotaTier tier, string capability)
    {
        // All tiers can use pattern-based NLP
        // AI-enhanced NLP requires Basic tier or higher
        return tier switch
        {
            QuotaTier.Free => true, // Pattern-based always available
            QuotaTier.Basic => true,
            QuotaTier.Pro => true,
            QuotaTier.Enterprise => true,
            QuotaTier.BringYourOwnKey => true,
            _ => true
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queryParser.Dispose();
        _conversationEngine.Dispose();
    }
}

/// <summary>
/// Configuration for the NLP capability handler.
/// </summary>
public sealed class NLPCapabilityConfig
{
    /// <summary>
    /// Gets or sets the default provider to use for NLP operations.
    /// </summary>
    public string? DefaultProvider { get; init; }

    /// <summary>
    /// Gets or sets the query parsing engine configuration.
    /// </summary>
    public QueryParsingConfig QueryParsing { get; init; } = new();

    /// <summary>
    /// Gets or sets the conversation engine configuration.
    /// </summary>
    public ConversationConfig Conversation { get; init; } = new();
}

/// <summary>
/// Represents a parsed command intent from natural language.
/// </summary>
public sealed record NLPCommandIntent
{
    /// <summary>Gets the resolved command name.</summary>
    public required string CommandName { get; init; }

    /// <summary>Gets the parsed parameters from the natural language input.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>Gets the confidence score (0.0 - 1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the original natural language input.</summary>
    public required string OriginalInput { get; init; }

    /// <summary>Gets an explanation of the interpretation.</summary>
    public string? Explanation { get; init; }

    /// <summary>Gets whether this was processed by AI (vs pattern matching).</summary>
    public bool ProcessedByAI { get; init; }

    /// <summary>Gets the session ID for conversational context.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets extracted entities from the input.</summary>
    public List<NLPEntity> Entities { get; init; } = new();

    /// <summary>Gets suggested follow-up actions.</summary>
    public List<string> SuggestedFollowUps { get; init; } = new();
}

/// <summary>
/// Represents a detected intent from natural language.
/// </summary>
public sealed record NLPDetectedIntent
{
    /// <summary>Gets the primary intent type.</summary>
    public required string IntentType { get; init; }

    /// <summary>Gets the sub-intent for more specific classification.</summary>
    public string? SubType { get; init; }

    /// <summary>Gets the confidence score (0.0 - 1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the original input text.</summary>
    public required string OriginalInput { get; init; }

    /// <summary>Gets extracted entities.</summary>
    public List<NLPEntity> Entities { get; init; } = new();

    /// <summary>Gets keywords extracted from the input.</summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>Gets whether this is a follow-up to a previous query.</summary>
    public bool IsFollowUp { get; init; }

    /// <summary>Gets the detected language code (ISO 639-1).</summary>
    public string Language { get; init; } = "en";
}

/// <summary>
/// Represents extracted commands from text.
/// </summary>
public sealed record NLPExtractedCommands
{
    /// <summary>Gets the list of extracted commands.</summary>
    public List<NLPCommandIntent> Commands { get; init; } = new();

    /// <summary>Gets the original text.</summary>
    public required string OriginalText { get; init; }

    /// <summary>Gets whether AI was used for extraction.</summary>
    public bool ProcessedByAI { get; init; }

    /// <summary>Gets any warnings during extraction.</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Represents the result of follow-up handling.
/// </summary>
public sealed record NLPFollowUpResult
{
    /// <summary>Gets whether this was recognized as a follow-up.</summary>
    public bool IsFollowUp { get; init; }

    /// <summary>Gets the resolved query with context applied.</summary>
    public required string ResolvedQuery { get; init; }

    /// <summary>Gets the resolved command intent.</summary>
    public NLPCommandIntent? ResolvedIntent { get; init; }

    /// <summary>Gets context that was applied to resolve the query.</summary>
    public Dictionary<string, object?> AppliedContext { get; init; } = new();

    /// <summary>Gets an explanation of how the query was resolved.</summary>
    public string? Explanation { get; init; }

    /// <summary>Gets token usage if AI was used.</summary>
    public TokenUsage? TokenUsage { get; init; }
}

/// <summary>
/// Represents an extracted entity from natural language.
/// </summary>
public sealed record NLPEntity
{
    /// <summary>Gets the entity type.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the entity value.</summary>
    public required string Value { get; init; }

    /// <summary>Gets the normalized value.</summary>
    public string? NormalizedValue { get; init; }

    /// <summary>Gets the confidence score.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the start position in original text.</summary>
    public int StartIndex { get; init; }

    /// <summary>Gets the end position in original text.</summary>
    public int EndIndex { get; init; }
}

/// <summary>
/// Session information for NLP conversations.
/// </summary>
public sealed record NLPSessionInfo
{
    /// <summary>Gets the session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets when the session was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets the number of turns in the conversation.</summary>
    public int TurnCount { get; init; }

    /// <summary>Gets the last command executed.</summary>
    public string? LastCommandName { get; init; }

    /// <summary>Gets whether the session is active.</summary>
    public bool IsActive { get; init; }
}

/// <summary>
/// Statistics about the NLP learning store.
/// </summary>
public sealed record NLPLearningStats
{
    /// <summary>Gets the total number of learned patterns.</summary>
    public int TotalPatterns { get; init; }

    /// <summary>Gets the number of corrections learned.</summary>
    public int CorrectionsLearned { get; init; }

    /// <summary>Gets the total successful interpretations.</summary>
    public int TotalSuccesses { get; init; }

    /// <summary>Gets the total failed interpretations.</summary>
    public int TotalFailures { get; init; }

    /// <summary>Gets the average confidence score.</summary>
    public double AverageConfidence { get; init; }

    /// <summary>Gets the number of synonyms in the store.</summary>
    public int SynonymCount { get; init; }
}
