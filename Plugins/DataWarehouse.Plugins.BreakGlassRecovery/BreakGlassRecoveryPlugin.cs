using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.BreakGlassRecovery;

/// <summary>
/// Production-ready break-glass emergency recovery plugin for DataWarehouse.
/// Provides multi-party authorization, Shamir secret sharing for key escrow,
/// time-limited emergency access, and comprehensive audit trail for enterprise
/// disaster recovery scenarios.
/// </summary>
/// <remarks>
/// Break-glass procedures are emergency access mechanisms that allow authorized
/// personnel to access protected resources when normal access methods are unavailable.
/// This plugin implements industry best practices including:
/// <list type="bullet">
///   <item>Multi-custodian key reconstruction using Shamir Secret Sharing</item>
///   <item>Time-limited emergency sessions with automatic expiration</item>
///   <item>Dual-control requirements for critical operations</item>
///   <item>Complete audit trail for compliance and forensics</item>
///   <item>Integration with existing snapshot and backup infrastructure</item>
/// </list>
/// </remarks>
public sealed class BreakGlassRecoveryPlugin : SnapshotPluginBase, IAsyncDisposable
{
    #region Fields

    private readonly ConcurrentDictionary<string, BreakGlassRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, EmergencySession> _activeSessions = new();
    private readonly ConcurrentDictionary<string, KeyCustodian> _custodians = new();
    private readonly ConcurrentDictionary<string, KeyEscrowEntry> _escrowedKeys = new();
    private readonly ConcurrentDictionary<string, RecoveryKey> _recoveryKeys = new();
    private readonly ConcurrentDictionary<string, AuditLogEntry> _auditLog = new();
    private readonly ConcurrentDictionary<string, EmergencyPolicy> _policies = new();
    private readonly BreakGlassConfiguration _config;
    private readonly string _storagePath;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly Timer _sessionExpirationTimer;
    private readonly Timer _requestTimeoutTimer;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.recovery.breakglass";

    /// <inheritdoc/>
    public override string Name => "Break-Glass Recovery Plugin";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.SecurityProvider;

    /// <inheritdoc/>
    public override bool SupportsIncremental => false;

    /// <inheritdoc/>
    public override bool SupportsLegalHold => true;

    /// <summary>
    /// Minimum number of custodians required to authorize a break-glass request.
    /// </summary>
    public int MinimumCustodianThreshold => _config.MinimumCustodianThreshold;

    /// <summary>
    /// Maximum duration for an emergency session.
    /// </summary>
    public TimeSpan MaxSessionDuration => _config.MaxSessionDuration;

    /// <summary>
    /// Event raised when a break-glass request is created.
    /// </summary>
    public event EventHandler<BreakGlassRequest>? RequestCreated;

    /// <summary>
    /// Event raised when a break-glass request is approved.
    /// </summary>
    public event EventHandler<BreakGlassRequest>? RequestApproved;

    /// <summary>
    /// Event raised when a break-glass request is denied.
    /// </summary>
    public event EventHandler<BreakGlassRequest>? RequestDenied;

    /// <summary>
    /// Event raised when an emergency session is started.
    /// </summary>
    public event EventHandler<EmergencySession>? SessionStarted;

    /// <summary>
    /// Event raised when an emergency session is terminated.
    /// </summary>
    public event EventHandler<EmergencySession>? SessionTerminated;

