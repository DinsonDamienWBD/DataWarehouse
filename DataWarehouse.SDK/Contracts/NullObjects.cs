using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// A no-op implementation of <see cref="IMessageBus"/> for use when intelligence
    /// integration is not available. All publish/send operations complete successfully
    /// as no-ops. Subscribe operations return a no-op disposable.
    /// </summary>
    /// <remarks>
    /// Use this instead of null checks throughout strategy and plugin code.
    /// <code>
    /// // Instead of:
    /// if (MessageBus != null) await MessageBus.PublishAsync(...);
    ///
    /// // Use:
    /// await MessageBus.PublishAsync(...); // NullMessageBus does nothing
    /// </code>
    /// </remarks>
    [SdkCompatibility("2.0.0", Notes = "Null-object pattern for optional IMessageBus dependency")]
    public sealed class NullMessageBus : IMessageBus
    {
        /// <summary>
        /// Gets the shared singleton instance.
        /// </summary>
        public static NullMessageBus Instance { get; } = new();

        private NullMessageBus() { }

        /// <inheritdoc />
        public Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
            => Task.FromResult(MessageResponse.Error("No message bus configured (NullMessageBus)"));

        /// <inheritdoc />
        public Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(MessageResponse.Error("No message bus configured (NullMessageBus)"));

        /// <inheritdoc />
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
            => NullSubscription.Instance;

        /// <inheritdoc />
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
            => NullSubscription.Instance;

        /// <inheritdoc />
        public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
            => NullSubscription.Instance;

        /// <inheritdoc />
        public void Unsubscribe(string topic) { }

        /// <inheritdoc />
        public IEnumerable<string> GetActiveTopics()
            => Enumerable.Empty<string>();

        /// <summary>
        /// A no-op disposable returned by subscribe methods.
        /// </summary>
        private sealed class NullSubscription : IDisposable
        {
            public static NullSubscription Instance { get; } = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// A no-op implementation of <see cref="ILogger"/> for use when logging
    /// is not configured. All log operations are silently discarded.
    /// </summary>
    /// <summary>
    /// A no-op <see cref="ILogger"/> singleton. Wraps the framework-provided
    /// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/> to ensure
    /// API-compatible null-object usage across the SDK.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Null-object pattern for optional ILogger dependency")]
    public static class NullLoggerProvider
    {
        /// <summary>
        /// Gets a shared no-op <see cref="ILogger"/> instance.
        /// </summary>
        public static ILogger Instance { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        /// <summary>
        /// Gets a shared no-op <see cref="ILogger{T}"/> instance for a specific category.
        /// </summary>
        /// <typeparam name="T">The category type.</typeparam>
        /// <returns>A no-op logger for the specified category.</returns>
        public static ILogger<T> For<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
    }
}
