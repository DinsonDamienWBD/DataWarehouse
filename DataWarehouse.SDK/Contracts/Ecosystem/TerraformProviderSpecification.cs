using System;
using System.Collections.Generic;
using System.Text;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

// =============================================================================
// Terraform Attribute Type Enum
// =============================================================================

/// <summary>
/// Terraform attribute type system for HCL schema definitions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Terraform provider (ECOS-13)")]
public enum TerraformAttrType
{
    /// <summary>String attribute (terraform: TypeString).</summary>
    String = 0,
    /// <summary>Integer attribute (terraform: TypeInt).</summary>
    Int = 1,
    /// <summary>Boolean attribute (terraform: TypeBool).</summary>
    Bool = 2,
    /// <summary>Float attribute (terraform: TypeFloat).</summary>
    Float = 3,
    /// <summary>List attribute (terraform: TypeList).</summary>
    List = 4,
    /// <summary>Map attribute (terraform: TypeMap).</summary>
    Map = 5,
    /// <summary>Block attribute (nested schema block).</summary>
    Block = 6
}

// =============================================================================
// Terraform Attribute Record
// =============================================================================

/// <summary>
/// Defines a single attribute within a Terraform resource or data source schema.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Terraform provider (ECOS-13)")]
public sealed record TerraformAttribute
{
    /// <summary>Attribute name in snake_case (e.g., "storage_size_gb").</summary>
    public required string Name { get; init; }

    /// <summary>Terraform attribute type.</summary>
    public required TerraformAttrType Type { get; init; }

    /// <summary>Whether this attribute is required in configuration.</summary>
    public bool Required { get; init; }

    /// <summary>Whether this attribute is optional in configuration.</summary>
    public bool Optional { get; init; }

    /// <summary>Whether this attribute is computed by the provider.</summary>
    public bool Computed { get; init; }

    /// <summary>Whether this attribute contains sensitive data (masked in output).</summary>
    public bool Sensitive { get; init; }

    /// <summary>Whether changing this attribute forces resource recreation.</summary>
    public bool ForceNew { get; init; }

    /// <summary>Default value if not specified, null if no default.</summary>
    public object? Default { get; init; }

    /// <summary>Human-readable description for documentation.</summary>
    public required string Description { get; init; }
}

// =============================================================================
// Terraform CRUD Operations
// =============================================================================

/// <summary>
/// Defines which CRUD operations a Terraform resource supports.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Terraform provider (ECOS-13)")]
public sealed record TerraformCrudOperations
{
    /// <summary>Whether Create is supported.</summary>
    public bool Create { get; init; } = true;

    /// <summary>Whether Read is supported.</summary>
    public bool Read { get; init; } = true;

    /// <summary>Whether Update is supported.</summary>
    public bool Update { get; init; } = true;

    /// <summary>Whether Delete is supported.</summary>
    public bool Delete { get; init; } = true;

    /// <summary>Whether Import is supported for existing resources.</summary>
    public bool Import { get; init; } = true;
}

// =============================================================================
// Terraform Resource Record
// =============================================================================

/// <summary>
/// Defines a Terraform managed resource with its schema and CRUD operations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Terraform provider (ECOS-13)")]
public sealed record TerraformResource
{
    /// <summary>Resource type name (e.g., "datawarehouse_instance").</summary>
    public required string TypeName { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>Schema attributes for this resource.</summary>
    public required IReadOnlyList<TerraformAttribute> Schema { get; init; }

    /// <summary>Supported CRUD operations.</summary>
    public required TerraformCrudOperations Operations { get; init; }
}

// =============================================================================
// Terraform Data Source Record
// =============================================================================

/// <summary>
/// Defines a Terraform data source (read-only) with its schema.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Terraform provider (ECOS-13)")]
public sealed record TerraformDataSource
{
    /// <summary>Data source type name (e.g., "datawarehouse_instance_status").</summary>
    public required string TypeName { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>Schema attributes returned by this data source.</summary>
    public required IReadOnlyList<TerraformAttribute> Schema { get; init; }
}

// =============================================================================
// Terraform Provider Specification
// =============================================================================

/// <summary>
/// Defines the complete Terraform provider schema for <c>terraform-provider-datawarehouse</c>.
/// Generates Go source code for a Terraform provider using the Terraform Plugin SDK v2.
/// The provider enables infrastructure teams to manage DW instances, VDEs, users,
/// policies, plugins, and replication topologies via <c>terraform apply</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Terraform provider (ECOS-13)")]
public static class TerraformProviderSpecification
{
    /// <summary>Provider name as registered in the Terraform registry.</summary>
    public const string ProviderName = "datawarehouse";

