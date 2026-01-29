// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AIInterface.Channels;

/// <summary>
/// Interface for all integration channels.
/// Channels receive external requests and translate them to AI capability requests
/// routed through the message bus to the AIAgents plugin.
/// </summary>
/// <remarks>
/// <para>
/// Integration channels are responsible for:
/// <list type="bullet">
/// <item>Receiving requests from external platforms (Slack, Teams, Discord, Voice, LLMs)</item>
/// <item>Validating incoming requests (signatures, authentication)</item>
/// <item>Translating platform-specific formats to AI capability requests</item>
/// <item>Routing requests to AIAgents via the message bus</item>
/// <item>Formatting AI responses for the target platform</item>
/// </list>
/// </para>
/// <para>
/// Channels do NOT perform any AI processing themselves. All AI work is delegated
/// to the AIAgents plugin through the message bus pattern.
/// </para>
/// </remarks>
public interface IIntegrationChannel
{
    /// <summary>
    /// Gets the unique channel identifier.
    /// </summary>
    /// <example>slack, teams, discord, alexa, chatgpt, claude_mcp</example>
    string ChannelId { get; }

    /// <summary>
    /// Gets the human-readable channel name.
    /// </summary>
    string ChannelName { get; }

    /// <summary>
    /// Gets the channel category for grouping.
    /// </summary>
    ChannelCategory Category { get; }

    /// <summary>
    /// Gets whether this channel is properly configured and ready to handle requests.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets whether this channel is currently enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Enables or disables this channel.
    /// </summary>
    /// <param name="enabled">True to enable, false to disable.</param>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Handles an incoming request from the external platform.
    /// </summary>
    /// <param name="request">The incoming channel request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response to send back to the platform.</returns>
    Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default);

    /// <summary>
    /// Validates the request signature/authentication.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="signature">The signature value.</param>
    /// <param name="headers">The request headers.</param>
    /// <returns>True if the signature is valid.</returns>
    bool ValidateSignature(string body, string signature, IDictionary<string, string> headers);

    /// <summary>
    /// Gets the webhook endpoint URL pattern for this channel.
    /// </summary>
    /// <returns>The webhook endpoint URL pattern.</returns>
    string GetWebhookEndpoint();

    /// <summary>
    /// Gets the channel configuration/manifest for setup.
    /// </summary>
    /// <returns>The configuration object.</returns>
    object GetConfiguration();

    /// <summary>
    /// Gets channel-specific health/status information.
    /// </summary>
    /// <returns>Health status information.</returns>
    ChannelHealth GetHealth();
}

/// <summary>
/// Categories of integration channels.
/// </summary>
public enum ChannelCategory
{
    /// <summary>Chat platforms (Slack, Teams, Discord).</summary>
    Chat,

    /// <summary>Voice assistants (Alexa, Google Assistant, Siri).</summary>
    Voice,

    /// <summary>LLM platforms (ChatGPT, Claude MCP, Generic).</summary>
    LLM,

    /// <summary>Generic webhook endpoints.</summary>
    Webhook
}

/// <summary>
/// Incoming request from a channel.
/// </summary>
public sealed class ChannelRequest
{
    /// <summary>Gets or sets the request identifier.</summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the request type/event.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets or sets the raw request body.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Gets or sets the parsed payload.</summary>
    public Dictionary<string, object> Payload { get; init; } = new();

    /// <summary>Gets or sets the request headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();

    /// <summary>Gets or sets the request timestamp.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets the user identifier.</summary>
    public string? UserId { get; init; }

    /// <summary>Gets or sets the conversation/session identifier.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Gets or sets the channel-specific context.</summary>
    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
/// Response to send back to a channel.
/// </summary>
public sealed class ChannelResponse
{
    /// <summary>Gets or sets the HTTP status code.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>Gets or sets the response body.</summary>
    public object? Body { get; init; }

    /// <summary>Gets or sets response headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();

    /// <summary>Gets or sets the content type.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>Creates a success response.</summary>
    public static ChannelResponse Success(object? body = null) => new()
    {
        StatusCode = 200,
        Body = body
    };

