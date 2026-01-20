namespace DataWarehouse.SDK.Federation;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Permissions that can be granted via capability tokens.
/// </summary>
[Flags]
public enum CapabilityPermissions
{
    /// <summary>No permissions.</summary>
    None = 0,
    /// <summary>Read object content.</summary>
    Read = 1 << 0,
    /// <summary>Write/modify object content.</summary>
    Write = 1 << 1,
    /// <summary>Delete object.</summary>
    Delete = 1 << 2,
    /// <summary>List objects in container.</summary>
    List = 1 << 3,
    /// <summary>Read object metadata.</summary>
    ReadMetadata = 1 << 4,
    /// <summary>Modify object metadata.</summary>
    WriteMetadata = 1 << 5,
    /// <summary>Create new objects.</summary>
    Create = 1 << 6,
    /// <summary>Share/delegate capabilities to others.</summary>
    Share = 1 << 7,
    /// <summary>Revoke shared capabilities.</summary>
    Revoke = 1 << 8,
    /// <summary>Administer object (change owner, etc).</summary>
    Admin = 1 << 9,
    /// <summary>Execute (for code objects).</summary>
    Execute = 1 << 10,
    /// <summary>Replicate object to other nodes.</summary>
    Replicate = 1 << 11,
    /// <summary>Standard reader permissions.</summary>
    Reader = Read | ReadMetadata | List,
    /// <summary>Standard writer permissions.</summary>
    Writer = Reader | Write | WriteMetadata | Create,
    /// <summary>Owner permissions (all except Admin).</summary>
    Owner = Writer | Delete | Share | Revoke | Replicate,
    /// <summary>Full permissions.</summary>
    Full = Owner | Admin | Execute
}

/// <summary>
/// Type of capability token.
/// </summary>
public enum CapabilityType
{
    /// <summary>Access to a specific object.</summary>
    Object = 0,
    /// <summary>Access to a container/namespace.</summary>
    Container = 1,
    /// <summary>Access to a node's resources.</summary>
    Node = 2,
    /// <summary>Federation-wide capability.</summary>
    Federation = 3,
    /// <summary>Wildcard capability (pattern-based).</summary>
    Wildcard = 4
}

/// <summary>
/// Constraints on capability usage.
/// </summary>
public sealed class CapabilityConstraints
{
    /// <summary>Token not valid before this time.</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>Token expires at this time.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Maximum number of uses (null = unlimited).</summary>
    public int? MaxUses { get; set; }

    /// <summary>Allowed source node IDs (empty = any).</summary>
    public HashSet<string> AllowedNodes { get; set; } = new();

    /// <summary>Allowed geographic regions (empty = any).</summary>
    public HashSet<string> AllowedRegions { get; set; } = new();

    /// <summary>Allowed IP ranges in CIDR notation (empty = any).</summary>
    public HashSet<string> AllowedIpRanges { get; set; } = new();

    /// <summary>Device/client identifiers that can use this token (empty = any).</summary>
    public HashSet<string> AllowedDevices { get; set; } = new();

    /// <summary>Allowed storage pool IDs (empty = any).</summary>
    public HashSet<string> AllowedPools { get; set; } = new();

    /// <summary>Allowed group IDs (empty = any).</summary>
    public HashSet<string> AllowedGroups { get; set; } = new();

    /// <summary>Minimum required encryption level.</summary>
    public string? RequiredEncryption { get; set; }

    /// <summary>Whether the capability can be delegated.</summary>
    public bool AllowDelegation { get; set; } = true;

    /// <summary>Maximum delegation depth (0 = no further delegation).</summary>
    public int MaxDelegationDepth { get; set; } = 3;

    /// <summary>Custom constraints as key-value pairs.</summary>
    public Dictionary<string, string> Custom { get; set; } = new();

