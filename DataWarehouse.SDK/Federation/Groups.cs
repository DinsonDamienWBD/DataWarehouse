namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A group identifier.
/// </summary>
public readonly struct GroupId : IEquatable<GroupId>, IComparable<GroupId>
{
    private readonly string _id;

    public static GroupId Empty { get; } = new(string.Empty);

    public string Value => _id ?? string.Empty;
    public bool IsEmpty => string.IsNullOrEmpty(_id);

    public GroupId(string id)
    {
        _id = id ?? string.Empty;
    }

    public static GroupId NewId() => new(Guid.NewGuid().ToString("N"));
    public static GroupId Parse(string id) => new(id);

    public override string ToString() => _id ?? string.Empty;
    public override int GetHashCode() => (_id ?? string.Empty).GetHashCode();
    public override bool Equals(object? obj) => obj is GroupId other && Equals(other);
    public bool Equals(GroupId other) => _id == other._id;
    public int CompareTo(GroupId other) => string.Compare(_id, other._id, StringComparison.Ordinal);

    public static bool operator ==(GroupId left, GroupId right) => left.Equals(right);
    public static bool operator !=(GroupId left, GroupId right) => !left.Equals(right);
}

/// <summary>
/// Type of group.
/// </summary>
public enum GroupType
{
    /// <summary>Standard user group.</summary>
    Standard = 0,
    /// <summary>Role-based group (Admin, Reader, etc.).</summary>
    Role = 1,
    /// <summary>Organization/department group.</summary>
    Organization = 2,
    /// <summary>Project-based group.</summary>
    Project = 3,
    /// <summary>Dynamic group (membership computed).</summary>
    Dynamic = 4
}

/// <summary>
/// A federation group for ACL.
/// </summary>
public sealed class FederationGroup
{
    /// <summary>Unique group identifier.</summary>
    public GroupId Id { get; init; }

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of the group.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Group type.</summary>
    public GroupType Type { get; set; } = GroupType.Standard;

    /// <summary>Direct member node IDs.</summary>
    public HashSet<string> Members { get; set; } = new();

    /// <summary>Nested group IDs (members inherit from these).</summary>
    public HashSet<string> NestedGroups { get; set; } = new();

    /// <summary>Parent group IDs (this group is nested in these).</summary>
    public HashSet<string> ParentGroups { get; set; } = new();

    /// <summary>Node that owns/manages this group.</summary>
    public string OwnerNodeId { get; set; } = string.Empty;

    /// <summary>When this group was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this group was last modified.</summary>
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this group is active.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Checks if a node is a direct member.
    /// </summary>
    public bool HasDirectMember(string nodeId) => Members.Contains(nodeId);

    /// <summary>
    /// Serializes to JSON.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, GroupJsonContext.Default.FederationGroup);

    /// <summary>
    /// Deserializes from JSON.
    /// </summary>
    public static FederationGroup? FromJson(string json) =>
        JsonSerializer.Deserialize(json, GroupJsonContext.Default.FederationGroup);
}

/// <summary>
/// A capability token issued to a group.
/// All members of the group inherit this capability.
/// </summary>
public sealed class GroupCapabilityToken
{
    /// <summary>Unique token identifier.</summary>
    public string TokenId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Group this token is issued to.</summary>
    public string GroupId { get; init; } = string.Empty;

    /// <summary>The underlying capability token.</summary>
    public CapabilityToken Capability { get; init; } = new();

    /// <summary>When this was issued.</summary>
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this group token is revoked.</summary>
    public bool IsRevoked { get; set; }

    /// <summary>
    /// Creates an individual token for a group member.
    /// </summary>
    public CapabilityToken CreateMemberToken(string memberId)
    {
        return new CapabilityToken
        {
            TokenId = $"{TokenId}:{memberId}",
            Type = Capability.Type,
            TargetId = Capability.TargetId,
            Permissions = Capability.Permissions,
            IssuerId = Capability.IssuerId,
            HolderId = memberId,
            IssuedAt = DateTimeOffset.UtcNow,
            Constraints = Capability.Constraints,
            ParentTokenId = TokenId,
            DelegationDepth = Capability.DelegationDepth + 1,
            DelegationChain = new List<string>(Capability.DelegationChain) { $"group:{GroupId}" }
        };
    }
}

/// <summary>
/// Registry for managing federation groups.
/// </summary>
public sealed class GroupRegistry
{
    private readonly ConcurrentDictionary<GroupId, FederationGroup> _groups = new();
    private readonly ConcurrentDictionary<string, GroupCapabilityToken> _groupTokens = new();
    private readonly ConcurrentDictionary<string, HashSet<GroupId>> _membershipCache = new();

