using DataWarehouse.Shared.Models;
using Newtonsoft.Json;
using System.Text;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Shared business logic for developer tools
/// Used by both CLI and GUI for feature parity
/// </summary>
public interface IDeveloperToolsService
{
    // API Explorer
    Task<IEnumerable<ApiEndpoint>> GetApiEndpointsAsync(CancellationToken ct = default);
    Task<ApiResponse> ExecuteApiCallAsync(ApiRequest request, CancellationToken ct = default);
    Task<string> GenerateCodeSnippetAsync(ApiEndpoint endpoint, string language, CancellationToken ct = default);

    // Schema Designer
    Task<IEnumerable<SchemaDefinition>> GetSchemasAsync(CancellationToken ct = default);
    Task<SchemaDefinition> GetSchemaAsync(string name, CancellationToken ct = default);
    Task<SchemaDefinition> CreateSchemaAsync(SchemaDefinition schema, CancellationToken ct = default);
    Task<SchemaDefinition> UpdateSchemaAsync(string name, SchemaDefinition schema, CancellationToken ct = default);
    Task DeleteSchemaAsync(string name, CancellationToken ct = default);
    Task<string> ExportSchemaAsync(string name, string format, CancellationToken ct = default);
    Task<SchemaDefinition> ImportSchemaAsync(string content, string format, CancellationToken ct = default);

    // Query Builder
    Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default);
    Task<IEnumerable<string>> GetFieldsAsync(string collection, CancellationToken ct = default);
    Task<QueryResult> ExecuteQueryAsync(QueryDefinition query, CancellationToken ct = default);
    Task<IEnumerable<QueryTemplate>> GetQueryTemplatesAsync(CancellationToken ct = default);
    Task<QueryTemplate> SaveQueryTemplateAsync(QueryTemplate template, CancellationToken ct = default);
    Task DeleteQueryTemplateAsync(string name, CancellationToken ct = default);
    string BuildQueryPreview(QueryDefinition query);
}

/// <summary>
/// Implementation of developer tools service
/// </summary>
public class DeveloperToolsService : IDeveloperToolsService
{
    private readonly InstanceManager _instanceManager;
    private readonly string _templatesDir;