    /// <summary>Provider version.</summary>
    public const string ProviderVersion = "1.0.0";

    /// <summary>Terraform registry source address.</summary>
    public const string RegistrySource = "registry.terraform.io/datawarehouse/datawarehouse";

    // -------------------------------------------------------------------------
    // Provider Configuration Schema
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the provider-level configuration schema for authenticating
    /// and connecting to a DataWarehouse server.
    /// </summary>
    public static IReadOnlyList<TerraformAttribute> GetProviderSchema()
    {
        return new TerraformAttribute[]
        {
            new()
            {
                Name = "host",
                Type = TerraformAttrType.String,
                Required = true,
                Description = "DataWarehouse server address (hostname or IP)"
            },
            new()
            {
                Name = "port",
                Type = TerraformAttrType.Int,
                Optional = true,
                Default = 5432,
                Description = "Connection port (default: 5432)"
            },
            new()
            {
                Name = "username",
                Type = TerraformAttrType.String,
                Required = true,
                Description = "Authentication username"
            },
            new()
            {
                Name = "password",
                Type = TerraformAttrType.String,
                Required = true,
                Sensitive = true,
                Description = "Authentication password"
            },
            new()
            {
                Name = "tls_enabled",
                Type = TerraformAttrType.Bool,
                Optional = true,
                Default = true,
                Description = "Enable TLS for connections (default: true)"
            },
            new()
            {
                Name = "tls_skip_verify",
                Type = TerraformAttrType.Bool,
                Optional = true,
                Default = false,
                Description = "Skip TLS certificate verification (default: false)"
            }
        };
    }

    // -------------------------------------------------------------------------
    // Resources (6 managed resources)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all 6 managed Terraform resources for the DataWarehouse provider.
    /// </summary>
    public static IReadOnlyList<TerraformResource> GetResources()
    {
        return new TerraformResource[]
        {
            BuildInstanceResource(),
            BuildVdeResource(),
            BuildUserResource(),
            BuildPolicyResource(),
            BuildPluginResource(),
            BuildReplicationTopologyResource()
        };
    }

