using System;
using System.Collections.Generic;
using System.Text;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

// =============================================================================
// Pulumi Resource Mapping
// =============================================================================

/// <summary>
/// Maps a Terraform resource type to its Pulumi class across all languages.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Pulumi provider (ECOS-14)")]
public sealed record PulumiResourceMapping
{
    /// <summary>Terraform resource type name (e.g., "datawarehouse_instance").</summary>
    public required string TerraformType { get; init; }

    /// <summary>Pulumi module-qualified class name (e.g., "DataWarehouse.Instance").</summary>
    public required string PulumiClassName { get; init; }

    /// <summary>TypeScript import path (e.g., "datawarehouse.Instance").</summary>
    public required string TypeScriptName { get; init; }

    /// <summary>Python import path (e.g., "pulumi_datawarehouse.Instance").</summary>
    public required string PythonName { get; init; }

    /// <summary>Go type path (e.g., "datawarehouse.Instance").</summary>
    public required string GoName { get; init; }

    /// <summary>C# type path (e.g., "DataWarehouse.Pulumi.Instance").</summary>
    public required string DotNetName { get; init; }
}

// =============================================================================
// Pulumi Provider Specification
// =============================================================================

/// <summary>
/// Defines the Pulumi bridge specification that wraps the Terraform provider
/// via <c>pulumi-terraform-bridge</c>. Generates bridge configuration, resource
/// mappings, and multi-language examples for Python, TypeScript, Go, and C#.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Pulumi provider (ECOS-14)")]
public static class PulumiProviderSpecification
{
    /// <summary>Pulumi provider name.</summary>
    public const string ProviderName = "datawarehouse";

    /// <summary>Terraform provider registry source that the bridge wraps.</summary>
    public const string TerraformProviderSource = "registry.terraform.io/datawarehouse/datawarehouse";

    /// <summary>Provider version.</summary>
    public const string Version = "1.0.0";

    /// <summary>JavaScript/TypeScript npm package name.</summary>
    public const string JavaScriptPackage = "@datawarehouse/pulumi";

    /// <summary>Python PyPI package name.</summary>
    public const string PythonPackage = "pulumi_datawarehouse";

    /// <summary>Go module path.</summary>
    public const string GoModule = "github.com/datawarehouse/pulumi-datawarehouse/sdk/go/datawarehouse";

    /// <summary>C#/.NET NuGet package name.</summary>
    public const string DotNetPackage = "DataWarehouse.Pulumi";

    // -------------------------------------------------------------------------
    // Resource Mapping
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the mapping from all 6 Terraform resources to Pulumi class names
    /// in each supported language.
    /// </summary>
    public static IReadOnlyList<PulumiResourceMapping> GetResourceMappings()
    {
        return new PulumiResourceMapping[]
        {
            new()
            {
                TerraformType = "datawarehouse_instance",
                PulumiClassName = "DataWarehouse.Instance",
                TypeScriptName = "datawarehouse.Instance",
                PythonName = "pulumi_datawarehouse.Instance",
                GoName = "datawarehouse.Instance",
                DotNetName = "DataWarehouse.Pulumi.Instance"
            },
            new()
            {
                TerraformType = "datawarehouse_vde",
                PulumiClassName = "DataWarehouse.Vde",
                TypeScriptName = "datawarehouse.Vde",
                PythonName = "pulumi_datawarehouse.Vde",
                GoName = "datawarehouse.Vde",
                DotNetName = "DataWarehouse.Pulumi.Vde"
            },
            new()
            {
                TerraformType = "datawarehouse_user",
                PulumiClassName = "DataWarehouse.User",
                TypeScriptName = "datawarehouse.User",
                PythonName = "pulumi_datawarehouse.User",
                GoName = "datawarehouse.User",
                DotNetName = "DataWarehouse.Pulumi.User"
            },
            new()
            {
                TerraformType = "datawarehouse_policy",
                PulumiClassName = "DataWarehouse.Policy",
                TypeScriptName = "datawarehouse.Policy",
                PythonName = "pulumi_datawarehouse.Policy",
                GoName = "datawarehouse.Policy",
                DotNetName = "DataWarehouse.Pulumi.Policy"
            },
            new()
            {
                TerraformType = "datawarehouse_plugin",
                PulumiClassName = "DataWarehouse.Plugin",
                TypeScriptName = "datawarehouse.Plugin",
                PythonName = "pulumi_datawarehouse.Plugin",
                GoName = "datawarehouse.Plugin",
                DotNetName = "DataWarehouse.Pulumi.Plugin"
            },
            new()
            {
                TerraformType = "datawarehouse_replication_topology",
                PulumiClassName = "DataWarehouse.ReplicationTopology",
                TypeScriptName = "datawarehouse.ReplicationTopology",
                PythonName = "pulumi_datawarehouse.ReplicationTopology",
                GoName = "datawarehouse.ReplicationTopology",
                DotNetName = "DataWarehouse.Pulumi.ReplicationTopology"
            }
        };
    }