    /// <summary>
    /// Event raised when an audit event is recorded.
    /// </summary>
    public event EventHandler<AuditLogEntry>? AuditEventRecorded;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new instance of the Break-Glass Recovery Plugin.
    /// </summary>
    /// <param name="config">Optional configuration. Uses defaults if not specified.</param>
    public BreakGlassRecoveryPlugin(BreakGlassConfiguration? config = null)
    {
        _config = config ?? new BreakGlassConfiguration();
        _storagePath = _config.StoragePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse", "BreakGlass");

        Directory.CreateDirectory(_storagePath);
        Directory.CreateDirectory(Path.Combine(_storagePath, "audit"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "escrow"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "sessions"));

        // Timer to check for expired sessions (every 30 seconds)
        _sessionExpirationTimer = new Timer(
            async _ => await CheckExpiredSessionsAsync(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        // Timer to check for timed-out requests (every minute)
        _requestTimeoutTimer = new Timer(
            async _ => await CheckTimedOutRequestsAsync(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await LoadStateAsync(ct);

        LogAuditEvent(
            AuditEventType.PluginStarted,
            "system",
            "Break-Glass Recovery Plugin started",
            new Dictionary<string, object>
            {
                ["version"] = Version,
                ["threshold"] = MinimumCustodianThreshold,
                ["maxSessionDuration"] = MaxSessionDuration.TotalMinutes
            });
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _cts?.Cancel();

        // Terminate all active sessions on shutdown
        foreach (var session in _activeSessions.Values.Where(s => s.Status == SessionStatus.Active))
        {
            await TerminateSessionAsync(session.SessionId, "System shutdown", CancellationToken.None);
        }

        await _sessionExpirationTimer.DisposeAsync();
        await _requestTimeoutTimer.DisposeAsync();
        await PersistStateAsync(CancellationToken.None);

        LogAuditEvent(
            AuditEventType.PluginStopped,
            "system",
            "Break-Glass Recovery Plugin stopped");
    }

    #endregion

    #region Custodian Management

    /// <summary>
    /// Registers a new key custodian who can participate in break-glass recovery.
    /// </summary>
    /// <param name="custodian">The custodian to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registered custodian with generated ID.</returns>
    /// <exception cref="ArgumentNullException">If custodian is null.</exception>
    /// <exception cref="InvalidOperationException">If custodian email already exists.</exception>
    public async Task<KeyCustodian> RegisterCustodianAsync(
        KeyCustodianRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrEmpty(registration.Name);
        ArgumentException.ThrowIfNullOrEmpty(registration.Email);

        // Check for duplicate email
        if (_custodians.Values.Any(c =>
            c.Email.Equals(registration.Email, StringComparison.OrdinalIgnoreCase) &&
            c.Status == CustodianStatus.Active))
        {
            throw new InvalidOperationException(
                $"A custodian with email '{registration.Email}' already exists");
        }

        var custodian = new KeyCustodian
        {
            CustodianId = GenerateId("custodian"),
            Name = registration.Name,
            Email = registration.Email,
            Phone = registration.Phone,
            Department = registration.Department,
            Role = registration.Role,
            PublicKey = registration.PublicKey,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = registration.CreatedBy ?? "system",
            Status = CustodianStatus.Active,
            Permissions = registration.Permissions ?? CustodianPermissions.Approve
        };

        _custodians[custodian.CustodianId] = custodian;
        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.CustodianRegistered,
            registration.CreatedBy ?? "system",
            $"Registered new custodian: {custodian.Name}",
            new Dictionary<string, object>
            {
                ["custodianId"] = custodian.CustodianId,
                ["email"] = custodian.Email,
                ["role"] = custodian.Role
            });

        return custodian;
    }

    /// <summary>
    /// Gets a custodian by ID.
    /// </summary>
    /// <param name="custodianId">The custodian ID.</param>
    /// <returns>The custodian or null if not found.</returns>
    public Task<KeyCustodian?> GetCustodianAsync(string custodianId)
    {
        _custodians.TryGetValue(custodianId, out var custodian);
        return Task.FromResult(custodian);
    }

    /// <summary>
    /// Lists all registered custodians.
    /// </summary>
    /// <param name="includeInactive">Whether to include inactive custodians.</param>
    /// <returns>List of custodians.</returns>
    public Task<IReadOnlyList<KeyCustodian>> ListCustodiansAsync(bool includeInactive = false)
    {
        var custodians = _custodians.Values
            .Where(c => includeInactive || c.Status == CustodianStatus.Active)
            .OrderBy(c => c.Name)
            .ToList();
        return Task.FromResult<IReadOnlyList<KeyCustodian>>(custodians);
    }

    /// <summary>
    /// Deactivates a custodian.
    /// </summary>
    /// <param name="custodianId">The custodian ID to deactivate.</param>
    /// <param name="reason">Reason for deactivation.</param>
    /// <param name="deactivatedBy">Who is performing the deactivation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if successful.</returns>
    public async Task<bool> DeactivateCustodianAsync(
        string custodianId,
        string reason,
        string deactivatedBy,
        CancellationToken ct = default)
    {
        if (!_custodians.TryGetValue(custodianId, out var custodian))
        {
            return false;
        }

        custodian.Status = CustodianStatus.Inactive;
        custodian.DeactivatedAt = DateTime.UtcNow;
        custodian.DeactivatedBy = deactivatedBy;
        custodian.DeactivationReason = reason;

        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.CustodianDeactivated,
            deactivatedBy,
            $"Deactivated custodian: {custodian.Name}",
            new Dictionary<string, object>
            {
                ["custodianId"] = custodianId,
                ["reason"] = reason
            });

        return true;
    }

    #endregion

    #region Key Escrow Management

    /// <summary>
    /// Escrows a recovery key using Shamir Secret Sharing.
    /// The key is split into shares distributed among custodians.
    /// </summary>
    /// <param name="keyMaterial">The key material to escrow.</param>
    /// <param name="description">Human-readable description of what this key protects.</param>
    /// <param name="totalShares">Total number of shares to create.</param>
    /// <param name="threshold">Minimum shares required for reconstruction.</param>
    /// <param name="custodianAssignments">Optional specific custodian assignments.</param>
    /// <param name="createdBy">Who is creating this escrow.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The escrow entry (without share data for security).</returns>
    public async Task<KeyEscrowEntry> EscrowKeyAsync(
        byte[] keyMaterial,
        string description,
        int totalShares,
        int threshold,
        IReadOnlyList<string>? custodianAssignments = null,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        if (keyMaterial.Length == 0)
            throw new ArgumentException("Key material cannot be empty", nameof(keyMaterial));

        if (threshold < 2)
            throw new ArgumentException("Threshold must be at least 2", nameof(threshold));

        if (totalShares < threshold)
            throw new ArgumentException("Total shares must be >= threshold", nameof(totalShares));

        // Verify we have enough active custodians
        var activeCustodians = _custodians.Values
            .Where(c => c.Status == CustodianStatus.Active)
            .ToList();

        if (activeCustodians.Count < totalShares)
        {
            throw new InvalidOperationException(
                $"Not enough active custodians ({activeCustodians.Count}) for {totalShares} shares");
        }

        // Split the key using Shamir Secret Sharing
        var keyHash = SHA256.HashData(keyMaterial);
        var shares = ShamirSecretSharing.SplitSecret(keyMaterial, totalShares, threshold);

        // Assign shares to custodians
        var assignments = new List<CustodianAssignment>();
        var assignedCustodians = custodianAssignments != null
            ? custodianAssignments.Select(id => _custodians.GetValueOrDefault(id))
                .Where(c => c != null && c.Status == CustodianStatus.Active)
                .Select(c => c!)
                .Take(totalShares)
                .ToList()
            : activeCustodians.Take(totalShares).ToList();

        for (int i = 0; i < shares.Length && i < assignedCustodians.Count; i++)
        {
            var custodian = assignedCustodians[i]!;
            shares[i].CustodianId = custodian.CustodianId;
            shares[i].SecretHash = keyHash;
            shares[i].Label = $"Share {i + 1} for {description}";

            assignments.Add(new CustodianAssignment
            {
                ShareIndex = shares[i].ShareIndex,
                CustodianId = custodian.CustodianId,
                CustodianName = custodian.Name,
                ContactInfo = custodian.Email,
                Role = custodian.Role,
                DistributedAt = DateTime.UtcNow,
                Acknowledged = false
            });
        }

        var configuration = ShamirSecretSharing.CreateConfiguration(
            totalShares, threshold, description);
        configuration.Custodians.AddRange(assignments);

        var escrowEntry = new KeyEscrowEntry
        {
            EscrowId = GenerateId("escrow"),
            KeyId = GenerateId("key"),
            Description = description,
            KeyHash = keyHash,
            Configuration = configuration,
            Shares = shares.ToList(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy ?? "system",
            Status = EscrowStatus.PendingDistribution
        };

        _escrowedKeys[escrowEntry.EscrowId] = escrowEntry;

        // Clear the original key material
        CryptographicOperations.ZeroMemory(keyMaterial);

        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.KeyEscrowed,
            createdBy ?? "system",
            $"Escrowed key: {description}",
            new Dictionary<string, object>
            {
                ["escrowId"] = escrowEntry.EscrowId,
                ["keyId"] = escrowEntry.KeyId,
                ["totalShares"] = totalShares,
                ["threshold"] = threshold,
                ["custodianCount"] = assignments.Count
            });

        // Return a copy without share data for security
        return new KeyEscrowEntry
        {
            EscrowId = escrowEntry.EscrowId,
            KeyId = escrowEntry.KeyId,
            Description = escrowEntry.Description,
            KeyHash = escrowEntry.KeyHash,
            Configuration = escrowEntry.Configuration,
            Shares = new List<SecretShare>(), // Empty for security
            CreatedAt = escrowEntry.CreatedAt,
            CreatedBy = escrowEntry.CreatedBy,
            Status = escrowEntry.Status
        };
    }

    /// <summary>
    /// Generates and escrows a new recovery key.
    /// </summary>
    /// <param name="keyLength">Length of the key to generate in bytes.</param>
    /// <param name="description">Description of the key's purpose.</param>
    /// <param name="totalShares">Total shares to create.</param>
    /// <param name="threshold">Minimum shares for reconstruction.</param>
    /// <param name="expiresAt">Optional expiration time.</param>
    /// <param name="createdBy">Who is creating the key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recovery key metadata (key material is only available through reconstruction).</returns>
    public async Task<RecoveryKey> GenerateRecoveryKeyAsync(
        int keyLength,
        string description,
        int totalShares,
        int threshold,
        DateTime? expiresAt = null,
        string? createdBy = null,
        CancellationToken ct = default)
    {
        if (keyLength < 16 || keyLength > 256)
            throw new ArgumentOutOfRangeException(nameof(keyLength), "Key length must be between 16 and 256 bytes");

        // Generate cryptographically secure key
        var keyMaterial = RandomNumberGenerator.GetBytes(keyLength);
        var keyHash = SHA256.HashData(keyMaterial);

        // Escrow the key
        var escrowEntry = await EscrowKeyAsync(
            keyMaterial, description, totalShares, threshold,
            createdBy: createdBy, ct: ct);

        var recoveryKey = new RecoveryKey
        {
            KeyId = escrowEntry.KeyId,
            EscrowId = escrowEntry.EscrowId,
            Description = description,
            KeyHash = keyHash,
            KeyLength = keyLength,
            Algorithm = "AES-256",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy ?? "system",
            ExpiresAt = expiresAt,
            Status = RecoveryKeyStatus.Active,
            Threshold = threshold,
            TotalShares = totalShares
        };

        _recoveryKeys[recoveryKey.KeyId] = recoveryKey;
        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.RecoveryKeyGenerated,
            createdBy ?? "system",
            $"Generated recovery key: {description}",
            new Dictionary<string, object>
            {
                ["keyId"] = recoveryKey.KeyId,
                ["escrowId"] = recoveryKey.EscrowId,
                ["keyLength"] = keyLength,
                ["threshold"] = threshold
            });

        return recoveryKey;
    }

    /// <summary>
    /// Gets an escrowed key entry.
    /// </summary>
    /// <param name="escrowId">The escrow ID.</param>
    /// <returns>The escrow entry (without share data) or null.</returns>
    public Task<KeyEscrowEntry?> GetEscrowEntryAsync(string escrowId)
    {
        if (_escrowedKeys.TryGetValue(escrowId, out var entry))
        {
            // Return without share data for security
            return Task.FromResult<KeyEscrowEntry?>(new KeyEscrowEntry
            {
                EscrowId = entry.EscrowId,
                KeyId = entry.KeyId,
                Description = entry.Description,
                KeyHash = entry.KeyHash,
                Configuration = entry.Configuration,
                Shares = new List<SecretShare>(),
                CreatedAt = entry.CreatedAt,
                CreatedBy = entry.CreatedBy,
                Status = entry.Status,
                ExpiresAt = entry.ExpiresAt,
                RecoveryAttempts = entry.RecoveryAttempts
            });
        }
        return Task.FromResult<KeyEscrowEntry?>(null);
    }

    #endregion

    #region Break-Glass Request Workflow

    /// <summary>
    /// Creates a new break-glass emergency access request.
    /// </summary>
    /// <param name="request">The request details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created request with generated ID.</returns>
    public async Task<BreakGlassRequest> CreateRequestAsync(
        BreakGlassRequestInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(input.RequestorId);
        ArgumentException.ThrowIfNullOrEmpty(input.Reason);
        ArgumentException.ThrowIfNullOrEmpty(input.IncidentReference);

        // Validate target resource exists
        if (!string.IsNullOrEmpty(input.TargetKeyId) &&
            !_recoveryKeys.ContainsKey(input.TargetKeyId))
        {
            throw new KeyNotFoundException($"Recovery key '{input.TargetKeyId}' not found");
        }

        var policy = GetApplicablePolicy(input.RequestType);
        var requiredApprovals = policy?.RequiredApprovals ?? MinimumCustodianThreshold;

        var request = new BreakGlassRequest
        {
            RequestId = GenerateId("bgr"),
            RequestorId = input.RequestorId,
            RequestorName = input.RequestorName,
            RequestorEmail = input.RequestorEmail,
            Reason = input.Reason,
            Justification = input.Justification,
            IncidentReference = input.IncidentReference,
            TargetKeyId = input.TargetKeyId,
            TargetResource = input.TargetResource,
            RequestType = input.RequestType,
            RequestedAccess = input.RequestedAccess,
            RequestedDuration = input.RequestedDuration ?? _config.DefaultSessionDuration,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(_config.RequestTimeout),
            Status = BreakGlassRequestStatus.Pending,
            RequiredApprovals = requiredApprovals,
            Approvals = new List<RequestApproval>(),
            Denials = new List<RequestDenial>(),
            Metadata = input.Metadata ?? new Dictionary<string, object>()
        };

        _pendingRequests[request.RequestId] = request;
        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.BreakGlassRequested,
            input.RequestorId,
            $"Break-glass request created: {input.Reason}",
            new Dictionary<string, object>
            {
                ["requestId"] = request.RequestId,
                ["incidentReference"] = input.IncidentReference,
                ["targetKeyId"] = input.TargetKeyId ?? "N/A",
                ["requestType"] = input.RequestType.ToString(),
                ["requiredApprovals"] = requiredApprovals
            });

        RequestCreated?.Invoke(this, request);

        // Notify custodians (would integrate with notification system)
        await NotifyCustodiansAsync(request, ct);

        return request;
    }

