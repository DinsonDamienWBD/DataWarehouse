// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Threading;

using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Interface for WORM (Write-Once-Read-Many) storage providers.
/// Provides immutable storage with retention policies, legal holds, and hardware/software enforcement.
/// </summary>
public interface IWormStorageProvider : IPlugin
{
    /// <summary>
    /// Gets the WORM enforcement mode for this provider.
    /// Indicates whether immutability is enforced in software, hardware, or hybrid mode.
    /// </summary>
    WormEnforcementMode EnforcementMode { get; }

    /// <summary>
    /// Writes data to WORM storage with the specified retention policy.
    /// Once written, the data cannot be modified or deleted until retention expires.
    /// </summary>
    /// <param name="objectId">Unique identifier for the object being stored.</param>
    /// <param name="data">Data stream to write to WORM storage.</param>
    /// <param name="retention">Retention policy specifying how long data must be preserved.</param>
    /// <param name="context">Write context with author and comment for audit trail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing storage key, content hash, and retention expiry.</returns>
    Task<WormWriteResult> WriteAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        WriteContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Reads data from WORM storage.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream containing the immutable data.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the object does not exist.</exception>
    Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current status of a WORM object including retention and legal holds.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Status information including existence, retention expiry, and active legal holds.</returns>
    Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Extends the retention period for a WORM object.
    /// Retention can only be extended, never shortened (compliance requirement).
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="newExpiry">New expiry date (must be later than current expiry).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when attempting to shorten retention.</exception>
    Task ExtendRetentionAsync(Guid objectId, DateTimeOffset newExpiry, CancellationToken ct = default);

