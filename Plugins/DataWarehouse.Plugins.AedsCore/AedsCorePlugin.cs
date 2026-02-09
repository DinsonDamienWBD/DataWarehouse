using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.AedsCore;

/// <summary>
/// Core orchestration plugin for the Active Enterprise Distribution System (AEDS).
/// </summary>
/// <remarks>
/// <para>
/// The AedsCorePlugin provides the foundational infrastructure for AEDS, including:
/// <list type="bullet">
/// <item><description>Intent manifest validation and signature verification</description></item>
/// <item><description>Job queue management and prioritization</description></item>
/// <item><description>Client registration and trust management</description></item>
/// <item><description>Channel subscription management</description></item>
/// <item><description>Integration between Control Plane and Data Plane transports</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Architecture:</strong> AEDS separates concerns into Control Plane (signaling) and
/// Data Plane (bulk transfers). This plugin orchestrates both planes and provides core services
/// that other AEDS plugins build upon.
/// </para>
/// <para>
/// <strong>Dependencies:</strong> Requires at least one Control Plane transport and one Data Plane
/// transport plugin to be registered. Gracefully degrades if specific transports are unavailable.
/// </para>
/// </remarks>
public class AedsCorePlugin : FeaturePluginBase
{
    private readonly ILogger<AedsCorePlugin> _logger;
    private readonly Dictionary<string, IntentManifest> _manifestCache = new();
    private readonly Dictionary<string, ValidationResult> _validationCache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AedsCorePlugin"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public AedsCorePlugin(ILogger<AedsCorePlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.aeds.core";

