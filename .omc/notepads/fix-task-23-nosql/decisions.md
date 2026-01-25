# Architectural Decisions - Task 23

## Decision 1: Driver Selection Strategy

**Context**: Plugin needs to support 50+ NoSQL database types listed in the enum.

**Decision**: Implement drivers for major engine categories rather than all 50+ engines.

**Categories Implemented**:
1. **MongoDB Protocol** (MongoDB.Driver): MongoDB, DocumentDB, CosmosDB (MongoDB API)
2. **Redis Protocol** (StackExchange.Redis): Redis, Memcached
3. **HTTP REST** (HttpClient): CouchDB, Elasticsearch, OpenSearch, Solr

**Rationale**:
- These 3 categories cover ~80% of real-world NoSQL usage
- Protocol compatibility allows one driver to support multiple engines (e.g., MongoDB driver works with DocumentDB)
- Adding more drivers later is straightforward (same pattern)
- Better to have 3 well-tested implementations than 50 half-working ones

**Alternatives Considered**:
- ❌ Implement all 50+ engines: Would require 50+ NuGet packages, massive complexity
- ❌ HTTP-only for everything: Not all databases have REST APIs, and native drivers are faster
- ❌ Plugin-per-engine: Too much code duplication

## Decision 2: Keep In-Memory Mode

**Context**: Original code had in-memory ConcurrentDictionary simulation.

**Decision**: Keep in-memory mode as `NoSqlEngine.InMemory` option.

**Rationale**:
- Essential for unit testing without external dependencies
- Useful for CI/CD pipelines
- Enables local development without Docker
- Clear documentation that it's NOT for production

**Implementation**:
- Added comment: "NOTE: This is ONLY for testing/embedded mode"
- All operations check `_config.Engine == NoSqlEngine.InMemory` first
- No performance overhead when using real databases

## Decision 3: Connection String as Primary Configuration

**Context**: Different databases have different connection formats.

**Decision**: Use `ConnectionString` as primary, `Endpoint` as fallback.

**Priority**:
1. `ConnectionString` (if provided)
2. `Endpoint` (for HTTP-based databases)
3. Throw exception (if neither)

**Rationale**:
- Connection strings are standard in .NET (EF Core, ADO.NET, etc.)
- MongoDB driver requires connection strings (not just host:port)
- HTTP-based databases work better with base URLs
- Consistent with existing SDK patterns

**Examples**:
```csharp
// MongoDB: connection string required
NoSqlConfig.MongoDB("mongodb://user:pass@host:27017/db?authSource=admin")

// Redis: connection string or endpoint
NoSqlConfig.Redis("host:6379,password=secret,ssl=true")

// CouchDB: endpoint preferred
NoSqlConfig.CouchDB("http://localhost:5984", "admin", "password")
```

## Decision 4: Not Using Base Class Connection Registry

**Context**: Base class `HybridDatabasePluginBase` has connection registry for multi-instance.

**Decision**: Initialize clients directly in `InitializeClient()` instead of relying solely on `CreateConnectionAsync()`.

**Rationale**:
- Client initialization happens once in constructor
- Connection registry is for dynamic multi-instance scenarios
- Single instance is the common case
- Direct initialization is simpler and clearer

**Note**: `CreateConnectionAsync()` still returns the client for registry compatibility.

## Decision 5: Redis Key Pattern

**Context**: Redis is key-value, needs a way to organize collections.

**Decision**: Use pattern `{database}:{collection}:{id}` for Redis keys.

**Rationale**:
- Mimics hierarchical structure of document databases
- Allows scanning by database or collection using pattern matching
- Compatible with Redis namespace conventions
- Enables `ListCollections()` by parsing key prefixes

**Example**:
```
myapp:users:123 → database="myapp", collection="users", id="123"
myapp:orders:456 → database="myapp", collection="orders", id="456"
```

**Alternative Considered**:
- ❌ Hash fields: More complex, harder to query
- ❌ Flat keys: No way to list collections

## Decision 6: Error Handling Strategy

**Context**: Multiple failure modes (missing config, unsupported engine, not found).

**Decision**: Use specific exception types for different scenarios.

**Mapping**:
- **Missing Configuration**: `InvalidOperationException("ConnectionString is required")`
- **Unsupported Engine**: `NotSupportedException("Engine X not implemented")`
- **Document Not Found**: `FileNotFoundException("Document not found: {path}")`

**Rationale**:
- Consistent with .NET conventions
- `FileNotFoundException` matches storage abstraction contract
- Clear error messages help debugging
- Callers can catch specific exceptions

## Decision 7: No Transaction Support (Yet)

**Context**: MongoDB supports multi-document transactions, but implementation is complex.

**Decision**: No transaction support in this iteration.

**Rationale**:
- Focus on getting CRUD operations working first
- Transactions are engine-specific (MongoDB has them, Redis doesn't)
- Can be added later without breaking changes
- Most NoSQL use cases are single-document operations

**Future Enhancement**:
```csharp
// Potential API:
await plugin.BeginTransactionAsync();
await plugin.SaveAsync(...);
await plugin.SaveAsync(...);
await plugin.CommitTransactionAsync();
```

## Decision 8: Dispose Pattern for Client Cleanup

**Context**: Database clients need proper disposal to avoid resource leaks.

**Decision**: Override `Dispose(bool)` to clean up `_httpClient` and `_redisConnection`.

**Implementation**:
```csharp
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        _httpClient?.Dispose();
        _redisConnection?.Dispose();
    }
    base.Dispose(disposing);
}
```

**Rationale**:
- `IMongoClient` doesn't implement IDisposable (connection pooling is internal)
- `HttpClient` and `IConnectionMultiplexer` do need disposal
- Following standard .NET dispose pattern
- Base class handles other cleanup

## Decision 9: Async-First API

**Context**: All database operations are I/O-bound.

**Decision**: All operations are async (no sync alternatives).

**Rationale**:
- NoSQL operations are inherently network-based (except in-memory)
- Async improves scalability (don't block threads)
- MongoDB.Driver and StackExchange.Redis are async-first
- Consistent with modern .NET practices

## Decision 10: Engine-Specific Query Syntax

**Context**: Each NoSQL database has different query syntax (MongoDB filter, Elasticsearch DSL, etc.).

**Decision**: Accept raw query strings per engine, no abstraction layer.

**Rationale**:
- Query abstraction is extremely complex (see ORM history)
- Users of specific databases know their query syntax
- Raw queries provide full power of each engine
- Simple pass-through is maintainable

**Examples**:
```csharp
// MongoDB: JSON filter
await ExecuteQueryAsync("mydb", "users", "{\"age\": {\"$gt\": 18}}");

// Elasticsearch: Query DSL
await ExecuteQueryAsync("default", "logs", "{\"query\":{\"match\":{\"level\":\"error\"}}}");

// Redis: Returns all matching keys (no filter syntax)
await ExecuteQueryAsync("myapp", "users", null);
```

**Alternative Considered**:
- ❌ Unified query language: Would limit capabilities, huge effort
- ❌ LINQ provider: Complex, performance overhead