    private static TerraformResource BuildInstanceResource() => new()
    {
        TypeName = "datawarehouse_instance",
        Description = "Manages a DataWarehouse instance with storage, replication, and plugin configuration",
        Operations = new TerraformCrudOperations(),
        Schema = new TerraformAttribute[]
        {
            new() { Name = "id", Type = TerraformAttrType.String, Computed = true, Description = "Instance identifier (assigned by server)" },
            new() { Name = "name", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Instance name (immutable after creation)" },
            new() { Name = "storage_size_gb", Type = TerraformAttrType.Int, Required = true, Description = "Storage allocation in gigabytes" },
            new() { Name = "replication_factor", Type = TerraformAttrType.Int, Optional = true, Default = 1, Description = "Number of data replicas (default: 1)" },
            new() { Name = "plugins_enabled", Type = TerraformAttrType.List, Optional = true, Description = "List of plugin IDs to enable" },
            new() { Name = "operational_profile", Type = TerraformAttrType.String, Optional = true, Default = "Balanced", Description = "Operational profile: Speed, Balanced, Standard, Strict, or Paranoid" },
            new() { Name = "status", Type = TerraformAttrType.String, Computed = true, Description = "Current instance status" },
            new() { Name = "created_at", Type = TerraformAttrType.String, Computed = true, Description = "Creation timestamp" }
        }
    };

    private static TerraformResource BuildVdeResource() => new()
    {
        TypeName = "datawarehouse_vde",
        Description = "Manages a Virtual Data Environment within a DataWarehouse instance",
        Operations = new TerraformCrudOperations(),
        Schema = new TerraformAttribute[]
        {
            new() { Name = "id", Type = TerraformAttrType.String, Computed = true, Description = "VDE identifier" },
            new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Parent instance ID" },
            new() { Name = "name", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "VDE name" },
            new() { Name = "block_size", Type = TerraformAttrType.Int, Optional = true, Default = 4096, Description = "Block size in bytes (default: 4096)" },
            new() { Name = "encryption_enabled", Type = TerraformAttrType.Bool, Optional = true, Default = true, Description = "Enable encryption at rest (default: true)" },
            new() { Name = "compression_codec", Type = TerraformAttrType.String, Optional = true, Default = "brotli", Description = "Compression codec (brotli, zstd, lz4, none)" },
            new() { Name = "replication_mode", Type = TerraformAttrType.String, Optional = true, Default = "sync", Description = "Replication mode (sync, async, none)" },
            new() { Name = "size_bytes", Type = TerraformAttrType.Int, Computed = true, Description = "Current VDE size in bytes" }
        }
    };

    private static TerraformResource BuildUserResource() => new()
    {
        TypeName = "datawarehouse_user",
        Description = "Manages a user account within a DataWarehouse instance",
        Operations = new TerraformCrudOperations(),
        Schema = new TerraformAttribute[]
        {
            new() { Name = "id", Type = TerraformAttrType.String, Computed = true, Description = "User identifier" },
            new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Parent instance ID" },
            new() { Name = "username", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Username (immutable)" },
            new() { Name = "password", Type = TerraformAttrType.String, Required = true, Sensitive = true, Description = "User password" },
            new() { Name = "roles", Type = TerraformAttrType.List, Optional = true, Description = "List of assigned roles" },
            new() { Name = "mfa_enabled", Type = TerraformAttrType.Bool, Optional = true, Default = false, Description = "Enable multi-factor authentication" }
        }
    };

    private static TerraformResource BuildPolicyResource() => new()
    {
        TypeName = "datawarehouse_policy",
        Description = "Manages a policy rule within a DataWarehouse instance",
        Operations = new TerraformCrudOperations(),
        Schema = new TerraformAttribute[]
        {
            new() { Name = "id", Type = TerraformAttrType.String, Computed = true, Description = "Policy identifier" },
            new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Parent instance ID" },
            new() { Name = "name", Type = TerraformAttrType.String, Required = true, Description = "Policy name" },
            new() { Name = "target_path", Type = TerraformAttrType.String, Required = true, Description = "Target path for policy application" },
            new() { Name = "feature", Type = TerraformAttrType.String, Required = true, Description = "Feature ID the policy governs" },
            new() { Name = "intensity_level", Type = TerraformAttrType.Int, Optional = true, Default = 50, Description = "Policy intensity level (0-100)" },
            new() { Name = "cascade_strategy", Type = TerraformAttrType.String, Optional = true, Default = "Inherit", Description = "Cascade strategy: Inherit, Enforce, Override, MostRestrictive, Custom" }
        }
    };

    private static TerraformResource BuildPluginResource() => new()
    {
        TypeName = "datawarehouse_plugin",
        Description = "Manages plugin enablement and configuration within a DataWarehouse instance",
        Operations = new TerraformCrudOperations(),
        Schema = new TerraformAttribute[]
        {
            new() { Name = "id", Type = TerraformAttrType.String, Computed = true, Description = "Plugin resource identifier" },
            new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Parent instance ID" },
            new() { Name = "plugin_id", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Plugin identifier (e.g., 'UltimateStorage')" },
            new() { Name = "enabled", Type = TerraformAttrType.Bool, Optional = true, Default = true, Description = "Whether the plugin is enabled" },
            new() { Name = "configuration", Type = TerraformAttrType.Map, Optional = true, Description = "Plugin-specific configuration key-value pairs" }
        }
    };

    private static TerraformResource BuildReplicationTopologyResource() => new()
    {
        TypeName = "datawarehouse_replication_topology",
        Description = "Manages replication topology across DataWarehouse nodes",
        Operations = new TerraformCrudOperations(),
        Schema = new TerraformAttribute[]
        {
            new() { Name = "id", Type = TerraformAttrType.String, Computed = true, Description = "Topology identifier" },
            new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, ForceNew = true, Description = "Parent instance ID" },
            new() { Name = "mode", Type = TerraformAttrType.String, Required = true, Description = "Replication mode: active-active or active-passive" },
            new() { Name = "nodes", Type = TerraformAttrType.Block, Required = true, Description = "List of node endpoint blocks (host, port, role)" },
            new() { Name = "sync_interval_seconds", Type = TerraformAttrType.Int, Optional = true, Default = 30, Description = "Synchronization interval in seconds (default: 30)" },
            new() { Name = "status", Type = TerraformAttrType.String, Computed = true, Description = "Current topology replication status" }
        }
    };

    // -------------------------------------------------------------------------
    // Data Sources (3 read-only data sources)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all 3 read-only Terraform data sources for the DataWarehouse provider.
    /// </summary>
    public static IReadOnlyList<TerraformDataSource> GetDataSources()
    {
        return new TerraformDataSource[]
        {
            new()
            {
                TypeName = "datawarehouse_instance_status",
                Description = "Reads the current health and status of a DataWarehouse instance",
                Schema = new TerraformAttribute[]
                {
                    new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, Description = "Instance ID to query" },
                    new() { Name = "health", Type = TerraformAttrType.String, Computed = true, Description = "Health status (Healthy, Degraded, Unhealthy)" },
                    new() { Name = "node_count", Type = TerraformAttrType.Int, Computed = true, Description = "Number of active nodes" },
                    new() { Name = "storage_used_gb", Type = TerraformAttrType.Float, Computed = true, Description = "Storage used in gigabytes" },
                    new() { Name = "uptime_seconds", Type = TerraformAttrType.Int, Computed = true, Description = "Instance uptime in seconds" }
                }
            },
            new()
            {
                TypeName = "datawarehouse_capabilities",
                Description = "Lists the supported capabilities of a DataWarehouse instance",
                Schema = new TerraformAttribute[]
                {
                    new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, Description = "Instance ID to query" },
                    new() { Name = "supported_plugins", Type = TerraformAttrType.List, Computed = true, Description = "List of supported plugin IDs" },
                    new() { Name = "supported_formats", Type = TerraformAttrType.List, Computed = true, Description = "List of supported data formats" },
                    new() { Name = "supported_protocols", Type = TerraformAttrType.List, Computed = true, Description = "List of supported wire protocols" }
                }
            },
            new()
            {
                TypeName = "datawarehouse_plugin_catalog",
                Description = "Lists all available plugins with their versions",
                Schema = new TerraformAttribute[]
                {
                    new() { Name = "instance_id", Type = TerraformAttrType.String, Required = true, Description = "Instance ID to query" },
                    new() { Name = "plugins", Type = TerraformAttrType.Block, Computed = true, Description = "List of available plugins with id, name, version, and description" }
                }
            }
        };
    }

