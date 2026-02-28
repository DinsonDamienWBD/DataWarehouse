using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Distributed;

/// <summary>
/// BitTorrent-style peer-to-peer swarm transfer strategy that splits data into pieces
/// and distributes downloads across a peer swarm with rarest-first piece selection,
/// SHA-256 per-piece integrity verification, and bounded-channel backpressure.
/// </summary>
/// <remarks>
/// <para>
/// Data is split into fixed-size pieces (default 256 KB). Each piece is hashed with SHA-256
/// before the transfer begins. A tracker endpoint is used to discover peers and their piece
/// availability bitmaps. Pieces are downloaded concurrently from multiple peers, with a
/// rarest-first selection algorithm that prioritizes pieces available from fewer peers.
/// </para>
/// <para>
/// Backpressure is enforced via a <see cref="Channel{T}"/> bounded to 64 pending piece indices.
/// This prevents fast peers from queuing unbounded work that overwhelms slow peers.
/// A <see cref="SemaphoreSlim"/> with capacity 8 limits total concurrent piece downloads.
/// </para>
/// <para>
/// If a downloaded piece fails SHA-256 verification, the offending peer is banned and the
/// piece is re-queued for download from a different peer. After all pieces are downloaded,
/// the strategy registers itself as a seeder with the tracker.
/// </para>
/// </remarks>
internal sealed class P2PSwarmStrategy : DataTransitStrategyBase
{
    /// <summary>
    /// Default piece size in bytes (256 KB).
    /// </summary>
    private const int DefaultPieceSize = 256 * 1024;

    /// <summary>
    /// Maximum number of piece indices that can be queued in the bounded channel.
    /// Prevents unbounded memory growth when piece production outpaces consumption.
    /// </summary>
    private const int PieceQueueCapacity = 64;

    /// <summary>
    /// Maximum number of concurrent piece downloads across all peers.
    /// </summary>
    private const int MaxConcurrentDownloads = 8;

    /// <summary>
    /// Maximum number of concurrent downloads allowed per individual peer.
    /// </summary>
    private const int ConcurrentDownloadLimitPerPeer = 4;

    /// <summary>
    /// Thread-safe storage of active swarm states keyed by transfer ID.
    /// </summary>
    private readonly BoundedDictionary<string, SwarmState> _swarmStates = new BoundedDictionary<string, SwarmState>(1000);

    /// <summary>
    /// Set of banned peer IDs that failed SHA-256 integrity verification.
    /// </summary>
    private readonly BoundedDictionary<string, bool> _bannedPeers = new BoundedDictionary<string, bool>(1000);

    /// <summary>
    /// HTTP client used for tracker communication and piece downloads.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "transit-p2p-swarm";

    /// <inheritdoc/>
    public override string Name => "P2P Swarm Transfer";

    /// <inheritdoc/>
    public override TransitCapabilities Capabilities => new()
    {
        SupportsP2P = true,
        SupportsStreaming = true,
        SupportsResumable = true,
        SupportsDelta = false,
        SupportsMultiPath = false,
        SupportsOffline = false,
        SupportsCompression = false,
        SupportsEncryption = false,
        MaxTransferSizeBytes = long.MaxValue,
        SupportedProtocols = ["p2p", "swarm", "http", "https"]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="P2PSwarmStrategy"/> class.
    /// Configures the internal <see cref="HttpClient"/> with connection pooling and
    /// a 30-second connect timeout for peer and tracker communication.
    /// </summary>
    public P2PSwarmStrategy()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = MaxConcurrentDownloads * 2
        };

