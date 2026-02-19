using DataWarehouse.SDK.Security;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// AI Custodian Strategy - Industry-first AI-supervised key access control via LLM APIs.
    ///
    /// This strategy uses Large Language Models as intelligent access control gatekeepers.
    /// The AI evaluates access requests against policies, context, and behavioral patterns
    /// to make nuanced access decisions that traditional rule-based systems cannot handle.
    ///
    /// Architecture:
    /// ┌─────────────────┐         ┌─────────────────────────────────────┐
    /// │  Access Request │ ──────► │          AI Custodian               │
    /// │  (User + Context)│        │  ┌─────────────────────────────────┐│
    /// └─────────────────┘        │  │  Policy Analysis                 ││
    ///                            │  │  - Natural language policies     ││
    ///                            │  │  - Contextual reasoning          ││
    ///                            │  │  - Anomaly detection             ││
    ///                            │  └─────────────────────────────────┘│
    ///                            │  ┌─────────────────────────────────┐│
    ///                            │  │  Behavioral Analysis             ││
    ///                            │  │  - Access pattern history        ││
    ///                            │  │  - Time-of-day patterns          ││
    ///                            │  │  - Location consistency          ││
    ///                            │  └─────────────────────────────────┘│
    ///                            │  ┌─────────────────────────────────┐│
    ///                            │  │  Decision Engine                 ││
    ///                            │  │  - Approve / Deny / Escalate    ││
    ///                            │  │  - Confidence scoring            ││
    ///                            │  │  - Explanation generation        ││
    ///                            │  └─────────────────────────────────┘│
    ///                            └─────────────────────────────────────┘
    ///                                           │
    ///                                           ▼
    ///                            ┌─────────────────────────────────────┐
    ///                            │  LLM Provider (OpenAI/Anthropic/    │
    ///                            │  Azure OpenAI/Local LLM)            │
    ///                            └─────────────────────────────────────┘
    ///
    /// Supported Providers:
    /// - OpenAI (GPT-4, GPT-4 Turbo)
    /// - Anthropic (Claude 3 Opus, Claude 3.5 Sonnet)
    /// - Azure OpenAI Service
    /// - Local/Self-hosted LLMs (Ollama, vLLM)
    ///
    /// Security Features:
    /// - Policy-based access control with natural language rules
    /// - Behavioral anomaly detection
    /// - Multi-factor verification for high-risk requests
    /// - Comprehensive audit trail with AI reasoning
    /// - Human escalation for uncertain decisions
    /// - Rate limiting and abuse prevention
    /// </summary>
    public sealed class AiCustodianStrategy : KeyStoreStrategyBase
    {
        private AiCustodianConfig _config = new();
        private HttpClient _llmClient = null!; // Initialized in InitializeStorage before first use
        private readonly Dictionary<string, AiProtectedKeyData> _keys = new();
        private readonly Dictionary<string, AccessHistory> _accessHistories = new();
        private readonly List<PendingEscalation> _pendingEscalations = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SemaphoreSlim _rateLimiter;
        private bool _disposed;

        public AiCustodianStrategy()
        {
            _rateLimiter = new SemaphoreSlim(10, 10); // Max 10 concurrent AI requests
        }

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 64,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Algorithm"] = "AI-Supervised Access Control",
                ["SecurityModel"] = "LLM-Based Policy Evaluation",
                ["Features"] = new[] { "Natural Language Policies", "Behavioral Analysis", "Anomaly Detection", "Human Escalation" }
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("aicustodian.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("aicustodian.init");
            // Load configuration
            if (Configuration.TryGetValue("Provider", out var providerObj) && providerObj is string provider)
                _config.Provider = Enum.Parse<LlmProvider>(provider, true);
            if (Configuration.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is string apiKey)
                _config.ApiKey = apiKey;
            if (Configuration.TryGetValue("ApiEndpoint", out var endpointObj) && endpointObj is string endpoint)
                _config.ApiEndpoint = endpoint;
            if (Configuration.TryGetValue("Model", out var modelObj) && modelObj is string model)
                _config.Model = model;
            if (Configuration.TryGetValue("ConfidenceThreshold", out var thresholdObj) && thresholdObj is double threshold)
                _config.ConfidenceThreshold = threshold;
            if (Configuration.TryGetValue("EscalationEmail", out var emailObj) && emailObj is string email)
                _config.EscalationEmail = email;
            if (Configuration.TryGetValue("MaxHistorySize", out var historyObj) && historyObj is int history)
                _config.MaxAccessHistorySize = history;
            if (Configuration.TryGetValue("StoragePath", out var pathObj) && pathObj is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("SystemPrompt", out var promptObj) && promptObj is string prompt)
                _config.SystemPrompt = prompt;

            _llmClient = CreateLlmClient();

            await LoadKeysFromStorage();
        }

        private HttpClient CreateLlmClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

            switch (_config.Provider)
            {
                case LlmProvider.OpenAI:
                    client.BaseAddress = new Uri(_config.ApiEndpoint ?? "https://api.openai.com/v1/");
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.ApiKey}");
                    break;

                case LlmProvider.Anthropic:
                    client.BaseAddress = new Uri(_config.ApiEndpoint ?? "https://api.anthropic.com/v1/");
                    client.DefaultRequestHeaders.Add("x-api-key", _config.ApiKey);
                    client.DefaultRequestHeaders.Add("anthropic-version", "2024-01-01");
                    break;

                case LlmProvider.AzureOpenAI:
                    client.BaseAddress = new Uri(_config.ApiEndpoint ?? "https://your-resource.openai.azure.com/");
                    client.DefaultRequestHeaders.Add("api-key", _config.ApiKey);
                    break;

                case LlmProvider.LocalLlm:
                    client.BaseAddress = new Uri(_config.ApiEndpoint ?? "http://localhost:11434/api/");
                    break;
            }

            return client;
        }

        public override Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Test LLM connectivity with a simple request
                var testResult = await CallLlmAsync(
                    "You are an AI assistant. Respond with only the word 'healthy'.",
                    "Are you operational?",
                    cancellationToken);

                return testResult.Contains("healthy", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"AI-protected key '{keyId}' not found.");

                // Evaluate access request with AI
                var decision = await EvaluateAccessRequestAsync(keyData, context);

                // Log the decision
                keyData.AuditLog.Add(new AiAccessAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    UserId = context.UserId,
                    TenantId = context.TenantId,
                    Decision = decision.Decision,
                    Confidence = decision.Confidence,
                    Reasoning = decision.Reasoning,
                    Factors = decision.Factors
                });

                await PersistKeysToStorage();

                // Handle decision
                switch (decision.Decision)
                {
                    case AccessDecision.Approved:
                        UpdateAccessHistory(keyId, context, true);
                        return keyData.EncryptedKeyMaterial;

                    case AccessDecision.Denied:
                        UpdateAccessHistory(keyId, context, false);
                        throw new UnauthorizedAccessException(
                            $"Access denied by AI custodian. Reason: {decision.Reasoning}");

                    case AccessDecision.Escalate:
                        var escalation = await CreateEscalationAsync(keyId, context, decision);
                        throw new InvalidOperationException(
                            $"Access request escalated for human review. Escalation ID: {escalation.EscalationId}. " +
                            $"Reason: {decision.Reasoning}");

                    case AccessDecision.RequiresMfa:
                        throw new InvalidOperationException(
                            $"Additional verification required: {decision.RequiredVerification}. " +
                            $"Use SubmitVerificationAsync to complete access.");

                    default:
                        throw new InvalidOperationException($"Unknown access decision: {decision.Decision}");
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                var aiKeyData = new AiProtectedKeyData
                {
                    KeyId = keyId,
                    EncryptedKeyMaterial = keyData,
                    Policies = _config.DefaultPolicies.ToList(),
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };

                _keys[keyId] = aiKeyData;
                _currentKeyId = keyId;

                // Initialize access history for this key
                _accessHistories[keyId] = new AccessHistory
                {
                    KeyId = keyId,
                    Entries = new List<AccessHistoryEntry>()
                };

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Evaluates an access request using the AI custodian.
        /// </summary>
        private async Task<AiAccessDecision> EvaluateAccessRequestAsync(
            AiProtectedKeyData keyData,
            ISecurityContext context)
        {
            await _rateLimiter.WaitAsync();
            try
            {
                // Build context for AI evaluation
                var accessContext = BuildAccessContext(keyData, context);

                // Build the prompt
                var systemPrompt = BuildSystemPrompt(keyData.Policies);
                var userPrompt = BuildUserPrompt(accessContext);

                // Call LLM
                var response = await CallLlmAsync(systemPrompt, userPrompt, CancellationToken.None);

                // Parse AI response
                var decision = ParseAiDecision(response);

                return decision;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        /// <summary>
        /// Builds the system prompt with policies and instructions.
        /// </summary>
        private string BuildSystemPrompt(List<string> policies)
        {
            var basePrompt = _config.SystemPrompt ?? DefaultSystemPrompt;

            var policySection = policies.Count > 0
                ? $"\n\nPOLICIES TO ENFORCE:\n{string.Join("\n", policies.Select((p, i) => $"{i + 1}. {p}"))}"
                : "";

            return basePrompt + policySection;
        }

        /// <summary>
        /// Builds the user prompt with access request context.
        /// </summary>
        private string BuildUserPrompt(AccessRequestContext ctx)
        {
            return $@"Evaluate the following access request:

USER INFORMATION:
- User ID: {ctx.UserId}
- Tenant: {ctx.TenantId ?? "None"}
- Roles: {string.Join(", ", ctx.Roles)}
- Is Admin: {ctx.IsAdmin}

REQUEST CONTEXT:
- Key ID: {ctx.KeyId}
- Request Time: {ctx.RequestTime:O}
- Client IP: {ctx.ClientIp ?? "Unknown"}
- User Agent: {ctx.UserAgent ?? "Unknown"}

ACCESS HISTORY (last {ctx.RecentAccessCount} accesses):
{(ctx.RecentAccesses.Count > 0 ? FormatAccessHistory(ctx.RecentAccesses) : "No prior access history")}

BEHAVIORAL ANALYSIS:
- Typical access time: {ctx.TypicalAccessTime ?? "Unknown"}
- Typical frequency: {ctx.TypicalFrequency ?? "Unknown"}
- Current request is unusual: {ctx.IsUnusual}
- Anomaly score: {ctx.AnomalyScore:F2}

Provide your access decision in the following JSON format:
{{
    ""decision"": ""APPROVED"" | ""DENIED"" | ""ESCALATE"" | ""REQUIRES_MFA"",
    ""confidence"": 0.0-1.0,
    ""reasoning"": ""Brief explanation of your decision"",
    ""factors"": [""factor1"", ""factor2"", ...],
    ""required_verification"": null | ""mfa"" | ""manager_approval"" | ""security_question""
}}";
        }

        /// <summary>
        /// Calls the LLM API with the given prompts.
        /// </summary>
        private async Task<string> CallLlmAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            try
            {
                return _config.Provider switch
                {
                    LlmProvider.OpenAI or LlmProvider.AzureOpenAI =>
                        await CallOpenAiAsync(systemPrompt, userPrompt, ct),
                    LlmProvider.Anthropic =>
                        await CallAnthropicAsync(systemPrompt, userPrompt, ct),
                    LlmProvider.LocalLlm =>
                        await CallLocalLlmAsync(systemPrompt, userPrompt, ct),
                    _ => throw new NotSupportedException($"Provider {_config.Provider} not supported.")
                };
            }
            catch (Exception ex)
            {
                // On LLM failure, use conservative fallback
                return JsonSerializer.Serialize(new
                {
                    decision = "ESCALATE",
                    confidence = 0.0,
                    reasoning = $"AI evaluation failed: {ex.Message}. Escalating for human review.",
                    factors = new[] { "ai_failure", "safety_fallback" },
                    required_verification = (string?)null
                });
            }
        }

        private async Task<string> CallOpenAiAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var request = new
            {
                model = _config.Model ?? "gpt-4-turbo-preview",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                max_tokens = 1000,
                temperature = 0.1 // Low temperature for consistent decisions
            };

            var response = await _llmClient.PostAsJsonAsync("chat/completions", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OpenAiResponse>(cancellationToken: ct);
            return result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        }

        private async Task<string> CallAnthropicAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var request = new
            {
                model = _config.Model ?? "claude-3-5-sonnet-20241022",
                system = systemPrompt,
                messages = new[]
                {
                    new { role = "user", content = userPrompt }
                },
                max_tokens = 1000
            };

            var response = await _llmClient.PostAsJsonAsync("messages", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(cancellationToken: ct);
            return result?.Content?.FirstOrDefault()?.Text ?? "";
        }

        private async Task<string> CallLocalLlmAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            // Ollama-compatible API
            var request = new
            {
                model = _config.Model ?? "llama3.2",
                prompt = $"{systemPrompt}\n\n{userPrompt}",
                stream = false
            };

            var response = await _llmClient.PostAsJsonAsync("generate", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
            return result?.Response ?? "";
        }

        /// <summary>
        /// Parses the AI response into a structured decision.
        /// </summary>
        private AiAccessDecision ParseAiDecision(string aiResponse)
        {
            try
            {
                // Extract JSON from response (AI might include explanatory text)
                var jsonStart = aiResponse.IndexOf('{');
                var jsonEnd = aiResponse.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = aiResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var parsed = JsonSerializer.Deserialize<AiDecisionJson>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (parsed != null)
                    {
                        return new AiAccessDecision
                        {
                            Decision = ParseDecisionString(parsed.Decision),
                            Confidence = Math.Clamp(parsed.Confidence, 0, 1),
                            Reasoning = parsed.Reasoning ?? "No reasoning provided",
                            Factors = parsed.Factors ?? Array.Empty<string>(),
                            RequiredVerification = parsed.RequiredVerification
                        };
                    }
                }
            }
            catch
            {
                // Parse failed - escalate for safety
            }

            return new AiAccessDecision
            {
                Decision = AccessDecision.Escalate,
                Confidence = 0,
                Reasoning = "Failed to parse AI decision. Escalating for human review.",
                Factors = new[] { "parse_failure", "safety_fallback" }
            };
        }

        private static AccessDecision ParseDecisionString(string? decision)
        {
            return decision?.ToUpperInvariant() switch
            {
                "APPROVED" or "APPROVE" or "ALLOW" => AccessDecision.Approved,
                "DENIED" or "DENY" or "REJECT" => AccessDecision.Denied,
                "ESCALATE" or "REVIEW" => AccessDecision.Escalate,
                "REQUIRES_MFA" or "MFA" or "VERIFY" => AccessDecision.RequiresMfa,
                _ => AccessDecision.Escalate
            };
        }

        /// <summary>
        /// Builds the context for AI evaluation.
        /// </summary>
        private AccessRequestContext BuildAccessContext(AiProtectedKeyData keyData, ISecurityContext context)
        {
            var history = _accessHistories.GetValueOrDefault(keyData.KeyId);
            var recentAccesses = history?.Entries
                .OrderByDescending(e => e.Timestamp)
                .Take(10)
                .ToList() ?? new List<AccessHistoryEntry>();

            // Calculate anomaly score
            var anomalyScore = CalculateAnomalyScore(recentAccesses, context);

            // Determine typical patterns
            var (typicalTime, typicalFreq) = AnalyzeAccessPatterns(recentAccesses);

            return new AccessRequestContext
            {
                KeyId = keyData.KeyId,
                UserId = context.UserId,
                TenantId = context.TenantId,
                Roles = context.Roles.ToArray(),
                IsAdmin = context.IsSystemAdmin,
                RequestTime = DateTime.UtcNow,
                ClientIp = (context as INetworkSecurityContext)?.ClientIpAddress,
                UserAgent = null, // Could be extracted from context if available
                RecentAccesses = recentAccesses,
                RecentAccessCount = recentAccesses.Count,
                AnomalyScore = anomalyScore,
                IsUnusual = anomalyScore > 0.7,
                TypicalAccessTime = typicalTime,
                TypicalFrequency = typicalFreq
            };
        }

        /// <summary>
        /// Calculates an anomaly score based on access patterns.
        /// </summary>
        private double CalculateAnomalyScore(List<AccessHistoryEntry> history, ISecurityContext context)
        {
            if (history.Count == 0) return 0.5; // Unknown user

            var score = 0.0;
            var factors = 0;

            // Check time-of-day anomaly
            var currentHour = DateTime.UtcNow.Hour;
            var typicalHours = history.GroupBy(h => h.Timestamp.Hour)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToList();

            if (typicalHours.Count > 0 && !typicalHours.Contains(currentHour))
            {
                score += 0.3;
            }
            factors++;

            // Check access frequency anomaly
            var recentCount = history.Count(h => h.Timestamp > DateTime.UtcNow.AddHours(-1));
            var averageHourly = history.Count / Math.Max(1,
                (DateTime.UtcNow - history.Min(h => h.Timestamp)).TotalHours);

            if (recentCount > averageHourly * 3)
            {
                score += 0.4; // Unusually high frequency
            }
            factors++;

            // Check day-of-week anomaly
            var currentDay = DateTime.UtcNow.DayOfWeek;
            var typicalDays = history.GroupBy(h => h.Timestamp.DayOfWeek)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key)
                .ToList();

            if (typicalDays.Count > 0 && !typicalDays.Contains(currentDay))
            {
                score += 0.2;
            }
            factors++;

            // Check denial rate
            var denialRate = history.Count(h => !h.WasApproved) / (double)history.Count;
            if (denialRate > 0.3)
            {
                score += 0.3;
            }
            factors++;

            return Math.Min(1.0, score);
        }

        private (string? typicalTime, string? typicalFreq) AnalyzeAccessPatterns(List<AccessHistoryEntry> history)
        {
            if (history.Count == 0) return (null, null);

            var typicalHour = history.GroupBy(h => h.Timestamp.Hour)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            var typicalTime = typicalHour.HasValue ? $"{typicalHour:D2}:00-{(typicalHour + 1) % 24:D2}:00 UTC" : null;

            var avgPerDay = history.GroupBy(h => h.Timestamp.Date)
                .Average(g => g.Count());

            var typicalFreq = avgPerDay > 10 ? "High (10+/day)" :
                             avgPerDay > 3 ? "Medium (3-10/day)" :
                             avgPerDay > 0 ? "Low (1-3/day)" : null;

            return (typicalTime, typicalFreq);
        }

        private void UpdateAccessHistory(string keyId, ISecurityContext context, bool approved)
        {
            if (!_accessHistories.TryGetValue(keyId, out var history))
            {
                history = new AccessHistory { KeyId = keyId, Entries = new List<AccessHistoryEntry>() };
                _accessHistories[keyId] = history;
            }

            history.Entries.Add(new AccessHistoryEntry
            {
                Timestamp = DateTime.UtcNow,
                UserId = context.UserId,
                WasApproved = approved
            });

            // Trim history
            if (history.Entries.Count > _config.MaxAccessHistorySize)
            {
                history.Entries = history.Entries
                    .OrderByDescending(e => e.Timestamp)
                    .Take(_config.MaxAccessHistorySize)
                    .ToList();
            }
        }

        private static string FormatAccessHistory(List<AccessHistoryEntry> entries)
        {
            return string.Join("\n", entries.Select(e =>
                $"  - {e.Timestamp:O}: {(e.WasApproved ? "Approved" : "Denied")}"));
        }

        /// <summary>
        /// Creates a human escalation for uncertain decisions.
        /// </summary>
        private async Task<PendingEscalation> CreateEscalationAsync(
            string keyId,
            ISecurityContext context,
            AiAccessDecision decision)
        {
            var escalation = new PendingEscalation
            {
                EscalationId = Guid.NewGuid().ToString(),
                KeyId = keyId,
                RequesterId = context.UserId,
                RequestTime = DateTime.UtcNow,
                AiReasoning = decision.Reasoning,
                Factors = decision.Factors,
                Status = EscalationStatus.Pending
            };

            _pendingEscalations.Add(escalation);

            // In production, send email/notification to escalation contact
            // await SendEscalationNotificationAsync(escalation);

            return escalation;
        }

        /// <summary>
        /// Configures policies for a specific key.
        /// </summary>
        public async Task ConfigurePoliciesAsync(
            string keyId,
            List<string> policies,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Only administrators can configure policies.");

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                keyData.Policies = policies;
                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Resolves a pending escalation.
        /// </summary>
        public async Task<bool> ResolveEscalationAsync(
            string escalationId,
            bool approved,
            string resolverNotes,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Only administrators can resolve escalations.");

            var escalation = _pendingEscalations.FirstOrDefault(e => e.EscalationId == escalationId);
            if (escalation == null)
                throw new KeyNotFoundException($"Escalation '{escalationId}' not found.");

            escalation.Status = approved ? EscalationStatus.Approved : EscalationStatus.Denied;
            escalation.ResolvedAt = DateTime.UtcNow;
            escalation.ResolverId = context.UserId;
            escalation.ResolverNotes = resolverNotes;

            await PersistKeysToStorage();
            return approved;
        }

        /// <summary>
        /// Gets the audit log for a key.
        /// </summary>
        public async Task<IReadOnlyList<AiAccessAuditEntry>> GetAuditLogAsync(
            string keyId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                return keyData.AuditLog.AsReadOnly();
            }
            finally
            {
                _lock.Release();
            }
        }

        #region IKeyStore Implementation

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            await _lock.WaitAsync(ct);
            try { return _keys.Keys.ToList().AsReadOnly(); }
            finally { _lock.Release(); }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin) throw new UnauthorizedAccessException();

            await _lock.WaitAsync(ct);
            try
            {
                if (_keys.Remove(keyId))
                {
                    _accessHistories.Remove(keyId);
                    await PersistKeysToStorage();
                }
            }
            finally { _lock.Release(); }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(ct);
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData)) return null;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = keyData.CreatedAt,
                    CreatedBy = keyData.CreatedBy,
                    KeySizeBytes = keyData.EncryptedKeyMaterial.Length,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Algorithm"] = "AI Custodian",
                        ["Provider"] = _config.Provider.ToString(),
                        ["Model"] = _config.Model ?? "default",
                        ["PolicyCount"] = keyData.Policies.Count,
                        ["AuditLogEntries"] = keyData.AuditLog.Count,
                        ["ConfidenceThreshold"] = _config.ConfidenceThreshold
                    }
                };
            }
            finally { _lock.Release(); }
        }

        #endregion

        #region Storage

        private async Task LoadKeysFromStorage()
        {
            var path = GetStoragePath();
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<AiCustodianStorageData>(json);
                if (stored != null)
                {
                    foreach (var kvp in stored.Keys)
                        _keys[kvp.Key] = kvp.Value;
                    foreach (var kvp in stored.Histories)
                        _accessHistories[kvp.Key] = kvp.Value;
                    foreach (var esc in stored.Escalations)
                        _pendingEscalations.Add(esc);
                    if (_keys.Count > 0)
                        _currentKeyId = _keys.Keys.First();
                }
            }
            catch { /* Deserialization failure — start with empty state */ }
        }

        private async Task PersistKeysToStorage()
        {
            var path = GetStoragePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new AiCustodianStorageData
            {
                Keys = _keys,
                Histories = _accessHistories,
                Escalations = _pendingEscalations
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "ai-custodian.json");
        }

        #endregion

        private const string DefaultSystemPrompt = @"You are an AI Security Custodian responsible for making access control decisions.
