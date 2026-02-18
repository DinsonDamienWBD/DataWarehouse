using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Migration
{
    /// <summary>
    /// F1: Plugin Migration Helper for transitioning from individual key management plugins
    /// to the unified UltimateKeyManagement plugin.
    ///
    /// Provides:
    /// - Automatic detection of old plugin references in code and configuration
    /// - Reference update suggestions with automated fix options
    /// - Compatibility shim for gradual migration
    /// - Migration progress tracking and reporting
    /// </summary>
    public sealed class PluginMigrationHelper : IDisposable
    {
        private readonly MigrationHelperConfig _config;
        private readonly ConcurrentDictionary<string, PluginReferenceInfo> _detectedReferences = new();
        private readonly ConcurrentBag<MigrationSuggestion> _suggestions = new();
        private readonly List<MigrationAction> _completedActions = new();
        private bool _disposed;

        /// <summary>
        /// Map of deprecated plugin IDs to their UltimateKeyManagement strategy equivalents.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> PluginMappings = new Dictionary<string, string>
        {
            // FileKeyStorePlugin -> FileKeyStoreStrategy
            ["datawarehouse.plugins.keystore.file"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy",
            ["DataWarehouse.Plugins.FileKeyStore.FileKeyStorePlugin"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy",

            // VaultKeyStorePlugin -> VaultKeyStoreStrategy
            ["datawarehouse.plugins.keystore.vault"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy",
            ["DataWarehouse.Plugins.VaultKeyStore.VaultKeyStorePlugin"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy",

            // KeyRotationPlugin -> Built-in KeyRotationScheduler
            ["datawarehouse.plugins.security.keyrotation"] = "com.datawarehouse.keymanagement.ultimate",
            ["DataWarehouse.Plugins.KeyRotation.KeyRotationPlugin"] = "com.datawarehouse.keymanagement.ultimate",

            // Common type name references
            ["FileKeyStorePlugin"] = "FileKeyStoreStrategy",
            ["VaultKeyStorePlugin"] = "VaultKeyStoreStrategy",
            ["KeyRotationPlugin"] = "UltimateKeyManagementPlugin"
        };

        /// <summary>
        /// Namespace migrations from old plugin namespaces to new strategy namespaces.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> NamespaceMappings = new Dictionary<string, string>
        {
            ["DataWarehouse.Plugins.FileKeyStore"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local",
            ["DataWarehouse.Plugins.VaultKeyStore"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement",
            ["DataWarehouse.Plugins.KeyRotation"] = "DataWarehouse.Plugins.UltimateKeyManagement"
        };

        /// <summary>
        /// Configuration type migrations.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> ConfigTypeMappings = new Dictionary<string, string>
        {
            ["FileKeyStoreConfig"] = "FileKeyStoreConfig",  // Same name, different namespace
            ["VaultConfig"] = "VaultKeyStoreConfig",
            ["HashiCorpVaultConfig"] = "VaultKeyStoreConfig", // Consolidated
            ["AzureKeyVaultConfig"] = "AzureKeyVaultStrategyConfig",
            ["AwsKmsConfig"] = "AwsKmsStrategyConfig",
            ["KeyRotationConfig"] = "KeyRotationPolicy"
        };

        public PluginMigrationHelper(MigrationHelperConfig? config = null)
        {
            _config = config ?? new MigrationHelperConfig();
        }

        #region Detection Methods

        /// <summary>
        /// Scans a directory for files containing references to deprecated plugins.
        /// </summary>
        /// <param name="rootDirectory">Root directory to scan.</param>
        /// <param name="filePatterns">File patterns to include (e.g., "*.cs", "*.json").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Migration scan result containing all detected references.</returns>
        public async Task<MigrationScanResult> ScanForDeprecatedReferencesAsync(
            string rootDirectory,
            IEnumerable<string>? filePatterns = null,
            CancellationToken cancellationToken = default)
        {
            var patterns = filePatterns?.ToList() ?? new List<string> { "*.cs", "*.json", "*.config", "*.xml", "*.csproj" };
            var scanResult = new MigrationScanResult
            {
                ScanStartedAt = DateTime.UtcNow,
                RootDirectory = rootDirectory
            };

            var files = new List<string>();
            foreach (var pattern in patterns)
            {
                files.AddRange(Directory.GetFiles(rootDirectory, pattern, SearchOption.AllDirectories));
            }

            scanResult.TotalFilesScanned = files.Count;

            var tasks = files.Select(file => ScanFileAsync(file, cancellationToken));
            var fileResults = await Task.WhenAll(tasks);

            foreach (var result in fileResults.Where(r => r.HasReferences))
            {
                scanResult.FilesWithReferences.Add(result);
            }

            scanResult.ScanCompletedAt = DateTime.UtcNow;
            scanResult.TotalReferencesFound = scanResult.FilesWithReferences.Sum(f => f.References.Count);

            // Generate suggestions based on detected references
            GenerateMigrationSuggestions(scanResult);

            return scanResult;
        }

        /// <summary>
        /// Scans a single file for deprecated plugin references.
        /// </summary>
        public async Task<FileScanResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var result = new FileScanResult { FilePath = filePath };

            try
            {
                var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                var lines = content.Split('\n');

                // Scan for plugin ID references
                foreach (var (oldId, newId) in PluginMappings)
                {
                    var pattern = Regex.Escape(oldId);
                    var matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);

                    foreach (Match match in matches)
                    {
                        var lineNumber = content[..match.Index].Count(c => c == '\n') + 1;
                        result.References.Add(new PluginReferenceInfo
                        {
                            FilePath = filePath,
                            LineNumber = lineNumber,
                            OldReference = oldId,
                            NewReference = newId,
                            ReferenceType = DetermineReferenceType(filePath, oldId),
                            Context = GetLineContext(lines, lineNumber - 1)
                        });
                    }
                }

                // Scan for namespace references
                foreach (var (oldNs, newNs) in NamespaceMappings)
                {
                    if (content.Contains(oldNs))
                    {
                        var pattern = $@"(using\s+)?{Regex.Escape(oldNs)}";
                        var matches = Regex.Matches(content, pattern);

                        foreach (Match match in matches)
                        {
                            var lineNumber = content[..match.Index].Count(c => c == '\n') + 1;
                            result.References.Add(new PluginReferenceInfo
                            {
                                FilePath = filePath,
                                LineNumber = lineNumber,
                                OldReference = oldNs,
                                NewReference = newNs,
                                ReferenceType = ReferenceType.NamespaceImport,
                                Context = GetLineContext(lines, lineNumber - 1)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.ScanError = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Scans loaded assemblies for references to deprecated plugins.
        /// </summary>
        public IReadOnlyList<AssemblyReferenceInfo> ScanLoadedAssemblies()
        {
            var results = new List<AssemblyReferenceInfo>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var referencedAssemblies = assembly.GetReferencedAssemblies();
                    foreach (var refAssembly in referencedAssemblies)
                    {
                        if (IsDeprecatedPluginAssembly(refAssembly.Name))
                        {
                            results.Add(new AssemblyReferenceInfo
                            {
                                ReferencingAssembly = assembly.GetName().Name ?? assembly.FullName ?? "Unknown",
                                ReferencedAssembly = refAssembly.Name ?? "Unknown",
                                Version = refAssembly.Version?.ToString() ?? "Unknown",
                                IsDeprecated = true,
                                MigrationTarget = GetMigrationTarget(refAssembly.Name)
                            });
                        }
                    }
                }
                catch
                {
                    // Skip assemblies that can't be inspected
                }
            }

            return results.AsReadOnly();
        }

        #endregion

        #region Suggestion Generation

        private void GenerateMigrationSuggestions(MigrationScanResult scanResult)
        {
            foreach (var fileResult in scanResult.FilesWithReferences)
            {
                var codeReferences = fileResult.References.Where(r => r.ReferenceType == ReferenceType.CodeReference).ToList();
                var configReferences = fileResult.References.Where(r => r.ReferenceType == ReferenceType.ConfigurationValue).ToList();
                var namespaceReferences = fileResult.References.Where(r => r.ReferenceType == ReferenceType.NamespaceImport).ToList();

                if (codeReferences.Count > 0)
                {
                    _suggestions.Add(new MigrationSuggestion
                    {
                        SuggestionId = Guid.NewGuid().ToString("N"),
                        FilePath = fileResult.FilePath,
                        SuggestionType = SuggestionType.UpdateCodeReferences,
                        Severity = MigrationSeverity.Warning,
                        Description = $"Found {codeReferences.Count} code reference(s) to deprecated plugins",
                        RecommendedAction = "Update code references to use UltimateKeyManagement strategies",
                        AffectedReferences = codeReferences,
                        AutoFixAvailable = true
                    });
                }

                if (configReferences.Count > 0)
                {
                    _suggestions.Add(new MigrationSuggestion
                    {
                        SuggestionId = Guid.NewGuid().ToString("N"),
                        FilePath = fileResult.FilePath,
                        SuggestionType = SuggestionType.UpdateConfiguration,
                        Severity = MigrationSeverity.Warning,
                        Description = $"Found {configReferences.Count} configuration reference(s) to deprecated plugins",
                        RecommendedAction = "Migrate configuration to UltimateKeyManagement format",
                        AffectedReferences = configReferences,
                        AutoFixAvailable = true
                    });
                }

                if (namespaceReferences.Count > 0)
                {
                    _suggestions.Add(new MigrationSuggestion
                    {
                        SuggestionId = Guid.NewGuid().ToString("N"),
                        FilePath = fileResult.FilePath,
                        SuggestionType = SuggestionType.UpdateNamespaces,
                        Severity = MigrationSeverity.Info,
                        Description = $"Found {namespaceReferences.Count} namespace import(s) that should be updated",
                        RecommendedAction = "Update using directives to new namespaces",
                        AffectedReferences = namespaceReferences,
                        AutoFixAvailable = true
                    });
                }
            }
        }

        /// <summary>
        /// Gets all generated migration suggestions.
        /// </summary>
        public IReadOnlyList<MigrationSuggestion> GetSuggestions()
        {
            return _suggestions.ToList().AsReadOnly();
        }

        #endregion

        #region Auto-Fix Methods

        /// <summary>
        /// Applies automatic fixes for a specific suggestion.
        /// </summary>
        /// <param name="suggestionId">The suggestion ID to apply.</param>
        /// <param name="createBackup">Whether to create a backup before modifying.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<MigrationActionResult> ApplyAutoFixAsync(
            string suggestionId,
            bool createBackup = true,
            CancellationToken cancellationToken = default)
        {
            var suggestion = _suggestions.FirstOrDefault(s => s.SuggestionId == suggestionId);
            if (suggestion == null)
            {
                return new MigrationActionResult
                {
                    Success = false,
                    ErrorMessage = $"Suggestion '{suggestionId}' not found"
                };
            }

            if (!suggestion.AutoFixAvailable)
            {
                return new MigrationActionResult
                {
                    Success = false,
                    ErrorMessage = "Auto-fix is not available for this suggestion"
                };
            }

            try
            {
                var filePath = suggestion.FilePath;

                if (createBackup)
                {
                    var backupPath = filePath + ".migration-backup";
                    File.Copy(filePath, backupPath, overwrite: true);
                }

                var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                var originalContent = content;

                foreach (var reference in suggestion.AffectedReferences)
                {
                    content = content.Replace(reference.OldReference, reference.NewReference);
                }

                if (content != originalContent)
                {
                    await File.WriteAllTextAsync(filePath, content, cancellationToken);
                }

                var action = new MigrationAction
                {
                    ActionId = Guid.NewGuid().ToString("N"),
                    SuggestionId = suggestionId,
                    ActionType = suggestion.SuggestionType.ToString(),
                    FilePath = filePath,
                    AppliedAt = DateTime.UtcNow,
                    ReplacementsCount = suggestion.AffectedReferences.Count
                };

                _completedActions.Add(action);

                return new MigrationActionResult
                {
                    Success = true,
                    Action = action,
                    FilePath = filePath,
                    ReplacementsApplied = suggestion.AffectedReferences.Count
                };
            }
            catch (Exception ex)
            {
                return new MigrationActionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Applies all available auto-fixes in a directory.
        /// </summary>
        public async Task<BatchMigrationResult> ApplyAllAutoFixesAsync(
            bool createBackups = true,
            CancellationToken cancellationToken = default)
        {
            var result = new BatchMigrationResult
            {
                StartedAt = DateTime.UtcNow
            };

            var fixableSuggestions = _suggestions.Where(s => s.AutoFixAvailable).ToList();
            result.TotalSuggestions = fixableSuggestions.Count;

            foreach (var suggestion in fixableSuggestions)
            {
                var actionResult = await ApplyAutoFixAsync(suggestion.SuggestionId, createBackups, cancellationToken);

                if (actionResult.Success)
                {
                    result.SuccessfulFixes++;
                    result.AppliedActions.Add(actionResult.Action!);
                }
                else
                {
                    result.FailedFixes++;
                    result.Errors.Add($"{suggestion.FilePath}: {actionResult.ErrorMessage}");
                }
            }

            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        #endregion

        #region Compatibility Shim

        /// <summary>
        /// Creates a compatibility shim that allows old plugin references to work
        /// with the new UltimateKeyManagement plugin during gradual migration.
        /// </summary>
        public CompatibilityShim CreateCompatibilityShim(UltimateKeyManagementPlugin ultimatePlugin)
        {
            return new CompatibilityShim(ultimatePlugin, _config);
        }

        #endregion

        #region Helper Methods

        private static ReferenceType DetermineReferenceType(string filePath, string reference)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch
            {
                ".json" or ".config" => ReferenceType.ConfigurationValue,
                ".csproj" or ".xml" => ReferenceType.ProjectReference,
                ".cs" when reference.Contains(".") => ReferenceType.CodeReference,
                _ => ReferenceType.Unknown
            };
        }

        private static string GetLineContext(string[] lines, int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= lines.Length)
                return "";

            var start = Math.Max(0, lineIndex - 1);
            var end = Math.Min(lines.Length - 1, lineIndex + 1);

            return string.Join("\n", lines[start..(end + 1)]).Trim();
        }

        private static bool IsDeprecatedPluginAssembly(string? assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
                return false;

            return assemblyName.Contains("FileKeyStore") ||
                   assemblyName.Contains("VaultKeyStore") ||
                   assemblyName.Contains("KeyRotation");
        }

        private static string GetMigrationTarget(string? assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
                return "DataWarehouse.Plugins.UltimateKeyManagement";

            if (assemblyName.Contains("FileKeyStore"))
                return "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy";
            if (assemblyName.Contains("VaultKeyStore"))
                return "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy";

            return "DataWarehouse.Plugins.UltimateKeyManagement";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _detectedReferences.Clear();

            GC.SuppressFinalize(this);
        }

        #endregion
    }

    #region Compatibility Shim

    /// <summary>
    /// Provides backward compatibility for code using deprecated plugin types.
    /// Wraps UltimateKeyManagement and exposes old plugin interfaces.
    /// </summary>
    public sealed class CompatibilityShim : IKeyStore
    {
        private readonly UltimateKeyManagementPlugin _ultimatePlugin;
        private readonly MigrationHelperConfig _config;
        private readonly ConcurrentDictionary<string, string> _legacyIdMappings = new();

        internal CompatibilityShim(UltimateKeyManagementPlugin ultimatePlugin, MigrationHelperConfig config)
        {
            _ultimatePlugin = ultimatePlugin;
            _config = config;

            // Register default legacy ID mappings
            _legacyIdMappings["datawarehouse.plugins.keystore.file"] =
                "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy";
            _legacyIdMappings["datawarehouse.plugins.keystore.vault"] =
                "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy";
        }

        /// <summary>
        /// Gets a key store by legacy plugin ID, returning the equivalent UltimateKeyManagement strategy.
        /// </summary>
        public IKeyStore? GetLegacyKeyStore(string legacyPluginId)
        {
            if (_legacyIdMappings.TryGetValue(legacyPluginId.ToLowerInvariant(), out var strategyId))
            {
                return _ultimatePlugin.GetKeyStore(strategyId);
            }

            // Try direct lookup
            return _ultimatePlugin.GetKeyStore(legacyPluginId);
        }

        /// <summary>
        /// Registers a custom legacy ID to strategy mapping.
        /// </summary>
        public void RegisterLegacyMapping(string legacyId, string strategyId)
        {
            _legacyIdMappings[legacyId.ToLowerInvariant()] = strategyId;
        }

        // IKeyStore implementation - delegates to UltimateKeyManagement

        public Task<string> GetCurrentKeyIdAsync()
        {
            var keyStoreIds = _ultimatePlugin.GetRegisteredKeyStoreIds();
            if (keyStoreIds.Count == 0)
                throw new InvalidOperationException("No key stores registered");

            var keyStore = _ultimatePlugin.GetKeyStore(keyStoreIds[0]);
            return keyStore?.GetCurrentKeyIdAsync() ?? Task.FromResult("default");
        }

        public byte[] GetKey(string keyId)
        {
            // Sync bridge: obsolete sync API wrapper
            return Task.Run(() => GetKeyAsync(keyId, new ShimSecurityContext())).GetAwaiter().GetResult();
        }

        public async Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
        {
            var keyStoreIds = _ultimatePlugin.GetRegisteredKeyStoreIds();
            foreach (var id in keyStoreIds)
            {
                var keyStore = _ultimatePlugin.GetKeyStore(id);
                if (keyStore != null)
                {
                    try
                    {
                        return await keyStore.GetKeyAsync(keyId, context);
                    }
                    catch (KeyNotFoundException)
                    {
                        continue;
                    }
                }
            }

            throw new KeyNotFoundException($"Key '{keyId}' not found in any registered key store");
        }

        public async Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
        {
            var keyStoreIds = _ultimatePlugin.GetRegisteredKeyStoreIds();
            if (keyStoreIds.Count == 0)
                throw new InvalidOperationException("No key stores registered");

            var keyStore = _ultimatePlugin.GetKeyStore(keyStoreIds[0]);
            if (keyStore == null)
                throw new InvalidOperationException("Default key store not available");

            return await keyStore.CreateKeyAsync(keyId, context);
        }

        private sealed class ShimSecurityContext : ISecurityContext
        {
            public string UserId => Environment.UserName;
            public string? TenantId => "migration-shim";
            public IEnumerable<string> Roles => new[] { "key-reader" };
            public bool IsSystemAdmin => false;
        }
    }

    #endregion

    #region Data Models

    public sealed class MigrationHelperConfig
    {
        public bool EmitDeprecationWarnings { get; set; } = true;
        public bool LogMigrationActivity { get; set; } = true;
        public string BackupDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "UltimateKeyManagement", "migration-backups");
        public List<string> ExcludePatterns { get; set; } = new() { "bin", "obj", "node_modules", ".git" };
    }

    public sealed class MigrationScanResult
    {
        public DateTime ScanStartedAt { get; set; }
        public DateTime ScanCompletedAt { get; set; }
        public string RootDirectory { get; set; } = "";
        public int TotalFilesScanned { get; set; }
        public int TotalReferencesFound { get; set; }
        public List<FileScanResult> FilesWithReferences { get; set; } = new();
    }

    public sealed class FileScanResult
    {
        public string FilePath { get; set; } = "";
        public List<PluginReferenceInfo> References { get; set; } = new();
        public string? ScanError { get; set; }
        public bool HasReferences => References.Count > 0;
    }

    public sealed class PluginReferenceInfo
    {
        public string FilePath { get; set; } = "";
        public int LineNumber { get; set; }
        public string OldReference { get; set; } = "";
        public string NewReference { get; set; } = "";
        public ReferenceType ReferenceType { get; set; }
        public string Context { get; set; } = "";
    }

    public sealed class AssemblyReferenceInfo
    {
        public string ReferencingAssembly { get; set; } = "";
        public string ReferencedAssembly { get; set; } = "";
        public string Version { get; set; } = "";
        public bool IsDeprecated { get; set; }
        public string MigrationTarget { get; set; } = "";
    }

    public sealed class MigrationSuggestion
    {
        public string SuggestionId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public SuggestionType SuggestionType { get; set; }
        public MigrationSeverity Severity { get; set; }
        public string Description { get; set; } = "";
        public string RecommendedAction { get; set; } = "";
        public List<PluginReferenceInfo> AffectedReferences { get; set; } = new();
        public bool AutoFixAvailable { get; set; }
    }

    public sealed class MigrationAction
    {
        public string ActionId { get; set; } = "";
        public string SuggestionId { get; set; } = "";
        public string ActionType { get; set; } = "";
        public string FilePath { get; set; } = "";
        public DateTime AppliedAt { get; set; }
        public int ReplacementsCount { get; set; }
    }

    public sealed class MigrationActionResult
    {
        public bool Success { get; set; }
        public MigrationAction? Action { get; set; }
        public string FilePath { get; set; } = "";
        public int ReplacementsApplied { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public sealed class BatchMigrationResult
    {
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public int TotalSuggestions { get; set; }
        public int SuccessfulFixes { get; set; }
        public int FailedFixes { get; set; }
        public List<MigrationAction> AppliedActions { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    public enum ReferenceType
    {
        Unknown,
        CodeReference,
        NamespaceImport,
        ConfigurationValue,
        ProjectReference,
        AssemblyReference
    }

    public enum SuggestionType
    {
        UpdateCodeReferences,
        UpdateConfiguration,
        UpdateNamespaces,
        RemoveDeprecatedPackage,
        AddNewPackageReference
    }

    public enum MigrationSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    #endregion
}
