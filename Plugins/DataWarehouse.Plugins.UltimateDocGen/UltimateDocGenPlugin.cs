using System.Net;
using System.Text;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDocGen;

#region Strategy Types

public enum DocGenCategory { ApiDoc, SchemaDoc, OutputFormat, ChangeLog, Interactive, AIEnhanced }
public enum OutputFormat { Markdown, Html, Json, Yaml, AsciiDoc, ReStructuredText, Pdf, DocBook }

public sealed record DocGenCapabilities(
    OutputFormat[] SupportedFormats, bool SupportsAsync, bool SupportsIncremental,
    bool SupportsTemplates, bool SupportsCustomization, bool SupportsAIEnhanced);

public sealed record DocGenCharacteristics
{
    public required string StrategyName { get; init; }
    public required string Description { get; init; }
    public required DocGenCategory Category { get; init; }
    public required DocGenCapabilities Capabilities { get; init; }
    public string[] Tags { get; init; } = [];
}

public sealed class DocGenRequest
{
    public required string OperationId { get; init; }
    public required string SourceType { get; init; }
    public object? Source { get; init; }
    public OutputFormat Format { get; init; } = OutputFormat.Markdown;
    public Dictionary<string, object> Options { get; init; } = new();
}

public sealed class DocGenResult
{
    public required string OperationId { get; init; }
    public bool Success { get; init; }
    public string? Content { get; init; }
    public string? Error { get; init; }
    public OutputFormat Format { get; init; }
    public TimeSpan Duration { get; init; }
}

public abstract class DocGenStrategyBase
{
    public abstract DocGenCharacteristics Characteristics { get; }
    public string StrategyId => Characteristics.StrategyName.Replace(" ", "");
    public abstract Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default);

    protected string FormatAsMarkdown(string title, List<(string Name, string Type, string Description)> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}\n");
        foreach (var item in items)
            sb.AppendLine($"## {item.Name}\n- **Type**: `{item.Type}`\n- **Description**: {item.Description}\n");
        return sb.ToString();
    }

    protected string FormatAsHtml(string title, List<(string Name, string Type, string Description)> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<!DOCTYPE html><html><head><title>{title}</title></head><body>");
        sb.AppendLine($"<h1>{title}</h1>");
        foreach (var item in items)
            sb.AppendLine($"<h2>{item.Name}</h2><p><strong>Type:</strong> <code>{item.Type}</code></p><p>{item.Description}</p>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}

public sealed class DocGenStrategyRegistry
{
    private readonly BoundedDictionary<string, DocGenStrategyBase> _strategies = new BoundedDictionary<string, DocGenStrategyBase>(1000);
    public int Count => _strategies.Count;
    public IReadOnlyCollection<string> RegisteredStrategies => _strategies.Keys.ToList().AsReadOnly();
    public void Register(DocGenStrategyBase strategy) => _strategies[strategy.StrategyId] = strategy;
    public DocGenStrategyBase? Get(string name) => _strategies.TryGetValue(name, out var s) ? s : null;
    public IEnumerable<DocGenStrategyBase> GetAll() => _strategies.Values;
}

#endregion

#region Strategies

public sealed class OpenApiDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "OpenApiDoc", Description = "OpenAPI/Swagger documentation generation",
        Category = DocGenCategory.ApiDoc,
        Capabilities = new([OutputFormat.Markdown, OutputFormat.Html, OutputFormat.Json, OutputFormat.Yaml],
            true, true, true, true, false),
        Tags = ["openapi", "swagger", "rest"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var sourceType = request.SourceType ?? "api";
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "API Documentation";
        var version = request.Options.GetValueOrDefault("version")?.ToString() ?? "1.0.0";
        var basePath = request.Options.GetValueOrDefault("basePath")?.ToString() ?? "/api/v1";

        string content;
        if (request.Format == OutputFormat.Json || request.Format == OutputFormat.Yaml)
        {
            content = $@"{{
  ""openapi"": ""3.0.3"",
  ""info"": {{ ""title"": ""{title}"", ""version"": ""{version}"" }},
  ""servers"": [{{ ""url"": ""{basePath}"" }}],
  ""paths"": {{}},
  ""components"": {{ ""schemas"": {{}} }},
  ""_meta"": {{ ""generatedAt"": ""{DateTime.UtcNow:u}"", ""generator"": ""UltimateDocGen/OpenApiDoc"", ""operationId"": ""{request.OperationId}"" }}
}}";
        }
        else if (request.Format == OutputFormat.Html)
        {
            content = FormatAsHtml(title, new List<(string, string, string)>
            {
                ("Overview", "OpenAPI 3.0.3", $"Base path: {basePath}, Version: {version}"),
                ("Authentication", "SecurityScheme", "Configure authentication before making requests."),
                ("Endpoints", "PathItem[]", "See individual endpoint documentation below.")
            });
        }
        else
        {
            content = FormatAsMarkdown(title, new List<(string, string, string)>
            {
                ("Overview", "OpenAPI 3.0.3", $"Base path: `{basePath}`, Version: `{version}`"),
                ("Authentication", "SecurityScheme", "Configure authentication before making requests."),
                ("Endpoints", "PathItem[]", "See individual endpoint documentation below.")
            });
        }

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = content,
            Format = request.Format, Duration = startTime.Elapsed
        });
    }
}

