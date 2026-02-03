namespace DataWarehouse.SDK.Contracts.Gaming;

/// <summary>
/// Represents a player's saved game state with versioning and metadata.
/// </summary>
/// <param name="PlayerId">The unique identifier of the player who owns this save.</param>
/// <param name="SlotId">The save slot identifier (for games with multiple save slots).</param>
/// <param name="Data">The serialized game state data.</param>
/// <param name="Version">Version number for conflict detection and resolution.</param>
/// <param name="SavedAt">The timestamp when this state was saved.</param>
/// <param name="GameVersion">The game version that created this save.</param>
/// <param name="Metadata">Optional metadata about the save (e.g., level, playtime, location).</param>
/// <param name="Checksum">Optional integrity checksum for corruption detection.</param>
public sealed record GameState(
    string PlayerId,
    string SlotId,
    byte[] Data,
    long Version,
    DateTimeOffset SavedAt,
    string GameVersion,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? Checksum = null)
{
    /// <summary>
    /// Gets the size of the game state data in bytes.
    /// </summary>
    public long SizeInBytes => Data.LongLength;

    /// <summary>
    /// Returns a human-readable description of the game state.
    /// </summary>
    public override string ToString()
        => $"GameState[Player:{PlayerId}, Slot:{SlotId}, Version:{Version}, Size:{SizeInBytes:N0} bytes, Saved:{SavedAt:yyyy-MM-dd HH:mm:ss}]";

    /// <summary>
    /// Creates a new game state with an incremented version number.
    /// </summary>
    public GameState WithNewVersion(byte[] newData)
        => this with { Data = newData, Version = Version + 1, SavedAt = DateTimeOffset.UtcNow };
}

/// <summary>
/// Represents an active player session within a game.
/// </summary>
/// <param name="PlayerId">The unique identifier of the player.</param>
/// <param name="PlayerName">The display name of the player.</param>
/// <param name="ConnectionId">The network connection identifier for this player.</param>
/// <param name="JoinedAt">The timestamp when the player joined.</param>
/// <param name="Latency">The network latency in milliseconds.</param>
/// <param name="IsHost">Indicates whether this player is the session host.</param>
/// <param name="IsReady">Indicates whether the player is ready to start/continue.</param>
/// <param name="CustomData">Optional custom player data (team, character, etc.).</param>
public sealed record PlayerSession(
    string PlayerId,
    string PlayerName,
    string ConnectionId,
    DateTimeOffset JoinedAt,
    int Latency,
    bool IsHost,
    bool IsReady,
    IReadOnlyDictionary<string, string>? CustomData = null)
{
    /// <summary>
    /// Returns a human-readable description of the player session.
    /// </summary>
    public override string ToString()
        => $"{PlayerName} ({PlayerId}) [{Latency}ms] {(IsHost ? "[HOST]" : "")} {(IsReady ? "[READY]" : "[NOT READY]")}";
}

/// <summary>
/// Represents a matchmaking request with player preferences and criteria.
/// </summary>
/// <param name="PlayerId">The unique identifier of the player requesting matchmaking.</param>
/// <param name="GameMode">The desired game mode (e.g., "deathmatch", "capture-flag", "co-op").</param>
/// <param name="SkillRating">The player's skill rating for skill-based matchmaking.</param>
/// <param name="Region">Preferred geographic region for low-latency matching.</param>
/// <param name="PartyMembers">Player IDs of party members who should be matched together.</param>
/// <param name="MaxLatency">Maximum acceptable latency in milliseconds.</param>
/// <param name="Timeout">Maximum time to wait for a match before giving up.</param>
/// <param name="CustomCriteria">Additional custom matchmaking criteria.</param>
public sealed record MatchRequest(
    string PlayerId,
    string GameMode,
    int? SkillRating = null,
    string? Region = null,
    IReadOnlyCollection<string>? PartyMembers = null,
    int MaxLatency = 150,
    TimeSpan Timeout = default,
    IReadOnlyDictionary<string, string>? CustomCriteria = null)
{
    /// <summary>
    /// Gets the total party size including the requesting player.
    /// </summary>
    public int PartySize => 1 + (PartyMembers?.Count ?? 0);

    /// <summary>
    /// Gets the effective timeout, using 60 seconds if not specified.
    /// </summary>
    public TimeSpan EffectiveTimeout => Timeout == default ? TimeSpan.FromSeconds(60) : Timeout;
}

/// <summary>
/// Represents a leaderboard entry with player score and ranking information.
/// </summary>
/// <param name="LeaderboardId">The unique identifier of the leaderboard.</param>
/// <param name="PlayerId">The unique identifier of the player.</param>
/// <param name="PlayerName">The display name of the player.</param>
/// <param name="Score">The player's score value.</param>
/// <param name="Rank">The player's current rank (1 = top position).</param>
/// <param name="SubmittedAt">The timestamp when the score was submitted.</param>
/// <param name="Metadata">Optional metadata associated with the score (replay URL, stats, etc.).</param>
public sealed record LeaderboardEntry(
    string LeaderboardId,
    string PlayerId,
    string PlayerName,
    long Score,
    int Rank,
    DateTimeOffset SubmittedAt,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// Returns a human-readable description of the leaderboard entry.
    /// </summary>
    public override string ToString()
        => $"#{Rank}: {PlayerName} - {Score:N0} points";
}

