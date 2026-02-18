using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Connectors;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.ConnectorIntegration;

#region INT1: ZeroDayConnectorGeneratorStrategy

/// <summary>
/// Source type for API documentation used in connector generation.
/// </summary>
public enum DocumentationSourceType
{
    /// <summary>OpenAPI/Swagger JSON or YAML specification.</summary>
    OpenApi,
    /// <summary>HTML documentation page.</summary>
    Html,
    /// <summary>Markdown documentation.</summary>
    Markdown,
    /// <summary>Plain text documentation.</summary>
    PlainText,
    /// <summary>GraphQL schema definition.</summary>
    GraphQL,
    /// <summary>WSDL for SOAP services.</summary>
    Wsdl
}

/// <summary>
/// Represents a documentation source for connector generation.
/// </summary>
public sealed record DocumentationSource
{
    /// <summary>URL or path to the documentation.</summary>
    public required string Location { get; init; }

    /// <summary>Type of documentation source.</summary>
    public required DocumentationSourceType SourceType { get; init; }

    /// <summary>Raw content if already fetched.</summary>
    public string? Content { get; init; }

    /// <summary>Base URL for the API being documented.</summary>
    public string? ApiBaseUrl { get; init; }

    /// <summary>API version identifier.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>Authentication requirements hint.</summary>
    public string? AuthenticationHint { get; init; }

    /// <summary>Additional metadata about the documentation.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Result of connector generation including the generated strategy code.
/// </summary>
public sealed record GeneratedConnectorResult
{
    /// <summary>Whether generation was successful.</summary>
    public required bool Success { get; init; }

    /// <summary>Generated strategy identifier.</summary>
    public string? StrategyId { get; init; }

    /// <summary>Generated C# code for the connector strategy.</summary>
    public string? GeneratedCode { get; init; }

    /// <summary>Compiled connector strategy instance (if WASM sandbox available).</summary>
    public IConnectionStrategy? CompiledStrategy { get; init; }

    /// <summary>Discovered endpoints from the documentation.</summary>
    public List<DiscoveredEndpoint> Endpoints { get; init; } = new();

    /// <summary>Detected authentication method.</summary>
    public string? DetectedAuthMethod { get; init; }

    /// <summary>Generation warnings.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Error message if generation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Hash of the source documentation for change detection.</summary>
    public string? SourceHash { get; init; }

    /// <summary>Time taken to generate.</summary>
    public TimeSpan GenerationTime { get; init; }
}

/// <summary>
/// Represents an endpoint discovered from API documentation.
/// </summary>
public sealed record DiscoveredEndpoint
{
    /// <summary>HTTP method (GET, POST, etc.).</summary>
    public required string Method { get; init; }

    /// <summary>Endpoint path.</summary>
    public required string Path { get; init; }

    /// <summary>Operation identifier.</summary>
    public string? OperationId { get; init; }

    /// <summary>Description of the endpoint.</summary>
    public string? Description { get; init; }

    /// <summary>Request parameters.</summary>
    public List<EndpointParameter> Parameters { get; init; } = new();

    /// <summary>Request body schema.</summary>
    public string? RequestBodySchema { get; init; }

    /// <summary>Response schema.</summary>
    public string? ResponseSchema { get; init; }

    /// <summary>Required authentication scopes.</summary>
    public List<string> RequiredScopes { get; init; } = new();
}

/// <summary>
/// Parameter for a discovered endpoint.
/// </summary>
public sealed record EndpointParameter
{
    /// <summary>Parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Parameter location (query, path, header, body).</summary>
    public required string In { get; init; }

    /// <summary>Whether parameter is required.</summary>
    public bool Required { get; init; }

    /// <summary>Parameter data type.</summary>
    public string? DataType { get; init; }

    /// <summary>Parameter description.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// INT1: Zero-Day Connector Generator Strategy ("Docu-Synthesis").
/// AI reads API documentation (Swagger/OpenAPI, HTML) and auto-generates IConnectionStrategy implementations.
/// Supports hot-swap capability when API versions change.
/// </summary>
public sealed class ZeroDayConnectorGeneratorStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, GeneratedConnectorResult> _generatedConnectors = new();
    private readonly ConcurrentDictionary<string, string> _sourceHashes = new();
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "feature-connector-generator";

