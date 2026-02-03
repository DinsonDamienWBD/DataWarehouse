using DataWarehouse.SDK.Security;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// Geo-Locked Key Strategy - Industry-first geographic access control for cryptographic keys.
    ///
    /// Features:
    /// - GPS coordinate verification for key access
    /// - Time-window restrictions (key only accessible during certain hours)
    /// - Geofencing with configurable radius (Haversine formula)
    /// - IP geolocation fallback when GPS unavailable
    /// - Location attestation using signed location proofs
    ///
    /// Security Model:
    /// Keys are protected by geographic constraints. Access is only granted when:
    /// 1. The requestor's location is within the configured geofence
    /// 2. The current time is within allowed access windows
    /// 3. Location proof can be cryptographically verified (if required)
    ///
    /// Use Cases:
    /// - Data sovereignty compliance (keys only accessible in specific countries)
    /// - Physical security zones (keys only accessible in secure facilities)
    /// - Time-based access control (keys only accessible during business hours)
    /// </summary>
    public sealed class GeoLockedKeyStrategy : KeyStoreStrategyBase
    {
        private GeoLockConfig _config = new();
        private readonly Dictionary<string, GeoLockedKeyData> _keys = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private HttpClient? _httpClient;
        private bool _disposed;

        // Earth's radius in kilometers for Haversine calculation
        private const double EarthRadiusKm = 6371.0;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 64,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Algorithm"] = "Geo-Locked Key Access Control",
                ["SecurityModel"] = "Geographic + Temporal Constraints",
                ["Features"] = new[] { "GPS Verification", "Time Windows", "Geofencing", "IP Geolocation", "Location Attestation" }
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("Latitude", out var lat) && lat is double latitude)
                _config.CenterLatitude = latitude;
            if (Configuration.TryGetValue("Longitude", out var lon) && lon is double longitude)
                _config.CenterLongitude = longitude;
            if (Configuration.TryGetValue("RadiusKm", out var rad) && rad is double radius)
                _config.RadiusKm = radius;
            if (Configuration.TryGetValue("AllowIpGeolocation", out var allowIp) && allowIp is bool allow)
                _config.AllowIpGeolocation = allow;
            if (Configuration.TryGetValue("RequireAttestation", out var reqAttest) && reqAttest is bool attest)
                _config.RequireLocationAttestation = attest;
            if (Configuration.TryGetValue("StoragePath", out var path) && path is string storagePath)
                _config.StoragePath = storagePath;
            if (Configuration.TryGetValue("AttestationPublicKey", out var pubKey) && pubKey is byte[] key)
                _config.AttestationPublicKey = key;
            if (Configuration.TryGetValue("IpGeolocationApiKey", out var apiKey) && apiKey is string api)
                _config.IpGeolocationApiKey = api;

            // Parse time windows
            if (Configuration.TryGetValue("TimeWindows", out var twObj) && twObj is TimeWindow[] windows)
                _config.AllowedTimeWindows = windows.ToList();

            _httpClient = new HttpClient();
            await LoadKeysFromStorage();
        }

        public override Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _keys.Count >= 0;
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Geo-locked key '{keyId}' not found.");

                // Verify geographic and temporal constraints
                await VerifyAccessConstraints(keyData, context);

                return keyData.EncryptedKeyMaterial;
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyMaterial, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                var geoKeyData = new GeoLockedKeyData
                {
                    KeyId = keyId,
                    EncryptedKeyMaterial = keyMaterial,
                    CenterLatitude = _config.CenterLatitude,
                    CenterLongitude = _config.CenterLongitude,
                    RadiusKm = _config.RadiusKm,
                    AllowedTimeWindows = _config.AllowedTimeWindows.ToList(),
                    RequireAttestation = _config.RequireLocationAttestation,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };

                _keys[keyId] = geoKeyData;
                _currentKeyId = keyId;

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Verifies that the access request meets all geographic and temporal constraints.
        /// </summary>
        private async Task VerifyAccessConstraints(GeoLockedKeyData keyData, ISecurityContext context)
        {
            // 1. Check time window constraints
            if (keyData.AllowedTimeWindows.Count > 0)
            {
                var now = DateTime.UtcNow;
                var inTimeWindow = keyData.AllowedTimeWindows.Any(tw => IsWithinTimeWindow(now, tw));

                if (!inTimeWindow)
                {
                    throw new UnauthorizedAccessException(
                        $"Key access denied: Current time is outside allowed access windows. " +
                        $"Allowed windows: {string.Join(", ", keyData.AllowedTimeWindows.Select(FormatTimeWindow))}");
                }
            }

            // 2. Get current location (from context or IP geolocation)
            var location = await GetRequestorLocation(context);

            if (location == null)
            {
                throw new UnauthorizedAccessException("Key access denied: Unable to determine requestor location.");
            }

            // 3. Verify location is within geofence
            var distance = CalculateHaversineDistance(
                keyData.CenterLatitude, keyData.CenterLongitude,
                location.Value.Latitude, location.Value.Longitude);

            if (distance > keyData.RadiusKm)
            {
                throw new UnauthorizedAccessException(
                    $"Key access denied: Requestor location ({location.Value.Latitude:F4}, {location.Value.Longitude:F4}) " +
                    $"is {distance:F2} km from authorized zone center. Maximum allowed: {keyData.RadiusKm} km.");
            }

            // 4. Verify location attestation if required
            if (keyData.RequireAttestation)
            {
                await VerifyLocationAttestation(context, location.Value);
            }

            // Log successful access
            keyData.AccessLog.Add(new GeoAccessLogEntry
            {
                Timestamp = DateTime.UtcNow,
                UserId = context.UserId,
                Latitude = location.Value.Latitude,
                Longitude = location.Value.Longitude,
                DistanceFromCenter = distance,
                AccessGranted = true
            });
        }

        /// <summary>
        /// Gets the requestor's location from context metadata or IP geolocation.
        /// </summary>
        private async Task<GeoLocation?> GetRequestorLocation(ISecurityContext context)
        {
            // First, try to get location from security context (GPS coordinates)
            if (context is IGeoSecurityContext geoContext)
            {
                return new GeoLocation
                {
                    Latitude = geoContext.Latitude,
                    Longitude = geoContext.Longitude,
                    Source = LocationSource.GPS,
                    Timestamp = DateTime.UtcNow
                };
            }

            // Fallback to IP geolocation if allowed
            if (_config.AllowIpGeolocation && _httpClient != null)
            {
                return await GetLocationFromIp(context);
            }

            return null;
        }

        /// <summary>
        /// Gets location from IP address using geolocation service.
        /// </summary>
        private async Task<GeoLocation?> GetLocationFromIp(ISecurityContext context)
        {
            try
            {
                // Get IP from context or detect current
                var ipAddress = context is INetworkSecurityContext netContext
                    ? netContext.ClientIpAddress
                    : null;

                if (string.IsNullOrEmpty(ipAddress))
                    return null;

                // Use ip-api.com (free tier) or configurable service
                var url = string.IsNullOrEmpty(_config.IpGeolocationApiKey)
                    ? $"http://ip-api.com/json/{ipAddress}"
                    : $"https://api.ipgeolocation.io/ipgeo?apiKey={_config.IpGeolocationApiKey}&ip={ipAddress}";

                var response = await _httpClient!.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonDocument.Parse(json);

                double lat, lon;
                if (string.IsNullOrEmpty(_config.IpGeolocationApiKey))
                {
                    // ip-api.com format
                    lat = result.RootElement.GetProperty("lat").GetDouble();
                    lon = result.RootElement.GetProperty("lon").GetDouble();
                }
                else
                {
                    // ipgeolocation.io format
                    lat = double.Parse(result.RootElement.GetProperty("latitude").GetString()!);
                    lon = double.Parse(result.RootElement.GetProperty("longitude").GetString()!);
                }

                return new GeoLocation
                {
                    Latitude = lat,
                    Longitude = lon,
                    Source = LocationSource.IpGeolocation,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculates the distance between two geographic points using the Haversine formula.
        /// Returns distance in kilometers.
        /// </summary>
        public static double CalculateHaversineDistance(
            double lat1, double lon1,
            double lat2, double lon2)
        {
            // Convert to radians
            var dLat = DegreesToRadians(lat2 - lat1);
            var dLon = DegreesToRadians(lon2 - lon1);

            var lat1Rad = DegreesToRadians(lat1);
            var lat2Rad = DegreesToRadians(lat2);

            // Haversine formula
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return EarthRadiusKm * c;
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

        /// <summary>
        /// Checks if a timestamp is within a time window.
        /// </summary>
        private static bool IsWithinTimeWindow(DateTime utcNow, TimeWindow window)
        {
            // Check day of week
            if (window.AllowedDays.Length > 0 && !window.AllowedDays.Contains(utcNow.DayOfWeek))
                return false;

            // Convert to window's timezone
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, window.TimeZone ?? TimeZoneInfo.Utc);
            var timeOfDay = localTime.TimeOfDay;

            // Check time range
            if (window.StartTime <= window.EndTime)
            {
                // Normal range (e.g., 09:00-17:00)
                return timeOfDay >= window.StartTime && timeOfDay <= window.EndTime;
            }
            else
            {
                // Overnight range (e.g., 22:00-06:00)
                return timeOfDay >= window.StartTime || timeOfDay <= window.EndTime;
            }
        }

        private static string FormatTimeWindow(TimeWindow tw)
        {
            var days = tw.AllowedDays.Length > 0
                ? string.Join(",", tw.AllowedDays.Select(d => d.ToString().Substring(0, 3)))
                : "All days";
            return $"{days} {tw.StartTime:hh\\:mm}-{tw.EndTime:hh\\:mm} {tw.TimeZone?.DisplayName ?? "UTC"}";
        }

        /// <summary>
        /// Verifies a cryptographically signed location attestation.
        /// Location attestations are signed proofs from trusted location providers (e.g., mobile OS secure enclave).
        /// </summary>
        private Task VerifyLocationAttestation(ISecurityContext context, GeoLocation location)
        {
            if (!(context is IAttestationSecurityContext attestContext))
            {
                throw new UnauthorizedAccessException(
                    "Key access denied: Location attestation required but not provided.");
            }

            // Verify attestation signature
            var attestation = attestContext.LocationAttestation;
            if (attestation == null || attestation.Length == 0)
            {
                throw new UnauthorizedAccessException("Key access denied: Empty location attestation.");
            }

            // Attestation format:
            // - 64 bytes: Timestamp (8) + Latitude (8) + Longitude (8) + Accuracy (8) + Provider ID (32)
            // - 64+ bytes: ECDSA signature over the above

            if (attestation.Length < 128)
            {
                throw new UnauthorizedAccessException("Key access denied: Invalid attestation format.");
            }

            var payload = attestation[..64];
            var signature = attestation[64..];

            // Verify timestamp is recent (within 5 minutes)
            var timestampTicks = BitConverter.ToInt64(payload, 0);
            var attestationTime = new DateTime(timestampTicks, DateTimeKind.Utc);
            if (Math.Abs((DateTime.UtcNow - attestationTime).TotalMinutes) > 5)
            {
                throw new UnauthorizedAccessException("Key access denied: Location attestation expired.");
            }

            // Verify location matches
            var attestedLat = BitConverter.ToDouble(payload, 8);
            var attestedLon = BitConverter.ToDouble(payload, 16);
            var distance = CalculateHaversineDistance(location.Latitude, location.Longitude, attestedLat, attestedLon);

            if (distance > 0.1) // Must match within 100 meters
            {
                throw new UnauthorizedAccessException(
                    "Key access denied: Attestation location does not match request location.");
            }

            // Verify signature using configured public key
            if (_config.AttestationPublicKey == null || _config.AttestationPublicKey.Length == 0)
            {
                throw new InvalidOperationException("Attestation verification configured but no public key provided.");
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(_config.AttestationPublicKey, out _);

            if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            {
                throw new UnauthorizedAccessException("Key access denied: Invalid attestation signature.");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Creates a signed location attestation for use in key access requests.
        /// This would typically be called on the client side with access to the signing key.
        /// </summary>
        public static byte[] CreateLocationAttestation(
            double latitude,
            double longitude,
            double accuracy,
            byte[] providerId,
            ECDsa signingKey)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(DateTime.UtcNow.Ticks);
            writer.Write(latitude);
            writer.Write(longitude);
            writer.Write(accuracy);

            // Pad or truncate provider ID to 32 bytes
            var providerBytes = new byte[32];
            Array.Copy(providerId, providerBytes, Math.Min(providerId.Length, 32));
            writer.Write(providerBytes);

            var payload = ms.ToArray();
            var signature = signingKey.SignData(payload, HashAlgorithmName.SHA256);

            var result = new byte[payload.Length + signature.Length];
            Array.Copy(payload, result, payload.Length);
            Array.Copy(signature, 0, result, payload.Length, signature.Length);

            return result;
        }

        /// <summary>
        /// Updates the geofence configuration for a specific key.
        /// </summary>
        public async Task UpdateGeofenceAsync(
            string keyId,
            double latitude,
            double longitude,
            double radiusKm,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Only system administrators can update geofences.");

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                keyData.CenterLatitude = latitude;
                keyData.CenterLongitude = longitude;
                keyData.RadiusKm = radiusKm;
                keyData.LastModifiedAt = DateTime.UtcNow;
                keyData.LastModifiedBy = context.UserId;

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Updates the time window configuration for a specific key.
        /// </summary>
        public async Task UpdateTimeWindowsAsync(
            string keyId,
            List<TimeWindow> timeWindows,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Only system administrators can update time windows.");

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                keyData.AllowedTimeWindows = timeWindows.ToList();
                keyData.LastModifiedAt = DateTime.UtcNow;
                keyData.LastModifiedBy = context.UserId;

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets the access log for a specific key.
        /// </summary>
        public async Task<IReadOnlyList<GeoAccessLogEntry>> GetAccessLogAsync(
            string keyId,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                return keyData.AccessLog.AsReadOnly();
            }
            finally
            {
                _lock.Release();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            await _lock.WaitAsync(ct);
            try { return _keys.Keys.ToList().AsReadOnly(); }
            finally { _lock.Release(); }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin) throw new UnauthorizedAccessException();

            await _lock.WaitAsync(ct);
            try
            {
                if (_keys.Remove(keyId)) await PersistKeysToStorage();
            }
            finally { _lock.Release(); }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(ct);
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData)) return null;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = keyData.CreatedAt,
                    CreatedBy = keyData.CreatedBy,
                    LastRotatedAt = keyData.LastModifiedAt,
                    KeySizeBytes = keyData.EncryptedKeyMaterial.Length,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Algorithm"] = "Geo-Locked Key",
                        ["CenterLatitude"] = keyData.CenterLatitude,
                        ["CenterLongitude"] = keyData.CenterLongitude,
                        ["RadiusKm"] = keyData.RadiusKm,
                        ["TimeWindows"] = keyData.AllowedTimeWindows.Count,
                        ["RequireAttestation"] = keyData.RequireAttestation,
                        ["AccessCount"] = keyData.AccessLog.Count
                    }
                };
            }
            finally { _lock.Release(); }
        }

        #region Storage

        private async Task LoadKeysFromStorage()
        {
            var path = GetStoragePath();
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, GeoLockedKeyDataSerialized>>(json);
                if (stored != null)
                {
                    foreach (var kvp in stored)
                        _keys[kvp.Key] = DeserializeKeyData(kvp.Value);
                    if (_keys.Count > 0)
                        _currentKeyId = _keys.Keys.First();
                }
            }
            catch { }
        }

        private async Task PersistKeysToStorage()
        {
            var path = GetStoragePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var toStore = _keys.ToDictionary(
                kvp => kvp.Key,
                kvp => SerializeKeyData(kvp.Value));

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "geolocked-keys.json");
        }

        private static GeoLockedKeyDataSerialized SerializeKeyData(GeoLockedKeyData data) => new()
        {
            KeyId = data.KeyId,
            EncryptedKeyMaterial = data.EncryptedKeyMaterial,
            CenterLatitude = data.CenterLatitude,
            CenterLongitude = data.CenterLongitude,
            RadiusKm = data.RadiusKm,
            AllowedTimeWindows = data.AllowedTimeWindows.Select(tw => new TimeWindowSerialized
            {
                StartTime = tw.StartTime.ToString(),
                EndTime = tw.EndTime.ToString(),
                TimeZoneId = tw.TimeZone?.Id ?? "UTC",
                AllowedDays = tw.AllowedDays.Select(d => (int)d).ToArray()
            }).ToList(),
            RequireAttestation = data.RequireAttestation,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy,
            LastModifiedAt = data.LastModifiedAt,
            LastModifiedBy = data.LastModifiedBy
        };

        private static GeoLockedKeyData DeserializeKeyData(GeoLockedKeyDataSerialized data) => new()
        {
            KeyId = data.KeyId,
            EncryptedKeyMaterial = data.EncryptedKeyMaterial ?? Array.Empty<byte>(),
            CenterLatitude = data.CenterLatitude,
            CenterLongitude = data.CenterLongitude,
            RadiusKm = data.RadiusKm,
            AllowedTimeWindows = data.AllowedTimeWindows?.Select(tw => new TimeWindow
            {
                StartTime = TimeSpan.Parse(tw.StartTime ?? "00:00"),
                EndTime = TimeSpan.Parse(tw.EndTime ?? "23:59"),
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById(tw.TimeZoneId ?? "UTC"),
                AllowedDays = tw.AllowedDays?.Select(d => (DayOfWeek)d).ToArray() ?? Array.Empty<DayOfWeek>()
            }).ToList() ?? new List<TimeWindow>(),
            RequireAttestation = data.RequireAttestation,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy,
            LastModifiedAt = data.LastModifiedAt,
            LastModifiedBy = data.LastModifiedBy
        };

        #endregion

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient?.Dispose();
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for geo-locked key access.
    /// </summary>
    public class GeoLockConfig
    {
        /// <summary>
        /// Center latitude of the geofence in degrees.
        /// </summary>
        public double CenterLatitude { get; set; }

        /// <summary>
        /// Center longitude of the geofence in degrees.
        /// </summary>
        public double CenterLongitude { get; set; }

        /// <summary>
        /// Radius of the geofence in kilometers.
        /// </summary>
        public double RadiusKm { get; set; } = 10.0;

        /// <summary>
        /// Whether to allow IP-based geolocation as a fallback.
        /// </summary>
        public bool AllowIpGeolocation { get; set; } = true;

        /// <summary>
        /// Whether to require cryptographically signed location attestations.
        /// </summary>
        public bool RequireLocationAttestation { get; set; }

        /// <summary>
        /// Public key for verifying location attestations (ECDSA P-256).
        /// </summary>
        public byte[]? AttestationPublicKey { get; set; }

        /// <summary>
        /// API key for IP geolocation service.
        /// </summary>
        public string? IpGeolocationApiKey { get; set; }

        /// <summary>
        /// Allowed time windows for key access.
        /// </summary>
        public List<TimeWindow> AllowedTimeWindows { get; set; } = new();

        /// <summary>
        /// Path to store encrypted key data.
        /// </summary>
        public string? StoragePath { get; set; }
    }

    /// <summary>
    /// Defines a time window during which key access is allowed.
    /// </summary>
    public class TimeWindow
    {
        /// <summary>
        /// Start time of the window (time of day).
        /// </summary>
        public TimeSpan StartTime { get; set; }

        /// <summary>
        /// End time of the window (time of day).
        /// </summary>
        public TimeSpan EndTime { get; set; }

        /// <summary>
        /// Timezone for the time window.
        /// </summary>
        public TimeZoneInfo? TimeZone { get; set; }

        /// <summary>
        /// Allowed days of the week. Empty means all days.
        /// </summary>
        public DayOfWeek[] AllowedDays { get; set; } = Array.Empty<DayOfWeek>();
    }

    internal class TimeWindowSerialized
    {
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? TimeZoneId { get; set; }
        public int[]? AllowedDays { get; set; }
    }

    /// <summary>
    /// Geographic location with source information.
    /// </summary>
    public struct GeoLocation
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public LocationSource Source { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Source of geographic location data.
    /// </summary>
    public enum LocationSource
    {
        GPS,
        IpGeolocation,
        ManualEntry,
        Attestation
    }

    /// <summary>
    /// Extended security context with geographic information.
    /// </summary>
    public interface IGeoSecurityContext : ISecurityContext
    {
        double Latitude { get; }
        double Longitude { get; }
        double? Accuracy { get; }
    }

    /// <summary>
    /// Extended security context with network information.
    /// </summary>
    public interface INetworkSecurityContext : ISecurityContext
    {
        string? ClientIpAddress { get; }
    }

    /// <summary>
    /// Extended security context with location attestation.
    /// </summary>
    public interface IAttestationSecurityContext : ISecurityContext
    {
        byte[]? LocationAttestation { get; }
    }

    /// <summary>
    /// Log entry for geo-locked key access attempts.
    /// </summary>
    public class GeoAccessLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string? UserId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double DistanceFromCenter { get; set; }
        public bool AccessGranted { get; set; }
        public string? DenialReason { get; set; }
    }

    internal class GeoLockedKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] EncryptedKeyMaterial { get; set; } = Array.Empty<byte>();
        public double CenterLatitude { get; set; }
        public double CenterLongitude { get; set; }
        public double RadiusKm { get; set; }
        public List<TimeWindow> AllowedTimeWindows { get; set; } = new();
        public bool RequireAttestation { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? LastModifiedAt { get; set; }
        public string? LastModifiedBy { get; set; }
        public List<GeoAccessLogEntry> AccessLog { get; set; } = new();
    }

    internal class GeoLockedKeyDataSerialized
    {
        public string KeyId { get; set; } = "";
        public byte[]? EncryptedKeyMaterial { get; set; }
        public double CenterLatitude { get; set; }
        public double CenterLongitude { get; set; }
        public double RadiusKm { get; set; }
        public List<TimeWindowSerialized>? AllowedTimeWindows { get; set; }
        public bool RequireAttestation { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? LastModifiedAt { get; set; }
        public string? LastModifiedBy { get; set; }
    }

    #endregion
}
