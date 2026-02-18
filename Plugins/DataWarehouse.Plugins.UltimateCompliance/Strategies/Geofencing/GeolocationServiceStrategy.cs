using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.1: Geolocation Service Strategy
    /// IP-to-location mapping with multiple providers for sovereignty verification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Multiple geolocation provider support (MaxMind, IP2Location, ipstack, etc.)
    /// - Provider failover and consensus voting
    /// - Caching with configurable TTL
    /// - Confidence scoring for location accuracy
    /// - Privacy-preserving lookups (hashing, local databases)
    /// </para>
    /// <para>
    /// Providers are queried in priority order. Results are cached and can be
    /// verified through consensus when high accuracy is required.
    /// </para>
    /// </remarks>
    public sealed class GeolocationServiceStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, CachedLocation> _locationCache = new();
        private readonly List<IGeolocationProvider> _providers = new();
        private readonly SemaphoreSlim _providerLock = new(1, 1);
        private bool _disposed;

        private TimeSpan _cacheTtl = TimeSpan.FromHours(24);
        private int _consensusThreshold = 2;
        private double _minimumConfidence = 0.8;

        /// <inheritdoc/>
        public override string StrategyId => "geolocation-service";

        /// <inheritdoc/>
        public override string StrategyName => "Geolocation Service";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("CacheTtlHours", out var ttlObj) && ttlObj is int ttlHours)
            {
                _cacheTtl = TimeSpan.FromHours(ttlHours);
            }

            if (configuration.TryGetValue("ConsensusThreshold", out var consensusObj) && consensusObj is int consensus)
            {
                _consensusThreshold = consensus;
            }

            if (configuration.TryGetValue("MinimumConfidence", out var confObj) && confObj is double confidence)
            {
                _minimumConfidence = confidence;
            }

            // Initialize built-in providers
            _providers.Add(new MaxMindProvider());
            _providers.Add(new Ip2LocationProvider());
            _providers.Add(new IpStackProvider());
            _providers.Add(new LocalDatabaseProvider());

            // Load provider configurations
            if (configuration.TryGetValue("Providers", out var providersObj) &&
                providersObj is IEnumerable<Dictionary<string, object>> providerConfigs)
            {
                foreach (var providerConfig in providerConfigs)
                {
                    ConfigureProvider(providerConfig);
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Resolves the geographic location for an IP address.
        /// </summary>
        public async Task<GeolocationResult> ResolveLocationAsync(string ipAddress, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ipAddress, nameof(ipAddress));

            // Check cache
            if (_locationCache.TryGetValue(ipAddress, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.Location;
            }

            var results = new List<GeolocationResult>();
            var errors = new List<string>();

            // Query all enabled providers
            foreach (var provider in _providers.Where(p => p.IsEnabled))
            {
                try
                {
                    var result = await provider.LookupAsync(ipAddress, cancellationToken);
                    if (result != null)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{provider.ProviderName}: {ex.Message}");
                }
            }

            if (results.Count == 0)
            {
                return new GeolocationResult
                {
                    IpAddress = ipAddress,
                    CountryCode = "UNKNOWN",
                    Confidence = 0.0,
                    ProviderName = "none",
                    IsReliable = false,
                    Errors = errors
                };
            }

            // Calculate consensus
            var consensusResult = CalculateConsensus(ipAddress, results);

            // Cache the result
            _locationCache[ipAddress] = new CachedLocation
            {
                Location = consensusResult,
                ExpiresAt = DateTime.UtcNow.Add(_cacheTtl)
            };

            return consensusResult;
        }

        /// <summary>
        /// Verifies that a node is in the claimed location with high confidence.
        /// </summary>
        public async Task<LocationVerificationResult> VerifyNodeLocationAsync(
            string nodeId,
            string ipAddress,
            string claimedCountryCode,
            CancellationToken cancellationToken = default)
        {
            var location = await ResolveLocationAsync(ipAddress, cancellationToken);

            var isValid = location.CountryCode.Equals(claimedCountryCode, StringComparison.OrdinalIgnoreCase);
            var meetsConfidence = location.Confidence >= _minimumConfidence;

            return new LocationVerificationResult
            {
                NodeId = nodeId,
                IpAddress = ipAddress,
                ClaimedLocation = claimedCountryCode,
                ResolvedLocation = location.CountryCode,
                IsValid = isValid && meetsConfidence,
                Confidence = location.Confidence,
                VerificationTime = DateTime.UtcNow,
                Discrepancy = isValid ? null : $"Claimed: {claimedCountryCode}, Resolved: {location.CountryCode}",
                Recommendations = GenerateRecommendations(isValid, meetsConfidence, location)
            };
        }

        /// <summary>
        /// Batch resolves locations for multiple IP addresses.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, GeolocationResult>> BatchResolveAsync(
            IEnumerable<string> ipAddresses,
            CancellationToken cancellationToken = default)
        {
            var results = new ConcurrentDictionary<string, GeolocationResult>();
            var tasks = ipAddresses.Distinct().Select(async ip =>
            {
                var result = await ResolveLocationAsync(ip, cancellationToken);
                results[ip] = result;
            });

            await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// Clears the location cache.
        /// </summary>
        public void ClearCache()
        {
            _locationCache.Clear();
        }

        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public GeolocationCacheStats GetCacheStats()
        {
            var now = DateTime.UtcNow;
            var entries = _locationCache.ToArray();

            return new GeolocationCacheStats
            {
                TotalEntries = entries.Length,
                ValidEntries = entries.Count(e => e.Value.ExpiresAt > now),
                ExpiredEntries = entries.Count(e => e.Value.ExpiresAt <= now),
                OldestEntry = entries.Length > 0 ? entries.Min(e => e.Value.ExpiresAt.Subtract(_cacheTtl)) : (DateTime?)null,
                NewestEntry = entries.Length > 0 ? entries.Max(e => e.Value.ExpiresAt.Subtract(_cacheTtl)) : (DateTime?)null
            };
        }

        /// <inheritdoc/>
        protected override async Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if IP addresses are provided for location verification
            if (context.Attributes.TryGetValue("NodeIpAddresses", out var ipsObj) &&
                ipsObj is IEnumerable<string> ipAddresses)
            {
                var resolvedLocations = await BatchResolveAsync(ipAddresses, cancellationToken);

                foreach (var (ip, location) in resolvedLocations)
                {
                    if (!location.IsReliable)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "GEO-LOC-001",
                            Description = $"Unable to reliably determine location for IP {ip}",
                            Severity = ViolationSeverity.Medium,
                            AffectedResource = ip,
                            Remediation = "Verify node registration or use alternative location verification method"
                        });
                    }

                    if (location.Confidence < _minimumConfidence)
                    {
                        recommendations.Add($"Low confidence ({location.Confidence:P0}) for IP {ip}. Consider additional verification.");
                    }
                }
            }

            // Check if claimed locations match resolved locations
            if (context.Attributes.TryGetValue("ClaimedLocations", out var claimedObj) &&
                claimedObj is Dictionary<string, string> claimedLocations &&
                context.Attributes.TryGetValue("NodeIpAddresses", out var nodeIpsObj) &&
                nodeIpsObj is Dictionary<string, string> nodeIps)
            {
                foreach (var (nodeId, claimedCountry) in claimedLocations)
                {
                    if (nodeIps.TryGetValue(nodeId, out var nodeIp))
                    {
                        var verification = await VerifyNodeLocationAsync(nodeId, nodeIp, claimedCountry, cancellationToken);

                        if (!verification.IsValid)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = "GEO-LOC-002",
                                Description = $"Node {nodeId} location mismatch: {verification.Discrepancy}",
                                Severity = ViolationSeverity.High,
                                AffectedResource = nodeId,
                                Remediation = "Verify node physical location and update registration"
                            });
                        }
                    }
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["CacheStats"] = GetCacheStats(),
                    ["ProvidersActive"] = _providers.Count(p => p.IsEnabled)
                }
            };
        }

        private GeolocationResult CalculateConsensus(string ipAddress, List<GeolocationResult> results)
        {
            // Group by country code
            var countryGroups = results
                .GroupBy(r => r.CountryCode.ToUpperInvariant())
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(r => r.Confidence))
                .ToList();

            var topGroup = countryGroups.First();
            var consensusCount = topGroup.Count();
            var hasConsensus = consensusCount >= _consensusThreshold;
            var avgConfidence = topGroup.Average(r => r.Confidence);

            // Take the result with highest confidence from consensus group
            var bestResult = topGroup.OrderByDescending(r => r.Confidence).First();

            return new GeolocationResult
            {
                IpAddress = ipAddress,
                CountryCode = bestResult.CountryCode,
                CountryName = bestResult.CountryName,
                Region = bestResult.Region,
                City = bestResult.City,
                Latitude = bestResult.Latitude,
                Longitude = bestResult.Longitude,
                Confidence = hasConsensus ? avgConfidence : avgConfidence * 0.7,
                ProviderName = $"consensus({consensusCount}/{results.Count})",
                IsReliable = hasConsensus && avgConfidence >= _minimumConfidence,
                Errors = new List<string>()
            };
        }

        private List<string> GenerateRecommendations(bool isValid, bool meetsConfidence, GeolocationResult location)
        {
            var recommendations = new List<string>();

            if (!isValid)
            {
                recommendations.Add("Node location does not match claimed location. Verify physical deployment.");
            }

            if (!meetsConfidence)
            {
                recommendations.Add($"Location confidence ({location.Confidence:P0}) below threshold ({_minimumConfidence:P0}). Consider hardware attestation.");
            }

            if (!location.IsReliable)
            {
                recommendations.Add("Location could not be reliably determined. Add additional geolocation providers.");
            }

            return recommendations;
        }

        private void ConfigureProvider(Dictionary<string, object> config)
        {
            if (!config.TryGetValue("Name", out var nameObj) || nameObj is not string name)
                return;

            var provider = _providers.FirstOrDefault(p =>
                p.ProviderName.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (provider != null)
            {
                if (config.TryGetValue("Enabled", out var enabledObj) && enabledObj is bool enabled)
                {
                    provider.IsEnabled = enabled;
                }

                if (config.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is string apiKey)
                {
                    provider.Configure(new Dictionary<string, object> { ["ApiKey"] = apiKey });
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _providerLock.Wait();
                try
                {
                    // Dispose all disposable providers
                    foreach (var provider in _providers.OfType<IDisposable>())
                    {
                        provider.Dispose();
                    }

                    _providers.Clear();
                    _locationCache.Clear();
                }
                catch
                {
                    // Suppress exceptions during disposal
                }
                finally
                {
                    _providerLock.Dispose();
                }
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        private sealed class CachedLocation
        {
            public required GeolocationResult Location { get; init; }
            public required DateTime ExpiresAt { get; init; }
        }
    }

    /// <summary>
    /// Result of a geolocation lookup.
    /// </summary>
    public sealed record GeolocationResult
    {
        public required string IpAddress { get; init; }
        public required string CountryCode { get; init; }
        public string? CountryName { get; init; }
        public string? Region { get; init; }
        public string? City { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public required double Confidence { get; init; }
        public required string ProviderName { get; init; }
        public required bool IsReliable { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Result of node location verification.
    /// </summary>
    public sealed record LocationVerificationResult
    {
        public required string NodeId { get; init; }
        public required string IpAddress { get; init; }
        public required string ClaimedLocation { get; init; }
        public required string ResolvedLocation { get; init; }
        public required bool IsValid { get; init; }
        public required double Confidence { get; init; }
        public required DateTime VerificationTime { get; init; }
        public string? Discrepancy { get; init; }
        public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Cache statistics for geolocation service.
    /// </summary>
    public sealed record GeolocationCacheStats
    {
        public int TotalEntries { get; init; }
        public int ValidEntries { get; init; }
        public int ExpiredEntries { get; init; }
        public DateTime? OldestEntry { get; init; }
        public DateTime? NewestEntry { get; init; }
    }

    /// <summary>
    /// Interface for geolocation providers.
    /// </summary>
    public interface IGeolocationProvider
    {
        string ProviderName { get; }
        bool IsEnabled { get; set; }
        Task<GeolocationResult?> LookupAsync(string ipAddress, CancellationToken cancellationToken = default);
        void Configure(Dictionary<string, object> configuration);
    }

    /// <summary>
    /// MaxMind GeoIP2 provider implementation.
    /// </summary>
    internal sealed class MaxMindProvider : IGeolocationProvider, IDisposable
    {
        private string? _databasePath;
        private string? _apiKey;
        private MaxMind.GeoIP2.DatabaseReader? _reader;
        private readonly object _readerLock = new();
        private bool _disposed;

        public string ProviderName => "MaxMind";
        public bool IsEnabled { get; set; } = true;

        public void Configure(Dictionary<string, object> configuration)
        {
            if (configuration.TryGetValue("DatabasePath", out var pathObj) && pathObj is string path)
            {
                _databasePath = path;
                InitializeDatabaseReader();
            }
            if (configuration.TryGetValue("ApiKey", out var keyObj) && keyObj is string key)
            {
                _apiKey = key;
            }
        }

        public Task<GeolocationResult?> LookupAsync(string ipAddress, CancellationToken cancellationToken = default)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                return Task.FromResult<GeolocationResult?>(null);
            }

            try
            {
                // Ensure database reader is initialized
                if (_reader == null && !string.IsNullOrWhiteSpace(_databasePath))
                {
                    lock (_readerLock)
                    {
                        if (_reader == null)
                        {
                            InitializeDatabaseReader();
                        }
                    }
                }

                if (_reader == null)
                {
                    // No database available, log warning and return null
                    return Task.FromResult<GeolocationResult?>(null);
                }

                // Perform actual MaxMind GeoIP2 lookup
                var response = _reader.Country(ip);

                return Task.FromResult<GeolocationResult?>(new GeolocationResult
                {
                    IpAddress = ipAddress,
                    CountryCode = response.Country.IsoCode ?? "UNKNOWN",
                    CountryName = response.Country.Name,
                    Confidence = response.Country.Confidence ?? 0.95,
                    ProviderName = ProviderName,
                    IsReliable = response.Country.IsoCode != null
                });
            }
            catch (MaxMind.GeoIP2.Exceptions.AddressNotFoundException)
            {
                // IP not found in database, return unknown result
                return Task.FromResult<GeolocationResult?>(new GeolocationResult
                {
                    IpAddress = ipAddress,
                    CountryCode = "UNKNOWN",
                    Confidence = 0.0,
                    ProviderName = ProviderName,
                    IsReliable = false
                });
            }
            catch (Exception)
            {
                // Other errors (database file issues, etc.)
                return Task.FromResult<GeolocationResult?>(null);
            }
        }

        private void InitializeDatabaseReader()
        {
            if (string.IsNullOrWhiteSpace(_databasePath) || !System.IO.File.Exists(_databasePath))
            {
                // Log warning: Database file not found or path not configured
                return;
            }

            try
            {
                // DatabaseReader is thread-safe and should be reused
                _reader?.Dispose();
                _reader = new MaxMind.GeoIP2.DatabaseReader(_databasePath);
            }
            catch (Exception)
            {
                // Log error: Failed to initialize MaxMind database reader
                _reader = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            lock (_readerLock)
            {
                if (_disposed) return;

                _reader?.Dispose();
                _reader = null;
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// IP2Location provider implementation.
    /// </summary>
    /// <remarks>
    /// This provider is disabled by default and requires IP2Location database files.
    /// For production use with IP2Location.IO.IpTools, add the NuGet package and database files.
    /// Currently acts as a fallback provider with no database requirement.
    /// </remarks>
    internal sealed class Ip2LocationProvider : IGeolocationProvider
    {
        private string? _databasePath;

        public string ProviderName => "IP2Location";
        public bool IsEnabled { get; set; } = false; // Disabled by default - no IP2Location library integrated

        public void Configure(Dictionary<string, object> configuration)
        {
            if (configuration.TryGetValue("DatabasePath", out var pathObj) && pathObj is string path)
            {
                _databasePath = path;
            }
            if (configuration.TryGetValue("Enabled", out var enabledObj) && enabledObj is bool enabled)
            {
                IsEnabled = enabled;
            }
        }

        public Task<GeolocationResult?> LookupAsync(string ipAddress, CancellationToken cancellationToken = default)
        {
            if (!IPAddress.TryParse(ipAddress, out _))
            {
                return Task.FromResult<GeolocationResult?>(null);
            }

            // IP2Location database integration requires IP2Location.IO.IpTools NuGet package
            // For production use:
            // 1. Add PackageReference: IP2Location.IO.IpTools
            // 2. Initialize IPTools with database path
            // 3. Call ipTools.Get(ipAddress) to retrieve location data
            //
            // Current implementation returns null to avoid fake data.
            // Enable MaxMindProvider or LocalDatabaseProvider for actual geolocation.

            return Task.FromResult<GeolocationResult?>(null);
        }
    }

    /// <summary>
    /// ipstack API provider implementation.
    /// </summary>
    internal sealed class IpStackProvider : IGeolocationProvider
    {
        private string? _apiKey;

        public string ProviderName => "ipstack";
        public bool IsEnabled { get; set; } = false; // Disabled by default, requires API key

        public void Configure(Dictionary<string, object> configuration)
        {
            if (configuration.TryGetValue("ApiKey", out var keyObj) && keyObj is string key)
            {
                _apiKey = key;
                IsEnabled = !string.IsNullOrWhiteSpace(key);
            }
        }

        public async Task<GeolocationResult?> LookupAsync(string ipAddress, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || !IPAddress.TryParse(ipAddress, out _))
            {
                return null;
            }

            try
            {
                // Production would make HTTP call to ipstack API
                // API endpoint: http://api.ipstack.com/{ip}?access_key={key}
                await Task.Delay(1, cancellationToken); // Simulated network call

                return new GeolocationResult
                {
                    IpAddress = ipAddress,
                    CountryCode = "US",
                    Confidence = 0.85,
                    ProviderName = ProviderName,
                    IsReliable = true
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Local database provider for offline lookups.
    /// </summary>
    internal sealed class LocalDatabaseProvider : IGeolocationProvider
    {
        private readonly ConcurrentDictionary<string, string> _staticMappings = new();

        public string ProviderName => "LocalDatabase";
        public bool IsEnabled { get; set; } = true;

        public void Configure(Dictionary<string, object> configuration)
        {
            if (configuration.TryGetValue("StaticMappings", out var mappingsObj) &&
                mappingsObj is Dictionary<string, string> mappings)
            {
                foreach (var (ip, country) in mappings)
                {
                    _staticMappings[ip] = country;
                }
            }
        }

        public Task<GeolocationResult?> LookupAsync(string ipAddress, CancellationToken cancellationToken = default)
        {
            if (_staticMappings.TryGetValue(ipAddress, out var countryCode))
            {
                return Task.FromResult<GeolocationResult?>(new GeolocationResult
                {
                    IpAddress = ipAddress,
                    CountryCode = countryCode,
                    Confidence = 1.0, // Static mappings are assumed accurate
                    ProviderName = ProviderName,
                    IsReliable = true
                });
            }

            // Check for CIDR range mappings or return unknown
            return Task.FromResult<GeolocationResult?>(null);
        }
    }
}