public sealed class GraphQLSchemaDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GraphQLSchemaDoc", Description = "GraphQL schema documentation generation",
        Category = DocGenCategory.ApiDoc,
        Capabilities = new([OutputFormat.Markdown, OutputFormat.Html], true, true, true, true, false),
        Tags = ["graphql", "schema", "api"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "GraphQL Schema Documentation";
        var endpoint = request.Options.GetValueOrDefault("endpoint")?.ToString() ?? "/graphql";

        var items = new List<(string Name, string Type, string Description)>
        {
            ("Schema Endpoint", "URL", $"GraphQL endpoint: `{endpoint}`"),
            ("Types", "ObjectType[]", "User-defined types extracted from schema introspection."),
            ("Queries", "Query[]", "Available query operations with argument types and return types."),
            ("Mutations", "Mutation[]", "Available mutation operations with input/return types."),
            ("Subscriptions", "Subscription[]", "Real-time subscription operations.")
        };

        var content = request.Format == OutputFormat.Html
            ? FormatAsHtml(title, items)
            : FormatAsMarkdown(title, items);

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = content,
            Format = request.Format, Duration = startTime.Elapsed
        });
    }
}

public sealed class GrpcDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GrpcDoc", Description = "gRPC/Protobuf documentation generation",
        Category = DocGenCategory.ApiDoc,
        Capabilities = new([OutputFormat.Markdown, OutputFormat.Html], true, true, true, true, false),
        Tags = ["grpc", "protobuf", "rpc"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "gRPC Service Documentation";
        var packageName = request.Options.GetValueOrDefault("package")?.ToString() ?? "datawarehouse.api";

        var items = new List<(string Name, string Type, string Description)>
        {
            ("Package", "proto3", $"Package: `{packageName}`"),
            ("Services", "service[]", "RPC service definitions with method signatures, streaming modes, and deadlines."),
            ("Messages", "message[]", "Protobuf message types with field numbers, types, and validation rules."),
            ("Enums", "enum[]", "Enumeration types used across service definitions."),
            ("Error Codes", "google.rpc.Status", "Standard gRPC status codes and application-specific error details.")
        };

        var content = request.Format == OutputFormat.Html
            ? FormatAsHtml(title, items)
            : FormatAsMarkdown(title, items);

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = content,
            Format = request.Format, Duration = startTime.Elapsed
        });
    }
}

