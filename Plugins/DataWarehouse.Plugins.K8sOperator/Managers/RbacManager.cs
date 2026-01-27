using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.K8sOperator.Managers;

/// <summary>
/// Manages RBAC (Role-Based Access Control) for DataWarehouse Kubernetes resources.
/// Provides ServiceAccount creation, Role/ClusterRole creation, RoleBinding/ClusterRoleBinding
/// management, least-privilege permissions, and namespace-scoped RBAC.
/// </summary>
public sealed class RbacManager
{
    private readonly IKubernetesClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the RbacManager.
    /// </summary>
    /// <param name="client">Kubernetes client for API interactions.</param>
    public RbacManager(IKubernetesClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Creates a ServiceAccount for a DataWarehouse component.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">ServiceAccount name.</param>
    /// <param name="config">ServiceAccount configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ServiceAccount creation.</returns>
    public async Task<RbacResult> CreateServiceAccountAsync(
        string namespaceName,
        string name,
        ServiceAccountConfig? config = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        config ??= new ServiceAccountConfig();

        var sa = new ServiceAccount
        {
            ApiVersion = "v1",
            Kind = "ServiceAccount",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/name"] = name
                },
                Annotations = config.Annotations
            },
            ImagePullSecrets = config.ImagePullSecrets?.Select(s => new LocalObjectReference { Name = s }).ToList(),
            Secrets = config.Secrets?.Select(s => new ObjectReference { Name = s }).ToList(),
            AutomountServiceAccountToken = config.AutomountServiceAccountToken
        };

        var json = JsonSerializer.Serialize(sa, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "v1",
            "serviceaccounts",
            namespaceName,
            name,
            json,
            ct);

        return new RbacResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "ServiceAccount",
            Namespace = namespaceName,
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a Role with specified permissions.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">Role name.</param>
    /// <param name="rules">Permission rules.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the Role creation.</returns>
    public async Task<RbacResult> CreateRoleAsync(
        string namespaceName,
        string name,
        List<PolicyRule> rules,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rules);

        var role = new Role
        {
            ApiVersion = "rbac.authorization.k8s.io/v1",
            Kind = "Role",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            Rules = rules.Select(r => new PolicyRuleResource
            {
                ApiGroups = r.ApiGroups,
                Resources = r.Resources,
                Verbs = r.Verbs,
                ResourceNames = r.ResourceNames
            }).ToList()
        };

        var json = JsonSerializer.Serialize(role, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "roles",
            namespaceName,
            name,
            json,
            ct);

        return new RbacResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "Role",
            Namespace = namespaceName,
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a ClusterRole with specified permissions.
    /// </summary>
    /// <param name="name">ClusterRole name.</param>
    /// <param name="rules">Permission rules.</param>
    /// <param name="aggregationLabels">Labels for role aggregation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ClusterRole creation.</returns>
    public async Task<RbacResult> CreateClusterRoleAsync(
        string name,
        List<PolicyRule> rules,
        Dictionary<string, string>? aggregationLabels = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rules);