    // -------------------------------------------------------------------------
    // Go Code Generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates the complete Go source code for <c>terraform-provider-datawarehouse</c>.
    /// Returns a dictionary mapping relative file paths to Go source content.
    /// Uses the Terraform Plugin SDK v2 and the DataWarehouse Go SDK from 89-07.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GenerateProviderGoCode()
    {
        var files = new Dictionary<string, string>(16);

        files["go.mod"] = GenerateGoMod();
        files["provider.go"] = GenerateProviderGo();
        files["resource_instance.go"] = GenerateResourceGo("instance", BuildInstanceResource());
        files["resource_vde.go"] = GenerateResourceGo("vde", BuildVdeResource());
        files["resource_user.go"] = GenerateResourceGo("user", BuildUserResource());
        files["resource_policy.go"] = GenerateResourceGo("policy", BuildPolicyResource());
        files["resource_plugin.go"] = GenerateResourceGo("plugin", BuildPluginResource());
        files["resource_replication.go"] = GenerateResourceGo("replication", BuildReplicationTopologyResource());

        var dataSources = GetDataSources();
        files["data_source_status.go"] = GenerateDataSourceGo("status", dataSources[0]);
        files["data_source_capabilities.go"] = GenerateDataSourceGo("capabilities", dataSources[1]);
        files["data_source_catalog.go"] = GenerateDataSourceGo("catalog", dataSources[2]);

        return files;
    }

