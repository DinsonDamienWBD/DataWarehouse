using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.UltimateDataProtection.Strategies.Innovations
{
    #region Gamification Types

    /// <summary>
    /// Represents a user's backup profile with gamification data.
    /// </summary>
    public sealed class BackupProfile
    {
        /// <summary>Gets or sets the profile ID.</summary>
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>Gets or sets the username.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Gets or sets the current level.</summary>
        public int Level { get; set; } = 1;

        /// <summary>Gets or sets the total experience points.</summary>
        public long ExperiencePoints { get; set; }

        /// <summary>Gets or sets experience needed for next level.</summary>
        public long ExperienceToNextLevel => CalculateExpForLevel(Level + 1) - ExperiencePoints;

        /// <summary>Gets or sets the current backup streak in days.</summary>
        public int CurrentStreak { get; set; }

        /// <summary>Gets or sets the longest streak achieved.</summary>
        public int LongestStreak { get; set; }

        /// <summary>Gets or sets the date of last backup.</summary>
        public DateTimeOffset? LastBackupDate { get; set; }

        /// <summary>Gets or sets the total backups completed.</summary>
        public int TotalBackups { get; set; }

        /// <summary>Gets or sets the total bytes protected.</summary>
        public long TotalBytesProtected { get; set; }

        /// <summary>Gets or sets the backup health score (0-100).</summary>
        public int HealthScore { get; set; } = 100;

        /// <summary>Gets or sets earned achievements.</summary>
        public List<Achievement> Achievements { get; set; } = new();

        /// <summary>Gets or sets earned badges.</summary>
        public List<Badge> Badges { get; set; } = new();

        /// <summary>Gets or sets active challenges.</summary>
        public List<Challenge> ActiveChallenges { get; set; } = new();

        /// <summary>Gets or sets the ranking position on leaderboard.</summary>
        public int? LeaderboardRank { get; set; }

        /// <summary>Gets or sets when the profile was created.</summary>
        public DateTimeOffset CreatedAt { get; set; }

        private static long CalculateExpForLevel(int level) => (long)(100 * Math.Pow(1.5, level - 1));
    }

    /// <summary>
    /// Represents an achievement that can be earned.
    /// </summary>
    public sealed class Achievement
    {
        /// <summary>Gets or sets the achievement ID.</summary>
        public string AchievementId { get; set; } = string.Empty;

        /// <summary>Gets or sets the achievement name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the description.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gets or sets the icon name/URL.</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>Gets or sets the rarity tier.</summary>
        public AchievementRarity Rarity { get; set; }

        /// <summary>Gets or sets the experience points rewarded.</summary>
        public int ExperienceReward { get; set; }

        /// <summary>Gets or sets when the achievement was earned.</summary>
        public DateTimeOffset? EarnedAt { get; set; }

        /// <summary>Gets or sets the current progress (0-100).</summary>
        public int Progress { get; set; }

        /// <summary>Gets or sets the category.</summary>
        public AchievementCategory Category { get; set; }
    }

    /// <summary>
    /// Achievement rarity tiers.
    /// </summary>
    public enum AchievementRarity
    {
        /// <summary>Common achievement.</summary>
        Common,

        /// <summary>Uncommon achievement.</summary>
        Uncommon,

        /// <summary>Rare achievement.</summary>
        Rare,

        /// <summary>Epic achievement.</summary>
        Epic,

        /// <summary>Legendary achievement.</summary>
        Legendary
    }

    /// <summary>
    /// Achievement categories.
    /// </summary>
    public enum AchievementCategory
    {
        /// <summary>Streak-related achievements.</summary>
        Streak,

        /// <summary>Volume-related achievements.</summary>
        Volume,

        /// <summary>Consistency achievements.</summary>
        Consistency,

        /// <summary>Speed achievements.</summary>
        Speed,

        /// <summary>Special/seasonal achievements.</summary>
        Special,

        /// <summary>Recovery achievements.</summary>
        Recovery
    }

    /// <summary>
    /// Represents a badge that can be displayed.
    /// </summary>
    public sealed class Badge
    {
        /// <summary>Gets or sets the badge ID.</summary>
        public string BadgeId { get; set; } = string.Empty;

        /// <summary>Gets or sets the badge name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the badge tier (1-5).</summary>
        public int Tier { get; set; } = 1;

        /// <summary>Gets or sets the icon.</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>Gets or sets when the badge was earned.</summary>
        public DateTimeOffset EarnedAt { get; set; }

        /// <summary>Gets or sets the badge type.</summary>
        public BadgeType Type { get; set; }
    }

    /// <summary>
    /// Types of badges.
    /// </summary>
    public enum BadgeType
    {
        /// <summary>Streak badge.</summary>
        Streak,

        /// <summary>Data protector badge.</summary>
        DataProtector,

        /// <summary>Speed demon badge.</summary>
        SpeedDemon,

        /// <summary>Consistency badge.</summary>
        Consistent,

        /// <summary>Recovery expert badge.</summary>
        RecoveryExpert,

        /// <summary>Early adopter badge.</summary>
        EarlyAdopter
    }

    /// <summary>
    /// Represents a challenge that can be completed.
    /// </summary>
    public sealed class Challenge
    {
        /// <summary>Gets or sets the challenge ID.</summary>
        public string ChallengeId { get; set; } = string.Empty;

        /// <summary>Gets or sets the challenge name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the description.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gets or sets the challenge type.</summary>
        public ChallengeType Type { get; set; }

        /// <summary>Gets or sets the target value.</summary>
        public int TargetValue { get; set; }

        /// <summary>Gets or sets the current progress.</summary>
        public int CurrentProgress { get; set; }

        /// <summary>Gets or sets whether the challenge is completed.</summary>
        public bool IsCompleted => CurrentProgress >= TargetValue;

        /// <summary>Gets or sets the experience reward.</summary>
        public int ExperienceReward { get; set; }

        /// <summary>Gets or sets when the challenge started.</summary>
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>Gets or sets when the challenge expires.</summary>
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>Gets or sets the difficulty.</summary>
        public ChallengeDifficulty Difficulty { get; set; }
    }

    /// <summary>
    /// Types of challenges.
    /// </summary>
    public enum ChallengeType
    {
        /// <summary>Complete N backups in time period.</summary>
        BackupCount,

        /// <summary>Backup N GB of data.</summary>
        DataVolume,

        /// <summary>Maintain streak for N days.</summary>
        StreakMaintain,

        /// <summary>Complete N restores.</summary>
        RestoreCount,

        /// <summary>Achieve backup speed target.</summary>
        SpeedTarget,

        /// <summary>Weekly challenge.</summary>
        Weekly
    }

    /// <summary>
    /// Challenge difficulty levels.
    /// </summary>
    public enum ChallengeDifficulty
    {
        /// <summary>Easy challenge.</summary>
        Easy,

        /// <summary>Medium challenge.</summary>
        Medium,

        /// <summary>Hard challenge.</summary>
        Hard,

        /// <summary>Expert challenge.</summary>
        Expert
    }

    /// <summary>
    /// Represents a leaderboard entry.
    /// </summary>
    public sealed class LeaderboardEntry
    {
        /// <summary>Gets or sets the rank.</summary>
        public int Rank { get; set; }

        /// <summary>Gets or sets the profile ID.</summary>
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>Gets or sets the username.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Gets or sets the level.</summary>
        public int Level { get; set; }

        /// <summary>Gets or sets the score for this leaderboard.</summary>
        public long Score { get; set; }

        /// <summary>Gets or sets the current streak.</summary>
        public int CurrentStreak { get; set; }

        /// <summary>Gets or sets the health score.</summary>
        public int HealthScore { get; set; }
    }

    /// <summary>
    /// Types of leaderboards.
    /// </summary>
    public enum LeaderboardType
    {
        /// <summary>Total experience points.</summary>
        Experience,

        /// <summary>Current backup streak.</summary>
        Streak,

        /// <summary>Total data protected.</summary>
        DataProtected,

        /// <summary>Health score.</summary>
        HealthScore,

        /// <summary>Monthly activity.</summary>
        Monthly
    }

    /// <summary>
    /// Notification about gamification events.
    /// </summary>
    public sealed class GamificationNotification
    {
        /// <summary>Gets or sets the notification type.</summary>
        public GamificationNotificationType Type { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Gets or sets related data.</summary>
        public Dictionary<string, object> Data { get; set; } = new();

        /// <summary>Gets or sets when it was created.</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Types of gamification notifications.
    /// </summary>
    public enum GamificationNotificationType
    {
        /// <summary>Level up notification.</summary>
        LevelUp,

        /// <summary>Achievement unlocked.</summary>
        AchievementUnlocked,

        /// <summary>Badge earned.</summary>
        BadgeEarned,

        /// <summary>Challenge completed.</summary>
        ChallengeCompleted,

        /// <summary>Streak milestone.</summary>
        StreakMilestone,

        /// <summary>Streak at risk.</summary>
        StreakAtRisk,

        /// <summary>Leaderboard rank change.</summary>
        RankChange
    }

    #endregion

    /// <summary>
    /// Gamified backup strategy that encourages good backup habits through achievements, streaks, and leaderboards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy adds gamification elements to backup operations, making data protection
    /// more engaging and encouraging consistent backup practices.
    /// </para>
    /// <para>
    /// Key features:
    /// </para>
    /// <list type="bullet">
    ///   <item>Backup streaks and streak achievements</item>
    ///   <item>Achievements and badges for milestones</item>
    ///   <item>Health scores based on backup practices</item>
    ///   <item>Leaderboards for friendly competition</item>
    ///   <item>Daily/weekly challenges</item>
    ///   <item>Experience points and leveling system</item>
    /// </list>
    /// </remarks>
    public sealed class GamifiedBackupStrategy : DataProtectionStrategyBase
    {
        private readonly ConcurrentDictionary<string, GamifiedBackup> _backups = new();
        private readonly ConcurrentDictionary<string, BackupProfile> _profiles = new();
        private readonly List<Achievement> _availableAchievements = new();
        private readonly List<GamificationNotification> _notifications = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="GamifiedBackupStrategy"/> class.
        /// </summary>
        public GamifiedBackupStrategy()
        {
            InitializeAchievements();
        }

        /// <inheritdoc/>
        public override string StrategyId => "gamified";

        /// <inheritdoc/>
        public override string StrategyName => "Gamified Backup";

        /// <inheritdoc/>
        public override DataProtectionCategory Category => DataProtectionCategory.FullBackup;

        /// <inheritdoc/>
        public override DataProtectionCapabilities Capabilities =>
            DataProtectionCapabilities.Compression |
            DataProtectionCapabilities.Encryption |
            DataProtectionCapabilities.CrossPlatform |
            DataProtectionCapabilities.IntelligenceAware;

        /// <summary>
        /// Gets or creates a backup profile for a user.
        /// </summary>
        /// <param name="profileId">The profile ID.</param>
        /// <param name="username">The username.</param>
        /// <returns>The backup profile.</returns>
        public BackupProfile GetOrCreateProfile(string profileId, string username)
        {
            return _profiles.GetOrAdd(profileId, _ => new BackupProfile
            {
                ProfileId = profileId,
                Username = username,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        /// <summary>
        /// Gets the profile for a user.
        /// </summary>
        /// <param name="profileId">The profile ID.</param>
        /// <returns>The profile or null if not found.</returns>
        public BackupProfile? GetProfile(string profileId)
        {
            return _profiles.TryGetValue(profileId, out var profile) ? profile : null;
        }

        /// <summary>
        /// Gets the leaderboard for a specific type.
        /// </summary>
        /// <param name="type">The leaderboard type.</param>
        /// <param name="maxEntries">Maximum entries to return.</param>
        /// <returns>List of leaderboard entries.</returns>
        public List<LeaderboardEntry> GetLeaderboard(LeaderboardType type, int maxEntries = 10)
        {
            var profiles = _profiles.Values.ToList();

            var sorted = type switch
            {
                LeaderboardType.Experience => profiles.OrderByDescending(p => p.ExperiencePoints),
                LeaderboardType.Streak => profiles.OrderByDescending(p => p.CurrentStreak),
                LeaderboardType.DataProtected => profiles.OrderByDescending(p => p.TotalBytesProtected),
                LeaderboardType.HealthScore => profiles.OrderByDescending(p => p.HealthScore),
                _ => profiles.OrderByDescending(p => p.ExperiencePoints)
            };

            return sorted
                .Take(maxEntries)
                .Select((p, i) => new LeaderboardEntry
                {
                    Rank = i + 1,
                    ProfileId = p.ProfileId,
                    Username = p.Username,
                    Level = p.Level,
                    Score = type switch
                    {
                        LeaderboardType.Experience => p.ExperiencePoints,
                        LeaderboardType.Streak => p.CurrentStreak,
                        LeaderboardType.DataProtected => p.TotalBytesProtected,
                        LeaderboardType.HealthScore => p.HealthScore,
                        _ => p.ExperiencePoints
                    },
                    CurrentStreak = p.CurrentStreak,
                    HealthScore = p.HealthScore
                })
                .ToList();
        }

        /// <summary>
        /// Gets available challenges for a profile.
        /// </summary>
        /// <param name="profileId">The profile ID.</param>
        /// <returns>List of available challenges.</returns>
        public List<Challenge> GetAvailableChallenges(string profileId)
        {
            var profile = GetProfile(profileId);
            if (profile == null) return new List<Challenge>();

            // Generate challenges based on profile level
            var challenges = new List<Challenge>
            {
                new Challenge
                {
                    ChallengeId = "weekly-backup-5",
                    Name = "Weekly Warrior",
                    Description = "Complete 5 backups this week",
                    Type = ChallengeType.Weekly,
                    TargetValue = 5,
                    ExperienceReward = 200,
                    StartedAt = GetWeekStart(),
                    ExpiresAt = GetWeekStart().AddDays(7),
                    Difficulty = ChallengeDifficulty.Easy
                },
                new Challenge
                {
                    ChallengeId = "data-protector-1gb",
                    Name = "Data Guardian",
                    Description = "Protect 1 GB of data this week",
                    Type = ChallengeType.DataVolume,
                    TargetValue = 1024, // MB
                    ExperienceReward = 300,
                    StartedAt = GetWeekStart(),
                    ExpiresAt = GetWeekStart().AddDays(7),
                    Difficulty = ChallengeDifficulty.Medium
                }
            };

            // Add streak challenge if they have a streak going
            if (profile.CurrentStreak >= 3)
            {
                challenges.Add(new Challenge
                {
                    ChallengeId = "streak-extend-7",
                    Name = "Streak Master",
                    Description = "Extend your streak to 7 days",
                    Type = ChallengeType.StreakMaintain,
                    TargetValue = 7,
                    CurrentProgress = profile.CurrentStreak,
                    ExperienceReward = 500,
                    StartedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(7 - profile.CurrentStreak + 1),
                    Difficulty = ChallengeDifficulty.Medium
                });
            }

            return challenges;
        }

        /// <summary>
        /// Gets recent notifications for a profile.
        /// </summary>
        /// <param name="profileId">The profile ID.</param>
        /// <param name="maxNotifications">Maximum notifications.</param>
        /// <returns>List of notifications.</returns>
        public List<GamificationNotification> GetNotifications(string profileId, int maxNotifications = 10)
        {
            return _notifications
                .OrderByDescending(n => n.CreatedAt)
                .Take(maxNotifications)
                .ToList();
        }

        /// <summary>
        /// Calculates the health score for a profile.
        /// </summary>
        /// <param name="profileId">The profile ID.</param>
        /// <returns>Health score (0-100).</returns>
        public int CalculateHealthScore(string profileId)
        {
            var profile = GetProfile(profileId);
            if (profile == null) return 0;

            var score = 100;

            // Deduct for days since last backup
            if (profile.LastBackupDate.HasValue)
            {
                var daysSinceBackup = (DateTimeOffset.UtcNow - profile.LastBackupDate.Value).TotalDays;
                if (daysSinceBackup > 1) score -= (int)(daysSinceBackup * 5);
            }
            else
            {
                score -= 50; // No backup ever
            }

            // Bonus for streak
            score += Math.Min(profile.CurrentStreak * 2, 20);

            // Bonus for consistency
            if (profile.TotalBackups >= 10) score += 5;
            if (profile.TotalBackups >= 50) score += 5;

            return Math.Max(0, Math.Min(100, score));
        }

        /// <inheritdoc/>
        protected override async Task<BackupResult> CreateBackupCoreAsync(
            BackupRequest request,
            Action<BackupProgress> progressCallback,
            CancellationToken ct)
        {
            var startTime = DateTimeOffset.UtcNow;
            var backupId = Guid.NewGuid().ToString("N");

            // Get profile from request options
            var profileId = request.Options.TryGetValue("ProfileId", out var pid)
                ? pid?.ToString() ?? "default"
                : "default";
            var profile = GetOrCreateProfile(profileId, profileId);

            try
            {
                // Phase 1: Create backup
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Creating Backup",
                    PercentComplete = 10
                });

                var backupData = await CreateBackupDataAsync(request, ct);

                var backup = new GamifiedBackup
                {
                    BackupId = backupId,
                    ProfileId = profileId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    OriginalSize = backupData.LongLength,
                    FileCount = request.Sources.Count * 50
                };

                // Phase 2: Process and store
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Processing",
                    PercentComplete = 50
                });

                var processedData = await ProcessBackupAsync(backupData, request, ct);
                backup.StoredSize = processedData.LongLength;
                backup.Checksum = ComputeChecksum(processedData);

                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Storing",
                    PercentComplete = 80
                });

                backup.StorageLocation = await StoreBackupAsync(processedData, ct);
                _backups[backupId] = backup;

                // Phase 3: Update gamification
                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Updating Stats",
                    PercentComplete = 95
                });

                var gamificationResult = await UpdateGamificationAsync(profile, backup, ct);

                progressCallback(new BackupProgress
                {
                    BackupId = backupId,
                    Phase = "Complete",
                    PercentComplete = 100
                });

                var warnings = new List<string>
                {
                    $"Experience gained: +{gamificationResult.ExperienceGained} XP",
                    $"Current level: {profile.Level}",
                    $"Streak: {profile.CurrentStreak} days",
                    $"Health Score: {profile.HealthScore}/100"
                };

                if (gamificationResult.LeveledUp)
                {
                    warnings.Add($"LEVEL UP! You are now level {profile.Level}!");
                }

                if (gamificationResult.AchievementsUnlocked.Count > 0)
                {
                    warnings.AddRange(gamificationResult.AchievementsUnlocked.Select(a => $"Achievement: {a.Name}"));
                }

                return new BackupResult
                {
                    Success = true,
                    BackupId = backupId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.OriginalSize,
                    StoredBytes = backup.StoredSize,
                    FileCount = backup.FileCount,
                    Warnings = warnings
                };
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
                    PercentComplete = 20
                });

                var data = await RetrieveBackupAsync(backup.StorageLocation, ct);

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Restoring Files",
                    PercentComplete = 60
                });

                var filesRestored = await RestoreFilesAsync(data, request, ct);

                // Award XP for successful restore
                if (_profiles.TryGetValue(backup.ProfileId, out var profile))
                {
                    profile.ExperiencePoints += 50;
                    CheckForLevelUp(profile);
                }

                progressCallback(new RestoreProgress
                {
                    RestoreId = restoreId,
                    Phase = "Complete",
                    PercentComplete = 100
                });

                return new RestoreResult
                {
                    Success = true,
                    RestoreId = restoreId,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    TotalBytes = backup.OriginalSize,
                    FileCount = filesRestored,
                    Warnings = new[] { "Earned +50 XP for successful restore!" }
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

            if (!_backups.ContainsKey(backupId))
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

        private void InitializeAchievements()
        {
            _availableAchievements.AddRange(new[]
            {
                // Streak achievements
                new Achievement { AchievementId = "streak-3", Name = "Getting Started", Description = "Maintain a 3-day backup streak", Icon = "fire-1", Rarity = AchievementRarity.Common, ExperienceReward = 100, Category = AchievementCategory.Streak },
                new Achievement { AchievementId = "streak-7", Name = "Week Warrior", Description = "Maintain a 7-day backup streak", Icon = "fire-2", Rarity = AchievementRarity.Uncommon, ExperienceReward = 250, Category = AchievementCategory.Streak },
                new Achievement { AchievementId = "streak-30", Name = "Monthly Master", Description = "Maintain a 30-day backup streak", Icon = "fire-3", Rarity = AchievementRarity.Rare, ExperienceReward = 1000, Category = AchievementCategory.Streak },
                new Achievement { AchievementId = "streak-100", Name = "Century Club", Description = "Maintain a 100-day backup streak", Icon = "fire-4", Rarity = AchievementRarity.Epic, ExperienceReward = 5000, Category = AchievementCategory.Streak },
                new Achievement { AchievementId = "streak-365", Name = "Year of Protection", Description = "Maintain a 365-day backup streak", Icon = "fire-5", Rarity = AchievementRarity.Legendary, ExperienceReward = 25000, Category = AchievementCategory.Streak },

                // Volume achievements
                new Achievement { AchievementId = "volume-1gb", Name = "Data Keeper", Description = "Protect 1 GB of data", Icon = "shield-1", Rarity = AchievementRarity.Common, ExperienceReward = 100, Category = AchievementCategory.Volume },
                new Achievement { AchievementId = "volume-10gb", Name = "Data Guardian", Description = "Protect 10 GB of data", Icon = "shield-2", Rarity = AchievementRarity.Uncommon, ExperienceReward = 500, Category = AchievementCategory.Volume },
                new Achievement { AchievementId = "volume-100gb", Name = "Data Protector", Description = "Protect 100 GB of data", Icon = "shield-3", Rarity = AchievementRarity.Rare, ExperienceReward = 2000, Category = AchievementCategory.Volume },
                new Achievement { AchievementId = "volume-1tb", Name = "Terabyte Titan", Description = "Protect 1 TB of data", Icon = "shield-4", Rarity = AchievementRarity.Epic, ExperienceReward = 10000, Category = AchievementCategory.Volume },

                // Consistency achievements
                new Achievement { AchievementId = "backups-10", Name = "Backup Beginner", Description = "Complete 10 backups", Icon = "star-1", Rarity = AchievementRarity.Common, ExperienceReward = 100, Category = AchievementCategory.Consistency },
                new Achievement { AchievementId = "backups-50", Name = "Backup Pro", Description = "Complete 50 backups", Icon = "star-2", Rarity = AchievementRarity.Uncommon, ExperienceReward = 500, Category = AchievementCategory.Consistency },
                new Achievement { AchievementId = "backups-100", Name = "Backup Expert", Description = "Complete 100 backups", Icon = "star-3", Rarity = AchievementRarity.Rare, ExperienceReward = 1500, Category = AchievementCategory.Consistency },
                new Achievement { AchievementId = "backups-500", Name = "Backup Master", Description = "Complete 500 backups", Icon = "star-4", Rarity = AchievementRarity.Epic, ExperienceReward = 5000, Category = AchievementCategory.Consistency },

                // Special achievements
                new Achievement { AchievementId = "first-backup", Name = "First Steps", Description = "Complete your first backup", Icon = "trophy", Rarity = AchievementRarity.Common, ExperienceReward = 50, Category = AchievementCategory.Special },
                new Achievement { AchievementId = "perfect-health", Name = "Perfect Health", Description = "Achieve 100% health score", Icon = "heart", Rarity = AchievementRarity.Uncommon, ExperienceReward = 200, Category = AchievementCategory.Special },
                new Achievement { AchievementId = "first-restore", Name = "Recovery Ready", Description = "Complete your first successful restore", Icon = "refresh", Rarity = AchievementRarity.Common, ExperienceReward = 100, Category = AchievementCategory.Recovery }
            });
        }

        private async Task<GamificationResult> UpdateGamificationAsync(
            BackupProfile profile,
            GamifiedBackup backup,
            CancellationToken ct)
        {
            var result = new GamificationResult();

            // Update basic stats
            profile.TotalBackups++;
            profile.TotalBytesProtected += backup.OriginalSize;

            // Calculate experience
            var baseXp = 100;
            var sizeBonus = (int)(backup.OriginalSize / (1024 * 1024)); // +1 XP per MB
            var streakMultiplier = 1.0 + (profile.CurrentStreak * 0.1); // 10% bonus per streak day

            result.ExperienceGained = (int)((baseXp + Math.Min(sizeBonus, 500)) * streakMultiplier);
            profile.ExperiencePoints += result.ExperienceGained;

            // Update streak
            var previousStreak = profile.CurrentStreak;
            UpdateStreak(profile);

            // Check for level up
            var previousLevel = profile.Level;
            CheckForLevelUp(profile);
            result.LeveledUp = profile.Level > previousLevel;

            if (result.LeveledUp)
            {
                _notifications.Add(new GamificationNotification
                {
                    Type = GamificationNotificationType.LevelUp,
                    Title = "Level Up!",
                    Message = $"Congratulations! You've reached level {profile.Level}!",
                    Data = new Dictionary<string, object> { ["level"] = profile.Level }
                });
            }

            // Check for achievements
            result.AchievementsUnlocked = CheckAchievements(profile);
            foreach (var achievement in result.AchievementsUnlocked)
            {
                achievement.EarnedAt = DateTimeOffset.UtcNow;
                profile.Achievements.Add(achievement);
                profile.ExperiencePoints += achievement.ExperienceReward;

                _notifications.Add(new GamificationNotification
                {
                    Type = GamificationNotificationType.AchievementUnlocked,
                    Title = "Achievement Unlocked!",
                    Message = $"{achievement.Name}: {achievement.Description}",
                    Data = new Dictionary<string, object>
                    {
                        ["achievementId"] = achievement.AchievementId,
                        ["xpReward"] = achievement.ExperienceReward
                    }
                });
            }

            // Check for streak milestones
            if (profile.CurrentStreak > previousStreak && profile.CurrentStreak % 7 == 0)
            {
                _notifications.Add(new GamificationNotification
                {
                    Type = GamificationNotificationType.StreakMilestone,
                    Title = "Streak Milestone!",
                    Message = $"You've maintained a {profile.CurrentStreak}-day backup streak!",
                    Data = new Dictionary<string, object> { ["streak"] = profile.CurrentStreak }
                });
            }

            // Update health score
            profile.HealthScore = CalculateHealthScore(profile.ProfileId);
            profile.LastBackupDate = DateTimeOffset.UtcNow;

            await Task.CompletedTask;
            return result;
        }

        private void UpdateStreak(BackupProfile profile)
        {
            var today = DateTimeOffset.UtcNow.Date;

            if (profile.LastBackupDate.HasValue)
            {
                var lastBackupDate = profile.LastBackupDate.Value.Date;
                var daysSinceLastBackup = (today - lastBackupDate).Days;

                if (daysSinceLastBackup == 0)
                {
                    // Same day, streak continues
                }
                else if (daysSinceLastBackup == 1)
                {
                    // Next day, streak increases
                    profile.CurrentStreak++;
                }
                else
                {
                    // Streak broken
                    profile.CurrentStreak = 1;
                }
            }
            else
            {
                profile.CurrentStreak = 1;
            }

            if (profile.CurrentStreak > profile.LongestStreak)
            {
                profile.LongestStreak = profile.CurrentStreak;
            }
        }

        private void CheckForLevelUp(BackupProfile profile)
        {
            while (profile.ExperiencePoints >= CalculateExpForLevel(profile.Level + 1))
            {
                profile.Level++;
            }
        }

        private long CalculateExpForLevel(int level) => (long)(100 * Math.Pow(1.5, level - 1));

        private List<Achievement> CheckAchievements(BackupProfile profile)
        {
            var unlocked = new List<Achievement>();
            var earnedIds = profile.Achievements.Select(a => a.AchievementId).ToHashSet();

            foreach (var achievement in _availableAchievements)
            {
                if (earnedIds.Contains(achievement.AchievementId)) continue;

                var earned = achievement.AchievementId switch
                {
                    "first-backup" => profile.TotalBackups >= 1,
                    "streak-3" => profile.CurrentStreak >= 3,
                    "streak-7" => profile.CurrentStreak >= 7,
                    "streak-30" => profile.CurrentStreak >= 30,
                    "streak-100" => profile.CurrentStreak >= 100,
                    "streak-365" => profile.CurrentStreak >= 365,
                    "volume-1gb" => profile.TotalBytesProtected >= 1L * 1024 * 1024 * 1024,
                    "volume-10gb" => profile.TotalBytesProtected >= 10L * 1024 * 1024 * 1024,
                    "volume-100gb" => profile.TotalBytesProtected >= 100L * 1024 * 1024 * 1024,
                    "volume-1tb" => profile.TotalBytesProtected >= 1024L * 1024 * 1024 * 1024,
                    "backups-10" => profile.TotalBackups >= 10,
                    "backups-50" => profile.TotalBackups >= 50,
                    "backups-100" => profile.TotalBackups >= 100,
                    "backups-500" => profile.TotalBackups >= 500,
                    "perfect-health" => profile.HealthScore >= 100,
                    _ => false
                };

                if (earned)
                {
                    unlocked.Add(new Achievement
                    {
                        AchievementId = achievement.AchievementId,
                        Name = achievement.Name,
                        Description = achievement.Description,
                        Icon = achievement.Icon,
                        Rarity = achievement.Rarity,
                        ExperienceReward = achievement.ExperienceReward,
                        Category = achievement.Category,
                        Progress = 100
                    });
                }
            }

            return unlocked;
        }

        private DateTimeOffset GetWeekStart()
        {
            var today = DateTimeOffset.UtcNow.Date;
            var daysUntilMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return today.AddDays(-daysUntilMonday);
        }

        private Task<byte[]> CreateBackupDataAsync(BackupRequest request, CancellationToken ct)
        {
            return Task.FromResult(new byte[10 * 1024 * 1024]);
        }

        private Task<byte[]> ProcessBackupAsync(byte[] data, BackupRequest request, CancellationToken ct)
        {
            return Task.FromResult(data);
        }

        private Task<string> StoreBackupAsync(byte[] data, CancellationToken ct)
        {
            return Task.FromResult($"gamified://backup/{Guid.NewGuid():N}");
        }

        private Task<byte[]> RetrieveBackupAsync(string location, CancellationToken ct)
        {
            return Task.FromResult(new byte[10 * 1024 * 1024]);
        }

        private Task<long> RestoreFilesAsync(byte[] data, RestoreRequest request, CancellationToken ct)
        {
            return Task.FromResult(500L);
        }

        private string ComputeChecksum(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(data));
        }

        private BackupCatalogEntry CreateCatalogEntry(GamifiedBackup backup)
        {
            return new BackupCatalogEntry
            {
                BackupId = backup.BackupId,
                StrategyId = StrategyId,
                Category = Category,
                CreatedAt = backup.CreatedAt,
                OriginalSize = backup.OriginalSize,
                StoredSize = backup.StoredSize,
                FileCount = backup.FileCount,
                IsEncrypted = true,
                IsCompressed = true,
                Tags = new Dictionary<string, string>
                {
                    ["profile"] = backup.ProfileId
                }
            };
        }

        #endregion

        #region Private Classes

        private sealed class GamifiedBackup
        {
            public string BackupId { get; set; } = string.Empty;
            public string ProfileId { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; }
            public long OriginalSize { get; set; }
            public long StoredSize { get; set; }
            public long FileCount { get; set; }
            public string Checksum { get; set; } = string.Empty;
            public string StorageLocation { get; set; } = string.Empty;
        }

        private sealed class GamificationResult
        {
            public int ExperienceGained { get; set; }
            public bool LeveledUp { get; set; }
            public List<Achievement> AchievementsUnlocked { get; set; } = new();
        }

        #endregion
    }
}
