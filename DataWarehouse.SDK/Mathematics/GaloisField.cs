using System;
using System.Runtime.CompilerServices;

namespace DataWarehouse.SDK.Mathematics;

/// <summary>
/// Provides Galois Field GF(2^8) arithmetic operations for Reed-Solomon encoding.
/// Uses irreducible polynomial 0x11D (x^8 + x^4 + x^3 + x^2 + 1).
/// Thread-safe static implementation with pre-computed lookup tables for performance.
/// </summary>
public static class GaloisField
{
    /// <summary>
    /// The irreducible polynomial used for GF(2^8): 0x11D
    /// </summary>
    private const int Polynomial = 0x11D;

    /// <summary>
    /// Field size (2^8 = 256)
    /// </summary>
    private const int FieldSize = 256;

    /// <summary>
    /// Pre-computed logarithm table for multiplication optimization.
    /// Maps field element to its discrete logarithm.
    /// </summary>
    private static readonly byte[] LogTable = new byte[FieldSize];

    /// <summary>
    /// Pre-computed antilogarithm (exponentiation) table.
    /// Maps discrete logarithm back to field element.
    /// </summary>
    private static readonly byte[] ExpTable = new byte[FieldSize * 2];

    /// <summary>
    /// Pre-computed inverse table for division operations.
    /// Maps field element to its multiplicative inverse.
    /// </summary>
    private static readonly byte[] InverseTable = new byte[FieldSize];

    /// <summary>
    /// Static constructor initializes lookup tables once at class load time.
    /// </summary>
    static GaloisField()
    {
        InitializeTables();
    }

    /// <summary>
    /// Initializes all pre-computed lookup tables for Galois Field operations.
    /// </summary>
    private static void InitializeTables()
    {
        // Generate exponential and logarithm tables
        int x = 1;
        for (int i = 0; i < FieldSize - 1; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;

            // Multiply by generator (2)
            x <<= 1;
            if (x >= FieldSize)
            {
                x ^= Polynomial;
            }
        }

        // Extend exp table for wrap-around (optimization)
        for (int i = FieldSize - 1; i < FieldSize * 2; i++)
        {
            ExpTable[i] = ExpTable[i - (FieldSize - 1)];
        }

        // Special case: log(0) is undefined, set to 0
        LogTable[0] = 0;

        // Generate inverse table
        InverseTable[0] = 0; // 0 has no inverse
        for (int i = 1; i < FieldSize; i++)
        {
            InverseTable[i] = ExpTable[(FieldSize - 1) - LogTable[i]];
        }
    }

    /// <summary>
    /// Adds two field elements. In GF(2^8), addition is XOR.
    /// </summary>
    /// <param name="a">First operand</param>
    /// <param name="b">Second operand</param>
    /// <returns>Sum of a and b in GF(2^8)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Add(byte a, byte b)
    {
        return (byte)(a ^ b);
    }

    /// <summary>
    /// Subtracts two field elements. In GF(2^8), subtraction is identical to addition (XOR).
    /// </summary>
    /// <param name="a">First operand</param>
    /// <param name="b">Second operand</param>
    /// <returns>Difference of a and b in GF(2^8)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Subtract(byte a, byte b)
    {
        return (byte)(a ^ b);
    }

    /// <summary>
    /// Multiplies two field elements using logarithm tables for performance.
    /// </summary>
    /// <param name="a">First operand</param>
    /// <param name="b">Second operand</param>
    /// <returns>Product of a and b in GF(2^8)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }

        // log(a*b) = log(a) + log(b) mod 255
        int logSum = LogTable[a] + LogTable[b];
        return ExpTable[logSum];
    }

    /// <summary>
    /// Divides two field elements. Division by zero throws an exception.
    /// </summary>
    /// <param name="a">Dividend</param>
    /// <param name="b">Divisor</param>
    /// <returns>Quotient of a and b in GF(2^8)</returns>
    /// <exception cref="DivideByZeroException">Thrown when b is zero</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Divide(byte a, byte b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("Cannot divide by zero in Galois Field");
        }

        if (a == 0)
        {
            return 0;
        }

        // log(a/b) = log(a) - log(b) mod 255
        int logDiff = LogTable[a] - LogTable[b];
        if (logDiff < 0)
        {
            logDiff += FieldSize - 1;
        }

        return ExpTable[logDiff];
    }

    /// <summary>
    /// Raises a field element to a power.
    /// </summary>
    /// <param name="a">Base</param>
    /// <param name="n">Exponent</param>
    /// <returns>a raised to the power n in GF(2^8)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Power(byte a, int n)
    {
        if (a == 0)
        {
            return 0;
        }

        if (n == 0)
        {
            return 1;
        }

        // log(a^n) = n * log(a) mod 255
        int logProduct = (LogTable[a] * n) % (FieldSize - 1);
        return ExpTable[logProduct];
    }

    /// <summary>
    /// Computes the multiplicative inverse of a field element.
    /// </summary>
    /// <param name="a">Field element</param>
    /// <returns>Multiplicative inverse of a</returns>
    /// <exception cref="ArgumentException">Thrown when a is zero (no inverse exists)</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Inverse(byte a)
    {
        if (a == 0)
        {
            throw new ArgumentException("Zero has no multiplicative inverse in Galois Field", nameof(a));
        }

        return InverseTable[a];
    }

    /// <summary>
    /// Gets the generator element (2) for GF(2^8).
    /// </summary>
    public static byte Generator => 2;

    /// <summary>
    /// Validates that a value is within the field range.
    /// </summary>
    /// <param name="value">Value to validate</param>
    /// <returns>True if value is valid (0-255)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidFieldElement(int value)
    {
        return value >= 0 && value < FieldSize;
    }

    /// <summary>
    /// Generates a power of the generator element.
    /// Useful for creating generator matrices.
    /// </summary>
    /// <param name="exponent">Exponent to raise generator to</param>
    /// <returns>Generator^exponent in GF(2^8)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GeneratorPower(int exponent)
    {
        return ExpTable[exponent % (FieldSize - 1)];
    }
}