    /// <summary>
    /// Places a legal hold on a WORM object.
    /// Objects under legal hold cannot be deleted even after retention expires.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="holdId">Unique identifier for this legal hold.</param>
    /// <param name="reason">Reason for the legal hold (e.g., litigation, investigation).</param>
    /// <param name="ct">Cancellation token.</param>
    Task PlaceLegalHoldAsync(Guid objectId, string holdId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Removes a legal hold from a WORM object.
    /// Once all legal holds are removed and retention has expired, the object can be deleted.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="holdId">Unique identifier of the legal hold to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the hold ID does not exist.</exception>
    Task RemoveLegalHoldAsync(Guid objectId, string holdId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a WORM object exists in storage.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object exists, false otherwise.</returns>
    Task<bool> ExistsAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets all active legal holds for a WORM object.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of active legal holds, empty if none.</returns>
    Task<IReadOnlyList<LegalHold>> GetLegalHoldsAsync(Guid objectId, CancellationToken ct = default);
}

/// <summary>
/// Result of a WORM write operation.
/// Contains proof of write including storage location, content hash, and retention details.
/// </summary>
public class WormWriteResult
{
    /// <summary>
    /// Storage key or path where the WORM data is stored.
    /// Provider-specific format (e.g., S3 key, filesystem path, blob name).
    /// </summary>
    public required string StorageKey { get; init; }

    /// <summary>
    /// Content hash of the written data for integrity verification.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Hash algorithm used for content verification.
    /// </summary>
    public required HashAlgorithmType HashAlgorithm { get; init; }

    /// <summary>
    /// Size of the written data in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// UTC timestamp when the write was performed.
    /// </summary>
    public required DateTimeOffset WrittenAt { get; init; }

    /// <summary>
    /// UTC timestamp when the retention period expires.
    /// After this time (and with no legal holds), the object may be deleted.
    /// </summary>
    public required DateTimeOffset RetentionExpiry { get; init; }

    /// <summary>
    /// Whether hardware-level immutability is enabled (for hardware/hybrid modes).
    /// </summary>
    public bool HardwareImmutabilityEnabled { get; init; }

    /// <summary>
    /// Provider-specific metadata about the write operation.
    /// May include version IDs, ARNs, or other provider-specific identifiers.
    /// </summary>
    public Dictionary<string, string>? ProviderMetadata { get; init; }
}

/// <summary>
/// Status information for a WORM object.
/// Provides complete view of retention, legal holds, and immutability state.
/// </summary>
public class WormObjectStatus
{
    /// <summary>
    /// Whether the object exists in WORM storage.
    /// </summary>
    public required bool Exists { get; init; }

    /// <summary>
    /// Object identifier.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// UTC timestamp when retention expires.
    /// Null if object doesn't exist or retention is indefinite.
    /// </summary>
    public DateTimeOffset? RetentionExpiry { get; init; }

    /// <summary>
    /// Whether the retention period has expired.
    /// True if current time is past RetentionExpiry.
    /// </summary>
    public bool IsRetentionExpired => RetentionExpiry.HasValue && DateTimeOffset.UtcNow > RetentionExpiry.Value;

    /// <summary>
    /// Active legal holds on this object.
    /// If any holds exist, object cannot be deleted even if retention has expired.
    /// </summary>
    public IReadOnlyList<LegalHold> LegalHolds { get; init; } = Array.Empty<LegalHold>();

    /// <summary>
    /// Whether any legal holds are active.
    /// </summary>
    public bool HasLegalHolds => LegalHolds.Count > 0;

    /// <summary>
    /// Whether the object can be deleted.
    /// True only if retention has expired AND no legal holds are active.
    /// </summary>
    public bool CanDelete => Exists && IsRetentionExpired && !HasLegalHolds;

    /// <summary>
    /// Size of the object in bytes.
    /// Null if object doesn't exist.
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// UTC timestamp when the object was written.
    /// Null if object doesn't exist.
    /// </summary>
    public DateTimeOffset? WrittenAt { get; init; }

    /// <summary>
    /// Content hash of the object for integrity verification.
    /// Null if object doesn't exist.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// Whether hardware-level immutability is enabled.
    /// </summary>
    public bool HardwareImmutabilityEnabled { get; init; }

    /// <summary>
    /// Storage key or path where the object is stored.
    /// Null if object doesn't exist.
    /// </summary>
    public string? StorageKey { get; init; }
}

/// <summary>
/// Represents a legal hold on a WORM object.
/// Legal holds prevent deletion for litigation, investigation, or compliance purposes.
/// </summary>
public class LegalHold
{
    /// <summary>
    /// Unique identifier for this legal hold.
    /// </summary>
    public required string HoldId { get; init; }

    /// <summary>
    /// Reason for the legal hold.
    /// Should explain the legal or compliance requirement (e.g., "Smith v. Acme litigation").
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// UTC timestamp when the hold was placed.
    /// </summary>
    public required DateTimeOffset PlacedAt { get; init; }

    /// <summary>
    /// Principal who placed the hold (username, service account, etc.).
    /// </summary>
    public required string PlacedBy { get; init; }

    /// <summary>
    /// Optional case number or reference for tracking.
    /// </summary>
    public string? CaseNumber { get; init; }

    /// <summary>
    /// Optional expiry date for the hold.
    /// If null, the hold is indefinite until manually removed.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Additional metadata about the hold.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Abstract base class for WORM storage provider plugins.
/// Provides common retention validation and legal hold management logic.
/// Plugin implementers should extend this class and implement the protected abstract methods.
/// </summary>
public abstract class WormStorageProviderPluginBase : IntegrityPluginBase, IWormStorageProvider, IIntelligenceAware
{
    /// <inheritdoc/>
    /// <remarks>
    /// Subclasses MUST override to perform actual integrity verification against WORM storage.
    /// The base implementation throws to prevent silent integrity bypass.
    /// </remarks>
    public override Task<Dictionary<string, object>> VerifyAsync(string key, CancellationToken ct = default)
        => throw new NotImplementedException(
            $"WORM provider '{GetType().Name}' must override VerifyAsync to perform actual integrity verification.");

    /// <inheritdoc/>
    public override async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        return await System.Security.Cryptography.SHA256.HashDataAsync(data, ct).ConfigureAwait(false);
    }

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.worm",
            DisplayName = $"{Name} - WORM Storage",
            Description = $"Write-Once-Read-Many storage ({EnforcementMode} enforcement)",
            Category = CapabilityCategory.TamperProof,
            SubCategory = "WORM",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "worm", "immutable", "tamper-proof", "compliance" },
            SemanticDescription = "Use for immutable WORM storage"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.worm.capability",
                Topic = "tamperproof.worm",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"WORM storage with {EnforcementMode} enforcement",
                Payload = new Dictionary<string, object>
                {
                    ["enforcementMode"] = EnforcementMode.ToString(),
                    ["supportsLegalHolds"] = true,
                    ["supportsRetentionExtension"] = true
                },
                Tags = new[] { "worm", "immutable", "tamper-proof" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted retention policy recommendation.
    /// </summary>
    protected virtual async Task<RetentionPolicyRecommendation?> RequestRetentionPolicyAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.worm.retention.recommend",
                Payload = { ["objectId"] = objectId.ToString() }
            };
            var response = await MessageBus.SendAsync("intelligence.worm.retention.recommend", request, TimeSpan.FromSeconds(10), ct);
            if (response.Success && response.Payload is RetentionPolicyRecommendation recommendation)
                return recommendation;
            if (response.Success && response.Payload is Dictionary<string, object> dict
                && dict.TryGetValue("recommendation", out var rec) && rec is RetentionPolicyRecommendation recTyped)
                return recTyped;
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { throw; }
        catch { /* Intelligence unavailable — graceful degradation */ }

        return null;
    }