        var clusterRole = new ClusterRole
        {
            ApiVersion = "rbac.authorization.k8s.io/v1",
            Kind = "ClusterRole",
            Metadata = new ObjectMeta
            {
                Name = name,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            Rules = rules.Select(r => new PolicyRuleResource
            {
                ApiGroups = r.ApiGroups,
                Resources = r.Resources,
                Verbs = r.Verbs,
                ResourceNames = r.ResourceNames,
                NonResourceURLs = r.NonResourceURLs
            }).ToList(),
            AggregationRule = aggregationLabels != null ? new AggregationRule
            {
                ClusterRoleSelectors = new List<LabelSelector>
                {
                    new LabelSelector { MatchLabels = aggregationLabels }
                }
            } : null
        };

        var json = JsonSerializer.Serialize(clusterRole, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "clusterroles",
            null!,
            name,
            json,
            ct);

        return new RbacResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "ClusterRole",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a RoleBinding to bind a Role to subjects.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">RoleBinding name.</param>
    /// <param name="roleName">Name of the Role to bind.</param>
    /// <param name="subjects">Subjects to bind the role to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the RoleBinding creation.</returns>
    public async Task<RbacResult> CreateRoleBindingAsync(
        string namespaceName,
        string name,
        string roleName,
        List<Subject> subjects,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        ArgumentNullException.ThrowIfNull(subjects);

        var roleBinding = new RoleBinding
        {
            ApiVersion = "rbac.authorization.k8s.io/v1",
            Kind = "RoleBinding",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            RoleRef = new RoleRef
            {
                ApiGroup = "rbac.authorization.k8s.io",
                Kind = "Role",
                Name = roleName
            },
            Subjects = subjects.Select(s => new SubjectResource
            {
                Kind = s.Kind.ToString(),
                Name = s.Name,
                Namespace = s.Namespace,
                ApiGroup = s.Kind == SubjectKind.User || s.Kind == SubjectKind.Group
                    ? "rbac.authorization.k8s.io"
                    : null
            }).ToList()
        };

        var json = JsonSerializer.Serialize(roleBinding, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "rolebindings",
            namespaceName,
            name,
            json,
            ct);

        return new RbacResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "RoleBinding",
            Namespace = namespaceName,
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a ClusterRoleBinding to bind a ClusterRole to subjects.
    /// </summary>
    /// <param name="name">ClusterRoleBinding name.</param>
    /// <param name="clusterRoleName">Name of the ClusterRole to bind.</param>
    /// <param name="subjects">Subjects to bind the role to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ClusterRoleBinding creation.</returns>
    public async Task<RbacResult> CreateClusterRoleBindingAsync(
        string name,
        string clusterRoleName,
        List<Subject> subjects,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterRoleName);
        ArgumentNullException.ThrowIfNull(subjects);

        var clusterRoleBinding = new ClusterRoleBinding
        {
            ApiVersion = "rbac.authorization.k8s.io/v1",
            Kind = "ClusterRoleBinding",
            Metadata = new ObjectMeta
            {
                Name = name,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator"
                }
            },
            RoleRef = new RoleRef
            {
                ApiGroup = "rbac.authorization.k8s.io",
                Kind = "ClusterRole",
                Name = clusterRoleName
            },
            Subjects = subjects.Select(s => new SubjectResource
            {
                Kind = s.Kind.ToString(),
                Name = s.Name,
                Namespace = s.Namespace,
                ApiGroup = s.Kind == SubjectKind.User || s.Kind == SubjectKind.Group
                    ? "rbac.authorization.k8s.io"
                    : null
            }).ToList()
        };

        var json = JsonSerializer.Serialize(clusterRoleBinding, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "clusterrolebindings",
            null!,
            name,
            json,
            ct);

        return new RbacResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "ClusterRoleBinding",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a complete RBAC setup for an operator component.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="componentName">Component name.</param>
    /// <param name="config">RBAC configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the complete RBAC setup.</returns>
    public async Task<RbacSetupResult> SetupOperatorRbacAsync(
        string namespaceName,
        string componentName,
        OperatorRbacConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentNullException.ThrowIfNull(config);

        var results = new List<RbacResult>();

        // 1. Create ServiceAccount
        var saResult = await CreateServiceAccountAsync(
            namespaceName,
            $"{componentName}-sa",
            new ServiceAccountConfig
            {
                AutomountServiceAccountToken = true,
                ImagePullSecrets = config.ImagePullSecrets
            },
            ct);
        results.Add(saResult);

        // 2. Create namespace-scoped Role if needed
        if (config.NamespacedRules.Count > 0)
        {
            var roleResult = await CreateRoleAsync(
                namespaceName,
                $"{componentName}-role",
                config.NamespacedRules,
                ct);
            results.Add(roleResult);

            // 3. Create RoleBinding
            var rbResult = await CreateRoleBindingAsync(
                namespaceName,
                $"{componentName}-rolebinding",
                $"{componentName}-role",
                new List<Subject>
                {
                    new Subject
                    {
                        Kind = SubjectKind.ServiceAccount,
                        Name = $"{componentName}-sa",
                        Namespace = namespaceName
                    }
                },
                ct);
            results.Add(rbResult);
        }

        // 4. Create ClusterRole if needed
        if (config.ClusterRules.Count > 0)
        {
            var crResult = await CreateClusterRoleAsync(
                $"{componentName}-clusterrole",
                config.ClusterRules,
                ct: ct);
            results.Add(crResult);

            // 5. Create ClusterRoleBinding
            var crbResult = await CreateClusterRoleBindingAsync(
                $"{componentName}-clusterrolebinding",
                $"{componentName}-clusterrole",
                new List<Subject>
                {
                    new Subject
                    {
                        Kind = SubjectKind.ServiceAccount,
                        Name = $"{componentName}-sa",
                        Namespace = namespaceName
                    }
                },
                ct);
            results.Add(crbResult);
        }

        return new RbacSetupResult
        {
            Success = results.All(r => r.Success),
            ServiceAccountName = $"{componentName}-sa",
            Results = results,
            Message = results.All(r => r.Success)
                ? "RBAC setup completed successfully"
                : "Some RBAC resources failed to create"
        };
    }

    /// <summary>
    /// Creates predefined least-privilege rules for DataWarehouse operators.
    /// </summary>
    /// <param name="operatorType">Type of operator.</param>
    /// <returns>List of policy rules with least-privilege permissions.</returns>
    public static OperatorRbacConfig GetLeastPrivilegeRules(OperatorType operatorType)
    {
        return operatorType switch
        {
            OperatorType.Storage => new OperatorRbacConfig
            {
                NamespacedRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "pods", "services", "configmaps", "secrets" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "persistentvolumeclaims" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "apps" },
                        Resources = new List<string> { "deployments", "statefulsets" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "events" },
                        Verbs = new List<string> { "create", "patch" }
                    }
                },
                ClusterRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "datawarehouse.io" },
                        Resources = new List<string> { "datawarehouseclusters", "datawarehousestorages" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "datawarehouse.io" },
                        Resources = new List<string> { "datawarehouseclusters/status", "datawarehousestorages/status" },
                        Verbs = new List<string> { "get", "update", "patch" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "datawarehouse.io" },
                        Resources = new List<string> { "datawarehouseclusters/finalizers", "datawarehousestorages/finalizers" },
                        Verbs = new List<string> { "update" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "storage.k8s.io" },
                        Resources = new List<string> { "storageclasses" },
                        Verbs = new List<string> { "get", "list", "watch" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "persistentvolumes" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    }
                }
            },
            OperatorType.Backup => new OperatorRbacConfig
            {
                NamespacedRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "batch" },
                        Resources = new List<string> { "jobs", "cronjobs" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "pods", "secrets", "configmaps" },
                        Verbs = new List<string> { "get", "list", "watch" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "events" },
                        Verbs = new List<string> { "create", "patch" }
                    }
                },
                ClusterRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "datawarehouse.io" },
                        Resources = new List<string> { "datawarehousebackups" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "datawarehouse.io" },
                        Resources = new List<string> { "datawarehousebackups/status" },
                        Verbs = new List<string> { "get", "update", "patch" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "velero.io" },
                        Resources = new List<string> { "backups", "schedules", "restores" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    }
                }
            },
            OperatorType.Monitoring => new OperatorRbacConfig
            {
                NamespacedRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "pods", "services", "endpoints" },
                        Verbs = new List<string> { "get", "list", "watch" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "configmaps" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch" }
                    }
                },
                ClusterRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "monitoring.coreos.com" },
                        Resources = new List<string> { "servicemonitors", "podmonitors", "prometheusrules" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "nodes", "nodes/metrics" },
                        Verbs = new List<string> { "get", "list", "watch" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "namespaces" },
                        Verbs = new List<string> { "get", "list", "watch" }
                    }
                }
            },
            OperatorType.Security => new OperatorRbacConfig
            {
                NamespacedRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "secrets" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "" },
                        Resources = new List<string> { "serviceaccounts" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "networking.k8s.io" },
                        Resources = new List<string> { "networkpolicies" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    }
                },
                ClusterRules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "cert-manager.io" },
                        Resources = new List<string> { "certificates", "issuers", "clusterissuers" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    },
                    new PolicyRule
                    {
                        ApiGroups = new List<string> { "rbac.authorization.k8s.io" },
                        Resources = new List<string> { "roles", "rolebindings" },
                        Verbs = new List<string> { "get", "list", "watch", "create", "update", "patch", "delete" }
                    }
                }
            },
            _ => new OperatorRbacConfig()
        };
    }

    /// <summary>
    /// Deletes RBAC resources for a component.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="componentName">Component name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the deletion.</returns>
    public async Task<RbacResult> DeleteOperatorRbacAsync(
        string namespaceName,
        string componentName,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        // Delete ClusterRoleBinding
        var crbResult = await _client.DeleteResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "clusterrolebindings",
            null!,
            $"{componentName}-clusterrolebinding",
            ct);
        if (!crbResult.Success && !crbResult.NotFound)
            errors.Add($"ClusterRoleBinding: {crbResult.Message}");

        // Delete ClusterRole
        var crResult = await _client.DeleteResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "clusterroles",
            null!,
            $"{componentName}-clusterrole",
            ct);
        if (!crResult.Success && !crResult.NotFound)
            errors.Add($"ClusterRole: {crResult.Message}");

        // Delete RoleBinding
        var rbResult = await _client.DeleteResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "rolebindings",
            namespaceName,
            $"{componentName}-rolebinding",
            ct);
        if (!rbResult.Success && !rbResult.NotFound)
            errors.Add($"RoleBinding: {rbResult.Message}");

        // Delete Role
        var roleResult = await _client.DeleteResourceAsync(
            "rbac.authorization.k8s.io/v1",
            "roles",
            namespaceName,
            $"{componentName}-role",
            ct);
        if (!roleResult.Success && !roleResult.NotFound)
            errors.Add($"Role: {roleResult.Message}");

        // Delete ServiceAccount
        var saResult = await _client.DeleteResourceAsync(
            "v1",
            "serviceaccounts",
            namespaceName,
            $"{componentName}-sa",
            ct);
        if (!saResult.Success && !saResult.NotFound)
            errors.Add($"ServiceAccount: {saResult.Message}");

        return new RbacResult
        {
            Success = errors.Count == 0,
            ResourceName = componentName,
            ResourceKind = "RbacResources",
            Namespace = namespaceName,
            Message = errors.Count > 0 ? string.Join("; ", errors) : "All RBAC resources deleted"
        };
    }
}

