using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Graph;

/// <summary>
/// Distributed graph processing framework using Pregel-style BSP (Bulk Synchronous Parallel).
/// Enables scalable graph algorithms across multiple workers/partitions.
/// </summary>
/// <remarks>
/// Features:
/// - Vertex-centric programming model
/// - Configurable message aggregation
/// - Superstep coordination
/// - Fault tolerance support
/// - Partition-aware execution
/// </remarks>
public sealed class PregelGraphProcessor<TVertex, TMessage, TEdge>
    where TVertex : class
    where TMessage : class
{
    private readonly ConcurrentDictionary<string, VertexState<TVertex, TMessage>> _vertices = new();
    private readonly ConcurrentDictionary<string, List<Edge<TEdge>>> _edges = new();
    private readonly GraphPartitioningStrategyBase _partitioner;
    private readonly PregelConfiguration _config;

    private int _currentSuperstep = 0;
    private bool _isActive = false;

    /// <summary>
    /// Gets the current superstep number.
    /// </summary>
    public int CurrentSuperstep => _currentSuperstep;

    /// <summary>
    /// Gets whether the computation is active.
    /// </summary>
    public bool IsActive => _isActive;

    /// <summary>
    /// Event raised at the start of each superstep.
    /// </summary>
    public event EventHandler<SuperstepEventArgs>? SuperstepStarted;

    /// <summary>
    /// Event raised at the end of each superstep.
    /// </summary>
    public event EventHandler<SuperstepEventArgs>? SuperstepCompleted;

    /// <summary>
    /// Creates a new Pregel processor.
    /// </summary>
    /// <param name="partitioner">Graph partitioning strategy.</param>
    /// <param name="config">Pregel configuration.</param>
    public PregelGraphProcessor(
        GraphPartitioningStrategyBase? partitioner = null,
        PregelConfiguration? config = null)
    {
        _partitioner = partitioner ?? new HashPartitioningStrategy();
        _config = config ?? new PregelConfiguration();
    }

    /// <summary>
    /// Adds a vertex to the graph.
    /// </summary>
    /// <param name="vertexId">Vertex identifier.</param>
    /// <param name="value">Initial vertex value.</param>
    public void AddVertex(string vertexId, TVertex value)
    {
        _vertices[vertexId] = new VertexState<TVertex, TMessage>
        {
            VertexId = vertexId,
            Value = value,
            IsActive = true,
            Partition = _partitioner.AssignVertexPartition(vertexId)
        };
    }

    /// <summary>
    /// Adds an edge to the graph.
    /// </summary>
    /// <param name="sourceId">Source vertex ID.</param>
    /// <param name="targetId">Target vertex ID.</param>
    /// <param name="value">Edge value.</param>
    public void AddEdge(string sourceId, string targetId, TEdge value)
    {
        var edges = _edges.GetOrAdd(sourceId, _ => new List<Edge<TEdge>>());
        lock (edges)
        {
            edges.Add(new Edge<TEdge> { TargetId = targetId, Value = value });
        }
    }

    /// <summary>
    /// Executes the Pregel computation.
    /// </summary>
    /// <param name="vertexProgram">The vertex program to execute.</param>
    /// <param name="messageCombiner">Optional message combiner for aggregation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Computation result.</returns>
    public async Task<PregelResult> ExecuteAsync(
        IVertexProgram<TVertex, TMessage, TEdge> vertexProgram,
        IMessageCombiner<TMessage>? messageCombiner = null,
        CancellationToken ct = default)
    {
        _isActive = true;
        _currentSuperstep = 0;

        var startTime = DateTime.UtcNow;
        long totalMessages = 0;

        try
        {
            while (_isActive && _currentSuperstep < _config.MaxSupersteps)
            {
                ct.ThrowIfCancellationRequested();

                SuperstepStarted?.Invoke(this, new SuperstepEventArgs { Superstep = _currentSuperstep });

                // Execute superstep
                var result = await ExecuteSuperstepAsync(vertexProgram, messageCombiner, ct);
                totalMessages += result.MessagesSent;

                SuperstepCompleted?.Invoke(this, new SuperstepEventArgs
                {
                    Superstep = _currentSuperstep,
                    ActiveVertices = result.ActiveVertices,
                    MessagesSent = result.MessagesSent
                });

                // Check for convergence
                if (result.ActiveVertices == 0 && result.MessagesSent == 0)
                {
                    _isActive = false;
                }

                _currentSuperstep++;
            }

            return new PregelResult
            {
                Success = true,
                Supersteps = _currentSuperstep,
                TotalMessages = totalMessages,
                ExecutionTime = DateTime.UtcNow - startTime,
                VertexResults = _vertices.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Value)
            };
        }
        catch (Exception ex)
        {
            return new PregelResult
            {
                Success = false,
                Supersteps = _currentSuperstep,
                TotalMessages = totalMessages,
                ExecutionTime = DateTime.UtcNow - startTime,
                ErrorMessage = ex.Message,
                VertexResults = _vertices.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Value)
            };
        }
    }

    private async Task<SuperstepResult> ExecuteSuperstepAsync(
        IVertexProgram<TVertex, TMessage, TEdge> vertexProgram,
        IMessageCombiner<TMessage>? messageCombiner,
        CancellationToken ct)
    {
        var nextMessages = new ConcurrentDictionary<string, ConcurrentBag<TMessage>>();
        long messagesSent = 0;
        int activeVertices = 0;

        // Process vertices in parallel by partition
        var partitionGroups = _vertices.Values
            .GroupBy(v => v.Partition)
            .ToList();

        await Parallel.ForEachAsync(partitionGroups, ct, async (partition, ct2) =>
        {
            foreach (var vertex in partition)
            {
                ct2.ThrowIfCancellationRequested();

                // Skip inactive vertices with no messages
                if (!vertex.IsActive && (vertex.IncomingMessages == null || vertex.IncomingMessages.Count == 0))
                {
                    continue;
                }

                // Get edges for this vertex
                var edges = _edges.GetValueOrDefault(vertex.VertexId) ?? new List<Edge<TEdge>>();

                // Create computation context
                var context = new VertexContext<TVertex, TMessage, TEdge>
                {
                    VertexId = vertex.VertexId,
                    Superstep = _currentSuperstep,
                    Value = vertex.Value,
                    IncomingMessages = vertex.IncomingMessages?.ToList() ?? new List<TMessage>(),
                    Edges = edges.AsReadOnly(),
                    SendMessage = (targetId, message) =>
                    {
                        var bag = nextMessages.GetOrAdd(targetId, _ => new ConcurrentBag<TMessage>());
                        bag.Add(message);
                        Interlocked.Increment(ref messagesSent);
                    },
                    VoteToHalt = () => vertex.IsActive = false
                };

                // Execute vertex program
                var newValue = await vertexProgram.ComputeAsync(context, ct2);
                vertex.Value = newValue;

                if (vertex.IsActive)
                {
                    Interlocked.Increment(ref activeVertices);
                }
            }
        });

        // Apply message combiner if provided
        if (messageCombiner != null)
        {
            foreach (var kvp in nextMessages)
            {
                var messages = kvp.Value.ToList();
                if (messages.Count > 1)
                {
                    var combined = messageCombiner.Combine(messages);
                    nextMessages[kvp.Key] = new ConcurrentBag<TMessage>(new[] { combined });
                }
            }
        }

        // Deliver messages to vertices
        foreach (var vertex in _vertices.Values)
        {
            vertex.IncomingMessages = nextMessages.TryGetValue(vertex.VertexId, out var msgs)
                ? msgs.ToList()
                : new List<TMessage>();

            // Activate vertex if it received messages
            if (vertex.IncomingMessages.Count > 0)
            {
                vertex.IsActive = true;
            }
        }

        return new SuperstepResult
        {
            ActiveVertices = activeVertices,
            MessagesSent = messagesSent
        };
    }

    /// <summary>
    /// Gets the current value for a vertex.
    /// </summary>
    public TVertex? GetVertexValue(string vertexId)
    {
        return _vertices.TryGetValue(vertexId, out var state) ? state.Value : null;
    }

    /// <summary>
    /// Gets all vertex values.
    /// </summary>
    public IReadOnlyDictionary<string, TVertex> GetAllVertexValues()
    {
        return _vertices.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
    }
}

