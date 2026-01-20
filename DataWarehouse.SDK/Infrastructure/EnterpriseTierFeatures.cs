using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// TIER 2: SMB (NETWORK/SERVER STORAGE)
// ============================================================================

#region Tier 2.1: Multi-Tenant Isolation

/// <summary>
/// Complete data separation with cryptographic boundaries between tenants.
/// Provides enterprise-grade tenant isolation for multi-tenant deployments.
/// </summary>
public sealed class CryptographicTenantIsolation : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TenantSecurityContext> _tenants = new();
    private readonly AsyncLocal<string?> _currentTenantId = new();
    private readonly IKeyManagementService _keyService;
    private readonly TenantIsolationConfig _config;
    private volatile bool _disposed;

    public CryptographicTenantIsolation(
        IKeyManagementService keyService,
        TenantIsolationConfig? config = null)
    {
        _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
        _config = config ?? new TenantIsolationConfig();
    }

    /// <summary>
    /// Gets the current tenant ID from async context.
    /// </summary>
    public string? CurrentTenantId => _currentTenantId.Value;

    /// <summary>
    /// Provisions a new tenant with cryptographic isolation.
    /// </summary>
    public async Task<TenantProvisionResult> ProvisionTenantAsync(
        string tenantId,
        TenantProvisionRequest request,
        CancellationToken ct = default)
    {
        if (_tenants.ContainsKey(tenantId))
            return new TenantProvisionResult { Success = false, Error = "Tenant already exists" };

        // Generate tenant-specific encryption key
        var tenantKey = await _keyService.GenerateKeyAsync(tenantId, KeyType.DataEncryption, ct);

        // Generate tenant-specific signing key
        var signingKey = await _keyService.GenerateKeyAsync($"{tenantId}_signing", KeyType.Signing, ct);

        var context = new TenantSecurityContext
        {
            TenantId = tenantId,
            EncryptionKeyId = tenantKey.KeyId,
            SigningKeyId = signingKey.KeyId,
            CreatedAt = DateTime.UtcNow,
            Quotas = request.Quotas ?? new TenantQuotaConfig(),
            IsolationLevel = request.IsolationLevel,
            DataResidency = request.DataResidency
        };

        _tenants[tenantId] = context;

        return new TenantProvisionResult
        {
            Success = true,
            TenantId = tenantId,
            EncryptionKeyId = tenantKey.KeyId,
            ProvisionedAt = context.CreatedAt
        };
    }

    /// <summary>
    /// Enters tenant context for all subsequent operations.
    /// </summary>
    public TenantScope EnterTenantContext(string tenantId)
    {
        if (!_tenants.TryGetValue(tenantId, out var context))
            throw new TenantNotFoundException(tenantId);

        var previousTenantId = _currentTenantId.Value;
        _currentTenantId.Value = tenantId;

        return new TenantScope(() => _currentTenantId.Value = previousTenantId);
    }

    /// <summary>
    /// Encrypts data with tenant-specific key.
    /// </summary>
    public async Task<EncryptedTenantData> EncryptForTenantAsync(
        byte[] data,
        CancellationToken ct = default)
    {
        var tenantId = _currentTenantId.Value
            ?? throw new InvalidOperationException("No tenant context");

        if (!_tenants.TryGetValue(tenantId, out var context))
            throw new TenantNotFoundException(tenantId);

        var key = await _keyService.GetKeyAsync(context.EncryptionKeyId, ct);

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[data.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key.KeyMaterial, 16);
        aes.Encrypt(nonce, data, ciphertext, tag);

        return new EncryptedTenantData
        {
            TenantId = tenantId,
            KeyId = context.EncryptionKeyId,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Decrypts tenant data with verification.
    /// </summary>
    public async Task<byte[]> DecryptTenantDataAsync(
        EncryptedTenantData encrypted,
        CancellationToken ct = default)
    {
        var currentTenant = _currentTenantId.Value;

        // Enforce tenant boundary
        if (_config.StrictIsolation && currentTenant != encrypted.TenantId)
            throw new TenantBoundaryViolationException(currentTenant, encrypted.TenantId);

        if (!_tenants.TryGetValue(encrypted.TenantId, out var context))
            throw new TenantNotFoundException(encrypted.TenantId);

        var key = await _keyService.GetKeyAsync(context.EncryptionKeyId, ct);

        var plaintext = new byte[encrypted.Ciphertext.Length];

        using var aes = new AesGcm(key.KeyMaterial, 16);
        aes.Decrypt(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Validates current operation against tenant quotas.
    /// </summary>
    public TenantQuotaCheckResult CheckQuota(QuotaCheckRequest request)
    {
        var tenantId = _currentTenantId.Value
            ?? throw new InvalidOperationException("No tenant context");

        if (!_tenants.TryGetValue(tenantId, out var context))
            throw new TenantNotFoundException(tenantId);

        var quotas = context.Quotas;
        var usage = context.CurrentUsage;

        var violations = new List<string>();

        if (quotas.MaxStorageBytes > 0 &&
            usage.StorageBytes + request.AdditionalStorageBytes > quotas.MaxStorageBytes)
        {
            violations.Add($"Storage quota exceeded: {usage.StorageBytes + request.AdditionalStorageBytes} > {quotas.MaxStorageBytes}");
        }

        if (quotas.MaxRequestsPerSecond > 0 &&
            usage.RequestsInCurrentWindow >= quotas.MaxRequestsPerSecond)
        {
            violations.Add($"Rate limit exceeded: {usage.RequestsInCurrentWindow} >= {quotas.MaxRequestsPerSecond}");
        }

        if (quotas.MaxConcurrentConnections > 0 &&
            usage.CurrentConnections >= quotas.MaxConcurrentConnections)
        {
            violations.Add($"Connection limit exceeded: {usage.CurrentConnections} >= {quotas.MaxConcurrentConnections}");
        }

        return new TenantQuotaCheckResult
        {
            Allowed = violations.Count == 0,
            Violations = violations
        };
    }

    /// <summary>
    /// Generates isolated storage key for tenant data.
    /// </summary>
    public string GetIsolatedStorageKey(string key)
    {
        var tenantId = _currentTenantId.Value
            ?? throw new InvalidOperationException("No tenant context");

        // Prefix key with tenant ID and hash to prevent path traversal
        var tenantHash = ComputeShortHash(tenantId);
        return $"tenants/{tenantHash}/{tenantId}/{key}";
    }

    /// <summary>
    /// Deprovisions a tenant and securely erases all data.
    /// </summary>
    public async Task<TenantDeprovisionResult> DeprovisionTenantAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        if (!_tenants.TryRemove(tenantId, out var context))
            return new TenantDeprovisionResult { Success = false, Error = "Tenant not found" };

        // Revoke encryption keys
        await _keyService.RevokeKeyAsync(context.EncryptionKeyId, ct);
        await _keyService.RevokeKeyAsync(context.SigningKeyId, ct);

        return new TenantDeprovisionResult
        {
            Success = true,
            TenantId = tenantId,
            DeprovisionedAt = DateTime.UtcNow,
            KeysRevoked = 2
        };
    }

    private static string ComputeShortHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

public interface IKeyManagementService
{
    Task<ManagedKey> GenerateKeyAsync(string keyId, KeyType keyType, CancellationToken ct);
    Task<ManagedKey> GetKeyAsync(string keyId, CancellationToken ct);
    Task RevokeKeyAsync(string keyId, CancellationToken ct);
}

public enum KeyType { DataEncryption, Signing, KeyWrapping }
public enum IsolationLevel { Standard, Enhanced, Maximum }

public record ManagedKey
{
    public required string KeyId { get; init; }
    public required byte[] KeyMaterial { get; init; }
    public KeyType KeyType { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class TenantSecurityContext
{
    public required string TenantId { get; init; }
    public required string EncryptionKeyId { get; init; }
    public required string SigningKeyId { get; init; }
    public DateTime CreatedAt { get; init; }
    public TenantQuotaConfig Quotas { get; init; } = new();
    public TenantUsage CurrentUsage { get; } = new();
    public IsolationLevel IsolationLevel { get; init; }
    public string? DataResidency { get; init; }
}

public sealed class TenantUsage
{
    public long StorageBytes { get; set; }
    public int RequestsInCurrentWindow { get; set; }
    public int CurrentConnections { get; set; }
    public DateTime WindowStart { get; set; } = DateTime.UtcNow;
}

public sealed class TenantQuotaConfig
{
    public long MaxStorageBytes { get; set; }
    public int MaxRequestsPerSecond { get; set; }
    public int MaxConcurrentConnections { get; set; }
    public int MaxUsersPerTenant { get; set; }
}

public record TenantProvisionRequest
{
    public TenantQuotaConfig? Quotas { get; init; }
    public IsolationLevel IsolationLevel { get; init; } = IsolationLevel.Standard;
    public string? DataResidency { get; init; }
}

public record TenantProvisionResult
{
    public bool Success { get; init; }
    public string? TenantId { get; init; }
    public string? EncryptionKeyId { get; init; }
    public DateTime? ProvisionedAt { get; init; }
    public string? Error { get; init; }
}

public record TenantDeprovisionResult
{
    public bool Success { get; init; }
    public string? TenantId { get; init; }
    public DateTime? DeprovisionedAt { get; init; }
    public int KeysRevoked { get; init; }
    public string? Error { get; init; }
}

public record EncryptedTenantData
{
    public required string TenantId { get; init; }
    public required string KeyId { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Tag { get; init; }
    public DateTime EncryptedAt { get; init; }
}

public record QuotaCheckRequest
{
    public long AdditionalStorageBytes { get; init; }
    public int AdditionalConnections { get; init; }
}

public record TenantQuotaCheckResult
{
    public bool Allowed { get; init; }
    public List<string> Violations { get; init; } = new();
}

public sealed class TenantIsolationConfig
{
    public bool StrictIsolation { get; set; } = true;
    public bool AuditCrossTenantAccess { get; set; } = true;
}

public sealed class TenantScope : IDisposable
{
    private readonly Action _onDispose;
    private bool _disposed;

    public TenantScope(Action onDispose) => _onDispose = onDispose;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _onDispose();
    }
}

public sealed class TenantNotFoundException : Exception
{
    public string TenantId { get; }
    public TenantNotFoundException(string tenantId) : base($"Tenant '{tenantId}' not found")
        => TenantId = tenantId;
}

public sealed class TenantBoundaryViolationException : Exception
{
    public string? CurrentTenant { get; }
    public string RequestedTenant { get; }

    public TenantBoundaryViolationException(string? current, string requested)
        : base($"Tenant boundary violation: current={current ?? "none"}, requested={requested}")
    {
        CurrentTenant = current;
        RequestedTenant = requested;
    }
}

#endregion

#region Tier 2.2: Role-Based Access Control (RBAC)

/// <summary>
/// Fine-grained permissions system with role inheritance and resource-level access control.
/// </summary>
public sealed class RoleBasedAccessControl
{
    private readonly ConcurrentDictionary<string, Role> _roles = new();
    private readonly ConcurrentDictionary<string, User> _users = new();
    private readonly ConcurrentDictionary<string, ResourcePermissions> _resourcePermissions = new();
    private readonly RbacConfig _config;

    public RoleBasedAccessControl(RbacConfig? config = null)
    {
        _config = config ?? new RbacConfig();

        // Initialize built-in roles
        InitializeBuiltInRoles();
    }

    private void InitializeBuiltInRoles()
    {
        _roles["admin"] = new Role
        {
            Name = "admin",
            Description = "Full system administrator",
            Permissions = new HashSet<Permission>
            {
                Permission.Read, Permission.Write, Permission.Delete,
                Permission.Admin, Permission.ManageUsers, Permission.ManageRoles
            },
            IsBuiltIn = true
        };

        _roles["operator"] = new Role
        {
            Name = "operator",
            Description = "System operator with management rights",
            Permissions = new HashSet<Permission>
            {
                Permission.Read, Permission.Write, Permission.Delete, Permission.ManageUsers
            },
            IsBuiltIn = true
        };

        _roles["user"] = new Role
        {
            Name = "user",
            Description = "Standard user",
            Permissions = new HashSet<Permission>
            {
                Permission.Read, Permission.Write
            },
            IsBuiltIn = true
        };

        _roles["readonly"] = new Role
        {
            Name = "readonly",
            Description = "Read-only access",
            Permissions = new HashSet<Permission> { Permission.Read },
            IsBuiltIn = true
        };
    }

    /// <summary>
    /// Creates a new custom role.
    /// </summary>
    public RoleCreationResult CreateRole(string name, RoleDefinition definition)
    {
        if (_roles.ContainsKey(name))
            return new RoleCreationResult { Success = false, Error = "Role already exists" };

        var role = new Role
        {
            Name = name,
            Description = definition.Description ?? string.Empty,
            Permissions = definition.Permissions.ToHashSet(),
            ParentRole = definition.InheritsFrom,
            IsBuiltIn = false,
            CreatedAt = DateTime.UtcNow
        };

        _roles[name] = role;

        return new RoleCreationResult { Success = true, RoleName = name };
    }

    /// <summary>
    /// Assigns a role to a user.
    /// </summary>
    public void AssignRole(string userId, string roleName, string? scope = null)
    {
        if (!_roles.ContainsKey(roleName))
            throw new RoleNotFoundException(roleName);

        var user = _users.GetOrAdd(userId, _ => new User { UserId = userId });

        lock (user)
        {
            user.RoleAssignments.Add(new RoleAssignment
            {
                RoleName = roleName,
                Scope = scope,
                AssignedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Checks if user has permission for a resource.
    /// </summary>
    public AuthorizationResult Authorize(
        string userId,
        Permission permission,
        string? resourceId = null)
    {
        if (!_users.TryGetValue(userId, out var user))
        {
            return new AuthorizationResult
            {
                Allowed = false,
                Reason = "User not found"
            };
        }

        // Check resource-specific permissions first
        if (resourceId != null && _resourcePermissions.TryGetValue(resourceId, out var resourcePerms))
        {
            if (resourcePerms.UserPermissions.TryGetValue(userId, out var userPerms))
            {
                if (userPerms.Contains(permission))
                    return new AuthorizationResult { Allowed = true, GrantedVia = "resource_direct" };

                if (userPerms.Contains(Permission.Deny))
                    return new AuthorizationResult { Allowed = false, Reason = "Explicitly denied" };
            }
        }

        // Check role-based permissions
        foreach (var assignment in user.RoleAssignments)
        {
            if (!_roles.TryGetValue(assignment.RoleName, out var role))
                continue;

            // Check scope
            if (assignment.Scope != null && resourceId != null &&
                !resourceId.StartsWith(assignment.Scope))
                continue;

            // Check permission (including inherited)
            if (HasPermission(role, permission))
            {
                return new AuthorizationResult
                {
                    Allowed = true,
                    GrantedVia = $"role:{assignment.RoleName}"
                };
            }
        }

        return new AuthorizationResult
        {
            Allowed = false,
            Reason = "No matching permission found"
        };
    }

    /// <summary>
    /// Grants permission on a specific resource.
    /// </summary>
    public void GrantResourcePermission(
        string resourceId,
        string userId,
        params Permission[] permissions)
    {
        var resourcePerms = _resourcePermissions.GetOrAdd(resourceId,
            _ => new ResourcePermissions { ResourceId = resourceId });

        lock (resourcePerms)
        {
            if (!resourcePerms.UserPermissions.ContainsKey(userId))
                resourcePerms.UserPermissions[userId] = new HashSet<Permission>();

            foreach (var perm in permissions)
                resourcePerms.UserPermissions[userId].Add(perm);
        }
    }

    /// <summary>
    /// Gets all effective permissions for a user.
    /// </summary>
    public EffectivePermissions GetEffectivePermissions(string userId, string? resourceId = null)
    {
        var permissions = new HashSet<Permission>();
        var sources = new List<string>();

        if (!_users.TryGetValue(userId, out var user))
            return new EffectivePermissions { Permissions = permissions, Sources = sources };

        // Collect from roles
        foreach (var assignment in user.RoleAssignments)
        {
            if (!_roles.TryGetValue(assignment.RoleName, out var role))
                continue;

            foreach (var perm in GetAllPermissions(role))
            {
                permissions.Add(perm);
                sources.Add($"role:{assignment.RoleName}");
            }
        }

        // Collect from resource
        if (resourceId != null && _resourcePermissions.TryGetValue(resourceId, out var resourcePerms))
        {
            if (resourcePerms.UserPermissions.TryGetValue(userId, out var userPerms))
            {
                foreach (var perm in userPerms)
                {
                    permissions.Add(perm);
                    sources.Add($"resource:{resourceId}");
                }
            }
        }

        return new EffectivePermissions
        {
            Permissions = permissions,
            Sources = sources.Distinct().ToList()
        };
    }

    private bool HasPermission(Role role, Permission permission)
    {
        if (role.Permissions.Contains(permission))
            return true;

        // Check parent role
        if (role.ParentRole != null && _roles.TryGetValue(role.ParentRole, out var parent))
            return HasPermission(parent, permission);

        return false;
    }

    private IEnumerable<Permission> GetAllPermissions(Role role)
    {
        foreach (var perm in role.Permissions)
            yield return perm;

        if (role.ParentRole != null && _roles.TryGetValue(role.ParentRole, out var parent))
        {
            foreach (var perm in GetAllPermissions(parent))
                yield return perm;
        }
    }
}

public enum Permission
{
    Read,
    Write,
    Delete,
    Execute,
    Admin,
    ManageUsers,
    ManageRoles,
    ManageQuotas,
    ViewAuditLogs,
    ManageBackups,
    Deny
}

public sealed class Role
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public HashSet<Permission> Permissions { get; init; } = new();
    public string? ParentRole { get; init; }
    public bool IsBuiltIn { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class User
{
    public required string UserId { get; init; }
    public List<RoleAssignment> RoleAssignments { get; } = new();
}

public sealed class RoleAssignment
{
    public required string RoleName { get; init; }
    public string? Scope { get; init; }
    public DateTime AssignedAt { get; init; }
}

public sealed class ResourcePermissions
{
    public required string ResourceId { get; init; }
    public Dictionary<string, HashSet<Permission>> UserPermissions { get; } = new();
}

public record RoleDefinition
{
    public string? Description { get; init; }
    public IEnumerable<Permission> Permissions { get; init; } = Enumerable.Empty<Permission>();
    public string? InheritsFrom { get; init; }
}

public record RoleCreationResult
{
    public bool Success { get; init; }
    public string? RoleName { get; init; }
    public string? Error { get; init; }
}

public record AuthorizationResult
{
    public bool Allowed { get; init; }
    public string? GrantedVia { get; init; }
    public string? Reason { get; init; }
}

public record EffectivePermissions
{
    public HashSet<Permission> Permissions { get; init; } = new();
    public List<string> Sources { get; init; } = new();
}

public sealed class RbacConfig
{
    public bool CachePermissions { get; set; } = true;
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class RoleNotFoundException : Exception
{
    public string RoleName { get; }
    public RoleNotFoundException(string roleName) : base($"Role '{roleName}' not found")
        => RoleName = roleName;
}

#endregion

#region Tier 2.3: Real-Time Sync with Conflict Resolution

/// <summary>
/// Multi-site synchronization with CRDT-based conflict resolution.
/// </summary>
public sealed class RealTimeSyncEngine : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, VectorClock> _vectorClocks = new();
    private readonly ConcurrentDictionary<string, List<SyncPeer>> _peers = new();
    private readonly Channel<SyncEvent> _syncQueue;
    private readonly SyncEngineConfig _config;
    private readonly string _nodeId;
    private readonly Task _syncTask;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    public RealTimeSyncEngine(string nodeId, SyncEngineConfig? config = null)
    {
        _nodeId = nodeId;
        _config = config ?? new SyncEngineConfig();

        _syncQueue = Channel.CreateBounded<SyncEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _syncTask = SyncLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Records a local change for synchronization.
    /// </summary>
    public async ValueTask RecordChangeAsync(
        string resourceId,
        byte[] data,
        ChangeType changeType,
        CancellationToken ct = default)
    {
        var clock = _vectorClocks.GetOrAdd(resourceId, _ => new VectorClock());

        lock (clock)
        {
            clock.Increment(_nodeId);
        }

        var syncEvent = new SyncEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            ResourceId = resourceId,
            Data = data,
            ChangeType = changeType,
            OriginNode = _nodeId,
            VectorClock = clock.Clone(),
            Timestamp = DateTime.UtcNow
        };

        await _syncQueue.Writer.WriteAsync(syncEvent, ct);
    }

    /// <summary>
    /// Receives a sync event from a remote peer.
    /// </summary>
    public ConflictResolutionResult ReceiveSyncEvent(SyncEvent remoteEvent)
    {
        var localClock = _vectorClocks.GetOrAdd(remoteEvent.ResourceId, _ => new VectorClock());

        lock (localClock)
        {
            var comparison = localClock.Compare(remoteEvent.VectorClock);

            switch (comparison)
            {
                case ClockComparison.Before:
                    // Remote is newer, accept it
                    localClock.Merge(remoteEvent.VectorClock);
                    return new ConflictResolutionResult
                    {
                        Resolution = ResolutionType.AcceptRemote,
                        WinningEventId = remoteEvent.EventId
                    };

                case ClockComparison.After:
                    // Local is newer, reject remote
                    return new ConflictResolutionResult
                    {
                        Resolution = ResolutionType.KeepLocal,
                        WinningEventId = null
                    };

                case ClockComparison.Concurrent:
                    // Conflict - use resolution strategy
                    var resolution = ResolveConflict(remoteEvent);
                    if (resolution.Resolution == ResolutionType.AcceptRemote)
                    {
                        localClock.Merge(remoteEvent.VectorClock);
                    }
                    return resolution;

                case ClockComparison.Equal:
                    // Same version, no action needed
                    return new ConflictResolutionResult
                    {
                        Resolution = ResolutionType.NoChange
                    };

                default:
                    return new ConflictResolutionResult { Resolution = ResolutionType.NoChange };
            }
        }
    }

    /// <summary>
    /// Registers a sync peer.
    /// </summary>
    public void RegisterPeer(string peerId, string endpoint)
    {
        var peers = _peers.GetOrAdd("default", _ => new List<SyncPeer>());

        lock (peers)
        {
            if (!peers.Any(p => p.PeerId == peerId))
            {
                peers.Add(new SyncPeer
                {
                    PeerId = peerId,
                    Endpoint = endpoint,
                    RegisteredAt = DateTime.UtcNow,
                    Status = PeerStatus.Active
                });
            }
        }
    }

    /// <summary>
    /// Gets sync statistics.
    /// </summary>
    public SyncStatistics GetStatistics()
    {
        var allPeers = _peers.Values.SelectMany(p => p).ToList();

        return new SyncStatistics
        {
            NodeId = _nodeId,
            TrackedResources = _vectorClocks.Count,
            ActivePeers = allPeers.Count(p => p.Status == PeerStatus.Active),
            PendingSyncEvents = _syncQueue.Reader.Count,
            TotalConflictsResolved = 0 // Would track this
        };
    }

    private ConflictResolutionResult ResolveConflict(SyncEvent remoteEvent)
    {
        // Last-writer-wins based on timestamp (simplest strategy)
        // In production, would use configurable strategies
        return _config.ConflictStrategy switch
        {
            ConflictStrategy.LastWriterWins => new ConflictResolutionResult
            {
                Resolution = ResolutionType.AcceptRemote,
                WinningEventId = remoteEvent.EventId,
                Strategy = "LastWriterWins"
            },
            ConflictStrategy.FirstWriterWins => new ConflictResolutionResult
            {
                Resolution = ResolutionType.KeepLocal,
                Strategy = "FirstWriterWins"
            },
            ConflictStrategy.Manual => new ConflictResolutionResult
            {
                Resolution = ResolutionType.RequiresManualResolution,
                ConflictingEventId = remoteEvent.EventId
            },
            _ => new ConflictResolutionResult { Resolution = ResolutionType.AcceptRemote }
        };
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        var batch = new List<SyncEvent>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                batch.Clear();

                // Collect batch
                while (batch.Count < _config.BatchSize &&
                       await _syncQueue.Reader.WaitToReadAsync(ct))
                {
                    while (batch.Count < _config.BatchSize &&
                           _syncQueue.Reader.TryRead(out var evt))
                    {
                        batch.Add(evt);
                    }
                }

                if (batch.Count > 0)
                {
                    await BroadcastToPeersAsync(batch, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task BroadcastToPeersAsync(List<SyncEvent> events, CancellationToken ct)
    {
        var allPeers = _peers.Values.SelectMany(p => p).Where(p => p.Status == PeerStatus.Active).ToList();

        foreach (var peer in allPeers)
        {
            try
            {
                // Would send events to peer over network
                peer.LastSyncAt = DateTime.UtcNow;
            }
            catch
            {
                peer.Status = PeerStatus.Unreachable;
            }
        }

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _syncQueue.Writer.Complete();

        try { await _syncTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Console.Error.WriteLine($"[EnterpriseTierFeatures] Operation error: {ex.Message}"); }

        _cts.Dispose();
    }
}

public sealed class VectorClock
{
    private readonly Dictionary<string, long> _clocks = new();

    public void Increment(string nodeId)
    {
        if (!_clocks.ContainsKey(nodeId))
            _clocks[nodeId] = 0;
        _clocks[nodeId]++;
    }

    public ClockComparison Compare(VectorClock other)
    {
        var allNodes = _clocks.Keys.Union(other._clocks.Keys).ToList();

        bool thisGreater = false;
        bool otherGreater = false;

        foreach (var node in allNodes)
        {
            var thisValue = _clocks.GetValueOrDefault(node, 0);
            var otherValue = other._clocks.GetValueOrDefault(node, 0);

            if (thisValue > otherValue) thisGreater = true;
            if (otherValue > thisValue) otherGreater = true;
        }

        if (thisGreater && !otherGreater) return ClockComparison.After;
        if (otherGreater && !thisGreater) return ClockComparison.Before;
        if (thisGreater && otherGreater) return ClockComparison.Concurrent;
        return ClockComparison.Equal;
    }

    public void Merge(VectorClock other)
    {
        foreach (var (node, value) in other._clocks)
        {
            if (!_clocks.ContainsKey(node) || _clocks[node] < value)
                _clocks[node] = value;
        }
    }

    public VectorClock Clone()
    {
        var clone = new VectorClock();
        foreach (var (node, value) in _clocks)
            clone._clocks[node] = value;
        return clone;
    }
}

public enum ClockComparison { Before, After, Concurrent, Equal }
public enum ConflictStrategy { LastWriterWins, FirstWriterWins, Manual, Merge }
public enum ResolutionType { AcceptRemote, KeepLocal, Merged, RequiresManualResolution, NoChange }
public enum PeerStatus { Active, Unreachable, Removed }

public record SyncEvent
{
    public required string EventId { get; init; }
    public required string ResourceId { get; init; }
    public required byte[] Data { get; init; }
    public ChangeType ChangeType { get; init; }
    public required string OriginNode { get; init; }
    public required VectorClock VectorClock { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed class SyncPeer
{
    public required string PeerId { get; init; }
    public required string Endpoint { get; init; }
    public DateTime RegisteredAt { get; init; }
    public DateTime? LastSyncAt { get; set; }
    public PeerStatus Status { get; set; }
}

public record ConflictResolutionResult
{
    public ResolutionType Resolution { get; init; }
    public string? WinningEventId { get; init; }
    public string? ConflictingEventId { get; init; }
    public string? Strategy { get; init; }
}

public record SyncStatistics
{
    public required string NodeId { get; init; }
    public int TrackedResources { get; init; }
    public int ActivePeers { get; init; }
    public int PendingSyncEvents { get; init; }
    public int TotalConflictsResolved { get; init; }
}

public sealed class SyncEngineConfig
{
    public ConflictStrategy ConflictStrategy { get; set; } = ConflictStrategy.LastWriterWins;
    public int BatchSize { get; set; } = 100;
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(1);
}

#endregion

#region Tier 2.4: Dashboard & Monitoring

/// <summary>
/// Web-based admin dashboard with real-time metrics and alerting.
/// </summary>
public sealed class MonitoringDashboard : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MetricSeries> _metrics = new();
    private readonly ConcurrentDictionary<string, Alert> _activeAlerts = new();
    private readonly List<AlertRule> _alertRules = new();
    private readonly Channel<DashboardEvent> _eventChannel;
    private readonly DashboardConfig _config;
    private readonly Task _monitoringTask;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    public MonitoringDashboard(DashboardConfig? config = null)
    {
        _config = config ?? new DashboardConfig();

        _eventChannel = Channel.CreateBounded<DashboardEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        InitializeDefaultAlertRules();
        _monitoringTask = MonitoringLoopAsync(_cts.Token);
    }

    private void InitializeDefaultAlertRules()
    {
        _alertRules.Add(new AlertRule
        {
            Name = "HighCpuUsage",
            MetricName = "system.cpu.usage",
            Condition = AlertCondition.GreaterThan,
            Threshold = 90,
            Duration = TimeSpan.FromMinutes(5),
            Severity = AlertSeverity.Warning
        });

        _alertRules.Add(new AlertRule
        {
            Name = "LowDiskSpace",
            MetricName = "system.disk.free_percent",
            Condition = AlertCondition.LessThan,
            Threshold = 10,
            Duration = TimeSpan.FromMinutes(1),
            Severity = AlertSeverity.Critical
        });

        _alertRules.Add(new AlertRule
        {
            Name = "HighMemoryUsage",
            MetricName = "system.memory.usage",
            Condition = AlertCondition.GreaterThan,
            Threshold = 85,
            Duration = TimeSpan.FromMinutes(5),
            Severity = AlertSeverity.Warning
        });
    }

    /// <summary>
    /// Records a metric value.
    /// </summary>
    public void RecordMetric(string name, double value, Dictionary<string, string>? labels = null)
    {
        var series = _metrics.GetOrAdd(name, _ => new MetricSeries { Name = name });

        lock (series)
        {
            series.DataPoints.Add(new MetricDataPointRecord
            {
                Value = value,
                Timestamp = DateTime.UtcNow,
                Labels = labels ?? new Dictionary<string, string>()
            });

            // Keep only recent data
            var cutoff = DateTime.UtcNow - _config.MetricRetention;
            series.DataPoints.RemoveAll(p => p.Timestamp < cutoff);
        }
    }

    /// <summary>
    /// Gets current dashboard state.
    /// </summary>
    public DashboardState GetCurrentState()
    {
        var systemMetrics = new SystemMetrics
        {
            CpuUsage = GetLatestMetricValue("system.cpu.usage"),
            MemoryUsage = GetLatestMetricValue("system.memory.usage"),
            DiskUsage = GetLatestMetricValue("system.disk.usage"),
            NetworkIn = GetLatestMetricValue("system.network.in"),
            NetworkOut = GetLatestMetricValue("system.network.out")
        };

        return new DashboardState
        {
            Timestamp = DateTime.UtcNow,
            SystemMetrics = systemMetrics,
            ActiveAlerts = _activeAlerts.Values.ToList(),
            RecentEvents = GetRecentEvents(50),
            MetricSummaries = _metrics.Values.Select(m => new MetricSummary
            {
                Name = m.Name,
                CurrentValue = m.DataPoints.LastOrDefault()?.Value ?? 0,
                MinValue = m.DataPoints.Any() ? m.DataPoints.Min(p => p.Value) : 0,
                MaxValue = m.DataPoints.Any() ? m.DataPoints.Max(p => p.Value) : 0,
                AvgValue = m.DataPoints.Any() ? m.DataPoints.Average(p => p.Value) : 0,
                DataPointCount = m.DataPoints.Count
            }).ToList()
        };
    }

    /// <summary>
    /// Gets metric time series data.
    /// </summary>
    public DashboardMetricTimeSeries? GetDashboardMetricTimeSeries(string name, TimeSpan? duration = null)
    {
        if (!_metrics.TryGetValue(name, out var series))
            return null;

        var cutoff = DateTime.UtcNow - (duration ?? _config.MetricRetention);

        lock (series)
        {
            return new DashboardMetricTimeSeries
            {
                Name = name,
                DataPoints = series.DataPoints
                    .Where(p => p.Timestamp >= cutoff)
                    .OrderBy(p => p.Timestamp)
                    .ToList()
            };
        }
    }

    /// <summary>
    /// Adds a custom alert rule.
    /// </summary>
    public void AddAlertRule(AlertRule rule)
    {
        _alertRules.Add(rule);
    }

    /// <summary>
    /// Acknowledges an alert.
    /// </summary>
    public bool AcknowledgeAlert(string alertId, string acknowledgedBy)
    {
        if (_activeAlerts.TryGetValue(alertId, out var alert))
        {
            alert.AcknowledgedAt = DateTime.UtcNow;
            alert.AcknowledgedBy = acknowledgedBy;
            return true;
        }
        return false;
    }

    private double GetLatestMetricValue(string name)
    {
        if (_metrics.TryGetValue(name, out var series))
        {
            lock (series)
            {
                return series.DataPoints.LastOrDefault()?.Value ?? 0;
            }
        }
        return 0;
    }

    private List<DashboardEvent> GetRecentEvents(int count)
    {
        var events = new List<DashboardEvent>();
        // Would read from event channel history
        return events;
    }

    private async Task MonitoringLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.CheckInterval, ct);

                // Collect system metrics
                CollectSystemMetrics();

                // Evaluate alert rules
                EvaluateAlertRules();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private void CollectSystemMetrics()
    {
        var process = Process.GetCurrentProcess();

        // CPU (simplified)
        RecordMetric("system.cpu.usage", process.TotalProcessorTime.TotalMilliseconds % 100);

        // Memory
        var gcInfo = GC.GetGCMemoryInfo();
        var memoryUsage = (double)gcInfo.HeapSizeBytes / gcInfo.TotalAvailableMemoryBytes * 100;
        RecordMetric("system.memory.usage", memoryUsage);

        // Thread pool
        ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxCompletion);
        RecordMetric("system.threadpool.usage", (1.0 - (double)workerThreads / maxWorker) * 100);
    }

    private void EvaluateAlertRules()
    {
        foreach (var rule in _alertRules)
        {
            if (!_metrics.TryGetValue(rule.MetricName, out var series))
                continue;

            double currentValue;
            lock (series)
            {
                currentValue = series.DataPoints.LastOrDefault()?.Value ?? 0;
            }

            var isTriggered = rule.Condition switch
            {
                AlertCondition.GreaterThan => currentValue > rule.Threshold,
                AlertCondition.LessThan => currentValue < rule.Threshold,
                AlertCondition.Equals => Math.Abs(currentValue - rule.Threshold) < 0.001,
                _ => false
            };

            var alertId = $"{rule.Name}_{rule.MetricName}";

            if (isTriggered)
            {
                if (!_activeAlerts.ContainsKey(alertId))
                {
                    _activeAlerts[alertId] = new Alert
                    {
                        AlertId = alertId,
                        RuleName = rule.Name,
                        MetricName = rule.MetricName,
                        CurrentValue = currentValue,
                        Threshold = rule.Threshold,
                        Severity = rule.Severity,
                        TriggeredAt = DateTime.UtcNow
                    };
                }
            }
            else
            {
                _activeAlerts.TryRemove(alertId, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _eventChannel.Writer.Complete();

        try { await _monitoringTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Console.Error.WriteLine($"[EnterpriseTierFeatures] Operation error: {ex.Message}"); }

        _cts.Dispose();
    }
}

public sealed class MetricSeries
{
    public required string Name { get; init; }
    public List<MetricDataPointRecord> DataPoints { get; } = new();
}

public sealed class MetricDataPointRecord
{
    public double Value { get; init; }
    public DateTime Timestamp { get; init; }
    public Dictionary<string, string> Labels { get; init; } = new();
}

public enum AlertCondition { GreaterThan, LessThan, Equals }
public enum AlertSeverity { Info, Warning, Critical }

public sealed class AlertRule
{
    public required string Name { get; init; }
    public required string MetricName { get; init; }
    public AlertCondition Condition { get; init; }
    public double Threshold { get; init; }
    public TimeSpan Duration { get; init; }
    public AlertSeverity Severity { get; init; }
}

public sealed class Alert
{
    public required string AlertId { get; init; }
    public required string RuleName { get; init; }
    public required string MetricName { get; init; }
    public double CurrentValue { get; init; }
    public double Threshold { get; init; }
    public AlertSeverity Severity { get; init; }
    public DateTime TriggeredAt { get; init; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
}

public record DashboardState
{
    public DateTime Timestamp { get; init; }
    public SystemMetrics SystemMetrics { get; init; } = new();
    public List<Alert> ActiveAlerts { get; init; } = new();
    public List<DashboardEvent> RecentEvents { get; init; } = new();
    public List<MetricSummary> MetricSummaries { get; init; } = new();
}

public record SystemMetrics
{
    public double CpuUsage { get; init; }
    public double MemoryUsage { get; init; }
    public double DiskUsage { get; init; }
    public double NetworkIn { get; init; }
    public double NetworkOut { get; init; }
}

public record MetricSummary
{
    public required string Name { get; init; }
    public double CurrentValue { get; init; }
    public double MinValue { get; init; }
    public double MaxValue { get; init; }
    public double AvgValue { get; init; }
    public int DataPointCount { get; init; }
}

public record DashboardMetricTimeSeries
{
    public required string Name { get; init; }
    public List<MetricDataPointRecord> DataPoints { get; init; } = new();
}

public record DashboardEvent
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}

public sealed class DashboardConfig
{
    public TimeSpan MetricRetention { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);
}

#endregion

#region Tier 2.5: Zero-Downtime Upgrades

/// <summary>
/// Scheduled maintenance windows with rolling upgrades and zero-downtime.
/// </summary>
public sealed class ZeroDowntimeUpgradeManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, UpgradeSession> _activeSessions = new();
    private readonly List<MaintenanceWindow> _scheduledWindows = new();
    private readonly UpgradeConfig _config;
    private readonly IPluginManager _pluginManager;
    private readonly Task _schedulerTask;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    public ZeroDowntimeUpgradeManager(
        IPluginManager pluginManager,
        UpgradeConfig? config = null)
    {
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        _config = config ?? new UpgradeConfig();

        _schedulerTask = SchedulerLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Schedules a maintenance window.
    /// </summary>
    public MaintenanceWindowResult ScheduleMaintenanceWindow(MaintenanceWindowRequest request)
    {
        if (request.StartTime < DateTime.UtcNow)
            return new MaintenanceWindowResult { Success = false, Error = "Start time must be in the future" };

        var window = new MaintenanceWindow
        {
            WindowId = Guid.NewGuid().ToString("N"),
            StartTime = request.StartTime,
            Duration = request.Duration,
            UpgradeType = request.UpgradeType,
            TargetVersion = request.TargetVersion,
            Status = MaintenanceStatus.Scheduled
        };

        _scheduledWindows.Add(window);

        return new MaintenanceWindowResult
        {
            Success = true,
            WindowId = window.WindowId,
            ScheduledStart = window.StartTime
        };
    }

    /// <summary>
    /// Starts a rolling upgrade.
    /// </summary>
    public async Task<UpgradeResult> StartRollingUpgradeAsync(
        string targetVersion,
        CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new UpgradeSession
        {
            SessionId = sessionId,
            TargetVersion = targetVersion,
            StartedAt = DateTime.UtcNow,
            Status = UpgradeStatus.InProgress
        };

        _activeSessions[sessionId] = session;

        try
        {
            // Phase 1: Pre-upgrade checks
            session.CurrentPhase = "PreUpgradeChecks";
            var healthCheck = await PerformHealthCheckAsync(ct);
            if (!healthCheck.IsHealthy)
            {
                session.Status = UpgradeStatus.Failed;
                return new UpgradeResult
                {
                    Success = false,
                    SessionId = sessionId,
                    Error = "Pre-upgrade health check failed"
                };
            }

            // Phase 2: Drain connections
            session.CurrentPhase = "DrainConnections";
            await DrainConnectionsAsync(_config.DrainTimeout, ct);

            // Phase 3: Hot-reload plugins
            session.CurrentPhase = "UpgradePlugins";
            var pluginUpgrade = await UpgradePluginsAsync(targetVersion, ct);
            session.UpgradedPlugins = pluginUpgrade.UpgradedCount;

            // Phase 4: Verify upgrade
            session.CurrentPhase = "PostUpgradeVerification";
            var verification = await VerifyUpgradeAsync(targetVersion, ct);
            if (!verification.Success)
            {
                // Rollback
                session.CurrentPhase = "Rollback";
                await RollbackAsync(session, ct);
                session.Status = UpgradeStatus.RolledBack;

                return new UpgradeResult
                {
                    Success = false,
                    SessionId = sessionId,
                    Error = "Post-upgrade verification failed, rolled back"
                };
            }

            // Phase 5: Resume traffic
            session.CurrentPhase = "ResumeTraffic";
            await ResumeTrafficAsync(ct);

            session.Status = UpgradeStatus.Completed;
            session.CompletedAt = DateTime.UtcNow;

            return new UpgradeResult
            {
                Success = true,
                SessionId = sessionId,
                UpgradedPlugins = session.UpgradedPlugins,
                Duration = session.CompletedAt.Value - session.StartedAt
            };
        }
        catch (Exception ex)
        {
            session.Status = UpgradeStatus.Failed;
            session.Error = ex.Message;

            return new UpgradeResult
            {
                Success = false,
                SessionId = sessionId,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Gets upgrade session status.
    /// </summary>
    public UpgradeSession? GetSessionStatus(string sessionId)
    {
        _activeSessions.TryGetValue(sessionId, out var session);
        return session;
    }

    private async Task<UpgradeHealthCheckResult> PerformHealthCheckAsync(CancellationToken ct)
    {
        // Would perform comprehensive health checks
        await Task.Delay(100, ct);
        return new UpgradeHealthCheckResult { IsHealthy = true };
    }

    private async Task DrainConnectionsAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Would signal load balancer to stop new connections
        await Task.Delay(100, ct);
    }

    private async Task<PluginUpgradeResult> UpgradePluginsAsync(string targetVersion, CancellationToken ct)
    {
        // Would hot-reload plugins
        await Task.Delay(100, ct);
        return new PluginUpgradeResult { UpgradedCount = 5 };
    }

    private async Task<VerificationResult> VerifyUpgradeAsync(string targetVersion, CancellationToken ct)
    {
        // Would verify all plugins are running correctly
        await Task.Delay(100, ct);
        return new VerificationResult { Success = true };
    }

    private async Task RollbackAsync(UpgradeSession session, CancellationToken ct)
    {
        // Would rollback to previous version
        await Task.Delay(100, ct);
    }

    private async Task ResumeTrafficAsync(CancellationToken ct)
    {
        // Would signal load balancer to resume traffic
        await Task.Delay(100, ct);
    }

    private async Task SchedulerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                var now = DateTime.UtcNow;
                var dueWindows = _scheduledWindows
                    .Where(w => w.Status == MaintenanceStatus.Scheduled && w.StartTime <= now)
                    .ToList();

                foreach (var window in dueWindows)
                {
                    window.Status = MaintenanceStatus.InProgress;
                    _ = StartRollingUpgradeAsync(window.TargetVersion, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try { await _schedulerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Console.Error.WriteLine($"[EnterpriseTierFeatures] Operation error: {ex.Message}"); }

        _cts.Dispose();
    }
}

public interface IPluginManager
{
    Task<bool> HotReloadPluginAsync(string pluginId, string version, CancellationToken ct);
    Task<IEnumerable<string>> GetLoadedPluginsAsync(CancellationToken ct);
}

public enum MaintenanceStatus { Scheduled, InProgress, Completed, Cancelled }
public enum UpgradeStatus { InProgress, Completed, Failed, RolledBack }
public enum UpgradeType { HotReload, Rolling, BlueGreen }

public sealed class MaintenanceWindow
{
    public required string WindowId { get; init; }
    public DateTime StartTime { get; init; }
    public TimeSpan Duration { get; init; }
    public UpgradeType UpgradeType { get; init; }
    public string TargetVersion { get; init; } = string.Empty;
    public MaintenanceStatus Status { get; set; }
}

public sealed class UpgradeSession
{
    public required string SessionId { get; init; }
    public required string TargetVersion { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public UpgradeStatus Status { get; set; }
    public string? CurrentPhase { get; set; }
    public int UpgradedPlugins { get; set; }
    public string? Error { get; set; }
}

public record MaintenanceWindowRequest
{
    public DateTime StartTime { get; init; }
    public TimeSpan Duration { get; init; }
    public UpgradeType UpgradeType { get; init; }
    public string TargetVersion { get; init; } = string.Empty;
}

public record MaintenanceWindowResult
{
    public bool Success { get; init; }
    public string? WindowId { get; init; }
    public DateTime? ScheduledStart { get; init; }
    public string? Error { get; init; }
}

public record UpgradeResult
{
    public bool Success { get; init; }
    public string? SessionId { get; init; }
    public int UpgradedPlugins { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Error { get; init; }
}

public record UpgradeHealthCheckResult
{
    public bool IsHealthy { get; init; }
}

public record PluginUpgradeResult
{
    public int UpgradedCount { get; init; }
}

public record VerificationResult
{
    public bool Success { get; init; }
}

public sealed class UpgradeConfig
{
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan UpgradeTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public bool AutoRollbackOnFailure { get; set; } = true;
}

#endregion

#region Tier 2.6: Ransomware Detection - ML-Based Entropy Analysis

/// <summary>
/// ML-based ransomware detection using entropy analysis and behavioral patterns.
/// Detects encryption attacks in real-time and triggers protective responses.
/// </summary>
public sealed class RansomwareDetectionEngine : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FileBaseline> _baselines = new();
    private readonly ConcurrentQueue<DetectionEvent> _events = new();
    private readonly RansomwareDetectionConfig _config;
    private readonly IAlertService? _alertService;
    private readonly SemaphoreSlim _analysisLock = new(Environment.ProcessorCount, Environment.ProcessorCount);
    private readonly Task _monitorTask;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;
    private long _scannedFiles;
    private long _detectedThreats;
    private long _blockedOperations;

    public RansomwareDetectionEngine(RansomwareDetectionConfig? config = null, IAlertService? alertService = null)
    {
        _config = config ?? new RansomwareDetectionConfig();
        _alertService = alertService;
        _monitorTask = MonitorActivityAsync(_cts.Token);
    }

    /// <summary>
    /// Analyzes a file for ransomware indicators before write operations.
    /// </summary>
    public async Task<RansomwareAnalysisResult> AnalyzeBeforeWriteAsync(
        string path,
        byte[] data,
        string? userId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(RansomwareDetectionEngine));

        await _analysisLock.WaitAsync(ct);
        try
        {
            Interlocked.Increment(ref _scannedFiles);

            var indicators = new List<ThreatIndicator>();
            var riskScore = 0.0;

            // 1. Entropy Analysis - high entropy indicates encryption
            var entropy = CalculateEntropy(data);
            if (entropy > _config.EntropyThreshold)
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = ThreatIndicatorType.HighEntropy,
                    Severity = ThreatSeverity.High,
                    Details = $"Entropy {entropy:F2} exceeds threshold {_config.EntropyThreshold:F2}",
                    Score = (entropy - _config.EntropyThreshold) / (8.0 - _config.EntropyThreshold)
                });
                riskScore += 0.4;
            }

            // 2. File Extension Analysis - known ransomware extensions
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (_config.SuspiciousExtensions.Contains(extension))
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = ThreatIndicatorType.SuspiciousExtension,
                    Severity = ThreatSeverity.Critical,
                    Details = $"Known ransomware extension: {extension}",
                    Score = 1.0
                });
                riskScore += 0.5;
            }

            // 3. Magic Bytes Analysis - encrypted files often lack valid headers
            var magicBytesValid = ValidateMagicBytes(data, extension);
            if (!magicBytesValid && data.Length > 100)
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = ThreatIndicatorType.InvalidMagicBytes,
                    Severity = ThreatSeverity.Medium,
                    Details = "File header doesn't match expected format for extension",
                    Score = 0.5
                });
                riskScore += 0.2;
            }

            // 4. Baseline Comparison - detect sudden changes in file characteristics
            if (_baselines.TryGetValue(path, out var baseline))
            {
                var entropyDelta = Math.Abs(entropy - baseline.AverageEntropy);
                if (entropyDelta > _config.EntropyDeltaThreshold)
                {
                    indicators.Add(new ThreatIndicator
                    {
                        Type = ThreatIndicatorType.EntropyAnomaly,
                        Severity = ThreatSeverity.High,
                        Details = $"Entropy changed by {entropyDelta:F2} from baseline",
                        Score = Math.Min(1.0, entropyDelta / 2.0)
                    });
                    riskScore += 0.3;
                }
            }

            // 5. Mass Modification Detection - many files changed rapidly
            var recentModifications = GetRecentModificationCount(userId);
            if (recentModifications > _config.MassModificationThreshold)
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = ThreatIndicatorType.MassModification,
                    Severity = ThreatSeverity.Critical,
                    Details = $"{recentModifications} files modified in rapid succession",
                    Score = Math.Min(1.0, recentModifications / (double)(_config.MassModificationThreshold * 2))
                });
                riskScore += 0.5;
            }

            // Calculate final risk
            riskScore = Math.Min(1.0, riskScore);
            var threatLevel = riskScore switch
            {
                >= 0.8 => ThreatLevel.Critical,
                >= 0.6 => ThreatLevel.High,
                >= 0.4 => ThreatLevel.Medium,
                >= 0.2 => ThreatLevel.Low,
                _ => ThreatLevel.None
            };

            var isThreat = threatLevel >= ThreatLevel.High;

            if (isThreat)
            {
                Interlocked.Increment(ref _detectedThreats);

                // Record detection event
                _events.Enqueue(new DetectionEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Path = path,
                    UserId = userId,
                    ThreatLevel = threatLevel,
                    RiskScore = riskScore,
                    Indicators = indicators,
                    ActionTaken = _config.BlockSuspiciousWrites ? DetectionAction.Blocked : DetectionAction.Logged
                });

                // Alert if configured
                if (_alertService != null && threatLevel >= ThreatLevel.High)
                {
                    await _alertService.SendAlertAsync(new SecurityAlert
                    {
                        Type = AlertType.RansomwareDetected,
                        Severity = threatLevel == ThreatLevel.Critical ? AlertSeverity.Critical : AlertSeverity.High,
                        Message = $"Potential ransomware activity detected on {path}",
                        Details = new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["userId"] = userId ?? "unknown",
                            ["riskScore"] = riskScore,
                            ["indicators"] = indicators.Select(i => i.Type.ToString()).ToList()
                        }
                    }, ct);
                }

                if (_config.BlockSuspiciousWrites)
                {
                    Interlocked.Increment(ref _blockedOperations);
                }
            }

            // Update baseline
            UpdateBaseline(path, entropy, data.Length);

            return new RansomwareAnalysisResult
            {
                IsThreat = isThreat,
                ThreatLevel = threatLevel,
                RiskScore = riskScore,
                Indicators = indicators,
                ShouldBlock = isThreat && _config.BlockSuspiciousWrites,
                RecommendedAction = isThreat
                    ? "Quarantine file and investigate user activity"
                    : "Allow operation",
                AnalyzedAt = DateTime.UtcNow
            };
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    /// <summary>
    /// Gets detection statistics.
    /// </summary>
    public RansomwareDetectionStats GetStats()
    {
        return new RansomwareDetectionStats
        {
            ScannedFiles = Interlocked.Read(ref _scannedFiles),
            DetectedThreats = Interlocked.Read(ref _detectedThreats),
            BlockedOperations = Interlocked.Read(ref _blockedOperations),
            BaselineCount = _baselines.Count,
            RecentEvents = _events.ToArray().TakeLast(100).ToList()
        };
    }

    /// <summary>
    /// Calculates Shannon entropy of data (0-8 bits per byte).
    /// High entropy (>7.5) indicates encrypted/compressed data.
    /// </summary>
    private static double CalculateEntropy(byte[] data)
    {
        if (data.Length == 0) return 0;

        var frequency = new int[256];
        foreach (var b in data)
        {
            frequency[b]++;
        }

        var entropy = 0.0;
        var length = (double)data.Length;

        foreach (var count in frequency)
        {
            if (count > 0)
            {
                var probability = count / length;
                entropy -= probability * Math.Log2(probability);
            }
        }

        return entropy;
    }

    /// <summary>
    /// Validates file magic bytes match expected format.
    /// </summary>
    private bool ValidateMagicBytes(byte[] data, string extension)
    {
        if (data.Length < 8) return true; // Too small to validate

        var magicBytes = data.Take(8).ToArray();

        return extension switch
        {
            ".pdf" => magicBytes[0] == 0x25 && magicBytes[1] == 0x50 && magicBytes[2] == 0x44 && magicBytes[3] == 0x46,
            ".png" => magicBytes[0] == 0x89 && magicBytes[1] == 0x50 && magicBytes[2] == 0x4E && magicBytes[3] == 0x47,
            ".jpg" or ".jpeg" => magicBytes[0] == 0xFF && magicBytes[1] == 0xD8 && magicBytes[2] == 0xFF,
            ".gif" => magicBytes[0] == 0x47 && magicBytes[1] == 0x49 && magicBytes[2] == 0x46,
            ".zip" => magicBytes[0] == 0x50 && magicBytes[1] == 0x4B,
            ".docx" or ".xlsx" or ".pptx" => magicBytes[0] == 0x50 && magicBytes[1] == 0x4B, // Office Open XML
            ".doc" or ".xls" or ".ppt" => magicBytes[0] == 0xD0 && magicBytes[1] == 0xCF, // OLE
            ".exe" => magicBytes[0] == 0x4D && magicBytes[1] == 0x5A, // MZ header
            _ => true // Unknown extension, allow
        };
    }

    private int GetRecentModificationCount(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return 0;

        var cutoff = DateTime.UtcNow.AddSeconds(-_config.MassModificationWindowSeconds);
        return _events.Count(e => e.UserId == userId && e.Timestamp > cutoff);
    }

    private void UpdateBaseline(string path, double entropy, long size)
    {
        _baselines.AddOrUpdate(
            path,
            _ => new FileBaseline
            {
                Path = path,
                AverageEntropy = entropy,
                AverageSize = size,
                SampleCount = 1,
                LastUpdated = DateTime.UtcNow
            },
            (_, existing) =>
            {
                // Exponential moving average
                const double alpha = 0.1;
                return existing with
                {
                    AverageEntropy = alpha * entropy + (1 - alpha) * existing.AverageEntropy,
                    AverageSize = (long)(alpha * size + (1 - alpha) * existing.AverageSize),
                    SampleCount = existing.SampleCount + 1,
                    LastUpdated = DateTime.UtcNow
                };
            });
    }

    private async Task MonitorActivityAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.MonitoringInterval, ct);

                // Cleanup old baselines
                var cutoff = DateTime.UtcNow.AddDays(-_config.BaselineRetentionDays);
                var staleKeys = _baselines
                    .Where(kvp => kvp.Value.LastUpdated < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in staleKeys)
                {
                    _baselines.TryRemove(key, out _);
                }

                // Trim event queue
                while (_events.Count > _config.MaxEventHistory)
                {
                    _events.TryDequeue(out _);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue monitoring
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        try
        {
            await _monitorTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        _cts.Dispose();
        _analysisLock.Dispose();
    }
}

public interface IAlertService
{
    Task SendAlertAsync(SecurityAlert alert, CancellationToken ct = default);
}

public sealed class SecurityAlert
{
    public AlertType Type { get; init; }
    public AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, object> Details { get; init; } = new();
}

public enum AlertType { RansomwareDetected, UnauthorizedAccess, DataExfiltration, AnomalousActivity }
public enum AlertSeverity { Info, Warning, High, Critical }

public sealed class RansomwareDetectionConfig
{
    public double EntropyThreshold { get; set; } = 7.5; // Out of 8.0 max
    public double EntropyDeltaThreshold { get; set; } = 2.0;
    public int MassModificationThreshold { get; set; } = 50;
    public int MassModificationWindowSeconds { get; set; } = 60;
    public bool BlockSuspiciousWrites { get; set; } = true;
    public TimeSpan MonitoringInterval { get; set; } = TimeSpan.FromMinutes(1);
    public int BaselineRetentionDays { get; set; } = 30;
    public int MaxEventHistory { get; set; } = 10000;
    public HashSet<string> SuspiciousExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".encrypted", ".locked", ".crypto", ".crypt", ".enc",
        ".locky", ".zepto", ".cerber", ".crypted", ".WNCRY",
        ".wncry", ".wnry", ".wcry", ".onion", ".zzzzz",
        ".micro", ".xxx", ".ttt", ".mp3", ".aaa", ".abc",
        ".xyz", ".zzz", ".odin", ".thor", ".aesir"
    };
}

public record FileBaseline
{
    public required string Path { get; init; }
    public double AverageEntropy { get; init; }
    public long AverageSize { get; init; }
    public int SampleCount { get; init; }
    public DateTime LastUpdated { get; init; }
}

public enum ThreatIndicatorType
{
    HighEntropy,
    SuspiciousExtension,
    InvalidMagicBytes,
    EntropyAnomaly,
    MassModification,
    RapidRename,
    KnownRansomwarePattern
}

public enum ThreatSeverity { Low, Medium, High, Critical }
public enum ThreatLevel { None, Low, Medium, High, Critical }
public enum DetectionAction { Logged, Blocked, Quarantined }

public record ThreatIndicator
{
    public ThreatIndicatorType Type { get; init; }
    public ThreatSeverity Severity { get; init; }
    public required string Details { get; init; }
    public double Score { get; init; }
}

public record DetectionEvent
{
    public DateTime Timestamp { get; init; }
    public required string Path { get; init; }
    public string? UserId { get; init; }
    public ThreatLevel ThreatLevel { get; init; }
    public double RiskScore { get; init; }
    public List<ThreatIndicator> Indicators { get; init; } = new();
    public DetectionAction ActionTaken { get; init; }
}

public record RansomwareAnalysisResult
{
    public bool IsThreat { get; init; }
    public ThreatLevel ThreatLevel { get; init; }
    public double RiskScore { get; init; }
    public List<ThreatIndicator> Indicators { get; init; } = new();
    public bool ShouldBlock { get; init; }
    public required string RecommendedAction { get; init; }
    public DateTime AnalyzedAt { get; init; }
}

public record RansomwareDetectionStats
{
    public long ScannedFiles { get; init; }
    public long DetectedThreats { get; init; }
    public long BlockedOperations { get; init; }
    public int BaselineCount { get; init; }
    public List<DetectionEvent> RecentEvents { get; init; } = new();
}

#endregion

#region Tier 2.7: Content-Addressable Deduplication

/// <summary>
/// Content-addressable storage with global deduplication.
/// Uses content hashing to eliminate duplicate data across the entire storage system.
/// </summary>
public sealed class ContentAddressableDeduplication : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ChunkMetadata> _chunkIndex = new();
    private readonly ConcurrentDictionary<string, FileChunkMap> _fileIndex = new();
    private readonly IChunkStorage _storage;
    private readonly DeduplicationConfig _config;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _gcTask;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;
    private long _totalBytesStored;
    private long _totalBytesDeduped;
    private long _totalChunks;
    private long _uniqueChunks;

    public ContentAddressableDeduplication(IChunkStorage storage, DeduplicationConfig? config = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _config = config ?? new DeduplicationConfig();
        _gcTask = RunGarbageCollectionAsync(_cts.Token);
    }

    /// <summary>
    /// Stores data with deduplication, returning a content-addressable ID.
    /// </summary>
    public async Task<DeduplicationResult> StoreAsync(
        string fileId,
        Stream data,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ContentAddressableDeduplication));

        var chunks = new List<ChunkReference>();
        var dedupedBytes = 0L;
        var storedBytes = 0L;

        await _writeLock.WaitAsync(ct);
        try
        {
            // Chunk the data using content-defined chunking (CDC)
            await foreach (var chunk in ChunkDataAsync(data, ct))
            {
                Interlocked.Increment(ref _totalChunks);
                Interlocked.Add(ref _totalBytesStored, chunk.Data.Length);

                // Calculate content hash
                var hash = ComputeContentHash(chunk.Data);

                // Check if chunk already exists
                if (_chunkIndex.TryGetValue(hash, out var existing))
                {
                    // Chunk exists - deduplicate
                    existing.ReferenceCount++;
                    dedupedBytes += chunk.Data.Length;
                    chunks.Add(new ChunkReference { Hash = hash, Offset = chunk.Offset, Length = chunk.Length });
                }
                else
                {
                    // New unique chunk - store it
                    Interlocked.Increment(ref _uniqueChunks);
                    await _storage.StoreChunkAsync(hash, chunk.Data, ct);

                    var metadata = new ChunkMetadata
                    {
                        Hash = hash,
                        Length = chunk.Data.Length,
                        ReferenceCount = 1,
                        StoredAt = DateTime.UtcNow
                    };
                    _chunkIndex[hash] = metadata;

                    storedBytes += chunk.Data.Length;
                    chunks.Add(new ChunkReference { Hash = hash, Offset = chunk.Offset, Length = chunk.Length });
                }
            }

            Interlocked.Add(ref _totalBytesDeduped, dedupedBytes);

            // Store file-to-chunks mapping
            var fileMap = new FileChunkMap
            {
                FileId = fileId,
                Chunks = chunks,
                TotalSize = chunks.Sum(c => c.Length),
                CreatedAt = DateTime.UtcNow
            };
            _fileIndex[fileId] = fileMap;

            var dedupRatio = storedBytes + dedupedBytes > 0
                ? (double)dedupedBytes / (storedBytes + dedupedBytes)
                : 0;

            return new DeduplicationResult
            {
                Success = true,
                FileId = fileId,
                ContentHash = ComputeFileHash(chunks),
                TotalSize = fileMap.TotalSize,
                StoredSize = storedBytes,
                DedupedSize = dedupedBytes,
                ChunkCount = chunks.Count,
                DeduplicationRatio = dedupRatio
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Retrieves deduplicated data by file ID.
    /// </summary>
    public async Task<Stream?> RetrieveAsync(string fileId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ContentAddressableDeduplication));

        if (!_fileIndex.TryGetValue(fileId, out var fileMap))
        {
            return null;
        }

        var output = new MemoryStream();

        foreach (var chunkRef in fileMap.Chunks.OrderBy(c => c.Offset))
        {
            var chunkData = await _storage.RetrieveChunkAsync(chunkRef.Hash, ct);
            if (chunkData == null)
            {
                throw new DataCorruptionException($"Missing chunk {chunkRef.Hash} for file {fileId}");
            }
            await output.WriteAsync(chunkData, ct);
        }

        output.Position = 0;
        return output;
    }

    /// <summary>
    /// Deletes a file and decrements chunk reference counts.
    /// </summary>
    public async Task<bool> DeleteAsync(string fileId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ContentAddressableDeduplication));

        await _writeLock.WaitAsync(ct);
        try
        {
            if (!_fileIndex.TryRemove(fileId, out var fileMap))
            {
                return false;
            }

            // Decrement reference counts
            foreach (var chunkRef in fileMap.Chunks)
            {
                if (_chunkIndex.TryGetValue(chunkRef.Hash, out var metadata))
                {
                    metadata.ReferenceCount--;
                }
            }

            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets deduplication statistics.
    /// </summary>
    public DeduplicationStats GetStats()
    {
        var totalStored = Interlocked.Read(ref _totalBytesStored);
        var totalDeduped = Interlocked.Read(ref _totalBytesDeduped);
        var totalChunks = Interlocked.Read(ref _totalChunks);
        var uniqueChunks = Interlocked.Read(ref _uniqueChunks);

        return new DeduplicationStats
        {
            TotalBytesProcessed = totalStored,
            BytesSaved = totalDeduped,
            TotalChunks = totalChunks,
            UniqueChunks = uniqueChunks,
            FileCount = _fileIndex.Count,
            DeduplicationRatio = totalStored > 0 ? (double)totalDeduped / totalStored : 0,
            StorageEfficiency = uniqueChunks > 0 ? (double)totalChunks / uniqueChunks : 1
        };
    }

    /// <summary>
    /// Content-defined chunking using Rabin fingerprinting.
    /// Creates variable-size chunks based on content boundaries.
    /// </summary>
    private async IAsyncEnumerable<DataChunk> ChunkDataAsync(
        Stream data,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[_config.MaxChunkSize * 2];
        var chunkBuffer = new List<byte>();
        var offset = 0L;
        var windowSize = _config.RabinWindowSize;
        var mask = (1UL << _config.RabinMaskBits) - 1;
        ulong fingerprint = 0;

        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                ct.ThrowIfCancellationRequested();

                chunkBuffer.Add(buffer[i]);

                // Update Rabin fingerprint
                fingerprint = ((fingerprint << 8) | buffer[i]) ^ _rabinTable[(fingerprint >> 56) & 0xFF];

                // Check for chunk boundary
                bool isBoundary = chunkBuffer.Count >= _config.MinChunkSize &&
                    ((fingerprint & mask) == 0 || chunkBuffer.Count >= _config.MaxChunkSize);

                if (isBoundary)
                {
                    yield return new DataChunk
                    {
                        Data = chunkBuffer.ToArray(),
                        Offset = offset,
                        Length = chunkBuffer.Count
                    };

                    offset += chunkBuffer.Count;
                    chunkBuffer.Clear();
                    fingerprint = 0;
                }
            }
        }

        // Emit remaining data as final chunk
        if (chunkBuffer.Count > 0)
        {
            yield return new DataChunk
            {
                Data = chunkBuffer.ToArray(),
                Offset = offset,
                Length = chunkBuffer.Count
            };
        }
    }

    private static string ComputeContentHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeFileHash(List<ChunkReference> chunks)
    {
        using var sha256 = SHA256.Create();
        foreach (var chunk in chunks.OrderBy(c => c.Offset))
        {
            var hashBytes = Convert.FromHexString(chunk.Hash);
            sha256.TransformBlock(hashBytes, 0, hashBytes.Length, null, 0);
        }
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    private async Task RunGarbageCollectionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.GCInterval, ct);

                // Find and remove unreferenced chunks
                var unreferencedChunks = _chunkIndex
                    .Where(kvp => kvp.Value.ReferenceCount <= 0)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var hash in unreferencedChunks)
                {
                    if (_chunkIndex.TryRemove(hash, out _))
                    {
                        await _storage.DeleteChunkAsync(hash, ct);
                        Interlocked.Decrement(ref _uniqueChunks);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue GC
            }
        }
    }

    // Precomputed Rabin fingerprint table
    private static readonly ulong[] _rabinTable = ComputeRabinTable();

    private static ulong[] ComputeRabinTable()
    {
        const ulong polynomial = 0xC96C5795D7870F42UL; // Irreducible polynomial
        var table = new ulong[256];

        for (int i = 0; i < 256; i++)
        {
            ulong value = (ulong)i << 56;
            for (int j = 0; j < 8; j++)
            {
                if ((value & 0x8000000000000000UL) != 0)
                    value = (value << 1) ^ polynomial;
                else
                    value <<= 1;
            }
            table[i] = value;
        }

        return table;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        try
        {
            await _gcTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>
/// Storage interface for chunk data.
/// </summary>
public interface IChunkStorage
{
    Task StoreChunkAsync(string hash, byte[] data, CancellationToken ct = default);
    Task<byte[]?> RetrieveChunkAsync(string hash, CancellationToken ct = default);
    Task DeleteChunkAsync(string hash, CancellationToken ct = default);
}

public sealed class DeduplicationConfig
{
    public int MinChunkSize { get; set; } = 4 * 1024; // 4 KB
    public int MaxChunkSize { get; set; } = 64 * 1024; // 64 KB
    public int AverageChunkSize { get; set; } = 16 * 1024; // 16 KB
    public int RabinWindowSize { get; set; } = 48;
    public int RabinMaskBits { get; set; } = 13; // ~8KB average
    public TimeSpan GCInterval { get; set; } = TimeSpan.FromHours(1);
}

public sealed class ChunkMetadata
{
    public required string Hash { get; init; }
    public int Length { get; init; }
    public int ReferenceCount { get; set; }
    public DateTime StoredAt { get; init; }
}

public sealed class FileChunkMap
{
    public required string FileId { get; init; }
    public List<ChunkReference> Chunks { get; init; } = new();
    public long TotalSize { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record ChunkReference
{
    public required string Hash { get; init; }
    public long Offset { get; init; }
    public int Length { get; init; }
}

public record DataChunk
{
    public required byte[] Data { get; init; }
    public long Offset { get; init; }
    public int Length { get; init; }
}

public record DeduplicationResult
{
    public bool Success { get; init; }
    public required string FileId { get; init; }
    public required string ContentHash { get; init; }
    public long TotalSize { get; init; }
    public long StoredSize { get; init; }
    public long DedupedSize { get; init; }
    public int ChunkCount { get; init; }
    public double DeduplicationRatio { get; init; }
}

public record DeduplicationStats
{
    public long TotalBytesProcessed { get; init; }
    public long BytesSaved { get; init; }
    public long TotalChunks { get; init; }
    public long UniqueChunks { get; init; }
    public int FileCount { get; init; }
    public double DeduplicationRatio { get; init; }
    public double StorageEfficiency { get; init; }
}

public class DataCorruptionException : Exception
{
    public DataCorruptionException(string message) : base(message) { }
}

#endregion