    /// <summary>
    /// Registers a new group.
    /// </summary>
    public void Register(FederationGroup group)
    {
        _groups[group.Id] = group;
        InvalidateMembershipCache();
    }

    /// <summary>
    /// Gets a group by ID.
    /// </summary>
    public FederationGroup? GetGroup(GroupId groupId)
    {
        _groups.TryGetValue(groupId, out var group);
        return group;
    }

    /// <summary>
    /// Gets a group by ID string.
    /// </summary>
    public FederationGroup? GetGroup(string groupId) => GetGroup(new GroupId(groupId));

    /// <summary>
    /// Removes a group.
    /// </summary>
    public bool Remove(GroupId groupId)
    {
        var removed = _groups.TryRemove(groupId, out _);
        if (removed)
            InvalidateMembershipCache();
        return removed;
    }

    /// <summary>
    /// Adds a member to a group.
    /// </summary>
    public bool AddMember(GroupId groupId, string memberId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;

        var added = group.Members.Add(memberId);
        if (added)
        {
            group.ModifiedAt = DateTimeOffset.UtcNow;
            InvalidateMembershipCache();
        }
        return added;
    }

    /// <summary>
    /// Removes a member from a group.
    /// </summary>
    public bool RemoveMember(GroupId groupId, string memberId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;

        var removed = group.Members.Remove(memberId);
        if (removed)
        {
            group.ModifiedAt = DateTimeOffset.UtcNow;
            InvalidateMembershipCache();
        }
        return removed;
    }

    /// <summary>
    /// Adds a nested group.
    /// </summary>
    public bool AddNestedGroup(GroupId parentId, GroupId childId)
    {
        if (!_groups.TryGetValue(parentId, out var parent))
            return false;
        if (!_groups.TryGetValue(childId, out var child))
            return false;

        // Check for cycles
        if (WouldCreateCycle(parentId, childId))
            return false;

        parent.NestedGroups.Add(childId.Value);
        child.ParentGroups.Add(parentId.Value);
        parent.ModifiedAt = DateTimeOffset.UtcNow;
        child.ModifiedAt = DateTimeOffset.UtcNow;

        InvalidateMembershipCache();
        return true;
    }

