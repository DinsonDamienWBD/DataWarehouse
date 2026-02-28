using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Immutable version vector for tracking causal ordering of tag updates across nodes.
/// Each node maintains a monotonically increasing counter. Comparing two version vectors
/// reveals whether one causally dominates the other or if they are concurrent (conflict).
/// </summary>
/// <remarks>
/// Version vectors are the foundation of CRDT conflict detection. Given vectors A and B:
/// <list type="bullet">
/// <item><description>A dominates B if A[n] >= B[n] for ALL nodes n, and A != B</description></item>
/// <item><description>A and B are concurrent if neither dominates the other</description></item>
/// <item><description>Merge(A, B) takes max(A[n], B[n]) for each node</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: CRDT tag versioning")]
public sealed record TagVersionVector
{
    private readonly IReadOnlyDictionary<string, long> _nodeVersions;

    /// <summary>
    /// Gets the per-node version counters.
    /// </summary>
    public IReadOnlyDictionary<string, long> NodeVersions => _nodeVersions;

    /// <summary>
    /// Creates a new empty version vector.
    /// </summary>
    public TagVersionVector()
    {
        _nodeVersions = new Dictionary<string, long>();
    }

    /// <summary>
    /// Creates a version vector from the given node version map.
    /// </summary>
    /// <param name="nodeVersions">The per-node version counters.</param>
    public TagVersionVector(IReadOnlyDictionary<string, long> nodeVersions)
    {
        _nodeVersions = nodeVersions ?? throw new ArgumentNullException(nameof(nodeVersions));
    }

    /// <summary>
    /// Gets the version counter for a specific node. Returns 0 if the node is not present.
    /// </summary>
    /// <param name="nodeId">The node identifier.</param>
    /// <returns>The monotonic version counter for the node, or 0 if unknown.</returns>
    public long GetVersion(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return _nodeVersions.TryGetValue(nodeId, out var version) ? version : 0;
    }

    /// <summary>
    /// Returns a new version vector with the specified node's counter incremented by 1.
    /// </summary>
    /// <param name="nodeId">The node whose counter to increment.</param>
    /// <returns>A new <see cref="TagVersionVector"/> with the incremented counter.</returns>
    public TagVersionVector Increment(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        var dict = new Dictionary<string, long>(_nodeVersions);
        dict[nodeId] = GetVersion(nodeId) + 1;
        return new TagVersionVector(dict);
    }

    /// <summary>
    /// Determines whether this version vector causally dominates another.
    /// A dominates B if A[n] >= B[n] for ALL nodes n in both vectors, and at least one A[n] > B[n].
    /// </summary>
    /// <param name="other">The version vector to compare against.</param>
    /// <returns><c>true</c> if this vector dominates <paramref name="other"/>.</returns>
    public bool Dominates(TagVersionVector other)
    {
        ArgumentNullException.ThrowIfNull(other);

        bool strictlyGreater = false;
        var allNodes = new HashSet<string>(_nodeVersions.Keys);
        foreach (var key in other._nodeVersions.Keys)
            allNodes.Add(key);

        foreach (var node in allNodes)
        {
            var thisVersion = GetVersion(node);
            var otherVersion = other.GetVersion(node);

            if (thisVersion < otherVersion)
                return false;
            if (thisVersion > otherVersion)
                strictlyGreater = true;
        }

        return strictlyGreater;
    }

    /// <summary>
    /// Determines whether this version vector is concurrent with another.
    /// Two vectors are concurrent if neither dominates the other and they are not equal.
    /// </summary>
    /// <param name="other">The version vector to compare against.</param>
    /// <returns><c>true</c> if the vectors are concurrent (conflict).</returns>
    public bool Concurrent(TagVersionVector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return !Dominates(other) && !other.Dominates(this) && !Equals(other);
    }

    /// <summary>
    /// Merges two version vectors by taking the maximum counter for each node.
    /// The result causally dominates or equals both inputs.
    /// </summary>
    /// <param name="a">First version vector.</param>
    /// <param name="b">Second version vector.</param>
    /// <returns>A new merged version vector.</returns>
    public static TagVersionVector Merge(TagVersionVector a, TagVersionVector b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var merged = new Dictionary<string, long>(a._nodeVersions);
        foreach (var (node, version) in b._nodeVersions)
        {
            if (merged.TryGetValue(node, out var existing))
                merged[node] = Math.Max(existing, version);
            else
                merged[node] = version;
        }

        return new TagVersionVector(merged);
    }

    /// <summary>
    /// Serializes this version vector to a byte array using JSON encoding.
    /// </summary>
    /// <returns>The serialized byte array.</returns>
    public byte[] Serialize()
    {
        var dict = new Dictionary<string, long>(_nodeVersions);
        return JsonSerializer.SerializeToUtf8Bytes(dict);
    }

    /// <summary>
    /// Deserializes a version vector from a byte array.
    /// </summary>
    /// <param name="data">The byte array to deserialize.</param>
    /// <returns>The deserialized <see cref="TagVersionVector"/>.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> is null.</exception>
    public static TagVersionVector Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dict = JsonSerializer.Deserialize<Dictionary<string, long>>(data)
                   ?? new Dictionary<string, long>();
        // Validate non-negative counters to prevent corrupt/malicious data from breaking Dominates comparison
        foreach (var (node, counter) in dict)
        {
            if (counter < 0)
                throw new FormatException($"Version vector counter for node '{node}' is negative ({counter}). Counters must be non-negative.");
        }
        return new TagVersionVector(dict);
    }

    /// <summary>
    /// Value-based equality: two version vectors are equal if they have the same
    /// set of nodes with the same counters (nodes with counter 0 are considered absent).
    /// </summary>
    public bool Equals(TagVersionVector? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        var allNodes = new HashSet<string>(_nodeVersions.Keys);
        foreach (var key in other._nodeVersions.Keys)
            allNodes.Add(key);

        foreach (var node in allNodes)
        {
            if (GetVersion(node) != other.GetVersion(node))
                return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var kvp in _nodeVersions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(kvp.Key, StringComparer.Ordinal);
            hash.Add(kvp.Value);
        }
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var entries = _nodeVersions.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}:{p.Value}");
        return $"VV({string.Join(", ", entries)})";
    }
}