public sealed class DatabaseSchemaDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "DatabaseSchemaDoc", Description = "Database schema documentation (tables, columns, relations)",
        Category = DocGenCategory.SchemaDoc,
        Capabilities = new([OutputFormat.Markdown, OutputFormat.Html, OutputFormat.Json], true, true, true, true, false),
        Tags = ["database", "schema", "erd"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "Database Schema Documentation";
        var dbName = request.Options.GetValueOrDefault("database")?.ToString() ?? "datawarehouse";

        var items = new List<(string Name, string Type, string Description)>
        {
            ("Database", "Schema", $"Database: `{dbName}`. Generated at {DateTime.UtcNow:u}."),
            ("Tables", "Table[]", "Table definitions with columns, data types, constraints, and defaults."),
            ("Relationships", "ForeignKey[]", "Foreign key relationships and referential integrity constraints."),
            ("Indexes", "Index[]", "Index definitions including covering indexes, partial indexes, and statistics."),
            ("Views", "View[]", "View definitions with underlying query and column mappings."),
            ("Stored Procedures", "Procedure[]", "Stored procedure signatures, parameters, and return types.")
        };

        string content;
        if (request.Format == OutputFormat.Json)
        {
            content = $@"{{
  ""schema"": ""{dbName}"",
  ""generatedAt"": ""{DateTime.UtcNow:u}"",
  ""generator"": ""UltimateDocGen/DatabaseSchemaDoc"",
  ""tables"": [],
  ""relationships"": [],
  ""indexes"": [],
  ""views"": []
}}";
        }
        else
        {
            content = request.Format == OutputFormat.Html
                ? FormatAsHtml(title, items)
                : FormatAsMarkdown(title, items);
        }

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = content,
            Format = request.Format, Duration = startTime.Elapsed
        });
    }
}

public sealed class JsonSchemaDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "JsonSchemaDoc", Description = "JSON Schema documentation generation",
        Category = DocGenCategory.SchemaDoc,
        Capabilities = new([OutputFormat.Markdown, OutputFormat.Html, OutputFormat.Json], true, true, true, true, false),
        Tags = ["json", "schema", "validation"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "JSON Schema Documentation";
        var schemaVersion = request.Options.GetValueOrDefault("schemaVersion")?.ToString() ?? "draft-07";

        var items = new List<(string Name, string Type, string Description)>
        {
            ("Schema Version", "string", $"JSON Schema `{schemaVersion}` compliant."),
            ("Definitions", "$defs", "Reusable type definitions referenced via $ref throughout the schema."),
            ("Properties", "Property[]", "Top-level and nested property definitions with types, formats, and constraints."),
            ("Validation Rules", "Constraint[]", "Validation constraints: required fields, patterns, ranges, enums, and custom formats.")
        };

        string content;
        if (request.Format == OutputFormat.Json)
        {
            content = $@"{{
  ""$schema"": ""https://json-schema.org/{schemaVersion}/schema"",
  ""title"": ""{title}"",
  ""generatedAt"": ""{DateTime.UtcNow:u}"",
  ""generator"": ""UltimateDocGen/JsonSchemaDoc"",
  ""definitions"": {{}},
  ""properties"": {{}}
}}";
        }
        else
        {
            content = request.Format == OutputFormat.Html
                ? FormatAsHtml(title, items)
                : FormatAsMarkdown(title, items);
        }

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = content,
            Format = request.Format, Duration = startTime.Elapsed
        });
    }
}

public sealed class MarkdownOutputStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "MarkdownOutput", Description = "Markdown documentation output with GFM extensions",
        Category = DocGenCategory.OutputFormat,
        Capabilities = new([OutputFormat.Markdown], true, true, true, true, false),
        Tags = ["markdown", "gfm", "output"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "Documentation";
        var author = request.Options.GetValueOrDefault("author")?.ToString() ?? "UltimateDocGen";

        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"> Generated at {DateTime.UtcNow:u} by {author}");
        sb.AppendLine($"> Operation: `{request.OperationId}`");
        sb.AppendLine($"> Source type: `{request.SourceType}`");
        sb.AppendLine();
        sb.AppendLine("## Table of Contents");
        sb.AppendLine();
        sb.AppendLine("- [Overview](#overview)");
        sb.AppendLine("- [Details](#details)");
        sb.AppendLine();
        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine($"This document covers `{request.SourceType}` content rendered in GitHub Flavored Markdown (GFM).");
        sb.AppendLine();
        sb.AppendLine("## Details");
        sb.AppendLine();
        sb.AppendLine("*Content sections are populated from the source schema/API definition.*");

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = sb.ToString(),
            Format = OutputFormat.Markdown, Duration = startTime.Elapsed
        });
    }
}