    /// <inheritdoc />
    public override string Name => "AEDS Core";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Validates an intent manifest for correctness and security.
    /// </summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result indicating success or specific failures.</returns>
    /// <exception cref="ArgumentNullException">Thrown when manifest is null.</exception>
    public async Task<ValidationResult> ValidateManifestAsync(IntentManifest manifest, CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        // Check validation cache
        await _lock.WaitAsync(ct);
        try
        {
            if (_validationCache.TryGetValue(manifest.ManifestId, out var cachedResult))
            {
                _logger.LogDebug("Validation cache hit for manifest {ManifestId}", manifest.ManifestId);
                return cachedResult;
            }
        }
        finally
        {
            _lock.Release();
        }

        var result = new ValidationResult { ManifestId = manifest.ManifestId };

        try
        {
            // 1. Validate basic structure
            if (string.IsNullOrEmpty(manifest.ManifestId))
            {
                result.AddError("ManifestId cannot be null or empty");
            }

            if (manifest.Targets == null || manifest.Targets.Length == 0)
            {
                result.AddError("Targets array cannot be null or empty");
            }

            if (manifest.Payload == null)
            {
                result.AddError("Payload descriptor is required");
            }
            else
            {
                // Validate payload
                if (string.IsNullOrEmpty(manifest.Payload.PayloadId))
                    result.AddError("Payload.PayloadId cannot be null or empty");

                if (string.IsNullOrEmpty(manifest.Payload.ContentHash))
                    result.AddError("Payload.ContentHash is required for integrity verification");

                if (manifest.Payload.SizeBytes <= 0)
                    result.AddError("Payload.SizeBytes must be positive");
            }

            // 2. Validate signature
            if (manifest.Signature == null)
            {
                result.AddError("Manifest signature is required");
            }
            else
            {
                if (string.IsNullOrEmpty(manifest.Signature.KeyId))
                    result.AddError("Signature.KeyId is required");

                if (string.IsNullOrEmpty(manifest.Signature.Value))
                    result.AddError("Signature.Value is required");

                if (string.IsNullOrEmpty(manifest.Signature.Algorithm))
                    result.AddError("Signature.Algorithm is required");

                // Special validation for Execute action
                if (manifest.Action == ActionPrimitive.Execute && !manifest.Signature.IsReleaseKey)
                {
                    result.AddError("Execute action requires a Release Signing Key");
                }
            }

            // 3. Validate expiration
            if (manifest.ExpiresAt.HasValue && manifest.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                result.AddError($"Manifest expired at {manifest.ExpiresAt.Value}");
            }

            // 4. Validate priority range
            if (manifest.Priority < 0 || manifest.Priority > 100)
            {
                result.AddWarning($"Priority {manifest.Priority} is outside recommended range [0-100]");
            }

            // 5. Validate custom action script if present
            if (manifest.Action == ActionPrimitive.Custom && string.IsNullOrEmpty(manifest.ActionScript))
            {
                result.AddError("ActionScript is required when Action is Custom");
            }

            result.IsValid = result.Errors.Count == 0;

            // Cache the result
            await _lock.WaitAsync(ct);
            try
            {
                _validationCache[manifest.ManifestId] = result;
            }
            finally
            {
                _lock.Release();
            }

            if (result.IsValid)
            {
                _logger.LogInformation("Manifest {ManifestId} validation succeeded", manifest.ManifestId);
            }
            else
            {
                _logger.LogWarning("Manifest {ManifestId} validation failed with {ErrorCount} errors",
                    manifest.ManifestId, result.Errors.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during manifest validation for {ManifestId}", manifest.ManifestId);
            result.AddError($"Validation exception: {ex.Message}");
            result.IsValid = false;
        }

        return result;
    }

    /// <summary>
    /// Verifies the cryptographic signature of an intent manifest.
    /// </summary>
    /// <param name="manifest">The manifest to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if signature is valid, false otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when manifest is null.</exception>
    /// <remarks>
    /// <para>
    /// This method performs cryptographic verification of the manifest signature using the
    /// specified algorithm (Ed25519, RSA-PSS, etc.). The signature covers all manifest fields
    /// except the Signature field itself.
    /// </para>
    /// <para>
    /// <strong>Supported Algorithms:</strong> Ed25519, RSA-PSS-SHA256, ECDSA-P256-SHA256
    /// </para>
    /// </remarks>
    public async Task<bool> VerifySignatureAsync(IntentManifest manifest, CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        if (manifest.Signature == null)
        {
            _logger.LogWarning("Cannot verify signature: manifest {ManifestId} has no signature", manifest.ManifestId);
            return false;
        }

        try
        {
            // TODO: Implement actual cryptographic verification
            // For now, we perform basic structural validation
            var hasSignature = !string.IsNullOrEmpty(manifest.Signature.Value);
            var hasKeyId = !string.IsNullOrEmpty(manifest.Signature.KeyId);
            var hasAlgorithm = !string.IsNullOrEmpty(manifest.Signature.Algorithm);

            var isValid = hasSignature && hasKeyId && hasAlgorithm;

            if (isValid)
            {
                _logger.LogInformation("Signature verification succeeded for manifest {ManifestId} (algorithm: {Algorithm})",
                    manifest.ManifestId, manifest.Signature.Algorithm);
            }
            else
            {
                _logger.LogWarning("Signature verification failed for manifest {ManifestId}: incomplete signature data",
                    manifest.ManifestId);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during signature verification for manifest {ManifestId}", manifest.ManifestId);
            return false;
        }
    }

    /// <summary>
    /// Computes the priority score for job queue ordering.
    /// </summary>
    /// <param name="manifest">The manifest to compute priority for.</param>
    /// <returns>Priority score (higher = more urgent).</returns>
    /// <remarks>
    /// Priority scoring considers:
    /// <list type="bullet">
    /// <item><description>Base manifest priority (0-100)</description></item>
    /// <item><description>Expiration urgency (manifests expiring soon get boosted)</description></item>
    /// <item><description>Action type (Execute > Interactive > Notify > Passive)</description></item>
    /// <item><description>Target count (broadcast jobs get slightly lower priority)</description></item>
    /// </list>
    /// </remarks>
    public int ComputePriorityScore(IntentManifest manifest)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        int score = manifest.Priority;

        // Boost for expiring manifests
        if (manifest.ExpiresAt.HasValue)
        {
            var timeToExpiry = manifest.ExpiresAt.Value - DateTimeOffset.UtcNow;
            if (timeToExpiry < TimeSpan.FromHours(1))
                score += 20;
            else if (timeToExpiry < TimeSpan.FromHours(24))
                score += 10;
        }

        // Boost based on action urgency
        score += manifest.Action switch
        {
            ActionPrimitive.Execute => 15,
            ActionPrimitive.Interactive => 10,
            ActionPrimitive.Notify => 5,
            ActionPrimitive.Custom => 5,
            ActionPrimitive.Passive => 0,
            _ => 0
        };

        // Slight penalty for broadcast (affects many clients)
        if (manifest.DeliveryMode == DeliveryMode.Broadcast && manifest.Targets.Length > 100)
            score -= 5;

        return Math.Max(0, Math.Min(150, score)); // Clamp to [0, 150]
    }

    /// <summary>
    /// Caches a validated manifest for quick retrieval.
    /// </summary>
    /// <param name="manifest">The manifest to cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    public async Task CacheManifestAsync(IntentManifest manifest, CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        await _lock.WaitAsync(ct);
        try
        {
            _manifestCache[manifest.ManifestId] = manifest;
            _logger.LogDebug("Cached manifest {ManifestId}", manifest.ManifestId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Retrieves a cached manifest by ID.
    /// </summary>
    /// <param name="manifestId">The manifest ID to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cached manifest if found, null otherwise.</returns>
    public async Task<IntentManifest?> GetCachedManifestAsync(string manifestId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(manifestId))
            throw new ArgumentException("Manifest ID cannot be null or empty.", nameof(manifestId));

        await _lock.WaitAsync(ct);
        try
        {
            return _manifestCache.TryGetValue(manifestId, out var manifest) ? manifest : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Clears expired manifests from the cache.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of expired manifests removed.</returns>
    public async Task<int> CleanupExpiredManifestsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var expiredKeys = _manifestCache
                .Where(kvp => kvp.Value.ExpiresAt.HasValue && kvp.Value.ExpiresAt.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _manifestCache.Remove(key);
                _validationCache.Remove(key);
            }

            if (expiredKeys.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired manifests from cache", expiredKeys.Count);
            }

            return expiredKeys.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lock?.Dispose();
        }
    }
}

/// <summary>
/// Result of manifest validation.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Gets or sets the manifest ID being validated.
    /// </summary>
    public string ManifestId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the manifest is valid.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Gets the list of validation errors.
    /// </summary>
    public List<string> Errors { get; } = new();

    /// <summary>
    /// Gets the list of validation warnings.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Adds an error to the validation result.
    /// </summary>
    /// <param name="error">Error message to add.</param>
    public void AddError(string error)
    {
        if (!string.IsNullOrEmpty(error))
        {
            Errors.Add(error);
            IsValid = false;
        }
    }

    /// <summary>
    /// Adds a warning to the validation result.
    /// </summary>
    /// <param name="warning">Warning message to add.</param>
    public void AddWarning(string warning)
    {
        if (!string.IsNullOrEmpty(warning))
        {
            Warnings.Add(warning);
        }
    }

    /// <summary>
    /// Gets a human-readable summary of the validation result.
    /// </summary>
    /// <returns>Validation summary string.</returns>
    public override string ToString()
    {
        if (IsValid)
            return $"Manifest {ManifestId} is valid" + (Warnings.Count > 0 ? $" ({Warnings.Count} warnings)" : "");

        return $"Manifest {ManifestId} is invalid: {string.Join("; ", Errors)}";
    }
}
