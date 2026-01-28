// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using DataWarehouse.Plugins.AIAgents.Capabilities;

namespace DataWarehouse.Plugins.AIAgents.Models;

/// <summary>
/// Represents a registered AI provider with its configuration and capabilities.
/// </summary>
public sealed record RegisteredProvider
{
    /// <summary>
    /// Unique name for this provider registration (e.g., "my-openai", "work-claude").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Provider type identifier (e.g., "openai", "anthropic", "google", "ollama").
    /// </summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// Display name for the provider.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// API key for the provider (encrypted at rest).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Custom endpoint URL (for Azure OpenAI, self-hosted models, etc.).
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Default model to use for this provider.
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// Organization ID (for OpenAI).
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// Additional provider-specific settings.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Settings { get; init; }

    /// <summary>
    /// When this provider was registered.
    /// </summary>
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this provider was last used.
    /// </summary>
    public DateTime? LastUsedAt { get; init; }

    /// <summary>
    /// Whether this provider is currently active.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Capabilities supported by this provider.
    /// </summary>
    public AICapability SupportedCapabilities { get; init; }

    /// <summary>
    /// Priority for fallback ordering (lower = higher priority).
    /// </summary>
    public int Priority { get; init; } = 100;
}

/// <summary>
/// Represents a capability-to-provider mapping for a user.
/// </summary>
public sealed record CapabilityMapping
{
    /// <summary>
    /// The capability being mapped.
    /// </summary>
    public required AICapability Capability { get; init; }

    /// <summary>
    /// Optional sub-capability for more granular mapping.
    /// If null, applies to all sub-capabilities of the parent capability.
    /// </summary>
    public AISubCapability? SubCapability { get; init; }

    /// <summary>
    /// Name of the registered provider to use.
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// Specific model to use (overrides provider default).
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    /// When this mapping was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this mapping was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>
    /// Whether this mapping is active.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Custom settings for this mapping.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Settings { get; init; }
}

/// <summary>
/// Represents the complete capability configuration for a user.
/// </summary>
public sealed class UserCapabilityConfiguration
{
    /// <summary>
    /// User identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Registered providers for this user.
    /// Key: provider name
    /// </summary>
    public ConcurrentDictionary<string, RegisteredProvider> Providers { get; init; } = new();

    /// <summary>
    /// Capability-to-provider mappings.
    /// Key: capability identifier (e.g., "Chat", "Chat.ChatStreaming")
    /// </summary>
    public ConcurrentDictionary<string, CapabilityMapping> Mappings { get; init; } = new();

    /// <summary>
    /// User-specific capability overrides (enable/disable).
    /// </summary>
    public ConcurrentDictionary<string, bool> CapabilityOverrides { get; init; } = new();

    /// <summary>
    /// Default provider for capabilities without explicit mapping.
    /// </summary>
    public string? DefaultProvider { get; init; }

    /// <summary>
    /// When this configuration was last updated.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Configuration version for optimistic concurrency.
    /// </summary>
    public long Version { get; set; } = 1;
}

/// <summary>
/// Represents the instance-level capability configuration (admin-controlled).
/// </summary>
public sealed class InstanceCapabilityConfiguration
{
    /// <summary>
    /// Instance identifier.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Set of enabled capabilities at the instance level.
    /// Only these capabilities are available to users.
    /// </summary>
    public HashSet<AICapability> EnabledCapabilities { get; init; } = new()
    {
        // Default capabilities enabled for new instances
        AICapability.Chat,
        AICapability.Generation,
        AICapability.Embeddings
    };

    /// <summary>
    /// Set of enabled sub-capabilities.
    /// If empty, all sub-capabilities of enabled capabilities are allowed.
    /// </summary>
    public HashSet<AISubCapability> EnabledSubCapabilities { get; init; } = new();

    /// <summary>
    /// Set of explicitly disabled sub-capabilities.
    /// </summary>
    public HashSet<AISubCapability> DisabledSubCapabilities { get; init; } = new();

    /// <summary>
    /// Global rate limits per capability.
    /// </summary>
    public Dictionary<AICapability, RateLimitConfig> RateLimits { get; init; } = new();

    /// <summary>
    /// List of allowed provider types at instance level.
    /// Empty means all providers are allowed.
    /// </summary>
    public HashSet<string> AllowedProviderTypes { get; init; } = new();

    /// <summary>
    /// List of blocked provider types at instance level.
    /// </summary>
    public HashSet<string> BlockedProviderTypes { get; init; } = new();

    /// <summary>
    /// Whether to allow BYOK (Bring Your Own Key) for this instance.
    /// </summary>
    public bool AllowBYOK { get; init; } = true;

    /// <summary>
    /// Default system-wide provider for users who haven't configured their own.
    /// </summary>
    public string? DefaultSystemProvider { get; init; }

