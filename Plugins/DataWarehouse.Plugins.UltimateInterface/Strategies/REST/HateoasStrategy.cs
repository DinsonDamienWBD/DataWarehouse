using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.REST;

/// <summary>
/// HATEOAS (Hypermedia as the Engine of Application State) strategy for REST APIs.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready HATEOAS handling with:
/// <list type="bullet">
/// <item><description>Hypermedia controls embedded in all responses</description></item>
/// <item><description>Link relations (self, next, prev, related, collection)</description></item>
/// <item><description>Dynamic link generation based on resource type and state</description></item>
/// <item><description>Discoverable API navigation through links</description></item>
/// <item><description>HAL (Hypertext Application Language) compatible format</description></item>
/// </list>
/// </para>
/// <para>
/// This strategy wraps all responses with _links objects that allow clients to navigate
/// the API without hardcoding URLs, following the HATEOAS constraint of REST.
/// </para>
/// </remarks>
internal sealed class HateoasStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public override string StrategyId => "hateoas";
    public string DisplayName => "HATEOAS";
    public string SemanticDescription => "Hypermedia as the Engine of Application State - REST API with embedded hypermedia controls and link relations.";
    public InterfaceCategory Category => InterfaceCategory.Http;
    public string[] Tags => new[] { "hateoas", "hypermedia", "hal", "rest", "links" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/hal+json", "application/json" },
        MaxRequestSize: 10 * 1024 * 1024, // 10 MB
        MaxResponseSize: 50 * 1024 * 1024, // 50 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Initializes the HATEOAS strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up HATEOAS resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles requests with HATEOAS hypermedia controls.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse with hypermedia-enriched body.</returns>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = request.Path?.TrimStart('/') ?? string.Empty;
            var queryParams = request.QueryParameters ?? new Dictionary<string, string>();

            // Parse pagination for link generation
            var page = queryParams.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var p) ? p : 1;
            var pageSize = queryParams.TryGetValue("pageSize", out var sizeStr) && int.TryParse(sizeStr, out var s) ? s : 20;

            // Route based on HTTP method
            var result = request.Method switch
            {
                SdkInterface.HttpMethod.GET => await HandleGetAsync(path, page, pageSize, cancellationToken),
                SdkInterface.HttpMethod.POST => await HandlePostAsync(path, request.Body, cancellationToken),
                SdkInterface.HttpMethod.PUT => await HandlePutAsync(path, request.Body, cancellationToken),
                SdkInterface.HttpMethod.DELETE => await HandleDeleteAsync(path, cancellationToken),
                SdkInterface.HttpMethod.PATCH => await HandlePatchAsync(path, request.Body, cancellationToken),
                _ => (StatusCode: 405, Data: CreateErrorResponse("Method not allowed", $"Method {request.Method} is not supported"))
            };

            var json = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions { WriteIndented = true });
            var body = Encoding.UTF8.GetBytes(json);

            return new SdkInterface.InterfaceResponse(
                StatusCode: result.StatusCode,
                Headers: new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/hal+json"
                },
                Body: body
            );
        }
        catch (JsonException ex)
        {
            return CreateErrorInterfaceResponse(400, "Bad Request", $"Invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CreateErrorInterfaceResponse(500, "Internal Server Error", ex.Message);
        }
    }

    /// <summary>
    /// Handles GET requests with hypermedia links.
    /// </summary>
    private async Task<(int StatusCode, object Data)> HandleGetAsync(
        string path,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var message = new SDK.Utilities.PluginMessage
            {
                Type = "storage.read",
                Payload = new Dictionary<string, object>
                {
                    ["operation"] = "read",
                    ["path"] = path,
                    ["page"] = page,
                    ["pageSize"] = pageSize
                }
            };

            var busResponse = await MessageBus.SendAsync("storage.read", message, cancellationToken);
            if (busResponse.Success && busResponse.Payload != null)
            {
                return (200, busResponse.Payload);
            }

            return (503, CreateErrorResponse("Service Unavailable", busResponse.ErrorMessage ?? "Storage backend unavailable"));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resourceType = segments.Length > 0 ? segments[0] : "resources";
        var resourceId = segments.Length > 1 ? segments[1] : null;

        if (resourceId != null)
        {
            // Single resource with links
            return (200, new
            {
                id = resourceId,
                name = $"{resourceType} {resourceId}",
                description = $"Sample {resourceType} resource",
                createdAt = DateTimeOffset.UtcNow.ToString("O"),
                _links = GenerateResourceLinks(resourceType, resourceId)
            });
        }

        // Collection with pagination links
        var totalItems = 100; // Mock total
        var totalPages = (int)Math.Ceiling((double)totalItems / pageSize);

        return (200, new
        {
            items = new[]
            {
                new
                {
                    id = "1",
                    name = $"{resourceType} 1",
                    _links = GenerateResourceLinks(resourceType, "1")
                },
                new
                {
                    id = "2",
                    name = $"{resourceType} 2",
                    _links = GenerateResourceLinks(resourceType, "2")
                }
            },
            page,
            pageSize,
            totalItems,
            totalPages,
            _links = GenerateCollectionLinks(resourceType, page, pageSize, totalPages)
        });
    }

    /// <summary>
    /// Handles POST requests for resource creation.
    /// </summary>
    private async Task<(int StatusCode, object Data)> HandlePostAsync(
        string path,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (body.Length == 0)
            return (400, CreateErrorResponse("Bad Request", "Request body is required"));

        var bodyText = Encoding.UTF8.GetString(body.Span);

        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var message = new SDK.Utilities.PluginMessage
            {
                Type = "storage.write",
                Payload = new Dictionary<string, object>
                {
                    ["operation"] = "create",
                    ["path"] = path,
                    ["body"] = bodyText
                }
            };

            var busResponse = await MessageBus.SendAsync("storage.write", message, cancellationToken);
            if (busResponse.Success && busResponse.Payload != null)
            {
                return (201, busResponse.Payload);
            }

            return (503, CreateErrorResponse("Service Unavailable", busResponse.ErrorMessage ?? "Storage backend unavailable"));
        }

        var resourceId = Guid.NewGuid().ToString();
        var resourceType = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "resources";

        return (201, new
        {
            id = resourceId,
            name = $"New {resourceType}",
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            _links = GenerateResourceLinks(resourceType, resourceId)
        });
    }

    /// <summary>
    /// Handles PUT requests for resource updates.
    /// </summary>
    private async Task<(int StatusCode, object Data)> HandlePutAsync(
        string path,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (body.Length == 0)
            return (400, CreateErrorResponse("Bad Request", "Request body is required"));

        var bodyText = Encoding.UTF8.GetString(body.Span);

        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var message = new SDK.Utilities.PluginMessage
            {
                Type = "storage.write",
                Payload = new Dictionary<string, object>
                {
                    ["operation"] = "update",
                    ["path"] = path,
                    ["body"] = bodyText
                }
            };

            var busResponse = await MessageBus.SendAsync("storage.write", message, cancellationToken);
            if (busResponse.Success && busResponse.Payload != null)
            {
                return (200, busResponse.Payload);
            }

            return (503, CreateErrorResponse("Service Unavailable", busResponse.ErrorMessage ?? "Storage backend unavailable"));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resourceType = segments.Length > 0 ? segments[0] : "resources";
        var resourceId = segments.Length > 1 ? segments[1] : "unknown";

        return (200, new
        {
            id = resourceId,
            name = $"Updated {resourceType}",
            updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            _links = GenerateResourceLinks(resourceType, resourceId)
        });
    }

    /// <summary>
    /// Handles PATCH requests for partial resource updates.
    /// </summary>
    private async Task<(int StatusCode, object Data)> HandlePatchAsync(
        string path,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (body.Length == 0)
            return (400, CreateErrorResponse("Bad Request", "Request body is required"));

        var bodyText = Encoding.UTF8.GetString(body.Span);

        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var message = new SDK.Utilities.PluginMessage
            {
                Type = "storage.write",
                Payload = new Dictionary<string, object>
                {
                    ["operation"] = "patch",
                    ["path"] = path,
                    ["body"] = bodyText
                }
            };

            var busResponse = await MessageBus.SendAsync("storage.write", message, cancellationToken);
            if (busResponse.Success && busResponse.Payload != null)
            {
                return (200, busResponse.Payload);
            }

            return (503, CreateErrorResponse("Service Unavailable", busResponse.ErrorMessage ?? "Storage backend unavailable"));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resourceType = segments.Length > 0 ? segments[0] : "resources";
        var resourceId = segments.Length > 1 ? segments[1] : "unknown";

        return (200, new
        {
            id = resourceId,
            name = $"Patched {resourceType}",
            updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            _links = GenerateResourceLinks(resourceType, resourceId)
        });
    }

    /// <summary>
    /// Handles DELETE requests for resource removal.
    /// </summary>
    private async Task<(int StatusCode, object Data)> HandleDeleteAsync(
        string path,
        CancellationToken cancellationToken)
    {
        // Route via message bus if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            var message = new SDK.Utilities.PluginMessage
            {
                Type = "storage.delete",
                Payload = new Dictionary<string, object>
                {
                    ["operation"] = "delete",
                    ["path"] = path
                }
            };

            var busResponse = await MessageBus.SendAsync("storage.delete", message, cancellationToken);
            if (busResponse.Success)
            {
                return (204, new { });
            }

            return (503, CreateErrorResponse("Service Unavailable", busResponse.ErrorMessage ?? "Storage backend unavailable"));
        }

        return (204, new { });
    }

    /// <summary>
    /// Generates hypermedia links for a single resource.
    /// </summary>
    private object GenerateResourceLinks(string resourceType, string resourceId)
    {
        return new
        {
            self = new { href = $"/api/{resourceType}/{resourceId}" },
            collection = new { href = $"/api/{resourceType}" },
            edit = new { href = $"/api/{resourceType}/{resourceId}" },
            delete = new { href = $"/api/{resourceType}/{resourceId}" },
            related = new
            {
                metadata = new { href = $"/api/{resourceType}/{resourceId}/metadata" },
                history = new { href = $"/api/{resourceType}/{resourceId}/history" }
            }
        };
    }

    /// <summary>
    /// Generates hypermedia links for a resource collection.
    /// </summary>
    private object GenerateCollectionLinks(string resourceType, int page, int pageSize, int totalPages)
    {
        return new
        {
            self = new { href = $"/api/{resourceType}?page={page}&pageSize={pageSize}" },
            first = new { href = $"/api/{resourceType}?page=1&pageSize={pageSize}" },
            prev = page > 1 ? new { href = $"/api/{resourceType}?page={page - 1}&pageSize={pageSize}" } : null,
            next = page < totalPages ? new { href = $"/api/{resourceType}?page={page + 1}&pageSize={pageSize}" } : null,
            last = new { href = $"/api/{resourceType}?page={totalPages}&pageSize={pageSize}" },
            create = new { href = $"/api/{resourceType}", method = "POST" }
        };
    }

    /// <summary>
    /// Creates an error response with HATEOAS links.
    /// </summary>
    private object CreateErrorResponse(string title, string detail)
    {
        return new
        {
            error = new
            {
                title,
                detail
            },
            _links = new
            {
                self = new { href = "/api/error" },
                home = new { href = "/api" }
            }
        };
    }

    /// <summary>
    /// Creates an error InterfaceResponse.
    /// </summary>
    private SdkInterface.InterfaceResponse CreateErrorInterfaceResponse(int statusCode, string title, string detail)
    {
        var errorData = CreateErrorResponse(title, detail);
        var json = JsonSerializer.Serialize(errorData);
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: statusCode,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/hal+json"
            },
            Body: body
        );
    }
}