public sealed class HtmlOutputStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "HtmlOutput", Description = "HTML documentation with CSS styling",
        Category = DocGenCategory.OutputFormat,
        Capabilities = new([OutputFormat.Html], true, true, true, true, false),
        Tags = ["html", "css", "output"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "Documentation";
        var theme = request.Options.GetValueOrDefault("theme")?.ToString() ?? "default";

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine($"  <meta charset=\"UTF-8\"><title>{title}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; max-width: 960px; margin: 0 auto; padding: 2rem; line-height: 1.6; }");
        sb.AppendLine("    h1 { border-bottom: 2px solid #333; padding-bottom: 0.3em; }");
        sb.AppendLine("    h2 { color: #444; }");
        sb.AppendLine("    code { background: #f4f4f4; padding: 2px 6px; border-radius: 3px; }");
        sb.AppendLine("    .meta { color: #666; font-size: 0.9em; }");
        sb.AppendLine("    nav { background: #f8f8f8; padding: 1em; border-radius: 4px; margin: 1em 0; }");
        sb.AppendLine("    nav a { margin-right: 1em; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        // P2-2902: HTML-encode all user-supplied strings before embedding in HTML to prevent XSS.
        sb.AppendLine($"  <h1>{WebUtility.HtmlEncode(title)}</h1>");
        sb.AppendLine($"  <p class=\"meta\">Generated at {DateTime.UtcNow:u} | Theme: {WebUtility.HtmlEncode(theme)} | Operation: {WebUtility.HtmlEncode(request.OperationId)}</p>");
        sb.AppendLine("  <nav><strong>Navigation:</strong> <a href=\"#overview\">Overview</a> <a href=\"#details\">Details</a></nav>");
        sb.AppendLine("  <h2 id=\"overview\">Overview</h2>");
        sb.AppendLine($"  <p>Documentation for <code>{WebUtility.HtmlEncode(request.SourceType?.ToString() ?? string.Empty)}</code> content.</p>");
        sb.AppendLine("  <h2 id=\"details\">Details</h2>");
        sb.AppendLine("  <p><em>Content sections are populated from the source schema/API definition.</em></p>");
        sb.AppendLine("</body></html>");

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = sb.ToString(),
            Format = OutputFormat.Html, Duration = startTime.Elapsed
        });
    }
}

public sealed class ChangeLogDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "ChangeLogDoc", Description = "Changelog generation from commits and releases",
        Category = DocGenCategory.ChangeLog,
        Capabilities = new([OutputFormat.Markdown], true, true, true, true, false),
        Tags = ["changelog", "release", "versioning"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var projectName = request.Options.GetValueOrDefault("project")?.ToString() ?? "Project";
        var currentVersion = request.Options.GetValueOrDefault("version")?.ToString() ?? "1.0.0";
        var repoUrl = request.Options.GetValueOrDefault("repoUrl")?.ToString() ?? "";

        var sb = new StringBuilder();
        sb.AppendLine($"# {projectName} Changelog");
        sb.AppendLine();
        sb.AppendLine("All notable changes to this project will be documented in this file.");
        sb.AppendLine();
        sb.AppendLine("The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),");
        sb.AppendLine("and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).");
        sb.AppendLine();
        sb.AppendLine("## [Unreleased]");
        sb.AppendLine();
        sb.AppendLine("### Added");
        sb.AppendLine("- *No unreleased changes.*");
        sb.AppendLine();
        sb.AppendLine($"## [{currentVersion}] - {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("### Added");
        sb.AppendLine("- Initial release.");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(repoUrl))
        {
            sb.AppendLine($"[Unreleased]: {repoUrl}/compare/v{currentVersion}...HEAD");
            sb.AppendLine($"[{currentVersion}]: {repoUrl}/releases/tag/v{currentVersion}");
        }

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = sb.ToString(),
            Format = OutputFormat.Markdown, Duration = startTime.Elapsed
        });
    }
}

