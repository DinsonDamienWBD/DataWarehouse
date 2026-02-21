using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol;

/// <summary>
/// Protocol family classification for database wire protocols.
/// </summary>
public enum ProtocolFamily
{
    /// <summary>Relational database protocols (PostgreSQL, MySQL, TDS, TNS).</summary>
    Relational,

    /// <summary>NoSQL database protocols (MongoDB, Redis, Cassandra).</summary>
    NoSQL,

    /// <summary>NewSQL distributed database protocols (CockroachDB, TiDB).</summary>
    NewSQL,

    /// <summary>Time series database protocols (InfluxDB, TimescaleDB).</summary>
    TimeSeries,

    /// <summary>Graph database protocols (Neo4j Bolt, Gremlin).</summary>
    Graph,

    /// <summary>Search engine protocols (Elasticsearch, OpenSearch).</summary>
    Search,

    /// <summary>Driver bridge protocols (ADO.NET, JDBC, ODBC).</summary>
    Driver,

    /// <summary>Cloud data warehouse protocols (Snowflake, BigQuery, Redshift).</summary>
    CloudDW,

    /// <summary>Embedded database protocols (SQLite, DuckDB, LevelDB).</summary>
    Embedded,

    /// <summary>Message queue protocols (Kafka, RabbitMQ, NATS).</summary>
    Messaging,

    /// <summary>Specialized analytics protocols (ClickHouse, Druid, Presto).</summary>
    Specialized
}

/// <summary>
/// Connection state for database protocol connections.
/// </summary>
public enum ProtocolConnectionState
{
    /// <summary>Connection has not been established.</summary>
    Disconnected,

    /// <summary>Connection is being established.</summary>
    Connecting,

    /// <summary>Authentication is in progress.</summary>
    Authenticating,

    /// <summary>Connection is ready for queries.</summary>
    Ready,

    /// <summary>A query is currently executing.</summary>
    Executing,

    /// <summary>Connection is being closed.</summary>
    Closing,

    /// <summary>Connection has failed.</summary>
    Failed
}

/// <summary>
/// Authentication method supported by database protocols.
/// </summary>
public enum AuthenticationMethod
{
    /// <summary>No authentication required.</summary>
    None,

    /// <summary>Clear text password authentication.</summary>
    ClearText,

    /// <summary>MD5 password hash authentication.</summary>
    MD5,

    /// <summary>SHA-256 password hash authentication.</summary>
    SHA256,

    /// <summary>SCRAM-SHA-256 authentication (RFC 7677).</summary>
    ScramSha256,

    /// <summary>SCRAM-SHA-512 authentication.</summary>
    ScramSha512,

    /// <summary>Kerberos/GSSAPI authentication.</summary>
    Kerberos,

    /// <summary>LDAP authentication.</summary>
    LDAP,

    /// <summary>Certificate-based authentication.</summary>
    Certificate,

    /// <summary>SASL authentication.</summary>
    SASL,

    /// <summary>AWS IAM authentication.</summary>
    AwsIam,

    /// <summary>Azure AD authentication.</summary>
    AzureAd,

    /// <summary>OAuth 2.0 / OIDC authentication.</summary>
    OAuth2,

    /// <summary>API key authentication.</summary>
    ApiKey,

    /// <summary>Token-based authentication.</summary>
    Token,

    /// <summary>Native password authentication (MySQL).</summary>
    NativePassword,

    /// <summary>Caching SHA-2 password authentication (MySQL 8.0+).</summary>
    CachingSha2,

    /// <summary>AWS IAM (Identity and Access Management).</summary>
    IAM,

    /// <summary>Active Directory authentication.</summary>
    ActiveDirectory,

    /// <summary>Azure Managed Identity.</summary>
    ManagedIdentity,

    /// <summary>GCP Service Account authentication.</summary>
    ServiceAccount,

    /// <summary>Windows Integrated authentication.</summary>
    Integrated,

    /// <summary>NATS NKey authentication.</summary>
    NKey
}

/// <summary>
/// Capabilities of a database protocol.
/// </summary>
public record ProtocolCapabilities
{
    /// <summary>Whether the protocol supports transactions.</summary>
    public bool SupportsTransactions { get; init; }

    /// <summary>Whether the protocol supports prepared statements.</summary>
    public bool SupportsPreparedStatements { get; init; }

