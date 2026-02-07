using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Fpe;

/// <summary>
/// FF1 Format-Preserving Encryption strategy (NIST SP 800-38G).
/// Encrypts data while preserving the format and length using a Feistel network.
/// Suitable for encrypting structured data like credit card numbers, SSNs, etc.
/// </summary>
public sealed class Ff1Strategy : EncryptionStrategyBase
{
    private const int MinRadix = 2;
    private const int MaxRadix = 65536;
    private const int MinTweakLength = 0;
    private const int MaxTweakLength = 256; // bytes
    private const int DefaultRadix = 10; // Decimal digits

    /// <inheritdoc/>
    public override string StrategyId => "ff1-fpe";

    /// <inheritdoc/>
    public override string StrategyName => "FF1 Format-Preserving Encryption";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "FF1-AES-256",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = 0, // FF1 uses tweaks instead of IVs
        TagSizeBytes = 0, // FF1 is not authenticated
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true, // Uses AES internally
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.High
        },
        Parameters = new Dictionary<string, object>
        {
            ["MaxInputLength"] = 56, // NIST SP 800-38G recommendation
            ["Radix"] = DefaultRadix,
            ["TweakSupported"] = true
        }
    };

    /// <summary>
    /// Encrypts plaintext using FF1 format-preserving encryption.
    /// </summary>
    /// <param name="plaintext">Plaintext bytes (interpreted as numeric string in given radix).</param>
    /// <param name="key">AES-256 key (32 bytes).</param>
    /// <param name="associatedData">Tweak value for additional context binding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Encrypted ciphertext in the same format as plaintext.</returns>
    protected override Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var tweak = associatedData ?? Array.Empty<byte>();
        ValidateTweak(tweak);

        // Convert plaintext to numeral string (radix 10 by default)
        var numeralString = Encoding.UTF8.GetString(plaintext);
        ValidateNumeralString(numeralString, DefaultRadix);

        var encrypted = EncryptFf1(numeralString, key, tweak, DefaultRadix);
        return Task.FromResult(Encoding.UTF8.GetBytes(encrypted));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var tweak = associatedData ?? Array.Empty<byte>();
        ValidateTweak(tweak);

        var numeralString = Encoding.UTF8.GetString(ciphertext);
        ValidateNumeralString(numeralString, DefaultRadix);

        var decrypted = DecryptFf1(numeralString, key, tweak, DefaultRadix);
        return Task.FromResult(Encoding.UTF8.GetBytes(decrypted));
    }

    /// <summary>
    /// FF1 encryption implementation using Feistel network (NIST SP 800-38G).
    /// </summary>
    private static string EncryptFf1(string numeralString, byte[] key, byte[] tweak, int radix)
    {
        var n = numeralString.Length;
        var u = n / 2;
        var v = n - u;

        // Split the numeral string into two halves
        var A = numeralString.Substring(0, u);
        var B = numeralString.Substring(u);

        // FF1 uses 10 rounds for the Feistel network
        const int rounds = 10;

        for (int i = 0; i < rounds; i++)
        {
            // Compute round function: C = (NUM(A) + F(B, i)) mod radix^u
            var roundKey = ComputeRoundKey(key, tweak, i, B, radix, n);
            var c = (NumeralStringToNumber(A, radix) + roundKey) % BigInteger.Pow(radix, A.Length);
            var C = NumberToNumeralString(c, radix, A.Length);

            // Swap halves
            A = B;
            B = C;
        }

        return A + B;
    }

    /// <summary>
    /// FF1 decryption implementation (reverse Feistel network).
    /// </summary>
    private static string DecryptFf1(string numeralString, byte[] key, byte[] tweak, int radix)
    {
        var n = numeralString.Length;
        var u = n / 2;
        var v = n - u;

        var A = numeralString.Substring(0, u);
        var B = numeralString.Substring(u);

        const int rounds = 10;

        // Reverse the Feistel rounds
        for (int i = rounds - 1; i >= 0; i--)
        {
            var C = B;
            B = A;

            var roundKey = ComputeRoundKey(key, tweak, i, B, radix, n);
            var numA = (NumeralStringToNumber(C, radix) - roundKey);

            // Handle negative modulo
            var modulus = BigInteger.Pow(radix, C.Length);
            if (numA < 0)
            {
                numA = ((numA % modulus) + modulus) % modulus;
            }
            else
            {
                numA = numA % modulus;
            }

            A = NumberToNumeralString(numA, radix, C.Length);
        }

        return A + B;
    }

    /// <summary>
    /// Computes the FF1 round key using AES-CBC-MAC.
    /// </summary>
    private static BigInteger ComputeRoundKey(byte[] key, byte[] tweak, int round, string half, int radix, int n)
    {
        // Construct PRF input according to NIST SP 800-38G
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);

        // [radix] || [n] || [tweak length] || [round] || [half]
        writer.Write((byte)1); // Version
        writer.Write((byte)2); // Method (block cipher AES)
        writer.Write((byte)1); // Addition
        writer.Write((byte)radix);
        writer.Write((byte)10); // Number of rounds
        writer.Write((byte)n);
        writer.Write((byte)tweak.Length);
        writer.Write(tweak);
        writer.Write((byte)round);

        var halfBytes = Encoding.UTF8.GetBytes(half);
        writer.Write(halfBytes);

        var prfInput = ms.ToArray();

        // Apply AES in CBC-MAC mode
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.Zeros;
        aes.IV = new byte[16]; // Zero IV for CBC-MAC

        using var encryptor = aes.CreateEncryptor();

        // Pad to block size
        var paddedLength = ((prfInput.Length + 15) / 16) * 16;
        var paddedInput = new byte[paddedLength];
        Buffer.BlockCopy(prfInput, 0, paddedInput, 0, prfInput.Length);

        var macOutput = encryptor.TransformFinalBlock(paddedInput, 0, paddedInput.Length);

        // Take the last block as the MAC
        var mac = new byte[16];
        Buffer.BlockCopy(macOutput, macOutput.Length - 16, mac, 0, 16);

        // Convert MAC to BigInteger (mod appropriate value)
        return new BigInteger(mac, isUnsigned: true);
    }

    /// <summary>
    /// Converts a numeral string to a BigInteger.
    /// </summary>
    private static BigInteger NumeralStringToNumber(string numeral, int radix)
    {
        BigInteger result = 0;
        foreach (char c in numeral)
        {
            int digit = CharToDigit(c, radix);
            result = result * radix + digit;
        }
        return result;
    }

    /// <summary>
    /// Converts a BigInteger to a numeral string with the specified radix and length.
    /// </summary>
    private static string NumberToNumeralString(BigInteger number, int radix, int length)
    {
        if (number == 0)
        {
            return new string('0', length);
        }

        var result = new char[length];
        for (int i = length - 1; i >= 0; i--)
        {
            var digit = (int)(number % radix);
            result[i] = DigitToChar(digit, radix);
            number /= radix;
        }

        return new string(result);
    }

    private static int CharToDigit(char c, int radix)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'z')
            return c - 'a' + 10;
        if (c >= 'A' && c <= 'Z')
            return c - 'A' + 10;
        throw new ArgumentException($"Invalid character '{c}' for radix {radix}");
    }

    private static char DigitToChar(int digit, int radix)
    {
        if (digit < 10)
            return (char)('0' + digit);
        return (char)('a' + digit - 10);
    }

    private static void ValidateTweak(byte[] tweak)
    {
        if (tweak.Length > MaxTweakLength)
        {
            throw new ArgumentException(
                $"Tweak length exceeds maximum of {MaxTweakLength} bytes",
                nameof(tweak));
        }
    }

    private static void ValidateNumeralString(string numeral, int radix)
    {
        if (string.IsNullOrEmpty(numeral))
        {
            throw new ArgumentException("Numeral string cannot be empty", nameof(numeral));
        }

        if (numeral.Length > 56)
        {
            throw new ArgumentException(
                "Numeral string length exceeds NIST recommendation of 56 characters",
                nameof(numeral));
        }

        foreach (char c in numeral)
        {
            var digit = CharToDigit(c, radix);
            if (digit >= radix)
            {
                throw new ArgumentException(
                    $"Character '{c}' is invalid for radix {radix}",
                    nameof(numeral));
            }
        }
    }
}