    public DeveloperToolsService(InstanceManager instanceManager)
    {
        _instanceManager = instanceManager;

        // Initialize templates directory
        _templatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "QueryTemplates");
        Directory.CreateDirectory(_templatesDir);
    }

    // ===== API Explorer =====

    public async Task<IEnumerable<ApiEndpoint>> GetApiEndpointsAsync(CancellationToken ct = default)
    {
        var response = await _instanceManager.ExecuteAsync(
            "api.endpoints.list",
            null,
            ct);

        if (response?.Data != null && response.Data.ContainsKey("endpoints"))
        {
            var json = JsonConvert.SerializeObject(response.Data["endpoints"]);
            return JsonConvert.DeserializeObject<List<ApiEndpoint>>(json) ?? new List<ApiEndpoint>();
        }

        return new List<ApiEndpoint>();
    }

    public async Task<ApiResponse> ExecuteApiCallAsync(ApiRequest request, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            var response = await _instanceManager.ExecuteAsync(
                "api.execute",
                new Dictionary<string, object>
                {
                    ["endpoint"] = request.Endpoint,
                    ["method"] = request.Method,
                    ["parameters"] = request.Parameters,
                    ["headers"] = request.Headers,
                    ["body"] = request.Body ?? new { }
                },
                ct);

            var duration = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            if (response?.Data != null)
            {
                return new ApiResponse
                {
                    StatusCode = response.Data.ContainsKey("statusCode")
                        ? Convert.ToInt32(response.Data["statusCode"])
                        : 200,
                    StatusMessage = response.Data.ContainsKey("statusMessage")
                        ? response.Data["statusMessage"]?.ToString() ?? "OK"
                        : "OK",
                    Headers = response.Data.ContainsKey("headers")
                        ? JsonConvert.DeserializeObject<Dictionary<string, string>>(
                            JsonConvert.SerializeObject(response.Data["headers"])) ?? new()
                        : new(),
                    Body = response.Data.ContainsKey("body") ? response.Data["body"] : null,
                    DurationMs = duration,
                    Success = response.Data.ContainsKey("success")
                        ? Convert.ToBoolean(response.Data["success"])
                        : true
                };
            }

            return new ApiResponse
            {
                StatusCode = 500,
                StatusMessage = "No response from server",
                DurationMs = duration,
                Success = false,
                ErrorMessage = "Empty response received"
            };
        }
        catch (Exception ex)
        {
            var duration = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
            return new ApiResponse
            {
                StatusCode = 500,
                StatusMessage = "Error",
                DurationMs = duration,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> GenerateCodeSnippetAsync(
        ApiEndpoint endpoint,
        string language,
        CancellationToken ct = default)
    {
        var response = await _instanceManager.ExecuteAsync(
            "api.codegen",
            new Dictionary<string, object>
            {
                ["endpoint"] = endpoint.Path,
                ["method"] = endpoint.Method,
                ["language"] = language,
                ["parameters"] = endpoint.Parameters
            },
            ct);

        if (response?.Data != null && response.Data.ContainsKey("code"))
        {
            return response.Data["code"]?.ToString() ?? string.Empty;
        }

        // Fallback: Generate simple snippet locally
        return GenerateLocalCodeSnippet(endpoint, language);
    }

    private string GenerateLocalCodeSnippet(ApiEndpoint endpoint, string language)
    {
        return language.ToLower() switch
        {
            "csharp" => GenerateCSharpSnippet(endpoint),
            "python" => GeneratePythonSnippet(endpoint),
            "javascript" => GenerateJavaScriptSnippet(endpoint),
            "curl" => GenerateCurlSnippet(endpoint),
            _ => $"// Code generation not supported for {language}"
        };
    }

    private string GenerateCSharpSnippet(ApiEndpoint endpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Net.Http;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine($"// {endpoint.Description}");
        sb.AppendLine($"var client = new HttpClient();");
        sb.AppendLine($"var response = await client.{endpoint.Method.ToUpper()}Async(\"{endpoint.Path}\");");
        sb.AppendLine("var content = await response.Content.ReadAsStringAsync();");
        return sb.ToString();
    }

    private string GeneratePythonSnippet(ApiEndpoint endpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import requests");
        sb.AppendLine();
        sb.AppendLine($"# {endpoint.Description}");
        sb.AppendLine($"response = requests.{endpoint.Method.ToLower()}('{endpoint.Path}')");
        sb.AppendLine("data = response.json()");
        return sb.ToString();
    }

    private string GenerateJavaScriptSnippet(ApiEndpoint endpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// {endpoint.Description}");
        sb.AppendLine($"fetch('{endpoint.Path}', {{");
        sb.AppendLine($"  method: '{endpoint.Method.ToUpper()}'");
        sb.AppendLine("})");
        sb.AppendLine("  .then(response => response.json())");
        sb.AppendLine("  .then(data => console.log(data));");
        return sb.ToString();
    }

    private string GenerateCurlSnippet(ApiEndpoint endpoint)
    {
        return $"curl -X {endpoint.Method.ToUpper()} \"{endpoint.Path}\"";
    }

    // ===== Schema Designer =====

    public async Task<IEnumerable<SchemaDefinition>> GetSchemasAsync(CancellationToken ct = default)
    {
        var response = await _instanceManager.ExecuteAsync(
            "schema.list",
            null,
            ct);

        if (response?.Data != null && response.Data.ContainsKey("schemas"))
        {
            var json = JsonConvert.SerializeObject(response.Data["schemas"]);
            return JsonConvert.DeserializeObject<List<SchemaDefinition>>(json) ?? new List<SchemaDefinition>();
        }

        return new List<SchemaDefinition>();
    }

    public async Task<SchemaDefinition> GetSchemaAsync(string name, CancellationToken ct = default)
    {
        var response = await _instanceManager.ExecuteAsync(
            "schema.get",
            new Dictionary<string, object> { ["name"] = name },
            ct);

        if (response?.Data != null && response.Data.ContainsKey("schema"))
        {
            var json = JsonConvert.SerializeObject(response.Data["schema"]);
            var schema = JsonConvert.DeserializeObject<SchemaDefinition>(json);
            if (schema != null)
                return schema;
        }

        throw new KeyNotFoundException($"Schema '{name}' not found");
    }

    public async Task<SchemaDefinition> CreateSchemaAsync(
        SchemaDefinition schema,
        CancellationToken ct = default)
    {
        schema.CreatedAt = DateTime.UtcNow;
        schema.UpdatedAt = DateTime.UtcNow;

        var response = await _instanceManager.ExecuteAsync(
            "schema.create",
            new Dictionary<string, object> { ["schema"] = schema },
            ct);

        if (response?.Data != null && response.Data.ContainsKey("schema"))
        {
            var json = JsonConvert.SerializeObject(response.Data["schema"]);
            var createdSchema = JsonConvert.DeserializeObject<SchemaDefinition>(json);
            if (createdSchema != null)
                return createdSchema;
        }

        return schema;
    }

    public async Task<SchemaDefinition> UpdateSchemaAsync(
        string name,
        SchemaDefinition schema,
        CancellationToken ct = default)
    {
        schema.UpdatedAt = DateTime.UtcNow;

        var response = await _instanceManager.ExecuteAsync(
            "schema.update",
            new Dictionary<string, object>
            {
                ["name"] = name,
                ["schema"] = schema
            },
            ct);

        if (response?.Data != null && response.Data.ContainsKey("schema"))
        {
            var json = JsonConvert.SerializeObject(response.Data["schema"]);
            var updatedSchema = JsonConvert.DeserializeObject<SchemaDefinition>(json);
            if (updatedSchema != null)
                return updatedSchema;
        }

        return schema;
    }

    public async Task DeleteSchemaAsync(string name, CancellationToken ct = default)
    {
        await _instanceManager.ExecuteAsync(
            "schema.delete",
            new Dictionary<string, object> { ["name"] = name },
            ct);
    }

    public async Task<string> ExportSchemaAsync(
        string name,
        string format,
        CancellationToken ct = default)
    {
        var schema = await GetSchemaAsync(name, ct);

        return format.ToLower() switch
        {
            "json" => JsonConvert.SerializeObject(schema, Formatting.Indented),
            "yaml" => ConvertSchemaToYaml(schema),
            "sql" => ConvertSchemaToSql(schema),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };
    }

    public async Task<SchemaDefinition> ImportSchemaAsync(
        string content,
        string format,
        CancellationToken ct = default)
    {
        SchemaDefinition? schema = format.ToLower() switch
        {
            "json" => JsonConvert.DeserializeObject<SchemaDefinition>(content),
            "yaml" => ConvertYamlToSchema(content),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };

        if (schema == null)
            throw new InvalidOperationException("Failed to parse schema");

        return await CreateSchemaAsync(schema, ct);
    }

    private string ConvertSchemaToYaml(SchemaDefinition schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"name: {schema.Name}");
        sb.AppendLine($"description: {schema.Description}");
        sb.AppendLine($"version: {schema.Version}");
        sb.AppendLine("fields:");

        foreach (var field in schema.Fields)
        {
            sb.AppendLine($"  - name: {field.Name}");
            sb.AppendLine($"    type: {field.Type}");
            sb.AppendLine($"    required: {field.Required.ToString().ToLower()}");
            if (!string.IsNullOrEmpty(field.Description))
                sb.AppendLine($"    description: {field.Description}");
        }

        return sb.ToString();
    }

    private SchemaDefinition ConvertYamlToSchema(string yaml)
    {
        // Simple YAML parser for basic schema structure
        // In production, use a proper YAML library
        var schema = new SchemaDefinition();
        var lines = yaml.Split('\n');
        SchemaField? currentField = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:"))
                schema.Name = trimmed.Substring(5).Trim();
            else if (trimmed.StartsWith("description:"))
                schema.Description = trimmed.Substring(12).Trim();
            else if (trimmed.StartsWith("version:"))
                schema.Version = trimmed.Substring(8).Trim();
            else if (trimmed.StartsWith("- name:"))
            {
                currentField = new SchemaField { Name = trimmed.Substring(7).Trim() };
                schema.Fields.Add(currentField);
            }
            else if (currentField != null)
            {
                if (trimmed.StartsWith("type:"))
                    currentField.Type = trimmed.Substring(5).Trim();
                else if (trimmed.StartsWith("required:"))
                    currentField.Required = bool.Parse(trimmed.Substring(9).Trim());
            }
        }

        return schema;
    }

    private string ConvertSchemaToSql(SchemaDefinition schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- Schema: {schema.Name}");
        sb.AppendLine($"-- {schema.Description}");
        sb.AppendLine();
        sb.AppendLine($"CREATE TABLE {schema.Name} (");

        var fieldLines = new List<string>();
        foreach (var field in schema.Fields)
        {
            var sqlType = ConvertTypeToSql(field.Type);
            var notNull = field.Required ? " NOT NULL" : "";
            fieldLines.Add($"  {field.Name} {sqlType}{notNull}");
        }

        sb.AppendLine(string.Join(",\n", fieldLines));
        sb.AppendLine(");");

        // Add indexes
        foreach (var index in schema.Indexes)
        {
            var unique = index.Unique ? "UNIQUE " : "";
            sb.AppendLine($"CREATE {unique}INDEX {index.Name} ON {schema.Name} ({string.Join(", ", index.Fields)});");
        }

        return sb.ToString();
    }

    private string ConvertTypeToSql(string type)
    {
        return type.ToLower() switch
        {
            "string" => "VARCHAR(255)",
            "int" => "INTEGER",
            "long" => "BIGINT",
            "bool" => "BOOLEAN",
            "datetime" => "TIMESTAMP",
            "decimal" => "DECIMAL(18, 2)",
            "guid" => "UUID",
            _ => "TEXT"
        };
    }

    // ===== Query Builder =====

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        var response = await _instanceManager.ExecuteAsync(
            "collections.list",
            null,
            ct);

        if (response?.Data != null && response.Data.ContainsKey("collections"))
        {
            var json = JsonConvert.SerializeObject(response.Data["collections"]);
            return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
        }

        return new List<string>();
    }

    public async Task<IEnumerable<string>> GetFieldsAsync(string collection, CancellationToken ct = default)
    {
        var response = await _instanceManager.ExecuteAsync(
            "collections.fields",
            new Dictionary<string, object> { ["collection"] = collection },
            ct);

        if (response?.Data != null && response.Data.ContainsKey("fields"))
        {
            var json = JsonConvert.SerializeObject(response.Data["fields"]);
            return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
        }

        return new List<string>();
    }

    public async Task<QueryResult> ExecuteQueryAsync(QueryDefinition query, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            var response = await _instanceManager.ExecuteAsync(
                "query.execute",
                new Dictionary<string, object> { ["query"] = query },
                ct);

            var duration = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            if (response?.Data != null)
            {
                var rows = new List<Dictionary<string, object>>();
                if (response.Data.ContainsKey("rows"))
                {
                    var json = JsonConvert.SerializeObject(response.Data["rows"]);
                    rows = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(json)
                        ?? new List<Dictionary<string, object>>();
                }

                return new QueryResult
                {
                    Success = true,
                    Rows = rows,
                    RowCount = rows.Count,
                    DurationMs = duration,
                    Metadata = response.Data.ContainsKey("metadata")
                        ? JsonConvert.DeserializeObject<Dictionary<string, object>>(
                            JsonConvert.SerializeObject(response.Data["metadata"])) ?? new()
                        : new()
                };
            }

            return new QueryResult
            {
                Success = false,
                ErrorMessage = "No response from server",
                DurationMs = duration
            };
        }
        catch (Exception ex)
        {
            var duration = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                DurationMs = duration
            };
        }
    }

    public async Task<IEnumerable<QueryTemplate>> GetQueryTemplatesAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask; // Suppress async warning

        var templates = new List<QueryTemplate>();

        if (!Directory.Exists(_templatesDir))
            return templates;

        foreach (var file in Directory.GetFiles(_templatesDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var template = JsonConvert.DeserializeObject<QueryTemplate>(json);
                if (template != null)
                    templates.Add(template);
            }
            catch
            {
                // Skip invalid templates
            }
        }

        return templates;
    }

    public async Task<QueryTemplate> SaveQueryTemplateAsync(
        QueryTemplate template,
        CancellationToken ct = default)
    {
        await Task.CompletedTask; // Suppress async warning

        template.UpdatedAt = DateTime.UtcNow;

        var filePath = Path.Combine(_templatesDir, $"{template.Id}.json");
        var json = JsonConvert.SerializeObject(template, Formatting.Indented);
        File.WriteAllText(filePath, json);

        return template;
    }

    public async Task DeleteQueryTemplateAsync(string name, CancellationToken ct = default)
    {
        await Task.CompletedTask; // Suppress async warning

        var templates = await GetQueryTemplatesAsync(ct);
        var template = templates.FirstOrDefault(t => t.Name == name);

        if (template != null)
        {
            var filePath = Path.Combine(_templatesDir, $"{template.Id}.json");
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    public string BuildQueryPreview(QueryDefinition query)
    {
        var sb = new StringBuilder();

        switch (query.Operation)
        {
            case QueryOperation.Select:
                sb.Append("SELECT ");
                sb.Append(query.SelectFields.Any()
                    ? string.Join(", ", query.SelectFields)
                    : "*");
                sb.AppendLine();
                sb.Append($"FROM {query.Collection}");
                break;

            case QueryOperation.Insert:
                sb.Append($"INSERT INTO {query.Collection} ");
                sb.Append("(...)");
                break;

            case QueryOperation.Update:
                sb.Append($"UPDATE {query.Collection} ");
                sb.Append("SET ...");
                break;

            case QueryOperation.Delete:
                sb.Append($"DELETE FROM {query.Collection}");
                break;

            case QueryOperation.Count:
                sb.Append($"SELECT COUNT(*) FROM {query.Collection}");
                break;
        }

        // Add joins
        foreach (var join in query.Joins)
        {
            sb.AppendLine();
            sb.Append($"{join.Type.ToString().ToUpper()} JOIN {join.Collection} ");
            sb.Append($"ON {query.Collection}.{join.LocalField} = {join.Collection}.{join.ForeignField}");
        }

        // Add filters
        if (query.Filters.Any())
        {
            sb.AppendLine();
            sb.Append("WHERE ");
            var filterStrings = new List<string>();

            foreach (var filter in query.Filters)
            {
                var op = GetOperatorString(filter.Operator);
                var value = filter.Value is string ? $"'{filter.Value}'" : filter.Value?.ToString() ?? "NULL";
                filterStrings.Add($"{filter.Field} {op} {value}");
            }

            sb.Append(string.Join(" AND ", filterStrings));
        }

        // Add aggregation
        if (query.Aggregation != null && query.Aggregation.GroupBy.Any())
        {
            sb.AppendLine();
            sb.Append($"GROUP BY {string.Join(", ", query.Aggregation.GroupBy)}");
        }

        // Add sorting
        if (query.Sorting.Any())
        {
            sb.AppendLine();
            sb.Append("ORDER BY ");
            var sortStrings = query.Sorting.Select(s =>
                $"{s.Field} {(s.Direction == SortDirection.Ascending ? "ASC" : "DESC")}");
            sb.Append(string.Join(", ", sortStrings));
        }

        // Add limit/offset
        if (query.Limit.HasValue)
        {
            sb.AppendLine();
            sb.Append($"LIMIT {query.Limit.Value}");
        }

        if (query.Offset.HasValue)
        {
            sb.Append($" OFFSET {query.Offset.Value}");
        }

        sb.Append(";");
        return sb.ToString();
    }

    private string GetOperatorString(QueryOperator op)
    {
        return op switch
        {
            QueryOperator.Equals => "=",
            QueryOperator.NotEquals => "!=",
            QueryOperator.GreaterThan => ">",
            QueryOperator.GreaterThanOrEqual => ">=",
            QueryOperator.LessThan => "<",
            QueryOperator.LessThanOrEqual => "<=",
            QueryOperator.Like => "LIKE",
            QueryOperator.NotLike => "NOT LIKE",
            QueryOperator.In => "IN",
            QueryOperator.NotIn => "NOT IN",
            QueryOperator.IsNull => "IS NULL",
            QueryOperator.IsNotNull => "IS NOT NULL",
            QueryOperator.Between => "BETWEEN",
            QueryOperator.Contains => "CONTAINS",
            QueryOperator.StartsWith => "STARTS WITH",
            QueryOperator.EndsWith => "ENDS WITH",
            _ => "="
        };
    }
}
