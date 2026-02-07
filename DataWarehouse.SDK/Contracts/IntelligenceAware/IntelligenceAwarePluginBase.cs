using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    /// <summary>
    /// Abstract base class for plugins that can leverage Universal Intelligence (T90).
    /// Extends <see cref="FeaturePluginBase"/> with automatic Intelligence discovery,
    /// capability caching, and protected helper methods for AI operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This base class provides:
    /// </para>
    /// <list type="bullet">
    ///   <item>Automatic T90 discovery on <see cref="StartAsync"/></item>
    ///   <item>Cached capability information with 60-second TTL</item>
    ///   <item>Protected helper methods for common AI operations</item>
    ///   <item>Graceful fallback when Intelligence is unavailable</item>
    ///   <item>Automatic subscription to Intelligence availability broadcasts</item>
    /// </list>
    /// <para>
    /// Derived classes should override the <c>On*Async</c> methods to implement
    /// Intelligence-enhanced behavior, and use the <c>Request*Async</c> helper
    /// methods to interact with T90.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public class MyIntelligentPlugin : IntelligenceAwarePluginBase
    /// {
    ///     public override string Id => "com.example.myplugin";
    ///     public override string Name => "My Intelligent Plugin";
    ///
    ///     protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    ///     {
    ///         // Intelligence is available - enable enhanced features
    ///         if (HasCapability(IntelligenceCapabilities.Embeddings))
    ///         {
    ///             // Initialize embedding-based features
    ///         }
    ///     }
    ///
    ///     protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    ///     {
    ///         // Intelligence unavailable - use fallback behavior
    ///         return Task.CompletedTask;
    ///     }
    /// }
    /// </code>
    /// </example>
    public abstract class IntelligenceAwarePluginBase : FeaturePluginBase, IIntelligenceAware, IIntelligenceAwareNotifiable
    {
        /// <summary>
        /// Default timeout for Intelligence discovery requests.
        /// </summary>
        protected static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Default TTL for cached capability information.
        /// </summary>
        protected static readonly TimeSpan CapabilityCacheTtl = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Cache for pending request responses.
        /// </summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageResponse>> _pendingRequests = new();

        /// <summary>
        /// Subscriptions to Intelligence topics.
        /// </summary>
        private readonly List<IDisposable> _intelligenceSubscriptions = new();

        /// <summary>
        /// Lock for thread-safe state updates.
        /// </summary>
        private readonly object _stateLock = new();

        /// <summary>
        /// Backing field for Intelligence availability.
        /// </summary>
        private volatile bool _isIntelligenceAvailable;

        /// <summary>
        /// Backing field for available capabilities.
        /// </summary>
        private IntelligenceCapabilities _availableCapabilities = IntelligenceCapabilities.None;

        /// <summary>
        /// When the capability cache was last refreshed.
        /// </summary>
        private DateTimeOffset _capabilityCacheTime = DateTimeOffset.MinValue;

        /// <summary>
        /// Whether discovery has been attempted.
        /// </summary>
        private volatile bool _discoveryAttempted;

        /// <summary>
        /// Gets whether Universal Intelligence (T90) is currently available.
        /// </summary>
        public bool IsIntelligenceAvailable => _isIntelligenceAvailable;

        /// <summary>
        /// Gets the capabilities provided by the discovered Intelligence system.
        /// </summary>
        public IntelligenceCapabilities AvailableCapabilities
        {
            get
            {
                lock (_stateLock)
                {
                    return _availableCapabilities;
                }
            }
        }

        /// <summary>
        /// Checks if a specific capability is available.
        /// </summary>
        /// <param name="capability">The capability to check.</param>
        /// <returns><c>true</c> if the capability is available; otherwise <c>false</c>.</returns>
        protected bool HasCapability(IntelligenceCapabilities capability)
        {
            return IsIntelligenceAvailable && (AvailableCapabilities & capability) == capability;
        }

        /// <summary>
        /// Checks if any of the specified capabilities are available.
        /// </summary>
        /// <param name="capabilities">The capabilities to check.</param>
        /// <returns><c>true</c> if any capability is available; otherwise <c>false</c>.</returns>
        protected bool HasAnyCapability(IntelligenceCapabilities capabilities)
        {
            return IsIntelligenceAvailable && (AvailableCapabilities & capabilities) != IntelligenceCapabilities.None;
        }

        /// <summary>
        /// Attempts to discover Universal Intelligence (T90) via the message bus.
        /// </summary>
        /// <param name="ct">Cancellation token for the discovery operation.</param>
        /// <returns>
        /// A task that resolves to <c>true</c> if Intelligence was discovered;
        /// <c>false</c> otherwise.
        /// </returns>
        public async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            // Check cache validity
            if (_discoveryAttempted && DateTimeOffset.UtcNow - _capabilityCacheTime < CapabilityCacheTtl)
            {
                return _isIntelligenceAvailable;
            }

            if (MessageBus == null)
            {
                UpdateIntelligenceState(false, IntelligenceCapabilities.None);
                return false;
            }

            try
            {
                var correlationId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<MessageResponse>();
                _pendingRequests[correlationId] = tcs;

                try
                {
                    // Send discovery request
                    var request = new PluginMessage
                    {
                        Type = "intelligence.discover.request",
                        CorrelationId = correlationId,
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["requestorId"] = Id,
                            ["requestorName"] = Name,
                            ["timestamp"] = DateTimeOffset.UtcNow
                        }
                    };

                    await MessageBus.PublishAsync(IntelligenceTopics.Discover, request, ct);

                    // Wait for response with timeout
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(DiscoveryTimeout);

                    try
                    {
                        var response = await tcs.Task.WaitAsync(cts.Token);

                        if (response.Success && response.Payload is Dictionary<string, object> payload)
                        {
                            var capabilities = IntelligenceCapabilities.None;
                            if (payload.TryGetValue("capabilities", out var capObj))
                            {
                                if (capObj is IntelligenceCapabilities caps)
                                    capabilities = caps;
                                else if (capObj is long longVal)
                                    capabilities = (IntelligenceCapabilities)longVal;
                                else if (long.TryParse(capObj?.ToString(), out var parsed))
                                    capabilities = (IntelligenceCapabilities)parsed;
                            }

                            UpdateIntelligenceState(true, capabilities);
                            return true;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout - Intelligence not available
                    }
                }
                finally
                {
                    _pendingRequests.TryRemove(correlationId, out _);
                }

                UpdateIntelligenceState(false, IntelligenceCapabilities.None);
                return false;
            }
            catch (Exception)
            {
                UpdateIntelligenceState(false, IntelligenceCapabilities.None);
                return false;
            }
        }

        /// <summary>
        /// Updates the Intelligence availability state.
        /// </summary>
        private void UpdateIntelligenceState(bool available, IntelligenceCapabilities capabilities)
        {
            lock (_stateLock)
            {
                _isIntelligenceAvailable = available;
                _availableCapabilities = capabilities;
                _capabilityCacheTime = DateTimeOffset.UtcNow;
                _discoveryAttempted = true;
            }
        }

        /// <summary>
        /// Starts the plugin with automatic Intelligence discovery.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public override async Task StartAsync(CancellationToken ct)
        {
            // Subscribe to Intelligence topics first
            SubscribeToIntelligenceTopics();

            // Attempt discovery
            var intelligenceAvailable = await DiscoverIntelligenceAsync(ct);

            // Call appropriate initialization method
            if (intelligenceAvailable)
            {
                await OnStartWithIntelligenceAsync(ct);
            }
            else
            {
                await OnStartWithoutIntelligenceAsync(ct);
            }

            await OnStartCoreAsync(ct);
        }

        /// <summary>
        /// Stops the plugin and cleans up Intelligence subscriptions.
        /// </summary>
        public override async Task StopAsync()
        {
            // Unsubscribe from Intelligence topics
            foreach (var subscription in _intelligenceSubscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }
            _intelligenceSubscriptions.Clear();

            // Clear pending requests
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetCanceled();
            }
            _pendingRequests.Clear();

            await OnStopCoreAsync();
        }

        /// <summary>
        /// Subscribes to Intelligence broadcast topics.
        /// </summary>
        private void SubscribeToIntelligenceTopics()
        {
            if (MessageBus == null) return;

            try
            {
                // Subscribe to availability broadcasts
                var availableSub = MessageBus.Subscribe(IntelligenceTopics.Available, async msg =>
                {
                    var capabilities = IntelligenceCapabilities.None;
                    if (msg.Payload.TryGetValue("capabilities", out var capObj))
                    {
                        if (capObj is IntelligenceCapabilities caps)
                            capabilities = caps;
                        else if (capObj is long longVal)
                            capabilities = (IntelligenceCapabilities)longVal;
                    }

                    UpdateIntelligenceState(true, capabilities);
                    await OnIntelligenceAvailableAsync(capabilities);
                });
                _intelligenceSubscriptions.Add(availableSub);

                // Subscribe to unavailability broadcasts
                var unavailableSub = MessageBus.Subscribe(IntelligenceTopics.Unavailable, async _ =>
                {
                    UpdateIntelligenceState(false, IntelligenceCapabilities.None);
                    await OnIntelligenceUnavailableAsync();
                });
                _intelligenceSubscriptions.Add(unavailableSub);

                // Subscribe to capability changes
                var changedSub = MessageBus.Subscribe(IntelligenceTopics.CapabilitiesChanged, msg =>
                {
                    if (msg.Payload.TryGetValue("capabilities", out var capObj))
                    {
                        var capabilities = IntelligenceCapabilities.None;
                        if (capObj is IntelligenceCapabilities caps)
                            capabilities = caps;
                        else if (capObj is long longVal)
                            capabilities = (IntelligenceCapabilities)longVal;

                        UpdateIntelligenceState(true, capabilities);
                    }
                    return Task.CompletedTask;
                });
                _intelligenceSubscriptions.Add(changedSub);

                // Subscribe to discovery responses
                var discoverResponseSub = MessageBus.Subscribe(IntelligenceTopics.DiscoverResponse, msg =>
                {
                    if (msg.CorrelationId != null && _pendingRequests.TryRemove(msg.CorrelationId, out var tcs))
                    {
                        tcs.TrySetResult(new MessageResponse
                        {
                            Success = true,
                            Payload = msg.Payload
                        });
                    }
                    return Task.CompletedTask;
                });
                _intelligenceSubscriptions.Add(discoverResponseSub);
            }
            catch
            {
                // Graceful degradation
            }
        }

        // ========================================
        // Lifecycle Hooks for Derived Classes
        // ========================================

        /// <summary>
        /// Called during startup when Intelligence is available.
        /// Override to initialize Intelligence-enhanced features.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the async operation.</returns>
        protected virtual Task OnStartWithIntelligenceAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Called during startup when Intelligence is NOT available.
        /// Override to initialize fallback behavior.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the async operation.</returns>
        protected virtual Task OnStartWithoutIntelligenceAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Called during startup after Intelligence-specific initialization.
        /// Override for common initialization logic.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the async operation.</returns>
        protected virtual Task OnStartCoreAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Called during shutdown for cleanup.
        /// Override for custom cleanup logic.
        /// </summary>
        /// <returns>A task representing the async operation.</returns>
        protected virtual Task OnStopCoreAsync() => Task.CompletedTask;

        // ========================================
        // IIntelligenceAwareNotifiable Implementation
        // ========================================

        /// <summary>
        /// Called when Universal Intelligence becomes available.
        /// </summary>
        public virtual Task OnIntelligenceAvailableAsync(IntelligenceCapabilities capabilities, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when Universal Intelligence becomes unavailable.
        /// </summary>
        public virtual Task OnIntelligenceUnavailableAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        // ========================================
        // Helper Methods for AI Requests
        // ========================================

        /// <summary>
        /// Sends an Intelligence request and waits for a response.
        /// </summary>
        /// <param name="topic">The request topic.</param>
        /// <param name="payload">The request payload.</param>
        /// <param name="timeout">Optional timeout override.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The response, or null if the request failed or timed out.</returns>
        protected async Task<MessageResponse?> SendIntelligenceRequestAsync(
            string topic,
            Dictionary<string, object> payload,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null)
                return null;

            var correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageResponse>();
            _pendingRequests[correlationId] = tcs;

            try
            {
                // Subscribe to response topic
                var responseTopic = $"{topic}.response";
                IDisposable? responseSub = null;

                try
                {
                    responseSub = MessageBus.Subscribe(responseTopic, msg =>
                    {
                        if (msg.CorrelationId == correlationId && _pendingRequests.TryRemove(correlationId, out var pendingTcs))
                        {
                            pendingTcs.TrySetResult(new MessageResponse
                            {
                                Success = msg.Payload.TryGetValue("success", out var s) && s is true,
                                Payload = msg.Payload
                            });
                        }
                        return Task.CompletedTask;
                    });

                    // Send request
                    var request = new PluginMessage
                    {
                        Type = $"{topic}.request",
                        CorrelationId = correlationId,
                        Source = Id,
                        Payload = payload
                    };

                    await MessageBus.PublishAsync(topic, request, ct);

                    // Wait for response
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

                    return await tcs.Task.WaitAsync(cts.Token);
                }
                finally
                {
                    responseSub?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                _pendingRequests.TryRemove(correlationId, out _);
            }
        }

        /// <summary>
        /// Requests embeddings from Intelligence.
        /// </summary>
        /// <param name="texts">The texts to embed.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Array of embedding vectors, or null if unavailable.</returns>
        protected async Task<float[][]?> RequestEmbeddingsAsync(
            string[] texts,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Embeddings))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["texts"] = texts,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (context?.PreferredModel != null)
                payload["model"] = context.PreferredModel;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestEmbeddings,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("embeddings", out var embObj) && embObj is float[][] embeddings)
                    return embeddings;
            }

            return null;
        }

        /// <summary>
        /// Requests text classification from Intelligence.
        /// </summary>
        /// <param name="text">The text to classify.</param>
        /// <param name="categories">Available categories.</param>
        /// <param name="multiLabel">Whether to allow multiple labels.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Classification results, or null if unavailable.</returns>
        protected async Task<ClassificationResult[]?> RequestClassificationAsync(
            string text,
            string[] categories,
            bool multiLabel = false,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Classification))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["text"] = text,
                ["categories"] = categories,
                ["multiLabel"] = multiLabel,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestClassification,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("classifications", out var classObj) && classObj is ClassificationResult[] classifications)
                    return classifications;
            }

            return null;
        }

        /// <summary>
        /// Requests anomaly detection from Intelligence.
        /// </summary>
        /// <param name="data">The data to analyze.</param>
        /// <param name="sensitivity">Detection sensitivity (0.0-1.0).</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Anomaly detection result, or null if unavailable.</returns>
        protected async Task<AnomalyDetectionResult?> RequestAnomalyDetectionAsync(
            object data,
            double sensitivity = 0.5,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.AnomalyDetection))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["data"] = data,
                ["sensitivity"] = sensitivity,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestAnomalyDetection,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new AnomalyDetectionResult
                {
                    IsAnomaly = result.TryGetValue("isAnomaly", out var a) && a is true,
                    Score = result.TryGetValue("score", out var s) && s is double score ? score : 0.0,
                    Anomalies = result.TryGetValue("anomalies", out var an) && an is AnomalyInfo[] anomalies
                        ? anomalies
                        : Array.Empty<AnomalyInfo>()
                };
            }

            return null;
        }

        /// <summary>
        /// Requests a prediction from Intelligence.
        /// </summary>
        /// <param name="predictionType">Type of prediction to make.</param>
        /// <param name="inputData">Input data for the prediction.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Prediction result, or null if unavailable.</returns>
        protected async Task<PredictionResult?> RequestPredictionAsync(
            string predictionType,
            Dictionary<string, object> inputData,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Prediction))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["predictionType"] = predictionType,
                ["inputData"] = inputData,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestPrediction,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new PredictionResult
                {
                    Prediction = result.TryGetValue("prediction", out var p) ? p : null,
                    Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.0,
                    Metadata = result.TryGetValue("metadata", out var m) && m is Dictionary<string, object> meta
                        ? meta
                        : new Dictionary<string, object>()
                };
            }

            return null;
        }

        /// <summary>
        /// Requests text completion from Intelligence.
        /// </summary>
        /// <param name="prompt">The prompt text.</param>
        /// <param name="systemMessage">Optional system message.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Generated text, or null if unavailable.</returns>
        protected async Task<string?> RequestCompletionAsync(
            string prompt,
            string? systemMessage = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.TextCompletion))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (systemMessage != null)
                payload["systemMessage"] = systemMessage;
            if (context?.MaxTokens != null)
                payload["maxTokens"] = context.MaxTokens.Value;
            if (context?.Temperature != null)
                payload["temperature"] = context.Temperature.Value;
            if (context?.PreferredModel != null)
                payload["model"] = context.PreferredModel;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestCompletion,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("content", out var content) && content is string text)
                    return text;
            }

            return null;
        }

        /// <summary>
        /// Requests summarization from Intelligence.
        /// </summary>
        /// <param name="text">The text to summarize.</param>
        /// <param name="maxLength">Optional maximum summary length.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Summary text, or null if unavailable.</returns>
        protected async Task<string?> RequestSummarizationAsync(
            string text,
            int? maxLength = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.Summarization))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["text"] = text,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (maxLength != null)
                payload["maxLength"] = maxLength.Value;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestSummarization,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("summary", out var summary) && summary is string summaryText)
                    return summaryText;
            }

            return null;
        }

        /// <summary>
        /// Requests entity extraction from Intelligence.
        /// </summary>
        /// <param name="text">The text to analyze.</param>
        /// <param name="entityTypes">Optional entity types to extract.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Extracted entities, or null if unavailable.</returns>
        protected async Task<ExtractedEntity[]?> RequestEntityExtractionAsync(
            string text,
            string[]? entityTypes = null,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.EntityExtraction))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["text"] = text,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            if (entityTypes != null)
                payload["entityTypes"] = entityTypes;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestEntityExtraction,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("entities", out var entities) && entities is ExtractedEntity[] entityArray)
                    return entityArray;
            }

            return null;
        }

        /// <summary>
        /// Requests PII detection from Intelligence.
        /// </summary>
        /// <param name="text">The text to analyze.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>PII detection result, or null if unavailable.</returns>
        protected async Task<PIIDetectionResult?> RequestPIIDetectionAsync(
            string text,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.PIIDetection))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["text"] = text,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestPIIDetection,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return new PIIDetectionResult
                {
                    ContainsPII = result.TryGetValue("containsPII", out var c) && c is true,
                    PIIItems = result.TryGetValue("piiItems", out var items) && items is PIIItem[] piiItems
                        ? piiItems
                        : Array.Empty<PIIItem>()
                };
            }

            return null;
        }

        /// <summary>
        /// Requests semantic search from Intelligence.
        /// </summary>
        /// <param name="query">The search query.</param>
        /// <param name="topK">Maximum number of results.</param>
        /// <param name="context">Optional Intelligence context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Search results, or null if unavailable.</returns>
        protected async Task<SemanticSearchResult[]?> RequestSemanticSearchAsync(
            string query,
            int topK = 10,
            IntelligenceContext? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SemanticSearch))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["query"] = query,
                ["topK"] = topK,
                ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestSemanticSearch,
                payload,
                context?.Timeout,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("results", out var results) && results is SemanticSearchResult[] searchResults)
                    return searchResults;
            }

            return null;
        }

        // ========================================
        // Long-Term Memory Helper Methods
        // ========================================

        /// <summary>
        /// Stores content in long-term memory via Intelligence.
        /// </summary>
        /// <param name="content">The content to store.</param>
        /// <param name="metadata">Optional metadata for the memory.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Memory ID if successful, null otherwise.</returns>
        protected async Task<string?> StoreMemoryAsync(
            string content,
            Dictionary<string, object>? metadata = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.MemoryStorage))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["content"] = content,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            if (metadata != null)
                payload["metadata"] = metadata;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.MemoryStore,
                payload,
                timeout: null,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("memoryId", out var memoryId) && memoryId is string id)
                    return id;
            }

            return null;
        }

        /// <summary>
        /// Recalls memories from long-term storage via Intelligence.
        /// </summary>
        /// <param name="query">The query to search for.</param>
        /// <param name="topK">Maximum number of memories to retrieve.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Retrieved memories, or null if unavailable.</returns>
        protected async Task<RetrievedMemory[]?> RecallMemoriesAsync(
            string query,
            int topK = 5,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.MemoryRetrieval))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["query"] = query,
                ["topK"] = topK,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.MemoryRecall,
                payload,
                timeout: null,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("memories", out var memories) && memories is RetrievedMemory[] retrieved)
                    return retrieved;
            }

            return null;
        }

        /// <summary>
        /// Triggers memory consolidation via Intelligence.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if consolidation was triggered successfully.</returns>
        protected async Task<bool> ConsolidateMemoriesAsync(CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.MemoryConsolidation))
                return false;

            var payload = new Dictionary<string, object>
            {
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.MemoryConsolidate,
                payload,
                timeout: null,
                ct);

            return response?.Success == true;
        }

        // ========================================
        // Tabular Model Helper Methods
        // ========================================

        /// <summary>
        /// Makes a prediction using a tabular model via Intelligence.
        /// </summary>
        /// <param name="row">The input row data.</param>
        /// <param name="modelId">The model identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Prediction result, or null if unavailable.</returns>
        protected async Task<TabularPrediction?> PredictTabularAsync(
            object[] row,
            string modelId,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.TabularClassification) &&
                !HasCapability(IntelligenceCapabilities.TabularRegression))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["row"] = row,
                ["modelId"] = modelId,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.TabularPredict,
                payload,
                timeout: null,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("prediction", out var prediction) && prediction is TabularPrediction pred)
                    return pred;
            }

            return null;
        }

        /// <summary>
        /// Trains a tabular model via Intelligence.
        /// </summary>
        /// <param name="data">Training data.</param>
        /// <param name="targetColumn">Target column name.</param>
        /// <param name="modelId">Model identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if training was initiated successfully.</returns>
        protected async Task<bool> TrainTabularModelAsync(
            object data,
            string targetColumn,
            string modelId,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.TabularClassification) &&
                !HasCapability(IntelligenceCapabilities.TabularRegression))
                return false;

            var payload = new Dictionary<string, object>
            {
                ["data"] = data,
                ["targetColumn"] = targetColumn,
                ["modelId"] = modelId,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.TabularTrain,
                payload,
                timeout: null,
                ct);

            return response?.Success == true;
        }

        /// <summary>
        /// Explains a tabular model prediction via Intelligence.
        /// </summary>
        /// <param name="row">The input row data.</param>
        /// <param name="modelId">The model identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Explanation with feature importance, or null if unavailable.</returns>
        protected async Task<TabularPrediction?> ExplainTabularPredictionAsync(
            object[] row,
            string modelId,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.TabularClassification) &&
                !HasCapability(IntelligenceCapabilities.TabularRegression))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["row"] = row,
                ["modelId"] = modelId,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.TabularExplain,
                payload,
                timeout: null,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("explanation", out var explanation) && explanation is TabularPrediction pred)
                    return pred;
            }

            return null;
        }

        // ========================================
        // Agent Helper Methods
        // ========================================

        /// <summary>
        /// Executes a task using an AI agent via Intelligence.
        /// </summary>
        /// <param name="task">The task description.</param>
        /// <param name="agentType">Type of agent to use (e.g., "react", "autogpt").</param>
        /// <param name="context">Optional agent context with tools and constraints.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Agent execution result, or null if unavailable.</returns>
        protected async Task<AgentExecutionResult?> ExecuteAgentTaskAsync(
            string task,
            string agentType = "react",
            object? context = null,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.TaskPlanning) &&
                !HasCapability(IntelligenceCapabilities.ToolUse))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["task"] = task,
                ["agentType"] = agentType,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            if (context != null)
                payload["context"] = context;

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.AgentExecute,
                payload,
                timeout: TimeSpan.FromMinutes(5),
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("result", out var execResult) && execResult is AgentExecutionResult agentResult)
                    return agentResult;
            }

            return null;
        }

        /// <summary>
        /// Registers a tool for agent use via Intelligence.
        /// </summary>
        /// <param name="toolDefinition">The tool definition.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if registration was successful.</returns>
        protected async Task<bool> RegisterAgentToolAsync(
            object toolDefinition,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.ToolUse))
                return false;

            var payload = new Dictionary<string, object>
            {
                ["tool"] = toolDefinition,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.AgentRegisterTool,
                payload,
                timeout: null,
                ct);

            return response?.Success == true;
        }

        /// <summary>
        /// Gets the current state of an agent via Intelligence.
        /// </summary>
        /// <param name="agentId">The agent identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Agent state, or null if unavailable.</returns>
        protected async Task<object?> GetAgentStateAsync(
            string agentId,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.TaskPlanning))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["agentId"] = agentId,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.AgentState,
                payload,
                timeout: null,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                return result.TryGetValue("state", out var state) ? state : null;
            }

            return null;
        }

        // ========================================
        // Evolving Intelligence Helper Methods
        // ========================================

        /// <summary>
        /// Records a learning interaction to improve the Intelligence system.
        /// </summary>
        /// <param name="query">The original query.</param>
        /// <param name="response">The generated response.</param>
        /// <param name="feedback">User feedback (e.g., "positive", "negative", rating).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if learning was recorded successfully.</returns>
        protected async Task<bool> LearnFromInteractionAsync(
            string query,
            string response,
            string feedback,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SelfReflection))
                return false;

            var payload = new Dictionary<string, object>
            {
                ["query"] = query,
                ["response"] = response,
                ["feedback"] = feedback,
                ["contextId"] = Guid.NewGuid().ToString("N"),
                ["timestamp"] = DateTimeOffset.UtcNow
            };

            var response_msg = await SendIntelligenceRequestAsync(
                IntelligenceTopics.EvolutionLearn,
                payload,
                timeout: null,
                ct);

            return response_msg?.Success == true;
        }

        /// <summary>
        /// Gets the expertise score for a specific domain.
        /// </summary>
        /// <param name="domain">The domain to query (e.g., "classification", "embeddings").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Expertise score (0.0-1.0), or null if unavailable.</returns>
        protected async Task<double?> GetExpertiseScoreAsync(
            string domain,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SelfReflection))
                return null;

            var payload = new Dictionary<string, object>
            {
                ["domain"] = domain,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.EvolutionExpertise,
                payload,
                timeout: null,
                ct);

            if (response?.Success == true && response.Payload is Dictionary<string, object> result)
            {
                if (result.TryGetValue("expertiseScore", out var score) && score is double expertiseScore)
                    return expertiseScore;
            }

            return null;
        }

        /// <summary>
        /// Triggers adaptive behavior adjustment via Intelligence.
        /// </summary>
        /// <param name="performanceMetrics">Performance metrics to guide adaptation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if adaptation was triggered successfully.</returns>
        protected async Task<bool> AdaptIntelligenceBehaviorAsync(
            Dictionary<string, object> performanceMetrics,
            CancellationToken ct = default)
        {
            if (!HasCapability(IntelligenceCapabilities.SelfReflection))
                return false;

            var payload = new Dictionary<string, object>
            {
                ["metrics"] = performanceMetrics,
                ["contextId"] = Guid.NewGuid().ToString("N"),
                ["timestamp"] = DateTimeOffset.UtcNow
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.EvolutionAdapt,
                payload,
                timeout: null,
                ct);

            return response?.Success == true;
        }

        // ========================================
        // Metadata Override
        // ========================================

        /// <summary>
        /// Gets metadata including Intelligence awareness information.
        /// </summary>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["IntelligenceAware"] = true;
            metadata["IntelligenceAvailable"] = IsIntelligenceAvailable;
            metadata["AvailableCapabilities"] = AvailableCapabilities.ToString();
            return metadata;
        }
    }

    // ========================================
    // Supporting Types
    // ========================================

    /// <summary>
    /// Result of a classification operation.
    /// </summary>
    public sealed class ClassificationResult
    {
        /// <summary>The classified category.</summary>
        public string Category { get; init; } = string.Empty;

        /// <summary>Confidence score (0.0-1.0).</summary>
        public double Confidence { get; init; }
    }

    /// <summary>
    /// Result of an anomaly detection operation.
    /// </summary>
    public sealed class AnomalyDetectionResult
    {
        /// <summary>Whether the data is anomalous overall.</summary>
        public bool IsAnomaly { get; init; }

        /// <summary>Overall anomaly score (0.0-1.0).</summary>
        public double Score { get; init; }

        /// <summary>Specific anomalies detected.</summary>
        public AnomalyInfo[] Anomalies { get; init; } = Array.Empty<AnomalyInfo>();
    }

    /// <summary>
    /// Information about a specific anomaly.
    /// </summary>
    public sealed class AnomalyInfo
    {
        /// <summary>Description of the anomaly.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Anomaly score (0.0-1.0).</summary>
        public double Score { get; init; }

        /// <summary>Location or field where anomaly was detected.</summary>
        public string? Location { get; init; }
    }

    /// <summary>
    /// Result of a prediction operation.
    /// </summary>
    public sealed class PredictionResult
    {
        /// <summary>The prediction value.</summary>
        public object? Prediction { get; init; }

        /// <summary>Confidence in the prediction (0.0-1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Additional metadata about the prediction.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// An extracted entity from text.
    /// </summary>
    public sealed class ExtractedEntity
    {
        /// <summary>The entity text.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Entity type (e.g., "PERSON", "ORGANIZATION").</summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>Confidence score (0.0-1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Start position in the original text.</summary>
        public int StartIndex { get; init; }

        /// <summary>End position in the original text.</summary>
        public int EndIndex { get; init; }
    }

    /// <summary>
    /// Result of PII detection.
    /// </summary>
    public sealed class PIIDetectionResult
    {
        /// <summary>Whether PII was found.</summary>
        public bool ContainsPII { get; init; }

        /// <summary>Detected PII items.</summary>
        public PIIItem[] PIIItems { get; init; } = Array.Empty<PIIItem>();
    }

    /// <summary>
    /// A detected PII item.
    /// </summary>
    public sealed class PIIItem
    {
        /// <summary>PII type (e.g., "EMAIL", "SSN", "PHONE").</summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>The detected PII value (may be redacted).</summary>
        public string Value { get; init; } = string.Empty;

        /// <summary>Confidence score (0.0-1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Start position in the original text.</summary>
        public int StartIndex { get; init; }

        /// <summary>End position in the original text.</summary>
        public int EndIndex { get; init; }
    }

    /// <summary>
    /// A semantic search result.
    /// </summary>
    public sealed class SemanticSearchResult
    {
        /// <summary>The matched item identifier.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Similarity score (0.0-1.0).</summary>
        public double Score { get; init; }

        /// <summary>The matched content or snippet.</summary>
        public string? Content { get; init; }

        /// <summary>Additional metadata about the result.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Retrieved memory with relevance score.
    /// </summary>
    public sealed class RetrievedMemory
    {
        /// <summary>Memory ID.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Memory content.</summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>Relevance score (0.0-1.0).</summary>
        public float RelevanceScore { get; init; }

        /// <summary>When the memory was created.</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>Optional metadata.</summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Result of a tabular model prediction.
    /// </summary>
    public sealed class TabularPrediction
    {
        /// <summary>Predicted value or class label.</summary>
        public object? PredictedValue { get; init; }

        /// <summary>Confidence score (0.0-1.0).</summary>
        public double Confidence { get; init; }

        /// <summary>Class probabilities for classification tasks.</summary>
        public Dictionary<string, double>? ClassProbabilities { get; init; }

        /// <summary>Feature importance values for this prediction.</summary>
        public Dictionary<string, double>? FeatureImportance { get; init; }

        /// <summary>Additional metadata.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Result of agent task execution.
    /// </summary>
    public sealed class AgentExecutionResult
    {
        /// <summary>Whether the task was completed successfully.</summary>
        public bool Success { get; init; }

        /// <summary>Final result or output.</summary>
        public string Result { get; init; } = string.Empty;

        /// <summary>Number of steps/iterations taken.</summary>
        public int StepsTaken { get; init; }

        /// <summary>Reasoning chain or thought process.</summary>
        public List<string> ReasoningChain { get; init; } = new();

        /// <summary>Total tokens consumed.</summary>
        public int TokensConsumed { get; init; }

        /// <summary>Execution duration in milliseconds.</summary>
        public long DurationMs { get; init; }

        /// <summary>Error message if failed.</summary>
        public string? Error { get; init; }
    }
}
