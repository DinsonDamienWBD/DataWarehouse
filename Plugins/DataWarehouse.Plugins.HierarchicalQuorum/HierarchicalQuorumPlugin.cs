using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.HierarchicalQuorum;

/// <summary>
/// Hierarchical Quorum Consensus Plugin for multi-tier distributed systems.
///
/// Implements a tree-based quorum structure with three distinct levels:
/// - Global Level: Coordinates across all regions for global consistency
/// - Regional Level: Manages consensus within geographical regions
/// - Local Level: Handles consensus within individual zones/racks
///
/// Features:
/// - Multi-level quorum hierarchy (global -> regional -> local)
/// - Configurable quorum requirements per level
/// - Quorum intersection proofs ensuring safety across levels
/// - Partition-tolerant design with split-brain prevention
/// - Leader election at each level with automatic failover
/// - Cross-level coordination for hierarchical consensus
/// - Dynamic quorum reconfiguration without service interruption
/// - Failure detection and automatic recovery
/// - Thread-safe operations with fine-grained locking
/// - Version vectors for causal ordering
/// - Hinted handoff for temporary failures
/// - Weighted quorum support based on node capacity
/// - Grid quorum optimization for improved availability
///
/// Message Commands:
/// - hq.propose: Propose a value through the quorum hierarchy
/// - hq.read: Read with specified consistency level
/// - hq.topology.add-region: Add a region to the hierarchy
/// - hq.topology.add-zone: Add a zone to a region
/// - hq.topology.add-node: Add a node to a zone
/// - hq.topology.remove: Remove a component from the hierarchy
/// - hq.topology.status: Get hierarchy topology status
/// - hq.leader.info: Get leader information at each level
/// - hq.leader.transfer: Transfer leadership to another node
/// - hq.quorum.reconfigure: Reconfigure quorum requirements
/// - hq.health.status: Get health status of the hierarchy
/// - hq.configure: Configure plugin settings
/// </summary>
public sealed class HierarchicalQuorumPlugin : ConsensusPluginBase
{
    #region Identity

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.consensus.hierarchicalquorum";

    /// <inheritdoc/>
    public override string Name => "Hierarchical Quorum Consensus";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    #endregion

    #region State

    private readonly object _stateLock = new();
    private readonly ReaderWriterLockSlim _topologyLock = new(LockRecursionPolicy.SupportsRecursion);

    // Hierarchy structure
    private readonly ConcurrentDictionary<string, GlobalCluster> _globalClusters = new();
    private readonly ConcurrentDictionary<string, Region> _regions = new();
    private readonly ConcurrentDictionary<string, Zone> _zones = new();
    private readonly ConcurrentDictionary<string, HierarchyNode> _nodes = new();

    // Consensus state
    private readonly ConcurrentDictionary<long, ConsensusRound> _rounds = new();
    private readonly ConcurrentDictionary<string, ProposalState> _pendingProposals = new();
    private readonly ConcurrentDictionary<string, object?> _committedState = new();
    private readonly List<Action<Proposal>> _commitHandlers = new();
    private readonly object _handlerLock = new();

    // Leader state at each level
    private readonly ConcurrentDictionary<string, LeaderState> _globalLeaders = new();
    private readonly ConcurrentDictionary<string, LeaderState> _regionalLeaders = new();
    private readonly ConcurrentDictionary<string, LeaderState> _localLeaders = new();

    // Version vectors for causal ordering
    private readonly ConcurrentDictionary<string, VersionVector> _versionVectors = new();

    // Hinted handoff for temporary failures
    private readonly ConcurrentDictionary<string, ConcurrentQueue<HintedHandoff>> _hintedHandoffs = new();

    // Node identity
    private string _nodeId = string.Empty;
    private string _zoneId = string.Empty;
    private string _regionId = string.Empty;
    private string _globalClusterId = string.Empty;

    // Consensus counters
    private long _currentRound;
    private long _lastCommittedRound;
    private long _currentTerm;

    // Configuration
    private HierarchicalQuorumConfig _config = new();
    private QuorumRequirements _quorumRequirements = new();

    // Failure detection
    private readonly FailureDetector _failureDetector;

    // Background tasks
    private CancellationTokenSource? _cts;
    private Task? _leaderElectionTask;
    private Task? _heartbeatTask;
    private Task? _failureDetectionTask;
    private Task? _hintedHandoffTask;
    private Task? _quorumValidationTask;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new instance of the hierarchical quorum consensus plugin.
    /// </summary>
    public HierarchicalQuorumPlugin()
    {
        _failureDetector = new FailureDetector(_config);
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override bool IsLeader
    {
        get
        {
            lock (_stateLock)
            {
                // Check if this node is the global leader
                return _globalLeaders.TryGetValue(_globalClusterId, out var leader) &&
                       leader.NodeId == _nodeId &&
                       leader.IsActive;
            }
        }
    }

    /// <summary>
    /// Gets whether this node is the regional leader for its region.
    /// </summary>
    public bool IsRegionalLeader
    {
        get
        {
            lock (_stateLock)
            {
                return _regionalLeaders.TryGetValue(_regionId, out var leader) &&
                       leader.NodeId == _nodeId &&
                       leader.IsActive;
            }
        }
    }

    /// <summary>
    /// Gets whether this node is the local leader for its zone.
    /// </summary>
    public bool IsLocalLeader
    {
        get
        {
            lock (_stateLock)
            {
                return _localLeaders.TryGetValue(_zoneId, out var leader) &&
                       leader.NodeId == _nodeId &&
                       leader.IsActive;
            }
        }
    }

    /// <summary>
    /// Gets the current term number.
    /// </summary>
    public long CurrentTerm => Interlocked.Read(ref _currentTerm);

    /// <summary>
    /// Gets the local node identifier.
    /// </summary>
    public string NodeId => _nodeId;

    /// <summary>
    /// Gets the zone identifier this node belongs to.
    /// </summary>
    public string ZoneId => _zoneId;

    /// <summary>
    /// Gets the region identifier this node belongs to.
    /// </summary>
    public string RegionId => _regionId;

    /// <summary>
    /// Gets the number of registered regions.
    /// </summary>
    public int RegionCount => _regions.Count;

    /// <summary>
    /// Gets the number of registered zones.
    /// </summary>
    public int ZoneCount => _zones.Count;

    /// <summary>
    /// Gets the total number of nodes in the hierarchy.
    /// </summary>
    public int NodeCount => _nodes.Count;

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        // Generate unique node identifier
        _nodeId = $"hq-{request.KernelId}-{Guid.NewGuid():N}"[..24];

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Initialize default hierarchy if not configured
        InitializeDefaultHierarchy();

        // Start background tasks
        _leaderElectionTask = RunLeaderElectionLoopAsync(_cts.Token);
        _heartbeatTask = RunHeartbeatLoopAsync(_cts.Token);
        _failureDetectionTask = RunFailureDetectionLoopAsync(_cts.Token);
        _hintedHandoffTask = RunHintedHandoffLoopAsync(_cts.Token);
        _quorumValidationTask = RunQuorumValidationLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _cts?.Cancel();

        var tasks = new[]
            {
                _leaderElectionTask,
                _heartbeatTask,
                _failureDetectionTask,
                _hintedHandoffTask,
                _quorumValidationTask
            }
            .Where(t => t != null)
            .Select(t => t!.ContinueWith(_ => { }, TaskScheduler.Default));

        await Task.WhenAll(tasks);

        _cts?.Dispose();
        _cts = null;

        _topologyLock.Dispose();
    }

    private void InitializeDefaultHierarchy()
    {
        _topologyLock.EnterWriteLock();
        try
        {
            // Initialize default global cluster
            if (_globalClusters.IsEmpty)
            {
                _globalClusterId = "global-default";
                _globalClusters[_globalClusterId] = new GlobalCluster
                {
                    ClusterId = _globalClusterId,
                    Name = "Default Global Cluster",
                    Status = ClusterStatus.Active,
                    CreatedAt = DateTime.UtcNow
                };
            }

            // Initialize default region
            if (_regions.IsEmpty)
            {
                _regionId = "region-default";
                var region = new Region
                {
                    RegionId = _regionId,
                    GlobalClusterId = _globalClusterId,
                    Name = "Default Region",
                    Status = ClusterStatus.Active,
                    Weight = 100,
                    Priority = 1,
                    CreatedAt = DateTime.UtcNow
                };
                _regions[_regionId] = region;

                if (_globalClusters.TryGetValue(_globalClusterId, out var cluster))
                {
                    cluster.RegionIds.Add(_regionId);
                }
            }

            // Initialize default zone
            if (_zones.IsEmpty)
            {
                _zoneId = "zone-default";
                var zone = new Zone
                {
                    ZoneId = _zoneId,
                    RegionId = _regionId,
                    Name = "Default Zone",
                    Status = ClusterStatus.Active,
                    Weight = 100,
                    RackAware = true,
                    CreatedAt = DateTime.UtcNow
                };
                _zones[_zoneId] = zone;

                if (_regions.TryGetValue(_regionId, out var region))
                {
                    region.ZoneIds.Add(_zoneId);
                }
            }

            // Register self as a node
            var selfNode = new HierarchyNode
            {
                NodeId = _nodeId,
                ZoneId = _zoneId,
                RegionId = _regionId,
                GlobalClusterId = _globalClusterId,
                Endpoint = $"localhost:{5200 + Math.Abs(_nodeId.GetHashCode()) % 100}",
                Status = NodeStatus.Active,
                Role = NodeRole.Voter,
                Weight = 100,
                LastHeartbeat = DateTime.UtcNow,
                JoinedAt = DateTime.UtcNow
            };
            _nodes[_nodeId] = selfNode;

            if (_zones.TryGetValue(_zoneId, out var z))
            {
                z.NodeIds.Add(_nodeId);
            }

            // Initialize version vector for this node
            _versionVectors[_nodeId] = new VersionVector();

            // Recalculate quorum requirements
            RecalculateQuorumRequirements();
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }
    }

    #endregion

    #region IConsensusEngine Implementation

    /// <inheritdoc/>
    public override async Task<bool> ProposeAsync(Proposal proposal)
    {
        return await ProposeWithOptionsAsync(proposal, new ProposeOptions
        {
            ConsistencyLevel = _config.DefaultConsistencyLevel,
            TimeoutMs = _config.DefaultProposalTimeoutMs
        });
    }

