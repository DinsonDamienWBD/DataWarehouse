using System.Data.Common;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Provides a factory for creating instances of DataWarehouse provider classes.
/// Use this class for dependency injection and DbProviderFactories registration.
/// </summary>
/// <remarks>
/// This class is thread-safe and can be used from multiple threads concurrently.
/// Register with DbProviderFactories using:
/// <code>
/// DbProviderFactories.RegisterFactory("DataWarehouse", DataWarehouseProviderFactory.Instance);
/// </code>
/// </remarks>
public sealed class DataWarehouseProviderFactory : DbProviderFactory
{
    /// <summary>
    /// The singleton instance of the DataWarehouseProviderFactory.
    /// </summary>
    public static readonly DataWarehouseProviderFactory Instance = new();

    /// <summary>
    /// Gets whether the factory supports DbDataSourceEnumerator.
    /// </summary>
    public override bool CanCreateDataSourceEnumerator => false;

    /// <summary>
    /// Gets whether the factory supports DbCommandBuilder.
    /// </summary>
    public override bool CanCreateCommandBuilder => true;

    /// <summary>
    /// Gets whether the factory supports DbDataAdapter.
    /// </summary>
    public override bool CanCreateDataAdapter => true;

    /// <summary>
    /// Gets the provider invariant name.
    /// </summary>
    public const string ProviderInvariantName = "DataWarehouse.Plugins.AdoNetProvider";

    /// <summary>
    /// Gets the provider name for display.
    /// </summary>
    public const string ProviderName = "DataWarehouse ADO.NET Provider";

    /// <summary>
    /// Gets the provider description.
    /// </summary>
    public const string ProviderDescription = "ADO.NET Data Provider for DataWarehouse";

    /// <summary>
    /// Initializes a new instance of the DataWarehouseProviderFactory class.
    /// Use the Instance property to get the singleton.
    /// </summary>
    private DataWarehouseProviderFactory()
    {
    }

    /// <inheritdoc/>
    public override DbCommand CreateCommand()
    {
        return new DataWarehouseCommand();
    }

    /// <inheritdoc/>
    public override DbConnection CreateConnection()
    {
        return new DataWarehouseConnection();
    }

    /// <inheritdoc/>
    public override DbConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new DataWarehouseConnectionStringBuilder();
    }

    /// <inheritdoc/>
    public override DbParameter CreateParameter()
    {
        return new DataWarehouseParameter();
    }

    /// <inheritdoc/>
    public override DbCommandBuilder CreateCommandBuilder()
    {
        return new DataWarehouseCommandBuilder();
    }

    /// <inheritdoc/>
    public override DbDataAdapter CreateDataAdapter()
    {
        return new DataWarehouseDataAdapter();
    }

    /// <summary>
    /// Creates a new DataWarehouseConnection with the specified connection string.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>A new connection.</returns>
    public DataWarehouseConnection CreateConnection(string connectionString)
    {
        return new DataWarehouseConnection(connectionString);
    }

    /// <summary>
    /// Registers this provider factory with DbProviderFactories.
    /// </summary>
    public static void Register()
    {
        DbProviderFactories.RegisterFactory(ProviderInvariantName, Instance);
    }
}

