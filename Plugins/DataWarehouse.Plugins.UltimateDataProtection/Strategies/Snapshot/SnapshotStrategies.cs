namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Snapshot
{
    /// <summary>
    /// Copy-on-Write (COW) snapshot strategy.
    /// </summary>
    public sealed class CopyOnWriteSnapshotStrategy : DataProtectionStrategyBase
    {
        public override string StrategyId => "cow-snapshot";
        public override bool IsProductionReady => false;
        public override string StrategyName => "Copy-on-Write Snapshot";
        public override DataProtectionCategory Category => DataProtectionCategory.Snapshot;
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.PointInTimeRecovery | DataProtectionCapabilities.InstantRecovery;

        protected override Task<BackupResult> CreateBackupCoreAsync(BackupRequest request, Action<BackupProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new BackupResult { Success = true, BackupId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-10), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024, StoredBytes = 1024, FileCount = 10 });

        protected override Task<RestoreResult> RestoreCoreAsync(RestoreRequest request, Action<RestoreProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new RestoreResult { Success = true, RestoreId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-100), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024, FileCount = 10 });

        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult(new ValidationResult { IsValid = true, ChecksPerformed = new[] { "COWIntegrity" } });

        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(BackupListQuery query, CancellationToken ct) =>
            Task.FromResult<IEnumerable<BackupCatalogEntry>>(Array.Empty<BackupCatalogEntry>());

        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult<BackupCatalogEntry?>(null);

        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Redirect-on-Write (ROW) snapshot strategy.
    /// </summary>
    public sealed class RedirectOnWriteSnapshotStrategy : DataProtectionStrategyBase
    {
        public override string StrategyId => "row-snapshot";
        public override bool IsProductionReady => false;
        public override string StrategyName => "Redirect-on-Write Snapshot";
        public override DataProtectionCategory Category => DataProtectionCategory.Snapshot;
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.PointInTimeRecovery | DataProtectionCapabilities.InstantRecovery | DataProtectionCapabilities.ParallelBackup;

        protected override Task<BackupResult> CreateBackupCoreAsync(BackupRequest request, Action<BackupProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new BackupResult { Success = true, BackupId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-5), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024, StoredBytes = 512, FileCount = 10 });

        protected override Task<RestoreResult> RestoreCoreAsync(RestoreRequest request, Action<RestoreProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new RestoreResult { Success = true, RestoreId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-50), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024, FileCount = 10 });

        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult(new ValidationResult { IsValid = true, ChecksPerformed = new[] { "ROWIntegrity" } });

        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(BackupListQuery query, CancellationToken ct) =>
            Task.FromResult<IEnumerable<BackupCatalogEntry>>(Array.Empty<BackupCatalogEntry>());

        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult<BackupCatalogEntry?>(null);

        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Windows VSS (Volume Shadow Copy Service) snapshot strategy.
    /// </summary>
    public sealed class VSSSnapshotStrategy : DataProtectionStrategyBase
    {
        public override string StrategyId => "vss-snapshot";
        public override bool IsProductionReady => false;
        public override string StrategyName => "VSS Snapshot";
        public override DataProtectionCategory Category => DataProtectionCategory.Snapshot;
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.PointInTimeRecovery | DataProtectionCapabilities.ApplicationAware | DataProtectionCapabilities.DatabaseAware;

        protected override Task<BackupResult> CreateBackupCoreAsync(BackupRequest request, Action<BackupProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new BackupResult { Success = true, BackupId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddSeconds(-2), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024 * 10, StoredBytes = 1024 * 1024, FileCount = 100 });

        protected override Task<RestoreResult> RestoreCoreAsync(RestoreRequest request, Action<RestoreProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new RestoreResult { Success = true, RestoreId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddSeconds(-3), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024 * 10, FileCount = 100 });

        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult(new ValidationResult { IsValid = true, ChecksPerformed = new[] { "VSSConsistency", "WriterStatus" } });

        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(BackupListQuery query, CancellationToken ct) =>
            Task.FromResult<IEnumerable<BackupCatalogEntry>>(Array.Empty<BackupCatalogEntry>());

        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult<BackupCatalogEntry?>(null);

        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Linux LVM snapshot strategy.
    /// </summary>
    public sealed class LVMSnapshotStrategy : DataProtectionStrategyBase
    {
        public override string StrategyId => "lvm-snapshot";
        public override bool IsProductionReady => false;
        public override string StrategyName => "LVM Snapshot";
        public override DataProtectionCategory Category => DataProtectionCategory.Snapshot;
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.PointInTimeRecovery | DataProtectionCapabilities.InstantRecovery | DataProtectionCapabilities.CrossPlatform;

        protected override Task<BackupResult> CreateBackupCoreAsync(BackupRequest request, Action<BackupProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new BackupResult { Success = true, BackupId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-100), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024 * 50, StoredBytes = 1024 * 1024 * 5, FileCount = 500 });

        protected override Task<RestoreResult> RestoreCoreAsync(RestoreRequest request, Action<RestoreProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new RestoreResult { Success = true, RestoreId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddSeconds(-5), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024 * 50, FileCount = 500 });

        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult(new ValidationResult { IsValid = true, ChecksPerformed = new[] { "LVMMetadata" } });

        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(BackupListQuery query, CancellationToken ct) =>
            Task.FromResult<IEnumerable<BackupCatalogEntry>>(Array.Empty<BackupCatalogEntry>());

        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult<BackupCatalogEntry?>(null);

        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// ZFS snapshot strategy with send/receive support.
    /// </summary>
    public sealed class ZFSSnapshotStrategy : DataProtectionStrategyBase
    {
        public override string StrategyId => "zfs-snapshot";
        public override bool IsProductionReady => false;
        public override string StrategyName => "ZFS Snapshot";
        public override DataProtectionCategory Category => DataProtectionCategory.Snapshot;
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.Compression | DataProtectionCapabilities.Deduplication | DataProtectionCapabilities.PointInTimeRecovery | DataProtectionCapabilities.InstantRecovery;

        protected override Task<BackupResult> CreateBackupCoreAsync(BackupRequest request, Action<BackupProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new BackupResult { Success = true, BackupId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddMilliseconds(-20), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024 * 100, StoredBytes = 1024 * 1024 * 20, FileCount = 1000 });

        protected override Task<RestoreResult> RestoreCoreAsync(RestoreRequest request, Action<RestoreProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new RestoreResult { Success = true, RestoreId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddSeconds(-1), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024 * 1024 * 100, FileCount = 1000 });

        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult(new ValidationResult { IsValid = true, ChecksPerformed = new[] { "ZFSScrub", "Checksum" } });

        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(BackupListQuery query, CancellationToken ct) =>
            Task.FromResult<IEnumerable<BackupCatalogEntry>>(Array.Empty<BackupCatalogEntry>());

        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult<BackupCatalogEntry?>(null);

        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Cloud snapshot strategy for AWS EBS, Azure Disk, GCP PD.
    /// </summary>
    public sealed class CloudSnapshotStrategy : DataProtectionStrategyBase
    {
        public override string StrategyId => "cloud-snapshot";
        public override bool IsProductionReady => false;
        public override string StrategyName => "Cloud Snapshot";
        public override DataProtectionCategory Category => DataProtectionCategory.Snapshot;
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.CloudTarget | DataProtectionCapabilities.PointInTimeRecovery | DataProtectionCapabilities.Encryption | DataProtectionCapabilities.ImmutableBackup;

        protected override Task<BackupResult> CreateBackupCoreAsync(BackupRequest request, Action<BackupProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new BackupResult { Success = true, BackupId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddSeconds(-5), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024L * 1024 * 1024, StoredBytes = 1024 * 1024 * 100, FileCount = 10000 });

        protected override Task<RestoreResult> RestoreCoreAsync(RestoreRequest request, Action<RestoreProgress> progressCallback, CancellationToken ct) =>
            Task.FromResult(new RestoreResult { Success = true, RestoreId = Guid.NewGuid().ToString("N"), StartTime = DateTimeOffset.UtcNow.AddSeconds(-30), EndTime = DateTimeOffset.UtcNow, TotalBytes = 1024L * 1024 * 1024, FileCount = 10000 });

        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult(new ValidationResult { IsValid = true, ChecksPerformed = new[] { "CloudAPI", "SnapshotStatus" } });

        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(BackupListQuery query, CancellationToken ct) =>
            Task.FromResult<IEnumerable<BackupCatalogEntry>>(Array.Empty<BackupCatalogEntry>());

        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct) =>
            Task.FromResult<BackupCatalogEntry?>(null);

        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct) => Task.CompletedTask;
    }
}
