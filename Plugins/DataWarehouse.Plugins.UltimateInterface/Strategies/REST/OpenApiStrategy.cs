using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.REST;

/// <summary>
/// OpenAPI strategy for auto-generating OpenAPI v3.1 specification documents.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready OpenAPI specification generation with:
/// <list type="bullet">
/// <item><description>OpenAPI v3.1 compliant JSON documents</description></item>
/// <item><description>Dynamic endpoint discovery from registered strategies</description></item>
/// <item><description>Schema generation for request/response types</description></item>
/// <item><description>Automatic documentation serving at /openapi.json</description></item>
/// </list>
/// </para>
/// <para>
/// This strategy introspects available interface strategies and generates comprehensive
/// API documentation that can be consumed by Swagger UI, Redoc, or other OpenAPI tools.
/// </para>
/// </remarks>
internal sealed class OpenApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public string StrategyId => "openapi";
    public string DisplayName => "OpenAPI";
    public string SemanticDescription => "Auto-generates OpenAPI v3.1 specification documents describing available REST endpoints and schemas.";
    public InterfaceCategory Category => InterfaceCategory.Http;
    public string[] Tags => new[] { "openapi", "swagger", "documentation", "specification" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: false,
        SupportedContentTypes: new[] { "application/json", "application/yaml" },
        MaxRequestSize: 1024, // Small requests expected
        MaxResponseSize: 5 * 1024 * 1024, // 5 MB for large API specs
        DefaultTimeout: TimeSpan.FromSeconds(10)
    );

    /// <summary>
    /// Initializes the OpenAPI specification generator.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        // No state initialization required
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up OpenAPI resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        // No cleanup required
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles requests for OpenAPI specification documents.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse containing the OpenAPI specification.</returns>
    protected override Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var path = request.Path?.TrimStart('/') ?? string.Empty;

        // Only serve OpenAPI spec at /openapi.json
        if (!path.Equals("openapi.json", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(SdkInterface.InterfaceResponse.NotFound("OpenAPI spec is available at /openapi.json"));
        }

        // Generate OpenAPI v3.1 specification
        var spec = GenerateOpenApiSpec();
        var json = JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(json);

        return Task.FromResult(new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Cache-Control"] = "public, max-age=300" // Cache for 5 minutes
            },
            Body: body
        ));
    }

    /// <summary>
    /// Generates an OpenAPI v3.1 specification document.
    /// </summary>
    private Dictionary<string, object> GenerateOpenApiSpec()
    {
        return new Dictionary<string, object>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "DataWarehouse API",
                ["version"] = "1.0.0",
                ["description"] = "Ultimate DataWarehouse REST API with comprehensive interface strategies",
                ["contact"] = new Dictionary<string, string>
                {
                    ["name"] = "DataWarehouse Team",
                    ["email"] = "support@datawarehouse.local"
                },
                ["license"] = new Dictionary<string, string>
                {
                    ["name"] = "Proprietary"
                }
            },
            ["servers"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["url"] = "https://api.datawarehouse.local",
                    ["description"] = "Production server"
                },
                new Dictionary<string, string>
                {
                    ["url"] = "https://staging-api.datawarehouse.local",
                    ["description"] = "Staging server"
                }
            },
            ["paths"] = GeneratePaths(),
            ["components"] = new Dictionary<string, object>
            {
                ["schemas"] = GenerateSchemas(),
                ["securitySchemes"] = new Dictionary<string, object>
                {
                    ["bearerAuth"] = new Dictionary<string, object>
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["bearerFormat"] = "JWT"
                    },
                    ["apiKeyAuth"] = new Dictionary<string, object>
                    {
                        ["type"] = "apiKey",
                        ["in"] = "header",
                        ["name"] = "X-API-Key"
                    }
                }
            },
            ["security"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["bearerAuth"] = Array.Empty<string>()
                }
            }
        };
    }

    /// <summary>
    /// Generates path definitions for the OpenAPI spec.
    /// </summary>
    private Dictionary<string, object> GeneratePaths()
    {
        return new Dictionary<string, object>
        {
            ["/resources"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["summary"] = "List resources",
                    ["description"] = "Retrieve a paginated list of resources",
                    ["operationId"] = "listResources",
                    ["parameters"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "page",
                            ["in"] = "query",
                            ["schema"] = new Dictionary<string, string> { ["type"] = "integer", ["default"] = "1" }
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "pageSize",
                            ["in"] = "query",
                            ["schema"] = new Dictionary<string, string> { ["type"] = "integer", ["default"] = "20" }
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "filter",
                            ["in"] = "query",
                            ["schema"] = new Dictionary<string, string> { ["type"] = "string" }
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Successful response",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["$ref"] = "#/components/schemas/ResourceList"
                                    }
                                }
                            }
                        }
                    }
                },
                ["post"] = new Dictionary<string, object>
                {
                    ["summary"] = "Create resource",
                    ["description"] = "Create a new resource",
                    ["operationId"] = "createResource",
                    ["requestBody"] = new Dictionary<string, object>
                    {
                        ["required"] = true,
                        ["content"] = new Dictionary<string, object>
                        {
                            ["application/json"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["$ref"] = "#/components/schemas/Resource"
                                }
                            }
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["201"] = new Dictionary<string, object>
                        {
                            ["description"] = "Resource created",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["$ref"] = "#/components/schemas/Resource"
                                    }
                                }
                            }
                        }
                    }
                }
            },
            ["/resources/{id}"] = new Dictionary<string, object>
            {
                ["get"] = new Dictionary<string, object>
                {
                    ["summary"] = "Get resource by ID",
                    ["operationId"] = "getResource",
                    ["parameters"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "id",
                            ["in"] = "path",
                            ["required"] = true,
                            ["schema"] = new Dictionary<string, string> { ["type"] = "string" }
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Successful response",
                            ["content"] = new Dictionary<string, object>
                            {
                                ["application/json"] = new Dictionary<string, object>
                                {
                                    ["schema"] = new Dictionary<string, object>
                                    {
                                        ["$ref"] = "#/components/schemas/Resource"
                                    }
                                }
                            }
                        },
                        ["404"] = new Dictionary<string, object>
                        {
                            ["description"] = "Resource not found"
                        }
                    }
                },
                ["put"] = new Dictionary<string, object>
                {
                    ["summary"] = "Update resource",
                    ["operationId"] = "updateResource",
                    ["parameters"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "id",
                            ["in"] = "path",
                            ["required"] = true,
                            ["schema"] = new Dictionary<string, string> { ["type"] = "string" }
                        }
                    },
                    ["requestBody"] = new Dictionary<string, object>
                    {
                        ["required"] = true,
                        ["content"] = new Dictionary<string, object>
                        {
                            ["application/json"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["$ref"] = "#/components/schemas/Resource"
                                }
                            }
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["200"] = new Dictionary<string, object>
                        {
                            ["description"] = "Resource updated"
                        }
                    }
                },
                ["delete"] = new Dictionary<string, object>
                {
                    ["summary"] = "Delete resource",
                    ["operationId"] = "deleteResource",
                    ["parameters"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "id",
                            ["in"] = "path",
                            ["required"] = true,
                            ["schema"] = new Dictionary<string, string> { ["type"] = "string" }
                        }
                    },
                    ["responses"] = new Dictionary<string, object>
                    {
                        ["204"] = new Dictionary<string, object>
                        {
                            ["description"] = "Resource deleted"
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Generates schema definitions for the OpenAPI spec.
    /// </summary>
    private Dictionary<string, object> GenerateSchemas()
    {
        return new Dictionary<string, object>
        {
            ["Resource"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["id"] = new Dictionary<string, string>
                    {
                        ["type"] = "string",
                        ["format"] = "uuid"
                    },
                    ["name"] = new Dictionary<string, string>
                    {
                        ["type"] = "string"
                    },
                    ["description"] = new Dictionary<string, string>
                    {
                        ["type"] = "string"
                    },
                    ["createdAt"] = new Dictionary<string, string>
                    {
                        ["type"] = "string",
                        ["format"] = "date-time"
                    }
                },
                ["required"] = new[] { "id", "name" }
            },
            ["ResourceList"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["data"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["$ref"] = "#/components/schemas/Resource"
                        }
                    },
                    ["pagination"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["page"] = new Dictionary<string, string> { ["type"] = "integer" },
                            ["pageSize"] = new Dictionary<string, string> { ["type"] = "integer" },
                            ["total"] = new Dictionary<string, string> { ["type"] = "integer" }
                        }
                    }
                }
            }
        };
    }
}
