using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DataWarehouse.Plugins.IsalEc
{
    /// <summary>
    /// SIMD operations for high-performance Galois Field arithmetic.
    /// Automatically detects and uses the best available SIMD instruction set:
    /// AVX-512, AVX2, SSE4.1, or scalar fallback.
    /// </summary>
    public static class SimdOperations
    {
        /// <summary>
        /// Available SIMD instruction sets.
        /// </summary>
        public enum SimdCapability
        {
            /// <summary>No SIMD support, scalar operations only.</summary>
            None = 0,
            /// <summary>SSE4.1 support (128-bit vectors).</summary>
            Sse41 = 1,
            /// <summary>AVX2 support (256-bit vectors).</summary>
            Avx2 = 2,
            /// <summary>AVX-512 support (512-bit vectors).</summary>
            Avx512 = 3
        }

        private static readonly SimdCapability _detectedCapability;
        private static readonly int _vectorSize;

        /// <summary>
        /// Static constructor to detect SIMD capabilities at startup.
        /// </summary>
        static SimdOperations()
        {
            _detectedCapability = DetectSimdCapability();
            _vectorSize = GetVectorSize(_detectedCapability);
        }

        /// <summary>
        /// Gets the detected SIMD capability of the current CPU.
        /// </summary>
        public static SimdCapability DetectedCapability => _detectedCapability;

        /// <summary>
        /// Gets the optimal vector size in bytes for the current CPU.
        /// </summary>
        public static int VectorSize => _vectorSize;

        /// <summary>
        /// Checks if any SIMD acceleration is available.
        /// </summary>
        public static bool IsSimdSupported => _detectedCapability != SimdCapability.None;

        /// <summary>
        /// Checks if AVX2 or better is available.
        /// </summary>
        public static bool IsAvx2Supported => _detectedCapability >= SimdCapability.Avx2;

        /// <summary>
        /// Checks if AVX-512 is available.
        /// </summary>
        public static bool IsAvx512Supported => _detectedCapability >= SimdCapability.Avx512;

        /// <summary>
        /// Detects the SIMD capability of the current CPU.
        /// </summary>
        private static SimdCapability DetectSimdCapability()
        {
            if (Avx512F.IsSupported && Avx512BW.IsSupported)
            {
                return SimdCapability.Avx512;
            }

            if (Avx2.IsSupported)
            {
                return SimdCapability.Avx2;
            }

            if (Sse41.IsSupported)
            {
                return SimdCapability.Sse41;
            }

            return SimdCapability.None;
        }

        /// <summary>
        /// Gets the vector size in bytes for a given SIMD capability.
        /// </summary>
        private static int GetVectorSize(SimdCapability capability)
        {
            return capability switch
            {
                SimdCapability.Avx512 => 64,
                SimdCapability.Avx2 => 32,
                SimdCapability.Sse41 => 16,
                _ => 1
            };
        }

        /// <summary>
        /// XOR two byte spans using SIMD acceleration.
        /// </summary>
        /// <param name="source">Source data.</param>
        /// <param name="target">Target data to XOR with source (modified in place).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void XorBytes(ReadOnlySpan<byte> source, Span<byte> target)
        {
            if (source.Length != target.Length)
            {
                throw new ArgumentException("Source and target must have the same length");
            }

            var length = source.Length;
            var offset = 0;

            if (IsAvx512Supported && length >= 64)
            {
                XorBytesAvx512(source, target, ref offset);
            }
            else if (IsAvx2Supported && length >= 32)
            {
                XorBytesAvx2(source, target, ref offset);
            }
            else if (_detectedCapability >= SimdCapability.Sse41 && length >= 16)
            {
                XorBytesSse(source, target, ref offset);
            }

            // Handle remaining bytes with scalar operations
            for (; offset < length; offset++)
            {
                target[offset] ^= source[offset];
            }
        }

        /// <summary>
        /// XOR using AVX-512 (512-bit, 64 bytes at a time).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void XorBytesAvx512(ReadOnlySpan<byte> source, Span<byte> target, ref int offset)
        {
            var length = source.Length;
            fixed (byte* srcPtr = source)
            fixed (byte* tgtPtr = target)
            {
                while (offset + 64 <= length)
                {
                    var srcVec = Avx512F.LoadVector512(srcPtr + offset);
                    var tgtVec = Avx512F.LoadVector512(tgtPtr + offset);
                    var result = Avx512F.Xor(srcVec, tgtVec);
                    Avx512F.Store(tgtPtr + offset, result);
                    offset += 64;
                }
            }
        }

        /// <summary>
        /// XOR using AVX2 (256-bit, 32 bytes at a time).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void XorBytesAvx2(ReadOnlySpan<byte> source, Span<byte> target, ref int offset)
        {
            var length = source.Length;
            fixed (byte* srcPtr = source)
            fixed (byte* tgtPtr = target)
            {
                while (offset + 32 <= length)
                {
                    var srcVec = Avx.LoadVector256(srcPtr + offset);
                    var tgtVec = Avx.LoadVector256(tgtPtr + offset);
                    var result = Avx2.Xor(srcVec, tgtVec);
                    Avx.Store(tgtPtr + offset, result);
                    offset += 32;
                }
            }
        }

        /// <summary>
        /// XOR using SSE (128-bit, 16 bytes at a time).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void XorBytesSse(ReadOnlySpan<byte> source, Span<byte> target, ref int offset)
        {
            var length = source.Length;
            fixed (byte* srcPtr = source)
            fixed (byte* tgtPtr = target)
            {
                while (offset + 16 <= length)
                {
                    var srcVec = Sse2.LoadVector128(srcPtr + offset);
                    var tgtVec = Sse2.LoadVector128(tgtPtr + offset);
                    var result = Sse2.Xor(srcVec, tgtVec);
                    Sse2.Store(tgtPtr + offset, result);
                    offset += 16;
                }
            }
        }

        /// <summary>
        /// Performs GF(2^8) multiplication of a byte array by a scalar using SIMD with shuffle-based lookup.
        /// This is the core operation for Reed-Solomon encoding.
        /// </summary>
        /// <param name="data">Data to multiply (modified in place).</param>
        /// <param name="scalar">Scalar multiplier in GF(2^8).</param>
        /// <param name="lowTable">Low nibble lookup table (16 bytes).</param>
        /// <param name="highTable">High nibble lookup table (16 bytes).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void GfMultiplyScalar(
            Span<byte> data,
            byte scalar,
            ReadOnlySpan<byte> lowTable,
            ReadOnlySpan<byte> highTable)
        {
            if (scalar == 0)
            {
                data.Clear();
                return;
            }

            if (scalar == 1)
            {
                return;
            }

            var length = data.Length;
            var offset = 0;

            fixed (byte* dataPtr = data)
            fixed (byte* lowPtr = lowTable)
            fixed (byte* highPtr = highTable)
            {
                if (IsAvx2Supported && length >= 32)
                {
                    GfMultiplyScalarAvx2(dataPtr, length, lowPtr, highPtr, ref offset);
                }
                else if (_detectedCapability >= SimdCapability.Sse41 && length >= 16)
                {
                    GfMultiplyScalarSse(dataPtr, length, lowPtr, highPtr, ref offset);
                }

                // Scalar fallback for remaining bytes - use direct table lookup
                var fullMulTable = GaloisField.Instance.GetMultiplicationRow(scalar);
                for (; offset < length; offset++)
                {
                    dataPtr[offset] = fullMulTable[dataPtr[offset]];
                }
            }
        }

        /// <summary>
        /// GF multiplication using AVX2 shuffle instructions.
        /// Uses two lookup tables for low and high nibbles.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void GfMultiplyScalarAvx2(
            byte* data,
            int length,
            byte* lowTable,
            byte* highTable,
            ref int offset)
        {
            // Load lookup tables into 256-bit vectors (duplicate 16-byte table)
            var lowVec128 = Sse2.LoadVector128(lowTable);
            var highVec128 = Sse2.LoadVector128(highTable);
            var lowVec = Vector256.Create(lowVec128, lowVec128);
            var highVec = Vector256.Create(highVec128, highVec128);

            // Mask for extracting low nibble
            var nibbleMask = Vector256.Create((byte)0x0F);

            while (offset + 32 <= length)
            {
                var dataVec = Avx.LoadVector256(data + offset);

                // Extract low and high nibbles
                var lowNibbles = Avx2.And(dataVec, nibbleMask);
                var highNibbles = Avx2.And(Avx2.ShiftRightLogical(dataVec.AsUInt16(), 4).AsByte(), nibbleMask);

                // Use shuffle as a 16-element lookup table
                var lowResult = Avx2.Shuffle(lowVec, lowNibbles);
                var highResult = Avx2.Shuffle(highVec, highNibbles);

                // XOR the results together
                var result = Avx2.Xor(lowResult, highResult);
                Avx.Store(data + offset, result);

                offset += 32;
            }
        }

        /// <summary>
        /// GF multiplication using SSE shuffle instructions.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void GfMultiplyScalarSse(
            byte* data,
            int length,
            byte* lowTable,
            byte* highTable,
            ref int offset)
        {
            // Load lookup tables
            var lowVec = Sse2.LoadVector128(lowTable);
            var highVec = Sse2.LoadVector128(highTable);

            // Mask for extracting low nibble
            var nibbleMask = Vector128.Create((byte)0x0F);

            while (offset + 16 <= length)
            {
                var dataVec = Sse2.LoadVector128(data + offset);

                // Extract low and high nibbles
                var lowNibbles = Sse2.And(dataVec, nibbleMask);
                var highNibbles = Sse2.And(Sse2.ShiftRightLogical(dataVec.AsUInt16(), 4).AsByte(), nibbleMask);

                // Use shuffle as a 16-element lookup table
                var lowResult = Ssse3.Shuffle(lowVec, lowNibbles);
                var highResult = Ssse3.Shuffle(highVec, highNibbles);

                // XOR the results together
                var result = Sse2.Xor(lowResult, highResult);
                Sse2.Store(data + offset, result);

                offset += 16;
            }
        }

        /// <summary>
        /// Performs GF(2^8) multiply-accumulate: target ^= source * scalar.
        /// This is used extensively in Reed-Solomon encoding for parity generation.
        /// </summary>
        /// <param name="source">Source data.</param>
        /// <param name="target">Target data (accumulator, modified in place).</param>
        /// <param name="scalar">Scalar multiplier in GF(2^8).</param>
        /// <param name="lowTable">Low nibble lookup table (16 bytes).</param>
        /// <param name="highTable">High nibble lookup table (16 bytes).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void GfMultiplyAccumulate(
            ReadOnlySpan<byte> source,
            Span<byte> target,
            byte scalar,
            ReadOnlySpan<byte> lowTable,
            ReadOnlySpan<byte> highTable)
        {
            if (source.Length != target.Length)
            {
                throw new ArgumentException("Source and target must have the same length");
            }

            if (scalar == 0)
            {
                return; // Adding zero has no effect
            }

            var length = source.Length;
            var offset = 0;

            fixed (byte* srcPtr = source)
            fixed (byte* tgtPtr = target)
            fixed (byte* lowPtr = lowTable)
            fixed (byte* highPtr = highTable)
            {
                if (scalar == 1)
                {
                    // Multiplication by 1 is identity, just XOR
                    XorBytesInternal(srcPtr, tgtPtr, length);
                    return;
                }

                if (IsAvx2Supported && length >= 32)
                {
                    GfMultiplyAccumulateAvx2(srcPtr, tgtPtr, length, lowPtr, highPtr, ref offset);
                }
                else if (_detectedCapability >= SimdCapability.Sse41 && length >= 16)
                {
                    GfMultiplyAccumulateSse(srcPtr, tgtPtr, length, lowPtr, highPtr, ref offset);
                }

                // Scalar fallback
                var fullMulTable = GaloisField.Instance.GetMultiplicationRow(scalar);
                for (; offset < length; offset++)
                {
                    tgtPtr[offset] ^= fullMulTable[srcPtr[offset]];
                }
            }
        }

        /// <summary>
        /// Internal XOR using raw pointers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void XorBytesInternal(byte* source, byte* target, int length)
        {
            var offset = 0;

            if (IsAvx2Supported)
            {
                while (offset + 32 <= length)
                {
                    var srcVec = Avx.LoadVector256(source + offset);
                    var tgtVec = Avx.LoadVector256(target + offset);
                    Avx.Store(target + offset, Avx2.Xor(srcVec, tgtVec));
                    offset += 32;
                }
            }

            while (offset + 8 <= length)
            {
                *(ulong*)(target + offset) ^= *(ulong*)(source + offset);
                offset += 8;
            }

            while (offset < length)
            {
                target[offset] ^= source[offset];
                offset++;
            }
        }

        /// <summary>
        /// GF multiply-accumulate using AVX2.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void GfMultiplyAccumulateAvx2(
            byte* source,
            byte* target,
            int length,
            byte* lowTable,
            byte* highTable,
            ref int offset)
        {
            var lowVec128 = Sse2.LoadVector128(lowTable);
            var highVec128 = Sse2.LoadVector128(highTable);
            var lowVec = Vector256.Create(lowVec128, lowVec128);
            var highVec = Vector256.Create(highVec128, highVec128);
            var nibbleMask = Vector256.Create((byte)0x0F);

            while (offset + 32 <= length)
            {
                var srcVec = Avx.LoadVector256(source + offset);
                var tgtVec = Avx.LoadVector256(target + offset);

                // GF multiply source by scalar
                var lowNibbles = Avx2.And(srcVec, nibbleMask);
                var highNibbles = Avx2.And(Avx2.ShiftRightLogical(srcVec.AsUInt16(), 4).AsByte(), nibbleMask);
                var lowResult = Avx2.Shuffle(lowVec, lowNibbles);
                var highResult = Avx2.Shuffle(highVec, highNibbles);
                var mulResult = Avx2.Xor(lowResult, highResult);

                // XOR with target (accumulate)
                var result = Avx2.Xor(tgtVec, mulResult);
                Avx.Store(target + offset, result);

                offset += 32;
            }
        }

        /// <summary>
        /// GF multiply-accumulate using SSE.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void GfMultiplyAccumulateSse(
            byte* source,
            byte* target,
            int length,
            byte* lowTable,
            byte* highTable,
            ref int offset)
        {
            var lowVec = Sse2.LoadVector128(lowTable);
            var highVec = Sse2.LoadVector128(highTable);
            var nibbleMask = Vector128.Create((byte)0x0F);

            while (offset + 16 <= length)
            {
                var srcVec = Sse2.LoadVector128(source + offset);
                var tgtVec = Sse2.LoadVector128(target + offset);

                var lowNibbles = Sse2.And(srcVec, nibbleMask);
                var highNibbles = Sse2.And(Sse2.ShiftRightLogical(srcVec.AsUInt16(), 4).AsByte(), nibbleMask);
                var lowResult = Ssse3.Shuffle(lowVec, lowNibbles);
                var highResult = Ssse3.Shuffle(highVec, highNibbles);
                var mulResult = Sse2.Xor(lowResult, highResult);

                var result = Sse2.Xor(tgtVec, mulResult);
                Sse2.Store(target + offset, result);

                offset += 16;
            }
        }

        /// <summary>
        /// Copies bytes using SIMD acceleration.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyBytes(ReadOnlySpan<byte> source, Span<byte> target)
        {
            source.CopyTo(target);
        }

        /// <summary>
        /// Clears bytes using SIMD acceleration.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearBytes(Span<byte> data)
        {
            data.Clear();
        }

        /// <summary>
        /// Gets information about the current SIMD capabilities.
        /// </summary>
        public static SimdInfo GetSimdInfo()
        {
            return new SimdInfo
            {
                Capability = _detectedCapability,
                VectorSizeBytes = _vectorSize,
                Sse2Supported = Sse2.IsSupported,
                Sse41Supported = Sse41.IsSupported,
                Ssse3Supported = Ssse3.IsSupported,
                Avx2Supported = Avx2.IsSupported,
                Avx512FSupported = Avx512F.IsSupported,
                Avx512BWSupported = Avx512BW.IsSupported
            };
        }
    }

    /// <summary>
    /// Information about SIMD capabilities.
    /// </summary>
    public sealed class SimdInfo
    {
        /// <summary>Highest supported SIMD capability.</summary>
        public SimdOperations.SimdCapability Capability { get; init; }

        /// <summary>Optimal vector size in bytes.</summary>
        public int VectorSizeBytes { get; init; }

        /// <summary>Whether SSE2 is supported.</summary>
        public bool Sse2Supported { get; init; }

        /// <summary>Whether SSE4.1 is supported.</summary>
        public bool Sse41Supported { get; init; }

        /// <summary>Whether SSSE3 is supported.</summary>
        public bool Ssse3Supported { get; init; }

        /// <summary>Whether AVX2 is supported.</summary>
        public bool Avx2Supported { get; init; }

        /// <summary>Whether AVX-512F is supported.</summary>
        public bool Avx512FSupported { get; init; }

        /// <summary>Whether AVX-512BW is supported.</summary>
        public bool Avx512BWSupported { get; init; }

        /// <summary>Returns a human-readable summary.</summary>
        public override string ToString()
        {
            return $"SIMD: {Capability} (VectorSize={VectorSizeBytes}B, AVX2={Avx2Supported}, AVX512={Avx512FSupported})";
        }
    }
}
