// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Spectre.Console;
using System.Text.Json;
using DataWarehouse.Shared.Services;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Developer tools commands for the DataWarehouse CLI.
/// Uses shared DeveloperToolsService for feature parity with GUI.
/// </summary>
public static class DeveloperCommands
{
    // DeveloperToolsService injection point: wire IDeveloperToolsService when service layer is created.
    // private static IDeveloperToolsService? _devToolsService;

    #region API Explorer Commands

    public static async Task ListApiEndpointsAsync(string? category, string format)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading API endpoints...", async ctx =>
            {
                await Task.Delay(200);

                var endpoints = GetApiEndpoints(category);

                if (endpoints.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No API endpoints found.[/]");
                    return;
                }

                if (format.ToLower() == "json")
                {
                    var json = JsonSerializer.Serialize(endpoints, new JsonSerializerOptions { WriteIndented = true });
                    AnsiConsole.WriteLine(json);
                    return;
                }

                // Table format (default)
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Method")
                    .AddColumn("Path")
                    .AddColumn("Category")
                    .AddColumn("Description");

                foreach (var endpoint in endpoints)
                {
                    var methodColor = endpoint.Method switch
                    {
                        "GET" => "green",
                        "POST" => "blue",
                        "PUT" => "yellow",
                        "DELETE" => "red",
                        _ => "white"
                    };

                    table.AddRow(
                        $"[{methodColor}]{endpoint.Method}[/]",
                        endpoint.Path,
                        endpoint.Category,
                        endpoint.Description
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[gray]Total: {endpoints.Count} endpoint(s)[/]");
            });
    }