    /// <summary>
    /// Checks if constraints are satisfied.
    /// </summary>
    public ConstraintValidation Validate(CapabilityContext context)
    {
        var now = DateTimeOffset.UtcNow;

        if (NotBefore.HasValue && now < NotBefore.Value)
            return ConstraintValidation.Failed("Token not yet valid");

        if (ExpiresAt.HasValue && now > ExpiresAt.Value)
            return ConstraintValidation.Failed("Token expired");

        if (AllowedNodes.Count > 0 && !string.IsNullOrEmpty(context.RequestingNodeId) &&
            !AllowedNodes.Contains(context.RequestingNodeId))
            return ConstraintValidation.Failed("Node not allowed");

        if (AllowedRegions.Count > 0 && !string.IsNullOrEmpty(context.Region) &&
            !AllowedRegions.Contains(context.Region))
            return ConstraintValidation.Failed("Region not allowed");

        if (AllowedDevices.Count > 0 && !string.IsNullOrEmpty(context.DeviceId) &&
            !AllowedDevices.Contains(context.DeviceId))
            return ConstraintValidation.Failed("Device not allowed");

        if (AllowedPools.Count > 0 && !string.IsNullOrEmpty(context.PoolId) &&
            !AllowedPools.Contains(context.PoolId))
            return ConstraintValidation.Failed("Pool not allowed");

        if (AllowedGroups.Count > 0 && context.GroupMemberships.Count > 0 &&
            !AllowedGroups.Overlaps(context.GroupMemberships))
            return ConstraintValidation.Failed("Group not allowed");

        return ConstraintValidation.Success();
    }
}

/// <summary>
/// Context for validating capability constraints.
/// </summary>
public sealed class CapabilityContext
{
    /// <summary>ID of the node making the request.</summary>
    public string? RequestingNodeId { get; set; }

    /// <summary>Geographic region of the request.</summary>
    public string? Region { get; set; }

    /// <summary>IP address of the requester.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Device identifier.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Storage pool ID being accessed.</summary>
    public string? PoolId { get; set; }

    /// <summary>Groups the requester is a member of.</summary>
    public HashSet<string> GroupMemberships { get; set; } = new();

    /// <summary>Current timestamp.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Additional context data.</summary>
    public Dictionary<string, string> Extra { get; set; } = new();
}

/// <summary>
/// Result of constraint validation.
/// </summary>
public readonly struct ConstraintValidation
{
    /// <summary>Whether validation succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>Error message if validation failed.</summary>
    public string? ErrorMessage { get; }

    private ConstraintValidation(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static ConstraintValidation Success() => new(true, null);
    public static ConstraintValidation Failed(string message) => new(false, message);
}

/// <summary>
/// A capability token granting access to resources.
/// </summary>
public sealed class CapabilityToken
{
    /// <summary>Unique token identifier.</summary>
    public string TokenId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Type of capability.</summary>
    public CapabilityType Type { get; init; } = CapabilityType.Object;

    /// <summary>Target resource ID (ObjectId, ContainerId, NodeId, or pattern).</summary>
    public string TargetId { get; init; } = string.Empty;

    /// <summary>Granted permissions.</summary>
    public CapabilityPermissions Permissions { get; init; } = CapabilityPermissions.None;

    /// <summary>Node that issued this capability.</summary>
    public string IssuerId { get; init; } = string.Empty;

    /// <summary>Node/user that holds this capability.</summary>
    public string HolderId { get; init; } = string.Empty;

    /// <summary>When this capability was issued.</summary>
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Usage constraints.</summary>
    public CapabilityConstraints Constraints { get; init; } = new();

    /// <summary>Parent token ID if delegated.</summary>
    public string? ParentTokenId { get; set; }

    /// <summary>Current delegation depth.</summary>
    public int DelegationDepth { get; set; } = 0;

    /// <summary>Chain of issuer node IDs (for delegation tracking).</summary>
    public List<string> DelegationChain { get; set; } = new();

    /// <summary>Cryptographic signature from the issuer.</summary>
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    /// <summary>Number of times this token has been used.</summary>
    public int UseCount { get; set; } = 0;

    /// <summary>Whether this token has been revoked.</summary>
    public bool IsRevoked { get; set; } = false;

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Gets the bytes to sign for this token.
    /// </summary>
    public byte[] GetSignableBytes()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(TokenId);
        writer.Write((int)Type);
        writer.Write(TargetId);
        writer.Write((int)Permissions);
        writer.Write(IssuerId);
        writer.Write(HolderId);
        writer.Write(IssuedAt.ToUnixTimeMilliseconds());
        writer.Write(ParentTokenId ?? string.Empty);
        writer.Write(DelegationDepth);

        // Include constraint info
        if (Constraints.NotBefore.HasValue)
            writer.Write(Constraints.NotBefore.Value.ToUnixTimeMilliseconds());
        if (Constraints.ExpiresAt.HasValue)
            writer.Write(Constraints.ExpiresAt.Value.ToUnixTimeMilliseconds());

        return ms.ToArray();
    }

    /// <summary>
    /// Checks if this token grants a specific permission.
    /// </summary>
    public bool HasPermission(CapabilityPermissions permission) =>
        (Permissions & permission) == permission;

    /// <summary>
    /// Checks if this token is valid for use.
    /// </summary>
    public CapabilityValidation Validate(CapabilityContext context)
    {
        if (IsRevoked)
            return CapabilityValidation.Failed("Token has been revoked");

        if (Constraints.MaxUses.HasValue && UseCount >= Constraints.MaxUses.Value)
            return CapabilityValidation.Failed("Maximum uses exceeded");

        var constraintResult = Constraints.Validate(context);
        if (!constraintResult.IsValid)
            return CapabilityValidation.Failed(constraintResult.ErrorMessage!);

        return CapabilityValidation.Success(this);
    }

    /// <summary>
    /// Serializes the token to JSON.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, CapabilityJsonContext.Default.CapabilityToken);

    /// <summary>
    /// Deserializes a token from JSON.
    /// </summary>
    public static CapabilityToken? FromJson(string json) =>
        JsonSerializer.Deserialize(json, CapabilityJsonContext.Default.CapabilityToken);
}