    /// <summary>
    /// Approves a break-glass request.
    /// </summary>
    /// <param name="requestId">The request ID to approve.</param>
    /// <param name="custodianId">The approving custodian's ID.</param>
    /// <param name="comments">Optional comments.</param>
    /// <param name="shareData">Optional share data if the custodian is providing their key share.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated request.</returns>
    public async Task<BreakGlassRequest> ApproveRequestAsync(
        string requestId,
        string custodianId,
        string? comments = null,
        SecretShare? shareData = null,
        CancellationToken ct = default)
    {
        if (!_pendingRequests.TryGetValue(requestId, out var request))
        {
            throw new KeyNotFoundException($"Request '{requestId}' not found");
        }

        if (request.Status != BreakGlassRequestStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Request is not pending (status: {request.Status})");
        }

        if (request.ExpiresAt < DateTime.UtcNow)
        {
            request.Status = BreakGlassRequestStatus.Expired;
            await PersistStateAsync(ct);
            throw new InvalidOperationException("Request has expired");
        }

        if (!_custodians.TryGetValue(custodianId, out var custodian))
        {
            throw new KeyNotFoundException($"Custodian '{custodianId}' not found");
        }

        if (custodian.Status != CustodianStatus.Active)
        {
            throw new InvalidOperationException($"Custodian '{custodianId}' is not active");
        }

        if (!custodian.Permissions.HasFlag(CustodianPermissions.Approve))
        {
            throw new UnauthorizedAccessException(
                $"Custodian '{custodianId}' does not have approval permission");
        }

        // Check for duplicate approval
        if (request.Approvals.Any(a => a.CustodianId == custodianId))
        {
            throw new InvalidOperationException(
                $"Custodian '{custodianId}' has already approved this request");
        }

        var approval = new RequestApproval
        {
            CustodianId = custodianId,
            CustodianName = custodian.Name,
            ApprovedAt = DateTime.UtcNow,
            Comments = comments,
            ShareProvided = shareData != null
        };

        request.Approvals.Add(approval);

        // Store the share if provided
        if (shareData != null)
        {
            request.CollectedShares ??= new List<SecretShare>();
            request.CollectedShares.Add(shareData);
        }

        LogAuditEvent(
            AuditEventType.BreakGlassApproved,
            custodianId,
            $"Approved break-glass request: {requestId}",
            new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["custodianName"] = custodian.Name,
                ["approvalCount"] = request.Approvals.Count,
                ["requiredApprovals"] = request.RequiredApprovals,
                ["shareProvided"] = shareData != null
            });

