namespace DataWarehouse.Plugins.JdbcBridge.Protocol;

/// <summary>
/// Provides DatabaseMetaData and ResultSetMetaData information for JDBC clients.
/// Implements JDBC metadata retrieval semantics.
/// </summary>
public sealed class MetadataProvider
{
    private readonly JdbcBridgeConfig _config;
    private readonly Func<string, CancellationToken, Task<JdbcQueryResult>>? _executeSqlAsync;

    /// <summary>
    /// Initializes a new instance of the MetadataProvider class.
    /// </summary>
    /// <param name="config">The bridge configuration.</param>
    /// <param name="executeSqlAsync">Optional SQL execution delegate for dynamic metadata.</param>
    public MetadataProvider(JdbcBridgeConfig config, Func<string, CancellationToken, Task<JdbcQueryResult>>? executeSqlAsync = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _executeSqlAsync = executeSqlAsync;
    }

    /// <summary>
    /// Gets database product information.
    /// </summary>
    /// <returns>Database metadata.</returns>
    public JdbcQueryResult GetDatabaseMetadata()
    {
        return new JdbcQueryResult
        {
            Columns = new List<JdbcColumnMetadata>
            {
                TypeConverter.CreateColumnMetadata("property_name", JdbcSqlTypes.VARCHAR, 0),
                TypeConverter.CreateColumnMetadata("property_value", JdbcSqlTypes.VARCHAR, 1)
            },
            Rows = new List<object?[]>
            {
                new object?[] { "databaseProductName", "DataWarehouse" },
                new object?[] { "databaseProductVersion", _config.ServerVersion },
                new object?[] { "databaseMajorVersion", 1 },
                new object?[] { "databaseMinorVersion", 0 },
                new object?[] { "driverName", "DataWarehouse JDBC Bridge" },
                new object?[] { "driverVersion", _config.ServerVersion },
                new object?[] { "driverMajorVersion", 1 },
                new object?[] { "driverMinorVersion", 0 },
                new object?[] { "jdbcMajorVersion", 4 },
                new object?[] { "jdbcMinorVersion", 2 },
                new object?[] { "supportsBatchUpdates", "true" },
                new object?[] { "supportsTransactions", "true" },
                new object?[] { "supportsSavepoints", "true" },
                new object?[] { "supportsResultSetType_FORWARD_ONLY", "true" },
                new object?[] { "supportsResultSetType_SCROLL_INSENSITIVE", "true" },
                new object?[] { "supportsResultSetType_SCROLL_SENSITIVE", "false" },
                new object?[] { "supportsResultSetConcurrency_READ_ONLY", "true" },
                new object?[] { "supportsResultSetConcurrency_UPDATABLE", "false" },
                new object?[] { "supportsResultSetHoldability_HOLD_CURSORS_OVER_COMMIT", "true" },
                new object?[] { "supportsResultSetHoldability_CLOSE_CURSORS_AT_COMMIT", "true" },
                new object?[] { "defaultTransactionIsolation", "2" }, // READ_COMMITTED
                new object?[] { "maxConnections", _config.MaxConnections.ToString() },
                new object?[] { "maxStatementLength", "16777216" }, // 16MB
                new object?[] { "maxColumnsInSelect", "1000" },
                new object?[] { "maxColumnsInTable", "1000" },
                new object?[] { "maxColumnsInIndex", "16" },
                new object?[] { "maxColumnsInGroupBy", "0" }, // unlimited
                new object?[] { "maxColumnsInOrderBy", "0" }, // unlimited
                new object?[] { "maxRowSize", "0" }, // unlimited
                new object?[] { "maxTableNameLength", "128" },
                new object?[] { "maxColumnNameLength", "128" },
                new object?[] { "maxCursorNameLength", "128" },
                new object?[] { "identifierQuoteString", "\"" },
                new object?[] { "catalogSeparator", "." },
                new object?[] { "catalogTerm", "catalog" },
                new object?[] { "schemaTerm", "schema" },
                new object?[] { "procedureTerm", "procedure" },
                new object?[] { "searchStringEscape", "\\" },
                new object?[] { "extraNameCharacters", "" },
                new object?[] { "sqlKeywords", "ABORT,ACCESS,ADD,ADMIN,AFTER,AGGREGATE,ALL,ALSO,ALTER" },
                new object?[] { "numericFunctions", "ABS,ACOS,ASIN,ATAN,ATAN2,CEILING,COS,COT,DEGREES,EXP,FLOOR,LOG,LOG10,MOD,PI,POWER,RADIANS,RAND,ROUND,SIGN,SIN,SQRT,TAN,TRUNCATE" },
                new object?[] { "stringFunctions", "ASCII,CHAR,CONCAT,INSERT,LCASE,LEFT,LENGTH,LOCATE,LTRIM,REPEAT,REPLACE,RIGHT,RTRIM,SPACE,SUBSTRING,UCASE" },
                new object?[] { "systemFunctions", "DATABASE,IFNULL,USER" },
                new object?[] { "timeDateFunctions", "CURDATE,CURTIME,DAYNAME,DAYOFMONTH,DAYOFWEEK,DAYOFYEAR,HOUR,MINUTE,MONTH,MONTHNAME,NOW,QUARTER,SECOND,TIMESTAMPADD,TIMESTAMPDIFF,WEEK,YEAR" },
                new object?[] { "URL", $"jdbc:datawarehouse://localhost:{_config.Port}" }
            }
        };
    }

