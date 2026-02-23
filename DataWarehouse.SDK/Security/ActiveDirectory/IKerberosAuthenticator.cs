using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Security.ActiveDirectory
{
    /// <summary>
    /// Encryption types supported by Kerberos ticket validation.
    /// Modern deployments should use AES variants; RC4-HMAC is legacy
    /// and should trigger deprecation warnings.
    /// </summary>
    public enum KerberosEncryptionType
    {
        /// <summary>
        /// AES-256-CTS-HMAC-SHA1-96 (etype 18) -- recommended default.
        /// </summary>
        Aes256CtsHmacSha1 = 18,

        /// <summary>
        /// AES-128-CTS-HMAC-SHA1-96 (etype 17) -- acceptable.
        /// </summary>
        Aes128CtsHmacSha1 = 17,

        /// <summary>
        /// RC4-HMAC (etype 23) -- legacy, vulnerable to pass-the-hash.
        /// Implementations should log a warning when this type is encountered.
        /// </summary>
        Rc4Hmac = 23
    }

    /// <summary>
    /// Represents a decoded Kerberos ticket with principal, lifetime, and encryption metadata.
    /// </summary>
    /// <param name="TokenData">Raw ticket bytes (AP-REQ or TGS-REP).</param>
    /// <param name="ClientPrincipal">The client's Kerberos principal (e.g., user@REALM).</param>
    /// <param name="ServicePrincipal">The target service principal (e.g., datawarehouse/host@REALM).</param>
    /// <param name="ValidUntil">Ticket expiration time from the KDC.</param>
    /// <param name="EncType">Encryption type used in the ticket.</param>
    public sealed record KerberosTicket(
        byte[] TokenData,
        string ClientPrincipal,
        string ServicePrincipal,
        DateTimeOffset ValidUntil,
        KerberosEncryptionType EncType);

    /// <summary>
    /// Result of a Kerberos ticket validation attempt. Contains the authenticated
    /// principal, group memberships (from PAC data), and any error information.
    /// </summary>
    /// <param name="IsValid">True if the ticket was successfully validated.</param>
    /// <param name="Principal">The authenticated Kerberos principal, or null on failure.</param>
    /// <param name="Groups">AD group memberships extracted from the Privilege Attribute Certificate (PAC), or null on failure.</param>
    /// <param name="ErrorMessage">Human-readable error description on failure, or null on success.</param>
    /// <param name="ExpiresAt">When the authenticated session expires, or null on failure.</param>
    public sealed record KerberosValidationResult(
        bool IsValid,
        string? Principal,
        string[]? Groups,
        string? ErrorMessage,
        DateTimeOffset? ExpiresAt);

    /// <summary>
    /// Configuration for the service principal used in Kerberos mutual authentication.
    /// The service principal identifies the DataWarehouse service to the KDC.
    /// </summary>
    /// <param name="ServiceName">Service name component of the SPN (default: "datawarehouse").</param>
    /// <param name="Realm">Kerberos realm (e.g., "CORP.EXAMPLE.COM"). Null to use system default.</param>
    /// <param name="KeytabPath">Path to the keytab file containing the service key. Null to use the system default keytab.</param>
    /// <param name="AllowDelegation">Whether the service is allowed to delegate credentials (constrained delegation).</param>
    public sealed record ServicePrincipalConfig(
        string ServiceName = "datawarehouse",
        string? Realm = null,
        string? KeytabPath = null,
        bool AllowDelegation = false);

    /// <summary>
    /// Contract for Kerberos authentication and ticket validation.
    /// Implementations handle the low-level ticket decryption and verification
    /// against the Key Distribution Center (KDC), extracting principal identity
    /// and AD group memberships from the Privilege Attribute Certificate (PAC).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface is consumed by <see cref="SpnegoNegotiator"/> to validate
    /// Kerberos tickets extracted from SPNEGO tokens during HTTP Negotiate auth.
    /// </para>
    /// <para>
    /// Implementations should support both Windows (SSPI) and cross-platform (GSSAPI)
    /// Kerberos stacks, selected at runtime based on the operating system.
    /// </para>
    /// </remarks>
    public interface IKerberosAuthenticator
    {
        /// <summary>
        /// Validates a Kerberos AP-REQ ticket, verifying the service principal,
        /// ticket integrity, and extracting client identity and group memberships.
        /// </summary>
        /// <param name="ticket">Raw Kerberos AP-REQ ticket bytes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result containing principal, groups, and expiry on success.</returns>
        ValueTask<KerberosValidationResult> ValidateTicketAsync(byte[] ticket, CancellationToken ct = default);

        /// <summary>
        /// Obtains a service token for mutual authentication (AP-REP).
        /// The returned token proves the server's identity to the client.
        /// </summary>
        /// <param name="config">Service principal configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>AP-REP token bytes for the mutual authentication response.</returns>
        ValueTask<byte[]> GetServiceTokenAsync(ServicePrincipalConfig config, CancellationToken ct = default);

        /// <summary>
        /// Indicates whether the Kerberos infrastructure (KDC) is reachable
        /// and the service keytab is available. Returns false if Kerberos
        /// authentication cannot proceed (e.g., no domain membership, no keytab).
        /// </summary>
        bool IsAvailable { get; }
    }
}