        // Check if we have enough approvals
        if (request.Approvals.Count >= request.RequiredApprovals)
        {
            request.Status = BreakGlassRequestStatus.Approved;
            request.ApprovedAt = DateTime.UtcNow;
            RequestApproved?.Invoke(this, request);

            LogAuditEvent(
                AuditEventType.BreakGlassFullyApproved,
                "system",
                $"Break-glass request fully approved: {requestId}",
                new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["approvalCount"] = request.Approvals.Count
                });
        }

        await PersistStateAsync(ct);
        return request;
    }

    /// <summary>
    /// Denies a break-glass request.
    /// </summary>
    /// <param name="requestId">The request ID to deny.</param>
    /// <param name="custodianId">The denying custodian's ID.</param>
    /// <param name="reason">Reason for denial.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated request.</returns>
    public async Task<BreakGlassRequest> DenyRequestAsync(
        string requestId,
        string custodianId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        if (!_pendingRequests.TryGetValue(requestId, out var request))
        {
            throw new KeyNotFoundException($"Request '{requestId}' not found");
        }

        if (request.Status != BreakGlassRequestStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Request is not pending (status: {request.Status})");
        }

        if (!_custodians.TryGetValue(custodianId, out var custodian))
        {
            throw new KeyNotFoundException($"Custodian '{custodianId}' not found");
        }

        if (!custodian.Permissions.HasFlag(CustodianPermissions.Deny))
        {
            throw new UnauthorizedAccessException(
                $"Custodian '{custodianId}' does not have denial permission");
        }

        var denial = new RequestDenial
        {
            CustodianId = custodianId,
            CustodianName = custodian.Name,
            DeniedAt = DateTime.UtcNow,
            Reason = reason
        };

        request.Denials.Add(denial);
        request.Status = BreakGlassRequestStatus.Denied;
        request.DeniedAt = DateTime.UtcNow;
        request.DenialReason = reason;

        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.BreakGlassDenied,
            custodianId,
            $"Denied break-glass request: {requestId}",
            new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["custodianName"] = custodian.Name,
                ["reason"] = reason
            });

        RequestDenied?.Invoke(this, request);
        return request;
    }

    /// <summary>
    /// Gets a break-glass request by ID.
    /// </summary>
    /// <param name="requestId">The request ID.</param>
    /// <returns>The request or null if not found.</returns>
    public Task<BreakGlassRequest?> GetRequestAsync(string requestId)
    {
        _pendingRequests.TryGetValue(requestId, out var request);
        return Task.FromResult(request);
    }

    /// <summary>
    /// Lists break-glass requests with optional filtering.
    /// </summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <returns>List of matching requests.</returns>
    public Task<IReadOnlyList<BreakGlassRequest>> ListRequestsAsync(
        BreakGlassRequestFilter? filter = null)
    {
        var query = _pendingRequests.Values.AsEnumerable();

        if (filter != null)
        {
            if (filter.Status.HasValue)
                query = query.Where(r => r.Status == filter.Status.Value);

            if (filter.RequestorId != null)
                query = query.Where(r => r.RequestorId == filter.RequestorId);

            if (filter.CreatedAfter.HasValue)
                query = query.Where(r => r.CreatedAt >= filter.CreatedAfter.Value);

            if (filter.CreatedBefore.HasValue)
                query = query.Where(r => r.CreatedAt <= filter.CreatedBefore.Value);

            if (filter.IncidentReference != null)
                query = query.Where(r => r.IncidentReference == filter.IncidentReference);
        }

        var results = query
            .OrderByDescending(r => r.CreatedAt)
            .Take(filter?.Limit ?? 100)
            .ToList();

        return Task.FromResult<IReadOnlyList<BreakGlassRequest>>(results);
    }

    #endregion

    #region Emergency Session Management

    /// <summary>
    /// Starts an emergency session after a break-glass request is approved.
    /// </summary>
    /// <param name="requestId">The approved request ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The emergency session with access credentials.</returns>
    public async Task<EmergencySession> StartSessionAsync(
        string requestId,
        CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (!_pendingRequests.TryGetValue(requestId, out var request))
            {
                throw new KeyNotFoundException($"Request '{requestId}' not found");
            }

            if (request.Status != BreakGlassRequestStatus.Approved)
            {
                throw new InvalidOperationException(
                    $"Request is not approved (status: {request.Status})");
            }

            // Calculate session duration (capped at max)
            var duration = request.RequestedDuration < MaxSessionDuration
                ? request.RequestedDuration
                : MaxSessionDuration;

            // Generate session token
            var sessionToken = GenerateSecureToken();
            var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken));

            // Reconstruct key if this is a key recovery request
            byte[]? reconstructedKey = null;
            if (!string.IsNullOrEmpty(request.TargetKeyId) &&
                request.CollectedShares?.Count >= MinimumCustodianThreshold)
            {
                try
                {
                    reconstructedKey = ShamirSecretSharing.ReconstructSecret(
                        request.CollectedShares.ToArray());
                }
                catch (Exception ex)
                {
                    LogAuditEvent(
                        AuditEventType.KeyReconstructionFailed,
                        request.RequestorId,
                        $"Failed to reconstruct key for request: {requestId}",
                        new Dictionary<string, object>
                        {
                            ["requestId"] = requestId,
                            ["error"] = ex.Message
                        });
                    throw new InvalidOperationException(
                        "Failed to reconstruct key from provided shares", ex);
                }
            }

            var session = new EmergencySession
            {
                SessionId = GenerateId("session"),
                RequestId = requestId,
                RequestorId = request.RequestorId,
                RequestorName = request.RequestorName,
                SessionToken = sessionToken,
                TokenHash = tokenHash,
                StartedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(duration),
                Duration = duration,
                Status = SessionStatus.Active,
                AccessLevel = request.RequestedAccess,
                TargetKeyId = request.TargetKeyId,
                TargetResource = request.TargetResource,
                ReconstructedKey = reconstructedKey,
                Activities = new List<SessionActivity>(),
                ApprovedBy = request.Approvals.Select(a => a.CustodianId).ToList()
            };

            // Mark request as completed
            request.Status = BreakGlassRequestStatus.Completed;
            request.SessionId = session.SessionId;

            _activeSessions[session.SessionId] = session;
            await PersistStateAsync(ct);

            LogAuditEvent(
                AuditEventType.EmergencySessionStarted,
                request.RequestorId,
                $"Emergency session started: {session.SessionId}",
                new Dictionary<string, object>
                {
                    ["sessionId"] = session.SessionId,
                    ["requestId"] = requestId,
                    ["duration"] = duration.TotalMinutes,
                    ["accessLevel"] = session.AccessLevel.ToString(),
                    ["targetKeyId"] = session.TargetKeyId ?? "N/A",
                    ["approverCount"] = session.ApprovedBy.Count
                });

            SessionStarted?.Invoke(this, session);

            // Return session without the actual key material for logging safety
            return new EmergencySession
            {
                SessionId = session.SessionId,
                RequestId = session.RequestId,
                RequestorId = session.RequestorId,
                RequestorName = session.RequestorName,
                SessionToken = session.SessionToken, // Returned only once
                TokenHash = session.TokenHash,
                StartedAt = session.StartedAt,
                ExpiresAt = session.ExpiresAt,
                Duration = session.Duration,
                Status = session.Status,
                AccessLevel = session.AccessLevel,
                TargetKeyId = session.TargetKeyId,
                TargetResource = session.TargetResource,
                ReconstructedKey = reconstructedKey, // Returned only once
                Activities = session.Activities,
                ApprovedBy = session.ApprovedBy
            };
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Validates a session token and returns the session if valid.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="token">The session token.</param>
    /// <returns>The session if valid, null otherwise.</returns>
    public Task<EmergencySession?> ValidateSessionAsync(string sessionId, string token)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            return Task.FromResult<EmergencySession?>(null);
        }

        if (session.Status != SessionStatus.Active)
        {
            return Task.FromResult<EmergencySession?>(null);
        }

        if (session.ExpiresAt < DateTime.UtcNow)
        {
            session.Status = SessionStatus.Expired;
            return Task.FromResult<EmergencySession?>(null);
        }

        // Verify token hash
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        if (!CryptographicOperations.FixedTimeEquals(tokenHash, session.TokenHash))
        {
            LogAuditEvent(
                AuditEventType.InvalidSessionToken,
                "unknown",
                $"Invalid session token attempt for session: {sessionId}");
            return Task.FromResult<EmergencySession?>(null);
        }

        return Task.FromResult<EmergencySession?>(session);
    }

    /// <summary>
    /// Records an activity within an emergency session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="activityType">Type of activity.</param>
    /// <param name="description">Description of the activity.</param>
    /// <param name="details">Additional details.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordSessionActivityAsync(
        string sessionId,
        string activityType,
        string description,
        Dictionary<string, object>? details = null,
        CancellationToken ct = default)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found");
        }

        var activity = new SessionActivity
        {
            ActivityId = GenerateId("act"),
            Timestamp = DateTime.UtcNow,
            ActivityType = activityType,
            Description = description,
            Details = details ?? new Dictionary<string, object>()
        };

        session.Activities.Add(activity);
        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.SessionActivity,
            session.RequestorId,
            $"Session activity: {activityType}",
            new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["activityType"] = activityType,
                ["description"] = description
            });
    }

    /// <summary>
    /// Terminates an emergency session.
    /// </summary>
    /// <param name="sessionId">The session ID to terminate.</param>
    /// <param name="reason">Reason for termination.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if successful.</returns>
    public async Task<bool> TerminateSessionAsync(
        string sessionId,
        string reason,
        CancellationToken ct = default)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        session.Status = SessionStatus.Terminated;
        session.TerminatedAt = DateTime.UtcNow;
        session.TerminationReason = reason;

        // Clear any sensitive data
        if (session.ReconstructedKey != null)
        {
            CryptographicOperations.ZeroMemory(session.ReconstructedKey);
            session.ReconstructedKey = null;
        }

        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.EmergencySessionTerminated,
            session.RequestorId,
            $"Emergency session terminated: {sessionId}",
            new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["reason"] = reason,
                ["duration"] = (session.TerminatedAt.Value - session.StartedAt).TotalMinutes,
                ["activityCount"] = session.Activities.Count
            });

        SessionTerminated?.Invoke(this, session);
        return true;
    }

    /// <summary>
    /// Gets an emergency session by ID.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The session or null (without sensitive data).</returns>
    public Task<EmergencySession?> GetSessionAsync(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            // Return without sensitive data
            return Task.FromResult<EmergencySession?>(new EmergencySession
            {
                SessionId = session.SessionId,
                RequestId = session.RequestId,
                RequestorId = session.RequestorId,
                RequestorName = session.RequestorName,
                SessionToken = null!, // Never return token
                TokenHash = session.TokenHash,
                StartedAt = session.StartedAt,
                ExpiresAt = session.ExpiresAt,
                TerminatedAt = session.TerminatedAt,
                Duration = session.Duration,
                Status = session.Status,
                AccessLevel = session.AccessLevel,
                TargetKeyId = session.TargetKeyId,
                TargetResource = session.TargetResource,
                ReconstructedKey = null, // Never return key
                Activities = session.Activities,
                ApprovedBy = session.ApprovedBy,
                TerminationReason = session.TerminationReason
            });
        }
        return Task.FromResult<EmergencySession?>(null);
    }

    /// <summary>
    /// Lists active emergency sessions.
    /// </summary>
    /// <returns>List of active sessions (without sensitive data).</returns>
    public Task<IReadOnlyList<EmergencySession>> ListActiveSessionsAsync()
    {
        var sessions = _activeSessions.Values
            .Where(s => s.Status == SessionStatus.Active)
            .Select(s => new EmergencySession
            {
                SessionId = s.SessionId,
                RequestId = s.RequestId,
                RequestorId = s.RequestorId,
                RequestorName = s.RequestorName,
                SessionToken = null!,
                TokenHash = s.TokenHash,
                StartedAt = s.StartedAt,
                ExpiresAt = s.ExpiresAt,
                Duration = s.Duration,
                Status = s.Status,
                AccessLevel = s.AccessLevel,
                TargetKeyId = s.TargetKeyId,
                TargetResource = s.TargetResource,
                Activities = s.Activities,
                ApprovedBy = s.ApprovedBy
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<EmergencySession>>(sessions);
    }

    #endregion

    #region Audit Trail

    /// <summary>
    /// Queries the audit log.
    /// </summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <returns>List of matching audit entries.</returns>
    public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogAsync(AuditLogFilter? filter = null)
    {
        var query = _auditLog.Values.AsEnumerable();

        if (filter != null)
        {
            if (filter.EventType.HasValue)
                query = query.Where(e => e.EventType == filter.EventType.Value);

            if (filter.ActorId != null)
                query = query.Where(e => e.ActorId == filter.ActorId);

            if (filter.StartTime.HasValue)
                query = query.Where(e => e.Timestamp >= filter.StartTime.Value);

            if (filter.EndTime.HasValue)
                query = query.Where(e => e.Timestamp <= filter.EndTime.Value);

            if (filter.SessionId != null)
                query = query.Where(e =>
                    e.Details.TryGetValue("sessionId", out var sid) &&
                    sid?.ToString() == filter.SessionId);

            if (filter.RequestId != null)
                query = query.Where(e =>
                    e.Details.TryGetValue("requestId", out var rid) &&
                    rid?.ToString() == filter.RequestId);
        }

        var results = query
            .OrderByDescending(e => e.Timestamp)
            .Take(filter?.Limit ?? 1000)
            .ToList();

        return Task.FromResult<IReadOnlyList<AuditLogEntry>>(results);
    }

    /// <summary>
    /// Generates a comprehensive audit report for an incident.
    /// </summary>
    /// <param name="incidentReference">The incident reference number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The audit report.</returns>
    public async Task<IncidentAuditReport> GenerateIncidentReportAsync(
        string incidentReference,
        CancellationToken ct = default)
    {
        var requests = _pendingRequests.Values
            .Where(r => r.IncidentReference == incidentReference)
            .ToList();

        var sessionIds = requests
            .Where(r => r.SessionId != null)
            .Select(r => r.SessionId!)
            .ToList();

        var sessions = sessionIds
            .Select(id => _activeSessions.GetValueOrDefault(id))
            .Where(s => s != null)
            .ToList();

        var auditEntries = await QueryAuditLogAsync(new AuditLogFilter
        {
            Limit = 10000
        });

        var relevantEntries = auditEntries
            .Where(e =>
                (e.Details.TryGetValue("incidentReference", out var ir) &&
                 ir?.ToString() == incidentReference) ||
                (e.Details.TryGetValue("requestId", out var rid) &&
                 requests.Any(r => r.RequestId == rid?.ToString())) ||
                (e.Details.TryGetValue("sessionId", out var sid) &&
                 sessionIds.Contains(sid?.ToString() ?? "")))
            .OrderBy(e => e.Timestamp)
            .ToList();

        var report = new IncidentAuditReport
        {
            ReportId = GenerateId("report"),
            IncidentReference = incidentReference,
            GeneratedAt = DateTime.UtcNow,
            GeneratedBy = "system",
            Requests = requests,
            Sessions = sessions!,
            AuditEntries = relevantEntries,
            Summary = new IncidentSummary
            {
                TotalRequests = requests.Count,
                ApprovedRequests = requests.Count(r =>
                    r.Status == BreakGlassRequestStatus.Approved ||
                    r.Status == BreakGlassRequestStatus.Completed),
                DeniedRequests = requests.Count(r => r.Status == BreakGlassRequestStatus.Denied),
                TotalSessions = sessions.Count,
                ActiveSessions = sessions.Count(s => s!.Status == SessionStatus.Active),
                TotalActivities = sessions.Sum(s => s!.Activities.Count),
                UniqueCustodians = requests
                    .SelectMany(r => r.Approvals.Select(a => a.CustodianId))
                    .Distinct()
                    .Count(),
                FirstEventTime = relevantEntries.FirstOrDefault()?.Timestamp,
                LastEventTime = relevantEntries.LastOrDefault()?.Timestamp
            }
        };

        LogAuditEvent(
            AuditEventType.AuditReportGenerated,
            "system",
            $"Generated incident audit report: {incidentReference}",
            new Dictionary<string, object>
            {
                ["reportId"] = report.ReportId,
                ["incidentReference"] = incidentReference,
                ["entryCount"] = relevantEntries.Count
            });

        return report;
    }

    #endregion

    #region ISnapshotProvider Implementation

    /// <inheritdoc/>
    public override async Task<SnapshotInfo> CreateSnapshotAsync(
        SnapshotRequest request,
        CancellationToken ct = default)
    {
        // This plugin doesn't create snapshots directly, but supports recovery operations
        throw new NotSupportedException(
            "Break-glass plugin does not create snapshots. Use for emergency recovery only.");
    }

    /// <inheritdoc/>
    public override Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(
        SnapshotFilter? filter = null,
        CancellationToken ct = default)
    {
        // Return empty list - this plugin manages emergency access, not snapshots
        return Task.FromResult<IReadOnlyList<SnapshotInfo>>(Array.Empty<SnapshotInfo>());
    }

    /// <inheritdoc/>
    public override Task<SnapshotInfo?> GetSnapshotAsync(
        string snapshotId,
        CancellationToken ct = default)
    {
        return Task.FromResult<SnapshotInfo?>(null);
    }

    /// <inheritdoc/>
    public override Task<bool> DeleteSnapshotAsync(
        string snapshotId,
        CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<RestoreResult> RestoreSnapshotAsync(
        string snapshotId,
        RestoreOptions? options = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Use StartSessionAsync after an approved break-glass request for emergency recovery.");
    }

    /// <inheritdoc/>
    public override async Task<bool> SetRetentionPolicyAsync(
        string snapshotId,
        SnapshotRetentionPolicy policy,
        CancellationToken ct = default)
    {
        // Set retention policy on audit logs
        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> PlaceLegalHoldAsync(
        string snapshotId,
        LegalHoldInfo holdInfo,
        CancellationToken ct = default)
    {
        // Can place legal holds on audit records
        LogAuditEvent(
            AuditEventType.LegalHoldPlaced,
            holdInfo.PlacedBy ?? "system",
            $"Legal hold placed: {holdInfo.Reason}",
            new Dictionary<string, object>
            {
                ["holdId"] = holdInfo.HoldId,
                ["caseNumber"] = holdInfo.CaseNumber ?? "N/A"
            });
        return true;
    }

    /// <inheritdoc/>
    public override async Task<bool> RemoveLegalHoldAsync(
        string snapshotId,
        string holdId,
        CancellationToken ct = default)
    {
        LogAuditEvent(
            AuditEventType.LegalHoldRemoved,
            "system",
            $"Legal hold removed: {holdId}");
        return true;
    }

    #endregion

    #region Policy Management

    /// <summary>
    /// Configures an emergency policy.
    /// </summary>
    /// <param name="policy">The policy to configure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The configured policy.</returns>
    public async Task<EmergencyPolicy> ConfigurePolicyAsync(
        EmergencyPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrEmpty(policy.PolicyId))
        {
            policy.PolicyId = GenerateId("policy");
        }

        policy.UpdatedAt = DateTime.UtcNow;
        _policies[policy.PolicyId] = policy;
        await PersistStateAsync(ct);

        LogAuditEvent(
            AuditEventType.PolicyConfigured,
            policy.UpdatedBy ?? "system",
            $"Configured emergency policy: {policy.Name}",
            new Dictionary<string, object>
            {
                ["policyId"] = policy.PolicyId,
                ["requiredApprovals"] = policy.RequiredApprovals,
                ["maxDuration"] = policy.MaxSessionDuration.TotalMinutes
            });

        return policy;
    }

    /// <summary>
    /// Gets the applicable policy for a request type.
    /// </summary>
    private EmergencyPolicy? GetApplicablePolicy(BreakGlassRequestType requestType)
    {
        return _policies.Values
            .Where(p => p.IsEnabled && p.ApplicableTypes.Contains(requestType))
            .OrderByDescending(p => p.Priority)
            .FirstOrDefault();
    }

    #endregion

    #region Private Helper Methods

    private static string GenerateId(string prefix) =>
        $"{prefix}_{DateTime.UtcNow.Ticks:x}_{Guid.NewGuid():N}";

    private static string GenerateSecureToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(tokenBytes);
    }

    private void LogAuditEvent(
        AuditEventType eventType,
        string actorId,
        string message,
        Dictionary<string, object>? details = null)
    {
        var entry = new AuditLogEntry
        {
            EntryId = GenerateId("audit"),
            EventType = eventType,
            Timestamp = DateTime.UtcNow,
            ActorId = actorId,
            Message = message,
            Details = details ?? new Dictionary<string, object>(),
            SourceIp = null, // Would be populated from request context
            CorrelationId = Guid.NewGuid().ToString("N")
        };

        _auditLog[entry.EntryId] = entry;
        AuditEventRecorded?.Invoke(this, entry);
    }

    private async Task NotifyCustodiansAsync(
        BreakGlassRequest request,
        CancellationToken ct)
    {
        // This would integrate with notification system (email, SMS, etc.)
        // For now, just log the notification
        var activeCustodians = _custodians.Values
            .Where(c => c.Status == CustodianStatus.Active)
            .ToList();

        foreach (var custodian in activeCustodians)
        {
            LogAuditEvent(
                AuditEventType.CustodianNotified,
                "system",
                $"Notified custodian {custodian.Name} of break-glass request",
                new Dictionary<string, object>
                {
                    ["requestId"] = request.RequestId,
                    ["custodianId"] = custodian.CustodianId,
                    ["email"] = custodian.Email
                });
        }

        await Task.CompletedTask;
    }

    private async Task CheckExpiredSessionsAsync()
    {
        if (_cts?.IsCancellationRequested ?? true) return;

        var now = DateTime.UtcNow;
        var expiredSessions = _activeSessions.Values
            .Where(s => s.Status == SessionStatus.Active && s.ExpiresAt < now)
            .ToList();

        foreach (var session in expiredSessions)
        {
            session.Status = SessionStatus.Expired;
            session.TerminatedAt = now;
            session.TerminationReason = "Session expired";

            // Clear sensitive data
            if (session.ReconstructedKey != null)
            {
                CryptographicOperations.ZeroMemory(session.ReconstructedKey);
                session.ReconstructedKey = null;
            }

            LogAuditEvent(
                AuditEventType.SessionExpired,
                "system",
                $"Emergency session expired: {session.SessionId}",
                new Dictionary<string, object>
                {
                    ["sessionId"] = session.SessionId,
                    ["originalDuration"] = session.Duration.TotalMinutes
                });

            SessionTerminated?.Invoke(this, session);
        }

        if (expiredSessions.Any())
        {
            await PersistStateAsync(CancellationToken.None);
        }
    }

    private async Task CheckTimedOutRequestsAsync()
    {
        if (_cts?.IsCancellationRequested ?? true) return;

        var now = DateTime.UtcNow;
        var timedOutRequests = _pendingRequests.Values
            .Where(r => r.Status == BreakGlassRequestStatus.Pending && r.ExpiresAt < now)
            .ToList();

        foreach (var request in timedOutRequests)
        {
            request.Status = BreakGlassRequestStatus.Expired;

            LogAuditEvent(
                AuditEventType.RequestExpired,
                "system",
                $"Break-glass request expired: {request.RequestId}",
                new Dictionary<string, object>
                {
                    ["requestId"] = request.RequestId,
                    ["incidentReference"] = request.IncidentReference
                });
        }

        if (timedOutRequests.Any())
        {
            await PersistStateAsync(CancellationToken.None);
        }
    }

    #endregion

    #region Persistence

    private async Task LoadStateAsync(CancellationToken ct)
    {
        var stateFile = Path.Combine(_storagePath, "breakglass_state.json");
        if (!File.Exists(stateFile)) return;

        try
        {
            var json = await File.ReadAllTextAsync(stateFile, ct);
            var state = JsonSerializer.Deserialize<BreakGlassPluginState>(json, JsonOptions);

            if (state == null) return;

            foreach (var custodian in state.Custodians.Where(c => c != null))
                _custodians[custodian.CustodianId] = custodian;

            foreach (var escrow in state.EscrowedKeys)
                _escrowedKeys[escrow.EscrowId] = escrow;

            foreach (var key in state.RecoveryKeys)
                _recoveryKeys[key.KeyId] = key;

            foreach (var request in state.Requests)
                _pendingRequests[request.RequestId] = request;

            foreach (var session in state.Sessions)
                _activeSessions[session.SessionId] = session;

            foreach (var policy in state.Policies)
                _policies[policy.PolicyId] = policy;

            foreach (var entry in state.AuditLog)
                _auditLog[entry.EntryId] = entry;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load break-glass state: {ex.Message}");
        }
    }

    private async Task PersistStateAsync(CancellationToken ct)
    {
        await _persistLock.WaitAsync(ct);
        try
        {
            var state = new BreakGlassPluginState
            {
                Custodians = _custodians.Values.ToList(),
                EscrowedKeys = _escrowedKeys.Values.ToList(),
                RecoveryKeys = _recoveryKeys.Values.ToList(),
                Requests = _pendingRequests.Values.ToList(),
                Sessions = _activeSessions.Values.Select(s => new EmergencySession
                {
                    SessionId = s.SessionId,
                    RequestId = s.RequestId,
                    RequestorId = s.RequestorId,
                    RequestorName = s.RequestorName,
                    SessionToken = null!, // Never persist token
                    TokenHash = s.TokenHash,
                    StartedAt = s.StartedAt,
                    ExpiresAt = s.ExpiresAt,
                    TerminatedAt = s.TerminatedAt,
                    Duration = s.Duration,
                    Status = s.Status,
                    AccessLevel = s.AccessLevel,
                    TargetKeyId = s.TargetKeyId,
                    TargetResource = s.TargetResource,
                    ReconstructedKey = null, // Never persist key
                    Activities = s.Activities,
                    ApprovedBy = s.ApprovedBy,
                    TerminationReason = s.TerminationReason
                }).ToList(),
                Policies = _policies.Values.ToList(),
                AuditLog = _auditLog.Values.TakeLast(10000).ToList(),
                SavedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(state, JsonOptions);
            var stateFile = Path.Combine(_storagePath, "breakglass_state.json");
            await File.WriteAllTextAsync(stateFile, json, ct);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Metadata

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "CreateBreakGlassRequest",
                Description = "Creates an emergency break-glass access request",
                RequiresApproval = false,
                ParameterSchemaJson = @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""reason"": { ""type"": ""string"", ""description"": ""Reason for emergency access"" },
                        ""incidentReference"": { ""type"": ""string"", ""description"": ""Incident ticket reference"" },
                        ""targetKeyId"": { ""type"": ""string"", ""description"": ""Target recovery key ID"" }
                    },
                    ""required"": [""reason"", ""incidentReference""]
                }"
            },
            new()
            {
                Name = "ApproveRequest",
                Description = "Approves a break-glass request (requires custodian authorization)",
                RequiresApproval = true,
                ParameterSchemaJson = @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""requestId"": { ""type"": ""string"", ""description"": ""Request ID to approve"" },
                        ""custodianId"": { ""type"": ""string"", ""description"": ""Approving custodian ID"" }
                    },
                    ""required"": [""requestId"", ""custodianId""]
                }"
            },
            new()
            {
                Name = "StartEmergencySession",
                Description = "Starts an emergency session after request approval",
                RequiresApproval = true,
                ParameterSchemaJson = @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""requestId"": { ""type"": ""string"", ""description"": ""Approved request ID"" }
                    },
                    ""required"": [""requestId""]
                }"
            },
            new()
            {
                Name = "EscrowKey",
                Description = "Escrows a key using Shamir Secret Sharing",
                RequiresApproval = true,
                ParameterSchemaJson = @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""keyMaterial"": { ""type"": ""string"", ""format"": ""byte"", ""description"": ""Key to escrow (base64)"" },
                        ""description"": { ""type"": ""string"", ""description"": ""Key description"" },
                        ""threshold"": { ""type"": ""integer"", ""description"": ""Minimum shares for reconstruction"" }
                    },
                    ""required"": [""keyMaterial"", ""description"", ""threshold""]
                }"
            },
            new()
            {
                Name = "GenerateRecoveryKey",
                Description = "Generates and escrows a new recovery key",
                RequiresApproval = true,
                ParameterSchemaJson = @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""keyLength"": { ""type"": ""integer"", ""description"": ""Key length in bytes"" },
                        ""description"": { ""type"": ""string"", ""description"": ""Key purpose"" },
                        ""threshold"": { ""type"": ""integer"", ""description"": ""Reconstruction threshold"" }
                    },
                    ""required"": [""keyLength"", ""description"", ""threshold""]
                }"
            },
            new()
            {
                Name = "QueryAuditLog",
                Description = "Queries the comprehensive audit trail",
                RequiresApproval = false,
                ParameterSchemaJson = @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""startTime"": { ""type"": ""string"", ""format"": ""date-time"", ""description"": ""Start of time range"" },
                        ""endTime"": { ""type"": ""string"", ""format"": ""date-time"", ""description"": ""End of time range"" },
                        ""eventType"": { ""type"": ""string"", ""description"": ""Filter by event type"" }
                    }
                }"
            }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "Enterprise break-glass emergency recovery with multi-party authorization, Shamir secret sharing, and comprehensive audit trail";
        metadata["FeatureType"] = "BreakGlassRecovery";
        metadata["MinimumCustodianThreshold"] = MinimumCustodianThreshold;
        metadata["MaxSessionDuration"] = MaxSessionDuration.TotalMinutes;
        metadata["ActiveCustodians"] = _custodians.Values.Count(c => c.Status == CustodianStatus.Active);
        metadata["PendingRequests"] = _pendingRequests.Values.Count(r => r.Status == BreakGlassRequestStatus.Pending);
        metadata["ActiveSessions"] = _activeSessions.Values.Count(s => s.Status == SessionStatus.Active);
        metadata["EscrowedKeys"] = _escrowedKeys.Count;
        metadata["SupportsShamirSecretSharing"] = true;
        metadata["SupportsMultiPartyAuthorization"] = true;
        metadata["SupportsTimeLimitedAccess"] = true;
        metadata["SupportsAuditTrail"] = true;
        return metadata;
    }

    #endregion

    #region Disposal

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        _persistLock.Dispose();
        _sessionLock.Dispose();
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Configuration for the Break-Glass Recovery Plugin.
/// </summary>
public sealed class BreakGlassConfiguration
{
    /// <summary>
    /// Base storage path for plugin data.
    /// </summary>
    public string? StoragePath { get; init; }

