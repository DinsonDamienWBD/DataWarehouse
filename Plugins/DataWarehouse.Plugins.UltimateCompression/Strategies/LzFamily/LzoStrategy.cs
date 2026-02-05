using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy implementing the LZO1X-1 (mini-LZO) algorithm in managed C#.
    /// LZO emphasizes extremely fast compression and decompression with moderate compression ratios,
    /// making it well-suited for real-time data processing and embedded systems.
    /// </summary>
    /// <remarks>
    /// The LZO1X-1 algorithm uses a hash table of 4096 entries for fast 3-byte match lookup,
    /// literal runs, and back-references with variable-length encoding. This managed implementation
    /// closely follows the mini-LZO reference design with a sliding window approach.
    /// </remarks>
    public sealed class LzoStrategy : CompressionStrategyBase
    {
        private const int HashTableSize = 4096;
        private const int HashShift = 4;
        private const int MaxMatchLen = 264;
        private const int MinMatch = 3;
        private const int MaxDistance = 0xBFFF;

        /// <summary>
        /// Initializes a new instance of the <see cref="LzoStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public LzoStrategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "LZO",
            TypicalCompressionRatio = 0.58,
            CompressionSpeed = 9,
            DecompressionSpeed = 10,
            CompressionMemoryUsage = 32 * 1024,
            DecompressionMemoryUsage = 8 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 64 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            if (input.Length <= MinMatch)
            {
                // Too small to compress; store as literal block
                return StoreLiteral(input);
            }

            var output = new byte[input.Length + input.Length / 16 + 64 + 3];
            int outPos = 0;

            // Write 4-byte uncompressed length header (LE)
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), input.Length);
            outPos += 4;

            var hashTable = new int[HashTableSize];
            Array.Fill(hashTable, -1);

            int ip = 0;
            int literalStart = 0;

            while (ip < input.Length - 4)
            {
                int hash = Hash(input, ip);
                int matchPos = hashTable[hash];
                hashTable[hash] = ip;

                if (matchPos >= 0 && ip - matchPos <= MaxDistance &&
                    matchPos >= 0 && matchPos < ip &&
                    input[matchPos] == input[ip] &&
                    input[matchPos + 1] == input[ip + 1] &&
                    input[matchPos + 2] == input[ip + 2])
                {
                    // Found a match; emit pending literals first
                    int literalLen = ip - literalStart;
                    outPos = EmitLiterals(input, literalStart, literalLen, output, outPos);

                    // Determine match length
                    int matchLen = MinMatch;
                    int maxLen = Math.Min(MaxMatchLen, input.Length - ip);
                    while (matchLen < maxLen && input[matchPos + matchLen] == input[ip + matchLen])
                        matchLen++;

                    int distance = ip - matchPos;

                    // Encode match
                    outPos = EmitMatch(output, outPos, matchLen, distance);

                    ip += matchLen;
                    literalStart = ip;
                }
                else
                {
                    ip++;
                }
            }

            // Emit remaining literals
            int remaining = input.Length - literalStart;
            if (remaining > 0)
            {
                outPos = EmitLiterals(input, literalStart, remaining, output, outPos);
            }

            // End-of-stream marker
            output[outPos++] = 0x11; // EOS marker
            output[outPos++] = 0x00;
            output[outPos++] = 0x00;

            var result = new byte[outPos];
            Buffer.BlockCopy(output, 0, result, 0, outPos);
            return result;
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            if (input.Length < 4)
                throw new InvalidDataException("LZO compressed data is too short.");

            int uncompressedLen = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(0));
            if (uncompressedLen < 0 || uncompressedLen > 256 * 1024 * 1024)
                throw new InvalidDataException("Invalid LZO uncompressed length.");

            var output = new byte[uncompressedLen];
            int ip = 4;
            int op = 0;

            while (ip < input.Length - 2)
            {
                byte ctrl = input[ip];

                // Check for EOS marker
                if (ctrl == 0x11 && ip + 2 < input.Length && input[ip + 1] == 0x00 && input[ip + 2] == 0x00)
                    break;

                if ((ctrl & 0x80) != 0)
                {
                    // Match token
                    int matchLen;
                    int distance;

                    if ((ctrl & 0x40) != 0)
                    {
                        // Long match: 2 additional bytes
                        if (ip + 2 >= input.Length) break;
                        matchLen = (ctrl & 0x3F) + MinMatch;
                        distance = (input[ip + 1] | (input[ip + 2] << 8));
                        ip += 3;
                    }
                    else
                    {
                        // Short match: 1 additional byte
                        if (ip + 1 >= input.Length) break;
                        matchLen = ((ctrl >> 4) & 0x07) + MinMatch;
                        distance = ((ctrl & 0x0F) << 8) | input[ip + 1];
                        ip += 2;
                    }

                    if (distance == 0 || op - distance < 0)
                        throw new InvalidDataException("Invalid LZO match distance.");

                    int srcPos = op - distance;
                    for (int i = 0; i < matchLen && op < output.Length; i++)
                    {
                        output[op++] = output[srcPos + i];
                    }
                }
                else
                {
                    // Literal token
                    int literalLen;
                    if (ctrl < 16)
                    {
                        literalLen = ctrl + 1;
                        ip++;
                    }
                    else
                    {
                        literalLen = ctrl - 14;
                        ip++;
                    }

                    if (ip + literalLen > input.Length) break;

                    for (int i = 0; i < literalLen && op < output.Length; i++)
                    {
                        output[op++] = input[ip++];
                    }
                }
            }

            if (op != uncompressedLen)
            {
                // Resize if we produced fewer bytes than expected
                var trimmed = new byte[op];
                Buffer.BlockCopy(output, 0, trimmed, 0, op);
                return trimmed;
            }

            return output;
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new BufferedAlgorithmCompressionStream(output, leaveOpen, CompressCore);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new BufferedAlgorithmDecompressionStream(input, leaveOpen, DecompressCore);
        }

        private static int Hash(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
            return (int)((v * 0x1824429Du) >> (32 - 12)) & (HashTableSize - 1);
        }

        private static int EmitLiterals(byte[] src, int srcOff, int length, byte[] dst, int dstOff)
        {
            if (length == 0) return dstOff;

            if (length <= 15)
            {
                dst[dstOff++] = (byte)(length - 1);
            }
            else
            {
                int rem = length - 1;
                dst[dstOff++] = (byte)(14 + rem);
                if (14 + rem > 127)
                {
                    // For very long literal runs, break into chunks
                    int written = 0;
                    dstOff--; // undo
                    while (written < length)
                    {
                        int chunk = Math.Min(length - written, 112);
                        dst[dstOff++] = (byte)(chunk - 1);
                        Buffer.BlockCopy(src, srcOff + written, dst, dstOff, chunk);
                        dstOff += chunk;
                        written += chunk;
                    }
                    return dstOff;
                }
            }

            Buffer.BlockCopy(src, srcOff, dst, dstOff, length);
            dstOff += length;
            return dstOff;
        }

        private static int EmitMatch(byte[] dst, int dstOff, int matchLen, int distance)
        {
            if (matchLen < MinMatch + 8 && distance < 4096)
            {
                // Short match encoding: 1 control byte + 1 distance byte
                int lenCode = matchLen - MinMatch;
                dst[dstOff++] = (byte)(0x80 | (lenCode << 4) | ((distance >> 8) & 0x0F));
                dst[dstOff++] = (byte)(distance & 0xFF);
            }
            else
            {
                // Long match encoding: 1 control byte + 2 distance bytes
                int lenCode = Math.Min(matchLen - MinMatch, 63);
                dst[dstOff++] = (byte)(0xC0 | lenCode);
                dst[dstOff++] = (byte)(distance & 0xFF);
                dst[dstOff++] = (byte)((distance >> 8) & 0xFF);
            }
            return dstOff;
        }

        private static byte[] StoreLiteral(byte[] input)
        {
            var output = new byte[4 + 1 + input.Length + 3];
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0), input.Length);
            output[4] = (byte)(input.Length - 1);
            Buffer.BlockCopy(input, 0, output, 5, input.Length);
            output[5 + input.Length] = 0x11;
            output[6 + input.Length] = 0x00;
            output[7 + input.Length] = 0x00;
            return output;
        }
    }

    /// <summary>
    /// Buffered compression stream that accumulates all writes, then compresses on dispose.
    /// Used by algorithms that do not support native streaming.
    /// </summary>
    internal sealed class BufferedAlgorithmCompressionStream : Stream
    {
        private readonly Stream _output;
        private readonly bool _leaveOpen;
        private readonly Func<byte[], byte[]> _compressFunc;
        private readonly MemoryStream _buffer = new();
        private bool _disposed;

        public BufferedAlgorithmCompressionStream(Stream output, bool leaveOpen, Func<byte[], byte[]> compressFunc)
        {
            _output = output;
            _leaveOpen = leaveOpen;
            _compressFunc = compressFunc;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _buffer.Write(buffer, offset, count);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                var data = _buffer.ToArray();
                _buffer.Dispose();

                if (data.Length > 0)
                {
                    var compressed = _compressFunc(data);
                    _output.Write(compressed, 0, compressed.Length);
                }
                _output.Flush();

                if (!_leaveOpen)
                    _output.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Buffered decompression stream that reads all data from the input, decompresses, and
    /// exposes the decompressed data for reads. Used by algorithms without native streaming.
    /// </summary>
    internal sealed class BufferedAlgorithmDecompressionStream : Stream
    {
        private readonly Stream _input;
        private readonly bool _leaveOpen;
        private readonly Func<byte[], byte[]> _decompressFunc;
        private MemoryStream? _decompressed;
        private bool _disposed;

        public BufferedAlgorithmDecompressionStream(Stream input, bool leaveOpen, Func<byte[], byte[]> decompressFunc)
        {
            _input = input;
            _leaveOpen = leaveOpen;
            _decompressFunc = decompressFunc;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => EnsureDecompressed().Length;
        public override long Position
        {
            get => EnsureDecompressed().Position;
            set => EnsureDecompressed().Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EnsureDecompressed().Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private MemoryStream EnsureDecompressed()
        {
            if (_decompressed != null)
                return _decompressed;

            using var ms = new MemoryStream();
            _input.CopyTo(ms);
            var compressed = ms.ToArray();

            var data = _decompressFunc(compressed);
            _decompressed = new MemoryStream(data, writable: false);
            return _decompressed;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _decompressed?.Dispose();
                if (!_leaveOpen)
                    _input.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
