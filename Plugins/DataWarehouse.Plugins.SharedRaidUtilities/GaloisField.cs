using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.SharedRaidUtilities
{
    /// <summary>
    /// GF(2^8) Galois Field implementation for Reed-Solomon error correction in RAID systems.
    /// Uses the standard irreducible polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D) as used in ZFS.
    /// Provides complete arithmetic operations for multi-parity RAID calculations.
    /// </summary>
    /// <remarks>
    /// This implementation uses precomputed logarithm and exponential tables for O(1)
    /// multiplication and division operations. The field generator is 2 (primitive element).
    /// This is a shared utility used across all RAID plugins to eliminate code duplication.
    /// </remarks>
    public sealed class GaloisField
    {
        /// <summary>
        /// Size of the Galois field (2^8 = 256 elements).
        /// </summary>
        public const int FieldSize = 256;

        /// <summary>
        /// The irreducible polynomial used for field reduction: x^8 + x^4 + x^3 + x^2 + 1.
        /// This is the same polynomial used by ZFS for RAID-Z parity calculations.
        /// </summary>
        private const int IrreduciblePolynomial = 0x11D;

        /// <summary>
        /// Primitive element (generator) of the field.
        /// </summary>
        private const byte Generator = 2;

        private readonly byte[] _expTable;
        private readonly byte[] _logTable;
        private readonly byte[,] _multiplyTable;
        private readonly byte[] _inverseTable;

        /// <summary>
        /// Generator polynomial coefficients for Reed-Solomon encoding.
        /// Index 0 = constant term.
        /// </summary>
        private readonly byte[][] _generatorPolynomials;

        /// <summary>
        /// Vandermonde matrix coefficients for parity calculation.
        /// Used for RAID-Z2 and RAID-Z3 parity.
        /// </summary>
        private readonly byte[,] _vandermondeMatrix;

        /// <summary>
        /// Creates a new Galois Field instance with precomputed tables.
        /// </summary>
        public GaloisField()
        {
            _expTable = new byte[FieldSize * 2];
            _logTable = new byte[FieldSize];
            _multiplyTable = new byte[FieldSize, FieldSize];
            _inverseTable = new byte[FieldSize];

            InitializeLogExpTables();
            InitializeMultiplyTable();
            InitializeInverseTable();

            _generatorPolynomials = new byte[4][];
            InitializeGeneratorPolynomials();

            _vandermondeMatrix = new byte[3, FieldSize];
            InitializeVandermondeMatrix();
        }

        private void InitializeLogExpTables()
        {
            int x = 1;
            for (int i = 0; i < FieldSize - 1; i++)
            {
                _expTable[i] = (byte)x;
                _logTable[x] = (byte)i;

                x <<= 1;
                if (x >= FieldSize)
                {
                    x ^= IrreduciblePolynomial;
                }
            }

            _expTable[FieldSize - 1] = _expTable[0];
            _logTable[0] = 0;

            for (int i = FieldSize - 1; i < FieldSize * 2; i++)
            {
                _expTable[i] = _expTable[i - (FieldSize - 1)];
            }
        }

        private void InitializeMultiplyTable()
        {
            for (int a = 0; a < FieldSize; a++)
            {
                for (int b = 0; b < FieldSize; b++)
                {
                    if (a == 0 || b == 0)
                    {
                        _multiplyTable[a, b] = 0;
                    }
                    else
                    {
                        _multiplyTable[a, b] = _expTable[_logTable[a] + _logTable[b]];
                    }
                }
            }
        }

        private void InitializeInverseTable()
        {
            _inverseTable[0] = 0;
            for (int i = 1; i < FieldSize; i++)
            {
                _inverseTable[i] = _expTable[255 - _logTable[i]];
            }
        }

        private void InitializeGeneratorPolynomials()
        {
            _generatorPolynomials[0] = new byte[] { 1 };

            for (int i = 1; i <= 3; i++)
            {
                _generatorPolynomials[i] = MultiplyPolynomial(
                    _generatorPolynomials[i - 1],
                    new byte[] { 1, Power(Generator, i - 1) });
            }
        }

        private void InitializeVandermondeMatrix()
        {
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < FieldSize; col++)
                {
                    _vandermondeMatrix[row, col] = Power(Generator, row * col);
                }
            }
        }

        /// <summary>
        /// Adds two elements in GF(2^8). Addition is XOR in binary fields.
        /// </summary>
        /// <param name="a">First operand.</param>
        /// <param name="b">Second operand.</param>
        /// <returns>Sum in GF(2^8).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Add(byte a, byte b) => (byte)(a ^ b);

        /// <summary>
        /// Subtracts two elements in GF(2^8). Subtraction equals addition in binary fields.
        /// </summary>
        /// <param name="a">Minuend.</param>
        /// <param name="b">Subtrahend.</param>
        /// <returns>Difference in GF(2^8).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Subtract(byte a, byte b) => (byte)(a ^ b);

        /// <summary>
        /// Multiplies two elements in GF(2^8) using precomputed tables.
        /// </summary>
        /// <param name="a">First factor.</param>
        /// <param name="b">Second factor.</param>
        /// <returns>Product in GF(2^8).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Multiply(byte a, byte b)
        {
            return _multiplyTable[a, b];
        }

        /// <summary>
        /// Divides two elements in GF(2^8).
        /// </summary>
        /// <param name="a">Dividend.</param>
        /// <param name="b">Divisor (must be non-zero).</param>
        /// <returns>Quotient in GF(2^8).</returns>
        /// <exception cref="DivideByZeroException">Thrown when b is zero.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Divide(byte a, byte b)
        {
            if (b == 0)
                throw new DivideByZeroException("Division by zero in Galois Field");
            if (a == 0)
                return 0;
            return _expTable[(_logTable[a] + 255 - _logTable[b]) % 255];
        }

        /// <summary>
        /// Computes base^exp in GF(2^8).
        /// </summary>
        /// <param name="base">Base value.</param>
        /// <param name="exp">Exponent.</param>
        /// <returns>Power in GF(2^8).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Power(int @base, int exp)
        {
            if (@base == 0)
                return 0;
            if (exp == 0)
                return 1;

            @base = @base & 0xFF;
            exp = ((exp % 255) + 255) % 255;

            if (@base == 0)
                return 0;

            var logBase = _logTable[@base];
            var result = (logBase * exp) % 255;
            return _expTable[result];
        }

        /// <summary>
        /// Computes the multiplicative inverse of an element in GF(2^8).
        /// </summary>
        /// <param name="a">Element to invert (must be non-zero).</param>
        /// <returns>Multiplicative inverse in GF(2^8).</returns>
        /// <exception cref="ArgumentException">Thrown when a is zero.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Inverse(byte a)
        {
            if (a == 0)
                throw new ArgumentException("Zero has no multiplicative inverse", nameof(a));
            return _inverseTable[a];
        }

        /// <summary>
        /// Computes the exponential (antilog) of a value.
        /// </summary>
        /// <param name="i">Log value.</param>
        /// <returns>Exponential in GF(2^8).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Exp(int i)
        {
            return _expTable[(i % 255 + 255) % 255];
        }

        /// <summary>
        /// Computes the logarithm of a value.
        /// </summary>
        /// <param name="a">Value to take log of (must be non-zero).</param>
        /// <returns>Logarithm in GF(2^8).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Log(byte a)
        {
            if (a == 0)
                throw new ArgumentException("Logarithm of zero is undefined", nameof(a));
            return _logTable[a];
        }

        /// <summary>
        /// Multiplies two polynomials in GF(2^8)[x].
        /// </summary>
        /// <param name="p1">First polynomial (coefficients).</param>
        /// <param name="p2">Second polynomial (coefficients).</param>
        /// <returns>Product polynomial.</returns>
        public byte[] MultiplyPolynomial(byte[] p1, byte[] p2)
        {
            var result = new byte[p1.Length + p2.Length - 1];

            for (int i = 0; i < p1.Length; i++)
            {
                for (int j = 0; j < p2.Length; j++)
                {
                    result[i + j] = Add(result[i + j], Multiply(p1[i], p2[j]));
                }
            }

            return result;
        }

        /// <summary>
        /// Evaluates a polynomial at a given point in GF(2^8).
        /// Uses Horner's method for efficiency.
        /// </summary>
        /// <param name="poly">Polynomial coefficients (index 0 = constant term).</param>
        /// <param name="x">Point at which to evaluate.</param>
        /// <returns>Polynomial value at x.</returns>
        public byte EvaluatePolynomial(byte[] poly, byte x)
        {
            if (poly.Length == 0)
                return 0;

            byte result = poly[poly.Length - 1];
            for (int i = poly.Length - 2; i >= 0; i--)
            {
                result = Add(Multiply(result, x), poly[i]);
            }
            return result;
        }

        /// <summary>
        /// Calculates P parity (XOR parity) for RAID-Z1.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks.</param>
        /// <returns>P parity block.</returns>
        public byte[] CalculatePParity(byte[][] dataBlocks)
        {
            if (dataBlocks.Length == 0)
                return Array.Empty<byte>();

            var maxLength = 0;
            foreach (var block in dataBlocks)
            {
                if (block != null && block.Length > maxLength)
                    maxLength = block.Length;
            }

            var parity = new byte[maxLength];

            foreach (var block in dataBlocks)
            {
                if (block != null)
                {
                    for (int i = 0; i < block.Length; i++)
                    {
                        parity[i] ^= block[i];
                    }
                }
            }

            return parity;
        }

        /// <summary>
        /// Calculates Q parity (Reed-Solomon parity) for RAID-Z2.
        /// Q[i] = sum of (data[j][i] * 2^j) for all j.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks.</param>
        /// <returns>Q parity block.</returns>
        public byte[] CalculateQParity(byte[][] dataBlocks)
        {
            if (dataBlocks.Length == 0)
                return Array.Empty<byte>();

            var maxLength = 0;
            foreach (var block in dataBlocks)
            {
                if (block != null && block.Length > maxLength)
                    maxLength = block.Length;
            }

            var parity = new byte[maxLength];

            for (int i = 0; i < maxLength; i++)
            {
                byte result = 0;
                for (int j = 0; j < dataBlocks.Length; j++)
                {
                    if (dataBlocks[j] != null && i < dataBlocks[j].Length)
                    {
                        var coefficient = Power(Generator, j);
                        result = Add(result, Multiply(dataBlocks[j][i], coefficient));
                    }
                }
                parity[i] = result;
            }

            return parity;
        }

        /// <summary>
        /// Calculates R parity (second Reed-Solomon parity) for RAID-Z3.
        /// R[i] = sum of (data[j][i] * 4^j) for all j.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks.</param>
        /// <returns>R parity block.</returns>
        public byte[] CalculateRParity(byte[][] dataBlocks)
        {
            if (dataBlocks.Length == 0)
                return Array.Empty<byte>();

            var maxLength = 0;
            foreach (var block in dataBlocks)
            {
                if (block != null && block.Length > maxLength)
                    maxLength = block.Length;
            }

            var parity = new byte[maxLength];

            for (int i = 0; i < maxLength; i++)
            {
                byte result = 0;
                for (int j = 0; j < dataBlocks.Length; j++)
                {
                    if (dataBlocks[j] != null && i < dataBlocks[j].Length)
                    {
                        var coefficient = Power(4, j);
                        result = Add(result, Multiply(dataBlocks[j][i], coefficient));
                    }
                }
                parity[i] = result;
            }

            return parity;
        }

        /// <summary>
        /// Reconstructs a single missing data block using P parity.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks (null for missing).</param>
        /// <param name="pParity">P parity block.</param>
        /// <param name="missingIndex">Index of the missing block.</param>
        /// <returns>Reconstructed data block.</returns>
        public byte[] ReconstructFromP(byte[][] dataBlocks, byte[] pParity, int missingIndex)
        {
            var result = new byte[pParity.Length];
            Array.Copy(pParity, result, pParity.Length);

            for (int i = 0; i < dataBlocks.Length; i++)
            {
                if (i != missingIndex && dataBlocks[i] != null)
                {
                    for (int j = 0; j < dataBlocks[i].Length && j < result.Length; j++)
                    {
                        result[j] ^= dataBlocks[i][j];
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Reconstructs two missing data blocks using P and Q parity.
        /// Implements the ZFS RAID-Z2 reconstruction algorithm.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks (will be modified with reconstructed data).</param>
        /// <param name="pParity">P parity block.</param>
        /// <param name="qParity">Q parity block.</param>
        /// <param name="missing1">Index of first missing block.</param>
        /// <param name="missing2">Index of second missing block.</param>
        public void ReconstructFromPQ(byte[][] dataBlocks, byte[] pParity, byte[] qParity,
            int missing1, int missing2)
        {
            var length = pParity.Length;
            dataBlocks[missing1] = new byte[length];
            dataBlocks[missing2] = new byte[length];

            var coef1 = Power(Generator, missing1);
            var coef2 = Power(Generator, missing2);

            var coefDiff = Add(coef1, coef2);
            if (coefDiff == 0)
            {
                throw new InvalidOperationException("Cannot reconstruct: coefficient difference is zero");
            }
            var coefDiffInv = Inverse(coefDiff);

            for (int i = 0; i < length; i++)
            {
                byte pSyndrome = pParity[i];
                byte qSyndrome = qParity[i];

                for (int j = 0; j < dataBlocks.Length; j++)
                {
                    if (j != missing1 && j != missing2 && dataBlocks[j] != null && i < dataBlocks[j].Length)
                    {
                        pSyndrome ^= dataBlocks[j][i];
                        qSyndrome = Add(qSyndrome, Multiply(dataBlocks[j][i], Power(Generator, j)));
                    }
                }

                var a = Multiply(Add(Multiply(pSyndrome, coef2), qSyndrome), coefDiffInv);
                var b = Add(pSyndrome, a);

                dataBlocks[missing1][i] = a;
                dataBlocks[missing2][i] = b;
            }
        }

        /// <summary>
        /// Reconstructs three missing data blocks using P, Q, and R parity.
        /// Implements the ZFS RAID-Z3 reconstruction algorithm using matrix inversion.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks (will be modified with reconstructed data).</param>
        /// <param name="pParity">P parity block.</param>
        /// <param name="qParity">Q parity block.</param>
        /// <param name="rParity">R parity block.</param>
        /// <param name="missing1">Index of first missing block.</param>
        /// <param name="missing2">Index of second missing block.</param>
        /// <param name="missing3">Index of third missing block.</param>
        public void ReconstructFromPQR(byte[][] dataBlocks, byte[] pParity, byte[] qParity, byte[] rParity,
            int missing1, int missing2, int missing3)
        {
            var length = pParity.Length;
            dataBlocks[missing1] = new byte[length];
            dataBlocks[missing2] = new byte[length];
            dataBlocks[missing3] = new byte[length];

            var a1 = (byte)1;
            var a2 = (byte)1;
            var a3 = (byte)1;
            var b1 = Power(Generator, missing1);
            var b2 = Power(Generator, missing2);
            var b3 = Power(Generator, missing3);
            var c1 = Power(4, missing1);
            var c2 = Power(4, missing2);
            var c3 = Power(4, missing3);

            var det = Add(Add(
                Multiply(Multiply(a1, b2), c3),
                Add(
                    Multiply(Multiply(a2, b3), c1),
                    Multiply(Multiply(a3, b1), c2))),
                Add(
                    Multiply(Multiply(a1, b3), c2),
                    Add(
                        Multiply(Multiply(a2, b1), c3),
                        Multiply(Multiply(a3, b2), c1))));

            if (det == 0)
            {
                throw new InvalidOperationException("Cannot reconstruct: matrix is singular");
            }

            var detInv = Inverse(det);

            var m11 = Multiply(Add(Multiply(b2, c3), Multiply(b3, c2)), detInv);
            var m12 = Multiply(Add(Multiply(a2, c3), Multiply(a3, c2)), detInv);
            var m13 = Multiply(Add(Multiply(a2, b3), Multiply(a3, b2)), detInv);
            var m21 = Multiply(Add(Multiply(b1, c3), Multiply(b3, c1)), detInv);
            var m22 = Multiply(Add(Multiply(a1, c3), Multiply(a3, c1)), detInv);
            var m23 = Multiply(Add(Multiply(a1, b3), Multiply(a3, b1)), detInv);
            var m31 = Multiply(Add(Multiply(b1, c2), Multiply(b2, c1)), detInv);
            var m32 = Multiply(Add(Multiply(a1, c2), Multiply(a2, c1)), detInv);
            var m33 = Multiply(Add(Multiply(a1, b2), Multiply(a2, b1)), detInv);

            for (int i = 0; i < length; i++)
            {
                byte pSyndrome = pParity[i];
                byte qSyndrome = qParity[i];
                byte rSyndrome = rParity[i];

                for (int j = 0; j < dataBlocks.Length; j++)
                {
                    if (j != missing1 && j != missing2 && j != missing3 &&
                        dataBlocks[j] != null && i < dataBlocks[j].Length)
                    {
                        pSyndrome ^= dataBlocks[j][i];
                        qSyndrome = Add(qSyndrome, Multiply(dataBlocks[j][i], Power(Generator, j)));
                        rSyndrome = Add(rSyndrome, Multiply(dataBlocks[j][i], Power(4, j)));
                    }
                }

                dataBlocks[missing1][i] = Add(Add(
                    Multiply(m11, pSyndrome),
                    Multiply(m12, qSyndrome)),
                    Multiply(m13, rSyndrome));

                dataBlocks[missing2][i] = Add(Add(
                    Multiply(m21, pSyndrome),
                    Multiply(m22, qSyndrome)),
                    Multiply(m23, rSyndrome));

                dataBlocks[missing3][i] = Add(Add(
                    Multiply(m31, pSyndrome),
                    Multiply(m32, qSyndrome)),
                    Multiply(m33, rSyndrome));
            }
        }

        /// <summary>
        /// Generates Reed-Solomon parity shards for erasure coding.
        /// </summary>
        /// <param name="dataShards">Array of data shards.</param>
        /// <param name="parityCount">Number of parity shards to generate.</param>
        /// <returns>Array of parity shards.</returns>
        public byte[][] GenerateParityShards(byte[][] dataShards, int parityCount)
        {
            if (parityCount < 1 || parityCount > 3)
                throw new ArgumentOutOfRangeException(nameof(parityCount), "Parity count must be 1-3");

            var parityShards = new byte[parityCount][];

            parityShards[0] = CalculatePParity(dataShards);

            if (parityCount >= 2)
            {
                parityShards[1] = CalculateQParity(dataShards);
            }

            if (parityCount >= 3)
            {
                parityShards[2] = CalculateRParity(dataShards);
            }

            return parityShards;
        }

        /// <summary>
        /// Verifies that data matches the expected parity values.
        /// </summary>
        /// <param name="dataBlocks">Array of data blocks.</param>
        /// <param name="pParity">Expected P parity.</param>
        /// <param name="qParity">Expected Q parity (optional, for RAID-Z2+).</param>
        /// <param name="rParity">Expected R parity (optional, for RAID-Z3).</param>
        /// <returns>True if all parity values match.</returns>
        public bool VerifyParity(byte[][] dataBlocks, byte[] pParity, byte[]? qParity = null, byte[]? rParity = null)
        {
            var calculatedP = CalculatePParity(dataBlocks);
            if (!ArraysEqual(calculatedP, pParity))
                return false;

            if (qParity != null)
            {
                var calculatedQ = CalculateQParity(dataBlocks);
                if (!ArraysEqual(calculatedQ, qParity))
                    return false;
            }

            if (rParity != null)
            {
                var calculatedR = CalculateRParity(dataBlocks);
                if (!ArraysEqual(calculatedR, rParity))
                    return false;
            }

            return true;
        }

        private static bool ArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }
    }
}