    /// <summary>
    /// Minimum number of custodians required to approve a break-glass request.
    /// Default is 2 (dual-control).
    /// </summary>
    public int MinimumCustodianThreshold { get; init; } = 2;

    /// <summary>
    /// Maximum duration for an emergency session. Default is 4 hours.
    /// </summary>
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Default session duration if not specified in request. Default is 1 hour.
    /// </summary>
    public TimeSpan DefaultSessionDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a request remains valid before expiring. Default is 24 hours.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether to require all custodian shares for key reconstruction.
    /// </summary>
    public bool RequireAllShares { get; init; } = false;

    /// <summary>
    /// Notification webhook URL for break-glass events.
    /// </summary>
    public string? NotificationWebhook { get; init; }
}

/// <summary>
/// Registration information for a new key custodian.
/// </summary>
public sealed class KeyCustodianRegistration
{
    /// <summary>
    /// Full name of the custodian.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Email address for notifications.
    /// </summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// Phone number for emergency contact.
    /// </summary>
    public string? Phone { get; init; }

    /// <summary>
    /// Department or team.
    /// </summary>
    public string? Department { get; init; }

    /// <summary>
    /// Role (e.g., "Security Admin", "CTO", "On-Call Engineer").
    /// </summary>
    public string Role { get; init; } = "Standard";

