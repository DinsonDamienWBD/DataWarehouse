using System.Data.Common;
using System.Runtime.Serialization;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Base exception class for all DataWarehouse ADO.NET provider errors.
/// Provides error codes, correlation tracking, and detailed error information.
/// Thread-safe and suitable for production use.
/// </summary>
[Serializable]
public class DataWarehouseProviderException : DbException
{
    /// <summary>
    /// Error codes for DataWarehouse provider exceptions.
    /// </summary>
    public enum ProviderErrorCode
    {
        /// <summary>Unknown error occurred.</summary>
        Unknown = 0,
        /// <summary>Connection failed to establish.</summary>
        ConnectionFailed = 1001,
        /// <summary>Connection was closed unexpectedly.</summary>
        ConnectionClosed = 1002,
        /// <summary>Connection string is invalid or malformed.</summary>
        InvalidConnectionString = 1003,
        /// <summary>Connection timeout occurred.</summary>
        ConnectionTimeout = 1004,
        /// <summary>Connection pool exhausted.</summary>
        PoolExhausted = 1005,
        /// <summary>Authentication failed.</summary>
        AuthenticationFailed = 1006,
        /// <summary>Command execution failed.</summary>
        CommandFailed = 2001,
        /// <summary>Command timeout occurred.</summary>
        CommandTimeout = 2002,
        /// <summary>Invalid SQL syntax.</summary>
        InvalidSyntax = 2003,
        /// <summary>Parameter binding failed.</summary>
        ParameterError = 2004,
        /// <summary>Transaction is required but not active.</summary>
        TransactionRequired = 3001,
        /// <summary>Transaction failed to commit.</summary>
        TransactionCommitFailed = 3002,
        /// <summary>Transaction failed to rollback.</summary>
        TransactionRollbackFailed = 3003,
        /// <summary>Transaction already completed.</summary>
        TransactionCompleted = 3004,
        /// <summary>Data reader encountered an error.</summary>
        ReaderError = 4001,
        /// <summary>Invalid column access.</summary>
        InvalidColumnAccess = 4002,
        /// <summary>Data type conversion failed.</summary>
        DataConversionError = 4003,
        /// <summary>Operation was cancelled.</summary>
        OperationCancelled = 5001,
        /// <summary>Resource not found.</summary>
        NotFound = 5002,
        /// <summary>Access denied.</summary>
        AccessDenied = 5003,
        /// <summary>Configuration error.</summary>
        ConfigurationError = 5004
    }

    private readonly ProviderErrorCode _providerErrorCode;

    /// <summary>
    /// Gets the provider-specific error code.
    /// </summary>
    public ProviderErrorCode ProviderError => _providerErrorCode;

    /// <summary>
    /// Gets the unique correlation ID for tracking this error.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets the timestamp when this exception was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Gets the SQL state code for this error (SQLSTATE format).
    /// </summary>
    public override string? SqlState => GetSqlState(_providerErrorCode);

    /// <summary>
    /// Gets the error code as an integer.
    /// </summary>
    public override int ErrorCode => (int)_providerErrorCode;

    /// <summary>
    /// Gets whether this error is transient and the operation can be retried.
    /// </summary>
    public override bool IsTransient => IsTransientError(_providerErrorCode);

