// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIAgents;

namespace DataWarehouse.Plugins.AIAgents.Capabilities;

/// <summary>
/// Base interface for all AI capability handlers.
/// Each handler implements a specific AI capability domain (generation, embeddings, vision, etc.)
/// and routes requests to the appropriate provider based on user configuration.
/// </summary>
public interface ICapabilityHandler
{
    /// <summary>
    /// Gets the unique identifier for this capability domain.
    /// </summary>
    string CapabilityDomain { get; }

    /// <summary>
    /// Gets the human-readable display name for this capability.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the list of specific capabilities supported by this handler.
    /// </summary>
    IReadOnlyList<string> SupportedCapabilities { get; }

    /// <summary>
    /// Checks if a specific capability is enabled for the given instance configuration.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration to check.</param>
    /// <param name="capability">The specific capability to check.</param>
    /// <returns>True if the capability is enabled, false otherwise.</returns>
    bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability);

    /// <summary>
    /// Gets the provider name configured for a specific capability on an instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="capability">The specific capability.</param>
    /// <returns>The provider name, or null if not configured.</returns>
    string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability);
}

/// <summary>
/// Configuration for AI capabilities on a specific instance.
/// Maps capabilities to providers and tracks enabled/disabled state.
/// </summary>
public class InstanceCapabilityConfig
{
    /// <summary>
    /// Gets or sets the instance identifier.
    /// </summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the user identifier owning this instance.
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the user's quota tier.
    /// </summary>
    public QuotaTier QuotaTier { get; init; } = QuotaTier.Free;

    /// <summary>
    /// Maps capability names to their configured provider names.
    /// Key: capability (e.g., "TextGeneration", "VectorGeneration")
    /// Value: provider name (e.g., "openai", "anthropic")
    /// </summary>
    public Dictionary<string, string> CapabilityProviderMappings { get; init; } = new();

    /// <summary>
    /// Set of explicitly disabled capabilities for this instance.
    /// </summary>
    public HashSet<string> DisabledCapabilities { get; init; } = new();

    /// <summary>
    /// User-provided API keys for BYOK scenarios.
    /// Key: provider name, Value: API key
    /// </summary>
    public Dictionary<string, string>? UserApiKeys { get; init; }

    /// <summary>
    /// Additional configuration options per capability.
    /// </summary>
    public Dictionary<string, Dictionary<string, object>> CapabilityOptions { get; init; } = new();
}

/// <summary>
/// Result of a capability operation.
/// Provides graceful error handling with detailed error information.
/// </summary>
/// <typeparam name="T">The type of the result data.</typeparam>
public class CapabilityResult<T>
{
    /// <summary>
    /// Gets whether the operation was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets whether the operation was successful (alias for Success).
    /// </summary>
    public bool IsSuccess => Success;

    /// <summary>
    /// Gets the result data if successful.
    /// </summary>
    public T? Data { get; init; }

    /// <summary>
    /// Gets the result data if successful (alias for Data).
    /// </summary>
    public T? Value => Data;

    /// <summary>
    /// Gets the error message if not successful.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the error code if not successful.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Gets the provider that handled the request.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Gets the model used for the request.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets token usage information if applicable.
    /// </summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// Gets the time taken to complete the operation.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Gets whether this error is retryable.
    /// </summary>
    public bool IsRetryable { get; init; }