    /// <summary>Whether the protocol supports cursors.</summary>
    public bool SupportsCursors { get; init; }

    /// <summary>Whether the protocol supports streaming results.</summary>
    public bool SupportsStreaming { get; init; }

    /// <summary>Whether the protocol supports batch operations.</summary>
    public bool SupportsBatch { get; init; }

    /// <summary>Whether the protocol supports notifications/pub-sub.</summary>
    public bool SupportsNotifications { get; init; }

    /// <summary>Whether the protocol supports SSL/TLS encryption.</summary>
    public bool SupportsSsl { get; init; }

    /// <summary>Whether the protocol supports compression.</summary>
    public bool SupportsCompression { get; init; }

    /// <summary>Whether the protocol supports multiplexing.</summary>
    public bool SupportsMultiplexing { get; init; }

    /// <summary>Whether the protocol supports server-side cursors.</summary>
    public bool SupportsServerCursors { get; init; }

    /// <summary>Whether the protocol supports bulk operations.</summary>
    public bool SupportsBulkOperations { get; init; }

    /// <summary>Whether the protocol supports query cancellation.</summary>
    public bool SupportsQueryCancellation { get; init; }

    /// <summary>Authentication methods supported by this protocol.</summary>
    public AuthenticationMethod[] SupportedAuthMethods { get; init; } = [AuthenticationMethod.ClearText];

    /// <summary>Creates capabilities for a standard relational database protocol.</summary>
    public static ProtocolCapabilities StandardRelational => new()
    {
        SupportsTransactions = true,
        SupportsPreparedStatements = true,
        SupportsCursors = true,
        SupportsStreaming = true,
        SupportsBatch = true,
        SupportsNotifications = false,
        SupportsSsl = true,
        SupportsCompression = false,
        SupportsMultiplexing = false,
        SupportsServerCursors = true,
        SupportsBulkOperations = true,
        SupportsQueryCancellation = true,
        SupportedAuthMethods = [AuthenticationMethod.ClearText, AuthenticationMethod.MD5, AuthenticationMethod.ScramSha256]
    };

    /// <summary>Creates capabilities for a standard NoSQL database protocol.</summary>
    public static ProtocolCapabilities StandardNoSql => new()
    {
        SupportsTransactions = false,
        SupportsPreparedStatements = false,
        SupportsCursors = true,
        SupportsStreaming = true,
        SupportsBatch = true,
        SupportsNotifications = true,
        SupportsSsl = true,
        SupportsCompression = true,
        SupportsMultiplexing = true,
        SupportsServerCursors = false,
        SupportsBulkOperations = true,
        SupportsQueryCancellation = true,
        SupportedAuthMethods = [AuthenticationMethod.ClearText, AuthenticationMethod.ScramSha256]
    };
}

/// <summary>
/// Information about a database wire protocol.
/// </summary>
public record ProtocolInfo
{
    /// <summary>Protocol name (e.g., "PostgreSQL Frontend/Backend Protocol").</summary>
    public string ProtocolName { get; init; } = "";

    /// <summary>Protocol version (e.g., "3.0").</summary>
    public string ProtocolVersion { get; init; } = "";

    /// <summary>Default port number.</summary>
    public int DefaultPort { get; init; }

    /// <summary>Protocol family classification.</summary>
    public ProtocolFamily Family { get; init; }

    /// <summary>Protocol capabilities.</summary>
    public ProtocolCapabilities Capabilities { get; init; } = new();

    /// <summary>Maximum packet size in bytes.</summary>
    public int MaxPacketSize { get; init; } = 1024 * 1024; // 1 MB default

    /// <summary>Protocol-specific parameters.</summary>
    public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Connection parameters for database protocols.
/// </summary>
public record ConnectionParameters
{
    /// <summary>Hostname or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>Port number (null for default).</summary>
    public int? Port { get; init; }

    /// <summary>Database/schema name.</summary>
    public string? Database { get; init; }

    /// <summary>Username for authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Password for authentication.</summary>
    public string? Password { get; init; }

    /// <summary>Whether to use SSL/TLS.</summary>
    public bool UseSsl { get; init; }

    /// <summary>SSL certificate for client authentication.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectionTimeoutMs { get; init; } = 30000;