    // -------------------------------------------------------------------------
    // Bridge Configuration Generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates the <c>pulumi-terraform-bridge</c> configuration files including
    /// the bridge YAML manifest and Go bridge code.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GenerateBridgeConfig()
    {
        var files = new Dictionary<string, string>(4);

        files["bridge.yaml"] = GenerateBridgeYaml();
        files["provider.go"] = GenerateBridgeProviderGo();
        files["resources.go"] = GenerateBridgeResourcesGo();

        return files;
    }

    private static string GenerateBridgeYaml()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("# Pulumi Terraform Bridge Configuration");
        sb.AppendLine("# Auto-generated by DataWarehouse SDK");
        sb.AppendLine();
        sb.AppendLine("name: datawarehouse");
        sb.Append("version: ").AppendLine(Version);
        sb.AppendLine();
        sb.AppendLine("terraform:");
        sb.Append("  source: ").AppendLine(TerraformProviderSource);
        sb.Append("  version: ").AppendLine(TerraformProviderSpecification.ProviderVersion);
        sb.AppendLine();
        sb.AppendLine("languages:");
        sb.AppendLine("  nodejs:");
        sb.Append("    package: ").AppendLine(JavaScriptPackage);
        sb.AppendLine("    compatibility: tfbridge20");
        sb.AppendLine("  python:");
        sb.Append("    package: ").AppendLine(PythonPackage);
        sb.AppendLine("    compatibility: tfbridge20");
        sb.AppendLine("  go:");
        sb.Append("    module: ").AppendLine(GoModule);
        sb.AppendLine("  csharp:");
        sb.Append("    package: ").AppendLine(DotNetPackage);
        sb.AppendLine("    rootNamespace: DataWarehouse.Pulumi");
        sb.AppendLine();
        sb.AppendLine("resources:");
        foreach (var mapping in GetResourceMappings())
        {
            sb.Append("  ").Append(mapping.TerraformType).AppendLine(":");
            sb.Append("    tok: datawarehouse:index:").AppendLine(mapping.PulumiClassName.Replace("DataWarehouse.", ""));
        }

