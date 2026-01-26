using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DataWarehouse.Plugins.OracleTnsProtocol.Protocol;

/// <summary>
/// Reads Oracle TNS (Transparent Network Substrate) packets from a network stream.
/// Handles the binary protocol format used by Oracle Net Services.
/// </summary>
/// <remarks>
/// TNS packet structure:
/// - Bytes 0-1: Packet length (big-endian)
/// - Bytes 2-3: Packet checksum (usually 0)
/// - Byte 4: Packet type
/// - Byte 5: Reserved flags
/// - Bytes 6-7: Header checksum (usually 0)
/// - Bytes 8+: Packet payload
///
/// Thread-safety: This class is not thread-safe. Each connection should have
/// its own TnsPacketReader instance.
/// </remarks>
public sealed class TnsPacketReader : IDisposable
{
    private readonly NetworkStream _stream;
    private readonly byte[] _headerBuffer = new byte[OracleProtocolConstants.TnsHeaderLength];
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the TnsPacketReader class.
    /// </summary>
    /// <param name="stream">The network stream to read from.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public TnsPacketReader(NetworkStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Reads a complete TNS packet from the network stream.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>The parsed TNS packet.</returns>
    /// <exception cref="EndOfStreamException">Thrown when the connection is closed.</exception>
    /// <exception cref="TnsProtocolException">Thrown when the packet format is invalid.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the reader has been disposed.</exception>
    public async Task<TnsPacket> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read the 8-byte header
        await ReadExactAsync(_headerBuffer, ct).ConfigureAwait(false);

        // Parse header
        var length = BinaryPrimitives.ReadUInt16BigEndian(_headerBuffer.AsSpan(0, 2));
        var packetChecksum = BinaryPrimitives.ReadUInt16BigEndian(_headerBuffer.AsSpan(2, 2));
        var packetType = (TnsPacketType)_headerBuffer[4];
        var flags = _headerBuffer[5];
        var headerChecksum = BinaryPrimitives.ReadUInt16BigEndian(_headerBuffer.AsSpan(6, 2));

        // Validate packet length
        if (length < OracleProtocolConstants.TnsHeaderLength)
        {
            throw new TnsProtocolException($"Invalid TNS packet length: {length}. Minimum is {OracleProtocolConstants.TnsHeaderLength}.");
        }

        if (length > OracleProtocolConstants.MaxSduSize)
        {
            throw new TnsProtocolException($"TNS packet length {length} exceeds maximum SDU size {OracleProtocolConstants.MaxSduSize}.");
        }

        // Validate packet type
        if (packetType > TnsPacketType.MaxPacketType)
        {
            throw new TnsProtocolException($"Unknown TNS packet type: {(int)packetType}.");
        }

        // Read payload
        var payloadLength = length - OracleProtocolConstants.TnsHeaderLength;
        var payload = new byte[payloadLength];

        if (payloadLength > 0)
        {
            await ReadExactAsync(payload, ct).ConfigureAwait(false);
        }