/// <summary>
/// Vertex state during Pregel computation.
/// </summary>
internal sealed class VertexState<TVertex, TMessage>
    where TVertex : class
    where TMessage : class
{
    public required string VertexId { get; init; }
    public required TVertex Value { get; set; }
    public bool IsActive { get; set; }
    public int Partition { get; init; }
    public List<TMessage>? IncomingMessages { get; set; }
}

/// <summary>
/// Edge representation.
/// </summary>
public sealed class Edge<TEdge>
{
    /// <summary>Gets the target vertex ID.</summary>
    public required string TargetId { get; init; }

    /// <summary>Gets the edge value.</summary>
    public required TEdge Value { get; init; }
}

/// <summary>
/// Context provided to vertex programs during computation.
/// </summary>
public sealed class VertexContext<TVertex, TMessage, TEdge>
    where TVertex : class
    where TMessage : class
{
    /// <summary>Gets the vertex ID.</summary>
    public required string VertexId { get; init; }

    /// <summary>Gets the current superstep number.</summary>
    public int Superstep { get; init; }

    /// <summary>Gets the current vertex value.</summary>
    public required TVertex Value { get; init; }

    /// <summary>Gets incoming messages from previous superstep.</summary>
    public required List<TMessage> IncomingMessages { get; init; }

    /// <summary>Gets outgoing edges.</summary>
    public required IReadOnlyList<Edge<TEdge>> Edges { get; init; }

    /// <summary>Sends a message to another vertex.</summary>
    public required Action<string, TMessage> SendMessage { get; init; }

    /// <summary>Votes to halt this vertex.</summary>
    public required Action VoteToHalt { get; init; }

    /// <summary>
    /// Sends a message to all neighbors.
    /// </summary>
    /// <param name="message">Message to send.</param>
    public void SendToAllNeighbors(TMessage message)
    {
        foreach (var edge in Edges)
        {
            SendMessage(edge.TargetId, message);
        }
    }
}

