using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DataWarehouse.Plugins.PostgresWireProtocol.Protocol;

/// <summary>
/// Reads PostgreSQL wire protocol messages from a network stream.
/// </summary>
public sealed class MessageReader
{
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[8192];

    public MessageReader(NetworkStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Reads the startup message (special format without message type byte).
    /// </summary>
    public async Task<StartupMessage> ReadStartupMessageAsync(CancellationToken ct = default)
    {
        var length = await ReadInt32Async(ct);
        if (length < 4 || length > 10000)
            throw new InvalidDataException($"Invalid startup message length: {length}");

        var body = new byte[length - 4];
        await ReadExactAsync(body, ct);

        var version = BinaryPrimitives.ReadInt32BigEndian(body);

        // Check for special codes
        if (version == PgProtocolConstants.SslRequestCode)
        {
            return new StartupMessage
            {
                ProtocolVersion = version,
                IsSslRequest = true
            };
        }

        if (version == PgProtocolConstants.CancelRequestCode)
        {
            return new StartupMessage
            {
                ProtocolVersion = version,
                IsCancelRequest = true,
                ProcessId = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4, 4)),
                SecretKey = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(8, 4))
            };
        }

        // Parse regular startup parameters
        var parameters = ParseKeyValuePairs(body.AsSpan(4));

        return new StartupMessage
        {
            ProtocolVersion = version,
            Parameters = parameters
        };
    }

    /// <summary>
    /// Reads a frontend message (with message type byte).
    /// </summary>
    public async Task<FrontendMessage> ReadFrontendMessageAsync(CancellationToken ct = default)
    {
        var messageTypeByte = await ReadByteAsync(ct);
        if (messageTypeByte == -1)
            throw new EndOfStreamException("Connection closed");

        var messageType = (PgMessageType)messageTypeByte;
        var length = await ReadInt32Async(ct);

        if (length < 4 || length > 1_000_000)
            throw new InvalidDataException($"Invalid message length: {length}");

        var body = new byte[length - 4];
        if (body.Length > 0)
            await ReadExactAsync(body, ct);

        return new FrontendMessage
        {
            Type = messageType,
            Body = body
        };
    }

    /// <summary>
    /// Reads a single byte from the stream.
    /// </summary>
    public async Task<int> ReadByteAsync(CancellationToken ct = default)
    {
        var buffer = new byte[1];
        var read = await _stream.ReadAsync(buffer, ct);
        return read == 0 ? -1 : buffer[0];
    }

    /// <summary>
    /// Reads a 32-bit big-endian integer.
    /// </summary>
    public async Task<int> ReadInt32Async(CancellationToken ct = default)
    {
        var buffer = new byte[4];
        await ReadExactAsync(buffer, ct);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }

    /// <summary>
    /// Reads a 16-bit big-endian integer.
    /// </summary>
    public async Task<short> ReadInt16Async(CancellationToken ct = default)
    {
        var buffer = new byte[2];
        await ReadExactAsync(buffer, ct);
        return BinaryPrimitives.ReadInt16BigEndian(buffer);
    }

    /// <summary>
    /// Reads a null-terminated string.
    /// </summary>
    public async Task<string> ReadNullTerminatedStringAsync(CancellationToken ct = default)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = await ReadByteAsync(ct);
            if (b == -1)
                throw new EndOfStreamException();
            if (b == 0)
                break;
            bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Reads exactly the specified number of bytes.
    /// </summary>
    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream");
            offset += read;
        }
    }

    /// <summary>
    /// Parses key-value pairs from startup message body.
    /// Format: key\0value\0key\0value\0\0
    /// </summary>
    private Dictionary<string, string> ParseKeyValuePairs(ReadOnlySpan<byte> data)
    {
        var parameters = new Dictionary<string, string>();
        var parts = new List<string>();

        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == 0)
            {
                if (i > start)
                {
                    var str = Encoding.UTF8.GetString(data.Slice(start, i - start));
                    parts.Add(str);
                }
                start = i + 1;
            }
        }

        // Pair up keys and values
        for (var i = 0; i < parts.Count - 1; i += 2)
        {
            parameters[parts[i]] = parts[i + 1];
        }

        return parameters;
    }
}

/// <summary>
/// Startup message structure.
/// </summary>
public sealed class StartupMessage
{
    public int ProtocolVersion { get; init; }
    public bool IsSslRequest { get; init; }
    public bool IsCancelRequest { get; init; }
    public int ProcessId { get; init; }
    public int SecretKey { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new();
}

/// <summary>
/// Frontend message structure.
/// </summary>
public sealed class FrontendMessage
{
    public PgMessageType Type { get; init; }
    public byte[] Body { get; init; } = Array.Empty<byte>();
}