    /// <summary>Command/query timeout in seconds.</summary>
    public int CommandTimeout { get; init; } = 60;

    /// <summary>Command/query timeout in milliseconds.</summary>
    public int CommandTimeoutMs { get; init; } = 60000;

    /// <summary>Authentication method to use.</summary>
    public AuthenticationMethod AuthMethod { get; init; } = AuthenticationMethod.ClearText;

    /// <summary>Additional connection parameters.</summary>
    public IReadOnlyDictionary<string, string> AdditionalParameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Extended properties for protocol-specific settings.</summary>
    public IReadOnlyDictionary<string, object?>? ExtendedProperties { get; init; }
}

/// <summary>
/// Result of a query execution.
/// </summary>
public record QueryResult
{
    /// <summary>Whether the query succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Number of rows affected (for DML).</summary>
    public long RowsAffected { get; init; }

    /// <summary>Result set rows (for SELECT).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; } = [];

    /// <summary>Column metadata.</summary>
    public IReadOnlyList<ColumnMetadata> Columns { get; init; } = [];

    /// <summary>Execution time in milliseconds.</summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Protocol-specific error code.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Additional result metadata.</summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Column metadata from query results.
/// </summary>
public record ColumnMetadata
{
    /// <summary>Column name.</summary>
    public required string Name { get; init; }

    /// <summary>Column data type (protocol-specific).</summary>
    public required string DataType { get; init; }

    /// <summary>Column ordinal position.</summary>
    public int Ordinal { get; init; }

    /// <summary>Whether the column is nullable.</summary>
    public bool IsNullable { get; init; } = true;

    /// <summary>Maximum length for string types.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Precision for numeric types.</summary>
    public int? Precision { get; init; }

    /// <summary>Scale for numeric types.</summary>
    public int? Scale { get; init; }
}

/// <summary>
/// Statistics for database protocol operations.
/// </summary>
public sealed class ProtocolStatistics
{
    /// <summary>Total connections established.</summary>
    public long ConnectionsEstablished { get; init; }

    /// <summary>Total connections failed.</summary>
    public long ConnectionsFailed { get; init; }

    /// <summary>Total queries executed.</summary>
    public long QueriesExecuted { get; init; }

    /// <summary>Total queries failed.</summary>
    public long QueriesFailed { get; init; }

    /// <summary>Total bytes sent.</summary>
    public long BytesSent { get; init; }

    /// <summary>Total bytes received.</summary>
    public long BytesReceived { get; init; }

    /// <summary>Total transactions started.</summary>
    public long TransactionsStarted { get; init; }

    /// <summary>Total transactions committed.</summary>
    public long TransactionsCommitted { get; init; }

    /// <summary>Total transactions rolled back.</summary>
    public long TransactionsRolledBack { get; init; }

    /// <summary>Average query execution time in milliseconds.</summary>
    public double AverageQueryTimeMs { get; init; }

    /// <summary>Start time of statistics tracking.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Last update time.</summary>
    public DateTime LastUpdateTime { get; init; }

    /// <summary>Creates empty statistics.</summary>
    public static ProtocolStatistics Empty => new()
    {
        StartTime = DateTime.UtcNow,
        LastUpdateTime = DateTime.UtcNow
    };
}

/// <summary>
/// Core interface for database wire protocol strategy implementations.
/// </summary>
public interface IDatabaseProtocolStrategy
{
    /// <summary>Gets the unique strategy identifier.</summary>
    string StrategyId { get; }

    /// <summary>Gets the human-readable strategy name.</summary>
    string StrategyName { get; }

    /// <summary>Gets protocol information.</summary>
    ProtocolInfo ProtocolInfo { get; }

    /// <summary>Connects to the database server.</summary>
    Task<bool> ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default);

    /// <summary>Disconnects from the database server.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Executes a query and returns results.</summary>
    Task<QueryResult> ExecuteQueryAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);

    /// <summary>Executes a non-query command.</summary>
    Task<QueryResult> ExecuteNonQueryAsync(string command, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);

    /// <summary>Begins a transaction.</summary>
    Task<string> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>Commits a transaction.</summary>
    Task CommitTransactionAsync(string transactionId, CancellationToken ct = default);