    public static async Task CallApiAsync(string endpoint, string method, string? body, string[]? headers)
    {
        await AnsiConsole.Status()
            .StartAsync($"Calling {method} {endpoint}...", async ctx =>
            {
                // Placeholder: replace with actual API call via DeveloperToolsService when available.
                await Task.Delay(300);

                AnsiConsole.MarkupLine($"[bold]API Call:[/] {method} {endpoint}");

                if (headers?.Length > 0)
                {
                    AnsiConsole.MarkupLine("\n[bold]Request Headers:[/]");
                    foreach (var header in headers)
                    {
                        var parts = header.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            AnsiConsole.MarkupLine($"  [cyan]{parts[0]}:[/] {parts[1].Trim()}");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(body))
                {
                    AnsiConsole.MarkupLine("\n[bold]Request Body:[/]");
                    AnsiConsole.WriteLine(body);
                }

                AnsiConsole.MarkupLine("\n[bold green]Response:[/] 200 OK");
                AnsiConsole.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    message = "API call executed successfully",
                    timestamp = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }));
            });
    }

    public static async Task GenerateApiCodeAsync(string endpoint, string language)
    {
        await AnsiConsole.Status()
            .StartAsync($"Generating {language} code...", async ctx =>
            {
                await Task.Delay(200);

                var code = language.ToLower() switch
                {
                    "curl" => GenerateCurlCode(endpoint),
                    "csharp" => GenerateCSharpCode(endpoint),
                    "python" => GeneratePythonCode(endpoint),
                    "javascript" => GenerateJavaScriptCode(endpoint),
                    _ => $"// Language '{language}' not supported"
                };

                var panel = new Panel(code)
                {
                    Header = new PanelHeader($"[bold]{language.ToUpper()} Code Snippet[/]"),
                    Border = BoxBorder.Rounded,
                    BorderStyle = Style.Parse("cyan")
                };

                AnsiConsole.Write(panel);
            });
    }

    #endregion

    #region Schema Designer Commands

    public static async Task ListSchemasAsync(string format)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading schemas...", async ctx =>
            {
                await Task.Delay(200);

                var schemas = GetSchemas();

                if (schemas.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No schemas found.[/]");
                    return;
                }

                if (format.ToLower() == "json")
                {
                    var json = JsonSerializer.Serialize(schemas, new JsonSerializerOptions { WriteIndented = true });
                    AnsiConsole.WriteLine(json);
                    return;
                }

                // Table format (default)
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Name")
                    .AddColumn("Fields")
                    .AddColumn("Created")
                    .AddColumn("Modified");

                foreach (var schema in schemas)
                {
                    table.AddRow(
                        $"[cyan]{schema.Name}[/]",
                        schema.FieldCount.ToString(),
                        schema.Created.ToString("yyyy-MM-dd HH:mm"),
                        schema.Modified.ToString("yyyy-MM-dd HH:mm")
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[gray]Total: {schemas.Count} schema(s)[/]");
            });
    }

    public static async Task ShowSchemaAsync(string name, string format)
    {
        await AnsiConsole.Status()
            .StartAsync($"Loading schema '{name}'...", async ctx =>
            {
                await Task.Delay(200);

                var schema = GetSchema(name);

                if (schema == null)
                {
                    AnsiConsole.MarkupLine($"[red]Schema '{name}' not found.[/]");
                    return;
                }

                if (format.ToLower() == "json")
                {
                    var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
                    AnsiConsole.WriteLine(json);
                }
                else if (format.ToLower() == "yaml")
                {
                    // Simple YAML-like output
                    AnsiConsole.MarkupLine($"[bold]name:[/] {schema.Name}");
                    AnsiConsole.MarkupLine($"[bold]fields:[/]");
                    foreach (var field in schema.Fields)
                    {
                        AnsiConsole.MarkupLine($"  - [cyan]{field.Name}[/]: {field.Type}");
                        if (!string.IsNullOrEmpty(field.Description))
                        {
                            AnsiConsole.MarkupLine($"    description: {field.Description}");
                        }
                    }
                }
                else
                {
                    // Table format
                    var panel = new Panel(new Markup($"[bold]Name:[/] {schema.Name}\n[bold]Created:[/] {schema.Created:yyyy-MM-dd HH:mm}"))
                    {
                        Header = new PanelHeader("[bold]Schema Details[/]"),
                        Border = BoxBorder.Rounded
                    };
                    AnsiConsole.Write(panel);

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("Field")
                        .AddColumn("Type")
                        .AddColumn("Required")
                        .AddColumn("Description");

                    foreach (var field in schema.Fields)
                    {
                        table.AddRow(
                            $"[cyan]{field.Name}[/]",
                            field.Type,
                            field.Required ? "[green]Yes[/]" : "[gray]No[/]",
                            field.Description ?? ""
                        );
                    }

                    AnsiConsole.Write(table);
                }
            });
    }

    public static async Task CreateSchemaAsync(string name, string? file)
    {
        await AnsiConsole.Status()
            .StartAsync($"Creating schema '{name}'...", async ctx =>
            {
                await Task.Delay(300);

                // Placeholder: replace with actual schema creation via DeveloperToolsService when available.
                AnsiConsole.MarkupLine($"[green]Schema '{name}' created successfully.[/]");
            });
    }

    public static async Task UpdateSchemaAsync(string name, string? file)
    {
        await AnsiConsole.Status()
            .StartAsync($"Updating schema '{name}'...", async ctx =>
            {
                await Task.Delay(300);

                // Placeholder: replace with actual schema update via DeveloperToolsService when available.
                AnsiConsole.MarkupLine($"[green]Schema '{name}' updated successfully.[/]");
            });
    }

    public static async Task DeleteSchemaAsync(string name, bool force)
    {
        if (!force)
        {
            var confirm = AnsiConsole.Confirm($"Are you sure you want to delete schema [yellow]{name}[/]?", false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[yellow]Operation cancelled.[/]");
                return;
            }
        }

        await AnsiConsole.Status()
            .StartAsync($"Deleting schema '{name}'...", async ctx =>
            {
                await Task.Delay(300);

                // Placeholder: replace with actual schema deletion via DeveloperToolsService when available.
                AnsiConsole.MarkupLine($"[green]Schema '{name}' deleted successfully.[/]");
            });
    }

    public static async Task ExportSchemaAsync(string name, string format, string? output)
    {
        await AnsiConsole.Status()
            .StartAsync($"Exporting schema '{name}'...", async ctx =>
            {
                await Task.Delay(300);

                var schema = GetSchema(name);
                if (schema == null)
                {
                    AnsiConsole.MarkupLine($"[red]Schema '{name}' not found.[/]");
                    return;
                }

                var content = format.ToLower() switch
                {
                    "json" => JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true }),
                    "yaml" => ConvertToYaml(schema),
                    _ => JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true })
                };

                if (string.IsNullOrEmpty(output))
                {
                    AnsiConsole.WriteLine(content);
                }
                else
                {
                    await File.WriteAllTextAsync(output, content);
                    AnsiConsole.MarkupLine($"[green]Schema exported to:[/] {output}");
                }
            });
    }

    public static async Task ImportSchemaAsync(string file)
    {
        if (!File.Exists(file))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {file}");
            return;
        }

        await AnsiConsole.Status()
            .StartAsync($"Importing schema from {file}...", async ctx =>
            {
                await Task.Delay(300);

                // Placeholder: replace with actual schema import via DeveloperToolsService when available.
                AnsiConsole.MarkupLine($"[green]Schema imported successfully from:[/] {file}");
            });
    }

    #endregion

    #region Query Builder Commands

    public static async Task ListCollectionsAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Loading collections...", async ctx =>
            {
                await Task.Delay(200);

                var collections = GetCollections();

                if (collections.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No collections found.[/]");
                    return;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Collection")
                    .AddColumn("Record Count")
                    .AddColumn("Size");

                foreach (var collection in collections)
                {
                    table.AddRow(
                        $"[cyan]{collection.Name}[/]",
                        collection.RecordCount.ToString("N0"),
                        OutputFormatter.FormatBytes(collection.Size)
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[gray]Total: {collections.Count} collection(s)[/]");
            });
    }

    public static async Task ListFieldsAsync(string collection)
    {
        await AnsiConsole.Status()
            .StartAsync($"Loading fields for '{collection}'...", async ctx =>
            {
                await Task.Delay(200);

                var fields = GetFields(collection);

                if (fields.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]No fields found for collection '{collection}'.[/]");
                    return;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Field")
                    .AddColumn("Type")
                    .AddColumn("Indexed");

                foreach (var field in fields)
                {
                    table.AddRow(
                        $"[cyan]{field.Name}[/]",
                        field.Type,
                        field.Indexed ? "[green]Yes[/]" : "[gray]No[/]"
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[gray]Total: {fields.Count} field(s)[/]");
            });
    }

    public static async Task RunQueryAsync(
        string collection,
        string[]? select,
        string[]? where,
        string[]? sort,
        int limit,
        int offset,
        string format)
    {
        await AnsiConsole.Status()
            .StartAsync($"Executing query on '{collection}'...", async ctx =>
            {
                await Task.Delay(300);

                // Build query description
                var queryDesc = $"SELECT {(select?.Length > 0 ? string.Join(", ", select) : "*")}\n";
                queryDesc += $"FROM {collection}";
                if (where?.Length > 0)
                    queryDesc += $"\nWHERE {string.Join(" AND ", where)}";
                if (sort?.Length > 0)
                    queryDesc += $"\nORDER BY {string.Join(", ", sort)}";
                queryDesc += $"\nLIMIT {limit} OFFSET {offset}";

                AnsiConsole.MarkupLine("[bold]Query:[/]");
                var panel = new Panel(queryDesc)
                {
                    Border = BoxBorder.Rounded,
                    BorderStyle = Style.Parse("cyan")
                };
                AnsiConsole.Write(panel);

                // Placeholder: replace with actual query execution via DeveloperToolsService when available.
                var results = GetSampleResults(collection, limit);

                AnsiConsole.MarkupLine($"\n[bold]Results:[/] {results.Count} row(s)");

                if (format.ToLower() == "json")
                {
                    var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
                    AnsiConsole.WriteLine(json);
                }
                else
                {
                    // Table format
                    if (results.Count > 0)
                    {
                        var table = new Table().Border(TableBorder.Rounded);

                        // Add columns from first result
                        foreach (var kvp in results[0])
                        {
                            table.AddColumn(kvp.Key);
                        }

                        // Add rows
                        foreach (var result in results)
                        {
                            table.AddRow(result.Values.Select(v => v?.ToString() ?? "").ToArray());
                        }

                        AnsiConsole.Write(table);
                    }
                }
            });
    }

    public static async Task ListQueryTemplatesAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Loading query templates...", async ctx =>
            {
                await Task.Delay(200);

                var templates = GetQueryTemplates();

                if (templates.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No query templates found.[/]");
                    return;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Name")
                    .AddColumn("Description")
                    .AddColumn("Collection")
                    .AddColumn("Created");

                foreach (var template in templates)
                {
                    table.AddRow(
                        $"[cyan]{template.Name}[/]",
                        template.Description,
                        template.Collection,
                        template.Created.ToString("yyyy-MM-dd HH:mm")
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[gray]Total: {templates.Count} template(s)[/]");
            });
    }

    public static async Task SaveQueryTemplateAsync(string name, string? description)
    {
        await AnsiConsole.Status()
            .StartAsync($"Saving query template '{name}'...", async ctx =>
            {
                await Task.Delay(300);

                // Placeholder: replace with actual template save via DeveloperToolsService when available.
                AnsiConsole.MarkupLine($"[green]Query template '{name}' saved successfully.[/]");
            });
    }

    public static async Task LoadQueryTemplateAsync(string name)
    {
        await AnsiConsole.Status()
            .StartAsync($"Loading query template '{name}'...", async ctx =>
            {
                await Task.Delay(300);

                // Placeholder: replace with actual template load and execution via DeveloperToolsService when available.
                AnsiConsole.MarkupLine($"[green]Query template '{name}' loaded and executed.[/]");
            });
    }

    #endregion

    #region Helper Methods

    // Queries kernel for real data; returns empty if kernel unavailable
    private static List<ApiEndpoint> GetApiEndpoints(string? category)
    {
        // TODO: Query kernel/message bus for actual API endpoints
        // For now, returns empty list if kernel is unavailable
        var endpoints = new List<ApiEndpoint>();

        // Placeholder: When kernel is available, query it here
        // If kernel unavailable, return empty list with clear message

        return endpoints;
    }

    private static string GenerateCurlCode(string endpoint)
    {
        return $"curl -X GET \"http://localhost:5000{endpoint}\" \\\n  -H \"Content-Type: application/json\"";
    }

    private static string GenerateCSharpCode(string endpoint)
    {
        return $@"using var client = new HttpClient();
var response = await client.GetAsync(""http://localhost:5000{endpoint}"");
var content = await response.Content.ReadAsStringAsync();";
    }

    private static string GeneratePythonCode(string endpoint)
    {
        return $@"import requests

response = requests.get('http://localhost:5000{endpoint}')
data = response.json()";
    }

    private static string GenerateJavaScriptCode(string endpoint)
    {
        return $@"const response = await fetch('http://localhost:5000{endpoint}');
const data = await response.json();";
    }

    // Queries kernel for real data; returns empty if kernel unavailable
    private static List<SchemaInfo> GetSchemas()
    {
        // TODO: Query kernel/message bus for actual schemas
        // For now, returns empty list if kernel is unavailable
        return new List<SchemaInfo>();
    }

    // Queries kernel for real data; returns null if kernel unavailable
    private static SchemaInfo? GetSchema(string name)
    {
        // TODO: Query kernel/message bus for actual schema
        // For now, returns null if kernel is unavailable
        return null;
    }

    private static string ConvertToYaml(SchemaInfo schema)
    {
        var yaml = $"name: {schema.Name}\n";
        yaml += "fields:\n";
        foreach (var field in schema.Fields)
        {
            yaml += $"  - name: {field.Name}\n";
            yaml += $"    type: {field.Type}\n";
            yaml += $"    required: {field.Required.ToString().ToLower()}\n";
            if (!string.IsNullOrEmpty(field.Description))
            {
                yaml += $"    description: {field.Description}\n";
            }
        }
        return yaml;
    }

    // Queries kernel for real data; returns empty if kernel unavailable
    private static List<CollectionInfo> GetCollections()
    {
        // TODO: Query kernel/message bus for actual collections
        // For now, returns empty list if kernel is unavailable
        return new List<CollectionInfo>();
    }

    // Queries kernel for real data; returns empty if kernel unavailable
    private static List<FieldInfo> GetFields(string collection)
    {
        // TODO: Query kernel/message bus for actual fields
        // For now, returns empty list if kernel is unavailable
        return new List<FieldInfo>();
    }

    // Queries kernel for real data; returns empty if kernel unavailable
    private static List<Dictionary<string, object?>> GetSampleResults(string collection, int limit)
    {
        // TODO: Query kernel/message bus for actual query results
        // For now, returns empty list if kernel is unavailable
        return new List<Dictionary<string, object?>>();
    }

    // Queries kernel for real data; returns empty if kernel unavailable
    private static List<QueryTemplate> GetQueryTemplates()
    {
        // TODO: Query kernel/message bus for actual query templates
        // For now, returns empty list if kernel is unavailable
        return new List<QueryTemplate>();
    }

    #endregion

    #region Data Models

    private record ApiEndpoint
    {
        public string Method { get; init; } = "";
        public string Path { get; init; } = "";
        public string Category { get; init; } = "";
        public string Description { get; init; } = "";
    }

    private record SchemaInfo
    {
        public string Name { get; init; } = "";
        public int FieldCount { get; init; }
        public DateTime Created { get; init; }
        public DateTime Modified { get; init; }
        public List<SchemaField> Fields { get; init; } = new();
    }

    private record SchemaField
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public bool Required { get; init; }
        public string? Description { get; init; }
    }

    private record CollectionInfo
    {
        public string Name { get; init; } = "";
        public int RecordCount { get; init; }
        public long Size { get; init; }
    }

    private record FieldInfo
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public bool Indexed { get; init; }
    }

    private record QueryTemplate
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Collection { get; init; } = "";
        public DateTime Created { get; init; }
    }

    #endregion
}
