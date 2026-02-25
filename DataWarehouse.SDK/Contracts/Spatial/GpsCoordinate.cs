namespace DataWarehouse.SDK.Contracts.Spatial;

/// <summary>
/// Represents a GPS coordinate with latitude, longitude, and optional altitude.
/// </summary>
/// <remarks>
/// This struct provides GPS coordinate representation with Haversine distance calculation
/// for accurate geographic distance computation on Earth's surface.
/// </remarks>
public readonly struct GpsCoordinate : IEquatable<GpsCoordinate>
{
    /// <summary>
    /// Gets the latitude in degrees (range: -90 to 90).
    /// </summary>
    public double Latitude { get; }

    /// <summary>
    /// Gets the longitude in degrees (range: -180 to 180).
    /// </summary>
    public double Longitude { get; }

    /// <summary>
    /// Gets the optional altitude in meters above sea level.
    /// </summary>
    public double? Altitude { get; }

    /// <summary>
    /// Initializes a new GPS coordinate.
    /// </summary>
    /// <param name="latitude">Latitude in degrees (-90 to 90).</param>
    /// <param name="longitude">Longitude in degrees (-180 to 180).</param>
    /// <param name="altitude">Optional altitude in meters.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when latitude or longitude is out of valid range.</exception>
    public GpsCoordinate(double latitude, double longitude, double? altitude = null)
    {
        if (latitude < -90.0 || latitude > 90.0)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be between -90 and 90 degrees.");
        if (longitude < -180.0 || longitude > 180.0)
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be between -180 and 180 degrees.");

        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
    }

    /// <summary>
    /// Calculates the Haversine distance (great-circle distance) to another GPS coordinate.
    /// </summary>
    /// <param name="other">The target GPS coordinate.</param>
    /// <returns>Distance in meters.</returns>
    /// <remarks>
    /// Uses the Haversine formula to calculate the shortest distance over the Earth's surface,
    /// assuming a spherical Earth with mean radius of 6,371,000 meters.
    /// Accuracy is typically within 0.5% for most distances.
    /// </remarks>
    public double HaversineDistanceTo(GpsCoordinate other)
    {
        const double EarthRadiusMeters = 6_371_000.0;

        var lat1Rad = Latitude * Math.PI / 180.0;
        var lat2Rad = other.Latitude * Math.PI / 180.0;
        var dLatRad = (other.Latitude - Latitude) * Math.PI / 180.0;
        var dLonRad = (other.Longitude - Longitude) * Math.PI / 180.0;

        var a = Math.Sin(dLatRad / 2) * Math.Sin(dLatRad / 2) +
                Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                Math.Sin(dLonRad / 2) * Math.Sin(dLonRad / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    /// <inheritdoc/>
    public bool Equals(GpsCoordinate other) =>
        Latitude == other.Latitude &&
        Longitude == other.Longitude &&
        Altitude == other.Altitude;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GpsCoordinate other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Latitude, Longitude, Altitude);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(GpsCoordinate left, GpsCoordinate right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(GpsCoordinate left, GpsCoordinate right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        Altitude.HasValue
            ? $"GPS({Latitude:F6}, {Longitude:F6}, {Altitude.Value:F1}m)"
            : $"GPS({Latitude:F6}, {Longitude:F6})";
}