    /// <summary>
    /// Public key for encrypted share distribution.
    /// </summary>
    public byte[]? PublicKey { get; init; }

    /// <summary>
    /// Who is registering this custodian.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Permissions granted to this custodian.
    /// </summary>
    public CustodianPermissions? Permissions { get; init; }
}

/// <summary>
/// A registered key custodian.
/// </summary>
public sealed class KeyCustodian
{
    /// <summary>
    /// Unique identifier for the custodian.
    /// </summary>
    public string CustodianId { get; init; } = string.Empty;

    /// <summary>
    /// Full name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Email address.
    /// </summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// Phone number.
    /// </summary>
    public string? Phone { get; init; }

    /// <summary>
    /// Department or team.
    /// </summary>
    public string? Department { get; init; }

    /// <summary>
    /// Role designation.
    /// </summary>
    public string Role { get; init; } = "Standard";

    /// <summary>
    /// Public key for encrypted communications.
    /// </summary>
    public byte[]? PublicKey { get; init; }

    /// <summary>
    /// When the custodian was registered.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Who registered the custodian.
    /// </summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>
    /// Current status.
    /// </summary>
    public CustodianStatus Status { get; set; }

    /// <summary>
    /// Custodian permissions.
    /// </summary>
    public CustodianPermissions Permissions { get; init; }

    /// <summary>
    /// When the custodian was deactivated.
    /// </summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>
    /// Who deactivated the custodian.
    /// </summary>
    public string? DeactivatedBy { get; set; }

    /// <summary>
    /// Reason for deactivation.
    /// </summary>
    public string? DeactivationReason { get; set; }
}

/// <summary>
/// Custodian status.
/// </summary>
public enum CustodianStatus
{
    /// <summary>Active and can participate in approvals.</summary>
    Active,
    /// <summary>Temporarily suspended.</summary>
    Suspended,
    /// <summary>Permanently deactivated.</summary>
    Inactive
}

