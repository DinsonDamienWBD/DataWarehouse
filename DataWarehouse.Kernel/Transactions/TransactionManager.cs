using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Transactions
{
    /// <summary>
    /// Basic transaction manager for coordinating multi-step operations.
    /// Provides compensation-based rollback for failures.
    /// </summary>
    public sealed class TransactionManager : ITransactionManager
    {
        private static readonly AsyncLocal<TransactionScope?> _ambientTransaction = new();
        private readonly ConcurrentDictionary<string, TransactionScope> _activeTransactions = new();

        public ITransactionScope? Current => _ambientTransaction.Value;

        public ITransactionScope BeginTransaction(TransactionOptions? options = null)
        {
            options ??= new TransactionOptions();

            var transaction = new TransactionScope(
                Guid.NewGuid().ToString("N"),
                options,
                this);

            _activeTransactions[transaction.TransactionId] = transaction;

            // Set as ambient if there isn't one
            if (_ambientTransaction.Value == null)
            {
                _ambientTransaction.Value = transaction;
            }

            return transaction;
        }

        internal void OnTransactionCompleted(TransactionScope transaction)
        {
            _activeTransactions.TryRemove(transaction.TransactionId, out _);

            if (_ambientTransaction.Value?.TransactionId == transaction.TransactionId)
            {
                _ambientTransaction.Value = null;
            }
        }

        /// <summary>
        /// Gets all active transactions.
        /// </summary>
        public IEnumerable<ITransactionScope> GetActiveTransactions()
        {
            return _activeTransactions.Values.ToArray();
        }
    }

    /// <summary>
    /// Default transaction scope implementation.
    /// </summary>
    public sealed class TransactionScope : ITransactionScope
    {
        private readonly TransactionOptions _options;
        private readonly TransactionManager _manager;
        private readonly List<Func<Task>> _compensations = new();
        private readonly ConcurrentDictionary<string, object> _resources = new();
        private readonly CancellationTokenSource _timeoutCts;
        private readonly object _stateLock = new();

        private TransactionState _state = TransactionState.Active;
        private bool _disposed;

        public string TransactionId { get; }
        public DateTime StartedAt { get; }

        public TransactionState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        internal TransactionScope(
            string transactionId,
            TransactionOptions options,
            TransactionManager manager)
        {
            TransactionId = transactionId;
            _options = options;
            _manager = manager;
            StartedAt = DateTime.UtcNow;

            _timeoutCts = new CancellationTokenSource(_options.Timeout);
            _timeoutCts.Token.Register(() =>
            {
                if (State == TransactionState.Active)
                {
                    _ = RollbackAsync();
                }
            });
        }

        public void RegisterCompensation(Func<Task> compensationAction)
        {
            ArgumentNullException.ThrowIfNull(compensationAction);

            lock (_stateLock)
            {
                if (_state != TransactionState.Active)
                {
                    throw new InvalidOperationException(
                        $"Cannot register compensation when transaction is in state {_state}");
                }

                _compensations.Add(compensationAction);
            }
        }

        public void EnlistResource(string resourceId, object resource)
        {
            ArgumentNullException.ThrowIfNull(resourceId);
            ArgumentNullException.ThrowIfNull(resource);

            lock (_stateLock)
            {
                if (_state != TransactionState.Active)
                {
                    throw new InvalidOperationException(
                        $"Cannot enlist resource when transaction is in state {_state}");
                }

                _resources[resourceId] = resource;
            }
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (_state != TransactionState.Active)
                {
                    throw new InvalidOperationException(
                        $"Cannot commit transaction in state {_state}");
                }

                _state = TransactionState.Committing;
            }

            try
            {
                // In a basic implementation, commit is essentially a no-op
                // since operations have already been executed.
                // In a more advanced implementation, this would be where
                // we make changes permanent.

                lock (_stateLock)
                {
                    _state = TransactionState.Committed;
                }

                // Clear compensations since we don't need them anymore
                _compensations.Clear();
            }
            catch (Exception)
            {
                lock (_stateLock)
                {
                    _state = TransactionState.Failed;
                }

                throw;
            }
            finally
            {
                _manager.OnTransactionCompleted(this);
            }
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (_state == TransactionState.Committed)
                {
                    throw new InvalidOperationException("Cannot rollback a committed transaction");
                }

                if (_state == TransactionState.RolledBack)
                {
                    return; // Already rolled back
                }

                _state = TransactionState.RollingBack;
            }

            var errors = new List<Exception>();

            // Execute compensations in reverse order
            for (var i = _compensations.Count - 1; i >= 0; i--)
            {
                try
                {
                    await _compensations[i]();
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            lock (_stateLock)
            {
                _state = TransactionState.RolledBack;
            }

            _manager.OnTransactionCompleted(this);

            if (errors.Count > 0)
            {
                throw new AggregateException("One or more compensation actions failed", errors);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // If transaction is still active, roll it back
            if (State == TransactionState.Active)
            {
                await RollbackAsync();
            }

            _timeoutCts.Dispose();
        }
    }

    /// <summary>
    /// Extensions for using transactions.
    /// </summary>
    public static class TransactionExtensions
    {
        /// <summary>
        /// Executes an action within a transaction scope.
        /// </summary>
        public static async Task<T> ExecuteInTransactionAsync<T>(
            this ITransactionManager manager,
            Func<ITransactionScope, Task<T>> action,
            TransactionOptions? options = null,
            CancellationToken ct = default)
        {
            await using var scope = manager.BeginTransaction(options);

            try
            {
                var result = await action(scope);
                await scope.CommitAsync(ct);
                return result;
            }
            catch
            {
                await scope.RollbackAsync(ct);
                throw;
            }
        }

        /// <summary>
        /// Executes an action within a transaction scope.
        /// </summary>
        public static async Task ExecuteInTransactionAsync(
            this ITransactionManager manager,
            Func<ITransactionScope, Task> action,
            TransactionOptions? options = null,
            CancellationToken ct = default)
        {
            await using var scope = manager.BeginTransaction(options);

            try
            {
                await action(scope);
                await scope.CommitAsync(ct);
            }
            catch
            {
                await scope.RollbackAsync(ct);
                throw;
            }
        }
    }
}
