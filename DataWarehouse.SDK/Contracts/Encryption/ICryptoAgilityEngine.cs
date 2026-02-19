using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Encryption
{
    /// <summary>
    /// Options for configuring a cryptographic algorithm migration plan.
    /// Controls double-encryption behavior, parallelism, and failure thresholds.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public record MigrationOptions
    {
        /// <summary>
        /// Whether to use double-encryption during the transition period.
        /// When enabled, objects are encrypted with both the source and target algorithms,
        /// allowing either to decrypt until the cutover completes.
        /// </summary>
        public bool UseDoubleEncryption { get; init; } = true;

        /// <summary>
        /// Maximum number of concurrent migration operations.
        /// Higher values increase throughput but consume more resources.
        /// </summary>
        public int MaxConcurrentMigrations { get; init; } = 4;

        /// <summary>
        /// Failure threshold (0.0-1.0) at which the migration automatically rolls back.
        /// For example, 0.05 means rollback if more than 5% of objects fail.
        /// </summary>
        public double RollbackOnFailureThreshold { get; init; } = 0.05;

        /// <summary>
        /// Number of objects to process in each migration batch.
        /// </summary>
        public int BatchSize { get; init; } = 100;

        /// <summary>
        /// Duration for which double-encrypted objects remain in dual-ciphertext state
        /// before the cutover must complete.
        /// </summary>
        public TimeSpan DoubleEncryptionDuration { get; init; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Whether to send a notification when the migration completes.
        /// </summary>
        public bool NotifyOnCompletion { get; init; } = true;
    }

    /// <summary>
    /// Defines the contract for a crypto-agility engine that manages automated migration
    /// from classical to post-quantum cryptographic algorithms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The crypto-agility engine provides:
    /// </para>
    /// <list type="bullet">
    ///   <item>Migration plan creation and lifecycle management</item>
    ///   <item>Double-encryption for zero-downtime algorithm transitions</item>
    ///   <item>Batch processing with configurable parallelism</item>
    ///   <item>Automatic rollback on failure threshold breach</item>
    ///   <item>Algorithm deprecation tracking and enforcement</item>
    /// </list>
    /// <para>
    /// Migration lifecycle: NotStarted -> Assessment -> DoubleEncryption -> Verification -> CutOver -> Cleanup -> Complete.
    /// At any point before CutOver, the migration can be rolled back to the source algorithm.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public interface ICryptoAgilityEngine : IPlugin
    {
        /// <summary>
        /// Creates a migration plan for transitioning from one algorithm to another.
        /// </summary>
        /// <param name="sourceAlgorithmId">The current algorithm protecting the data.</param>
        /// <param name="targetAlgorithmId">The target algorithm to migrate to.</param>
        /// <param name="options">Optional migration configuration. Uses defaults if null.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created migration plan.</returns>
        /// <exception cref="ArgumentException">If source or target algorithm is unknown or target is deprecated.</exception>
        Task<MigrationPlan> CreateMigrationPlanAsync(
            string sourceAlgorithmId,
            string targetAlgorithmId,
            MigrationOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Begins executing a migration plan. Transitions the plan from NotStarted to Assessment.
        /// </summary>
        /// <param name="planId">The migration plan identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current migration status after starting.</returns>
        /// <exception cref="InvalidOperationException">If the plan does not exist or is not in NotStarted state.</exception>
        Task<MigrationStatus> StartMigrationAsync(string planId, CancellationToken ct = default);

        /// <summary>
        /// Gets the current status of a migration plan including progress and error details.
        /// </summary>
        /// <param name="planId">The migration plan identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current migration status.</returns>
        /// <exception cref="KeyNotFoundException">If the plan does not exist.</exception>
        Task<MigrationStatus> GetMigrationStatusAsync(string planId, CancellationToken ct = default);

        /// <summary>
        /// Pauses an active migration. Can be resumed later with <see cref="ResumeMigrationAsync"/>.
        /// </summary>
        /// <param name="planId">The migration plan identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">If the plan is not in an active phase.</exception>
        Task PauseMigrationAsync(string planId, CancellationToken ct = default);

        /// <summary>
        /// Resumes a previously paused migration from where it left off.
        /// </summary>
        /// <param name="planId">The migration plan identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">If the plan is not paused.</exception>
        Task ResumeMigrationAsync(string planId, CancellationToken ct = default);

        /// <summary>
        /// Rolls back a migration to the source algorithm, reversing any changes made.
        /// Only available before the CutOver phase completes.
        /// </summary>
        /// <param name="planId">The migration plan identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">If rollback is not available for the current phase.</exception>
        Task RollbackMigrationAsync(string planId, CancellationToken ct = default);

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
        Task<DoubleEncryptionEnvelope> DoubleEncryptAsync(
            Guid objectId,
            byte[] plaintext,
            string primaryAlgorithmId,
            string secondaryAlgorithmId,
            CancellationToken ct = default);

        /// <summary>
        /// Decrypts data from a double-encryption envelope using the preferred algorithm.
        /// Falls back to the other algorithm if the preferred one fails.
        /// </summary>
        /// <param name="envelope">The double-encryption envelope.</param>
        /// <param name="preferredAlgorithmId">The preferred algorithm to try first.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The decrypted plaintext.</returns>
        Task<byte[]> DecryptFromEnvelopeAsync(
            DoubleEncryptionEnvelope envelope,
            string preferredAlgorithmId,
            CancellationToken ct = default);

        /// <summary>
        /// Gets all algorithms that are marked as deprecated and should be migrated away from.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of deprecated algorithm profiles.</returns>
        Task<IReadOnlyList<AlgorithmProfile>> GetDeprecatedAlgorithmsAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets all currently active (in-progress) migration plans.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of active migration plans.</returns>
        Task<IReadOnlyList<MigrationPlan>> GetActiveMigrationsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Abstract base class for crypto-agility engine implementations.
    /// Provides concrete plan management, status tracking, and deprecated algorithm queries.
    /// Leaves migration execution, double-encryption, and rollback as abstract methods
    /// that require access to encryption strategy instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This base class manages:
    /// </para>
    /// <list type="bullet">
    ///   <item>Thread-safe migration plan storage via ConcurrentDictionary</item>
    ///   <item>Plan creation with validation against PqcAlgorithmRegistry</item>
    ///   <item>Status reporting and active migration queries</item>
    ///   <item>Intelligence-aware capability declaration (Security/CryptoAgility)</item>
    /// </list>
    /// <para>
    /// Derived classes must implement: StartMigrationAsync, ResumeMigrationAsync,
    /// RollbackMigrationAsync, DoubleEncryptAsync, DecryptFromEnvelopeAsync.
    /// These require access to actual encryption strategy instances for crypto operations.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public abstract class CryptoAgilityEngineBase : FeaturePluginBase, ICryptoAgilityEngine, IIntelligenceAware
    {
        /// <summary>
        /// Thread-safe storage for migration plans indexed by plan ID.
        /// </summary>
        protected readonly ConcurrentDictionary<string, MigrationPlan> MigrationPlans = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Known algorithm profiles from sources beyond PqcAlgorithmRegistry
        /// (e.g., classical algorithms registered at runtime).
        /// </summary>
        protected readonly ConcurrentDictionary<string, AlgorithmProfile> KnownAlgorithms = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc/>
        public override string FeatureCategory => "Security";

        /// <summary>
        /// Declares capabilities for the Intelligence-aware capability registry.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = "security.crypto-agility",
                DisplayName = "Crypto-Agility Engine",
                Description = "Automated PQC migration with zero-downtime double-encryption transitions",
                Category = CapabilityCategory.Security,
                SubCategory = "CryptoAgility",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version
            }
        };

        /// <inheritdoc/>
        public virtual Task<MigrationPlan> CreateMigrationPlanAsync(
            string sourceAlgorithmId,
            string targetAlgorithmId,
            MigrationOptions? options = null,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceAlgorithmId);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetAlgorithmId);

            var sourceProfile = ResolveAlgorithmProfile(sourceAlgorithmId);
            var targetProfile = ResolveAlgorithmProfile(targetAlgorithmId);

            if (targetProfile.IsDeprecated)
            {
                throw new ArgumentException(
                    $"Target algorithm '{targetAlgorithmId}' is deprecated and cannot be used as a migration target.",
                    nameof(targetAlgorithmId));
            }

            options ??= new MigrationOptions();

            var plan = new MigrationPlan
            {
                PlanId = $"mig-{Guid.NewGuid():N}",
                SourceAlgorithm = sourceProfile,
                TargetAlgorithm = targetProfile,
                Phase = MigrationPhase.NotStarted,
                UseDoubleEncryption = options.UseDoubleEncryption,
                RollbackOnFailureThreshold = options.RollbackOnFailureThreshold,
                MaxConcurrentMigrations = options.MaxConcurrentMigrations
            };

            if (!MigrationPlans.TryAdd(plan.PlanId, plan))
            {
                throw new InvalidOperationException("Failed to create migration plan due to ID collision.");
            }

            return Task.FromResult(plan);
        }

        /// <inheritdoc/>
        public virtual Task<MigrationStatus> GetMigrationStatusAsync(string planId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(planId);

            if (!MigrationPlans.TryGetValue(planId, out var plan))
            {
                throw new KeyNotFoundException($"Migration plan '{planId}' not found.");
            }

            var total = plan.ObjectsTotal;
            var progress = total > 0 ? (double)plan.ObjectsMigrated / total : 0.0;
            var isRollbackAvailable = plan.Phase is not MigrationPhase.Complete
                and not MigrationPhase.Failed
                and not MigrationPhase.RolledBack
                and not MigrationPhase.Cleanup;

            var status = new MigrationStatus
            {
                PlanId = plan.PlanId,
                Phase = plan.Phase,
                Progress = progress,
                ObjectsTotal = plan.ObjectsTotal,
                ObjectsMigrated = plan.ObjectsMigrated,
                ObjectsFailed = plan.ObjectsFailed,
                CurrentBatchSize = plan.MaxConcurrentMigrations,
                IsRollbackAvailable = isRollbackAvailable
            };

            return Task.FromResult(status);
        }

        /// <inheritdoc/>
        public virtual Task PauseMigrationAsync(string planId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(planId);

            if (!MigrationPlans.TryGetValue(planId, out var plan))
            {
                throw new KeyNotFoundException($"Migration plan '{planId}' not found.");
            }

            if (plan.Phase is MigrationPhase.Complete or MigrationPhase.Failed or MigrationPhase.RolledBack or MigrationPhase.NotStarted)
            {
                throw new InvalidOperationException(
                    $"Cannot pause migration in phase '{plan.Phase}'. Only active migrations can be paused.");
            }

            // Store current phase for resume; actual pause implementation in derived class
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public virtual Task<IReadOnlyList<AlgorithmProfile>> GetDeprecatedAlgorithmsAsync(CancellationToken ct = default)
        {
            var deprecated = new List<AlgorithmProfile>();

            // Check PQC registry
            foreach (var kvp in PqcAlgorithmRegistry.Algorithms)
            {
                if (kvp.Value.Profile.IsDeprecated)
                {
                    deprecated.Add(kvp.Value.Profile);
                }
            }

            // Check locally registered algorithms
            foreach (var kvp in KnownAlgorithms)
            {
                if (kvp.Value.IsDeprecated)
                {
                    deprecated.Add(kvp.Value);
                }
            }

            return Task.FromResult<IReadOnlyList<AlgorithmProfile>>(deprecated);
        }

        /// <inheritdoc/>
        public virtual Task<IReadOnlyList<MigrationPlan>> GetActiveMigrationsAsync(CancellationToken ct = default)
        {
            var active = MigrationPlans.Values
                .Where(p => p.Phase is not MigrationPhase.Complete
                    and not MigrationPhase.Failed
                    and not MigrationPhase.RolledBack
                    and not MigrationPhase.NotStarted)
                .ToList();

            return Task.FromResult<IReadOnlyList<MigrationPlan>>(active);
        }

        /// <inheritdoc/>
        public abstract Task<MigrationStatus> StartMigrationAsync(string planId, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task ResumeMigrationAsync(string planId, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task RollbackMigrationAsync(string planId, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<DoubleEncryptionEnvelope> DoubleEncryptAsync(
            Guid objectId,
            byte[] plaintext,
            string primaryAlgorithmId,
            string secondaryAlgorithmId,
            CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<byte[]> DecryptFromEnvelopeAsync(
            DoubleEncryptionEnvelope envelope,
            string preferredAlgorithmId,
            CancellationToken ct = default);

        /// <summary>
        /// Registers a classical or custom algorithm profile for use in migration plans.
        /// </summary>
        /// <param name="profile">The algorithm profile to register.</param>
        public void RegisterAlgorithm(AlgorithmProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            KnownAlgorithms[profile.AlgorithmId] = profile;
        }

        /// <summary>
        /// Resolves an algorithm profile by ID, checking the PQC registry first,
        /// then locally registered algorithms.
        /// </summary>
        /// <param name="algorithmId">The algorithm identifier.</param>
        /// <returns>The resolved algorithm profile.</returns>
        /// <exception cref="ArgumentException">If the algorithm is not found in any registry.</exception>
        protected AlgorithmProfile ResolveAlgorithmProfile(string algorithmId)
        {
            // Check PQC registry first
            if (PqcAlgorithmRegistry.Algorithms.TryGetValue(algorithmId, out var pqcInfo))
            {
                return pqcInfo.Profile;
            }

            // Check locally registered algorithms
            if (KnownAlgorithms.TryGetValue(algorithmId, out var profile))
            {
                return profile;
            }

            throw new ArgumentException(
                $"Algorithm '{algorithmId}' not found in PQC registry or known algorithms. " +
                "Register it using RegisterAlgorithm() before creating a migration plan.",
                nameof(algorithmId));
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ActiveMigrations"] = MigrationPlans.Values.Count(p =>
                p.Phase is not MigrationPhase.Complete
                    and not MigrationPhase.Failed
                    and not MigrationPhase.RolledBack
                    and not MigrationPhase.NotStarted);
            metadata["TotalMigrationPlans"] = MigrationPlans.Count;
            metadata["RegisteredAlgorithms"] = PqcAlgorithmRegistry.Algorithms.Count + KnownAlgorithms.Count;
            metadata["DeprecatedAlgorithms"] = PqcAlgorithmRegistry.Algorithms.Values.Count(a => a.Profile.IsDeprecated)
                + KnownAlgorithms.Values.Count(a => a.IsDeprecated);
            return metadata;
        }
    }
}