    private bool WouldCreateCycle(GroupId parentId, GroupId childId)
    {
        // Check if parent is already reachable from child
        var visited = new HashSet<GroupId>();
        var queue = new Queue<GroupId>();
        queue.Enqueue(childId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == parentId)
                return true;

            if (!visited.Add(current))
                continue;

            if (_groups.TryGetValue(current, out var group))
            {
                foreach (var nested in group.NestedGroups)
                {
                    queue.Enqueue(new GroupId(nested));
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all groups a member belongs to (including nested).
    /// </summary>
    public IReadOnlySet<GroupId> GetMemberGroups(string memberId)
    {
        if (_membershipCache.TryGetValue(memberId, out var cached))
            return cached;

        var groups = new HashSet<GroupId>();

        // Find direct memberships
        foreach (var (groupId, group) in _groups)
        {
            if (group.Members.Contains(memberId) && group.IsActive)
            {
                groups.Add(groupId);
                // Add all parent groups recursively
                AddParentGroups(groupId, groups);
            }
        }

        _membershipCache[memberId] = groups;
        return groups;
    }

    private void AddParentGroups(GroupId groupId, HashSet<GroupId> result)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return;

        foreach (var parentId in group.ParentGroups)
        {
            var pid = new GroupId(parentId);
            if (result.Add(pid))
            {
                AddParentGroups(pid, result);
            }
        }
    }

    /// <summary>
    /// Gets all members of a group (including nested group members).
    /// </summary>
    public IReadOnlySet<string> GetAllMembers(GroupId groupId)
    {
        var members = new HashSet<string>();
        CollectMembers(groupId, members, new HashSet<GroupId>());
        return members;
    }

    private void CollectMembers(GroupId groupId, HashSet<string> members, HashSet<GroupId> visited)
    {
        if (!visited.Add(groupId))
            return;

        if (!_groups.TryGetValue(groupId, out var group))
            return;

        // Add direct members
        foreach (var member in group.Members)
        {
            members.Add(member);
        }

        // Add nested group members
        foreach (var nestedId in group.NestedGroups)
        {
            CollectMembers(new GroupId(nestedId), members, visited);
        }
    }

    /// <summary>
    /// Checks if a member is in a group (directly or via nesting).
    /// </summary>
    public bool IsMember(string memberId, GroupId groupId)
    {
        var memberGroups = GetMemberGroups(memberId);
        return memberGroups.Contains(groupId);
    }

    /// <summary>
    /// Issues a capability to a group.
    /// </summary>
    public GroupCapabilityToken IssueGroupCapability(
        GroupId groupId,
        CapabilityToken capability)
    {
        var token = new GroupCapabilityToken
        {
            GroupId = groupId.Value,
            Capability = capability
        };

        _groupTokens[token.TokenId] = token;
        return token;
    }

    /// <summary>
    /// Gets capabilities for a member (from their group memberships).
    /// </summary>
    public IReadOnlyList<CapabilityToken> GetMemberCapabilities(string memberId)
    {
        var groups = GetMemberGroups(memberId);
        var capabilities = new List<CapabilityToken>();

        foreach (var token in _groupTokens.Values)
        {
            if (token.IsRevoked)
                continue;

            if (groups.Contains(new GroupId(token.GroupId)))
            {
                capabilities.Add(token.CreateMemberToken(memberId));
            }
        }

        return capabilities;
    }

    /// <summary>
    /// Revokes a group capability token.
    /// </summary>
    public bool RevokeGroupCapability(string tokenId)
    {
        if (_groupTokens.TryGetValue(tokenId, out var token))
        {
            token.IsRevoked = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets all groups.
    /// </summary>
    public IReadOnlyList<FederationGroup> GetAllGroups()
    {
        return _groups.Values.ToList();
    }

    /// <summary>
    /// Gets groups by type.
    /// </summary>
    public IReadOnlyList<FederationGroup> GetGroupsByType(GroupType type)
    {
        return _groups.Values.Where(g => g.Type == type && g.IsActive).ToList();
    }

    private void InvalidateMembershipCache()
    {
        _membershipCache.Clear();
    }
}

/// <summary>
/// Extended capability verifier with group support.
/// </summary>
public sealed class GroupAwareCapabilityVerifier
{
    private readonly CapabilityVerifier _baseVerifier;
    private readonly GroupRegistry _groupRegistry;

    public GroupAwareCapabilityVerifier(
        CapabilityVerifier baseVerifier,
        GroupRegistry groupRegistry)
    {
        _baseVerifier = baseVerifier;
        _groupRegistry = groupRegistry;
    }

    /// <summary>
    /// Verifies a capability with group expansion.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(
        CapabilityToken token,
        CapabilityContext context,
        CancellationToken ct = default)
    {
        // Expand group memberships into context
        if (context.GroupMemberships.Count == 0 && !string.IsNullOrEmpty(context.RequestingNodeId))
        {
            var groups = _groupRegistry.GetMemberGroups(context.RequestingNodeId);
            context.GroupMemberships = groups.Select(g => g.Value).ToHashSet();
        }

        // Verify with base verifier
        return await _baseVerifier.VerifyAsync(token, context, ct);
    }

    /// <summary>
    /// Gets all effective capabilities for a node (individual + group).
    /// </summary>
    public async Task<IReadOnlyList<CapabilityToken>> GetEffectiveCapabilitiesAsync(
        string nodeId,
        CapabilityStore capabilityStore,
        CancellationToken ct = default)
    {
        var capabilities = new List<CapabilityToken>();

        // Get individual capabilities
        var individual = await capabilityStore.GetTokensForHolderAsync(nodeId, ct);
        capabilities.AddRange(individual);

        // Get group capabilities
        var groupCaps = _groupRegistry.GetMemberCapabilities(nodeId);
        capabilities.AddRange(groupCaps);

        return capabilities;
    }
}

/// <summary>
/// Predefined role groups.
/// </summary>
public static class WellKnownGroups
{
    public static FederationGroup CreateAdminGroup(string ownerNodeId) => new()
    {
        Id = GroupId.Parse("admins"),
        Name = "Administrators",
        Description = "Full system access",
        Type = GroupType.Role,
        OwnerNodeId = ownerNodeId
    };

    public static FederationGroup CreateReadersGroup(string ownerNodeId) => new()
    {
        Id = GroupId.Parse("readers"),
        Name = "Readers",
        Description = "Read-only access",
        Type = GroupType.Role,
        OwnerNodeId = ownerNodeId
    };

    public static FederationGroup CreateWritersGroup(string ownerNodeId) => new()
    {
        Id = GroupId.Parse("writers"),
        Name = "Writers",
        Description = "Read and write access",
        Type = GroupType.Role,
        OwnerNodeId = ownerNodeId
    };

    public static FederationGroup CreateGuestsGroup(string ownerNodeId) => new()
    {
        Id = GroupId.Parse("guests"),
        Name = "Guests",
        Description = "Limited guest access",
        Type = GroupType.Role,
        OwnerNodeId = ownerNodeId
    };
}

/// <summary>
/// JSON serialization context for groups.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FederationGroup))]
[JsonSerializable(typeof(GroupCapabilityToken))]
[JsonSerializable(typeof(List<FederationGroup>))]
internal partial class GroupJsonContext : JsonSerializerContext
{
}