    /// <summary>
    /// Initializes a new instance of the DataWarehouseProviderException class.
    /// </summary>
    /// <param name="errorCode">The provider error code.</param>
    /// <param name="message">The error message.</param>
    public DataWarehouseProviderException(ProviderErrorCode errorCode, string message)
        : base(message)
    {
        _providerErrorCode = errorCode;
        CorrelationId = GenerateCorrelationId();
        Timestamp = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseProviderException class with an inner exception.
    /// </summary>
    /// <param name="errorCode">The provider error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception that caused this error.</param>
    public DataWarehouseProviderException(ProviderErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        _providerErrorCode = errorCode;
        CorrelationId = GenerateCorrelationId();
        Timestamp = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseProviderException class with a correlation ID.
    /// </summary>
    /// <param name="errorCode">The provider error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="correlationId">The correlation ID for tracking.</param>
    public DataWarehouseProviderException(ProviderErrorCode errorCode, string message, string correlationId)
        : base(message)
    {
        _providerErrorCode = errorCode;
        CorrelationId = correlationId ?? GenerateCorrelationId();
        Timestamp = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseProviderException class for deserialization.
    /// </summary>
#pragma warning disable SYSLIB0051 // Type or member is obsolete
    protected DataWarehouseProviderException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        _providerErrorCode = (ProviderErrorCode)info.GetInt32(nameof(ProviderError));
        CorrelationId = info.GetString(nameof(CorrelationId)) ?? GenerateCorrelationId();
        Timestamp = (DateTimeOffset)info.GetValue(nameof(Timestamp), typeof(DateTimeOffset))!;
    }

    /// <inheritdoc/>
    [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051")]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(ProviderError), (int)_providerErrorCode);
        info.AddValue(nameof(CorrelationId), CorrelationId);
        info.AddValue(nameof(Timestamp), Timestamp);
    }
#pragma warning restore SYSLIB0051

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"[{_providerErrorCode}] CorrelationId: {CorrelationId}, Timestamp: {Timestamp:O} - {Message}";
    }

    private static string GenerateCorrelationId()
    {
        return $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
    }

    private static string GetSqlState(ProviderErrorCode errorCode)
    {
        return errorCode switch
        {
            ProviderErrorCode.ConnectionFailed => "08001",
            ProviderErrorCode.ConnectionClosed => "08003",
            ProviderErrorCode.InvalidConnectionString => "08001",
            ProviderErrorCode.ConnectionTimeout => "08006",
            ProviderErrorCode.PoolExhausted => "08004",
            ProviderErrorCode.AuthenticationFailed => "28000",
            ProviderErrorCode.CommandFailed => "42000",
            ProviderErrorCode.CommandTimeout => "57014",
            ProviderErrorCode.InvalidSyntax => "42601",
            ProviderErrorCode.ParameterError => "22023",
            ProviderErrorCode.TransactionRequired => "25001",
            ProviderErrorCode.TransactionCommitFailed => "40001",
            ProviderErrorCode.TransactionRollbackFailed => "40001",
            ProviderErrorCode.TransactionCompleted => "25P01",
            ProviderErrorCode.ReaderError => "HY000",
            ProviderErrorCode.InvalidColumnAccess => "42S22",
            ProviderErrorCode.DataConversionError => "22000",
            ProviderErrorCode.OperationCancelled => "57014",
            ProviderErrorCode.NotFound => "02000",
            ProviderErrorCode.AccessDenied => "42501",
            ProviderErrorCode.ConfigurationError => "F0000",
            _ => "HY000"
        };
    }

    private static bool IsTransientError(ProviderErrorCode errorCode)
    {
        return errorCode switch
        {
            ProviderErrorCode.ConnectionTimeout => true,
            ProviderErrorCode.CommandTimeout => true,
            ProviderErrorCode.PoolExhausted => true,
            ProviderErrorCode.TransactionCommitFailed => true,
            _ => false
        };
    }
}

/// <summary>
/// Exception thrown when a connection operation fails.
/// </summary>
[Serializable]
public sealed class DataWarehouseConnectionException : DataWarehouseProviderException
{
    /// <summary>
    /// Gets the connection string that was used (sanitized).
    /// </summary>
    public string? SanitizedConnectionString { get; }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseConnectionException class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="connectionString">The connection string (will be sanitized).</param>
    public DataWarehouseConnectionException(string message, string? connectionString = null)
        : base(ProviderErrorCode.ConnectionFailed, message)
    {
        SanitizedConnectionString = SanitizeConnectionString(connectionString);
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseConnectionException class with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    /// <param name="connectionString">The connection string (will be sanitized).</param>
    public DataWarehouseConnectionException(string message, Exception innerException, string? connectionString = null)
        : base(ProviderErrorCode.ConnectionFailed, message, innerException)
    {
        SanitizedConnectionString = SanitizeConnectionString(connectionString);
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseConnectionException class for deserialization.
    /// </summary>
#pragma warning disable SYSLIB0051
    private DataWarehouseConnectionException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        SanitizedConnectionString = info.GetString(nameof(SanitizedConnectionString));
    }
#pragma warning restore SYSLIB0051

    private static string? SanitizeConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return null;

        // Mask password and other sensitive values
        var result = System.Text.RegularExpressions.Regex.Replace(
            connectionString,
            @"(password|pwd|secret|key|token)=([^;]*)",
            "$1=****",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return result;
    }
}

/// <summary>
/// Exception thrown when a command execution fails.
/// </summary>
[Serializable]
public sealed class DataWarehouseCommandException : DataWarehouseProviderException
{
    /// <summary>
    /// Gets the command text that failed (truncated for safety).
    /// </summary>
    public string? CommandText { get; }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommandException class.
    /// </summary>
    /// <param name="errorCode">The specific error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="commandText">The command text (will be truncated).</param>
    public DataWarehouseCommandException(ProviderErrorCode errorCode, string message, string? commandText = null)
        : base(errorCode, message)
    {
        CommandText = TruncateCommandText(commandText);
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommandException class with an inner exception.
    /// </summary>
    /// <param name="errorCode">The specific error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    /// <param name="commandText">The command text (will be truncated).</param>
    public DataWarehouseCommandException(ProviderErrorCode errorCode, string message, Exception innerException, string? commandText = null)
        : base(errorCode, message, innerException)
    {
        CommandText = TruncateCommandText(commandText);
    }

#pragma warning disable SYSLIB0051
    private DataWarehouseCommandException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        CommandText = info.GetString(nameof(CommandText));
    }
#pragma warning restore SYSLIB0051