        _httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc/>
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            // Check if the tracker endpoint is reachable
            var trackerUri = new Uri(endpoint.Uri, "swarm/health");
            using var request = new HttpRequestMessage(HttpMethod.Head, trackerUri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            if (!string.IsNullOrEmpty(endpoint.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// Transfers data using BitTorrent-style piece-based distribution across a peer swarm.
    /// Splits the source data into SHA-256-hashed pieces, discovers peers via a tracker,
    /// and downloads pieces concurrently with rarest-first selection and bounded backpressure.
    /// </summary>
    public override async Task<TransitResult> TransferAsync(
        TransitRequest request,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transferId = request.TransferId;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveTransferCancellations[transferId] = cts;
        IncrementActiveTransfers();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var totalSize = DetermineTotalSize(request);
            var pieceSize = DeterminePieceSize(request);
            var pieceCount = (int)Math.Ceiling((double)totalSize / pieceSize);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Computing piece hashes",
                PercentComplete = 0,
                TotalBytes = totalSize
            });

            // Step 1-2: Split data into pieces and compute SHA-256 hashes
            var pieces = await ComputePieceHashesAsync(request.DataStream, totalSize, pieceSize, pieceCount, cts.Token);

            // Create bounded channel for piece queue (backpressure)
            var pieceChannel = Channel.CreateBounded<int>(new BoundedChannelOptions(PieceQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            // Initialize swarm state
            var swarmState = new SwarmState
            {
                TransferId = transferId,
                TotalSize = totalSize,
                PieceSize = pieceSize,
                Pieces = pieces,
                Peers = new BoundedDictionary<string, SwarmPeer>(1000),
                PieceQueue = pieceChannel,
                ConcurrentDownloadLimit = ConcurrentDownloadLimitPerPeer,
                Request = request
            };
            _swarmStates[transferId] = swarmState;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Discovering peers",
                PercentComplete = 0,
                TotalBytes = totalSize
            });

            // Step 3: Peer discovery via tracker
            await DiscoverPeersAsync(swarmState, request.Destination, cts.Token);

            if (swarmState.Peers.IsEmpty)
            {
                throw new InvalidOperationException(
                    "No peers discovered from tracker. Cannot complete P2P swarm transfer.");
            }

            // Step 5: Enqueue all needed pieces
            var enqueueTask = EnqueuePiecesAsync(swarmState, pieceChannel.Writer, cts.Token);

            // Step 6: Download loop with concurrency control
            var downloadedBytes = await DownloadPiecesFromSwarmAsync(
                swarmState, pieceChannel.Reader, progress, cts.Token);

            await enqueueTask;

            // Step 7: Register as seeder
            await RegisterAsSeederAsync(swarmState, request.Destination, cts.Token);

            stopwatch.Stop();

            RecordTransferSuccess(downloadedBytes);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = downloadedBytes,
                TotalBytes = totalSize,
                PercentComplete = 100.0,
                CurrentPhase = "Completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = downloadedBytes,
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId,
                Metadata = new Dictionary<string, string>
                {
                    ["peerCount"] = swarmState.Peers.Count.ToString(),
                    ["pieceCount"] = pieceCount.ToString(),
                    ["pieceSize"] = pieceSize.ToString(),
                    ["bannedPeers"] = _bannedPeers.Count.ToString()
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            RecordTransferFailure();

            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                BytesTransferred = 0,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message,
                StrategyUsed = StrategyId
            };
        }
        finally
        {
            DecrementActiveTransfers();
            ActiveTransferCancellations.TryRemove(transferId, out _);
            cts.Dispose();
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// Resumes an interrupted P2P swarm transfer by re-announcing to the tracker,
    /// enqueuing only non-downloaded pieces, and resuming the download loop.
    /// </summary>
    public override async Task<TransitResult> ResumeTransferAsync(
        string transferId,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);

        if (!_swarmStates.TryGetValue(transferId, out var swarmState))
        {
            throw new ArgumentException(
                $"No swarm state found for transfer '{transferId}'. Cannot resume.",
                nameof(transferId));
        }

        if (swarmState.Request is null)
        {
            throw new InvalidOperationException(
                $"Transfer '{transferId}' has no stored request context. Cannot resume.");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveTransferCancellations[transferId] = cts;
        IncrementActiveTransfers();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Count previously downloaded pieces
            var previouslyDownloaded = swarmState.Pieces
                .Where(p => p.Downloaded)
                .Sum(p => (long)p.Size);
            var completedPieceCount = swarmState.Pieces.Count(p => p.Downloaded);
            var totalPieces = swarmState.Pieces.Length;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = previouslyDownloaded,
                TotalBytes = swarmState.TotalSize,
                PercentComplete = totalPieces > 0 ? (double)completedPieceCount / totalPieces * 100.0 : 0,
                CurrentPhase = $"Resuming from piece {completedPieceCount}/{totalPieces}"
            });

            // Re-announce to tracker to refresh peer list
            await DiscoverPeersAsync(swarmState, swarmState.Request.Destination, cts.Token);

            // Create new bounded channel for remaining pieces
            var pieceChannel = Channel.CreateBounded<int>(new BoundedChannelOptions(PieceQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });
            swarmState.PieceQueue = pieceChannel;

            // Enqueue only non-downloaded pieces
            var enqueueTask = EnqueuePiecesAsync(swarmState, pieceChannel.Writer, cts.Token);

            // Resume download loop
            var downloadedBytes = await DownloadPiecesFromSwarmAsync(
                swarmState, pieceChannel.Reader, progress, cts.Token);

            await enqueueTask;

            // Register as seeder
            await RegisterAsSeederAsync(swarmState, swarmState.Request.Destination, cts.Token);

            stopwatch.Stop();

            var totalBytes = previouslyDownloaded + downloadedBytes;
            RecordTransferSuccess(totalBytes);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytes,
                TotalBytes = swarmState.TotalSize,
                PercentComplete = 100.0,
                CurrentPhase = "Resumed transfer completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = totalBytes,
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId,
                Metadata = new Dictionary<string, string>
                {
                    ["resumed"] = "true",
                    ["skippedPieces"] = completedPieceCount.ToString(),
                    ["downloadedPieces"] = (totalPieces - completedPieceCount).ToString(),
                    ["peerCount"] = swarmState.Peers.Count.ToString()
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            RecordTransferFailure();

            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                BytesTransferred = 0,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message,
                StrategyUsed = StrategyId
            };
        }
        finally
        {
            DecrementActiveTransfers();
            ActiveTransferCancellations.TryRemove(transferId, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Computes SHA-256 hashes for each piece of the source data stream.
    /// The stream is read sequentially and each piece is hashed independently.
    /// </summary>
    /// <param name="stream">The source data stream. Must be seekable.</param>
    /// <param name="totalSize">The total data size in bytes.</param>
    /// <param name="pieceSize">The size of each piece in bytes.</param>
    /// <param name="pieceCount">The total number of pieces.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of <see cref="PieceInfo"/> with SHA-256 hashes computed for each piece.</returns>
    private static async Task<PieceInfo[]> ComputePieceHashesAsync(
        Stream? stream,
        long totalSize,
        int pieceSize,
        int pieceCount,
        CancellationToken ct)
    {
        if (stream is null)
        {
            throw new InvalidOperationException(
                "No data stream available. P2P swarm transfer requires a source data stream for piece hashing.");
        }

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var pieces = new PieceInfo[pieceCount];
        var buffer = new byte[pieceSize];

        for (var i = 0; i < pieceCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var offset = (long)i * pieceSize;
            var remaining = totalSize - offset;
            var currentSize = (int)Math.Min(remaining, pieceSize);

            var totalRead = 0;
            while (totalRead < currentSize)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, currentSize - totalRead), ct);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            // Compute SHA-256 hash for this piece
            var hash = SHA256.HashData(buffer.AsSpan(0, totalRead));

            pieces[i] = new PieceInfo(
                Index: i,
                Offset: offset,
                Size: totalRead,
                Sha256Hash: hash,
                Downloaded: false);
        }

        return pieces;
    }