    /// <summary>Creates an error response.</summary>
    public static ChannelResponse Error(int statusCode, string message) => new()
    {
        StatusCode = statusCode,
        Body = new { error = message }
    };

    /// <summary>Creates a verification/challenge response.</summary>
    public static ChannelResponse Challenge(string challenge) => new()
    {
        StatusCode = 200,
        Body = challenge,
        ContentType = "text/plain"
    };
}

/// <summary>
/// Channel health/status information.
/// </summary>
public sealed class ChannelHealth
{
    /// <summary>Gets or sets whether the channel is healthy.</summary>
    public bool IsHealthy { get; init; } = true;

    /// <summary>Gets or sets the last successful request time.</summary>
    public DateTime? LastSuccessfulRequest { get; init; }

    /// <summary>Gets or sets the last error time.</summary>
    public DateTime? LastError { get; init; }

    /// <summary>Gets or sets the last error message.</summary>
    public string? LastErrorMessage { get; init; }

    /// <summary>Gets or sets the total request count.</summary>
    public long TotalRequests { get; init; }

    /// <summary>Gets or sets the successful request count.</summary>
    public long SuccessfulRequests { get; init; }

    /// <summary>Gets or sets the failed request count.</summary>
    public long FailedRequests { get; init; }

    /// <summary>Gets or sets additional health details.</summary>
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>
/// AI capability request to send to the AIAgents plugin via message bus.
/// </summary>
public sealed class AICapabilityRequest
{
    /// <summary>Gets or sets the capability identifier.</summary>
    /// <example>ai.chat, ai.complete, ai.embed, nl.query.search</example>
    public string Capability { get; init; } = string.Empty;

    /// <summary>Gets or sets the request payload.</summary>
    public Dictionary<string, object> Payload { get; init; } = new();

    /// <summary>Gets or sets the user identifier.</summary>
    public string? UserId { get; init; }

    /// <summary>Gets or sets the conversation identifier.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Gets or sets the source channel.</summary>
    public string? SourceChannel { get; init; }

    /// <summary>Gets or sets additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Response from an AI capability request.
/// </summary>
public sealed class AICapabilityResponse
{
    /// <summary>Gets or sets whether the request succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the response content.</summary>
    public string? Response { get; init; }

    /// <summary>Gets or sets structured response data.</summary>
    public object? Data { get; init; }

    /// <summary>Gets or sets the error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets or sets suggested follow-up actions.</summary>
    public List<string> SuggestedFollowUps { get; init; } = new();

    /// <summary>Gets or sets additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Base implementation for integration channels with common functionality.
/// </summary>
public abstract class IntegrationChannelBase : IIntegrationChannel
{
    private bool _isEnabled = true;
    private long _totalRequests;
    private long _successfulRequests;
    private long _failedRequests;
    private DateTime? _lastSuccessfulRequest;
    private DateTime? _lastError;
    private string? _lastErrorMessage;

    /// <summary>
    /// Gets the message bus for routing AI requests.
    /// </summary>
    protected IMessageBus? MessageBus { get; private set; }

    /// <inheritdoc />
    public abstract string ChannelId { get; }

    /// <inheritdoc />
    public abstract string ChannelName { get; }

    /// <inheritdoc />
    public abstract ChannelCategory Category { get; }

    /// <inheritdoc />
    public abstract bool IsConfigured { get; }

    /// <inheritdoc />
    public bool IsEnabled => _isEnabled && IsConfigured;

    /// <inheritdoc />
    public void SetEnabled(bool enabled) => _isEnabled = enabled;

    /// <summary>
    /// Initializes the channel with the message bus.
    /// </summary>
    /// <param name="messageBus">The message bus for AI routing.</param>
    public void Initialize(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
    }

    /// <inheritdoc />
    public abstract Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default);

    /// <inheritdoc />
    public abstract bool ValidateSignature(string body, string signature, IDictionary<string, string> headers);

    /// <inheritdoc />
    public virtual string GetWebhookEndpoint() => $"/api/ai/channels/{ChannelId}/webhook";

