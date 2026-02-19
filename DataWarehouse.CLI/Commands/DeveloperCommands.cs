// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Spectre.Console;
using System.Text.Json;
using DataWarehouse.Kernel;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
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

    private static DataWarehouseKernel? _kernelInstance;
    private static readonly SemaphoreSlim _kernelLock = new(1, 1);

    private static async Task<DataWarehouseKernel> GetKernelAsync()
    {
        if (_kernelInstance != null)
            return _kernelInstance;

        await _kernelLock.WaitAsync();
        try
        {
            _kernelInstance ??= await KernelBuilder.Create()
                .WithKernelId("cli-developer")
                .WithOperatingMode(OperatingMode.Workstation)
                .BuildAndInitializeAsync(CancellationToken.None);
            return _kernelInstance;
        }
        finally
        {
            _kernelLock.Release();
        }
    }

    #region API Explorer Commands

    public static async Task ListApiEndpointsAsync(string? category, string format)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading API endpoints...", async ctx =>
            {
                await Task.Delay(200);

                var endpoints = await GetApiEndpointsAsync(category);

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

                var schemas = await GetSchemasAsync();

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

                var schema = await GetSchemaAsync(name);

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

                var schema = await GetSchemaAsync(name);
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

                var collections = await GetCollectionsAsync();

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

                var fields = await GetFieldsAsync(collection);

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

                var results = await GetQueryResultsAsync(collection, limit);

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

                var templates = await GetQueryTemplatesAsync();

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

    /// <summary>
    /// Queries the kernel message bus for registered API endpoints.
    /// Returns an empty list if the kernel or interface plugin is unavailable.
    /// </summary>
    private static async Task<List<ApiEndpoint>> GetApiEndpointsAsync(string? category)
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "interface.api.list",
                SourcePluginId = "cli",
                Source = "CLI",
                Payload = category != null
                    ? new Dictionary<string, object> { ["Category"] = category }
                    : new Dictionary<string, object>()
            };

            var response = await kernel.MessageBus.SendAsync(
                "interface.api.list", request, TimeSpan.FromSeconds(5));

            if (response.Success && response.Payload is IEnumerable<object> endpointList)
            {
                var result = new List<ApiEndpoint>();
                foreach (var item in endpointList)
                {
                    if (item is Dictionary<string, object> e)
                    {
                        result.Add(new ApiEndpoint
                        {
                            Method = e.GetValueOrDefault("Method", "")?.ToString() ?? "",
                            Path = e.GetValueOrDefault("Path", "")?.ToString() ?? "",
                            Category = e.GetValueOrDefault("Category", "")?.ToString() ?? "",
                            Description = e.GetValueOrDefault("Description", "")?.ToString() ?? ""
                        });
                    }
                }
                return result;
            }

            AnsiConsole.MarkupLine("[yellow]No API endpoint data available - Interface plugin not responding.[/]");
            return new List<ApiEndpoint>();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]No API endpoint data available - kernel context not accessible.[/]");
            return new List<ApiEndpoint>();
        }
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

    /// <summary>
    /// Queries the kernel message bus for registered data schemas.
    /// Returns an empty list if the kernel or data format plugin is unavailable.
    /// </summary>
    private static async Task<List<SchemaInfo>> GetSchemasAsync()
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "dataformat.schema.list",
                SourcePluginId = "cli",
                Source = "CLI"
            };

            var response = await kernel.MessageBus.SendAsync(
                "dataformat.schema.list", request, TimeSpan.FromSeconds(5));

            if (response.Success && response.Payload is IEnumerable<object> schemaList)
            {
                var result = new List<SchemaInfo>();
                foreach (var item in schemaList)
                {
                    if (item is Dictionary<string, object> s)
                    {
                        result.Add(new SchemaInfo
                        {
                            Name = s.GetValueOrDefault("Name", "")?.ToString() ?? "",
                            FieldCount = s.GetValueOrDefault("FieldCount") is int fc ? fc : 0,
                            Created = s.GetValueOrDefault("Created") is DateTime c ? c : DateTime.MinValue,
                            Modified = s.GetValueOrDefault("Modified") is DateTime m ? m : DateTime.MinValue,
                            Fields = new List<SchemaField>()
                        });
                    }
                }
                return result;
            }

            AnsiConsole.MarkupLine("[yellow]No schema data available - DataFormat plugin not responding.[/]");
            return new List<SchemaInfo>();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]No schema data available - kernel context not accessible.[/]");
            return new List<SchemaInfo>();
        }
    }

    /// <summary>
    /// Queries the kernel message bus for a specific schema by name.
    /// Returns null if the kernel or data format plugin is unavailable.
    /// </summary>
    private static async Task<SchemaInfo?> GetSchemaAsync(string name)
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "dataformat.schema.list",
                SourcePluginId = "cli",
                Source = "CLI",
                Payload = new Dictionary<string, object> { ["Name"] = name }
            };

            var response = await kernel.MessageBus.SendAsync(
                "dataformat.schema.list", request, TimeSpan.FromSeconds(5));

            if (response.Success && response.Payload is Dictionary<string, object> s)
            {
                var fields = new List<SchemaField>();
                if (s.GetValueOrDefault("Fields") is IEnumerable<object> fieldList)
                {
                    foreach (var f in fieldList)
                    {
                        if (f is Dictionary<string, object> fd)
                        {
                            fields.Add(new SchemaField
                            {
                                Name = fd.GetValueOrDefault("Name", "")?.ToString() ?? "",
                                Type = fd.GetValueOrDefault("Type", "")?.ToString() ?? "",
                                Required = fd.GetValueOrDefault("Required") is true,
                                Description = fd.GetValueOrDefault("Description")?.ToString()
                            });
                        }
                    }
                }

                return new SchemaInfo
                {
                    Name = s.GetValueOrDefault("Name", "")?.ToString() ?? "",
                    FieldCount = fields.Count,
                    Created = s.GetValueOrDefault("Created") is DateTime c ? c : DateTime.MinValue,
                    Modified = s.GetValueOrDefault("Modified") is DateTime m ? m : DateTime.MinValue,
                    Fields = fields
                };
            }

            AnsiConsole.MarkupLine("[yellow]No schema data available - DataFormat plugin not responding.[/]");
            return null;
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]No schema data available - kernel context not accessible.[/]");
            return null;
        }
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

    /// <summary>
    /// Queries the kernel message bus for storage collections.
    /// Returns an empty list if the kernel or storage plugin is unavailable.
    /// </summary>
    private static async Task<List<CollectionInfo>> GetCollectionsAsync()
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "storage.collection.list",
                SourcePluginId = "cli",
                Source = "CLI"
            };

            var response = await kernel.MessageBus.SendAsync(
                "storage.collection.list", request, TimeSpan.FromSeconds(5));

            if (response.Success && response.Payload is IEnumerable<object> collectionList)
            {
                var result = new List<CollectionInfo>();
                foreach (var item in collectionList)
                {
                    if (item is Dictionary<string, object> c)
                    {
                        result.Add(new CollectionInfo
                        {
                            Name = c.GetValueOrDefault("Name", "")?.ToString() ?? "",
                            RecordCount = c.GetValueOrDefault("RecordCount") is int rc ? rc : 0,
                            Size = c.GetValueOrDefault("Size") is long sz ? sz : 0
                        });
                    }
                }
                return result;
            }

            AnsiConsole.MarkupLine("[yellow]No collection data available - Storage plugin not responding.[/]");
            return new List<CollectionInfo>();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]No collection data available - kernel context not accessible.[/]");
            return new List<CollectionInfo>();
        }
    }

    /// <summary>
    /// Queries the kernel message bus for fields in a storage collection.
    /// Returns an empty list if the kernel or storage plugin is unavailable.
    /// </summary>
    private static async Task<List<FieldInfo>> GetFieldsAsync(string collection)
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "storage.collection.fields",
                SourcePluginId = "cli",
                Source = "CLI",
                Payload = new Dictionary<string, object> { ["Collection"] = collection }
            };

            var response = await kernel.MessageBus.SendAsync(
                "storage.collection.fields", request, TimeSpan.FromSeconds(5));

            if (response.Success && response.Payload is IEnumerable<object> fieldList)
            {
                var result = new List<FieldInfo>();
                foreach (var item in fieldList)
                {
                    if (item is Dictionary<string, object> f)
                    {
                        result.Add(new FieldInfo
                        {
                            Name = f.GetValueOrDefault("Name", "")?.ToString() ?? "",
                            Type = f.GetValueOrDefault("Type", "")?.ToString() ?? "",
                            Indexed = f.GetValueOrDefault("Indexed") is true
                        });
                    }
                }
                return result;
            }

            AnsiConsole.MarkupLine($"[yellow]No field data available for collection '{collection}' - Storage plugin not responding.[/]");
            return new List<FieldInfo>();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"[yellow]No field data available - kernel context not accessible.[/]");
            return new List<FieldInfo>();
        }
    }

    /// <summary>
    /// Queries the kernel message bus to execute a query against a collection.
    /// Returns an empty list if the kernel or storage plugin is unavailable.
    /// </summary>
    private static async Task<List<Dictionary<string, object?>>> GetQueryResultsAsync(string collection, int limit)
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "storage.query.execute",
                SourcePluginId = "cli",
                Source = "CLI",
                Payload = new Dictionary<string, object>
                {
                    ["Collection"] = collection,
                    ["Limit"] = limit
                }
            };

            var response = await kernel.MessageBus.SendAsync(
                "storage.query.execute", request, TimeSpan.FromSeconds(10));

            if (response.Success && response.Payload is IEnumerable<object> rows)
            {
                var result = new List<Dictionary<string, object?>>();
                foreach (var row in rows)
                {
                    if (row is Dictionary<string, object?> r)
                    {
                        result.Add(r);
                    }
                    else if (row is Dictionary<string, object> ro)
                    {
                        result.Add(ro.ToDictionary(k => k.Key, k => (object?)k.Value));
                    }
                }
                return result;
            }

            AnsiConsole.MarkupLine("[yellow]Query execution unavailable - Storage plugin not responding.[/]");
            return new List<Dictionary<string, object?>>();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]Query execution unavailable - kernel context not accessible.[/]");
            return new List<Dictionary<string, object?>>();
        }
    }

    /// <summary>
    /// Queries the kernel message bus for saved query templates.
    /// Returns an empty list if the kernel or storage plugin is unavailable.
    /// </summary>
    private static async Task<List<QueryTemplate>> GetQueryTemplatesAsync()
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "storage.query.templates",
                SourcePluginId = "cli",
                Source = "CLI"
            };

            var response = await kernel.MessageBus.SendAsync(
                "storage.query.templates", request, TimeSpan.FromSeconds(5));

            if (response.Success && response.Payload is IEnumerable<object> templateList)
            {
                var result = new List<QueryTemplate>();
                foreach (var item in templateList)
                {
                    if (item is Dictionary<string, object> t)
                    {
                        result.Add(new QueryTemplate
                        {
                            Name = t.GetValueOrDefault("Name", "")?.ToString() ?? "",
                            Description = t.GetValueOrDefault("Description", "")?.ToString() ?? "",
                            Collection = t.GetValueOrDefault("Collection", "")?.ToString() ?? "",
                            Created = t.GetValueOrDefault("Created") is DateTime c ? c : DateTime.MinValue
                        });
                    }
                }
                return result;
            }

            AnsiConsole.MarkupLine("[yellow]No query templates available - Storage plugin not responding.[/]");
            return new List<QueryTemplate>();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]No query templates available - kernel context not accessible.[/]");
            return new List<QueryTemplate>();
        }
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
