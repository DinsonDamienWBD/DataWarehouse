using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Federation;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// C9: CAPABILITY VERIFICATION HANDLERS WITH AUDIT TRAIL
// H3: Comprehensive Audit Logging
// H4: Capability Audit Trail
// ============================================================================

#region Capability Verification Handler

/// <summary>
/// Handles capability verification requests with full audit trail.
/// </summary>
public sealed class CapabilityVerificationHandler
{
    private readonly CapabilityVerifier _verifier;
    private readonly CapabilityStore _store;
    private readonly CapabilityAuditTrail _auditTrail;
    private readonly ConcurrentDictionary<string, CachedVerification> _verificationCache = new();
    private readonly TimeSpan _cacheExpiry;

    public CapabilityVerificationHandler(
        CapabilityVerifier verifier,
        CapabilityStore store,
        CapabilityAuditTrail? auditTrail = null,
        TimeSpan? cacheExpiry = null)
    {
        _verifier = verifier;
        _store = store;
        _auditTrail = auditTrail ?? new CapabilityAuditTrail();
        _cacheExpiry = cacheExpiry ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Handles a capability verification request message.
    /// </summary>
    public async Task<VerifyCapabilityResponse> HandleVerifyRequestAsync(
        VerifyCapabilityRequest request,
        string requesterId,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Check cache first
            var cacheKey = $"{request.TokenId}:{request.TargetId}:{request.RequiredPermissions}";
            if (_verificationCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                await LogAuditEventAsync(request, cached.Result.IsValid, requesterId, "cached", startTime, ct);
                return new VerifyCapabilityResponse
                {
                    RequestId = request.RequestId,
                    IsValid = cached.Result.IsValid,
                    ErrorMessage = cached.Result.ErrorMessage,
                    FromCache = true
                };
            }

            // Get token
            var token = await _store.GetTokenAsync(request.TokenId, ct);
            if (token == null)
            {
                await LogAuditEventAsync(request, false, requesterId, "token_not_found", startTime, ct);
                return new VerifyCapabilityResponse
                {
                    RequestId = request.RequestId,
                    IsValid = false,
                    ErrorMessage = "Token not found"
                };
            }

            // Build context
            var context = new CapabilityContext
            {
                CurrentTime = DateTimeOffset.UtcNow,
                RequesterId = requesterId,
                RequestIp = request.RequestIp,
                DeviceId = request.DeviceId,
                NodeId = request.NodeId,
                Region = request.Region
            };

            // Verify
            var result = await _verifier.VerifyAsync(token, context, ct);

            // Check specific access if requested
            if (result.IsValid && !string.IsNullOrEmpty(request.TargetId))
            {
                var hasAccess = await _verifier.CanAccessAsync(
                    token, request.TargetId, request.RequiredPermissions, context, ct);

                if (!hasAccess)
                {
                    result = VerificationResult.Failed("Access denied for target/permissions");
                }
            }

            // Record usage
            if (result.IsValid)
            {
                await _store.RecordUsageAsync(request.TokenId, ct);
            }

            // Cache result
            _verificationCache[cacheKey] = new CachedVerification
            {
                Result = result,
                ExpiresAt = DateTime.UtcNow.Add(_cacheExpiry)
            };

            // Audit
            await LogAuditEventAsync(request, result.IsValid, requesterId,
                result.IsValid ? "verified" : result.ErrorMessage ?? "verification_failed", startTime, ct);

            return new VerifyCapabilityResponse
            {
                RequestId = request.RequestId,
                IsValid = result.IsValid,
                ErrorMessage = result.ErrorMessage,
                Token = result.IsValid ? token : null,
                VerifiedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            await LogAuditEventAsync(request, false, requesterId, $"error:{ex.Message}", startTime, ct);
            return new VerifyCapabilityResponse
            {
                RequestId = request.RequestId,
                IsValid = false,
                ErrorMessage = $"Verification error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Handles a capability refresh request.
    /// </summary>
    public async Task<RefreshCapabilityResponse> HandleRefreshRequestAsync(
        RefreshCapabilityRequest request,
        string requesterId,
        CancellationToken ct = default)
    {
        var token = await _store.GetTokenAsync(request.TokenId, ct);
        if (token == null)
        {
            return new RefreshCapabilityResponse
            {
                RequestId = request.RequestId,
                Success = false,
                ErrorMessage = "Token not found"
            };
        }

        // Verify current token is valid
        var context = new CapabilityContext
        {
            CurrentTime = DateTimeOffset.UtcNow,
            RequesterId = requesterId
        };
        var result = await _verifier.VerifyAsync(token, context, ct);
        if (!result.IsValid)
        {
            return new RefreshCapabilityResponse
            {
                RequestId = request.RequestId,
                Success = false,
                ErrorMessage = "Current token is invalid"
            };
        }

        // Create refreshed token with new expiry
        var refreshedToken = await _store.RefreshTokenAsync(request.TokenId, request.NewExpiry, ct);
        if (refreshedToken == null)
        {
            return new RefreshCapabilityResponse
            {
                RequestId = request.RequestId,
                Success = false,
                ErrorMessage = "Token refresh failed"
            };
        }

        // Audit
        await _auditTrail.LogEventAsync(new CapabilityAuditEvent
        {
            EventType = CapabilityAuditEventType.TokenRefreshed,
            TokenId = request.TokenId,
            ActorId = requesterId,
            Details = $"Refreshed until {request.NewExpiry}"
        }, ct);

        return new RefreshCapabilityResponse
        {
            RequestId = request.RequestId,
            Success = true,
            RefreshedToken = refreshedToken
        };
    }

    /// <summary>
    /// Handles a revocation list check request.
    /// </summary>
    public async Task<CheckRevocationResponse> HandleCheckRevocationAsync(
        CheckRevocationRequest request,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, bool>();
        foreach (var tokenId in request.TokenIds)
        {
            results[tokenId] = await _store.IsRevokedAsync(tokenId, ct);
        }

        return new CheckRevocationResponse
        {
            RequestId = request.RequestId,
            RevokedTokens = results.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList(),
            CheckedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Handles a capability chain validation request.
    /// </summary>
    public async Task<ValidateChainResponse> HandleValidateChainAsync(
        ValidateChainRequest request,
        CancellationToken ct = default)
    {
        var token = await _store.GetTokenAsync(request.TokenId, ct);
        if (token == null)
        {
            return new ValidateChainResponse
            {
                RequestId = request.RequestId,
                IsValid = false,
                ErrorMessage = "Token not found"
            };
        }

        // Build chain
        var chain = new List<CapabilityToken> { token };
        var current = token;

        while (!string.IsNullOrEmpty(current.ParentTokenId) && chain.Count < 20)
        {
            var parent = await _store.GetTokenAsync(current.ParentTokenId, ct);
            if (parent == null)
            {
                return new ValidateChainResponse
                {
                    RequestId = request.RequestId,
                    IsValid = false,
                    ErrorMessage = $"Parent token {current.ParentTokenId} not found",
                    Chain = chain
                };
            }

            chain.Add(parent);
            current = parent;
        }

        // Validate entire chain
        foreach (var chainToken in chain)
        {
            if (await _store.IsRevokedAsync(chainToken.TokenId, ct))
            {
                return new ValidateChainResponse
                {
                    RequestId = request.RequestId,
                    IsValid = false,
                    ErrorMessage = $"Token {chainToken.TokenId} in chain is revoked",
                    Chain = chain
                };
            }
        }

        return new ValidateChainResponse
        {
            RequestId = request.RequestId,
            IsValid = true,
            Chain = chain
        };
    }

    private async Task LogAuditEventAsync(
        VerifyCapabilityRequest request,
        bool success,
        string requesterId,
        string result,
        DateTime startTime,
        CancellationToken ct)
    {
        await _auditTrail.LogEventAsync(new CapabilityAuditEvent
        {
            EventType = success ? CapabilityAuditEventType.VerificationSuccess : CapabilityAuditEventType.VerificationFailure,
            TokenId = request.TokenId,
            TargetId = request.TargetId,
            ActorId = requesterId,
            RequestIp = request.RequestIp,
            Details = result,
            Duration = DateTime.UtcNow - startTime
        }, ct);
    }

    private sealed class CachedVerification
    {
        public required VerificationResult Result { get; init; }
        public DateTime ExpiresAt { get; init; }
    }
}

#endregion

#region Request/Response Messages

public sealed class VerifyCapabilityRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public required string TokenId { get; init; }
    public string? TargetId { get; init; }
    public CapabilityPermissions RequiredPermissions { get; init; }
    public string? RequestIp { get; init; }
    public string? DeviceId { get; init; }
    public string? NodeId { get; init; }
    public string? Region { get; init; }
}

public sealed class VerifyCapabilityResponse
{
    public required string RequestId { get; init; }
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public CapabilityToken? Token { get; init; }
    public DateTime VerifiedAt { get; init; }
    public bool FromCache { get; init; }
}

public sealed class RefreshCapabilityRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public required string TokenId { get; init; }
    public DateTimeOffset NewExpiry { get; init; }
}

public sealed class RefreshCapabilityResponse
{
    public required string RequestId { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public CapabilityToken? RefreshedToken { get; init; }
}

public sealed class CheckRevocationRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public required List<string> TokenIds { get; init; }
}

public sealed class CheckRevocationResponse
{
    public required string RequestId { get; init; }
    public List<string> RevokedTokens { get; init; } = new();
    public DateTime CheckedAt { get; init; }
}

public sealed class ValidateChainRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public required string TokenId { get; init; }
}

public sealed class ValidateChainResponse
{
    public required string RequestId { get; init; }
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public List<CapabilityToken> Chain { get; init; } = new();
}

#endregion

#region Capability Audit Trail

/// <summary>
/// Immutable audit trail for capability operations.
/// </summary>
public sealed class CapabilityAuditTrail
{
    private readonly ConcurrentQueue<CapabilityAuditEvent> _events = new();
    private readonly string? _persistPath;
    private readonly int _maxInMemory;
    private long _sequenceNumber;

    public CapabilityAuditTrail(string? persistPath = null, int maxInMemory = 10000)
    {
        _persistPath = persistPath;
        _maxInMemory = maxInMemory;
    }

    /// <summary>
    /// Logs an audit event.
    /// </summary>
    public async Task LogEventAsync(CapabilityAuditEvent evt, CancellationToken ct = default)
    {
        evt.SequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        evt.Timestamp = DateTime.UtcNow;

        // Calculate hash chain
        var prevHash = _events.LastOrDefault()?.Hash ?? new byte[32];
        evt.Hash = ComputeEventHash(evt, prevHash);

        _events.Enqueue(evt);

        // Trim if over limit
        while (_events.Count > _maxInMemory)
        {
            _events.TryDequeue(out _);
        }

        // Persist if path configured
        if (!string.IsNullOrEmpty(_persistPath))
        {
            await PersistEventAsync(evt, ct);
        }
    }

    /// <summary>
    /// Queries audit events.
    /// </summary>
    public IEnumerable<CapabilityAuditEvent> Query(
        DateTime? from = null,
        DateTime? to = null,
        string? tokenId = null,
        string? actorId = null,
        CapabilityAuditEventType? eventType = null)
    {
        return _events.Where(e =>
            (!from.HasValue || e.Timestamp >= from.Value) &&
            (!to.HasValue || e.Timestamp <= to.Value) &&
            (string.IsNullOrEmpty(tokenId) || e.TokenId == tokenId) &&
            (string.IsNullOrEmpty(actorId) || e.ActorId == actorId) &&
            (!eventType.HasValue || e.EventType == eventType.Value));
    }

    /// <summary>
    /// Gets audit statistics.
    /// </summary>
    public CapabilityAuditStats GetStats()
    {
        var events = _events.ToArray();
        return new CapabilityAuditStats
        {
            TotalEvents = events.Length,
            VerificationSuccessCount = events.Count(e => e.EventType == CapabilityAuditEventType.VerificationSuccess),
            VerificationFailureCount = events.Count(e => e.EventType == CapabilityAuditEventType.VerificationFailure),
            TokenCreatedCount = events.Count(e => e.EventType == CapabilityAuditEventType.TokenCreated),
            TokenRevokedCount = events.Count(e => e.EventType == CapabilityAuditEventType.TokenRevoked),
            UniqueTokens = events.Select(e => e.TokenId).Where(t => t != null).Distinct().Count(),
            UniqueActors = events.Select(e => e.ActorId).Where(a => a != null).Distinct().Count(),
            OldestEvent = events.Length > 0 ? events.Min(e => e.Timestamp) : null,
            NewestEvent = events.Length > 0 ? events.Max(e => e.Timestamp) : null
        };
    }

    /// <summary>
    /// Exports audit trail for external verification.
    /// </summary>
    public async Task<byte[]> ExportAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var events = Query(from, to).ToList();
        var json = JsonSerializer.SerializeToUtf8Bytes(events);
        return await Task.FromResult(json);
    }

    /// <summary>
    /// Verifies audit trail integrity.
    /// </summary>
    public VerifyAuditResult VerifyIntegrity()
    {
        var events = _events.ToArray();
        if (events.Length == 0)
            return new VerifyAuditResult { IsValid = true };

        byte[] prevHash = new byte[32];
        for (int i = 0; i < events.Length; i++)
        {
            var expectedHash = ComputeEventHash(events[i], prevHash);
            if (!events[i].Hash.SequenceEqual(expectedHash))
            {
                return new VerifyAuditResult
                {
                    IsValid = false,
                    ErrorMessage = $"Hash mismatch at sequence {events[i].SequenceNumber}",
                    FailedAtSequence = events[i].SequenceNumber
                };
            }
            prevHash = events[i].Hash;
        }

        return new VerifyAuditResult { IsValid = true };
    }

    private static byte[] ComputeEventHash(CapabilityAuditEvent evt, byte[] prevHash)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(evt.SequenceNumber);
        writer.Write(evt.Timestamp.ToBinary());
        writer.Write((int)evt.EventType);
        writer.Write(evt.TokenId ?? "");
        writer.Write(evt.ActorId ?? "");
        writer.Write(evt.Details ?? "");
        writer.Write(prevHash);

        return SHA256.HashData(ms.ToArray());
    }

    private async Task PersistEventAsync(CapabilityAuditEvent evt, CancellationToken ct)
    {
        var logFile = Path.Combine(_persistPath!, $"capability_audit_{evt.Timestamp:yyyyMMdd}.jsonl");
        var json = JsonSerializer.Serialize(evt) + Environment.NewLine;
        await File.AppendAllTextAsync(logFile, json, ct);
    }
}

/// <summary>
/// Audit event types.
/// </summary>
public enum CapabilityAuditEventType
{
    TokenCreated,
    TokenDelegated,
    TokenRevoked,
    TokenRefreshed,
    TokenExpired,
    VerificationSuccess,
    VerificationFailure,
    UsageRecorded,
    ChainValidated
}

/// <summary>
/// Audit event record.
/// </summary>
public sealed class CapabilityAuditEvent
{
    public long SequenceNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public CapabilityAuditEventType EventType { get; init; }
    public string? TokenId { get; init; }
    public string? TargetId { get; init; }
    public string? ActorId { get; init; }
    public string? RequestIp { get; init; }
    public string? Details { get; init; }
    public TimeSpan Duration { get; init; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Audit statistics.
/// </summary>
public sealed class CapabilityAuditStats
{
    public int TotalEvents { get; init; }
    public int VerificationSuccessCount { get; init; }
    public int VerificationFailureCount { get; init; }
    public int TokenCreatedCount { get; init; }
    public int TokenRevokedCount { get; init; }
    public int UniqueTokens { get; init; }
    public int UniqueActors { get; init; }
    public DateTime? OldestEvent { get; init; }
    public DateTime? NewestEvent { get; init; }
    public double VerificationSuccessRate =>
        VerificationSuccessCount + VerificationFailureCount > 0
            ? (double)VerificationSuccessCount / (VerificationSuccessCount + VerificationFailureCount)
            : 0;
}

/// <summary>
/// Result of audit verification.
/// </summary>
public sealed class VerifyAuditResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public long? FailedAtSequence { get; init; }
}

#endregion