/// <summary>
/// Represents an active multiplayer game session.
/// </summary>
/// <param name="SessionId">The unique identifier of the multiplayer session.</param>
/// <param name="HostPlayerId">The player ID of the session host.</param>
/// <param name="GameMode">The game mode being played.</param>
/// <param name="MaxPlayers">The maximum number of players allowed.</param>
/// <param name="CurrentPlayers">The collection of currently connected players.</param>
/// <param name="IsPrivate">Indicates whether the session is private (not joinable via matchmaking).</param>
/// <param name="IsStarted">Indicates whether the game has started.</param>
/// <param name="CreatedAt">The timestamp when the session was created.</param>
/// <param name="ServerRegion">The geographic region where the game server is located.</param>
/// <param name="ServerEndpoint">The network endpoint (IP:port) for connecting to the game server.</param>
/// <param name="Metadata">Optional session metadata (map, rules, custom settings, etc.).</param>
public sealed record MultiplayerSession(
    string SessionId,
    string HostPlayerId,
    string GameMode,
    int MaxPlayers,
    IReadOnlyCollection<PlayerSession> CurrentPlayers,
    bool IsPrivate,
    bool IsStarted,
    DateTimeOffset CreatedAt,
    string? ServerRegion = null,
    string? ServerEndpoint = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// Gets the current number of players in the session.
    /// </summary>
    public int PlayerCount => CurrentPlayers.Count;

    /// <summary>
    /// Gets the number of available slots remaining.
    /// </summary>
    public int AvailableSlots => MaxPlayers - PlayerCount;

    /// <summary>
    /// Indicates whether the session is full.
    /// </summary>
    public bool IsFull => PlayerCount >= MaxPlayers;

    /// <summary>
    /// Indicates whether all players are ready to start.
    /// </summary>
    public bool AllPlayersReady => CurrentPlayers.All(p => p.IsReady);

    /// <summary>
    /// Gets the average latency of all players in milliseconds.
    /// </summary>
    public double AverageLatency => CurrentPlayers.Any() ? CurrentPlayers.Average(p => p.Latency) : 0;

    /// <summary>
    /// Returns a human-readable description of the multiplayer session.
    /// </summary>
    public override string ToString()
        => $"Session[{SessionId}]: {GameMode} - {PlayerCount}/{MaxPlayers} players {(IsStarted ? "[IN PROGRESS]" : "[WAITING]")}";
}

/// <summary>
/// Represents a synchronization message for multiplayer game state updates.
/// </summary>
/// <param name="MessageType">The type of synchronization message (e.g., "state_update", "player_action", "event").</param>
/// <param name="SenderId">The player ID of the sender.</param>
/// <param name="Timestamp">The timestamp when the message was created.</param>
/// <param name="Payload">The message payload containing game state data.</param>
/// <param name="SequenceNumber">Optional sequence number for ordered delivery.</param>
/// <param name="RequiresAck">Indicates whether this message requires acknowledgment.</param>
public sealed record SyncMessage(
    string MessageType,
    string SenderId,
    DateTimeOffset Timestamp,
    byte[] Payload,
    long? SequenceNumber = null,
    bool RequiresAck = false)
{
    /// <summary>
    /// Gets the size of the message payload in bytes.
    /// </summary>
    public long PayloadSize => Payload.LongLength;

    /// <summary>
    /// Returns a human-readable description of the sync message.
    /// </summary>
    public override string ToString()
        => $"SyncMessage[{MessageType}] from {SenderId}, {PayloadSize} bytes, Seq:{SequenceNumber ?? -1}";
}

/// <summary>
/// Represents matchmaking criteria categories for organizing matchmaking rules.
/// </summary>
public enum MatchmakingCriteria
{
    /// <summary>
    /// Match based on player skill rating (ELO, MMR, etc.).
    /// </summary>
    SkillBased = 0,

    /// <summary>
    /// Match based on geographic region for low latency.
    /// </summary>
    RegionBased = 1,

    /// <summary>
    /// Match based on game mode preference.
    /// </summary>
    GameModeBased = 2,

    /// <summary>
    /// Match based on similar player level or experience.
    /// </summary>
    LevelBased = 3,

    /// <summary>
    /// Match based on custom criteria defined by the game.
    /// </summary>
    Custom = 99
}

/// <summary>
/// Represents the state of a multiplayer session.
/// </summary>
public enum SessionState
{
    /// <summary>
    /// Session is being created.
    /// </summary>
    Creating = 0,

    /// <summary>
    /// Session is waiting for players to join.
    /// </summary>
    WaitingForPlayers = 1,

    /// <summary>
    /// Session has enough players and is ready to start.
    /// </summary>
    Ready = 2,

    /// <summary>
    /// Game is in progress.
    /// </summary>
    InProgress = 3,

    /// <summary>
    /// Game has ended but session is still active (for results, rematch, etc.).
    /// </summary>
    Ended = 4,

    /// <summary>
    /// Session is being terminated.
    /// </summary>
    Closing = 5,

    /// <summary>
    /// Session has been terminated and is no longer accessible.
    /// </summary>
    Closed = 6
}

/// <summary>
/// Represents game save conflict resolution strategies.
/// </summary>
public enum SaveConflictResolution
{
    /// <summary>
    /// Keep the local save, overwriting the remote save.
    /// </summary>
    PreferLocal = 0,

    /// <summary>
    /// Keep the remote save, discarding the local save.
    /// </summary>
    PreferRemote = 1,

    /// <summary>
    /// Keep the save with the newer timestamp.
    /// </summary>
    PreferNewer = 2,

    /// <summary>
    /// Keep the save with the higher version number.
    /// </summary>
    PreferHigherVersion = 3,

    /// <summary>
    /// Prompt the user to choose which save to keep.
    /// </summary>
    PromptUser = 4,

    /// <summary>
    /// Attempt to merge both saves (requires game-specific logic).
    /// </summary>
    Merge = 5
}