    /// <inheritdoc />
    public abstract object GetConfiguration();

    /// <inheritdoc />
    public ChannelHealth GetHealth() => new()
    {
        IsHealthy = IsEnabled && _lastError == null || (_lastSuccessfulRequest > _lastError),
        LastSuccessfulRequest = _lastSuccessfulRequest,
        LastError = _lastError,
        LastErrorMessage = _lastErrorMessage,
        TotalRequests = _totalRequests,
        SuccessfulRequests = _successfulRequests,
        FailedRequests = _failedRequests
    };

    /// <summary>
    /// Routes an AI capability request through the message bus.
    /// </summary>
    /// <param name="capability">The AI capability to invoke.</param>
    /// <param name="payload">The request payload.</param>
    /// <param name="userId">Optional user identifier.</param>
    /// <param name="conversationId">Optional conversation identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The AI capability response.</returns>
    protected async Task<AICapabilityResponse> RouteToAIAgentsAsync(
        string capability,
        Dictionary<string, object> payload,
        string? userId = null,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalRequests);

        if (MessageBus == null)
        {
            Interlocked.Increment(ref _failedRequests);
            _lastError = DateTime.UtcNow;
            _lastErrorMessage = "Message bus not configured";
            return new AICapabilityResponse
            {
                Success = false,
                Error = "Message bus not configured"
            };
        }

        try
        {
            // Add metadata
            payload["_sourceChannel"] = ChannelId;
            if (!string.IsNullOrEmpty(userId))
                payload["userId"] = userId;
            if (!string.IsNullOrEmpty(conversationId))
                payload["conversationId"] = conversationId;

            // Route through message bus
            var response = await MessageBus.RequestAsync(capability, payload, ct);

            Interlocked.Increment(ref _successfulRequests);
            _lastSuccessfulRequest = DateTime.UtcNow;

            // Extract response from payload
            if (response?.TryGetValue("_response", out var result) == true)
            {
                return ParseAIResponse(result);
            }

            return new AICapabilityResponse
            {
                Success = true,
                Data = response
            };
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedRequests);
            _lastError = DateTime.UtcNow;
            _lastErrorMessage = ex.Message;

            return new AICapabilityResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Parses an AI response from the raw result.
    /// </summary>
    private AICapabilityResponse ParseAIResponse(object? result)
    {
        if (result == null)
        {
            return new AICapabilityResponse { Success = true };
        }

        // Handle dictionary response
        if (result is Dictionary<string, object> dict)
        {
            var success = !dict.ContainsKey("error");
            var response = dict.TryGetValue("response", out var r) ? r?.ToString()
                         : dict.TryGetValue("content", out var c) ? c?.ToString()
                         : dict.TryGetValue("message", out var m) ? m?.ToString()
                         : null;

            return new AICapabilityResponse
            {
                Success = success,
                Response = response,
                Data = dict.TryGetValue("data", out var d) ? d : null,
                Error = dict.TryGetValue("error", out var e) ? e?.ToString() : null,
                SuggestedFollowUps = dict.TryGetValue("suggestions", out var s) && s is List<string> list
                    ? list : new List<string>()
            };
        }

        // Handle as raw response
        return new AICapabilityResponse
        {
            Success = true,
            Response = result.ToString(),
            Data = result
        };
    }
}

/// <summary>
/// Interface for message bus communication.
/// Allows channels to route requests to the AIAgents plugin.
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// Sends a request and waits for a response.
    /// </summary>
    /// <param name="messageType">The message type (e.g., "ai.chat", "ai.complete").</param>
    /// <param name="payload">The message payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response payload.</returns>
    Task<Dictionary<string, object>?> RequestAsync(
        string messageType,
        Dictionary<string, object> payload,
        CancellationToken ct = default);

    /// <summary>
    /// Publishes a message without waiting for a response.
    /// </summary>
    /// <param name="messageType">The message type.</param>
    /// <param name="payload">The message payload.</param>
    void Publish(string messageType, Dictionary<string, object> payload);
}
