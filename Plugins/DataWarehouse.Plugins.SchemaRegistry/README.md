# Schema Registry Plugin

Production-ready plugin for tracking database and table schemas with comprehensive version management, change detection, and compatibility checking.

## Features

- **Schema Registration**: Register schemas from imported databases with full column definitions
- **Version History**: Automatic tracking of schema versions over time with change detection
- **Schema Evolution**: Detect schema drift and breaking changes between versions
- **Compatibility Checking**: Analyze compatibility between different schema versions
- **Search & Discovery**: Find schemas by name, column, type, or source
- **Migration Support**: Generate diff reports for schema migrations
- **Import/Export**: Bulk import and export of schema definitions

## Message Commands

### schema.register
Register or update a schema.

**Payload**:
```json
{
  "name": "invoices",
  "source": "SqlServer:AdventureWorks",
  "database": "AdventureWorks",
  "schemaNamespace": "dbo",
  "columns": [
    {
      "name": "id",
      "dataType": "int",
      "isPrimaryKey": true,
      "isNullable": false,
      "isAutoIncrement": true,
      "ordinal": 0
    },
    {
      "name": "amount",
      "dataType": "decimal",
      "precision": 18,
      "scale": 2,
      "isNullable": false,
      "ordinal": 1
    },
    {
      "name": "date",
      "dataType": "datetime",
      "isNullable": false,
      "ordinal": 2
    }
  ],
  "primaryKeys": ["id"],
  "indexes": [
    {
      "name": "IX_invoices_date",
      "columns": ["date"],
      "isUnique": false
    }
  ]
}
```

### schema.get
Get schema definition by ID.

**Payload**:
```json
{
  "schemaId": "SqlServer:AdventureWorks.invoices",
  "includeHistory": true
}
```

### schema.list
List all schemas, optionally filtered by source or database.

**Payload**:
```json
{
  "source": "SqlServer:AdventureWorks",
  "database": "AdventureWorks"
}
```

### schema.versions
Get version history for a schema.

**Payload**:
```json
{
  "schemaId": "SqlServer:AdventureWorks.invoices"
}
```

### schema.diff
Compare two schema versions.

**Payload**:
```json
{
  "schemaId": "SqlServer:AdventureWorks.invoices",
  "fromVersion": 1,
  "toVersion": 2
}
```

### schema.search
Search schemas by criteria.

**Payload**:
```json
{
  "namePattern": "inv*",
  "columnName": "amount",
  "columnType": "decimal",
  "source": "SqlServer:AdventureWorks",
  "limit": 100
}
```

### schema.delete
Delete a schema.

**Payload**:
```json
{
  "schemaId": "SqlServer:AdventureWorks.invoices"
}
```

### schema.stats
Get registry statistics.

**Payload**: (empty)

### schema.export
Export all schemas to JSON.

**Payload**: (empty)

### schema.import
Import schemas from JSON.

**Payload**:
```json
{
  "json": "[{\"schemaId\":\"...\",\"name\":\"...\"}]"
}
```

## Schema Change Types

- `ColumnAdded`: New column added to table
- `ColumnRemoved`: Column removed from table (breaking)
- `ColumnModified`: Column definition changed
- `ColumnRenamed`: Column renamed (breaking)
- `PrimaryKeyAdded`: Primary key added
- `PrimaryKeyRemoved`: Primary key removed (breaking)
- `ForeignKeyAdded`: Foreign key constraint added
- `ForeignKeyRemoved`: Foreign key removed (breaking)
- `IndexAdded`: Index added
- `IndexRemoved`: Index removed
- `IndexModified`: Index modified
- `TableRenamed`: Table renamed (breaking)

## Storage

Schemas are persisted to:
```
%LOCALAPPDATA%\DataWarehouse\schemas\
```

Each schema is stored as a separate JSON file with its full definition, version history, and metadata.

## Usage Examples

### Register a Schema

```csharp
await messageBus.PublishAsync("schema.register", new PluginMessage
{
    Type = "schema.register",
    Payload = new Dictionary<string, object>
    {
        ["name"] = "customers",
        ["source"] = "SqlServer:CRM",
        ["columns"] = new List<ColumnDefinition>
        {
            new() { Name = "id", DataType = "int", IsPrimaryKey = true },
            new() { Name = "email", DataType = "varchar", MaxLength = 255 }
        }
    }
});
```

### Compare Schema Versions

```csharp
await messageBus.PublishAsync("schema.diff", new PluginMessage
{
    Type = "schema.diff",
    Payload = new Dictionary<string, object>
    {
        ["schemaId"] = "SqlServer:CRM.customers",
        ["fromVersion"] = 1,
        ["toVersion"] = 2
    }
});
```

### Search by Column

```csharp
await messageBus.PublishAsync("schema.search", new PluginMessage
{
    Type = "schema.search",
    Payload = new Dictionary<string, object>
    {
        ["columnName"] = "email",
        ["limit"] = 50
    }
});
```

## Architecture

- **SchemaRegistryPlugin**: Main plugin coordinating message handling
- **SchemaStorage**: Persistent storage with indexing for fast queries
- **SchemaVersionManager**: Version tracking and change detection
- **SchemaRegistryTypes**: Type definitions for schemas, columns, and metadata

## Performance

- In-memory indexes for source and column lookups
- Lazy-loaded schema definitions
- Efficient change detection using SHA-256 checksums
- Concurrent access with proper locking

## Integration

Works seamlessly with:
- Database Import Plugin (automatically registers schemas)
- Federated Query Plugin (uses schema information for queries)
- Metadata plugins (schema discovery and cataloging)