    /// <summary>
    /// When this configuration was last updated.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who last updated this configuration.
    /// </summary>
    public string? LastUpdatedBy { get; set; }

    /// <summary>
    /// Configuration version for optimistic concurrency.
    /// </summary>
    public long Version { get; set; } = 1;
}

/// <summary>
/// Rate limit configuration for a capability.
/// </summary>
public sealed record RateLimitConfig
{
    /// <summary>
    /// Maximum requests per minute.
    /// </summary>
    public int RequestsPerMinute { get; init; } = 60;

    /// <summary>
    /// Maximum tokens per minute.
    /// </summary>
    public int TokensPerMinute { get; init; } = 100000;

    /// <summary>
    /// Maximum requests per day.
    /// </summary>
    public int RequestsPerDay { get; init; } = 10000;

    /// <summary>
    /// Maximum tokens per day.
    /// </summary>
    public int TokensPerDay { get; init; } = 1000000;

    /// <summary>
    /// Maximum concurrent requests.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 10;

    /// <summary>
    /// Burst allowance (requests above limit briefly allowed).
    /// </summary>
    public int BurstAllowance { get; init; } = 10;
}

/// <summary>
/// Result of resolving a provider for a capability request.
/// </summary>
public sealed record ProviderResolutionResult
{
    /// <summary>
    /// Whether resolution was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The resolved provider, if successful.
    /// </summary>
    public RegisteredProvider? Provider { get; init; }

    /// <summary>
    /// The model to use.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Error message if resolution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Error code if resolution failed.
    /// </summary>
    public ProviderResolutionError? ErrorCode { get; init; }

    /// <summary>
    /// Whether a fallback provider was used.
    /// </summary>
    public bool IsFallback { get; init; }

    /// <summary>
    /// Original provider name that failed (if fallback was used).
    /// </summary>
    public string? OriginalProviderName { get; init; }

    /// <summary>
    /// Creates a successful resolution result.
    /// </summary>
    public static ProviderResolutionResult Resolved(RegisteredProvider provider, string? model = null)
        => new()
        {
            Success = true,
            Provider = provider,
            Model = model ?? provider.DefaultModel
        };

    /// <summary>
    /// Creates a failed resolution result.
    /// </summary>
    public static ProviderResolutionResult Failed(ProviderResolutionError error, string message)
        => new()
        {
            Success = false,
            ErrorCode = error,
            ErrorMessage = message
        };

    /// <summary>
    /// Creates a fallback resolution result.
    /// </summary>
    public static ProviderResolutionResult Fallback(RegisteredProvider provider, string originalProvider, string? model = null)
        => new()
        {
            Success = true,
            Provider = provider,
            Model = model ?? provider.DefaultModel,
            IsFallback = true,
            OriginalProviderName = originalProvider
        };
}

/// <summary>
/// Error codes for provider resolution failures.
/// </summary>
public enum ProviderResolutionError
{
    /// <summary>The requested capability is disabled at the instance level.</summary>
    CapabilityDisabled,

    /// <summary>No provider is configured for this capability.</summary>
    NoProviderConfigured,

    /// <summary>The configured provider is not available.</summary>
    ProviderUnavailable,

    /// <summary>The configured provider does not support the required capability.</summary>
    ProviderDoesNotSupportCapability,

    /// <summary>The user is not authorized to use this capability.</summary>
    NotAuthorized,

    /// <summary>Rate limit exceeded.</summary>
    RateLimitExceeded,

    /// <summary>The provider type is blocked at the instance level.</summary>
    ProviderTypeBlocked,

    /// <summary>BYOK is not allowed but no system provider is configured.</summary>
    BYOKNotAllowed,

    /// <summary>API key is invalid or expired.</summary>
    InvalidApiKey,

    /// <summary>Unknown error.</summary>
    Unknown
}

/// <summary>
/// Event raised when a capability configuration changes.
/// </summary>
public sealed record CapabilityConfigurationChangedEvent
{
    /// <summary>
    /// Type of configuration that changed.
    /// </summary>
    public required ConfigurationChangeType ChangeType { get; init; }

    /// <summary>
    /// Entity ID (user ID or instance ID).
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// What was changed.
    /// </summary>
    public required string ChangeDescription { get; init; }

    /// <summary>
    /// Previous value (for auditing).
    /// </summary>
    public string? PreviousValue { get; init; }

    /// <summary>
    /// New value (for auditing).
    /// </summary>
    public string? NewValue { get; init; }

    /// <summary>
    /// Who made the change.
    /// </summary>
    public required string ChangedBy { get; init; }

    /// <summary>
    /// When the change occurred.
    /// </summary>
    public DateTime ChangedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Type of configuration change.
/// </summary>
public enum ConfigurationChangeType
{
    /// <summary>Instance capability enabled/disabled.</summary>
    InstanceCapability,

