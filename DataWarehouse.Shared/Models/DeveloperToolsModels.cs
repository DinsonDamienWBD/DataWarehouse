namespace DataWarehouse.Shared.Models;

// API Explorer Models

/// <summary>
/// Represents an API endpoint in the system
/// </summary>
public class ApiEndpoint
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string Description { get; set; } = string.Empty;
    public List<ApiParameter> Parameters { get; set; } = new();
    public ApiResponseSchema ResponseSchema { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public bool RequiresAuth { get; set; }
}

/// <summary>
/// API parameter definition
/// </summary>
public class ApiParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string Description { get; set; } = string.Empty;
    public object? DefaultValue { get; set; }
    public string Location { get; set; } = "query"; // query, path, header, body
}

/// <summary>
/// API response schema
/// </summary>
public class ApiResponseSchema
{
    public string Type { get; set; } = "object";
    public Dictionary<string, string> Properties { get; set; } = new();
    public string Example { get; set; } = string.Empty;
}

/// <summary>
/// Request to execute an API call
/// </summary>
public class ApiRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public Dictionary<string, object> Parameters { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();
    public object? Body { get; set; }
}

/// <summary>
/// Response from an API call
/// </summary>
public class ApiResponse
{
    public int StatusCode { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public object? Body { get; set; }
    public long DurationMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

// Schema Designer Models

/// <summary>
/// Schema definition for data structures
/// </summary>
public class SchemaDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public List<SchemaField> Fields { get; set; } = new();
    public List<SchemaIndex> Indexes { get; set; } = new();
    public List<SchemaConstraint> Constraints { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Field definition in a schema
/// </summary>
public class SchemaField
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public bool Nullable { get; set; }
    public object? DefaultValue { get; set; }
    public string Description { get; set; } = string.Empty;
    public SchemaValidation? Validation { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Field validation rules
/// </summary>
public class SchemaValidation
{
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public object? MinValue { get; set; }
    public object? MaxValue { get; set; }
    public string? Pattern { get; set; }
    public List<object>? AllowedValues { get; set; }
    public string? CustomValidator { get; set; }
}

/// <summary>
/// Index definition for a schema
/// </summary>
public class SchemaIndex
{
    public string Name { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = new();
    public bool Unique { get; set; }
    public string Type { get; set; } = "btree"; // btree, hash, fulltext, etc.
}

/// <summary>
/// Constraint definition for a schema
/// </summary>
public class SchemaConstraint
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "check"; // check, foreign_key, unique, etc.
    public string Expression { get; set; } = string.Empty;
    public Dictionary<string, object> Options { get; set; } = new();
}

// Query Builder Models

/// <summary>
/// Query definition for the query builder
/// </summary>
public class QueryDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public QueryOperation Operation { get; set; } = QueryOperation.Select;
    public List<string> SelectFields { get; set; } = new();
    public List<QueryFilter> Filters { get; set; } = new();
    public List<QuerySort> Sorting { get; set; } = new();
    public List<QueryJoin> Joins { get; set; } = new();
    public QueryAggregation? Aggregation { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }
    public Dictionary<string, object> Options { get; set; } = new();
}

/// <summary>
/// Query operation type
/// </summary>
public enum QueryOperation
{
    Select,
    Insert,
    Update,
    Delete,
    Count,
    Aggregate
}

/// <summary>
/// Query filter condition
/// </summary>
public class QueryFilter
{
    public string Field { get; set; } = string.Empty;
    public QueryOperator Operator { get; set; } = QueryOperator.Equals;
    public object? Value { get; set; }
    public QueryLogic Logic { get; set; } = QueryLogic.And;
}

/// <summary>
/// Query operator type
/// </summary>
public enum QueryOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Like,
    NotLike,
    In,
    NotIn,
    IsNull,
    IsNotNull,
    Between,
    Contains,
    StartsWith,
    EndsWith
}

/// <summary>
/// Logic operator for combining filters
/// </summary>
public enum QueryLogic
{
    And,
    Or,
    Not
}

/// <summary>
/// Sort definition for a query
/// </summary>
public class QuerySort
{
    public string Field { get; set; } = string.Empty;
    public SortDirection Direction { get; set; } = SortDirection.Ascending;
}

/// <summary>
/// Sort direction
/// </summary>
public enum SortDirection
{
    Ascending,
    Descending
}

/// <summary>
/// Join definition for a query
/// </summary>
public class QueryJoin
{
    public string Collection { get; set; } = string.Empty;
    public JoinType Type { get; set; } = JoinType.Inner;
    public string LocalField { get; set; } = string.Empty;
    public string ForeignField { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
}

/// <summary>
/// Join type
/// </summary>
public enum JoinType
{
    Inner,
    Left,
    Right,
    Full,
    Cross
}

/// <summary>
/// Aggregation definition for a query
/// </summary>
public class QueryAggregation
{
    public List<string> GroupBy { get; set; } = new();
    public List<AggregateFunction> Functions { get; set; } = new();
    public List<QueryFilter> Having { get; set; } = new();
}

/// <summary>
/// Aggregate function definition
/// </summary>
public class AggregateFunction
{
    public string Name { get; set; } = string.Empty;
    public AggregateFunctionType Type { get; set; } = AggregateFunctionType.Count;
    public string Field { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
}

/// <summary>
/// Aggregate function type
/// </summary>
public enum AggregateFunctionType
{
    Count,
    Sum,
    Avg,
    Min,
    Max,
    StdDev,
    Variance,
    First,
    Last
}

/// <summary>
/// Result of a query execution
/// </summary>
public class QueryResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<Dictionary<string, object>> Rows { get; set; } = new();
    public int RowCount { get; set; }
    public long DurationMs { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Saved query template
/// </summary>
public class QueryTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public QueryDefinition Query { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsFavorite { get; set; }
}
