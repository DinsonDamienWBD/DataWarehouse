using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Security;

#region Helper Types

/// <summary>
/// Permission level for intelligence access.
/// </summary>
public enum IntelligencePermissionLevel
{
    /// <summary>No access.</summary>
    None = 0,

    /// <summary>Read-only access.</summary>
    Read = 1,

    /// <summary>Can execute queries but not modify.</summary>
    Query = 2,

    /// <summary>Can modify knowledge and settings.</summary>
    Write = 3,

    /// <summary>Full administrative access.</summary>
    Admin = 4
}

/// <summary>
/// Access decision result.
/// </summary>
public sealed record AccessDecision
{
    /// <summary>Whether access is allowed.</summary>
    public bool Allowed { get; init; }

    /// <summary>Reason for the decision.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Required permission level.</summary>
    public IntelligencePermissionLevel RequiredLevel { get; init; }

    /// <summary>Actual permission level.</summary>
    public IntelligencePermissionLevel ActualLevel { get; init; }

    /// <summary>Additional restrictions applied.</summary>
    public List<string> Restrictions { get; init; } = new();

    /// <summary>Creates an allowed decision.</summary>
    public static AccessDecision Allow(string reason = "Access granted") =>
        new() { Allowed = true, Reason = reason };

    /// <summary>Creates a denied decision.</summary>
    public static AccessDecision Deny(string reason) =>
        new() { Allowed = false, Reason = reason };
}

/// <summary>
/// Rate limit result.
/// </summary>
public sealed record RateLimitResult
{
    /// <summary>Whether the request is allowed.</summary>
    public bool Allowed { get; init; }

    /// <summary>Remaining quota.</summary>
    public long RemainingQuota { get; init; }

    /// <summary>Time until quota resets.</summary>
    public TimeSpan TimeUntilReset { get; init; }

    /// <summary>Current usage count.</summary>
    public long CurrentUsage { get; init; }

    /// <summary>Maximum allowed usage.</summary>
    public long MaxUsage { get; init; }

    /// <summary>Retry after this duration if rate limited.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// Cost tracking entry.
/// </summary>
public sealed record CostEntry
{
    /// <summary>User or organization ID.</summary>
    public string EntityId { get; init; } = "";

    /// <summary>Operation type.</summary>
    public string OperationType { get; init; } = "";

    /// <summary>Token count for this operation.</summary>
    public int TokenCount { get; init; }

    /// <summary>Estimated cost in currency units.</summary>
    public decimal EstimatedCost { get; init; }

    /// <summary>Timestamp of the operation.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Model or provider used.</summary>
    public string ModelId { get; init; } = "";
}

/// <summary>
/// Knowledge domain restriction.
/// </summary>
public sealed record DomainRestriction
{
    /// <summary>Domain identifier.</summary>
    public string Domain { get; init; } = "";

    /// <summary>Whether access is allowed.</summary>
    public bool IsAllowed { get; init; }

    /// <summary>Reason for restriction.</summary>
    public string? Reason { get; init; }

    /// <summary>Start time for restriction.</summary>
    public DateTime? EffectiveFrom { get; init; }

    /// <summary>End time for restriction.</summary>
    public DateTime? EffectiveUntil { get; init; }
}

/// <summary>
/// Throttle status information.
/// </summary>
public sealed record ThrottleStatus
{
    /// <summary>Current throttle factor (1.0 = no throttling).</summary>
    public double ThrottleFactor { get; init; } = 1.0;

    /// <summary>Current system load percentage.</summary>
    public double SystemLoad { get; init; }

    /// <summary>Active request count.</summary>
    public int ActiveRequests { get; init; }

    /// <summary>Maximum concurrent requests.</summary>
    public int MaxConcurrentRequests { get; init; }

    /// <summary>Whether system is under heavy load.</summary>
    public bool IsUnderLoad => SystemLoad > 0.8;

    /// <summary>Recommended delay before retry.</summary>
    public TimeSpan RecommendedDelay { get; init; }
}

#endregion

#region N1: Access Control

/// <summary>
/// Per-instance access control for intelligence strategies.
/// Controls which users/roles can access specific intelligence instances.
/// </summary>
public sealed class InstancePermissions
{
    private readonly BoundedDictionary<string, Dictionary<string, IntelligencePermissionLevel>> _permissions = new BoundedDictionary<string, Dictionary<string, IntelligencePermissionLevel>>(1000);
    private readonly BoundedDictionary<string, IntelligencePermissionLevel> _defaultPermissions = new BoundedDictionary<string, IntelligencePermissionLevel>(1000);
    private readonly object _lockObject = new();

    /// <summary>
    /// Grants permission to a user/role for an intelligence instance.
    /// </summary>
    /// <param name="instanceId">The intelligence instance ID.</param>
    /// <param name="principalId">User or role ID.</param>
    /// <param name="level">Permission level to grant.</param>
    public void Grant(string instanceId, string principalId, IntelligencePermissionLevel level)
    {
        var instancePermissions = _permissions.GetOrAdd(instanceId, _ => new Dictionary<string, IntelligencePermissionLevel>());
        lock (instancePermissions)
        {
            instancePermissions[principalId] = level;
        }
    }