#region Configuration Classes

/// <summary>Configuration for ServiceAccount creation.</summary>
public sealed class ServiceAccountConfig
{
    /// <summary>Annotations to add to the ServiceAccount.</summary>
    public Dictionary<string, string>? Annotations { get; set; }

    /// <summary>Image pull secrets to associate.</summary>
    public List<string>? ImagePullSecrets { get; set; }

    /// <summary>Secrets to mount.</summary>
    public List<string>? Secrets { get; set; }

    /// <summary>Whether to automount the service account token.</summary>
    public bool AutomountServiceAccountToken { get; set; } = true;
}

/// <summary>Policy rule definition.</summary>
public sealed class PolicyRule
{
    /// <summary>API groups to apply to.</summary>
    public List<string> ApiGroups { get; set; } = new();

    /// <summary>Resources to apply to.</summary>
    public List<string> Resources { get; set; } = new();

    /// <summary>Verbs (actions) allowed.</summary>
    public List<string> Verbs { get; set; } = new();

    /// <summary>Specific resource names (optional).</summary>
    public List<string>? ResourceNames { get; set; }

    /// <summary>Non-resource URLs (for ClusterRoles only).</summary>
    public List<string>? NonResourceURLs { get; set; }
}

/// <summary>Subject for role bindings.</summary>
public sealed class Subject
{
    /// <summary>Kind of subject.</summary>
    public SubjectKind Kind { get; set; }