    /// <summary>
    /// Gets available catalogs (databases).
    /// </summary>
    /// <returns>Catalog list.</returns>
    public JdbcQueryResult GetCatalogs()
    {
        return new JdbcQueryResult
        {
            Columns = new List<JdbcColumnMetadata>
            {
                TypeConverter.CreateColumnMetadata("TABLE_CAT", JdbcSqlTypes.VARCHAR, 0)
            },
            Rows = new List<object?[]>
            {
                new object?[] { _config.DefaultDatabase }
            }
        };
    }

    /// <summary>
    /// Gets available schemas.
    /// </summary>
    /// <param name="catalog">Optional catalog filter.</param>
    /// <param name="schemaPattern">Optional schema pattern filter.</param>
    /// <returns>Schema list.</returns>
    public JdbcQueryResult GetSchemas(string? catalog = null, string? schemaPattern = null)
    {
        return new JdbcQueryResult
        {
            Columns = new List<JdbcColumnMetadata>
            {
                TypeConverter.CreateColumnMetadata("TABLE_SCHEM", JdbcSqlTypes.VARCHAR, 0),
                TypeConverter.CreateColumnMetadata("TABLE_CATALOG", JdbcSqlTypes.VARCHAR, 1)
            },
            Rows = new List<object?[]>
            {
                new object?[] { "public", _config.DefaultDatabase },
                new object?[] { "information_schema", _config.DefaultDatabase }
            }
        };
    }

    /// <summary>
    /// Gets available table types.
    /// </summary>
    /// <returns>Table type list.</returns>
    public JdbcQueryResult GetTableTypes()
    {
        return new JdbcQueryResult
        {
            Columns = new List<JdbcColumnMetadata>
            {
                TypeConverter.CreateColumnMetadata("TABLE_TYPE", JdbcSqlTypes.VARCHAR, 0)
            },
            Rows = new List<object?[]>
            {
                new object?[] { "TABLE" },
                new object?[] { "VIEW" },
                new object?[] { "SYSTEM TABLE" },
                new object?[] { "SYSTEM VIEW" }
            }
        };
    }