    #endregion

    /// <summary>
    /// Gets the category of this plugin (always StorageProvider for WORM).
    /// </summary>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Gets the WORM enforcement mode for this provider implementation.
    /// </summary>
    public abstract WormEnforcementMode EnforcementMode { get; }

    /// <summary>
    /// Writes data to WORM storage with validation and enforcement.
    /// </summary>
    public async Task<WormWriteResult> WriteAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        WriteContext context,
        CancellationToken ct = default)
    {
        // Validate inputs
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        if (data == null || !data.CanRead)
            throw new ArgumentException("Data stream must be readable.", nameof(data));

        if (retention == null)
            throw new ArgumentNullException(nameof(retention));

        if (retention.RetentionPeriod <= TimeSpan.Zero)
            throw new ArgumentException("Retention period must be positive.", nameof(retention));

        context.ValidateOrThrow();

        // Check if object already exists (WORM = write once)
        if (await ExistsAsync(objectId, ct))
            throw new InvalidOperationException($"Object {objectId} already exists in WORM storage. WORM objects cannot be overwritten.");

        // Delegate to provider-specific implementation
        return await WriteInternalAsync(objectId, data, retention, context, ct);
    }

    /// <summary>
    /// Extends retention with validation to prevent shortening.
    /// </summary>
    public async Task ExtendRetentionAsync(Guid objectId, DateTimeOffset newExpiry, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        // Get current status
        var status = await GetStatusAsync(objectId, ct);

        if (!status.Exists)
            throw new KeyNotFoundException($"Object {objectId} does not exist in WORM storage.");

        // Reject past dates regardless of current retention state
        if (newExpiry <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException(
                $"New expiry {newExpiry:O} is in the past. Retention expiry must be in the future.",
                nameof(newExpiry));
        }

        // Validate that retention is being extended, not shortened
        if (status.RetentionExpiry.HasValue && newExpiry <= status.RetentionExpiry.Value)
        {
            throw new InvalidOperationException(
                $"Cannot shorten retention period. Current expiry: {status.RetentionExpiry.Value:O}, " +
                $"requested expiry: {newExpiry:O}. Retention can only be extended.");
        }

        // If current retention is indefinite (null expiry), setting a finite expiry would shorten it
        if (!status.RetentionExpiry.HasValue)
        {
            throw new InvalidOperationException(
                "Cannot set a finite expiry on an object with indefinite retention. " +
                "Indefinite retention can only remain indefinite.");
        }

        // Delegate to provider-specific implementation
        await ExtendRetentionInternalAsync(objectId, newExpiry, ct);
    }

    /// <summary>
    /// Places a legal hold with validation.
    /// </summary>
    public async Task PlaceLegalHoldAsync(Guid objectId, string holdId, string reason, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        if (string.IsNullOrWhiteSpace(holdId))
            throw new ArgumentException("Hold ID cannot be empty.", nameof(holdId));

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason for legal hold is required.", nameof(reason));

        // Verify object exists
        var status = await GetStatusAsync(objectId, ct);
        if (!status.Exists)
            throw new KeyNotFoundException($"Object {objectId} does not exist in WORM storage.");

        // Check if hold already exists
        if (status.LegalHolds.Any(h => h.HoldId == holdId))
            throw new InvalidOperationException($"Legal hold '{holdId}' already exists on object {objectId}.");

        // Delegate to provider-specific implementation
        await PlaceLegalHoldInternalAsync(objectId, holdId, reason, ct);
    }

    /// <summary>
    /// Removes a legal hold with validation.
    /// </summary>
    public async Task RemoveLegalHoldAsync(Guid objectId, string holdId, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        if (string.IsNullOrWhiteSpace(holdId))
            throw new ArgumentException("Hold ID cannot be empty.", nameof(holdId));

        // Verify object and hold exist
        var status = await GetStatusAsync(objectId, ct);
        if (!status.Exists)
            throw new KeyNotFoundException($"Object {objectId} does not exist in WORM storage.");

        if (!status.LegalHolds.Any(h => h.HoldId == holdId))
            throw new KeyNotFoundException($"Legal hold '{holdId}' not found on object {objectId}.");

        // Delegate to provider-specific implementation
        await RemoveLegalHoldInternalAsync(objectId, holdId, ct);
    }

    /// <summary>
    /// Provider-specific write implementation.
    /// Must handle actual storage, hash computation, and retention enforcement.
    /// </summary>
    protected abstract Task<WormWriteResult> WriteInternalAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        WriteContext context,
        CancellationToken ct);

    /// <summary>
    /// Provider-specific retention extension implementation.
    /// Must update retention metadata and enforce the new expiry.
    /// </summary>
    protected abstract Task ExtendRetentionInternalAsync(
        Guid objectId,
        DateTimeOffset newExpiry,
        CancellationToken ct);

    /// <summary>
    /// Provider-specific legal hold placement implementation.
    /// Must persist the hold and prevent deletion.
    /// </summary>
    protected abstract Task PlaceLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        string reason,
        CancellationToken ct);

    /// <summary>
    /// Provider-specific legal hold removal implementation.
    /// Must remove the hold from metadata.
    /// </summary>
    protected abstract Task RemoveLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        CancellationToken ct);

    // Delegate remaining interface methods to derived classes
    public abstract Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default);
    public abstract Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);
    public abstract Task<bool> ExistsAsync(Guid objectId, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<LegalHold>> GetLegalHoldsAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Provides metadata about this WORM provider for diagnostics and AI agents.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "WormStorage";
        metadata["EnforcementMode"] = EnforcementMode.ToString();
        metadata["SupportsLegalHolds"] = true;
        metadata["SupportsRetentionExtension"] = true;
        metadata["HardwareEnforced"] = EnforcementMode == WormEnforcementMode.HardwareIntegrated ||
                                       EnforcementMode == WormEnforcementMode.Hybrid;
        return metadata;
    }
}

#region Stub Types for WORM Intelligence Integration

/// <summary>Stub type for retention policy recommendation from AI.</summary>
public record RetentionPolicyRecommendation(
    TimeSpan RecommendedRetention,
    bool ShouldPlaceLegalHold,
    string Rationale,
    double ConfidenceScore);

#endregion