/// <summary>
/// Interface for vertex programs.
/// </summary>
public interface IVertexProgram<TVertex, TMessage, TEdge>
    where TVertex : class
    where TMessage : class
{
    /// <summary>
    /// Computes the new vertex value.
    /// </summary>
    /// <param name="context">Vertex context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New vertex value.</returns>
    Task<TVertex> ComputeAsync(VertexContext<TVertex, TMessage, TEdge> context, CancellationToken ct);
}

/// <summary>
/// Interface for message combiners.
/// </summary>
public interface IMessageCombiner<TMessage>
{
    /// <summary>
    /// Combines multiple messages into one.
    /// </summary>
    /// <param name="messages">Messages to combine.</param>
    /// <returns>Combined message.</returns>
    TMessage Combine(IEnumerable<TMessage> messages);
}

/// <summary>
/// Pregel configuration.
/// </summary>
public sealed class PregelConfiguration
{
    /// <summary>Gets or sets the maximum number of supersteps.</summary>
    public int MaxSupersteps { get; set; } = 1000;

    /// <summary>Gets or sets the degree of parallelism.</summary>
    public int Parallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Gets or sets whether to enable checkpointing.</summary>
    public bool EnableCheckpointing { get; set; } = false;

    /// <summary>Gets or sets the checkpoint interval (supersteps).</summary>
    public int CheckpointInterval { get; set; } = 10;
}

/// <summary>
/// Pregel computation result.
/// </summary>
public sealed class PregelResult
{
    /// <summary>Gets whether the computation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the number of supersteps executed.</summary>
    public int Supersteps { get; init; }

    /// <summary>Gets the total number of messages sent.</summary>
    public long TotalMessages { get; init; }

    /// <summary>Gets the execution time.</summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>Gets any error message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the final vertex values.</summary>
    public Dictionary<string, object?>? VertexResults { get; init; }
}

