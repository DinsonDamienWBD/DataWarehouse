using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateEncryption.CryptoAgility;

/// <summary>
/// Production crypto-agility engine that orchestrates zero-downtime migration from classical
/// to post-quantum cryptographic algorithms. Manages the full migration lifecycle including
/// assessment, double-encryption, verification, cut-over, and cleanup phases.
/// </summary>
/// <remarks>
/// <para>
/// The engine coordinates three key operations during migration:
/// </para>
/// <list type="bullet">
///   <item>Double-encryption of new writes using both source and target algorithms</item>
///   <item>Batch re-encryption of existing objects via MigrationWorker</item>
///   <item>Rollback support at any phase before cut-over completion</item>
/// </list>
/// <para>
/// Bus topics published: encryption.migration.started, encryption.migration.completed,
/// encryption.migration.failed, encryption.migration.progress, encryption.migration.rolledback,
/// encryption.migration.resumed.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto agility engine")]
public sealed class CryptoAgilityEngine : CryptoAgilityEngineBase, IDisposable
{
    /// <summary>
    /// Active migration workers indexed by plan ID.
    /// </summary>
    private readonly BoundedDictionary<string, MigrationWorker> _workers = new BoundedDictionary<string, MigrationWorker>(1000);

    /// <summary>
    /// Double-encryption services indexed by plan ID.
    /// </summary>
    private readonly BoundedDictionary<string, DoubleEncryptionService> _doubleEncryptionServices = new BoundedDictionary<string, DoubleEncryptionService>(1000);

    /// <summary>
    /// Cancellation token sources for each migration, enabling pause and cancel.
    /// </summary>
    private readonly BoundedDictionary<string, CancellationTokenSource> _cancellationSources = new BoundedDictionary<string, CancellationTokenSource>(1000);

    /// <summary>
    /// Semaphore limiting the total number of concurrent migrations across all plans.
    /// </summary>
    private readonly SemaphoreSlim _migrationConcurrencyLimiter = new(4, 4);

    /// <summary>
    /// Tracks the phase a migration was in before being paused, for correct resume.
    /// </summary>
    private readonly BoundedDictionary<string, MigrationPhase> _pausedPhases = new BoundedDictionary<string, MigrationPhase>(1000);

    private bool _disposed;

    /// <inheritdoc/>
    public override string Id => "encryption.crypto-agility";

    /// <inheritdoc/>
    public override string Name => "Crypto-Agility Engine";

    /// <inheritdoc/>
    public override string Version => "5.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.SecurityProvider;

    /// <summary>
    /// Starts executing a migration plan. Transitions through Assessment and into
    /// DoubleEncryption phase, creating the required services and workers.
    /// </summary>
    /// <param name="planId">The migration plan identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current migration status after starting.</returns>
    /// <exception cref="InvalidOperationException">If the plan does not exist or is not in a startable state.</exception>
    public override async Task<MigrationStatus> StartMigrationAsync(string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        if (!MigrationPlans.TryGetValue(planId, out var plan))
        {
            throw new KeyNotFoundException($"Migration plan '{planId}' not found.");
        }

        if (plan.Phase is not MigrationPhase.NotStarted and not MigrationPhase.RolledBack)
        {
            throw new InvalidOperationException(
                $"Cannot start migration in phase '{plan.Phase}'. Plan must be in NotStarted or RolledBack state.");
        }

        await _migrationConcurrencyLimiter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Phase 1: Assessment - validate algorithms are available
            plan.Phase = MigrationPhase.Assessment;
            plan.StartedAt = DateTimeOffset.UtcNow;

            var sourceProfile = ResolveAlgorithmProfile(plan.SourceAlgorithm.AlgorithmId);
            var targetProfile = ResolveAlgorithmProfile(plan.TargetAlgorithm.AlgorithmId);

            // Create cancellation source for this migration
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cancellationSources[planId] = cts;

            // Phase 2: DoubleEncryption - create services for dual-algorithm writes
            plan.Phase = MigrationPhase.DoubleEncryption;

            var doubleEncryptionService = new DoubleEncryptionService(MessageBus!);
            _doubleEncryptionServices[planId] = doubleEncryptionService;

            // Create migration worker for batch processing existing objects
            var options = new MigrationOptions
            {
                UseDoubleEncryption = plan.UseDoubleEncryption,
                MaxConcurrentMigrations = plan.MaxConcurrentMigrations,
                RollbackOnFailureThreshold = plan.RollbackOnFailureThreshold
            };

            var worker = new MigrationWorker(plan, doubleEncryptionService, MessageBus!, options);
            _workers[planId] = worker;

            // Start the worker
            await worker.StartAsync(cts.Token).ConfigureAwait(false);

            // Publish migration started event
            await PublishMigrationEventAsync("encryption.migration.started", plan, ct).ConfigureAwait(false);
        }
        catch
        {
            _migrationConcurrencyLimiter.Release();
            throw;
        }