/// <summary>
/// Result of capability validation.
/// </summary>
public readonly struct CapabilityValidation
{
    /// <summary>Whether the capability is valid.</summary>
    public bool IsValid { get; }

    /// <summary>The validated token if successful.</summary>
    public CapabilityToken? Token { get; }

    /// <summary>Error message if validation failed.</summary>
    public string? ErrorMessage { get; }

    private CapabilityValidation(bool isValid, CapabilityToken? token, string? errorMessage)
    {
        IsValid = isValid;
        Token = token;
        ErrorMessage = errorMessage;
    }

    public static CapabilityValidation Success(CapabilityToken token) => new(true, token, null);
    public static CapabilityValidation Failed(string message) => new(false, null, message);
}

/// <summary>
/// Issues capability tokens.
/// </summary>
public sealed class CapabilityIssuer
{
    private readonly NodeIdentityManager _identityManager;

    public CapabilityIssuer(NodeIdentityManager identityManager)
    {
        _identityManager = identityManager;
    }

    /// <summary>
    /// Issues a new capability token.
    /// </summary>
    public CapabilityToken Issue(
        CapabilityType type,
        string targetId,
        CapabilityPermissions permissions,
        string holderId,
        CapabilityConstraints? constraints = null)
    {
        var localId = _identityManager.LocalIdentity.Id.ToHex();

        var token = new CapabilityToken
        {
            Type = type,
            TargetId = targetId,
            Permissions = permissions,
            IssuerId = localId,
            HolderId = holderId,
            IssuedAt = DateTimeOffset.UtcNow,
            Constraints = constraints ?? new CapabilityConstraints(),
            DelegationChain = new List<string> { localId }
        };

        token.Signature = _identityManager.Sign(token.GetSignableBytes());
        return token;
    }

    /// <summary>
    /// Issues a delegated capability from an existing token.
    /// </summary>
    public CapabilityToken? Delegate(
        CapabilityToken parentToken,
        string newHolderId,
        CapabilityPermissions? restrictedPermissions = null,
        CapabilityConstraints? additionalConstraints = null)
    {
        // Check if delegation is allowed
        if (!parentToken.Constraints.AllowDelegation)
            return null;

        if (parentToken.DelegationDepth >= parentToken.Constraints.MaxDelegationDepth)
            return null;

        // Permissions can only be reduced, not expanded
        var permissions = restrictedPermissions.HasValue
            ? parentToken.Permissions & restrictedPermissions.Value
            : parentToken.Permissions;

        var localId = _identityManager.LocalIdentity.Id.ToHex();

        // Merge constraints (more restrictive wins)
        var constraints = MergeConstraints(parentToken.Constraints, additionalConstraints);

        var delegatedToken = new CapabilityToken
        {
            Type = parentToken.Type,
            TargetId = parentToken.TargetId,
            Permissions = permissions,
            IssuerId = localId,
            HolderId = newHolderId,
            IssuedAt = DateTimeOffset.UtcNow,
            Constraints = constraints,
            ParentTokenId = parentToken.TokenId,
            DelegationDepth = parentToken.DelegationDepth + 1,
            DelegationChain = new List<string>(parentToken.DelegationChain) { localId }
        };

        delegatedToken.Signature = _identityManager.Sign(delegatedToken.GetSignableBytes());
        return delegatedToken;
    }