        return sb.ToString();
    }

    private static string GenerateBridgeProviderGo()
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"github.com/pulumi/pulumi-terraform-bridge/v3/pkg/tfbridge\"");
        sb.AppendLine("\ttf \"github.com/datawarehouse/terraform-provider-datawarehouse\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("func main() {");
        sb.AppendLine("\ttfbridge.Main(context.Background(), \"datawarehouse\", Provider())");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// Provider returns the Pulumi bridge provider info.");
        sb.AppendLine("func Provider() tfbridge.ProviderInfo {");
        sb.AppendLine("\treturn tfbridge.ProviderInfo{");
        sb.AppendLine("\t\tP:           tf.Provider(),");
        sb.AppendLine("\t\tName:        \"datawarehouse\",");
        sb.Append("\t\tVersion:     \"").Append(Version).AppendLine("\",");
        sb.AppendLine("\t\tGitHubOrg:   \"datawarehouse\",");
        sb.AppendLine("\t\tResources:   bridgeResources(),");
        sb.AppendLine("\t\tJavaScript: &tfbridge.JavaScriptInfo{");
        sb.Append("\t\t\tPackageName: \"").Append(JavaScriptPackage).AppendLine("\",");
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t\tPython: &tfbridge.PythonInfo{");
        sb.Append("\t\t\tPackageName: \"").Append(PythonPackage).AppendLine("\",");
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t\tGolang: &tfbridge.GolangInfo{");
        sb.Append("\t\t\tImportBasePath: \"").Append(GoModule).AppendLine("\",");
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t\tCSharp: &tfbridge.CSharpInfo{");
        sb.Append("\t\t\tPackageReferences: map[string]string{\"").Append(DotNetPackage).AppendLine("\": \"1.0.0\"},");
        sb.AppendLine("\t\t\tRootNamespace:     \"DataWarehouse.Pulumi\",");
        sb.AppendLine("\t\t},");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateBridgeResourcesGo()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import \"github.com/pulumi/pulumi-terraform-bridge/v3/pkg/tfbridge\"");
        sb.AppendLine();
        sb.AppendLine("func bridgeResources() map[string]*tfbridge.ResourceInfo {");
        sb.AppendLine("\treturn map[string]*tfbridge.ResourceInfo{");
        foreach (var mapping in GetResourceMappings())
        {
            var tok = mapping.PulumiClassName.Replace("DataWarehouse.", "");
            sb.Append("\t\t\"").Append(mapping.TerraformType).AppendLine("\": {");
            sb.Append("\t\t\tTok: \"datawarehouse:index:").Append(tok).AppendLine("\",");
            sb.AppendLine("\t\t},");
        }
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Pulumi Examples (4 languages)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates example Pulumi programs in Python, TypeScript, Go, and C#
    /// demonstrating how to create an instance, VDE, and replication topology.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GeneratePulumiExamples()
    {
        var examples = new Dictionary<string, string>(4)
        {
            ["example_python.py"] = GeneratePythonExample(),
            ["example_typescript.ts"] = GenerateTypeScriptExample(),
            ["example_go.go"] = GenerateGoExample(),
            ["example_csharp.cs"] = GenerateCSharpExample()
        };
        return examples;
    }

    private static string GeneratePythonExample()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("\"\"\"DataWarehouse Pulumi example — Python.\"\"\"");
        sb.AppendLine("import pulumi");
        sb.AppendLine("import pulumi_datawarehouse as dw");
        sb.AppendLine();
        sb.AppendLine("# Create a DataWarehouse instance");
        sb.AppendLine("instance = dw.Instance(\"my-dw\",");
        sb.AppendLine("    name=\"production\",");
        sb.AppendLine("    storage_size_gb=500,");
        sb.AppendLine("    replication_factor=3,");
        sb.AppendLine("    operational_profile=\"Balanced\",");
        sb.AppendLine("    plugins_enabled=[\"UltimateStorage\", \"UltimateEncryption\"],");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("# Create a VDE within the instance");
        sb.AppendLine("vde = dw.Vde(\"main-vde\",");
        sb.AppendLine("    instance_id=instance.id,");
        sb.AppendLine("    name=\"primary\",");
        sb.AppendLine("    block_size=4096,");
        sb.AppendLine("    encryption_enabled=True,");
        sb.AppendLine("    compression_codec=\"brotli\",");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("# Configure replication topology");
        sb.AppendLine("topology = dw.ReplicationTopology(\"repl\",");
        sb.AppendLine("    instance_id=instance.id,");
        sb.AppendLine("    mode=\"active-active\",");
        sb.AppendLine("    sync_interval_seconds=15,");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("pulumi.export(\"instance_id\", instance.id)");
        sb.AppendLine("pulumi.export(\"vde_id\", vde.id)");
        return sb.ToString();
    }

    private static string GenerateTypeScriptExample()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("import * as dw from \"@datawarehouse/pulumi\";");
        sb.AppendLine();
        sb.AppendLine("// Create a DataWarehouse instance");
        sb.AppendLine("const instance = new dw.Instance(\"my-dw\", {");
        sb.AppendLine("    name: \"production\",");
        sb.AppendLine("    storageSizeGb: 500,");
        sb.AppendLine("    replicationFactor: 3,");
        sb.AppendLine("    operationalProfile: \"Balanced\",");
        sb.AppendLine("    pluginsEnabled: [\"UltimateStorage\", \"UltimateEncryption\"],");
        sb.AppendLine("});");
        sb.AppendLine();
        sb.AppendLine("// Create a VDE within the instance");
        sb.AppendLine("const vde = new dw.Vde(\"main-vde\", {");
        sb.AppendLine("    instanceId: instance.id,");
        sb.AppendLine("    name: \"primary\",");
        sb.AppendLine("    blockSize: 4096,");
        sb.AppendLine("    encryptionEnabled: true,");
        sb.AppendLine("    compressionCodec: \"brotli\",");
        sb.AppendLine("});");
        sb.AppendLine();
        sb.AppendLine("// Configure replication topology");
        sb.AppendLine("const topology = new dw.ReplicationTopology(\"repl\", {");
        sb.AppendLine("    instanceId: instance.id,");
        sb.AppendLine("    mode: \"active-active\",");
        sb.AppendLine("    syncIntervalSeconds: 15,");
        sb.AppendLine("});");
        sb.AppendLine();
        sb.AppendLine("export const instanceId = instance.id;");
        sb.AppendLine("export const vdeId = vde.id;");
        return sb.ToString();
    }

    private static string GenerateGoExample()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("package main");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"github.com/pulumi/pulumi/sdk/v3/go/pulumi\"");
        sb.AppendLine("\tdw \"github.com/datawarehouse/pulumi-datawarehouse/sdk/go/datawarehouse\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("func main() {");
        sb.AppendLine("\tpulumi.Run(func(ctx *pulumi.Context) error {");
        sb.AppendLine("\t\t// Create a DataWarehouse instance");
        sb.AppendLine("\t\tinstance, err := dw.NewInstance(ctx, \"my-dw\", &dw.InstanceArgs{");
        sb.AppendLine("\t\t\tName:               pulumi.String(\"production\"),");
        sb.AppendLine("\t\t\tStorageSizeGb:       pulumi.Int(500),");
        sb.AppendLine("\t\t\tReplicationFactor:   pulumi.Int(3),");
        sb.AppendLine("\t\t\tOperationalProfile:  pulumi.String(\"Balanced\"),");
        sb.AppendLine("\t\t\tPluginsEnabled:      pulumi.StringArray{pulumi.String(\"UltimateStorage\"), pulumi.String(\"UltimateEncryption\")},");
        sb.AppendLine("\t\t})");
        sb.AppendLine("\t\tif err != nil { return err }");
        sb.AppendLine();
        sb.AppendLine("\t\t// Create a VDE within the instance");
        sb.AppendLine("\t\tvde, err := dw.NewVde(ctx, \"main-vde\", &dw.VdeArgs{");
        sb.AppendLine("\t\t\tInstanceId:        instance.ID(),");
        sb.AppendLine("\t\t\tName:              pulumi.String(\"primary\"),");
        sb.AppendLine("\t\t\tBlockSize:         pulumi.Int(4096),");
        sb.AppendLine("\t\t\tEncryptionEnabled: pulumi.Bool(true),");
        sb.AppendLine("\t\t\tCompressionCodec:  pulumi.String(\"brotli\"),");
        sb.AppendLine("\t\t})");
        sb.AppendLine("\t\tif err != nil { return err }");
        sb.AppendLine();
        sb.AppendLine("\t\t// Configure replication topology");
        sb.AppendLine("\t\t_, err = dw.NewReplicationTopology(ctx, \"repl\", &dw.ReplicationTopologyArgs{");
        sb.AppendLine("\t\t\tInstanceId:          instance.ID(),");
        sb.AppendLine("\t\t\tMode:                pulumi.String(\"active-active\"),");
        sb.AppendLine("\t\t\tSyncIntervalSeconds: pulumi.Int(15),");
        sb.AppendLine("\t\t})");
        sb.AppendLine("\t\tif err != nil { return err }");
        sb.AppendLine();
        sb.AppendLine("\t\tctx.Export(\"instanceId\", instance.ID())");
        sb.AppendLine("\t\tctx.Export(\"vdeId\", vde.ID())");
        sb.AppendLine("\t\treturn nil");
        sb.AppendLine("\t})");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateCSharpExample()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("using Pulumi;");
        sb.AppendLine("using DataWarehouse.Pulumi;");
        sb.AppendLine();
        sb.AppendLine("return await Deployment.RunAsync(() =>");
        sb.AppendLine("{");
        sb.AppendLine("    // Create a DataWarehouse instance");
        sb.AppendLine("    var instance = new Instance(\"my-dw\", new InstanceArgs");
        sb.AppendLine("    {");
        sb.AppendLine("        Name = \"production\",");
        sb.AppendLine("        StorageSizeGb = 500,");
        sb.AppendLine("        ReplicationFactor = 3,");
        sb.AppendLine("        OperationalProfile = \"Balanced\",");
        sb.AppendLine("        PluginsEnabled = { \"UltimateStorage\", \"UltimateEncryption\" },");
        sb.AppendLine("    });");
        sb.AppendLine();
        sb.AppendLine("    // Create a VDE within the instance");
        sb.AppendLine("    var vde = new Vde(\"main-vde\", new VdeArgs");
        sb.AppendLine("    {");
        sb.AppendLine("        InstanceId = instance.Id,");
        sb.AppendLine("        Name = \"primary\",");
        sb.AppendLine("        BlockSize = 4096,");
        sb.AppendLine("        EncryptionEnabled = true,");
        sb.AppendLine("        CompressionCodec = \"brotli\",");
        sb.AppendLine("    });");
        sb.AppendLine();
        sb.AppendLine("    // Configure replication topology");
        sb.AppendLine("    var topology = new ReplicationTopology(\"repl\", new ReplicationTopologyArgs");
        sb.AppendLine("    {");
        sb.AppendLine("        InstanceId = instance.Id,");
        sb.AppendLine("        Mode = \"active-active\",");
        sb.AppendLine("        SyncIntervalSeconds = 15,");
        sb.AppendLine("    });");
        sb.AppendLine();
        sb.AppendLine("    return new Dictionary<string, object?>");
        sb.AppendLine("    {");
        sb.AppendLine("        [\"instanceId\"] = instance.Id,");
        sb.AppendLine("        [\"vdeId\"] = vde.Id,");
        sb.AppendLine("    };");
        sb.AppendLine("});");
        return sb.ToString();
    }
}
