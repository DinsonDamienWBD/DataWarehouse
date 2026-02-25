using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateEncryption.CryptoAgility;

/// <summary>
/// Processes batches of objects for cryptographic algorithm migration, re-encrypting from
/// the source algorithm to the target algorithm. Uses bounded channels for backpressure,
/// configurable concurrency via semaphore, and automatic rollback signaling when the
/// failure threshold is exceeded.
/// </summary>
/// <remarks>
/// <para>
/// The worker processes <see cref="MigrationBatch"/> items from a bounded channel:
/// </para>
/// <list type="bullet">
///   <item>Each batch contains object IDs to migrate between algorithms</item>
///   <item>Objects are decrypted with the source algorithm and re-encrypted with the target</item>
///   <item>If <see cref="MigrationOptions.UseDoubleEncryption"/> is true, creates
///     <see cref="DoubleEncryptionEnvelope"/> instead of direct re-encryption</item>
///   <item>Progress is published to "encryption.migration.progress" every N objects</item>
///   <item>If the failure rate exceeds <see cref="MigrationOptions.RollbackOnFailureThreshold"/>,
///     processing stops and a rollback signal is emitted</item>
/// </list>
/// <para>
/// The channel is bounded (capacity 100) with <see cref="BoundedChannelFullMode.Wait"/>
/// to provide backpressure when the worker falls behind batch production.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto agility engine")]
public sealed class MigrationWorker : IDisposable
{
    private readonly MigrationPlan _plan;
    private readonly DoubleEncryptionService _doubleEncryptionService;
    private readonly IMessageBus _messageBus;
    private readonly MigrationOptions _options;

    /// <summary>
    /// Bounded channel providing backpressure for batch processing.
    /// </summary>
    private readonly Channel<MigrationBatch> _batchChannel;

    /// <summary>
    /// Limits concurrent re-encryption operations within a batch.
    /// </summary>
    private readonly SemaphoreSlim _concurrencyLimiter;

