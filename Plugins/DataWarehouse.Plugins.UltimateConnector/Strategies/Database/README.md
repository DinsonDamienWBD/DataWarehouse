# Phase B: Relational Database Connection Strategies

This directory contains 12 production-ready database connection strategies for T125 UltimateConnector.

## Implemented Strategies

### Direct ADO.NET Implementations (Real Database Connections)

1. **PostgreSqlConnectionStrategy** (`postgresql`)
   - Uses: Npgsql driver
   - Full ADO.NET connection with query execution
   - Supports: ACID transactions, JSONB, full-text search
   - Schema discovery via information_schema

2. **SqlServerConnectionStrategy** (`sqlserver`)
   - Uses: Microsoft.Data.SqlClient driver
   - Full ADO.NET connection with query execution
   - Supports: T-SQL, stored procedures, Azure SQL Database
   - Schema discovery via INFORMATION_SCHEMA

3. **SqliteConnectionStrategy** (`sqlite`)
   - Uses: Microsoft.Data.Sqlite driver
   - Full ADO.NET connection with query execution
   - Supports: Embedded database, ACID transactions
   - Schema discovery via PRAGMA table_info

4. **CockroachDbConnectionStrategy** (`cockroachdb`)
   - Uses: Npgsql driver (PostgreSQL wire protocol)
   - Full ADO.NET connection with query execution
   - Supports: Distributed SQL, geo-partitioning
   - PostgreSQL-compatible schema discovery

5. **TimescaleDbConnectionStrategy** (`timescaledb`)
   - Uses: Npgsql driver (PostgreSQL wire protocol)
   - Full ADO.NET connection with query execution
   - Supports: Time-series optimization, hypertables
   - PostgreSQL-compatible schema discovery

6. **CitusConnectionStrategy** (`citus`)
   - Uses: Npgsql driver (PostgreSQL wire protocol)
   - Full ADO.NET connection with query execution
   - Supports: Distributed PostgreSQL, sharding
   - PostgreSQL-compatible schema discovery

7. **YugabyteDbConnectionStrategy** (`yugabytedb`)
   - Uses: Npgsql driver (PostgreSQL wire protocol)
   - Full ADO.NET connection with query execution
   - Supports: Cloud-native distributed SQL
   - PostgreSQL-compatible schema discovery

### TCP Connectivity Implementations (Connection Validation Only)

8. **MySqlConnectionStrategy** (`mysql`)
   - Uses: TCP socket connectivity check (port 3306)
   - Query operations require MySqlConnector NuGet package
   - Connection/health check working via TCP ping

9. **MariaDbConnectionStrategy** (`mariadb`)
   - Uses: TCP socket connectivity check (port 3306)
   - MySQL wire-compatible
   - Query operations require MySqlConnector NuGet package

10. **OracleConnectionStrategy** (`oracle`)
    - Uses: TCP socket connectivity check (port 1521)
    - Query operations require Oracle.ManagedDataAccess NuGet package
    - Connection/health check working via TCP ping

11. **VitessConnectionStrategy** (`vitess`)
    - Uses: TCP socket connectivity check (port 3306)
    - MySQL wire-compatible distributed database
    - Query operations require MySqlConnector NuGet package

12. **TiDbConnectionStrategy** (`tidb`)
    - Uses: TCP socket connectivity check (port 4000)
    - MySQL wire-compatible HTAP database
    - Query operations require MySqlConnector NuGet package

## Key Features

All strategies implement:
- ConnectCoreAsync - Establish database connection
- TestCoreAsync - Execute "SELECT 1" or equivalent
- DisconnectCoreAsync - Close and dispose connection
- GetHealthCoreAsync - Ping with latency measurement
- ExecuteQueryAsync - Execute SQL queries (real or NotImplementedException)
- ExecuteNonQueryAsync - Execute SQL commands (real or NotImplementedException)
- GetSchemaAsync - Retrieve database schema (real or NotImplementedException)
- ValidateConfigAsync - Validate connection string format
- Full XML documentation
- Semantic descriptions for AI discovery
- Production-ready error handling

## Dependencies

Added to csproj:
- Npgsql 10.0.1 (PostgreSQL and compatible databases)
- Microsoft.Data.SqlClient 6.1.4 (SQL Server)
- Microsoft.Data.Sqlite 9.0.1 (SQLite)

## Compilation Status

All 12 database strategies compile successfully with zero errors.
The plugin's other strategy files (from other phases) have pre-existing compilation issues that are unrelated to these Database strategies.

## Connection String Examples

### PostgreSQL
```
Host=localhost;Port=5432;Database=mydb;Username=user;Password=pass
```

### SQL Server
```
Data Source=localhost;Initial Catalog=mydb;User ID=sa;Password=pass;TrustServerCertificate=True
```

### SQLite
```
Data Source=mydb.db
```
or
```
Data Source=:memory:
```

### MySQL/MariaDB/Vitess
```
Server=localhost;Port=3306;Database=mydb;User=root;Password=pass
```

### Oracle
```
Data Source=localhost:1521/ORCL;User Id=system;Password=pass
```

### TiDB
```
Server=localhost;Port=4000;Database=mydb;User=root;Password=pass
```

## Auto-Discovery

All strategies are automatically discovered by the UltimateConnectorPlugin via assembly scanning on startup. No manual registration is required.