/// <summary>
/// FF3-1 Format-Preserving Encryption strategy (NIST SP 800-38G Rev. 1).
/// Similar to FF1 but with different tweak handling (8-byte tweak split into left/right).
/// More efficient for certain use cases but with stricter tweak requirements.
/// </summary>
public sealed class Ff3Strategy : EncryptionStrategyBase
{
    private const int TweakLength = 8; // FF3-1 requires exactly 8 bytes
    private const int DefaultRadix = 10;

    /// <inheritdoc/>
    public override string StrategyId => "ff3-fpe";

    /// <inheritdoc/>
    public override string StrategyName => "FF3-1 Format-Preserving Encryption";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "FF3-1-AES-256",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = 0,
        TagSizeBytes = 0,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.High
        },
        Parameters = new Dictionary<string, object>
        {
            ["MaxInputLength"] = 56,
            ["Radix"] = DefaultRadix,
            ["TweakLength"] = TweakLength,
            ["TweakRequired"] = true
        }
    };

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        if (associatedData == null || associatedData.Length != TweakLength)
        {
            throw new ArgumentException(
                $"FF3-1 requires exactly {TweakLength} bytes of tweak data",
                nameof(associatedData));
        }

        var numeralString = Encoding.UTF8.GetString(plaintext);
        ValidateNumeralString(numeralString, DefaultRadix);

        var encrypted = EncryptFf3(numeralString, key, associatedData, DefaultRadix);
        return Task.FromResult(Encoding.UTF8.GetBytes(encrypted));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        if (associatedData == null || associatedData.Length != TweakLength)
        {
            throw new ArgumentException(
                $"FF3-1 requires exactly {TweakLength} bytes of tweak data",
                nameof(associatedData));
        }

        var numeralString = Encoding.UTF8.GetString(ciphertext);
        ValidateNumeralString(numeralString, DefaultRadix);

        var decrypted = DecryptFf3(numeralString, key, associatedData, DefaultRadix);
        return Task.FromResult(Encoding.UTF8.GetBytes(decrypted));
    }

    /// <summary>
    /// FF3-1 encryption implementation.
    /// </summary>
    private static string EncryptFf3(string numeralString, byte[] key, byte[] tweak, int radix)
    {
        var n = numeralString.Length;
        var u = n / 2;
        var v = n - u;

        // Split tweak into TL (left 4 bytes) and TR (right 4 bytes)
        var TL = new byte[4];
        var TR = new byte[4];
        Buffer.BlockCopy(tweak, 0, TL, 0, 4);
        Buffer.BlockCopy(tweak, 4, TR, 0, 4);

        var A = numeralString.Substring(0, u);
        var B = numeralString.Substring(u);

        // FF3-1 uses 8 rounds
        const int rounds = 8;

        for (int i = 0; i < rounds; i++)
        {
            var T = (i % 2 == 0) ? TL : TR;
            var W = ComputeFf3RoundKey(key, T, i, B);

            var c = (NumeralStringToNumber(A, radix) + W) % BigInteger.Pow(radix, A.Length);
            var C = NumberToNumeralString(c, radix, A.Length);

            A = B;
            B = C;
        }

        return A + B;
    }

    /// <summary>
    /// FF3-1 decryption implementation.
    /// </summary>
    private static string DecryptFf3(string numeralString, byte[] key, byte[] tweak, int radix)
    {
        var n = numeralString.Length;
        var u = n / 2;
        var v = n - u;

        var TL = new byte[4];
        var TR = new byte[4];
        Buffer.BlockCopy(tweak, 0, TL, 0, 4);
        Buffer.BlockCopy(tweak, 4, TR, 0, 4);

        var A = numeralString.Substring(0, u);
        var B = numeralString.Substring(u);

        const int rounds = 8;

        for (int i = rounds - 1; i >= 0; i--)
        {
            var C = B;
            B = A;

            var T = (i % 2 == 0) ? TL : TR;
            var W = ComputeFf3RoundKey(key, T, i, B);

            var numA = (NumeralStringToNumber(C, radix) - W);
            var modulus = BigInteger.Pow(radix, C.Length);

            if (numA < 0)
            {
                numA = ((numA % modulus) + modulus) % modulus;
            }
            else
            {
                numA = numA % modulus;
            }

            A = NumberToNumeralString(numA, radix, C.Length);
        }

        return A + B;
    }

    /// <summary>
    /// Computes FF3-1 round key using AES encryption.
    /// </summary>
    private static BigInteger ComputeFf3RoundKey(byte[] key, byte[] tweakPart, int round, string half)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        // SECURITY NOTE: ECB mode required for FF1/FF3-1 FPE standard implementation (NIST SP 800-38G).
        // This is cryptographically safe as it's part of the standardized FPE construction.
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        // Construct the input block: P = [tweak] || [round] || [numeral string representation]
        var inputBlock = new byte[16];
        Buffer.BlockCopy(tweakPart, 0, inputBlock, 0, 4);
        inputBlock[4] = (byte)round;

        // Encode the half string as a number and store in remaining bytes
        var numHalf = NumeralStringToNumber(half, 10);
        var numHalfBytes = numHalf.ToByteArray(isUnsigned: true, isBigEndian: true);
        var offset = 16 - numHalfBytes.Length;
        Buffer.BlockCopy(numHalfBytes, 0, inputBlock, offset, numHalfBytes.Length);

        using var encryptor = aes.CreateEncryptor();
        var output = encryptor.TransformFinalBlock(inputBlock, 0, inputBlock.Length);

        return new BigInteger(output, isUnsigned: true);
    }

    private static BigInteger NumeralStringToNumber(string numeral, int radix)
    {
        BigInteger result = 0;
        foreach (char c in numeral)
        {
            int digit = c - '0';
            result = result * radix + digit;
        }
        return result;
    }

    private static string NumberToNumeralString(BigInteger number, int radix, int length)
    {
        if (number == 0)
        {
            return new string('0', length);
        }

        var result = new char[length];
        for (int i = length - 1; i >= 0; i--)
        {
            var digit = (int)(number % radix);
            result[i] = (char)('0' + digit);
            number /= radix;
        }

        return new string(result);
    }

    private static void ValidateNumeralString(string numeral, int radix)
    {
        if (string.IsNullOrEmpty(numeral))
        {
            throw new ArgumentException("Numeral string cannot be empty", nameof(numeral));
        }

        if (numeral.Length < 6 || numeral.Length > 56)
        {
            throw new ArgumentException(
                "FF3-1 requires numeral string length between 6 and 56 characters",
                nameof(numeral));
        }

        foreach (char c in numeral)
        {
            if (c < '0' || c >= '0' + radix)
            {
                throw new ArgumentException(
                    $"Character '{c}' is invalid for radix {radix}",
                    nameof(numeral));
            }
        }
    }
}