    /// <summary>
    /// Revokes permission from a user/role for an intelligence instance.
    /// </summary>
    /// <param name="instanceId">The intelligence instance ID.</param>
    /// <param name="principalId">User or role ID.</param>
    public void Revoke(string instanceId, string principalId)
    {
        if (_permissions.TryGetValue(instanceId, out var instancePermissions))
        {
            lock (instancePermissions)
            {
                instancePermissions.Remove(principalId);
            }
        }
    }

    /// <summary>
    /// Sets the default permission level for an instance.
    /// </summary>
    /// <param name="instanceId">The intelligence instance ID.</param>
    /// <param name="level">Default permission level.</param>
    public void SetDefault(string instanceId, IntelligencePermissionLevel level)
    {
        _defaultPermissions[instanceId] = level;
    }

    /// <summary>
    /// Checks if a principal has the required permission level.
    /// </summary>
    /// <param name="instanceId">The intelligence instance ID.</param>
    /// <param name="principalId">User or role ID.</param>
    /// <param name="requiredLevel">Required permission level.</param>
    /// <returns>Access decision.</returns>
    public AccessDecision CheckAccess(string instanceId, string principalId, IntelligencePermissionLevel requiredLevel)
    {
        var actualLevel = GetPermissionLevel(instanceId, principalId);

        if (actualLevel >= requiredLevel)
        {
            return new AccessDecision
            {
                Allowed = true,
                Reason = $"Permission granted: {actualLevel} >= {requiredLevel}",
                RequiredLevel = requiredLevel,
                ActualLevel = actualLevel
            };
        }

        return new AccessDecision
        {
            Allowed = false,
            Reason = $"Insufficient permission: {actualLevel} < {requiredLevel}",
            RequiredLevel = requiredLevel,
            ActualLevel = actualLevel
        };
    }

    /// <summary>
    /// Gets the permission level for a principal on an instance.
    /// </summary>
    /// <param name="instanceId">The intelligence instance ID.</param>
    /// <param name="principalId">User or role ID.</param>
    /// <returns>Permission level.</returns>
    public IntelligencePermissionLevel GetPermissionLevel(string instanceId, string principalId)
    {
        if (_permissions.TryGetValue(instanceId, out var instancePermissions))
        {
            lock (instancePermissions)
            {
                if (instancePermissions.TryGetValue(principalId, out var level))
                {
                    return level;
                }
            }
        }

        return _defaultPermissions.GetValueOrDefault(instanceId, IntelligencePermissionLevel.None);
    }

    /// <summary>
    /// Lists all permissions for an instance.
    /// </summary>
    /// <param name="instanceId">The intelligence instance ID.</param>
    /// <returns>Dictionary of principal IDs to permission levels.</returns>
    public Dictionary<string, IntelligencePermissionLevel> ListPermissions(string instanceId)
    {
        if (_permissions.TryGetValue(instanceId, out var instancePermissions))
        {
            lock (instancePermissions)
            {
                return new Dictionary<string, IntelligencePermissionLevel>(instancePermissions);
            }
        }

        return new Dictionary<string, IntelligencePermissionLevel>();
    }
}

/// <summary>
/// Per-user access control for intelligence operations.
/// Tracks user-specific permissions, quotas, and restrictions.
/// </summary>
public sealed class UserPermissions
{
    private readonly BoundedDictionary<string, UserPermissionProfile> _userProfiles = new BoundedDictionary<string, UserPermissionProfile>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _userRoles = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, IntelligencePermissionLevel> _rolePermissions = new BoundedDictionary<string, IntelligencePermissionLevel>(1000);

    /// <summary>
    /// Creates or updates a user permission profile.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="profile">Permission profile.</param>
    public void SetUserProfile(string userId, UserPermissionProfile profile)
    {
        _userProfiles[userId] = profile;
    }

    /// <summary>
    /// Assigns a role to a user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="roleId">Role ID to assign.</param>
    public void AssignRole(string userId, string roleId)
    {
        var roles = _userRoles.GetOrAdd(userId, _ => new HashSet<string>());
        lock (roles)
        {
            roles.Add(roleId);
        }
    }

    /// <summary>
    /// Removes a role from a user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="roleId">Role ID to remove.</param>
    public void RemoveRole(string userId, string roleId)
    {
        if (_userRoles.TryGetValue(userId, out var roles))
        {
            lock (roles)
            {
                roles.Remove(roleId);
            }
        }
    }

    /// <summary>
    /// Sets the permission level for a role.
    /// </summary>
    /// <param name="roleId">Role ID.</param>
    /// <param name="level">Permission level.</param>
    public void SetRolePermission(string roleId, IntelligencePermissionLevel level)
    {
        _rolePermissions[roleId] = level;
    }

