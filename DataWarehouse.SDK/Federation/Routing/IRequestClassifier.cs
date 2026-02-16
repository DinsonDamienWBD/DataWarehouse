using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Federation.Routing;

/// <summary>
/// Defines a strategy for classifying storage requests as Object or FilePath language.
/// </summary>
/// <remarks>
/// <para>
/// Classification determines which routing pipeline handles a request. Object language
/// routes to UUID-based object storage, metadata queries, and object operations. FilePath
/// language routes to path-based filesystem operations, directory listings, and VDE integration.
/// </para>
/// <para>
/// Implementations must provide O(1) classification via pattern matching on request properties.
/// Database lookups and network calls are prohibited in classification logic to maintain
/// low-latency routing.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Pluggable request classification strategy")]
public interface IRequestClassifier
{
    /// <summary>
    /// Classifies a storage request as Object or FilePath language.
    /// </summary>
    /// <param name="request">The storage request to classify.</param>
    /// <returns>The classified request language (Object or FilePath).</returns>
    /// <remarks>
    /// Classification should be O(1) via pattern matching on:
    /// <list type="bullet">
    /// <item><description>Address kind (ObjectKey vs FilePath)</description></item>
    /// <item><description>UUID patterns in object keys</description></item>
    /// <item><description>Operation type (Query/GetMetadata = Object, List = FilePath)</description></item>
    /// <item><description>Metadata signals (keys like "object-id", "path")</description></item>
    /// <item><description>Optional language hints (when PreferHints = true)</description></item>
    /// </list>
    /// </remarks>
    RequestLanguage Classify(StorageRequest request);
}