    private CapabilityConstraints MergeConstraints(
        CapabilityConstraints parent,
        CapabilityConstraints? additional)
    {
        if (additional == null)
            return parent;

        var merged = new CapabilityConstraints
        {
            // Use more restrictive time bounds
            NotBefore = Max(parent.NotBefore, additional.NotBefore),
            ExpiresAt = Min(parent.ExpiresAt, additional.ExpiresAt),

            // Use more restrictive usage limit
            MaxUses = Min(parent.MaxUses, additional.MaxUses),

            // Intersect allowed sets (more restrictive)
            AllowedNodes = Intersect(parent.AllowedNodes, additional.AllowedNodes),
            AllowedRegions = Intersect(parent.AllowedRegions, additional.AllowedRegions),
            AllowedDevices = Intersect(parent.AllowedDevices, additional.AllowedDevices),
            AllowedIpRanges = Intersect(parent.AllowedIpRanges, additional.AllowedIpRanges),

            // Use most restrictive encryption
            RequiredEncryption = additional.RequiredEncryption ?? parent.RequiredEncryption,

            // Delegation only if both allow
            AllowDelegation = parent.AllowDelegation && additional.AllowDelegation,
            MaxDelegationDepth = Math.Min(parent.MaxDelegationDepth, additional.MaxDelegationDepth)
        };

        // Merge custom constraints
        foreach (var kvp in parent.Custom)
            merged.Custom[kvp.Key] = kvp.Value;
        foreach (var kvp in additional.Custom)
            merged.Custom[kvp.Key] = kvp.Value;

        return merged;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (!a.HasValue) return b;
        if (!b.HasValue) return a;
        return a.Value > b.Value ? a : b;
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (!a.HasValue) return b;
        if (!b.HasValue) return a;
        return a.Value < b.Value ? a : b;
    }

    private static int? Min(int? a, int? b)
    {
        if (!a.HasValue) return b;
        if (!b.HasValue) return a;
        return Math.Min(a.Value, b.Value);
    }

    private static HashSet<string> Intersect(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0) return new HashSet<string>(b);
        if (b.Count == 0) return new HashSet<string>(a);
        return new HashSet<string>(a.Intersect(b));
    }
}

/// <summary>
/// Verifies capability tokens.
/// </summary>
public sealed class CapabilityVerifier
{
    private readonly NodeRegistry _nodeRegistry;
    private readonly CapabilityStore _tokenStore;

    public CapabilityVerifier(NodeRegistry nodeRegistry, CapabilityStore tokenStore)
    {
        _nodeRegistry = nodeRegistry;
        _tokenStore = tokenStore;
    }

    /// <summary>
    /// Verifies a capability token and its chain.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(
        CapabilityToken token,
        CapabilityContext context,
        CancellationToken ct = default)
    {
        // Check if revoked
        if (await _tokenStore.IsRevokedAsync(token.TokenId, ct))
            return VerificationResult.Failed("Token has been revoked");

        // Validate constraints
        var validation = token.Validate(context);
        if (!validation.IsValid)
            return VerificationResult.Failed(validation.ErrorMessage!);

        // Verify signature chain
        return await VerifySignatureChainAsync(token, ct);
    }

    private async Task<VerificationResult> VerifySignatureChainAsync(
        CapabilityToken token,
        CancellationToken ct)
    {
        // Get issuer node
        var issuerNode = _nodeRegistry.GetNode(NodeId.FromHex(token.IssuerId));
        if (issuerNode == null)
            return VerificationResult.Failed($"Unknown issuer: {token.IssuerId}");

        // Verify signature
        using var keyPair = NodeKeyPair.ImportPublicKey(issuerNode.PublicKey);
        if (!keyPair.Verify(token.GetSignableBytes(), token.Signature))
            return VerificationResult.Failed("Invalid signature");

        // If delegated, verify parent chain
        if (!string.IsNullOrEmpty(token.ParentTokenId))
        {
            var parentToken = await _tokenStore.GetTokenAsync(token.ParentTokenId, ct);
            if (parentToken == null)
                return VerificationResult.Failed("Parent token not found");

            if (await _tokenStore.IsRevokedAsync(parentToken.TokenId, ct))
                return VerificationResult.Failed("Parent token has been revoked");

            // Verify holder of parent token is issuer of this token
            if (parentToken.HolderId != token.IssuerId)
                return VerificationResult.Failed("Delegation chain broken: holder mismatch");

            // Permissions must be subset
            if ((token.Permissions & ~parentToken.Permissions) != CapabilityPermissions.None)
                return VerificationResult.Failed("Delegated token has permissions not in parent");

            // Recursively verify parent (up to max depth)
            if (token.DelegationDepth > 10)
                return VerificationResult.Failed("Delegation chain too deep");

            var parentResult = await VerifySignatureChainAsync(parentToken, ct);
            if (!parentResult.IsValid)
                return VerificationResult.Failed($"Parent verification failed: {parentResult.ErrorMessage}");
        }

        return VerificationResult.Success(token);
    }

    /// <summary>
    /// Quick check if a token grants specific access.
    /// </summary>
    public async Task<bool> CanAccessAsync(
        CapabilityToken token,
        string targetId,
        CapabilityPermissions requiredPermissions,
        CapabilityContext context,
        CancellationToken ct = default)
    {
        // Check target matches
        if (token.TargetId != targetId && token.Type != CapabilityType.Wildcard)
            return false;

        // Check permissions
        if (!token.HasPermission(requiredPermissions))
            return false;

        // Full verification
        var result = await VerifyAsync(token, context, ct);
        return result.IsValid;
    }
}