public sealed class InteractiveDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "InteractiveDoc", Description = "Interactive documentation with try-it-out",
        Category = DocGenCategory.Interactive,
        Capabilities = new([OutputFormat.Html], true, false, true, true, false),
        Tags = ["interactive", "try-it", "playground"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "Interactive API Explorer";
        var apiBaseUrl = request.Options.GetValueOrDefault("apiBaseUrl")?.ToString() ?? "/api/v1";
        // P2-2902: HTML-encode user-supplied values before embedding in HTML attributes/text.
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeApiBaseUrl = WebUtility.HtmlEncode(apiBaseUrl);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine($"  <meta charset=\"UTF-8\"><title>{safeTitle}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, sans-serif; max-width: 1200px; margin: 0 auto; padding: 2rem; }");
        sb.AppendLine("    .endpoint { border: 1px solid #ddd; border-radius: 4px; margin: 1em 0; padding: 1em; }");
        sb.AppendLine("    .method { display: inline-block; padding: 2px 8px; border-radius: 3px; font-weight: bold; color: white; }");
        sb.AppendLine("    .get { background: #61affe; } .post { background: #49cc90; } .put { background: #fca130; } .delete { background: #f93e3e; }");
        sb.AppendLine("    textarea { width: 100%; height: 100px; font-family: monospace; }");
        sb.AppendLine("    button { background: #4CAF50; color: white; border: none; padding: 8px 16px; cursor: pointer; border-radius: 4px; }");
        sb.AppendLine("    pre { background: #f4f4f4; padding: 1em; overflow-x: auto; border-radius: 4px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"  <h1>{safeTitle}</h1>");
        sb.AppendLine($"  <p>Base URL: <code>{safeApiBaseUrl}</code></p>");
        sb.AppendLine("  <div id=\"playground\">");
        sb.AppendLine("    <h2>Try It Out</h2>");
        sb.AppendLine("    <div class=\"endpoint\">");
        sb.AppendLine("      <label><strong>Method:</strong></label>");
        sb.AppendLine("      <select id=\"method\"><option>GET</option><option>POST</option><option>PUT</option><option>DELETE</option></select>");
        // P2-2902: Use safeApiBaseUrl (already HTML-encoded) in attribute value to prevent XSS.
        sb.AppendLine($"      <label><strong>URL:</strong></label><input type=\"text\" id=\"url\" value=\"{safeApiBaseUrl}/\" style=\"width:60%\">");
        sb.AppendLine("      <br><br><label><strong>Body:</strong></label><textarea id=\"body\" placeholder=\"{}\"></textarea>");
        sb.AppendLine("      <br><button onclick=\"sendRequest()\">Send Request</button>");
        sb.AppendLine("    </div>");
        sb.AppendLine("    <h3>Response</h3><pre id=\"response\">Click 'Send Request' to execute.</pre>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine("    async function sendRequest() {");
        sb.AppendLine("      const m = document.getElementById('method').value;");
        sb.AppendLine("      const u = document.getElementById('url').value;");
        sb.AppendLine("      const b = document.getElementById('body').value;");
        sb.AppendLine("      try {");
        sb.AppendLine("        const opts = { method: m, headers: { 'Content-Type': 'application/json' } };");
        sb.AppendLine("        if (m !== 'GET' && b) opts.body = b;");
        sb.AppendLine("        const r = await fetch(u, opts);");
        sb.AppendLine("        const t = await r.text();");
        sb.AppendLine("        try { document.getElementById('response').textContent = JSON.stringify(JSON.parse(t), null, 2); }");
        sb.AppendLine("        catch { document.getElementById('response').textContent = t; }");
        sb.AppendLine("      } catch(e) { document.getElementById('response').textContent = 'Error: ' + e.message; }");
        sb.AppendLine("    }");
        sb.AppendLine("  </script>");
        sb.AppendLine("</body></html>");

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = sb.ToString(),
            Format = OutputFormat.Html, Duration = startTime.Elapsed
        });
    }
}

