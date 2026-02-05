using System;
using System.IO;
using System.IO.Compression;
using DataWarehouse.SDK.Contracts.Compression;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;

namespace DataWarehouse.Plugins.UltimateCompression.Strategies.Archive
{
    /// <summary>
    /// ZIP archive compression strategy using the standard System.IO.Compression.ZipArchive.
    /// Creates a single-entry ZIP archive with Deflate compression.
    /// Provides broad compatibility with all ZIP-aware tools and libraries.
    /// </summary>
    /// <remarks>
    /// ZIP format advantages:
    /// 1. Universal support across platforms and tools
    /// 2. Built-in CRC32 checksums for data integrity
    /// 3. Optional compression per entry (supports stored/deflated modes)
    /// 4. Standardized file metadata storage
    /// This implementation creates minimal ZIP archives with a single compressed entry.
    /// </remarks>
    public sealed class ZipStrategy : CompressionStrategyBase
    {
        private const string DefaultEntryName = "data.bin";

        /// <summary>
        /// Initializes a new instance of the <see cref="ZipStrategy"/> class
        /// with the default compression level.
        /// </summary>
        public ZipStrategy() : base(SDK.Contracts.Compression.CompressionLevel.Default)
        {
        }

        /// <inheritdoc/>
        public override CompressionCharacteristics Characteristics { get; } = new()
        {
            AlgorithmName = "ZIP",
            TypicalCompressionRatio = 0.40,
            CompressionSpeed = 5,
            DecompressionSpeed = 6,
            CompressionMemoryUsage = 128 * 1024,
            DecompressionMemoryUsage = 128 * 1024,
            SupportsStreaming = true,
            SupportsParallelCompression = false,
            SupportsParallelDecompression = false,
            SupportsRandomAccess = true,
            MinimumRecommendedSize = 64,
            OptimalBlockSize = 4096
        };

        /// <inheritdoc/>
        protected override byte[] CompressCore(byte[] input)
        {
            using var output = new MemoryStream();

            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry(DefaultEntryName, ConvertCompressionLevel(Level));

                using (var entryStream = entry.Open())
                {
                    entryStream.Write(input, 0, input.Length);
                }
            }

            return output.ToArray();
        }

        /// <inheritdoc/>
        protected override byte[] DecompressCore(byte[] input)
        {
            using var inputStream = new MemoryStream(input);
            using var archive = new ZipArchive(inputStream, ZipArchiveMode.Read);

            if (archive.Entries.Count == 0)
                throw new InvalidDataException("ZIP archive contains no entries.");

            // Read first entry
            var entry = archive.Entries[0];

            using var entryStream = entry.Open();
            using var outputStream = new MemoryStream();

            entryStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        /// <inheritdoc/>
        protected override Stream CreateCompressionStreamCore(Stream output, bool leaveOpen)
        {
            return new ZipCompressionStream(output, leaveOpen, Level);
        }

        /// <inheritdoc/>
        protected override Stream CreateDecompressionStreamCore(Stream input, bool leaveOpen)
        {
            return new ZipDecompressionStream(input, leaveOpen);
        }

        /// <inheritdoc/>
        public override long EstimateCompressedSize(long inputSize)
        {
            // ZIP overhead: local header (~30 bytes) + central directory (~46 bytes) + EOCD (~22 bytes)
            return (long)(inputSize * 0.45) + 128;
        }

        /// <summary>
        /// Converts SDK CompressionLevel to System.IO.Compression.CompressionLevel.
        /// </summary>
        private static SysCompressionLevel ConvertCompressionLevel(SDK.Contracts.Compression.CompressionLevel level)
        {
            return level switch
            {
                SDK.Contracts.Compression.CompressionLevel.Fastest => SysCompressionLevel.Fastest,
                SDK.Contracts.Compression.CompressionLevel.Fast => SysCompressionLevel.Fastest,
                SDK.Contracts.Compression.CompressionLevel.Default => SysCompressionLevel.Optimal,
                SDK.Contracts.Compression.CompressionLevel.Better => SysCompressionLevel.SmallestSize,
                SDK.Contracts.Compression.CompressionLevel.Best => SysCompressionLevel.SmallestSize,
                _ => SysCompressionLevel.Optimal
            };
        }

        #region Stream Wrappers

        private sealed class ZipCompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private readonly ZipArchive _archive;
            private readonly Stream _entryStream;
            private bool _disposed;

            public ZipCompressionStream(Stream output, bool leaveOpen, SDK.Contracts.Compression.CompressionLevel level)
            {
                _baseStream = output;
                _leaveOpen = leaveOpen;

                _archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var sysLevel = ConvertCompressionLevel(level);
                var entry = _archive.CreateEntry(DefaultEntryName, sysLevel);
                _entryStream = entry.Open();
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _entryStream.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                _entryStream.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _entryStream?.Dispose();
                    _archive?.Dispose();

                    if (!_leaveOpen)
                        _baseStream?.Dispose();

                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class ZipDecompressionStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly bool _leaveOpen;
            private readonly ZipArchive _archive;
            private readonly Stream _entryStream;
            private bool _disposed;

            public ZipDecompressionStream(Stream input, bool leaveOpen)
            {
                _baseStream = input;
                _leaveOpen = leaveOpen;

                _archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);

                if (_archive.Entries.Count == 0)
                    throw new InvalidDataException("ZIP archive contains no entries.");

                _entryStream = _archive.Entries[0].Open();
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _entryStream.Read(buffer, offset, count);
            }

            public override void Flush()
            {
                _entryStream.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _entryStream?.Dispose();
                    _archive?.Dispose();

                    if (!_leaveOpen)
                        _baseStream?.Dispose();

                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        #endregion
    }
}