    /// <summary>
    /// Gets the effective permission level for a user (combines profile and roles).
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <returns>Highest permission level.</returns>
    public IntelligencePermissionLevel GetEffectivePermission(string userId)
    {
        var maxLevel = IntelligencePermissionLevel.None;

        // Check user profile
        if (_userProfiles.TryGetValue(userId, out var profile))
        {
            maxLevel = profile.BasePermissionLevel;
        }

        // Check roles
        if (_userRoles.TryGetValue(userId, out var roles))
        {
            lock (roles)
            {
                foreach (var roleId in roles)
                {
                    if (_rolePermissions.TryGetValue(roleId, out var roleLevel) && roleLevel > maxLevel)
                    {
                        maxLevel = roleLevel;
                    }
                }
            }
        }

        return maxLevel;
    }

    /// <summary>
    /// Checks if a user can perform an operation.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="operation">Operation to check.</param>
    /// <param name="requiredLevel">Required permission level.</param>
    /// <returns>Access decision.</returns>
    public AccessDecision CheckAccess(string userId, string operation, IntelligencePermissionLevel requiredLevel)
    {
        var profile = _userProfiles.GetValueOrDefault(userId);

        // Check if user is suspended
        if (profile?.IsSuspended == true)
        {
            return AccessDecision.Deny($"User {userId} is suspended: {profile.SuspensionReason}");
        }

        // Check effective permission
        var effectiveLevel = GetEffectivePermission(userId);
        if (effectiveLevel < requiredLevel)
        {
            return new AccessDecision
            {
                Allowed = false,
                Reason = $"Insufficient permission for {operation}",
                RequiredLevel = requiredLevel,
                ActualLevel = effectiveLevel
            };
        }

        // Check operation-specific restrictions
        if (profile?.RestrictedOperations?.Contains(operation) == true)
        {
            return AccessDecision.Deny($"Operation {operation} is restricted for user {userId}");
        }

        return AccessDecision.Allow($"User {userId} authorized for {operation}");
    }

    /// <summary>
    /// Gets a user's permission profile.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <returns>Permission profile or null if not found.</returns>
    public UserPermissionProfile? GetProfile(string userId)
    {
        return _userProfiles.GetValueOrDefault(userId);
    }

    /// <summary>
    /// Suspends a user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="reason">Suspension reason.</param>
    /// <param name="until">Optional suspension end time.</param>
    public void SuspendUser(string userId, string reason, DateTime? until = null)
    {
        var profile = _userProfiles.GetOrAdd(userId, _ => new UserPermissionProfile { UserId = userId });
        _userProfiles[userId] = profile with
        {
            IsSuspended = true,
            SuspensionReason = reason,
            SuspendedUntil = until
        };
    }

    /// <summary>
    /// Reinstates a suspended user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    public void ReinstateUser(string userId)
    {
        if (_userProfiles.TryGetValue(userId, out var profile))
        {
            _userProfiles[userId] = profile with
            {
                IsSuspended = false,
                SuspensionReason = null,
                SuspendedUntil = null
            };
        }
    }
}

/// <summary>
/// User permission profile.
/// </summary>
public sealed record UserPermissionProfile
{
    /// <summary>User ID.</summary>
    public string UserId { get; init; } = "";

    /// <summary>Base permission level.</summary>
    public IntelligencePermissionLevel BasePermissionLevel { get; init; } = IntelligencePermissionLevel.Read;

    /// <summary>Whether the user is suspended.</summary>
    public bool IsSuspended { get; init; }

    /// <summary>Suspension reason if suspended.</summary>
    public string? SuspensionReason { get; init; }

    /// <summary>Suspension end time if temporary.</summary>
    public DateTime? SuspendedUntil { get; init; }

    /// <summary>Operations restricted for this user.</summary>
    public HashSet<string>? RestrictedOperations { get; init; }

    /// <summary>Daily token quota.</summary>
    public long DailyTokenQuota { get; init; } = 100_000;

    /// <summary>Daily query quota.</summary>
    public int DailyQueryQuota { get; init; } = 1000;

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Command whitelist for controlling allowed AI commands per user/role.
/// Prevents unauthorized or dangerous commands from being executed.
/// </summary>
public sealed class CommandWhitelist
{
    private readonly BoundedDictionary<string, HashSet<string>> _userWhitelists = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _roleWhitelists = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly HashSet<string> _globalWhitelist = new();
    private readonly HashSet<string> _globalBlacklist = new();
    private readonly object _lockObject = new();

