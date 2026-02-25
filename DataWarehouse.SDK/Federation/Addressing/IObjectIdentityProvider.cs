using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Federation.Addressing;

/// <summary>
/// Provides UUID generation and validation for location-independent object identity.
/// </summary>
/// <remarks>
/// <para>
/// Object identity providers generate globally unique identifiers (UUIDs) that remain
/// constant across object replication, migration, and federation. The same UUID always
/// refers to the same logical object, regardless of which storage node holds a copy.
/// </para>
/// <para>
/// <strong>UUID v7 Format:</strong> Implementations should use UUID v7 (RFC 9562) which
/// embeds a 48-bit millisecond timestamp in the most significant bits. This provides:
/// <list type="bullet">
///   <item><description>Time-ordered sorting: earlier UUIDs sort before later UUIDs</description></item>
///   <item><description>Natural time-range queries without secondary indexes</description></item>
///   <item><description>B-tree index efficiency: sequential inserts without page splits</description></item>
///   <item><description>Global uniqueness without coordination: timestamp + random bits</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: UUID-based object identity (FOS-02)")]
public interface IObjectIdentityProvider
{
    /// <summary>
    /// Generates a new globally unique object identity.
    /// </summary>
    /// <remarks>
    /// Implementations should use UUID v7 for time-ordering properties. The generated
    /// identity is guaranteed to be unique across all nodes and time periods without
    /// requiring coordination or central allocation.
    /// </remarks>
    /// <returns>A new unique object identity.</returns>
    ObjectIdentity Generate();

    /// <summary>
    /// Parses an object identity from its string representation.
    /// </summary>
    /// <param name="value">The string representation of the UUID (standard format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).</param>
    /// <param name="identity">When this method returns, contains the parsed identity if parsing succeeded; otherwise <see cref="ObjectIdentity.Empty"/>.</param>
    /// <returns><c>true</c> if the string was successfully parsed; otherwise <c>false</c>.</returns>
    bool TryParse(string value, out ObjectIdentity identity);

    /// <summary>
    /// Validates that an object identity is well-formed and meets version requirements.
    /// </summary>
    /// <param name="identity">The identity to validate.</param>
    /// <returns>
    /// <c>true</c> if the identity is well-formed (non-empty, correct version and variant bits);
    /// otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// For UUID v7 implementations, this checks:
    /// <list type="bullet">
    ///   <item><description>Identity is not empty</description></item>
    ///   <item><description>Version field = 7 (0111 in bits 48-51)</description></item>
    ///   <item><description>Variant field = 2 (10xx in bits 64-65, RFC 4122 variant)</description></item>
    /// </list>
    /// </remarks>
    bool IsValid(ObjectIdentity identity);
}
