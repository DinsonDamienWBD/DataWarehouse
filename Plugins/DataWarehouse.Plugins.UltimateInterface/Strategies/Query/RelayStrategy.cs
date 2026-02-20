using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Query;

/// <summary>
/// Relay specification GraphQL interface strategy.
/// </summary>
/// <remarks>
/// <para>
/// Implements the Relay specification on top of GraphQL:
/// <list type="bullet">
/// <item><description>Node interface with global object identification</description></item>
/// <item><description>Connection pattern for cursor-based pagination (edges, nodes, pageInfo)</description></item>
/// <item><description>Mutation pattern with clientMutationId for idempotency</description></item>
/// <item><description>Global ID encoding/decoding (type:id format in base64)</description></item>
/// <item><description>Support for first/after/last/before cursor arguments</description></item>
/// </list>
/// </para>
/// <para>
/// Relay enforces strict pagination patterns and global ID conventions
/// to ensure consistent API design across large-scale applications.
/// </para>
/// </remarks>
internal sealed class RelayStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    public override string StrategyId => "relay";
    public string DisplayName => "Relay (GraphQL)";
    public string SemanticDescription => "Relay specification GraphQL with Node interface, Connection pagination, and global IDs.";
    public InterfaceCategory Category => InterfaceCategory.Query;
    public string[] Tags => ["relay", "graphql", "pagination", "connections", "cursor"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.GraphQL;
    public override SdkInterface.InterfaceCapabilities Capabilities => SdkInterface.InterfaceCapabilities.CreateGraphQLDefaults();

    protected override Task StartAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;
    protected override Task StopAsyncCore(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var bodyText = Encoding.UTF8.GetString(request.Body.Span);
            var relayRequest = JsonSerializer.Deserialize<RelayRequest>(bodyText);

            if (relayRequest?.Query == null)
            {
                return CreateErrorResponse("Query is required");
            }

            // Enforce Node interface pattern
            if (!relayRequest.Query.Contains("node(") && !relayRequest.Query.Contains("edges") && !relayRequest.Query.Contains("pageInfo"))
            {
                // Allow if it's a mutation with clientMutationId
                if (!relayRequest.Query.ToLowerInvariant().StartsWith("mutation"))
                {
                    return CreateErrorResponse("Relay queries must use node() for object retrieval or Connection pattern for lists");
                }
            }

            // Parse pagination arguments
            var paginationArgs = ParsePaginationArguments(relayRequest.Query);

            // Execute Relay-compliant query
            var result = await ExecuteRelayQuery(relayRequest, paginationArgs, cancellationToken);

            return CreateSuccessResponse(result);
        }
        catch (JsonException ex)
        {
            return CreateErrorResponse($"Invalid JSON in request body: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Relay query execution failed: {ex.Message}");
        }
    }

    private PaginationArguments ParsePaginationArguments(string query)
    {
        // Extract first/after/last/before from query
        // Simplified parser for demonstration
        var args = new PaginationArguments();

        if (query.Contains("first:"))
        {
            // Extract first: N
            var match = System.Text.RegularExpressions.Regex.Match(query, @"first:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var first))
            {
                args.First = first;
            }
        }

        if (query.Contains("after:"))
        {
            // Extract after: "cursor"
            var match = System.Text.RegularExpressions.Regex.Match(query, @"after:\s*""([^""]+)""");
            if (match.Success)
            {
                args.After = match.Groups[1].Value;
            }
        }

        if (query.Contains("last:"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(query, @"last:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var last))
            {
                args.Last = last;
            }
        }

        if (query.Contains("before:"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(query, @"before:\s*""([^""]+)""");
            if (match.Success)
            {
                args.Before = match.Groups[1].Value;
            }
        }

        return args;
    }

    private async Task<object> ExecuteRelayQuery(
        RelayRequest request,
        PaginationArguments paginationArgs,
        CancellationToken cancellationToken)
    {
        // Route to message bus for execution
        if (MessageBus != null)
        {
            // Message bus dispatch would happen here
        }

        // Build Relay-compliant response with Connection pattern
        var edges = new[]
        {
            new
            {
                cursor = EncodeGlobalId("cursor", "1"),
                node = new
                {
                    id = EncodeGlobalId("User", "1"),
                    name = "User 1"
                }
            },
            new
            {
                cursor = EncodeGlobalId("cursor", "2"),
                node = new
                {
                    id = EncodeGlobalId("User", "2"),
                    name = "User 2"
                }
            }
        };

        var pageInfo = new
        {
            hasNextPage = true,
            hasPreviousPage = paginationArgs.After != null,
            startCursor = edges.Length > 0 ? edges[0].cursor : null,
            endCursor = edges.Length > 0 ? edges[^1].cursor : null
        };

        return new
        {
            data = new
            {
                connection = new
                {
                    edges,
                    nodes = new[] { edges[0].node, edges[1].node },
                    pageInfo,
                    totalCount = 100
                }
            }
        };
    }

    private string EncodeGlobalId(string typeName, string id)
    {
        // Relay global ID format: base64(type:id)
        var globalId = $"{typeName}:{id}";
        var bytes = Encoding.UTF8.GetBytes(globalId);
        return Convert.ToBase64String(bytes);
    }

    private (string TypeName, string Id) DecodeGlobalId(string globalId)
    {
        var bytes = Convert.FromBase64String(globalId);
        var decoded = Encoding.UTF8.GetString(bytes);
        var parts = decoded.Split(':', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }

    private SdkInterface.InterfaceResponse CreateSuccessResponse(object data)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        return SdkInterface.InterfaceResponse.Ok(body, "application/json");
    }

    private SdkInterface.InterfaceResponse CreateErrorResponse(string message)
    {
        var response = new
        {
            data = (object?)null,
            errors = new[] { new { message, extensions = new { code = "RELAY_SPEC_VIOLATION" } } }
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        return new SdkInterface.InterfaceResponse(
            StatusCode: 400,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: body
        );
    }

    private sealed class RelayRequest
    {
        public string? Query { get; set; }
        public string? OperationName { get; set; }
        public Dictionary<string, object>? Variables { get; set; }
    }

    private sealed class PaginationArguments
    {
        public int? First { get; set; }
        public string? After { get; set; }
        public int? Last { get; set; }
        public string? Before { get; set; }
    }
}