        return new TnsPacket
        {
            PacketType = packetType,
            Flags = flags,
            Payload = payload
        };
    }

    /// <summary>
    /// Reads a TNS CONNECT packet and parses the connection string.
    /// </summary>
    /// <param name="packet">The connect packet to parse.</param>
    /// <returns>The parsed connection request.</returns>
    /// <exception cref="ArgumentNullException">Thrown when packet is null.</exception>
    /// <exception cref="TnsProtocolException">Thrown when the connect packet format is invalid.</exception>
    public TnsConnectRequest ParseConnectPacket(TnsPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.PacketType != TnsPacketType.Connect)
        {
            throw new TnsProtocolException($"Expected CONNECT packet, got {packet.PacketType}.");
        }

        var payload = packet.Payload;
        if (payload.Length < 26)
        {
            throw new TnsProtocolException("CONNECT packet payload too short.");
        }

        var offset = 0;

        // Read connect header fields
        var version = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var compatibleVersion = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var serviceOptions = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var sduSize = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var tduSize = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var protocolCharacteristics = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var lineCharacteristics = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var maxTransmitData = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var ntlTraceUniqueId = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var connectDataLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var connectDataOffset = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;

        var maxReceivedConnectData = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;

        // Read connect data (connection string)
        var connectString = string.Empty;
        if (connectDataLength > 0 && connectDataOffset >= 26 && connectDataOffset + connectDataLength <= payload.Length)
        {
            var connectDataStart = connectDataOffset - OracleProtocolConstants.TnsHeaderLength;
            if (connectDataStart >= 0 && connectDataStart + connectDataLength <= payload.Length)
            {
                connectString = Encoding.ASCII.GetString(payload, connectDataStart, connectDataLength).TrimEnd('\0');
            }
        }

        // Parse the connection string for service name, SID, etc.
        var (serviceName, sid, parameters) = ParseConnectionString(connectString);

        return new TnsConnectRequest
        {
            Version = version,
            CompatibleVersion = compatibleVersion,
            SduSize = sduSize > 0 ? sduSize : OracleProtocolConstants.DefaultSduSize,
            TduSize = tduSize > 0 ? tduSize : OracleProtocolConstants.DefaultTduSize,
            ServiceOptions = serviceOptions,
            ConnectString = connectString,
            ServiceName = serviceName,
            Sid = sid,
            Parameters = parameters
        };
    }

    /// <summary>
    /// Reads a TNS DATA packet containing TTC (Two-Task Common) protocol data.
    /// </summary>
    /// <param name="packet">The data packet to parse.</param>
    /// <returns>The parsed data payload.</returns>
    /// <exception cref="ArgumentNullException">Thrown when packet is null.</exception>
    /// <exception cref="TnsProtocolException">Thrown when the data packet format is invalid.</exception>
    public TnsDataPayload ParseDataPacket(TnsPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.PacketType != TnsPacketType.Data)
        {
            throw new TnsProtocolException($"Expected DATA packet, got {packet.PacketType}.");
        }

        var payload = packet.Payload;
        if (payload.Length < 2)
        {
            throw new TnsProtocolException("DATA packet payload too short.");
        }

        // Data packet header
        var dataFlags = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2));

        // Check for TTC (Two-Task Common) function code
        TtiFunctionCode? functionCode = null;
        var ttiPayload = Array.Empty<byte>();

        if (payload.Length > 2)
        {
            functionCode = (TtiFunctionCode)payload[2];
            if (payload.Length > 3)
            {
                ttiPayload = payload[3..];
            }
        }

        return new TnsDataPayload
        {
            DataFlags = dataFlags,
            FunctionCode = functionCode,
            Payload = ttiPayload
        };
    }

    /// <summary>
    /// Parses an Oracle connection string to extract service name, SID, and parameters.
    /// </summary>
    /// <param name="connectString">The TNS connection string.</param>
    /// <returns>Tuple containing service name, SID, and parameter dictionary.</returns>
    private (string serviceName, string sid, Dictionary<string, string> parameters) ParseConnectionString(string connectString)
    {
        var serviceName = string.Empty;
        var sid = string.Empty;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(connectString))
        {
            return (serviceName, sid, parameters);
        }

        // Parse TNS format: (DESCRIPTION=(CONNECT_DATA=(SERVICE_NAME=xxx)(SID=yyy))...)
        // Also handle simple format: SERVICE_NAME=xxx
        try
        {
            var normalized = connectString.Replace(" ", "").Replace("\n", "").Replace("\r", "");

            // Extract SERVICE_NAME
            var serviceNameMatch = System.Text.RegularExpressions.Regex.Match(
                normalized, @"SERVICE_NAME=([^)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (serviceNameMatch.Success)
            {
                serviceName = serviceNameMatch.Groups[1].Value;
            }

            // Extract SID
            var sidMatch = System.Text.RegularExpressions.Regex.Match(
                normalized, @"(?<!SERVICE_NAME)\bSID=([^)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (sidMatch.Success)
            {
                sid = sidMatch.Groups[1].Value;
            }

            // Extract HOST
            var hostMatch = System.Text.RegularExpressions.Regex.Match(
                normalized, @"HOST=([^)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (hostMatch.Success)
            {
                parameters["HOST"] = hostMatch.Groups[1].Value;
            }

            // Extract PORT
            var portMatch = System.Text.RegularExpressions.Regex.Match(
                normalized, @"PORT=([^)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (portMatch.Success)
            {
                parameters["PORT"] = portMatch.Groups[1].Value;
            }

            // Extract PROGRAM
            var programMatch = System.Text.RegularExpressions.Regex.Match(
                normalized, @"PROGRAM=([^)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (programMatch.Success)
            {
                parameters["PROGRAM"] = programMatch.Groups[1].Value;
            }

            // Extract SERVER
            var serverMatch = System.Text.RegularExpressions.Regex.Match(
                normalized, @"SERVER=([^)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (serverMatch.Success)
            {
                parameters["SERVER"] = serverMatch.Groups[1].Value;
            }
        }
        catch
        {
            // If parsing fails, return empty values - connection will use defaults
        }

        return (serviceName, sid, parameters);
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the stream.
    /// </summary>
    /// <param name="buffer">Buffer to read into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="EndOfStreamException">Thrown when connection closes before all bytes are read.</exception>
    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Connection closed by remote host.");
            }
            offset += read;
        }
    }

    /// <summary>
    /// Releases resources used by the TnsPacketReader.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Note: We don't own the stream, so we don't dispose it
        }
    }
}