    /// <inheritdoc/>
    public override string StrategyName => "Zero-Day Connector Generator";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Docu-Synthesis",
        Description = "AI-powered connector generation from API documentation with hot-swap capability for version changes",
        Capabilities = IntelligenceCapabilities.CodeGeneration | IntelligenceCapabilities.Classification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "WasmSandboxEnabled", Description = "Enable WASM sandbox for generated code execution", Required = false, DefaultValue = "false" },
            new ConfigurationRequirement { Key = "AutoHotSwap", Description = "Automatically hot-swap connectors when API versions change", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "ValidationLevel", Description = "Code validation level (strict, normal, minimal)", Required = false, DefaultValue = "normal" }
        },
        CostTier = 4,
        LatencyTier = 4,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "connector", "generation", "openapi", "swagger", "codegen", "hot-swap" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();

    /// <summary>
    /// Initializes a new instance of <see cref="ZeroDayConnectorGeneratorStrategy"/>.
    /// </summary>
    public ZeroDayConnectorGeneratorStrategy() : this(SharedHttpClient) { }

    /// <summary>
    /// Initializes a new instance with a custom HTTP client.
    /// </summary>
    /// <param name="httpClient">HTTP client for fetching documentation.</param>
    public ZeroDayConnectorGeneratorStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Generates a connector strategy from API documentation.
    /// </summary>
    /// <param name="source">Documentation source to parse.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Generated connector result with code and optionally compiled strategy.</returns>
    public async Task<GeneratedConnectorResult> GenerateConnectorAsync(DocumentationSource source, CancellationToken ct = default)
    {
        if (AIProvider == null)
        {
            return new GeneratedConnectorResult
            {
                Success = false,
                ErrorMessage = "AI provider not configured. Configure an AI provider to enable connector generation."
            };
        }

        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();

            // Fetch documentation content if not provided
            var content = source.Content;
            if (string.IsNullOrEmpty(content))
            {
                content = await FetchDocumentationAsync(source.Location, ct);
            }

            // Compute source hash for change detection
            var sourceHash = ComputeHash(content);

            // Check if we already have this version cached
            if (_sourceHashes.TryGetValue(source.Location, out var cachedHash) && cachedHash == sourceHash)
            {
                if (_generatedConnectors.TryGetValue(source.Location, out var cached))
                {
                    return cached with { GenerationTime = sw.Elapsed };
                }
            }

            // Parse documentation based on type
            var endpoints = await ParseDocumentationAsync(source.SourceType, content, ct);

            if (endpoints.Count == 0)
            {
                return new GeneratedConnectorResult
                {
                    Success = false,
                    ErrorMessage = "No endpoints discovered from documentation",
                    GenerationTime = sw.Elapsed
                };
            }

            // Detect authentication method
            var authMethod = DetectAuthenticationMethod(content, endpoints);

            // Generate strategy ID
            var strategyId = GenerateStrategyId(source);

            // Generate connector code using AI
            var generatedCode = await GenerateConnectorCodeAsync(source, endpoints, authMethod, ct);

            // Validate generated code
            var validationResult = ValidateGeneratedCode(generatedCode, GetConfig("ValidationLevel") ?? "normal");
            warnings.AddRange(validationResult.Warnings);

            if (!validationResult.IsValid)
            {
                return new GeneratedConnectorResult
                {
                    Success = false,
                    StrategyId = strategyId,
                    GeneratedCode = generatedCode,
                    Endpoints = endpoints,
                    DetectedAuthMethod = authMethod,
                    Warnings = warnings,
                    ErrorMessage = $"Code validation failed: {string.Join("; ", validationResult.Errors)}",
                    SourceHash = sourceHash,
                    GenerationTime = sw.Elapsed
                };
            }

            // Attempt WASM compilation if enabled
            IConnectionStrategy? compiledStrategy = null;
            if (bool.Parse(GetConfig("WasmSandboxEnabled") ?? "false"))
            {
                compiledStrategy = await CompileInWasmSandboxAsync(generatedCode, ct);
                if (compiledStrategy == null)
                {
                    warnings.Add("WASM compilation unavailable; code generated but not compiled");
                }
            }

            var result = new GeneratedConnectorResult
            {
                Success = true,
                StrategyId = strategyId,
                GeneratedCode = generatedCode,
                CompiledStrategy = compiledStrategy,
                Endpoints = endpoints,
                DetectedAuthMethod = authMethod,
                Warnings = warnings,
                SourceHash = sourceHash,
                GenerationTime = sw.Elapsed
            };

            // Cache the result
            _generatedConnectors[source.Location] = result;
            _sourceHashes[source.Location] = sourceHash;

            return result;
        });
    }

    /// <summary>
    /// Checks if a connector needs to be regenerated due to API version changes.
    /// </summary>
    /// <param name="source">Documentation source to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the API has changed and connector should be regenerated.</returns>
    public async Task<bool> CheckForApiChangesAsync(DocumentationSource source, CancellationToken ct = default)
    {
        var content = source.Content ?? await FetchDocumentationAsync(source.Location, ct);
        var newHash = ComputeHash(content);

        if (_sourceHashes.TryGetValue(source.Location, out var existingHash))
        {
            return newHash != existingHash;
        }

        return true;
    }

    /// <summary>
    /// Hot-swaps an existing connector with a newly generated version.
    /// </summary>
    /// <param name="source">Documentation source for the new version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New generated connector result.</returns>
    public async Task<GeneratedConnectorResult> HotSwapConnectorAsync(DocumentationSource source, CancellationToken ct = default)
    {
        // Remove cached version
        _generatedConnectors.TryRemove(source.Location, out _);
        _sourceHashes.TryRemove(source.Location, out _);

        // Regenerate
        return await GenerateConnectorAsync(source, ct);
    }

    private async Task<string> FetchDocumentationAsync(string location, CancellationToken ct)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            var response = await _httpClient.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        if (File.Exists(location))
        {
            return await File.ReadAllTextAsync(location, ct);
        }

        throw new ArgumentException($"Cannot fetch documentation from location: {location}");
    }

    private async Task<List<DiscoveredEndpoint>> ParseDocumentationAsync(DocumentationSourceType sourceType, string content, CancellationToken ct)
    {
        var endpoints = new List<DiscoveredEndpoint>();

        switch (sourceType)
        {
            case DocumentationSourceType.OpenApi:
                endpoints = ParseOpenApiSpec(content);
                break;

            case DocumentationSourceType.Html:
            case DocumentationSourceType.Markdown:
            case DocumentationSourceType.PlainText:
                endpoints = await ParseWithAIAsync(content, sourceType, ct);
                break;

            case DocumentationSourceType.GraphQL:
                endpoints = ParseGraphQLSchema(content);
                break;

            case DocumentationSourceType.Wsdl:
                endpoints = ParseWsdl(content);
                break;
        }

        return endpoints;
    }

    private List<DiscoveredEndpoint> ParseOpenApiSpec(string content)
    {
        var endpoints = new List<DiscoveredEndpoint>();

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("paths", out var paths))
            {
                foreach (var path in paths.EnumerateObject())
                {
                    foreach (var method in path.Value.EnumerateObject())
                    {
                        if (method.Name is "get" or "post" or "put" or "delete" or "patch")
                        {
                            var endpoint = new DiscoveredEndpoint
                            {
                                Method = method.Name.ToUpperInvariant(),
                                Path = path.Name,
                                OperationId = method.Value.TryGetProperty("operationId", out var opId) ? opId.GetString() : null,
                                Description = method.Value.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
                                Parameters = ParseOpenApiParameters(method.Value),
                                RequestBodySchema = ExtractRequestBodySchema(method.Value),
                                ResponseSchema = ExtractResponseSchema(method.Value),
                                RequiredScopes = ExtractSecurityScopes(method.Value)
                            };
                            endpoints.Add(endpoint);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // May be YAML - attempt basic parsing
            endpoints = ParseOpenApiYaml(content);
        }

        return endpoints;
    }

    private List<EndpointParameter> ParseOpenApiParameters(JsonElement methodElement)
    {
        var parameters = new List<EndpointParameter>();

        if (methodElement.TryGetProperty("parameters", out var paramsArray))
        {
            foreach (var param in paramsArray.EnumerateArray())
            {
                parameters.Add(new EndpointParameter
                {
                    Name = param.GetProperty("name").GetString() ?? "unknown",
                    In = param.TryGetProperty("in", out var inProp) ? inProp.GetString() ?? "query" : "query",
                    Required = param.TryGetProperty("required", out var req) && req.GetBoolean(),
                    DataType = param.TryGetProperty("schema", out var schema) && schema.TryGetProperty("type", out var type) ? type.GetString() : null,
                    Description = param.TryGetProperty("description", out var desc) ? desc.GetString() : null
                });
            }
        }

        return parameters;
    }

    private string? ExtractRequestBodySchema(JsonElement methodElement)
    {
        if (methodElement.TryGetProperty("requestBody", out var requestBody) &&
            requestBody.TryGetProperty("content", out var content))
        {
            foreach (var contentType in content.EnumerateObject())
            {
                if (contentType.Value.TryGetProperty("schema", out var schema))
                {
                    return schema.GetRawText();
                }
            }
        }
        return null;
    }

    private string? ExtractResponseSchema(JsonElement methodElement)
    {
        if (methodElement.TryGetProperty("responses", out var responses))
        {
            if (responses.TryGetProperty("200", out var ok) || responses.TryGetProperty("201", out ok))
            {
                if (ok.TryGetProperty("content", out var content))
                {
                    foreach (var contentType in content.EnumerateObject())
                    {
                        if (contentType.Value.TryGetProperty("schema", out var schema))
                        {
                            return schema.GetRawText();
                        }
                    }
                }
            }
        }
        return null;
    }

    private List<string> ExtractSecurityScopes(JsonElement methodElement)
    {
        var scopes = new List<string>();
        if (methodElement.TryGetProperty("security", out var security))
        {
            foreach (var secReq in security.EnumerateArray())
            {
                foreach (var scheme in secReq.EnumerateObject())
                {
                    foreach (var scope in scheme.Value.EnumerateArray())
                    {
                        if (scope.GetString() is string s)
                            scopes.Add(s);
                    }
                }
            }
        }
        return scopes;
    }

    private List<DiscoveredEndpoint> ParseOpenApiYaml(string content)
    {
        // Basic YAML parsing for paths
        var endpoints = new List<DiscoveredEndpoint>();
        var pathPattern = new Regex(@"^\s{2}(/[^\s:]+):\s*$", RegexOptions.Multiline);
        var methodPattern = new Regex(@"^\s{4}(get|post|put|delete|patch):\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        var pathMatches = pathPattern.Matches(content);
        foreach (Match pathMatch in pathMatches)
        {
            var path = pathMatch.Groups[1].Value;
            var startIndex = pathMatch.Index;
            var endIndex = pathMatches.Cast<Match>().FirstOrDefault(m => m.Index > startIndex)?.Index ?? content.Length;
            var pathSection = content.Substring(startIndex, endIndex - startIndex);

            var methodMatches = methodPattern.Matches(pathSection);
            foreach (Match methodMatch in methodMatches)
            {
                endpoints.Add(new DiscoveredEndpoint
                {
                    Method = methodMatch.Groups[1].Value.ToUpperInvariant(),
                    Path = path
                });
            }
        }

        return endpoints;
    }

    private async Task<List<DiscoveredEndpoint>> ParseWithAIAsync(string content, DocumentationSourceType sourceType, CancellationToken ct)
    {
        if (AIProvider == null) return new List<DiscoveredEndpoint>();

        var prompt = $@"Parse this {sourceType} API documentation and extract all API endpoints.

Documentation:
{(content.Length > 8000 ? content.Substring(0, 8000) + "..." : content)}

Return a JSON array of endpoints with this structure:
[
  {{
    ""method"": ""GET"",
    ""path"": ""/api/users"",
    ""operationId"": ""getUsers"",
    ""description"": ""Get all users"",
    ""parameters"": [
      {{ ""name"": ""limit"", ""in"": ""query"", ""required"": false, ""dataType"": ""integer"" }}
    ]
  }}
]

Return ONLY the JSON array, no other text.";

        var response = await AIProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 4000,
            Temperature = 0.1f
        }, ct);

        RecordTokens(response.Usage?.TotalTokens ?? 0);

        return ParseEndpointsFromJson(response.Content ?? "[]");
    }

    private List<DiscoveredEndpoint> ParseEndpointsFromJson(string json)
    {
        var endpoints = new List<DiscoveredEndpoint>();

        try
        {
            var jsonStart = json.IndexOf('[');
            var jsonEnd = json.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                json = json.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var parameters = new List<EndpointParameter>();
                if (item.TryGetProperty("parameters", out var paramsArray))
                {
                    foreach (var param in paramsArray.EnumerateArray())
                    {
                        parameters.Add(new EndpointParameter
                        {
                            Name = param.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown",
                            In = param.TryGetProperty("in", out var i) ? i.GetString() ?? "query" : "query",
                            Required = param.TryGetProperty("required", out var r) && r.GetBoolean(),
                            DataType = param.TryGetProperty("dataType", out var d) ? d.GetString() : null,
                            Description = param.TryGetProperty("description", out var desc) ? desc.GetString() : null
                        });
                    }
                }

                endpoints.Add(new DiscoveredEndpoint
                {
                    Method = item.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET",
                    Path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/",
                    OperationId = item.TryGetProperty("operationId", out var op) ? op.GetString() : null,
                    Description = item.TryGetProperty("description", out var de) ? de.GetString() : null,
                    Parameters = parameters
                });
            }
        }
        catch (JsonException)
        {
            // Failed to parse AI response
        }

        return endpoints;
    }

    private List<DiscoveredEndpoint> ParseGraphQLSchema(string content)
    {
        var endpoints = new List<DiscoveredEndpoint>();
        var queryPattern = new Regex(@"type\s+Query\s*\{([^}]+)\}", RegexOptions.Singleline);
        var mutationPattern = new Regex(@"type\s+Mutation\s*\{([^}]+)\}", RegexOptions.Singleline);
        var fieldPattern = new Regex(@"(\w+)(?:\([^)]*\))?\s*:\s*(\w+)", RegexOptions.Multiline);

        var queryMatch = queryPattern.Match(content);
        if (queryMatch.Success)
        {
            var fields = fieldPattern.Matches(queryMatch.Groups[1].Value);
            foreach (Match field in fields)
            {
                endpoints.Add(new DiscoveredEndpoint
                {
                    Method = "QUERY",
                    Path = field.Groups[1].Value,
                    OperationId = field.Groups[1].Value,
                    ResponseSchema = field.Groups[2].Value
                });
            }
        }

        var mutationMatch = mutationPattern.Match(content);
        if (mutationMatch.Success)
        {
            var fields = fieldPattern.Matches(mutationMatch.Groups[1].Value);
            foreach (Match field in fields)
            {
                endpoints.Add(new DiscoveredEndpoint
                {
                    Method = "MUTATION",
                    Path = field.Groups[1].Value,
                    OperationId = field.Groups[1].Value,
                    ResponseSchema = field.Groups[2].Value
                });
            }
        }

        return endpoints;
    }

    private List<DiscoveredEndpoint> ParseWsdl(string content)
    {
        var endpoints = new List<DiscoveredEndpoint>();
        var operationPattern = new Regex(@"<wsdl:operation\s+name=""(\w+)""", RegexOptions.IgnoreCase);

        foreach (Match match in operationPattern.Matches(content))
        {
            endpoints.Add(new DiscoveredEndpoint
            {
                Method = "SOAP",
                Path = match.Groups[1].Value,
                OperationId = match.Groups[1].Value
            });
        }

        return endpoints;
    }

    private string DetectAuthenticationMethod(string content, List<DiscoveredEndpoint> endpoints)
    {
        var contentLower = content.ToLowerInvariant();

        if (contentLower.Contains("oauth") || contentLower.Contains("oauth2"))
            return "oauth2";
        if (contentLower.Contains("bearer") || contentLower.Contains("jwt"))
            return "bearer";
        if (contentLower.Contains("api-key") || contentLower.Contains("apikey") || contentLower.Contains("x-api-key"))
            return "apikey";
        if (contentLower.Contains("basic auth"))
            return "basic";

        if (endpoints.Any(e => e.RequiredScopes.Count > 0))
            return "oauth2";

        return "none";
    }

    private string GenerateStrategyId(DocumentationSource source)
    {
        var baseId = source.ApiBaseUrl ?? source.Location;
        var sanitized = Regex.Replace(baseId, @"[^a-zA-Z0-9]", "-").ToLowerInvariant();
        sanitized = Regex.Replace(sanitized, @"-+", "-").Trim('-');
        if (sanitized.Length > 50) sanitized = sanitized.Substring(0, 50);
        return $"generated-{sanitized}";
    }

    private async Task<string> GenerateConnectorCodeAsync(DocumentationSource source, List<DiscoveredEndpoint> endpoints, string authMethod, CancellationToken ct)
    {
        if (AIProvider == null) return string.Empty;

        var endpointSummary = string.Join("\n", endpoints.Take(20).Select(e => $"  - {e.Method} {e.Path}: {e.Description ?? "No description"}"));

        var prompt = $@"Generate a C# IConnectionStrategy implementation for this API.

API Base URL: {source.ApiBaseUrl ?? "https://api.example.com"}
Authentication: {authMethod}
Endpoints ({endpoints.Count} total, showing first 20):
{endpointSummary}

Requirements:
1. Implement IConnectionStrategy interface from DataWarehouse.SDK.Connectors
2. Include ConnectAsync, TestConnectionAsync, DisconnectAsync, GetHealthAsync, ValidateConfigAsync
3. Use HttpClient for HTTP calls
4. Handle authentication based on detected method: {authMethod}
5. Include proper error handling and logging
6. Add XML documentation comments

Generate ONLY the C# class code, no markdown formatting.";

        var response = await AIProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 4000,
            Temperature = 0.2f
        }, ct);

        RecordTokens(response.Usage?.TotalTokens ?? 0);

        var code = response.Content ?? string.Empty;

        // Clean up any markdown formatting
        code = Regex.Replace(code, @"^```(?:csharp|cs)?\s*", "", RegexOptions.Multiline);
        code = Regex.Replace(code, @"```\s*$", "", RegexOptions.Multiline);

        return code.Trim();
    }

    private (bool IsValid, List<string> Errors, List<string> Warnings) ValidateGeneratedCode(string code, string validationLevel)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors.Add("Generated code is empty");
            return (false, errors, warnings);
        }

        // Check for required interface implementation
        if (!code.Contains("IConnectionStrategy"))
            errors.Add("Code does not implement IConnectionStrategy");

        // Check for required methods
        var requiredMethods = new[] { "ConnectAsync", "TestConnectionAsync", "DisconnectAsync", "GetHealthAsync", "ValidateConfigAsync" };
        foreach (var method in requiredMethods)
        {
            if (!code.Contains(method))
            {
                if (validationLevel == "strict")
                    errors.Add($"Missing required method: {method}");
                else
                    warnings.Add($"Missing method: {method}");
            }
        }

        // Check for dangerous patterns
        var dangerousPatterns = new[] { "Process.Start", "File.Delete", "Directory.Delete", "Environment.Exit", "Assembly.Load" };
        foreach (var pattern in dangerousPatterns)
        {
            if (code.Contains(pattern))
                errors.Add($"Dangerous pattern detected: {pattern}");
        }

        return (errors.Count == 0, errors, warnings);
    }

    private async Task<IConnectionStrategy?> CompileInWasmSandboxAsync(string code, CancellationToken ct)
    {
        // WASM sandbox compilation would integrate with T111 if available
        // For now, return null indicating compilation not available
        await Task.CompletedTask;
        return null;
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}

#endregion

#region INT2: SemanticSchemaAlignmentStrategy

/// <summary>
/// Represents a schema source for semantic alignment.
/// </summary>
public sealed record SchemaSource
{
    /// <summary>Identifier for this schema source.</summary>
    public required string SourceId { get; init; }

    /// <summary>System name (e.g., "SAP", "Salesforce", "Internal").</summary>
    public required string SystemName { get; init; }

    /// <summary>Schema/table/entity name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Fields in this schema.</summary>
    public required List<SchemaField> Fields { get; init; }

