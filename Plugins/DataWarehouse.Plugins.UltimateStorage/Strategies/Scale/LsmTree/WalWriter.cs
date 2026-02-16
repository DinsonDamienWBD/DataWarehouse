using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Write-ahead log for crash recovery.
    /// Append-only binary format with length-prefixed entries.
    /// </summary>
    public sealed class WalWriter : IAsyncDisposable
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _writeLock;
        private FileStream? _fileStream;
        private bool _disposed;

        /// <summary>
        /// Initializes a new WalWriter for the given file path.
        /// </summary>
        /// <param name="filePath">Path to the WAL file.</param>
        public WalWriter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _writeLock = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Opens or creates the WAL file for writing.
        /// </summary>
        public async Task OpenAsync(CancellationToken ct = default)
        {
            if (_fileStream != null)
            {
                return;
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                if (_fileStream == null)
                {
                    var directory = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    _fileStream = new FileStream(
                        _filePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        useAsync: true);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Appends a WAL entry to the log.
        /// Entry format: [op:1][key_len:4][key:bytes][value_len:4][value:bytes]
        /// </summary>
        /// <param name="entry">Entry to append.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task AppendAsync(WalEntry entry, CancellationToken ct = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WalWriter));
            }

            if (_fileStream == null)
            {
                await OpenAsync(ct);
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                // Calculate total size
                var keyLen = entry.Key.Length;
                var valueLen = entry.Value?.Length ?? 0;
                var totalSize = 1 + 4 + keyLen + 4 + valueLen;

                var buffer = new byte[totalSize];
                var span = buffer.AsSpan();

                // Write operation type
                span[0] = (byte)entry.Op;
                var offset = 1;

                // Write key length and key
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), keyLen);
                offset += 4;
                entry.Key.CopyTo(span.Slice(offset, keyLen));
                offset += keyLen;

                // Write value length and value
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), valueLen);
                offset += 4;
                if (entry.Value != null && valueLen > 0)
                {
                    entry.Value.CopyTo(span.Slice(offset, valueLen));
                }

                await _fileStream!.WriteAsync(buffer, ct);
                await _fileStream.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Replays all entries from the WAL file.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of WAL entries.</returns>
        public async IAsyncEnumerable<WalEntry> ReplayAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!File.Exists(_filePath))
            {
                yield break;
            }

            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            var buffer = new byte[4096];

            while (stream.Position < stream.Length)
            {
                ct.ThrowIfCancellationRequested();

                // Read operation type
                var opByte = stream.ReadByte();
                if (opByte == -1)
                {
                    break;
                }
                var op = (WalOp)opByte;

                // Read key length
                if (await stream.ReadAsync(buffer.AsMemory(0, 4), ct) != 4)
                {
                    break;
                }
                var keyLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4));

                if (keyLen < 0 || keyLen > 1024 * 1024) // Sanity check
                {
                    break;
                }

                // Read key
                var key = new byte[keyLen];
                if (await stream.ReadAsync(key, ct) != keyLen)
                {
                    break;
                }

                // Read value length
                if (await stream.ReadAsync(buffer.AsMemory(0, 4), ct) != 4)
                {
                    break;
                }
                var valueLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4));

                if (valueLen < 0 || valueLen > 1024 * 1024) // Sanity check
                {
                    break;
                }

                // Read value
                byte[]? value = null;
                if (valueLen > 0)
                {
                    value = new byte[valueLen];
                    if (await stream.ReadAsync(value, ct) != valueLen)
                    {
                        break;
                    }
                }

                yield return new WalEntry(key, value, op);
            }
        }

        /// <summary>
        /// Truncates the WAL file (clears all entries).
        /// </summary>
        public async Task TruncateAsync(CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                if (_fileStream != null)
                {
                    await _fileStream.DisposeAsync();
                    _fileStream = null;
                }

                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Disposes resources used by the WalWriter.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_fileStream != null)
            {
                await _fileStream.DisposeAsync();
                _fileStream = null;
            }

            _writeLock?.Dispose();
        }
    }
}