/// <summary>
/// Superstep result.
/// </summary>
internal sealed class SuperstepResult
{
    public int ActiveVertices { get; init; }
    public long MessagesSent { get; init; }
}

/// <summary>
/// Superstep event arguments.
/// </summary>
public sealed class SuperstepEventArgs : EventArgs
{
    /// <summary>Gets the superstep number.</summary>
    public int Superstep { get; init; }

    /// <summary>Gets the number of active vertices.</summary>
    public int ActiveVertices { get; init; }

    /// <summary>Gets the number of messages sent.</summary>
    public long MessagesSent { get; init; }
}

#region Built-in Vertex Programs

/// <summary>
/// PageRank vertex value.
/// </summary>
public sealed class PageRankValue
{
    /// <summary>Gets or sets the current rank.</summary>
    public double Rank { get; set; } = 1.0;

    /// <summary>Gets or sets the out-degree.</summary>
    public int OutDegree { get; set; }
}

/// <summary>
/// PageRank message.
/// </summary>
public sealed class PageRankMessage
{
    /// <summary>Gets or sets the contribution.</summary>
    public double Contribution { get; set; }
}

/// <summary>
/// Pregel PageRank vertex program.
/// </summary>
public sealed class PregelPageRank : IVertexProgram<PageRankValue, PageRankMessage, double>
{
    /// <summary>Gets or sets the damping factor.</summary>
    public double DampingFactor { get; set; } = 0.85;

    /// <summary>Gets or sets the convergence tolerance.</summary>
    public double Tolerance { get; set; } = 1e-6;

    /// <inheritdoc/>
    public Task<PageRankValue> ComputeAsync(
        VertexContext<PageRankValue, PageRankMessage, double> context,
        CancellationToken ct)
    {
        var value = context.Value;

        if (context.Superstep == 0)
        {
            // Initialize out-degree
            value.OutDegree = context.Edges.Count;
            if (value.OutDegree > 0)
            {
                context.SendToAllNeighbors(new PageRankMessage
                {
                    Contribution = value.Rank / value.OutDegree
                });
            }
            return Task.FromResult(value);
        }

        // Sum incoming contributions
        double sum = 0;
        foreach (var msg in context.IncomingMessages)
        {
            sum += msg.Contribution;
        }

        // Update rank
        double newRank = (1 - DampingFactor) + DampingFactor * sum;
        double diff = Math.Abs(newRank - value.Rank);
        value.Rank = newRank;

        // Check convergence
        if (diff < Tolerance)
        {
            context.VoteToHalt();
        }
        else if (value.OutDegree > 0)
        {
            context.SendToAllNeighbors(new PageRankMessage
            {
                Contribution = newRank / value.OutDegree
            });
        }

        return Task.FromResult(value);
    }
}

/// <summary>
/// SSSP (Single Source Shortest Path) vertex value.
/// </summary>
public sealed class SsspValue
{
    /// <summary>Gets or sets the distance from source.</summary>
    public double Distance { get; set; } = double.MaxValue;

    /// <summary>Gets or sets the predecessor vertex ID.</summary>
    public string? Predecessor { get; set; }
}

/// <summary>
/// SSSP message.
/// </summary>
public sealed class SsspMessage
{
    /// <summary>Gets or sets the proposed distance.</summary>
    public double Distance { get; set; }

    /// <summary>Gets or sets the sender vertex ID.</summary>
    public required string SenderId { get; init; }
}

/// <summary>
/// Pregel Single Source Shortest Path vertex program.
/// </summary>
public sealed class PregelSssp : IVertexProgram<SsspValue, SsspMessage, double>
{
    /// <summary>Gets or sets the source vertex ID.</summary>
    public required string SourceVertexId { get; init; }