    /// <summary>Sample data for profiling (optional).</summary>
    public List<Dictionary<string, object>>? SampleData { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Field definition in a schema.
/// </summary>
public sealed record SchemaField
{
    /// <summary>Field name as defined in source system.</summary>
    public required string Name { get; init; }

    /// <summary>Data type.</summary>
    public required string DataType { get; init; }

    /// <summary>Description if available.</summary>
    public string? Description { get; init; }

    /// <summary>Whether this is a primary key.</summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>Whether this is a foreign key.</summary>
    public bool IsForeignKey { get; init; }

    /// <summary>Sample values for profiling.</summary>
    public List<string>? SampleValues { get; init; }
}

/// <summary>
/// Result of schema alignment containing entity mappings.
/// </summary>
public sealed record EntityMapping
{
    /// <summary>Whether alignment was successful.</summary>
    public required bool Success { get; init; }

    /// <summary>Unified virtual schema name.</summary>
    public string? UnifiedSchemaName { get; init; }

    /// <summary>Field mappings between schemas.</summary>
    public List<FieldMapping> FieldMappings { get; init; } = new();

    /// <summary>Discovered semantic entities.</summary>
    public List<SemanticEntity> SemanticEntities { get; init; } = new();

    /// <summary>Confidence score of the alignment (0-1).</summary>
    public double ConfidenceScore { get; init; }

    /// <summary>Warnings during alignment.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Error message if alignment failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Processing time.</summary>
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Mapping between fields across schemas.
/// </summary>
public sealed record FieldMapping
{
    /// <summary>Unified field name in virtual schema.</summary>
    public required string UnifiedFieldName { get; init; }

    /// <summary>Semantic type of this field.</summary>
    public required string SemanticType { get; init; }

    /// <summary>Source field mappings.</summary>
    public required List<SourceFieldMapping> SourceMappings { get; init; }

    /// <summary>Confidence of this mapping (0-1).</summary>
    public double Confidence { get; init; }

    /// <summary>Transformation rules if needed.</summary>
    public string? TransformationRule { get; init; }
}

/// <summary>
/// Mapping of a source field to the unified schema.
/// </summary>
public sealed record SourceFieldMapping
{
    /// <summary>Source system identifier.</summary>
    public required string SourceId { get; init; }

    /// <summary>Original field name.</summary>
    public required string FieldName { get; init; }

    /// <summary>Original data type.</summary>
    public required string DataType { get; init; }
}

/// <summary>
/// Discovered semantic entity from schema alignment.
/// </summary>
public sealed record SemanticEntity
{
    /// <summary>Entity name (e.g., "Customer", "Product", "Order").</summary>
    public required string EntityName { get; init; }

    /// <summary>Entity type category.</summary>
    public required string EntityType { get; init; }

    /// <summary>Fields belonging to this entity.</summary>
    public required List<string> Fields { get; init; }

    /// <summary>Relationships to other entities.</summary>
    public List<EntityRelationship> Relationships { get; init; } = new();
}

/// <summary>
/// Relationship between semantic entities.
/// </summary>
public sealed record EntityRelationship
{
    /// <summary>Related entity name.</summary>
    public required string RelatedEntity { get; init; }

    /// <summary>Relationship type (one-to-one, one-to-many, many-to-many).</summary>
    public required string RelationshipType { get; init; }

    /// <summary>Foreign key field.</summary>
    public string? ForeignKeyField { get; init; }
}

/// <summary>
/// INT2: Semantic Schema Alignment Strategy ("Entity Resolver").
/// AI-driven data profiling for semantic entity matching across heterogeneous systems.
/// Discovers semantic equivalences and builds Unified Virtual Schema.
/// </summary>
public sealed class SemanticSchemaAlignmentStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, float[]> _fieldEmbeddingCache = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-semantic-schema-alignment";

    /// <inheritdoc/>
    public override string StrategyName => "Semantic Schema Alignment";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Entity Resolver",
        Description = "AI-driven semantic entity matching across heterogeneous systems with unified virtual schema generation",
        Capabilities = IntelligenceCapabilities.SemanticSearch | IntelligenceCapabilities.Classification | IntelligenceCapabilities.Embeddings,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MinConfidenceThreshold", Description = "Minimum confidence for automatic mapping (0-1)", Required = false, DefaultValue = "0.75" },
            new ConfigurationRequirement { Key = "EnableDataProfiling", Description = "Enable sample data profiling for better matching", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "MaxSampleSize", Description = "Maximum sample size for data profiling", Required = false, DefaultValue = "1000" }
        },
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "schema", "alignment", "semantic", "entity", "mapping", "unified-schema" }
    };

    /// <summary>
    /// Aligns multiple schemas to create a unified virtual schema.
    /// </summary>
    /// <param name="schemas">Schema sources to align.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Entity mapping with unified schema.</returns>
    public async Task<EntityMapping> AlignSchemasAsync(SchemaSource[] schemas, CancellationToken ct = default)
    {
        if (AIProvider == null)
        {
            return new EntityMapping
            {
                Success = false,
                ErrorMessage = "AI provider not configured. Configure an AI provider to enable schema alignment."
            };
        }

        if (schemas.Length < 2)
        {
            return new EntityMapping
            {
                Success = false,
                ErrorMessage = "At least two schemas are required for alignment."
            };
        }

        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();

            // Step 1: Generate embeddings for all fields
            var fieldEmbeddings = await GenerateFieldEmbeddingsAsync(schemas, ct);

            // Step 2: Find semantic matches using embeddings
            var matches = FindSemanticMatches(schemas, fieldEmbeddings);

            // Step 3: Use AI to refine and validate matches
            var refinedMappings = await RefineMatchesWithAIAsync(schemas, matches, ct);

            // Step 4: Discover semantic entities
            var entities = await DiscoverSemanticEntitiesAsync(schemas, refinedMappings, ct);

            // Step 5: Build unified schema
            var unifiedSchemaName = GenerateUnifiedSchemaName(schemas);
            var fieldMappings = BuildFieldMappings(refinedMappings);

            // Calculate overall confidence
            var avgConfidence = fieldMappings.Count > 0 ? fieldMappings.Average(m => m.Confidence) : 0;

            return new EntityMapping
            {
                Success = true,
                UnifiedSchemaName = unifiedSchemaName,
                FieldMappings = fieldMappings,
                SemanticEntities = entities,
                ConfidenceScore = avgConfidence,
                Warnings = warnings,
                ProcessingTime = sw.Elapsed
            };
        });
    }

    private async Task<Dictionary<string, float[]>> GenerateFieldEmbeddingsAsync(SchemaSource[] schemas, CancellationToken ct)
    {
        var embeddings = new Dictionary<string, float[]>();

        foreach (var schema in schemas)
        {
            foreach (var field in schema.Fields)
            {
                var key = $"{schema.SourceId}.{field.Name}";

                if (_fieldEmbeddingCache.TryGetValue(key, out var cached))
                {
                    embeddings[key] = cached;
                    continue;
                }

                // Create rich text representation for embedding
                var fieldText = BuildFieldTextRepresentation(schema, field);
                var embedding = await AIProvider!.GetEmbeddingsAsync(fieldText, ct);
                RecordEmbeddings(1);

                embeddings[key] = embedding;
                _fieldEmbeddingCache[key] = embedding;
            }
        }

        return embeddings;
    }

    private string BuildFieldTextRepresentation(SchemaSource schema, SchemaField field)
    {
        var sb = new StringBuilder();
        sb.Append($"System: {schema.SystemName}, Schema: {schema.SchemaName}, Field: {field.Name}, Type: {field.DataType}");

        if (!string.IsNullOrEmpty(field.Description))
            sb.Append($", Description: {field.Description}");

        if (field.IsPrimaryKey)
            sb.Append(", Primary Key");

        if (field.IsForeignKey)
            sb.Append(", Foreign Key");

        if (field.SampleValues?.Count > 0)
            sb.Append($", Sample values: {string.Join(", ", field.SampleValues.Take(5))}");

        return sb.ToString();
    }

    private List<(string Field1, string Field2, float Similarity)> FindSemanticMatches(SchemaSource[] schemas, Dictionary<string, float[]> embeddings)
    {
        var matches = new List<(string, string, float)>();
        var threshold = float.Parse(GetConfig("MinConfidenceThreshold") ?? "0.75");

        var keys = embeddings.Keys.ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            for (int j = i + 1; j < keys.Count; j++)
            {
                // Only compare fields from different schemas
                var source1 = keys[i].Split('.')[0];
                var source2 = keys[j].Split('.')[0];
                if (source1 == source2) continue;

                var similarity = CalculateCosineSimilarity(embeddings[keys[i]], embeddings[keys[j]]);
                if (similarity >= threshold)
                {
                    matches.Add((keys[i], keys[j], similarity));
                }
            }
        }

        return matches.OrderByDescending(m => m.Item3).ToList();
    }

    private async Task<List<(string UnifiedName, string SemanticType, List<(string SourceId, string FieldName, string DataType)> Sources, double Confidence)>> RefineMatchesWithAIAsync(
        SchemaSource[] schemas,
        List<(string Field1, string Field2, float Similarity)> matches,
        CancellationToken ct)
    {
        if (AIProvider == null || matches.Count == 0)
            return new List<(string, string, List<(string, string, string)>, double)>();

        var schemaContext = string.Join("\n", schemas.Select(s =>
            $"Schema {s.SourceId} ({s.SystemName}.{s.SchemaName}): {string.Join(", ", s.Fields.Select(f => f.Name))}"));

        var matchesText = string.Join("\n", matches.Take(50).Select(m =>
            $"  {m.Field1} <-> {m.Field2} (similarity: {m.Similarity:F2})"));

        var prompt = $@"Analyze these schema field matches and identify semantic equivalences.

Schemas:
{schemaContext}

Potential matches (by embedding similarity):
{matchesText}

Known semantic equivalences examples:
- SAP KUNNR = Salesforce AccountID = Internal Client_UUID (Customer ID)
- SAP MATNR = Product SKU = ProductCode (Material/Product Number)
- Email, EmailAddress, E_MAIL, SMTP (Email Address)

For each match group, provide:
1. A unified field name (English, CamelCase)
2. Semantic type (customer_id, product_id, email, phone, address, name, date, amount, etc.)
3. Confidence (0-1)

Return JSON array:
[
  {{
    ""unifiedName"": ""CustomerId"",
    ""semanticType"": ""customer_id"",
    ""sources"": [
      {{ ""sourceId"": ""sap"", ""fieldName"": ""KUNNR"", ""dataType"": ""string"" }},
      {{ ""sourceId"": ""sfdc"", ""fieldName"": ""AccountId"", ""dataType"": ""string"" }}
    ],
    ""confidence"": 0.95
  }}
]";

        var response = await AIProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 3000,
            Temperature = 0.2f
        }, ct);

        RecordTokens(response.Usage?.TotalTokens ?? 0);

        return ParseRefinedMappings(response.Content ?? "[]", schemas);
    }

    private List<(string UnifiedName, string SemanticType, List<(string SourceId, string FieldName, string DataType)> Sources, double Confidence)> ParseRefinedMappings(string json, SchemaSource[] schemas)
    {
        var result = new List<(string, string, List<(string, string, string)>, double)>();

        try
        {
            var jsonStart = json.IndexOf('[');
            var jsonEnd = json.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                json = json.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var unifiedName = item.GetProperty("unifiedName").GetString() ?? "Unknown";
                var semanticType = item.GetProperty("semanticType").GetString() ?? "unknown";
                var confidence = item.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5;

                var sources = new List<(string, string, string)>();
                if (item.TryGetProperty("sources", out var sourcesArray))
                {
                    foreach (var source in sourcesArray.EnumerateArray())
                    {
                        var sourceId = source.GetProperty("sourceId").GetString() ?? "";
                        var fieldName = source.GetProperty("fieldName").GetString() ?? "";
                        var dataType = source.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "string" : "string";
                        sources.Add((sourceId, fieldName, dataType));
                    }
                }

                result.Add((unifiedName, semanticType, sources, confidence));
            }
        }
        catch (JsonException)
        {
            // Failed to parse
        }

        return result;
    }

    private async Task<List<SemanticEntity>> DiscoverSemanticEntitiesAsync(
        SchemaSource[] schemas,
        List<(string UnifiedName, string SemanticType, List<(string SourceId, string FieldName, string DataType)> Sources, double Confidence)> mappings,
        CancellationToken ct)
    {
        if (AIProvider == null || mappings.Count == 0)
            return new List<SemanticEntity>();

        var mappingsSummary = string.Join("\n", mappings.Select(m =>
            $"  {m.UnifiedName} ({m.SemanticType}): {string.Join(", ", m.Sources.Select(s => $"{s.SourceId}.{s.FieldName}"))}"));

        var prompt = $@"Based on these field mappings, identify the semantic entities and their relationships.

Mapped fields:
{mappingsSummary}

Identify:
1. Major entities (Customer, Order, Product, etc.)
2. Which fields belong to each entity
3. Relationships between entities

Return JSON:
[
  {{
    ""entityName"": ""Customer"",
    ""entityType"": ""master_data"",
    ""fields"": [""CustomerId"", ""CustomerName"", ""Email""],
    ""relationships"": [
      {{ ""relatedEntity"": ""Order"", ""relationshipType"": ""one-to-many"", ""foreignKeyField"": ""CustomerId"" }}
    ]
  }}
]";

        var response = await AIProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 2000,
            Temperature = 0.2f
        }, ct);

        RecordTokens(response.Usage?.TotalTokens ?? 0);

        return ParseSemanticEntities(response.Content ?? "[]");
    }

    private List<SemanticEntity> ParseSemanticEntities(string json)
    {
        var entities = new List<SemanticEntity>();

        try
        {
            var jsonStart = json.IndexOf('[');
            var jsonEnd = json.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                json = json.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var relationships = new List<EntityRelationship>();
                if (item.TryGetProperty("relationships", out var relsArray))
                {
                    foreach (var rel in relsArray.EnumerateArray())
                    {
                        relationships.Add(new EntityRelationship
                        {
                            RelatedEntity = rel.GetProperty("relatedEntity").GetString() ?? "",
                            RelationshipType = rel.GetProperty("relationshipType").GetString() ?? "unknown",
                            ForeignKeyField = rel.TryGetProperty("foreignKeyField", out var fk) ? fk.GetString() : null
                        });
                    }
                }

                var fields = new List<string>();
                if (item.TryGetProperty("fields", out var fieldsArray))
                {
                    foreach (var field in fieldsArray.EnumerateArray())
                    {
                        if (field.GetString() is string f)
                            fields.Add(f);
                    }
                }

                entities.Add(new SemanticEntity
                {
                    EntityName = item.GetProperty("entityName").GetString() ?? "Unknown",
                    EntityType = item.TryGetProperty("entityType", out var et) ? et.GetString() ?? "unknown" : "unknown",
                    Fields = fields,
                    Relationships = relationships
                });
            }
        }
        catch (JsonException)
        {
            // Failed to parse
        }

        return entities;
    }

    private string GenerateUnifiedSchemaName(SchemaSource[] schemas)
    {
        var systems = string.Join("_", schemas.Select(s => s.SystemName).Distinct().Take(3));
        return $"Unified_{systems}_Schema";
    }

    private List<FieldMapping> BuildFieldMappings(
        List<(string UnifiedName, string SemanticType, List<(string SourceId, string FieldName, string DataType)> Sources, double Confidence)> refinedMappings)
    {
        return refinedMappings.Select(m => new FieldMapping
        {
            UnifiedFieldName = m.UnifiedName,
            SemanticType = m.SemanticType,
            SourceMappings = m.Sources.Select(s => new SourceFieldMapping
            {
                SourceId = s.SourceId,
                FieldName = s.FieldName,
                DataType = s.DataType
            }).ToList(),
            Confidence = m.Confidence
        }).ToList();
    }

    private static float CalculateCosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }
}