    /// <summary>
    /// Gets the suggested retry delay if the error is retryable.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Gets additional error details (e.g., from the provider).
    /// </summary>
    public IReadOnlyDictionary<string, object>? ErrorDetails { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="data">The result data.</param>
    /// <param name="provider">The provider that handled the request.</param>
    /// <param name="model">The model used.</param>
    /// <param name="usage">Token usage information.</param>
    /// <param name="duration">Operation duration.</param>
    /// <returns>A successful capability result.</returns>
    public static CapabilityResult<T> Ok(T data, string? provider = null, string? model = null, TokenUsage? usage = null, TimeSpan? duration = null)
        => new()
        {
            Success = true,
            Data = data,
            Provider = provider,
            Model = model,
            Usage = usage,
            Duration = duration
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="errorCode">The error code.</param>
    /// <param name="isRetryable">Whether the operation can be retried.</param>
    /// <param name="retryAfter">Suggested retry delay.</param>
    /// <returns>A failed capability result.</returns>
    public static CapabilityResult<T> Fail(
        string errorMessage,
        string? errorCode = null,
        bool isRetryable = false,
        TimeSpan? retryAfter = null)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
            IsRetryable = isRetryable,
            RetryAfter = retryAfter
        };

    /// <summary>
    /// Creates a result indicating the capability is disabled at the instance level.
    /// </summary>
    /// <param name="capability">The disabled capability name.</param>
    /// <returns>A failed capability result with CAPABILITY_DISABLED code.</returns>
    public static CapabilityResult<T> Disabled(string capability)
        => new()
        {
            Success = false,
            ErrorMessage = $"Capability '{capability}' is disabled for this instance. Contact your administrator to enable it.",
            ErrorCode = "CAPABILITY_DISABLED",
            IsRetryable = false
        };

    /// <summary>
    /// Creates a result indicating no provider is configured for the capability.
    /// </summary>
    /// <param name="capability">The capability without a provider.</param>
    /// <returns>A failed capability result with NO_PROVIDER code.</returns>
    public static CapabilityResult<T> NoProvider(string capability)
        => new()
        {
            Success = false,
            ErrorMessage = $"No provider configured for capability '{capability}'. Register a provider and set up a mapping.",
            ErrorCode = "NO_PROVIDER",
            IsRetryable = false
        };

    /// <summary>
    /// Creates a result indicating the provider is unavailable.
    /// </summary>
    /// <param name="providerName">The unavailable provider name.</param>
    /// <param name="reason">Optional reason for unavailability.</param>
    /// <returns>A failed capability result with PROVIDER_UNAVAILABLE code.</returns>
    public static CapabilityResult<T> ProviderUnavailable(string providerName, string? reason = null)
        => new()
        {
            Success = false,
            ErrorMessage = $"Provider '{providerName}' is currently unavailable." + (reason != null ? $" Reason: {reason}" : ""),
            ErrorCode = "PROVIDER_UNAVAILABLE",
            IsRetryable = true,
            RetryAfter = TimeSpan.FromSeconds(30)
        };

    /// <summary>
    /// Creates a result indicating rate limit exceeded.
    /// </summary>
    /// <param name="retryAfter">When to retry.</param>
    /// <returns>A failed capability result with RATE_LIMIT_EXCEEDED code.</returns>
    public static CapabilityResult<T> RateLimitExceeded(TimeSpan? retryAfter = null)
        => new()
        {
            Success = false,
            ErrorMessage = "Rate limit exceeded. Please try again later.",
            ErrorCode = "RATE_LIMIT_EXCEEDED",
            IsRetryable = true,
            RetryAfter = retryAfter ?? TimeSpan.FromMinutes(1)
        };

    /// <summary>
    /// Creates a result indicating invalid API key.
    /// </summary>
    /// <param name="providerName">The provider with the invalid key.</param>
    /// <returns>A failed capability result with INVALID_API_KEY code.</returns>
    public static CapabilityResult<T> InvalidApiKey(string providerName)
        => new()
        {
            Success = false,
            ErrorMessage = $"Invalid or expired API key for provider '{providerName}'. Please update your API key.",
            ErrorCode = "INVALID_API_KEY",
            IsRetryable = false
        };

    /// <summary>
    /// Creates a result indicating authorization failure.
    /// </summary>
    /// <param name="reason">The reason for authorization failure.</param>
    /// <returns>A failed capability result with NOT_AUTHORIZED code.</returns>
    public static CapabilityResult<T> NotAuthorized(string? reason = null)
        => new()
        {
            Success = false,
            ErrorMessage = reason ?? "You are not authorized to use this capability.",
            ErrorCode = "NOT_AUTHORIZED",
            IsRetryable = false
        };

    /// <summary>
    /// Converts this result to a result of a different type (for error propagation).
    /// Only valid for failed results.
    /// </summary>
    /// <typeparam name="TNew">The new result type.</typeparam>
    /// <returns>A new result with the same error information.</returns>
    /// <exception cref="InvalidOperationException">If called on a successful result.</exception>
    public CapabilityResult<TNew> ToFailedResult<TNew>()
    {
        if (Success)
            throw new InvalidOperationException("Cannot convert a successful result to a failed result.");

        return new CapabilityResult<TNew>
        {
            Success = false,
            ErrorMessage = ErrorMessage,
            ErrorCode = ErrorCode,
            IsRetryable = IsRetryable,
            RetryAfter = RetryAfter,
            ErrorDetails = ErrorDetails,
            Provider = Provider
        };
    }
}

/// <summary>
/// Token usage information for AI operations.
/// </summary>
public class TokenUsage
{
    /// <summary>
    /// Gets the number of input/prompt tokens.
    /// </summary>
    public int InputTokens { get; init; }

    /// <summary>
    /// Gets the number of output/completion tokens.
    /// </summary>
    public int OutputTokens { get; init; }

    /// <summary>
    /// Gets the total tokens used.
    /// </summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// Gets the estimated cost in USD.
    /// </summary>
    public double? EstimatedCostUsd { get; init; }
}

