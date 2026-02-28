using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Message bus interface for plugin-to-plugin and kernel-to-plugin communication.
    /// Plugins NEVER directly reference each other - all communication goes through the message bus.
    /// </summary>
    public interface IMessageBus
    {
        /// <summary>
        /// Publish a message to all subscribers of the specified topic.
        /// Fire-and-forget - does not wait for handlers to complete.
        /// </summary>
        Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Publish a message and wait for all handlers to complete.
        /// </summary>
        Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Send a command and wait for a response.
        /// Used for request-response patterns.
        /// </summary>
        Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Send a command and wait for a response with timeout.
        /// </summary>
        Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default);

        /// <summary>
        /// Subscribe to messages on a topic.
        /// </summary>
        IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler);

        /// <summary>
        /// Subscribe to messages on a topic with response capability.
        /// </summary>
        IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler);

        /// <summary>
        /// Subscribe to messages matching a pattern (e.g., "storage.*", "*.error").
        /// </summary>
        IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler);

        /// <summary>
        /// Unsubscribe all handlers for a topic.
        /// </summary>
        void Unsubscribe(string topic);

        /// <summary>
        /// Get all active subscriptions.
        /// </summary>
        IEnumerable<string> GetActiveTopics();
    }

    /// <summary>
    /// Response from a message handler.
    /// </summary>
    public class MessageResponse
    {
        /// <summary>
        /// Whether the operation was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Response payload (can be any serializable object).
        /// </summary>
        public object? Payload { get; init; }

        /// <summary>
        /// Error message if not successful.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Error code for programmatic handling.
        /// </summary>
        public string? ErrorCode { get; init; }

        /// <summary>
        /// Response metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Create a success response.
        /// </summary>
        public static MessageResponse Ok(object? payload = null) => new()
        {
            Success = true,
            Payload = payload
        };

        /// <summary>
        /// Create an error response.
        /// </summary>
        public static MessageResponse Error(string message, string? code = null) => new()
        {
            Success = false,
            ErrorMessage = message,
            ErrorCode = code
        };
    }

    /// <summary>
    /// Standard message topics used by the kernel and built-in plugins.
    /// </summary>
    public static class MessageTopics
    {
        // System topics
        public const string SystemStartup = "system.startup";
        public const string SystemShutdown = "system.shutdown";
        public const string SystemHealthCheck = "system.healthcheck";

        // Plugin lifecycle
        public const string PluginLoaded = "plugin.loaded";
        public const string PluginUnloaded = "plugin.unloaded";
        public const string PluginError = "plugin.error";

        // Storage operations
        public const string StorageSave = "storage.save";
        public const string StorageLoad = "storage.load";
        public const string StorageDelete = "storage.delete";
        public const string StorageSaved = "storage.saved";
        public const string StorageLoaded = "storage.loaded";
        public const string StorageDeleted = "storage.deleted";

        // Pipeline operations
        public const string PipelineExecute = "pipeline.execute";
        public const string PipelineCompleted = "pipeline.completed";
        public const string PipelineError = "pipeline.error";

        // AI operations
        public const string AIQuery = "ai.query";
        public const string AIEmbed = "ai.embed";
        public const string AIResponse = "ai.response";

        // Metadata operations
        public const string MetadataIndex = "metadata.index";
        public const string MetadataSearch = "metadata.search";
        public const string MetadataUpdate = "metadata.update";

        // Security operations
        public const string SecurityAuth = "security.auth";
        public const string SecurityACL = "security.acl";
        public const string SecurityAudit = "security.audit";

        // Configuration
        public const string ConfigChanged = "config.changed";
        public const string ConfigReload = "config.reload";

        // Authentication events
        public const string AuthKeyRotated = "security.key.rotated";
        public const string AuthSigningKeyChanged = "security.signing.changed";
        public const string AuthReplayDetected = "security.replay.detected";

        // Knowledge and Capability topics
        public const string KnowledgeRegister = "knowledge.register";
        public const string KnowledgeQuery = "knowledge.query";
        public const string KnowledgeResponse = "knowledge.response";
        public const string KnowledgeUpdate = "knowledge.update";
        public const string CapabilityRegister = "capability.register";
        public const string CapabilityUnregister = "capability.unregister";
        public const string CapabilityQuery = "capability.query";
        public const string CapabilityChanged = "capability.changed";
    }

    /// <summary>
    /// Extended message bus with advanced features for enterprise scenarios.
    /// </summary>
    public interface IAdvancedMessageBus : IMessageBus
    {
        /// <summary>
        /// Publish with delivery guarantees (at-least-once).
        /// </summary>
        Task PublishReliableAsync(string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Publish with confirmation - returns result indicating delivery status.
        /// Use this when you need to know if the message was successfully delivered.
        /// </summary>
        Task<PublishResult> PublishWithConfirmationAsync(string topic, PluginMessage message, PublishOptions? options = null, CancellationToken ct = default);

        /// <summary>
        /// Subscribe with message filtering.
        /// </summary>
        IDisposable Subscribe(string topic, Func<PluginMessage, bool> filter, Func<PluginMessage, Task> handler);

        /// <summary>
        /// Create a message group for related operations.
        /// </summary>
        IMessageGroup CreateGroup(string groupId);

        /// <summary>
        /// Get message bus statistics.
        /// </summary>
        MessageBusStatistics GetStatistics();
    }

    /// <summary>
    /// Message group for transactional/related operations.
    /// </summary>
    public interface IMessageGroup : IDisposable
    {
        /// <summary>
        /// Group identifier.
        /// </summary>
        string GroupId { get; }

        /// <summary>
        /// Add a message to the group.
        /// </summary>
        Task AddAsync(string topic, PluginMessage message);

        /// <summary>
        /// Commit all messages in the group atomically.
        /// </summary>
        Task CommitAsync(CancellationToken ct = default);

        /// <summary>
        /// Rollback/discard all messages in the group.
        /// </summary>
        Task RollbackAsync();
    }

    /// <summary>
    /// Message bus statistics.
    /// </summary>
    public class MessageBusStatistics
    {
        public long TotalMessagesPublished { get; init; }
        public long TotalMessagesDelivered { get; init; }
        public long TotalMessagesFailed { get; init; }
        public int ActiveSubscriptions { get; init; }
        public int ActiveTopics { get; init; }
        public TimeSpan AverageDeliveryTime { get; init; }
        public Dictionary<string, long> MessagesByTopic { get; init; } = new();
    }

    /// <summary>
    /// Abstract base class for IMessageBus implementations.
    /// Provides common infrastructure to reduce boilerplate code.
    /// </summary>
    public abstract class MessageBusBase : IMessageBus
    {
        /// <summary>
        /// Publish a message to all subscribers (fire and forget).
        /// </summary>
        public abstract Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Publish a message and wait for all handlers to complete before returning.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Contract</strong>: The returned task completes only after all synchronous and
        /// asynchronous subscribers have finished processing the message.
        /// </para>
        /// <para>
        /// <strong>Default implementation warning</strong>: The base implementation falls back to
        /// <see cref="PublishAsync"/> which is fire-and-forget and does NOT wait for handler
        /// completion (finding P2-117). Concrete message bus implementations MUST override this
        /// method to honour the await-handlers semantic; otherwise callers that depend on
        /// handler completion (e.g., flushing audit records before shutdown) will silently
        /// receive a Task that completes before handlers run.
        /// </para>
        /// </remarks>
        public virtual Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            // WARNING: this default does NOT wait for handler completion.
            // Override in concrete implementations that support synchronous handler dispatch.
            return PublishAsync(topic, message, ct);
        }

        /// <summary>
        /// Send a message and wait for a response.
        /// </summary>
        public abstract Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default);

        /// <summary>
        /// Send a message with timeout. Default implementation wraps SendAsync with timeout.
        /// </summary>
        public virtual async Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                return await SendAsync(topic, message, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return MessageResponse.Error($"Request timed out after {timeout.TotalMilliseconds}ms", "TIMEOUT");
            }
        }

        /// <summary>
        /// Subscribe to messages on a topic.
        /// </summary>
        public abstract IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler);

        /// <summary>
        /// Subscribe with response capability.
        /// Default: wraps the handler and ignores response in publish scenarios.
        /// </summary>
        public virtual IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
        {
            return Subscribe(topic, async msg => { await handler(msg); });
        }

        /// <summary>
        /// Subscribe to pattern. Override for optimized implementation.
        /// Default: not supported, throws NotSupportedException.
        /// </summary>
        public virtual IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
        {
            throw new NotSupportedException("Pattern subscriptions not supported by this message bus implementation");
        }

        /// <summary>
        /// Unsubscribe all handlers for a topic.
        /// </summary>
        public abstract void Unsubscribe(string topic);

        /// <summary>
        /// Get all active topics.
        /// </summary>
        public abstract IEnumerable<string> GetActiveTopics();

        /// <summary>
        /// Helper to create a simple subscription handle.
        /// </summary>
        protected static IDisposable CreateHandle(Action onDispose)
        {
            return new SubscriptionHandle(onDispose);
        }

        private sealed class SubscriptionHandle : IDisposable
        {
            private readonly Action _onDispose;
            private bool _disposed;

            public SubscriptionHandle(Action onDispose) => _onDispose = onDispose;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _onDispose();
            }
        }
    }

    /// <summary>
    /// Configuration for message authentication on a specific topic.
    /// </summary>
    public class MessageAuthenticationOptions
    {
        /// <summary>
        /// Whether messages on this topic require HMAC signatures.
        /// </summary>
        public bool RequireSignature { get; init; }

        /// <summary>
        /// Maximum age of a message before it is rejected (replay protection).
        /// Default: 5 minutes.
        /// </summary>
        public TimeSpan MaxMessageAge { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Whether to track nonces for replay detection.
        /// When enabled, duplicate nonces within the MaxMessageAge window are rejected.
        /// </summary>
        public bool EnableReplayDetection { get; init; } = true;

        /// <summary>
        /// The hash algorithm to use for HMAC signing.
        /// Default: SHA256 (HMAC-SHA256).
        /// </summary>
        public System.Security.Cryptography.HashAlgorithmName HmacAlgorithm { get; init; } = System.Security.Cryptography.HashAlgorithmName.SHA256;
    }

    /// <summary>
    /// Result of message verification.
    /// </summary>
    public record MessageVerificationResult
    {
        /// <summary>
        /// Whether the message passed verification.
        /// </summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// Reason for verification failure, if any.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// The type of failure (for programmatic handling).
        /// </summary>
        public MessageVerificationFailure? FailureType { get; init; }

        /// <summary>
        /// Creates a successful verification result.
        /// </summary>
        public static MessageVerificationResult Valid() => new() { IsValid = true };

        /// <summary>
        /// Creates a failed verification result.
        /// </summary>
        public static MessageVerificationResult Invalid(MessageVerificationFailure failureType, string reason) =>
            new() { IsValid = false, FailureType = failureType, FailureReason = reason };
    }

    /// <summary>
    /// Types of message verification failures.
    /// </summary>
    public enum MessageVerificationFailure
    {
        /// <summary>Message has no signature but topic requires one.</summary>
        MissingSignature,

        /// <summary>HMAC signature does not match message content.</summary>
        InvalidSignature,

        /// <summary>Message has expired (ExpiresAt is in the past).</summary>
        Expired,

        /// <summary>Message nonce was already seen (replay attack detected).</summary>
        ReplayDetected,

        /// <summary>Message has no nonce but replay detection is enabled.</summary>
        MissingNonce
    }

    /// <summary>
    /// Extension of IMessageBus that adds HMAC-SHA256 message authentication and replay protection.
    /// Implementations sign messages on publish and verify on receive for authenticated topics.
    /// Authentication is opt-in per topic via ConfigureAuthentication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface extends IMessageBus to provide:
    /// </para>
    /// <list type="bullet">
    ///   <item>HMAC-SHA256 message signing (or configurable algorithm via MessageAuthenticationOptions)</item>
    ///   <item>Constant-time signature verification using CryptographicOperations.FixedTimeEquals</item>
    ///   <item>Nonce-based replay protection with configurable time window</item>
    ///   <item>Per-topic authentication configuration (not all topics need authentication)</item>
    /// </list>
    /// <para>
    /// Usage: Register authenticated topics, then use standard Publish/Subscribe.
    /// The authenticated bus automatically signs outgoing messages and verifies incoming ones.
    /// </para>
    /// </remarks>
    public interface IAuthenticatedMessageBus : IMessageBus
    {
        /// <summary>
        /// Configures authentication requirements for a topic.
        /// Messages published to this topic will be signed; received messages will be verified.
        /// </summary>
        /// <param name="topic">The topic to configure.</param>
        /// <param name="options">Authentication options.</param>
        void ConfigureAuthentication(string topic, MessageAuthenticationOptions options);

        /// <summary>
        /// Configures authentication for topics matching a pattern.
        /// </summary>
        /// <param name="topicPattern">Glob pattern (e.g., "security.*", "*.sensitive").</param>
        /// <param name="options">Authentication options.</param>
        void ConfigureAuthenticationPattern(string topicPattern, MessageAuthenticationOptions options);

        /// <summary>
        /// Sets the signing key used for HMAC message authentication.
        /// The key should be provisioned securely (e.g., from IKeyStore).
        /// </summary>
        /// <param name="key">The HMAC signing key.</param>
        void SetSigningKey(byte[] key);

        /// <summary>
        /// Rotates the signing key. The old key is retained for verification of in-flight messages
        /// during the grace period.
        /// </summary>
        /// <param name="newKey">The new HMAC signing key.</param>
        /// <param name="gracePeriod">How long to accept signatures from the old key.</param>
        void RotateSigningKey(byte[] newKey, TimeSpan gracePeriod);

        /// <summary>
        /// Manually verifies a message's signature and replay protection.
        /// Useful for messages received from external sources.
        /// </summary>
        /// <param name="message">The message to verify.</param>
        /// <param name="topic">The topic the message was received on.</param>
        /// <returns>Verification result with pass/fail and reason.</returns>
        MessageVerificationResult VerifyMessage(PluginMessage message, string topic);

        /// <summary>
        /// Gets whether a topic has authentication configured.
        /// </summary>
        bool IsAuthenticatedTopic(string topic);

        /// <summary>
        /// Gets the authentication options for a topic, or null if not configured.
        /// </summary>
        MessageAuthenticationOptions? GetAuthenticationOptions(string topic);
    }

    /// <summary>
    /// Standard message topics that should use authenticated messaging.
    /// </summary>
    public static class AuthenticatedMessageTopics
    {
        /// <summary>Security-sensitive operations should be authenticated.</summary>
        public const string SecurityPrefix = "security.";

        /// <summary>Key management operations should be authenticated.</summary>
        public const string KeyStorePrefix = "keystore.";

        /// <summary>Plugin lifecycle (load/unload) should be authenticated to prevent spoofing.</summary>
        public const string PluginLifecyclePrefix = "plugin.";

        /// <summary>System-level commands should be authenticated.</summary>
        public const string SystemPrefix = "system.";
    }
}