#endregion

#region INT3: UniversalQueryTranspilationStrategy

/// <summary>
/// Target dialect for query transpilation.
/// </summary>
public enum TargetDialect
{
    /// <summary>MongoDB aggregation pipeline.</summary>
    MongoDb,
    /// <summary>Neo4j Cypher query language.</summary>
    Cypher,
    /// <summary>InfluxDB InfluxQL.</summary>
    InfluxQL,
    /// <summary>InfluxDB Flux query language.</summary>
    Flux,
    /// <summary>IBM CICS transaction commands.</summary>
    CicsTransaction,
    /// <summary>HL7 FHIR search parameters.</summary>
    Hl7Fhir,
    /// <summary>GraphQL query.</summary>
    GraphQL,
    /// <summary>Elasticsearch Query DSL.</summary>
    ElasticsearchDsl,
    /// <summary>Apache Cassandra CQL.</summary>
    CassandraCql,
    /// <summary>Redis commands.</summary>
    Redis,
    /// <summary>Standard SQL (ANSI).</summary>
    AnsiSql,
    /// <summary>PostgreSQL-specific SQL.</summary>
    PostgreSql,
    /// <summary>MySQL-specific SQL.</summary>
    MySql,
    /// <summary>Microsoft SQL Server T-SQL.</summary>
    TSql,
    /// <summary>Oracle PL/SQL.</summary>
    OraclePlSql
}

/// <summary>
/// Result of query transpilation.
/// </summary>
public sealed record TranspilationResult
{
    /// <summary>Whether transpilation was successful.</summary>
    public required bool Success { get; init; }

    /// <summary>Original SQL query.</summary>
    public string? OriginalQuery { get; init; }

    /// <summary>Transpiled query in target dialect.</summary>
    public string? TranspiledQuery { get; init; }

    /// <summary>Target dialect.</summary>
    public TargetDialect TargetDialect { get; init; }

    /// <summary>Explain plan for the transpiled query.</summary>
    public QueryExplainPlan? ExplainPlan { get; init; }

    /// <summary>Validation result.</summary>
    public QueryValidationResult? Validation { get; init; }

    /// <summary>Warnings during transpilation.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Error message if transpilation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Processing time.</summary>
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Explain plan for a transpiled query.
/// </summary>
public sealed record QueryExplainPlan
{
    /// <summary>Estimated cost (relative units).</summary>
    public double EstimatedCost { get; init; }

    /// <summary>Estimated rows affected.</summary>
    public long EstimatedRows { get; init; }

    /// <summary>Operations in the plan.</summary>
    public List<QueryOperation> Operations { get; init; } = new();

    /// <summary>Indexes that would be used.</summary>
    public List<string> UsedIndexes { get; init; } = new();

    /// <summary>Optimization suggestions.</summary>
    public List<string> Suggestions { get; init; } = new();
}

/// <summary>
/// Operation in a query explain plan.
/// </summary>
public sealed record QueryOperation
{
    /// <summary>Operation type (scan, seek, join, sort, etc.).</summary>
    public required string OperationType { get; init; }

    /// <summary>Target object (table, index, collection).</summary>
    public string? Target { get; init; }

    /// <summary>Estimated cost.</summary>
    public double Cost { get; init; }

    /// <summary>Additional details.</summary>
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>
/// Validation result for a transpiled query.
/// </summary>
public sealed record QueryValidationResult
{
    /// <summary>Whether the query is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Syntax errors.</summary>
    public List<string> SyntaxErrors { get; init; } = new();

    /// <summary>Semantic warnings.</summary>
    public List<string> SemanticWarnings { get; init; } = new();

    /// <summary>Security concerns.</summary>
    public List<string> SecurityConcerns { get; init; } = new();
}

/// <summary>
/// INT3: Universal Query Transpilation Strategy ("Time-Travel Query Transpilation").
/// Translates SQL to any native query language with validation and explain plan generation.
/// </summary>
public sealed class UniversalQueryTranspilationStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, string> _transpilationCache = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-query-transpilation";

    /// <inheritdoc/>
    public override string StrategyName => "Universal Query Transpilation";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Time-Travel Query",
        Description = "AI-powered SQL to native query language transpilation with validation and explain plans",
        Capabilities = IntelligenceCapabilities.CodeGeneration | IntelligenceCapabilities.Classification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "EnableCaching", Description = "Cache transpilation results", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "ValidateBeforeReturn", Description = "Validate transpiled queries", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "GenerateExplainPlan", Description = "Generate explain plans", Required = false, DefaultValue = "true" }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "query", "transpilation", "sql", "mongodb", "cypher", "graphql", "fhir" }
    };

    /// <summary>
    /// Transpiles a SQL query to the target dialect.
    /// </summary>
    /// <param name="sql">SQL query to transpile.</param>
    /// <param name="target">Target query dialect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transpilation result.</returns>
    public async Task<TranspilationResult> TranspileAsync(string sql, TargetDialect target, CancellationToken ct = default)
    {
        if (AIProvider == null)
        {
            return new TranspilationResult
            {
                Success = false,
                OriginalQuery = sql,
                TargetDialect = target,
                ErrorMessage = "AI provider not configured. Configure an AI provider to enable query transpilation."
            };
        }

        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();

            var cacheKey = $"{target}:{ComputeQueryHash(sql)}";
            if (bool.Parse(GetConfig("EnableCaching") ?? "true") && _transpilationCache.TryGetValue(cacheKey, out var cached))
            {
                return new TranspilationResult
                {
                    Success = true,
                    OriginalQuery = sql,
                    TranspiledQuery = cached,
                    TargetDialect = target,
                    ProcessingTime = sw.Elapsed
                };
            }

            var transpiledQuery = await TranspileWithAIAsync(sql, target, ct);

            if (string.IsNullOrEmpty(transpiledQuery))
            {
                return new TranspilationResult
                {
                    Success = false,
                    OriginalQuery = sql,
                    TargetDialect = target,
                    ErrorMessage = "Transpilation produced empty result",
                    ProcessingTime = sw.Elapsed
                };
            }

            QueryValidationResult? validation = null;
            if (bool.Parse(GetConfig("ValidateBeforeReturn") ?? "true"))
            {
                validation = await ValidateQueryAsync(transpiledQuery, target, ct);
                if (!validation.IsValid)
                    warnings.AddRange(validation.SyntaxErrors);
            }

            QueryExplainPlan? explainPlan = null;
            if (bool.Parse(GetConfig("GenerateExplainPlan") ?? "true"))
                explainPlan = await GenerateExplainPlanAsync(transpiledQuery, target, ct);

            if (bool.Parse(GetConfig("EnableCaching") ?? "true"))
                _transpilationCache[cacheKey] = transpiledQuery;

            return new TranspilationResult
            {
                Success = validation?.IsValid ?? true,
                OriginalQuery = sql,
                TranspiledQuery = transpiledQuery,
                TargetDialect = target,
                ExplainPlan = explainPlan,
                Validation = validation,
                Warnings = warnings,
                ProcessingTime = sw.Elapsed
            };
        });
    }

    private async Task<string> TranspileWithAIAsync(string sql, TargetDialect target, CancellationToken ct)
    {
        var dialectInfo = GetDialectInfo(target);
        var prompt = $@"Transpile this SQL query to {target} ({dialectInfo.Description}).

SQL Query:
{sql}

{dialectInfo.Guidelines}

Return ONLY the transpiled query, no explanation or markdown formatting.";

        var response = await AIProvider!.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 2000,
            Temperature = 0.1f
        }, ct);

        RecordTokens(response.Usage?.TotalTokens ?? 0);
        var result = response.Content ?? "";
        result = Regex.Replace(result, @"^```(?:\w+)?\s*", "", RegexOptions.Multiline);
        result = Regex.Replace(result, @"```\s*$", "", RegexOptions.Multiline);
        return result.Trim();
    }

    private async Task<QueryValidationResult> ValidateQueryAsync(string query, TargetDialect dialect, CancellationToken ct)
    {
        var prompt = $@"Validate this {dialect} query for syntax errors, semantic issues, and security concerns.
Query: {query}
Return JSON: {{""isValid"": true/false, ""syntaxErrors"": [], ""semanticWarnings"": [], ""securityConcerns"": []}}";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 1000, Temperature = 0.1f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);
        return ParseValidationResult(response.Content ?? "{}");
    }

    private async Task<QueryExplainPlan> GenerateExplainPlanAsync(string query, TargetDialect dialect, CancellationToken ct)
    {
        var prompt = $@"Generate explain plan for this {dialect} query: {query}
Return JSON: {{""estimatedCost"": 0, ""estimatedRows"": 0, ""operations"": [], ""usedIndexes"": [], ""suggestions"": []}}";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 1500, Temperature = 0.2f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);
        return ParseExplainPlan(response.Content ?? "{}");
    }

    private (string Description, string Guidelines) GetDialectInfo(TargetDialect dialect) => dialect switch
    {
        TargetDialect.MongoDb => ("MongoDB Aggregation Pipeline", "Use $match, $project, $group, $sort stages."),
        TargetDialect.Cypher => ("Neo4j Cypher", "Use MATCH, WHERE, RETURN clauses with relationship patterns."),
        TargetDialect.InfluxQL => ("InfluxDB InfluxQL", "Use SELECT, FROM, WHERE, GROUP BY TIME()."),
        TargetDialect.Flux => ("InfluxDB Flux", "Use from(), range(), filter(), map() with pipe-forward |>."),
        TargetDialect.CicsTransaction => ("IBM CICS", "Generate EXEC CICS commands."),
        TargetDialect.Hl7Fhir => ("HL7 FHIR Search", "Use FHIR search parameters with modifiers."),
        TargetDialect.GraphQL => ("GraphQL Query", "Use query/mutation with fields and arguments."),
        TargetDialect.ElasticsearchDsl => ("Elasticsearch DSL", "Use bool query with must, should, filter."),
        TargetDialect.CassandraCql => ("Cassandra CQL", "Consider partition key restrictions."),
        TargetDialect.Redis => ("Redis Commands", "Use appropriate data structure commands."),
        _ => ($"{dialect} SQL", "Follow standard SQL conventions.")
    };

    private QueryValidationResult ParseValidationResult(string json)
    {
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
                var root = doc.RootElement;
                return new QueryValidationResult
                {
                    IsValid = root.TryGetProperty("isValid", out var v) && v.GetBoolean(),
                    SyntaxErrors = ParseStringArray(root, "syntaxErrors"),
                    SemanticWarnings = ParseStringArray(root, "semanticWarnings"),
                    SecurityConcerns = ParseStringArray(root, "securityConcerns")
                };
            }
        }
        catch { }
        return new QueryValidationResult { IsValid = true };
    }

    private QueryExplainPlan ParseExplainPlan(string json)
    {
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
                var root = doc.RootElement;
                var operations = new List<QueryOperation>();
                if (root.TryGetProperty("operations", out var ops))
                {
                    foreach (var op in ops.EnumerateArray())
                    {
                        operations.Add(new QueryOperation
                        {
                            OperationType = op.TryGetProperty("operationType", out var ot) ? ot.GetString() ?? "Unknown" : "Unknown",
                            Target = op.TryGetProperty("target", out var t) ? t.GetString() : null,
                            Cost = op.TryGetProperty("cost", out var c) ? c.GetDouble() : 0
                        });
                    }
                }
                return new QueryExplainPlan
                {
                    EstimatedCost = root.TryGetProperty("estimatedCost", out var ec) ? ec.GetDouble() : 0,
                    EstimatedRows = root.TryGetProperty("estimatedRows", out var er) ? er.GetInt64() : 0,
                    Operations = operations,
                    UsedIndexes = ParseStringArray(root, "usedIndexes"),
                    Suggestions = ParseStringArray(root, "suggestions")
                };
            }
        }
        catch { }
        return new QueryExplainPlan();
    }

    private static List<string> ParseStringArray(JsonElement root, string prop)
    {
        var result = new List<string>();
        if (root.TryGetProperty(prop, out var arr))
            foreach (var item in arr.EnumerateArray())
                if (item.GetString() is string s) result.Add(s);
        return result;
    }

    private static string ComputeQueryHash(string query)
    {
        var normalized = Regex.Replace(query.ToLowerInvariant(), @"\s+", " ").Trim();
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).Substring(0, 16);
    }
}

#endregion

#region INT4: LegacyBehavioralModelingStrategy