    private static string? TruncateCommandText(string? commandText)
    {
        if (string.IsNullOrEmpty(commandText))
            return null;

        const int maxLength = 500;
        return commandText.Length > maxLength
            ? commandText[..maxLength] + "..."
            : commandText;
    }
}

/// <summary>
/// Exception thrown when a transaction operation fails.
/// </summary>
[Serializable]
public sealed class DataWarehouseTransactionException : DataWarehouseProviderException
{
    /// <summary>
    /// Gets the transaction ID if available.
    /// </summary>
    public string? TransactionId { get; }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseTransactionException class.
    /// </summary>
    /// <param name="errorCode">The specific error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="transactionId">The transaction ID.</param>
    public DataWarehouseTransactionException(ProviderErrorCode errorCode, string message, string? transactionId = null)
        : base(errorCode, message)
    {
        TransactionId = transactionId;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseTransactionException class with an inner exception.
    /// </summary>
    /// <param name="errorCode">The specific error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    /// <param name="transactionId">The transaction ID.</param>
    public DataWarehouseTransactionException(ProviderErrorCode errorCode, string message, Exception innerException, string? transactionId = null)
        : base(errorCode, message, innerException)
    {
        TransactionId = transactionId;
    }

#pragma warning disable SYSLIB0051
    private DataWarehouseTransactionException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        TransactionId = info.GetString(nameof(TransactionId));
    }
#pragma warning restore SYSLIB0051
}

/// <summary>
/// Exception thrown when data reader operations fail.
/// </summary>
[Serializable]
public sealed class DataWarehouseDataReaderException : DataWarehouseProviderException
{
    /// <summary>
    /// Gets the column index that caused the error, if applicable.
    /// </summary>
    public int? ColumnIndex { get; }

    /// <summary>
    /// Gets the column name that caused the error, if applicable.
    /// </summary>
    public string? ColumnName { get; }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseDataReaderException class.
    /// </summary>
    /// <param name="errorCode">The specific error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="columnIndex">The column index.</param>
    /// <param name="columnName">The column name.</param>
    public DataWarehouseDataReaderException(ProviderErrorCode errorCode, string message, int? columnIndex = null, string? columnName = null)
        : base(errorCode, message)
    {
        ColumnIndex = columnIndex;
        ColumnName = columnName;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseDataReaderException class with an inner exception.
    /// </summary>
    /// <param name="errorCode">The specific error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    /// <param name="columnIndex">The column index.</param>
    /// <param name="columnName">The column name.</param>
    public DataWarehouseDataReaderException(ProviderErrorCode errorCode, string message, Exception innerException, int? columnIndex = null, string? columnName = null)
        : base(errorCode, message, innerException)
    {
        ColumnIndex = columnIndex;
        ColumnName = columnName;
    }

#pragma warning disable SYSLIB0051
    private DataWarehouseDataReaderException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        ColumnIndex = (int?)info.GetValue(nameof(ColumnIndex), typeof(int?));
        ColumnName = info.GetString(nameof(ColumnName));
    }
#pragma warning restore SYSLIB0051
}