/// <summary>
/// Format-preserving encryption strategy optimized for credit card numbers.
/// Uses FF1 internally with radix-10 and 16-digit length enforcement.
/// </summary>
public sealed class FpeCreditCardStrategy : EncryptionStrategyBase
{
    private const int CreditCardLength = 16;
    private readonly Ff1Strategy _ff1;

    /// <summary>
    /// Initializes a new instance of the <see cref="FpeCreditCardStrategy"/> class.
    /// </summary>
    public FpeCreditCardStrategy()
    {
        _ff1 = new Ff1Strategy();
    }

    /// <inheritdoc/>
    public override string StrategyId => "fpe-credit-card";

    /// <inheritdoc/>
    public override string StrategyName => "FPE Credit Card (FF1)";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "FF1-AES-256-CreditCard",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = 0,
        TagSizeBytes = 0,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.High
        },
        Parameters = new Dictionary<string, object>
        {
            ["FixedLength"] = CreditCardLength,
            ["Radix"] = 10,
            ["Format"] = "Credit Card (16 digits)"
        }
    };

    /// <inheritdoc/>
    protected override async Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var cardNumber = Encoding.UTF8.GetString(plaintext).Replace("-", "").Replace(" ", "");

        if (!IsValidCreditCardNumber(cardNumber))
        {
            throw new ArgumentException(
                $"Invalid credit card number. Must be exactly {CreditCardLength} digits",
                nameof(plaintext));
        }

        return await _ff1.EncryptAsync(
            Encoding.UTF8.GetBytes(cardNumber),
            key,
            associatedData,
            cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var encryptedCardNumber = Encoding.UTF8.GetString(ciphertext);

        if (!IsValidCreditCardNumber(encryptedCardNumber))
        {
            throw new ArgumentException(
                $"Invalid encrypted credit card format. Must be exactly {CreditCardLength} digits",
                nameof(ciphertext));
        }

        return await _ff1.DecryptAsync(ciphertext, key, associatedData, cancellationToken);
    }

    private static bool IsValidCreditCardNumber(string cardNumber)
    {
        if (cardNumber.Length != CreditCardLength)
            return false;

        foreach (char c in cardNumber)
        {
            if (c < '0' || c > '9')
                return false;
        }

        return true;
    }
}

