using System;
using System.Buffers.Binary;
using System.IO;
using DataWarehouse.SDK.Contracts.Compression;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.LzFamily
{
    /// <summary>
    /// Compression strategy implementing the classic LZ77 sliding window algorithm.
    /// Uses a 32KB window, up to 258-byte matches, and a hash chain for efficient
    /// match finding. Output is encoded as a sequence of literal and match tokens.
    /// </summary>
    /// <remarks>
    /// LZ77 (Lempel-Ziv 1977) is the foundational algorithm for most modern LZ-family
    /// compressors. It scans input data using a sliding window and replaces repeated
    /// occurrences with back-references (distance, length pairs). This implementation
    /// uses hash chains indexed by 3-byte sequences for efficient match lookup.
    /// </remarks>
    public sealed class Lz77Strategy : CompressionStrategyBase
    {
        private const int WindowSize = 32768;     // 32 KB sliding window
        private const int MaxMatchLength = 258;
        private const int MinMatch = 3;
        private const int HashBits = 15;
        private const int HashSize = 1 << HashBits;
        private const int HashMask = HashSize - 1;
        private const int MaxChainLength = 128;

        /// <summary>
        /// Initializes a new instance of the <see cref="Lz77Strategy"/> class
        /// with the default compression level.
        /// </summary>
        public Lz77Strategy() : base(CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "LZ77",
            TypicalCompressionRatio = 0.45,
            CompressionSpeed = 5,
            DecompressionSpeed = 8,
            CompressionMemoryUsage = 256 * 1024,
            DecompressionMemoryUsage = 64 * 1024,
            SupportsStreaming = false,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = false,
            MinimumRecommendedSize = 128,
            OptimalBlockSize = 32 * 1024
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            if (input.Length < MinMatch)
                return CreateLiteralBlock(input);

            using var output = new MemoryStream(input.Length);
            var writer = new BinaryWriter(output);

            // Header: uncompressed length
            writer.Write(input.Length);

            var head = new int[HashSize];
            var prev = new int[WindowSize];
            Array.Fill(head, -1);

            int pos = 0;
            var literals = new MemoryStream();

            while (pos < input.Length)
            {
                int bestLen = MinMatch - 1;
                int bestDist = 0;

                if (pos + MinMatch <= input.Length)
                {
                    int hash = HashFunc(input, pos);
                    int chainPos = head[hash];

                    // Update hash chain
                    prev[pos & (WindowSize - 1)] = head[hash];
                    head[hash] = pos;

                    int chainCount = 0;
                    while (chainPos >= 0 && chainPos >= pos - WindowSize && chainCount < MaxChainLength)
                    {
                        int matchLen = 0;
                        int maxLen = Math.Min(MaxMatchLength, input.Length - pos);

                        while (matchLen < maxLen && input[chainPos + matchLen] == input[pos + matchLen])
                            matchLen++;

                        if (matchLen > bestLen)
                        {
                            bestLen = matchLen;
                            bestDist = pos - chainPos;
                            if (matchLen == MaxMatchLength) break;
                        }

                        chainPos = prev[chainPos & (WindowSize - 1)];
                        chainCount++;
                    }
                }

                if (bestLen >= MinMatch)
                {
                    // Flush any pending literals
                    FlushLiterals(writer, literals);

                    // Write match token: 0xFF marker, then 2-byte length, 2-byte distance
                    writer.Write((byte)0xFF);
                    writer.Write((ushort)bestLen);
                    writer.Write((ushort)bestDist);

                    // Update hash chain for skipped positions
                    for (int i = 1; i < bestLen && pos + i + MinMatch <= input.Length; i++)
                    {
                        int h = HashFunc(input, pos + i);
                        prev[(pos + i) & (WindowSize - 1)] = head[h];
                        head[h] = pos + i;
                    }

                    pos += bestLen;
                }
                else
                {
                    literals.WriteByte(input[pos]);
                    pos++;
                }
            }

            FlushLiterals(writer, literals);
            literals.Dispose();

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            if (input.Length < 4)
                throw new InvalidDataException("LZ77 compressed data is too short.");

            using var stream = new MemoryStream(input);
            var reader = new BinaryReader(stream);

            int uncompressedLen = reader.ReadInt32();
            if (uncompressedLen < 0 || uncompressedLen > 256 * 1024 * 1024)
                throw new InvalidDataException("Invalid LZ77 uncompressed length.");

            var output = new byte[uncompressedLen];
            int op = 0;

            while (stream.Position < stream.Length && op < uncompressedLen)
            {
                byte tag = reader.ReadByte();

                if (tag == 0xFF)
                {
                    // Match token
                    if (stream.Position + 4 > stream.Length)
                        throw new InvalidDataException("Truncated LZ77 match token.");

                    int matchLen = reader.ReadUInt16();
                    int distance = reader.ReadUInt16();

                    if (distance == 0 || op - distance < 0)
                        throw new InvalidDataException("Invalid LZ77 match distance.");

                    int srcPos = op - distance;
                    for (int i = 0; i < matchLen && op < uncompressedLen; i++)
                    {
                        output[op++] = output[srcPos + i];
                    }
                }
                else if (tag == 0xFE)
                {
                    // Literal run
                    if (stream.Position + 2 > stream.Length)
                        throw new InvalidDataException("Truncated LZ77 literal header.");

                    int literalLen = reader.ReadUInt16();
                    if (stream.Position + literalLen > stream.Length)
                        throw new InvalidDataException("Truncated LZ77 literal data.");

                    int bytesRead = reader.Read(output, op, Math.Min(literalLen, uncompressedLen - op));
                    op += bytesRead;
                }
                else
                {
                    // Single literal byte (tag < 0xFE)
                    output[op++] = tag;
                }
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

        private static int HashFunc(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
            return (int)((v * 0x9E3779B1u) >> (32 - HashBits)) & HashMask;
        }

        private static void FlushLiterals(BinaryWriter writer, MemoryStream literals)
        {
            if (literals.Length == 0) return;

            var data = literals.ToArray();

            if (data.Length == 1 && data[0] != 0xFF && data[0] != 0xFE)
            {
                // Single literal that is not a control byte
                writer.Write(data[0]);
            }
            else
            {
                // Literal run
                writer.Write((byte)0xFE);
                writer.Write((ushort)data.Length);
                writer.Write(data);
            }

            literals.SetLength(0);
        }

        private static byte[] CreateLiteralBlock(byte[] input)
        {
            using var output = new MemoryStream(input.Length + 256);
            var writer = new BinaryWriter(output);
            writer.Write(input.Length);
            if (input.Length > 0)
            {
                writer.Write((byte)0xFE);
                writer.Write((ushort)input.Length);
                writer.Write(input);
            }
            return output.ToArray();
        }
    }
}
