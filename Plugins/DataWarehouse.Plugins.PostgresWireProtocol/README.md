# DataWarehouse.Plugins.PostgresWireProtocol

PostgreSQL Wire Protocol Plugin for SQL tool compatibility.

## Overview

This plugin implements the PostgreSQL wire protocol version 3.0, allowing SQL tools like DBeaver, DataGrip, pgAdmin, and others to connect to DataWarehouse as if it were a PostgreSQL database.

## Features

- **Full PostgreSQL Wire Protocol 3.0 Support**
  - Simple query protocol (Query message)
  - Extended query protocol (Parse, Bind, Execute)
  - Connection pooling
  - SSL negotiation

- **Authentication Methods**
  - MD5 password authentication
  - SCRAM-SHA-256 (planned)
  - Trust authentication (no password)

- **SQL Support**
  - SELECT queries
  - INSERT, UPDATE, DELETE statements
  - Transaction support (BEGIN, COMMIT, ROLLBACK)
  - Prepared statements and portals

- **System Catalog Compatibility**
  - pg_catalog queries
  - information_schema queries
  - Client tool compatibility (version(), current_schema(), etc.)

## Compatible Clients

This plugin enables DataWarehouse to be queryable from:

- **GUI Tools**: DBeaver, DataGrip, pgAdmin, TablePlus
- **Command Line**: psql
- **Programming Languages**:
  - Python: psycopg2, asyncpg
  - Node.js: pg (node-postgres)
  - .NET: Npgsql
  - Java: JDBC PostgreSQL driver
  - Any ODBC PostgreSQL driver

## Configuration

```json
{
  "PostgresWireProtocol": {
    "Port": 5432,
    "MaxConnections": 100,
    "AuthMethod": "md5",
    "SslMode": "prefer",
    "ConnectionTimeoutSeconds": 30,
    "QueryTimeoutSeconds": 300
  }
}
```

### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Port` | int | 5432 | Port to listen on |
| `MaxConnections` | int | 100 | Maximum concurrent connections |
| `AuthMethod` | string | "md5" | Authentication method: "md5", "scram-sha-256", "trust" |
| `SslMode` | string | "prefer" | SSL mode: "disable", "allow", "prefer", "require" |
| `ConnectionTimeoutSeconds` | int | 30 | Connection timeout |
| `QueryTimeoutSeconds` | int | 300 | Query execution timeout |

## Usage

### Connect with psql

```bash
psql -h localhost -p 5432 -U admin -d datawarehouse
```

### Connect with DBeaver

1. Create new connection
2. Select PostgreSQL
3. Host: localhost
4. Port: 5432
5. Database: datawarehouse
6. Username: admin
7. Test connection

### Connect with Python

```python
import psycopg2

conn = psycopg2.connect(
    host="localhost",
    port=5432,
    database="datawarehouse",
    user="admin",
    password="password"
)

cur = conn.cursor()
cur.execute("SELECT * FROM manifests LIMIT 10")
rows = cur.fetchall()
```

### Connect with Node.js

```javascript
const { Client } = require('pg');

const client = new Client({
  host: 'localhost',
  port: 5432,
  database: 'datawarehouse',
  user: 'admin',
  password: 'password',
});

await client.connect();
const res = await client.query('SELECT * FROM manifests LIMIT 10');
console.log(res.rows);
```

### Connect with .NET (Npgsql)

```csharp
using Npgsql;

var connectionString = "Host=localhost;Port=5432;Database=datawarehouse;Username=admin;Password=password";
await using var dataSource = NpgsqlDataSource.Create(connectionString);

await using var cmd = dataSource.CreateCommand("SELECT * FROM manifests LIMIT 10");
await using var reader = await cmd.ExecuteReaderAsync();

while (await reader.ReadAsync())
{
    Console.WriteLine(reader.GetString(0));
}
```

## Message Commands

The plugin supports the following message commands:

- `postgres.start` - Start the protocol listener (auto-started on plugin start)
- `postgres.stop` - Stop the listener
- `postgres.status` - Get current status and connection statistics
- `postgres.connections` - List all active connections

## Architecture

### Components

1. **PostgresWireProtocolPlugin.cs** - Main plugin class
2. **PostgresWireProtocolTypes.cs** - Configuration and data structures
3. **Protocol/MessageReader.cs** - Reads wire protocol messages
4. **Protocol/MessageWriter.cs** - Writes wire protocol messages
5. **Protocol/ProtocolHandler.cs** - Handles individual connections
6. **Protocol/QueryProcessor.cs** - Processes SQL queries
7. **Protocol/TypeConverter.cs** - Converts between DataWarehouse and PostgreSQL types

### Message Flow

```
Client → Startup → Authentication → Ready
       ↓
Query Message → Parse → Execute → Result Rows → Ready
       ↓
Extended Query → Parse → Bind → Execute → Result Rows → Sync → Ready
       ↓
Terminate → Connection Closed
```

## PostgreSQL Type Mappings

| PostgreSQL Type | OID | .NET Type |
|----------------|-----|-----------|
| bool | 16 | bool |
| int2 | 21 | short |
| int4 | 23 | int |
| int8 | 20 | long |
| float4 | 700 | float |
| float8 | 701 | double |
| numeric | 1700 | decimal |
| text | 25 | string |
| varchar | 1043 | string |
| bytea | 17 | byte[] |
| timestamp | 1114 | DateTime |
| timestamptz | 1184 | DateTimeOffset |
| uuid | 2950 | Guid |
| json | 114 | string |
| jsonb | 3802 | string |

## System Catalog Support

The plugin provides basic compatibility with PostgreSQL system catalogs:

- `version()` - Returns server version
- `current_schema()` - Returns current schema
- `pg_catalog.pg_type` - Type information
- `pg_catalog.pg_database` - Database list
- `pg_catalog.pg_tables` - Table list
- `information_schema.tables` - Standard SQL schema

## Integration with DataWarehouse

The plugin integrates with DataWarehouse through a SQL executor callback:

```csharp
plugin.SetSqlExecutor(async (sql, ct) => {
    // Execute SQL against DataWarehouse
    // Return QueryResult with columns and rows
});
```

This allows the plugin to delegate actual query execution to other components like:
- FederatedQuery plugin
- SqlInterface plugin
- Direct storage access

## Limitations

- No support for COPY command
- Limited system catalog support (only essential queries)
- No support for stored procedures/functions
- No support for triggers
- Transaction isolation levels not fully implemented

## Future Enhancements

- [ ] Full SSL/TLS support
- [ ] SCRAM-SHA-256 authentication
- [ ] Extended COPY protocol support
- [ ] More comprehensive system catalog support
- [ ] Query result caching
- [ ] Connection rate limiting
- [ ] Query cancellation support

## License

Part of the DataWarehouse project.

## Plugin ID

`com.datawarehouse.protocol.postgres`

## Category

InterfaceProvider