/// <summary>
/// Traffic sample for behavioral modeling.
/// </summary>
public sealed record TrafficSample
{
    /// <summary>Unique identifier for this sample.</summary>
    public required string SampleId { get; init; }
    /// <summary>Timestamp of the traffic.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>Request data.</summary>
    public required Dictionary<string, object> Request { get; init; }
    /// <summary>Response data.</summary>
    public required Dictionary<string, object> Response { get; init; }
    /// <summary>Duration of the operation.</summary>
    public TimeSpan Duration { get; init; }
    /// <summary>Whether the operation was successful.</summary>
    public bool Success { get; init; } = true;
    /// <summary>Screen capture for terminal/green-screen systems.</summary>
    public byte[]? ScreenCapture { get; init; }
    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Traffic samples collection for learning.
/// </summary>
public sealed record TrafficSamples
{
    /// <summary>System identifier being modeled.</summary>
    public required string SystemId { get; init; }
    /// <summary>System type (mainframe, terminal, legacy-api, etc.).</summary>
    public required string SystemType { get; init; }
    /// <summary>Individual traffic samples.</summary>
    public required List<TrafficSample> Samples { get; init; }
    /// <summary>Sampling period start.</summary>
    public DateTimeOffset PeriodStart { get; init; }
    /// <summary>Sampling period end.</summary>
    public DateTimeOffset PeriodEnd { get; init; }
}

/// <summary>
/// Learned behavioral model for a legacy system.
/// </summary>
public sealed record BehavioralModel
{
    /// <summary>Whether model generation was successful.</summary>
    public required bool Success { get; init; }
    /// <summary>Model identifier.</summary>
    public string? ModelId { get; init; }
    /// <summary>System being modeled.</summary>
    public string? SystemId { get; init; }
    /// <summary>Discovered operations/endpoints.</summary>
    public List<DiscoveredLegacyOperation> Operations { get; init; } = new();
    /// <summary>Generated API specification (OpenAPI format).</summary>
    public string? GeneratedApiSpec { get; init; }
    /// <summary>State machine model if applicable.</summary>
    public StateMachineModel? StateMachine { get; init; }
    /// <summary>Detected anomaly patterns.</summary>
    public List<BehavioralAnomalyPattern> AnomalyPatterns { get; init; } = new();
    /// <summary>Model confidence score.</summary>
    public double ConfidenceScore { get; init; }
    /// <summary>Warnings during model generation.</summary>
    public List<string> Warnings { get; init; } = new();
    /// <summary>Error message if generation failed.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Processing time.</summary>
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Operation discovered from traffic analysis.
/// </summary>
public sealed record DiscoveredLegacyOperation
{
    /// <summary>Operation name/identifier.</summary>
    public required string OperationName { get; init; }
    /// <summary>HTTP method equivalent.</summary>
    public required string HttpMethod { get; init; }
    /// <summary>Suggested REST path.</summary>
    public required string SuggestedPath { get; init; }
    /// <summary>Input parameters.</summary>
    public List<LegacyOperationParameter> InputParameters { get; init; } = new();
    /// <summary>Output schema.</summary>
    public string? OutputSchema { get; init; }
    /// <summary>Sample count used for learning.</summary>
    public int SampleCount { get; init; }
    /// <summary>Average response time.</summary>
    public TimeSpan AverageResponseTime { get; init; }
    /// <summary>Success rate observed.</summary>
    public double SuccessRate { get; init; }
}

/// <summary>
/// Parameter for a discovered operation.
/// </summary>
public sealed record LegacyOperationParameter
{
    /// <summary>Parameter name.</summary>
    public required string Name { get; init; }
    /// <summary>Inferred data type.</summary>
    public required string DataType { get; init; }
    /// <summary>Whether parameter is required.</summary>
    public bool Required { get; init; }
    /// <summary>Sample values observed.</summary>
    public List<string> SampleValues { get; init; } = new();
    /// <summary>Inferred validation pattern.</summary>
    public string? ValidationPattern { get; init; }
}

/// <summary>
/// State machine model for stateful legacy systems.
/// </summary>
public sealed record StateMachineModel
{
    /// <summary>States in the system.</summary>
    public List<SystemState> States { get; init; } = new();
    /// <summary>Transitions between states.</summary>
    public List<StateTransition> Transitions { get; init; } = new();
    /// <summary>Initial state.</summary>
    public string? InitialState { get; init; }
}

/// <summary>
/// State in a state machine model.
/// </summary>
public sealed record SystemState
{
    /// <summary>State name.</summary>
    public required string Name { get; init; }
    /// <summary>State description.</summary>
    public string? Description { get; init; }
    /// <summary>Whether this is a terminal state.</summary>
    public bool IsTerminal { get; init; }
}

/// <summary>
/// Transition between states.
/// </summary>
public sealed record StateTransition
{
    /// <summary>Source state.</summary>
    public required string FromState { get; init; }
    /// <summary>Target state.</summary>
    public required string ToState { get; init; }
    /// <summary>Trigger/action causing transition.</summary>
    public required string Trigger { get; init; }
    /// <summary>Conditions for transition.</summary>
    public string? Condition { get; init; }
}

/// <summary>
/// Anomaly pattern detected in traffic.
/// </summary>
public sealed record BehavioralAnomalyPattern
{
    /// <summary>Pattern name.</summary>
    public required string PatternName { get; init; }
    /// <summary>Pattern description.</summary>
    public required string Description { get; init; }
    /// <summary>Severity level.</summary>
    public required string Severity { get; init; }
    /// <summary>Detection conditions.</summary>
    public string? DetectionCondition { get; init; }
    /// <summary>Occurrence count in samples.</summary>
    public int OccurrenceCount { get; init; }
}

/// <summary>
/// INT4: Legacy Behavioral Modeling Strategy ("Ghost in the Shell").
/// AI learns system behavioral patterns from traffic to expose legacy systems as clean REST/GraphQL APIs.
/// Includes vision AI for green-screen output parsing.
/// </summary>
public sealed class LegacyBehavioralModelingStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, BehavioralModel> _learnedModels = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-behavioral-modeling";

    /// <inheritdoc/>
    public override string StrategyName => "Legacy Behavioral Modeling";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Ghost in the Shell",
        Description = "AI learns legacy system behavior from traffic to expose as modern REST/GraphQL APIs with anomaly detection",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.AnomalyDetection | IntelligenceCapabilities.ImageAnalysis,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MinSamplesForLearning", Description = "Minimum traffic samples required", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "EnableVisionParsing", Description = "Enable vision AI for green-screen parsing", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "AnomalyDetectionEnabled", Description = "Enable pattern anomaly detection", Required = false, DefaultValue = "true" }
        },
        CostTier = 4,
        LatencyTier = 4,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "legacy", "behavioral", "modeling", "rest", "graphql", "mainframe", "terminal" }
    };

    /// <summary>
    /// Learns behavioral patterns from traffic samples.
    /// </summary>
    /// <param name="samples">Traffic samples to learn from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Behavioral model.</returns>
    public async Task<BehavioralModel> LearnBehaviorAsync(TrafficSamples samples, CancellationToken ct = default)
    {
        if (AIProvider == null)
            return new BehavioralModel { Success = false, ErrorMessage = "AI provider not configured." };

        var minSamples = int.Parse(GetConfig("MinSamplesForLearning") ?? "100");
        if (samples.Samples.Count < minSamples)
            return new BehavioralModel { Success = false, SystemId = samples.SystemId, ErrorMessage = $"Insufficient samples. Min: {minSamples}, provided: {samples.Samples.Count}" };

        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var operationGroups = ClusterTraffic(samples);
            var operations = new List<DiscoveredLegacyOperation>();

            foreach (var group in operationGroups)
            {
                var op = await AnalyzeOperationGroupAsync(group.Key, group.Value, ct);
                if (op != null) operations.Add(op);
            }

            StateMachineModel? stateMachine = null;
            if (samples.SystemType is "mainframe" or "terminal" or "stateful")
                stateMachine = await BuildStateMachineAsync(samples.SystemType, operations, ct);

            var anomalyPatterns = new List<BehavioralAnomalyPattern>();
            if (bool.Parse(GetConfig("AnomalyDetectionEnabled") ?? "true"))
                anomalyPatterns = await DetectAnomalyPatternsAsync(samples, ct);

            var apiSpec = GenerateOpenApiSpec(samples.SystemId, operations);
            var modelId = $"model-{samples.SystemId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var confidence = operations.Count > 0 ? operations.Average(o => o.SuccessRate) * (Math.Min(1.0, samples.Samples.Count / 500.0)) : 0;

            var model = new BehavioralModel
            {
                Success = true,
                ModelId = modelId,
                SystemId = samples.SystemId,
                Operations = operations,
                GeneratedApiSpec = apiSpec,
                StateMachine = stateMachine,
                AnomalyPatterns = anomalyPatterns,
                ConfidenceScore = confidence,
                ProcessingTime = sw.Elapsed
            };

            _learnedModels[samples.SystemId] = model;
            return model;
        });
    }

    /// <summary>
    /// Parses green-screen output using vision AI.
    /// </summary>
    /// <param name="screenCapture">Screen capture image bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed screen data.</returns>
    public async Task<Dictionary<string, object>> ParseGreenScreenAsync(byte[] screenCapture, CancellationToken ct = default)
    {
        if (AIProvider == null || !bool.Parse(GetConfig("EnableVisionParsing") ?? "true"))
            return new Dictionary<string, object> { ["error"] = "Vision parsing not available" };

        var prompt = "Analyze this terminal screen and extract: menu options, data fields, status messages, cursor position, errors. Return structured JSON.";
        var response = await AIProvider.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 2000, Temperature = 0.1f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);

        try
        {
            var content = response.Content ?? "{}";
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(content.Substring(start, end - start + 1));
                return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.ToString());
            }
        }
        catch { }
        return new Dictionary<string, object> { ["raw"] = response.Content ?? "" };
    }

    /// <summary>
    /// Gets a previously learned model.
    /// </summary>
    public BehavioralModel? GetLearnedModel(string systemId) => _learnedModels.TryGetValue(systemId, out var m) ? m : null;

    private Dictionary<string, List<TrafficSample>> ClusterTraffic(TrafficSamples samples)
    {
        var groups = new Dictionary<string, List<TrafficSample>>();
        foreach (var sample in samples.Samples)
        {
            var sig = string.Join("|", sample.Request.Keys.OrderBy(k => k));
            if (!groups.ContainsKey(sig)) groups[sig] = new List<TrafficSample>();
            groups[sig].Add(sample);
        }
        return groups;
    }

    private async Task<DiscoveredLegacyOperation?> AnalyzeOperationGroupAsync(string sig, List<TrafficSample> samples, CancellationToken ct)
    {
        if (samples.Count == 0) return null;

        var sampleJson = JsonSerializer.Serialize(samples.Take(3).Select(s => new { s.Request, s.Response, s.Success }));
        var prompt = $@"Analyze these traffic samples and identify the operation:
{sampleJson}
Return JSON: {{""operationName"": ""X"", ""httpMethod"": ""GET"", ""suggestedPath"": ""/api/x"", ""inputParameters"": [{{""name"": ""id"", ""dataType"": ""string"", ""required"": true}}]}}";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 1000, Temperature = 0.2f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);

        try
        {
            var json = response.Content ?? "{}";
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
                var root = doc.RootElement;
                var parameters = new List<LegacyOperationParameter>();
                if (root.TryGetProperty("inputParameters", out var pArr))
                    foreach (var p in pArr.EnumerateArray())
                        parameters.Add(new LegacyOperationParameter
                        {
                            Name = p.GetProperty("name").GetString() ?? "unknown",
                            DataType = p.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "string" : "string",
                            Required = p.TryGetProperty("required", out var r) && r.GetBoolean()
                        });

                return new DiscoveredLegacyOperation
                {
                    OperationName = root.GetProperty("operationName").GetString() ?? "Unknown",
                    HttpMethod = root.TryGetProperty("httpMethod", out var hm) ? hm.GetString() ?? "GET" : "GET",
                    SuggestedPath = root.TryGetProperty("suggestedPath", out var sp) ? sp.GetString() ?? "/" : "/",
                    InputParameters = parameters,
                    SampleCount = samples.Count,
                    AverageResponseTime = TimeSpan.FromMilliseconds(samples.Average(s => s.Duration.TotalMilliseconds)),
                    SuccessRate = (double)samples.Count(s => s.Success) / samples.Count
                };
            }
        }
        catch { }
        return null;
    }

    private async Task<StateMachineModel> BuildStateMachineAsync(string systemType, List<DiscoveredLegacyOperation> ops, CancellationToken ct)
    {
        var opsSummary = string.Join(", ", ops.Select(o => o.OperationName));
        var prompt = $"For a {systemType} system with operations [{opsSummary}], identify states and transitions. Return JSON: {{\"states\": [{{\"name\": \"X\"}}], \"transitions\": [{{\"fromState\": \"A\", \"toState\": \"B\", \"trigger\": \"op\"}}], \"initialState\": \"X\"}}";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 1500, Temperature = 0.2f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);

        try
        {
            var json = response.Content ?? "{}";
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
                var root = doc.RootElement;
                var states = new List<SystemState>();
                var transitions = new List<StateTransition>();

                if (root.TryGetProperty("states", out var sArr))
                    foreach (var s in sArr.EnumerateArray())
                        states.Add(new SystemState { Name = s.GetProperty("name").GetString() ?? "Unknown" });

                if (root.TryGetProperty("transitions", out var tArr))
                    foreach (var t in tArr.EnumerateArray())
                        transitions.Add(new StateTransition
                        {
                            FromState = t.GetProperty("fromState").GetString() ?? "",
                            ToState = t.GetProperty("toState").GetString() ?? "",
                            Trigger = t.GetProperty("trigger").GetString() ?? ""
                        });

                return new StateMachineModel
                {
                    States = states,
                    Transitions = transitions,
                    InitialState = root.TryGetProperty("initialState", out var init) ? init.GetString() : null
                };
            }
        }
        catch { }
        return new StateMachineModel();
    }

    private async Task<List<BehavioralAnomalyPattern>> DetectAnomalyPatternsAsync(TrafficSamples samples, CancellationToken ct)
    {
        var avgDuration = samples.Samples.Average(s => s.Duration.TotalMilliseconds);
        var failureRate = (double)samples.Samples.Count(s => !s.Success) / samples.Samples.Count;

        var prompt = $"Traffic stats: {samples.Samples.Count} samples, avg duration {avgDuration:F0}ms, failure rate {failureRate:P1}. Identify anomaly patterns. Return JSON array: [{{\"patternName\": \"X\", \"description\": \"Y\", \"severity\": \"medium\", \"detectionCondition\": \"Z\"}}]";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 1000, Temperature = 0.2f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);

        var patterns = new List<BehavioralAnomalyPattern>();
        try
        {
            var json = response.Content ?? "[]";
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
                foreach (var item in doc.RootElement.EnumerateArray())
                    patterns.Add(new BehavioralAnomalyPattern
                    {
                        PatternName = item.GetProperty("patternName").GetString() ?? "Unknown",
                        Description = item.GetProperty("description").GetString() ?? "",
                        Severity = item.TryGetProperty("severity", out var s) ? s.GetString() ?? "medium" : "medium",
                        DetectionCondition = item.TryGetProperty("detectionCondition", out var d) ? d.GetString() : null
                    });
            }
        }
        catch { }
        return patterns;
    }

    private string GenerateOpenApiSpec(string systemId, List<DiscoveredLegacyOperation> operations)
    {
        var spec = new
        {
            openapi = "3.0.3",
            info = new { title = $"{systemId} API", description = "Auto-generated from behavioral modeling", version = "1.0.0" },
            paths = operations.ToDictionary(op => op.SuggestedPath, op => new Dictionary<string, object>
            {
                [op.HttpMethod.ToLowerInvariant()] = new
                {
                    operationId = op.OperationName,
                    parameters = op.InputParameters.Select(p => new { name = p.Name, @in = "query", required = p.Required, schema = new { type = "string" } }),
                    responses = new Dictionary<string, object> { ["200"] = new { description = "Success" } }
                }
            })
        };
        return JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true });
    }
}

