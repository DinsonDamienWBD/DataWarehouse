using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DataWarehouse.Plugins.JdbcBridge.Protocol;

/// <summary>
/// Reads JDBC wire protocol messages from a network stream.
/// Uses a length-prefixed binary protocol for efficient network transport.
/// </summary>
public sealed class MessageReader : IDisposable
{
    private readonly NetworkStream _stream;
    private readonly byte[] _headerBuffer = new byte[5]; // 1 byte type + 4 bytes length
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the MessageReader class.
    /// </summary>
    /// <param name="stream">The network stream to read from.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public MessageReader(NetworkStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Reads a complete message from the stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message type and body.</returns>
    /// <exception cref="EndOfStreamException">Thrown when the stream ends unexpectedly.</exception>
    /// <exception cref="InvalidDataException">Thrown when message format is invalid.</exception>
    public async Task<(JdbcMessageType Type, byte[] Body)> ReadMessageAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _readLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read header: 1 byte type + 4 bytes length (big-endian)
            await ReadExactAsync(_headerBuffer, ct).ConfigureAwait(false);

            var messageType = (JdbcMessageType)_headerBuffer[0];
            var length = BinaryPrimitives.ReadInt32BigEndian(_headerBuffer.AsSpan(1, 4));

            if (length < 0 || length > 16 * 1024 * 1024) // Max 16MB message
            {
                throw new InvalidDataException($"Invalid message length: {length}");
            }

            // Read body
            var body = new byte[length];
            if (length > 0)
            {
                await ReadExactAsync(body, ct).ConfigureAwait(false);
            }

            return (messageType, body);
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// Reads a string from a byte array at the specified offset.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The string value.</returns>
    public static string ReadString(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
        {
            return string.Empty;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;

        if (length < 0)
        {
            return string.Empty;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        if (offset + length > data.Length)
        {
            throw new InvalidDataException("String length exceeds available data");
        }

        var str = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return str;
    }

    /// <summary>
    /// Reads a nullable string from a byte array at the specified offset.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The string value or null.</returns>
    public static string? ReadNullableString(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;

        if (length < 0)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        if (offset + length > data.Length)
        {
            throw new InvalidDataException("String length exceeds available data");
        }

        var str = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return str;
    }

    /// <summary>
    /// Reads a 32-bit integer from a byte array.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The integer value.</returns>
    public static int ReadInt32(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            throw new InvalidDataException("Not enough data for int32");
        }

        var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    /// <summary>
    /// Reads a 64-bit integer from a byte array.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The long value.</returns>
    public static long ReadInt64(byte[] data, ref int offset)
    {
        if (offset + 8 > data.Length)
        {
            throw new InvalidDataException("Not enough data for int64");
        }

        var value = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset, 8));
        offset += 8;
        return value;
    }

    /// <summary>
    /// Reads a 16-bit integer from a byte array.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The short value.</returns>
    public static short ReadInt16(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length)
        {
            throw new InvalidDataException("Not enough data for int16");
        }

        var value = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    /// <summary>
    /// Reads a single byte from a byte array.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The byte value.</returns>
    public static byte ReadByte(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
        {
            throw new InvalidDataException("Not enough data for byte");
        }

        return data[offset++];
    }

    /// <summary>
    /// Reads a boolean from a byte array.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The boolean value.</returns>
    public static bool ReadBoolean(byte[] data, ref int offset)
    {
        return ReadByte(data, ref offset) != 0;
    }

    /// <summary>
    /// Reads a double from a byte array.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The double value.</returns>
    public static double ReadDouble(byte[] data, ref int offset)
    {
        if (offset + 8 > data.Length)
        {
            throw new InvalidDataException("Not enough data for double");
        }

        var value = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(offset, 8));
        offset += 8;
        return value;
    }

    /// <summary>
    /// Reads a float from a byte array.
    /// </summary>
    /// <param name="data">The byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The float value.</returns>
    public static float ReadFloat(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            throw new InvalidDataException("Not enough data for float");
        }

        var value = BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    /// <summary>
    /// Reads a byte array from a byte array.
    /// </summary>
    /// <param name="data">The source byte array.</param>
    /// <param name="offset">The starting offset (updated after reading).</param>
    /// <returns>The byte array value or null.</returns>
    public static byte[]? ReadBytes(byte[] data, ref int offset)
    {
        var length = ReadInt32(data, ref offset);

        if (length < 0)
        {
            return null;
        }

        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        if (offset + length > data.Length)
        {
            throw new InvalidDataException("Byte array length exceeds available data");
        }

        var result = new byte[length];
        Array.Copy(data, offset, result, 0, length);
        offset += length;
        return result;
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the stream.
    /// </summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="EndOfStreamException">Thrown when the stream ends before all bytes are read.</exception>
    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Connection closed unexpectedly");
            }
            offset += read;
        }
    }

    /// <summary>
    /// Disposes the message reader.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readLock.Dispose();
    }
}