/// <summary>
/// Represents a TNS packet read from the network.
/// </summary>
public sealed class TnsPacket
{
    /// <summary>
    /// Gets the packet type.
    /// </summary>
    public TnsPacketType PacketType { get; init; }

    /// <summary>
    /// Gets the packet flags from the header.
    /// </summary>
    public byte Flags { get; init; }

    /// <summary>
    /// Gets the packet payload (excluding header).
    /// </summary>
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Represents a parsed TNS CONNECT request.
/// </summary>
public sealed class TnsConnectRequest
{
    /// <summary>
    /// Gets the TNS protocol version.
    /// </summary>
    public int Version { get; init; }

    /// <summary>
    /// Gets the compatible TNS version.
    /// </summary>
    public int CompatibleVersion { get; init; }

    /// <summary>
    /// Gets the requested SDU (Session Data Unit) size.
    /// </summary>
    public int SduSize { get; init; }

    /// <summary>
    /// Gets the requested TDU (Transport Data Unit) size.
    /// </summary>
    public int TduSize { get; init; }

    /// <summary>
    /// Gets the service options flags.
    /// </summary>
    public int ServiceOptions { get; init; }

    /// <summary>
    /// Gets the raw connection string.
    /// </summary>
    public string ConnectString { get; init; } = string.Empty;

    /// <summary>
    /// Gets the requested service name.
    /// </summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the requested SID.
    /// </summary>
    public string Sid { get; init; } = string.Empty;

    /// <summary>
    /// Gets additional connection parameters.
    /// </summary>
    public Dictionary<string, string> Parameters { get; init; } = new();
}

/// <summary>
/// Represents a parsed TNS DATA packet payload.
/// </summary>
public sealed class TnsDataPayload
{
    /// <summary>
    /// Gets the data flags from the packet.
    /// </summary>
    public int DataFlags { get; init; }

    /// <summary>
    /// Gets the TTC/TTI function code (if present).
    /// </summary>
    public TtiFunctionCode? FunctionCode { get; init; }

    /// <summary>
    /// Gets the payload data after the function code.
    /// </summary>
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Exception thrown when TNS protocol errors are encountered.
/// </summary>
public class TnsProtocolException : Exception
{
    /// <summary>
    /// Initializes a new instance of TnsProtocolException.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TnsProtocolException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of TnsProtocolException.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TnsProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