/// <summary>
/// Provides a command builder for the DataWarehouse provider.
/// Generates INSERT, UPDATE, and DELETE commands based on a SELECT command.
/// </summary>
public sealed class DataWarehouseCommandBuilder : DbCommandBuilder
{
    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommandBuilder class.
    /// </summary>
    public DataWarehouseCommandBuilder()
    {
        QuotePrefix = "\"";
        QuoteSuffix = "\"";
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommandBuilder class with a data adapter.
    /// </summary>
    /// <param name="adapter">The data adapter to use.</param>
    public DataWarehouseCommandBuilder(DataWarehouseDataAdapter adapter)
        : this()
    {
        DataAdapter = adapter;
    }

    /// <summary>
    /// Gets or sets the data adapter.
    /// </summary>
    public new DataWarehouseDataAdapter? DataAdapter
    {
        get => (DataWarehouseDataAdapter?)base.DataAdapter;
        set => base.DataAdapter = value;
    }

    /// <summary>
    /// Gets the automatically generated command for deleting rows.
    /// </summary>
    /// <returns>The delete command.</returns>
    public new DataWarehouseCommand GetDeleteCommand()
    {
        return (DataWarehouseCommand)base.GetDeleteCommand();
    }

    /// <summary>
    /// Gets the automatically generated command for deleting rows with specified option.
    /// </summary>
    /// <param name="useColumnsForParameterNames">Whether to use column names for parameter names.</param>
    /// <returns>The delete command.</returns>
    public new DataWarehouseCommand GetDeleteCommand(bool useColumnsForParameterNames)
    {
        return (DataWarehouseCommand)base.GetDeleteCommand(useColumnsForParameterNames);
    }

    /// <summary>
    /// Gets the automatically generated command for inserting rows.
    /// </summary>
    /// <returns>The insert command.</returns>
    public new DataWarehouseCommand GetInsertCommand()
    {
        return (DataWarehouseCommand)base.GetInsertCommand();
    }

    /// <summary>
    /// Gets the automatically generated command for inserting rows with specified option.
    /// </summary>
    /// <param name="useColumnsForParameterNames">Whether to use column names for parameter names.</param>
    /// <returns>The insert command.</returns>
    public new DataWarehouseCommand GetInsertCommand(bool useColumnsForParameterNames)
    {
        return (DataWarehouseCommand)base.GetInsertCommand(useColumnsForParameterNames);
    }

    /// <summary>
    /// Gets the automatically generated command for updating rows.
    /// </summary>
    /// <returns>The update command.</returns>
    public new DataWarehouseCommand GetUpdateCommand()
    {
        return (DataWarehouseCommand)base.GetUpdateCommand();
    }

    /// <summary>
    /// Gets the automatically generated command for updating rows with specified option.
    /// </summary>
    /// <param name="useColumnsForParameterNames">Whether to use column names for parameter names.</param>
    /// <returns>The update command.</returns>
    public new DataWarehouseCommand GetUpdateCommand(bool useColumnsForParameterNames)
    {
        return (DataWarehouseCommand)base.GetUpdateCommand(useColumnsForParameterNames);
    }

    /// <inheritdoc/>
    protected override void ApplyParameterInfo(DbParameter parameter, System.Data.DataRow row, System.Data.StatementType statementType, bool whereClause)
    {
        if (parameter is DataWarehouseParameter dwParam)
        {
            var schemaRow = row;
            if (schemaRow["DataType"] is Type dataType)
            {
                dwParam.DbType = TypeToDbType(dataType);
            }
            if (schemaRow["ColumnSize"] is int size)
            {
                dwParam.Size = size;
            }
        }
    }

    /// <inheritdoc/>
    protected override string GetParameterName(int parameterOrdinal)
    {
        return $"@p{parameterOrdinal}";
    }

    /// <inheritdoc/>
    protected override string GetParameterName(string parameterName)
    {
        return $"@{parameterName}";
    }

    /// <inheritdoc/>
    protected override string GetParameterPlaceholder(int parameterOrdinal)
    {
        return $"@p{parameterOrdinal}";
    }

    /// <inheritdoc/>
    protected override void SetRowUpdatingHandler(DbDataAdapter adapter)
    {
        if (adapter is DataWarehouseDataAdapter dwAdapter)
        {
            dwAdapter.RowUpdating += (sender, e) => RowUpdatingHandler(e);
        }
    }

    private static System.Data.DbType TypeToDbType(Type type)
    {
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => System.Data.DbType.Boolean,
            TypeCode.Byte => System.Data.DbType.Byte,
            TypeCode.Char => System.Data.DbType.StringFixedLength,
            TypeCode.DateTime => System.Data.DbType.DateTime,
            TypeCode.Decimal => System.Data.DbType.Decimal,
            TypeCode.Double => System.Data.DbType.Double,
            TypeCode.Int16 => System.Data.DbType.Int16,
            TypeCode.Int32 => System.Data.DbType.Int32,
            TypeCode.Int64 => System.Data.DbType.Int64,
            TypeCode.SByte => System.Data.DbType.SByte,
            TypeCode.Single => System.Data.DbType.Single,
            TypeCode.String => System.Data.DbType.String,
            TypeCode.UInt16 => System.Data.DbType.UInt16,
            TypeCode.UInt32 => System.Data.DbType.UInt32,
            TypeCode.UInt64 => System.Data.DbType.UInt64,
            _ when type == typeof(byte[]) => System.Data.DbType.Binary,
            _ when type == typeof(Guid) => System.Data.DbType.Guid,
            _ when type == typeof(DateTimeOffset) => System.Data.DbType.DateTimeOffset,
            _ when type == typeof(TimeSpan) => System.Data.DbType.Time,
            _ => System.Data.DbType.Object
        };
    }
}