    // -------------------------------------------------------------------------
    // Go Code Generation Helpers
    // -------------------------------------------------------------------------

    private static string GenerateGoMod()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("module github.com/datawarehouse/terraform-provider-datawarehouse");
        sb.AppendLine();
        sb.AppendLine("go 1.21");
        sb.AppendLine();
        sb.AppendLine("require (");
        sb.AppendLine("\tgithub.com/hashicorp/terraform-plugin-sdk/v2 v2.31.0");
        sb.AppendLine("\tgithub.com/datawarehouse/datawarehouse-go v1.0.0");
        sb.AppendLine(")");
        return sb.ToString();
    }

    private static string GenerateProviderGo()
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"context\"");
        sb.AppendLine();
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/diag\"");
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/helper/schema\"");
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/plugin\"");
        sb.AppendLine("\tdw \"github.com/datawarehouse/datawarehouse-go\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("func main() {");
        sb.AppendLine("\tplugin.Serve(&plugin.ServeOpts{ProviderFunc: Provider})");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// Provider returns the datawarehouse Terraform provider.");
        sb.AppendLine("func Provider() *schema.Provider {");
        sb.AppendLine("\treturn &schema.Provider{");
        sb.AppendLine("\t\tSchema: map[string]*schema.Schema{");

        foreach (var attr in GetProviderSchema())
        {
            sb.Append("\t\t\t\"").Append(attr.Name).AppendLine("\": {");
            sb.Append("\t\t\t\tType:        schema.").AppendLine(GoSchemaType(attr.Type) + ",");
            if (attr.Required) sb.AppendLine("\t\t\t\tRequired:    true,");
            if (attr.Optional) sb.AppendLine("\t\t\t\tOptional:    true,");
            if (attr.Sensitive) sb.AppendLine("\t\t\t\tSensitive:   true,");
            if (attr.Default is not null) sb.Append("\t\t\t\tDefaultFunc: schema.EnvDefaultFunc(\"DW_").Append(attr.Name.ToUpperInvariant()).AppendLine("\", " + GoDefaultValue(attr.Default) + "),");
            sb.Append("\t\t\t\tDescription: \"").Append(attr.Description).AppendLine("\",");
            sb.AppendLine("\t\t\t},");
        }

        sb.AppendLine("\t\t},");
        sb.AppendLine("\t\tResourcesMap: map[string]*schema.Resource{");
        foreach (var r in GetResources())
        {
            var shortName = r.TypeName.Replace("datawarehouse_", "");
            sb.Append("\t\t\t\"").Append(r.TypeName).Append("\": resource").Append(PascalCase(shortName)).AppendLine("(),");
        }
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t\tDataSourcesMap: map[string]*schema.Resource{");
        foreach (var ds in GetDataSources())
        {
            var shortName = ds.TypeName.Replace("datawarehouse_", "");
            sb.Append("\t\t\t\"").Append(ds.TypeName).Append("\": dataSource").Append(PascalCase(shortName)).AppendLine("(),");
        }
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t\tConfigureContextFunc: providerConfigure,");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("func providerConfigure(ctx context.Context, d *schema.ResourceData) (interface{}, diag.Diagnostics) {");
        sb.AppendLine("\thost := d.Get(\"host\").(string)");
        sb.AppendLine("\tport := d.Get(\"port\").(int)");
        sb.AppendLine("\tusername := d.Get(\"username\").(string)");
        sb.AppendLine("\tpassword := d.Get(\"password\").(string)");
        sb.AppendLine("\ttlsEnabled := d.Get(\"tls_enabled\").(bool)");
        sb.AppendLine("\ttlsSkipVerify := d.Get(\"tls_skip_verify\").(bool)");
        sb.AppendLine();
        sb.AppendLine("\tclient, err := dw.NewClient(ctx,");
        sb.AppendLine("\t\tdw.WithHost(host),");
        sb.AppendLine("\t\tdw.WithPort(port),");
        sb.AppendLine("\t\tdw.WithCredentials(username, password),");
        sb.AppendLine("\t\tdw.WithTLS(tlsEnabled, tlsSkipVerify),");
        sb.AppendLine("\t)");
        sb.AppendLine("\tif err != nil {");
        sb.AppendLine("\t\treturn nil, diag.FromErr(err)");
        sb.AppendLine("\t}");
        sb.AppendLine();
        sb.AppendLine("\treturn client, nil");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateResourceGo(string shortName, TerraformResource resource)
    {
        var funcName = PascalCase(shortName);
        var sb = new StringBuilder(2048);
        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"context\"");
        sb.AppendLine();
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/diag\"");
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/helper/schema\"");
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/plugin\"");
        sb.AppendLine("\tdw \"github.com/datawarehouse/datawarehouse-go\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.Append("func resource").Append(funcName).AppendLine("() *schema.Resource {");
        sb.AppendLine("\treturn &schema.Resource{");
        sb.Append("\t\tDescription:   \"").Append(resource.Description).AppendLine("\",");
        sb.Append("\t\tCreateContext: resource").Append(funcName).AppendLine("Create,");
        sb.Append("\t\tReadContext:   resource").Append(funcName).AppendLine("Read,");
        sb.Append("\t\tUpdateContext: resource").Append(funcName).AppendLine("Update,");
        sb.Append("\t\tDeleteContext: resource").Append(funcName).AppendLine("Delete,");
        sb.AppendLine("\t\tImporter: &schema.ResourceImporter{");
        sb.AppendLine("\t\t\tStateContext: schema.ImportStatePassthroughContext,");
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t\tSchema: map[string]*schema.Schema{");
        foreach (var attr in resource.Schema)
        {
            AppendSchemaAttribute(sb, attr, "\t\t\t");
        }
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        sb.AppendLine();

        // CRUD stubs
        foreach (var op in new[] { "Create", "Read", "Update", "Delete" })
        {
            sb.Append("func resource").Append(funcName).Append(op)
              .AppendLine("(ctx context.Context, d *schema.ResourceData, meta interface{}) diag.Diagnostics {");
            sb.AppendLine("\tclient := meta.(*dw.Client)");
            sb.AppendLine("\t_ = client");
            if (op == "Create")
            {
                sb.AppendLine("\t// TODO: Call client API to create resource");
                sb.AppendLine("\td.SetId(\"generated-id\")");
            }
            else if (op == "Delete")
            {
                sb.AppendLine("\t// TODO: Call client API to delete resource");
                sb.AppendLine("\td.SetId(\"\")");
            }
            else
            {
                sb.Append("\t// TODO: Call client API to ").Append(op.ToLowerInvariant()).AppendLine(" resource");
            }
            sb.AppendLine("\treturn nil");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GenerateDataSourceGo(string shortName, TerraformDataSource dataSource)
    {
        var funcName = PascalCase(shortName);
        var sb = new StringBuilder(1024);
        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"context\"");
        sb.AppendLine();
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/diag\"");
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/helper/schema\"");
        sb.AppendLine("\t\"github.com/hashicorp/terraform-plugin-sdk/v2/plugin\"");
        sb.AppendLine("\tdw \"github.com/datawarehouse/datawarehouse-go\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.Append("func dataSource").Append(funcName).AppendLine("() *schema.Resource {");
        sb.AppendLine("\treturn &schema.Resource{");
        sb.Append("\t\tDescription: \"").Append(dataSource.Description).AppendLine("\",");
        sb.Append("\t\tReadContext: dataSource").Append(funcName).AppendLine("Read,");
        sb.AppendLine("\t\tSchema: map[string]*schema.Schema{");
        foreach (var attr in dataSource.Schema)
        {
            AppendSchemaAttribute(sb, attr, "\t\t\t");
        }
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.Append("func dataSource").Append(funcName)
          .AppendLine("Read(ctx context.Context, d *schema.ResourceData, meta interface{}) diag.Diagnostics {");
        sb.AppendLine("\tclient := meta.(*dw.Client)");
        sb.AppendLine("\t_ = client");
        sb.AppendLine("\t// TODO: Call client API to read data source");
        sb.AppendLine("\td.SetId(\"data-source-id\")");
        sb.AppendLine("\treturn nil");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendSchemaAttribute(StringBuilder sb, TerraformAttribute attr, string indent)
    {
        sb.Append(indent).Append('"').Append(attr.Name).AppendLine("\": {");
        sb.Append(indent).Append("\tType:        schema.").Append(GoSchemaType(attr.Type)).AppendLine(",");
        if (attr.Required) sb.Append(indent).AppendLine("\tRequired:    true,");
        if (attr.Optional) sb.Append(indent).AppendLine("\tOptional:    true,");
        if (attr.Computed) sb.Append(indent).AppendLine("\tComputed:    true,");
        if (attr.Sensitive) sb.Append(indent).AppendLine("\tSensitive:   true,");
        if (attr.ForceNew) sb.Append(indent).AppendLine("\tForceNew:    true,");
        if (attr.Default is not null) sb.Append(indent).Append("\tDefault:     ").Append(GoDefaultValue(attr.Default)).AppendLine(",");
        sb.Append(indent).Append("\tDescription: \"").Append(attr.Description).AppendLine("\",");
        sb.Append(indent).AppendLine("},");
    }

    private static string GoSchemaType(TerraformAttrType type) => type switch
    {
        TerraformAttrType.String => "TypeString",
        TerraformAttrType.Int => "TypeInt",
        TerraformAttrType.Bool => "TypeBool",
        TerraformAttrType.Float => "TypeFloat",
        TerraformAttrType.List => "TypeList",
        TerraformAttrType.Map => "TypeMap",
        TerraformAttrType.Block => "TypeList",
        _ => "TypeString"
    };

    private static string GoDefaultValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        int i => i.ToString(),
        string s => $"\"{s}\"",
        _ => $"\"{value}\""
    };

    private static string PascalCase(string snake)
    {
        var sb = new StringBuilder(snake.Length);
        var capitalize = true;
        foreach (var c in snake)
        {
            if (c == '_')
            {
                capitalize = true;
                continue;
            }
            sb.Append(capitalize ? char.ToUpperInvariant(c) : c);
            capitalize = false;
        }
        return sb.ToString();
    }
}