    /// <summary>Rolls back a transaction.</summary>
    Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default);

    /// <summary>Gets the current connection state.</summary>
    ProtocolConnectionState ConnectionState { get; }

    /// <summary>Gets protocol statistics.</summary>
    ProtocolStatistics GetStatistics();

    /// <summary>Resets protocol statistics.</summary>
    void ResetStatistics();

    /// <summary>Sends a ping/keepalive message.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for database wire protocol strategies.
/// Extends StrategyBase for unified lifecycle, counters, retry, and health infrastructure.
/// Provides common functionality for connection management, statistics tracking,
/// protocol encoding/decoding, and intelligence integration.
/// </summary>
public abstract class DatabaseProtocolStrategyBase : StrategyBase, IDatabaseProtocolStrategy
{
    // Statistics tracking
    private long _connectionsEstablished;
    private long _connectionsFailed;
    private long _queriesExecuted;
    private long _queriesFailed;
    private long _bytesSent;
    private long _bytesReceived;
    private long _transactionsStarted;
    private long _transactionsCommitted;
    private long _transactionsRolledBack;
    private long _totalQueryTimeMs;
    private readonly DateTime _startTime;
    private DateTime _lastUpdateTime;

    // Connection state
    private volatile ProtocolConnectionState _connectionState = ProtocolConnectionState.Disconnected;
    protected TcpClient? TcpClient;
    protected NetworkStream? NetworkStream;
    protected SslStream? SslStream;
    protected Stream? ActiveStream;
    protected ConnectionParameters? CurrentParameters;

    // Transaction tracking
    protected readonly BoundedDictionary<string, object> ActiveTransactions = new BoundedDictionary<string, object>(1000);

