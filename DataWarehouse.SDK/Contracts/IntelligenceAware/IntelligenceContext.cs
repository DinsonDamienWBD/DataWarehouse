using System;
using System.Collections.Generic;
using System.Threading;

namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    /// <summary>
    /// Context object for passing AI state and configuration through operations.
    /// Provides a standardized way to convey Intelligence settings, constraints,
    /// and preferences to AI-powered operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IntelligenceContext"/> serves as a carrier for AI-related metadata
    /// that flows through the plugin pipeline. It enables:
    /// </para>
    /// <list type="bullet">
    ///   <item>Passing user preferences for AI behavior</item>
    ///   <item>Setting resource constraints (tokens, timeouts)</item>
    ///   <item>Specifying required capabilities</item>
    ///   <item>Tracking AI operation provenance</item>
    ///   <item>Enabling/disabling specific AI features per-operation</item>
    /// </list>
    /// <para>
    /// The context is immutable by design. Use the fluent <c>With*</c> methods
    /// to create modified copies for specific operations.
    /// </para>
    /// </remarks>
    public sealed record IntelligenceContext
    {
        /// <summary>
        /// Gets the unique identifier for this context instance.
        /// </summary>
        /// <remarks>
        /// Used for tracking and correlating AI operations across the system.
        /// </remarks>
        public string ContextId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Gets when this context was created.
        /// </summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets the correlation ID for tracing related operations.
        /// </summary>
        /// <remarks>
        /// When set, this ID links multiple AI operations that belong to
        /// the same logical request or workflow.
        /// </remarks>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Gets the user or agent ID initiating the AI operation.
        /// </summary>
        public string? UserId { get; init; }

        /// <summary>
        /// Gets the session ID for multi-turn conversations.
        /// </summary>
        public string? SessionId { get; init; }

        /// <summary>
        /// The identity of the principal on whose behalf this intelligence operation is performed.
        /// When an AI agent processes this operation, the AI's own privileges are IRRELEVANT.
        /// Access control ALWAYS uses Identity.EffectivePrincipalId (the original user).
        /// The AI agent should be appended to DelegationChain for audit purposes.
        /// </summary>
        public DataWarehouse.SDK.Security.CommandIdentity? Identity { get; init; }

        // ========================================
        // Capability Requirements
        // ========================================

        /// <summary>
        /// Gets the capabilities required for this operation.
        /// </summary>
        /// <remarks>
        /// If the required capabilities are not available, the operation
        /// should fail fast rather than attempt a degraded execution.
        /// </remarks>
        public IntelligenceCapabilities RequiredCapabilities { get; init; } = IntelligenceCapabilities.None;

        /// <summary>
        /// Gets the preferred capabilities for this operation.
        /// </summary>
        /// <remarks>
        /// These capabilities enhance the operation but are not strictly required.
        /// Operations proceed with degraded functionality if unavailable.
        /// </remarks>
        public IntelligenceCapabilities PreferredCapabilities { get; init; } = IntelligenceCapabilities.None;

        // ========================================
        // Resource Constraints
        // ========================================

        /// <summary>
        /// Gets the maximum number of tokens to use for AI operations.
        /// </summary>
        /// <remarks>
        /// Applies to operations like text completion and conversation.
        /// Null means no explicit limit (use provider defaults).
        /// </remarks>
        public int? MaxTokens { get; init; }

        /// <summary>
        /// Gets the timeout for AI operations.
        /// </summary>
        /// <remarks>
        /// Individual operations should respect this timeout and abort
        /// gracefully if exceeded. Defaults to 30 seconds if not specified.
        /// </remarks>
        public TimeSpan? Timeout { get; init; }

        /// <summary>
        /// Gets the temperature for AI generation (0.0-1.0).
        /// </summary>
        /// <remarks>
        /// Lower values produce more deterministic output.
        /// Higher values produce more creative/varied output.
        /// </remarks>
        public float? Temperature { get; init; }

        /// <summary>
        /// Gets the maximum cost budget for this operation.
        /// </summary>
        /// <remarks>
        /// Used to prevent runaway costs on expensive AI operations.
        /// The unit is provider-specific (typically USD or credits).
        /// </remarks>
        public decimal? MaxCost { get; init; }

        // ========================================
        // Behavior Configuration
        // ========================================

        /// <summary>
        /// Gets whether to enable caching of AI results.
        /// </summary>
        /// <remarks>
        /// When true, identical requests may return cached results
        /// to reduce latency and costs.
        /// </remarks>
        public bool EnableCaching { get; init; } = true;

        /// <summary>
        /// Gets the cache TTL for AI results.
        /// </summary>
        public TimeSpan? CacheTtl { get; init; }

        /// <summary>
        /// Gets whether to enable fallback to alternative providers.
        /// </summary>
        /// <remarks>
        /// When true, if the primary provider fails, the system
        /// automatically tries alternative providers.
        /// </remarks>
        public bool EnableFallback { get; init; } = true;

        /// <summary>
        /// Gets whether to enable streaming responses.
        /// </summary>
        /// <remarks>
        /// When true, operations that support streaming will
        /// return partial results as they become available.
        /// </remarks>
        public bool EnableStreaming { get; init; }

        /// <summary>
        /// Gets whether AI operations are allowed for this context.
        /// </summary>
        /// <remarks>
        /// Setting this to false completely bypasses AI processing.
        /// Useful for testing or when AI should be explicitly disabled.
        /// </remarks>
        public bool IntelligenceEnabled { get; init; } = true;

        /// <summary>
        /// Gets whether to include provenance information in results.
        /// </summary>
        /// <remarks>
        /// When true, results include metadata about which provider
        /// and model generated the output.
        /// </remarks>
        public bool IncludeProvenance { get; init; }

        // ========================================
        // Provider Preferences
        // ========================================

        /// <summary>
        /// Gets the preferred AI provider ID.
        /// </summary>
        /// <remarks>
        /// If specified and available, this provider is used.
        /// Falls back to alternatives if unavailable and fallback is enabled.
        /// </remarks>
        public string? PreferredProvider { get; init; }

        /// <summary>
        /// Gets the preferred model ID.
        /// </summary>
        /// <remarks>
        /// Provider-specific model identifier (e.g., "gpt-4", "claude-3-opus").
        /// </remarks>
        public string? PreferredModel { get; init; }

        /// <summary>
        /// Gets provider IDs that should be excluded from selection.
        /// </summary>
        public IReadOnlyList<string> ExcludedProviders { get; init; } = Array.Empty<string>();

        // ========================================
        // Custom Configuration
        // ========================================

        /// <summary>
        /// Gets custom metadata for provider-specific configuration.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// Gets custom tags for categorization and filtering.
        /// </summary>
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        // ========================================
        // Factory Methods
        // ========================================

        /// <summary>
        /// Creates a default Intelligence context.
        /// </summary>
        /// <returns>A new context with default settings.</returns>
        public static IntelligenceContext Default => new();

        /// <summary>
        /// Creates a context for high-performance operations.
        /// </summary>
        /// <returns>A context optimized for speed over accuracy.</returns>
        public static IntelligenceContext HighPerformance => new()
        {
            Timeout = TimeSpan.FromSeconds(5),
            EnableCaching = true,
            CacheTtl = TimeSpan.FromMinutes(5),
            Temperature = 0.0f
        };

        /// <summary>
        /// Creates a context for high-quality operations.
        /// </summary>
        /// <returns>A context optimized for accuracy over speed.</returns>
        public static IntelligenceContext HighQuality => new()
        {
            Timeout = TimeSpan.FromMinutes(2),
            EnableCaching = false,
            Temperature = 0.3f,
            IncludeProvenance = true
        };

        /// <summary>
        /// Creates a context for cost-sensitive operations.
        /// </summary>
        /// <returns>A context optimized for minimal cost.</returns>
        public static IntelligenceContext LowCost => new()
        {
            MaxTokens = 500,
            EnableCaching = true,
            CacheTtl = TimeSpan.FromHours(1),
            EnableFallback = false
        };

        /// <summary>
        /// Creates a context with AI disabled (bypass mode).
        /// </summary>
        /// <returns>A context that bypasses all AI processing.</returns>
        public static IntelligenceContext Disabled => new()
        {
            IntelligenceEnabled = false
        };

        // ========================================
        // Fluent Builder Methods
        // ========================================

        /// <summary>
        /// Creates a copy with the specified correlation ID.
        /// </summary>
        /// <param name="correlationId">The correlation ID to set.</param>
        /// <returns>A new context with the specified correlation ID.</returns>
        public IntelligenceContext WithCorrelation(string correlationId) =>
            this with { CorrelationId = correlationId };

        /// <summary>
        /// Creates a copy with the specified user ID.
        /// </summary>
        /// <param name="userId">The user ID to set.</param>
        /// <returns>A new context with the specified user ID.</returns>
        public IntelligenceContext WithUser(string userId) =>
            this with { UserId = userId };

        /// <summary>
        /// Creates a copy with the specified required capabilities.
        /// </summary>
        /// <param name="capabilities">The capabilities to require.</param>
        /// <returns>A new context with the specified requirements.</returns>
        public IntelligenceContext RequiringCapabilities(IntelligenceCapabilities capabilities) =>
            this with { RequiredCapabilities = capabilities };

        /// <summary>
        /// Creates a copy with the specified timeout.
        /// </summary>
        /// <param name="timeout">The timeout to set.</param>
        /// <returns>A new context with the specified timeout.</returns>
        public IntelligenceContext WithTimeout(TimeSpan timeout) =>
            this with { Timeout = timeout };

        /// <summary>
        /// Creates a copy with the specified provider preference.
        /// </summary>
        /// <param name="providerId">The preferred provider ID.</param>
        /// <param name="modelId">Optional preferred model ID.</param>
        /// <returns>A new context with the specified preferences.</returns>
        public IntelligenceContext PreferringProvider(string providerId, string? modelId = null) =>
            this with { PreferredProvider = providerId, PreferredModel = modelId };

        /// <summary>
        /// Creates a copy with additional metadata.
        /// </summary>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The metadata value.</param>
        /// <returns>A new context with the additional metadata.</returns>
        public IntelligenceContext WithMetadata(string key, object value)
        {
            var newMetadata = new Dictionary<string, object>(Metadata) { [key] = value };
            return this with { Metadata = newMetadata };
        }

        /// <summary>
        /// Creates a child context for a sub-operation.
        /// </summary>
        /// <returns>A new context that inherits settings but has a new ID.</returns>
        public IntelligenceContext CreateChild() =>
            this with
            {
                ContextId = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
                CorrelationId = CorrelationId ?? ContextId
            };

        /// <summary>
        /// AUTH-11 (CVSS 3.8): Validates that the identity is present and valid for AI agent operations.
        /// Throws if identity is null or missing required fields when an AI operation requires identity.
        /// Call this at operation boundaries before executing AI agent actions.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when Identity is null or the identity does not contain a valid principal.
        /// </exception>
        public void ValidateIdentityForAiOperation()
        {
            if (Identity == null)
            {
                throw new InvalidOperationException(
                    "AI agent operation requires a valid CommandIdentity. " +
                    "Identity is null -- ensure the IntelligenceContext is created with a proper identity chain (AUTH-11).");
            }

            if (string.IsNullOrWhiteSpace(Identity.OnBehalfOfPrincipalId))
            {
                throw new InvalidOperationException(
                    "AI agent operation requires a valid OnBehalfOfPrincipalId. " +
                    "The principal ID is empty -- the originating user identity must be established (AUTH-11).");
            }

            if (Identity.ActorType == DataWarehouse.SDK.Security.ActorType.AiAgent &&
                string.IsNullOrWhiteSpace(Identity.ActorId))
            {
                throw new InvalidOperationException(
                    "AI agent actor must have a non-empty ActorId for audit trail purposes (AUTH-11).");
            }
        }

        /// <summary>
        /// AUTH-11: Creates a copy with a validated identity, ensuring the identity is not null.
        /// </summary>
        /// <param name="identity">The CommandIdentity to set. Must not be null.</param>
        /// <returns>A new context with the specified identity.</returns>
        /// <exception cref="ArgumentNullException">Thrown when identity is null.</exception>
        public IntelligenceContext WithIdentity(DataWarehouse.SDK.Security.CommandIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);
            return this with { Identity = identity };
        }
    }
}