        return await GetMigrationStatusAsync(planId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a previously paused migration from where it left off.
    /// </summary>
    /// <param name="planId">The migration plan identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If the plan is not paused.</exception>
    public override async Task ResumeMigrationAsync(string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        if (!MigrationPlans.TryGetValue(planId, out var plan))
        {
            throw new KeyNotFoundException($"Migration plan '{planId}' not found.");
        }

        if (!_pausedPhases.TryRemove(planId, out var previousPhase))
        {
            throw new InvalidOperationException(
                $"Migration plan '{planId}' is not paused. Current phase: '{plan.Phase}'.");
        }

        // Restore the phase the migration was in before pausing
        plan.Phase = previousPhase;

        // Create a new CTS for the resumed migration
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_cancellationSources.TryGetValue(planId, out var oldCts))
        {
            oldCts.Dispose();
        }
        _cancellationSources[planId] = cts;

        // Resume the worker if it exists
        if (_workers.TryGetValue(planId, out var worker))
        {
            await worker.ResumeAsync().ConfigureAwait(false);
        }

        await PublishMigrationEventAsync("encryption.migration.resumed", plan, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pauses an active migration. Stores the current phase for resume.
    /// </summary>
    /// <param name="planId">The migration plan identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public override async Task PauseMigrationAsync(string planId, CancellationToken ct = default)
    {
        await base.PauseMigrationAsync(planId, ct).ConfigureAwait(false);

        if (!MigrationPlans.TryGetValue(planId, out var plan))
        {
            return;
        }

        // Remember the current phase before pausing
        _pausedPhases[planId] = plan.Phase;

        // Cancel the worker's token to stop processing
        if (_cancellationSources.TryGetValue(planId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        // Pause the worker
        if (_workers.TryGetValue(planId, out var worker))
        {
            await worker.PauseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rolls back a migration to the source algorithm, reversing any changes made.
    /// Stops the migration worker, removes double-encryption for new writes,
    /// and re-encrypts any migrated objects back to the source algorithm.
    /// </summary>
    /// <param name="planId">The migration plan identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If rollback is not available.</exception>
    public override async Task RollbackMigrationAsync(string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        if (!MigrationPlans.TryGetValue(planId, out var plan))
        {
            throw new KeyNotFoundException($"Migration plan '{planId}' not found.");
        }

        if (plan.Phase is MigrationPhase.Complete or MigrationPhase.RolledBack or MigrationPhase.Failed)
        {
            throw new InvalidOperationException(
                $"Cannot rollback migration in phase '{plan.Phase}'. " +
                "Rollback is only available for active or paused migrations.");
        }

        // Step 1: Stop the migration worker
        if (_cancellationSources.TryRemove(planId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (_workers.TryRemove(planId, out var worker))
        {
            await worker.PauseAsync().ConfigureAwait(false);
            worker.Dispose();
        }

        // Step 2: Remove double-encryption service for new writes
        if (_doubleEncryptionServices.TryRemove(planId, out var doubleEncService))
        {
            // Issue re-encryption commands for any migrated objects back to source algorithm
            if (plan.ObjectsMigrated > 0 && MessageBus != null)
            {
                await MessageBus.PublishAsync("encryption.reencrypt.batch", new PluginMessage
                {
                    Type = "encryption.reencrypt.batch",
                    SourcePluginId = Id,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["planId"] = planId,
                        ["sourceAlgorithm"] = plan.TargetAlgorithm.AlgorithmId,
                        ["targetAlgorithm"] = plan.SourceAlgorithm.AlgorithmId,
                        ["direction"] = "rollback",
                        ["objectCount"] = plan.ObjectsMigrated
                    }
                }, ct).ConfigureAwait(false);
            }
        }

        // Step 3: Update plan state
        plan.Phase = MigrationPhase.RolledBack;
        plan.CompletedAt = DateTimeOffset.UtcNow;

        // Remove paused state if any
        _pausedPhases.TryRemove(planId, out _);

        // Release concurrency slot
        _migrationConcurrencyLimiter.Release();

        // Publish rollback event
        await PublishMigrationEventAsync("encryption.migration.rolledback", plan, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Encrypts data with both the primary and secondary algorithms simultaneously,
    /// producing a <see cref="DoubleEncryptionEnvelope"/> that can be decrypted by either.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object being encrypted.</param>
    /// <param name="plaintext">The data to encrypt.</param>
    /// <param name="primaryAlgorithmId">Primary (current) algorithm identifier.</param>
    /// <param name="secondaryAlgorithmId">Secondary (target) algorithm identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A double-encryption envelope containing ciphertext from both algorithms.</returns>
    public override async Task<DoubleEncryptionEnvelope> DoubleEncryptAsync(
        Guid objectId,
        byte[] plaintext,
        string primaryAlgorithmId,
        string secondaryAlgorithmId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryAlgorithmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondaryAlgorithmId);

        // Find or create a DoubleEncryptionService for this operation
        var service = _doubleEncryptionServices.Values.FirstOrDefault()
            ?? new DoubleEncryptionService(MessageBus!);

        return await service.EncryptAsync(objectId, plaintext, primaryAlgorithmId, secondaryAlgorithmId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Decrypts data from a double-encryption envelope using the preferred algorithm.
    /// Falls back to the other algorithm if the preferred one fails.
    /// </summary>
    /// <param name="envelope">The double-encryption envelope.</param>
    /// <param name="preferredAlgorithmId">The preferred algorithm to try first.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decrypted plaintext.</returns>
    public override async Task<byte[]> DecryptFromEnvelopeAsync(
        DoubleEncryptionEnvelope envelope,
        string preferredAlgorithmId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredAlgorithmId);

        var service = _doubleEncryptionServices.Values.FirstOrDefault()
            ?? new DoubleEncryptionService(MessageBus!);

        return await service.DecryptAsync(envelope, preferredAlgorithmId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a migration that has passed verification, transitioning through
    /// CutOver and Cleanup phases to the Complete state.
    /// </summary>
    /// <param name="planId">The migration plan identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If the plan is not in a completable phase.</exception>
    public async Task CompleteMigrationAsync(string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        if (!MigrationPlans.TryGetValue(planId, out var plan))
        {
            throw new KeyNotFoundException($"Migration plan '{planId}' not found.");
        }

        if (plan.Phase is not MigrationPhase.Verification and not MigrationPhase.DoubleEncryption)
        {
            throw new InvalidOperationException(
                $"Cannot complete migration in phase '{plan.Phase}'. " +
                "Plan must be in Verification or DoubleEncryption phase.");
        }

        // CutOver: switch primary decryption to target algorithm
        plan.Phase = MigrationPhase.CutOver;

        if (MessageBus != null)
        {
            await MessageBus.PublishAsync("encryption.migration.cutover", new PluginMessage
            {
                Type = "encryption.migration.cutover",
                SourcePluginId = Id,
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["planId"] = planId,
                    ["targetAlgorithm"] = plan.TargetAlgorithm.AlgorithmId,
                    ["sourceAlgorithm"] = plan.SourceAlgorithm.AlgorithmId
                }
            }, ct).ConfigureAwait(false);
        }

        // Cleanup: remove source algorithm ciphertext from double-encrypted objects
        plan.Phase = MigrationPhase.Cleanup;

        if (_doubleEncryptionServices.TryRemove(planId, out var doubleEncService) && MessageBus != null)
        {
            await MessageBus.PublishAsync("encryption.migration.cleanup", new PluginMessage
            {
                Type = "encryption.migration.cleanup",
                SourcePluginId = Id,
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["planId"] = planId,
                    ["algorithmToRemove"] = plan.SourceAlgorithm.AlgorithmId,
                    ["algorithmToKeep"] = plan.TargetAlgorithm.AlgorithmId
                }
            }, ct).ConfigureAwait(false);
        }

        // Stop and dispose the worker
        if (_workers.TryRemove(planId, out var worker))
        {
            worker.Dispose();
        }

        if (_cancellationSources.TryRemove(planId, out var cts))
        {
            cts.Dispose();
        }

        _pausedPhases.TryRemove(planId, out _);

        // Mark complete
        plan.Phase = MigrationPhase.Complete;
        plan.CompletedAt = DateTimeOffset.UtcNow;

        // Release concurrency slot
        _migrationConcurrencyLimiter.Release();

        // Publish completion event
        await PublishMigrationEventAsync("encryption.migration.completed", plan, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles migration failure signaled by a worker (e.g., failure threshold exceeded).
    /// </summary>
    /// <param name="planId">The migration plan identifier.</param>
    /// <param name="reason">Failure reason.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task HandleMigrationFailureAsync(string planId, string reason, CancellationToken ct = default)
    {
        if (!MigrationPlans.TryGetValue(planId, out var plan))
        {
            return;
        }

        plan.Phase = MigrationPhase.Failed;
        plan.CompletedAt = DateTimeOffset.UtcNow;

        // Clean up resources
        if (_workers.TryRemove(planId, out var worker))
        {
            worker.Dispose();
        }

        if (_cancellationSources.TryRemove(planId, out var cts))
        {
            cts.Dispose();
        }

        if (_doubleEncryptionServices.TryRemove(planId, out _)) { }
        _pausedPhases.TryRemove(planId, out _);

        _migrationConcurrencyLimiter.Release();

        if (MessageBus != null)
        {
            await MessageBus.PublishAsync("encryption.migration.failed", new PluginMessage
            {
                Type = "encryption.migration.failed",
                SourcePluginId = Id,
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["planId"] = planId,
                    ["sourceAlgorithm"] = plan.SourceAlgorithm.AlgorithmId,
                    ["targetAlgorithm"] = plan.TargetAlgorithm.AlgorithmId,
                    ["reason"] = reason,
                    ["objectsMigrated"] = plan.ObjectsMigrated,
                    ["objectsFailed"] = plan.ObjectsFailed
                }
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes a migration lifecycle event to the message bus.
    /// </summary>
    private async Task PublishMigrationEventAsync(string topic, MigrationPlan plan, CancellationToken ct)
    {
        if (MessageBus == null)
        {
            return;
        }

        await MessageBus.PublishAsync(topic, new PluginMessage
        {
            Type = topic,
            SourcePluginId = Id,
            Source = Id,
            Payload = new Dictionary<string, object>
            {
                ["planId"] = plan.PlanId,
                ["sourceAlgorithm"] = plan.SourceAlgorithm.AlgorithmId,
                ["targetAlgorithm"] = plan.TargetAlgorithm.AlgorithmId,
                ["phase"] = plan.Phase.ToString(),
                ["objectsTotal"] = plan.ObjectsTotal,
                ["objectsMigrated"] = plan.ObjectsMigrated,
                ["objectsFailed"] = plan.ObjectsFailed,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ActiveWorkers"] = _workers.Count;
        metadata["ActiveDoubleEncryptionServices"] = _doubleEncryptionServices.Count;
        metadata["PausedMigrations"] = _pausedPhases.Count;
        return metadata;
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes all managed resources including workers, cancellation sources,
    /// and the concurrency limiter.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(); false if from finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            // Dispose all workers
            foreach (var kvp in _workers)
            {
                kvp.Value.Dispose();
            }
            _workers.Clear();

            // Dispose all cancellation sources
            foreach (var kvp in _cancellationSources)
            {
                kvp.Value.Dispose();
            }
            _cancellationSources.Clear();

            _doubleEncryptionServices.Clear();
            _pausedPhases.Clear();
            _migrationConcurrencyLimiter.Dispose();
        }

        base.Dispose(disposing);
    }
}