    /// <summary>
    /// Gets tables matching the specified criteria.
    /// </summary>
    /// <param name="catalog">Catalog name pattern.</param>
    /// <param name="schemaPattern">Schema name pattern.</param>
    /// <param name="tableNamePattern">Table name pattern.</param>
    /// <param name="types">Table types to include.</param>
    /// <returns>Table list.</returns>
    public JdbcQueryResult GetTables(string? catalog, string? schemaPattern, string? tableNamePattern, string[]? types)
    {
        var columns = new List<JdbcColumnMetadata>
        {
            TypeConverter.CreateColumnMetadata("TABLE_CAT", JdbcSqlTypes.VARCHAR, 0),
            TypeConverter.CreateColumnMetadata("TABLE_SCHEM", JdbcSqlTypes.VARCHAR, 1),
            TypeConverter.CreateColumnMetadata("TABLE_NAME", JdbcSqlTypes.VARCHAR, 2),
            TypeConverter.CreateColumnMetadata("TABLE_TYPE", JdbcSqlTypes.VARCHAR, 3),
            TypeConverter.CreateColumnMetadata("REMARKS", JdbcSqlTypes.VARCHAR, 4),
            TypeConverter.CreateColumnMetadata("TYPE_CAT", JdbcSqlTypes.VARCHAR, 5),
            TypeConverter.CreateColumnMetadata("TYPE_SCHEM", JdbcSqlTypes.VARCHAR, 6),
            TypeConverter.CreateColumnMetadata("TYPE_NAME", JdbcSqlTypes.VARCHAR, 7),
            TypeConverter.CreateColumnMetadata("SELF_REFERENCING_COL_NAME", JdbcSqlTypes.VARCHAR, 8),
            TypeConverter.CreateColumnMetadata("REF_GENERATION", JdbcSqlTypes.VARCHAR, 9)
        };

        // Return basic system tables
        var rows = new List<object?[]>
        {
            new object?[] { _config.DefaultDatabase, "public", "manifests", "TABLE", "Data manifests", null, null, null, null, null },
            new object?[] { _config.DefaultDatabase, "public", "blobs", "TABLE", "Binary large objects", null, null, null, null, null },
            new object?[] { _config.DefaultDatabase, "information_schema", "tables", "SYSTEM VIEW", "System tables view", null, null, null, null, null },
            new object?[] { _config.DefaultDatabase, "information_schema", "columns", "SYSTEM VIEW", "System columns view", null, null, null, null, null }
        };

        // Filter by table name pattern
        if (!string.IsNullOrEmpty(tableNamePattern) && tableNamePattern != "%")
        {
            var pattern = tableNamePattern.Replace("%", ".*").Replace("_", ".");
            rows = rows.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r[2]?.ToString() ?? "", pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        }

        // Filter by types
        if (types != null && types.Length > 0)
        {
            var typeSet = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
            rows = rows.Where(r => typeSet.Contains(r[3]?.ToString() ?? "")).ToList();
        }

        return new JdbcQueryResult
        {
            Columns = columns,
            Rows = rows
        };
    }