#endregion

#region INT5: SmartQuotaTradingStrategy

/// <summary>
/// Request for scheduling optimization.
/// </summary>
public sealed record RequestBatch
{
    /// <summary>Batch identifier.</summary>
    public required string BatchId { get; init; }
    /// <summary>Requests to schedule.</summary>
    public required List<SchedulableRequest> Requests { get; init; }
    /// <summary>Target connectors with their quota information.</summary>
    public required List<ConnectorQuotaInfo> Connectors { get; init; }
    /// <summary>Optimization priority (cost, speed, balance).</summary>
    public string Priority { get; init; } = "balance";
    /// <summary>Deadline for batch completion.</summary>
    public DateTimeOffset? Deadline { get; init; }
}

/// <summary>
/// Individual request to be scheduled.
/// </summary>
public sealed record SchedulableRequest
{
    /// <summary>Request identifier.</summary>
    public required string RequestId { get; init; }
    /// <summary>Target connector ID.</summary>
    public required string ConnectorId { get; init; }
    /// <summary>Request priority (1-10).</summary>
    public int Priority { get; init; } = 5;
    /// <summary>Estimated cost units.</summary>
    public double EstimatedCost { get; init; } = 1.0;
    /// <summary>Whether request can be delayed.</summary>
    public bool CanDelay { get; init; } = true;
    /// <summary>Maximum delay allowed.</summary>
    public TimeSpan? MaxDelay { get; init; }
    /// <summary>Request payload.</summary>
    public Dictionary<string, object> Payload { get; init; } = new();
}

/// <summary>
/// Quota information for a connector.
/// </summary>
public sealed record ConnectorQuotaInfo
{
    /// <summary>Connector identifier.</summary>
    public required string ConnectorId { get; init; }
    /// <summary>Requests per minute limit.</summary>
    public int RateLimit { get; init; } = 60;
    /// <summary>Current usage in this period.</summary>
    public int CurrentUsage { get; init; }
    /// <summary>Quota reset time.</summary>
    public DateTimeOffset ResetTime { get; init; }
    /// <summary>Burst limit (short-term max).</summary>
    public int? BurstLimit { get; init; }
    /// <summary>Cost per request.</summary>
    public double CostPerRequest { get; init; } = 0.01;
    /// <summary>Historical usage patterns.</summary>
    public List<UsagePattern> HistoricalPatterns { get; init; } = new();
}

/// <summary>
/// Historical usage pattern.
/// </summary>
public sealed record UsagePattern
{
    /// <summary>Day of week (0=Sunday).</summary>
    public int DayOfWeek { get; init; }
    /// <summary>Hour of day (0-23).</summary>
    public int HourOfDay { get; init; }
    /// <summary>Average requests in this slot.</summary>
    public double AverageRequests { get; init; }
    /// <summary>Peak requests observed.</summary>
    public int PeakRequests { get; init; }
}

/// <summary>
/// Schedule recommendation from quota optimization.
/// </summary>
public sealed record ScheduleRecommendation
{
    /// <summary>Whether optimization was successful.</summary>
    public required bool Success { get; init; }
    /// <summary>Scheduled requests with timing.</summary>
    public List<ScheduledRequest> ScheduledRequests { get; init; } = new();
    /// <summary>Total estimated cost.</summary>
    public double TotalEstimatedCost { get; init; }
    /// <summary>Estimated completion time.</summary>
    public DateTimeOffset EstimatedCompletion { get; init; }
    /// <summary>Cost savings vs. immediate execution.</summary>
    public double CostSavings { get; init; }
    /// <summary>Optimization insights.</summary>
    public List<string> Insights { get; init; } = new();
    /// <summary>Error message if optimization failed.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Processing time.</summary>
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Scheduled request with timing information.
/// </summary>
public sealed record ScheduledRequest
{
    /// <summary>Original request identifier.</summary>
    public required string RequestId { get; init; }
    /// <summary>Scheduled execution time.</summary>
    public required DateTimeOffset ScheduledTime { get; init; }
    /// <summary>Target connector.</summary>
    public required string ConnectorId { get; init; }
    /// <summary>Estimated cost at this time.</summary>
    public double EstimatedCost { get; init; }
    /// <summary>Reason for this scheduling.</summary>
    public string? SchedulingReason { get; init; }
}

/// <summary>
/// INT5: Smart Quota Trading Strategy ("Quota Trading").
/// AI learns reset cycles, burst limits, and rate patterns to optimize cross-connector traffic.
/// </summary>
public sealed class SmartQuotaTradingStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, List<(DateTimeOffset Time, int Usage)>> _usageHistory = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-quota-trading";

    /// <inheritdoc/>
    public override string StrategyName => "Smart Quota Trading";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Quota Trading",
        Description = "AI-powered request scheduling with rate limit awareness, cost optimization, and cross-connector traffic balancing",
        Capabilities = IntelligenceCapabilities.Prediction | IntelligenceCapabilities.Classification,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "OptimizationWindow", Description = "Time window for optimization (minutes)", Required = false, DefaultValue = "60" },
            new ConfigurationRequirement { Key = "CostWeight", Description = "Weight for cost optimization (0-1)", Required = false, DefaultValue = "0.5" },
            new ConfigurationRequirement { Key = "SpeedWeight", Description = "Weight for speed optimization (0-1)", Required = false, DefaultValue = "0.5" }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "quota", "rate-limit", "optimization", "scheduling", "cost" }
    };

    /// <summary>
    /// Optimizes request scheduling based on quotas and patterns.
    /// </summary>
    /// <param name="requests">Request batch to optimize.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Schedule recommendation.</returns>
    public async Task<ScheduleRecommendation> OptimizeScheduleAsync(RequestBatch requests, CancellationToken ct = default)
    {
        if (AIProvider == null)
            return new ScheduleRecommendation { Success = false, ErrorMessage = "AI provider not configured." };

        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var now = DateTimeOffset.UtcNow;
            var insights = new List<string>();
            var scheduled = new List<ScheduledRequest>();
            var costWeight = double.Parse(GetConfig("CostWeight") ?? "0.5");
            var speedWeight = double.Parse(GetConfig("SpeedWeight") ?? "0.5");

            // Group requests by connector
            var byConnector = requests.Requests.GroupBy(r => r.ConnectorId).ToDictionary(g => g.Key, g => g.ToList());

            // Get quota info for each connector
            var quotaLookup = requests.Connectors.ToDictionary(c => c.ConnectorId);

            foreach (var (connectorId, connectorRequests) in byConnector)
            {
                if (!quotaLookup.TryGetValue(connectorId, out var quota))
                {
                    // No quota info, schedule immediately
                    foreach (var req in connectorRequests)
                        scheduled.Add(new ScheduledRequest { RequestId = req.RequestId, ScheduledTime = now, ConnectorId = connectorId, EstimatedCost = req.EstimatedCost, SchedulingReason = "No quota constraints" });
                    continue;
                }

                // Calculate optimal scheduling
                var optimized = await OptimizeConnectorScheduleAsync(connectorRequests, quota, now, costWeight, speedWeight, ct);
                scheduled.AddRange(optimized.Requests);
                insights.AddRange(optimized.Insights);
            }

            // Sort by scheduled time
            scheduled = scheduled.OrderBy(s => s.ScheduledTime).ToList();

            var totalCost = scheduled.Sum(s => s.EstimatedCost);
            var immediateCost = requests.Requests.Sum(r => r.EstimatedCost * 1.2); // Assume 20% premium for immediate
            var savings = Math.Max(0, immediateCost - totalCost);

            return new ScheduleRecommendation
            {
                Success = true,
                ScheduledRequests = scheduled,
                TotalEstimatedCost = totalCost,
                EstimatedCompletion = scheduled.Count > 0 ? scheduled.Max(s => s.ScheduledTime) : now,
                CostSavings = savings,
                Insights = insights,
                ProcessingTime = sw.Elapsed
            };
        });
    }

    private async Task<(List<ScheduledRequest> Requests, List<string> Insights)> OptimizeConnectorScheduleAsync(
        List<SchedulableRequest> requests, ConnectorQuotaInfo quota, DateTimeOffset now, double costWeight, double speedWeight, CancellationToken ct)
    {
        var scheduled = new List<ScheduledRequest>();
        var insights = new List<string>();

        // Sort by priority
        var sorted = requests.OrderByDescending(r => r.Priority).ThenBy(r => r.CanDelay ? 1 : 0).ToList();

        // Calculate available slots
        var available = quota.RateLimit - quota.CurrentUsage;
        var resetIn = quota.ResetTime - now;

        if (available >= sorted.Count)
        {
            // Can schedule all immediately
            insights.Add($"{quota.ConnectorId}: All {sorted.Count} requests can be executed immediately within quota.");
            foreach (var req in sorted)
                scheduled.Add(new ScheduledRequest { RequestId = req.RequestId, ScheduledTime = now, ConnectorId = quota.ConnectorId, EstimatedCost = req.EstimatedCost * quota.CostPerRequest, SchedulingReason = "Within current quota" });
        }
        else
        {
            // Need to spread across reset periods
            var currentSlot = now;
            var slotUsage = quota.CurrentUsage;

            foreach (var req in sorted)
            {
                if (slotUsage < quota.RateLimit)
                {
                    scheduled.Add(new ScheduledRequest { RequestId = req.RequestId, ScheduledTime = currentSlot, ConnectorId = quota.ConnectorId, EstimatedCost = req.EstimatedCost * quota.CostPerRequest, SchedulingReason = "Current period" });
                    slotUsage++;
                }
                else if (req.CanDelay)
                {
                    // Schedule after reset
                    currentSlot = quota.ResetTime;
                    slotUsage = 1;
                    scheduled.Add(new ScheduledRequest { RequestId = req.RequestId, ScheduledTime = currentSlot, ConnectorId = quota.ConnectorId, EstimatedCost = req.EstimatedCost * quota.CostPerRequest * 0.9, SchedulingReason = "Delayed to next quota period (10% cost savings)" });
                }
                else
                {
                    // Must execute now despite over quota
                    scheduled.Add(new ScheduledRequest { RequestId = req.RequestId, ScheduledTime = now, ConnectorId = quota.ConnectorId, EstimatedCost = req.EstimatedCost * quota.CostPerRequest * 1.5, SchedulingReason = "Priority request (50% overage cost)" });
                }
            }

            insights.Add($"{quota.ConnectorId}: Quota optimization spread {sorted.Count} requests, {available} immediate, rest delayed.");
        }

        // Use AI for pattern-based optimization hints
        if (quota.HistoricalPatterns.Count > 0)
        {
            var patternInsights = await AnalyzePatternsAsync(quota, ct);
            insights.AddRange(patternInsights);
        }

        return (scheduled, insights);
    }

    private async Task<List<string>> AnalyzePatternsAsync(ConnectorQuotaInfo quota, CancellationToken ct)
    {
        var patterns = quota.HistoricalPatterns.Take(10).ToList();
        if (patterns.Count == 0) return new List<string>();

        var prompt = $"Analyze usage patterns for {quota.ConnectorId}: {JsonSerializer.Serialize(patterns)}. Suggest optimal scheduling windows. Return JSON array of insights: [\"insight1\", \"insight2\"]";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 500, Temperature = 0.3f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);

        try
        {
            var json = response.Content ?? "[]";
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
                return doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
        }
        catch { }
        return new List<string>();
    }

    /// <summary>
    /// Records usage for pattern learning.
    /// </summary>
    public void RecordUsage(string connectorId, int requestCount)
    {
        if (!_usageHistory.TryGetValue(connectorId, out var history))
        {
            history = new List<(DateTimeOffset, int)>();
            _usageHistory[connectorId] = history;
        }
        history.Add((DateTimeOffset.UtcNow, requestCount));
        if (history.Count > 1000) history.RemoveRange(0, history.Count - 1000);
    }
}