    /// <summary>
    /// Adds a command to the global whitelist.
    /// </summary>
    /// <param name="command">Command to whitelist.</param>
    public void AddGlobalCommand(string command)
    {
        lock (_lockObject)
        {
            _globalWhitelist.Add(command.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Adds a command to the global blacklist.
    /// </summary>
    /// <param name="command">Command to blacklist.</param>
    public void AddGlobalBlacklist(string command)
    {
        lock (_lockObject)
        {
            _globalBlacklist.Add(command.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Adds a command to a user's whitelist.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="command">Command to whitelist.</param>
    public void AddUserCommand(string userId, string command)
    {
        var userList = _userWhitelists.GetOrAdd(userId, _ => new HashSet<string>());
        lock (userList)
        {
            userList.Add(command.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Adds a command to a role's whitelist.
    /// </summary>
    /// <param name="roleId">Role ID.</param>
    /// <param name="command">Command to whitelist.</param>
    public void AddRoleCommand(string roleId, string command)
    {
        var roleList = _roleWhitelists.GetOrAdd(roleId, _ => new HashSet<string>());
        lock (roleList)
        {
            roleList.Add(command.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Checks if a command is allowed for a user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="command">Command to check.</param>
    /// <param name="userRoles">User's roles.</param>
    /// <returns>Access decision.</returns>
    public AccessDecision IsCommandAllowed(string userId, string command, IEnumerable<string>? userRoles = null)
    {
        var normalizedCommand = command.ToLowerInvariant();

        // Check global blacklist first
        lock (_lockObject)
        {
            if (_globalBlacklist.Contains(normalizedCommand))
            {
                return AccessDecision.Deny($"Command '{command}' is globally blacklisted");
            }
        }

        // Check global whitelist
        lock (_lockObject)
        {
            if (_globalWhitelist.Contains(normalizedCommand))
            {
                return AccessDecision.Allow($"Command '{command}' is globally whitelisted");
            }
        }

        // Check user whitelist
        if (_userWhitelists.TryGetValue(userId, out var userList))
        {
            lock (userList)
            {
                if (userList.Contains(normalizedCommand))
                {
                    return AccessDecision.Allow($"Command '{command}' is whitelisted for user {userId}");
                }
            }
        }

        // Check role whitelists
        if (userRoles != null)
        {
            foreach (var roleId in userRoles)
            {
                if (_roleWhitelists.TryGetValue(roleId, out var roleList))
                {
                    lock (roleList)
                    {
                        if (roleList.Contains(normalizedCommand))
                        {
                            return AccessDecision.Allow($"Command '{command}' is whitelisted for role {roleId}");
                        }
                    }
                }
            }
        }

        return AccessDecision.Deny($"Command '{command}' is not whitelisted");
    }

    /// <summary>
    /// Gets all whitelisted commands for a user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="userRoles">User's roles.</param>
    /// <returns>Set of allowed commands.</returns>
    public HashSet<string> GetAllowedCommands(string userId, IEnumerable<string>? userRoles = null)
    {
        var allowed = new HashSet<string>();

        // Add global whitelist
        lock (_lockObject)
        {
            foreach (var cmd in _globalWhitelist)
            {
                if (!_globalBlacklist.Contains(cmd))
                {
                    allowed.Add(cmd);
                }
            }
        }

        // Add user whitelist
        if (_userWhitelists.TryGetValue(userId, out var userList))
        {
            lock (userList)
            {
                foreach (var cmd in userList)
                {
                    allowed.Add(cmd);
                }
            }
        }

        // Add role whitelists
        if (userRoles != null)
        {
            foreach (var roleId in userRoles)
            {
                if (_roleWhitelists.TryGetValue(roleId, out var roleList))
                {
                    lock (roleList)
                    {
                        foreach (var cmd in roleList)
                        {
                            allowed.Add(cmd);
                        }
                    }
                }
            }
        }

        return allowed;
    }
}

/// <summary>
/// Domain restrictions for knowledge access.
/// Controls which knowledge domains users can access.
/// </summary>
public sealed class DomainRestrictions
{
    private readonly BoundedDictionary<string, List<DomainRestriction>> _userRestrictions = new BoundedDictionary<string, List<DomainRestriction>>(1000);
    private readonly BoundedDictionary<string, List<DomainRestriction>> _globalRestrictions = new BoundedDictionary<string, List<DomainRestriction>>(1000);
    private readonly HashSet<string> _sensitivedomains = new();
    private readonly object _lockObject = new();

    /// <summary>
    /// Marks a domain as sensitive (requires explicit access grant).
    /// </summary>
    /// <param name="domain">Domain to mark as sensitive.</param>
    public void MarkSensitive(string domain)
    {
        lock (_lockObject)
        {
            _sensitivedomains.Add(domain.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Adds a global domain restriction.
    /// </summary>
    /// <param name="restriction">Restriction to add.</param>
    public void AddGlobalRestriction(DomainRestriction restriction)
    {
        var restrictions = _globalRestrictions.GetOrAdd(restriction.Domain.ToLowerInvariant(), _ => new List<DomainRestriction>());
        lock (restrictions)
        {
            restrictions.Add(restriction);
        }
    }

    /// <summary>
    /// Adds a user-specific domain restriction.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="restriction">Restriction to add.</param>
    public void AddUserRestriction(string userId, DomainRestriction restriction)
    {
        var restrictions = _userRestrictions.GetOrAdd($"{userId}:{restriction.Domain.ToLowerInvariant()}", _ => new List<DomainRestriction>());
        lock (restrictions)
        {
            restrictions.Add(restriction);
        }
    }

    /// <summary>
    /// Checks if a user can access a domain.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="domain">Domain to check.</param>
    /// <returns>Access decision.</returns>
    public AccessDecision CheckDomainAccess(string userId, string domain)
    {
        var normalizedDomain = domain.ToLowerInvariant();
        var now = DateTime.UtcNow;

        // Check if domain is sensitive
        bool isSensitive;
        lock (_lockObject)
        {
            isSensitive = _sensitivedomains.Contains(normalizedDomain);
        }

        // Check user-specific restrictions
        if (_userRestrictions.TryGetValue($"{userId}:{normalizedDomain}", out var userRestrictions))
        {
            lock (userRestrictions)
            {
                foreach (var restriction in userRestrictions)
                {
                    if (IsRestrictionActive(restriction, now))
                    {
                        if (!restriction.IsAllowed)
                        {
                            return AccessDecision.Deny(restriction.Reason ?? $"Domain '{domain}' is restricted for user {userId}");
                        }
                        else if (isSensitive)
                        {
                            // Explicit grant for sensitive domain
                            return AccessDecision.Allow($"User {userId} has explicit access to sensitive domain '{domain}'");
                        }
                    }
                }
            }
        }

        // Check global restrictions
        if (_globalRestrictions.TryGetValue(normalizedDomain, out var globalRestrictions))
        {
            lock (globalRestrictions)
            {
                foreach (var restriction in globalRestrictions)
                {
                    if (IsRestrictionActive(restriction, now) && !restriction.IsAllowed)
                    {
                        return AccessDecision.Deny(restriction.Reason ?? $"Domain '{domain}' is globally restricted");
                    }
                }
            }
        }

        // If domain is sensitive and no explicit grant found
        if (isSensitive)
        {
            return AccessDecision.Deny($"Domain '{domain}' is sensitive and requires explicit access grant");
        }

        return AccessDecision.Allow($"Access to domain '{domain}' allowed");
    }

    /// <summary>
    /// Gets all restricted domains for a user.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <returns>List of restricted domains.</returns>
    public List<string> GetRestrictedDomains(string userId)
    {
        var restricted = new List<string>();
        var now = DateTime.UtcNow;

        // Add global restrictions
        foreach (var kvp in _globalRestrictions)
        {
            lock (kvp.Value)
            {
                if (kvp.Value.Any(r => IsRestrictionActive(r, now) && !r.IsAllowed))
                {
                    restricted.Add(kvp.Key);
                }
            }
        }

        // Add user restrictions
        foreach (var kvp in _userRestrictions.Where(k => k.Key.StartsWith($"{userId}:")))
        {
            var domain = kvp.Key.Substring(userId.Length + 1);
            lock (kvp.Value)
            {
                if (kvp.Value.Any(r => IsRestrictionActive(r, now) && !r.IsAllowed))
                {
                    restricted.Add(domain);
                }
            }
        }

        return restricted.Distinct().ToList();
    }

    private static bool IsRestrictionActive(DomainRestriction restriction, DateTime now)
    {
        if (restriction.EffectiveFrom.HasValue && now < restriction.EffectiveFrom.Value)
            return false;

        if (restriction.EffectiveUntil.HasValue && now > restriction.EffectiveUntil.Value)
            return false;

        return true;
    }
}

#endregion

#region N2: Rate Limiting

/// <summary>
/// Query rate limiter using sliding window algorithm.
/// Limits the number of queries per user/organization within a time window.
/// </summary>
public sealed class QueryRateLimiter
{
    private readonly BoundedDictionary<string, SlidingWindowCounter> _counters = new BoundedDictionary<string, SlidingWindowCounter>(1000);
    private readonly BoundedDictionary<string, RateLimitConfig> _configs = new BoundedDictionary<string, RateLimitConfig>(1000);
    private readonly RateLimitConfig _defaultConfig;

    /// <summary>
    /// Creates a new query rate limiter.
    /// </summary>
    /// <param name="defaultQueriesPerMinute">Default queries per minute limit.</param>
    /// <param name="defaultWindowSeconds">Default window size in seconds.</param>
    public QueryRateLimiter(int defaultQueriesPerMinute = 60, int defaultWindowSeconds = 60)
    {
        _defaultConfig = new RateLimitConfig
        {
            MaxRequests = defaultQueriesPerMinute,
            WindowSeconds = defaultWindowSeconds
        };
    }

    /// <summary>
    /// Sets a custom rate limit for an entity.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <param name="config">Rate limit configuration.</param>
    public void SetConfig(string entityId, RateLimitConfig config)
    {
        _configs[entityId] = config;
    }

    /// <summary>
    /// Checks if a request is allowed and records it if so.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <returns>Rate limit result.</returns>
    public RateLimitResult CheckAndRecord(string entityId)
    {
        var config = _configs.GetValueOrDefault(entityId, _defaultConfig);
        var counter = _counters.GetOrAdd(entityId, _ => new SlidingWindowCounter(config.WindowSeconds));

        var currentCount = counter.GetCount();

        if (currentCount >= config.MaxRequests)
        {
            var timeUntilReset = counter.GetTimeUntilReset();
            return new RateLimitResult
            {
                Allowed = false,
                RemainingQuota = 0,
                CurrentUsage = currentCount,
                MaxUsage = config.MaxRequests,
                TimeUntilReset = timeUntilReset,
                RetryAfter = timeUntilReset
            };
        }

        counter.Increment();

        return new RateLimitResult
        {
            Allowed = true,
            RemainingQuota = config.MaxRequests - currentCount - 1,
            CurrentUsage = currentCount + 1,
            MaxUsage = config.MaxRequests,
            TimeUntilReset = counter.GetTimeUntilReset()
        };
    }

    /// <summary>
    /// Gets current usage for an entity.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <returns>Current usage count.</returns>
    public long GetCurrentUsage(string entityId)
    {
        return _counters.TryGetValue(entityId, out var counter)
            ? counter.GetCount()
            : 0;
    }

    /// <summary>
    /// Resets the rate limit counter for an entity.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    public void Reset(string entityId)
    {
        _counters.TryRemove(entityId, out _);
    }

    /// <summary>
    /// Gets statistics for all entities.
    /// </summary>
    /// <returns>Dictionary of entity IDs to current usage.</returns>
    public Dictionary<string, long> GetAllStats()
    {
        return _counters.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.GetCount()
        );
    }
}

/// <summary>
/// Rate limit configuration.
/// </summary>
public sealed record RateLimitConfig
{
    /// <summary>Maximum requests allowed in the window.</summary>
    public int MaxRequests { get; init; } = 60;

    /// <summary>Window size in seconds.</summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>Burst limit (maximum requests in a short burst).</summary>
    public int BurstLimit { get; init; } = 10;

    /// <summary>Whether to allow request queuing when limit is reached.</summary>
    public bool AllowQueuing { get; init; }

    /// <summary>Maximum queue size if queuing is enabled.</summary>
    public int MaxQueueSize { get; init; } = 100;
}

/// <summary>
/// Sliding window counter for rate limiting.
/// </summary>
internal sealed class SlidingWindowCounter
{
    private readonly int _windowSeconds;
    private readonly ConcurrentQueue<DateTime> _timestamps = new();
    private readonly object _lockObject = new();

    public SlidingWindowCounter(int windowSeconds)
    {
        _windowSeconds = windowSeconds;
    }

    public void Increment()
    {
        CleanupOldEntries();
        _timestamps.Enqueue(DateTime.UtcNow);
    }

    public long GetCount()
    {
        CleanupOldEntries();
        return _timestamps.Count;
    }

    public TimeSpan GetTimeUntilReset()
    {
        if (_timestamps.TryPeek(out var oldest))
        {
            var resetTime = oldest.AddSeconds(_windowSeconds);
            var remaining = resetTime - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return TimeSpan.Zero;
    }

    private void CleanupOldEntries()
    {
        var threshold = DateTime.UtcNow.AddSeconds(-_windowSeconds);

        lock (_lockObject)
        {
            while (_timestamps.TryPeek(out var oldest) && oldest < threshold)
            {
                _timestamps.TryDequeue(out _);
            }
        }
    }
}

/// <summary>
/// Cost limiter for AI spending per user/organization.
/// Tracks token consumption and enforces spending limits.
/// </summary>
public sealed class CostLimiter
{
    private readonly BoundedDictionary<string, CostTracker> _trackers = new BoundedDictionary<string, CostTracker>(1000);
    private readonly BoundedDictionary<string, CostLimitConfig> _configs = new BoundedDictionary<string, CostLimitConfig>(1000);
    private readonly CostLimitConfig _defaultConfig;

    /// <summary>
    /// Cost per 1000 tokens by model.
    /// </summary>
    public BoundedDictionary<string, decimal> ModelCosts { get; } = new(1000)
    {
        ["gpt-4"] = 0.03m,
        ["gpt-4-turbo"] = 0.01m,
        ["gpt-3.5-turbo"] = 0.0015m,
        ["claude-3-opus"] = 0.015m,
        ["claude-3-sonnet"] = 0.003m,
        ["claude-3-haiku"] = 0.00025m,
        ["default"] = 0.01m
    };

    /// <summary>
    /// Creates a new cost limiter.
    /// </summary>
    /// <param name="defaultDailyLimit">Default daily cost limit in currency units.</param>
    /// <param name="defaultMonthlyLimit">Default monthly cost limit in currency units.</param>
    public CostLimiter(decimal defaultDailyLimit = 10m, decimal defaultMonthlyLimit = 100m)
    {
        _defaultConfig = new CostLimitConfig
        {
            DailyLimit = defaultDailyLimit,
            MonthlyLimit = defaultMonthlyLimit
        };
    }

    /// <summary>
    /// Sets a custom cost limit configuration for an entity.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <param name="config">Cost limit configuration.</param>
    public void SetConfig(string entityId, CostLimitConfig config)
    {
        _configs[entityId] = config;
    }

    /// <summary>
    /// Records a cost entry and checks if within limits.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <param name="tokenCount">Number of tokens consumed.</param>
    /// <param name="modelId">Model ID for cost calculation.</param>
    /// <returns>Rate limit result.</returns>
    public RateLimitResult RecordAndCheck(string entityId, int tokenCount, string modelId = "default")
    {
        var config = _configs.GetValueOrDefault(entityId, _defaultConfig);
        var tracker = _trackers.GetOrAdd(entityId, _ => new CostTracker());

        var costPer1000 = ModelCosts.GetValueOrDefault(modelId, ModelCosts["default"]);
        var cost = (tokenCount / 1000m) * costPer1000;

        var dailyUsage = tracker.GetDailyCost();
        var monthlyUsage = tracker.GetMonthlyCost();

        // Check daily limit
        if (dailyUsage + cost > config.DailyLimit)
        {
            return new RateLimitResult
            {
                Allowed = false,
                RemainingQuota = 0,
                CurrentUsage = (long)(dailyUsage * 1000), // Convert to millicents for precision
                MaxUsage = (long)(config.DailyLimit * 1000),
                TimeUntilReset = tracker.GetTimeUntilDailyReset(),
                RetryAfter = tracker.GetTimeUntilDailyReset()
            };
        }

        // Check monthly limit
        if (monthlyUsage + cost > config.MonthlyLimit)
        {
            return new RateLimitResult
            {
                Allowed = false,
                RemainingQuota = 0,
                CurrentUsage = (long)(monthlyUsage * 1000),
                MaxUsage = (long)(config.MonthlyLimit * 1000),
                TimeUntilReset = tracker.GetTimeUntilMonthlyReset(),
                RetryAfter = tracker.GetTimeUntilMonthlyReset()
            };
        }

        // Record the cost
        tracker.Record(new CostEntry
        {
            EntityId = entityId,
            OperationType = "ai_completion",
            TokenCount = tokenCount,
            EstimatedCost = cost,
            ModelId = modelId
        });

        var remainingDaily = config.DailyLimit - dailyUsage - cost;

        return new RateLimitResult
        {
            Allowed = true,
            RemainingQuota = (long)(remainingDaily * 1000),
            CurrentUsage = (long)((dailyUsage + cost) * 1000),
            MaxUsage = (long)(config.DailyLimit * 1000),
            TimeUntilReset = tracker.GetTimeUntilDailyReset()
        };
    }

    /// <summary>
    /// Gets the current daily cost for an entity.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <returns>Current daily cost.</returns>
    public decimal GetDailyCost(string entityId)
    {
        return _trackers.TryGetValue(entityId, out var tracker)
            ? tracker.GetDailyCost()
            : 0m;
    }

    /// <summary>
    /// Gets the current monthly cost for an entity.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <returns>Current monthly cost.</returns>
    public decimal GetMonthlyCost(string entityId)
    {
        return _trackers.TryGetValue(entityId, out var tracker)
            ? tracker.GetMonthlyCost()
            : 0m;
    }

    /// <summary>
    /// Gets detailed cost breakdown for an entity.
    /// </summary>
    /// <param name="entityId">User or organization ID.</param>
    /// <param name="days">Number of days to look back.</param>
    /// <returns>List of cost entries.</returns>
    public List<CostEntry> GetCostHistory(string entityId, int days = 30)
    {
        return _trackers.TryGetValue(entityId, out var tracker)
            ? tracker.GetHistory(days)
            : new List<CostEntry>();
    }
}

/// <summary>
/// Cost limit configuration.
/// </summary>
public sealed record CostLimitConfig
{
    /// <summary>Daily spending limit in currency units.</summary>
    public decimal DailyLimit { get; init; } = 10m;

    /// <summary>Monthly spending limit in currency units.</summary>
    public decimal MonthlyLimit { get; init; } = 100m;

    /// <summary>Per-request cost limit.</summary>
    public decimal PerRequestLimit { get; init; } = 1m;

    /// <summary>Whether to send alerts when approaching limit.</summary>
    public bool EnableAlerts { get; init; } = true;

    /// <summary>Alert threshold as percentage of limit (0.0-1.0).</summary>
    public double AlertThreshold { get; init; } = 0.8;
}

/// <summary>
/// Cost tracker for an entity.
/// </summary>
internal sealed class CostTracker
{
    private readonly ConcurrentQueue<CostEntry> _entries = new();
    private readonly object _lockObject = new();

    public void Record(CostEntry entry)
    {
        _entries.Enqueue(entry);
        CleanupOldEntries();
    }

    public decimal GetDailyCost()
    {
        var startOfDay = DateTime.UtcNow.Date;
        return _entries
            .Where(e => e.Timestamp >= startOfDay)
            .Sum(e => e.EstimatedCost);
    }

    public decimal GetMonthlyCost()
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return _entries
            .Where(e => e.Timestamp >= startOfMonth)
            .Sum(e => e.EstimatedCost);
    }

    public TimeSpan GetTimeUntilDailyReset()
    {
        var tomorrow = DateTime.UtcNow.Date.AddDays(1);
        return tomorrow - DateTime.UtcNow;
    }

    public TimeSpan GetTimeUntilMonthlyReset()
    {
        var nextMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        return nextMonth - DateTime.UtcNow;
    }

    public List<CostEntry> GetHistory(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return _entries.Where(e => e.Timestamp >= cutoff).ToList();
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow.AddDays(-90); // Keep 90 days of history

        lock (_lockObject)
        {
            while (_entries.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
            {
                _entries.TryDequeue(out _);
            }
        }
    }
}

/// <summary>
/// Throttle manager for system load management.
/// Dynamically throttles requests based on system load.
/// </summary>
public sealed class ThrottleManager
{
    private readonly SemaphoreSlim _requestSemaphore;
    private readonly int _maxConcurrentRequests;
    private double _currentLoad;
    private int _activeRequests; // Accessed via Volatile.Read/Interlocked - volatile keyword not needed
    private readonly ConcurrentQueue<(DateTime, int)> _loadHistory = new();
    private readonly Timer _loadUpdateTimer;
    private readonly object _lockObject = new();

    /// <summary>
    /// Creates a new throttle manager.
    /// </summary>
    /// <param name="maxConcurrentRequests">Maximum concurrent requests.</param>
    public ThrottleManager(int maxConcurrentRequests = 100)
    {
        _maxConcurrentRequests = maxConcurrentRequests;
        _requestSemaphore = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);

        // Update load metrics every second
        _loadUpdateTimer = new Timer(UpdateLoadMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Attempts to acquire a request slot.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <returns>True if slot acquired, false if throttled.</returns>
    public async Task<bool> TryAcquireAsync(TimeSpan timeout)
    {
        var acquired = await _requestSemaphore.WaitAsync(timeout);

        if (acquired)
        {
            Interlocked.Increment(ref _activeRequests);
        }

        return acquired;
    }

    /// <summary>
    /// Releases a request slot.
    /// </summary>
    public void Release()
    {
        Interlocked.Decrement(ref _activeRequests);
        _requestSemaphore.Release();
    }

    /// <summary>
    /// Gets the current throttle status.
    /// </summary>
    /// <returns>Throttle status.</returns>
    public ThrottleStatus GetStatus()
    {
        var activeRequests = Volatile.Read(ref _activeRequests);
        var load = (double)activeRequests / _maxConcurrentRequests;

        var throttleFactor = CalculateThrottleFactor(load);
        var recommendedDelay = CalculateRecommendedDelay(load);

        return new ThrottleStatus
        {
            ThrottleFactor = throttleFactor,
            SystemLoad = load,
            ActiveRequests = activeRequests,
            MaxConcurrentRequests = _maxConcurrentRequests,
            RecommendedDelay = recommendedDelay
        };
    }

    /// <summary>
    /// Calculates the recommended delay before retrying.
    /// </summary>
    /// <param name="load">Current load percentage.</param>
    /// <returns>Recommended delay.</returns>
    public TimeSpan CalculateRecommendedDelay(double load)
    {
        if (load < 0.5) return TimeSpan.Zero;
        if (load < 0.7) return TimeSpan.FromMilliseconds(100);
        if (load < 0.8) return TimeSpan.FromMilliseconds(500);
        if (load < 0.9) return TimeSpan.FromSeconds(1);
        return TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Calculates the throttle factor (1.0 = no throttling, 0.0 = full throttling).
    /// </summary>
    /// <param name="load">Current load percentage.</param>
    /// <returns>Throttle factor.</returns>
    public double CalculateThrottleFactor(double load)
    {
        if (load < 0.5) return 1.0;
        if (load < 0.7) return 0.8;
        if (load < 0.8) return 0.5;
        if (load < 0.9) return 0.2;
        return 0.1;
    }

    /// <summary>
    /// Executes an action with throttling.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="action">Action to execute.</param>
    /// <param name="timeout">Maximum time to wait for slot.</param>
    /// <returns>Result of the action.</returns>
    /// <exception cref="TimeoutException">If throttled and timeout exceeded.</exception>
    public async Task<T> ExecuteWithThrottlingAsync<T>(Func<Task<T>> action, TimeSpan timeout)
    {
        if (!await TryAcquireAsync(timeout))
        {
            var status = GetStatus();
            throw new TimeoutException(
                $"Request throttled. Load: {status.SystemLoad:P0}, " +
                $"Active: {status.ActiveRequests}/{status.MaxConcurrentRequests}. " +
                $"Retry after: {status.RecommendedDelay}");
        }

        try
        {
            return await action();
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// Gets load history for the specified duration.
    /// </summary>
    /// <param name="duration">Duration to look back.</param>
    /// <returns>List of (timestamp, activeRequests) tuples.</returns>
    public List<(DateTime Timestamp, int ActiveRequests)> GetLoadHistory(TimeSpan duration)
    {
        var cutoff = DateTime.UtcNow - duration;
        return _loadHistory
            .Where(x => x.Item1 >= cutoff)
            .Select(x => (x.Item1, x.Item2))
            .ToList();
    }

    private void UpdateLoadMetrics(object? state)
    {
        var activeRequests = Volatile.Read(ref _activeRequests);
        var now = DateTime.UtcNow;

        _loadHistory.Enqueue((now, activeRequests));

        // Keep only last 5 minutes of history
        var cutoff = now.AddMinutes(-5);
        while (_loadHistory.TryPeek(out var oldest) && oldest.Item1 < cutoff)
        {
            _loadHistory.TryDequeue(out _);
        }

        _currentLoad = (double)activeRequests / _maxConcurrentRequests;
    }

    /// <summary>
    /// Disposes the throttle manager.
    /// </summary>
    public void Dispose()
    {
        _loadUpdateTimer.Dispose();
        _requestSemaphore.Dispose();
    }
}

#endregion
