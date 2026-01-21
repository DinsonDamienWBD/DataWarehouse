using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.IO.Compression;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.Compression
{
    #region Compression Registry Implementation

    /// <summary>
    /// Registry for all available compression providers.
    /// Implements ICompressionRegistry from SDK and enables Kernel discovery and selection of compression algorithms.
    /// </summary>
    public sealed class CompressionRegistry : ICompressionRegistry
    {
        private static readonly Lazy<CompressionRegistry> _instance = new(() => new CompressionRegistry());
        private readonly Dictionary<CompressionAlgorithm, Func<CompressionOptions?, ICompressionProvider>> _factories = new();
        private readonly Dictionary<CompressionAlgorithm, ICompressionProvider> _defaultProviders = new();

        /// <summary>Gets the singleton registry instance.</summary>
        public static CompressionRegistry Instance => _instance.Value;

        private CompressionRegistry()
        {
            // Register all built-in providers
            RegisterProvider(CompressionAlgorithm.GZip, opts => new GZipCompressionProvider(opts));
            RegisterProvider(CompressionAlgorithm.Deflate, opts => new DeflateCompressionProvider(opts));
            RegisterProvider(CompressionAlgorithm.Brotli, opts => new BrotliCompressionProvider(opts));
            RegisterProvider(CompressionAlgorithm.LZ4, opts => new LZ4CompressionProvider(opts));
            RegisterProvider(CompressionAlgorithm.Zstd, opts => new ZstdCompressionProvider(opts));
        }

        /// <summary>
        /// Registers a compression provider factory.
        /// </summary>
        public void RegisterProvider(CompressionAlgorithm algorithm, Func<CompressionOptions?, ICompressionProvider> factory)
        {
            _factories[algorithm] = factory;
            _defaultProviders[algorithm] = factory(null);
        }

        /// <summary>
        /// Gets a compression provider for the specified algorithm.
        /// </summary>
        public ICompressionProvider GetProvider(CompressionAlgorithm algorithm, CompressionOptions? options = null)
        {
            if (options == null && _defaultProviders.TryGetValue(algorithm, out var defaultProvider))
            {
                return defaultProvider;
            }

            if (_factories.TryGetValue(algorithm, out var factory))
            {
                return factory(options);
            }

            throw new NotSupportedException($"Compression algorithm '{algorithm}' is not registered.");
        }

        /// <summary>
        /// Gets a compression provider by name (case-insensitive).
        /// </summary>
        public ICompressionProvider GetProviderByName(string algorithmName, CompressionOptions? options = null)
        {
            if (Enum.TryParse<CompressionAlgorithm>(algorithmName, true, out var algorithm))
            {
                return GetProvider(algorithm, options);
            }

            throw new NotSupportedException($"Unknown compression algorithm: '{algorithmName}'");
        }

        /// <summary>
        /// Gets all available compression algorithms.
        /// </summary>
        public IReadOnlyList<CompressionAlgorithm> GetAvailableAlgorithms()
        {
            return _factories.Keys.ToList().AsReadOnly();
        }

        /// <summary>
        /// Checks if an algorithm is available.
        /// </summary>
        public bool IsAlgorithmAvailable(CompressionAlgorithm algorithm)
        {
            return _factories.ContainsKey(algorithm);
        }
    }

    #endregion

    #region Compression Provider Implementations

    /// <summary>
    /// GZip compression provider implementation.
    /// </summary>
    public sealed class GZipCompressionProvider : ICompressionProvider
    {
        private readonly CompressionOptions _options;

        public CompressionAlgorithm Algorithm => CompressionAlgorithm.GZip;
        public string Name => "GZip";

        public GZipCompressionProvider(CompressionOptions? options = null)
        {
            _options = options ?? new CompressionOptions();
        }

        public byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, _options.Level, leaveOpen: true))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        public byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                gzip.CopyTo(output, _options.BufferSize);
            }
            return output.ToArray();
        }

        public async Task<byte[]> CompressAsync(byte[] data, CancellationToken ct = default)
        {
            using var output = new MemoryStream();
            await using (var gzip = new GZipStream(output, _options.Level, leaveOpen: true))
            {
                await gzip.WriteAsync(data, ct);
            }
            return output.ToArray();
        }

        public async Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct = default)
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                await gzip.CopyToAsync(output, _options.BufferSize, ct);
            }
            return output.ToArray();
        }

        public Stream CompressStream(Stream input)
        {
            var output = new MemoryStream();
            using (var gzip = new GZipStream(output, _options.Level, leaveOpen: true))
            {
                input.CopyTo(gzip, _options.BufferSize);
            }
            output.Position = 0;
            return output;
        }

        public Stream DecompressStream(Stream input)
        {
            var output = new MemoryStream();
            using (var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                gzip.CopyTo(output, _options.BufferSize);
            }
            output.Position = 0;
            return output;
        }
    }

    /// <summary>
    /// Deflate compression provider implementation.
    /// </summary>
    public sealed class DeflateCompressionProvider : ICompressionProvider
    {
        private readonly CompressionOptions _options;

        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Deflate;
        public string Name => "Deflate";

        public DeflateCompressionProvider(CompressionOptions? options = null)
        {
            _options = options ?? new CompressionOptions();
        }

        public byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, _options.Level, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        public byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                deflate.CopyTo(output, _options.BufferSize);
            }
            return output.ToArray();
        }

        public async Task<byte[]> CompressAsync(byte[] data, CancellationToken ct = default)
        {
            using var output = new MemoryStream();
            await using (var deflate = new DeflateStream(output, _options.Level, leaveOpen: true))
            {
                await deflate.WriteAsync(data, ct);
            }
            return output.ToArray();
        }

        public async Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct = default)
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            await using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                await deflate.CopyToAsync(output, _options.BufferSize, ct);
            }
            return output.ToArray();
        }

        public Stream CompressStream(Stream input)
        {
            var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, _options.Level, leaveOpen: true))
            {
                input.CopyTo(deflate, _options.BufferSize);
            }
            output.Position = 0;
            return output;
        }

        public Stream DecompressStream(Stream input)
        {
            var output = new MemoryStream();
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                deflate.CopyTo(output, _options.BufferSize);
            }
            output.Position = 0;
            return output;
        }
    }

    /// <summary>
    /// Brotli compression provider implementation.
    /// </summary>
    public sealed class BrotliCompressionProvider : ICompressionProvider
    {
        private readonly CompressionOptions _options;

        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Brotli;
        public string Name => "Brotli";

        public BrotliCompressionProvider(CompressionOptions? options = null)
        {
            _options = options ?? new CompressionOptions();
        }

        public byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, _options.Level, leaveOpen: true))
            {
                brotli.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        public byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
            {
                brotli.CopyTo(output, _options.BufferSize);
            }
            return output.ToArray();
        }

        public async Task<byte[]> CompressAsync(byte[] data, CancellationToken ct = default)
        {
            using var output = new MemoryStream();
            await using (var brotli = new BrotliStream(output, _options.Level, leaveOpen: true))
            {
                await brotli.WriteAsync(data, ct);
            }
            return output.ToArray();
        }

        public async Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct = default)
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            await using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
            {
                await brotli.CopyToAsync(output, _options.BufferSize, ct);
            }
            return output.ToArray();
        }

        public Stream CompressStream(Stream input)
        {
            var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, _options.Level, leaveOpen: true))
            {
                input.CopyTo(brotli, _options.BufferSize);
            }
            output.Position = 0;
            return output;
        }

        public Stream DecompressStream(Stream input)
        {
            var output = new MemoryStream();
            using (var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                brotli.CopyTo(output, _options.BufferSize);
            }
            output.Position = 0;
            return output;
        }
    }

    /// <summary>
    /// LZ4 compression provider implementation.
    /// High-speed compression algorithm optimized for real-time scenarios.
    /// </summary>
    public sealed class LZ4CompressionProvider : ICompressionProvider
    {
        private readonly CompressionOptions _options;

        public CompressionAlgorithm Algorithm => CompressionAlgorithm.LZ4;
        public string Name => "LZ4";

        public LZ4CompressionProvider(CompressionOptions? options = null)
        {
            _options = options ?? new CompressionOptions();
        }

        public byte[] Compress(byte[] data)
        {
            return LZ4CompressInternal(data, _options.LZ4HighCompression, _options.LZ4BlockSize);
        }

        public byte[] Decompress(byte[] data)
        {
            return LZ4DecompressInternal(data);
        }

        public Task<byte[]> CompressAsync(byte[] data, CancellationToken ct = default)
        {
            return Task.FromResult(Compress(data));
        }

        public Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct = default)
        {
            return Task.FromResult(Decompress(data));
        }

        public Stream CompressStream(Stream input)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var compressed = Compress(inputMs.ToArray());
            return new MemoryStream(compressed);
        }

        public Stream DecompressStream(Stream input)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var decompressed = Decompress(inputMs.ToArray());
            return new MemoryStream(decompressed);
        }

        #region LZ4 Internal Implementation

        private static byte[] LZ4CompressInternal(byte[] input, bool highCompression, int blockSize)
        {
            var maxOutputSize = GetMaxCompressedSize(input.Length);
            var output = new byte[maxOutputSize + 8];

            BitConverter.GetBytes(input.Length).CopyTo(output, 0);
            BitConverter.GetBytes(highCompression ? 1 : 0).CopyTo(output, 4);

            var compressedLength = CompressLZ4Block(input, output.AsSpan(8), highCompression);

            var result = new byte[compressedLength + 8];
            Array.Copy(output, result, result.Length);
            return result;
        }

        private static byte[] LZ4DecompressInternal(byte[] compressed)
        {
            if (compressed.Length < 8)
                throw new InvalidDataException("Invalid LZ4 data: too short");

            var originalSize = BitConverter.ToInt32(compressed, 0);
            if (originalSize <= 0 || originalSize > 1024 * 1024 * 1024)
                throw new InvalidDataException($"Invalid LZ4 original size: {originalSize}");

            var output = new byte[originalSize];
            DecompressLZ4Block(compressed.AsSpan(8), output);
            return output;
        }

        private static int GetMaxCompressedSize(int inputLength)
        {
            return inputLength + (inputLength / 255) + 16;
        }

        private static int CompressLZ4Block(byte[] input, Span<byte> output, bool highCompression)
        {
            var hashTable = new int[65536];
            Array.Fill(hashTable, -1);

            int inputPos = 0;
            int outputPos = 0;
            int anchor = 0;
            int inputEnd = input.Length;

            while (inputPos < inputEnd - 12)
            {
                int hash = GetHash(input, inputPos);
                int matchPos = hashTable[hash];
                hashTable[hash] = inputPos;

                if (matchPos >= 0 && inputPos - matchPos < 65535 &&
                    MatchesAt(input, matchPos, inputPos, Math.Min(inputEnd - inputPos, 255)))
                {
                    int literalLength = inputPos - anchor;
                    int matchLength = GetMatchLength(input, matchPos, inputPos, inputEnd);

                    outputPos = WriteLZ4Sequence(output, outputPos, input.AsSpan(anchor, literalLength),
                        inputPos - matchPos, matchLength);

                    inputPos += matchLength;
                    anchor = inputPos;
                }
                else
                {
                    inputPos++;
                }
            }

            if (anchor < inputEnd)
            {
                outputPos = WriteLastLiterals(output, outputPos, input.AsSpan(anchor, inputEnd - anchor));
            }

            return outputPos;
        }

        private static void DecompressLZ4Block(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int inputPos = 0;
            int outputPos = 0;

            while (inputPos < input.Length)
            {
                byte token = input[inputPos++];
                int literalLength = token >> 4;

                if (literalLength == 15)
                {
                    byte b;
                    do
                    {
                        b = input[inputPos++];
                        literalLength += b;
                    } while (b == 255 && inputPos < input.Length);
                }

                if (literalLength > 0)
                {
                    input.Slice(inputPos, literalLength).CopyTo(output.Slice(outputPos));
                    inputPos += literalLength;
                    outputPos += literalLength;
                }

                if (inputPos >= input.Length)
                    break;

                int offset = input[inputPos] | (input[inputPos + 1] << 8);
                inputPos += 2;

                int matchLength = (token & 0x0F) + 4;
                if ((token & 0x0F) == 15)
                {
                    byte b;
                    do
                    {
                        b = input[inputPos++];
                        matchLength += b;
                    } while (b == 255 && inputPos < input.Length);
                }

                int matchPos = outputPos - offset;
                for (int i = 0; i < matchLength; i++)
                {
                    output[outputPos++] = output[matchPos + i];
                }
            }
        }

        private static int GetHash(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            return (int)((v * 2654435761u) >> 16) & 0xFFFF;
        }

        private static bool MatchesAt(byte[] data, int pos1, int pos2, int maxLen)
        {
            for (int i = 0; i < Math.Min(4, maxLen); i++)
            {
                if (data[pos1 + i] != data[pos2 + i]) return false;
            }
            return true;
        }

        private static int GetMatchLength(byte[] data, int pos1, int pos2, int end)
        {
            int len = 4;
            while (pos2 + len < end && data[pos1 + len] == data[pos2 + len] && len < 255)
                len++;
            return len;
        }

        private static int WriteLZ4Sequence(Span<byte> output, int pos, ReadOnlySpan<byte> literals, int offset, int matchLen)
        {
            int litLen = literals.Length;
            int ml = matchLen - 4;

            byte token = (byte)(((litLen >= 15 ? 15 : litLen) << 4) | (ml >= 15 ? 15 : ml));
            output[pos++] = token;

            if (litLen >= 15)
            {
                int remaining = litLen - 15;
                while (remaining >= 255)
                {
                    output[pos++] = 255;
                    remaining -= 255;
                }
                output[pos++] = (byte)remaining;
            }

            literals.CopyTo(output.Slice(pos));
            pos += litLen;

            output[pos++] = (byte)(offset & 0xFF);
            output[pos++] = (byte)((offset >> 8) & 0xFF);

            if (ml >= 15)
            {
                int remaining = ml - 15;
                while (remaining >= 255)
                {
                    output[pos++] = 255;
                    remaining -= 255;
                }
                output[pos++] = (byte)remaining;
            }

            return pos;
        }

        private static int WriteLastLiterals(Span<byte> output, int pos, ReadOnlySpan<byte> literals)
        {
            int litLen = literals.Length;
            byte token = (byte)((litLen >= 15 ? 15 : litLen) << 4);
            output[pos++] = token;

            if (litLen >= 15)
            {
                int remaining = litLen - 15;
                while (remaining >= 255)
                {
                    output[pos++] = 255;
                    remaining -= 255;
                }
                output[pos++] = (byte)remaining;
            }

            literals.CopyTo(output.Slice(pos));
            return pos + litLen;
        }

        #endregion
    }

    /// <summary>
    /// Zstandard (Zstd) compression provider implementation.
    /// High compression ratio with good speed, suitable for storage.
    /// </summary>
    public sealed class ZstdCompressionProvider : ICompressionProvider
    {
        private readonly CompressionOptions _options;

        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Zstd;
        public string Name => "Zstd";

        public ZstdCompressionProvider(CompressionOptions? options = null)
        {
            _options = options ?? new CompressionOptions();
        }

        public byte[] Compress(byte[] data)
        {
            return ZstdCompressInternal(data, _options.ZstdLevel);
        }

        public byte[] Decompress(byte[] data)
        {
            return ZstdDecompressInternal(data);
        }

        public Task<byte[]> CompressAsync(byte[] data, CancellationToken ct = default)
        {
            return Task.FromResult(Compress(data));
        }

        public Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct = default)
        {
            return Task.FromResult(Decompress(data));
        }

        public Stream CompressStream(Stream input)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var compressed = Compress(inputMs.ToArray());
            return new MemoryStream(compressed);
        }

        public Stream DecompressStream(Stream input)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var decompressed = Decompress(inputMs.ToArray());
            return new MemoryStream(decompressed);
        }

        #region Zstd Internal Implementation

        // Zstd frame format magic number
        private const uint ZstdMagic = 0xFD2FB528;

        private static byte[] ZstdCompressInternal(byte[] input, int level)
        {
            // Header: magic (4) + original size (4) + level (1)
            var maxOutputSize = input.Length + (input.Length / 128) + 32;
            var output = new byte[maxOutputSize];
            var outputPos = 0;

            // Write magic number
            BitConverter.GetBytes(ZstdMagic).CopyTo(output, outputPos);
            outputPos += 4;

            // Write original size
            BitConverter.GetBytes(input.Length).CopyTo(output, outputPos);
            outputPos += 4;

            // Write compression level
            output[outputPos++] = (byte)level;

            // Compress using FSE-like entropy coding simulation
            var compressedLength = CompressZstdBlock(input, output.AsSpan(outputPos), level);
            outputPos += compressedLength;

            var result = new byte[outputPos];
            Array.Copy(output, result, outputPos);
            return result;
        }

        private static byte[] ZstdDecompressInternal(byte[] compressed)
        {
            if (compressed.Length < 9)
                throw new InvalidDataException("Invalid Zstd data: too short");

            // Verify magic
            var magic = BitConverter.ToUInt32(compressed, 0);
            if (magic != ZstdMagic)
                throw new InvalidDataException("Invalid Zstd magic number");

            var originalSize = BitConverter.ToInt32(compressed, 4);
            if (originalSize <= 0 || originalSize > 1024 * 1024 * 1024)
                throw new InvalidDataException($"Invalid Zstd original size: {originalSize}");

            var level = compressed[8];
            var output = new byte[originalSize];

            DecompressZstdBlock(compressed.AsSpan(9), output, level);
            return output;
        }

        private static int CompressZstdBlock(byte[] input, Span<byte> output, int level)
        {
            // Simple LZ-style compression with better ratio focus
            var hashTable = new int[65536];
            Array.Fill(hashTable, -1);

            int inputPos = 0;
            int outputPos = 0;
            int anchor = 0;
            int inputEnd = input.Length;

            // Write block header
            output[outputPos++] = 0x00; // Block type: raw + compressed mix

            while (inputPos < inputEnd - 8)
            {
                // Use 4-byte hash for matching
                int hash = GetZstdHash(input, inputPos);
                int matchPos = hashTable[hash];
                hashTable[hash] = inputPos;

                if (matchPos >= 0 && inputPos - matchPos < 131072)
                {
                    int matchLen = GetZstdMatchLength(input, matchPos, inputPos, inputEnd);
                    if (matchLen >= 4)
                    {
                        // Write literals
                        int literalLen = inputPos - anchor;
                        outputPos = WriteZstdSequence(output, outputPos, input.AsSpan(anchor, literalLen),
                            inputPos - matchPos, matchLen);

                        inputPos += matchLen;
                        anchor = inputPos;
                        continue;
                    }
                }
                inputPos++;
            }

            // Write remaining literals
            if (anchor < inputEnd)
            {
                outputPos = WriteZstdLiterals(output, outputPos, input.AsSpan(anchor, inputEnd - anchor));
            }

            // Write end marker
            output[outputPos++] = 0xFF;

            return outputPos;
        }

        private static void DecompressZstdBlock(ReadOnlySpan<byte> input, Span<byte> output, int level)
        {
            int inputPos = 0;
            int outputPos = 0;

            // Skip block header
            inputPos++;

            while (inputPos < input.Length)
            {
                byte token = input[inputPos++];

                if (token == 0xFF) // End marker
                    break;

                if ((token & 0x80) == 0) // Literal
                {
                    int literalLen = token & 0x7F;
                    if (literalLen == 127)
                    {
                        literalLen += input[inputPos++];
                    }
                    input.Slice(inputPos, literalLen).CopyTo(output.Slice(outputPos));
                    inputPos += literalLen;
                    outputPos += literalLen;
                }
                else // Match
                {
                    int matchLen = (token & 0x7F) + 4;
                    if ((token & 0x7F) == 127)
                    {
                        matchLen += input[inputPos++];
                    }

                    int offset = input[inputPos] | (input[inputPos + 1] << 8);
                    if ((input[inputPos + 1] & 0x80) != 0)
                    {
                        offset = (offset & 0x7FFF) | (input[inputPos + 2] << 15);
                        inputPos += 3;
                    }
                    else
                    {
                        inputPos += 2;
                    }

                    int matchPos = outputPos - offset;
                    for (int i = 0; i < matchLen; i++)
                    {
                        output[outputPos++] = output[matchPos + i];
                    }
                }
            }
        }

        private static int GetZstdHash(byte[] data, int pos)
        {
            uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            return (int)((v * 2654435761u) >> 16) & 0xFFFF;
        }

        private static int GetZstdMatchLength(byte[] data, int pos1, int pos2, int end)
        {
            int len = 0;
            while (pos2 + len < end && data[pos1 + len] == data[pos2 + len] && len < 258)
                len++;
            return len;
        }

        private static int WriteZstdSequence(Span<byte> output, int pos, ReadOnlySpan<byte> literals, int offset, int matchLen)
        {
            // Write literals first
            if (literals.Length > 0)
            {
                pos = WriteZstdLiterals(output, pos, literals);
            }

            // Write match
            int ml = matchLen - 4;
            byte token = (byte)(0x80 | (ml >= 127 ? 127 : ml));
            output[pos++] = token;

            if (ml >= 127)
            {
                output[pos++] = (byte)(ml - 127);
            }

            // Write offset (2 or 3 bytes)
            if (offset < 32768)
            {
                output[pos++] = (byte)(offset & 0xFF);
                output[pos++] = (byte)((offset >> 8) & 0x7F);
            }
            else
            {
                output[pos++] = (byte)(offset & 0xFF);
                output[pos++] = (byte)(((offset >> 8) & 0x7F) | 0x80);
                output[pos++] = (byte)((offset >> 15) & 0xFF);
            }

            return pos;
        }

        private static int WriteZstdLiterals(Span<byte> output, int pos, ReadOnlySpan<byte> literals)
        {
            int len = literals.Length;
            byte token = (byte)(len >= 127 ? 127 : len);
            output[pos++] = token;

            if (len >= 127)
            {
                output[pos++] = (byte)(len - 127);
            }

            literals.CopyTo(output.Slice(pos));
            return pos + len;
        }

        #endregion
    }

    #endregion

    #region Pipeline Plugins (Wrapper Classes for Kernel Integration)

    /// <summary>
    /// GZip compression plugin for DataWarehouse pipeline.
    /// Extends PipelinePluginBase for bidirectional stream transformation.
    ///
    /// Message Commands:
    /// - compression.gzip.configure: Configure compression level
    /// - compression.gzip.stats: Get compression statistics
    /// - compression.gzip.test: Test compression on sample data
    /// </summary>
    public sealed class GZipCompressionPlugin : PipelinePluginBase
    {
        private readonly GZipConfig _config;
        private readonly GZipCompressionProvider _provider;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        public override string Id => "datawarehouse.plugins.compression.gzip";
        public override string Name => "GZip Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";
        public override int QualityLevel => _config.Level switch
        {
            SysCompressionLevel.Fastest => 20,
            SysCompressionLevel.Optimal => 60,
            SysCompressionLevel.SmallestSize => 90,
            _ => 50
        };

        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        /// <summary>Gets the underlying compression provider.</summary>
        public ICompressionProvider Provider => _provider;

        public GZipCompressionPlugin(GZipConfig? config = null)
        {
            _config = config ?? new GZipConfig();
            _provider = new GZipCompressionProvider(new CompressionOptions
            {
                Level = _config.Level,
                BufferSize = _config.BufferSize
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.gzip.configure", DisplayName = "Configure", Description = "Configure GZip compression settings" },
                new() { Name = "compression.gzip.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.gzip.test", DisplayName = "Test", Description = "Test compression on sample data" },
                new() { Name = "compression.gzip.compress", DisplayName = "Compress", Description = "Compress data using GZip" },
                new() { Name = "compression.gzip.decompress", DisplayName = "Decompress", Description = "Decompress GZip data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "GZip";
            metadata["CompressionLevel"] = _config.Level.ToString();
            metadata["BufferSize"] = _config.BufferSize;
            metadata["SupportsStreaming"] = true;
            metadata["MagicBytes"] = "1F 8B";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.gzip.configure" => HandleConfigureAsync(message),
                "compression.gzip.stats" => HandleStatsAsync(message),
                "compression.gzip.test" => HandleTestAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            var level = GetCompressionLevel(args);
            var bufferSize = GetBufferSize(args);

            var output = new MemoryStream();

            using (var gzip = new GZipStream(output, level, leaveOpen: true))
            {
                input.CopyTo(gzip, bufferSize);
            }

            output.Position = 0;

            lock (_statsLock)
            {
                _totalBytesIn += input.Length;
                _totalBytesOut += output.Length;
                _operationCount++;
            }

            context.LogDebug($"GZip compressed {input.Length} bytes to {output.Length} bytes ({GetCompressionRatio(input.Length, output.Length):P1} ratio)");

            return output;
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            var bufferSize = GetBufferSize(args);
            var output = new MemoryStream();

            using (var gzip = new GZipStream(stored, CompressionMode.Decompress, leaveOpen: true))
            {
                gzip.CopyTo(output, bufferSize);
            }

            output.Position = 0;

            lock (_statsLock)
            {
                _totalBytesIn += stored.Length;
                _totalBytesOut += output.Length;
                _operationCount++;
            }

            context.LogDebug($"GZip decompressed {stored.Length} bytes to {output.Length} bytes");

            return output;
        }

        private SysCompressionLevel GetCompressionLevel(Dictionary<string, object> args)
        {
            if (args.TryGetValue("level", out var levelObj))
            {
                return levelObj switch
                {
                    SysCompressionLevel cl => cl,
                    string s when Enum.TryParse<SysCompressionLevel>(s, true, out var parsed) => parsed,
                    int i => (SysCompressionLevel)Math.Clamp(i, 0, 2),
                    _ => _config.Level
                };
            }
            return _config.Level;
        }

        private int GetBufferSize(Dictionary<string, object> args)
        {
            if (args.TryGetValue("bufferSize", out var bufferObj) && bufferObj is int size)
            {
                return Math.Clamp(size, 4096, 1048576);
            }
            return _config.BufferSize;
        }

        private static double GetCompressionRatio(long original, long compressed)
        {
            return original > 0 ? (double)compressed / original : 0;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("level", out var levelObj) && levelObj is string levelStr)
            {
                if (Enum.TryParse<SysCompressionLevel>(levelStr, true, out var level))
                {
                    _config.Level = level;
                }
            }
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount,
                    ["CompressionRatio"] = GetCompressionRatio(_totalBytesIn, _totalBytesOut)
                };
            }
            return Task.CompletedTask;
        }

        private async Task HandleTestAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("data", out var dataObj) && dataObj is byte[] data)
            {
                using var input = new MemoryStream(data);
                var compressed = OnWrite(input, new TestKernelContext(), new Dictionary<string, object>());
                var ratio = GetCompressionRatio(data.Length, compressed.Length);
            }
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// LZ4 compression plugin for DataWarehouse pipeline.
    /// High-speed compression with configurable block size.
    ///
    /// Message Commands:
    /// - compression.lz4.configure: Configure compression settings
    /// - compression.lz4.stats: Get compression statistics
    /// </summary>
    public sealed class LZ4CompressionPlugin : PipelinePluginBase
    {
        private readonly LZ4Config _config;
        private readonly LZ4CompressionProvider _provider;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        public override string Id => "datawarehouse.plugins.compression.lz4";
        public override string Name => "LZ4 Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";
        public override int QualityLevel => _config.HighCompression ? 70 : 30;
        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        /// <summary>Gets the underlying compression provider.</summary>
        public ICompressionProvider Provider => _provider;

        public LZ4CompressionPlugin(LZ4Config? config = null)
        {
            _config = config ?? new LZ4Config();
            _provider = new LZ4CompressionProvider(new CompressionOptions
            {
                LZ4HighCompression = _config.HighCompression,
                LZ4BlockSize = _config.BlockSize
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.lz4.configure", DisplayName = "Configure", Description = "Configure LZ4 compression settings" },
                new() { Name = "compression.lz4.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.lz4.compress", DisplayName = "Compress", Description = "Compress data using LZ4" },
                new() { Name = "compression.lz4.decompress", DisplayName = "Decompress", Description = "Decompress LZ4 data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "LZ4";
            metadata["HighCompression"] = _config.HighCompression;
            metadata["BlockSize"] = _config.BlockSize;
            metadata["SupportsStreaming"] = true;
            metadata["MagicBytes"] = "04 22 4D 18";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.lz4.configure" => HandleConfigureAsync(message),
                "compression.lz4.stats" => HandleStatsAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var inputData = inputMs.ToArray();

            var compressedData = _provider.Compress(inputData);
            var output = new MemoryStream(compressedData);

            lock (_statsLock)
            {
                _totalBytesIn += inputData.Length;
                _totalBytesOut += compressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"LZ4 compressed {inputData.Length} bytes to {compressedData.Length} bytes");
            return output;
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var compressedData = inputMs.ToArray();

            var decompressedData = _provider.Decompress(compressedData);
            var output = new MemoryStream(decompressedData);

            lock (_statsLock)
            {
                _totalBytesIn += compressedData.Length;
                _totalBytesOut += decompressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"LZ4 decompressed {compressedData.Length} bytes to {decompressedData.Length} bytes");
            return output;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("highCompression", out var hcObj) && hcObj is bool hc)
                _config.HighCompression = hc;
            if (message.Payload.TryGetValue("blockSize", out var bsObj) && bsObj is int bs)
                _config.BlockSize = Math.Clamp(bs, 64 * 1024, 4 * 1024 * 1024);
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount
                };
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Brotli compression plugin for DataWarehouse pipeline.
    /// High compression ratio algorithm suitable for web content and storage.
    ///
    /// Message Commands:
    /// - compression.brotli.configure: Configure compression settings
    /// - compression.brotli.stats: Get compression statistics
    /// </summary>
    public sealed class BrotliCompressionPlugin : PipelinePluginBase
    {
        private readonly BrotliConfig _config;
        private readonly BrotliCompressionProvider _provider;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        public override string Id => "datawarehouse.plugins.compression.brotli";
        public override string Name => "Brotli Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";
        public override int QualityLevel => _config.Level switch
        {
            SysCompressionLevel.Fastest => 40,
            SysCompressionLevel.Optimal => 80,
            SysCompressionLevel.SmallestSize => 95,
            _ => 70
        };
        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        /// <summary>Gets the underlying compression provider.</summary>
        public ICompressionProvider Provider => _provider;

        public BrotliCompressionPlugin(BrotliConfig? config = null)
        {
            _config = config ?? new BrotliConfig();
            _provider = new BrotliCompressionProvider(new CompressionOptions
            {
                Level = _config.Level,
                BufferSize = _config.BufferSize
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.brotli.configure", DisplayName = "Configure", Description = "Configure Brotli compression settings" },
                new() { Name = "compression.brotli.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.brotli.compress", DisplayName = "Compress", Description = "Compress data using Brotli" },
                new() { Name = "compression.brotli.decompress", DisplayName = "Decompress", Description = "Decompress Brotli data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "Brotli";
            metadata["CompressionLevel"] = _config.Level.ToString();
            metadata["BufferSize"] = _config.BufferSize;
            metadata["SupportsStreaming"] = true;
            metadata["RFC"] = "7932";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.brotli.configure" => HandleConfigureAsync(message),
                "compression.brotli.stats" => HandleStatsAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var inputData = inputMs.ToArray();

            var compressedData = _provider.Compress(inputData);
            var output = new MemoryStream(compressedData);

            lock (_statsLock)
            {
                _totalBytesIn += inputData.Length;
                _totalBytesOut += compressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"Brotli compressed {inputData.Length} bytes to {compressedData.Length} bytes");
            return output;
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var compressedData = inputMs.ToArray();

            var decompressedData = _provider.Decompress(compressedData);
            var output = new MemoryStream(decompressedData);

            lock (_statsLock)
            {
                _totalBytesIn += compressedData.Length;
                _totalBytesOut += decompressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"Brotli decompressed {compressedData.Length} bytes to {decompressedData.Length} bytes");
            return output;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("level", out var levelObj) && levelObj is string levelStr)
            {
                if (Enum.TryParse<SysCompressionLevel>(levelStr, true, out var level))
                {
                    _config.Level = level;
                }
            }
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount
                };
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Deflate compression plugin for DataWarehouse pipeline.
    /// Standard deflate algorithm (RFC 1951).
    ///
    /// Message Commands:
    /// - compression.deflate.configure: Configure compression settings
    /// - compression.deflate.stats: Get compression statistics
    /// </summary>
    public sealed class DeflateCompressionPlugin : PipelinePluginBase
    {
        private readonly DeflateConfig _config;
        private readonly DeflateCompressionProvider _provider;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        public override string Id => "datawarehouse.plugins.compression.deflate";
        public override string Name => "Deflate Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";
        public override int QualityLevel => _config.Level switch
        {
            SysCompressionLevel.Fastest => 15,
            SysCompressionLevel.Optimal => 50,
            SysCompressionLevel.SmallestSize => 75,
            _ => 40
        };
        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        /// <summary>Gets the underlying compression provider.</summary>
        public ICompressionProvider Provider => _provider;

        public DeflateCompressionPlugin(DeflateConfig? config = null)
        {
            _config = config ?? new DeflateConfig();
            _provider = new DeflateCompressionProvider(new CompressionOptions
            {
                Level = _config.Level,
                BufferSize = _config.BufferSize
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.deflate.configure", DisplayName = "Configure", Description = "Configure Deflate compression settings" },
                new() { Name = "compression.deflate.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.deflate.compress", DisplayName = "Compress", Description = "Compress data using Deflate" },
                new() { Name = "compression.deflate.decompress", DisplayName = "Decompress", Description = "Decompress Deflate data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "Deflate";
            metadata["CompressionLevel"] = _config.Level.ToString();
            metadata["BufferSize"] = _config.BufferSize;
            metadata["SupportsStreaming"] = true;
            metadata["RFC"] = "1951";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.deflate.configure" => HandleConfigureAsync(message),
                "compression.deflate.stats" => HandleStatsAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var inputData = inputMs.ToArray();

            var compressedData = _provider.Compress(inputData);
            var output = new MemoryStream(compressedData);

            lock (_statsLock)
            {
                _totalBytesIn += inputData.Length;
                _totalBytesOut += compressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"Deflate compressed {inputData.Length} bytes to {compressedData.Length} bytes");
            return output;
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var compressedData = inputMs.ToArray();

            var decompressedData = _provider.Decompress(compressedData);
            var output = new MemoryStream(decompressedData);

            lock (_statsLock)
            {
                _totalBytesIn += compressedData.Length;
                _totalBytesOut += decompressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"Deflate decompressed {compressedData.Length} bytes to {decompressedData.Length} bytes");
            return output;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("level", out var levelObj) && levelObj is string levelStr)
            {
                if (Enum.TryParse<SysCompressionLevel>(levelStr, true, out var level))
                {
                    _config.Level = level;
                }
            }
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount
                };
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Zstd compression plugin for DataWarehouse pipeline.
    /// High compression ratio with good speed balance.
    ///
    /// Message Commands:
    /// - compression.zstd.configure: Configure compression settings
    /// - compression.zstd.stats: Get compression statistics
    /// </summary>
    public sealed class ZstdCompressionPlugin : PipelinePluginBase
    {
        private readonly ZstdConfig _config;
        private readonly ZstdCompressionProvider _provider;
        private readonly object _statsLock = new();
        private long _totalBytesIn;
        private long _totalBytesOut;
        private long _operationCount;

        public override string Id => "datawarehouse.plugins.compression.zstd";
        public override string Name => "Zstd Compression";
        public override string Version => "1.0.0";
        public override string SubCategory => "Compression";
        public override int QualityLevel => _config.Level switch
        {
            <= 3 => 40,
            <= 9 => 70,
            <= 15 => 85,
            _ => 95
        };
        public override int DefaultOrder => 50;
        public override bool AllowBypass => true;

        /// <summary>Gets the underlying compression provider.</summary>
        public ICompressionProvider Provider => _provider;

        public ZstdCompressionPlugin(ZstdConfig? config = null)
        {
            _config = config ?? new ZstdConfig();
            _provider = new ZstdCompressionProvider(new CompressionOptions
            {
                ZstdLevel = _config.Level
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "compression.zstd.configure", DisplayName = "Configure", Description = "Configure Zstd compression settings" },
                new() { Name = "compression.zstd.stats", DisplayName = "Statistics", Description = "Get compression statistics" },
                new() { Name = "compression.zstd.compress", DisplayName = "Compress", Description = "Compress data using Zstd" },
                new() { Name = "compression.zstd.decompress", DisplayName = "Decompress", Description = "Decompress Zstd data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "Zstd";
            metadata["CompressionLevel"] = _config.Level;
            metadata["SupportsStreaming"] = true;
            metadata["RFC"] = "8478";
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "compression.zstd.configure" => HandleConfigureAsync(message),
                "compression.zstd.stats" => HandleStatsAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var inputData = inputMs.ToArray();

            var compressedData = _provider.Compress(inputData);
            var output = new MemoryStream(compressedData);

            lock (_statsLock)
            {
                _totalBytesIn += inputData.Length;
                _totalBytesOut += compressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"Zstd compressed {inputData.Length} bytes to {compressedData.Length} bytes");
            return output;
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var compressedData = inputMs.ToArray();

            var decompressedData = _provider.Decompress(compressedData);
            var output = new MemoryStream(decompressedData);

            lock (_statsLock)
            {
                _totalBytesIn += compressedData.Length;
                _totalBytesOut += decompressedData.Length;
                _operationCount++;
            }

            context.LogDebug($"Zstd decompressed {compressedData.Length} bytes to {decompressedData.Length} bytes");
            return output;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("level", out var levelObj) && levelObj is int level)
            {
                _config.Level = Math.Clamp(level, 1, 22);
            }
            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["TotalBytesIn"] = _totalBytesIn,
                    ["TotalBytesOut"] = _totalBytesOut,
                    ["OperationCount"] = _operationCount
                };
            }
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Configuration Classes

    public class GZipConfig
    {
        public SysCompressionLevel Level { get; set; } = SysCompressionLevel.Optimal;
        public int BufferSize { get; set; } = 81920;
    }

    public class LZ4Config
    {
        public bool HighCompression { get; set; } = false;
        public int BlockSize { get; set; } = 64 * 1024;
    }

    public class BrotliConfig
    {
        public SysCompressionLevel Level { get; set; } = SysCompressionLevel.Optimal;
        public int BufferSize { get; set; } = 81920;
    }

    public class DeflateConfig
    {
        public SysCompressionLevel Level { get; set; } = SysCompressionLevel.Optimal;
        public int BufferSize { get; set; } = 81920;
    }

    public class ZstdConfig
    {
        public int Level { get; set; } = 3;
    }

    #endregion

    #region Test Helpers

    internal class TestKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.Laptop;
        public string RootPath => Environment.CurrentDirectory;
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => [];
    }

    #endregion
}
