using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DataWarehouse.SDK.Contracts.Interface;

/// <summary>
/// Descriptor for a gRPC service generated from plugin capabilities.
/// Used for runtime reflection-based serving without build-time protoc codegen.
/// </summary>
public sealed record GrpcServiceDescriptor
{
    /// <summary>Full service name (e.g., "datawarehouse.v1.StorageService").</summary>
    public required string FullServiceName { get; init; }

    /// <summary>Short service name.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Plugin ID that provides this service.</summary>
    public required string PluginId { get; init; }

    /// <summary>RPC method descriptors within this service.</summary>
    public IReadOnlyList<GrpcMethodDescriptor> Methods { get; init; } = Array.Empty<GrpcMethodDescriptor>();
}

/// <summary>
/// Descriptor for a single gRPC RPC method.
/// </summary>
public sealed record GrpcMethodDescriptor
{
    /// <summary>Method name (e.g., "Store", "Retrieve").</summary>
    public required string Name { get; init; }

    /// <summary>Input message type name.</summary>
    public required string InputType { get; init; }

    /// <summary>Output message type name.</summary>
    public required string OutputType { get; init; }

    /// <summary>Whether the server streams the response.</summary>
    public bool ServerStreaming { get; init; }

    /// <summary>Whether the client streams the request.</summary>
    public bool ClientStreaming { get; init; }

    /// <summary>Source capability name.</summary>
    public required string CapabilityName { get; init; }
}

/// <summary>
/// Generates Protocol Buffer (.proto) file content and runtime service descriptors
/// from a <see cref="DynamicApiModel"/>. Supports server streaming for query capabilities.
/// </summary>
/// <remarks>
/// <para>Type mapping from API model to protobuf:</para>
/// <list type="bullet">
///   <item><description>string -> string</description></item>
///   <item><description>int -> int32</description></item>
///   <item><description>long -> int64</description></item>
///   <item><description>bool -> bool</description></item>
///   <item><description>bytes -> bytes</description></item>
///   <item><description>datetime -> google.protobuf.Timestamp</description></item>
///   <item><description>object -> google.protobuf.Struct</description></item>
/// </list>
/// </remarks>
public sealed class GrpcServiceGenerator
{
    private const string PackageName = "datawarehouse.v1";
    private const string ProtoSyntax = "proto3";