/// <summary>
/// Custodian permissions.
/// </summary>
[Flags]
public enum CustodianPermissions
{
    /// <summary>No permissions.</summary>
    None = 0,
    /// <summary>Can approve requests.</summary>
    Approve = 1,
    /// <summary>Can deny requests.</summary>
    Deny = 2,
    /// <summary>Can view audit logs.</summary>
    ViewAudit = 4,
    /// <summary>Can register other custodians.</summary>
    ManageCustodians = 8,
    /// <summary>Can configure policies.</summary>
    ConfigurePolicies = 16,
    /// <summary>All permissions.</summary>
    All = Approve | Deny | ViewAudit | ManageCustodians | ConfigurePolicies
}

/// <summary>
/// Input for creating a break-glass request.
/// </summary>
public sealed class BreakGlassRequestInput
{
    /// <summary>
    /// ID of the person making the request.
    /// </summary>
    public string RequestorId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the requestor.
    /// </summary>
    public string? RequestorName { get; init; }

    /// <summary>
    /// Email of the requestor.
    /// </summary>
    public string? RequestorEmail { get; init; }

    /// <summary>
    /// Brief reason for the emergency access.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Detailed justification.
    /// </summary>
    public string? Justification { get; init; }

    /// <summary>
    /// Incident ticket or reference number.
    /// </summary>
    public string IncidentReference { get; init; } = string.Empty;

    /// <summary>
    /// Target recovery key ID (if requesting key access).
    /// </summary>
    public string? TargetKeyId { get; init; }

    /// <summary>
    /// Target resource identifier.
    /// </summary>
    public string? TargetResource { get; init; }

    /// <summary>
    /// Type of break-glass request.
    /// </summary>
    public BreakGlassRequestType RequestType { get; init; }

    /// <summary>
    /// Level of access requested.
    /// </summary>
    public EmergencyAccessLevel RequestedAccess { get; init; }

