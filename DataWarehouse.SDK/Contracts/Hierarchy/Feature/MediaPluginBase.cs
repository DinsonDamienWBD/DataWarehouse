using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for media processing plugins (transcoding, image processing, audio).
/// </summary>
public abstract class MediaPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Media";

    /// <summary>Media type handled (e.g., "Video", "Image", "Audio").</summary>
    public abstract string MediaType { get; }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["MediaType"] = MediaType;
        return metadata;
    }
}