    /// <summary>User provider registered/unregistered.</summary>
    UserProvider,

    /// <summary>User capability mapping changed.</summary>
    UserMapping,

    /// <summary>Rate limit changed.</summary>
    RateLimit,

    /// <summary>Provider type blocked/unblocked.</summary>
    ProviderTypeBlock
}

/// <summary>
/// Statistics for capability usage.
/// </summary>
public sealed record CapabilityUsageStats
{
    /// <summary>
    /// Capability this stat is for.
    /// </summary>
    public AICapability Capability { get; init; }

    /// <summary>
    /// Sub-capability if applicable.
    /// </summary>
    public AISubCapability? SubCapability { get; init; }

    /// <summary>
    /// Total requests made.
    /// </summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// Successful requests.
    /// </summary>
    public long SuccessfulRequests { get; init; }

    /// <summary>
    /// Failed requests.
    /// </summary>
    public long FailedRequests { get; init; }

    /// <summary>
    /// Total input tokens used.
    /// </summary>
    public long TotalInputTokens { get; init; }

    /// <summary>
    /// Total output tokens used.
    /// </summary>
    public long TotalOutputTokens { get; init; }

    /// <summary>
    /// Total estimated cost in USD.
    /// </summary>
    public decimal TotalCostUsd { get; init; }

    /// <summary>
    /// Average latency in milliseconds.
    /// </summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>
    /// P95 latency in milliseconds.
    /// </summary>
    public double P95LatencyMs { get; init; }

    /// <summary>
    /// P99 latency in milliseconds.
    /// </summary>
    public double P99LatencyMs { get; init; }

    /// <summary>
    /// Period start for these stats.
    /// </summary>
    public DateTime PeriodStart { get; init; }

    /// <summary>
    /// Period end for these stats.
    /// </summary>
    public DateTime PeriodEnd { get; init; }
}

/// <summary>
/// Request to execute a capability operation.
/// </summary>
public sealed record CapabilityRequest
{
    /// <summary>
    /// User making the request.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Capability being requested.
    /// </summary>
    public required AICapability Capability { get; init; }

    /// <summary>
    /// Sub-capability being requested.
    /// </summary>
    public AISubCapability? SubCapability { get; init; }

    /// <summary>
    /// Request payload (capability-specific).
    /// </summary>
    public required object Payload { get; init; }

    /// <summary>
    /// Optional specific provider to use.
    /// </summary>
    public string? PreferredProvider { get; init; }

    /// <summary>
    /// Optional specific model to use.
    /// </summary>
    public string? PreferredModel { get; init; }

    /// <summary>
    /// Request correlation ID for tracing.
    /// </summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// When the request was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Additional request options.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Options { get; init; }
}

/// <summary>
/// Response from a capability operation.
/// </summary>
/// <typeparam name="T">Type of the result data.</typeparam>
public sealed record CapabilityResponse<T>
{
    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Result data if successful.
    /// </summary>
    public T? Data { get; init; }

    /// <summary>
    /// Error information if not successful.
    /// </summary>
    public CapabilityError? Error { get; init; }

    /// <summary>
    /// Provider that handled the request.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Model used for the request.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Token usage information.
    /// </summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// Request duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Request ID for correlation.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Whether a fallback provider was used.
    /// </summary>
    public bool UsedFallback { get; init; }

    /// <summary>
    /// Creates a successful response.
    /// </summary>
    public static CapabilityResponse<T> Ok(
        T data,
        string requestId,
        string? provider = null,
        string? model = null,
        TokenUsage? usage = null,
        TimeSpan? duration = null)
        => new()
        {
            Success = true,
            Data = data,
            RequestId = requestId,
            Provider = provider,
            Model = model,
            Usage = usage,
            Duration = duration ?? TimeSpan.Zero
        };

    /// <summary>
    /// Creates a failed response.
    /// </summary>
    public static CapabilityResponse<T> Fail(
        string requestId,
        CapabilityError error,
        TimeSpan? duration = null)
        => new()
        {
            Success = false,
            Error = error,
            RequestId = requestId,
            Duration = duration ?? TimeSpan.Zero
        };
}

/// <summary>
/// Error information from a capability operation.
/// </summary>
public sealed record CapabilityError
{
    /// <summary>
    /// Error code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Additional error details.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Details { get; init; }

    /// <summary>
    /// Inner error from the provider.
    /// </summary>
    public string? ProviderError { get; init; }

    /// <summary>
    /// Whether the error is retryable.
    /// </summary>
    public bool IsRetryable { get; init; }

    /// <summary>
    /// Suggested retry delay if retryable.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Common error: capability disabled.
    /// </summary>
    public static CapabilityError CapabilityDisabled(string capability)
        => new()
        {
            Code = "CAPABILITY_DISABLED",
            Message = $"The capability '{capability}' is disabled for this instance.",
            IsRetryable = false
        };

