using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Storage;
using System;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Federation.Routing;

/// <summary>
/// Pattern-based implementation of <see cref="IRequestClassifier"/> using heuristics on request properties.
/// </summary>
/// <remarks>
/// <para>
/// PatternBasedClassifier uses O(1) pattern matching to classify requests as Object or FilePath language.
/// Classification is based on:
/// <list type="number">
/// <item><description>Optional language hints (when PreferHints = true)</description></item>
/// <item><description>StorageAddress kind (ObjectKey vs FilePath)</description></item>
/// <item><description>UUID pattern matching in object keys</description></item>
/// <item><description>Operation type signals (GetMetadata/SetMetadata/Query = Object, List = FilePath)</description></item>
/// <item><description>Metadata key signals ("object-id"/"uuid" = Object, "path"/"directory" = FilePath)</description></item>
/// <item><description>Configurable default language as fallback</description></item>
/// </list>
/// </para>
/// <para>
/// This classifier is suitable for most scenarios. Custom classifiers can implement
/// <see cref="IRequestClassifier"/> for domain-specific classification logic.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Pattern-based request classifier")]
public sealed class PatternBasedClassifier : IRequestClassifier
{
    private static readonly Regex UuidPattern = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly PatternClassifierConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatternBasedClassifier"/> class.
    /// </summary>
    /// <param name="config">
    /// Optional configuration. If null, uses default configuration (Object language default, prefer hints).
    /// </param>
    public PatternBasedClassifier(PatternClassifierConfiguration? config = null)
    {
        _config = config ?? new PatternClassifierConfiguration();
    }

    /// <inheritdoc />
    public RequestLanguage Classify(StorageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. If language hint is set and PreferHints is enabled, use the hint
        if (_config.PreferHints && request.LanguageHint.HasValue)
        {
            return request.LanguageHint.Value;
        }

        // 2. Classify based on StorageAddress kind
        var addressClassification = ClassifyByAddress(request.Address);
        if (addressClassification.HasValue)
        {
            return addressClassification.Value;
        }

        // 3. Classify based on operation type
        var operationClassification = ClassifyByOperation(request.Operation);
        if (operationClassification.HasValue)
        {
            return operationClassification.Value;
        }

        // 4. Classify based on metadata signals
        if (request.Metadata != null)
        {
            var metadataClassification = ClassifyByMetadata(request.Metadata);
            if (metadataClassification.HasValue)
            {
                return metadataClassification.Value;
            }
        }

        // 5. Fallback to default language
        return _config.DefaultLanguage;
    }

    /// <summary>
    /// Classifies request based on the StorageAddress kind and content.
    /// </summary>
    private static RequestLanguage? ClassifyByAddress(StorageAddress address)
    {
        // Explicit kind-based classification
        if (address.Kind == StorageAddressKind.ObjectKey)
        {
            return RequestLanguage.Object;
        }

        if (address.Kind == StorageAddressKind.FilePath)
        {
            return RequestLanguage.FilePath;
        }

        // For ObjectKeyAddress, check if the key matches UUID pattern
        if (address is ObjectKeyAddress objectKey)
        {
            if (UuidPattern.IsMatch(objectKey.Key))
            {
                return RequestLanguage.Object;
            }
        }

        // No classification based on address
        return null;
    }

    /// <summary>
    /// Classifies request based on the operation type.
    /// </summary>
    private static RequestLanguage? ClassifyByOperation(StorageOperation operation)
    {
        return operation switch
        {
            // Metadata operations and queries are Object language
            StorageOperation.GetMetadata => RequestLanguage.Object,
            StorageOperation.SetMetadata => RequestLanguage.Object,
            StorageOperation.Query => RequestLanguage.Object,

            // List operation is FilePath language (directory listings)
            StorageOperation.List => RequestLanguage.FilePath,

            // Read, Write, Delete are ambiguous - need more context
            _ => null
        };
    }

    /// <summary>
    /// Classifies request based on metadata key signals.
    /// </summary>
    private static RequestLanguage? ClassifyByMetadata(System.Collections.Generic.IReadOnlyDictionary<string, string> metadata)
    {
        // Object language signals
        if (metadata.ContainsKey("object-id") || metadata.ContainsKey("uuid"))
        {
            return RequestLanguage.Object;
        }

        // FilePath language signals
        if (metadata.ContainsKey("path") || metadata.ContainsKey("directory"))
        {
            return RequestLanguage.FilePath;
        }

        // No classification based on metadata
        return null;
    }
}

/// <summary>
/// Configuration for <see cref="PatternBasedClassifier"/>.
/// </summary>
/// <remarks>
/// Controls the default language fallback and whether to prefer explicit language hints
/// over automatic classification.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Pattern classifier configuration")]
public sealed record PatternClassifierConfiguration
{
    /// <summary>
    /// Gets the default language to use when classification is ambiguous.
    /// Default: <see cref="RequestLanguage.Object"/> (assumes object storage by default).
    /// </summary>
    public RequestLanguage DefaultLanguage { get; init; } = RequestLanguage.Object;

    /// <summary>
    /// Gets whether to prefer explicit language hints over automatic classification.
    /// When true, if <see cref="StorageRequest.LanguageHint"/> is set, it is used immediately
    /// without further classification logic.
    /// Default: true (respect explicit hints).
    /// </summary>
    public bool PreferHints { get; init; } = true;
}