    /// <summary>
    /// Gets columns for the specified table.
    /// </summary>
    /// <param name="catalog">Catalog name pattern.</param>
    /// <param name="schemaPattern">Schema name pattern.</param>
    /// <param name="tableNamePattern">Table name pattern.</param>
    /// <param name="columnNamePattern">Column name pattern.</param>
    /// <returns>Column list.</returns>
    public JdbcQueryResult GetColumns(string? catalog, string? schemaPattern, string? tableNamePattern, string? columnNamePattern)
    {
        var columns = new List<JdbcColumnMetadata>
        {
            TypeConverter.CreateColumnMetadata("TABLE_CAT", JdbcSqlTypes.VARCHAR, 0),
            TypeConverter.CreateColumnMetadata("TABLE_SCHEM", JdbcSqlTypes.VARCHAR, 1),
            TypeConverter.CreateColumnMetadata("TABLE_NAME", JdbcSqlTypes.VARCHAR, 2),
            TypeConverter.CreateColumnMetadata("COLUMN_NAME", JdbcSqlTypes.VARCHAR, 3),
            TypeConverter.CreateColumnMetadata("DATA_TYPE", JdbcSqlTypes.INTEGER, 4),
            TypeConverter.CreateColumnMetadata("TYPE_NAME", JdbcSqlTypes.VARCHAR, 5),
            TypeConverter.CreateColumnMetadata("COLUMN_SIZE", JdbcSqlTypes.INTEGER, 6),
            TypeConverter.CreateColumnMetadata("BUFFER_LENGTH", JdbcSqlTypes.INTEGER, 7),
            TypeConverter.CreateColumnMetadata("DECIMAL_DIGITS", JdbcSqlTypes.INTEGER, 8),
            TypeConverter.CreateColumnMetadata("NUM_PREC_RADIX", JdbcSqlTypes.INTEGER, 9),
            TypeConverter.CreateColumnMetadata("NULLABLE", JdbcSqlTypes.INTEGER, 10),
            TypeConverter.CreateColumnMetadata("REMARKS", JdbcSqlTypes.VARCHAR, 11),
            TypeConverter.CreateColumnMetadata("COLUMN_DEF", JdbcSqlTypes.VARCHAR, 12),
            TypeConverter.CreateColumnMetadata("SQL_DATA_TYPE", JdbcSqlTypes.INTEGER, 13),
            TypeConverter.CreateColumnMetadata("SQL_DATETIME_SUB", JdbcSqlTypes.INTEGER, 14),
            TypeConverter.CreateColumnMetadata("CHAR_OCTET_LENGTH", JdbcSqlTypes.INTEGER, 15),
            TypeConverter.CreateColumnMetadata("ORDINAL_POSITION", JdbcSqlTypes.INTEGER, 16),
            TypeConverter.CreateColumnMetadata("IS_NULLABLE", JdbcSqlTypes.VARCHAR, 17),
            TypeConverter.CreateColumnMetadata("SCOPE_CATALOG", JdbcSqlTypes.VARCHAR, 18),
            TypeConverter.CreateColumnMetadata("SCOPE_SCHEMA", JdbcSqlTypes.VARCHAR, 19),
            TypeConverter.CreateColumnMetadata("SCOPE_TABLE", JdbcSqlTypes.VARCHAR, 20),
            TypeConverter.CreateColumnMetadata("SOURCE_DATA_TYPE", JdbcSqlTypes.SMALLINT, 21),
            TypeConverter.CreateColumnMetadata("IS_AUTOINCREMENT", JdbcSqlTypes.VARCHAR, 22),
            TypeConverter.CreateColumnMetadata("IS_GENERATEDCOLUMN", JdbcSqlTypes.VARCHAR, 23)
        };

        // Return sample columns for known tables
        var rows = new List<object?[]>();

        if (string.IsNullOrEmpty(tableNamePattern) || tableNamePattern == "%" || tableNamePattern.Equals("manifests", StringComparison.OrdinalIgnoreCase))
        {
            rows.AddRange(new[]
            {
                CreateColumnRow(_config.DefaultDatabase, "public", "manifests", "id", JdbcSqlTypes.VARCHAR, 1),
                CreateColumnRow(_config.DefaultDatabase, "public", "manifests", "content", JdbcSqlTypes.LONGVARCHAR, 2),
                CreateColumnRow(_config.DefaultDatabase, "public", "manifests", "created_at", JdbcSqlTypes.TIMESTAMP, 3),
                CreateColumnRow(_config.DefaultDatabase, "public", "manifests", "updated_at", JdbcSqlTypes.TIMESTAMP, 4)
            });
        }

        if (string.IsNullOrEmpty(tableNamePattern) || tableNamePattern == "%" || tableNamePattern.Equals("blobs", StringComparison.OrdinalIgnoreCase))
        {
            rows.AddRange(new[]
            {
                CreateColumnRow(_config.DefaultDatabase, "public", "blobs", "id", JdbcSqlTypes.VARCHAR, 1),
                CreateColumnRow(_config.DefaultDatabase, "public", "blobs", "data", JdbcSqlTypes.LONGVARBINARY, 2),
                CreateColumnRow(_config.DefaultDatabase, "public", "blobs", "size", JdbcSqlTypes.BIGINT, 3),
                CreateColumnRow(_config.DefaultDatabase, "public", "blobs", "content_type", JdbcSqlTypes.VARCHAR, 4),
                CreateColumnRow(_config.DefaultDatabase, "public", "blobs", "created_at", JdbcSqlTypes.TIMESTAMP, 5)
            });
        }

        return new JdbcQueryResult
        {
            Columns = columns,
            Rows = rows
        };
    }

    /// <summary>
    /// Gets primary keys for the specified table.
    /// </summary>
    /// <param name="catalog">Catalog name.</param>
    /// <param name="schema">Schema name.</param>
    /// <param name="table">Table name.</param>
    /// <returns>Primary key list.</returns>
    public JdbcQueryResult GetPrimaryKeys(string? catalog, string? schema, string table)
    {
        var columns = new List<JdbcColumnMetadata>
        {
            TypeConverter.CreateColumnMetadata("TABLE_CAT", JdbcSqlTypes.VARCHAR, 0),
            TypeConverter.CreateColumnMetadata("TABLE_SCHEM", JdbcSqlTypes.VARCHAR, 1),
            TypeConverter.CreateColumnMetadata("TABLE_NAME", JdbcSqlTypes.VARCHAR, 2),
            TypeConverter.CreateColumnMetadata("COLUMN_NAME", JdbcSqlTypes.VARCHAR, 3),
            TypeConverter.CreateColumnMetadata("KEY_SEQ", JdbcSqlTypes.SMALLINT, 4),
            TypeConverter.CreateColumnMetadata("PK_NAME", JdbcSqlTypes.VARCHAR, 5)
        };

        var rows = new List<object?[]>();

        // Return id as primary key for known tables
        if (table.Equals("manifests", StringComparison.OrdinalIgnoreCase) || table.Equals("blobs", StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new object?[]
            {
                _config.DefaultDatabase,
                schema ?? "public",
                table,
                "id",
                (short)1,
                $"pk_{table}"
            });
        }

        return new JdbcQueryResult
        {
            Columns = columns,
            Rows = rows
        };
    }