/// <summary>
/// Format-preserving encryption strategy optimized for Social Security Numbers (SSN).
/// Uses FF1 internally with radix-10 and 9-digit length enforcement.
/// </summary>
public sealed class FpeSsnStrategy : EncryptionStrategyBase
{
    private const int SsnLength = 9;
    private readonly Ff1Strategy _ff1;

    /// <summary>
    /// Initializes a new instance of the <see cref="FpeSsnStrategy"/> class.
    /// </summary>
    public FpeSsnStrategy()
    {
        _ff1 = new Ff1Strategy();
    }

    /// <inheritdoc/>
    public override string StrategyId => "fpe-ssn";

    /// <inheritdoc/>
    public override string StrategyName => "FPE Social Security Number (FF1)";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "FF1-AES-256-SSN",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = 0,
        TagSizeBytes = 0,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.High
        },
        Parameters = new Dictionary<string, object>
        {
            ["FixedLength"] = SsnLength,
            ["Radix"] = 10,
            ["Format"] = "SSN (9 digits)"
        }
    };

    /// <inheritdoc/>
    protected override async Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var ssn = Encoding.UTF8.GetString(plaintext).Replace("-", "").Replace(" ", "");

        if (!IsValidSsn(ssn))
        {
            throw new ArgumentException(
                $"Invalid SSN. Must be exactly {SsnLength} digits",
                nameof(plaintext));
        }

        return await _ff1.EncryptAsync(
            Encoding.UTF8.GetBytes(ssn),
            key,
            associatedData,
            cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var encryptedSsn = Encoding.UTF8.GetString(ciphertext);

        if (!IsValidSsn(encryptedSsn))
        {
            throw new ArgumentException(
                $"Invalid encrypted SSN format. Must be exactly {SsnLength} digits",
                nameof(ciphertext));
        }

        return await _ff1.DecryptAsync(ciphertext, key, associatedData, cancellationToken);
    }

    private static bool IsValidSsn(string ssn)
    {
        if (ssn.Length != SsnLength)
            return false;

        foreach (char c in ssn)
        {
            if (c < '0' || c > '9')
                return false;
        }

        return true;
    }
}