#endregion

#region INT6: ApiArchaeologistStrategy

/// <summary>
/// Options for API discovery.
/// </summary>
public sealed record DiscoveryOptions
{
    /// <summary>Maximum endpoints to probe.</summary>
    public int MaxEndpoints { get; init; } = 100;
    /// <summary>Request delay between probes (ms).</summary>
    public int DelayBetweenProbesMs { get; init; } = 500;
    /// <summary>HTTP methods to use (GET, HEAD only for safety).</summary>
    public List<string> AllowedMethods { get; init; } = new() { "GET", "HEAD" };
    /// <summary>Path patterns to explore.</summary>
    public List<string> PathPatterns { get; init; } = new() { "/api", "/v1", "/v2", "/graphql", "/rest" };
    /// <summary>Headers to include in probes.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();
    /// <summary>Whether to follow redirects.</summary>
    public bool FollowRedirects { get; init; } = true;
    /// <summary>Stop on first error.</summary>
    public bool StopOnError { get; init; } = false;
}

/// <summary>
/// Discovered API capabilities.
/// </summary>
public sealed record DiscoveredCapabilities
{
    /// <summary>Whether discovery was successful.</summary>
    public required bool Success { get; init; }
    /// <summary>Base URL that was probed.</summary>
    public string? BaseUrl { get; init; }
    /// <summary>Discovered endpoints.</summary>
    public List<DiscoveredApiEndpoint> Endpoints { get; init; } = new();
    /// <summary>Undocumented fields found.</summary>
    public List<UndocumentedField> UndocumentedFields { get; init; } = new();
    /// <summary>Hidden parameters discovered.</summary>
    public List<HiddenParameter> HiddenParameters { get; init; } = new();
    /// <summary>Higher limits discovered.</summary>
    public List<DiscoveredLimit> HigherLimits { get; init; } = new();
    /// <summary>Security observations.</summary>
    public List<string> SecurityNotes { get; init; } = new();
    /// <summary>Error message if discovery failed.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Processing time.</summary>
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Discovered API endpoint.
/// </summary>
public sealed record DiscoveredApiEndpoint
{
    /// <summary>HTTP method.</summary>
    public required string Method { get; init; }
    /// <summary>Endpoint path.</summary>
    public required string Path { get; init; }
    /// <summary>HTTP status code received.</summary>
    public int StatusCode { get; init; }
    /// <summary>Whether endpoint requires authentication.</summary>
    public bool RequiresAuth { get; init; }
    /// <summary>Response content type.</summary>
    public string? ContentType { get; init; }
    /// <summary>Inferred purpose.</summary>
    public string? InferredPurpose { get; init; }
    /// <summary>Whether this appears undocumented.</summary>
    public bool PossiblyUndocumented { get; init; }
}

/// <summary>
/// Undocumented field in API response.
/// </summary>
public sealed record UndocumentedField
{
    /// <summary>Endpoint where field was found.</summary>
    public required string Endpoint { get; init; }
    /// <summary>Field name.</summary>
    public required string FieldName { get; init; }
    /// <summary>Inferred data type.</summary>
    public string? DataType { get; init; }
    /// <summary>Sample value.</summary>
    public string? SampleValue { get; init; }
    /// <summary>Inferred purpose.</summary>
    public string? InferredPurpose { get; init; }
}

/// <summary>
/// Hidden parameter discovered.
/// </summary>
public sealed record HiddenParameter
{
    /// <summary>Endpoint where parameter works.</summary>
    public required string Endpoint { get; init; }
    /// <summary>Parameter name.</summary>
    public required string ParameterName { get; init; }
    /// <summary>Parameter location (query, header).</summary>
    public required string Location { get; init; }
    /// <summary>Effect when used.</summary>
    public string? Effect { get; init; }
}

/// <summary>
/// Discovered limit higher than documented.
/// </summary>
public sealed record DiscoveredLimit
{
    /// <summary>Limit type (page_size, rate_limit, etc.).</summary>
    public required string LimitType { get; init; }
    /// <summary>Documented limit.</summary>
    public int? DocumentedLimit { get; init; }
    /// <summary>Actual limit discovered.</summary>
    public required int ActualLimit { get; init; }
    /// <summary>How it was discovered.</summary>
    public string? DiscoveryMethod { get; init; }
}

/// <summary>
/// INT6: API Archaeologist Strategy ("Undocumented Endpoint Discovery").
/// AI-powered safe fuzzing for hidden API capabilities with non-destructive probing.
/// </summary>
public sealed class ApiArchaeologistStrategy : FeatureStrategyBase
{
    private readonly HttpClient _httpClient;
    private static readonly string[] CommonHiddenParams = { "debug", "verbose", "include_deleted", "show_all", "admin", "internal", "beta", "limit", "page_size", "fields", "expand" };
    private static readonly string[] CommonPaths = { "health", "status", "info", "version", "metrics", "debug", "admin", "internal", "graphql", "swagger", "openapi", "docs" };

    /// <inheritdoc/>
    public override string StrategyId => "feature-api-archaeologist";

    /// <inheritdoc/>
    public override string StrategyName => "API Archaeologist";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Undocumented Discovery",
        Description = "AI-powered safe API probing to discover undocumented endpoints, hidden parameters, and higher limits",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.SemanticSearch,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxProbeDepth", Description = "Maximum path depth for probing", Required = false, DefaultValue = "3" },
            new ConfigurationRequirement { Key = "RateLimitMs", Description = "Minimum delay between probes", Required = false, DefaultValue = "500" },
            new ConfigurationRequirement { Key = "SafeMode", Description = "Only use GET/HEAD methods", Required = false, DefaultValue = "true" }
        },
        CostTier = 2,
        LatencyTier = 3,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "api", "discovery", "fuzzing", "undocumented", "hidden", "archaeology" }
    };

    private static readonly HttpClient SharedHttpClient = new HttpClient();

    /// <summary>
    /// Initializes a new instance of <see cref="ApiArchaeologistStrategy"/>.
    /// </summary>
    public ApiArchaeologistStrategy() : this(SharedHttpClient) { }

    /// <summary>
    /// Initializes with custom HTTP client.
    /// </summary>
    public ApiArchaeologistStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Discovers hidden API capabilities through safe probing.
    /// </summary>
    /// <param name="baseUrl">Base URL to probe.</param>
    /// <param name="options">Discovery options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Discovered capabilities.</returns>
    public async Task<DiscoveredCapabilities> DiscoverAsync(string baseUrl, DiscoveryOptions options, CancellationToken ct = default)
    {
        if (AIProvider == null)
            return new DiscoveredCapabilities { Success = false, ErrorMessage = "AI provider not configured." };

        // Enforce safe mode
        if (bool.Parse(GetConfig("SafeMode") ?? "true"))
            options = options with { AllowedMethods = new List<string> { "GET", "HEAD" } };

        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var endpoints = new List<DiscoveredApiEndpoint>();
            var undocFields = new List<UndocumentedField>();
            var hiddenParams = new List<HiddenParameter>();
            var limits = new List<DiscoveredLimit>();
            var securityNotes = new List<string>();

            // Phase 1: Probe common paths
            foreach (var path in CommonPaths.Concat(options.PathPatterns).Distinct())
            {
                if (ct.IsCancellationRequested) break;
                var endpoint = await ProbeEndpointAsync(baseUrl, $"/{path.TrimStart('/')}", options, ct);
                if (endpoint != null) endpoints.Add(endpoint);
                await Task.Delay(options.DelayBetweenProbesMs, ct);
            }

            // Phase 2: Probe hidden parameters on successful endpoints
            var successfulEndpoints = endpoints.Where(e => e.StatusCode is >= 200 and < 300).ToList();
            foreach (var endpoint in successfulEndpoints.Take(10))
            {
                if (ct.IsCancellationRequested) break;
                var discovered = await ProbeHiddenParamsAsync(baseUrl, endpoint.Path, options, ct);
                hiddenParams.AddRange(discovered);
            }

            // Phase 3: Analyze responses for undocumented fields
            foreach (var endpoint in successfulEndpoints.Take(5))
            {
                if (ct.IsCancellationRequested) break;
                var fields = await AnalyzeResponseFieldsAsync(baseUrl, endpoint.Path, options, ct);
                undocFields.AddRange(fields);
            }

            // Phase 4: Probe limits
            foreach (var endpoint in successfulEndpoints.Take(3))
            {
                if (ct.IsCancellationRequested) break;
                var discoveredLimits = await ProbeLimitsAsync(baseUrl, endpoint.Path, options, ct);
                limits.AddRange(discoveredLimits);
            }

            // Security analysis
            if (endpoints.Any(e => e.Path.Contains("admin") && e.StatusCode != 401))
                securityNotes.Add("Warning: Admin endpoint accessible without authentication");
            if (endpoints.Any(e => e.Path.Contains("debug") && e.StatusCode == 200))
                securityNotes.Add("Warning: Debug endpoint exposed");

            return new DiscoveredCapabilities
            {
                Success = true,
                BaseUrl = baseUrl,
                Endpoints = endpoints,
                UndocumentedFields = undocFields,
                HiddenParameters = hiddenParams,
                HigherLimits = limits,
                SecurityNotes = securityNotes,
                ProcessingTime = sw.Elapsed
            };
        });
    }

    private async Task<DiscoveredApiEndpoint?> ProbeEndpointAsync(string baseUrl, string path, DiscoveryOptions options, CancellationToken ct)
    {
        try
        {
            var url = $"{baseUrl.TrimEnd('/')}{path}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (key, value) in options.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            return new DiscoveredApiEndpoint
            {
                Method = "GET",
                Path = path,
                StatusCode = (int)response.StatusCode,
                RequiresAuth = response.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                ContentType = response.Content.Headers.ContentType?.MediaType,
                PossiblyUndocumented = path.Contains("internal") || path.Contains("debug") || path.Contains("admin")
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<HiddenParameter>> ProbeHiddenParamsAsync(string baseUrl, string path, DiscoveryOptions options, CancellationToken ct)
    {
        var discovered = new List<HiddenParameter>();

        foreach (var param in CommonHiddenParams)
        {
            try
            {
                var urlWithParam = $"{baseUrl.TrimEnd('/')}{path}?{param}=true";
                var urlWithout = $"{baseUrl.TrimEnd('/')}{path}";

                using var reqWith = new HttpRequestMessage(HttpMethod.Get, urlWithParam);
                using var reqWithout = new HttpRequestMessage(HttpMethod.Get, urlWithout);

                var respWith = await _httpClient.SendAsync(reqWith, ct);
                await Task.Delay(options.DelayBetweenProbesMs / 2, ct);
                var respWithout = await _httpClient.SendAsync(reqWithout, ct);

                var bodyWith = await respWith.Content.ReadAsStringAsync(ct);
                var bodyWithout = await respWithout.Content.ReadAsStringAsync(ct);

                if (bodyWith.Length != bodyWithout.Length || respWith.StatusCode != respWithout.StatusCode)
                {
                    discovered.Add(new HiddenParameter
                    {
                        Endpoint = path,
                        ParameterName = param,
                        Location = "query",
                        Effect = bodyWith.Length > bodyWithout.Length ? "Returns more data" : "Changes response"
                    });
                }

                await Task.Delay(options.DelayBetweenProbesMs, ct);
            }
            catch { }
        }

        return discovered;
    }

    private async Task<List<UndocumentedField>> AnalyzeResponseFieldsAsync(string baseUrl, string path, DiscoveryOptions options, CancellationToken ct)
    {
        var fields = new List<UndocumentedField>();

        try
        {
            var url = $"{baseUrl.TrimEnd('/')}{path}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            {
                using var doc = JsonDocument.Parse(body);
                var fieldNames = ExtractFieldNames(doc.RootElement, "");

                // Use AI to identify potentially undocumented fields
                var prompt = $"Analyze these API response fields and identify which might be undocumented/internal: {string.Join(", ", fieldNames.Take(50))}. Return JSON: [{{\"field\": \"name\", \"reason\": \"why\"}}]";
                var aiResp = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 500, Temperature = 0.2f }, ct);
                RecordTokens(aiResp.Usage?.TotalTokens ?? 0);

                try
                {
                    var json = aiResp.Content ?? "[]";
                    var start = json.IndexOf('[');
                    var end = json.LastIndexOf(']');
                    if (start >= 0 && end > start)
                    {
                        using var parsed = JsonDocument.Parse(json.Substring(start, end - start + 1));
                        foreach (var item in parsed.RootElement.EnumerateArray())
                        {
                            fields.Add(new UndocumentedField
                            {
                                Endpoint = path,
                                FieldName = item.GetProperty("field").GetString() ?? "",
                                InferredPurpose = item.TryGetProperty("reason", out var r) ? r.GetString() : null
                            });
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return fields;
    }

    private List<string> ExtractFieldNames(JsonElement element, string prefix)
    {
        var names = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var name = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    names.Add(name);
                    names.AddRange(ExtractFieldNames(prop.Value, name));
                }
                break;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > 0)
                    names.AddRange(ExtractFieldNames(element[0], prefix + "[]"));
                break;
        }
        return names;
    }

    private async Task<List<DiscoveredLimit>> ProbeLimitsAsync(string baseUrl, string path, DiscoveryOptions options, CancellationToken ct)
    {
        var limits = new List<DiscoveredLimit>();

        // Probe page_size limits
        var pageSizes = new[] { 100, 500, 1000, 5000 };
        foreach (var size in pageSizes)
        {
            try
            {
                var url = $"{baseUrl.TrimEnd('/')}{path}?limit={size}&page_size={size}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (body.Length > 1000) // Got substantial data
                    {
                        limits.Add(new DiscoveredLimit { LimitType = "page_size", ActualLimit = size, DiscoveryMethod = "Query parameter probe" });
                        break;
                    }
                }
                await Task.Delay(options.DelayBetweenProbesMs, ct);
            }
            catch { }
        }

        return limits;
    }
}

