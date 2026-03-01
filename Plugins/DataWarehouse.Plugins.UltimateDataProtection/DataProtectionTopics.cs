namespace DataWarehouse.Plugins.UltimateDataProtection
{
    /// <summary>
    /// Message bus topics for data protection operations.
    /// Provides standardized topic names for backup, restore, and recovery messaging.
    /// </summary>
    public static class DataProtectionTopics
    {
        /// <summary>Topic prefix for all data protection messages.</summary>
        public const string Prefix = "dataprotection";

        #region Backup Topics

        /// <summary>Request to start a backup operation.</summary>
        public const string BackupRequest = $"{Prefix}.backup.request";

        /// <summary>Response to a backup request.</summary>
        public const string BackupResponse = $"{Prefix}.backup.response";

        /// <summary>Backup progress update.</summary>
        public const string BackupProgress = $"{Prefix}.backup.progress";

        /// <summary>Backup completed successfully.</summary>
        public const string BackupCompleted = $"{Prefix}.backup.completed";

        /// <summary>Backup failed.</summary>
        public const string BackupFailed = $"{Prefix}.backup.failed";

        /// <summary>Backup cancelled.</summary>
        public const string BackupCancelled = $"{Prefix}.backup.cancelled";

        #endregion

        #region Restore Topics

        /// <summary>Request to start a restore operation.</summary>
        public const string RestoreRequest = $"{Prefix}.restore.request";

        /// <summary>Response to a restore request.</summary>
        public const string RestoreResponse = $"{Prefix}.restore.response";

        /// <summary>Restore progress update.</summary>
        public const string RestoreProgress = $"{Prefix}.restore.progress";

        /// <summary>Restore completed successfully.</summary>
        public const string RestoreCompleted = $"{Prefix}.restore.completed";

        /// <summary>Restore failed.</summary>
        public const string RestoreFailed = $"{Prefix}.restore.failed";

        /// <summary>Restore cancelled.</summary>
        public const string RestoreCancelled = $"{Prefix}.restore.cancelled";

        #endregion

        #region Catalog Topics

        /// <summary>Request to list backups.</summary>
        public const string CatalogList = $"{Prefix}.catalog.list";

        /// <summary>Response with backup list.</summary>
        public const string CatalogListResponse = $"{Prefix}.catalog.list.response";

        /// <summary>Request backup information.</summary>
        public const string CatalogInfo = $"{Prefix}.catalog.info";

        /// <summary>Response with backup information.</summary>
        public const string CatalogInfoResponse = $"{Prefix}.catalog.info.response";

        /// <summary>Catalog updated notification.</summary>
        public const string CatalogUpdated = $"{Prefix}.catalog.updated";

        #endregion

        #region Validation Topics

        /// <summary>Request backup validation.</summary>
        public const string ValidationRequest = $"{Prefix}.validation.request";

        /// <summary>Validation result.</summary>
        public const string ValidationResponse = $"{Prefix}.validation.response";

        /// <summary>Validation completed notification.</summary>
        public const string ValidationCompleted = $"{Prefix}.validation.completed";

        #endregion

        #region Retention Topics

        /// <summary>Retention policy enforcement started.</summary>
        public const string RetentionStarted = $"{Prefix}.retention.started";

        /// <summary>Retention policy enforcement completed.</summary>
        public const string RetentionCompleted = $"{Prefix}.retention.completed";

        /// <summary>Backup expired and marked for deletion.</summary>
        public const string BackupExpired = $"{Prefix}.retention.expired";

        #endregion

        #region CDP Topics

        /// <summary>CDP journal entry captured.</summary>
        public const string CdpJournalEntry = $"{Prefix}.cdp.journal.entry";

        /// <summary>CDP replication event.</summary>
        public const string CdpReplication = $"{Prefix}.cdp.replication";

        /// <summary>CDP checkpoint created.</summary>
        public const string CdpCheckpoint = $"{Prefix}.cdp.checkpoint";

        #endregion

        #region Disaster Recovery Topics

        /// <summary>DR failover initiated.</summary>
        public const string DrFailoverStarted = $"{Prefix}.dr.failover.started";

        /// <summary>DR failover completed.</summary>
        public const string DrFailoverCompleted = $"{Prefix}.dr.failover.completed";

        /// <summary>DR failback initiated.</summary>
        public const string DrFailbackStarted = $"{Prefix}.dr.failback.started";

        /// <summary>DR failback completed.</summary>
        public const string DrFailbackCompleted = $"{Prefix}.dr.failback.completed";

        /// <summary>DR health check.</summary>
        public const string DrHealthCheck = $"{Prefix}.dr.health";

        #endregion

        #region Intelligence Topics

        /// <summary>Request optimal backup strategy recommendation.</summary>
        public const string IntelligenceRecommendation = $"{Prefix}.intelligence.recommend";

        /// <summary>Intelligence recommendation response.</summary>
        public const string IntelligenceRecommendationResponse = $"{Prefix}.intelligence.recommend.response";

        /// <summary>Request anomaly detection on backup data.</summary>
        public const string IntelligenceAnomalyDetection = $"{Prefix}.intelligence.anomaly";

        /// <summary>Anomaly detection response.</summary>
        public const string IntelligenceAnomalyResponse = $"{Prefix}.intelligence.anomaly.response";

        /// <summary>Request optimal recovery point recommendation.</summary>
        public const string IntelligenceRecoveryPoint = $"{Prefix}.intelligence.recovery.point";

        /// <summary>Recovery point recommendation response.</summary>
        public const string IntelligenceRecoveryPointResponse = $"{Prefix}.intelligence.recovery.point.response";

        #endregion

        #region Cross-Cloud Topics

        /// <summary>Request to upload a backup to a cloud provider.</summary>
        public const string CrossCloudUpload = $"{Prefix}.crosscloud.upload";

        /// <summary>Upload to cloud provider completed.</summary>
        public const string CrossCloudUploadCompleted = $"{Prefix}.crosscloud.upload.completed";

        #endregion

        #region Metrics Topics

        /// <summary>Backup metrics update.</summary>
        public const string MetricsBackup = $"{Prefix}.metrics.backup";

        /// <summary>Restore metrics update.</summary>
        public const string MetricsRestore = $"{Prefix}.metrics.restore";

        /// <summary>Storage consumption metrics.</summary>
        public const string MetricsStorage = $"{Prefix}.metrics.storage";

        /// <summary>Overall protection status.</summary>
        public const string MetricsStatus = $"{Prefix}.metrics.status";

        #endregion
    }
}
