namespace DataWarehouse.SDK.Contracts.Gaming;

/// <summary>
/// Describes the capabilities of a gaming service strategy.
/// </summary>
/// <param name="SupportsCloudSaves">
/// Indicates whether the strategy supports cloud-based game state persistence.
/// </param>
/// <param name="SupportsMultiplayer">
/// Indicates whether the strategy supports multiplayer session management and synchronization.
/// </param>
/// <param name="SupportsMatchmaking">
/// Indicates whether the strategy supports automated player matchmaking.
/// </param>
/// <param name="SupportsLeaderboards">
/// Indicates whether the strategy supports leaderboard tracking and rankings.
/// </param>
/// <param name="MaxPlayersPerSession">
/// The maximum number of players allowed in a single multiplayer session.
/// Null if there is no limit or multiplayer is not supported.
/// </param>
/// <param name="MaxSaveSize">
/// The maximum size of a single game save in bytes.
/// Null if there is no size limit or cloud saves are not supported.
/// </param>
/// <param name="SupportsVoiceChat">
/// Indicates whether the strategy provides voice chat functionality.
/// </param>
/// <param name="SupportsTextChat">
/// Indicates whether the strategy provides text chat functionality.
/// </param>
/// <param name="SupportsPartySystem">
/// Indicates whether the strategy supports creating player parties/groups before matchmaking.
/// </param>
/// <param name="SupportsSpectatorMode">
/// Indicates whether the strategy supports spectator mode for watching live matches.
/// </param>
/// <param name="SupportsCrossPlay">
/// Indicates whether the strategy supports cross-platform play.
/// </param>
/// <param name="SupportsProgressionTracking">
/// Indicates whether the strategy supports player progression, achievements, and unlockables.
/// </param>
/// <param name="SupportsInGamePurchases">
/// Indicates whether the strategy supports in-game purchase transactions.
/// </param>
/// <param name="MaxSaveSlots">
/// The maximum number of save slots per player.
/// Null if unlimited or cloud saves are not supported.
/// </param>
/// <param name="SupportedPlatforms">
/// The set of gaming platforms supported (e.g., "PC", "PS5", "Xbox", "Mobile").
/// Empty set if platform information is not available.
/// </param>
/// <remarks>
/// <para>
/// This record is immutable and should be constructed once during strategy initialization.
/// </para>
/// <para>
/// <strong>Example Usage:</strong>
/// <code>
/// var capabilities = new GamingCapabilities(
///     SupportsCloudSaves: true,
///     SupportsMultiplayer: true,
///     SupportsMatchmaking: true,
///     SupportsLeaderboards: true,
///     MaxPlayersPerSession: 64,
///     MaxSaveSize: 10_485_760, // 10 MB
///     SupportsVoiceChat: true,
///     SupportsTextChat: true,
///     SupportsPartySystem: true,
///     SupportsSpectatorMode: true,
///     SupportsCrossPlay: true,
///     SupportsProgressionTracking: true,
///     SupportsInGamePurchases: true,
///     MaxSaveSlots: 5,
///     SupportedPlatforms: new[] { "PC", "PS5", "Xbox", "Switch" }
/// );
/// </code>
/// </para>
/// </remarks>
public sealed record GamingCapabilities(
    bool SupportsCloudSaves,
    bool SupportsMultiplayer,
    bool SupportsMatchmaking,
    bool SupportsLeaderboards,
    int? MaxPlayersPerSession,
    long? MaxSaveSize,
    bool SupportsVoiceChat,
    bool SupportsTextChat,
    bool SupportsPartySystem,
    bool SupportsSpectatorMode,
    bool SupportsCrossPlay,
    bool SupportsProgressionTracking,
    bool SupportsInGamePurchases,
    int? MaxSaveSlots,
    IReadOnlySet<string> SupportedPlatforms)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GamingCapabilities"/> record with minimal capabilities.
    /// </summary>
    public GamingCapabilities()
        : this(
            SupportsCloudSaves: false,
            SupportsMultiplayer: false,
            SupportsMatchmaking: false,
            SupportsLeaderboards: false,
            MaxPlayersPerSession: null,
            MaxSaveSize: null,
            SupportsVoiceChat: false,
            SupportsTextChat: false,
            SupportsPartySystem: false,
            SupportsSpectatorMode: false,
            SupportsCrossPlay: false,
            SupportsProgressionTracking: false,
            SupportsInGamePurchases: false,
            MaxSaveSlots: null,
            SupportedPlatforms: new HashSet<string>())
    {
    }

    /// <summary>
    /// Determines whether the specified save size is within the maximum supported size.
    /// </summary>
    /// <param name="saveSize">The save size to check in bytes.</param>
    /// <returns>
    /// <c>true</c> if the save size is supported or no limit is defined; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsSaveSize(long saveSize)
    {
        if (!SupportsCloudSaves || MaxSaveSize is null)
            return SupportsCloudSaves;

        return saveSize <= MaxSaveSize.Value;
    }

    /// <summary>
    /// Determines whether the specified number of players is within the session limit.
    /// </summary>
    /// <param name="playerCount">The number of players to check.</param>
    /// <returns>
    /// <c>true</c> if the player count is supported or no limit is defined; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsPlayerCount(int playerCount)
    {
        if (!SupportsMultiplayer || MaxPlayersPerSession is null)
            return SupportsMultiplayer;

        return playerCount <= MaxPlayersPerSession.Value;
    }

    /// <summary>
    /// Determines whether the specified platform is supported.
    /// </summary>
    /// <param name="platform">The platform identifier to check (case-insensitive).</param>
    /// <returns>
    /// <c>true</c> if the platform is supported or no platforms are specified; otherwise, <c>false</c>.
    /// </returns>
    public bool SupportsPlatform(string platform)
    {
        if (SupportedPlatforms.Count == 0)
            return true;

        return SupportedPlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a capability score from 0-100 indicating the overall feature richness of the gaming service.
    /// </summary>
    public int CapabilityScore
    {
        get
        {
            int score = 0;
            if (SupportsCloudSaves) score += 10;
            if (SupportsMultiplayer) score += 15;
            if (SupportsMatchmaking) score += 12;
            if (SupportsLeaderboards) score += 8;
            if (SupportsVoiceChat) score += 10;
            if (SupportsTextChat) score += 6;
            if (SupportsPartySystem) score += 8;
            if (SupportsSpectatorMode) score += 6;
            if (SupportsCrossPlay) score += 10;
            if (SupportsProgressionTracking) score += 8;
            if (SupportsInGamePurchases) score += 7;
            return score;
        }
    }

    /// <summary>
    /// Indicates whether the service supports any form of social/communication features.
    /// </summary>
    public bool SupportsSocialFeatures => SupportsVoiceChat || SupportsTextChat || SupportsPartySystem;

    /// <summary>
    /// Indicates whether the service provides a complete multiplayer gaming backend.
    /// </summary>
    public bool IsFullMultiplayerBackend =>
        SupportsMultiplayer && SupportsMatchmaking && SupportsCloudSaves && SupportsLeaderboards;
}