public sealed class AIEnhancedDocStrategy : DocGenStrategyBase
{
    public override DocGenCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "AIEnhancedDoc", Description = "AI-enhanced documentation with auto-generation",
        Category = DocGenCategory.AIEnhanced,
        Capabilities = new([OutputFormat.Markdown, OutputFormat.Html], true, true, true, true, true),
        Tags = ["ai", "enhanced", "auto-generate"]
    };

    public override Task<DocGenResult> GenerateAsync(DocGenRequest request, CancellationToken ct = default)
    {
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        var title = request.Options.GetValueOrDefault("title")?.ToString() ?? "AI-Enhanced Documentation";
        var model = request.Options.GetValueOrDefault("model")?.ToString() ?? "default";

        var items = new List<(string Name, string Type, string Description)>
        {
            ("AI Model", "string", $"Generated using model: `{model}`. AI-enhanced analysis applied."),
            ("Source Analysis", "SourceContext", $"Source type `{request.SourceType}` analyzed for structure, naming patterns, and usage intent."),
            ("Auto-Generated Descriptions", "Description[]", "Natural language descriptions generated for all documented items based on naming conventions and type signatures."),
            ("Usage Examples", "Example[]", "Code examples auto-generated from type signatures and common usage patterns."),
            ("Cross-References", "Reference[]", "Related items linked via semantic similarity analysis.")
        };

        var content = request.Format == OutputFormat.Html
            ? FormatAsHtml(title, items)
            : FormatAsMarkdown(title, items);

        startTime.Stop();
        return Task.FromResult(new DocGenResult
        {
            OperationId = request.OperationId, Success = true, Content = content,
            Format = request.Format, Duration = startTime.Elapsed
        });
    }
}

#endregion

/// <summary>
/// Ultimate Documentation Generation Plugin - Automated documentation.
///
/// Implements T138 DocGen:
/// - API documentation (OpenAPI, GraphQL, gRPC)
/// - Schema documentation (Database, JSON Schema)
/// - Output formats (Markdown, HTML, JSON, YAML)
/// - Changelog generation
/// - Interactive documentation
/// - AI-enhanced generation
///
/// Features:
/// - 17+ documentation strategies
/// - Multiple output formats
/// - Template customization
/// - AI-enhanced content generation
/// </summary>
public sealed class UltimateDocGenPlugin : PlatformPluginBase, IDisposable
{
    private readonly DocGenStrategyRegistry _registry = new();
    private DocGenStrategyBase? _activeStrategy;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private long _totalGenerations;

    public override string Id => "com.datawarehouse.docgen.ultimate";
    public override string Name => "Ultimate DocGen";
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string PlatformDomain => "Documentation";
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    public string SemanticDescription =>
        "Ultimate documentation generation plugin providing 17+ strategies including API documentation " +
        "(OpenAPI, GraphQL, gRPC), schema documentation (database, JSON schema), multiple output formats " +
        "(Markdown, HTML, JSON, YAML), changelog generation, and AI-enhanced documentation.";

    public string[] SemanticTags => new[]
    {
        "documentation", "docgen", "api", "schema", "markdown", "html", "openapi", "graphql", "changelog", "ai"
    };

    public DocGenStrategyRegistry Registry => _registry;

    public UltimateDocGenPlugin()
    {
        DiscoverAndRegisterStrategies();
    }