    /// <summary>
    /// Gets index information for the specified table.
    /// </summary>
    /// <param name="catalog">Catalog name.</param>
    /// <param name="schema">Schema name.</param>
    /// <param name="table">Table name.</param>
    /// <param name="unique">If true, return only unique indexes.</param>
    /// <param name="approximate">If true, allow approximate values.</param>
    /// <returns>Index information.</returns>
    public JdbcQueryResult GetIndexInfo(string? catalog, string? schema, string table, bool unique, bool approximate)
    {
        var columns = new List<JdbcColumnMetadata>
        {
            TypeConverter.CreateColumnMetadata("TABLE_CAT", JdbcSqlTypes.VARCHAR, 0),
            TypeConverter.CreateColumnMetadata("TABLE_SCHEM", JdbcSqlTypes.VARCHAR, 1),
            TypeConverter.CreateColumnMetadata("TABLE_NAME", JdbcSqlTypes.VARCHAR, 2),
            TypeConverter.CreateColumnMetadata("NON_UNIQUE", JdbcSqlTypes.BOOLEAN, 3),
            TypeConverter.CreateColumnMetadata("INDEX_QUALIFIER", JdbcSqlTypes.VARCHAR, 4),
            TypeConverter.CreateColumnMetadata("INDEX_NAME", JdbcSqlTypes.VARCHAR, 5),
            TypeConverter.CreateColumnMetadata("TYPE", JdbcSqlTypes.SMALLINT, 6),
            TypeConverter.CreateColumnMetadata("ORDINAL_POSITION", JdbcSqlTypes.SMALLINT, 7),
            TypeConverter.CreateColumnMetadata("COLUMN_NAME", JdbcSqlTypes.VARCHAR, 8),
            TypeConverter.CreateColumnMetadata("ASC_OR_DESC", JdbcSqlTypes.VARCHAR, 9),
            TypeConverter.CreateColumnMetadata("CARDINALITY", JdbcSqlTypes.BIGINT, 10),
            TypeConverter.CreateColumnMetadata("PAGES", JdbcSqlTypes.BIGINT, 11),
            TypeConverter.CreateColumnMetadata("FILTER_CONDITION", JdbcSqlTypes.VARCHAR, 12)
        };

        var rows = new List<object?[]>();

        // Return primary key index for known tables
        if (table.Equals("manifests", StringComparison.OrdinalIgnoreCase) || table.Equals("blobs", StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new object?[]
            {
                _config.DefaultDatabase,
                schema ?? "public",
                table,
                false, // non_unique
                null, // index_qualifier
                $"pk_{table}", // index_name
                (short)3, // type (tableIndexOther)
                (short)1, // ordinal_position
                "id", // column_name
                "A", // asc_or_desc
                0L, // cardinality
                0L, // pages
                null // filter_condition
            });
        }

        return new JdbcQueryResult
        {
            Columns = columns,
            Rows = rows
        };
    }

