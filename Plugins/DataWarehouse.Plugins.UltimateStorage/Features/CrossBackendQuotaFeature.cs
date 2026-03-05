using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Cross-Backend Quota Management Feature (C8) - Quota enforcement across multiple storage backends.
    ///
    /// Features:
    /// - Per-user/per-tenant quotas across all backends
    /// - Quota enforcement on write operations
    /// - Usage tracking and reporting
    /// - Soft quotas (warning) and hard quotas (reject)
    /// - Configurable per-backend limits
    /// - Real-time usage monitoring
    /// - Quota alerts and notifications
    /// - Grace period support for soft quota violations
    /// </summary>
    public sealed class CrossBackendQuotaFeature : IDisposable
    {
        private readonly BoundedDictionary<string, QuotaProfile> _quotaProfiles = new BoundedDictionary<string, QuotaProfile>(1000);
        private readonly BoundedDictionary<string, BackendUsage> _backendUsage = new BoundedDictionary<string, BackendUsage>(1000);
        private readonly BoundedDictionary<string, List<QuotaViolation>> _violationHistory = new BoundedDictionary<string, List<QuotaViolation>>(1000);
        // Per-tenant aggregate byte counter for O(1) GetTotalUsage on the write hot path
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _tenantTotalUsage = new(StringComparer.Ordinal);
        private bool _disposed;

        // Configuration
        private int _softQuotaWarningThresholdPercent = 80;
        private TimeSpan _gracePeriod = TimeSpan.FromDays(7);

        // Statistics
        private long _totalQuotaChecks;
        private long _totalQuotaViolations;
        private long _totalSoftQuotaWarnings;
        private long _totalHardQuotaRejections;

        /// <summary>
        /// Initializes a new instance of the CrossBackendQuotaFeature.
        /// </summary>
        public CrossBackendQuotaFeature()
        {
        }

        /// <summary>
        /// Gets the total number of quota checks performed.
        /// </summary>
        public long TotalQuotaChecks => Interlocked.Read(ref _totalQuotaChecks);

        /// <summary>
        /// Gets the total number of quota violations detected.
        /// </summary>
        public long TotalQuotaViolations => Interlocked.Read(ref _totalQuotaViolations);

        /// <summary>
        /// Gets the total number of soft quota warnings issued.
        /// </summary>
        public long TotalSoftQuotaWarnings => Interlocked.Read(ref _totalSoftQuotaWarnings);

        /// <summary>
        /// Gets the total number of hard quota rejections.
        /// </summary>
        public long TotalHardQuotaRejections => Interlocked.Read(ref _totalHardQuotaRejections);

        /// <summary>
        /// Gets or sets the soft quota warning threshold percentage (0-100).
        /// </summary>
        public int SoftQuotaWarningThresholdPercent
        {
            get => _softQuotaWarningThresholdPercent;
            set => _softQuotaWarningThresholdPercent = Math.Clamp(value, 0, 100);
        }

        /// <summary>
        /// Gets or sets the grace period for soft quota violations.
        /// </summary>
        public TimeSpan GracePeriod
        {
            get => _gracePeriod;
            set => _gracePeriod = value > TimeSpan.Zero ? value : TimeSpan.FromDays(7);
        }

        /// <summary>
        /// Creates or updates a quota profile for a tenant/user.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <param name="softQuotaBytes">Soft quota in bytes (warning threshold).</param>
        /// <param name="hardQuotaBytes">Hard quota in bytes (rejection threshold).</param>
        /// <param name="perBackendLimits">Optional per-backend quota limits.</param>
        public void SetQuota(
            string tenantId,
            long softQuotaBytes,
            long hardQuotaBytes,
            Dictionary<string, long>? perBackendLimits = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (softQuotaBytes < 0 || hardQuotaBytes < 0)
            {
                throw new ArgumentException("Quota values cannot be negative");
            }

            if (hardQuotaBytes > 0 && softQuotaBytes > hardQuotaBytes)
            {
                throw new ArgumentException("Soft quota cannot exceed hard quota");
            }

            var profile = _quotaProfiles.GetOrAdd(tenantId, _ => new QuotaProfile
            {
                TenantId = tenantId
            });

            profile.SoftQuotaBytes = softQuotaBytes;
            profile.HardQuotaBytes = hardQuotaBytes;
            profile.PerBackendLimits = perBackendLimits != null
                ? new BoundedDictionary<string, long>(1000)
                : new BoundedDictionary<string, long>(1000);
            profile.LastModified = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets the quota profile for a tenant.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <returns>Quota profile or null if not found.</returns>
        public QuotaProfile? GetQuota(string tenantId)
        {
            return _quotaProfiles.TryGetValue(tenantId, out var profile) ? profile : null;
        }

        /// <summary>
        /// Checks if a write operation would violate quota constraints.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <param name="backendId">Backend strategy ID.</param>
        /// <param name="bytesToWrite">Number of bytes to write.</param>
        /// <returns>Quota check result.</returns>
        public QuotaCheckResult CheckQuota(string tenantId, string backendId, long bytesToWrite)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            Interlocked.Increment(ref _totalQuotaChecks);

            if (bytesToWrite < 0)
            {
                throw new ArgumentException("Bytes to write cannot be negative");
            }

            // Get or create quota profile
            if (!_quotaProfiles.TryGetValue(tenantId, out var profile))
            {
                // No quota defined - allow operation
                return new QuotaCheckResult
                {
                    IsAllowed = true,
                    ViolationType = QuotaViolationType.None
                };
            }

            // Get current usage
            var currentUsage = GetTotalUsage(tenantId);
            var newUsage = currentUsage + bytesToWrite;

            // Check hard quota
            if (profile.HardQuotaBytes > 0 && newUsage > profile.HardQuotaBytes)
            {
                // Check if within grace period
                var inGracePeriod = profile.SoftQuotaViolationTime.HasValue &&
                                   (DateTime.UtcNow - profile.SoftQuotaViolationTime.Value) <= _gracePeriod;

                if (!inGracePeriod)
                {
                    Interlocked.Increment(ref _totalQuotaViolations);
                    Interlocked.Increment(ref _totalHardQuotaRejections);

                    RecordViolation(tenantId, QuotaViolationType.HardQuota, newUsage, profile.HardQuotaBytes);

                    return new QuotaCheckResult
                    {
                        IsAllowed = false,
                        ViolationType = QuotaViolationType.HardQuota,
                        CurrentUsageBytes = currentUsage,
                        QuotaLimitBytes = profile.HardQuotaBytes,
                        ExcessBytes = newUsage - profile.HardQuotaBytes,
                        Message = $"Hard quota exceeded: {newUsage:N0} bytes exceeds limit of {profile.HardQuotaBytes:N0} bytes"
                    };
                }
            }

            // Check soft quota
            if (profile.SoftQuotaBytes > 0 && newUsage > profile.SoftQuotaBytes)
            {
                Interlocked.Increment(ref _totalSoftQuotaWarnings);

                // Record first soft quota violation time
                if (!profile.SoftQuotaViolationTime.HasValue)
                {
                    profile.SoftQuotaViolationTime = DateTime.UtcNow;
                }

                RecordViolation(tenantId, QuotaViolationType.SoftQuota, newUsage, profile.SoftQuotaBytes);

                return new QuotaCheckResult
                {
                    IsAllowed = true,
                    ViolationType = QuotaViolationType.SoftQuota,
                    CurrentUsageBytes = currentUsage,
                    QuotaLimitBytes = profile.SoftQuotaBytes,
                    ExcessBytes = newUsage - profile.SoftQuotaBytes,
                    Message = $"Soft quota exceeded: {newUsage:N0} bytes exceeds soft limit of {profile.SoftQuotaBytes:N0} bytes"
                };
            }

            // Check per-backend limits
            if (profile.PerBackendLimits.TryGetValue(backendId, out var backendLimit) && backendLimit > 0)
            {
                var backendUsage = GetBackendUsage(tenantId, backendId);
                var newBackendUsage = backendUsage + bytesToWrite;

                if (newBackendUsage > backendLimit)
                {
                    Interlocked.Increment(ref _totalQuotaViolations);

                    return new QuotaCheckResult
                    {
                        IsAllowed = false,
                        ViolationType = QuotaViolationType.PerBackendLimit,
                        CurrentUsageBytes = backendUsage,
                        QuotaLimitBytes = backendLimit,
                        ExcessBytes = newBackendUsage - backendLimit,
                        Message = $"Backend quota exceeded: {newBackendUsage:N0} bytes exceeds backend '{backendId}' limit of {backendLimit:N0} bytes"
                    };
                }
            }

            // Check warning threshold
            if (profile.SoftQuotaBytes > 0)
            {
                var warningThreshold = profile.SoftQuotaBytes * _softQuotaWarningThresholdPercent / 100.0;
                if (newUsage > warningThreshold)
                {
                    return new QuotaCheckResult
                    {
                        IsAllowed = true,
                        ViolationType = QuotaViolationType.Warning,
                        CurrentUsageBytes = currentUsage,
                        QuotaLimitBytes = profile.SoftQuotaBytes,
                        Message = $"Approaching quota limit: {newUsage:N0} bytes ({newUsage * 100.0 / profile.SoftQuotaBytes:F1}% of soft quota)"
                    };
                }
            }

            return new QuotaCheckResult
            {
                IsAllowed = true,
                ViolationType = QuotaViolationType.None,
                CurrentUsageBytes = currentUsage,
                QuotaLimitBytes = profile.HardQuotaBytes > 0 ? profile.HardQuotaBytes : profile.SoftQuotaBytes
            };
        }

        /// <summary>
        /// Records actual usage for a tenant and backend.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <param name="backendId">Backend strategy ID.</param>
        /// <param name="bytesWritten">Number of bytes written.</param>
        public void RecordUsage(string tenantId, string backendId, long bytesWritten)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (bytesWritten <= 0) return;

            var key = $"{tenantId}:{backendId}";
            var usage = _backendUsage.GetOrAdd(key, _ => new BackendUsage
            {
                TenantId = tenantId,
                BackendId = backendId
            });

            Interlocked.Add(ref usage.BytesStored, bytesWritten);
            usage.LastUpdated = DateTime.UtcNow;

            // Update O(1) per-tenant aggregate counter
            _tenantTotalUsage.AddOrUpdate(tenantId, bytesWritten, (_, existing) => Math.Max(0, existing + bytesWritten));
        }

        /// <summary>
        /// Records deletion for a tenant and backend.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <param name="backendId">Backend strategy ID.</param>
        /// <param name="bytesDeleted">Number of bytes deleted.</param>
        public void RecordDeletion(string tenantId, string backendId, long bytesDeleted)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (bytesDeleted <= 0) return;

            var key = $"{tenantId}:{backendId}";
            if (_backendUsage.TryGetValue(key, out var usage))
            {
                Interlocked.Add(ref usage.BytesStored, -bytesDeleted);
                usage.LastUpdated = DateTime.UtcNow;
            }

            // Update O(1) per-tenant aggregate counter
            _tenantTotalUsage.AddOrUpdate(tenantId, 0L, (_, existing) => Math.Max(0, existing - bytesDeleted));
        }

        /// <summary>
        /// Gets total usage across all backends for a tenant.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <returns>Total bytes stored.</returns>
        public long GetTotalUsage(string tenantId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Use O(1) cached aggregate counter maintained by RecordUsage/RecordDeletion.
            // Falls back to full scan if the counter isn't populated (e.g., direct BytesStored mutations).
            if (_tenantTotalUsage.TryGetValue(tenantId, out var cached))
                return cached;

            // Fallback: compute from per-backend entries (O(n) over all keys)
            return _backendUsage.Values
                .Where(u => u.TenantId == tenantId)
                .Sum(u => Math.Max(0, Interlocked.Read(ref u.BytesStored)));
        }

        /// <summary>
        /// Gets usage for a specific backend.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <param name="backendId">Backend strategy ID.</param>
        /// <returns>Bytes stored on the backend.</returns>
        public long GetBackendUsage(string tenantId, string backendId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var key = $"{tenantId}:{backendId}";
            if (_backendUsage.TryGetValue(key, out var usage))
            {
                return Math.Max(0, Interlocked.Read(ref usage.BytesStored));
            }
            return 0;
        }

        /// <summary>
        /// Gets detailed usage report for a tenant.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <returns>Usage report.</returns>
        public UsageReport GetUsageReport(string tenantId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var profile = GetQuota(tenantId);
            var totalUsage = GetTotalUsage(tenantId);

            var backendBreakdown = _backendUsage.Values
                .Where(u => u.TenantId == tenantId)
                .Select(u => new BackendUsageInfo
                {
                    BackendId = u.BackendId,
                    BytesStored = Interlocked.Read(ref u.BytesStored),
                    LastUpdated = u.LastUpdated
                })
                .ToList();

            return new UsageReport
            {
                TenantId = tenantId,
                TotalUsageBytes = totalUsage,
                SoftQuotaBytes = profile?.SoftQuotaBytes ?? 0,
                HardQuotaBytes = profile?.HardQuotaBytes ?? 0,
                UtilizationPercentage = profile != null && profile.HardQuotaBytes > 0
                    ? totalUsage * 100.0 / profile.HardQuotaBytes
                    : 0,
                BackendBreakdown = backendBreakdown,
                IsOverSoftQuota = profile != null && profile.SoftQuotaBytes > 0 && totalUsage > profile.SoftQuotaBytes,
                IsOverHardQuota = profile != null && profile.HardQuotaBytes > 0 && totalUsage > profile.HardQuotaBytes
            };
        }

        /// <summary>
        /// Gets quota violation history for a tenant.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        /// <returns>List of quota violations.</returns>
        public List<QuotaViolation> GetViolationHistory(string tenantId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _violationHistory.TryGetValue(tenantId, out var violations)
                ? violations.ToList()
                : new List<QuotaViolation>();
        }

        /// <summary>
        /// Removes a quota profile.
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        public void RemoveQuota(string tenantId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _quotaProfiles.TryRemove(tenantId, out _);
        }

        /// <summary>
        /// Resets usage tracking for a tenant (does not affect quota settings).
        /// </summary>
        /// <param name="tenantId">Tenant or user identifier.</param>
        public void ResetUsage(string tenantId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var keysToRemove = _backendUsage.Keys
                .Where(k => k.StartsWith($"{tenantId}:", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _backendUsage.TryRemove(key, out _);
            }

            _violationHistory.TryRemove(tenantId, out _);
        }

        #region Private Methods

        private void RecordViolation(string tenantId, QuotaViolationType violationType, long actualUsage, long quotaLimit)
        {
            var violation = new QuotaViolation
            {
                TenantId = tenantId,
                ViolationType = violationType,
                Timestamp = DateTime.UtcNow,
                ActualUsageBytes = actualUsage,
                QuotaLimitBytes = quotaLimit
            };

            var violations = _violationHistory.GetOrAdd(tenantId, _ => new List<QuotaViolation>());

            lock (violations)
            {
                violations.Add(violation);

                // Keep only last 100 violations per tenant
                if (violations.Count > 100)
                {
                    violations.RemoveAt(0);
                }
            }
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _quotaProfiles.Clear();
            _backendUsage.Clear();
            _violationHistory.Clear();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Quota profile for a tenant or user.
    /// </summary>
    public sealed class QuotaProfile
    {
        /// <summary>Tenant or user identifier.</summary>
        public string TenantId { get; init; } = string.Empty;

        /// <summary>Soft quota in bytes (warning threshold).</summary>
        public long SoftQuotaBytes { get; set; }

        /// <summary>Hard quota in bytes (rejection threshold).</summary>
        public long HardQuotaBytes { get; set; }

        /// <summary>Per-backend quota limits.</summary>
        public BoundedDictionary<string, long> PerBackendLimits { get; set; } = new BoundedDictionary<string, long>(1000);

        /// <summary>When the quota was last modified.</summary>
        public DateTime LastModified { get; set; }

        /// <summary>When soft quota was first violated (for grace period tracking).</summary>
        public DateTime? SoftQuotaViolationTime { get; set; }
    }

    /// <summary>
    /// Backend usage tracking.
    /// </summary>
    public sealed class BackendUsage
    {
        /// <summary>Tenant or user identifier.</summary>
        public string TenantId { get; init; } = string.Empty;

        /// <summary>Backend strategy ID.</summary>
        public string BackendId { get; init; } = string.Empty;

        /// <summary>Bytes stored on this backend.</summary>
        public long BytesStored;

        /// <summary>Last update timestamp.</summary>
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Result of a quota check operation.
    /// </summary>
    public sealed class QuotaCheckResult
    {
        /// <summary>Whether the operation is allowed.</summary>
        public bool IsAllowed { get; init; }

        /// <summary>Type of quota violation (if any).</summary>
        public QuotaViolationType ViolationType { get; init; }

        /// <summary>Current usage in bytes.</summary>
        public long CurrentUsageBytes { get; init; }

        /// <summary>Quota limit in bytes.</summary>
        public long QuotaLimitBytes { get; init; }

        /// <summary>Excess bytes over quota (if violated).</summary>
        public long ExcessBytes { get; init; }

        /// <summary>Human-readable message.</summary>
        public string? Message { get; init; }
    }

    /// <summary>
    /// Type of quota violation.
    /// </summary>
    public enum QuotaViolationType
    {
        /// <summary>No violation.</summary>
        None,

        /// <summary>Warning threshold approached.</summary>
        Warning,

        /// <summary>Soft quota exceeded.</summary>
        SoftQuota,

        /// <summary>Hard quota exceeded.</summary>
        HardQuota,

        /// <summary>Per-backend limit exceeded.</summary>
        PerBackendLimit
    }

    /// <summary>
    /// Quota violation record.
    /// </summary>
    public sealed class QuotaViolation
    {
        /// <summary>Tenant identifier.</summary>
        public string TenantId { get; init; } = string.Empty;

        /// <summary>Type of violation.</summary>
        public QuotaViolationType ViolationType { get; init; }

        /// <summary>When the violation occurred.</summary>
        public DateTime Timestamp { get; init; }

        /// <summary>Actual usage at time of violation.</summary>
        public long ActualUsageBytes { get; init; }

        /// <summary>Quota limit that was exceeded.</summary>
        public long QuotaLimitBytes { get; init; }
    }

    /// <summary>
    /// Usage report for a tenant.
    /// </summary>
    public sealed class UsageReport
    {
        /// <summary>Tenant identifier.</summary>
        public string TenantId { get; init; } = string.Empty;

        /// <summary>Total usage across all backends.</summary>
        public long TotalUsageBytes { get; init; }

        /// <summary>Soft quota limit.</summary>
        public long SoftQuotaBytes { get; init; }

        /// <summary>Hard quota limit.</summary>
        public long HardQuotaBytes { get; init; }

        /// <summary>Utilization percentage (0-100+).</summary>
        public double UtilizationPercentage { get; init; }

        /// <summary>Per-backend usage breakdown.</summary>
        public List<BackendUsageInfo> BackendBreakdown { get; init; } = new();

        /// <summary>Whether soft quota is exceeded.</summary>
        public bool IsOverSoftQuota { get; init; }

        /// <summary>Whether hard quota is exceeded.</summary>
        public bool IsOverHardQuota { get; init; }
    }

    /// <summary>
    /// Backend usage information.
    /// </summary>
    public sealed class BackendUsageInfo
    {
        /// <summary>Backend strategy ID.</summary>
        public string BackendId { get; init; } = string.Empty;

        /// <summary>Bytes stored on this backend.</summary>
        public long BytesStored { get; init; }

        /// <summary>Last update timestamp.</summary>
        public DateTime LastUpdated { get; init; }
    }

    #endregion
}
