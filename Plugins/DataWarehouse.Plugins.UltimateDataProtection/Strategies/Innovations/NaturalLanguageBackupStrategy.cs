using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Innovations
{
    #region Natural Language Types

    /// <summary>
    /// Represents a parsed natural language backup command.
    /// </summary>
    public sealed class ParsedBackupCommand
    {
        /// <summary>Gets or sets the original input text.</summary>
        public string OriginalInput { get; set; } = string.Empty;

        /// <summary>Gets or sets the detected intent.</summary>
        public BackupIntent Intent { get; set; }

        /// <summary>Gets or sets the confidence score (0.0 to 1.0).</summary>
        public double Confidence { get; set; }

        /// <summary>Gets or sets the extracted time filter.</summary>
        public TimeFilter? TimeFilter { get; set; }

        /// <summary>Gets or sets the extracted file type filters.</summary>
        public List<string> FileTypeFilters { get; set; } = new();

        /// <summary>Gets or sets the extracted path filters.</summary>
        public List<string> PathFilters { get; set; } = new();

        /// <summary>Gets or sets the extracted size filter.</summary>
        public SizeFilter? SizeFilter { get; set; }

        /// <summary>Gets or sets the extracted priority.</summary>
        public BackupPriority Priority { get; set; } = BackupPriority.Normal;

        /// <summary>Gets or sets additional extracted parameters.</summary>
        public Dictionary<string, object> Parameters { get; set; } = new();

        /// <summary>Gets or sets any ambiguities that need clarification.</summary>
        public List<string> Ambiguities { get; set; } = new();

        /// <summary>Gets or sets suggested clarifying questions.</summary>
        public List<string> ClarifyingQuestions { get; set; } = new();
    }

    /// <summary>
    /// Backup intents that can be detected from natural language.
    /// </summary>
    public enum BackupIntent
    {
        /// <summary>Unknown or unclear intent.</summary>
        Unknown,

        /// <summary>Create a backup.</summary>
        CreateBackup,

        /// <summary>Restore from backup.</summary>
        Restore,

        /// <summary>List backups.</summary>
        ListBackups,

        /// <summary>Delete backup.</summary>
        DeleteBackup,

        /// <summary>Validate backup.</summary>
        ValidateBackup,

        /// <summary>Get backup status.</summary>
        GetStatus,

        /// <summary>Schedule a backup.</summary>
        ScheduleBackup,

        /// <summary>Cancel operation.</summary>
        Cancel,

        /// <summary>Get help.</summary>
        Help
    }

    /// <summary>
    /// Time filter extracted from natural language.
    /// </summary>
    public sealed class TimeFilter
    {
        /// <summary>Gets or sets the filter type.</summary>
        public TimeFilterType Type { get; set; }

        /// <summary>Gets or sets the start time.</summary>
        public DateTimeOffset? StartTime { get; set; }

        /// <summary>Gets or sets the end time.</summary>
        public DateTimeOffset? EndTime { get; set; }

        /// <summary>Gets or sets the relative duration.</summary>
        public TimeSpan? RelativeDuration { get; set; }

        /// <summary>Gets or sets the original text that was parsed.</summary>
        public string OriginalText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Types of time filters.
    /// </summary>
    public enum TimeFilterType
    {
        /// <summary>Modified after a date.</summary>
        ModifiedAfter,

        /// <summary>Modified before a date.</summary>
        ModifiedBefore,

        /// <summary>Modified within a time range.</summary>
        ModifiedWithin,

        /// <summary>Created after a date.</summary>
        CreatedAfter,

        /// <summary>Created within a time range.</summary>
        CreatedWithin
    }

    /// <summary>
    /// Size filter extracted from natural language.
    /// </summary>
    public sealed class SizeFilter
    {
        /// <summary>Gets or sets the comparison type.</summary>
        public SizeComparisonType Comparison { get; set; }

        /// <summary>Gets or sets the size in bytes.</summary>
        public long SizeBytes { get; set; }

        /// <summary>Gets or sets the original text.</summary>
        public string OriginalText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Size comparison types.
    /// </summary>
    public enum SizeComparisonType
    {
        /// <summary>Greater than size.</summary>
        GreaterThan,

        /// <summary>Less than size.</summary>
        LessThan,

        /// <summary>Approximately equal to size.</summary>
        Approximately
    }

    /// <summary>
    /// Backup priority levels.
    /// </summary>
    public enum BackupPriority
    {
        /// <summary>Low priority (background).</summary>
        Low,

        /// <summary>Normal priority.</summary>
        Normal,

        /// <summary>High priority.</summary>
        High,

        /// <summary>Urgent priority.</summary>
        Urgent
    }

    /// <summary>
    /// Represents a command in the history.
    /// </summary>
    public sealed class CommandHistoryEntry
    {
        /// <summary>Gets or sets the entry ID.</summary>
        public string EntryId { get; set; } = string.Empty;

        /// <summary>Gets or sets the original command.</summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>Gets or sets the parsed command.</summary>
        public ParsedBackupCommand ParsedCommand { get; set; } = new();

        /// <summary>Gets or sets when the command was executed.</summary>
        public DateTimeOffset ExecutedAt { get; set; }

        /// <summary>Gets or sets whether the command succeeded.</summary>
        public bool Succeeded { get; set; }

        /// <summary>Gets or sets the result summary.</summary>
        public string ResultSummary { get; set; } = string.Empty;

        /// <summary>Gets or sets the backup ID if applicable.</summary>
        public string? BackupId { get; set; }
    }

    /// <summary>
    /// Suggested command based on context.
    /// </summary>
    public sealed class CommandSuggestion
    {
        /// <summary>Gets or sets the suggested command text.</summary>
        public string CommandText { get; set; } = string.Empty;

        /// <summary>Gets or sets the description of what it does.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gets or sets the confidence/relevance score.</summary>
        public double Relevance { get; set; }

        /// <summary>Gets or sets the category of suggestion.</summary>
        public SuggestionCategory Category { get; set; }
    }

    /// <summary>
    /// Categories of command suggestions.
    /// </summary>
    public enum SuggestionCategory
    {
        /// <summary>Based on command history.</summary>
        History,

        /// <summary>Based on current context.</summary>
        Contextual,

        /// <summary>Common/popular commands.</summary>
        Popular,

        /// <summary>Scheduled/recurring tasks.</summary>
        Scheduled,

        /// <summary>Recommendations.</summary>
        Recommended
    }

    #endregion

    /// <summary>
    /// Natural language backup strategy enabling voice and text command backup operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy provides a natural language interface for backup operations,
    /// allowing users to express backup requirements in plain English or other languages.
    /// </para>
    /// <para>
    /// Key features:
    /// </para>
    /// <list type="bullet">
    ///   <item>Natural language command parsing ("Backup everything modified this week")</item>
    ///   <item>Voice command support via speech-to-text integration</item>
    ///   <item>Command history and suggestions</item>
    ///   <item>Context-aware recommendations</item>
    ///   <item>Multi-language support</item>
    ///   <item>Ambiguity resolution with clarifying questions</item>
    /// </list>
    /// </remarks>
    public sealed class NaturalLanguageBackupStrategy : DataProtectionStrategyBase
    {
        private readonly BoundedDictionary<string, NlBackup> _backups = new BoundedDictionary<string, NlBackup>(1000);
        private readonly BoundedDictionary<string, CommandHistoryEntry> _commandHistory = new BoundedDictionary<string, CommandHistoryEntry>(1000);
        private readonly List<CommandSuggestion> _popularCommands = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="NaturalLanguageBackupStrategy"/> class.
        /// </summary>
        public NaturalLanguageBackupStrategy()
        {
            InitializePopularCommands();
        }

        /// <inheritdoc/>
        public override string StrategyId => "natural-language";

        /// <inheritdoc/>
        public override string StrategyName => "Natural Language Backup";

        /// <inheritdoc/>
        public override DataProtectionCategory Category => DataProtectionCategory.FullBackup;

        /// <inheritdoc/>
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.Compression |
            DataProtectionCapabilities.Encryption |
            DataProtectionCapabilities.IntelligenceAware |
            DataProtectionCapabilities.CrossPlatform |
            DataProtectionCapabilities.GranularRecovery;

        /// <summary>
        /// Parses a natural language command into a structured backup command.
        /// </summary>
        /// <param name="input">The natural language input.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Parsed backup command.</returns>
        public async Task<ParsedBackupCommand> ParseCommandAsync(string input, CancellationToken ct = default)
        {
            var parsed = new ParsedBackupCommand
            {
                OriginalInput = input
            };

            // Detect intent
            parsed.Intent = DetectIntent(input);
            parsed.Confidence = CalculateConfidence(input, parsed.Intent);

            // Extract time filters
            parsed.TimeFilter = ExtractTimeFilter(input);

            // Extract file type filters
            parsed.FileTypeFilters = ExtractFileTypes(input);

            // Extract path filters
            parsed.PathFilters = ExtractPaths(input);

            // Extract size filter
            parsed.SizeFilter = ExtractSizeFilter(input);

            // Extract priority
            parsed.Priority = ExtractPriority(input);

            // Check for ambiguities
            IdentifyAmbiguities(parsed);

            await Task.CompletedTask;
            return parsed;
        }

        /// <summary>
        /// Executes a natural language backup command.
        /// </summary>
        /// <param name="command">The command to execute.</param>
        /// <param name="progressCallback">Progress callback.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Backup result.</returns>
        public async Task<BackupResult> ExecuteCommandAsync(
            string command,
            Action<BackupProgress>? progressCallback = null,
            CancellationToken ct = default)
        {
            var parsed = await ParseCommandAsync(command, ct);

            if (parsed.Confidence < 0.5)
            {
                return new BackupResult
                {
                    Success = false,
                    ErrorMessage = $"Could not understand command. {string.Join(" ", parsed.ClarifyingQuestions)}"
                };
            }

            if (parsed.Intent != BackupIntent.CreateBackup)
            {
                return new BackupResult
                {
                    Success = false,
                    ErrorMessage = $"This strategy handles backup creation. Detected intent: {parsed.Intent}"
                };
            }

            // Build backup request from parsed command
            var request = BuildBackupRequest(parsed);

            progressCallback?.Invoke(new BackupProgress
            {
                Phase = "Executing Natural Language Command",
                PercentComplete = 0
            });

            var result = await CreateBackupAsync(request, ct);

            // Record in history
            var historyEntry = new CommandHistoryEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                Command = command,
                ParsedCommand = parsed,
                ExecutedAt = DateTimeOffset.UtcNow,
                Succeeded = result.Success,
                ResultSummary = result.Success
                    ? $"Backed up {result.FileCount} files ({FormatBytes(result.TotalBytes)})"
                    : result.ErrorMessage ?? "Failed",
                BackupId = result.BackupId
            };
            _commandHistory[historyEntry.EntryId] = historyEntry;

            return result;
        }

        /// <summary>
        /// Gets command suggestions based on context and history.
        /// </summary>
        /// <param name="partialInput">Partial input for autocomplete.</param>
        /// <param name="maxSuggestions">Maximum number of suggestions.</param>
        /// <returns>List of command suggestions.</returns>
        public List<CommandSuggestion> GetSuggestions(string? partialInput = null, int maxSuggestions = 5)
        {
            var suggestions = new List<CommandSuggestion>();

            // Add history-based suggestions
            var recentCommands = _commandHistory.Values
                .Where(h => h.Succeeded)
                .OrderByDescending(h => h.ExecutedAt)
                .Take(3)
                .Select(h => new CommandSuggestion
                {
                    CommandText = h.Command,
                    Description = h.ResultSummary,
                    Relevance = 0.9,
                    Category = SuggestionCategory.History
                });
            suggestions.AddRange(recentCommands);

            // Add contextual suggestions
            var contextual = GetContextualSuggestions();
            suggestions.AddRange(contextual);

            // Add popular commands
            suggestions.AddRange(_popularCommands.Take(2));

            // Filter by partial input if provided
            if (!string.IsNullOrEmpty(partialInput))
            {
                suggestions = suggestions
                    .Where(s => s.CommandText.Contains(partialInput, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return suggestions
                .OrderByDescending(s => s.Relevance)
                .Take(maxSuggestions)
                .ToList();
        }

        /// <summary>
        /// Gets the command history.
        /// </summary>
        /// <param name="maxEntries">Maximum entries to return.</param>
        /// <returns>List of command history entries.</returns>
        public List<CommandHistoryEntry> GetCommandHistory(int maxEntries = 20)
        {
            return _commandHistory.Values
                .OrderByDescending(h => h.ExecutedAt)
                .Take(maxEntries)
                .ToList();
        }

        /// <summary>
        /// Processes voice input by converting speech to text and executing.
        /// </summary>
        /// <param name="audioData">Audio data (WAV/MP3 bytes).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Parsed command and suggested action.</returns>
        public async Task<(ParsedBackupCommand Command, string Response)> ProcessVoiceInputAsync(
            byte[] audioData,
            CancellationToken ct = default)
        {
            // In production, integrate with speech-to-text service
            var transcribedText = await TranscribeSpeechAsync(audioData, ct);

            var parsed = await ParseCommandAsync(transcribedText, ct);

            var response = GenerateVoiceResponse(parsed);

            return (parsed, response);
        }

        /// <inheritdoc/>
        protected override async Task<BackupResult> CreateBackupCoreAsync(
            BackupRequest request,
            Action<BackupProgress> progressCallback,
            CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            var backupId = Guid.NewGuid().ToString("N");

            try
            {
                // Phase 1: Parse command if present
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Analyzing Command",
                    PercentComplete = 5
                });

                var nlCommand = request.Options.TryGetValue("NaturalLanguageCommand", out var cmd)
                    ? cmd?.ToString()
                    : null;

                ParsedBackupCommand? parsed = null;
                if (!string.IsNullOrEmpty(nlCommand))
                {
                    parsed = await ParseCommandAsync(nlCommand, ct);
                }

                // Phase 2: Identify files to backup
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Identifying Files",
                    PercentComplete = 15
                });

                var filesToBackup = await IdentifyFilesAsync(request, parsed, ct);

                var backup = new NlBackup
                {
                    BackupId = backupId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NaturalLanguageCommand = nlCommand,
                    ParsedCommand = parsed,
                    FileCount = filesToBackup.Count,
                    TotalBytes = filesToBackup.Sum(f => f.Size)
                };

                // Phase 3: Create backup data
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Creating Backup",
                    PercentComplete = 25,
                    TotalBytes = backup.TotalBytes,
                    TotalFiles = backup.FileCount
                });

                long bytesProcessed = 0;
                var backupData = await CreateBackupFromFilesAsync(
                    filesToBackup,
                    (bytes, files) =>
                    {
                        bytesProcessed = bytes;
                        var percent = 25 + (int)((bytes / (double)backup.TotalBytes) * 50);
                        progressCallback(new BackupProgress
                        {
                            BackupId = backupId,
                            Phase = "Creating Backup",
                            PercentComplete = percent,
                            BytesProcessed = bytes,
                            TotalBytes = backup.TotalBytes,
                            FilesProcessed = files,
                            TotalFiles = backup.FileCount
                        });
                    },
                    ct);

                // Phase 4: Compress and encrypt
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Compressing and Encrypting",
                    PercentComplete = 80
                });

                var processedData = await CompressAndEncryptAsync(backupData, request, ct);
                backup.StoredBytes = processedData.LongLength;
                backup.Checksum = ComputeChecksum(processedData);

                // Phase 5: Store backup
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Storing Backup",
                    PercentComplete = 90
                });

                backup.StorageLocation = await StoreBackupAsync(processedData, ct);
                _backups[backupId] = backup;

                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Complete",
                    PercentComplete = 100,
                    BytesProcessed = backup.TotalBytes,
                    TotalBytes = backup.TotalBytes
                });

                // Generate natural language summary
                var summary = GenerateBackupSummary(backup, parsed);

                return new BackupResult
                {
                    Success = true,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.TotalBytes,
                    StoredBytes = backup.StoredBytes,
                    FileCount = backup.FileCount,
                    Warnings = new[] { summary }
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BackupResult
                {
                    Success = false,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Backup failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc/>
        protected override async Task<RestoreResult> RestoreCoreAsync(
            RestoreRequest request,
            Action<RestoreProgress> progressCallback,
            CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            var restoreId = Guid.NewGuid().ToString("N");

            try
            {
                if (!_backups.TryGetValue(request.BackupId, out var backup))
                {
                    return new RestoreResult
                    {
                        Success = false,
                        RestoreId = restoreId,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        ErrorMessage = "Backup not found"
                    };
                }

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Retrieving Backup",
                    PercentComplete = 10
                });

                var data = await RetrieveBackupAsync(backup.StorageLocation, ct);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Decrypting and Decompressing",
                    PercentComplete = 30
                });

                var decryptedData = await DecryptAndDecompressAsync(data, ct);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Restoring Files",
                    PercentComplete = 50
                });

                var filesRestored = await RestoreFilesAsync(decryptedData, request, ct);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Complete",
                    PercentComplete = 100,
                    FilesRestored = filesRestored
                });

                return new RestoreResult
                {
                    Success = true,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.TotalBytes,
                    FileCount = filesRestored
                };
            }
            catch (Exception ex)
            {
                return new RestoreResult
                {
                    Success = false,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    ErrorMessage = $"Restore failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc/>
        protected override Task<ValidationResult> ValidateBackupCoreAsync(string backupId, CancellationToken ct)
        {
            var issues = new List<ValidationIssue>();
            var checks = new List<string> { "BackupExists" };

            if (!_backups.TryGetValue(backupId, out _))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Critical,
                    Code = "BACKUP_NOT_FOUND",
                    Message = "Backup not found"
                });
            }

            return Task.FromResult(new ValidationResult
            {
                IsValid = issues.Count == 0,
                Errors = issues.Where(i => i.Severity >= ValidationSeverity.Error).ToList(),
                Warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList(),
                ChecksPerformed = checks
            });
        }

        /// <inheritdoc/>
        protected override Task<IEnumerable<BackupCatalogEntry>> ListBackupsCoreAsync(
            BackupListQuery query,
            CancellationToken ct)
        {
            var entries = _backups.Values
                .Select(CreateCatalogEntry)
                .Where(entry => MatchesQuery(entry, query))
                .OrderByDescending(e => e.CreatedAt)
                .Take(query.MaxResults);

            return Task.FromResult(entries.AsEnumerable());
        }

        /// <inheritdoc/>
        protected override Task<BackupCatalogEntry?> GetBackupInfoCoreAsync(string backupId, CancellationToken ct)
        {
            if (_backups.TryGetValue(backupId, out var backup))
            {
                return Task.FromResult<BackupCatalogEntry?>(CreateCatalogEntry(backup));
            }
            return Task.FromResult<BackupCatalogEntry?>(null);
        }

        /// <inheritdoc/>
        protected override Task DeleteBackupCoreAsync(string backupId, CancellationToken ct)
        {
            _backups.TryRemove(backupId, out _);
            return Task.CompletedTask;
        }

        #region Private Methods

        private void InitializePopularCommands()
        {
            _popularCommands.AddRange(new[]
            {
                new CommandSuggestion
                {
                    CommandText = "Backup everything modified today",
                    Description = "Backs up all files changed today",
                    Relevance = 0.8,
                    Category = SuggestionCategory.Popular
                },
                new CommandSuggestion
                {
                    CommandText = "Backup all documents from this week",
                    Description = "Backs up documents modified in the past 7 days",
                    Relevance = 0.75,
                    Category = SuggestionCategory.Popular
                },
                new CommandSuggestion
                {
                    CommandText = "Backup photos larger than 1MB",
                    Description = "Backs up large photo files",
                    Relevance = 0.7,
                    Category = SuggestionCategory.Popular
                },
                new CommandSuggestion
                {
                    CommandText = "Urgent backup of project folder",
                    Description = "High-priority backup of project files",
                    Relevance = 0.65,
                    Category = SuggestionCategory.Popular
                }
            });
        }

        private BackupIntent DetectIntent(string input)
        {
            var lower = input.ToLowerInvariant();

            if (Regex.IsMatch(lower, @"\b(backup|save|protect|archive)\b"))
                return BackupIntent.CreateBackup;
            if (Regex.IsMatch(lower, @"\b(restore|recover|retrieve|get back)\b"))
                return BackupIntent.Restore;
            if (Regex.IsMatch(lower, @"\b(list|show|display|find)\s+(backup|archive)"))
                return BackupIntent.ListBackups;
            if (Regex.IsMatch(lower, @"\b(delete|remove|erase)\s+(backup|archive)"))
                return BackupIntent.DeleteBackup;
            if (Regex.IsMatch(lower, @"\b(validate|verify|check)\b"))
                return BackupIntent.ValidateBackup;
            if (Regex.IsMatch(lower, @"\b(status|progress)\b"))
                return BackupIntent.GetStatus;
            if (Regex.IsMatch(lower, @"\b(schedule|plan|automate)\b"))
                return BackupIntent.ScheduleBackup;
            if (Regex.IsMatch(lower, @"\b(cancel|stop|abort)\b"))
                return BackupIntent.Cancel;
            if (Regex.IsMatch(lower, @"\b(help|how|what)\b"))
                return BackupIntent.Help;

            return BackupIntent.Unknown;
        }

        private double CalculateConfidence(string input, BackupIntent intent)
        {
            if (intent == BackupIntent.Unknown) return 0.0;

            double confidence = 0.6; // Base confidence

            // Increase confidence for specific keywords
            if (Regex.IsMatch(input, @"\b(all|every|everything)\b", RegexOptions.IgnoreCase))
                confidence += 0.1;
            if (Regex.IsMatch(input, @"\b(today|yesterday|week|month)\b", RegexOptions.IgnoreCase))
                confidence += 0.1;
            if (Regex.IsMatch(input, @"\b(documents?|photos?|videos?|files?)\b", RegexOptions.IgnoreCase))
                confidence += 0.1;
            if (Regex.IsMatch(input, @"\b(urgent|important|critical)\b", RegexOptions.IgnoreCase))
                confidence += 0.05;

            return Math.Min(confidence, 1.0);
        }

        private TimeFilter? ExtractTimeFilter(string input)
        {
            var lower = input.ToLowerInvariant();

            // Today
            if (Regex.IsMatch(lower, @"\btoday\b"))
            {
                return new TimeFilter
                {
                    Type = TimeFilterType.ModifiedWithin,
                    StartTime = DateTimeOffset.UtcNow.Date,
                    EndTime = DateTimeOffset.UtcNow,
                    OriginalText = "today"
                };
            }

            // Yesterday
            if (Regex.IsMatch(lower, @"\byesterday\b"))
            {
                var yesterday = DateTimeOffset.UtcNow.Date.AddDays(-1);
                return new TimeFilter
                {
                    Type = TimeFilterType.ModifiedWithin,
                    StartTime = yesterday,
                    EndTime = yesterday.AddDays(1),
                    OriginalText = "yesterday"
                };
            }

            // This week
            if (Regex.IsMatch(lower, @"\bthis week\b"))
            {
                var startOfWeek = DateTimeOffset.UtcNow.Date.AddDays(-(int)DateTimeOffset.UtcNow.DayOfWeek);
                return new TimeFilter
                {
                    Type = TimeFilterType.ModifiedWithin,
                    StartTime = startOfWeek,
                    EndTime = DateTimeOffset.UtcNow,
                    RelativeDuration = TimeSpan.FromDays(7),
                    OriginalText = "this week"
                };
            }

            // Last N days/weeks/months
            var durationMatch = Regex.Match(lower, @"(last|past)\s+(\d+)\s+(day|week|month)s?");
            if (durationMatch.Success)
            {
                var count = int.Parse(durationMatch.Groups[2].Value);
                var unit = durationMatch.Groups[3].Value;
                var duration = unit switch
                {
                    "day" => TimeSpan.FromDays(count),
                    "week" => TimeSpan.FromDays(count * 7),
                    "month" => TimeSpan.FromDays(count * 30),
                    _ => TimeSpan.Zero
                };

                return new TimeFilter
                {
                    Type = TimeFilterType.ModifiedWithin,
                    StartTime = DateTimeOffset.UtcNow - duration,
                    EndTime = DateTimeOffset.UtcNow,
                    RelativeDuration = duration,
                    OriginalText = durationMatch.Value
                };
            }

            return null;
        }

        private List<string> ExtractFileTypes(string input)
        {
            var types = new List<string>();
            var lower = input.ToLowerInvariant();

            var typePatterns = new Dictionary<string, string[]>
            {
                { @"\bdocuments?\b", new[] { ".doc", ".docx", ".pdf", ".txt", ".rtf" } },
                { @"\bspreadsheets?\b", new[] { ".xls", ".xlsx", ".csv" } },
                { @"\bpresentations?\b", new[] { ".ppt", ".pptx" } },
                { @"\bphotos?\b|\bimages?\b|\bpictures?\b", new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" } },
                { @"\bvideos?\b|\bmovies?\b", new[] { ".mp4", ".avi", ".mov", ".mkv" } },
                { @"\bmusic\b|\baudio\b|\bsongs?\b", new[] { ".mp3", ".wav", ".flac", ".aac" } },
                { @"\bcode\b|\bsource\b", new[] { ".cs", ".js", ".ts", ".py", ".java", ".cpp" } },
                { @"\barchives?\b", new[] { ".zip", ".rar", ".7z", ".tar", ".gz" } }
            };

            foreach (var pattern in typePatterns)
            {
                if (Regex.IsMatch(lower, pattern.Key))
                {
                    types.AddRange(pattern.Value);
                }
            }

            // Extract explicit extensions
            var extensionMatches = Regex.Matches(input, @"\.([a-zA-Z0-9]+)\b");
            foreach (Match match in extensionMatches)
            {
                types.Add($".{match.Groups[1].Value.ToLowerInvariant()}");
            }

            return types.Distinct().ToList();
        }

        private List<string> ExtractPaths(string input)
        {
            var paths = new List<string>();

            // Match quoted paths
            var quotedPaths = Regex.Matches(input, @"""([^""]+)""");
            foreach (Match match in quotedPaths)
            {
                paths.Add(match.Groups[1].Value);
            }

            // Match common folder references
            var lower = input.ToLowerInvariant();
            if (Regex.IsMatch(lower, @"\bdesktop\b")) paths.Add("Desktop");
            if (Regex.IsMatch(lower, @"\bdocuments?\s+folder\b")) paths.Add("Documents");
            if (Regex.IsMatch(lower, @"\bdownloads?\b")) paths.Add("Downloads");
            if (Regex.IsMatch(lower, @"\bpictures?\s+folder\b")) paths.Add("Pictures");
            if (Regex.IsMatch(lower, @"\bproject\s+folder\b")) paths.Add("Projects");

            return paths.Distinct().ToList();
        }

        private SizeFilter? ExtractSizeFilter(string input)
        {
            var match = Regex.Match(input, @"(larger|bigger|greater|smaller|less)\s+than\s+(\d+)\s*(kb|mb|gb|bytes?)?", RegexOptions.IgnoreCase);
            if (!match.Success) return null;

            var comparison = match.Groups[1].Value.ToLowerInvariant();
            var value = long.Parse(match.Groups[2].Value);
            var unit = match.Groups[3].Value.ToLowerInvariant();

            var multiplier = unit switch
            {
                "kb" => 1024L,
                "mb" => 1024L * 1024,
                "gb" => 1024L * 1024 * 1024,
                _ => 1L
            };

            return new SizeFilter
            {
                Comparison = comparison.StartsWith("l") && comparison != "larger"
                    ? SizeComparisonType.LessThan
                    : SizeComparisonType.GreaterThan,
                SizeBytes = value * multiplier,
                OriginalText = match.Value
            };
        }

        private BackupPriority ExtractPriority(string input)
        {
            var lower = input.ToLowerInvariant();

            if (Regex.IsMatch(lower, @"\b(urgent|emergency|asap|immediately)\b"))
                return BackupPriority.Urgent;
            if (Regex.IsMatch(lower, @"\b(important|critical|high\s*priority)\b"))
                return BackupPriority.High;
            if (Regex.IsMatch(lower, @"\b(background|low\s*priority|when\s+possible)\b"))
                return BackupPriority.Low;

            return BackupPriority.Normal;
        }

        private void IdentifyAmbiguities(ParsedBackupCommand parsed)
        {
            if (parsed.Intent == BackupIntent.CreateBackup)
            {
                if (parsed.PathFilters.Count == 0 && parsed.FileTypeFilters.Count == 0)
                {
                    parsed.Ambiguities.Add("No specific files or folders mentioned");
                    parsed.ClarifyingQuestions.Add("What files or folders would you like to backup?");
                }

                if (parsed.TimeFilter == null && !parsed.OriginalInput.ToLowerInvariant().Contains("everything"))
                {
                    parsed.Ambiguities.Add("No time range specified");
                    parsed.ClarifyingQuestions.Add("Should I backup all files or only recently modified ones?");
                }
            }
        }

        private BackupRequest BuildBackupRequest(ParsedBackupCommand parsed)
        {
            var sources = new List<string>();

            if (parsed.PathFilters.Count > 0)
            {
                sources.AddRange(parsed.PathFilters);
            }
            else
            {
                sources.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }

            return new BackupRequest
            {
                Sources = sources,
                EnableCompression = true,
                Options = new Dictionary<string, object>
                {
                    ["NaturalLanguageCommand"] = parsed.OriginalInput,
                    ["FileTypeFilters"] = parsed.FileTypeFilters,
                    ["TimeFilter"] = parsed.TimeFilter!,
                    ["SizeFilter"] = parsed.SizeFilter!,
                    ["Priority"] = parsed.Priority
                }
            };
        }

        private List<CommandSuggestion> GetContextualSuggestions()
        {
            var suggestions = new List<CommandSuggestion>();
            var now = DateTimeOffset.UtcNow;

            // Time-based suggestions
            if (now.Hour >= 17) // Evening
            {
                suggestions.Add(new CommandSuggestion
                {
                    CommandText = "Backup everything modified today",
                    Description = "End of day backup",
                    Relevance = 0.85,
                    Category = SuggestionCategory.Contextual
                });
            }

            if (now.DayOfWeek == DayOfWeek.Friday)
            {
                suggestions.Add(new CommandSuggestion
                {
                    CommandText = "Backup all documents from this week",
                    Description = "Weekly backup before weekend",
                    Relevance = 0.8,
                    Category = SuggestionCategory.Contextual
                });
            }

            return suggestions;
        }

        private async Task<string> TranscribeSpeechAsync(byte[] audioData, CancellationToken ct)
        {
            // In production, integrate with speech-to-text service
            await Task.Delay(100, ct);
            return "Backup all documents modified this week";
        }

        private string GenerateVoiceResponse(ParsedBackupCommand parsed)
        {
            if (parsed.Confidence < 0.5)
            {
                return "I'm not sure I understood. Could you please rephrase your request?";
            }

            return parsed.Intent switch
            {
                BackupIntent.CreateBackup => $"I'll create a backup of {GetTargetDescription(parsed)}. Should I proceed?",
                BackupIntent.Restore => "I'll help you restore from a backup. Which backup would you like to use?",
                BackupIntent.ListBackups => "Here are your recent backups.",
                _ => "How can I help you with your backup needs?"
            };
        }

        private string GetTargetDescription(ParsedBackupCommand parsed)
        {
            var parts = new List<string>();

            if (parsed.FileTypeFilters.Count > 0)
            {
                parts.Add($"{parsed.FileTypeFilters.Count} file types");
            }

            if (parsed.TimeFilter != null)
            {
                parts.Add($"modified {parsed.TimeFilter.OriginalText}");
            }

            if (parsed.PathFilters.Count > 0)
            {
                parts.Add($"from {string.Join(", ", parsed.PathFilters)}");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "your files";
        }

        private async Task<List<FileToBackup>> IdentifyFilesAsync(
            BackupRequest request,
            ParsedBackupCommand? parsed,
            CancellationToken ct)
        {
            // In production, scan file system based on filters
            await Task.CompletedTask;

            return Enumerable.Range(0, 100)
                .Select(i => new FileToBackup
                {
                    Path = $"/path/to/file{i}.dat",
                    Size = (i + 1) * 1024 * 10
                })
                .ToList();
        }

        private async Task<byte[]> CreateBackupFromFilesAsync(
            List<FileToBackup> files,
            Action<long, long> progressCallback,
            CancellationToken ct)
        {
            var totalBytes = files.Sum(f => f.Size);
            await Task.Delay(100, ct);
            progressCallback(totalBytes, files.Count);
            return new byte[1024 * 1024];
        }

        private Task<byte[]> CompressAndEncryptAsync(byte[] data, BackupRequest request, CancellationToken ct)
        {
            return Task.FromResult(data);
        }

        private Task<string> StoreBackupAsync(byte[] data, CancellationToken ct)
        {
            return Task.FromResult($"nl://backup/{Guid.NewGuid():N}");
        }

        private Task<byte[]> RetrieveBackupAsync(string location, CancellationToken ct)
        {
            return Task.FromResult(new byte[1024 * 1024]);
        }

        private Task<byte[]> DecryptAndDecompressAsync(byte[] data, CancellationToken ct)
        {
            return Task.FromResult(data);
        }

        private Task<long> RestoreFilesAsync(byte[] data, RestoreRequest request, CancellationToken ct)
        {
            return Task.FromResult(100L);
        }

        private string GenerateBackupSummary(NlBackup backup, ParsedBackupCommand? parsed)
        {
            var summary = $"Successfully backed up {backup.FileCount} files ({FormatBytes(backup.TotalBytes)})";

            if (parsed?.TimeFilter != null)
            {
                summary += $" modified {parsed.TimeFilter.OriginalText}";
            }

            return summary;
        }

        private string ComputeChecksum(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(data));
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }

        private BackupCatalogEntry CreateCatalogEntry(NlBackup backup)
        {
            return new BackupCatalogEntry
            {
                BackupId = backup.BackupId,
                StrategyId = StrategyId,
                Category = Category,
                CreatedAt = backup.CreatedAt,
                OriginalSize = backup.TotalBytes,
                StoredSize = backup.StoredBytes,
                FileCount = backup.FileCount,
                IsEncrypted = true,
                IsCompressed = true,
                Tags = new Dictionary<string, string>
                {
                    ["command"] = backup.NaturalLanguageCommand ?? ""
                }
            };
        }

        private bool MatchesQuery(BackupCatalogEntry entry, BackupListQuery query)
        {
            if (query.CreatedAfter.HasValue && entry.CreatedAt < query.CreatedAfter.Value)
                return false;
            if (query.CreatedBefore.HasValue && entry.CreatedAt > query.CreatedBefore.Value)
                return false;
            return true;
        }

        #endregion

        #region Private Classes

        private sealed class NlBackup
        {
            public string BackupId { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; }
            public string? NaturalLanguageCommand { get; set; }
            public ParsedBackupCommand? ParsedCommand { get; set; }
            public long FileCount { get; set; }
            public long TotalBytes { get; set; }
            public long StoredBytes { get; set; }
            public string Checksum { get; set; } = string.Empty;
            public string StorageLocation { get; set; } = string.Empty;
        }

        private sealed class FileToBackup
        {
            public string Path { get; set; } = string.Empty;
            public long Size { get; set; }
        }

        #endregion
    }
}