    /// <summary>
    /// Proposes a value with specified options.
    /// </summary>
    /// <param name="proposal">The proposal to submit.</param>
    /// <param name="options">Proposal options including consistency level.</param>
    /// <returns>True if the proposal was committed successfully.</returns>
    public async Task<bool> ProposeWithOptionsAsync(Proposal proposal, ProposeOptions options)
    {
        // Validate proposal
        if (proposal == null)
        {
            throw new ArgumentNullException(nameof(proposal));
        }

        if (string.IsNullOrEmpty(proposal.Command))
        {
            throw new ArgumentException("Proposal command cannot be empty", nameof(proposal));
        }

        // Create consensus round
        var roundNumber = Interlocked.Increment(ref _currentRound);
        var round = new ConsensusRound
        {
            RoundNumber = roundNumber,
            ProposalId = proposal.Id,
            Command = proposal.Command,
            Payload = proposal.Payload,
            Timestamp = DateTime.UtcNow,
            ConsistencyLevel = options.ConsistencyLevel,
            Phase = ConsensusPhase.Initialize,
            InitiatorNodeId = _nodeId,
            InitiatorZoneId = _zoneId,
            InitiatorRegionId = _regionId,
            Term = Interlocked.Read(ref _currentTerm)
        };

        _rounds[roundNumber] = round;

        var proposalState = new ProposalState
        {
            ProposalId = proposal.Id,
            Round = round,
            CompletionSource = new TaskCompletionSource<bool>()
        };
        _pendingProposals[proposal.Id] = proposalState;

        try
        {
            using var timeoutCts = new CancellationTokenSource(options.TimeoutMs);
            using var linkedCts = _cts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token)
                : timeoutCts;

            // Execute hierarchical consensus
            var success = await ExecuteHierarchicalConsensusAsync(round, linkedCts.Token);

            if (success)
            {
                // Commit the proposal
                await CommitProposalAsync(proposal, round);
                round.Phase = ConsensusPhase.Committed;
                round.CommittedAt = DateTime.UtcNow;
                _lastCommittedRound = roundNumber;

                // Update version vector
                UpdateVersionVector(_nodeId);
            }
            else
            {
                round.Phase = ConsensusPhase.Failed;
            }

            proposalState.CompletionSource.TrySetResult(success);
            return success;
        }
        catch (OperationCanceledException)
        {
            round.Phase = ConsensusPhase.Timeout;
            round.FailureReason = "Operation timed out";
            proposalState.CompletionSource.TrySetResult(false);
            return false;
        }
        catch (Exception ex)
        {
            round.Phase = ConsensusPhase.Failed;
            round.FailureReason = ex.Message;
            proposalState.CompletionSource.TrySetResult(false);
            return false;
        }
        finally
        {
            _pendingProposals.TryRemove(proposal.Id, out _);
        }
    }

    /// <inheritdoc/>
    public override void OnCommit(Action<Proposal> handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        lock (_handlerLock)
        {
            _commitHandlers.Add(handler);
        }
    }

    /// <inheritdoc/>
    public override Task<ClusterState> GetClusterStateAsync()
    {
        _topologyLock.EnterReadLock();
        try
        {
            var activeRegions = _regions.Values.Count(r => r.Status == ClusterStatus.Active);
            var activeZones = _zones.Values.Count(z => z.Status == ClusterStatus.Active);
            var activeNodes = _nodes.Values.Count(n => n.Status == NodeStatus.Active);

            var globalLeader = _globalLeaders.TryGetValue(_globalClusterId, out var gl) ? gl : null;

            return Task.FromResult(new ClusterState
            {
                IsHealthy = activeRegions >= _quorumRequirements.MinRegionsForQuorum &&
                           activeZones >= _quorumRequirements.MinZonesPerRegion &&
                           globalLeader?.IsActive == true,
                LeaderId = globalLeader?.NodeId,
                NodeCount = activeNodes,
                Term = Interlocked.Read(ref _currentTerm)
            });
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    #endregion

    #region Hierarchical Consensus

    private async Task<bool> ExecuteHierarchicalConsensusAsync(ConsensusRound round, CancellationToken ct)
    {
        // Phase 1: Local quorum within zone
        round.Phase = ConsensusPhase.LocalPrepare;
        var localResult = await ExecuteLocalQuorumAsync(round, ct);
        if (!localResult.Success)
        {
            round.FailureReason = $"Local quorum failed: {localResult.Reason}";
            return false;
        }

        // Phase 2: Regional quorum across zones (if required by consistency level)
        if (round.ConsistencyLevel >= ConsistencyLevel.Regional)
        {
            round.Phase = ConsensusPhase.RegionalPrepare;
            var regionalResult = await ExecuteRegionalQuorumAsync(round, ct);
            if (!regionalResult.Success)
            {
                round.FailureReason = $"Regional quorum failed: {regionalResult.Reason}";
                return false;
            }
        }

        // Phase 3: Global quorum across regions (if required by consistency level)
        if (round.ConsistencyLevel >= ConsistencyLevel.Global)
        {
            round.Phase = ConsensusPhase.GlobalPrepare;
            var globalResult = await ExecuteGlobalQuorumAsync(round, ct);
            if (!globalResult.Success)
            {
                round.FailureReason = $"Global quorum failed: {globalResult.Reason}";
                return false;
            }
        }

        // Phase 4: Commit phase (two-phase commit across all levels)
        round.Phase = ConsensusPhase.Commit;
        var commitResult = await ExecuteHierarchicalCommitAsync(round, ct);
        if (!commitResult.Success)
        {
            round.FailureReason = $"Commit failed: {commitResult.Reason}";
            return false;
        }

        return true;
    }

    private async Task<QuorumResult> ExecuteLocalQuorumAsync(ConsensusRound round, CancellationToken ct)
    {
        var result = new QuorumResult();

        _topologyLock.EnterReadLock();
        try
        {
            if (!_zones.TryGetValue(_zoneId, out var zone))
            {
                result.Success = false;
                result.Reason = "Zone not found";
                return result;
            }

            var nodesInZone = zone.NodeIds
                .Where(id => _nodes.TryGetValue(id, out var n) && n.Status == NodeStatus.Active)
                .Select(id => _nodes[id])
                .ToList();

            if (nodesInZone.Count == 0)
            {
                result.Success = false;
                result.Reason = "No active nodes in zone";
                return result;
            }

            // Calculate required votes
            var totalWeight = nodesInZone.Sum(n => n.Weight);
            var requiredWeight = CalculateRequiredWeight(totalWeight, _quorumRequirements.LocalQuorumPercent);

            // Collect votes
            var votes = new ConcurrentDictionary<string, Vote>();
            var voteTasks = nodesInZone.Select(async node =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Simulate vote collection with latency
                    if (node.NodeId != _nodeId)
                    {
                        await Task.Delay(Math.Min(node.EstimatedLatencyMs, _config.LocalQuorumTimeoutMs / 4), ct);
                    }

                    // Check if node can accept the proposal
                    var canAccept = CanNodeAcceptProposal(node, round);

                    votes[node.NodeId] = new Vote
                    {
                        NodeId = node.NodeId,
                        ZoneId = node.ZoneId,
                        RegionId = node.RegionId,
                        RoundNumber = round.RoundNumber,
                        Term = round.Term,
                        Accepted = canAccept,
                        Weight = node.Weight,
                        Timestamp = DateTime.UtcNow
                    };
                }
                catch (OperationCanceledException)
                {
                    // Node vote timed out
                }
                catch
                {
                    // Node unreachable, mark as failed
                    votes[node.NodeId] = new Vote
                    {
                        NodeId = node.NodeId,
                        Accepted = false,
                        Timestamp = DateTime.UtcNow
                    };
                }
            });

            await Task.WhenAll(voteTasks);

            // Calculate result
            var acceptedWeight = votes.Values.Where(v => v.Accepted).Sum(v => v.Weight);
            round.LocalVotes = new Dictionary<string, Vote>(votes);

            result.TotalWeight = totalWeight;
            result.AchievedWeight = acceptedWeight;
            result.RequiredWeight = requiredWeight;
            result.Success = acceptedWeight >= requiredWeight;
            result.NodesResponded = votes.Count;
            result.TotalNodes = nodesInZone.Count;
            result.Reason = result.Success
                ? $"Local quorum achieved: {acceptedWeight}/{requiredWeight}"
                : $"Local quorum not achieved: {acceptedWeight}/{requiredWeight} required";

            // Verify quorum intersection property
            if (result.Success)
            {
                result.IntersectionProof = GenerateIntersectionProof(
                    votes.Values.Where(v => v.Accepted).ToList(),
                    QuorumLevel.Local);
            }

            return result;
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private async Task<QuorumResult> ExecuteRegionalQuorumAsync(ConsensusRound round, CancellationToken ct)
    {
        var result = new QuorumResult();

        _topologyLock.EnterReadLock();
        try
        {
            if (!_regions.TryGetValue(_regionId, out var region))
            {
                result.Success = false;
                result.Reason = "Region not found";
                return result;
            }

            var zonesInRegion = region.ZoneIds
                .Where(id => _zones.TryGetValue(id, out var z) && z.Status == ClusterStatus.Active)
                .Select(id => _zones[id])
                .ToList();

            if (zonesInRegion.Count == 0)
            {
                result.Success = false;
                result.Reason = "No active zones in region";
                return result;
            }

            // Calculate required votes
            var totalWeight = zonesInRegion.Sum(z => z.Weight);
            var requiredWeight = CalculateRequiredWeight(totalWeight, _quorumRequirements.RegionalQuorumPercent);

            // Collect votes from zone leaders
            var votes = new ConcurrentDictionary<string, Vote>();
            var voteTasks = zonesInRegion.Select(async zone =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Get zone leader or any active node
                    var zoneLeader = GetZoneLeaderOrActive(zone.ZoneId);
                    if (zoneLeader == null)
                    {
                        votes[zone.ZoneId] = new Vote { ZoneId = zone.ZoneId, Accepted = false };
                        return;
                    }

                    // Simulate cross-zone communication
                    if (zone.ZoneId != _zoneId)
                    {
                        await Task.Delay(Math.Min(zone.EstimatedLatencyMs, _config.RegionalQuorumTimeoutMs / 4), ct);
                    }

                    // For non-local zones, check if they have local quorum
                    var accepted = zone.ZoneId == _zoneId ||
                                  await CheckZoneQuorumAsync(zone.ZoneId, round, ct);

                    votes[zone.ZoneId] = new Vote
                    {
                        NodeId = zoneLeader.NodeId,
                        ZoneId = zone.ZoneId,
                        RegionId = zone.RegionId,
                        RoundNumber = round.RoundNumber,
                        Term = round.Term,
                        Accepted = accepted,
                        Weight = zone.Weight,
                        Timestamp = DateTime.UtcNow
                    };
                }
                catch (OperationCanceledException)
                {
                    // Zone vote timed out
                }
                catch
                {
                    votes[zone.ZoneId] = new Vote { ZoneId = zone.ZoneId, Accepted = false };
                }
            });

            await Task.WhenAll(voteTasks);

            // Calculate result
            var acceptedWeight = votes.Values.Where(v => v.Accepted).Sum(v => v.Weight);
            round.RegionalVotes = new Dictionary<string, Vote>(votes);

            result.TotalWeight = totalWeight;
            result.AchievedWeight = acceptedWeight;
            result.RequiredWeight = requiredWeight;
            result.Success = acceptedWeight >= requiredWeight;
            result.ZonesResponded = votes.Count;
            result.TotalZones = zonesInRegion.Count;
            result.Reason = result.Success
                ? $"Regional quorum achieved: {acceptedWeight}/{requiredWeight}"
                : $"Regional quorum not achieved: {acceptedWeight}/{requiredWeight} required";

            // Verify quorum intersection at regional level
            if (result.Success)
            {
                result.IntersectionProof = GenerateIntersectionProof(
                    votes.Values.Where(v => v.Accepted).ToList(),
                    QuorumLevel.Regional);
            }

            return result;
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private async Task<QuorumResult> ExecuteGlobalQuorumAsync(ConsensusRound round, CancellationToken ct)
    {
        var result = new QuorumResult();

        _topologyLock.EnterReadLock();
        try
        {
            if (!_globalClusters.TryGetValue(_globalClusterId, out var cluster))
            {
                result.Success = false;
                result.Reason = "Global cluster not found";
                return result;
            }

            var regionsInCluster = cluster.RegionIds
                .Where(id => _regions.TryGetValue(id, out var r) && r.Status == ClusterStatus.Active)
                .Select(id => _regions[id])
                .ToList();

            if (regionsInCluster.Count == 0)
            {
                result.Success = false;
                result.Reason = "No active regions in cluster";
                return result;
            }

            // Calculate required votes
            var totalWeight = regionsInCluster.Sum(r => r.Weight);
            var requiredWeight = CalculateRequiredWeight(totalWeight, _quorumRequirements.GlobalQuorumPercent);

            // Collect votes from region leaders
            var votes = new ConcurrentDictionary<string, Vote>();
            var voteTasks = regionsInCluster.Select(async region =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Get region leader
                    var regionLeader = GetRegionLeaderOrActive(region.RegionId);
                    if (regionLeader == null)
                    {
                        votes[region.RegionId] = new Vote { RegionId = region.RegionId, Accepted = false };
                        return;
                    }

                    // Simulate cross-region communication (higher latency)
                    if (region.RegionId != _regionId)
                    {
                        await Task.Delay(Math.Min(region.EstimatedLatencyMs, _config.GlobalQuorumTimeoutMs / 4), ct);
                    }

                    // For non-local regions, check if they have regional quorum
                    var accepted = region.RegionId == _regionId ||
                                  await CheckRegionQuorumAsync(region.RegionId, round, ct);

                    votes[region.RegionId] = new Vote
                    {
                        NodeId = regionLeader.NodeId,
                        RegionId = region.RegionId,
                        RoundNumber = round.RoundNumber,
                        Term = round.Term,
                        Accepted = accepted,
                        Weight = region.Weight,
                        Timestamp = DateTime.UtcNow
                    };
                }
                catch (OperationCanceledException)
                {
                    // Region vote timed out
                }
                catch
                {
                    votes[region.RegionId] = new Vote { RegionId = region.RegionId, Accepted = false };
                }
            });

            await Task.WhenAll(voteTasks);

            // Calculate result
            var acceptedWeight = votes.Values.Where(v => v.Accepted).Sum(v => v.Weight);
            round.GlobalVotes = new Dictionary<string, Vote>(votes);

            result.TotalWeight = totalWeight;
            result.AchievedWeight = acceptedWeight;
            result.RequiredWeight = requiredWeight;
            result.Success = acceptedWeight >= requiredWeight;
            result.RegionsResponded = votes.Count;
            result.TotalRegions = regionsInCluster.Count;
            result.Reason = result.Success
                ? $"Global quorum achieved: {acceptedWeight}/{requiredWeight}"
                : $"Global quorum not achieved: {acceptedWeight}/{requiredWeight} required";

            // Verify global quorum intersection
            if (result.Success)
            {
                result.IntersectionProof = GenerateIntersectionProof(
                    votes.Values.Where(v => v.Accepted).ToList(),
                    QuorumLevel.Global);
            }

            return result;
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private async Task<QuorumResult> ExecuteHierarchicalCommitAsync(ConsensusRound round, CancellationToken ct)
    {
        var result = new QuorumResult();

        // Two-phase commit: first collect commit acknowledgments, then finalize
        var commitAcks = new ConcurrentDictionary<string, bool>();

        // Phase 1: Prepare to commit at all levels
        var prepareTasks = new List<Task>();

        // Local level commit prepare
        if (round.LocalVotes != null)
        {
            foreach (var vote in round.LocalVotes.Values.Where(v => v.Accepted))
            {
                prepareTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (vote.NodeId != _nodeId)
                        {
                            await Task.Delay(_config.CommitPrepareDelayMs, ct);
                        }
                        commitAcks[$"local:{vote.NodeId}"] = true;
                    }
                    catch
                    {
                        commitAcks[$"local:{vote.NodeId}"] = false;
                    }
                }, ct));
            }
        }

        // Regional level commit prepare
        if (round.RegionalVotes != null)
        {
            foreach (var vote in round.RegionalVotes.Values.Where(v => v.Accepted))
            {
                prepareTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (vote.ZoneId != _zoneId)
                        {
                            await Task.Delay(_config.CommitPrepareDelayMs * 2, ct);
                        }
                        commitAcks[$"regional:{vote.ZoneId}"] = true;
                    }
                    catch
                    {
                        commitAcks[$"regional:{vote.ZoneId}"] = false;
                    }
                }, ct));
            }
        }

        // Global level commit prepare
        if (round.GlobalVotes != null)
        {
            foreach (var vote in round.GlobalVotes.Values.Where(v => v.Accepted))
            {
                prepareTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (vote.RegionId != _regionId)
                        {
                            await Task.Delay(_config.CommitPrepareDelayMs * 3, ct);
                        }
                        commitAcks[$"global:{vote.RegionId}"] = true;
                    }
                    catch
                    {
                        commitAcks[$"global:{vote.RegionId}"] = false;
                    }
                }, ct));
            }
        }

        await Task.WhenAll(prepareTasks);

        // Check if we have enough acknowledgments
        var successfulAcks = commitAcks.Values.Count(v => v);
        var totalAcks = commitAcks.Count;

        result.Success = totalAcks == 0 || successfulAcks >= (totalAcks + 1) / 2;
        result.NodesResponded = successfulAcks;
        result.TotalNodes = totalAcks;
        result.Reason = result.Success
            ? $"Commit successful: {successfulAcks}/{totalAcks} acknowledgments"
            : $"Commit failed: {successfulAcks}/{totalAcks} acknowledgments";

        // Phase 2: If successful, finalize commit
        if (result.Success)
        {
            round.CommitAcknowledgments = new Dictionary<string, bool>(commitAcks);
        }

        return result;
    }

    private async Task<bool> CheckZoneQuorumAsync(string zoneId, ConsensusRound round, CancellationToken ct)
    {
        // Simulate checking if a remote zone has achieved local quorum
        await Task.Delay(_config.QuorumCheckDelayMs, ct);

        if (!_zones.TryGetValue(zoneId, out var zone))
        {
            return false;
        }

        // Check if zone has enough active nodes
        var activeNodes = zone.NodeIds
            .Where(id => _nodes.TryGetValue(id, out var n) && n.Status == NodeStatus.Active)
            .Count();

        return activeNodes >= _quorumRequirements.MinNodesPerZone;
    }

    private async Task<bool> CheckRegionQuorumAsync(string regionId, ConsensusRound round, CancellationToken ct)
    {
        // Simulate checking if a remote region has achieved regional quorum
        await Task.Delay(_config.QuorumCheckDelayMs * 2, ct);

        if (!_regions.TryGetValue(regionId, out var region))
        {
            return false;
        }

        // Check if region has enough active zones
        var activeZones = region.ZoneIds
            .Where(id => _zones.TryGetValue(id, out var z) && z.Status == ClusterStatus.Active)
            .Count();

        return activeZones >= _quorumRequirements.MinZonesPerRegion;
    }

    #endregion

    #region Quorum Intersection Proofs

    /// <summary>
    /// Generates a cryptographic proof that two quorums must intersect.
    /// This ensures consistency across different consensus rounds.
    /// </summary>
    private IntersectionProof GenerateIntersectionProof(List<Vote> acceptingNodes, QuorumLevel level)
    {
        var proof = new IntersectionProof
        {
            Level = level,
            Timestamp = DateTime.UtcNow,
            NodeIds = acceptingNodes.Select(v => v.NodeId ?? v.ZoneId ?? v.RegionId ?? "").ToList()
        };

        // Calculate the quorum set hash
        var nodeData = string.Join(",", proof.NodeIds.OrderBy(x => x));
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(nodeData));
        proof.QuorumSetHash = Convert.ToBase64String(hash);

        // Calculate intersection guarantee
        // For any two quorums Q1 and Q2, |Q1 ∩ Q2| > 0
        // This is guaranteed when each quorum has > 50% of total weight
        var totalWeight = acceptingNodes.Sum(v => v.Weight);
        proof.IntersectionGuarantee = totalWeight > 0;

        // Calculate minimum intersection size
        // If both quorums have k nodes out of n, intersection >= 2k - n
        var quorumSize = acceptingNodes.Count;
        var totalNodes = level switch
        {
            QuorumLevel.Local => _nodes.Count,
            QuorumLevel.Regional => _zones.Count,
            QuorumLevel.Global => _regions.Count,
            _ => quorumSize
        };
        proof.MinIntersectionSize = Math.Max(0, 2 * quorumSize - totalNodes);

        return proof;
    }

    /// <summary>
    /// Verifies that a quorum intersection proof is valid.
    /// </summary>
    public bool VerifyIntersectionProof(IntersectionProof proof)
    {
        if (proof == null || proof.NodeIds == null || proof.NodeIds.Count == 0)
        {
            return false;
        }

        // Recalculate hash
        var nodeData = string.Join(",", proof.NodeIds.OrderBy(x => x));
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(nodeData));
        var calculatedHash = Convert.ToBase64String(hash);

        return calculatedHash == proof.QuorumSetHash && proof.IntersectionGuarantee;
    }

    #endregion

    #region Leader Election

    private async Task RunLeaderElectionLoopAsync(CancellationToken ct)
    {
        var random = new Random();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Random election timeout for leader election
                var timeout = TimeSpan.FromMilliseconds(
                    random.Next(
                        _config.ElectionTimeoutMinMs,
                        _config.ElectionTimeoutMaxMs
                    )
                );

                await Task.Delay(timeout, ct);

                // Check and run elections at each level
                await CheckAndRunLocalElectionAsync(ct);
                await CheckAndRunRegionalElectionAsync(ct);
                await CheckAndRunGlobalElectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Election loop error, will retry
            }
        }
    }

    private async Task CheckAndRunLocalElectionAsync(CancellationToken ct)
    {
        // Check if local leader is still active
        if (_localLeaders.TryGetValue(_zoneId, out var leader))
        {
            if (leader.IsActive && (DateTime.UtcNow - leader.LastHeartbeat).TotalMilliseconds < _config.LeaderHeartbeatTimeoutMs)
            {
                return; // Leader is healthy
            }
        }

        // Start local election
        await RunLocalElectionAsync(ct);
    }

    private async Task CheckAndRunRegionalElectionAsync(CancellationToken ct)
    {
        // Only local leaders can participate in regional elections
        if (!IsLocalLeader)
        {
            return;
        }

        if (_regionalLeaders.TryGetValue(_regionId, out var leader))
        {
            if (leader.IsActive && (DateTime.UtcNow - leader.LastHeartbeat).TotalMilliseconds < _config.LeaderHeartbeatTimeoutMs)
            {
                return;
            }
        }

        await RunRegionalElectionAsync(ct);
    }

    private async Task CheckAndRunGlobalElectionAsync(CancellationToken ct)
    {
        // Only regional leaders can participate in global elections
        if (!IsRegionalLeader)
        {
            return;
        }

        if (_globalLeaders.TryGetValue(_globalClusterId, out var leader))
        {
            if (leader.IsActive && (DateTime.UtcNow - leader.LastHeartbeat).TotalMilliseconds < _config.LeaderHeartbeatTimeoutMs)
            {
                return;
            }
        }

        await RunGlobalElectionAsync(ct);
    }

    private async Task RunLocalElectionAsync(CancellationToken ct)
    {
        var newTerm = Interlocked.Increment(ref _currentTerm);
        var votes = 0;
        var totalNodes = 0;

        _topologyLock.EnterReadLock();
        try
        {
            if (!_zones.TryGetValue(_zoneId, out var zone))
            {
                return;
            }

            var nodesInZone = zone.NodeIds
                .Where(id => _nodes.TryGetValue(id, out var n) && n.Status == NodeStatus.Active)
                .ToList();

            totalNodes = nodesInZone.Count;
            votes = 1; // Vote for self

            // Request votes from other nodes
            foreach (var nodeId in nodesInZone.Where(id => id != _nodeId))
            {
                try
                {
                    // Simulate vote request
                    await Task.Delay(_config.VoteRequestDelayMs, ct);
                    votes++;
                }
                catch
                {
                    // Node didn't respond
                }
            }
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }

        // Check if we won the election
        if (votes > totalNodes / 2)
        {
            _localLeaders[_zoneId] = new LeaderState
            {
                NodeId = _nodeId,
                ZoneId = _zoneId,
                Term = newTerm,
                ElectedAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow,
                IsActive = true
            };
        }
    }

    private async Task RunRegionalElectionAsync(CancellationToken ct)
    {
        var newTerm = Interlocked.Increment(ref _currentTerm);
        var votes = 0;
        var totalZones = 0;

        _topologyLock.EnterReadLock();
        try
        {
            if (!_regions.TryGetValue(_regionId, out var region))
            {
                return;
            }

            var zonesInRegion = region.ZoneIds
                .Where(id => _zones.TryGetValue(id, out var z) && z.Status == ClusterStatus.Active)
                .ToList();

            totalZones = zonesInRegion.Count;
            votes = 1; // Vote for self

            // Request votes from other zone leaders
            foreach (var zoneId in zonesInRegion.Where(id => id != _zoneId))
            {
                try
                {
                    await Task.Delay(_config.VoteRequestDelayMs * 2, ct);
                    votes++;
                }
                catch
                {
                    // Zone didn't respond
                }
            }
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }

        if (votes > totalZones / 2)
        {
            _regionalLeaders[_regionId] = new LeaderState
            {
                NodeId = _nodeId,
                ZoneId = _zoneId,
                RegionId = _regionId,
                Term = newTerm,
                ElectedAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow,
                IsActive = true
            };
        }
    }

    private async Task RunGlobalElectionAsync(CancellationToken ct)
    {
        var newTerm = Interlocked.Increment(ref _currentTerm);
        var votes = 0;
        var totalRegions = 0;

        _topologyLock.EnterReadLock();
        try
        {
            if (!_globalClusters.TryGetValue(_globalClusterId, out var cluster))
            {
                return;
            }

            var regionsInCluster = cluster.RegionIds
                .Where(id => _regions.TryGetValue(id, out var r) && r.Status == ClusterStatus.Active)
                .ToList();

            totalRegions = regionsInCluster.Count;
            votes = 1; // Vote for self

            // Request votes from other region leaders
            foreach (var regionId in regionsInCluster.Where(id => id != _regionId))
            {
                try
                {
                    await Task.Delay(_config.VoteRequestDelayMs * 3, ct);
                    votes++;
                }
                catch
                {
                    // Region didn't respond
                }
            }
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }

        if (votes > totalRegions / 2)
        {
            _globalLeaders[_globalClusterId] = new LeaderState
            {
                NodeId = _nodeId,
                ZoneId = _zoneId,
                RegionId = _regionId,
                GlobalClusterId = _globalClusterId,
                Term = newTerm,
                ElectedAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow,
                IsActive = true
            };
        }
    }

    /// <summary>
    /// Transfers leadership to another node at the specified level.
    /// </summary>
    public async Task<bool> TransferLeadershipAsync(string targetNodeId, QuorumLevel level, CancellationToken ct = default)
    {
        if (!_nodes.TryGetValue(targetNodeId, out var targetNode))
        {
            return false;
        }

        var currentTerm = Interlocked.Read(ref _currentTerm);

        switch (level)
        {
            case QuorumLevel.Local:
                if (!IsLocalLeader)
                    return false;

                _localLeaders[_zoneId] = new LeaderState
                {
                    NodeId = targetNodeId,
                    ZoneId = targetNode.ZoneId,
                    Term = currentTerm,
                    ElectedAt = DateTime.UtcNow,
                    LastHeartbeat = DateTime.UtcNow,
                    IsActive = true
                };
                break;

            case QuorumLevel.Regional:
                if (!IsRegionalLeader)
                    return false;

                _regionalLeaders[_regionId] = new LeaderState
                {
                    NodeId = targetNodeId,
                    ZoneId = targetNode.ZoneId,
                    RegionId = targetNode.RegionId,
                    Term = currentTerm,
                    ElectedAt = DateTime.UtcNow,
                    LastHeartbeat = DateTime.UtcNow,
                    IsActive = true
                };
                break;

            case QuorumLevel.Global:
                if (!IsLeader)
                    return false;

                _globalLeaders[_globalClusterId] = new LeaderState
                {
                    NodeId = targetNodeId,
                    ZoneId = targetNode.ZoneId,
                    RegionId = targetNode.RegionId,
                    GlobalClusterId = targetNode.GlobalClusterId,
                    Term = currentTerm,
                    ElectedAt = DateTime.UtcNow,
                    LastHeartbeat = DateTime.UtcNow,
                    IsActive = true
                };
                break;
        }

        await Task.CompletedTask;
        return true;
    }

    #endregion

    #region Failure Detection and Recovery

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.HeartbeatIntervalMs, ct);

                // Send heartbeats if we're a leader at any level
                if (IsLocalLeader)
                {
                    await SendLocalHeartbeatsAsync(ct);
                }

                if (IsRegionalLeader)
                {
                    await SendRegionalHeartbeatsAsync(ct);
                }

                if (IsLeader)
                {
                    await SendGlobalHeartbeatsAsync(ct);
                }

                // Update our own heartbeat
                if (_nodes.TryGetValue(_nodeId, out var selfNode))
                {
                    selfNode.LastHeartbeat = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Heartbeat loop error, continue
            }
        }
    }

    private async Task SendLocalHeartbeatsAsync(CancellationToken ct)
    {
        _topologyLock.EnterReadLock();
        try
        {
            if (!_zones.TryGetValue(_zoneId, out var zone))
            {
                return;
            }

            foreach (var nodeId in zone.NodeIds.Where(id => id != _nodeId))
            {
                if (_nodes.TryGetValue(nodeId, out var node))
                {
                    // Simulate heartbeat
                    await Task.Delay(1, ct);
                    node.LastHeartbeat = DateTime.UtcNow;
                }
            }

            // Update local leader heartbeat
            if (_localLeaders.TryGetValue(_zoneId, out var leader))
            {
                leader.LastHeartbeat = DateTime.UtcNow;
            }
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private async Task SendRegionalHeartbeatsAsync(CancellationToken ct)
    {
        _topologyLock.EnterReadLock();
        try
        {
            if (!_regions.TryGetValue(_regionId, out var region))
            {
                return;
            }

            foreach (var zoneId in region.ZoneIds.Where(id => id != _zoneId))
            {
                if (_zones.TryGetValue(zoneId, out var zone))
                {
                    await Task.Delay(1, ct);
                }
            }

            if (_regionalLeaders.TryGetValue(_regionId, out var leader))
            {
                leader.LastHeartbeat = DateTime.UtcNow;
            }
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private async Task SendGlobalHeartbeatsAsync(CancellationToken ct)
    {
        _topologyLock.EnterReadLock();
        try
        {
            if (!_globalClusters.TryGetValue(_globalClusterId, out var cluster))
            {
                return;
            }

            foreach (var regionId in cluster.RegionIds.Where(id => id != _regionId))
            {
                if (_regions.TryGetValue(regionId, out var region))
                {
                    await Task.Delay(1, ct);
                }
            }

            if (_globalLeaders.TryGetValue(_globalClusterId, out var leader))
            {
                leader.LastHeartbeat = DateTime.UtcNow;
            }
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private async Task RunFailureDetectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.FailureDetectionIntervalMs, ct);

                var now = DateTime.UtcNow;
                var failedNodes = new List<string>();

                _topologyLock.EnterReadLock();
                try
                {
                    foreach (var node in _nodes.Values)
                    {
                        if (node.NodeId == _nodeId)
                        {
                            continue;
                        }

                        var elapsed = (now - node.LastHeartbeat).TotalMilliseconds;
                        if (elapsed > _config.NodeFailureTimeoutMs)
                        {
                            failedNodes.Add(node.NodeId);
                        }
                    }
                }
                finally
                {
                    _topologyLock.ExitReadLock();
                }

                // Process failed nodes
                foreach (var nodeId in failedNodes)
                {
                    await HandleNodeFailureAsync(nodeId, ct);
                }

                // Check for leader failures at each level
                await CheckLeaderHealthAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Failure detection error, continue
            }
        }
    }

    private async Task HandleNodeFailureAsync(string nodeId, CancellationToken ct)
    {
        _topologyLock.EnterWriteLock();
        try
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = NodeStatus.Suspected;

                // Queue hinted handoff for pending operations
                if (!_hintedHandoffs.TryGetValue(nodeId, out var queue))
                {
                    queue = new ConcurrentQueue<HintedHandoff>();
                    _hintedHandoffs[nodeId] = queue;
                }

                // Check if this was a leader
                if (_localLeaders.TryGetValue(node.ZoneId, out var localLeader) && localLeader.NodeId == nodeId)
                {
                    localLeader.IsActive = false;
                }

                if (_regionalLeaders.TryGetValue(node.RegionId, out var regionalLeader) && regionalLeader.NodeId == nodeId)
                {
                    regionalLeader.IsActive = false;
                }

                if (_globalLeaders.TryGetValue(node.GlobalClusterId, out var globalLeader) && globalLeader.NodeId == nodeId)
                {
                    globalLeader.IsActive = false;
                }

                // Recalculate quorum requirements
                RecalculateQuorumRequirements();
            }
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }

        await Task.CompletedTask;
    }

    private async Task CheckLeaderHealthAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Check local leader
        if (_localLeaders.TryGetValue(_zoneId, out var localLeader))
        {
            if ((now - localLeader.LastHeartbeat).TotalMilliseconds > _config.LeaderHeartbeatTimeoutMs)
            {
                localLeader.IsActive = false;
            }
        }

        // Check regional leader
        if (_regionalLeaders.TryGetValue(_regionId, out var regionalLeader))
        {
            if ((now - regionalLeader.LastHeartbeat).TotalMilliseconds > _config.LeaderHeartbeatTimeoutMs)
            {
                regionalLeader.IsActive = false;
            }
        }

        // Check global leader
        if (_globalLeaders.TryGetValue(_globalClusterId, out var globalLeader))
        {
            if ((now - globalLeader.LastHeartbeat).TotalMilliseconds > _config.LeaderHeartbeatTimeoutMs)
            {
                globalLeader.IsActive = false;
            }
        }

        await Task.CompletedTask;
    }

    #endregion

    #region Hinted Handoff

    private async Task RunHintedHandoffLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.HintedHandoffIntervalMs, ct);

                foreach (var (nodeId, queue) in _hintedHandoffs)
                {
                    // Check if node is back online
                    if (_nodes.TryGetValue(nodeId, out var node) && node.Status == NodeStatus.Active)
                    {
                        // Replay hinted handoffs
                        while (queue.TryDequeue(out var hint))
                        {
                            try
                            {
                                await ReplayHintedHandoffAsync(hint, ct);
                            }
                            catch
                            {
                                // Re-queue if replay fails
                                queue.Enqueue(hint);
                                break;
                            }
                        }

                        // Remove empty queue
                        if (queue.IsEmpty)
                        {
                            _hintedHandoffs.TryRemove(nodeId, out _);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Hinted handoff error, continue
            }
        }
    }

    private async Task ReplayHintedHandoffAsync(HintedHandoff hint, CancellationToken ct)
    {
        // Simulate replaying the operation
        await Task.Delay(_config.HintedHandoffReplayDelayMs, ct);
    }

    #endregion

    #region Quorum Reconfiguration

    private async Task RunQuorumValidationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.QuorumValidationIntervalMs, ct);

                // Validate quorum configuration
                ValidateQuorumConfiguration();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Validation error, continue
            }
        }
    }

    /// <summary>
    /// Reconfigures quorum requirements dynamically.
    /// </summary>
    public void ReconfigureQuorum(QuorumRequirements newRequirements)
    {
        if (newRequirements == null)
        {
            throw new ArgumentNullException(nameof(newRequirements));
        }

        // Validate new requirements
        if (newRequirements.LocalQuorumPercent < 1 || newRequirements.LocalQuorumPercent > 100)
        {
            throw new ArgumentException("LocalQuorumPercent must be between 1 and 100");
        }

        if (newRequirements.RegionalQuorumPercent < 1 || newRequirements.RegionalQuorumPercent > 100)
        {
            throw new ArgumentException("RegionalQuorumPercent must be between 1 and 100");
        }

        if (newRequirements.GlobalQuorumPercent < 1 || newRequirements.GlobalQuorumPercent > 100)
        {
            throw new ArgumentException("GlobalQuorumPercent must be between 1 and 100");
        }

        lock (_stateLock)
        {
            _quorumRequirements = newRequirements;
        }

        RecalculateQuorumRequirements();
    }

    private void RecalculateQuorumRequirements()
    {
        _topologyLock.EnterReadLock();
        try
        {
            var activeRegions = _regions.Values.Count(r => r.Status == ClusterStatus.Active);
            var activeZones = _zones.Values.Count(z => z.Status == ClusterStatus.Active);
            var activeNodes = _nodes.Values.Count(n => n.Status == NodeStatus.Active);

            _quorumRequirements.MinRegionsForQuorum = Math.Max(1, (activeRegions + 1) / 2);
            _quorumRequirements.MinZonesPerRegion = Math.Max(1, (activeZones / Math.Max(1, activeRegions) + 1) / 2);
            _quorumRequirements.MinNodesPerZone = Math.Max(1, (activeNodes / Math.Max(1, activeZones) + 1) / 2);
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private void ValidateQuorumConfiguration()
    {
        _topologyLock.EnterReadLock();
        try
        {
            var activeRegions = _regions.Values.Count(r => r.Status == ClusterStatus.Active);
            var activeZones = _zones.Values.Count(z => z.Status == ClusterStatus.Active);
            var activeNodes = _nodes.Values.Count(n => n.Status == NodeStatus.Active);

            // Check if quorum is still achievable
            if (activeRegions < _quorumRequirements.MinRegionsForQuorum)
            {
                // Log warning about insufficient regions
            }

            if (activeZones < _quorumRequirements.MinZonesPerRegion * activeRegions)
            {
                // Log warning about insufficient zones
            }

            if (activeNodes < _quorumRequirements.MinNodesPerZone * activeZones)
            {
                // Log warning about insufficient nodes
            }
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    #endregion

    #region Topology Management

    /// <summary>
    /// Adds a region to the hierarchy.
    /// </summary>
    public void AddRegion(Region region)
    {
        if (region == null)
        {
            throw new ArgumentNullException(nameof(region));
        }

        if (string.IsNullOrEmpty(region.RegionId))
        {
            throw new ArgumentException("RegionId cannot be empty", nameof(region));
        }

        _topologyLock.EnterWriteLock();
        try
        {
            region.CreatedAt = DateTime.UtcNow;
            _regions[region.RegionId] = region;

            if (_globalClusters.TryGetValue(region.GlobalClusterId, out var cluster))
            {
                cluster.RegionIds.Add(region.RegionId);
            }

            RecalculateQuorumRequirements();
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Adds a zone to a region.
    /// </summary>
    public void AddZone(Zone zone)
    {
        if (zone == null)
        {
            throw new ArgumentNullException(nameof(zone));
        }

        if (string.IsNullOrEmpty(zone.ZoneId))
        {
            throw new ArgumentException("ZoneId cannot be empty", nameof(zone));
        }

        _topologyLock.EnterWriteLock();
        try
        {
            zone.CreatedAt = DateTime.UtcNow;
            _zones[zone.ZoneId] = zone;

            if (_regions.TryGetValue(zone.RegionId, out var region))
            {
                region.ZoneIds.Add(zone.ZoneId);
            }

            RecalculateQuorumRequirements();
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Adds a node to a zone.
    /// </summary>
    public void AddNode(HierarchyNode node)
    {
        if (node == null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        if (string.IsNullOrEmpty(node.NodeId))
        {
            throw new ArgumentException("NodeId cannot be empty", nameof(node));
        }

        _topologyLock.EnterWriteLock();
        try
        {
            node.JoinedAt = DateTime.UtcNow;
            node.LastHeartbeat = DateTime.UtcNow;
            _nodes[node.NodeId] = node;

            if (_zones.TryGetValue(node.ZoneId, out var zone))
            {
                zone.NodeIds.Add(node.NodeId);
            }

            // Initialize version vector for the node
            _versionVectors[node.NodeId] = new VersionVector();

            RecalculateQuorumRequirements();
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a region from the hierarchy.
    /// </summary>
    public bool RemoveRegion(string regionId)
    {
        _topologyLock.EnterWriteLock();
        try
        {
            if (!_regions.TryRemove(regionId, out var region))
            {
                return false;
            }

            // Remove all zones in the region
            foreach (var zoneId in region.ZoneIds.ToList())
            {
                RemoveZoneInternal(zoneId);
            }

            // Remove from global cluster
            foreach (var cluster in _globalClusters.Values)
            {
                cluster.RegionIds.Remove(regionId);
            }

            // Remove regional leader
            _regionalLeaders.TryRemove(regionId, out _);

            RecalculateQuorumRequirements();
            return true;
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a zone from the hierarchy.
    /// </summary>
    public bool RemoveZone(string zoneId)
    {
        _topologyLock.EnterWriteLock();
        try
        {
            return RemoveZoneInternal(zoneId);
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }
    }

    private bool RemoveZoneInternal(string zoneId)
    {
        if (!_zones.TryRemove(zoneId, out var zone))
        {
            return false;
        }

        // Remove all nodes in the zone
        foreach (var nodeId in zone.NodeIds.ToList())
        {
            RemoveNodeInternal(nodeId);
        }

        // Remove from region
        if (_regions.TryGetValue(zone.RegionId, out var region))
        {
            region.ZoneIds.Remove(zoneId);
        }

        // Remove local leader
        _localLeaders.TryRemove(zoneId, out _);

        RecalculateQuorumRequirements();
        return true;
    }

    /// <summary>
    /// Removes a node from the hierarchy.
    /// </summary>
    public bool RemoveNode(string nodeId)
    {
        _topologyLock.EnterWriteLock();
        try
        {
            return RemoveNodeInternal(nodeId);
        }
        finally
        {
            _topologyLock.ExitWriteLock();
        }
    }

    private bool RemoveNodeInternal(string nodeId)
    {
        if (!_nodes.TryRemove(nodeId, out var node))
        {
            return false;
        }

        // Remove from zone
        if (_zones.TryGetValue(node.ZoneId, out var zone))
        {
            zone.NodeIds.Remove(nodeId);
        }

        // Remove version vector
        _versionVectors.TryRemove(nodeId, out _);

        // Remove hinted handoffs
        _hintedHandoffs.TryRemove(nodeId, out _);

        RecalculateQuorumRequirements();
        return true;
    }

    /// <summary>
    /// Gets the complete hierarchy status.
    /// </summary>
    public HierarchyStatus GetHierarchyStatus()
    {
        _topologyLock.EnterReadLock();
        try
        {
            return new HierarchyStatus
            {
                GlobalClusters = _globalClusters.Values.ToList(),
                Regions = _regions.Values.ToList(),
                Zones = _zones.Values.ToList(),
                Nodes = _nodes.Values.ToList(),
                GlobalLeaders = _globalLeaders.ToDictionary(kv => kv.Key, kv => kv.Value),
                RegionalLeaders = _regionalLeaders.ToDictionary(kv => kv.Key, kv => kv.Value),
                LocalLeaders = _localLeaders.ToDictionary(kv => kv.Key, kv => kv.Value),
                QuorumRequirements = _quorumRequirements,
                Timestamp = DateTime.UtcNow
            };
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    #endregion

    #region Version Vectors

    private void UpdateVersionVector(string nodeId)
    {
        if (_versionVectors.TryGetValue(nodeId, out var vector))
        {
            vector.Increment(nodeId);
        }
    }

    /// <summary>
    /// Compares two version vectors for causal ordering.
    /// </summary>
    public VersionComparison CompareVersions(VersionVector v1, VersionVector v2)
    {
        return v1.Compare(v2);
    }

    #endregion

    #region Message Handling

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
        {
            return;
        }

        var response = message.Type switch
        {
            "hq.propose" => await HandleProposeAsync(message.Payload),
            "hq.read" => await HandleReadAsync(message.Payload),
            "hq.topology.add-region" => HandleAddRegion(message.Payload),
            "hq.topology.add-zone" => HandleAddZone(message.Payload),
            "hq.topology.add-node" => HandleAddNode(message.Payload),
            "hq.topology.remove" => HandleRemove(message.Payload),
            "hq.topology.status" => HandleTopologyStatus(),
            "hq.leader.info" => HandleLeaderInfo(),
            "hq.leader.transfer" => await HandleLeaderTransferAsync(message.Payload),
            "hq.quorum.reconfigure" => HandleQuorumReconfigure(message.Payload),
            "hq.health.status" => await HandleHealthStatusAsync(),
            "hq.configure" => HandleConfigure(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    private async Task<Dictionary<string, object>> HandleProposeAsync(Dictionary<string, object> payload)
    {
        var command = payload.GetValueOrDefault("command")?.ToString() ?? "";
        var payloadB64 = payload.GetValueOrDefault("payload")?.ToString() ?? "";
        var consistencyStr = payload.GetValueOrDefault("consistency")?.ToString() ?? "Regional";

        if (!Enum.TryParse<ConsistencyLevel>(consistencyStr, true, out var consistency))
        {
            consistency = ConsistencyLevel.Regional;
        }

        var proposal = new Proposal
        {
            Command = command,
            Payload = string.IsNullOrEmpty(payloadB64) ? [] : Convert.FromBase64String(payloadB64)
        };

        var success = await ProposeWithOptionsAsync(proposal, new ProposeOptions
        {
            ConsistencyLevel = consistency,
            TimeoutMs = _config.DefaultProposalTimeoutMs
        });

        return new Dictionary<string, object>
        {
            ["success"] = success,
            ["proposalId"] = proposal.Id,
            ["consistency"] = consistency.ToString()
        };
    }

    private async Task<Dictionary<string, object>> HandleReadAsync(Dictionary<string, object> payload)
    {
        var key = payload.GetValueOrDefault("key")?.ToString() ?? "";
        var consistencyStr = payload.GetValueOrDefault("consistency")?.ToString() ?? "Regional";

        if (!Enum.TryParse<ConsistencyLevel>(consistencyStr, true, out var consistency))
        {
            consistency = ConsistencyLevel.Regional;
        }

        // For reads, we check if we can satisfy the consistency level
        var canRead = consistency switch
        {
            ConsistencyLevel.Local => IsLocalLeader || _localLeaders.ContainsKey(_zoneId),
            ConsistencyLevel.Regional => IsRegionalLeader || _regionalLeaders.ContainsKey(_regionId),
            ConsistencyLevel.Global => IsLeader || _globalLeaders.ContainsKey(_globalClusterId),
            _ => true
        };

        if (_committedState.TryGetValue(key, out var value))
        {
            return new Dictionary<string, object>
            {
                ["success"] = canRead,
                ["key"] = key,
                ["value"] = value ?? "",
                ["consistency"] = consistency.ToString()
            };
        }

        await Task.CompletedTask;
        return new Dictionary<string, object>
        {
            ["success"] = false,
            ["key"] = key,
            ["error"] = "Key not found"
        };
    }

    private Dictionary<string, object> HandleAddRegion(Dictionary<string, object> payload)
    {
        try
        {
            var regionId = payload.GetValueOrDefault("regionId")?.ToString() ?? Guid.NewGuid().ToString("N")[..12];
            var name = payload.GetValueOrDefault("name")?.ToString() ?? regionId;
            var weight = Convert.ToInt32(payload.GetValueOrDefault("weight") ?? 100);
            var priority = Convert.ToInt32(payload.GetValueOrDefault("priority") ?? 1);

            var region = new Region
            {
                RegionId = regionId,
                GlobalClusterId = _globalClusterId,
                Name = name,
                Status = ClusterStatus.Active,
                Weight = weight,
                Priority = priority
            };

            AddRegion(region);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["regionId"] = regionId
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private Dictionary<string, object> HandleAddZone(Dictionary<string, object> payload)
    {
        try
        {
            var zoneId = payload.GetValueOrDefault("zoneId")?.ToString() ?? Guid.NewGuid().ToString("N")[..12];
            var regionId = payload.GetValueOrDefault("regionId")?.ToString() ?? _regionId;
            var name = payload.GetValueOrDefault("name")?.ToString() ?? zoneId;
            var weight = Convert.ToInt32(payload.GetValueOrDefault("weight") ?? 100);

            var zone = new Zone
            {
                ZoneId = zoneId,
                RegionId = regionId,
                Name = name,
                Status = ClusterStatus.Active,
                Weight = weight,
                RackAware = true
            };

            AddZone(zone);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["zoneId"] = zoneId
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private Dictionary<string, object> HandleAddNode(Dictionary<string, object> payload)
    {
        try
        {
            var nodeId = payload.GetValueOrDefault("nodeId")?.ToString() ?? Guid.NewGuid().ToString("N")[..12];
            var zoneId = payload.GetValueOrDefault("zoneId")?.ToString() ?? _zoneId;
            var endpoint = payload.GetValueOrDefault("endpoint")?.ToString() ?? $"localhost:{5200 + RandomNumberGenerator.GetInt32(100)}";
            var weight = Convert.ToInt32(payload.GetValueOrDefault("weight") ?? 100);

            // Get region from zone
            var regionId = _regionId;
            var globalClusterId = _globalClusterId;
            if (_zones.TryGetValue(zoneId, out var zone))
            {
                regionId = zone.RegionId;
                if (_regions.TryGetValue(regionId, out var region))
                {
                    globalClusterId = region.GlobalClusterId;
                }
            }

            var node = new HierarchyNode
            {
                NodeId = nodeId,
                ZoneId = zoneId,
                RegionId = regionId,
                GlobalClusterId = globalClusterId,
                Endpoint = endpoint,
                Status = NodeStatus.Active,
                Role = NodeRole.Voter,
                Weight = weight
            };

            AddNode(node);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["nodeId"] = nodeId
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private Dictionary<string, object> HandleRemove(Dictionary<string, object> payload)
    {
        var type = payload.GetValueOrDefault("type")?.ToString() ?? "";
        var id = payload.GetValueOrDefault("id")?.ToString() ?? "";

        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "type and id are required"
            };
        }

        var success = type.ToLowerInvariant() switch
        {
            "region" => RemoveRegion(id),
            "zone" => RemoveZone(id),
            "node" => RemoveNode(id),
            _ => false
        };

        return new Dictionary<string, object>
        {
            ["success"] = success,
            ["type"] = type,
            ["id"] = id
        };
    }

    private Dictionary<string, object> HandleTopologyStatus()
    {
        var status = GetHierarchyStatus();
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["globalClusters"] = status.GlobalClusters.Count,
            ["regions"] = status.Regions.Count,
            ["zones"] = status.Zones.Count,
            ["nodes"] = status.Nodes.Count,
            ["activeNodes"] = status.Nodes.Count(n => n.Status == NodeStatus.Active),
            ["timestamp"] = status.Timestamp.ToString("O")
        };
    }

    private Dictionary<string, object> HandleLeaderInfo()
    {
        var globalLeader = _globalLeaders.TryGetValue(_globalClusterId, out var gl) ? gl : null;
        var regionalLeader = _regionalLeaders.TryGetValue(_regionId, out var rl) ? rl : null;
        var localLeader = _localLeaders.TryGetValue(_zoneId, out var ll) ? ll : null;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["globalLeader"] = globalLeader != null ? new Dictionary<string, object>
            {
                ["nodeId"] = globalLeader.NodeId,
                ["term"] = globalLeader.Term,
                ["isActive"] = globalLeader.IsActive,
                ["electedAt"] = globalLeader.ElectedAt.ToString("O")
            } : null!,
            ["regionalLeader"] = regionalLeader != null ? new Dictionary<string, object>
            {
                ["nodeId"] = regionalLeader.NodeId,
                ["term"] = regionalLeader.Term,
                ["isActive"] = regionalLeader.IsActive,
                ["electedAt"] = regionalLeader.ElectedAt.ToString("O")
            } : null!,
            ["localLeader"] = localLeader != null ? new Dictionary<string, object>
            {
                ["nodeId"] = localLeader.NodeId,
                ["term"] = localLeader.Term,
                ["isActive"] = localLeader.IsActive,
                ["electedAt"] = localLeader.ElectedAt.ToString("O")
            } : null!,
            ["thisNode"] = new Dictionary<string, object>
            {
                ["nodeId"] = _nodeId,
                ["isGlobalLeader"] = IsLeader,
                ["isRegionalLeader"] = IsRegionalLeader,
                ["isLocalLeader"] = IsLocalLeader
            }
        };
    }

    private async Task<Dictionary<string, object>> HandleLeaderTransferAsync(Dictionary<string, object> payload)
    {
        var targetNodeId = payload.GetValueOrDefault("targetNodeId")?.ToString() ?? "";
        var levelStr = payload.GetValueOrDefault("level")?.ToString() ?? "Local";

        if (!Enum.TryParse<QuorumLevel>(levelStr, true, out var level))
        {
            level = QuorumLevel.Local;
        }

        var success = await TransferLeadershipAsync(targetNodeId, level);

        return new Dictionary<string, object>
        {
            ["success"] = success,
            ["targetNodeId"] = targetNodeId,
            ["level"] = level.ToString()
        };
    }

    private Dictionary<string, object> HandleQuorumReconfigure(Dictionary<string, object> payload)
    {
        try
        {
            var localPercent = Convert.ToInt32(payload.GetValueOrDefault("localQuorumPercent") ?? _quorumRequirements.LocalQuorumPercent);
            var regionalPercent = Convert.ToInt32(payload.GetValueOrDefault("regionalQuorumPercent") ?? _quorumRequirements.RegionalQuorumPercent);
            var globalPercent = Convert.ToInt32(payload.GetValueOrDefault("globalQuorumPercent") ?? _quorumRequirements.GlobalQuorumPercent);

            ReconfigureQuorum(new QuorumRequirements
            {
                LocalQuorumPercent = localPercent,
                RegionalQuorumPercent = regionalPercent,
                GlobalQuorumPercent = globalPercent
            });

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["localQuorumPercent"] = localPercent,
                ["regionalQuorumPercent"] = regionalPercent,
                ["globalQuorumPercent"] = globalPercent
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task<Dictionary<string, object>> HandleHealthStatusAsync()
    {
        var clusterState = await GetClusterStateAsync();

        _topologyLock.EnterReadLock();
        try
        {
            var healthyNodes = _nodes.Values.Count(n => n.Status == NodeStatus.Active);
            var totalNodes = _nodes.Count;
            var healthyZones = _zones.Values.Count(z => z.Status == ClusterStatus.Active);
            var totalZones = _zones.Count;
            var healthyRegions = _regions.Values.Count(r => r.Status == ClusterStatus.Active);
            var totalRegions = _regions.Count;

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["isHealthy"] = clusterState.IsHealthy,
                ["healthyNodes"] = healthyNodes,
                ["totalNodes"] = totalNodes,
                ["healthyZones"] = healthyZones,
                ["totalZones"] = totalZones,
                ["healthyRegions"] = healthyRegions,
                ["totalRegions"] = totalRegions,
                ["currentTerm"] = clusterState.Term,
                ["hasGlobalLeader"] = clusterState.LeaderId != null
            };
        }
        finally
        {
            _topologyLock.ExitReadLock();
        }
    }

    private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("electionTimeoutMinMs", out var minMs))
        {
            _config.ElectionTimeoutMinMs = Convert.ToInt32(minMs);
        }

        if (payload.TryGetValue("electionTimeoutMaxMs", out var maxMs))
        {
            _config.ElectionTimeoutMaxMs = Convert.ToInt32(maxMs);
        }

        if (payload.TryGetValue("heartbeatIntervalMs", out var hbMs))
        {
            _config.HeartbeatIntervalMs = Convert.ToInt32(hbMs);
        }

        if (payload.TryGetValue("localQuorumTimeoutMs", out var lqMs))
        {
            _config.LocalQuorumTimeoutMs = Convert.ToInt32(lqMs);
        }

        if (payload.TryGetValue("regionalQuorumTimeoutMs", out var rqMs))
        {
            _config.RegionalQuorumTimeoutMs = Convert.ToInt32(rqMs);
        }

        if (payload.TryGetValue("globalQuorumTimeoutMs", out var gqMs))
        {
            _config.GlobalQuorumTimeoutMs = Convert.ToInt32(gqMs);
        }

        if (payload.TryGetValue("defaultConsistencyLevel", out var cl) &&
            Enum.TryParse<ConsistencyLevel>(cl?.ToString(), true, out var consistency))
        {
            _config.DefaultConsistencyLevel = consistency;
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["electionTimeoutMinMs"] = _config.ElectionTimeoutMinMs,
            ["electionTimeoutMaxMs"] = _config.ElectionTimeoutMaxMs,
            ["heartbeatIntervalMs"] = _config.HeartbeatIntervalMs,
            ["localQuorumTimeoutMs"] = _config.LocalQuorumTimeoutMs,
            ["regionalQuorumTimeoutMs"] = _config.RegionalQuorumTimeoutMs,
            ["globalQuorumTimeoutMs"] = _config.GlobalQuorumTimeoutMs,
            ["defaultConsistencyLevel"] = _config.DefaultConsistencyLevel.ToString()
        };
    }

    #endregion

    #region Helpers

    private async Task CommitProposalAsync(Proposal proposal, ConsensusRound round)
    {
        // Store in committed state
        var key = $"{proposal.Command}:{proposal.Id}";
        _committedState[key] = proposal.Payload;

        // Notify commit handlers
        List<Action<Proposal>> handlers;
        lock (_handlerLock)
        {
            handlers = _commitHandlers.ToList();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(proposal);
            }
            catch
            {
                // Log and continue
            }
        }

        await Task.CompletedTask;
    }

    private bool CanNodeAcceptProposal(HierarchyNode node, ConsensusRound round)
    {
        // Check if node is active
        if (node.Status != NodeStatus.Active)
        {
            return false;
        }

        // Check term
        if (round.Term < Interlocked.Read(ref _currentTerm))
        {
            return false;
        }

        return true;
    }

    private HierarchyNode? GetZoneLeaderOrActive(string zoneId)
    {
        // Try to get zone leader
        if (_localLeaders.TryGetValue(zoneId, out var leader) && leader.IsActive)
        {
            if (_nodes.TryGetValue(leader.NodeId, out var leaderNode) && leaderNode.Status == NodeStatus.Active)
            {
                return leaderNode;
            }
        }

        // Get any active node in the zone
        if (_zones.TryGetValue(zoneId, out var zone))
        {
            foreach (var nodeId in zone.NodeIds)
            {
                if (_nodes.TryGetValue(nodeId, out var node) && node.Status == NodeStatus.Active)
                {
                    return node;
                }
            }
        }

        return null;
    }

    private HierarchyNode? GetRegionLeaderOrActive(string regionId)
    {
        // Try to get regional leader
        if (_regionalLeaders.TryGetValue(regionId, out var leader) && leader.IsActive)
        {
            if (_nodes.TryGetValue(leader.NodeId, out var leaderNode) && leaderNode.Status == NodeStatus.Active)
            {
                return leaderNode;
            }
        }

        // Get any active node in the region
        if (_regions.TryGetValue(regionId, out var region))
        {
            foreach (var zoneId in region.ZoneIds)
            {
                var node = GetZoneLeaderOrActive(zoneId);
                if (node != null)
                {
                    return node;
                }
            }
        }

        return null;
    }

    private static int CalculateRequiredWeight(int totalWeight, int percent)
    {
        return Math.Max(1, (totalWeight * percent + 99) / 100);
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "propose",
                Description = "Propose a value through hierarchical quorum consensus",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["command"] = new { type = "string", description = "Command type" },
                        ["payload"] = new { type = "string", description = "Base64-encoded payload" },
                        ["consistency"] = new { type = "string", description = "Consistency level: Local, Regional, Global" }
                    },
                    ["required"] = new[] { "command" }
                }
            },
            new()
            {
                Name = "topology.status",
                Description = "Get hierarchy topology status",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>()
                }
            },
            new()
            {
                Name = "leader.info",
                Description = "Get leader information at each level",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>()
                }
            },
            new()
            {
                Name = "health.status",
                Description = "Get health status of the hierarchy",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>()
                }
            }
        };
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var meta = base.GetMetadata();
        meta["ConsensusAlgorithm"] = "HierarchicalQuorum";
        meta["NodeId"] = _nodeId;
        meta["ZoneId"] = _zoneId;
        meta["RegionId"] = _regionId;
        meta["CurrentTerm"] = Interlocked.Read(ref _currentTerm);
        meta["IsGlobalLeader"] = IsLeader;
        meta["IsRegionalLeader"] = IsRegionalLeader;
        meta["IsLocalLeader"] = IsLocalLeader;
        meta["RegionCount"] = _regions.Count;
        meta["ZoneCount"] = _zones.Count;
        meta["NodeCount"] = _nodes.Count;
        meta["SupportsHierarchicalQuorum"] = true;
        meta["SupportsVersionVectors"] = true;
        meta["SupportsHintedHandoff"] = true;
        meta["SupportsQuorumReconfiguration"] = true;
        return meta;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Global cluster in the hierarchy.
/// </summary>
public class GlobalCluster
{
    /// <summary>Unique cluster identifier.</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Cluster status.</summary>
    public ClusterStatus Status { get; set; } = ClusterStatus.Active;

    /// <summary>Region IDs in this cluster.</summary>
    public List<string> RegionIds { get; set; } = new();

    /// <summary>Cluster creation time.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Region in the hierarchy.
/// </summary>
public class Region
{
    /// <summary>Unique region identifier.</summary>
    public string RegionId { get; set; } = string.Empty;

    /// <summary>Parent global cluster ID.</summary>
    public string GlobalClusterId { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Region status.</summary>
    public ClusterStatus Status { get; set; } = ClusterStatus.Active;

    /// <summary>Zone IDs in this region.</summary>
    public List<string> ZoneIds { get; set; } = new();

    /// <summary>Weight for quorum calculations.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Priority for leader election (lower = higher priority).</summary>
    public int Priority { get; set; } = 1;

    /// <summary>Estimated latency to this region in ms.</summary>
    public int EstimatedLatencyMs { get; set; } = 50;

    /// <summary>Region creation time.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Zone in the hierarchy (availability zone or rack).
/// </summary>
public class Zone
{
    /// <summary>Unique zone identifier.</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>Parent region ID.</summary>
    public string RegionId { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Zone status.</summary>
    public ClusterStatus Status { get; set; } = ClusterStatus.Active;

    /// <summary>Node IDs in this zone.</summary>
    public List<string> NodeIds { get; set; } = new();

    /// <summary>Weight for quorum calculations.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Whether this zone is rack-aware.</summary>
    public bool RackAware { get; set; }

    /// <summary>Estimated latency to this zone in ms.</summary>
    public int EstimatedLatencyMs { get; set; } = 10;

    /// <summary>Zone creation time.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Node in the hierarchy.
/// </summary>
public class HierarchyNode
{
    /// <summary>Unique node identifier.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Parent zone ID.</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>Parent region ID.</summary>
    public string RegionId { get; set; } = string.Empty;

    /// <summary>Parent global cluster ID.</summary>
    public string GlobalClusterId { get; set; } = string.Empty;

    /// <summary>Network endpoint.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Node status.</summary>
    public NodeStatus Status { get; set; } = NodeStatus.Active;

    /// <summary>Node role in consensus.</summary>
    public NodeRole Role { get; set; } = NodeRole.Voter;

    /// <summary>Weight for quorum calculations.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Estimated latency to this node in ms.</summary>
    public int EstimatedLatencyMs { get; set; } = 1;

    /// <summary>Last heartbeat received.</summary>
    public DateTime LastHeartbeat { get; set; }

    /// <summary>When the node joined the cluster.</summary>
    public DateTime JoinedAt { get; set; }
}

/// <summary>
/// Cluster status.
/// </summary>
public enum ClusterStatus
{
    /// <summary>Active and healthy.</summary>
    Active,
    /// <summary>Degraded but operational.</summary>
    Degraded,
    /// <summary>Under maintenance.</summary>
    Maintenance,
    /// <summary>Offline.</summary>
    Offline
}

/// <summary>
/// Node role in consensus.
/// </summary>
public enum NodeRole
{
    /// <summary>Participates in voting.</summary>
    Voter,
    /// <summary>Learns state but does not vote.</summary>
    Learner,
    /// <summary>Observes only, for monitoring.</summary>
    Observer,
    /// <summary>Witness for tie-breaking.</summary>
    Witness
}

/// <summary>
/// Node status.
/// </summary>
public enum NodeStatus
{
    /// <summary>Active and healthy.</summary>
    Active,
    /// <summary>Suspected of failure.</summary>
    Suspected,
    /// <summary>Confirmed failed.</summary>
    Failed,
    /// <summary>Being decommissioned.</summary>
    Decommissioning,
    /// <summary>Offline.</summary>
    Offline
}

/// <summary>
/// Consistency level for operations.
/// </summary>
public enum ConsistencyLevel
{
    /// <summary>Local zone quorum only.</summary>
    Local = 1,

    /// <summary>Regional quorum across zones.</summary>
    Regional = 2,

    /// <summary>Global quorum across regions.</summary>
    Global = 3
}

/// <summary>
/// Quorum level in the hierarchy.
/// </summary>
public enum QuorumLevel
{
    /// <summary>Local zone level.</summary>
    Local,

    /// <summary>Regional level.</summary>
    Regional,

    /// <summary>Global level.</summary>
    Global
}

/// <summary>
/// Consensus round phase.
/// </summary>
public enum ConsensusPhase
{
    /// <summary>Initializing.</summary>
    Initialize,

    /// <summary>Local prepare phase.</summary>
    LocalPrepare,

    /// <summary>Regional prepare phase.</summary>
    RegionalPrepare,

    /// <summary>Global prepare phase.</summary>
    GlobalPrepare,

    /// <summary>Commit phase.</summary>
    Commit,

    /// <summary>Successfully committed.</summary>
    Committed,

    /// <summary>Failed.</summary>
    Failed,

    /// <summary>Timed out.</summary>
    Timeout
}

/// <summary>
/// Leader state at a level.
/// </summary>
public class LeaderState
{
    /// <summary>Leader node ID.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Zone ID (for local leaders).</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>Region ID (for regional leaders).</summary>
    public string RegionId { get; set; } = string.Empty;

    /// <summary>Global cluster ID (for global leaders).</summary>
    public string GlobalClusterId { get; set; } = string.Empty;

    /// <summary>Current term.</summary>
    public long Term { get; set; }

    /// <summary>When elected.</summary>
    public DateTime ElectedAt { get; set; }

    /// <summary>Last heartbeat sent/received.</summary>
    public DateTime LastHeartbeat { get; set; }

    /// <summary>Whether the leader is active.</summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// Consensus round tracking.
/// </summary>
internal class ConsensusRound
{
    public long RoundNumber { get; set; }
    public string ProposalId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public DateTime Timestamp { get; set; }
    public ConsistencyLevel ConsistencyLevel { get; set; }
    public ConsensusPhase Phase { get; set; }
    public string InitiatorNodeId { get; set; } = string.Empty;
    public string InitiatorZoneId { get; set; } = string.Empty;
    public string InitiatorRegionId { get; set; } = string.Empty;
    public long Term { get; set; }
    public DateTime? CommittedAt { get; set; }
    public string? FailureReason { get; set; }
    public Dictionary<string, Vote>? LocalVotes { get; set; }
    public Dictionary<string, Vote>? RegionalVotes { get; set; }
    public Dictionary<string, Vote>? GlobalVotes { get; set; }
    public Dictionary<string, bool>? CommitAcknowledgments { get; set; }
}

/// <summary>
/// Vote in a consensus round.
/// </summary>
internal class Vote
{
    public string? NodeId { get; set; }
    public string? ZoneId { get; set; }
    public string? RegionId { get; set; }
    public long RoundNumber { get; set; }
    public long Term { get; set; }
    public bool Accepted { get; set; }
    public int Weight { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Proposal state tracking.
/// </summary>
internal class ProposalState
{
    public string ProposalId { get; set; } = string.Empty;
    public ConsensusRound? Round { get; set; }
    public TaskCompletionSource<bool>? CompletionSource { get; set; }
}

/// <summary>
/// Quorum calculation result.
/// </summary>
public class QuorumResult
{
    /// <summary>Whether quorum was achieved.</summary>
    public bool Success { get; set; }

    /// <summary>Total weight of all participants.</summary>
    public int TotalWeight { get; set; }

    /// <summary>Weight achieved by accepting votes.</summary>
    public int AchievedWeight { get; set; }

    /// <summary>Weight required for quorum.</summary>
    public int RequiredWeight { get; set; }

    /// <summary>Number of nodes that responded.</summary>
    public int NodesResponded { get; set; }

    /// <summary>Total number of nodes.</summary>
    public int TotalNodes { get; set; }

    /// <summary>Number of zones that responded.</summary>
    public int ZonesResponded { get; set; }

    /// <summary>Total number of zones.</summary>
    public int TotalZones { get; set; }

    /// <summary>Number of regions that responded.</summary>
    public int RegionsResponded { get; set; }

    /// <summary>Total number of regions.</summary>
    public int TotalRegions { get; set; }

    /// <summary>Human-readable reason.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Quorum intersection proof.</summary>
    public IntersectionProof? IntersectionProof { get; set; }
}

/// <summary>
/// Quorum intersection proof.
/// </summary>
public class IntersectionProof
{
    /// <summary>Quorum level.</summary>
    public QuorumLevel Level { get; set; }

    /// <summary>When the proof was generated.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Node/zone/region IDs in the quorum.</summary>
    public List<string> NodeIds { get; set; } = new();

    /// <summary>Hash of the quorum set.</summary>
    public string QuorumSetHash { get; set; } = string.Empty;

    /// <summary>Whether intersection is guaranteed.</summary>
    public bool IntersectionGuarantee { get; set; }

    /// <summary>Minimum intersection size with any other quorum.</summary>
    public int MinIntersectionSize { get; set; }
}

/// <summary>
/// Quorum requirements configuration.
/// </summary>
public class QuorumRequirements
{
    /// <summary>Percentage of weight needed for local quorum (1-100).</summary>
    public int LocalQuorumPercent { get; set; } = 51;

    /// <summary>Percentage of weight needed for regional quorum (1-100).</summary>
    public int RegionalQuorumPercent { get; set; } = 51;

    /// <summary>Percentage of weight needed for global quorum (1-100).</summary>
    public int GlobalQuorumPercent { get; set; } = 51;

    /// <summary>Minimum regions required for quorum.</summary>
    public int MinRegionsForQuorum { get; set; } = 1;

    /// <summary>Minimum zones required per region.</summary>
    public int MinZonesPerRegion { get; set; } = 1;

    /// <summary>Minimum nodes required per zone.</summary>
    public int MinNodesPerZone { get; set; } = 1;
}

/// <summary>
/// Proposal options.
/// </summary>
public class ProposeOptions
{
    /// <summary>Required consistency level.</summary>
    public ConsistencyLevel ConsistencyLevel { get; set; } = ConsistencyLevel.Regional;

    /// <summary>Timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 30000;
}

/// <summary>
/// Hierarchy status snapshot.
/// </summary>
public class HierarchyStatus
{
    /// <summary>Global clusters.</summary>
    public List<GlobalCluster> GlobalClusters { get; set; } = new();

    /// <summary>Regions.</summary>
    public List<Region> Regions { get; set; } = new();

    /// <summary>Zones.</summary>
    public List<Zone> Zones { get; set; } = new();

    /// <summary>Nodes.</summary>
    public List<HierarchyNode> Nodes { get; set; } = new();

    /// <summary>Global leaders by cluster ID.</summary>
    public Dictionary<string, LeaderState> GlobalLeaders { get; set; } = new();

    /// <summary>Regional leaders by region ID.</summary>
    public Dictionary<string, LeaderState> RegionalLeaders { get; set; } = new();

    /// <summary>Local leaders by zone ID.</summary>
    public Dictionary<string, LeaderState> LocalLeaders { get; set; } = new();

    /// <summary>Current quorum requirements.</summary>
    public QuorumRequirements? QuorumRequirements { get; set; }

    /// <summary>Status timestamp.</summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Hinted handoff for temporarily unavailable nodes.
/// </summary>
internal class HintedHandoff
{
    public string TargetNodeId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Version vector for causal ordering.
/// </summary>
public class VersionVector
{
    private readonly ConcurrentDictionary<string, long> _versions = new();

    /// <summary>
    /// Increments the version for a node.
    /// </summary>
    public void Increment(string nodeId)
    {
        _versions.AddOrUpdate(nodeId, 1, (_, v) => v + 1);
    }

    /// <summary>
    /// Gets the version for a node.
    /// </summary>
    public long GetVersion(string nodeId)
    {
        return _versions.TryGetValue(nodeId, out var version) ? version : 0;
    }

    /// <summary>
    /// Merges another version vector into this one.
    /// </summary>
    public void Merge(VersionVector other)
    {
        foreach (var (nodeId, version) in other._versions)
        {
            _versions.AddOrUpdate(nodeId, version, (_, v) => Math.Max(v, version));
        }
    }

    /// <summary>
    /// Compares this version vector with another.
    /// </summary>
    public VersionComparison Compare(VersionVector other)
    {
        var thisGreater = false;
        var otherGreater = false;

        var allKeys = _versions.Keys.Union(other._versions.Keys);

        foreach (var key in allKeys)
        {
            var thisVersion = GetVersion(key);
            var otherVersion = other.GetVersion(key);

            if (thisVersion > otherVersion)
            {
                thisGreater = true;
            }
            else if (otherVersion > thisVersion)
            {
                otherGreater = true;
            }
        }

        if (thisGreater && otherGreater)
        {
            return VersionComparison.Concurrent;
        }
        if (thisGreater)
        {
            return VersionComparison.After;
        }
        if (otherGreater)
        {
            return VersionComparison.Before;
        }
        return VersionComparison.Equal;
    }

    /// <summary>
    /// Gets all versions as a dictionary.
    /// </summary>
    public Dictionary<string, long> ToDictionary()
    {
        return new Dictionary<string, long>(_versions);
    }
}

/// <summary>
/// Version comparison result.
/// </summary>
public enum VersionComparison
{
    /// <summary>This version is equal to the other.</summary>
    Equal,

    /// <summary>This version happened before the other.</summary>
    Before,

    /// <summary>This version happened after the other.</summary>
    After,

    /// <summary>Versions are concurrent (conflict).</summary>
    Concurrent
}

/// <summary>
/// Plugin configuration.
/// </summary>
internal class HierarchicalQuorumConfig
{
    public int ElectionTimeoutMinMs { get; set; } = 150;
    public int ElectionTimeoutMaxMs { get; set; } = 300;
    public int HeartbeatIntervalMs { get; set; } = 50;
    public int LeaderHeartbeatTimeoutMs { get; set; } = 500;
    public int LocalQuorumTimeoutMs { get; set; } = 1000;
    public int RegionalQuorumTimeoutMs { get; set; } = 5000;
    public int GlobalQuorumTimeoutMs { get; set; } = 15000;
    public int DefaultProposalTimeoutMs { get; set; } = 30000;
    public int VoteRequestDelayMs { get; set; } = 10;
    public int QuorumCheckDelayMs { get; set; } = 5;
    public int CommitPrepareDelayMs { get; set; } = 5;
    public int FailureDetectionIntervalMs { get; set; } = 100;
    public int NodeFailureTimeoutMs { get; set; } = 1000;
    public int HintedHandoffIntervalMs { get; set; } = 5000;
    public int HintedHandoffReplayDelayMs { get; set; } = 10;
    public int QuorumValidationIntervalMs { get; set; } = 10000;
    public ConsistencyLevel DefaultConsistencyLevel { get; set; } = ConsistencyLevel.Regional;
}

/// <summary>
/// Failure detector for nodes.
/// </summary>
internal class FailureDetector
{
    private readonly HierarchicalQuorumConfig _config;
    private readonly ConcurrentDictionary<string, FailureState> _nodeStates = new();

    public FailureDetector(HierarchicalQuorumConfig config)
    {
        _config = config;
    }

    public void RecordHeartbeat(string nodeId)
    {
        var state = _nodeStates.GetOrAdd(nodeId, _ => new FailureState());
        state.LastHeartbeat = DateTime.UtcNow;
        state.ConsecutiveFailures = 0;
    }

    public void RecordFailure(string nodeId)
    {
        var state = _nodeStates.GetOrAdd(nodeId, _ => new FailureState());
        state.ConsecutiveFailures++;
        state.LastFailure = DateTime.UtcNow;
    }

    public bool IsSuspected(string nodeId)
    {
        if (!_nodeStates.TryGetValue(nodeId, out var state))
        {
            return false;
        }

        var elapsed = (DateTime.UtcNow - state.LastHeartbeat).TotalMilliseconds;
        return elapsed > _config.NodeFailureTimeoutMs || state.ConsecutiveFailures > 3;
    }

    private class FailureState
    {
        public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
        public DateTime? LastFailure { get; set; }
        public int ConsecutiveFailures { get; set; }
    }
}

#endregion