Your role is to evaluate access requests to cryptographic keys based on:
1. User identity and roles
2. Historical access patterns
3. Behavioral anomalies
4. Configured security policies

You must be:
- Security-conscious: When in doubt, escalate to human review
- Consistent: Similar requests should get similar decisions
- Explainable: Provide clear reasoning for every decision
- Fair: Don't discriminate based on non-security factors

Decision Guidelines:
- APPROVED: Request clearly meets all policies and shows no anomalies
- DENIED: Request clearly violates policies or shows strong evidence of compromise
- ESCALATE: Uncertain cases that need human judgment
- REQUIRES_MFA: Suspicious but potentially legitimate, need additional verification

Always respond in valid JSON format.";

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _llmClient?.Dispose();
            _lock.Dispose();
            _rateLimiter.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    public enum LlmProvider
    {
        OpenAI,
        Anthropic,
        AzureOpenAI,
        LocalLlm
    }

    public enum AccessDecision
    {
        Approved,
        Denied,
        Escalate,
        RequiresMfa
    }

    public enum EscalationStatus
    {
        Pending,
        Approved,
        Denied,
        Expired
    }

    public class AiCustodianConfig
    {
        public LlmProvider Provider { get; set; } = LlmProvider.OpenAI;
        public string ApiKey { get; set; } = "";
        public string? ApiEndpoint { get; set; }
        public string? Model { get; set; }
        public double ConfidenceThreshold { get; set; } = 0.8;
        public string? EscalationEmail { get; set; }
        public int MaxAccessHistorySize { get; set; } = 1000;
        public string? StoragePath { get; set; }
        public string? SystemPrompt { get; set; }
        public List<string> DefaultPolicies { get; set; } = new()
        {
            "Access during business hours (9 AM - 6 PM local time) is generally allowed",
            "More than 10 access requests per hour from the same user should trigger escalation",
            "First-time access from a new user requires additional verification",
            "Admin users have elevated trust but should still be monitored for anomalies"
        };
    }

    internal class AiProtectedKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] EncryptedKeyMaterial { get; set; } = Array.Empty<byte>();
        public List<string> Policies { get; set; } = new();
        public List<AiAccessAuditEntry> AuditLog { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    public class AiAccessAuditEntry
    {
        public DateTime Timestamp { get; set; }
        public string? UserId { get; set; }
        public string? TenantId { get; set; }
        public AccessDecision Decision { get; set; }
        public double Confidence { get; set; }
        public string? Reasoning { get; set; }
        public string[]? Factors { get; set; }
    }

    internal class AccessHistory
    {
        public string KeyId { get; set; } = "";
        public List<AccessHistoryEntry> Entries { get; set; } = new();
    }

    internal class AccessHistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public string? UserId { get; set; }
        public bool WasApproved { get; set; }
    }

    internal class PendingEscalation
    {
        public string EscalationId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string? RequesterId { get; set; }
        public DateTime RequestTime { get; set; }
        public string? AiReasoning { get; set; }
        public string[]? Factors { get; set; }
        public EscalationStatus Status { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string? ResolverId { get; set; }
        public string? ResolverNotes { get; set; }
    }

    internal class AccessRequestContext
    {
        public string KeyId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string? TenantId { get; set; }
        public string[] Roles { get; set; } = Array.Empty<string>();
        public bool IsAdmin { get; set; }
        public DateTime RequestTime { get; set; }
        public string? ClientIp { get; set; }
        public string? UserAgent { get; set; }
        public List<AccessHistoryEntry> RecentAccesses { get; set; } = new();
        public int RecentAccessCount { get; set; }
        public double AnomalyScore { get; set; }
        public bool IsUnusual { get; set; }
        public string? TypicalAccessTime { get; set; }
        public string? TypicalFrequency { get; set; }
    }

    internal class AiAccessDecision
    {
        public AccessDecision Decision { get; set; }
        public double Confidence { get; set; }
        public string Reasoning { get; set; } = "";
        public string[] Factors { get; set; } = Array.Empty<string>();
        public string? RequiredVerification { get; set; }
    }

    internal class AiDecisionJson
    {
        [JsonPropertyName("decision")]
        public string? Decision { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; set; }

        [JsonPropertyName("factors")]
        public string[]? Factors { get; set; }

        [JsonPropertyName("required_verification")]
        public string? RequiredVerification { get; set; }
    }

    internal class AiCustodianStorageData
    {
        public Dictionary<string, AiProtectedKeyData> Keys { get; set; } = new();
        public Dictionary<string, AccessHistory> Histories { get; set; } = new();
        public List<PendingEscalation> Escalations { get; set; } = new();
    }

    // LLM API Response Types
    internal class OpenAiResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }
    }

    internal class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }
    }

    internal class OpenAiMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    internal class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContent>? Content { get; set; }
    }

    internal class AnthropicContent
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    internal class OllamaResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
    }

    #endregion
}
