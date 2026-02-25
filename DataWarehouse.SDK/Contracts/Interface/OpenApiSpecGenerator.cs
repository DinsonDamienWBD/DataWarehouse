using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.SDK.Contracts.Interface;

/// <summary>
/// Generates an OpenAPI 3.1 specification (JSON) from a <see cref="DynamicApiModel"/>.
/// The generated spec includes paths, schemas, security definitions, and plugin-based tags.
/// Served at /api/openapi.json with a Swagger UI hint at /api/docs.
/// </summary>
public sealed class OpenApiSpecGenerator
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _baseUrl;
    private readonly string _title;

    /// <summary>
    /// Initializes a new instance of <see cref="OpenApiSpecGenerator"/>.
    /// </summary>
    /// <param name="baseUrl">The server base URL (e.g., "https://localhost:5001").</param>
    /// <param name="title">The API title shown in the spec.</param>
    public OpenApiSpecGenerator(string baseUrl = "https://localhost:5001", string title = "DataWarehouse API")
    {
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _title = title ?? throw new ArgumentNullException(nameof(title));
    }

    /// <summary>
    /// Generates an OpenAPI 3.1 JSON specification string from the given API model.
    /// </summary>
    /// <param name="model">The dynamic API model containing endpoints and data types.</param>
    /// <returns>A JSON string containing the full OpenAPI 3.1 specification.</returns>
    public string Generate(DynamicApiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        using var stream = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        WriteMetadata(writer, model);
        WriteServers(writer);
        WritePaths(writer, model);
        WriteComponents(writer, model);
        WriteSecurity(writer);
        WriteTags(writer, model);

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteMetadata(Utf8JsonWriter writer, DynamicApiModel model)
    {
        writer.WriteString("openapi", "3.1.0");

        writer.WriteStartObject("info");
        writer.WriteString("title", _title);
        writer.WriteString("description",
            $"Auto-generated API from {model.PluginCount} registered plugins. " +
            $"Generated at {model.GeneratedAt:O}.");
        writer.WriteString("version", model.ApiVersion);

        writer.WriteStartObject("contact");
        writer.WriteString("name", "DataWarehouse System");
        writer.WriteEndObject();

        writer.WriteStartObject("license");
        writer.WriteString("name", "Apache 2.0");
        writer.WriteString("url", "https://www.apache.org/licenses/LICENSE-2.0");
        writer.WriteEndObject();

        writer.WriteEndObject(); // info
    }

    private void WriteServers(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("servers");

        writer.WriteStartObject();
        writer.WriteString("url", _baseUrl);
        writer.WriteString("description", "Primary API server");
        writer.WriteEndObject();

        writer.WriteEndArray();
    }

    private static void WritePaths(Utf8JsonWriter writer, DynamicApiModel model)
    {
        writer.WriteStartObject("paths");

        // Group endpoints by path to combine methods under one path object
        var pathGroups = model.Endpoints
            .Where(e => !e.IsStreaming)
            .GroupBy(e => e.Path);

        foreach (var group in pathGroups)
        {
            writer.WriteStartObject(group.Key);

            foreach (var endpoint in group)
            {
                WriteOperation(writer, endpoint);
            }

            writer.WriteEndObject();
        }

        // Streaming endpoints get documented with a note
        var streamingEndpoints = model.Endpoints.Where(e => e.IsStreaming).ToList();
        foreach (var endpoint in streamingEndpoints)
        {
            writer.WriteStartObject(endpoint.Path);
            writer.WriteStartObject("get");
            writer.WriteString("operationId", endpoint.OperationId);
            writer.WriteString("summary", endpoint.Description ?? endpoint.CapabilityName);
            writer.WriteString("description",
                $"WebSocket streaming endpoint. Connect via ws:// or wss:// protocol. " +
                $"Plugin: {endpoint.PluginId}, Capability: {endpoint.CapabilityName}");

            writer.WriteStartArray("tags");
            writer.WriteStringValue(endpoint.Tag);
            writer.WriteStringValue("streaming");
            writer.WriteEndArray();

            WriteResponses(writer, endpoint);
            writer.WriteEndObject(); // get
            writer.WriteEndObject(); // path
        }

        writer.WriteEndObject(); // paths
    }

    private static void WriteOperation(Utf8JsonWriter writer, ApiEndpoint endpoint)
    {
        var method = endpoint.Method.ToString().ToLowerInvariant();
        writer.WriteStartObject(method);

        writer.WriteString("operationId", endpoint.OperationId);
        writer.WriteString("summary", endpoint.Description ?? endpoint.CapabilityName);
        writer.WriteString("description",
            $"Plugin: {endpoint.PluginId} | Capability: {endpoint.CapabilityName}");

        // Tags
        writer.WriteStartArray("tags");
        writer.WriteStringValue(endpoint.Tag);
        writer.WriteEndArray();

        // Parameters
        if (endpoint.Parameters.Count > 0)
        {
            writer.WriteStartArray("parameters");
            foreach (var param in endpoint.Parameters)
            {
                WriteParameter(writer, param);
            }
            writer.WriteEndArray();
        }

        // Request body
        if (endpoint.RequestBody != null)
        {
            WriteRequestBody(writer, endpoint.RequestBody);
        }

        // Responses
        WriteResponses(writer, endpoint);

        // Security
        writer.WriteStartArray("security");
        writer.WriteStartObject();
        writer.WriteStartArray("bearerAuth");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteEndObject(); // method
    }

    private static void WriteParameter(Utf8JsonWriter writer, ApiParameter param)
    {
        writer.WriteStartObject();
        writer.WriteString("name", param.Name);
        writer.WriteString("in", param.In switch
        {
            ApiParameterLocation.Path => "path",
            ApiParameterLocation.Header => "header",
            _ => "query"
        });
        writer.WriteBoolean("required", param.Required);
        if (param.Description != null)
        {
            writer.WriteString("description", param.Description);
        }

        writer.WriteStartObject("schema");
        WriteJsonSchemaType(writer, param.Type);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteRequestBody(Utf8JsonWriter writer, ApiDataType bodyType)
    {
        writer.WriteStartObject("requestBody");
        writer.WriteBoolean("required", true);
        writer.WriteStartObject("content");
        writer.WriteStartObject("application/json");

        writer.WriteStartObject("schema");
        writer.WriteString("$ref", $"#/components/schemas/{bodyType.Name}");
        writer.WriteEndObject();

        writer.WriteEndObject(); // application/json
        writer.WriteEndObject(); // content
        writer.WriteEndObject(); // requestBody
    }

    private static void WriteResponses(Utf8JsonWriter writer, ApiEndpoint endpoint)
    {
        writer.WriteStartObject("responses");

        // 200 OK
        writer.WriteStartObject("200");
        writer.WriteString("description", "Successful operation");
        if (endpoint.ResponseBody != null)
        {
            writer.WriteStartObject("content");
            writer.WriteStartObject("application/json");
            writer.WriteStartObject("schema");
            writer.WriteString("$ref", $"#/components/schemas/{endpoint.ResponseBody.Name}");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject(); // 200

        // 400 Bad Request
        writer.WriteStartObject("400");
        writer.WriteString("description", "Bad request - invalid parameters or body");
        WriteErrorResponseContent(writer);
        writer.WriteEndObject();

        // 401 Unauthorized
        writer.WriteStartObject("401");
        writer.WriteString("description", "Unauthorized - missing or invalid authentication");
        WriteErrorResponseContent(writer);
        writer.WriteEndObject();

        // 404 Not Found
        writer.WriteStartObject("404");
        writer.WriteString("description", "Resource not found");
        WriteErrorResponseContent(writer);
        writer.WriteEndObject();

        // 500 Internal Server Error
        writer.WriteStartObject("500");
        writer.WriteString("description", "Internal server error");
        WriteErrorResponseContent(writer);
        writer.WriteEndObject();

        writer.WriteEndObject(); // responses
    }

    private static void WriteErrorResponseContent(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("content");
        writer.WriteStartObject("application/json");
        writer.WriteStartObject("schema");
        writer.WriteString("$ref", "#/components/schemas/ErrorResponse");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteComponents(Utf8JsonWriter writer, DynamicApiModel model)
    {
        writer.WriteStartObject("components");

        // Schemas
        writer.WriteStartObject("schemas");

        // Collect all referenced types from endpoints
        var referencedTypes = new Dictionary<string, ApiDataType>();
        foreach (var dt in model.DataTypes)
        {
            referencedTypes[dt.Name] = dt;
        }
        foreach (var ep in model.Endpoints)
        {
            if (ep.RequestBody != null) referencedTypes[ep.RequestBody.Name] = ep.RequestBody;
            if (ep.ResponseBody != null) referencedTypes[ep.ResponseBody.Name] = ep.ResponseBody;
        }

        foreach (var dataType in referencedTypes.Values)
        {
            WriteSchemaObject(writer, dataType);
        }

        writer.WriteEndObject(); // schemas

        // Security schemes
        writer.WriteStartObject("securitySchemes");
        writer.WriteStartObject("bearerAuth");
        writer.WriteString("type", "http");
        writer.WriteString("scheme", "bearer");
        writer.WriteString("bearerFormat", "JWT");
        writer.WriteString("description", "JWT Bearer token authentication");
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndObject(); // components
    }

    private static void WriteSchemaObject(Utf8JsonWriter writer, ApiDataType dataType)
    {
        writer.WriteStartObject(dataType.Name);
        writer.WriteString("type", "object");
        if (dataType.Description != null)
        {
            writer.WriteString("description", dataType.Description);
        }

        if (dataType.Properties.Count > 0)
        {
            writer.WriteStartObject("properties");
            foreach (var prop in dataType.Properties.Values)
            {
                writer.WriteStartObject(prop.Name);
                WriteJsonSchemaType(writer, prop.Type);
                if (prop.Nullable)
                {
                    WriteNullable(writer);
                }
                if (prop.Description != null)
                {
                    writer.WriteString("description", prop.Description);
                }
                if (prop.Type == "array" && prop.ArrayElementType != null)
                {
                    writer.WriteStartObject("items");
                    WriteJsonSchemaType(writer, prop.ArrayElementType);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject(); // properties

            // Required fields (non-nullable properties)
            var required = dataType.Properties.Values.Where(p => !p.Nullable).Select(p => p.Name).ToList();
            if (required.Count > 0)
            {
                writer.WriteStartArray("required");
                foreach (var r in required)
                {
                    writer.WriteStringValue(r);
                }
                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteJsonSchemaType(Utf8JsonWriter writer, string type)
    {
        switch (type.ToLowerInvariant())
        {
            case "string":
                writer.WriteString("type", "string");
                break;
            case "int":
                writer.WriteString("type", "integer");
                writer.WriteString("format", "int32");
                break;
            case "long":
                writer.WriteString("type", "integer");
                writer.WriteString("format", "int64");
                break;
            case "bool":
                writer.WriteString("type", "boolean");
                break;
            case "datetime":
                writer.WriteString("type", "string");
                writer.WriteString("format", "date-time");
                break;
            case "bytes":
                writer.WriteString("type", "string");
                writer.WriteString("format", "byte");
                break;
            case "object":
                writer.WriteString("type", "object");
                writer.WriteStartObject("additionalProperties");
                writer.WriteEndObject();
                break;
            case "array":
                writer.WriteString("type", "array");
                break;
            default:
                writer.WriteString("type", "string");
                break;
        }
    }

    private static void WriteNullable(Utf8JsonWriter writer)
    {
        // OpenAPI 3.1 uses JSON Schema nullable via type array
        // But for simplicity, use the nullable keyword
        writer.WriteBoolean("nullable", true);
    }

    private static void WriteSecurity(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("security");
        writer.WriteStartObject();
        writer.WriteStartArray("bearerAuth");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private static void WriteTags(Utf8JsonWriter writer, DynamicApiModel model)
    {
        writer.WriteStartArray("tags");

        var tags = model.Endpoints.Select(e => e.Tag).Distinct().OrderBy(t => t);
        foreach (var tag in tags)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tag);
            writer.WriteString("description", $"Operations provided by plugin: {tag}");
            writer.WriteEndObject();
        }

        // Add streaming tag if any streaming endpoints exist
        if (model.Endpoints.Any(e => e.IsStreaming))
        {
            writer.WriteStartObject();
            writer.WriteString("name", "streaming");
            writer.WriteString("description", "WebSocket streaming endpoints");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