    // Buffer pool helper method for protocol encoding
    protected static byte[] RentBuffer(int size) => ArrayPool<byte>.Shared.Rent(size);
    protected static void ReturnBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);

    /// <inheritdoc/>
    public abstract override string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string StrategyName { get; }

    /// <summary>
    /// Bridges StrategyBase.Name to domain-specific StrategyName.
    /// </summary>
    public override string Name => StrategyName;

    /// <inheritdoc/>
    public abstract ProtocolInfo ProtocolInfo { get; }

    /// <inheritdoc/>
    public ProtocolConnectionState ConnectionState => _connectionState;

    /// <summary>Initializes a new instance of the base strategy.</summary>
    protected DatabaseProtocolStrategyBase()
    {
        _startTime = DateTime.UtcNow;
        _lastUpdateTime = DateTime.UtcNow;
    }

    #region Connection Management

    /// <inheritdoc/>
    public virtual async Task<bool> ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        if (_connectionState != ProtocolConnectionState.Disconnected)
        {
            throw new InvalidOperationException($"Cannot connect: current state is {_connectionState}");
        }

        _connectionState = ProtocolConnectionState.Connecting;
        CurrentParameters = parameters;

        try
        {
            // Establish TCP connection
            var port = parameters.Port ?? ProtocolInfo.DefaultPort;
            TcpClient = new TcpClient();

            using var timeoutCts = new CancellationTokenSource(parameters.ConnectionTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await TcpClient.ConnectAsync(parameters.Host, port, linkedCts.Token);
            NetworkStream = TcpClient.GetStream();
            ActiveStream = NetworkStream;

            // Establish SSL if required
            if (parameters.UseSsl)
            {
                await EstablishSslAsync(parameters, linkedCts.Token);
            }

            // Perform protocol-specific handshake
            _connectionState = ProtocolConnectionState.Authenticating;
            await PerformHandshakeAsync(parameters, linkedCts.Token);

            // Authenticate
            await AuthenticateAsync(parameters, linkedCts.Token);

            _connectionState = ProtocolConnectionState.Ready;
            Interlocked.Increment(ref _connectionsEstablished);
            _lastUpdateTime = DateTime.UtcNow;

            return true;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _connectionsFailed);
            _connectionState = ProtocolConnectionState.Failed;
            await CleanupConnectionAsync();
            throw;
        }
    }

    /// <summary>Establishes SSL/TLS connection.</summary>
    protected virtual async Task EstablishSslAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (NetworkStream == null)
        {
            throw new InvalidOperationException("Network stream not established");
        }

        // Notify server of SSL upgrade if required by protocol
        await NotifySslUpgradeAsync(ct);

        SslStream = new SslStream(
            NetworkStream,
            leaveInnerStreamOpen: false,
            ValidateServerCertificate,
            SelectClientCertificate);

        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = parameters.Host,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.Online
        };

        if (parameters.ClientCertificate != null)
        {
            sslOptions.ClientCertificates = [parameters.ClientCertificate];
        }

        await SslStream.AuthenticateAsClientAsync(sslOptions, ct);
        ActiveStream = SslStream;
    }

    /// <summary>Notifies the server of SSL upgrade. Override for protocol-specific behavior.</summary>
    protected virtual Task NotifySslUpgradeAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Validates server certificate.</summary>
    protected virtual bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        // In production, implement proper certificate validation
        return sslPolicyErrors == SslPolicyErrors.None;
    }

    /// <summary>Selects client certificate for mutual TLS.</summary>
    protected virtual X509Certificate? SelectClientCertificate(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers)
    {
        return localCertificates.Count > 0 ? localCertificates[0] : null;
    }

    /// <summary>Performs protocol-specific handshake. Must be implemented by derived classes.</summary>
    protected abstract Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);

    /// <summary>Performs authentication. Must be implemented by derived classes.</summary>
    protected abstract Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);

    /// <inheritdoc/>
    public virtual async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_connectionState == ProtocolConnectionState.Disconnected)
        {
            return;
        }

        _connectionState = ProtocolConnectionState.Closing;

        try
        {
            // Send protocol-specific disconnect message
            await SendDisconnectMessageAsync(ct);
        }
        catch
        {
            // Ignore errors during disconnect
        }
        finally
        {
            await CleanupConnectionAsync();
            _connectionState = ProtocolConnectionState.Disconnected;
        }
    }

    /// <summary>Sends protocol-specific disconnect message.</summary>
    protected virtual Task SendDisconnectMessageAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Cleans up connection resources.</summary>
    protected virtual async Task CleanupConnectionAsync()
    {
        if (SslStream != null)
        {
            await SslStream.DisposeAsync();
            SslStream = null;
        }

        if (NetworkStream != null)
        {
            await NetworkStream.DisposeAsync();
            NetworkStream = null;
        }

        TcpClient?.Dispose();
        TcpClient = null;
        ActiveStream = null;
        CurrentParameters = null;
        ActiveTransactions.Clear();
    }

    #endregion

    #region Query Execution

    /// <inheritdoc/>
    public virtual async Task<QueryResult> ExecuteQueryAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        EnsureConnected();

        var startTime = DateTime.UtcNow;
        _connectionState = ProtocolConnectionState.Executing;

        try
        {
            var result = await ExecuteQueryCoreAsync(query, parameters, ct);

            Interlocked.Increment(ref _queriesExecuted);
            var executionTime = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
            Interlocked.Add(ref _totalQueryTimeMs, executionTime);
            _lastUpdateTime = DateTime.UtcNow;

            return result with { ExecutionTimeMs = executionTime };
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _queriesFailed);
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        finally
        {
            _connectionState = ProtocolConnectionState.Ready;
        }
    }

    /// <summary>Core query execution. Must be implemented by derived classes.</summary>
    protected abstract Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);

    /// <inheritdoc/>
    public virtual async Task<QueryResult> ExecuteNonQueryAsync(string command, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        EnsureConnected();

        var startTime = DateTime.UtcNow;
        _connectionState = ProtocolConnectionState.Executing;

        try
        {
            var result = await ExecuteNonQueryCoreAsync(command, parameters, ct);

            Interlocked.Increment(ref _queriesExecuted);
            var executionTime = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
            Interlocked.Add(ref _totalQueryTimeMs, executionTime);
            _lastUpdateTime = DateTime.UtcNow;

            return result with { ExecutionTimeMs = executionTime };
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _queriesFailed);
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        finally
        {
            _connectionState = ProtocolConnectionState.Ready;
        }
    }

    /// <summary>Core non-query execution. Must be implemented by derived classes.</summary>
    protected abstract Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);

    #endregion

    #region Transaction Management

    /// <inheritdoc/>
    public virtual async Task<string> BeginTransactionAsync(CancellationToken ct = default)
    {
        EnsureConnected();

        if (!ProtocolInfo.Capabilities.SupportsTransactions)
        {
            throw new NotSupportedException($"Protocol {StrategyId} does not support transactions");
        }

        var transactionId = await BeginTransactionCoreAsync(ct);
        ActiveTransactions[transactionId] = new object();
        Interlocked.Increment(ref _transactionsStarted);
        _lastUpdateTime = DateTime.UtcNow;

        return transactionId;
    }

    /// <summary>Core transaction begin. Override for protocol-specific implementation.</summary>
    protected virtual async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        await ExecuteNonQueryCoreAsync("BEGIN", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    public virtual async Task CommitTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        EnsureConnected();

        if (!ActiveTransactions.TryRemove(transactionId, out _))
        {
            throw new InvalidOperationException($"Transaction {transactionId} not found");
        }

        await CommitTransactionCoreAsync(transactionId, ct);
        Interlocked.Increment(ref _transactionsCommitted);
        _lastUpdateTime = DateTime.UtcNow;
    }

    /// <summary>Core transaction commit. Override for protocol-specific implementation.</summary>
    protected virtual async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteNonQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    public virtual async Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        EnsureConnected();

        if (!ActiveTransactions.TryRemove(transactionId, out _))
        {
            throw new InvalidOperationException($"Transaction {transactionId} not found");
        }

        await RollbackTransactionCoreAsync(transactionId, ct);
        Interlocked.Increment(ref _transactionsRolledBack);
        _lastUpdateTime = DateTime.UtcNow;
    }

    /// <summary>Core transaction rollback. Override for protocol-specific implementation.</summary>
    protected virtual async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteNonQueryCoreAsync("ROLLBACK", null, ct);
    }

    #endregion

    #region Protocol Encoding/Decoding Helpers

    /// <summary>Sends data over the active stream.</summary>
    protected async Task SendAsync(byte[] data, CancellationToken ct)
    {
        if (ActiveStream == null)
        {
            throw new InvalidOperationException("No active stream");
        }

        await ActiveStream.WriteAsync(data, ct);
        await ActiveStream.FlushAsync(ct);
        Interlocked.Add(ref _bytesSent, data.Length);
    }

    /// <summary>Sends data over the active stream.</summary>
    protected async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (ActiveStream == null)
        {
            throw new InvalidOperationException("No active stream");
        }

        await ActiveStream.WriteAsync(data, ct);
        await ActiveStream.FlushAsync(ct);
        Interlocked.Add(ref _bytesSent, data.Length);
    }

    /// <summary>Receives data from the active stream.</summary>
    protected async Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (ActiveStream == null)
        {
            throw new InvalidOperationException("No active stream");
        }

        var bytesRead = await ActiveStream.ReadAsync(buffer.AsMemory(offset, count), ct);
        Interlocked.Add(ref _bytesReceived, bytesRead);
        return bytesRead;
    }

    /// <summary>Receives exact number of bytes from the active stream.</summary>
    protected async Task<byte[]> ReceiveExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var bytesRead = await ReceiveAsync(buffer, offset, count - offset, ct);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException($"Connection closed, expected {count} bytes, received {offset}");
            }
            offset += bytesRead;
        }

        return buffer;
    }

    /// <summary>Writes a 32-bit integer in network byte order (big-endian).</summary>
    protected static void WriteInt32BE(Span<byte> buffer, int value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }

    /// <summary>Reads a 32-bit integer in network byte order (big-endian).</summary>
    protected static int ReadInt32BE(ReadOnlySpan<byte> buffer)
    {
        return (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
    }

    /// <summary>Writes a 32-bit integer in little-endian byte order.</summary>
    protected static void WriteInt32LE(Span<byte> buffer, int value)
    {
        buffer[0] = (byte)value;
        buffer[1] = (byte)(value >> 8);
        buffer[2] = (byte)(value >> 16);
        buffer[3] = (byte)(value >> 24);
    }

    /// <summary>Reads a 32-bit integer in little-endian byte order.</summary>
    protected static int ReadInt32LE(ReadOnlySpan<byte> buffer)
    {
        return buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
    }

    /// <summary>Writes a 16-bit integer in network byte order (big-endian).</summary>
    protected static void WriteInt16BE(Span<byte> buffer, short value)
    {
        buffer[0] = (byte)(value >> 8);
        buffer[1] = (byte)value;
    }

    /// <summary>Reads a 16-bit integer in network byte order (big-endian).</summary>
    protected static short ReadInt16BE(ReadOnlySpan<byte> buffer)
    {
        return (short)((buffer[0] << 8) | buffer[1]);
    }

    /// <summary>Writes a null-terminated string.</summary>
    protected static int WriteNullTerminatedString(Span<byte> buffer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        bytes.CopyTo(buffer);
        buffer[bytes.Length] = 0;
        return bytes.Length + 1;
    }

    /// <summary>Reads a null-terminated string.</summary>
    protected static string ReadNullTerminatedString(ReadOnlySpan<byte> buffer, out int bytesRead)
    {
        var nullIndex = buffer.IndexOf((byte)0);
        if (nullIndex < 0)
        {
            throw new InvalidDataException("Null terminator not found");
        }

        bytesRead = nullIndex + 1;
        return System.Text.Encoding.UTF8.GetString(buffer[..nullIndex]);
    }

    #endregion

    #region Statistics and Health

    /// <inheritdoc/>
    public virtual ProtocolStatistics GetStatistics()
    {
        var totalQueries = Interlocked.Read(ref _queriesExecuted);
        var avgTime = totalQueries > 0 ? (double)Interlocked.Read(ref _totalQueryTimeMs) / totalQueries : 0;

        return new ProtocolStatistics
        {
            ConnectionsEstablished = Interlocked.Read(ref _connectionsEstablished),
            ConnectionsFailed = Interlocked.Read(ref _connectionsFailed),
            QueriesExecuted = totalQueries,
            QueriesFailed = Interlocked.Read(ref _queriesFailed),
            BytesSent = Interlocked.Read(ref _bytesSent),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            TransactionsStarted = Interlocked.Read(ref _transactionsStarted),
            TransactionsCommitted = Interlocked.Read(ref _transactionsCommitted),
            TransactionsRolledBack = Interlocked.Read(ref _transactionsRolledBack),
            AverageQueryTimeMs = avgTime,
            StartTime = _startTime,
            LastUpdateTime = _lastUpdateTime
        };
    }

    /// <inheritdoc/>
    public virtual void ResetStatistics()
    {
        Interlocked.Exchange(ref _connectionsEstablished, 0);
        Interlocked.Exchange(ref _connectionsFailed, 0);
        Interlocked.Exchange(ref _queriesExecuted, 0);
        Interlocked.Exchange(ref _queriesFailed, 0);
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _transactionsStarted, 0);
        Interlocked.Exchange(ref _transactionsCommitted, 0);
        Interlocked.Exchange(ref _transactionsRolledBack, 0);
        Interlocked.Exchange(ref _totalQueryTimeMs, 0);
        _lastUpdateTime = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_connectionState != ProtocolConnectionState.Ready)
        {
            return false;
        }

        try
        {
            return await PingCoreAsync(ct);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Core ping implementation. Override for protocol-specific behavior.</summary>
    protected virtual async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        // Default: execute a simple query
        var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
        return result.Success;
    }

    #endregion

    #region Intelligence Integration

    /// <summary>Gets knowledge about this strategy for AI discovery.</summary>
    public virtual KnowledgeObject GetStrategyKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"strategy.{StrategyId}",
            Topic = "database-protocol",
            SourcePluginId = "sdk.strategy",
            SourcePluginName = StrategyName,
            KnowledgeType = "capability",
            Description = GetStrategyDescription(),
            Payload = GetKnowledgePayload(),
            Tags = GetKnowledgeTags()
        };
    }

    /// <summary>Gets a description for this strategy.</summary>
    protected virtual string GetStrategyDescription() =>
        $"{StrategyName} wire protocol strategy for {ProtocolInfo.ProtocolName}";

    /// <summary>Gets the knowledge payload.</summary>
    protected virtual Dictionary<string, object> GetKnowledgePayload() => new()
    {
        ["protocolName"] = ProtocolInfo.ProtocolName,
        ["protocolVersion"] = ProtocolInfo.ProtocolVersion,
        ["defaultPort"] = ProtocolInfo.DefaultPort,
        ["family"] = ProtocolInfo.Family.ToString(),
        ["supportsTransactions"] = ProtocolInfo.Capabilities.SupportsTransactions,
        ["supportsSsl"] = ProtocolInfo.Capabilities.SupportsSsl,
        ["supportsStreaming"] = ProtocolInfo.Capabilities.SupportsStreaming
    };

    /// <summary>Gets tags for this strategy.</summary>
    protected virtual string[] GetKnowledgeTags() =>
    [
        "strategy",
        "database-protocol",
        ProtocolInfo.Family.ToString().ToLowerInvariant(),
        ProtocolInfo.ProtocolName.ToLowerInvariant().Replace(" ", "-")
    ];

    /// <summary>Gets the registered capability for this strategy.</summary>
    public virtual RegisteredCapability GetStrategyCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"strategy.{StrategyId}",
            DisplayName = StrategyName,
            Description = GetStrategyDescription(),
            Category = CapabilityCategory.Connector,
            PluginId = "sdk.strategy",
            PluginName = StrategyName,
            PluginVersion = "1.0.0",
            Tags = GetKnowledgeTags(),
            Metadata = GetKnowledgePayload(),
            SemanticDescription = $"Use {StrategyName} for {ProtocolInfo.Family} database connectivity via {ProtocolInfo.ProtocolName}"
        };
    }

    #endregion

    #region Helpers

    /// <summary>Ensures the connection is established.</summary>
    protected void EnsureConnected()
    {
        if (_connectionState != ProtocolConnectionState.Ready)
        {
            throw new InvalidOperationException($"Not connected: current state is {_connectionState}");
        }
    }

    #endregion

    #region Dispose Pattern (StrategyBase)

    /// <summary>
    /// Overrides StrategyBase.DisposeAsyncCore to disconnect and release connection resources.
    /// Called by StrategyBase.DisposeAsync() before the synchronous dispose.
    /// </summary>
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <summary>
    /// Overrides StrategyBase.Dispose(bool) to release unmanaged resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            TcpClient?.Dispose();
            TcpClient = null;
            ActiveStream = null;
        }
        base.Dispose(disposing);
    }

    #endregion
}