    /// <summary>
    /// Common error: no provider configured.
    /// </summary>
    public static CapabilityError NoProvider(string capability)
        => new()
        {
            Code = "NO_PROVIDER",
            Message = $"No provider is configured for capability '{capability}'.",
            IsRetryable = false
        };

    /// <summary>
    /// Common error: provider unavailable.
    /// </summary>
    public static CapabilityError ProviderUnavailable(string provider, string? details = null)
        => new()
        {
            Code = "PROVIDER_UNAVAILABLE",
            Message = $"Provider '{provider}' is currently unavailable.",
            ProviderError = details,
            IsRetryable = true,
            RetryAfter = TimeSpan.FromSeconds(30)
        };

    /// <summary>
    /// Common error: rate limit exceeded.
    /// </summary>
    public static CapabilityError RateLimitExceeded(TimeSpan? retryAfter = null)
        => new()
        {
            Code = "RATE_LIMIT_EXCEEDED",
            Message = "Rate limit exceeded. Please try again later.",
            IsRetryable = true,
            RetryAfter = retryAfter ?? TimeSpan.FromMinutes(1)
        };
}

/// <summary>
/// User-specific provider configuration for BYOK (Bring Your Own Key) scenarios.
/// </summary>
public sealed record UserProviderConfig
{
    /// <summary>
    /// User identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Provider type (e.g., "openai", "anthropic", "google").
    /// </summary>
    public required string ProviderType { get; init; }

    /// <summary>
    /// User-assigned name for this provider configuration.
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// API key for the provider (should be encrypted at rest).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Custom endpoint URL (for Azure OpenAI, self-hosted models, etc.).
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Default model to use with this provider.
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// Organization ID (for OpenAI).
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// Region (for AWS Bedrock, etc.).
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Project ID (for Google Cloud).
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// Whether this provider configuration is active.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// When this configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this configuration was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>
    /// When this provider was last used.
    /// </summary>
    public DateTime? LastUsedAt { get; init; }

    /// <summary>
    /// Additional provider-specific options.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Options { get; init; }

    /// <summary>
    /// Priority for fallback ordering (lower = higher priority).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Maximum tokens per request for this provider.
    /// </summary>
    public int? MaxTokensPerRequest { get; init; }

    /// <summary>
    /// Monthly budget limit in USD for this provider (null = unlimited).
    /// </summary>
    public decimal? MonthlyBudgetUsd { get; init; }

    /// <summary>
    /// Current month's spending in USD.
    /// </summary>
    public decimal CurrentMonthSpendingUsd { get; init; }
}

/// <summary>
/// Capability-to-provider mapping configuration.
/// Defines which provider handles which capability for a user.
/// </summary>
public sealed record CapabilityMappingConfig
{
    /// <summary>
    /// User identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Capability being mapped.
    /// </summary>
    public required AICapability Capability { get; init; }

    /// <summary>
    /// Optional sub-capability for fine-grained mapping.
    /// </summary>
    public AISubCapability? SubCapability { get; init; }

    /// <summary>
    /// Name of the provider to use for this capability.
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// Model to use (overrides provider default).
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    /// Whether this mapping is active.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// When this mapping was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this mapping was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>
    /// Custom settings for this capability-provider combination.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Settings { get; init; }

    /// <summary>
    /// Gets the mapping key for indexing.
    /// </summary>
    public string Key => SubCapability.HasValue
        ? $"{Capability}.{SubCapability.Value}"
        : Capability.ToString();
}

/// <summary>
/// Summary of a user's AI capability configuration.
/// </summary>
public sealed record UserCapabilitySummary
{
    /// <summary>
    /// User identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Number of registered providers.
    /// </summary>
    public int RegisteredProviders { get; init; }

    /// <summary>
    /// Number of active capability mappings.
    /// </summary>
    public int ActiveMappings { get; init; }

    /// <summary>
    /// Capabilities that have mappings.
    /// </summary>
    public IReadOnlyList<AICapability> MappedCapabilities { get; init; } = Array.Empty<AICapability>();

    /// <summary>
    /// Capabilities that are enabled at instance level but not mapped.
    /// </summary>
    public IReadOnlyList<AICapability> UnmappedCapabilities { get; init; } = Array.Empty<AICapability>();

    /// <summary>
    /// Default provider name, if set.
    /// </summary>
    public string? DefaultProvider { get; init; }

    /// <summary>
    /// Total estimated monthly cost across all providers.
    /// </summary>
    public decimal TotalMonthlySpendingUsd { get; init; }

    /// <summary>
    /// When the configuration was last updated.
    /// </summary>
    public DateTime LastUpdatedAt { get; init; }
}