    /// <summary>Name of the subject.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Namespace (for ServiceAccounts).</summary>
    public string? Namespace { get; set; }
}

/// <summary>Types of subjects for role bindings.</summary>
public enum SubjectKind
{
    /// <summary>User subject.</summary>
    User,
    /// <summary>Group subject.</summary>
    Group,
    /// <summary>ServiceAccount subject.</summary>
    ServiceAccount
}

/// <summary>Configuration for operator RBAC setup.</summary>
public sealed class OperatorRbacConfig
{
    /// <summary>Namespace-scoped rules.</summary>
    public List<PolicyRule> NamespacedRules { get; set; } = new();

    /// <summary>Cluster-scoped rules.</summary>
    public List<PolicyRule> ClusterRules { get; set; } = new();

    /// <summary>Image pull secrets.</summary>
    public List<string>? ImagePullSecrets { get; set; }
}

/// <summary>Types of operators.</summary>
public enum OperatorType
{
    /// <summary>Storage operator.</summary>
    Storage,
    /// <summary>Backup operator.</summary>
    Backup,
    /// <summary>Monitoring operator.</summary>
    Monitoring,
    /// <summary>Security operator.</summary>
    Security,
    /// <summary>General operator.</summary>
    General
}

/// <summary>Result of an RBAC operation.</summary>
public sealed class RbacResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the resource.</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Kind of the resource.</summary>
    public string ResourceKind { get; set; } = string.Empty;

    /// <summary>Namespace of the resource.</summary>
    public string? Namespace { get; set; }

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of a complete RBAC setup.</summary>
public sealed class RbacSetupResult
{
    /// <summary>Whether the setup succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the created ServiceAccount.</summary>
    public string ServiceAccountName { get; set; } = string.Empty;

