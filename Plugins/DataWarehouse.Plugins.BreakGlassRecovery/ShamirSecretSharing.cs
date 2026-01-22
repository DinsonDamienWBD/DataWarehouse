using System.Security.Cryptography;

namespace DataWarehouse.Plugins.BreakGlassRecovery;

/// <summary>
/// Production-ready implementation of Shamir's Secret Sharing Scheme.
/// Provides cryptographic key splitting and reconstruction with configurable thresholds.
/// Operates over GF(256) for byte-level secret sharing.
/// </summary>
public sealed class ShamirSecretSharing
{
    /// <summary>
    /// Galois Field GF(2^8) with irreducible polynomial x^8 + x^4 + x^3 + x + 1 (0x11B).
    /// Used for AES-compatible finite field arithmetic.
    /// </summary>
    private static readonly byte[] ExpTable = new byte[512];
    private static readonly byte[] LogTable = new byte[256];

    static ShamirSecretSharing()
    {
        InitializeGaloisFieldTables();
    }

    /// <summary>
    /// Initializes the exponential and logarithm tables for GF(256) arithmetic.
    /// Uses the same field as AES for compatibility.
    /// </summary>
    private static void InitializeGaloisFieldTables()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            ExpTable[i] = (byte)x;
            ExpTable[i + 255] = (byte)x;
            LogTable[x] = (byte)i;
            x = (x << 1) ^ x;
            if ((x & 0x100) != 0)
            {
                x ^= 0x11B;
            }
        }
        LogTable[0] = 0;
    }

    /// <summary>
    /// Multiplies two elements in GF(256).
    /// </summary>
    private static byte GfMultiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return ExpTable[LogTable[a] + LogTable[b]];
    }

    /// <summary>
    /// Computes the multiplicative inverse in GF(256).
    /// </summary>
    private static byte GfInverse(byte a)
    {
        if (a == 0) throw new DivideByZeroException("Cannot compute inverse of zero in GF(256)");
        return ExpTable[255 - LogTable[a]];
    }

    /// <summary>
    /// Adds two elements in GF(256) (XOR operation).
    /// </summary>
    private static byte GfAdd(byte a, byte b) => (byte)(a ^ b);

    /// <summary>
    /// Splits a secret into n shares requiring k shares for reconstruction.
    /// Uses cryptographically secure random polynomial coefficients.
    /// </summary>
    /// <param name="secret">The secret byte array to split.</param>
    /// <param name="totalShares">Total number of shares to generate (n).</param>
    /// <param name="threshold">Minimum shares required for reconstruction (k).</param>
    /// <returns>Array of secret shares.</returns>
    /// <exception cref="ArgumentException">If parameters are invalid.</exception>
    public static SecretShare[] SplitSecret(byte[] secret, int totalShares, int threshold)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
            throw new ArgumentException("Secret cannot be empty", nameof(secret));

        if (totalShares < 2 || totalShares > 255)
            throw new ArgumentException("Total shares must be between 2 and 255", nameof(totalShares));

        if (threshold < 2 || threshold > totalShares)
            throw new ArgumentException("Threshold must be between 2 and total shares", nameof(threshold));

        var shares = new SecretShare[totalShares];
        for (int i = 0; i < totalShares; i++)
        {
            shares[i] = new SecretShare
            {
                ShareIndex = (byte)(i + 1),
                Threshold = threshold,
                TotalShares = totalShares,
                Data = new byte[secret.Length],
                CreatedAt = DateTime.UtcNow,
                ShareId = Guid.NewGuid().ToString("N")
            };
        }

        using var rng = RandomNumberGenerator.Create();
        var coefficients = new byte[threshold];

        for (int byteIndex = 0; byteIndex < secret.Length; byteIndex++)
        {
            coefficients[0] = secret[byteIndex];
            rng.GetBytes(coefficients, 1, threshold - 1);

            for (int shareIndex = 0; shareIndex < totalShares; shareIndex++)
            {
                byte x = (byte)(shareIndex + 1);
                byte y = EvaluatePolynomial(coefficients, x);
                shares[shareIndex].Data[byteIndex] = y;
            }
        }

        CryptographicOperations.ZeroMemory(coefficients);
        return shares;
    }

    /// <summary>
    /// Evaluates a polynomial at point x using Horner's method in GF(256).
    /// </summary>
    private static byte EvaluatePolynomial(byte[] coefficients, byte x)
    {
        byte result = 0;
        for (int i = coefficients.Length - 1; i >= 0; i--)
        {
            result = GfAdd(GfMultiply(result, x), coefficients[i]);
        }
        return result;
    }

    /// <summary>
    /// Reconstructs the secret from a subset of shares using Lagrange interpolation.
    /// </summary>
    /// <param name="shares">Array of shares (must meet threshold requirement).</param>
    /// <returns>The reconstructed secret.</returns>
    /// <exception cref="ArgumentException">If insufficient shares provided.</exception>
    public static byte[] ReconstructSecret(SecretShare[] shares)
    {
        ArgumentNullException.ThrowIfNull(shares);

        if (shares.Length == 0)
            throw new ArgumentException("No shares provided", nameof(shares));

        var validShares = shares.Where(s => s != null && s.Data != null && s.Data.Length > 0).ToArray();

        if (validShares.Length == 0)
            throw new ArgumentException("No valid shares provided", nameof(shares));

        int threshold = validShares[0].Threshold;
        if (validShares.Length < threshold)
            throw new ArgumentException($"Insufficient shares: {validShares.Length} provided, {threshold} required");

        var shareIndices = new HashSet<byte>();
        foreach (var share in validShares)
        {
            if (!shareIndices.Add(share.ShareIndex))
                throw new ArgumentException($"Duplicate share index: {share.ShareIndex}");
        }

        int secretLength = validShares[0].Data.Length;
        foreach (var share in validShares)
        {
            if (share.Data.Length != secretLength)
                throw new ArgumentException("All shares must have the same length");
        }

        var usedShares = validShares.Take(threshold).ToArray();
        var secret = new byte[secretLength];
        var xValues = usedShares.Select(s => s.ShareIndex).ToArray();

        for (int byteIndex = 0; byteIndex < secretLength; byteIndex++)
        {
            var yValues = usedShares.Select(s => s.Data[byteIndex]).ToArray();
            secret[byteIndex] = LagrangeInterpolate(xValues, yValues, 0);
        }

        return secret;
    }

    /// <summary>
    /// Performs Lagrange interpolation at point x in GF(256).
    /// </summary>
    private static byte LagrangeInterpolate(byte[] xValues, byte[] yValues, byte x)
    {
        byte result = 0;
        int k = xValues.Length;

        for (int i = 0; i < k; i++)
        {
            byte numerator = 1;
            byte denominator = 1;

            for (int j = 0; j < k; j++)
            {
                if (i == j) continue;

                numerator = GfMultiply(numerator, GfAdd(x, xValues[j]));
                denominator = GfMultiply(denominator, GfAdd(xValues[i], xValues[j]));
            }

            byte lagrangeBasis = GfMultiply(numerator, GfInverse(denominator));
            result = GfAdd(result, GfMultiply(yValues[i], lagrangeBasis));
        }

        return result;
    }

    /// <summary>
    /// Verifies that a set of shares can reconstruct a secret with the expected hash.
    /// </summary>
    /// <param name="shares">The shares to verify.</param>
    /// <param name="expectedHash">The expected SHA-256 hash of the secret.</param>
    /// <returns>True if the shares reconstruct to a secret with the expected hash.</returns>
    public static bool VerifyShares(SecretShare[] shares, byte[] expectedHash)
    {
        try
        {
            var secret = ReconstructSecret(shares);
            var actualHash = SHA256.HashData(secret);
            CryptographicOperations.ZeroMemory(secret);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a new share configuration with the specified parameters.
    /// </summary>
    public static ShareConfiguration CreateConfiguration(int totalShares, int threshold, string description)
    {
        return new ShareConfiguration
        {
            ConfigurationId = Guid.NewGuid().ToString("N"),
            TotalShares = totalShares,
            Threshold = threshold,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Represents a single share of a split secret.
/// </summary>
public sealed class SecretShare
{
    /// <summary>
    /// Unique identifier for this share.
    /// </summary>
    public string ShareId { get; init; } = string.Empty;

    /// <summary>
    /// The X-coordinate index (1-255) for this share.
    /// </summary>
    public byte ShareIndex { get; init; }

    /// <summary>
    /// Minimum number of shares required for reconstruction.
    /// </summary>
    public int Threshold { get; init; }

    /// <summary>
    /// Total number of shares created.
    /// </summary>
    public int TotalShares { get; init; }

    /// <summary>
    /// The share data (Y-values for each byte of the secret).
    /// </summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// When this share was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Optional custodian identifier for this share.
    /// </summary>
    public string? CustodianId { get; set; }

    /// <summary>
    /// Optional label for organizational purposes.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// SHA-256 hash of the original secret (for verification).
    /// </summary>
    public byte[]? SecretHash { get; set; }

    /// <summary>
    /// Creates a secure, serializable representation of this share.
    /// </summary>
    public SerializableShare ToSerializable()
    {
        return new SerializableShare
        {
            ShareId = ShareId,
            ShareIndex = ShareIndex,
            Threshold = Threshold,
            TotalShares = TotalShares,
            DataBase64 = Convert.ToBase64String(Data),
            CreatedAt = CreatedAt,
            CustodianId = CustodianId,
            Label = Label,
            SecretHashBase64 = SecretHash != null ? Convert.ToBase64String(SecretHash) : null
        };
    }

    /// <summary>
    /// Creates a SecretShare from a serialized representation.
    /// </summary>
    public static SecretShare FromSerializable(SerializableShare serializable)
    {
        return new SecretShare
        {
            ShareId = serializable.ShareId,
            ShareIndex = serializable.ShareIndex,
            Threshold = serializable.Threshold,
            TotalShares = serializable.TotalShares,
            Data = Convert.FromBase64String(serializable.DataBase64),
            CreatedAt = serializable.CreatedAt,
            CustodianId = serializable.CustodianId,
            Label = serializable.Label,
            SecretHash = serializable.SecretHashBase64 != null
                ? Convert.FromBase64String(serializable.SecretHashBase64)
                : null
        };
    }
}

/// <summary>
/// Serializable representation of a secret share for storage and transmission.
/// </summary>
public sealed class SerializableShare
{
    public string ShareId { get; init; } = string.Empty;
    public byte ShareIndex { get; init; }
    public int Threshold { get; init; }
    public int TotalShares { get; init; }
    public string DataBase64 { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string? CustodianId { get; init; }
    public string? Label { get; init; }
    public string? SecretHashBase64 { get; init; }
}

/// <summary>
/// Configuration for a share distribution scheme.
/// </summary>
public sealed class ShareConfiguration
{
    /// <summary>
    /// Unique identifier for this configuration.
    /// </summary>
    public string ConfigurationId { get; init; } = string.Empty;

    /// <summary>
    /// Total number of shares to create.
    /// </summary>
    public int TotalShares { get; init; }

    /// <summary>
    /// Minimum shares required for reconstruction.
    /// </summary>
    public int Threshold { get; init; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// When this configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// List of custodian assignments.
    /// </summary>
    public List<CustodianAssignment> Custodians { get; init; } = new();
}

/// <summary>
/// Assignment of a share to a custodian.
/// </summary>
public sealed class CustodianAssignment
{
    /// <summary>
    /// Share index assigned to this custodian.
    /// </summary>
    public byte ShareIndex { get; init; }

    /// <summary>
    /// Custodian identifier.
    /// </summary>
    public string CustodianId { get; init; } = string.Empty;

    /// <summary>
    /// Custodian name.
    /// </summary>
    public string CustodianName { get; init; } = string.Empty;

    /// <summary>
    /// Contact information for notification.
    /// </summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// Role of the custodian (e.g., "Primary", "Backup", "Executive").
    /// </summary>
    public string Role { get; init; } = "Standard";

    /// <summary>
    /// When the share was distributed to this custodian.
    /// </summary>
    public DateTime? DistributedAt { get; init; }

    /// <summary>
    /// Acknowledgment status.
    /// </summary>
    public bool Acknowledged { get; init; }

    /// <summary>
    /// When the custodian acknowledged receipt.
    /// </summary>
    public DateTime? AcknowledgedAt { get; init; }
}

/// <summary>
/// Key escrow entry for encrypted backup recovery.
/// </summary>
public sealed class KeyEscrowEntry
{
    /// <summary>
    /// Unique identifier for this escrow entry.
    /// </summary>
    public string EscrowId { get; init; } = string.Empty;

    /// <summary>
    /// The key identifier this escrow protects.
    /// </summary>
    public string KeyId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the original key for verification.
    /// </summary>
    public byte[] KeyHash { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Share configuration used for this escrow.
    /// </summary>
    public ShareConfiguration Configuration { get; init; } = new();

    /// <summary>
    /// The generated shares (stored securely or distributed).
    /// </summary>
    public List<SecretShare> Shares { get; init; } = new();

    /// <summary>
    /// When this escrow was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Who created this escrow entry.
    /// </summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>
    /// Current status of the escrow.
    /// </summary>
    public EscrowStatus Status { get; init; } = EscrowStatus.Active;

    /// <summary>
    /// When the escrow expires (if applicable).
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Recovery attempts made against this escrow.
    /// </summary>
    public List<RecoveryAttempt> RecoveryAttempts { get; init; } = new();
}

/// <summary>
/// Status of a key escrow entry.
/// </summary>
public enum EscrowStatus
{
    /// <summary>
    /// Escrow is active and can be used for recovery.
    /// </summary>
    Active,

    /// <summary>
    /// Escrow has been used for recovery.
    /// </summary>
    Recovered,

    /// <summary>
    /// Escrow has expired.
    /// </summary>
    Expired,

    /// <summary>
    /// Escrow has been revoked.
    /// </summary>
    Revoked,

    /// <summary>
    /// Escrow is pending distribution of shares.
    /// </summary>
    PendingDistribution
}

/// <summary>
/// Record of a recovery attempt against an escrow.
/// </summary>
public sealed class RecoveryAttempt
{
    /// <summary>
    /// Unique identifier for this attempt.
    /// </summary>
    public string AttemptId { get; init; } = string.Empty;

    /// <summary>
    /// When the attempt was made.
    /// </summary>
    public DateTime AttemptedAt { get; init; }

    /// <summary>
    /// Who initiated the recovery.
    /// </summary>
    public string InitiatedBy { get; init; } = string.Empty;

    /// <summary>
    /// Share indices that were provided.
    /// </summary>
    public List<byte> SharesProvided { get; init; } = new();

    /// <summary>
    /// Whether the recovery was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Reason for the attempt.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Emergency ticket or incident reference.
    /// </summary>
    public string? IncidentReference { get; init; }
}
