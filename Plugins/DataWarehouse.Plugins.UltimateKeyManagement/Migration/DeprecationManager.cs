using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Migration
{
    /// <summary>
    /// F3: Deprecation Manager for managing the deprecation lifecycle of individual
    /// key management plugins and providing clear migration paths.
    ///
    /// Features:
    /// - Mark old plugins as deprecated with configurable warnings
    /// - Provide migration path information in deprecation messages
    /// - Version-based deprecation timeline tracking
    /// - Runtime deprecation warning emission
    /// - Deprecation audit logging
    /// </summary>
    public sealed class DeprecationManager : IDisposable
    {
        private static readonly Lazy<DeprecationManager> _instance = new(() => new DeprecationManager());
        private readonly ConcurrentDictionary<string, DeprecationInfo> _deprecatedItems = new();
        private readonly ConcurrentBag<DeprecationWarning> _emittedWarnings = new();
        private readonly DeprecationManagerOptions _options;
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>
        /// Gets the singleton instance of the DeprecationManager.
        /// </summary>
        public static DeprecationManager Instance => _instance.Value;

        /// <summary>
        /// Event raised when a deprecation warning is emitted.
        /// </summary>
        public event EventHandler<DeprecationWarningEventArgs>? WarningEmitted;

        public DeprecationManager() : this(new DeprecationManagerOptions()) { }

        public DeprecationManager(DeprecationManagerOptions options)
        {
            _options = options;
            RegisterBuiltInDeprecations();
        }

        #region Registration

        /// <summary>
        /// Registers all built-in deprecations for individual key management plugins.
        /// </summary>
        private void RegisterBuiltInDeprecations()
        {
            // FileKeyStorePlugin deprecation
            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "datawarehouse.plugins.keystore.file",
                ItemType = DeprecatedItemType.Plugin,
                ItemName = "FileKeyStorePlugin",
                FullTypeName = "DataWarehouse.Plugins.FileKeyStore.FileKeyStorePlugin",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                DeprecationDate = new DateTime(2025, 1, 1),
                RemovalDate = new DateTime(2026, 1, 1),
                MigrationTarget = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy",
                MigrationGuideUrl = "https://docs.datawarehouse.io/migration/ultimate-key-management",
                Reason = "Consolidated into UltimateKeyManagement plugin for unified key management",
                MigrationSteps = new List<string>
                {
                    "1. Install DataWarehouse.Plugins.UltimateKeyManagement package",
                    "2. Replace FileKeyStorePlugin reference with UltimateKeyManagementPlugin",
                    "3. Use FileKeyStoreStrategy instead of FileKeyStorePlugin",
                    "4. Migrate configuration using ConfigurationMigrator.MigrateFileKeyStoreConfig()",
                    "5. Update DI registration: services.AddSingleton<IKeyStore, FileKeyStoreStrategy>()",
                    "6. Test key retrieval and creation operations"
                }
            });

            // VaultKeyStorePlugin deprecation
            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "datawarehouse.plugins.keystore.vault",
                ItemType = DeprecatedItemType.Plugin,
                ItemName = "VaultKeyStorePlugin",
                FullTypeName = "DataWarehouse.Plugins.VaultKeyStore.VaultKeyStorePlugin",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                DeprecationDate = new DateTime(2025, 1, 1),
                RemovalDate = new DateTime(2026, 1, 1),
                MigrationTarget = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy",
                MigrationGuideUrl = "https://docs.datawarehouse.io/migration/ultimate-key-management",
                Reason = "Consolidated into UltimateKeyManagement plugin with expanded vault support",
                MigrationSteps = new List<string>
                {
                    "1. Install DataWarehouse.Plugins.UltimateKeyManagement package",
                    "2. Replace VaultKeyStorePlugin reference with UltimateKeyManagementPlugin",
                    "3. Use VaultKeyStoreStrategy (HashiCorp), AzureKeyVaultStrategy, or AwsKmsStrategy",
                    "4. Migrate configuration using ConfigurationMigrator.MigrateVaultKeyStoreConfig()",
                    "5. Update DI registration to use the appropriate strategy",
                    "6. Test envelope encryption operations if using HSM features"
                }
            });

            // KeyRotationPlugin deprecation
            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "datawarehouse.plugins.security.keyrotation",
                ItemType = DeprecatedItemType.Plugin,
                ItemName = "KeyRotationPlugin",
                FullTypeName = "DataWarehouse.Plugins.KeyRotation.KeyRotationPlugin",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                DeprecationDate = new DateTime(2025, 1, 1),
                RemovalDate = new DateTime(2026, 1, 1),
                MigrationTarget = "DataWarehouse.Plugins.UltimateKeyManagement.KeyRotationScheduler",
                MigrationGuideUrl = "https://docs.datawarehouse.io/migration/ultimate-key-management",
                Reason = "Key rotation is now built into UltimateKeyManagement with enhanced features",
                MigrationSteps = new List<string>
                {
                    "1. Install DataWarehouse.Plugins.UltimateKeyManagement package",
                    "2. Remove KeyRotationPlugin reference",
                    "3. Configure rotation via UltimateKeyManagementConfig.DefaultRotationPolicy",
                    "4. Set EnableKeyRotation = true in configuration",
                    "5. Migrate rotation schedules using ConfigurationMigrator",
                    "6. Test scheduled rotation and re-encryption flows"
                }
            });

            // Deprecated configuration types
            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "config.FileKeyStoreConfig",
                ItemType = DeprecatedItemType.ConfigurationType,
                ItemName = "FileKeyStoreConfig (standalone)",
                FullTypeName = "DataWarehouse.Plugins.FileKeyStore.FileKeyStoreConfig",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                MigrationTarget = "UltimateKeyManagementConfig.StrategyConfigurations",
                Reason = "Configuration consolidated into UltimateKeyManagementConfig"
            });

            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "config.VaultConfig",
                ItemType = DeprecatedItemType.ConfigurationType,
                ItemName = "VaultConfig (standalone)",
                FullTypeName = "DataWarehouse.Plugins.VaultKeyStore.VaultConfig",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                MigrationTarget = "UltimateKeyManagementConfig.StrategyConfigurations",
                Reason = "Configuration consolidated into UltimateKeyManagementConfig"
            });

            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "config.KeyRotationConfig",
                ItemType = DeprecatedItemType.ConfigurationType,
                ItemName = "KeyRotationConfig (standalone)",
                FullTypeName = "DataWarehouse.Plugins.KeyRotation.KeyRotationConfig",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                MigrationTarget = "UltimateKeyManagementConfig.DefaultRotationPolicy",
                Reason = "Rotation configuration integrated into UltimateKeyManagementConfig"
            });

            // Deprecated namespaces
            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "namespace.FileKeyStore",
                ItemType = DeprecatedItemType.Namespace,
                ItemName = "DataWarehouse.Plugins.FileKeyStore",
                FullTypeName = "DataWarehouse.Plugins.FileKeyStore",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                MigrationTarget = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local",
                Reason = "Namespace consolidated into UltimateKeyManagement"
            });

            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "namespace.VaultKeyStore",
                ItemType = DeprecatedItemType.Namespace,
                ItemName = "DataWarehouse.Plugins.VaultKeyStore",
                FullTypeName = "DataWarehouse.Plugins.VaultKeyStore",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                MigrationTarget = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement",
                Reason = "Namespace consolidated into UltimateKeyManagement"
            });

            RegisterDeprecation(new DeprecationInfo
            {
                ItemId = "namespace.KeyRotation",
                ItemType = DeprecatedItemType.Namespace,
                ItemName = "DataWarehouse.Plugins.KeyRotation",
                FullTypeName = "DataWarehouse.Plugins.KeyRotation",
                DeprecatedInVersion = new Version(2, 0, 0),
                RemovedInVersion = new Version(3, 0, 0),
                MigrationTarget = "DataWarehouse.Plugins.UltimateKeyManagement",
                Reason = "Namespace consolidated into UltimateKeyManagement"
            });
        }

        /// <summary>
        /// Registers a deprecation for a specific item.
        /// </summary>
        public void RegisterDeprecation(DeprecationInfo info)
        {
            _deprecatedItems[info.ItemId] = info;
        }

        /// <summary>
        /// Unregisters a deprecation (useful for testing).
        /// </summary>
        public bool UnregisterDeprecation(string itemId)
        {
            return _deprecatedItems.TryRemove(itemId, out _);
        }

        #endregion

        #region Warning Emission

        /// <summary>
        /// Checks if an item is deprecated and emits a warning if appropriate.
        /// </summary>
        /// <param name="itemId">The item identifier to check.</param>
        /// <param name="callerFilePath">Caller file path (auto-populated).</param>
        /// <param name="callerLineNumber">Caller line number (auto-populated).</param>
        /// <param name="callerMemberName">Caller member name (auto-populated).</param>
        /// <returns>True if the item is deprecated.</returns>
        public bool CheckAndWarn(
            string itemId,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0,
            [CallerMemberName] string callerMemberName = "")
        {
            if (!_deprecatedItems.TryGetValue(itemId, out var info))
                return false;

            EmitWarning(info, callerFilePath, callerLineNumber, callerMemberName);
            return true;
        }

        /// <summary>
        /// Checks if a type is deprecated and emits a warning if appropriate.
        /// </summary>
        public bool CheckTypeAndWarn(
            Type type,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0,
            [CallerMemberName] string callerMemberName = "")
        {
            var fullName = type.FullName ?? type.Name;

            var info = _deprecatedItems.Values.FirstOrDefault(d =>
                d.FullTypeName.Equals(fullName, StringComparison.OrdinalIgnoreCase) ||
                d.ItemName.Equals(type.Name, StringComparison.OrdinalIgnoreCase));

            if (info == null)
                return false;

            EmitWarning(info, callerFilePath, callerLineNumber, callerMemberName);
            return true;
        }

        /// <summary>
        /// Emits a deprecation warning for the specified item.
        /// </summary>
        private void EmitWarning(DeprecationInfo info, string callerFilePath, int callerLineNumber, string callerMemberName)
        {
            // Check if we should suppress duplicate warnings
            var warningKey = $"{info.ItemId}:{callerFilePath}:{callerLineNumber}";
            if (_options.SuppressDuplicateWarnings)
            {
                if (_emittedWarnings.Any(w => w.WarningKey == warningKey))
                    return;
            }

            var warning = new DeprecationWarning
            {
                WarningKey = warningKey,
                ItemId = info.ItemId,
                ItemName = info.ItemName,
                ItemType = info.ItemType,
                CallerFilePath = callerFilePath,
                CallerLineNumber = callerLineNumber,
                CallerMemberName = callerMemberName,
                EmittedAt = DateTime.UtcNow,
                Message = FormatWarningMessage(info),
                MigrationTarget = info.MigrationTarget,
                MigrationGuideUrl = info.MigrationGuideUrl
            };

            _emittedWarnings.Add(warning);

            // Emit to configured outputs
            if (_options.EmitToConsole)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[DEPRECATION WARNING] {warning.Message}");
                Console.ResetColor();
            }

            if (_options.EmitToTrace)
            {
                Trace.TraceWarning($"[DEPRECATION] {warning.Message}");
            }

            if (_options.EmitToDebug)
            {
                Debug.WriteLine($"[DEPRECATION] {warning.Message}");
            }

            // Raise event
            WarningEmitted?.Invoke(this, new DeprecationWarningEventArgs(warning));

            // Throw if configured to treat warnings as errors
            if (_options.TreatWarningsAsErrors)
            {
                throw new DeprecationException(warning.Message, info);
            }
        }

        /// <summary>
        /// Formats a deprecation warning message.
        /// </summary>
        private static string FormatWarningMessage(DeprecationInfo info)
        {
            var message = $"'{info.ItemName}' is deprecated";

            if (info.DeprecatedInVersion != null)
                message += $" as of version {info.DeprecatedInVersion}";

            if (info.RemovedInVersion != null)
                message += $" and will be removed in version {info.RemovedInVersion}";

            if (!string.IsNullOrEmpty(info.Reason))
                message += $". Reason: {info.Reason}";

            if (!string.IsNullOrEmpty(info.MigrationTarget))
                message += $". Migrate to: {info.MigrationTarget}";

            if (!string.IsNullOrEmpty(info.MigrationGuideUrl))
                message += $". Guide: {info.MigrationGuideUrl}";

            return message;
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Gets deprecation information for a specific item.
        /// </summary>
        public DeprecationInfo? GetDeprecationInfo(string itemId)
        {
            return _deprecatedItems.TryGetValue(itemId, out var info) ? info : null;
        }

        /// <summary>
        /// Gets all registered deprecations.
        /// </summary>
        public IReadOnlyList<DeprecationInfo> GetAllDeprecations()
        {
            return _deprecatedItems.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets deprecations by type.
        /// </summary>
        public IReadOnlyList<DeprecationInfo> GetDeprecationsByType(DeprecatedItemType itemType)
        {
            return _deprecatedItems.Values
                .Where(d => d.ItemType == itemType)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets all emitted warnings.
        /// </summary>
        public IReadOnlyList<DeprecationWarning> GetEmittedWarnings()
        {
            return _emittedWarnings.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets a deprecation timeline summary.
        /// </summary>
        public DeprecationTimeline GetTimeline()
        {
            var now = DateTime.UtcNow;

            return new DeprecationTimeline
            {
                CurrentlyDeprecated = _deprecatedItems.Values
                    .Where(d => d.DeprecationDate <= now && (d.RemovalDate == null || d.RemovalDate > now))
                    .OrderBy(d => d.RemovalDate)
                    .ToList(),
                UpcomingRemovals = _deprecatedItems.Values
                    .Where(d => d.RemovalDate != null && d.RemovalDate > now && d.RemovalDate <= now.AddMonths(6))
                    .OrderBy(d => d.RemovalDate)
                    .ToList(),
                AlreadyRemoved = _deprecatedItems.Values
                    .Where(d => d.RemovalDate != null && d.RemovalDate <= now)
                    .OrderByDescending(d => d.RemovalDate)
                    .ToList()
            };
        }

        /// <summary>
        /// Checks if any deprecated items are scheduled for removal within the specified timeframe.
        /// </summary>
        public IReadOnlyList<DeprecationInfo> GetItemsScheduledForRemoval(TimeSpan within)
        {
            var deadline = DateTime.UtcNow.Add(within);

            return _deprecatedItems.Values
                .Where(d => d.RemovalDate != null && d.RemovalDate <= deadline)
                .OrderBy(d => d.RemovalDate)
                .ToList()
                .AsReadOnly();
        }

        #endregion

        #region Migration Helpers

        /// <summary>
        /// Gets the migration steps for a deprecated item.
        /// </summary>
        public IReadOnlyList<string> GetMigrationSteps(string itemId)
        {
            if (_deprecatedItems.TryGetValue(itemId, out var info))
            {
                return info.MigrationSteps.AsReadOnly();
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Generates a migration report for all deprecated items in use.
        /// </summary>
        public DeprecationReport GenerateReport()
        {
            var report = new DeprecationReport
            {
                GeneratedAt = DateTime.UtcNow,
                TotalDeprecatedItems = _deprecatedItems.Count,
                WarningsEmitted = _emittedWarnings.Count,
                UniqueItemsWarned = _emittedWarnings.Select(w => w.ItemId).Distinct().Count()
            };

            foreach (var (itemId, info) in _deprecatedItems)
            {
                var warningsForItem = _emittedWarnings.Where(w => w.ItemId == itemId).ToList();

                report.Items.Add(new DeprecationReportItem
                {
                    ItemId = itemId,
                    ItemName = info.ItemName,
                    ItemType = info.ItemType,
                    DeprecatedInVersion = info.DeprecatedInVersion?.ToString() ?? "Unknown",
                    RemovedInVersion = info.RemovedInVersion?.ToString() ?? "TBD",
                    MigrationTarget = info.MigrationTarget,
                    WarningCount = warningsForItem.Count,
                    Locations = warningsForItem
                        .Select(w => $"{w.CallerFilePath}:{w.CallerLineNumber}")
                        .Distinct()
                        .ToList(),
                    MigrationSteps = info.MigrationSteps
                });
            }

            return report;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _deprecatedItems.Clear();

            GC.SuppressFinalize(this);
        }
    }

    #region Data Models

    public sealed class DeprecationManagerOptions
    {
        public bool EmitToConsole { get; set; } = true;
        public bool EmitToTrace { get; set; } = true;
        public bool EmitToDebug { get; set; } = true;
        public bool SuppressDuplicateWarnings { get; set; } = true;
        public bool TreatWarningsAsErrors { get; set; } = false;
    }

    public sealed class DeprecationInfo
    {
        public string ItemId { get; init; } = "";
        public DeprecatedItemType ItemType { get; init; }
        public string ItemName { get; init; } = "";
        public string FullTypeName { get; init; } = "";
        public Version? DeprecatedInVersion { get; init; }
        public Version? RemovedInVersion { get; init; }
        public DateTime? DeprecationDate { get; init; }
        public DateTime? RemovalDate { get; init; }
        public string MigrationTarget { get; init; } = "";
        public string? MigrationGuideUrl { get; init; }
        public string Reason { get; init; } = "";
        public List<string> MigrationSteps { get; init; } = new();
    }

    public sealed class DeprecationWarning
    {
        public string WarningKey { get; init; } = "";
        public string ItemId { get; init; } = "";
        public string ItemName { get; init; } = "";
        public DeprecatedItemType ItemType { get; init; }
        public string CallerFilePath { get; init; } = "";
        public int CallerLineNumber { get; init; }
        public string CallerMemberName { get; init; } = "";
        public DateTime EmittedAt { get; init; }
        public string Message { get; init; } = "";
        public string MigrationTarget { get; init; } = "";
        public string? MigrationGuideUrl { get; init; }
    }

    public sealed class DeprecationWarningEventArgs : EventArgs
    {
        public DeprecationWarning Warning { get; }

        public DeprecationWarningEventArgs(DeprecationWarning warning)
        {
            Warning = warning;
        }
    }

    public sealed class DeprecationTimeline
    {
        public List<DeprecationInfo> CurrentlyDeprecated { get; init; } = new();
        public List<DeprecationInfo> UpcomingRemovals { get; init; } = new();
        public List<DeprecationInfo> AlreadyRemoved { get; init; } = new();
    }

    public sealed class DeprecationReport
    {
        public DateTime GeneratedAt { get; init; }
        public int TotalDeprecatedItems { get; init; }
        public int WarningsEmitted { get; init; }
        public int UniqueItemsWarned { get; init; }
        public List<DeprecationReportItem> Items { get; init; } = new();
    }

    public sealed class DeprecationReportItem
    {
        public string ItemId { get; init; } = "";
        public string ItemName { get; init; } = "";
        public DeprecatedItemType ItemType { get; init; }
        public string DeprecatedInVersion { get; init; } = "";
        public string RemovedInVersion { get; init; } = "";
        public string MigrationTarget { get; init; } = "";
        public int WarningCount { get; init; }
        public List<string> Locations { get; init; } = new();
        public List<string> MigrationSteps { get; init; } = new();
    }

    public enum DeprecatedItemType
    {
        Plugin,
        Namespace,
        ConfigurationType,
        Method,
        Property,
        Interface,
        Class
    }

    public sealed class DeprecationException : Exception
    {
        public DeprecationInfo DeprecationInfo { get; }

        public DeprecationException(string message, DeprecationInfo info) : base(message)
        {
            DeprecationInfo = info;
        }
    }

    #endregion
}
