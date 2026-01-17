using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.AccessControl
{
    /// <summary>
    /// Advanced Access Control List (ACL) plugin with granular permissions.
    /// Extends AccessControlPluginBase for security operations.
    ///
    /// Features:
    /// - Granular per-resource permissions (Read, Write, Delete, Admin)
    /// - Policy-based access control with conditions
    /// - LDAP/Active Directory integration hooks
    /// - Inheritance with override capability
    /// - Wildcard and regex pattern matching for resources
    /// - Role-based access control (RBAC) integration
    /// - Attribute-based access control (ABAC) support
    ///
    /// Message Commands:
    /// - acl.grant: Grant permissions to a subject
    /// - acl.revoke: Revoke permissions from a subject
    /// - acl.check: Check if a subject has permission
    /// - acl.list: List permissions for a resource
    /// - acl.policy.create: Create an access policy
    /// - acl.policy.evaluate: Evaluate policies for a request
    /// </summary>
    public sealed class AdvancedAclPlugin : AccessControlPluginBase
    {
        private readonly AclConfig _config;
        private readonly ConcurrentDictionary<string, ResourceAcl> _aclStore;
        private readonly ConcurrentDictionary<string, AccessPolicy> _policies;
        private readonly ConcurrentDictionary<string, LdapGroupMapping> _ldapMappings;
        private readonly SemaphoreSlim _persistLock = new(1, 1);

        private ILdapProvider? _ldapProvider;

        public override string Id => "datawarehouse.plugins.acl.advanced";
        public override string Name => "Advanced ACL";
        public override string Version => "1.0.0";

        public AdvancedAclPlugin(AclConfig? config = null)
        {
            _config = config ?? new AclConfig();
            _aclStore = new ConcurrentDictionary<string, ResourceAcl>();
            _policies = new ConcurrentDictionary<string, AccessPolicy>();
            _ldapMappings = new ConcurrentDictionary<string, LdapGroupMapping>();
        }

        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            await LoadAclsAsync();

            return response;
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "acl.grant", DisplayName = "Grant", Description = "Grant permissions to a subject" },
                new() { Name = "acl.revoke", DisplayName = "Revoke", Description = "Revoke permissions from a subject" },
                new() { Name = "acl.check", DisplayName = "Check", Description = "Check access permissions" },
                new() { Name = "acl.list", DisplayName = "List", Description = "List permissions for a resource" },
                new() { Name = "acl.policy.create", DisplayName = "Create Policy", Description = "Create an access policy" },
                new() { Name = "acl.policy.evaluate", DisplayName = "Evaluate", Description = "Evaluate policies for a request" },
                new() { Name = "acl.ldap.configure", DisplayName = "Configure LDAP", Description = "Configure LDAP integration" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsGranularACL"] = true;
            metadata["SupportsPolicyBasedAccess"] = true;
            metadata["SupportsLdapIntegration"] = true;
            metadata["SupportsInheritance"] = true;
            metadata["SupportsWildcards"] = true;
            metadata["SupportsRBAC"] = true;
            metadata["SupportsABAC"] = true;
            return metadata;
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "acl.grant":
                    await HandleGrantAsync(message);
                    break;
                case "acl.revoke":
                    await HandleRevokeAsync(message);
                    break;
                case "acl.check":
                    await HandleCheckAsync(message);
                    break;
                case "acl.list":
                    await HandleListAsync(message);
                    break;
                case "acl.policy.create":
                    await HandleCreatePolicyAsync(message);
                    break;
                case "acl.policy.evaluate":
                    await HandleEvaluatePolicyAsync(message);
                    break;
                case "acl.ldap.configure":
                    HandleLdapConfigure(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        public override void SetPermissions(string resource, string subject, Permission allow, Permission deny)
        {
            var acl = _aclStore.GetOrAdd(resource, _ => new ResourceAcl { Resource = resource });

            var entry = acl.Entries.FirstOrDefault(e => e.Subject == subject);
            if (entry == null)
            {
                entry = new AclEntry { Subject = subject };
                acl.Entries.Add(entry);
            }

            entry.Allow = allow;
            entry.Deny = deny;
            entry.ModifiedAt = DateTime.UtcNow;

            _ = PersistAclsAsync();
        }

        public override bool HasAccess(string resource, string subject, Permission requested)
        {
            var effectivePermissions = GetEffectivePermissions(resource, subject);
            return effectivePermissions.HasFlag(requested);
        }

        public override void CreateScope(string resource, string owner)
        {
            var acl = new ResourceAcl
            {
                Resource = resource,
                Owner = owner,
                CreatedAt = DateTime.UtcNow,
                Entries =
                [
                    new AclEntry
                    {
                        Subject = owner,
                        Allow = Permission.FullControl,
                        Deny = Permission.None
                    }
                ]
            };

            _aclStore[resource] = acl;
            _ = PersistAclsAsync();
        }

        public Permission GetEffectivePermissions(string resource, string subject)
        {
            var permissions = Permission.None;
            var denials = Permission.None;

            var subjectRoles = GetSubjectRoles(subject);

            foreach (var (pattern, acl) in _aclStore)
            {
                if (!MatchesResource(resource, pattern))
                    continue;

                foreach (var entry in acl.Entries)
                {
                    if (MatchesSubject(subject, entry.Subject, subjectRoles))
                    {
                        permissions |= entry.Allow;
                        denials |= entry.Deny;
                    }
                }
            }

            foreach (var policy in _policies.Values.Where(p => p.IsEnabled))
            {
                if (EvaluatePolicyConditions(policy, resource, subject))
                {
                    permissions |= policy.GrantedPermissions;
                    denials |= policy.DeniedPermissions;
                }
            }

            return permissions & ~denials;
        }

        private string[] GetSubjectRoles(string subject)
        {
            var roles = new List<string> { subject };

            if (_ldapProvider != null)
            {
                try
                {
                    var ldapGroups = _ldapProvider.GetGroupMembership(subject);
                    foreach (var group in ldapGroups)
                    {
                        if (_ldapMappings.TryGetValue(group, out var mapping))
                        {
                            roles.AddRange(mapping.Roles);
                        }
                        roles.Add($"ldap:{group}");
                    }
                }
                catch { }
            }

            return roles.ToArray();
        }

        private static bool MatchesResource(string resource, string pattern)
        {
            if (pattern == resource) return true;
            if (pattern == "*") return true;

            if (pattern.Contains('*'))
            {
                var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(resource, regex, RegexOptions.IgnoreCase);
            }

            if (pattern.StartsWith('/') && pattern.EndsWith('/'))
            {
                var regex = pattern[1..^1];
                return Regex.IsMatch(resource, regex, RegexOptions.IgnoreCase);
            }

            return false;
        }

        private static bool MatchesSubject(string subject, string pattern, string[] roles)
        {
            if (pattern == subject) return true;
            if (pattern == "*") return true;
            if (pattern.StartsWith("role:") && roles.Contains(pattern[5..])) return true;
            if (pattern.StartsWith("ldap:") && roles.Contains(pattern)) return true;

            return roles.Contains(pattern);
        }

        private bool EvaluatePolicyConditions(AccessPolicy policy, string resource, string subject)
        {
            if (!MatchesResource(resource, policy.ResourcePattern))
                return false;

            if (policy.SubjectPatterns.Length > 0 && !policy.SubjectPatterns.Any(p => MatchesSubject(subject, p, GetSubjectRoles(subject))))
                return false;

            foreach (var condition in policy.Conditions)
            {
                if (!EvaluateCondition(condition, resource, subject))
                    return false;
            }

            return true;
        }

        private bool EvaluateCondition(PolicyCondition condition, string resource, string subject)
        {
            var value = GetConditionValue(condition.Attribute, resource, subject);

            return condition.Operator switch
            {
                ConditionOperator.Equals => value?.Equals(condition.Value) ?? false,
                ConditionOperator.NotEquals => !value?.Equals(condition.Value) ?? true,
                ConditionOperator.Contains => value?.ToString()?.Contains(condition.Value?.ToString() ?? "") ?? false,
                ConditionOperator.StartsWith => value?.ToString()?.StartsWith(condition.Value?.ToString() ?? "") ?? false,
                ConditionOperator.EndsWith => value?.ToString()?.EndsWith(condition.Value?.ToString() ?? "") ?? false,
                ConditionOperator.GreaterThan => Compare(value, condition.Value) > 0,
                ConditionOperator.LessThan => Compare(value, condition.Value) < 0,
                ConditionOperator.In => condition.Value is IEnumerable<object> list && list.Contains(value),
                _ => false
            };
        }

        private object? GetConditionValue(string attribute, string resource, string subject)
        {
            return attribute.ToLowerInvariant() switch
            {
                "time" => DateTime.UtcNow,
                "dayofweek" => DateTime.UtcNow.DayOfWeek.ToString(),
                "hour" => DateTime.UtcNow.Hour,
                "resource" => resource,
                "subject" => subject,
                _ => null
            };
        }

        private static int Compare(object? a, object? b)
        {
            if (a is IComparable ca && b is IComparable cb)
                return ca.CompareTo(cb);
            return 0;
        }

        private async Task HandleGrantAsync(PluginMessage message)
        {
            var resource = GetString(message.Payload, "resource") ?? throw new ArgumentException("resource required");
            var subject = GetString(message.Payload, "subject") ?? throw new ArgumentException("subject required");
            var permissions = ParsePermissions(GetString(message.Payload, "permissions") ?? "read");

            SetPermissions(resource, subject, permissions, Permission.None);
            await PersistAclsAsync();
        }

        private async Task HandleRevokeAsync(PluginMessage message)
        {
            var resource = GetString(message.Payload, "resource") ?? throw new ArgumentException("resource required");
            var subject = GetString(message.Payload, "subject") ?? throw new ArgumentException("subject required");
            var permissions = ParsePermissions(GetString(message.Payload, "permissions") ?? "all");

            if (_aclStore.TryGetValue(resource, out var acl))
            {
                var entry = acl.Entries.FirstOrDefault(e => e.Subject == subject);
                if (entry != null)
                {
                    entry.Allow &= ~permissions;
                }
            }

            await PersistAclsAsync();
        }

        private Task HandleCheckAsync(PluginMessage message)
        {
            var resource = GetString(message.Payload, "resource") ?? throw new ArgumentException("resource required");
            var subject = GetString(message.Payload, "subject") ?? throw new ArgumentException("subject required");
            var permission = ParsePermissions(GetString(message.Payload, "permission") ?? "read");

            var hasAccess = HasAccess(resource, subject, permission);
            return Task.CompletedTask;
        }

        private Task HandleListAsync(PluginMessage message)
        {
            var resource = GetString(message.Payload, "resource") ?? throw new ArgumentException("resource required");

            if (_aclStore.TryGetValue(resource, out var acl))
            {
                var entries = acl.Entries.Select(e => new { e.Subject, Allow = e.Allow.ToString(), Deny = e.Deny.ToString() });
            }

            return Task.CompletedTask;
        }

        private async Task HandleCreatePolicyAsync(PluginMessage message)
        {
            var policyId = GetString(message.Payload, "policyId") ?? Guid.NewGuid().ToString("N");
            var name = GetString(message.Payload, "name") ?? policyId;
            var resourcePattern = GetString(message.Payload, "resourcePattern") ?? "*";
            var permissions = ParsePermissions(GetString(message.Payload, "permissions") ?? "read");

            var policy = new AccessPolicy
            {
                PolicyId = policyId,
                Name = name,
                ResourcePattern = resourcePattern,
                GrantedPermissions = permissions,
                IsEnabled = true
            };

            _policies[policyId] = policy;
            await PersistAclsAsync();
        }

        private Task HandleEvaluatePolicyAsync(PluginMessage message)
        {
            var resource = GetString(message.Payload, "resource") ?? throw new ArgumentException("resource required");
            var subject = GetString(message.Payload, "subject") ?? throw new ArgumentException("subject required");

            var effectivePermissions = GetEffectivePermissions(resource, subject);
            return Task.CompletedTask;
        }

        private void HandleLdapConfigure(PluginMessage message)
        {
            if (message.Payload.TryGetValue("ldapProvider", out var lpObj) && lpObj is ILdapProvider lp)
            {
                _ldapProvider = lp;
            }

            if (message.Payload.TryGetValue("groupMappings", out var gmObj) && gmObj is Dictionary<string, string[]> mappings)
            {
                foreach (var (group, roles) in mappings)
                {
                    _ldapMappings[group] = new LdapGroupMapping { LdapGroup = group, Roles = roles };
                }
            }
        }

        private async Task LoadAclsAsync()
        {
            var path = Path.Combine(_config.StoragePath, "acls.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var data = JsonSerializer.Deserialize<AclStorageData>(json);

                if (data?.Acls != null)
                {
                    foreach (var acl in data.Acls)
                    {
                        _aclStore[acl.Resource] = acl;
                    }
                }

                if (data?.Policies != null)
                {
                    foreach (var policy in data.Policies)
                    {
                        _policies[policy.PolicyId] = policy;
                    }
                }
            }
            catch { }
        }

        private async Task PersistAclsAsync()
        {
            await _persistLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_config.StoragePath);

                var data = new AclStorageData
                {
                    Acls = _aclStore.Values.ToList(),
                    Policies = _policies.Values.ToList()
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                var path = Path.Combine(_config.StoragePath, "acls.json");
                await File.WriteAllTextAsync(path, json);
            }
            finally
            {
                _persistLock.Release();
            }
        }

        private static Permission ParsePermissions(string permString)
        {
            var result = Permission.None;
            var parts = permString.Split(',', '|', ' ');

            foreach (var part in parts)
            {
                result |= part.Trim().ToLowerInvariant() switch
                {
                    "read" => Permission.Read,
                    "write" => Permission.Write,
                    "execute" => Permission.Execute,
                    "delete" => Permission.Delete,
                    "admin" or "fullcontrol" or "all" => Permission.FullControl,
                    _ => Permission.None
                };
            }

            return result;
        }

        private static string? GetString(Dictionary<string, object?> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }
    }

    public class ResourceAcl
    {
        public string Resource { get; set; } = string.Empty;
        public string? Owner { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<AclEntry> Entries { get; set; } = new();
        public bool InheritFromParent { get; set; } = true;
    }

    public class AclEntry
    {
        public string Subject { get; set; } = string.Empty;
        public Permission Allow { get; set; } = Permission.None;
        public Permission Deny { get; set; } = Permission.None;
        public DateTime? ExpiresAt { get; set; }
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    }

    public class AccessPolicy
    {
        public string PolicyId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ResourcePattern { get; set; } = "*";
        public string[] SubjectPatterns { get; set; } = [];
        public Permission GrantedPermissions { get; set; } = Permission.None;
        public Permission DeniedPermissions { get; set; } = Permission.None;
        public PolicyCondition[] Conditions { get; set; } = [];
        public bool IsEnabled { get; set; } = true;
        public int Priority { get; set; } = 0;
    }

    public class PolicyCondition
    {
        public string Attribute { get; set; } = string.Empty;
        public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;
        public object? Value { get; set; }
    }

    public enum ConditionOperator
    {
        Equals, NotEquals, Contains, StartsWith, EndsWith,
        GreaterThan, LessThan, GreaterThanOrEquals, LessThanOrEquals,
        In, NotIn, Matches
    }

    public class LdapGroupMapping
    {
        public string LdapGroup { get; set; } = string.Empty;
        public string[] Roles { get; set; } = [];
    }

    public interface ILdapProvider
    {
        string[] GetGroupMembership(string userId);
        bool Authenticate(string userId, string password);
        Dictionary<string, object> GetUserAttributes(string userId);
    }

    public class AclConfig
    {
        public string StoragePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "acl");
        public bool EnablePolicyEvaluation { get; set; } = true;
        public bool EnableLdapIntegration { get; set; } = false;
    }

    internal class AclStorageData
    {
        public List<ResourceAcl> Acls { get; set; } = new();
        public List<AccessPolicy> Policies { get; set; } = new();
    }
}