    /// <summary>
    /// Gets type information for the database.
    /// </summary>
    /// <returns>Type information.</returns>
    public JdbcQueryResult GetTypeInfo()
    {
        var columns = new List<JdbcColumnMetadata>
        {
            TypeConverter.CreateColumnMetadata("TYPE_NAME", JdbcSqlTypes.VARCHAR, 0),
            TypeConverter.CreateColumnMetadata("DATA_TYPE", JdbcSqlTypes.INTEGER, 1),
            TypeConverter.CreateColumnMetadata("PRECISION", JdbcSqlTypes.INTEGER, 2),
            TypeConverter.CreateColumnMetadata("LITERAL_PREFIX", JdbcSqlTypes.VARCHAR, 3),
            TypeConverter.CreateColumnMetadata("LITERAL_SUFFIX", JdbcSqlTypes.VARCHAR, 4),
            TypeConverter.CreateColumnMetadata("CREATE_PARAMS", JdbcSqlTypes.VARCHAR, 5),
            TypeConverter.CreateColumnMetadata("NULLABLE", JdbcSqlTypes.SMALLINT, 6),
            TypeConverter.CreateColumnMetadata("CASE_SENSITIVE", JdbcSqlTypes.BOOLEAN, 7),
            TypeConverter.CreateColumnMetadata("SEARCHABLE", JdbcSqlTypes.SMALLINT, 8),
            TypeConverter.CreateColumnMetadata("UNSIGNED_ATTRIBUTE", JdbcSqlTypes.BOOLEAN, 9),
            TypeConverter.CreateColumnMetadata("FIXED_PREC_SCALE", JdbcSqlTypes.BOOLEAN, 10),
            TypeConverter.CreateColumnMetadata("AUTO_INCREMENT", JdbcSqlTypes.BOOLEAN, 11),
            TypeConverter.CreateColumnMetadata("LOCAL_TYPE_NAME", JdbcSqlTypes.VARCHAR, 12),
            TypeConverter.CreateColumnMetadata("MINIMUM_SCALE", JdbcSqlTypes.SMALLINT, 13),
            TypeConverter.CreateColumnMetadata("MAXIMUM_SCALE", JdbcSqlTypes.SMALLINT, 14),
            TypeConverter.CreateColumnMetadata("SQL_DATA_TYPE", JdbcSqlTypes.INTEGER, 15),
            TypeConverter.CreateColumnMetadata("SQL_DATETIME_SUB", JdbcSqlTypes.INTEGER, 16),
            TypeConverter.CreateColumnMetadata("NUM_PREC_RADIX", JdbcSqlTypes.INTEGER, 17)
        };

        var rows = new List<object?[]>
        {
            CreateTypeRow("VARCHAR", JdbcSqlTypes.VARCHAR, int.MaxValue, "'", "'", "length"),
            CreateTypeRow("CHAR", JdbcSqlTypes.CHAR, 255, "'", "'", "length"),
            CreateTypeRow("LONGVARCHAR", JdbcSqlTypes.LONGVARCHAR, int.MaxValue, "'", "'", null),
            CreateTypeRow("INTEGER", JdbcSqlTypes.INTEGER, 10, null, null, null),
            CreateTypeRow("BIGINT", JdbcSqlTypes.BIGINT, 19, null, null, null),
            CreateTypeRow("SMALLINT", JdbcSqlTypes.SMALLINT, 5, null, null, null),
            CreateTypeRow("TINYINT", JdbcSqlTypes.TINYINT, 3, null, null, null),
            CreateTypeRow("DOUBLE", JdbcSqlTypes.DOUBLE, 15, null, null, null),
            CreateTypeRow("REAL", JdbcSqlTypes.REAL, 7, null, null, null),
            CreateTypeRow("DECIMAL", JdbcSqlTypes.DECIMAL, 38, null, null, "precision,scale"),
            CreateTypeRow("NUMERIC", JdbcSqlTypes.NUMERIC, 38, null, null, "precision,scale"),
            CreateTypeRow("BOOLEAN", JdbcSqlTypes.BOOLEAN, 1, null, null, null),
            CreateTypeRow("DATE", JdbcSqlTypes.DATE, 10, "'", "'", null),
            CreateTypeRow("TIME", JdbcSqlTypes.TIME, 8, "'", "'", null),
            CreateTypeRow("TIMESTAMP", JdbcSqlTypes.TIMESTAMP, 29, "'", "'", null),
            CreateTypeRow("BINARY", JdbcSqlTypes.BINARY, int.MaxValue, "X'", "'", "length"),
            CreateTypeRow("VARBINARY", JdbcSqlTypes.VARBINARY, int.MaxValue, "X'", "'", "length"),
            CreateTypeRow("BLOB", JdbcSqlTypes.BLOB, int.MaxValue, null, null, null),
            CreateTypeRow("CLOB", JdbcSqlTypes.CLOB, int.MaxValue, null, null, null)
        };

        return new JdbcQueryResult
        {
            Columns = columns,
            Rows = rows
        };
    }

