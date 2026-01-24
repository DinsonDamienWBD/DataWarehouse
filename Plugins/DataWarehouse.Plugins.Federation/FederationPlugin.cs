using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Federation;

/// <summary>
/// Federation protocol plugin for secure communication between DataWarehouse instances.
/// Provides trust establishment, cross-federation queries, identity federation with
/// delegated authentication, and secure data sharing across organizational boundaries.
///
/// Features:
/// - Trust establishment using PKI and certificate exchange
/// - Cross-federation queries with query forwarding and result aggregation
/// - Identity federation with SAML/OAuth token translation
/// - Data sharing policies and access control
/// - Secure inter-cluster communication with mTLS
/// - Federation health monitoring and automatic failover
/// - Audit logging for all cross-federation operations
///
/// Message Commands:
/// - federation.trust.establish: Establish trust with a remote instance
/// - federation.trust.revoke: Revoke trust from an instance
/// - federation.trust.list: List all trusted instances
/// - federation.query.execute: Execute a cross-federation query
/// - federation.data.share: Share data with a federated instance
/// - federation.data.request: Request data from a federated instance
/// - federation.identity.translate: Translate identity tokens
/// - federation.policy.set: Set data sharing policy
/// - federation.policy.get: Get data sharing policies
/// - federation.health.check: Check federation health
/// - federation.status: Get federation status
/// </summary>
public sealed class FederationPlugin : ReplicationPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.federation.core";

    /// <inheritdoc />
    public override string Name => "Federation Protocol Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    #region Private Fields

    private readonly ConcurrentDictionary<string, FederatedInstance> _trustedInstances = new();
    private readonly ConcurrentDictionary<string, FederationPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, IdentityMapping> _identityMappings = new();
    private readonly ConcurrentDictionary<string, PendingTrustRequest> _pendingTrustRequests = new();
    private readonly ConcurrentDictionary<string, QueryExecution> _activeQueries = new();
    private readonly ConcurrentQueue<FederationAuditEntry> _auditLog = new();
    private readonly SemaphoreSlim _trustLock = new(1, 1);
    private readonly object _auditLock = new();

    private CancellationTokenSource? _cts;
    private Task? _healthCheckTask;
    private Task? _tokenRefreshTask;
    private HttpClient? _httpClient;
    private X509Certificate2? _localCertificate;
    private RSA? _localPrivateKey;
    private string _instanceId = string.Empty;
    private string _instanceEndpoint = string.Empty;
    private bool _isRunning;

    private const int MaxAuditEntries = 50000;
    private const int TrustEstablishmentTimeoutSeconds = 120;
    private const int QueryTimeoutSeconds = 300;
    private const int HealthCheckIntervalSeconds = 30;
    private const int TokenRefreshIntervalSeconds = 300;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _instanceId = $"federation-{request.KernelId}-{Guid.NewGuid():N}"[..32];

        if (request.Config?.TryGetValue("endpoint", out var endpoint) == true && endpoint is string ep)
        {
            _instanceEndpoint = ep;
        }
        else
        {
            _instanceEndpoint = $"https://localhost:5500";
        }

        // Generate or load local certificate for mTLS
        InitializeLocalCertificate(request.Config);

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate,
            ClientCertificateOptions = ClientCertificateOption.Manual
        };

        if (_localCertificate != null)
        {
            handler.ClientCertificates.Add(_localCertificate);
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(QueryTimeoutSeconds)
        };

        _isRunning = true;

        // Start background tasks
        _healthCheckTask = RunHealthCheckLoopAsync(_cts.Token);
        _tokenRefreshTask = RunTokenRefreshLoopAsync(_cts.Token);

        AuditLog("System", "FederationStarted", $"Instance {_instanceId} started");
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        var tasks = new List<Task>();
        if (_healthCheckTask != null) tasks.Add(_healthCheckTask);
        if (_tokenRefreshTask != null) tasks.Add(_tokenRefreshTask);

        await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { })));

        _httpClient?.Dispose();
        _httpClient = null;
        _cts?.Dispose();
        _cts = null;

        AuditLog("System", "FederationStopped", $"Instance {_instanceId} stopped");
    }

    #endregion

    #region Message Handling

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return;

        var response = message.Type switch
        {
            "federation.trust.establish" => await HandleEstablishTrustAsync(message.Payload),
            "federation.trust.revoke" => await HandleRevokeTrustAsync(message.Payload),
            "federation.trust.list" => HandleListTrustedInstances(),
            "federation.trust.accept" => await HandleAcceptTrustAsync(message.Payload),
            "federation.query.execute" => await HandleExecuteQueryAsync(message.Payload),
            "federation.data.share" => await HandleShareDataAsync(message.Payload),
            "federation.data.request" => await HandleRequestDataAsync(message.Payload),
            "federation.identity.translate" => await HandleTranslateIdentityAsync(message.Payload),
            "federation.identity.map" => HandleMapIdentity(message.Payload),
            "federation.policy.set" => HandleSetPolicy(message.Payload),
            "federation.policy.get" => HandleGetPolicies(message.Payload),
            "federation.health.check" => await HandleHealthCheckAsync(message.Payload),
            "federation.status" => HandleGetStatus(),
            "federation.audit.query" => HandleQueryAuditLog(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    #endregion

    #region Trust Establishment

    private async Task<Dictionary<string, object>> HandleEstablishTrustAsync(Dictionary<string, object> payload)
    {
        var instanceId = payload.GetValueOrDefault("instanceId")?.ToString();
        var endpoint = payload.GetValueOrDefault("endpoint")?.ToString();
        var certificatePem = payload.GetValueOrDefault("certificate")?.ToString();
        var trustLevel = payload.GetValueOrDefault("trustLevel")?.ToString() ?? "Standard";

        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(endpoint))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "instanceId and endpoint are required"
            };
        }

        if (_trustedInstances.ContainsKey(instanceId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Instance {instanceId} is already trusted"
            };
        }

        await _trustLock.WaitAsync();
        try
        {
            // Parse trust level
            if (!Enum.TryParse<TrustLevel>(trustLevel, true, out var level))
            {
                level = TrustLevel.Standard;
            }

            // Create trust request
            var requestId = Guid.NewGuid().ToString("N");
            var request = new PendingTrustRequest
            {
                RequestId = requestId,
                RemoteInstanceId = instanceId,
                RemoteEndpoint = endpoint,
                RequestedTrustLevel = level,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddSeconds(TrustEstablishmentTimeoutSeconds),
                State = TrustRequestState.Pending
            };

            // Parse and validate certificate if provided
            X509Certificate2? remoteCert = null;
            if (!string.IsNullOrEmpty(certificatePem))
            {
                try
                {
                    remoteCert = X509Certificate2.CreateFromPem(certificatePem);
                    request.RemoteCertificateThumbprint = remoteCert.Thumbprint;
                }
                catch (Exception ex)
                {
                    return new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = $"Invalid certificate: {ex.Message}"
                    };
                }
            }

            _pendingTrustRequests[requestId] = request;

            // Send trust establishment request to remote instance
            var trustResponse = await SendTrustRequestAsync(endpoint, requestId, level);

            if (trustResponse.Success)
            {
                // Trust established
                var instance = new FederatedInstance
                {
                    InstanceId = instanceId,
                    Endpoint = endpoint,
                    TrustLevel = level,
                    Certificate = remoteCert,
                    CertificateThumbprint = remoteCert?.Thumbprint,
                    TrustedAt = DateTime.UtcNow,
                    LastHealthCheck = DateTime.UtcNow,
                    IsHealthy = true,
                    Capabilities = trustResponse.Capabilities ?? new List<string>()
                };

                _trustedInstances[instanceId] = instance;
                request.State = TrustRequestState.Accepted;

                AuditLog(instanceId, "TrustEstablished", $"Trust established with level {level}");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["requestId"] = requestId,
                    ["instanceId"] = instanceId,
                    ["trustLevel"] = level.ToString(),
                    ["trustedAt"] = instance.TrustedAt.ToString("O")
                };
            }
            else
            {
                request.State = TrustRequestState.Rejected;
                request.RejectionReason = trustResponse.Error;

                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["requestId"] = requestId,
                    ["error"] = trustResponse.Error ?? "Trust request rejected"
                };
            }
        }
        finally
        {
            _trustLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> HandleAcceptTrustAsync(Dictionary<string, object> payload)
    {
        var requestId = payload.GetValueOrDefault("requestId")?.ToString();
        var accept = payload.GetValueOrDefault("accept") as bool? ?? false;
        var reason = payload.GetValueOrDefault("reason")?.ToString();

        if (string.IsNullOrEmpty(requestId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "requestId is required"
            };
        }

        if (!_pendingTrustRequests.TryGetValue(requestId, out var request))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Trust request not found or expired"
            };
        }

        if (request.ExpiresAt < DateTime.UtcNow)
        {
            request.State = TrustRequestState.Expired;
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Trust request has expired"
            };
        }

        if (accept)
        {
            request.State = TrustRequestState.Accepted;
            AuditLog(request.RemoteInstanceId, "TrustAccepted", $"Trust request {requestId} accepted");
        }
        else
        {
            request.State = TrustRequestState.Rejected;
            request.RejectionReason = reason;
            AuditLog(request.RemoteInstanceId, "TrustRejected", $"Trust request {requestId} rejected: {reason}");
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["requestId"] = requestId,
            ["accepted"] = accept
        };
    }

    private async Task<Dictionary<string, object>> HandleRevokeTrustAsync(Dictionary<string, object> payload)
    {
        var instanceId = payload.GetValueOrDefault("instanceId")?.ToString();
        var reason = payload.GetValueOrDefault("reason")?.ToString() ?? "Trust revoked by administrator";

        if (string.IsNullOrEmpty(instanceId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "instanceId is required"
            };
        }

        if (!_trustedInstances.TryRemove(instanceId, out var instance))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Instance {instanceId} is not trusted"
            };
        }

        // Notify remote instance of trust revocation
        try
        {
            await NotifyTrustRevocationAsync(instance, reason);
        }
        catch
        {
            // Best effort notification
        }

        // Invalidate any identity mappings
        var mappingsToRemove = _identityMappings
            .Where(kv => kv.Value.SourceInstanceId == instanceId || kv.Value.TargetInstanceId == instanceId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in mappingsToRemove)
        {
            _identityMappings.TryRemove(key, out _);
        }

        AuditLog(instanceId, "TrustRevoked", reason);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["instanceId"] = instanceId,
            ["revokedAt"] = DateTime.UtcNow.ToString("O")
        };
    }

    private Dictionary<string, object> HandleListTrustedInstances()
    {
        var instances = _trustedInstances.Values.Select(i => new Dictionary<string, object>
        {
            ["instanceId"] = i.InstanceId,
            ["endpoint"] = i.Endpoint,
            ["trustLevel"] = i.TrustLevel.ToString(),
            ["trustedAt"] = i.TrustedAt.ToString("O"),
            ["isHealthy"] = i.IsHealthy,
            ["lastHealthCheck"] = i.LastHealthCheck.ToString("O"),
            ["capabilities"] = i.Capabilities
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["instances"] = instances,
            ["count"] = instances.Count
        };
    }

    #endregion

    #region Cross-Federation Queries

    private async Task<Dictionary<string, object>> HandleExecuteQueryAsync(Dictionary<string, object> payload)
    {
        var query = payload.GetValueOrDefault("query")?.ToString();
        var targetInstancesJson = payload.GetValueOrDefault("targetInstances")?.ToString();
        var timeoutSeconds = payload.GetValueOrDefault("timeout") as int? ?? QueryTimeoutSeconds;
        var aggregationStrategy = payload.GetValueOrDefault("aggregation")?.ToString() ?? "Merge";

        if (string.IsNullOrEmpty(query))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "query is required"
            };
        }

        // Determine target instances
        var targetInstances = new List<string>();
        if (!string.IsNullOrEmpty(targetInstancesJson))
        {
            try
            {
                targetInstances = JsonSerializer.Deserialize<List<string>>(targetInstancesJson) ?? new List<string>();
            }
            catch
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "Invalid targetInstances format"
                };
            }
        }
        else
        {
            // Query all trusted instances
            targetInstances = _trustedInstances.Keys.ToList();
        }

        if (targetInstances.Count == 0)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "No target instances specified or available"
            };
        }

        // Validate all target instances are trusted
        var untrustedInstances = targetInstances.Where(id => !_trustedInstances.ContainsKey(id)).ToList();
        if (untrustedInstances.Any())
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Untrusted instances: {string.Join(", ", untrustedInstances)}"
            };
        }

        var queryId = Guid.NewGuid().ToString("N");
        var execution = new QueryExecution
        {
            QueryId = queryId,
            Query = query,
            TargetInstances = targetInstances,
            StartedAt = DateTime.UtcNow,
            State = QueryExecutionState.Running
        };

        _activeQueries[queryId] = execution;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            // Execute query on all target instances in parallel
            var tasks = targetInstances.Select(instanceId =>
                ExecuteRemoteQueryAsync(instanceId, query, cts.Token));

            var results = await Task.WhenAll(tasks);

            // Aggregate results based on strategy
            var aggregatedResult = AggregateQueryResults(results, aggregationStrategy);

            execution.State = QueryExecutionState.Completed;
            execution.CompletedAt = DateTime.UtcNow;

            AuditLog("Query", "FederatedQueryExecuted", $"Query {queryId} executed on {targetInstances.Count} instances");

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["queryId"] = queryId,
                ["results"] = aggregatedResult.Results,
                ["instanceResults"] = results.Select(r => new Dictionary<string, object>
                {
                    ["instanceId"] = r.InstanceId,
                    ["success"] = r.Success,
                    ["resultCount"] = r.ResultCount,
                    ["duration"] = r.Duration.TotalMilliseconds,
                    ["error"] = r.Error ?? ""
                }).ToList(),
                ["totalResults"] = aggregatedResult.TotalCount,
                ["duration"] = (execution.CompletedAt.Value - execution.StartedAt).TotalMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            execution.State = QueryExecutionState.TimedOut;
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["queryId"] = queryId,
                ["error"] = "Query timed out"
            };
        }
        catch (Exception ex)
        {
            execution.State = QueryExecutionState.Failed;
            execution.Error = ex.Message;
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["queryId"] = queryId,
                ["error"] = ex.Message
            };
        }
        finally
        {
            _activeQueries.TryRemove(queryId, out _);
        }
    }

    private async Task<RemoteQueryResult> ExecuteRemoteQueryAsync(
        string instanceId,
        string query,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (!_trustedInstances.TryGetValue(instanceId, out var instance))
        {
            return new RemoteQueryResult
            {
                InstanceId = instanceId,
                Success = false,
                Error = "Instance not trusted",
                Duration = stopwatch.Elapsed
            };
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{instance.Endpoint}/api/federation/query")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { query, sourceInstance = _instanceId }, _jsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };

            // Add authentication header
            request.Headers.Authorization = new AuthenticationHeaderValue("Federation", GenerateAuthToken(instanceId));

            var response = await _httpClient!.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<JsonElement>(content);
                return new RemoteQueryResult
                {
                    InstanceId = instanceId,
                    Success = true,
                    Results = result,
                    ResultCount = result.TryGetProperty("count", out var count) ? count.GetInt32() : 0,
                    Duration = stopwatch.Elapsed
                };
            }
            else
            {
                return new RemoteQueryResult
                {
                    InstanceId = instanceId,
                    Success = false,
                    Error = $"HTTP {response.StatusCode}: {content}",
                    Duration = stopwatch.Elapsed
                };
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new RemoteQueryResult
            {
                InstanceId = instanceId,
                Success = false,
                Error = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    private AggregatedQueryResult AggregateQueryResults(RemoteQueryResult[] results, string strategy)
    {
        var aggregated = new AggregatedQueryResult
        {
            Results = new List<object>(),
            TotalCount = 0
        };

        foreach (var result in results.Where(r => r.Success && r.Results.ValueKind != JsonValueKind.Undefined))
        {
            if (result.Results.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    aggregated.Results.Add(item);
                    aggregated.TotalCount++;
                }
            }
            else
            {
                aggregated.Results.Add(result.Results);
                aggregated.TotalCount += result.ResultCount;
            }
        }

        return aggregated;
    }

    #endregion

    #region Data Sharing

    private async Task<Dictionary<string, object>> HandleShareDataAsync(Dictionary<string, object> payload)
    {
        var targetInstanceId = payload.GetValueOrDefault("targetInstance")?.ToString();
        var dataId = payload.GetValueOrDefault("dataId")?.ToString();
        var dataType = payload.GetValueOrDefault("dataType")?.ToString() ?? "blob";
        var accessLevel = payload.GetValueOrDefault("accessLevel")?.ToString() ?? "Read";
        var expiresAtStr = payload.GetValueOrDefault("expiresAt")?.ToString();

        if (string.IsNullOrEmpty(targetInstanceId) || string.IsNullOrEmpty(dataId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "targetInstance and dataId are required"
            };
        }

        if (!_trustedInstances.TryGetValue(targetInstanceId, out var instance))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Instance {targetInstanceId} is not trusted"
            };
        }

        // Check trust level allows sharing
        if (instance.TrustLevel < TrustLevel.Standard)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Insufficient trust level for data sharing"
            };
        }

        // Check sharing policy
        if (!IsDataSharingAllowed(targetInstanceId, dataType, accessLevel))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Data sharing not permitted by policy"
            };
        }

        DateTime? expiresAt = null;
        if (!string.IsNullOrEmpty(expiresAtStr) && DateTime.TryParse(expiresAtStr, out var expiry))
        {
            expiresAt = expiry;
        }

        var shareId = Guid.NewGuid().ToString("N");

        try
        {
            // Notify remote instance of shared data
            var shareResponse = await NotifyDataShareAsync(instance, shareId, dataId, dataType, accessLevel, expiresAt);

            if (shareResponse.Success)
            {
                AuditLog(targetInstanceId, "DataShared", $"Shared {dataType} {dataId} with access level {accessLevel}");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["shareId"] = shareId,
                    ["dataId"] = dataId,
                    ["targetInstance"] = targetInstanceId,
                    ["accessLevel"] = accessLevel,
                    ["expiresAt"] = expiresAt?.ToString("O") ?? "never"
                };
            }
            else
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = shareResponse.Error ?? "Remote instance rejected share"
                };
            }
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task<Dictionary<string, object>> HandleRequestDataAsync(Dictionary<string, object> payload)
    {
        var sourceInstanceId = payload.GetValueOrDefault("sourceInstance")?.ToString();
        var dataId = payload.GetValueOrDefault("dataId")?.ToString();

        if (string.IsNullOrEmpty(sourceInstanceId) || string.IsNullOrEmpty(dataId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "sourceInstance and dataId are required"
            };
        }

        if (!_trustedInstances.TryGetValue(sourceInstanceId, out var instance))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Instance {sourceInstanceId} is not trusted"
            };
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{instance.Endpoint}/api/federation/data/{dataId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Federation", GenerateAuthToken(sourceInstanceId));

            var response = await _httpClient!.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(content);

                AuditLog(sourceInstanceId, "DataRequested", $"Retrieved data {dataId}");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["dataId"] = dataId,
                    ["sourceInstance"] = sourceInstanceId,
                    ["data"] = data
                };
            }
            else
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = $"HTTP {response.StatusCode}"
                };
            }
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private bool IsDataSharingAllowed(string targetInstanceId, string dataType, string accessLevel)
    {
        // Check global policies
        foreach (var policy in _policies.Values.Where(p => p.IsEnabled))
        {
            if (policy.TargetInstanceId == "*" || policy.TargetInstanceId == targetInstanceId)
            {
                if (policy.DeniedDataTypes.Contains(dataType) || policy.DeniedDataTypes.Contains("*"))
                {
                    return false;
                }

                if (policy.AllowedDataTypes.Contains(dataType) || policy.AllowedDataTypes.Contains("*"))
                {
                    if (policy.MaxAccessLevel != null)
                    {
                        var requestedLevel = Enum.TryParse<AccessLevel>(accessLevel, out var level) ? level : AccessLevel.Read;
                        if (requestedLevel > policy.MaxAccessLevel)
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }
        }

        // Default: allow if trusted
        return _trustedInstances.ContainsKey(targetInstanceId);
    }

    #endregion

    #region Identity Federation

    private async Task<Dictionary<string, object>> HandleTranslateIdentityAsync(Dictionary<string, object> payload)
    {
        var sourceToken = payload.GetValueOrDefault("sourceToken")?.ToString();
        var sourceInstanceId = payload.GetValueOrDefault("sourceInstance")?.ToString();
        var targetInstanceId = payload.GetValueOrDefault("targetInstance")?.ToString();
        var tokenType = payload.GetValueOrDefault("tokenType")?.ToString() ?? "Bearer";

        if (string.IsNullOrEmpty(sourceToken) || string.IsNullOrEmpty(sourceInstanceId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "sourceToken and sourceInstance are required"
            };
        }

        // Validate source instance is trusted
        if (!_trustedInstances.TryGetValue(sourceInstanceId, out var sourceInstance))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Source instance {sourceInstanceId} is not trusted"
            };
        }

        // Check trust level supports identity federation
        if (sourceInstance.TrustLevel < TrustLevel.Full)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Trust level does not support identity federation"
            };
        }

        try
        {
            // Validate token with source instance
            var validationResult = await ValidateTokenWithInstanceAsync(sourceInstance, sourceToken, tokenType);

            if (!validationResult.IsValid)
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "Token validation failed"
                };
            }

            // Look up identity mapping
            var mappingKey = $"{sourceInstanceId}:{validationResult.SubjectId}";
            var localIdentity = validationResult.SubjectId;

            if (_identityMappings.TryGetValue(mappingKey, out var mapping))
            {
                localIdentity = mapping.LocalIdentityId;
            }

            // Generate local token
            var localToken = GenerateLocalToken(localIdentity, validationResult.Claims, validationResult.ExpiresAt);

            AuditLog(sourceInstanceId, "IdentityTranslated", $"Translated identity for {validationResult.SubjectId}");

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["localToken"] = localToken,
                ["localIdentity"] = localIdentity,
                ["sourceIdentity"] = validationResult.SubjectId,
                ["expiresAt"] = validationResult.ExpiresAt?.ToString("O") ?? ""
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private Dictionary<string, object> HandleMapIdentity(Dictionary<string, object> payload)
    {
        var sourceInstanceId = payload.GetValueOrDefault("sourceInstance")?.ToString();
        var sourceIdentityId = payload.GetValueOrDefault("sourceIdentity")?.ToString();
        var localIdentityId = payload.GetValueOrDefault("localIdentity")?.ToString();

        if (string.IsNullOrEmpty(sourceInstanceId) ||
            string.IsNullOrEmpty(sourceIdentityId) ||
            string.IsNullOrEmpty(localIdentityId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "sourceInstance, sourceIdentity, and localIdentity are required"
            };
        }

        var mappingKey = $"{sourceInstanceId}:{sourceIdentityId}";
        var mapping = new IdentityMapping
        {
            SourceInstanceId = sourceInstanceId,
            SourceIdentityId = sourceIdentityId,
            LocalIdentityId = localIdentityId,
            TargetInstanceId = _instanceId,
            CreatedAt = DateTime.UtcNow
        };

        _identityMappings[mappingKey] = mapping;

        AuditLog(sourceInstanceId, "IdentityMapped", $"Mapped {sourceIdentityId} to {localIdentityId}");

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["mappingKey"] = mappingKey,
            ["sourceIdentity"] = sourceIdentityId,
            ["localIdentity"] = localIdentityId
        };
    }

    private async Task<TokenValidationResult> ValidateTokenWithInstanceAsync(
        FederatedInstance instance,
        string token,
        string tokenType)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{instance.Endpoint}/api/federation/identity/validate")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { token, tokenType }, _jsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Federation", GenerateAuthToken(instance.InstanceId));

            var response = await _httpClient!.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<JsonElement>(content);
                return new TokenValidationResult
                {
                    IsValid = true,
                    SubjectId = result.GetProperty("subjectId").GetString() ?? "",
                    Claims = new Dictionary<string, string>(),
                    ExpiresAt = result.TryGetProperty("expiresAt", out var exp)
                        ? DateTime.Parse(exp.GetString()!)
                        : null
                };
            }
            else
            {
                return new TokenValidationResult { IsValid = false };
            }
        }
        catch
        {
            return new TokenValidationResult { IsValid = false };
        }
    }

    private string GenerateLocalToken(string identity, Dictionary<string, string> claims, DateTime? expiresAt)
    {
        var tokenData = new
        {
            sub = identity,
            iss = _instanceId,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = expiresAt.HasValue
                ? new DateTimeOffset(expiresAt.Value).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            claims
        };

        var json = JsonSerializer.Serialize(tokenData, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Sign with local private key
        if (_localPrivateKey != null)
        {
            var signature = _localPrivateKey.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{Convert.ToBase64String(bytes)}.{Convert.ToBase64String(signature)}";
        }

        return Convert.ToBase64String(bytes);
    }

    #endregion

    #region Policies

    private Dictionary<string, object> HandleSetPolicy(Dictionary<string, object> payload)
    {
        var policyId = payload.GetValueOrDefault("policyId")?.ToString() ?? Guid.NewGuid().ToString("N");
        var name = payload.GetValueOrDefault("name")?.ToString() ?? "Unnamed Policy";
        var targetInstanceId = payload.GetValueOrDefault("targetInstance")?.ToString() ?? "*";
        var allowedTypesJson = payload.GetValueOrDefault("allowedDataTypes")?.ToString();
        var deniedTypesJson = payload.GetValueOrDefault("deniedDataTypes")?.ToString();
        var maxAccessLevelStr = payload.GetValueOrDefault("maxAccessLevel")?.ToString();
        var isEnabled = payload.GetValueOrDefault("enabled") as bool? ?? true;

        var maxLevel = AccessLevel.Read;
        if (!string.IsNullOrEmpty(maxAccessLevelStr) && Enum.TryParse<AccessLevel>(maxAccessLevelStr, out var parsedLevel))
        {
            maxLevel = parsedLevel;
        }

        var policy = new FederationPolicy
        {
            PolicyId = policyId,
            Name = name,
            TargetInstanceId = targetInstanceId,
            IsEnabled = isEnabled,
            CreatedAt = DateTime.UtcNow,
            AllowedDataTypes = ParseJsonStringArray(allowedTypesJson) ?? new List<string> { "*" },
            DeniedDataTypes = ParseJsonStringArray(deniedTypesJson) ?? new List<string>(),
            MaxAccessLevel = maxLevel
        };

        _policies[policyId] = policy;

        AuditLog("Policy", "PolicySet", $"Set policy {policyId}: {name}");

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["policyId"] = policyId,
            ["name"] = name
        };
    }

    private Dictionary<string, object> HandleGetPolicies(Dictionary<string, object> payload)
    {
        var targetInstanceId = payload.GetValueOrDefault("targetInstance")?.ToString();

        var policies = _policies.Values
            .Where(p => string.IsNullOrEmpty(targetInstanceId) ||
                        p.TargetInstanceId == targetInstanceId ||
                        p.TargetInstanceId == "*")
            .Select(p => new Dictionary<string, object>
            {
                ["policyId"] = p.PolicyId,
                ["name"] = p.Name,
                ["targetInstance"] = p.TargetInstanceId,
                ["allowedDataTypes"] = p.AllowedDataTypes,
                ["deniedDataTypes"] = p.DeniedDataTypes,
                ["maxAccessLevel"] = p.MaxAccessLevel?.ToString() ?? "unlimited",
                ["enabled"] = p.IsEnabled,
                ["createdAt"] = p.CreatedAt.ToString("O")
            })
            .ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["policies"] = policies,
            ["count"] = policies.Count
        };
    }

    #endregion

    #region Health & Status

    private async Task<Dictionary<string, object>> HandleHealthCheckAsync(Dictionary<string, object> payload)
    {
        var targetInstanceId = payload.GetValueOrDefault("instanceId")?.ToString();

        if (!string.IsNullOrEmpty(targetInstanceId))
        {
            // Check specific instance
            if (!_trustedInstances.TryGetValue(targetInstanceId, out var instance))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = $"Instance {targetInstanceId} is not trusted"
                };
            }

            var health = await CheckInstanceHealthAsync(instance);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["instanceId"] = targetInstanceId,
                ["isHealthy"] = health.IsHealthy,
                ["latencyMs"] = health.LatencyMs,
                ["lastCheck"] = health.CheckedAt.ToString("O"),
                ["error"] = health.Error ?? ""
            };
        }
        else
        {
            // Check all instances
            var results = new List<Dictionary<string, object>>();

            foreach (var instance in _trustedInstances.Values)
            {
                var health = await CheckInstanceHealthAsync(instance);
                results.Add(new Dictionary<string, object>
                {
                    ["instanceId"] = instance.InstanceId,
                    ["isHealthy"] = health.IsHealthy,
                    ["latencyMs"] = health.LatencyMs,
                    ["lastCheck"] = health.CheckedAt.ToString("O"),
                    ["error"] = health.Error ?? ""
                });
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["instances"] = results,
                ["healthyCount"] = results.Count(r => (bool)r["isHealthy"]),
                ["totalCount"] = results.Count
            };
        }
    }

    private Dictionary<string, object> HandleGetStatus()
    {
        var healthyCount = _trustedInstances.Values.Count(i => i.IsHealthy);
        var totalCount = _trustedInstances.Count;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["instanceId"] = _instanceId,
            ["endpoint"] = _instanceEndpoint,
            ["isRunning"] = _isRunning,
            ["trustedInstances"] = totalCount,
            ["healthyInstances"] = healthyCount,
            ["pendingTrustRequests"] = _pendingTrustRequests.Count(r => r.Value.State == TrustRequestState.Pending),
            ["activeQueries"] = _activeQueries.Count,
            ["policies"] = _policies.Count,
            ["identityMappings"] = _identityMappings.Count
        };
    }

    private Dictionary<string, object> HandleQueryAuditLog(Dictionary<string, object> payload)
    {
        var instanceId = payload.GetValueOrDefault("instanceId")?.ToString();
        var action = payload.GetValueOrDefault("action")?.ToString();
        var limit = payload.GetValueOrDefault("limit") as int? ?? 100;
        var afterStr = payload.GetValueOrDefault("after")?.ToString();

        DateTime? after = null;
        if (!string.IsNullOrEmpty(afterStr) && DateTime.TryParse(afterStr, out var afterDate))
        {
            after = afterDate;
        }

        var entries = _auditLog
            .Where(e => (string.IsNullOrEmpty(instanceId) || e.InstanceId == instanceId) &&
                       (string.IsNullOrEmpty(action) || e.Action == action) &&
                       (!after.HasValue || e.Timestamp > after))
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .Select(e => new Dictionary<string, object>
            {
                ["timestamp"] = e.Timestamp.ToString("O"),
                ["instanceId"] = e.InstanceId,
                ["action"] = e.Action,
                ["details"] = e.Details
            })
            .ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["entries"] = entries,
            ["count"] = entries.Count
        };
    }

    #endregion

    #region IReplicationService Implementation

    /// <inheritdoc />
    public override async Task<bool> RestoreAsync(string blobId, string? replicaId)
    {
        if (string.IsNullOrEmpty(replicaId))
        {
            // Try all federated instances
            foreach (var instance in _trustedInstances.Values.Where(i => i.IsHealthy))
            {
                try
                {
                    var result = await HandleRequestDataAsync(new Dictionary<string, object>
                    {
                        ["sourceInstance"] = instance.InstanceId,
                        ["dataId"] = blobId
                    });

                    if (result.GetValueOrDefault("success") is bool success && success)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Try next instance
                }
            }
            return false;
        }
        else
        {
            var result = await HandleRequestDataAsync(new Dictionary<string, object>
            {
                ["sourceInstance"] = replicaId,
                ["dataId"] = blobId
            });

            return result.GetValueOrDefault("success") is bool success && success;
        }
    }

    /// <inheritdoc />
    public override Task<string[]> GetAvailableReplicasAsync(string blobId)
    {
        return Task.FromResult(_trustedInstances.Values
            .Where(i => i.IsHealthy && i.TrustLevel >= TrustLevel.Standard)
            .Select(i => i.InstanceId)
            .ToArray());
    }

    #endregion

    #region Background Tasks

    private async Task RunHealthCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(HealthCheckIntervalSeconds), ct);

                foreach (var instance in _trustedInstances.Values.ToList())
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var health = await CheckInstanceHealthAsync(instance);
                        instance.IsHealthy = health.IsHealthy;
                        instance.LastHealthCheck = health.CheckedAt;
                    }
                    catch
                    {
                        instance.IsHealthy = false;
                        instance.LastHealthCheck = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue health checks
            }
        }
    }

    private async Task RunTokenRefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(TokenRefreshIntervalSeconds), ct);

                // Clean up expired trust requests
                var expiredRequests = _pendingTrustRequests
                    .Where(r => r.Value.ExpiresAt < DateTime.UtcNow)
                    .Select(r => r.Key)
                    .ToList();

                foreach (var key in expiredRequests)
                {
                    if (_pendingTrustRequests.TryRemove(key, out var request))
                    {
                        request.State = TrustRequestState.Expired;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue
            }
        }
    }

    private async Task<InstanceHealthResult> CheckInstanceHealthAsync(FederatedInstance instance)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{instance.Endpoint}/api/federation/health");
            request.Headers.Authorization = new AuthenticationHeaderValue("Federation", GenerateAuthToken(instance.InstanceId));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _httpClient!.SendAsync(request, cts.Token);

            stopwatch.Stop();

            return new InstanceHealthResult
            {
                IsHealthy = response.IsSuccessStatusCode,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                CheckedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new InstanceHealthResult
            {
                IsHealthy = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                CheckedAt = DateTime.UtcNow,
                Error = ex.Message
            };
        }
    }

    #endregion

    #region Helper Methods

    private void InitializeLocalCertificate(Dictionary<string, object>? config)
    {
        try
        {
            if (config?.TryGetValue("certificatePath", out var certPath) == true && certPath is string path)
            {
                var password = config.TryGetValue("certificatePassword", out var pwd)
                    ? pwd?.ToString()
                    : null;

                _localCertificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
                _localPrivateKey = _localCertificate.GetRSAPrivateKey();
            }
            else
            {
                // Generate self-signed certificate for development
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    $"CN=DataWarehouse-{_instanceId}",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        critical: true));

                _localCertificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddYears(1));

                _localPrivateKey = _localCertificate.GetRSAPrivateKey();
            }
        }
        catch
        {
            // Certificate initialization failed - continue without mTLS
        }
    }

    private bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? cert,
        X509Chain? chain,
        System.Net.Security.SslPolicyErrors errors)
    {
        if (cert == null) return false;

        // Check if certificate thumbprint matches a trusted instance
        var thumbprint = cert.Thumbprint;
        return _trustedInstances.Values.Any(i =>
            string.Equals(i.CertificateThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    private string GenerateAuthToken(string targetInstanceId)
    {
        var tokenData = new
        {
            iss = _instanceId,
            aud = targetInstanceId,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        };

        var json = JsonSerializer.Serialize(tokenData, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        if (_localPrivateKey != null)
        {
            var signature = _localPrivateKey.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{Convert.ToBase64String(bytes)}.{Convert.ToBase64String(signature)}";
        }

        return Convert.ToBase64String(bytes);
    }

    private async Task<TrustEstablishmentResponse> SendTrustRequestAsync(
        string endpoint,
        string requestId,
        TrustLevel trustLevel)
    {
        try
        {
            var requestBody = new
            {
                requestId,
                sourceInstance = _instanceId,
                sourceEndpoint = _instanceEndpoint,
                requestedTrustLevel = trustLevel.ToString(),
                certificate = _localCertificate != null
                    ? Convert.ToBase64String(_localCertificate.Export(X509ContentType.Cert))
                    : null,
                capabilities = new[] { "query", "data_share", "identity_federation" }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient!.PostAsync($"{endpoint}/api/federation/trust/request", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<JsonElement>(responseContent);
                return new TrustEstablishmentResponse
                {
                    Success = true,
                    Capabilities = result.TryGetProperty("capabilities", out var caps)
                        ? caps.EnumerateArray().Select(c => c.GetString()!).ToList()
                        : new List<string>()
                };
            }
            else
            {
                return new TrustEstablishmentResponse
                {
                    Success = false,
                    Error = responseContent
                };
            }
        }
        catch (Exception ex)
        {
            return new TrustEstablishmentResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private async Task NotifyTrustRevocationAsync(FederatedInstance instance, string reason)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { sourceInstance = _instanceId, reason }, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            await _httpClient!.PostAsync($"{instance.Endpoint}/api/federation/trust/revoked", content);
        }
        catch
        {
            // Best effort
        }
    }

    private async Task<DataShareResponse> NotifyDataShareAsync(
        FederatedInstance instance,
        string shareId,
        string dataId,
        string dataType,
        string accessLevel,
        DateTime? expiresAt)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    shareId,
                    dataId,
                    dataType,
                    accessLevel,
                    expiresAt = expiresAt?.ToString("O"),
                    sourceInstance = _instanceId
                }, _jsonOptions),
                Encoding.UTF8,
                "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{instance.Endpoint}/api/federation/data/shared")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Federation", GenerateAuthToken(instance.InstanceId));

            var response = await _httpClient!.SendAsync(request);

            return new DataShareResponse
            {
                Success = response.IsSuccessStatusCode
            };
        }
        catch (Exception ex)
        {
            return new DataShareResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private void AuditLog(string instanceId, string action, string details)
    {
        lock (_auditLock)
        {
            _auditLog.Enqueue(new FederationAuditEntry
            {
                Timestamp = DateTime.UtcNow,
                InstanceId = instanceId,
                Action = action,
                Details = details
            });

            // Trim old entries
            while (_auditLog.Count > MaxAuditEntries)
            {
                _auditLog.TryDequeue(out _);
            }
        }
    }

    private List<string>? ParseJsonStringArray(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "trust.establish", Description = "Establish trust with remote instance" },
            new() { Name = "trust.revoke", Description = "Revoke trust from instance" },
            new() { Name = "query.execute", Description = "Execute cross-federation queries" },
            new() { Name = "data.share", Description = "Share data with federated instances" },
            new() { Name = "identity.translate", Description = "Translate identity tokens" },
            new() { Name = "policy.manage", Description = "Manage federation policies" }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "Federation";
        metadata["InstanceId"] = _instanceId;
        metadata["SupportsMTLS"] = _localCertificate != null;
        metadata["SupportsIdentityFederation"] = true;
        metadata["SupportsCrossQueries"] = true;
        metadata["SupportsDataSharing"] = true;
        return metadata;
    }

    #endregion

    #region Internal Types

    private sealed class FederatedInstance
    {
        public string InstanceId { get; init; } = string.Empty;
        public string Endpoint { get; init; } = string.Empty;
        public TrustLevel TrustLevel { get; init; }
        public X509Certificate2? Certificate { get; init; }
        public string? CertificateThumbprint { get; init; }
        public DateTime TrustedAt { get; init; }
        public DateTime LastHealthCheck { get; set; }
        public bool IsHealthy { get; set; }
        public List<string> Capabilities { get; init; } = new();
    }

    private sealed class PendingTrustRequest
    {
        public string RequestId { get; init; } = string.Empty;
        public string RemoteInstanceId { get; init; } = string.Empty;
        public string RemoteEndpoint { get; init; } = string.Empty;
        public string? RemoteCertificateThumbprint { get; set; }
        public TrustLevel RequestedTrustLevel { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public TrustRequestState State { get; set; }
        public string? RejectionReason { get; set; }
    }

    private sealed class FederationPolicy
    {
        public string PolicyId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string TargetInstanceId { get; init; } = "*";
        public List<string> AllowedDataTypes { get; init; } = new();
        public List<string> DeniedDataTypes { get; init; } = new();
        public AccessLevel? MaxAccessLevel { get; init; }
        public bool IsEnabled { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class IdentityMapping
    {
        public string SourceInstanceId { get; init; } = string.Empty;
        public string SourceIdentityId { get; init; } = string.Empty;
        public string TargetInstanceId { get; init; } = string.Empty;
        public string LocalIdentityId { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }

    private sealed class QueryExecution
    {
        public string QueryId { get; init; } = string.Empty;
        public string Query { get; init; } = string.Empty;
        public List<string> TargetInstances { get; init; } = new();
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public QueryExecutionState State { get; set; }
        public string? Error { get; set; }
    }

    private sealed class RemoteQueryResult
    {
        public string InstanceId { get; init; } = string.Empty;
        public bool Success { get; init; }
        public JsonElement Results { get; init; }
        public int ResultCount { get; init; }
        public TimeSpan Duration { get; init; }
        public string? Error { get; init; }
    }

    private sealed class AggregatedQueryResult
    {
        public List<object> Results { get; init; } = new();
        public int TotalCount { get; set; }
    }

    private sealed class FederationAuditEntry
    {
        public DateTime Timestamp { get; init; }
        public string InstanceId { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
    }

    private sealed class TrustEstablishmentResponse
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public List<string>? Capabilities { get; init; }
    }

    private sealed class DataShareResponse
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
    }

    private sealed class TokenValidationResult
    {
        public bool IsValid { get; init; }
        public string SubjectId { get; init; } = string.Empty;
        public Dictionary<string, string> Claims { get; init; } = new();
        public DateTime? ExpiresAt { get; init; }
    }

    private sealed class InstanceHealthResult
    {
        public bool IsHealthy { get; init; }
        public long LatencyMs { get; init; }
        public DateTime CheckedAt { get; init; }
        public string? Error { get; init; }
    }

    private enum TrustLevel
    {
        Limited = 1,
        Standard = 2,
        Full = 3,
        Administrative = 4
    }

    private enum TrustRequestState
    {
        Pending,
        Accepted,
        Rejected,
        Expired
    }

    private enum QueryExecutionState
    {
        Pending,
        Running,
        Completed,
        Failed,
        TimedOut
    }

    private enum AccessLevel
    {
        Read = 1,
        Write = 2,
        Delete = 3,
        Admin = 4
    }

    #endregion
}