/// <summary>
/// Result of token verification.
/// </summary>
public readonly struct VerificationResult
{
    /// <summary>Whether verification succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>The verified token if successful.</summary>
    public CapabilityToken? Token { get; }

    /// <summary>Error message if verification failed.</summary>
    public string? ErrorMessage { get; }

    private VerificationResult(bool isValid, CapabilityToken? token, string? errorMessage)
    {
        IsValid = isValid;
        Token = token;
        ErrorMessage = errorMessage;
    }

    public static VerificationResult Success(CapabilityToken token) => new(true, token, null);
    public static VerificationResult Failed(string message) => new(false, null, message);
}

/// <summary>
/// Storage for capability tokens.
/// </summary>
public sealed class CapabilityStore
{
    private readonly Dictionary<string, CapabilityToken> _tokens = new();
    private readonly HashSet<string> _revokedTokens = new();
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Stores a token.
    /// </summary>
    public Task StoreTokenAsync(CapabilityToken token, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            _tokens[token.TokenId] = token;
        }
        finally { _lock.ExitWriteLock(); }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a token by ID.
    /// </summary>
    public Task<CapabilityToken?> GetTokenAsync(string tokenId, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            _tokens.TryGetValue(tokenId, out var token);
            return Task.FromResult(token);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Revokes a token and all its delegated children.
    /// </summary>
    public Task RevokeTokenAsync(string tokenId, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            _revokedTokens.Add(tokenId);

            if (_tokens.TryGetValue(tokenId, out var token))
                token.IsRevoked = true;

            // Revoke all tokens delegated from this one
            foreach (var t in _tokens.Values)
            {
                if (t.ParentTokenId == tokenId || t.DelegationChain.Contains(tokenId))
                {
                    _revokedTokens.Add(t.TokenId);
                    t.IsRevoked = true;
                }
            }
        }
        finally { _lock.ExitWriteLock(); }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks if a token is revoked.
    /// </summary>
    public Task<bool> IsRevokedAsync(string tokenId, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            return Task.FromResult(_revokedTokens.Contains(tokenId));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Gets all tokens for a holder.
    /// </summary>
    public Task<IReadOnlyList<CapabilityToken>> GetTokensForHolderAsync(
        string holderId,
        CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            var tokens = _tokens.Values
                .Where(t => t.HolderId == holderId && !t.IsRevoked)
                .ToList();
            return Task.FromResult<IReadOnlyList<CapabilityToken>>(tokens);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Gets all tokens for a target.
    /// </summary>
    public Task<IReadOnlyList<CapabilityToken>> GetTokensForTargetAsync(
        string targetId,
        CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            var tokens = _tokens.Values
                .Where(t => t.TargetId == targetId && !t.IsRevoked)
                .ToList();
            return Task.FromResult<IReadOnlyList<CapabilityToken>>(tokens);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Increments the use count for a token.
    /// </summary>
    public Task IncrementUseCountAsync(string tokenId, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_tokens.TryGetValue(tokenId, out var token))
                token.UseCount++;
        }
        finally { _lock.ExitWriteLock(); }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up expired tokens.
    /// </summary>
    public Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;

        _lock.EnterWriteLock();
        try
        {
            var expired = _tokens.Values
                .Where(t => t.Constraints.ExpiresAt.HasValue && t.Constraints.ExpiresAt.Value < now)
                .Select(t => t.TokenId)
                .ToList();

            foreach (var tokenId in expired)
            {
                _tokens.Remove(tokenId);
                removed++;
            }
        }
        finally { _lock.ExitWriteLock(); }

        return Task.FromResult(removed);
    }
}

/// <summary>
/// JSON serialization context for capabilities.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityToken))]
[JsonSerializable(typeof(CapabilityConstraints))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class CapabilityJsonContext : JsonSerializerContext
{
}