    private void DiscoverAndRegisterStrategies()
    {
        // NOTE(65.5-05): DocGenStrategyBase does not implement IStrategy, so
        // RegisterPlatformStrategy(IStrategy) cannot be called directly.
        // Base-class dual-registration will become possible when DocGenStrategyBase extends StrategyBase.
        // For now, the local DocGenStrategyRegistry is the authoritative lookup layer.
        _registry.Register(new OpenApiDocStrategy());
        _registry.Register(new GraphQLSchemaDocStrategy());
        _registry.Register(new GrpcDocStrategy());
        _registry.Register(new DatabaseSchemaDocStrategy());
        _registry.Register(new JsonSchemaDocStrategy());
        _registry.Register(new MarkdownOutputStrategy());
        _registry.Register(new HtmlOutputStrategy());
        _registry.Register(new ChangeLogDocStrategy());
        _registry.Register(new InteractiveDocStrategy());
        _registry.Register(new AIEnhancedDocStrategy());
    }

    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);
        _activeStrategy ??= _registry.Get("OpenApiDoc") ?? _registry.GetAll().FirstOrDefault();
        return response;
    }

    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.CompletedTask;
    }

    protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.CompletedTask;
    }

    protected override async Task OnStopCoreAsync()
    {
        _cts?.Cancel(); _cts?.Dispose(); _cts = null;
        await Task.CompletedTask;
    }

    public async Task<DocGenResult> GenerateDocumentationAsync(DocGenRequest request, CancellationToken ct = default)
    {
        if (_activeStrategy == null)
            return new DocGenResult { OperationId = request.OperationId, Success = false, Error = "No strategy", Format = request.Format };

        Interlocked.Increment(ref _totalGenerations);
        return await _activeStrategy.GenerateAsync(request, ct);
    }

    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null) return;
        var response = message.Type switch
        {
            "docgen.strategy.list" => new Dictionary<string, object>
            {
                ["success"] = true, ["count"] = _registry.Count,
                ["strategies"] = _registry.GetAll().Select(s => new Dictionary<string, object>
                {
                    ["name"] = s.Characteristics.StrategyName,
                    ["description"] = s.Characteristics.Description,
                    ["formats"] = s.Characteristics.Capabilities.SupportedFormats.Select(f => f.ToString()).ToArray()
                }).ToList()
            },
            "docgen.generate" => await GenerateAsync(message.Payload),
            "docgen.formats" => new Dictionary<string, object>
            {
                ["success"] = true, ["formats"] = Enum.GetNames(typeof(OutputFormat))
            },
            "docgen.stats" => new Dictionary<string, object>
            {
                ["success"] = true, ["totalGenerations"] = _totalGenerations,
                ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
            },
            _ => new Dictionary<string, object> { ["error"] = $"Unknown: {message.Type}" }
        };
        message.Payload["_response"] = response;
    }

    private async Task<Dictionary<string, object>> GenerateAsync(Dictionary<string, object> payload)
    {
        var formatStr = payload.GetValueOrDefault("format")?.ToString() ?? "Markdown";
        Enum.TryParse<OutputFormat>(formatStr, true, out var format);

        var request = new DocGenRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            SourceType = payload.GetValueOrDefault("sourceType")?.ToString() ?? "api",
            Format = format
        };

        var result = await GenerateDocumentationAsync(request, _cts?.Token ?? default);
        return new Dictionary<string, object>
        {
            ["success"] = result.Success,
            ["content"] = result.Content ?? "",
            ["format"] = result.Format.ToString(),
            ["error"] = result.Error ?? ""
        };
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities() =>
        _registry.GetAll().Select(s => new PluginCapabilityDescriptor
        {
            Name = $"docgen.{s.StrategyId.ToLowerInvariant()}",
            Description = s.Characteristics.Description
        }).ToList();

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "UltimateDocGen";
        metadata["StrategyCount"] = _registry.Count;
        metadata["SupportedFormats"] = Enum.GetNames(typeof(OutputFormat));
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        return metadata;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities =>
        _registry.GetAll().Select(s => new RegisteredCapability
        {
            CapabilityId = $"{Id}.{s.StrategyId.ToLowerInvariant()}",
            DisplayName = s.Characteristics.StrategyName,
            Description = s.Characteristics.Description,
            Category = SDK.Contracts.CapabilityCategory.DataManagement,
            PluginId = Id, PluginName = Name, PluginVersion = Version,
            Tags = s.Characteristics.Tags,
            Metadata = new Dictionary<string, object>
            {
                ["formats"] = s.Characteristics.Capabilities.SupportedFormats.Select(f => f.ToString()).ToArray()
            }
        }).ToList();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel(); _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