    /// <summary>Individual operation results.</summary>
    public List<RbacResult> Results { get; set; } = new();

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

#endregion

#region Kubernetes Resource Types

internal sealed class ServiceAccount
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public List<LocalObjectReference>? ImagePullSecrets { get; set; }
    public List<ObjectReference>? Secrets { get; set; }
    public bool? AutomountServiceAccountToken { get; set; }
}

internal sealed class LocalObjectReference
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class ObjectReference
{
    public string? ApiVersion { get; set; }
    public string? Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Namespace { get; set; }
}

internal sealed class Role
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public List<PolicyRuleResource> Rules { get; set; } = new();
}

internal sealed class ClusterRole
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public List<PolicyRuleResource> Rules { get; set; } = new();
    public AggregationRule? AggregationRule { get; set; }
}

internal sealed class AggregationRule
{
    public List<LabelSelector>? ClusterRoleSelectors { get; set; }
}

internal sealed class PolicyRuleResource
{
    public List<string>? ApiGroups { get; set; }
    public List<string>? Resources { get; set; }
    public List<string>? Verbs { get; set; }
    public List<string>? ResourceNames { get; set; }
    public List<string>? NonResourceURLs { get; set; }
}

internal sealed class RoleBinding
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public RoleRef RoleRef { get; set; } = new();
    public List<SubjectResource>? Subjects { get; set; }
}

internal sealed class ClusterRoleBinding
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public RoleRef RoleRef { get; set; } = new();
    public List<SubjectResource>? Subjects { get; set; }
}

internal sealed class RoleRef
{
    public string ApiGroup { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

internal sealed class SubjectResource
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Namespace { get; set; }
    public string? ApiGroup { get; set; }
}

#endregion