/// <summary>
/// Provides a data adapter for the DataWarehouse provider.
/// Enables filling DataSets and DataTables from DataWarehouse queries.
/// </summary>
public sealed class DataWarehouseDataAdapter : DbDataAdapter
{
    /// <summary>
    /// Initializes a new instance of the DataWarehouseDataAdapter class.
    /// </summary>
    public DataWarehouseDataAdapter()
    {
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseDataAdapter class with a select command.
    /// </summary>
    /// <param name="selectCommand">The select command.</param>
    public DataWarehouseDataAdapter(DataWarehouseCommand selectCommand)
    {
        SelectCommand = selectCommand;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseDataAdapter class with command text and connection.
    /// </summary>
    /// <param name="selectCommandText">The select command text.</param>
    /// <param name="connection">The connection.</param>
    public DataWarehouseDataAdapter(string selectCommandText, DataWarehouseConnection connection)
    {
        SelectCommand = new DataWarehouseCommand(selectCommandText, connection);
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseDataAdapter class with command text and connection string.
    /// </summary>
    /// <param name="selectCommandText">The select command text.</param>
    /// <param name="connectionString">The connection string.</param>
    public DataWarehouseDataAdapter(string selectCommandText, string connectionString)
    {
        SelectCommand = new DataWarehouseCommand(selectCommandText, new DataWarehouseConnection(connectionString));
    }

    /// <summary>
    /// Gets or sets the select command.
    /// </summary>
    public new DataWarehouseCommand? SelectCommand
    {
        get => (DataWarehouseCommand?)base.SelectCommand;
        set => base.SelectCommand = value;
    }

    /// <summary>
    /// Gets or sets the insert command.
    /// </summary>
    public new DataWarehouseCommand? InsertCommand
    {
        get => (DataWarehouseCommand?)base.InsertCommand;
        set => base.InsertCommand = value;
    }

    /// <summary>
    /// Gets or sets the update command.
    /// </summary>
    public new DataWarehouseCommand? UpdateCommand
    {
        get => (DataWarehouseCommand?)base.UpdateCommand;
        set => base.UpdateCommand = value;
    }

    /// <summary>
    /// Gets or sets the delete command.
    /// </summary>
    public new DataWarehouseCommand? DeleteCommand
    {
        get => (DataWarehouseCommand?)base.DeleteCommand;
        set => base.DeleteCommand = value;
    }

    /// <summary>
    /// Occurs during Update when a row is being updated.
    /// </summary>
    public event EventHandler<DataWarehouseRowUpdatingEventArgs>? RowUpdating;

    /// <summary>
    /// Occurs during Update after a row has been updated.
    /// </summary>
    public event EventHandler<DataWarehouseRowUpdatedEventArgs>? RowUpdated;

    /// <inheritdoc/>
    protected override RowUpdatingEventArgs CreateRowUpdatingEvent(
        System.Data.DataRow dataRow,
        System.Data.IDbCommand? command,
        System.Data.StatementType statementType,
        System.Data.Common.DataTableMapping tableMapping)
    {
        return new DataWarehouseRowUpdatingEventArgs(dataRow, command as DataWarehouseCommand, statementType, tableMapping);
    }

    /// <inheritdoc/>
    protected override RowUpdatedEventArgs CreateRowUpdatedEvent(
        System.Data.DataRow dataRow,
        System.Data.IDbCommand? command,
        System.Data.StatementType statementType,
        System.Data.Common.DataTableMapping tableMapping)
    {
        return new DataWarehouseRowUpdatedEventArgs(dataRow, command as DataWarehouseCommand, statementType, tableMapping);
    }

    /// <inheritdoc/>
    protected override void OnRowUpdating(RowUpdatingEventArgs value)
    {
        RowUpdating?.Invoke(this, (DataWarehouseRowUpdatingEventArgs)value);
    }

    /// <inheritdoc/>
    protected override void OnRowUpdated(RowUpdatedEventArgs value)
    {
        RowUpdated?.Invoke(this, (DataWarehouseRowUpdatedEventArgs)value);
    }
}

/// <summary>
/// Event arguments for the RowUpdating event.
/// </summary>
public sealed class DataWarehouseRowUpdatingEventArgs : RowUpdatingEventArgs
{
    /// <summary>
    /// Initializes a new instance of the DataWarehouseRowUpdatingEventArgs class.
    /// </summary>
    /// <param name="row">The row being updated.</param>
    /// <param name="command">The command used to update the row.</param>
    /// <param name="statementType">The type of SQL statement.</param>
    /// <param name="tableMapping">The table mapping.</param>
    public DataWarehouseRowUpdatingEventArgs(
        System.Data.DataRow row,
        DataWarehouseCommand? command,
        System.Data.StatementType statementType,
        System.Data.Common.DataTableMapping tableMapping)
        : base(row, command, statementType, tableMapping)
    {
    }

    /// <summary>
    /// Gets or sets the command used for the update.
    /// </summary>
    public new DataWarehouseCommand? Command
    {
        get => (DataWarehouseCommand?)base.Command;
        set => base.Command = value;
    }
}

/// <summary>
/// Event arguments for the RowUpdated event.
/// </summary>
public sealed class DataWarehouseRowUpdatedEventArgs : RowUpdatedEventArgs
{
    /// <summary>
    /// Initializes a new instance of the DataWarehouseRowUpdatedEventArgs class.
    /// </summary>
    /// <param name="row">The row that was updated.</param>
    /// <param name="command">The command used to update the row.</param>
    /// <param name="statementType">The type of SQL statement.</param>
    /// <param name="tableMapping">The table mapping.</param>
    public DataWarehouseRowUpdatedEventArgs(
        System.Data.DataRow row,
        DataWarehouseCommand? command,
        System.Data.StatementType statementType,
        System.Data.Common.DataTableMapping tableMapping)
        : base(row, command, statementType, tableMapping)
    {
    }

    /// <summary>
    /// Gets the command used for the update.
    /// </summary>
    public new DataWarehouseCommand? Command => (DataWarehouseCommand?)base.Command;
}