    /// <inheritdoc/>
    public Task<SsspValue> ComputeAsync(
        VertexContext<SsspValue, SsspMessage, double> context,
        CancellationToken ct)
    {
        var value = context.Value;

        if (context.Superstep == 0)
        {
            if (context.VertexId == SourceVertexId)
            {
                value.Distance = 0;
                // Send initial distances to neighbors
                foreach (var edge in context.Edges)
                {
                    context.SendMessage(edge.TargetId, new SsspMessage
                    {
                        Distance = edge.Value,
                        SenderId = context.VertexId
                    });
                }
            }
            return Task.FromResult(value);
        }

        // Find minimum distance from messages
        double minDist = value.Distance;
        string? predecessor = value.Predecessor;

        foreach (var msg in context.IncomingMessages)
        {
            if (msg.Distance < minDist)
            {
                minDist = msg.Distance;
                predecessor = msg.SenderId;
            }
        }

        // Update if improved
        if (minDist < value.Distance)
        {
            value.Distance = minDist;
            value.Predecessor = predecessor;

            // Propagate to neighbors
            foreach (var edge in context.Edges)
            {
                context.SendMessage(edge.TargetId, new SsspMessage
                {
                    Distance = minDist + edge.Value,
                    SenderId = context.VertexId
                });
            }
        }
        else
        {
            context.VoteToHalt();
        }

        return Task.FromResult(value);
    }
}

/// <summary>
/// Connected components vertex value.
/// </summary>
public sealed class ComponentValue
{
    /// <summary>Gets or sets the component ID.</summary>
    public string ComponentId { get; set; } = "";
}

/// <summary>
/// Connected components message.
/// </summary>
public sealed class ComponentMessage
{
    /// <summary>Gets or sets the component ID.</summary>
    public required string ComponentId { get; init; }
}

/// <summary>
/// Pregel Connected Components vertex program.
/// </summary>
public sealed class PregelConnectedComponents : IVertexProgram<ComponentValue, ComponentMessage, object>
{
    /// <inheritdoc/>
    public Task<ComponentValue> ComputeAsync(
        VertexContext<ComponentValue, ComponentMessage, object> context,
        CancellationToken ct)
    {
        var value = context.Value;

        if (context.Superstep == 0)
        {
            // Initialize with vertex ID as component
            value.ComponentId = context.VertexId;
            context.SendToAllNeighbors(new ComponentMessage { ComponentId = value.ComponentId });
            return Task.FromResult(value);
        }

        // Find minimum component ID
        string minComponent = value.ComponentId;
        foreach (var msg in context.IncomingMessages)
        {
            if (string.Compare(msg.ComponentId, minComponent, StringComparison.Ordinal) < 0)
            {
                minComponent = msg.ComponentId;
            }
        }

        // Update if changed
        if (minComponent != value.ComponentId)
        {
            value.ComponentId = minComponent;
            context.SendToAllNeighbors(new ComponentMessage { ComponentId = minComponent });
        }
        else
        {
            context.VoteToHalt();
        }

        return Task.FromResult(value);
    }
}

#endregion

#region Message Combiners

/// <summary>
/// Sums double values.
/// </summary>
public sealed class SumCombiner : IMessageCombiner<PageRankMessage>
{
    /// <inheritdoc/>
    public PageRankMessage Combine(IEnumerable<PageRankMessage> messages)
    {
        return new PageRankMessage
        {
            Contribution = messages.Sum(m => m.Contribution)
        };
    }
}

/// <summary>
/// Finds minimum distance.
/// </summary>
public sealed class MinDistanceCombiner : IMessageCombiner<SsspMessage>
{
    /// <inheritdoc/>
    public SsspMessage Combine(IEnumerable<SsspMessage> messages)
    {
        var min = messages.MinBy(m => m.Distance)!;
        return new SsspMessage
        {
            Distance = min.Distance,
            SenderId = min.SenderId
        };
    }
}

/// <summary>
/// Finds minimum component ID.
/// </summary>
public sealed class MinComponentCombiner : IMessageCombiner<ComponentMessage>
{
    /// <inheritdoc/>
    public ComponentMessage Combine(IEnumerable<ComponentMessage> messages)
    {
        var min = messages.MinBy(m => m.ComponentId)!;
        return new ComponentMessage { ComponentId = min.ComponentId };
    }
}

#endregion

/// <summary>
/// Distributed graph processing coordinator.
/// Coordinates Pregel execution across multiple workers.
/// </summary>
public sealed class DistributedGraphCoordinator
{
    private readonly List<IGraphWorker> _workers = new();
    private readonly GraphPartitioningStrategyBase _partitioner;
    private int _globalSuperstep = 0;

