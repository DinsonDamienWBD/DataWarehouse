using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Services
{
    // ================================================================================
    // T127 Phase C: CLI/GUI Intelligence Integration Service
    // Provides conversational AI interface capabilities for CLI/GUI applications.
    // ================================================================================

    #region Main Service

    /// <summary>
    /// Provides conversational AI interface capabilities for CLI/GUI applications.
    /// Enables natural language command parsing, AI agent orchestration, and smart
    /// recommendations through the Universal Intelligence (T90) system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This service acts as the bridge between user interfaces (CLI, GUI) and the
    /// T90 Intelligence system, providing:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>C1 - Conversational Interface</b>: Natural language command parsing with context awareness</item>
    ///   <item><b>C2 - AI Agent Mode</b>: Spawning specialized AI agents for complex tasks</item>
    ///   <item><b>C3 - GUI Intelligence Support</b>: Chat panels, search suggestions, and smart recommendations</item>
    /// </list>
    /// <para>
    /// The service gracefully degrades when Intelligence is unavailable, returning null
    /// or empty results rather than throwing exceptions.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var service = new IntelligenceInterfaceService(messageBus);
    /// await service.InitializeAsync();
    ///
    /// // Parse natural language command
    /// var command = await service.ParseNaturalLanguageAsync("Upload this file to S3", ct);
    /// // command.CommandType = "Upload", command.Parameters["destination"] = "s3"
    ///
    /// // Get search suggestions
    /// var suggestions = await service.GetSearchSuggestionsAsync("Q4 sal", ct);
    /// // Returns: "Q4 sales report", "Q4 salary data", etc.
    ///
    /// // Spawn an AI agent for complex tasks
    /// var task = await service.SpawnAgentAsync(AgentType.FileOrganization, "Organize my documents", ct);
    /// </code>
    /// </example>
    public sealed class IntelligenceInterfaceService : IDisposable, IAsyncDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageResponse>> _pendingRequests = new();
        private readonly List<IDisposable> _subscriptions = new();
        private readonly object _historyLock = new();
        private readonly List<ConversationMessage> _history = new();
        private readonly ConcurrentDictionary<string, AgentTask> _activeAgents = new();
        private readonly SemaphoreSlim _initLock = new(1, 1);

        private volatile bool _isIntelligenceAvailable;
        private IntelligenceCapabilities _availableCapabilities = IntelligenceCapabilities.None;
        private volatile bool _initialized;
        private volatile bool _disposed;

        /// <summary>
        /// Default timeout for Intelligence requests.
        /// </summary>
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Timeout for discovery requests.
        /// </summary>
        private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Maximum conversation history entries to maintain.
        /// </summary>
        private const int MaxHistorySize = 100;

        /// <summary>
        /// Initializes a new instance of the <see cref="IntelligenceInterfaceService"/> class.
        /// </summary>
        /// <param name="messageBus">The message bus for communicating with T90 Intelligence.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public IntelligenceInterfaceService(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        /// <summary>
        /// Gets whether Universal Intelligence (T90) is currently available.
        /// </summary>
        public bool IsIntelligenceAvailable => _isIntelligenceAvailable;

        /// <summary>
        /// Gets the capabilities provided by the discovered Intelligence system.
        /// </summary>
        public IntelligenceCapabilities AvailableCapabilities => _availableCapabilities;

        /// <summary>
        /// Gets the current conversation history.
        /// </summary>
        /// <remarks>
        /// Returns a copy to prevent external modification. History is capped at <see cref="MaxHistorySize"/> entries.
        /// </remarks>
        public IReadOnlyList<ConversationMessage> ConversationHistory
        {
            get
            {
                lock (_historyLock)
                {
                    return _history.ToList().AsReadOnly();
                }
            }
        }

        /// <summary>
        /// Gets the active AI agents spawned by this service.
        /// </summary>
        public IReadOnlyDictionary<string, AgentTask> ActiveAgents => _activeAgents;

        /// <summary>
        /// Event raised when Intelligence availability changes.
        /// </summary>
        public event EventHandler<IntelligenceAvailabilityChangedEventArgs>? IntelligenceAvailabilityChanged;

        /// <summary>
        /// Event raised when an agent task status changes.
        /// </summary>
        public event EventHandler<AgentTaskStatusChangedEventArgs>? AgentTaskStatusChanged;

        // ========================================
        // Initialization
        // ========================================

        /// <summary>
        /// Initializes the service and discovers Intelligence availability.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task that completes when initialization is finished.</returns>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _initLock.WaitAsync(ct);
            try
            {
                if (_initialized)
                    return;

                SubscribeToIntelligenceTopics();
                await DiscoverIntelligenceAsync(ct);
                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Subscribes to Intelligence broadcast topics for availability notifications.
        /// </summary>
        private void SubscribeToIntelligenceTopics()
        {
            // Subscribe to availability broadcasts
            var availableSub = _messageBus.Subscribe(IntelligenceTopics.Available, msg =>
            {
                var capabilities = ParseCapabilities(msg.Payload);
                UpdateIntelligenceState(true, capabilities);
                return Task.CompletedTask;
            });
            _subscriptions.Add(availableSub);

            // Subscribe to unavailability broadcasts
            var unavailableSub = _messageBus.Subscribe(IntelligenceTopics.Unavailable, _ =>
            {
                UpdateIntelligenceState(false, IntelligenceCapabilities.None);
                return Task.CompletedTask;
            });
            _subscriptions.Add(unavailableSub);

            // Subscribe to capability changes
            var changedSub = _messageBus.Subscribe(IntelligenceTopics.CapabilitiesChanged, msg =>
            {
                var capabilities = ParseCapabilities(msg.Payload);
                UpdateIntelligenceState(true, capabilities);
                return Task.CompletedTask;
            });
            _subscriptions.Add(changedSub);
        }

        /// <summary>
        /// Attempts to discover Universal Intelligence (T90) via the message bus.
        /// </summary>
        private async Task DiscoverIntelligenceAsync(CancellationToken ct)
        {
            try
            {
                var correlationId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<MessageResponse>();
                _pendingRequests[correlationId] = tcs;

                // Subscribe to discovery response
                IDisposable? responseSub = null;
                try
                {
                    responseSub = _messageBus.Subscribe(IntelligenceTopics.DiscoverResponse, msg =>
                    {
                        if (msg.CorrelationId == correlationId && _pendingRequests.TryRemove(correlationId, out var pendingTcs))
                        {
                            pendingTcs.TrySetResult(new MessageResponse
                            {
                                Success = true,
                                Payload = msg.Payload
                            });
                        }
                        return Task.CompletedTask;
                    });

                    var request = new PluginMessage
                    {
                        Type = "intelligence.discover.request",
                        CorrelationId = correlationId,
                        Source = "IntelligenceInterfaceService",
                        Payload = new Dictionary<string, object>
                        {
                            ["requestorId"] = "IntelligenceInterfaceService",
                            ["timestamp"] = DateTimeOffset.UtcNow
                        }
                    };

                    await _messageBus.PublishAsync(IntelligenceTopics.Discover, request, ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(DiscoveryTimeout);

                    try
                    {
                        var response = await tcs.Task.WaitAsync(cts.Token);
                        if (response.Success && response.Payload is Dictionary<string, object> payload)
                        {
                            var capabilities = ParseCapabilities(payload);
                            UpdateIntelligenceState(true, capabilities);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout - Intelligence not available
                    }
                }
                finally
                {
                    responseSub?.Dispose();
                    _pendingRequests.TryRemove(correlationId, out _);
                }

                UpdateIntelligenceState(false, IntelligenceCapabilities.None);
            }
            catch
            {
                UpdateIntelligenceState(false, IntelligenceCapabilities.None);
            }
        }

        /// <summary>
        /// Updates the Intelligence availability state and raises events.
        /// </summary>
        private void UpdateIntelligenceState(bool available, IntelligenceCapabilities capabilities)
        {
            var previousAvailable = _isIntelligenceAvailable;
            _isIntelligenceAvailable = available;
            _availableCapabilities = capabilities;

            if (previousAvailable != available)
            {
                IntelligenceAvailabilityChanged?.Invoke(this, new IntelligenceAvailabilityChangedEventArgs
                {
                    IsAvailable = available,
                    Capabilities = capabilities,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        }

        /// <summary>
        /// Parses capabilities from a message payload.
        /// </summary>
        private static IntelligenceCapabilities ParseCapabilities(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("capabilities", out var capObj))
            {
                if (capObj is IntelligenceCapabilities caps)
                    return caps;
                if (capObj is long longVal)
                    return (IntelligenceCapabilities)longVal;
                if (long.TryParse(capObj?.ToString(), out var parsed))
                    return (IntelligenceCapabilities)parsed;
            }
            return IntelligenceCapabilities.None;
        }

        /// <summary>
        /// Checks if a specific capability is available.
        /// </summary>
        private bool HasCapability(IntelligenceCapabilities capability)
        {
            return _isIntelligenceAvailable && (_availableCapabilities & capability) == capability;
        }

        // ========================================
        // C1: Conversational Interface Service
        // ========================================

        /// <summary>
        /// Parses a natural language command into a structured command with AI-parsed parameters.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Supports parsing commands such as:
        /// </para>
        /// <list type="bullet">
        ///   <item>"Upload this file" -> Upload command with parsed parameters (127.C1.2)</item>
        ///   <item>"Find documents about Q4 sales" -> Semantic search query (127.C1.3)</item>
        ///   <item>"Encrypt everything with military-grade" -> Selects AES-256-GCM (127.C1.4)</item>
        /// </list>
        /// <para>
        /// The conversation history is maintained for context-aware follow-ups (127.C1.5).
        /// </para>
        /// </remarks>
        /// <param name="input">The natural language input from the user.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A <see cref="ParsedCommand"/> containing the structured command, or null if
        /// Intelligence is unavailable or parsing failed.
        /// </returns>
        public async Task<ParsedCommand?> ParseNaturalLanguageAsync(string input, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrWhiteSpace(input))
                return null;

            // 127.C1.1: Accept natural language commands when Intelligence available
            if (!HasCapability(IntelligenceCapabilities.IntentRecognition) &&
                !HasCapability(IntelligenceCapabilities.NLP))
            {
                return null;
            }

            // Add user message to history
            AddToHistory(new ConversationMessage
            {
                Role = ConversationRole.User,
                Content = input,
                Timestamp = DateTimeOffset.UtcNow
            });

            var payload = new Dictionary<string, object>
            {
                ["input"] = input,
                ["contextId"] = Guid.NewGuid().ToString("N"),
                ["commandContext"] = GetCommandContext()
            };

            // Include conversation history for context-aware follow-ups (127.C1.5)
            lock (_historyLock)
            {
                if (_history.Count > 0)
                {
                    payload["conversationHistory"] = _history.Select(m => new Dictionary<string, object>
                    {
                        ["role"] = m.Role.ToString().ToLowerInvariant(),
                        ["content"] = m.Content,
                        ["timestamp"] = m.Timestamp.ToString("O")
                    }).ToList();
                }
            }

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.parse-command",
                payload,
                DefaultTimeout,
                ct);

            if (response?.Success != true || response.Payload is not Dictionary<string, object> result)
                return null;

            var parsedCommand = new ParsedCommand
            {
                OriginalInput = input,
                CommandType = result.TryGetValue("commandType", out var ct2) && ct2 is string cmdType ? cmdType : "Unknown",
                Intent = result.TryGetValue("intent", out var intent) && intent is string intentStr ? intentStr : null,
                Parameters = result.TryGetValue("parameters", out var p) && p is Dictionary<string, object> parameters ? parameters : new Dictionary<string, object>(),
                Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.0,
                RequiresConfirmation = result.TryGetValue("requiresConfirmation", out var rc) && rc is true,
                SuggestedAction = result.TryGetValue("suggestedAction", out var sa) && sa is string action ? action : null,
                AlternativeInterpretations = result.TryGetValue("alternatives", out var alts) && alts is List<object> altList
                    ? altList.OfType<string>().ToArray()
                    : Array.Empty<string>()
            };

            // 127.C1.2: Parse "Upload this file" -> structured command
            if (parsedCommand.CommandType == "Upload" || parsedCommand.Intent == "file.upload")
            {
                EnrichUploadCommand(parsedCommand, input);
            }
            // 127.C1.3: Parse "Find documents about X" -> semantic search query
            else if (parsedCommand.CommandType == "Search" || parsedCommand.Intent == "document.search")
            {
                EnrichSearchCommand(parsedCommand, input);
            }
            // 127.C1.4: Parse encryption commands -> select appropriate cipher
            else if (parsedCommand.CommandType == "Encrypt" || parsedCommand.Intent == "security.encrypt")
            {
                EnrichEncryptionCommand(parsedCommand, input);
            }

            // Add assistant response to history
            AddToHistory(new ConversationMessage
            {
                Role = ConversationRole.Assistant,
                Content = $"Parsed command: {parsedCommand.CommandType} with {parsedCommand.Parameters.Count} parameters",
                Timestamp = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    ["parsedCommand"] = parsedCommand.CommandType,
                    ["confidence"] = parsedCommand.Confidence
                }
            });

            return parsedCommand;
        }

        /// <summary>
        /// Gets command context for natural language parsing.
        /// </summary>
        private static Dictionary<string, object> GetCommandContext()
        {
            return new Dictionary<string, object>
            {
                ["availableCommands"] = new[]
                {
                    "Upload", "Download", "Search", "List", "Delete", "Move", "Copy",
                    "Encrypt", "Decrypt", "Compress", "Decompress", "Tag", "Share"
                },
                ["supportedProtocols"] = new[] { "file", "s3", "azure", "gcs", "ftp", "sftp" },
                ["encryptionAlgorithms"] = new[] { "AES-256-GCM", "AES-256-CBC", "ChaCha20-Poly1305" },
                ["compressionAlgorithms"] = new[] { "gzip", "brotli", "zstd", "lz4" }
            };
        }

        /// <summary>
        /// Enriches an upload command with parsed file and destination parameters.
        /// </summary>
        private static void EnrichUploadCommand(ParsedCommand command, string input)
        {
            // Parse common upload patterns
            var pathMatch = Regex.Match(input, @"(?:upload|send|put)\s+(?:file\s+)?[""']?([^""']+)[""']?\s+(?:to\s+)?(\w+)?", RegexOptions.IgnoreCase);
            if (pathMatch.Success)
            {
                if (!command.Parameters.ContainsKey("sourcePath"))
                    command.Parameters["sourcePath"] = pathMatch.Groups[1].Value.Trim();
                if (pathMatch.Groups[2].Success && !command.Parameters.ContainsKey("destination"))
                    command.Parameters["destination"] = pathMatch.Groups[2].Value.Trim();
            }
        }

        /// <summary>
        /// Enriches a search command with semantic query parameters.
        /// </summary>
        private static void EnrichSearchCommand(ParsedCommand command, string input)
        {
            command.Parameters["searchType"] = "semantic";
            // Extract search terms from common patterns
            var queryMatch = Regex.Match(input, @"(?:find|search|look for)\s+(?:documents?|files?)?\s*(?:about|for|containing|with)?\s*(.+)", RegexOptions.IgnoreCase);
            if (queryMatch.Success && !command.Parameters.ContainsKey("query"))
            {
                command.Parameters["query"] = queryMatch.Groups[1].Value.Trim();
            }
        }

        /// <summary>
        /// Enriches an encryption command with cipher selection based on natural language.
        /// </summary>
        private static void EnrichEncryptionCommand(ParsedCommand command, string input)
        {
            // 127.C1.4: Map "military-grade" to AES-256-GCM
            var lowerInput = input.ToLowerInvariant();
            if (lowerInput.Contains("military") || lowerInput.Contains("highest") || lowerInput.Contains("maximum") || lowerInput.Contains("top secret"))
            {
                command.Parameters["algorithm"] = "AES-256-GCM";
                command.Parameters["keySize"] = 256;
                command.Parameters["securityLevel"] = "Military";
            }
            else if (lowerInput.Contains("fast") || lowerInput.Contains("quick") || lowerInput.Contains("performance"))
            {
                command.Parameters["algorithm"] = "ChaCha20-Poly1305";
                command.Parameters["securityLevel"] = "High";
            }
            else
            {
                // Default to strong encryption
                command.Parameters["algorithm"] = "AES-256-GCM";
                command.Parameters["securityLevel"] = "Standard";
            }
        }

        /// <summary>
        /// Adds a message to conversation history with size management.
        /// </summary>
        private void AddToHistory(ConversationMessage message)
        {
            lock (_historyLock)
            {
                _history.Add(message);
                // Maintain maximum history size (127.C1.5)
                while (_history.Count > MaxHistorySize)
                {
                    _history.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Clears the conversation history.
        /// </summary>
        public void ClearConversationHistory()
        {
            lock (_historyLock)
            {
                _history.Clear();
            }
        }

        // ========================================
        // C2: AI Agent Mode
        // ========================================

        /// <summary>
        /// Spawns an AI agent for a complex task.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Supports spawning agents for tasks such as:
        /// </para>
        /// <list type="bullet">
        ///   <item>"Organize my files" -> File organization agent (127.C2.2)</item>
        ///   <item>"Optimize storage costs" -> Tiering analysis agent (127.C2.3)</item>
        ///   <item>"Check for compliance issues" -> Compliance audit agent (127.C2.4)</item>
        /// </list>
        /// </remarks>
        /// <param name="type">The type of agent to spawn.</param>
        /// <param name="objective">The natural language objective for the agent.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// An <see cref="AgentTask"/> representing the spawned agent, or null if
        /// Intelligence is unavailable.
        /// </returns>
        public async Task<AgentTask?> SpawnAgentAsync(AgentType type, string objective, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 127.C2.1: Support spawning AI agents for complex tasks
            if (!HasCapability(IntelligenceCapabilities.FunctionCalling) &&
                !HasCapability(IntelligenceCapabilities.Conversation))
            {
                return null;
            }

            var taskId = Guid.NewGuid().ToString("N");
            var agentTask = new AgentTask
            {
                TaskId = taskId,
                AgentType = type,
                Objective = objective,
                Status = AgentTaskStatus.Initializing,
                CreatedAt = DateTimeOffset.UtcNow,
                Progress = 0.0
            };

            _activeAgents[taskId] = agentTask;
            RaiseAgentStatusChanged(agentTask);

            var payload = new Dictionary<string, object>
            {
                ["taskId"] = taskId,
                ["agentType"] = type.ToString(),
                ["objective"] = objective,
                ["contextId"] = Guid.NewGuid().ToString("N"),
                ["agentConfig"] = GetAgentConfiguration(type)
            };

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.spawn-agent",
                payload,
                DefaultTimeout,
                ct);

            if (response?.Success != true)
            {
                agentTask.Status = AgentTaskStatus.Failed;
                agentTask.ErrorMessage = response?.ErrorMessage ?? "Failed to spawn agent";
                RaiseAgentStatusChanged(agentTask);
                return agentTask;
            }

            agentTask.Status = AgentTaskStatus.Running;
            RaiseAgentStatusChanged(agentTask);

            // Subscribe to agent progress updates
            SubscribeToAgentUpdates(taskId);

            return agentTask;
        }

        /// <summary>
        /// Gets agent configuration based on agent type.
        /// </summary>
        private static Dictionary<string, object> GetAgentConfiguration(AgentType type)
        {
            return type switch
            {
                // 127.C2.2: File organization agent
                AgentType.FileOrganization => new Dictionary<string, object>
                {
                    ["capabilities"] = new[] { "analyze", "categorize", "move", "rename", "tag" },
                    ["rules"] = new[] { "group_by_type", "group_by_date", "detect_duplicates" }
                },
                // 127.C2.3: Tiering analysis agent
                AgentType.StorageOptimization => new Dictionary<string, object>
                {
                    ["capabilities"] = new[] { "analyze_access_patterns", "recommend_tier", "estimate_costs" },
                    ["tiers"] = new[] { "hot", "warm", "cool", "archive" }
                },
                // 127.C2.4: Compliance audit agent
                AgentType.ComplianceAudit => new Dictionary<string, object>
                {
                    ["capabilities"] = new[] { "detect_pii", "classify_sensitivity", "check_retention" },
                    ["frameworks"] = new[] { "GDPR", "HIPAA", "SOC2", "PCI-DSS" }
                },
                AgentType.DataMigration => new Dictionary<string, object>
                {
                    ["capabilities"] = new[] { "analyze_source", "validate_schema", "transform", "migrate" }
                },
                AgentType.SecurityAssessment => new Dictionary<string, object>
                {
                    ["capabilities"] = new[] { "scan_vulnerabilities", "check_encryption", "audit_access" }
                },
                _ => new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Subscribes to progress updates for an agent task.
        /// </summary>
        private void SubscribeToAgentUpdates(string taskId)
        {
            var progressTopic = $"intelligence.agent.{taskId}.progress";
            var sub = _messageBus.Subscribe(progressTopic, msg =>
            {
                if (_activeAgents.TryGetValue(taskId, out var task))
                {
                    if (msg.Payload.TryGetValue("progress", out var p) && p is double progress)
                        task.Progress = progress;
                    if (msg.Payload.TryGetValue("status", out var s) && s is string status)
                        task.Status = Enum.TryParse<AgentTaskStatus>(status, true, out var st) ? st : task.Status;
                    if (msg.Payload.TryGetValue("message", out var m) && m is string message)
                        task.StatusMessage = message;
                    if (msg.Payload.TryGetValue("result", out var r))
                        task.Result = r;

                    task.LastUpdatedAt = DateTimeOffset.UtcNow;
                    RaiseAgentStatusChanged(task);
                }
                return Task.CompletedTask;
            });
            _subscriptions.Add(sub);
        }

        /// <summary>
        /// Cancels an active agent task.
        /// </summary>
        /// <param name="taskId">The task ID to cancel.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if cancellation was requested successfully.</returns>
        public async Task<bool> CancelAgentTaskAsync(string taskId, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_activeAgents.TryGetValue(taskId, out var task))
                return false;

            if (task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled)
                return false;

            var payload = new Dictionary<string, object>
            {
                ["taskId"] = taskId,
                ["action"] = "cancel"
            };

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.agent-control",
                payload,
                TimeSpan.FromSeconds(5),
                ct);

            if (response?.Success == true)
            {
                task.Status = AgentTaskStatus.Cancelled;
                task.LastUpdatedAt = DateTimeOffset.UtcNow;
                RaiseAgentStatusChanged(task);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Raises the agent status changed event.
        /// </summary>
        private void RaiseAgentStatusChanged(AgentTask task)
        {
            AgentTaskStatusChanged?.Invoke(this, new AgentTaskStatusChangedEventArgs
            {
                Task = task,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        // ========================================
        // C3: GUI Intelligence Support
        // ========================================

        /// <summary>
        /// Gets AI-powered search suggestions based on partial input.
        /// </summary>
        /// <remarks>
        /// Provides type-ahead suggestions using semantic understanding (127.C3.2).
        /// </remarks>
        /// <param name="partial">The partial search query.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A collection of <see cref="SearchSuggestion"/> objects, or empty if unavailable.
        /// </returns>
        public async Task<IEnumerable<SearchSuggestion>> GetSearchSuggestionsAsync(string partial, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrWhiteSpace(partial))
                return Enumerable.Empty<SearchSuggestion>();

            // 127.C3.2: AI-powered search suggestions
            if (!HasCapability(IntelligenceCapabilities.SemanticSearch) &&
                !HasCapability(IntelligenceCapabilities.NLP))
            {
                return Enumerable.Empty<SearchSuggestion>();
            }

            var payload = new Dictionary<string, object>
            {
                ["partial"] = partial,
                ["maxSuggestions"] = 10,
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.search-suggestions",
                payload,
                TimeSpan.FromSeconds(2), // Quick timeout for UI responsiveness
                ct);

            if (response?.Success != true || response.Payload is not Dictionary<string, object> result)
                return Enumerable.Empty<SearchSuggestion>();

            if (!result.TryGetValue("suggestions", out var suggestionsObj) || suggestionsObj is not List<object> suggestionsList)
                return Enumerable.Empty<SearchSuggestion>();

            return suggestionsList
                .OfType<Dictionary<string, object>>()
                .Select(s => new SearchSuggestion
                {
                    Text = s.TryGetValue("text", out var t) && t is string text ? text : string.Empty,
                    Category = s.TryGetValue("category", out var c) && c is string cat ? cat : null,
                    Score = s.TryGetValue("score", out var sc) && sc is double score ? score : 0.0,
                    Metadata = s.TryGetValue("metadata", out var m) && m is Dictionary<string, object> meta ? meta : new Dictionary<string, object>()
                })
                .Where(s => !string.IsNullOrEmpty(s.Text))
                .ToList();
        }

        /// <summary>
        /// Gets smart recommendations based on file context.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Provides intelligent recommendations such as (127.C3.3):
        /// </para>
        /// <list type="bullet">
        ///   <item>"This file looks like PII, encrypt?"</item>
        ///   <item>"This file hasn't been accessed in 90 days, archive?"</item>
        ///   <item>"Similar file exists, deduplicate?"</item>
        /// </list>
        /// </remarks>
        /// <param name="context">The file context to analyze.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A collection of <see cref="SmartRecommendation"/> objects, or empty if unavailable.
        /// </returns>
        public async Task<IEnumerable<SmartRecommendation>> GetRecommendationsAsync(FileContext context, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (context == null)
                return Enumerable.Empty<SmartRecommendation>();

            // 127.C3.3: Smart recommendations
            if (!HasCapability(IntelligenceCapabilities.Classification) &&
                !HasCapability(IntelligenceCapabilities.PIIDetection))
            {
                return Enumerable.Empty<SmartRecommendation>();
            }

            var payload = new Dictionary<string, object>
            {
                ["fileName"] = context.FileName ?? string.Empty,
                ["fileType"] = context.FileType ?? string.Empty,
                ["fileSize"] = context.FileSize,
                ["lastAccessed"] = context.LastAccessed?.ToString("O") ?? string.Empty,
                ["lastModified"] = context.LastModified?.ToString("O") ?? string.Empty,
                ["tags"] = context.Tags ?? Array.Empty<string>(),
                ["metadata"] = context.Metadata ?? new Dictionary<string, object>(),
                ["contextId"] = Guid.NewGuid().ToString("N")
            };

            if (!string.IsNullOrEmpty(context.ContentSample))
            {
                payload["contentSample"] = context.ContentSample;
            }

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.smart-recommendations",
                payload,
                DefaultTimeout,
                ct);

            if (response?.Success != true || response.Payload is not Dictionary<string, object> result)
                return Enumerable.Empty<SmartRecommendation>();

            if (!result.TryGetValue("recommendations", out var recsObj) || recsObj is not List<object> recsList)
                return Enumerable.Empty<SmartRecommendation>();

            return recsList
                .OfType<Dictionary<string, object>>()
                .Select(r => new SmartRecommendation
                {
                    Id = r.TryGetValue("id", out var id) && id is string idStr ? idStr : Guid.NewGuid().ToString("N"),
                    Type = r.TryGetValue("type", out var t) && t is string typeStr && Enum.TryParse<RecommendationType>(typeStr, true, out var rt) ? rt : RecommendationType.General,
                    Title = r.TryGetValue("title", out var title) && title is string titleStr ? titleStr : string.Empty,
                    Description = r.TryGetValue("description", out var desc) && desc is string descStr ? descStr : string.Empty,
                    Severity = r.TryGetValue("severity", out var sev) && sev is string sevStr && Enum.TryParse<RecommendationSeverity>(sevStr, true, out var rs) ? rs : RecommendationSeverity.Info,
                    Confidence = r.TryGetValue("confidence", out var conf) && conf is double confVal ? confVal : 0.0,
                    SuggestedAction = r.TryGetValue("suggestedAction", out var action) && action is string actionStr ? actionStr : null,
                    ActionParameters = r.TryGetValue("actionParameters", out var ap) && ap is Dictionary<string, object> apDict ? apDict : new Dictionary<string, object>(),
                    AutoApplyable = r.TryGetValue("autoApplyable", out var auto) && auto is true
                })
                .Where(r => !string.IsNullOrEmpty(r.Title))
                .ToList();
        }

        /// <summary>
        /// Builds a filter expression from a natural language description.
        /// </summary>
        /// <remarks>
        /// Converts natural language filter descriptions to structured filter expressions (127.C3.4).
        /// Example: "files larger than 10MB created this month" -> structured filter.
        /// </remarks>
        /// <param name="description">The natural language filter description.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A <see cref="FilterExpression"/> representing the filter, or null if unavailable.
        /// </returns>
        public async Task<FilterExpression?> BuildFilterFromNaturalLanguageAsync(string description, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrWhiteSpace(description))
                return null;

            // 127.C3.4: Natural language filter builder
            if (!HasCapability(IntelligenceCapabilities.NLP) &&
                !HasCapability(IntelligenceCapabilities.IntentRecognition))
            {
                return null;
            }

            var payload = new Dictionary<string, object>
            {
                ["description"] = description,
                ["contextId"] = Guid.NewGuid().ToString("N"),
                ["filterContext"] = new Dictionary<string, object>
                {
                    ["availableFields"] = new[]
                    {
                        "fileName", "fileType", "fileSize", "createdAt", "modifiedAt",
                        "lastAccessed", "tags", "contentType", "owner", "path"
                    },
                    ["operators"] = new[]
                    {
                        "equals", "notEquals", "contains", "startsWith", "endsWith",
                        "greaterThan", "lessThan", "between", "in", "notIn"
                    }
                }
            };

            var response = await SendIntelligenceRequestAsync(
                "intelligence.request.build-filter",
                payload,
                DefaultTimeout,
                ct);

            if (response?.Success != true || response.Payload is not Dictionary<string, object> result)
                return null;

            return new FilterExpression
            {
                OriginalDescription = description,
                Field = result.TryGetValue("field", out var f) && f is string field ? field : null,
                Operator = result.TryGetValue("operator", out var op) && op is string opStr ? opStr : "equals",
                Value = result.TryGetValue("value", out var v) ? v : null,
                LogicalOperator = result.TryGetValue("logicalOperator", out var lo) && lo is string loStr ? loStr : null,
                SubExpressions = ParseSubExpressions(result),
                IsCompound = result.TryGetValue("isCompound", out var ic) && ic is true,
                Confidence = result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.0
            };
        }

        /// <summary>
        /// Parses sub-expressions from a filter result.
        /// </summary>
        private static List<FilterExpression> ParseSubExpressions(Dictionary<string, object> result)
        {
            if (!result.TryGetValue("subExpressions", out var subObj) || subObj is not List<object> subList)
                return new List<FilterExpression>();

            return subList
                .OfType<Dictionary<string, object>>()
                .Select(s => new FilterExpression
                {
                    Field = s.TryGetValue("field", out var f) && f is string field ? field : null,
                    Operator = s.TryGetValue("operator", out var op) && op is string opStr ? opStr : "equals",
                    Value = s.TryGetValue("value", out var v) ? v : null,
                    LogicalOperator = s.TryGetValue("logicalOperator", out var lo) && lo is string loStr ? loStr : null
                })
                .ToList();
        }

        /// <summary>
        /// Creates a chat panel data model for AI interaction.
        /// </summary>
        /// <remarks>
        /// Provides a data model suitable for binding to GUI chat panels (127.C3.1).
        /// </remarks>
        /// <param name="sessionId">Optional session ID for conversation continuity.</param>
        /// <returns>A new <see cref="ChatPanelModel"/> instance.</returns>
        public ChatPanelModel CreateChatPanelModel(string? sessionId = null)
        {
            return new ChatPanelModel
            {
                SessionId = sessionId ?? Guid.NewGuid().ToString("N"),
                IsIntelligenceAvailable = _isIntelligenceAvailable,
                AvailableCapabilities = _availableCapabilities,
                Messages = ConversationHistory.ToList()
            };
        }

        /// <summary>
        /// Sends a chat message and gets an AI response.
        /// </summary>
        /// <param name="model">The chat panel model.</param>
        /// <param name="message">The user's message.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The AI response message, or null if unavailable.</returns>
        public async Task<ConversationMessage?> SendChatMessageAsync(ChatPanelModel model, string message, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrWhiteSpace(message) || !HasCapability(IntelligenceCapabilities.Conversation))
                return null;

            var userMessage = new ConversationMessage
            {
                Role = ConversationRole.User,
                Content = message,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = model.SessionId
            };

            model.Messages.Add(userMessage);
            AddToHistory(userMessage);

            var payload = new Dictionary<string, object>
            {
                ["sessionId"] = model.SessionId,
                ["message"] = message,
                ["contextId"] = Guid.NewGuid().ToString("N"),
                ["history"] = model.Messages.Select(m => new Dictionary<string, object>
                {
                    ["role"] = m.Role.ToString().ToLowerInvariant(),
                    ["content"] = m.Content
                }).ToList()
            };

            var response = await SendIntelligenceRequestAsync(
                IntelligenceTopics.RequestConversation,
                payload,
                DefaultTimeout,
                ct);

            if (response?.Success != true || response.Payload is not Dictionary<string, object> result)
                return null;

            var assistantMessage = new ConversationMessage
            {
                Role = ConversationRole.Assistant,
                Content = result.TryGetValue("response", out var r) && r is string resp ? resp : string.Empty,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = model.SessionId,
                Metadata = result.TryGetValue("metadata", out var m) && m is Dictionary<string, object> meta ? meta : new Dictionary<string, object>()
            };

            model.Messages.Add(assistantMessage);
            AddToHistory(assistantMessage);

            return assistantMessage;
        }

        // ========================================
        // Helper Methods
        // ========================================

        /// <summary>
        /// Sends an Intelligence request and waits for a response.
        /// </summary>
        private async Task<MessageResponse?> SendIntelligenceRequestAsync(
            string topic,
            Dictionary<string, object> payload,
            TimeSpan timeout,
            CancellationToken ct)
        {
            if (!_isIntelligenceAvailable)
                return null;

            var correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageResponse>();
            _pendingRequests[correlationId] = tcs;

            IDisposable? responseSub = null;

            try
            {
                var responseTopic = $"{topic}.response";
                responseSub = _messageBus.Subscribe(responseTopic, msg =>
                {
                    if (msg.CorrelationId == correlationId && _pendingRequests.TryRemove(correlationId, out var pendingTcs))
                    {
                        pendingTcs.TrySetResult(new MessageResponse
                        {
                            Success = msg.Payload.TryGetValue("success", out var s) && s is true,
                            Payload = msg.Payload,
                            ErrorMessage = msg.Payload.TryGetValue("error", out var e) && e is string err ? err : null
                        });
                    }
                    return Task.CompletedTask;
                });

                var request = new PluginMessage
                {
                    Type = $"{topic}.request",
                    CorrelationId = correlationId,
                    Source = "IntelligenceInterfaceService",
                    Payload = payload
                };

                await _messageBus.PublishAsync(topic, request, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                return await tcs.Task.WaitAsync(cts.Token);
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
                responseSub?.Dispose();
                _pendingRequests.TryRemove(correlationId, out _);
            }
        }

        // ========================================
        // Disposal
        // ========================================

        /// <summary>
        /// Disposes the service and releases all resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var sub in _subscriptions)
            {
                try { sub.Dispose(); } catch { }
            }
            _subscriptions.Clear();

            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetCanceled();
            }
            _pendingRequests.Clear();

            _initLock.Dispose();
        }

        /// <summary>
        /// Asynchronously disposes the service.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    #endregion

    // ================================================================================
    // SUPPORTING TYPES
    // ================================================================================

    #region C1: Conversational Interface Types

    /// <summary>
    /// Represents a parsed natural language command with structured parameters.
    /// </summary>
    public sealed class ParsedCommand
    {
        /// <summary>
        /// Gets or sets the original natural language input.
        /// </summary>
        public string OriginalInput { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the identified command type (e.g., "Upload", "Search", "Encrypt").
        /// </summary>
        public string CommandType { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the detected intent (e.g., "file.upload", "document.search").
        /// </summary>
        public string? Intent { get; init; }

        /// <summary>
        /// Gets or sets the parsed parameters extracted from the input.
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; } = new();

        /// <summary>
        /// Gets or sets the confidence score for the parsing (0.0-1.0).
        /// </summary>
        public double Confidence { get; init; }

        /// <summary>
        /// Gets or sets whether the command requires user confirmation before execution.
        /// </summary>
        public bool RequiresConfirmation { get; init; }

        /// <summary>
        /// Gets or sets the suggested action to execute.
        /// </summary>
        public string? SuggestedAction { get; init; }

        /// <summary>
        /// Gets or sets alternative interpretations of the command.
        /// </summary>
        public string[] AlternativeInterpretations { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Represents a message in a conversation history.
    /// </summary>
    public sealed class ConversationMessage
    {
        /// <summary>
        /// Gets or sets the role of the message sender.
        /// </summary>
        public ConversationRole Role { get; init; } = ConversationRole.User;

        /// <summary>
        /// Gets or sets the message content.
        /// </summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp of the message.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets or sets the session ID for conversation continuity.
        /// </summary>
        public string? SessionId { get; init; }

        /// <summary>
        /// Gets or sets additional metadata for the message.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Defines the roles in a conversation.
    /// </summary>
    public enum ConversationRole
    {
        /// <summary>User/human message.</summary>
        User,

        /// <summary>AI assistant message.</summary>
        Assistant,

        /// <summary>System message for context.</summary>
        System
    }

    #endregion

    #region C2: AI Agent Types

    /// <summary>
    /// Represents an AI agent task.
    /// </summary>
    public sealed class AgentTask
    {
        /// <summary>
        /// Gets or sets the unique task identifier.
        /// </summary>
        public string TaskId { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the type of agent.
        /// </summary>
        public AgentType AgentType { get; init; }

        /// <summary>
        /// Gets or sets the task objective in natural language.
        /// </summary>
        public string Objective { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the current task status.
        /// </summary>
        public AgentTaskStatus Status { get; set; }

        /// <summary>
        /// Gets or sets the task progress (0.0-1.0).
        /// </summary>
        public double Progress { get; set; }

        /// <summary>
        /// Gets or sets the current status message.
        /// </summary>
        public string? StatusMessage { get; set; }

        /// <summary>
        /// Gets or sets the task result when completed.
        /// </summary>
        public object? Result { get; set; }

        /// <summary>
        /// Gets or sets the error message if failed.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Gets or sets when the task was created.
        /// </summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>
        /// Gets or sets when the task was last updated.
        /// </summary>
        public DateTimeOffset LastUpdatedAt { get; set; }
    }

    /// <summary>
    /// Defines the types of AI agents that can be spawned.
    /// </summary>
    public enum AgentType
    {
        /// <summary>Agent for organizing files by category, date, or type.</summary>
        FileOrganization,

        /// <summary>Agent for analyzing and optimizing storage costs.</summary>
        StorageOptimization,

        /// <summary>Agent for auditing compliance requirements.</summary>
        ComplianceAudit,

        /// <summary>Agent for migrating data between systems.</summary>
        DataMigration,

        /// <summary>Agent for security assessment and recommendations.</summary>
        SecurityAssessment,

        /// <summary>Agent for data cleanup and deduplication.</summary>
        DataCleanup,

        /// <summary>Agent for metadata enrichment.</summary>
        MetadataEnrichment,

        /// <summary>General-purpose agent.</summary>
        General
    }

    /// <summary>
    /// Defines the status of an agent task.
    /// </summary>
    public enum AgentTaskStatus
    {
        /// <summary>Task is being initialized.</summary>
        Initializing,

        /// <summary>Task is queued for execution.</summary>
        Queued,

        /// <summary>Task is currently running.</summary>
        Running,

        /// <summary>Task is paused.</summary>
        Paused,

        /// <summary>Task completed successfully.</summary>
        Completed,

        /// <summary>Task failed.</summary>
        Failed,

        /// <summary>Task was cancelled.</summary>
        Cancelled
    }

    #endregion

    #region C3: GUI Intelligence Types

    /// <summary>
    /// Represents a search suggestion from AI.
    /// </summary>
    public sealed class SearchSuggestion
    {
        /// <summary>
        /// Gets or sets the suggested search text.
        /// </summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the category of the suggestion.
        /// </summary>
        public string? Category { get; init; }

        /// <summary>
        /// Gets or sets the relevance score (0.0-1.0).
        /// </summary>
        public double Score { get; init; }

        /// <summary>
        /// Gets or sets additional metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Represents a smart recommendation from AI.
    /// </summary>
    public sealed class SmartRecommendation
    {
        /// <summary>
        /// Gets or sets the unique recommendation identifier.
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the type of recommendation.
        /// </summary>
        public RecommendationType Type { get; init; }

        /// <summary>
        /// Gets or sets the recommendation title.
        /// </summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the detailed description.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the severity/importance level.
        /// </summary>
        public RecommendationSeverity Severity { get; init; }

        /// <summary>
        /// Gets or sets the confidence score (0.0-1.0).
        /// </summary>
        public double Confidence { get; init; }

        /// <summary>
        /// Gets or sets the suggested action to take.
        /// </summary>
        public string? SuggestedAction { get; init; }

        /// <summary>
        /// Gets or sets parameters for the suggested action.
        /// </summary>
        public Dictionary<string, object> ActionParameters { get; init; } = new();

        /// <summary>
        /// Gets or sets whether this recommendation can be auto-applied.
        /// </summary>
        public bool AutoApplyable { get; init; }
    }

    /// <summary>
    /// Defines the types of smart recommendations.
    /// </summary>
    public enum RecommendationType
    {
        /// <summary>General recommendation.</summary>
        General,

        /// <summary>Security-related recommendation (e.g., encrypt PII).</summary>
        Security,

        /// <summary>Compliance-related recommendation.</summary>
        Compliance,

        /// <summary>Performance optimization recommendation.</summary>
        Performance,

        /// <summary>Cost optimization recommendation.</summary>
        CostOptimization,

        /// <summary>Storage tiering recommendation.</summary>
        StorageTiering,

        /// <summary>Data quality recommendation.</summary>
        DataQuality,

        /// <summary>Duplicate detection recommendation.</summary>
        Deduplication,

        /// <summary>Archival recommendation.</summary>
        Archival,

        /// <summary>Access control recommendation.</summary>
        AccessControl
    }

    /// <summary>
    /// Defines the severity levels for recommendations.
    /// </summary>
    public enum RecommendationSeverity
    {
        /// <summary>Informational only.</summary>
        Info,

        /// <summary>Low priority suggestion.</summary>
        Low,

        /// <summary>Medium priority suggestion.</summary>
        Medium,

        /// <summary>High priority - should be addressed.</summary>
        High,

        /// <summary>Critical - requires immediate attention.</summary>
        Critical
    }

    /// <summary>
    /// Represents a filter expression built from natural language.
    /// </summary>
    public sealed class FilterExpression
    {
        /// <summary>
        /// Gets or sets the original natural language description.
        /// </summary>
        public string? OriginalDescription { get; init; }

        /// <summary>
        /// Gets or sets the field to filter on.
        /// </summary>
        public string? Field { get; init; }

        /// <summary>
        /// Gets or sets the comparison operator.
        /// </summary>
        public string Operator { get; init; } = "equals";

        /// <summary>
        /// Gets or sets the value to compare against.
        /// </summary>
        public object? Value { get; init; }

        /// <summary>
        /// Gets or sets the logical operator for compound expressions (AND, OR).
        /// </summary>
        public string? LogicalOperator { get; init; }

        /// <summary>
        /// Gets or sets sub-expressions for compound filters.
        /// </summary>
        public List<FilterExpression> SubExpressions { get; init; } = new();

        /// <summary>
        /// Gets or sets whether this is a compound (multi-condition) filter.
        /// </summary>
        public bool IsCompound { get; init; }

        /// <summary>
        /// Gets or sets the confidence in the filter interpretation (0.0-1.0).
        /// </summary>
        public double Confidence { get; init; }
    }

    /// <summary>
    /// Context information about a file for generating recommendations.
    /// </summary>
    public sealed class FileContext
    {
        /// <summary>
        /// Gets or sets the file name.
        /// </summary>
        public string? FileName { get; init; }

        /// <summary>
        /// Gets or sets the file type/extension.
        /// </summary>
        public string? FileType { get; init; }

        /// <summary>
        /// Gets or sets the file size in bytes.
        /// </summary>
        public long FileSize { get; init; }

        /// <summary>
        /// Gets or sets when the file was last accessed.
        /// </summary>
        public DateTimeOffset? LastAccessed { get; init; }

        /// <summary>
        /// Gets or sets when the file was last modified.
        /// </summary>
        public DateTimeOffset? LastModified { get; init; }

        /// <summary>
        /// Gets or sets the file tags.
        /// </summary>
        public string[]? Tags { get; init; }

        /// <summary>
        /// Gets or sets a sample of the file content for analysis.
        /// </summary>
        public string? ContentSample { get; init; }

        /// <summary>
        /// Gets or sets additional file metadata.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Data model for a GUI chat panel.
    /// </summary>
    public sealed class ChatPanelModel
    {
        /// <summary>
        /// Gets or sets the session identifier.
        /// </summary>
        public string SessionId { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets whether Intelligence is available.
        /// </summary>
        public bool IsIntelligenceAvailable { get; init; }

        /// <summary>
        /// Gets or sets the available capabilities.
        /// </summary>
        public IntelligenceCapabilities AvailableCapabilities { get; init; }

        /// <summary>
        /// Gets or sets the conversation messages.
        /// </summary>
        public List<ConversationMessage> Messages { get; init; } = new();

        /// <summary>
        /// Gets or sets whether the chat is currently processing.
        /// </summary>
        public bool IsProcessing { get; set; }

        /// <summary>
        /// Gets or sets the current input text.
        /// </summary>
        public string CurrentInput { get; set; } = string.Empty;
    }

    #endregion

    #region Event Args

    /// <summary>
    /// Event arguments for Intelligence availability changes.
    /// </summary>
    public sealed class IntelligenceAvailabilityChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets whether Intelligence is now available.
        /// </summary>
        public bool IsAvailable { get; init; }

        /// <summary>
        /// Gets or sets the available capabilities.
        /// </summary>
        public IntelligenceCapabilities Capabilities { get; init; }

        /// <summary>
        /// Gets or sets when the change occurred.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Event arguments for agent task status changes.
    /// </summary>
    public sealed class AgentTaskStatusChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the affected task.
        /// </summary>
        public AgentTask Task { get; init; } = null!;

        /// <summary>
        /// Gets or sets when the change occurred.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; }
    }

    #endregion
}
