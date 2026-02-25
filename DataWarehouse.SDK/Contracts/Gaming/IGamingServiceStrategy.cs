namespace DataWarehouse.SDK.Contracts.Gaming;

/// <summary>
/// Defines the contract for gaming service strategies that provide cloud saves,
/// multiplayer synchronization, matchmaking, and leaderboard functionality.
/// </summary>
/// <remarks>
/// <para>
/// Implementations provide gaming backend services including:
/// <list type="bullet">
/// <item><description>Cloud-based game state persistence and synchronization</description></item>
/// <item><description>Real-time multiplayer session management</description></item>
/// <item><description>Player matchmaking based on skill, region, or custom criteria</description></item>
/// <item><description>Leaderboard tracking and ranking systems</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Strategy Examples:</strong>
/// <list type="bullet">
/// <item><description>Steam Cloud Save integration</description></item>
/// <item><description>PlayStation Network services</description></item>
/// <item><description>Xbox Live services</description></item>
/// <item><description>Epic Online Services (EOS)</description></item>
/// <item><description>PlayFab backend integration</description></item>
/// <item><description>Custom gaming backend implementation</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> Implementations should be thread-safe as multiple game instances
/// or players may interact with the service concurrently.
/// </para>
/// </remarks>
public interface IGamingServiceStrategy
{
    /// <summary>
    /// Gets the capabilities of this gaming service strategy, including cloud save support,
    /// multiplayer features, matchmaking, and technical limits.
    /// </summary>
    /// <value>
    /// A <see cref="GamingCapabilities"/> instance describing the strategy's capabilities.
    /// </value>
    GamingCapabilities Capabilities { get; }

    /// <summary>
    /// Saves the current game state to cloud storage asynchronously.
    /// </summary>
    /// <param name="playerId">The unique identifier of the player.</param>
    /// <param name="gameState">The game state data to save.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result indicates
    /// whether the save operation was successful.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="playerId"/> or <paramref name="gameState"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the game state size exceeds <see cref="GamingCapabilities.MaxSaveSize"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when cloud saves are not supported by this strategy.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the save operation fails due to network errors or quota limits.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Game state is automatically versioned to support conflict resolution. If a newer
    /// version exists remotely, the save may fail and require conflict resolution.
    /// </para>
    /// <para>
    /// Consider compressing large game states before saving to reduce bandwidth usage.
    /// </para>
    /// </remarks>
    Task<bool> SaveGameStateAsync(
        string playerId,
        GameState gameState,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the player's game state from cloud storage asynchronously.
    /// </summary>
    /// <param name="playerId">The unique identifier of the player.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the loaded game state, or null if no saved state exists for this player.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="playerId"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when cloud saves are not supported by this strategy.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the load operation fails due to network errors or corrupted data.
    /// </exception>
    /// <remarks>
    /// <para>
    /// If multiple save slots exist, this method returns the most recent save.
    /// Use overloads with slot parameters for multi-slot save systems.
    /// </para>
    /// <para>
    /// The returned game state includes version information for conflict detection.
    /// </para>
    /// </remarks>
    Task<GameState?> LoadGameStateAsync(
        string playerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes multiplayer game data for a session asynchronously.
    /// </summary>
    /// <param name="session">The multiplayer session to synchronize.</param>
    /// <param name="message">The synchronization message containing game state updates.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="session"/> or <paramref name="message"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when multiplayer synchronization is not supported by this strategy.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the session is no longer active or the player is not a participant.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method broadcasts game state updates to all active players in the session.
    /// Updates are typically delivered within 100-200ms on good network connections.
    /// </para>
    /// <para>
    /// For real-time games, consider using UDP-based custom protocols for lower latency.
    /// </para>
    /// </remarks>
    Task SyncMultiplayerAsync(
        MultiplayerSession session,
        SyncMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds and joins a multiplayer match based on matchmaking criteria.
    /// </summary>
    /// <param name="request">The matchmaking request with player preferences and criteria.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the matched multiplayer session, or null if no suitable match was found within timeout.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when matchmaking is not supported by this strategy.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when matchmaking fails due to service errors or the player is already in a session.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Matchmaking typically considers factors like:
    /// <list type="bullet">
    /// <item><description>Player skill rating (ELO, MMR)</description></item>
    /// <item><description>Geographic region for latency optimization</description></item>
    /// <item><description>Preferred game mode</description></item>
    /// <item><description>Party size and composition</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// If no match is found immediately, the player is placed in a matchmaking queue.
    /// The operation may take several seconds to minutes depending on player population.
    /// </para>
    /// </remarks>
    Task<MultiplayerSession?> MatchmakeAsync(
        MatchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new multiplayer session for hosting or private matches.
    /// </summary>
    /// <param name="hostPlayerId">The player ID of the session host.</param>
    /// <param name="maxPlayers">The maximum number of players allowed in the session.</param>
    /// <param name="isPrivate">Whether the session is private (not joinable via matchmaking).</param>
    /// <param name="metadata">Optional session metadata (game mode, map, rules, etc.).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the newly created multiplayer session.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="hostPlayerId"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxPlayers"/> exceeds <see cref="GamingCapabilities.MaxPlayersPerSession"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when multiplayer sessions are not supported by this strategy.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The host player is automatically added to the session upon creation.
    /// Other players can join via session ID or matchmaking.
    /// </para>
    /// </remarks>
    Task<MultiplayerSession> CreateSessionAsync(
        string hostPlayerId,
        int maxPlayers,
        bool isPrivate = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves leaderboard entries for the specified leaderboard.
    /// </summary>
    /// <param name="leaderboardId">The unique identifier of the leaderboard.</param>
    /// <param name="topCount">The number of top entries to retrieve.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the leaderboard entries ranked by score.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="leaderboardId"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when leaderboards are not supported by this strategy.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Leaderboard entries are typically cached and updated periodically (every few minutes).
    /// For real-time rankings, consider using separate APIs if available.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(
        string leaderboardId,
        int topCount = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a score to the specified leaderboard.
    /// </summary>
    /// <param name="leaderboardId">The unique identifier of the leaderboard.</param>
    /// <param name="playerId">The player submitting the score.</param>
    /// <param name="score">The score value to submit.</param>
    /// <param name="metadata">Optional metadata associated with the score (e.g., replay data).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains
    /// the player's new leaderboard entry with updated rank.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="leaderboardId"/> or <paramref name="playerId"/> is null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when leaderboards are not supported by this strategy.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The strategy determines whether to keep the highest score, latest score, or accumulate scores.
    /// Check the leaderboard configuration for score submission behavior.
    /// </para>
    /// </remarks>
    Task<LeaderboardEntry> SubmitScoreAsync(
        string leaderboardId,
        string playerId,
        long score,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);
}