    /// <summary>
    /// Number of objects processed between progress publications.
    /// </summary>
    private const int ProgressReportInterval = 100;

    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;
    private volatile bool _paused;
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private long _successCount;
    private long _failureCount;
    private long _totalProcessed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationWorker"/> class.
    /// </summary>
    /// <param name="plan">The migration plan being executed.</param>
    /// <param name="doubleEncryptionService">Service for double-encryption operations.</param>
    /// <param name="messageBus">Message bus for delegating encryption and publishing progress.</param>
    /// <param name="options">Migration configuration options.</param>
    /// <exception cref="ArgumentNullException">If any parameter is null.</exception>
    public MigrationWorker(
        MigrationPlan plan,
        DoubleEncryptionService doubleEncryptionService,
        IMessageBus messageBus,
        MigrationOptions options)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _doubleEncryptionService = doubleEncryptionService ?? throw new ArgumentNullException(nameof(doubleEncryptionService));
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _batchChannel = Channel.CreateBounded<MigrationBatch>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _concurrencyLimiter = new SemaphoreSlim(
            _options.MaxConcurrentMigrations,
            _options.MaxConcurrentMigrations);
    }

    /// <summary>
    /// Starts processing batches from the channel. Launches a background task that
    /// continuously reads and processes batches until cancellation.
    /// </summary>
    /// <param name="ct">Cancellation token that stops processing when cancelled.</param>
    /// <returns>A task that completes when the worker has started (not when processing finishes).</returns>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _paused = false;
        _processingTask = Task.Run(() => ProcessBatchesAsync(_processingCts.Token), _processingCts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues a batch of objects for migration processing.
    /// If the channel is full (100 pending batches), this call will wait until space is available.
    /// </summary>
    /// <param name="batch">The migration batch to process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the batch has been enqueued.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="batch"/> is null.</exception>
    public async Task EnqueueBatchAsync(MigrationBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _batchChannel.Writer.WriteAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pauses batch processing. The worker stops reading from the channel but does not
    /// discard pending batches. Already in-progress operations complete before pausing.
    /// </summary>
    /// <returns>A task that completes when the pause signal has been set.</returns>
    public Task PauseAsync()
    {
        _paused = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes batch processing after a pause. If the worker was not paused, this is a no-op.
    /// </summary>
    /// <returns>A task that completes when processing has resumed.</returns>
    public Task ResumeAsync()
    {
        _paused = false;

        // Release the pause gate if it was held
        if (_pauseGate.CurrentCount == 0)
        {
            try
            {
                _pauseGate.Release();
            }
            catch (SemaphoreFullException ex)
            {

                // Already released, no-op
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current progress of the migration worker.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MigrationStatus"/> reflecting current counts and progress.</returns>
    public Task<MigrationStatus> GetProgressAsync(CancellationToken ct = default)
    {
        var total = _plan.ObjectsTotal;
        var migrated = Interlocked.Read(ref _successCount);
        var failed = Interlocked.Read(ref _failureCount);
        var progress = total > 0 ? (double)migrated / total : 0.0;

        var status = new MigrationStatus
        {
            PlanId = _plan.PlanId,
            Phase = _plan.Phase,
            Progress = progress,
            ObjectsTotal = total,
            ObjectsMigrated = migrated,
            ObjectsFailed = failed,
            CurrentBatchSize = _options.BatchSize,
            IsRollbackAvailable = _plan.Phase is not MigrationPhase.Complete
                and not MigrationPhase.Failed
                and not MigrationPhase.RolledBack
        };

        return Task.FromResult(status);
    }

    /// <summary>
    /// Main batch processing loop. Reads batches from the channel and processes each
    /// object, tracking success/failure counts and publishing progress.
    /// </summary>
    private async Task ProcessBatchesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var batch in _batchChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Check for pause
                while (_paused && !ct.IsCancellationRequested)
                {
                    await _pauseGate.WaitAsync(ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();

                await ProcessSingleBatchAsync(batch, ct).ConfigureAwait(false);

                // Check failure threshold
                var totalProcessed = Interlocked.Read(ref _totalProcessed);
                var failures = Interlocked.Read(ref _failureCount);

                if (totalProcessed > 0 && (double)failures / totalProcessed > _options.RollbackOnFailureThreshold)
                {
                    // Signal rollback - failure threshold exceeded
                    await PublishRollbackSignalAsync(
                        $"Failure rate {(double)failures / totalProcessed:P2} exceeds threshold {_options.RollbackOnFailureThreshold:P2}",
                        ct).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal cancellation, do not propagate
        }
        catch (ChannelClosedException ex)
        {

            // Channel was completed, no more batches
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes a single batch of objects, re-encrypting each from source to target algorithm.
    /// Uses a semaphore to limit concurrency within the batch.
    /// </summary>
    private async Task ProcessSingleBatchAsync(MigrationBatch batch, CancellationToken ct)
    {
        var tasks = new List<Task>(batch.ObjectIds.Length);

        foreach (var objectId in batch.ObjectIds)
        {
            await _concurrencyLimiter.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await MigrateObjectAsync(objectId, batch.SourceAlgorithm, batch.TargetAlgorithm, ct)
                        .ConfigureAwait(false);

                    Interlocked.Increment(ref _successCount);
                    _plan.ObjectsMigrated = Interlocked.Read(ref _successCount);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _failureCount);
                    _plan.ObjectsFailed = Interlocked.Read(ref _failureCount);
                }
                finally
                {
                    Interlocked.Increment(ref _totalProcessed);
                    _concurrencyLimiter.Release();
                }

                // Publish progress at configured interval
                var processed = Interlocked.Read(ref _totalProcessed);
                if (processed % ProgressReportInterval == 0)
                {
                    await PublishProgressAsync(ct).ConfigureAwait(false);
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Migrates a single object from source to target algorithm. If double-encryption is enabled,
    /// creates a DoubleEncryptionEnvelope; otherwise performs direct re-encryption.
    /// </summary>
    private async Task MigrateObjectAsync(
        Guid objectId,
        string sourceAlgorithm,
        string targetAlgorithm,
        CancellationToken ct)
    {
        if (_options.UseDoubleEncryption)
        {
            // Retrieve existing ciphertext via bus
            var existingData = await RetrieveObjectDataAsync(objectId, sourceAlgorithm, ct).ConfigureAwait(false);

            // Decrypt with source algorithm
            byte[] plaintext;
            try
            {
                var decryptResponse = await _messageBus.SendAsync("encryption.decrypt", new PluginMessage
                {
                    Type = "encryption.decrypt",
                    Source = nameof(MigrationWorker),
                    Payload = new Dictionary<string, object>
                    {
                        ["strategyId"] = sourceAlgorithm,
                        ["data"] = existingData
                    }
                }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

                plaintext = ExtractResultBytes(decryptResponse, "encryption.decrypt")
                    ?? throw new InvalidOperationException($"Decrypt returned no data for object {objectId}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to decrypt object '{objectId}' with source algorithm '{sourceAlgorithm}'.", ex);
            }

            try
            {
                // Create double-encryption envelope
                await _doubleEncryptionService.EncryptAsync(
                    objectId, plaintext, sourceAlgorithm, targetAlgorithm, ct).ConfigureAwait(false);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        else
        {
            // Direct re-encryption via bus
            await _messageBus.PublishAsync("encryption.reencrypt", new PluginMessage
            {
                Type = "encryption.reencrypt",
                Source = nameof(MigrationWorker),
                Payload = new Dictionary<string, object>
                {
                    ["objectId"] = objectId.ToString(),
                    ["oldStrategyId"] = sourceAlgorithm,
                    ["newStrategyId"] = targetAlgorithm
                }
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Retrieves existing encrypted data for an object via the message bus.
    /// </summary>
    private async Task<byte[]> RetrieveObjectDataAsync(Guid objectId, string algorithmId, CancellationToken ct)
    {
        var response = await _messageBus.SendAsync("encryption.retrieve", new PluginMessage
        {
            Type = "encryption.retrieve",
            Source = nameof(MigrationWorker),
            Payload = new Dictionary<string, object>
            {
                ["objectId"] = objectId.ToString(),
                ["algorithmId"] = algorithmId
            }
        }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        return ExtractResultBytes(response, "encryption.retrieve")
            ?? throw new InvalidOperationException($"Failed to retrieve data for object '{objectId}'.");
    }

    /// <summary>
    /// Extracts a byte[] result from a <see cref="MessageResponse"/>, checking both
    /// the direct payload and dictionary payload patterns.
    /// </summary>
    private static byte[]? ExtractResultBytes(MessageResponse response, string operation)
    {
        if (!response.Success)
        {
            return null;
        }

        // Direct byte array payload
        if (response.Payload is byte[] directBytes)
        {
            return directBytes;
        }

        // Dictionary payload with "result" key
        if (response.Payload is Dictionary<string, object> dict
            && dict.TryGetValue("result", out var resultObj)
            && resultObj is byte[] resultBytes)
        {
            return resultBytes;
        }

        return null;
    }

    /// <summary>
    /// Publishes migration progress to the message bus.
    /// </summary>
    private async Task PublishProgressAsync(CancellationToken ct)
    {
        try
        {
            var migrated = Interlocked.Read(ref _successCount);
            var failed = Interlocked.Read(ref _failureCount);
            var total = _plan.ObjectsTotal;
            var progress = total > 0 ? (double)migrated / total : 0.0;

            await _messageBus.PublishAsync("encryption.migration.progress", new PluginMessage
            {
                Type = "encryption.migration.progress",
                Source = nameof(MigrationWorker),
                Payload = new Dictionary<string, object>
                {
                    ["planId"] = _plan.PlanId,
                    ["objectsMigrated"] = migrated,
                    ["objectsFailed"] = failed,
                    ["objectsTotal"] = total,
                    ["progress"] = progress,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                }
            }, ct).ConfigureAwait(false);
        }
        catch
        {

            // Progress reporting is best-effort; do not fail the migration
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// Publishes a rollback signal when the failure threshold is exceeded.
    /// </summary>
    private async Task PublishRollbackSignalAsync(string reason, CancellationToken ct)
    {
        try
        {
            await _messageBus.PublishAsync("encryption.migration.failed", new PluginMessage
            {
                Type = "encryption.migration.failed",
                Source = nameof(MigrationWorker),
                Payload = new Dictionary<string, object>
                {
                    ["planId"] = _plan.PlanId,
                    ["reason"] = reason,
                    ["action"] = "rollback",
                    ["objectsMigrated"] = Interlocked.Read(ref _successCount),
                    ["objectsFailed"] = Interlocked.Read(ref _failureCount)
                }
            }, ct).ConfigureAwait(false);
        }
        catch
        {

            // Best-effort notification
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// Disposes all managed resources including the channel, semaphores,
    /// and cancellation token sources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal the channel to complete
        _batchChannel.Writer.TryComplete();

        // Cancel processing
        if (_processingCts != null)
        {
            _processingCts.Cancel();
            _processingCts.Dispose();
            _processingCts = null;
        }

        _concurrencyLimiter.Dispose();
        _pauseGate.Dispose();
    }
}
