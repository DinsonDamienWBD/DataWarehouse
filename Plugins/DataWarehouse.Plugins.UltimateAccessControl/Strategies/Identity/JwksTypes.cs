using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// JWKS (JSON Web Key Set) response.
    /// Shared type for OAuth2 and OIDC strategies.
    /// </summary>
    internal sealed class JwksResponse
    {
        public List<JwkKey> Keys { get; set; } = new();
    }

    /// <summary>
    /// JWK (JSON Web Key) structure.
    /// Shared type for OAuth2 and OIDC strategies.
    /// </summary>
    internal sealed class JwkKey
    {
        public string? Kty { get; set; } // Key type: RSA, EC
        public string? Use { get; set; } // sig, enc
        public string? Kid { get; set; } // Key ID
        public string? Alg { get; set; } // Algorithm: RS256, ES256, etc.

        // RSA public key parameters
        public string? N { get; set; } // Modulus
        public string? E { get; set; } // Exponent

        // EC public key parameters
        public string? Crv { get; set; } // Curve: P-256, P-384, P-521
        public string? X { get; set; } // X coordinate
        public string? Y { get; set; } // Y coordinate
    }

    /// <summary>
    /// Cached JWKS information.
    /// Shared type for OAuth2 and OIDC strategies.
    /// </summary>
    internal sealed class CachedJwks
    {
        public required JwksResponse Jwks { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }
}