    /// <summary>
    /// Gets procedure information (stored procedures).
    /// </summary>
    /// <param name="catalog">Catalog name pattern.</param>
    /// <param name="schemaPattern">Schema name pattern.</param>
    /// <param name="procedureNamePattern">Procedure name pattern.</param>
    /// <returns>Procedure list.</returns>
    public JdbcQueryResult GetProcedures(string? catalog, string? schemaPattern, string? procedureNamePattern)
    {
        var columns = new List<JdbcColumnMetadata>
        {
            TypeConverter.CreateColumnMetadata("PROCEDURE_CAT", JdbcSqlTypes.VARCHAR, 0),
            TypeConverter.CreateColumnMetadata("PROCEDURE_SCHEM", JdbcSqlTypes.VARCHAR, 1),
            TypeConverter.CreateColumnMetadata("PROCEDURE_NAME", JdbcSqlTypes.VARCHAR, 2),
            TypeConverter.CreateColumnMetadata("reserved1", JdbcSqlTypes.VARCHAR, 3),
            TypeConverter.CreateColumnMetadata("reserved2", JdbcSqlTypes.VARCHAR, 4),
            TypeConverter.CreateColumnMetadata("reserved3", JdbcSqlTypes.VARCHAR, 5),
            TypeConverter.CreateColumnMetadata("REMARKS", JdbcSqlTypes.VARCHAR, 6),
            TypeConverter.CreateColumnMetadata("PROCEDURE_TYPE", JdbcSqlTypes.SMALLINT, 7),
            TypeConverter.CreateColumnMetadata("SPECIFIC_NAME", JdbcSqlTypes.VARCHAR, 8)
        };

        // No stored procedures by default
        return new JdbcQueryResult
        {
            Columns = columns,
            Rows = new List<object?[]>()
        };
    }

    /// <summary>
    /// Creates a column row for getColumns result.
    /// </summary>
    private static object?[] CreateColumnRow(string catalog, string schema, string table, string column, int sqlType, int ordinal)
    {
        var precision = TypeConverter.GetPrecision(sqlType);
        var scale = TypeConverter.GetScale(sqlType);

        return new object?[]
        {
            catalog, // TABLE_CAT
            schema, // TABLE_SCHEM
            table, // TABLE_NAME
            column, // COLUMN_NAME
            sqlType, // DATA_TYPE
            TypeConverter.GetTypeName(sqlType), // TYPE_NAME
            precision, // COLUMN_SIZE
            0, // BUFFER_LENGTH
            scale, // DECIMAL_DIGITS
            10, // NUM_PREC_RADIX
            1, // NULLABLE (columnNullable)
            null, // REMARKS
            null, // COLUMN_DEF
            0, // SQL_DATA_TYPE
            0, // SQL_DATETIME_SUB
            precision, // CHAR_OCTET_LENGTH
            ordinal, // ORDINAL_POSITION
            "YES", // IS_NULLABLE
            null, // SCOPE_CATALOG
            null, // SCOPE_SCHEMA
            null, // SCOPE_TABLE
            null, // SOURCE_DATA_TYPE
            "NO", // IS_AUTOINCREMENT
            "NO" // IS_GENERATEDCOLUMN
        };
    }

    /// <summary>
    /// Creates a type row for getTypeInfo result.
    /// </summary>
    private static object?[] CreateTypeRow(string typeName, int dataType, int precision, string? prefix, string? suffix, string? createParams)
    {
        return new object?[]
        {
            typeName, // TYPE_NAME
            dataType, // DATA_TYPE
            precision, // PRECISION
            prefix, // LITERAL_PREFIX
            suffix, // LITERAL_SUFFIX
            createParams, // CREATE_PARAMS
            (short)1, // NULLABLE (typeNullable)
            true, // CASE_SENSITIVE
            (short)3, // SEARCHABLE (typeSearchable)
            false, // UNSIGNED_ATTRIBUTE
            false, // FIXED_PREC_SCALE
            false, // AUTO_INCREMENT
            typeName, // LOCAL_TYPE_NAME
            (short)0, // MINIMUM_SCALE
            (short)0, // MAXIMUM_SCALE
            0, // SQL_DATA_TYPE
            0, // SQL_DATETIME_SUB
            10 // NUM_PREC_RADIX
        };
    }
}
