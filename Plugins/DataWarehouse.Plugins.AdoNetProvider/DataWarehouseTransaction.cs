using System.Data;
using System.Data.Common;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Represents a transaction to be performed at a DataWarehouse database.
/// Provides commit, rollback, and savepoint support with proper isolation levels.
/// </summary>
/// <remarks>
/// Transactions are created by calling BeginTransaction on a DataWarehouseConnection.
/// This class is not thread-safe; operations should be performed on a single thread.
/// </remarks>
public sealed class DataWarehouseTransaction : DbTransaction
{
    private readonly DataWarehouseConnection _connection;
    private readonly IsolationLevel _isolationLevel;
    private readonly string _transactionId;
    private bool _completed;
    private bool _disposed;

    /// <inheritdoc/>
    public override IsolationLevel IsolationLevel => _isolationLevel;

    /// <inheritdoc/>
    protected override DbConnection? DbConnection => _connection;

    /// <summary>
    /// Gets whether the transaction has completed (committed or rolled back).
    /// </summary>
    public bool IsCompleted => _completed;

    /// <summary>
    /// Gets the unique identifier for this transaction.
    /// </summary>
    public string TransactionId => _transactionId;

    /// <summary>
    /// Initializes a new instance of the DataWarehouseTransaction class.
    /// </summary>
    /// <param name="connection">The connection associated with this transaction.</param>
    /// <param name="isolationLevel">The isolation level for this transaction.</param>
    internal DataWarehouseTransaction(DataWarehouseConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _isolationLevel = isolationLevel;
        _transactionId = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Begins the transaction on the server.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal async Task BeginAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();

        var isolationCommand = _isolationLevel switch
        {
            IsolationLevel.ReadUncommitted => "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED",
            IsolationLevel.ReadCommitted => "SET TRANSACTION ISOLATION LEVEL READ COMMITTED",
            IsolationLevel.RepeatableRead => "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ",
            IsolationLevel.Serializable => "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE",
            IsolationLevel.Snapshot => "SET TRANSACTION ISOLATION LEVEL SNAPSHOT",
            _ => null
        };

        if (isolationCommand != null)
        {
            await ExecuteInternalAsync(isolationCommand, cancellationToken);
        }

        await ExecuteInternalAsync("BEGIN", cancellationToken);
    }

    /// <inheritdoc/>
    public override void Commit()
    {
        CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously commits the database transaction.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfCompleted();

        try
        {
            await ExecuteInternalAsync("COMMIT", cancellationToken);
            _completed = true;
            _connection.ClearTransaction();
        }
        catch (Exception ex)
        {
            throw new DataWarehouseTransactionException(
                DataWarehouseProviderException.ProviderErrorCode.TransactionCommitFailed,
                $"Failed to commit transaction: {ex.Message}",
                ex,
                _transactionId);
        }
    }

    /// <inheritdoc/>
    public override void Rollback()
    {
        RollbackAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously rolls back the database transaction.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfCompleted();

        try
        {
            await ExecuteInternalAsync("ROLLBACK", cancellationToken);
            _completed = true;
            _connection.ClearTransaction();
        }
        catch (Exception ex)
        {
            // Mark as completed even if rollback fails to prevent further operations
            _completed = true;
            _connection.ClearTransaction();
            throw new DataWarehouseTransactionException(
                DataWarehouseProviderException.ProviderErrorCode.TransactionRollbackFailed,
                $"Failed to rollback transaction: {ex.Message}",
                ex,
                _transactionId);
        }
    }

    /// <summary>
    /// Creates a savepoint in the transaction.
    /// </summary>
    /// <param name="savepointName">The name of the savepoint.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public override async Task SaveAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(savepointName);
        ThrowIfDisposed();
        ThrowIfCompleted();

        var sanitizedName = SanitizeSavepointName(savepointName);
        await ExecuteInternalAsync($"SAVEPOINT {sanitizedName}", cancellationToken);
    }

    /// <summary>
    /// Releases a savepoint in the transaction.
    /// </summary>
    /// <param name="savepointName">The name of the savepoint.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public override async Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(savepointName);
        ThrowIfDisposed();
        ThrowIfCompleted();

        var sanitizedName = SanitizeSavepointName(savepointName);
        await ExecuteInternalAsync($"RELEASE SAVEPOINT {sanitizedName}", cancellationToken);
    }

    /// <summary>
    /// Rolls back to a savepoint in the transaction.
    /// </summary>
    /// <param name="savepointName">The name of the savepoint.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task RollbackToAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(savepointName);
        ThrowIfDisposed();
        ThrowIfCompleted();

        var sanitizedName = SanitizeSavepointName(savepointName);
        await ExecuteInternalAsync($"ROLLBACK TO SAVEPOINT {sanitizedName}", cancellationToken);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // If transaction is not completed, attempt to rollback
            if (!_completed)
            {
                try
                {
                    Rollback();
                }
                catch
                {
                    // Ignore errors during disposal rollback
                }
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // If transaction is not completed, attempt to rollback
        if (!_completed)
        {
            try
            {
                await RollbackAsync();
            }
            catch
            {
                // Ignore errors during disposal rollback
            }
        }

        _disposed = true;
        await base.DisposeAsync();
    }

    private async Task ExecuteInternalAsync(string commandText, CancellationToken cancellationToken)
    {
        using var command = new DataWarehouseCommand(commandText, _connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new DataWarehouseTransactionException(
                DataWarehouseProviderException.ProviderErrorCode.TransactionCompleted,
                "Transaction has already been committed or rolled back.",
                _transactionId);
        }
    }

    private static string SanitizeSavepointName(string name)
    {
        // Only allow alphanumeric characters and underscores
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sanitized.Append(c);
        }
        return sanitized.Length > 0 ? sanitized.ToString() : "sp_default";
    }
}
