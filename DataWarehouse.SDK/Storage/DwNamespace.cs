using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Storage;

/// <summary>
/// dw:// bucket-based storage address representing objects in named buckets.
/// Format: dw://mybucket/path/to/object
/// </summary>
/// <param name="Bucket">The bucket name (3-63 chars, lowercase alphanumeric + hyphens, S3-compatible).</param>
/// <param name="ObjectPath">The object path within the bucket (max 1024 chars, no path traversal).</param>
[SdkCompatibility("5.0.0", Notes = "Phase 63: dw:// universal namespace")]
public sealed record DwBucketAddress(string Bucket, string ObjectPath) : StorageAddress
{
    /// <inheritdoc />
    public override StorageAddressKind Kind => StorageAddressKind.DwBucket;

    /// <inheritdoc />
    public override string ToKey() => $"dw://{Bucket}/{ObjectPath}";

    /// <inheritdoc />
    public override Uri ToUri() => new Uri($"dw://{Bucket}/{ObjectPath}");
}

/// <summary>
/// dw:// node-based storage address representing objects on specific cluster nodes.
/// Format: dw://node@hostname/path
/// </summary>
/// <param name="NodeId">The hostname or node identifier.</param>
/// <param name="ObjectPath">The object path on the node (max 1024 chars, no path traversal).</param>
[SdkCompatibility("5.0.0", Notes = "Phase 63: dw:// universal namespace")]
public sealed record DwNodeAddress(string NodeId, string ObjectPath) : StorageAddress
{
    /// <inheritdoc />
    public override StorageAddressKind Kind => StorageAddressKind.DwNode;

    /// <inheritdoc />
    public override string ToKey() => $"dw://node@{NodeId}/{ObjectPath}";

    /// <inheritdoc />
    public override Uri ToUri() => new Uri($"dw://node@{NodeId}/{ObjectPath}");
}

/// <summary>
/// dw:// cluster-based storage address representing cluster-wide distributed keys.
/// Format: dw://cluster:name/key
/// </summary>
/// <param name="ClusterName">The cluster name (1-255 chars, alphanumeric + hyphens + dots).</param>
/// <param name="Key">The distributed key within the cluster.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 63: dw:// universal namespace")]
public sealed record DwClusterAddress(string ClusterName, string Key) : StorageAddress
{
    /// <inheritdoc />
    public override StorageAddressKind Kind => StorageAddressKind.DwCluster;

    /// <inheritdoc />
    public override string ToKey() => $"dw://cluster:{ClusterName}/{Key}";

    /// <inheritdoc />
    public override Uri ToUri() => new Uri($"dw://cluster:{ClusterName}/{Key}");
}