    /// <summary>
    /// Generates a complete .proto file from the dynamic API model.
    /// </summary>
    /// <param name="model">The API model to generate from.</param>
    /// <returns>The .proto file content as a string.</returns>
    public string GenerateProtoFile(DynamicApiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var sb = new StringBuilder(4096);

        WriteHeader(sb);
        WriteImports(sb, model);
        WritePackage(sb);
        WriteOptions(sb);

        // Group endpoints by plugin to generate one service per plugin
        var pluginGroups = model.Endpoints
            .GroupBy(e => e.PluginId)
            .OrderBy(g => g.Key);

        foreach (var group in pluginGroups)
        {
            WriteService(sb, group.Key, group.ToList());
        }

        // Write message types
        WriteMessageTypes(sb, model);

        // Write common messages
        WriteCommonMessages(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Generates runtime gRPC service descriptors for reflection-based serving.
    /// </summary>
    /// <param name="model">The API model to generate from.</param>
    /// <returns>A list of service descriptors.</returns>
    public IReadOnlyList<GrpcServiceDescriptor> GenerateServiceDescriptors(DynamicApiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var descriptors = new List<GrpcServiceDescriptor>();
        var pluginGroups = model.Endpoints.GroupBy(e => e.PluginId);

        foreach (var group in pluginGroups)
        {
            var serviceName = ToServiceName(group.Key);
            var methods = new List<GrpcMethodDescriptor>();

            foreach (var endpoint in group)
            {
                var methodName = ToMethodName(endpoint);
                var isStreaming = endpoint.IsStreaming ||
                    endpoint.CapabilityName.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                    endpoint.CapabilityName.Contains("search", StringComparison.OrdinalIgnoreCase);

                methods.Add(new GrpcMethodDescriptor
                {
                    Name = methodName,
                    InputType = $"{methodName}Request",
                    OutputType = isStreaming ? $"{methodName}ResponseBatch" : $"{methodName}Response",
                    ServerStreaming = isStreaming,
                    ClientStreaming = false,
                    CapabilityName = endpoint.CapabilityName
                });
            }

            descriptors.Add(new GrpcServiceDescriptor
            {
                FullServiceName = $"{PackageName}.{serviceName}",
                ServiceName = serviceName,
                PluginId = group.Key,
                Methods = methods.AsReadOnly()
            });
        }

        return descriptors.AsReadOnly();
    }

    #region Proto Generation

    private static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine("// Auto-generated by DataWarehouse DynamicApiGenerator");
        sb.AppendLine("// DO NOT EDIT - regenerated when plugin capabilities change");
        sb.AppendLine();
        sb.AppendLine($"syntax = \"{ProtoSyntax}\";");
        sb.AppendLine();
    }

    private static void WriteImports(StringBuilder sb, DynamicApiModel model)
    {
        var needsTimestamp = model.DataTypes.Any(dt =>
            dt.Properties.Values.Any(p =>
                p.Type.Equals("datetime", StringComparison.OrdinalIgnoreCase)));
        var needsStruct = model.DataTypes.Any(dt =>
            dt.Properties.Values.Any(p =>
                p.Type.Equals("object", StringComparison.OrdinalIgnoreCase)));

        if (needsTimestamp)
            sb.AppendLine("import \"google/protobuf/timestamp.proto\";");
        if (needsStruct)
            sb.AppendLine("import \"google/protobuf/struct.proto\";");
        sb.AppendLine("import \"google/protobuf/empty.proto\";");
        sb.AppendLine();
    }

    private static void WritePackage(StringBuilder sb)
    {
        sb.AppendLine($"package {PackageName};");
        sb.AppendLine();
    }

    private static void WriteOptions(StringBuilder sb)
    {
        sb.AppendLine("option csharp_namespace = \"DataWarehouse.Grpc.V1\";");
        sb.AppendLine("option go_package = \"datawarehouse/v1\";");
        sb.AppendLine();
    }

    private static void WriteService(StringBuilder sb, string pluginId, List<ApiEndpoint> endpoints)
    {
        var serviceName = ToServiceName(pluginId);
        sb.AppendLine($"// Service for plugin: {pluginId}");
        sb.AppendLine($"service {serviceName} {{");

        foreach (var endpoint in endpoints)
        {
            var methodName = ToMethodName(endpoint);
            var isStreaming = endpoint.IsStreaming ||
                endpoint.CapabilityName.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                endpoint.CapabilityName.Contains("search", StringComparison.OrdinalIgnoreCase);

            var returnType = isStreaming ? $"stream {methodName}Response" : $"{methodName}Response";
            sb.AppendLine($"  // {endpoint.Description}");
            sb.AppendLine($"  rpc {methodName}({methodName}Request) returns ({returnType});");
        }

        // Always add a GetCapabilities RPC
        sb.AppendLine($"  // List available capabilities for this plugin");
        sb.AppendLine($"  rpc GetCapabilities(google.protobuf.Empty) returns (CapabilityList);");

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteMessageTypes(StringBuilder sb, DynamicApiModel model)
    {
        var writtenTypes = new HashSet<string>();

        foreach (var endpoint in model.Endpoints)
        {
            var methodName = ToMethodName(endpoint);

            // Request message
            var reqName = $"{methodName}Request";
            if (writtenTypes.Add(reqName))
            {
                sb.AppendLine($"message {reqName} {{");
                if (endpoint.RequestBody != null)
                {
                    WriteMessageFields(sb, endpoint.RequestBody);
                }
                else if (endpoint.Parameters.Count > 0)
                {
                    int fieldNum = 1;
                    foreach (var param in endpoint.Parameters)
                    {
                        sb.AppendLine($"  {MapToProtoType(param.Type)} {ToSnakeCase(param.Name)} = {fieldNum++};");
                    }
                }
                else
                {
                    sb.AppendLine("  // No parameters");
                }
                sb.AppendLine("}");
                sb.AppendLine();
            }

            // Response message
            var respName = $"{methodName}Response";
            if (writtenTypes.Add(respName))
            {
                sb.AppendLine($"message {respName} {{");
                if (endpoint.ResponseBody != null)
                {
                    WriteMessageFields(sb, endpoint.ResponseBody);
                }
                else
                {
                    sb.AppendLine("  bool success = 1;");
                    sb.AppendLine("  string message = 2;");
                }
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }
    }

    private static void WriteMessageFields(StringBuilder sb, ApiDataType dataType)
    {
        int fieldNum = 1;
        foreach (var prop in dataType.Properties.Values)
        {
            var protoType = MapToProtoType(prop.Type);
            var fieldName = ToSnakeCase(prop.Name);

            if (prop.Type.Equals("array", StringComparison.OrdinalIgnoreCase))
            {
                var elementType = prop.ArrayElementType != null
                    ? MapToProtoType(prop.ArrayElementType)
                    : "bytes";
                sb.AppendLine($"  repeated {elementType} {fieldName} = {fieldNum++};");
            }
            else
            {
                sb.AppendLine($"  {protoType} {fieldName} = {fieldNum++};");
            }
        }
    }

    private static void WriteCommonMessages(StringBuilder sb)
    {
        sb.AppendLine("// Common messages");
        sb.AppendLine();
        sb.AppendLine("message CapabilityList {");
        sb.AppendLine("  repeated CapabilityInfo capabilities = 1;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("message CapabilityInfo {");
        sb.AppendLine("  string capability_id = 1;");
        sb.AppendLine("  string display_name = 2;");
        sb.AppendLine("  string description = 3;");
        sb.AppendLine("  string category = 4;");
        sb.AppendLine("  bool is_available = 5;");
        sb.AppendLine("  repeated string tags = 6;");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    #endregion

    #region Type Mapping

    private static string MapToProtoType(string apiType)
    {
        return apiType.ToLowerInvariant() switch
        {
            "string" => "string",
            "int" => "int32",
            "long" => "int64",
            "bool" => "bool",
            "bytes" => "bytes",
            "datetime" => "google.protobuf.Timestamp",
            "object" => "google.protobuf.Struct",
            "array" => "bytes", // fallback; repeated handled separately
            _ => "string"
        };
    }

    private static string ToServiceName(string pluginId)
    {
        var parts = pluginId.Split('.', '-', '_');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : ""))) + "Service";
    }

    private static string ToMethodName(ApiEndpoint endpoint)
    {
        var parts = endpoint.CapabilityName.Split('.', '-', '_');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : "")));
    }

    private static string ToSnakeCase(string input)
    {
        var result = new StringBuilder(input.Length + 4);
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c) && i > 0)
            {
                result.Append('_');
            }
            result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }

    #endregion
}
