using DataWarehouse.SDK.Contracts;
using System.Collections.Immutable;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// Cloud VM specification for provisioning.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Cloud VM specification (ENV-04)")]
public sealed record VmSpec
{
    /// <summary>Gets the VM image ID (AMI ID, Azure image URN, GCP image family).</summary>
    public string? ImageId { get; init; }

    /// <summary>Gets the instance type (t3.medium, Standard_D2s_v3, n2-standard-2).</summary>
    public string InstanceType { get; init; } = "general-purpose";

    /// <summary>Gets the root volume size in GB.</summary>
    public int StorageGb { get; init; } = 100;

    /// <summary>Gets the deployment region/location/zone.</summary>
    public string? Region { get; init; }

    /// <summary>Gets resource tags for identification and cost tracking.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Gets the security group or firewall configuration.</summary>
    public string? SecurityGroup { get; init; }

    /// <summary>Gets the VPC subnet ID.</summary>
    public string? SubnetId { get; init; }
}