/// <summary>
/// Interface for database protocol strategy registry.
/// </summary>
public interface IDatabaseProtocolStrategyRegistry
{
    /// <summary>Registers a protocol strategy.</summary>
    void Register(IDatabaseProtocolStrategy strategy);

    /// <summary>Gets a strategy by ID.</summary>
    IDatabaseProtocolStrategy? GetStrategy(string strategyId);

    /// <summary>Gets all registered strategies.</summary>
    IReadOnlyCollection<IDatabaseProtocolStrategy> GetAllStrategies();

    /// <summary>Gets strategies by protocol family.</summary>
    IReadOnlyCollection<IDatabaseProtocolStrategy> GetStrategiesByFamily(ProtocolFamily family);

    /// <summary>Discovers strategies from assemblies.</summary>
    void DiscoverStrategies(params System.Reflection.Assembly[] assemblies);
}

/// <summary>
/// Default implementation of protocol strategy registry.
/// </summary>
public sealed class DatabaseProtocolStrategyRegistry : IDatabaseProtocolStrategyRegistry
{
    private readonly BoundedDictionary<string, IDatabaseProtocolStrategy> _strategies = new BoundedDictionary<string, IDatabaseProtocolStrategy>(1000);

    /// <inheritdoc/>
    public void Register(IDatabaseProtocolStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <inheritdoc/>
    public IDatabaseProtocolStrategy? GetStrategy(string strategyId)
    {
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IDatabaseProtocolStrategy> GetAllStrategies()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IDatabaseProtocolStrategy> GetStrategiesByFamily(ProtocolFamily family)
    {
        return _strategies.Values
            .Where(s => s.ProtocolInfo.Family == family)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public void DiscoverStrategies(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(IDatabaseProtocolStrategy);

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IDatabaseProtocolStrategy strategy)
                        {
                            Register(strategy);
                        }
                    }
                    catch
                    {
                        // Skip types that can't be instantiated
                    }
                }
            }
            catch
            {
                // Skip assemblies that can't be scanned
            }
        }
    }
}

/// <summary>
/// Array pool for efficient buffer management.
/// </summary>
public static class ArrayPool<T>
{
    private static readonly System.Buffers.ArrayPool<T> _pool = System.Buffers.ArrayPool<T>.Shared;

    /// <summary>Gets the shared array pool.</summary>
    public static System.Buffers.ArrayPool<T> Shared => _pool;
}
