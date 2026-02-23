using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.Security.ActiveDirectory
{
    /// <summary>
    /// Tracks the state of a multi-leg SPNEGO negotiation.
    /// </summary>
    public enum NegotiationState
    {
        /// <summary>No tokens exchanged yet.</summary>
        Initial,

        /// <summary>Server has issued a challenge token; awaiting client response.</summary>
        ChallengeIssued,

        /// <summary>Authentication completed successfully.</summary>
        Authenticated,

        /// <summary>Authentication failed (invalid token, unsupported mechanism, etc.).</summary>
        Failed
    }

    /// <summary>
    /// Response from a single SPNEGO negotiation step.
    /// </summary>
    /// <param name="ResponseToken">SPNEGO response token to send back to the client, or null if no response is needed.</param>
    /// <param name="State">Current negotiation state after processing.</param>
    /// <param name="Result">Kerberos validation result when authentication completes, or null if still negotiating.</param>
    public sealed record SpnegoResponse(
        byte[]? ResponseToken,
        NegotiationState State,
        KerberosValidationResult? Result);

    /// <summary>
    /// Tracks the state of an individual SPNEGO negotiation session.
    /// Each client connection should have its own <see cref="SpnegoContext"/>
    /// to support multi-leg negotiation.
    /// </summary>
    public sealed class SpnegoContext
    {
        /// <summary>
        /// Current state of the negotiation.
        /// </summary>
        public NegotiationState State { get; internal set; } = NegotiationState.Initial;

        /// <summary>
        /// Session key established during authentication, available after
        /// <see cref="NegotiationState.Authenticated"/> state is reached.
        /// </summary>
        public byte[]? SessionKey { get; internal set; }

        /// <summary>
        /// The authenticated principal after successful negotiation.
        /// </summary>
        public string? AuthenticatedPrincipal { get; internal set; }

        /// <summary>
        /// Number of negotiation legs processed.
        /// </summary>
        public int LegCount { get; internal set; }
    }

    /// <summary>
    /// Handles SPNEGO (RFC 4178) token exchange for HTTP Negotiate authentication.
    /// Parses GSS-API headers, extracts Kerberos AP-REQ tokens, and delegates
    /// validation to <see cref="IKerberosAuthenticator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SPNEGO (Simple and Protected GSSAPI Negotiation Mechanism) wraps Kerberos
    /// tokens in a mechanism-negotiation envelope. This class handles the envelope
    /// parsing and delegates the actual Kerberos validation to the injected authenticator.
    /// </para>
    /// <para>
    /// Usage: For each incoming HTTP request with an "Authorization: Negotiate" header,
    /// create a <see cref="SpnegoContext"/> and call <see cref="ProcessTokenAsync"/>
    /// with the Base64-decoded token. If the response state is <see cref="NegotiationState.ChallengeIssued"/>,
    /// send the response token back in a "WWW-Authenticate: Negotiate {token}" header.
    /// </para>
    /// </remarks>
    public sealed class SpnegoNegotiator
    {
        // GSS-API OIDs
        private static readonly byte[] SpnegoOid = { 0x2b, 0x06, 0x01, 0x05, 0x05, 0x02 }; // 1.3.6.1.5.5.2
        private static readonly byte[] KerberosOid = { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02 }; // 1.2.840.113554.1.2.2

        private readonly IKerberosAuthenticator _authenticator;
        private readonly ILogger _logger;

        /// <summary>
        /// Whether to reject tickets encrypted with legacy RC4-HMAC.
        /// When false (default), RC4-HMAC tickets are processed but a warning is logged.
        /// When true, RC4-HMAC tickets are rejected with an error.
        /// </summary>
        public bool RejectLegacyEncryption { get; set; }

        /// <summary>
        /// Creates a new SPNEGO negotiator.
        /// </summary>
        /// <param name="authenticator">Kerberos authenticator for ticket validation.</param>
        /// <param name="logger">Optional logger for diagnostic output.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="authenticator"/> is null.</exception>
        public SpnegoNegotiator(IKerberosAuthenticator authenticator, ILogger? logger = null)
        {
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Processes an incoming SPNEGO token and advances the negotiation state.
        /// </summary>
        /// <param name="incomingToken">The raw SPNEGO token from the client (Base64-decoded from the Authorization header).</param>
        /// <param name="context">The negotiation context for this session. Must not be null.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// An <see cref="SpnegoResponse"/> containing:
        /// <list type="bullet">
        /// <item><description>A response token to send to the client (if multi-leg negotiation)</description></item>
        /// <item><description>The updated negotiation state</description></item>
        /// <item><description>The Kerberos validation result (when authentication completes)</description></item>
        /// </list>
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="incomingToken"/> or <paramref name="context"/> is null.</exception>
        public async ValueTask<SpnegoResponse> ProcessTokenAsync(
            byte[] incomingToken,
            SpnegoContext context,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(incomingToken);
            ArgumentNullException.ThrowIfNull(context);

            context.LegCount++;

            try
            {
                // Step 1: Parse GSS-API / SPNEGO wrapper
                var kerberosToken = ExtractKerberosToken(incomingToken);
                if (kerberosToken is null)
                {
                    _logger.LogWarning("Failed to extract Kerberos token from SPNEGO wrapper");
                    context.State = NegotiationState.Failed;
                    return new SpnegoResponse(
                        null,
                        NegotiationState.Failed,
                        new KerberosValidationResult(false, null, null, "Invalid SPNEGO token: could not extract Kerberos mechanism token", null));
                }

                // Step 2: Detect and handle legacy encryption
                if (DetectRc4Hmac(kerberosToken))
                {
                    if (RejectLegacyEncryption)
                    {
                        _logger.LogWarning("Rejected RC4-HMAC encrypted Kerberos ticket (legacy encryption disallowed)");
                        context.State = NegotiationState.Failed;
                        return new SpnegoResponse(
                            null,
                            NegotiationState.Failed,
                            new KerberosValidationResult(false, null, null, "RC4-HMAC encryption rejected: upgrade to AES encryption", null));
                    }

                    _logger.LogWarning("Processing RC4-HMAC encrypted Kerberos ticket -- weak encryption, consider upgrading to AES");
                }

                // Step 3: Validate Kerberos ticket via IKerberosAuthenticator
                var result = await _authenticator.ValidateTicketAsync(kerberosToken, ct).ConfigureAwait(false);

                if (result.IsValid)
                {
                    context.State = NegotiationState.Authenticated;
                    context.AuthenticatedPrincipal = result.Principal;
                    _logger.LogInformation("SPNEGO authentication succeeded for principal {Principal}", result.Principal);

                    // Build mutual-auth response token (SPNEGO accept-completed)
                    var responseToken = BuildSpnegoAcceptResponse();
                    return new SpnegoResponse(responseToken, NegotiationState.Authenticated, result);
                }

                // Authentication failed
                _logger.LogWarning("Kerberos ticket validation failed: {Error}", result.ErrorMessage);
                context.State = NegotiationState.Failed;
                return new SpnegoResponse(null, NegotiationState.Failed, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SPNEGO negotiation failed with unexpected error");
                context.State = NegotiationState.Failed;
                return new SpnegoResponse(
                    null,
                    NegotiationState.Failed,
                    new KerberosValidationResult(false, null, null, $"SPNEGO negotiation error: {ex.Message}", null));
            }
        }

        /// <summary>
        /// Extracts the Kerberos mechanism token from a GSS-API/SPNEGO wrapper.
        /// Handles both raw Kerberos tokens and SPNEGO-wrapped tokens.
        /// </summary>
        /// <param name="token">The incoming GSS-API token.</param>
        /// <returns>The extracted Kerberos AP-REQ token, or null if parsing fails.</returns>
        private byte[]? ExtractKerberosToken(byte[] token)
        {
            if (token.Length < 2)
                return null;

            try
            {
                var offset = 0;

                // GSS-API tokens start with 0x60 (APPLICATION [0] CONSTRUCTED)
                if (token[offset] == 0x60)
                {
                    offset++;
                    var totalLength = ReadAsn1Length(token, ref offset);
                    if (totalLength < 0 || offset + totalLength > token.Length)
                        return null;

                    // OID tag (0x06)
                    if (offset < token.Length && token[offset] == 0x06)
                    {
                        offset++;
                        var oidLength = ReadAsn1Length(token, ref offset);
                        if (oidLength < 0 || offset + oidLength > token.Length)
                            return null;

                        var oid = token.AsSpan(offset, oidLength);
                        offset += oidLength;

                        // Check for SPNEGO OID (1.3.6.1.5.5.2)
                        if (oid.SequenceEqual(SpnegoOid))
                        {
                            return ExtractKerberosFromSpnego(token, offset);
                        }

                        // Check for raw Kerberos OID (1.2.840.113554.1.2.2)
                        if (oid.SequenceEqual(KerberosOid))
                        {
                            // Remaining bytes are the Kerberos token
                            var remaining = token.Length - offset;
                            if (remaining > 0)
                            {
                                var kerberosToken = new byte[remaining];
                                Buffer.BlockCopy(token, offset, kerberosToken, 0, remaining);
                                return kerberosToken;
                            }
                        }
                    }
                }

                // If not GSS-API wrapped, treat the entire token as a raw Kerberos token
                // (some clients send unwrapped AP-REQ directly)
                if (token.Length > 10)
                    return token;

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts Kerberos token from within an SPNEGO NegTokenInit structure.
        /// </summary>
        private byte[]? ExtractKerberosFromSpnego(byte[] token, int offset)
        {
            // SPNEGO wraps in a NegTokenInit or NegTokenResp (SEQUENCE)
            // We need to find the mechToken field containing the Kerberos AP-REQ

            // Skip through SPNEGO ASN.1 structure to find the mechanism token
            // NegTokenInit ::= SEQUENCE {
            //   mechTypes [0] MechTypeList,
            //   reqFlags  [1] ContextFlags OPTIONAL,
            //   mechToken [2] OCTET STRING OPTIONAL,
            //   ...
            // }

            if (offset >= token.Length)
                return null;

            // Expect context-specific [0] CONSTRUCTED for NegTokenInit
            if (token[offset] == 0xA0)
            {
                offset++;
                var innerLen = ReadAsn1Length(token, ref offset);
                if (innerLen < 0)
                    return null;

                // Inside is a SEQUENCE (0x30)
                if (offset < token.Length && token[offset] == 0x30)
                {
                    offset++;
                    var seqLen = ReadAsn1Length(token, ref offset);
                    if (seqLen < 0)
                        return null;

                    var seqEnd = offset + seqLen;

                    // Walk through the tagged fields looking for [2] mechToken
                    while (offset < seqEnd && offset < token.Length)
                    {
                        var tag = token[offset];
                        offset++;
                        var fieldLen = ReadAsn1Length(token, ref offset);
                        if (fieldLen < 0)
                            return null;

                        if (tag == 0xA2) // [2] = mechToken
                        {
                            // Expect OCTET STRING (0x04)
                            if (offset < token.Length && token[offset] == 0x04)
                            {
                                offset++;
                                var tokenLen = ReadAsn1Length(token, ref offset);
                                if (tokenLen > 0 && offset + tokenLen <= token.Length)
                                {
                                    var kerberosToken = new byte[tokenLen];
                                    Buffer.BlockCopy(token, offset, kerberosToken, 0, tokenLen);
                                    return kerberosToken;
                                }
                            }
                            return null;
                        }

                        // Skip this field
                        offset += fieldLen;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Heuristic detection of RC4-HMAC encrypted tickets.
        /// In Kerberos AP-REQ, the encryption type is encoded in the EncryptedData
        /// structure. RC4-HMAC uses etype 23.
        /// </summary>
        private static bool DetectRc4Hmac(byte[] kerberosToken)
        {
            // Kerberos AP-REQ contains an Authenticator encrypted with the session key.
            // The EncryptedData structure includes an etype field.
            // We scan for the etype value 23 (RC4-HMAC) in the ASN.1 structure.
            // This is a heuristic -- full ASN.1 parsing would be more precise but
            // the authenticator handles the definitive check.

            if (kerberosToken.Length < 20)
                return false;

            // Look for etype field pattern: context tag [0] INTEGER 23
            for (var i = 0; i < kerberosToken.Length - 3; i++)
            {
                // Context-specific [0] CONSTRUCTED, length, INTEGER tag, length 1, value 23
                if (kerberosToken[i] == 0xA0 &&
                    i + 4 < kerberosToken.Length &&
                    kerberosToken[i + 2] == 0x02 &&
                    kerberosToken[i + 3] == 0x01 &&
                    kerberosToken[i + 4] == (byte)KerberosEncryptionType.Rc4Hmac)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds a minimal SPNEGO NegTokenResp with accept-completed status.
        /// </summary>
        private static byte[] BuildSpnegoAcceptResponse()
        {
            // NegTokenResp with accept-completed (0x00)
            // negState [0] ENUMERATED { accept-completed (0) }
            return
            [
                0xA1, 0x07,             // [1] CONSTRUCTED (NegTokenResp)
                0x30, 0x05,             // SEQUENCE
                0xA0, 0x03,             // [0] negState
                0x0A, 0x01, 0x00        // ENUMERATED accept-completed
            ];
        }

        /// <summary>
        /// Reads a DER-encoded ASN.1 length at the specified offset.
        /// Advances the offset past the length bytes.
        /// </summary>
        /// <returns>The decoded length, or -1 if the encoding is invalid.</returns>
        private static int ReadAsn1Length(byte[] data, ref int offset)
        {
            if (offset >= data.Length)
                return -1;

            var firstByte = data[offset++];

            // Short form: length fits in 7 bits
            if ((firstByte & 0x80) == 0)
                return firstByte;

            // Long form: first byte indicates number of subsequent length bytes
            var numBytes = firstByte & 0x7F;
            if (numBytes == 0 || numBytes > 4 || offset + numBytes > data.Length)
                return -1;

            var length = 0;
            for (var i = 0; i < numBytes; i++)
            {
                length = (length << 8) | data[offset++];
            }

            return length;
        }
    }
}
