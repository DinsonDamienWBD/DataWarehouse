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
        /// Publish a message and wait for all handlers to complete.
        /// Default implementation calls PublishAsync and awaits.
        /// </summary>
        public virtual Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
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
}
