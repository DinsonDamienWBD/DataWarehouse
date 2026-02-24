using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DataWarehouse.SDK.Utilities;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence;

#region 90.B1: Core Gateway Interfaces

/// <summary>
/// Master interface for all AI interactions within the Intelligence Gateway.
/// Provides a unified entry point for synchronous, asynchronous, and streaming AI operations.
/// </summary>
/// <remarks>
/// <para>
/// The Intelligence Gateway acts as the central hub for all AI interactions, providing:
/// </para>
/// <list type="bullet">
///   <item>Request/response operations via <see cref="ProcessRequestAsync"/></item>
///   <item>Session-based conversations via <see cref="CreateSessionAsync"/></item>
///   <item>Streaming responses via <see cref="StreamResponseAsync"/></item>
///   <item>Capability discovery via <see cref="Capabilities"/></item>
/// </list>
/// <para>
/// Implementations should be thread-safe and handle concurrent requests appropriately.
/// </para>
/// </remarks>
public interface IIntelligenceGateway
{
    /// <summary>
    /// Processes an intelligence request and returns a response.
    /// </summary>
    /// <param name="request">The intelligence request to process.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the intelligence response.</returns>
    /// <exception cref="ArgumentNullException">Thrown when request is null.</exception>
    /// <exception cref="IntelligenceException">Thrown when processing fails.</exception>
    Task<IntelligenceResponse> ProcessRequestAsync(IntelligenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates a new intelligence session for multi-turn conversations.
    /// </summary>
    /// <param name="options">Session configuration options.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the created session.</returns>
    /// <exception cref="ArgumentNullException">Thrown when options is null.</exception>
    /// <exception cref="IntelligenceException">Thrown when session creation fails.</exception>
    Task<IIntelligenceSession> CreateSessionAsync(SessionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Streams intelligence response chunks asynchronously.
    /// </summary>
    /// <param name="request">The intelligence request to process.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>An async enumerable of response chunks.</returns>
    /// <exception cref="ArgumentNullException">Thrown when request is null.</exception>
    IAsyncEnumerable<IntelligenceChunk> StreamResponseAsync(IntelligenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Gets the capabilities supported by this gateway.
    /// </summary>
    GatewayCapabilities Capabilities { get; }
}

/// <summary>
/// Session management interface for multi-turn AI conversations.
/// Maintains conversation history and state across multiple interactions.
/// </summary>
/// <remarks>
/// <para>
/// Sessions provide stateful conversation management with features including:
/// </para>
/// <list type="bullet">
///   <item>Automatic history tracking</item>
///   <item>Session state management (Active, Paused, Closed)</item>
///   <item>Context preservation across turns</item>
/// </list>
/// <para>
/// Sessions must be explicitly closed via <see cref="CloseAsync"/> to release resources.
/// Implements <see cref="IAsyncDisposable"/> for automatic cleanup in using statements.
/// </para>
/// </remarks>
public interface IIntelligenceSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this session.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// Gets the current state of the session.
    /// </summary>
    SessionState State { get; }

    /// <summary>
    /// Gets the conversation history for this session.
    /// </summary>
    IReadOnlyList<ConversationTurn> History { get; }

    /// <summary>
    /// Sends a message and receives a response within this session context.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the intelligence response.</returns>
    /// <exception cref="InvalidOperationException">Thrown when session is not active.</exception>
    /// <exception cref="ArgumentException">Thrown when message is null or empty.</exception>
    Task<IntelligenceResponse> SendAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Closes the session and releases associated resources.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task representing the close operation.</returns>
    Task CloseAsync(CancellationToken ct = default);
}

/// <summary>
/// Channel abstraction for different client interfaces (CLI, GUI, API, WebSocket).
/// Provides a unified message transport layer across different communication protocols.
/// </summary>
/// <remarks>
/// <para>
/// Channels abstract the communication protocol details, allowing the gateway to
/// work uniformly across different client types while respecting protocol-specific
/// characteristics like:
/// </para>
/// <list type="bullet">
///   <item>CLI: Line-based input/output</item>
///   <item>GUI: Event-driven messaging</item>
///   <item>API: Request/response HTTP</item>
///   <item>WebSocket: Bidirectional streaming</item>
/// </list>
/// </remarks>
public interface IIntelligenceChannel : IAsyncDisposable
{
    /// <summary>
    /// Gets the type of this channel.
    /// </summary>
    ChannelType Type { get; }

    /// <summary>
    /// Receives the next message from the channel.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the received message.</returns>
    /// <exception cref="OperationCanceledException">Thrown when operation is cancelled.</exception>
    /// <exception cref="ChannelClosedException">Thrown when channel is closed.</exception>
    Task<ChannelMessage> ReceiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a message through the channel.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task representing the send operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when message is null.</exception>
    /// <exception cref="ChannelClosedException">Thrown when channel is closed.</exception>
    Task SendAsync(ChannelMessage message, CancellationToken ct = default);

    /// <summary>
    /// Streams messages from the channel asynchronously.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>An async enumerable of channel messages.</returns>
    IAsyncEnumerable<ChannelMessage> StreamAsync(CancellationToken ct = default);
}

/// <summary>
/// Router interface for selecting appropriate AI providers based on requirements.
/// Manages provider registration, discovery, and intelligent selection.
/// </summary>
/// <remarks>
/// <para>
/// The provider router enables:
/// </para>
/// <list type="bullet">
///   <item>Dynamic provider registration and discovery</item>
///   <item>Requirement-based provider selection</item>
///   <item>Fallback and redundancy handling</item>
///   <item>Load balancing across providers</item>
/// </list>
/// </remarks>
public interface IProviderRouter
{
    /// <summary>
    /// Selects the most appropriate provider based on requirements.
    /// </summary>
    /// <param name="requirements">The requirements the provider must satisfy.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the selected provider, or null if none match.</returns>
    Task<IIntelligenceStrategy?> SelectProviderAsync(ProviderRequirements requirements, CancellationToken ct = default);

    /// <summary>
    /// Gets all available providers.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the list of available providers.</returns>
    Task<IReadOnlyList<IIntelligenceStrategy>> GetAvailableProvidersAsync(CancellationToken ct = default);

    /// <summary>
    /// Registers a provider with the router.
    /// </summary>
    /// <param name="provider">The provider to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when provider is null.</exception>
    void RegisterProvider(IIntelligenceStrategy provider);
}

#endregion

#region 90.B2: Provider Management

/// <summary>
/// Interface for managing API keys, quotas, and usage limits for AI providers.
/// Tracks subscription state and enforces usage boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Subscriptions track:
/// </para>
/// <list type="bullet">
///   <item>API authentication credentials</item>
///   <item>Quota limits and current usage</item>
///   <item>Rate limiting state</item>
///   <item>Billing information</item>
/// </list>
/// </remarks>
public interface IProviderSubscription
{
    /// <summary>
    /// Gets the provider identifier this subscription is for.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the API key for authentication.
    /// </summary>
    string ApiKey { get; }

    /// <summary>
    /// Gets the quota information for this subscription.
    /// </summary>
    QuotaInfo Quota { get; }

    /// <summary>
    /// Gets the current usage statistics.
    /// </summary>
    UsageInfo CurrentUsage { get; }

    /// <summary>
    /// Gets whether current usage is within subscription limits.
    /// </summary>
    bool IsWithinLimits { get; }

    /// <summary>
    /// Refreshes quota information from the provider.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task representing the refresh operation.</returns>
    Task RefreshQuotaAsync(CancellationToken ct = default);
}

/// <summary>
/// Interface for selecting optimal providers based on configurable criteria.
/// Supports multiple selection strategies for different use cases.
/// </summary>
/// <remarks>
/// <para>
/// Selection strategies include:
/// </para>
/// <list type="bullet">
///   <item><see cref="SelectionStrategy.Cost"/> - Minimize cost</item>
///   <item><see cref="SelectionStrategy.Performance"/> - Maximize speed</item>
///   <item><see cref="SelectionStrategy.Capability"/> - Best capability match</item>
///   <item><see cref="SelectionStrategy.RoundRobin"/> - Distribute load evenly</item>
/// </list>
/// </remarks>
public interface IProviderSelector
{
    /// <summary>
    /// Selects a provider based on the configured strategy and criteria.
    /// </summary>
    /// <param name="criteria">Selection criteria to apply.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the selected provider, or null if none match.</returns>
    Task<IIntelligenceStrategy?> SelectAsync(SelectionCriteria criteria, CancellationToken ct = default);

    /// <summary>
    /// Gets or sets the selection strategy.
    /// </summary>
    SelectionStrategy Strategy { get; set; }
}

/// <summary>
/// Interface for mapping capabilities to providers.
/// Enables capability-based provider discovery and routing.
/// </summary>
/// <remarks>
/// <para>
/// The capability router maintains a mapping of capabilities to providers,
/// enabling queries like "which providers support embeddings?" or
/// "which providers support image generation?"
/// </para>
/// </remarks>
public interface ICapabilityRouter
{
    /// <summary>
    /// Finds all providers that support the specified capability.
    /// </summary>
    /// <param name="capability">The capability to search for.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing providers with the capability.</returns>
    Task<IEnumerable<IIntelligenceStrategy>> FindProvidersWithCapabilityAsync(
        IntelligenceCapabilities capability,
        CancellationToken ct = default);

    /// <summary>
    /// Registers a capability mapping for a provider.
    /// </summary>
    /// <param name="capability">The capability being registered.</param>
    /// <param name="providerId">The provider ID that supports this capability.</param>
    void RegisterCapabilityMapping(IntelligenceCapabilities capability, string providerId);
}

#endregion

#region Supporting Types

/// <summary>
/// Intelligence request for gateway operations.
/// </summary>
public sealed class IntelligenceRequest
{
    /// <summary>
    /// Gets or sets the unique request identifier.
    /// </summary>
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the prompt or query text.
    /// </summary>
    public required string Prompt { get; set; }

    /// <summary>
    /// Gets or sets the optional system message for context.
    /// </summary>
    public string? SystemMessage { get; set; }

    /// <summary>
    /// Gets or sets the maximum tokens to generate.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets the temperature for generation.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Gets or sets the preferred provider ID.
    /// </summary>
    public string? PreferredProviderId { get; set; }

    /// <summary>
    /// Gets or sets required capabilities.
    /// </summary>
    public IntelligenceCapabilities RequiredCapabilities { get; set; } = IntelligenceCapabilities.None;

    /// <summary>
    /// Gets or sets additional request metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets whether streaming is requested.
    /// </summary>
    public bool EnableStreaming { get; set; }

    /// <summary>
    /// Gets or sets the request timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets the session ID for session-bound requests.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The identity of the principal on whose behalf this intelligence request is made.
    /// When an AI agent processes this request, the AI's own privileges are IRRELEVANT.
    /// Access control ALWAYS uses Identity.EffectivePrincipalId (the original user).
    /// The AI agent should be appended to DelegationChain for audit purposes.
    /// </summary>
    public DataWarehouse.SDK.Security.CommandIdentity? Identity { get; init; }
}

/// <summary>
/// Intelligence response from gateway operations.
/// </summary>
public sealed class IntelligenceResponse
{
    /// <summary>
    /// Gets or sets the request ID this response corresponds to.
    /// </summary>
    public required string RequestId { get; set; }

    /// <summary>
    /// Gets or sets whether the request was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the response content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider ID that handled the request.
    /// </summary>
    public string? ProviderId { get; set; }

    /// <summary>
    /// Gets or sets the model used for generation.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets token usage information.
    /// </summary>
    public TokenUsage? Usage { get; set; }

    /// <summary>
    /// Gets or sets the finish reason.
    /// </summary>
    public string? FinishReason { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the error code if applicable.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Gets or sets the response latency.
    /// </summary>
    public TimeSpan Latency { get; set; }

    /// <summary>
    /// Gets or sets the response timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets additional response metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Creates a successful response.
    /// </summary>
    public static IntelligenceResponse CreateSuccess(string requestId, string content, string? providerId = null)
    {
        return new IntelligenceResponse
        {
            RequestId = requestId,
            Success = true,
            Content = content,
            ProviderId = providerId,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a failure response.
    /// </summary>
    public static IntelligenceResponse CreateFailure(string requestId, string errorMessage, string? errorCode = null)
    {
        return new IntelligenceResponse
        {
            RequestId = requestId,
            Success = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Token usage information for AI operations.
/// </summary>
public sealed class TokenUsage
{
    /// <summary>
    /// Gets or sets the number of prompt tokens.
    /// </summary>
    public int PromptTokens { get; set; }

    /// <summary>
    /// Gets or sets the number of completion tokens.
    /// </summary>
    public int CompletionTokens { get; set; }

    /// <summary>
    /// Gets the total tokens used.
    /// </summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// Streaming response chunk.
/// </summary>
public sealed class IntelligenceChunk
{
    /// <summary>
    /// Gets or sets the chunk index.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the content delta.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is the final chunk.
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    /// Gets or sets the finish reason (for final chunks).
    /// </summary>
    public string? FinishReason { get; set; }

    /// <summary>
    /// Gets or sets token usage (for final chunks).
    /// </summary>
    public TokenUsage? Usage { get; set; }
}

/// <summary>
/// Session configuration options.
/// </summary>
public sealed class SessionOptions
{
    /// <summary>
    /// Gets or sets the optional system message for the session.
    /// </summary>
    public string? SystemMessage { get; set; }

    /// <summary>
    /// Gets or sets the preferred provider ID.
    /// </summary>
    public string? PreferredProviderId { get; set; }

    /// <summary>
    /// Gets or sets the maximum history length.
    /// </summary>
    public int MaxHistoryLength { get; set; } = 100;

    /// <summary>
    /// Gets or sets the session timeout.
    /// </summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets session metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets whether to auto-save session state.
    /// </summary>
    public bool AutoPersist { get; set; }
}

/// <summary>
/// Session state enumeration.
/// </summary>
public enum SessionState
{
    /// <summary>Session is active and accepting messages.</summary>
    Active,

    /// <summary>Session is paused temporarily.</summary>
    Paused,

    /// <summary>Session is closed and no longer usable.</summary>
    Closed,

    /// <summary>Session has expired due to timeout.</summary>
    Expired,

    /// <summary>Session encountered an error.</summary>
    Error
}

/// <summary>
/// A single turn in a conversation.
/// </summary>
public sealed class ConversationTurn
{
    /// <summary>
    /// Gets or sets the turn index.
    /// </summary>
    public int TurnIndex { get; set; }

    /// <summary>
    /// Gets or sets the role (user, assistant, system).
    /// </summary>
    public required string Role { get; set; }

    /// <summary>
    /// Gets or sets the message content.
    /// </summary>
    public required string Content { get; set; }

    /// <summary>
    /// Gets or sets the turn timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets token usage for this turn.
    /// </summary>
    public TokenUsage? Usage { get; set; }

    /// <summary>
    /// Gets or sets turn metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Channel type enumeration.
/// </summary>
public enum ChannelType
{
    /// <summary>Command-line interface channel.</summary>
    CLI,

    /// <summary>Graphical user interface channel.</summary>
    GUI,

    /// <summary>REST API channel.</summary>
    API,

    /// <summary>WebSocket bidirectional channel.</summary>
    WebSocket,

    /// <summary>gRPC channel.</summary>
    Grpc,

    /// <summary>Custom channel implementation.</summary>
    Custom
}

/// <summary>
/// Message for channel communication.
/// </summary>
public sealed class ChannelMessage
{
    /// <summary>
    /// Gets or sets the message ID.
    /// </summary>
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the message type.
    /// </summary>
    public required ChannelMessageType Type { get; set; }

    /// <summary>
    /// Gets or sets the message content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the session ID if applicable.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the message timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets message metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets binary payload if applicable.
    /// </summary>
    public byte[]? BinaryPayload { get; set; }
}

/// <summary>
/// Channel message type enumeration.
/// </summary>
public enum ChannelMessageType
{
    /// <summary>User query or input.</summary>
    Query,

    /// <summary>AI response.</summary>
    Response,

    /// <summary>Streaming chunk.</summary>
    StreamChunk,

    /// <summary>Error notification.</summary>
    Error,

    /// <summary>Control message (ping, status, etc.).</summary>
    Control,

    /// <summary>Session management message.</summary>
    SessionControl
}

/// <summary>
/// Provider requirements for selection.
/// </summary>
public sealed class ProviderRequirements
{
    /// <summary>
    /// Gets or sets required capabilities.
    /// </summary>
    public IntelligenceCapabilities RequiredCapabilities { get; set; } = IntelligenceCapabilities.None;

    /// <summary>
    /// Gets or sets maximum acceptable latency.
    /// </summary>
    public TimeSpan? MaxLatency { get; set; }

    /// <summary>
    /// Gets or sets maximum cost tier (1-5).
    /// </summary>
    public int? MaxCostTier { get; set; }

    /// <summary>
    /// Gets or sets whether offline capability is required.
    /// </summary>
    public bool RequireOffline { get; set; }

    /// <summary>
    /// Gets or sets preferred provider IDs.
    /// </summary>
    public string[]? PreferredProviders { get; set; }

    /// <summary>
    /// Gets or sets excluded provider IDs.
    /// </summary>
    public string[]? ExcludedProviders { get; set; }

    /// <summary>
    /// Gets or sets minimum expected tokens.
    /// </summary>
    public int? MinTokens { get; set; }
}

/// <summary>
/// Quota information for provider subscriptions.
/// </summary>
public sealed class QuotaInfo
{
    /// <summary>
    /// Gets or sets the total requests allowed per period.
    /// </summary>
    public long TotalRequestsPerPeriod { get; set; }

    /// <summary>
    /// Gets or sets the total tokens allowed per period.
    /// </summary>
    public long TotalTokensPerPeriod { get; set; }

    /// <summary>
    /// Gets or sets the quota period.
    /// </summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets the rate limit (requests per minute).
    /// </summary>
    public int RateLimitPerMinute { get; set; }

    /// <summary>
    /// Gets or sets the concurrent request limit.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 10;

    /// <summary>
    /// Gets or sets when the quota resets.
    /// </summary>
    public DateTimeOffset? ResetTime { get; set; }

    /// <summary>
    /// Gets or sets whether this is a free tier.
    /// </summary>
    public bool IsFreeTier { get; set; }
}

/// <summary>
/// Current usage statistics for provider subscriptions.
/// </summary>
public sealed class UsageInfo
{
    /// <summary>
    /// Gets or sets requests used this period.
    /// </summary>
    public long RequestsUsed { get; set; }

    /// <summary>
    /// Gets or sets tokens used this period.
    /// </summary>
    public long TokensUsed { get; set; }

    /// <summary>
    /// Gets or sets when usage tracking started.
    /// </summary>
    public DateTimeOffset PeriodStart { get; set; }

    /// <summary>
    /// Gets or sets when usage was last updated.
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets current active requests.
    /// </summary>
    public int ActiveRequests { get; set; }

    /// <summary>
    /// Gets or sets estimated cost for current period.
    /// </summary>
    public decimal EstimatedCost { get; set; }

    /// <summary>
    /// Gets or sets the currency for cost.
    /// </summary>
    public string CostCurrency { get; set; } = "USD";
}

/// <summary>
/// Selection criteria for provider selection.
/// </summary>
public sealed class SelectionCriteria
{
    /// <summary>
    /// Gets or sets required capabilities.
    /// </summary>
    public IntelligenceCapabilities RequiredCapabilities { get; set; } = IntelligenceCapabilities.None;

    /// <summary>
    /// Gets or sets whether to prefer low cost.
    /// </summary>
    public bool PreferLowCost { get; set; }

    /// <summary>
    /// Gets or sets whether to prefer low latency.
    /// </summary>
    public bool PreferLowLatency { get; set; }

    /// <summary>
    /// Gets or sets whether to require availability.
    /// </summary>
    public bool RequireAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum quality score.
    /// </summary>
    public double? MinQualityScore { get; set; }

    /// <summary>
    /// Gets or sets additional scoring weights.
    /// </summary>
    public Dictionary<string, double>? ScoringWeights { get; set; }
}

/// <summary>
/// Selection strategy enumeration.
/// </summary>
public enum SelectionStrategy
{
    /// <summary>Select lowest cost provider.</summary>
    Cost,

    /// <summary>Select fastest provider.</summary>
    Performance,

    /// <summary>Select best capability match.</summary>
    Capability,

    /// <summary>Round-robin across providers.</summary>
    RoundRobin,

    /// <summary>Weighted random selection.</summary>
    WeightedRandom,

    /// <summary>Select provider with most available quota.</summary>
    AvailableQuota,

    /// <summary>Custom scoring function.</summary>
    Custom
}

/// <summary>
/// Gateway capabilities descriptor.
/// </summary>
public sealed class GatewayCapabilities
{
    /// <summary>
    /// Gets or sets supported AI capabilities.
    /// </summary>
    public IntelligenceCapabilities SupportedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets supported channel types.
    /// </summary>
    public ChannelType[] SupportedChannels { get; set; } = Array.Empty<ChannelType>();

    /// <summary>
    /// Gets or sets whether streaming is supported.
    /// </summary>
    public bool SupportsStreaming { get; set; }

    /// <summary>
    /// Gets or sets whether sessions are supported.
    /// </summary>
    public bool SupportsSessions { get; set; }

    /// <summary>
    /// Gets or sets maximum concurrent sessions.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 100;

    /// <summary>
    /// Gets or sets available provider count.
    /// </summary>
    public int AvailableProviders { get; set; }

    /// <summary>
    /// Gets or sets gateway version.
    /// </summary>
    public string Version { get; set; } = "1.0.0";
}

/// <summary>
/// Exception for intelligence gateway operations.
/// </summary>
public class IntelligenceException : Exception
{
    /// <summary>
    /// Gets the error code.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Gets the provider ID if applicable.
    /// </summary>
    public string? ProviderId { get; }

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public IntelligenceException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance with error code.
    /// </summary>
    public IntelligenceException(string message, string errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance with error code and provider.
    /// </summary>
    public IntelligenceException(string message, string errorCode, string providerId)
        : base(message)
    {
        ErrorCode = errorCode;
        ProviderId = providerId;
    }

    /// <summary>
    /// Initializes a new instance with inner exception.
    /// </summary>
    public IntelligenceException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Exception for channel-related errors.
/// </summary>
public class ChannelClosedException : Exception
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public ChannelClosedException() : base("Channel is closed") { }

    /// <summary>
    /// Initializes a new instance with message.
    /// </summary>
    public ChannelClosedException(string message) : base(message) { }
}

#endregion

#region 90.B3: Base Classes

/// <summary>
/// Base implementation for intelligence gateways.
/// Provides provider registration, session management, request routing, and statistics tracking.
/// </summary>
/// <remarks>
/// <para>
/// This base class handles common gateway functionality including:
/// </para>
/// <list type="bullet">
///   <item>Provider lifecycle management</item>
///   <item>Session tracking and cleanup</item>
///   <item>Request routing to appropriate providers</item>
///   <item>Comprehensive statistics collection</item>
/// </list>
/// <para>
/// Derived classes must implement <see cref="ExecuteRequestAsync"/> for provider-specific logic.
/// </para>
/// </remarks>
public abstract class IntelligenceGatewayBase : IIntelligenceGateway, IProviderRouter, IAsyncDisposable
{
    private readonly BoundedDictionary<string, IIntelligenceStrategy> _providers = new BoundedDictionary<string, IIntelligenceStrategy>(1000);
    private readonly BoundedDictionary<string, IntelligenceSessionBase> _sessions = new BoundedDictionary<string, IntelligenceSessionBase>(1000);
    private readonly object _statsLock = new();

    private long _totalRequests;
    private long _successfulRequests;
    private long _failedRequests;
    private long _totalTokensUsed;
    private long _totalLatencyTicks;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private bool _disposed;

    /// <summary>
    /// Gets the gateway identifier.
    /// </summary>
    public abstract string GatewayId { get; }

    /// <summary>
    /// Gets the gateway name.
    /// </summary>
    public abstract string GatewayName { get; }

    /// <inheritdoc/>
    public virtual GatewayCapabilities Capabilities => new()
    {
        SupportedCapabilities = GetAggregatedCapabilities(),
        SupportedChannels = new[] { ChannelType.API },
        SupportsStreaming = true,
        SupportsSessions = true,
        MaxConcurrentSessions = 1000,
        AvailableProviders = _providers.Count(p => p.Value.IsAvailable),
        Version = "1.0.0"
    };

    /// <inheritdoc/>
    public virtual async Task<IntelligenceResponse> ProcessRequestAsync(IntelligenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var provider = await SelectProviderForRequestAsync(request, ct).ConfigureAwait(false);
            if (provider == null)
            {
                Interlocked.Increment(ref _failedRequests);
                return IntelligenceResponse.CreateFailure(request.RequestId, "No suitable provider available", "NO_PROVIDER");
            }

            var response = await ExecuteRequestAsync(provider, request, ct).ConfigureAwait(false);

            stopwatch.Stop();
            response.Latency = stopwatch.Elapsed;
            response.ProviderId = provider.StrategyId;

            if (response.Success)
            {
                RecordSuccess(stopwatch.Elapsed.TotalMilliseconds, response.Usage?.TotalTokens ?? 0);
            }
            else
            {
                RecordFailure();
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            RecordFailure();
            return IntelligenceResponse.CreateFailure(request.RequestId, ex.Message, "EXECUTION_ERROR");
        }
    }

    /// <inheritdoc/>
    public virtual async Task<IIntelligenceSession> CreateSessionAsync(SessionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sessionId = Guid.NewGuid().ToString("N");
        var provider = options.PreferredProviderId != null
            ? _providers.GetValueOrDefault(options.PreferredProviderId)
            : await SelectProviderAsync(new ProviderRequirements(), ct).ConfigureAwait(false);

        var session = CreateSessionInstance(sessionId, options, provider);
        _sessions[sessionId] = session;

        return session;
    }

    /// <inheritdoc/>
    public virtual async IAsyncEnumerable<IntelligenceChunk> StreamResponseAsync(
        IntelligenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = await SelectProviderForRequestAsync(request, ct).ConfigureAwait(false);
        if (provider == null)
        {
            yield return new IntelligenceChunk
            {
                Index = 0,
                IsFinal = true,
                FinishReason = "error"
            };
            yield break;
        }

        var index = 0;
        await foreach (var chunk in ExecuteStreamingRequestAsync(provider, request, ct).ConfigureAwait(false))
        {
            chunk.Index = index++;
            yield return chunk;
        }
    }

    /// <inheritdoc/>
    public virtual void RegisterProvider(IIntelligenceStrategy provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.StrategyId] = provider;
    }

    /// <summary>
    /// Unregisters a provider from the gateway.
    /// </summary>
    /// <param name="providerId">The provider ID to unregister.</param>
    /// <returns>True if the provider was removed, false if not found.</returns>
    public virtual bool UnregisterProvider(string providerId)
    {
        return _providers.TryRemove(providerId, out _);
    }

    /// <inheritdoc/>
    public virtual Task<IIntelligenceStrategy?> SelectProviderAsync(ProviderRequirements requirements, CancellationToken ct = default)
    {
        var candidates = _providers.Values
            .Where(p => p.IsAvailable)
            .Where(p => (p.Info.Capabilities & requirements.RequiredCapabilities) == requirements.RequiredCapabilities);

        if (requirements.MaxCostTier.HasValue)
        {
            candidates = candidates.Where(p => p.Info.CostTier <= requirements.MaxCostTier.Value);
        }

        if (requirements.RequireOffline)
        {
            candidates = candidates.Where(p => p.Info.SupportsOfflineMode);
        }

        if (requirements.PreferredProviders?.Length > 0)
        {
            var preferred = candidates.Where(p => requirements.PreferredProviders.Contains(p.StrategyId));
            if (preferred.Any())
            {
                candidates = preferred;
            }
        }

        if (requirements.ExcludedProviders?.Length > 0)
        {
            candidates = candidates.Where(p => !requirements.ExcludedProviders.Contains(p.StrategyId));
        }

        // Default: prefer lower cost, then lower latency
        var selected = candidates
            .OrderBy(p => p.Info.CostTier)
            .ThenBy(p => p.Info.LatencyTier)
            .FirstOrDefault();

        return Task.FromResult<IIntelligenceStrategy?>(selected);
    }

    /// <inheritdoc/>
    public virtual Task<IReadOnlyList<IIntelligenceStrategy>> GetAvailableProvidersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IIntelligenceStrategy> result = _providers.Values
            .Where(p => p.IsAvailable)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Executes a request against a specific provider.
    /// </summary>
    /// <param name="provider">The provider to use.</param>
    /// <param name="request">The request to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The intelligence response.</returns>
    protected abstract Task<IntelligenceResponse> ExecuteRequestAsync(
        IIntelligenceStrategy provider,
        IntelligenceRequest request,
        CancellationToken ct);

    /// <summary>
    /// Executes a streaming request against a specific provider.
    /// </summary>
    /// <param name="provider">The provider to use.</param>
    /// <param name="request">The request to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of response chunks.</returns>
    protected abstract IAsyncEnumerable<IntelligenceChunk> ExecuteStreamingRequestAsync(
        IIntelligenceStrategy provider,
        IntelligenceRequest request,
        CancellationToken ct);

    /// <summary>
    /// Creates a session instance.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="options">Session options.</param>
    /// <param name="provider">The provider to use.</param>
    /// <returns>The created session.</returns>
    protected virtual IntelligenceSessionBase CreateSessionInstance(
        string sessionId,
        SessionOptions options,
        IIntelligenceStrategy? provider)
    {
        return new DefaultIntelligenceSession(sessionId, options, provider, this);
    }

    /// <summary>
    /// Selects a provider for a specific request.
    /// </summary>
    protected virtual Task<IIntelligenceStrategy?> SelectProviderForRequestAsync(IntelligenceRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.PreferredProviderId))
        {
            var preferred = _providers.GetValueOrDefault(request.PreferredProviderId);
            if (preferred?.IsAvailable == true)
            {
                return Task.FromResult<IIntelligenceStrategy?>(preferred);
            }
        }

        return SelectProviderAsync(new ProviderRequirements
        {
            RequiredCapabilities = request.RequiredCapabilities
        }, ct);
    }

    /// <summary>
    /// Gets aggregated capabilities from all providers.
    /// </summary>
    protected virtual IntelligenceCapabilities GetAggregatedCapabilities()
    {
        var capabilities = IntelligenceCapabilities.None;
        foreach (var provider in _providers.Values.Where(p => p.IsAvailable))
        {
            capabilities |= provider.Info.Capabilities;
        }
        return capabilities;
    }

    /// <summary>
    /// Records a successful operation.
    /// </summary>
    protected void RecordSuccess(double latencyMs, int tokens)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _successfulRequests);
        Interlocked.Add(ref _totalLatencyTicks, (long)(latencyMs * TimeSpan.TicksPerMillisecond));
        Interlocked.Add(ref _totalTokensUsed, tokens);
    }

    /// <summary>
    /// Records a failed operation.
    /// </summary>
    protected void RecordFailure()
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _failedRequests);
    }

    /// <summary>
    /// Gets gateway statistics.
    /// </summary>
    public virtual GatewayStatistics GetStatistics()
    {
        var totalReqs = Interlocked.Read(ref _totalRequests);
        return new GatewayStatistics
        {
            TotalRequests = totalReqs,
            SuccessfulRequests = Interlocked.Read(ref _successfulRequests),
            FailedRequests = Interlocked.Read(ref _failedRequests),
            TotalTokensUsed = Interlocked.Read(ref _totalTokensUsed),
            AverageLatencyMs = totalReqs > 0
                ? Interlocked.Read(ref _totalLatencyTicks) / (double)TimeSpan.TicksPerMillisecond / totalReqs
                : 0,
            ActiveSessions = _sessions.Count,
            RegisteredProviders = _providers.Count,
            AvailableProviders = _providers.Count(p => p.Value.IsAvailable),
            StartTime = _startTime
        };
    }

    /// <summary>
    /// Removes a session from tracking.
    /// </summary>
    internal void RemoveSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Close all sessions
        var sessions = _sessions.Values.ToArray();
        foreach (var session in sessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in IntelligenceGateway.cs");
                // Ignore disposal errors
            }
        }

        _sessions.Clear();
        _providers.Clear();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Gateway statistics.
/// </summary>
public sealed class GatewayStatistics
{
    /// <summary>Total requests processed.</summary>
    public long TotalRequests { get; init; }

    /// <summary>Successful requests.</summary>
    public long SuccessfulRequests { get; init; }

    /// <summary>Failed requests.</summary>
    public long FailedRequests { get; init; }

    /// <summary>Total tokens used.</summary>
    public long TotalTokensUsed { get; init; }

    /// <summary>Average latency in milliseconds.</summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>Active session count.</summary>
    public int ActiveSessions { get; init; }

    /// <summary>Registered provider count.</summary>
    public int RegisteredProviders { get; init; }

    /// <summary>Available provider count.</summary>
    public int AvailableProviders { get; init; }

    /// <summary>Gateway start time.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Success rate as percentage.</summary>
    public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests * 100 : 0;
}

/// <summary>
/// Base class for intelligence sessions.
/// </summary>
public abstract class IntelligenceSessionBase : IIntelligenceSession
{
    private readonly List<ConversationTurn> _history = new();
    private readonly object _historyLock = new();
    private SessionState _state = SessionState.Active;

    /// <inheritdoc/>
    public string SessionId { get; }

    /// <summary>
    /// Gets the session options.
    /// </summary>
    protected SessionOptions Options { get; }

    /// <summary>
    /// Gets the assigned provider.
    /// </summary>
    protected IIntelligenceStrategy? Provider { get; }

    /// <inheritdoc/>
    public SessionState State => _state;

    /// <inheritdoc/>
    public IReadOnlyList<ConversationTurn> History
    {
        get
        {
            lock (_historyLock)
            {
                return _history.ToList();
            }
        }
    }

    /// <summary>
    /// Initializes a new session.
    /// </summary>
    protected IntelligenceSessionBase(string sessionId, SessionOptions options, IIntelligenceStrategy? provider)
    {
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Provider = provider;

        // Add system message if provided
        if (!string.IsNullOrEmpty(options.SystemMessage))
        {
            AddTurn("system", options.SystemMessage);
        }
    }

    /// <inheritdoc/>
    public abstract Task<IntelligenceResponse> SendAsync(string message, CancellationToken ct = default);

    /// <inheritdoc/>
    public virtual Task CloseAsync(CancellationToken ct = default)
    {
        _state = SessionState.Closed;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a turn to the conversation history.
    /// </summary>
    protected void AddTurn(string role, string content, TokenUsage? usage = null)
    {
        lock (_historyLock)
        {
            var turn = new ConversationTurn
            {
                TurnIndex = _history.Count,
                Role = role,
                Content = content,
                Timestamp = DateTimeOffset.UtcNow,
                Usage = usage
            };
            _history.Add(turn);

            // Trim history if needed
            while (_history.Count > Options.MaxHistoryLength && _history.Count > 1)
            {
                // Keep system message if present
                var removeIndex = _history[0].Role == "system" ? 1 : 0;
                _history.RemoveAt(removeIndex);
            }
        }
    }

    /// <summary>
    /// Sets the session state.
    /// </summary>
    protected void SetState(SessionState state)
    {
        _state = state;
    }

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
    {
        _state = SessionState.Closed;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Default session implementation.
/// </summary>
internal sealed class DefaultIntelligenceSession : IntelligenceSessionBase
{
    private readonly IntelligenceGatewayBase _gateway;

    public DefaultIntelligenceSession(
        string sessionId,
        SessionOptions options,
        IIntelligenceStrategy? provider,
        IntelligenceGatewayBase gateway)
        : base(sessionId, options, provider)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public override async Task<IntelligenceResponse> SendAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("Message cannot be null or empty", nameof(message));
        }

        if (State != SessionState.Active)
        {
            throw new InvalidOperationException($"Session is not active (state: {State})");
        }

        AddTurn("user", message);

        var request = new IntelligenceRequest
        {
            Prompt = message,
            SessionId = SessionId,
            PreferredProviderId = Provider?.StrategyId
        };

        var response = await _gateway.ProcessRequestAsync(request, ct).ConfigureAwait(false);

        if (response.Success)
        {
            AddTurn("assistant", response.Content, response.Usage);
        }
        else
        {
            SetState(SessionState.Error);
        }

        return response;
    }

    public override async Task CloseAsync(CancellationToken ct = default)
    {
        await base.CloseAsync(ct).ConfigureAwait(false);
        _gateway.RemoveSession(SessionId);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Base implementation for intelligence channels.
/// Provides message serialization, connection state management, and heartbeat handling.
/// </summary>
/// <remarks>
/// <para>
/// This base class provides:
/// </para>
/// <list type="bullet">
///   <item>Bounded channel-based message queuing</item>
///   <item>Connection state tracking</item>
///   <item>Heartbeat mechanism for connection health</item>
///   <item>Thread-safe message operations</item>
/// </list>
/// </remarks>
public abstract class IntelligenceChannelBase : IIntelligenceChannel
{
    private readonly Channel<ChannelMessage> _inbound;
    private readonly Channel<ChannelMessage> _outbound;
    private readonly CancellationTokenSource _heartbeatCts = new();
    private Task? _heartbeatTask;
    private bool _disposed;

    /// <summary>
    /// Connection state enumeration.
    /// </summary>
    protected enum ConnectionState
    {
        /// <summary>Not connected.</summary>
        Disconnected,

        /// <summary>Connection in progress.</summary>
        Connecting,

        /// <summary>Connected and ready.</summary>
        Connected,

        /// <summary>Disconnecting.</summary>
        Disconnecting,

        /// <summary>Connection failed.</summary>
        Failed
    }

    /// <summary>
    /// Current connection state.
    /// </summary>
    protected ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <inheritdoc/>
    public abstract ChannelType Type { get; }

    /// <summary>
    /// Gets or sets the heartbeat interval.
    /// </summary>
    protected TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether heartbeat is enabled.
    /// </summary>
    protected bool HeartbeatEnabled { get; set; }

    /// <summary>
    /// Initializes a new channel.
    /// </summary>
    /// <param name="capacity">Message queue capacity.</param>
    protected IntelligenceChannelBase(int capacity = 1000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _inbound = Channel.CreateBounded<ChannelMessage>(options);
        _outbound = Channel.CreateBounded<ChannelMessage>(options);
    }

    /// <summary>
    /// Opens the channel connection.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task OpenAsync(CancellationToken ct = default)
    {
        if (State == ConnectionState.Connected)
        {
            return;
        }

        State = ConnectionState.Connecting;

        try
        {
            await ConnectCoreAsync(ct).ConfigureAwait(false);
            State = ConnectionState.Connected;

            if (HeartbeatEnabled)
            {
                StartHeartbeat();
            }
        }
        catch
        {
            State = ConnectionState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Closes the channel connection.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task CloseAsync(CancellationToken ct = default)
    {
        if (State == ConnectionState.Disconnected || State == ConnectionState.Disconnecting)
        {
            return;
        }

        State = ConnectionState.Disconnecting;

        await _heartbeatCts.CancelAsync();
        if (_heartbeatTask != null)
        {
            try
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"Caught OperationCanceledException in IntelligenceGateway.cs");
                // Expected
            }
        }

        try
        {
            await DisconnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            State = ConnectionState.Disconnected;
            _inbound.Writer.TryComplete();
            _outbound.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public virtual async Task<ChannelMessage> ReceiveAsync(CancellationToken ct = default)
    {
        if (State == ConnectionState.Disconnected || State == ConnectionState.Failed)
        {
            throw new ChannelClosedException("Channel is not connected");
        }

        try
        {
            return await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ChannelClosedException("Channel was closed");
        }
    }

    /// <inheritdoc/>
    public virtual async Task SendAsync(ChannelMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (State != ConnectionState.Connected)
        {
            throw new ChannelClosedException("Channel is not connected");
        }

        await _outbound.Writer.WriteAsync(message, ct).ConfigureAwait(false);
        await TransmitMessageAsync(message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public virtual async IAsyncEnumerable<ChannelMessage> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var message in _inbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Queues an inbound message.
    /// </summary>
    protected async Task QueueInboundAsync(ChannelMessage message, CancellationToken ct = default)
    {
        await _inbound.Writer.WriteAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the actual connection.
    /// </summary>
    protected abstract Task ConnectCoreAsync(CancellationToken ct);

    /// <summary>
    /// Performs the actual disconnection.
    /// </summary>
    protected abstract Task DisconnectCoreAsync(CancellationToken ct);

    /// <summary>
    /// Transmits a message through the underlying transport.
    /// </summary>
    protected abstract Task TransmitMessageAsync(ChannelMessage message, CancellationToken ct);

    /// <summary>
    /// Handles a heartbeat tick.
    /// </summary>
    protected virtual Task OnHeartbeatAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    private void StartHeartbeat()
    {
        _heartbeatTask = Task.Run(async () =>
        {
            while (!_heartbeatCts.IsCancellationRequested && State == ConnectionState.Connected)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, _heartbeatCts.Token).ConfigureAwait(false);
                    await OnHeartbeatAsync(_heartbeatCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"Caught OperationCanceledException in IntelligenceGateway.cs");
                    break;
                }
            }
        }, _heartbeatCts.Token);
    }

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await CloseAsync().ConfigureAwait(false);
        _heartbeatCts.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Base class for knowledge handlers.
/// Provides query parsing, result formatting, and error handling.
/// </summary>
/// <remarks>
/// <para>
/// Knowledge handlers process queries against the intelligence system, providing:
/// </para>
/// <list type="bullet">
///   <item>Query parsing and validation</item>
///   <item>Result aggregation and formatting</item>
///   <item>Error handling and recovery</item>
///   <item>Caching for frequently accessed knowledge</item>
/// </list>
/// </remarks>
public abstract class KnowledgeHandlerBase
{
    private readonly BoundedDictionary<string, CachedKnowledge> _cache = new BoundedDictionary<string, CachedKnowledge>(1000);
    private readonly TimeSpan _defaultCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the handler identifier.
    /// </summary>
    public abstract string HandlerId { get; }

    /// <summary>
    /// Gets the supported query types.
    /// </summary>
    public abstract IReadOnlyList<string> SupportedQueryTypes { get; }

    /// <summary>
    /// Handles a knowledge query.
    /// </summary>
    /// <param name="query">The query to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The query result.</returns>
    public virtual async Task<KnowledgeQueryResult> HandleQueryAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Validate query
        var validationResult = ValidateQuery(query);
        if (!validationResult.IsValid)
        {
            return KnowledgeQueryResult.CreateFailure(query.QueryId, validationResult.ErrorMessage ?? "Invalid query");
        }

        // Check cache
        var cacheKey = BuildCacheKey(query);
        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.Result;
        }

        try
        {
            // Execute query
            var result = await ExecuteQueryCoreAsync(query, ct).ConfigureAwait(false);

            // Cache result
            if (result.Success && ShouldCache(query))
            {
                _cache[cacheKey] = new CachedKnowledge(result, _defaultCacheDuration);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return KnowledgeQueryResult.CreateFailure(query.QueryId, $"Query execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates a query before execution.
    /// </summary>
    protected virtual QueryValidationResult ValidateQuery(KnowledgeQuery query)
    {
        if (string.IsNullOrEmpty(query.QueryType))
        {
            return QueryValidationResult.Invalid("Query type is required");
        }

        if (!SupportedQueryTypes.Contains(query.QueryType, StringComparer.OrdinalIgnoreCase))
        {
            return QueryValidationResult.Invalid($"Unsupported query type: {query.QueryType}");
        }

        return QueryValidationResult.Valid();
    }

    /// <summary>
    /// Executes the query.
    /// </summary>
    protected abstract Task<KnowledgeQueryResult> ExecuteQueryCoreAsync(KnowledgeQuery query, CancellationToken ct);

    /// <summary>
    /// Builds a cache key for a query.
    /// </summary>
    protected virtual string BuildCacheKey(KnowledgeQuery query)
    {
        return $"{query.QueryType}:{query.QueryText}:{string.Join(",", query.Parameters.Select(p => $"{p.Key}={p.Value}"))}";
    }

    /// <summary>
    /// Determines if a query result should be cached.
    /// </summary>
    protected virtual bool ShouldCache(KnowledgeQuery query)
    {
        return true;
    }

    /// <summary>
    /// Clears the cache.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Removes expired cache entries.
    /// </summary>
    public void PruneCache()
    {
        var expiredKeys = _cache.Where(kvp => kvp.Value.IsExpired).Select(kvp => kvp.Key).ToList();
        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private sealed class CachedKnowledge
    {
        public KnowledgeQueryResult Result { get; }
        public DateTimeOffset ExpiresAt { get; }
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

        public CachedKnowledge(KnowledgeQueryResult result, TimeSpan duration)
        {
            Result = result;
            ExpiresAt = DateTimeOffset.UtcNow + duration;
        }
    }
}

/// <summary>
/// Knowledge query input.
/// </summary>
public sealed class KnowledgeQuery
{
    /// <summary>
    /// Gets or sets the query ID.
    /// </summary>
    public string QueryId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the query type.
    /// </summary>
    public required string QueryType { get; set; }

    /// <summary>
    /// Gets or sets the query text.
    /// </summary>
    public string QueryText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets query parameters.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>
    /// Gets or sets the maximum results to return.
    /// </summary>
    public int MaxResults { get; set; } = 100;
}

/// <summary>
/// Knowledge query result.
/// </summary>
public sealed class KnowledgeQueryResult
{
    /// <summary>
    /// Gets or sets the query ID.
    /// </summary>
    public required string QueryId { get; set; }

    /// <summary>
    /// Gets or sets whether the query was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result items.
    /// </summary>
    public List<KnowledgeResultItem> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets the total count before pagination.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets query execution time.
    /// </summary>
    public TimeSpan ExecutionTime { get; set; }

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static KnowledgeQueryResult CreateSuccess(string queryId, List<KnowledgeResultItem> items)
    {
        return new KnowledgeQueryResult
        {
            QueryId = queryId,
            Success = true,
            Items = items,
            TotalCount = items.Count
        };
    }

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static KnowledgeQueryResult CreateFailure(string queryId, string errorMessage)
    {
        return new KnowledgeQueryResult
        {
            QueryId = queryId,
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Single item in knowledge query result.
/// </summary>
public sealed class KnowledgeResultItem
{
    /// <summary>
    /// Gets or sets the item ID.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the item content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the relevance score.
    /// </summary>
    public double Score { get; set; }

    /// <summary>
    /// Gets or sets item metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Query validation result.
/// </summary>
public sealed class QueryValidationResult
{
    /// <summary>
    /// Gets whether the query is valid.
    /// </summary>
    public bool IsValid { get; private init; }

    /// <summary>
    /// Gets the error message if invalid.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// Creates a valid result.
    /// </summary>
    public static QueryValidationResult Valid() => new() { IsValid = true };

    /// <summary>
    /// Creates an invalid result.
    /// </summary>
    public static QueryValidationResult Invalid(string message) => new() { IsValid = false, ErrorMessage = message };
}

#endregion