    /// <summary>
    /// Discovers peers via the tracker endpoint by announcing this transfer.
    /// The tracker returns a list of peers with their piece availability bitmaps.
    /// </summary>
    /// <param name="state">The swarm state containing transfer metadata.</param>
    /// <param name="destination">The destination endpoint containing the tracker URI.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task DiscoverPeersAsync(SwarmState state, TransitEndpoint destination, CancellationToken ct)
    {
        var announceUri = new Uri(destination.Uri, $"swarm/announce");

        var announcePayload = JsonSerializer.Serialize(new
        {
            state.TransferId,
            PieceCount = state.Pieces.Length,
            state.TotalSize,
            PieceSize = state.PieceSize
        });

        using var content = new StringContent(announcePayload, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, announceUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        if (!string.IsNullOrEmpty(destination.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var peerList = JsonSerializer.Deserialize<TrackerResponse>(responseBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (peerList?.Peers is not null)
        {
            foreach (var trackerPeer in peerList.Peers)
            {
                if (_bannedPeers.ContainsKey(trackerPeer.PeerId))
                {
                    continue; // Skip banned peers
                }

                var bitmap = trackerPeer.PieceBitmap ?? new bool[state.Pieces.Length];
                var peer = new SwarmPeer(
                    PeerId: trackerPeer.PeerId,
                    EndpointUri: trackerPeer.EndpointUri,
                    PieceBitmap: bitmap,
                    ActiveDownloads: 0,
                    BytesDownloaded: 0,
                    LastSeen: DateTime.UtcNow);

                state.Peers[trackerPeer.PeerId] = peer;
            }
        }
    }

    /// <summary>
    /// Enqueues piece indices into the bounded channel. Only non-downloaded pieces are enqueued.
    /// The channel writer is completed after all pieces are enqueued so that consumers
    /// know when the entire piece set has been queued.
    /// </summary>
    /// <param name="state">The swarm state containing piece information.</param>
    /// <param name="writer">The channel writer for piece indices.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task EnqueuePiecesAsync(
        SwarmState state,
        ChannelWriter<int> writer,
        CancellationToken ct)
    {
        try
        {
            // Sort pieces by rarity (fewest peers having the piece first)
            var piecesByRarity = Enumerable.Range(0, state.Pieces.Length)
                .Where(i => !state.Pieces[i].Downloaded)
                .OrderBy(i => state.Peers.Values.Count(p => p.PieceBitmap.Length > i && p.PieceBitmap[i]))
                .ToList();

            foreach (var pieceIndex in piecesByRarity)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteAsync(pieceIndex, ct);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Downloads pieces from the swarm using concurrent tasks controlled by a semaphore.
    /// Each piece is downloaded from the peer with the lowest active download count that
    /// has the piece available. Downloaded pieces are verified against their SHA-256 hash;
    /// mismatches cause the offending peer to be banned and the piece to be re-queued.
    /// </summary>
    /// <param name="state">The swarm state containing peers and piece metadata.</param>
    /// <param name="reader">The channel reader providing piece indices to download.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total bytes downloaded across all pieces in this session.</returns>
    private async Task<long> DownloadPiecesFromSwarmAsync(
        SwarmState state,
        ChannelReader<int> reader,
        IProgress<TransitProgress>? progress,
        CancellationToken ct)
    {
        long totalBytesDownloaded = 0;
        var completedPieces = state.Pieces.Count(p => p.Downloaded);
        var totalPieces = state.Pieces.Length;
        var semaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);
        var downloadTasks = new List<Task>();

        // Pieces that need re-queuing after hash mismatch
        var requeueList = new ConcurrentBag<int>();

        await foreach (var pieceIndex in reader.ReadAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            // Skip already downloaded pieces (could happen on resume re-queue)
            if (state.Pieces[pieceIndex].Downloaded)
            {
                continue;
            }

            await semaphore.WaitAsync(ct);

            var capturedIndex = pieceIndex;
            var task = Task.Run(async () =>
            {
                try
                {
                    var piece = state.Pieces[capturedIndex];

                    // Select peer: has the piece AND fewest active downloads
                    var selectedPeer = SelectPeer(state, capturedIndex);
                    if (selectedPeer is null)
                    {
                        // No peer available for this piece, re-queue
                        requeueList.Add(capturedIndex);
                        return;
                    }

                    // Increment active downloads for this peer
                    var updatedPeer = selectedPeer with { ActiveDownloads = selectedPeer.ActiveDownloads + 1 };
                    state.Peers[selectedPeer.PeerId] = updatedPeer;

                    try
                    {
                        // Download piece from selected peer
                        var pieceData = await DownloadPieceFromPeerAsync(
                            state.TransferId, capturedIndex, selectedPeer.EndpointUri, ct);

                        // Verify SHA-256 hash
                        var actualHash = SHA256.HashData(pieceData);
                        if (!actualHash.AsSpan().SequenceEqual(piece.Sha256Hash))
                        {
                            // Hash mismatch: ban peer, re-queue piece
                            _bannedPeers[selectedPeer.PeerId] = true;
                            state.Peers.TryRemove(selectedPeer.PeerId, out _);
                            requeueList.Add(capturedIndex);
                            return;
                        }

                        // Mark piece as downloaded
                        state.Pieces[capturedIndex] = piece with { Downloaded = true };
                        Interlocked.Add(ref totalBytesDownloaded, pieceData.Length);

                        // Update peer stats
                        var finalPeer = state.Peers.GetValueOrDefault(selectedPeer.PeerId);
                        if (finalPeer is not null)
                        {
                            state.Peers[selectedPeer.PeerId] = finalPeer with
                            {
                                BytesDownloaded = finalPeer.BytesDownloaded + pieceData.Length,
                                LastSeen = DateTime.UtcNow
                            };
                        }

                        var completed = Interlocked.Increment(ref completedPieces);
                        var pct = totalPieces > 0 ? (double)completed / totalPieces * 100.0 : 0;

                        progress?.Report(new TransitProgress
                        {
                            TransferId = state.TransferId,
                            BytesTransferred = Interlocked.Read(ref totalBytesDownloaded),
                            TotalBytes = state.TotalSize,
                            PercentComplete = pct,
                            CurrentPhase = $"Downloading piece {completed}/{totalPieces}"
                        });
                    }
                    finally
                    {
                        // Decrement active downloads for this peer
                        var currentPeer = state.Peers.GetValueOrDefault(selectedPeer.PeerId);
                        if (currentPeer is not null)
                        {
                            state.Peers[selectedPeer.PeerId] = currentPeer with
                            {
                                ActiveDownloads = Math.Max(0, currentPeer.ActiveDownloads - 1)
                            };
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);

            downloadTasks.Add(task);
        }

        // Wait for all download tasks to complete
        await Task.WhenAll(downloadTasks);

        // Handle re-queued pieces (retry from remaining peers)
        if (!requeueList.IsEmpty)
        {
            var retryChannel = Channel.CreateBounded<int>(new BoundedChannelOptions(PieceQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            var hasAvailablePeers = state.Peers.Any();
            if (hasAvailablePeers)
            {
                // Write re-queued pieces concurrently with the reader — both tasks run in parallel.
                // Capture the task so its exceptions are observed (finding 2675).
                var writeTask = Task.Run(async () =>
                {
                    foreach (var idx in requeueList)
                    {
                        if (!state.Pieces[idx].Downloaded)
                        {
                            await retryChannel.Writer.WriteAsync(idx, ct);
                        }
                    }
                    retryChannel.Writer.Complete();
                }, ct);

                // Recursively process re-queued pieces
                var retryBytes = await DownloadPiecesFromSwarmAsync(
                    state, retryChannel.Reader, progress, ct);
                Interlocked.Add(ref totalBytesDownloaded, retryBytes);

                // Await the write task to observe any exceptions.
                await writeTask;
            }
            else
            {
                // Check if all pieces are done despite re-queues
                var incompletePieces = state.Pieces.Count(p => !p.Downloaded);
                if (incompletePieces > 0)
                {
                    throw new InvalidOperationException(
                        $"Transfer failed: {incompletePieces} pieces could not be downloaded. " +
                        $"All available peers were banned or unavailable.");
                }
            }
        }

        return Interlocked.Read(ref totalBytesDownloaded);
    }

    /// <summary>
    /// Selects the optimal peer for downloading a specific piece using rarest-first selection.
    /// Chooses the peer that HAS the piece (bitmap check) and has the fewest active downloads
    /// (load balancing), while respecting the per-peer concurrent download limit.
    /// </summary>
    /// <param name="state">The swarm state containing peer information.</param>
    /// <param name="pieceIndex">The index of the piece to download.</param>
    /// <returns>The selected peer, or null if no peer has the piece available.</returns>
    private SwarmPeer? SelectPeer(SwarmState state, int pieceIndex)
    {
        return state.Peers.Values
            .Where(p => !_bannedPeers.ContainsKey(p.PeerId))
            .Where(p => p.PieceBitmap.Length > pieceIndex && p.PieceBitmap[pieceIndex])
            .Where(p => p.ActiveDownloads < state.ConcurrentDownloadLimit)
            .OrderBy(p => p.ActiveDownloads)
            .FirstOrDefault();
    }

    /// <summary>
    /// Downloads a single piece from a specific peer via HTTP GET.
    /// The piece is requested from <c>{peerEndpoint}/swarm/piece/{transferId}/{pieceIndex}</c>.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="pieceIndex">The zero-based piece index.</param>
    /// <param name="peerEndpointUri">The peer's HTTP endpoint URI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The raw piece data as a byte array.</returns>
    private async Task<byte[]> DownloadPieceFromPeerAsync(
        string transferId,
        int pieceIndex,
        string peerEndpointUri,
        CancellationToken ct)
    {
        var pieceUri = $"{peerEndpointUri.TrimEnd('/')}/swarm/piece/{transferId}/{pieceIndex}";

        using var request = new HttpRequestMessage(HttpMethod.Get, pieceUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Registers this node as a seeder with the tracker after all pieces have been downloaded.
    /// Sends a POST to <c>{destination.Uri}/swarm/seed</c> with the transfer ID and piece count.
    /// </summary>
    /// <param name="state">The swarm state with completed transfer information.</param>
    /// <param name="destination">The destination endpoint containing the tracker URI.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RegisterAsSeederAsync(SwarmState state, TransitEndpoint destination, CancellationToken ct)
    {
        var seedUri = new Uri(destination.Uri, "swarm/seed");

        var seedPayload = JsonSerializer.Serialize(new
        {
            state.TransferId,
            PieceCount = state.Pieces.Length,
            CompletedPieces = state.Pieces.Count(p => p.Downloaded)
        });

        using var content = new StringContent(seedPayload, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, seedUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        if (!string.IsNullOrEmpty(destination.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.AuthToken);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            // Best-effort seeding registration; failure does not affect transfer success
        }
        catch
        {

            // Seeding registration is best-effort
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// Determines the total data size from the request using <see cref="TransitRequest.SizeBytes"/>
    /// or falling back to the stream length.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <returns>The total data size in bytes.</returns>
    private static long DetermineTotalSize(TransitRequest request)
    {
        if (request.SizeBytes > 0) return request.SizeBytes;
        if (request.DataStream is { CanSeek: true }) return request.DataStream.Length;

        throw new InvalidOperationException(
            "Cannot determine total size: SizeBytes not set and stream is not seekable. " +
            "P2P swarm transfer requires a known total size for piece hashing.");
    }

    /// <summary>
    /// Determines the piece size from request metadata or falls back to <see cref="DefaultPieceSize"/>.
    /// Callers can specify a custom piece size via the <c>pieceSize</c> metadata key.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <returns>The piece size in bytes.</returns>
    private static int DeterminePieceSize(TransitRequest request)
    {
        if (request.Metadata.TryGetValue("pieceSize", out var pieceSizeStr) &&
            int.TryParse(pieceSizeStr, out var customPieceSize) &&
            customPieceSize > 0)
        {
            return customPieceSize;
        }

        return DefaultPieceSize;
    }

    #region Internal Types

    /// <summary>
    /// Represents a peer in the swarm with its piece availability bitmap and download statistics.
    /// Immutable record; updates are performed by replacing the entry in <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    /// <param name="PeerId">Unique identifier for the peer.</param>
    /// <param name="EndpointUri">HTTP endpoint URI for downloading pieces from this peer.</param>
    /// <param name="PieceBitmap">Boolean array indicating which pieces this peer has available.</param>
    /// <param name="ActiveDownloads">Number of downloads currently in progress from this peer.</param>
    /// <param name="BytesDownloaded">Total bytes downloaded from this peer across all pieces.</param>
    /// <param name="LastSeen">Timestamp of the last successful interaction with this peer.</param>
    internal sealed record SwarmPeer(
        string PeerId,
        string EndpointUri,
        bool[] PieceBitmap,
        int ActiveDownloads,
        long BytesDownloaded,
        DateTime LastSeen);

    /// <summary>
    /// Represents a single piece of data within the swarm transfer, including its position,
    /// SHA-256 hash for integrity verification, and download status.
    /// </summary>
    /// <param name="Index">Zero-based index of this piece.</param>
    /// <param name="Offset">Byte offset of this piece within the source data.</param>
    /// <param name="Size">Size of this piece in bytes.</param>
    /// <param name="Sha256Hash">SHA-256 hash of the piece data for integrity verification.</param>
    /// <param name="Downloaded">Whether this piece has been successfully downloaded and verified.</param>
    internal sealed record PieceInfo(
        int Index,
        long Offset,
        int Size,
        byte[] Sha256Hash,
        bool Downloaded);

    /// <summary>
    /// Mutable state for an active swarm transfer. Tracks pieces, peers, and the piece queue.
    /// Thread safety is provided by <see cref="ConcurrentDictionary{TKey,TValue}"/> for peers
    /// and <see cref="Channel{T}"/> for the piece queue.
    /// </summary>
    internal sealed class SwarmState
    {
        /// <summary>
        /// Unique identifier for this swarm transfer.
        /// </summary>
        public required string TransferId { get; init; }

        /// <summary>
        /// Total size of the data being transferred in bytes.
        /// </summary>
        public required long TotalSize { get; init; }

        /// <summary>
        /// Size of each piece in bytes (default 256 KB).
        /// </summary>
        public required int PieceSize { get; init; }

        /// <summary>
        /// Array of all pieces with their hashes and download status.
        /// Individual elements are replaced atomically via record <c>with</c> expressions.
        /// </summary>
        public required PieceInfo[] Pieces { get; init; }

        /// <summary>
        /// Thread-safe dictionary of active peers keyed by peer ID.
        /// Peer records are replaced atomically for state updates.
        /// </summary>
        public required BoundedDictionary<string, SwarmPeer> Peers { get; init; }

        /// <summary>
        /// Bounded channel for pending piece indices. Provides backpressure to prevent
        /// fast producers from overwhelming slow consumers.
        /// </summary>
        public required Channel<int> PieceQueue { get; set; }

        /// <summary>
        /// Maximum concurrent downloads allowed per individual peer.
        /// </summary>
        public required int ConcurrentDownloadLimit { get; init; }

        /// <summary>
        /// The original transfer request, stored for resume operations.
        /// </summary>
        public TransitRequest? Request { get; init; }
    }

    /// <summary>
    /// Response from the swarm tracker containing discovered peers.
    /// </summary>
    private sealed class TrackerResponse
    {
        /// <summary>
        /// List of peers reported by the tracker.
        /// </summary>
        public List<TrackerPeer>? Peers { get; set; }
    }

    /// <summary>
    /// Represents a peer as reported by the swarm tracker.
    /// </summary>
    private sealed class TrackerPeer
    {
        /// <summary>
        /// Unique identifier for the peer.
        /// </summary>
        public string PeerId { get; set; } = string.Empty;

        /// <summary>
        /// HTTP endpoint URI for the peer.
        /// </summary>
        public string EndpointUri { get; set; } = string.Empty;

        /// <summary>
        /// Boolean array indicating which pieces this peer has available.
        /// </summary>
        public bool[]? PieceBitmap { get; set; }
    }

    #endregion
}