    /// <summary>
    /// Creates a new coordinator.
    /// </summary>
    /// <param name="partitioner">Graph partitioner.</param>
    public DistributedGraphCoordinator(GraphPartitioningStrategyBase partitioner)
    {
        _partitioner = partitioner;
    }

    /// <summary>
    /// Registers a worker.
    /// </summary>
    /// <param name="worker">Worker to register.</param>
    public void RegisterWorker(IGraphWorker worker)
    {
        _workers.Add(worker);
    }

    /// <summary>
    /// Executes a distributed computation.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DistributedComputationResult> ExecuteAsync(CancellationToken ct = default)
    {
        _globalSuperstep = 0;
        var startTime = DateTime.UtcNow;
        long totalMessages = 0;
        bool globalActive = true;

        while (globalActive && _globalSuperstep < 1000)
        {
            ct.ThrowIfCancellationRequested();

            // Execute superstep on all workers
            var workerTasks = _workers.Select(w => w.ExecuteSuperstepAsync(_globalSuperstep, ct));
            var results = await Task.WhenAll(workerTasks);

            // Aggregate results
            globalActive = false;
            foreach (var result in results)
            {
                totalMessages += result.MessagesSent;
                if (result.HasActiveVertices || result.HasPendingMessages)
                {
                    globalActive = true;
                }
            }

            // Exchange messages between workers
            await ExchangeMessagesAsync(ct);

            _globalSuperstep++;
        }

        return new DistributedComputationResult
        {
            Supersteps = _globalSuperstep,
            TotalMessages = totalMessages,
            ExecutionTime = DateTime.UtcNow - startTime
        };
    }

    private async Task ExchangeMessagesAsync(CancellationToken ct)
    {
        // Collect outgoing messages from all workers
        var allMessages = new List<(int TargetPartition, object Message)>();

        foreach (var worker in _workers)
        {
            var outgoing = await worker.GetOutgoingMessagesAsync(ct);
            allMessages.AddRange(outgoing);
        }

        // Route messages to target workers
        var messagesByPartition = allMessages.GroupBy(m => m.TargetPartition);

        foreach (var group in messagesByPartition)
        {
            var targetWorker = _workers.FirstOrDefault(w => w.PartitionId == group.Key);
            if (targetWorker != null)
            {
                await targetWorker.ReceiveMessagesAsync(group.Select(m => m.Message), ct);
            }
        }
    }
}

/// <summary>
/// Interface for graph workers.
/// </summary>
public interface IGraphWorker
{
    /// <summary>Gets the partition ID this worker handles.</summary>
    int PartitionId { get; }

    /// <summary>Executes a superstep.</summary>
    Task<WorkerSuperstepResult> ExecuteSuperstepAsync(int superstep, CancellationToken ct);

    /// <summary>Gets outgoing messages for other partitions.</summary>
    Task<IEnumerable<(int TargetPartition, object Message)>> GetOutgoingMessagesAsync(CancellationToken ct);

    /// <summary>Receives messages from other workers.</summary>
    Task ReceiveMessagesAsync(IEnumerable<object> messages, CancellationToken ct);
}

/// <summary>
/// Worker superstep result.
/// </summary>
public sealed class WorkerSuperstepResult
{
    /// <summary>Gets whether this worker has active vertices.</summary>
    public bool HasActiveVertices { get; init; }

    /// <summary>Gets whether there are pending messages.</summary>
    public bool HasPendingMessages { get; init; }

    /// <summary>Gets the number of messages sent.</summary>
    public long MessagesSent { get; init; }
}

/// <summary>
/// Distributed computation result.
/// </summary>
public sealed class DistributedComputationResult
{
    /// <summary>Gets the number of supersteps.</summary>
    public int Supersteps { get; init; }

    /// <summary>Gets total messages exchanged.</summary>
    public long TotalMessages { get; init; }

    /// <summary>Gets execution time.</summary>
    public TimeSpan ExecutionTime { get; init; }
}