#endregion

#region INT7: ProbabilisticDataBufferingStrategy

/// <summary>
/// Context for data prediction.
/// </summary>
public sealed record PredictionContext
{
    /// <summary>Connector identifier.</summary>
    public required string ConnectorId { get; init; }
    /// <summary>Data type being predicted.</summary>
    public required string DataType { get; init; }
    /// <summary>Historical data points.</summary>
    public List<HistoricalDataPoint> History { get; init; } = new();
    /// <summary>Prediction horizon.</summary>
    public TimeSpan PredictionHorizon { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Minimum confidence required.</summary>
    public double MinConfidence { get; init; } = 0.7;
    /// <summary>Additional context metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Historical data point for prediction.
/// </summary>
public sealed record HistoricalDataPoint
{
    /// <summary>Timestamp of the data.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>Data value.</summary>
    public required object Value { get; init; }
    /// <summary>Tags/labels.</summary>
    public Dictionary<string, string> Tags { get; init; } = new();
}

/// <summary>
/// Predicted data with confidence intervals.
/// </summary>
public sealed record PredictedData
{
    /// <summary>Whether prediction was successful.</summary>
    public required bool Success { get; init; }
    /// <summary>Data path that was predicted.</summary>
    public string? DataPath { get; init; }
    /// <summary>Predicted values with confidence.</summary>
    public List<PredictedValue> Predictions { get; init; } = new();
    /// <summary>Overall confidence score.</summary>
    public double OverallConfidence { get; init; }
    /// <summary>Prediction method used.</summary>
    public string? PredictionMethod { get; init; }
    /// <summary>When actuals should replace predictions.</summary>
    public DateTimeOffset ExpectedActualTime { get; init; }
    /// <summary>Error message if prediction failed.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Processing time.</summary>
    public TimeSpan ProcessingTime { get; init; }
}

/// <summary>
/// Single predicted value with confidence interval.
/// </summary>
public sealed record PredictedValue
{
    /// <summary>Timestamp for prediction.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>Predicted value.</summary>
    public required object Value { get; init; }
    /// <summary>Confidence score (0-1).</summary>
    public required double Confidence { get; init; }
    /// <summary>Lower bound of confidence interval.</summary>
    public object? LowerBound { get; init; }
    /// <summary>Upper bound of confidence interval.</summary>
    public object? UpperBound { get; init; }
    /// <summary>Whether this has been replaced with actual.</summary>
    public bool ReplacedWithActual { get; init; }
    /// <summary>Actual value if replaced.</summary>
    public object? ActualValue { get; init; }
}

/// <summary>
/// INT7: Probabilistic Data Buffering Strategy ("Quantum Data Buffering").
/// Generates predictions for slow connections with confidence intervals, enabling "Negative Latency" UX.
/// </summary>
public sealed class ProbabilisticDataBufferingStrategy : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, List<HistoricalDataPoint>> _historyCache = new();
    private readonly ConcurrentDictionary<string, PredictedData> _activeBuffers = new();

    /// <inheritdoc/>
    public override string StrategyId => "feature-probabilistic-buffering";

    /// <inheritdoc/>
    public override string StrategyName => "Probabilistic Data Buffering";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Quantum Data Buffering",
        Description = "AI-powered predictive data buffering with confidence intervals for negative latency UX on slow connections",
        Capabilities = IntelligenceCapabilities.Prediction | IntelligenceCapabilities.Summarization,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "DefaultConfidenceThreshold", Description = "Default minimum confidence (0-1)", Required = false, DefaultValue = "0.75" },
            new ConfigurationRequirement { Key = "MaxPredictionHorizon", Description = "Maximum prediction horizon (minutes)", Required = false, DefaultValue = "30" },
            new ConfigurationRequirement { Key = "HistoryWindowSize", Description = "Historical data points to consider", Required = false, DefaultValue = "100" }
        },
        CostTier = 3,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = true,
        Tags = new[] { "prediction", "buffering", "latency", "caching", "probabilistic", "negative-latency" }
    };

    /// <summary>
    /// Generates predictions for a data path.
    /// </summary>
    /// <param name="dataPath">Data path to predict.</param>
    /// <param name="context">Prediction context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Predicted data with confidence intervals.</returns>
    public async Task<PredictedData> PredictAsync(string dataPath, PredictionContext context, CancellationToken ct = default)
    {
        if (AIProvider == null)
            return new PredictedData { Success = false, ErrorMessage = "AI provider not configured." };

        return await ExecuteWithTrackingAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var minConfidence = context.MinConfidence > 0 ? context.MinConfidence : double.Parse(GetConfig("DefaultConfidenceThreshold") ?? "0.75");

            // Get or build history
            var history = context.History.Count > 0 ? context.History : GetCachedHistory(dataPath);

            if (history.Count < 3)
            {
                return new PredictedData
                {
                    Success = false,
                    DataPath = dataPath,
                    ErrorMessage = "Insufficient historical data for prediction (minimum 3 points required)",
                    ProcessingTime = sw.Elapsed
                };
            }

            // Generate predictions
            var predictions = await GeneratePredictionsAsync(dataPath, history, context, ct);

            // Filter by confidence
            var validPredictions = predictions.Where(p => p.Confidence >= minConfidence).ToList();

            if (validPredictions.Count == 0)
            {
                return new PredictedData
                {
                    Success = false,
                    DataPath = dataPath,
                    ErrorMessage = $"No predictions met confidence threshold ({minConfidence:P0})",
                    Predictions = predictions,
                    ProcessingTime = sw.Elapsed
                };
            }

            var result = new PredictedData
            {
                Success = true,
                DataPath = dataPath,
                Predictions = validPredictions,
                OverallConfidence = validPredictions.Average(p => p.Confidence),
                PredictionMethod = DeterminePredictionMethod(history),
                ExpectedActualTime = DateTimeOffset.UtcNow + context.PredictionHorizon,
                ProcessingTime = sw.Elapsed
            };

            _activeBuffers[dataPath] = result;
            return result;
        });
    }

    /// <summary>
    /// Replaces predictions with actual data as it arrives.
    /// </summary>
    /// <param name="dataPath">Data path.</param>
    /// <param name="actualValue">Actual value received.</param>
    /// <param name="timestamp">Timestamp of actual value.</param>
    /// <returns>Updated predicted data.</returns>
    public PredictedData? ReplaceWithActual(string dataPath, object actualValue, DateTimeOffset timestamp)
    {
        if (!_activeBuffers.TryGetValue(dataPath, out var buffer))
            return null;

        var updatedPredictions = buffer.Predictions.Select(p =>
        {
            if (Math.Abs((p.Timestamp - timestamp).TotalSeconds) < 60)
            {
                return p with { ReplacedWithActual = true, ActualValue = actualValue };
            }
            return p;
        }).ToList();

        var updated = buffer with { Predictions = updatedPredictions };
        _activeBuffers[dataPath] = updated;

        // Add to history cache
        AddToHistory(dataPath, new HistoricalDataPoint { Timestamp = timestamp, Value = actualValue });

        return updated;
    }

    /// <summary>
    /// Gets buffered prediction if available.
    /// </summary>
    public PredictedData? GetBufferedPrediction(string dataPath) => _activeBuffers.TryGetValue(dataPath, out var p) ? p : null;

    private async Task<List<PredictedValue>> GeneratePredictionsAsync(string dataPath, List<HistoricalDataPoint> history, PredictionContext context, CancellationToken ct)
    {
        var predictions = new List<PredictedValue>();
        var now = DateTimeOffset.UtcNow;
        var method = DeterminePredictionMethod(history);

        // Use statistical methods first
        if (method == "time_series" && history.All(h => h.Value is double or int or float or decimal))
        {
            predictions = GenerateTimeSeriesPredictions(history, context.PredictionHorizon);
        }
        else
        {
            // Use AI for complex predictions
            predictions = await GenerateAIPredictionsAsync(dataPath, history, context, ct);
        }

        return predictions;
    }

    private List<PredictedValue> GenerateTimeSeriesPredictions(List<HistoricalDataPoint> history, TimeSpan horizon)
    {
        var predictions = new List<PredictedValue>();
        var values = history.Select(h => Convert.ToDouble(h.Value)).ToList();
        var timestamps = history.Select(h => h.Timestamp).ToList();

        // Simple linear regression
        var n = values.Count;
        var avgX = Enumerable.Range(0, n).Average();
        var avgY = values.Average();
        var slope = Enumerable.Range(0, n).Sum(i => (i - avgX) * (values[i] - avgY)) / Enumerable.Range(0, n).Sum(i => Math.Pow(i - avgX, 2));
        var intercept = avgY - slope * avgX;

        // Calculate standard error for confidence intervals
        var residuals = values.Select((v, i) => v - (slope * i + intercept)).ToList();
        var stdError = Math.Sqrt(residuals.Sum(r => r * r) / (n - 2));

        // Generate predictions
        var intervalMs = n > 1 ? (timestamps[^1] - timestamps[0]).TotalMilliseconds / (n - 1) : 60000;
        var steps = (int)(horizon.TotalMilliseconds / intervalMs);

        for (int i = 1; i <= Math.Min(steps, 10); i++)
        {
            var x = n - 1 + i;
            var predicted = slope * x + intercept;
            var confidence = Math.Max(0.5, 1 - (i * 0.05)); // Confidence decreases with horizon

            predictions.Add(new PredictedValue
            {
                Timestamp = timestamps[^1].AddMilliseconds(intervalMs * i),
                Value = predicted,
                Confidence = confidence,
                LowerBound = predicted - 1.96 * stdError,
                UpperBound = predicted + 1.96 * stdError
            });
        }

        return predictions;
    }

    private async Task<List<PredictedValue>> GenerateAIPredictionsAsync(string dataPath, List<HistoricalDataPoint> history, PredictionContext context, CancellationToken ct)
    {
        var historyJson = JsonSerializer.Serialize(history.TakeLast(20).Select(h => new { h.Timestamp, h.Value }));
        var prompt = $@"Predict future values for '{dataPath}' based on this history:
{historyJson}

Data type: {context.DataType}
Prediction horizon: {context.PredictionHorizon.TotalMinutes} minutes

Return JSON array with predictions and confidence:
[{{""timestamp"": ""ISO8601"", ""value"": X, ""confidence"": 0.8, ""lowerBound"": X-Y, ""upperBound"": X+Y}}]";

        var response = await AIProvider!.CompleteAsync(new AIRequest { Prompt = prompt, MaxTokens = 1000, Temperature = 0.2f }, ct);
        RecordTokens(response.Usage?.TotalTokens ?? 0);

        var predictions = new List<PredictedValue>();
        try
        {
            var json = response.Content ?? "[]";
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(json.Substring(start, end - start + 1));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    predictions.Add(new PredictedValue
                    {
                        Timestamp = item.TryGetProperty("timestamp", out var ts) ? DateTimeOffset.Parse(ts.GetString()!) : DateTimeOffset.UtcNow,
                        Value = item.TryGetProperty("value", out var v) ? v.GetDouble() : 0,
                        Confidence = item.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5,
                        LowerBound = item.TryGetProperty("lowerBound", out var lb) ? lb.GetDouble() : null,
                        UpperBound = item.TryGetProperty("upperBound", out var ub) ? ub.GetDouble() : null
                    });
                }
            }
        }
        catch { }

        return predictions;
    }

    private string DeterminePredictionMethod(List<HistoricalDataPoint> history)
    {
        if (history.Count < 3) return "insufficient_data";
        if (history.All(h => h.Value is double or int or float or decimal)) return "time_series";
        return "ai_based";
    }

    private List<HistoricalDataPoint> GetCachedHistory(string dataPath)
    {
        return _historyCache.TryGetValue(dataPath, out var h) ? h : new List<HistoricalDataPoint>();
    }

    private void AddToHistory(string dataPath, HistoricalDataPoint point)
    {
        if (!_historyCache.TryGetValue(dataPath, out var history))
        {
            history = new List<HistoricalDataPoint>();
            _historyCache[dataPath] = history;
        }
        history.Add(point);

        var maxSize = int.Parse(GetConfig("HistoryWindowSize") ?? "100");
        if (history.Count > maxSize)
            history.RemoveRange(0, history.Count - maxSize);
    }

    /// <summary>
    /// Clears prediction buffers.
    /// </summary>
    public void ClearBuffers()
    {
        _activeBuffers.Clear();
    }

    /// <summary>
    /// Seeds history for a data path.
    /// </summary>
    public void SeedHistory(string dataPath, IEnumerable<HistoricalDataPoint> points)
    {
        _historyCache[dataPath] = points.ToList();
    }
}

#endregion