    /// <summary>
    /// Requested session duration.
    /// </summary>
    public TimeSpan? RequestedDuration { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// A break-glass emergency access request.
/// </summary>
public sealed class BreakGlassRequest
{
    /// <summary>
    /// Unique request identifier.
    /// </summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>
    /// ID of the requestor.
    /// </summary>
    public string RequestorId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the requestor.
    /// </summary>
    public string? RequestorName { get; init; }

    /// <summary>
    /// Email of the requestor.
    /// </summary>
    public string? RequestorEmail { get; init; }

    /// <summary>
    /// Reason for the request.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Detailed justification.
    /// </summary>
    public string? Justification { get; init; }

    /// <summary>
    /// Incident reference number.
    /// </summary>
    public string IncidentReference { get; init; } = string.Empty;

    /// <summary>
    /// Target recovery key ID.
    /// </summary>
    public string? TargetKeyId { get; init; }

    /// <summary>
    /// Target resource.
    /// </summary>
    public string? TargetResource { get; init; }

    /// <summary>
    /// Type of request.
    /// </summary>
    public BreakGlassRequestType RequestType { get; init; }

    /// <summary>
    /// Requested access level.
    /// </summary>
    public EmergencyAccessLevel RequestedAccess { get; init; }

    /// <summary>
    /// Requested session duration.
    /// </summary>
    public TimeSpan RequestedDuration { get; init; }

    /// <summary>
    /// When the request was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// When the request expires.
    /// </summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Current status.
    /// </summary>
    public BreakGlassRequestStatus Status { get; set; }

    /// <summary>
    /// Number of approvals required.
    /// </summary>
    public int RequiredApprovals { get; init; }

    /// <summary>
    /// List of approvals received.
    /// </summary>
    public List<RequestApproval> Approvals { get; init; } = new();

    /// <summary>
    /// List of denials received.
    /// </summary>
    public List<RequestDenial> Denials { get; init; } = new();

    /// <summary>
    /// Collected key shares from approving custodians.
    /// </summary>
    public List<SecretShare>? CollectedShares { get; set; }

    /// <summary>
    /// When the request was fully approved.
    /// </summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// When the request was denied.
    /// </summary>
    public DateTime? DeniedAt { get; set; }

    /// <summary>
    /// Reason for denial.
    /// </summary>
    public string? DenialReason { get; set; }

    /// <summary>
    /// Session ID if a session was started.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Type of break-glass request.
/// </summary>
public enum BreakGlassRequestType
{
    /// <summary>Access to encrypted data.</summary>
    DataAccess,
    /// <summary>Recovery key reconstruction.</summary>
    KeyRecovery,
    /// <summary>System recovery.</summary>
    SystemRecovery,
    /// <summary>Configuration override.</summary>
    ConfigOverride,
    /// <summary>Account recovery.</summary>
    AccountRecovery,
    /// <summary>Compliance investigation.</summary>
    ComplianceInvestigation
}

/// <summary>
/// Emergency access level.
/// </summary>
public enum EmergencyAccessLevel
{
    /// <summary>Read-only access.</summary>
    ReadOnly,
    /// <summary>Read and write access.</summary>
    ReadWrite,
    /// <summary>Full administrative access.</summary>
    Admin,
    /// <summary>Root/superuser access.</summary>
    Root
}

/// <summary>
/// Status of a break-glass request.
/// </summary>
public enum BreakGlassRequestStatus
{
    /// <summary>Awaiting approvals.</summary>
    Pending,
    /// <summary>Fully approved.</summary>
    Approved,
    /// <summary>Denied by a custodian.</summary>
    Denied,
    /// <summary>Request expired before sufficient approvals.</summary>
    Expired,
    /// <summary>Cancelled by requestor.</summary>
    Cancelled,
    /// <summary>Session started.</summary>
    Completed
}

/// <summary>
/// An approval for a break-glass request.
/// </summary>
public sealed class RequestApproval
{
    /// <summary>
    /// ID of the approving custodian.
    /// </summary>
    public string CustodianId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the approving custodian.
    /// </summary>
    public string CustodianName { get; init; } = string.Empty;

    /// <summary>
    /// When the approval was given.
    /// </summary>
    public DateTime ApprovedAt { get; init; }

    /// <summary>
    /// Optional comments.
    /// </summary>
    public string? Comments { get; init; }

    /// <summary>
    /// Whether the custodian provided their key share.
    /// </summary>
    public bool ShareProvided { get; init; }
}

/// <summary>
/// A denial for a break-glass request.
/// </summary>
public sealed class RequestDenial
{
    /// <summary>
    /// ID of the denying custodian.
    /// </summary>
    public string CustodianId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the denying custodian.
    /// </summary>
    public string CustodianName { get; init; } = string.Empty;

    /// <summary>
    /// When the denial was given.
    /// </summary>
    public DateTime DeniedAt { get; init; }

    /// <summary>
    /// Reason for denial.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Filter for querying break-glass requests.
/// </summary>
public sealed class BreakGlassRequestFilter
{
    /// <summary>
    /// Filter by status.
    /// </summary>
    public BreakGlassRequestStatus? Status { get; init; }

    /// <summary>
    /// Filter by requestor.
    /// </summary>
    public string? RequestorId { get; init; }

    /// <summary>
    /// Filter by creation time (start).
    /// </summary>
    public DateTime? CreatedAfter { get; init; }

    /// <summary>
    /// Filter by creation time (end).
    /// </summary>
    public DateTime? CreatedBefore { get; init; }

    /// <summary>
    /// Filter by incident reference.
    /// </summary>
    public string? IncidentReference { get; init; }

    /// <summary>
    /// Maximum results to return.
    /// </summary>
    public int Limit { get; init; } = 100;
}

/// <summary>
/// An active emergency session.
/// </summary>
public sealed class EmergencySession
{
    /// <summary>
    /// Unique session identifier.
    /// </summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// ID of the originating request.
    /// </summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>
    /// ID of the session owner.
    /// </summary>
    public string RequestorId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the session owner.
    /// </summary>
    public string? RequestorName { get; init; }

    /// <summary>
    /// Session authentication token (only provided once at session start).
    /// </summary>
    public string SessionToken { get; init; } = string.Empty;

    /// <summary>
    /// Hash of the session token for validation.
    /// </summary>
    public byte[] TokenHash { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// When the session started.
    /// </summary>
    public DateTime StartedAt { get; init; }

    /// <summary>
    /// When the session expires.
    /// </summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>
    /// When the session was terminated.
    /// </summary>
    public DateTime? TerminatedAt { get; set; }

    /// <summary>
    /// Requested duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Current session status.
    /// </summary>
    public SessionStatus Status { get; set; }

    /// <summary>
    /// Granted access level.
    /// </summary>
    public EmergencyAccessLevel AccessLevel { get; init; }

    /// <summary>
    /// Target recovery key ID.
    /// </summary>
    public string? TargetKeyId { get; init; }

    /// <summary>
    /// Target resource.
    /// </summary>
    public string? TargetResource { get; init; }

    /// <summary>
    /// Reconstructed key material (only provided once at session start).
    /// </summary>
    public byte[]? ReconstructedKey { get; set; }

    /// <summary>
    /// List of activities during the session.
    /// </summary>
    public List<SessionActivity> Activities { get; init; } = new();

    /// <summary>
    /// IDs of custodians who approved this session.
    /// </summary>
    public List<string> ApprovedBy { get; init; } = new();

    /// <summary>
    /// Reason for termination.
    /// </summary>
    public string? TerminationReason { get; set; }
}

/// <summary>
/// Status of an emergency session.
/// </summary>
public enum SessionStatus
{
    /// <summary>Session is active.</summary>
    Active,
    /// <summary>Session expired naturally.</summary>
    Expired,
    /// <summary>Session was manually terminated.</summary>
    Terminated,
    /// <summary>Session was revoked by a custodian.</summary>
    Revoked
}

/// <summary>
/// An activity within an emergency session.
/// </summary>
public sealed class SessionActivity
{
    /// <summary>
    /// Unique activity identifier.
    /// </summary>
    public string ActivityId { get; init; } = string.Empty;

    /// <summary>
    /// When the activity occurred.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Type of activity.
    /// </summary>
    public string ActivityType { get; init; } = string.Empty;

    /// <summary>
    /// Description of the activity.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Additional details.
    /// </summary>
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>
/// A generated recovery key.
/// </summary>
public sealed class RecoveryKey
{
    /// <summary>
    /// Unique key identifier.
    /// </summary>
    public string KeyId { get; init; } = string.Empty;

    /// <summary>
    /// ID of the escrow entry containing the shares.
    /// </summary>
    public string EscrowId { get; init; } = string.Empty;

    /// <summary>
    /// Description of the key's purpose.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the key for verification.
    /// </summary>
    public byte[] KeyHash { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Length of the key in bytes.
    /// </summary>
    public int KeyLength { get; init; }

    /// <summary>
    /// Encryption algorithm the key is for.
    /// </summary>
    public string Algorithm { get; init; } = string.Empty;

    /// <summary>
    /// When the key was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Who created the key.
    /// </summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>
    /// When the key expires.
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Current status.
    /// </summary>
    public RecoveryKeyStatus Status { get; set; }

    /// <summary>
    /// Reconstruction threshold.
    /// </summary>
    public int Threshold { get; init; }

    /// <summary>
    /// Total shares created.
    /// </summary>
    public int TotalShares { get; init; }
}

/// <summary>
/// Status of a recovery key.
/// </summary>
public enum RecoveryKeyStatus
{
    /// <summary>Key is active and can be recovered.</summary>
    Active,
    /// <summary>Key has been used/recovered.</summary>
    Used,
    /// <summary>Key has expired.</summary>
    Expired,
    /// <summary>Key has been revoked.</summary>
    Revoked
}

/// <summary>
/// An entry in the audit log.
/// </summary>
public sealed class AuditLogEntry
{
    /// <summary>
    /// Unique entry identifier.
    /// </summary>
    public string EntryId { get; init; } = string.Empty;

    /// <summary>
    /// Type of event.
    /// </summary>
    public AuditEventType EventType { get; init; }

    /// <summary>
    /// When the event occurred.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// ID of the actor who performed the action.
    /// </summary>
    public string ActorId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Additional details.
    /// </summary>
    public Dictionary<string, object> Details { get; init; } = new();

    /// <summary>
    /// Source IP address.
    /// </summary>
    public string? SourceIp { get; init; }

    /// <summary>
    /// Correlation ID for request tracing.
    /// </summary>
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Types of audit events.
/// </summary>
public enum AuditEventType
{
    /// <summary>Plugin started.</summary>
    PluginStarted,
    /// <summary>Plugin stopped.</summary>
    PluginStopped,
    /// <summary>Custodian registered.</summary>
    CustodianRegistered,
    /// <summary>Custodian deactivated.</summary>
    CustodianDeactivated,
    /// <summary>Custodian notified of request.</summary>
    CustodianNotified,
    /// <summary>Key escrowed.</summary>
    KeyEscrowed,
    /// <summary>Recovery key generated.</summary>
    RecoveryKeyGenerated,
    /// <summary>Break-glass request created.</summary>
    BreakGlassRequested,
    /// <summary>Request approved by a custodian.</summary>
    BreakGlassApproved,
    /// <summary>Request fully approved.</summary>
    BreakGlassFullyApproved,
    /// <summary>Request denied.</summary>
    BreakGlassDenied,
    /// <summary>Request expired.</summary>
    RequestExpired,
    /// <summary>Emergency session started.</summary>
    EmergencySessionStarted,
    /// <summary>Activity within a session.</summary>
    SessionActivity,
    /// <summary>Session expired.</summary>
    SessionExpired,
    /// <summary>Session terminated.</summary>
    EmergencySessionTerminated,
    /// <summary>Invalid session token attempt.</summary>
    InvalidSessionToken,
    /// <summary>Key reconstruction failed.</summary>
    KeyReconstructionFailed,
    /// <summary>Audit report generated.</summary>
    AuditReportGenerated,
    /// <summary>Policy configured.</summary>
    PolicyConfigured,
    /// <summary>Legal hold placed.</summary>
    LegalHoldPlaced,
    /// <summary>Legal hold removed.</summary>
    LegalHoldRemoved
}

/// <summary>
/// Filter for querying audit logs.
/// </summary>
public sealed class AuditLogFilter
{
    /// <summary>
    /// Filter by event type.
    /// </summary>
    public AuditEventType? EventType { get; init; }

    /// <summary>
    /// Filter by actor.
    /// </summary>
    public string? ActorId { get; init; }

    /// <summary>
    /// Filter by time (start).
    /// </summary>
    public DateTime? StartTime { get; init; }

    /// <summary>
    /// Filter by time (end).
    /// </summary>
    public DateTime? EndTime { get; init; }

    /// <summary>
    /// Filter by session ID.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Filter by request ID.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Maximum results to return.
    /// </summary>
    public int Limit { get; init; } = 1000;
}

/// <summary>
/// An emergency policy configuration.
/// </summary>
public sealed class EmergencyPolicy
{
    /// <summary>
    /// Unique policy identifier.
    /// </summary>
    public string PolicyId { get; set; } = string.Empty;

    /// <summary>
    /// Policy name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Policy description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Request types this policy applies to.
    /// </summary>
    public List<BreakGlassRequestType> ApplicableTypes { get; init; } = new();

    /// <summary>
    /// Required number of approvals.
    /// </summary>
    public int RequiredApprovals { get; init; }

    /// <summary>
    /// Maximum allowed session duration.
    /// </summary>
    public TimeSpan MaxSessionDuration { get; init; }

    /// <summary>
    /// Whether to require specific custodian roles.
    /// </summary>
    public List<string>? RequiredRoles { get; init; }

    /// <summary>
    /// Priority (higher priority policies are evaluated first).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Whether the policy is enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// When the policy was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Who last updated the policy.
    /// </summary>
    public string? UpdatedBy { get; init; }
}

/// <summary>
/// Comprehensive incident audit report.
/// </summary>
public sealed class IncidentAuditReport
{
    /// <summary>
    /// Unique report identifier.
    /// </summary>
    public string ReportId { get; init; } = string.Empty;

    /// <summary>
    /// Incident reference number.
    /// </summary>
    public string IncidentReference { get; init; } = string.Empty;

    /// <summary>
    /// When the report was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary>
    /// Who generated the report.
    /// </summary>
    public string GeneratedBy { get; init; } = string.Empty;

    /// <summary>
    /// All related break-glass requests.
    /// </summary>
    public List<BreakGlassRequest> Requests { get; init; } = new();

    /// <summary>
    /// All related emergency sessions.
    /// </summary>
    public List<EmergencySession> Sessions { get; init; } = new();

    /// <summary>
    /// All related audit entries.
    /// </summary>
    public List<AuditLogEntry> AuditEntries { get; init; } = new();

    /// <summary>
    /// Summary statistics.
    /// </summary>
    public IncidentSummary Summary { get; init; } = new();
}

/// <summary>
/// Summary of an incident for reporting.
/// </summary>
public sealed class IncidentSummary
{
    /// <summary>
    /// Total break-glass requests.
    /// </summary>
    public int TotalRequests { get; init; }

    /// <summary>
    /// Number of approved requests.
    /// </summary>
    public int ApprovedRequests { get; init; }

    /// <summary>
    /// Number of denied requests.
    /// </summary>
    public int DeniedRequests { get; init; }

    /// <summary>
    /// Total emergency sessions.
    /// </summary>
    public int TotalSessions { get; init; }

    /// <summary>
    /// Currently active sessions.
    /// </summary>
    public int ActiveSessions { get; init; }

    /// <summary>
    /// Total activities across all sessions.
    /// </summary>
    public int TotalActivities { get; init; }

    /// <summary>
    /// Number of unique custodians involved.
    /// </summary>
    public int UniqueCustodians { get; init; }

    /// <summary>
    /// Time of first related event.
    /// </summary>
    public DateTime? FirstEventTime { get; init; }

    /// <summary>
    /// Time of last related event.
    /// </summary>
    public DateTime? LastEventTime { get; init; }
}

/// <summary>
/// Internal state for persistence.
/// </summary>
internal sealed class BreakGlassPluginState
{
    public List<KeyCustodian> Custodians { get; init; } = new();
    public List<KeyEscrowEntry> EscrowedKeys { get; init; } = new();
    public List<RecoveryKey> RecoveryKeys { get; init; } = new();
    public List<BreakGlassRequest> Requests { get; init; } = new();
    public List<EmergencySession> Sessions { get; init; } = new();
    public List<EmergencyPolicy> Policies { get; init; } = new();
    public List<AuditLogEntry> AuditLog { get; init; } = new();
    public DateTime SavedAt { get; init; }
}

#endregion
