using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DataWarehouse.Plugins.OracleTnsProtocol.Protocol;

/// <summary>
/// Writes Oracle TNS (Transparent Network Substrate) packets to a network stream.
/// Handles the binary protocol format used by Oracle Net Services.
/// </summary>
/// <remarks>
/// This class constructs properly formatted TNS packets including headers
/// and sends them over the network. All writes are done atomically to prevent
/// partial packet transmission.
///
/// Thread-safety: This class is not thread-safe. Each connection should have
/// its own TnsPacketWriter instance. Use locking if concurrent writes are needed.
/// </remarks>
public sealed class TnsPacketWriter : IDisposable
{
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the TnsPacketWriter class.
    /// </summary>
    /// <param name="stream">The network stream to write to.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public TnsPacketWriter(NetworkStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Writes a TNS packet with the specified type and payload.
    /// </summary>
    /// <param name="packetType">The TNS packet type.</param>
    /// <param name="payload">The packet payload (excluding header).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <exception cref="ArgumentNullException">Thrown when payload is null.</exception>
    /// <exception cref="ArgumentException">Thrown when payload exceeds maximum size.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the writer has been disposed.</exception>
    public async Task WritePacketAsync(TnsPacketType packetType, byte[] payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(payload);

        var totalLength = OracleProtocolConstants.TnsHeaderLength + payload.Length;
        if (totalLength > OracleProtocolConstants.MaxSduSize)
        {
            throw new ArgumentException($"Packet size {totalLength} exceeds maximum SDU size {OracleProtocolConstants.MaxSduSize}.");
        }

        var packet = new byte[totalLength];

        // Write TNS header
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), (ushort)totalLength);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0); // Packet checksum
        packet[4] = (byte)packetType;
        packet[5] = 0; // Reserved flags
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0); // Header checksum

        // Copy payload
        payload.CopyTo(packet, OracleProtocolConstants.TnsHeaderLength);

        // Write atomically
        await _stream.WriteAsync(packet, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a TNS ACCEPT packet in response to a CONNECT request.
    /// </summary>
    /// <param name="tnsVersion">The TNS protocol version to accept.</param>
    /// <param name="sduSize">The negotiated SDU size.</param>
    /// <param name="tduSize">The negotiated TDU size.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteAcceptPacketAsync(int tnsVersion, int sduSize, int tduSize, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Accept packet payload structure
        // Bytes 0-1: Version
        // Bytes 2-3: Service options
        // Bytes 4-5: SDU size
        // Bytes 6-7: TDU size
        // Bytes 8-15: Various flags and options
        // Bytes 16-17: Accept data length
        // Bytes 18-19: Accept data offset
        // Bytes 20+: Accept data

        var acceptData = Encoding.ASCII.GetBytes("(DESCRIPTION=(TMP=)(VSNNUM=318767104)(ERR=0))");
        var payloadLength = 24 + acceptData.Length;
        var payload = new byte[payloadLength];

        var offset = 0;

        // Version
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), (ushort)tnsVersion);
        offset += 2;

        // Service options (same as received or default)
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), 0x0001);
        offset += 2;

        // SDU size
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), (ushort)sduSize);
        offset += 2;

        // TDU size
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), (ushort)tduSize);
        offset += 2;

        // Hardware characteristics (byte order, etc.)
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), 0x0101);
        offset += 2;

        // Line turnaround value
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), 0);
        offset += 2;

        // Value of 1 in hardware byte order
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), 0x0001);
        offset += 2;

        // Accept data length
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), (ushort)acceptData.Length);
        offset += 2;

        // Accept data offset (from start of packet including header)
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), (ushort)(32)); // Header(8) + payload header(24)
        offset += 2;

        // Connection flags (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), 0x0041);
        offset += 2;

        // Reserved
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset, 2), 0);
        offset += 2;

        // Copy accept data
        acceptData.CopyTo(payload, offset);

        await WritePacketAsync(TnsPacketType.Accept, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a TNS REFUSE packet to reject a connection.
    /// </summary>
    /// <param name="reason">The refusal reason code.</param>
    /// <param name="message">The error message to send.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteRefusePacketAsync(int reason, string message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var messageBytes = Encoding.ASCII.GetBytes(message);
        var payload = new byte[8 + messageBytes.Length];

        // User reason
        payload[0] = (byte)reason;

        // System reason
        payload[1] = 0;

        // Data length
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), (ushort)messageBytes.Length);

        // Reserved
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 0);

        // Error message
        messageBytes.CopyTo(payload, 8);

        await WritePacketAsync(TnsPacketType.Refuse, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a TNS REDIRECT packet to direct the client to another address.
    /// </summary>
    /// <param name="redirectAddress">The address to redirect to (TNS format).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteRedirectPacketAsync(string redirectAddress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var addressBytes = Encoding.ASCII.GetBytes(redirectAddress);
        var payload = new byte[2 + addressBytes.Length];

        // Data length
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)addressBytes.Length);

        // Redirect address
        addressBytes.CopyTo(payload, 2);

        await WritePacketAsync(TnsPacketType.Redirect, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a TNS DATA packet with TTC/TTI function code and payload.
    /// </summary>
    /// <param name="functionCode">The TTC function code.</param>
    /// <param name="data">The data payload.</param>
    /// <param name="dataFlags">Optional data flags (default: 0).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteDataPacketAsync(TtiFunctionCode functionCode, byte[] data, int dataFlags = 0, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);

        var payload = new byte[3 + data.Length];

        // Data flags (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)dataFlags);

        // Function code
        payload[2] = (byte)functionCode;

        // Data
        data.CopyTo(payload, 3);

        await WritePacketAsync(TnsPacketType.Data, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a raw TNS DATA packet without TTC function code.
    /// </summary>
    /// <param name="data">The raw data to send.</param>
    /// <param name="dataFlags">Data flags (default: 0).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteRawDataPacketAsync(byte[] data, int dataFlags = 0, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);

        var payload = new byte[2 + data.Length];

        // Data flags
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)dataFlags);

        // Data
        data.CopyTo(payload, 2);

        await WritePacketAsync(TnsPacketType.Data, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an authentication challenge for O7LOGON/O8LOGON.
    /// </summary>
    /// <param name="challenge">The authentication challenge data.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteAuthChallengeAsync(OracleAuthChallenge challenge, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(challenge);

        // Build the auth challenge response according to Oracle protocol
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Authentication type (O8LOGON = 0x78)
        bw.Write((byte)(challenge.AuthVersion == 8 ? TtiFunctionCode.O8Logon : TtiFunctionCode.O7Logon));

        // Session key length
        bw.Write((byte)challenge.SessionKey.Length);
        bw.Write(challenge.SessionKey);

        // Salt length
        bw.Write((byte)challenge.Salt.Length);
        bw.Write(challenge.Salt);

        // Iteration count (for PBKDF2)
        if (challenge.AuthVersion >= 8)
        {
            bw.Write(BitConverter.IsLittleEndian
                ? BinaryPrimitives.ReverseEndianness(challenge.Iterations)
                : challenge.Iterations);
        }

        // Server nonce
        bw.Write((byte)challenge.Nonce.Length);
        bw.Write(challenge.Nonce);

        // Flag indicating more auth data follows
        bw.Write((byte)0x01);

        var authData = ms.ToArray();
        await WriteDataPacketAsync(TtiFunctionCode.Authentication, authData, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an authentication success response.
    /// </summary>
    /// <param name="sessionInfo">Optional session information to include.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteAuthSuccessAsync(Dictionary<string, string>? sessionInfo = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Return code: 0 = success
        bw.Write((byte)OracleProtocolConstants.ReturnCodeSuccess);

        // Success flag
        bw.Write((byte)0x01);

        // Session ID info (simplified)
        bw.Write((byte)0x00); // No additional data

        var data = ms.ToArray();
        await WriteDataPacketAsync(TtiFunctionCode.Authentication, data, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an error response packet.
    /// </summary>
    /// <param name="errorCode">The Oracle error code (e.g., 1017 for invalid credentials).</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteErrorResponseAsync(int errorCode, string errorMessage, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Error marker
        bw.Write((byte)OracleProtocolConstants.ErrorMarker);

        // Error code (big-endian)
        var errorCodeBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(errorCodeBytes, errorCode);
        bw.Write(errorCodeBytes);

        // Error message length
        var msgBytes = Encoding.UTF8.GetBytes(errorMessage);
        bw.Write((byte)msgBytes.Length);
        bw.Write(msgBytes);

        // End marker
        bw.Write((byte)OracleProtocolConstants.EndOfData);

        var errorData = ms.ToArray();
        await WriteRawDataPacketAsync(errorData, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a column description (describe response) for a cursor.
    /// </summary>
    /// <param name="columns">The column descriptions.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteDescribeResponseAsync(List<OracleColumnDescription> columns, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(columns);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Describe response header
        bw.Write((byte)0x08); // Describe complete marker

        // Number of columns
        bw.Write((byte)columns.Count);

        foreach (var col in columns)
        {
            // Column type OID
            bw.Write((byte)col.TypeOid);

            // Column max size
            BinaryPrimitives.WriteUInt16BigEndian(ms.GetBuffer().AsSpan((int)ms.Position, 2), (ushort)col.DisplaySize);
            ms.Position += 2;

            // Precision and scale for NUMBER types
            bw.Write((byte)col.Precision);
            bw.Write((byte)col.Scale);

            // Nullable flag
            bw.Write((byte)(col.IsNullable ? 1 : 0));

            // Column name length and name
            var nameBytes = Encoding.UTF8.GetBytes(col.Name);
            bw.Write((byte)nameBytes.Length);
            bw.Write(nameBytes);
        }

        // End marker
        bw.Write((byte)OracleProtocolConstants.EndOfData);

        var describeData = ms.ToArray();
        await WriteDataPacketAsync(TtiFunctionCode.Describe, describeData, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes row data for a fetch response.
    /// </summary>
    /// <param name="rows">The rows to send.</param>
    /// <param name="columns">The column metadata for type information.</param>
    /// <param name="hasMoreRows">Whether more rows are available.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteFetchResponseAsync(
        List<List<byte[]?>> rows,
        List<OracleColumnDescription> columns,
        bool hasMoreRows,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Fetch response header
        bw.Write((byte)(hasMoreRows ? OracleProtocolConstants.MoreData : OracleProtocolConstants.EndOfData));

        // Row count
        BinaryPrimitives.WriteUInt16BigEndian(ms.GetBuffer().AsSpan((int)ms.Position, 2), (ushort)rows.Count);
        ms.Position += 2;

        foreach (var row in rows)
        {
            // Row indicator (0 = data row)
            bw.Write((byte)0x07);

            for (var i = 0; i < row.Count && i < columns.Count; i++)
            {
                var value = row[i];

                if (value == null)
                {
                    // NULL indicator
                    bw.Write((byte)0xFF);
                }
                else
                {
                    // Value length (1 byte for small values, 2 bytes for larger)
                    if (value.Length < 254)
                    {
                        bw.Write((byte)value.Length);
                    }
                    else
                    {
                        bw.Write((byte)0xFE);
                        BinaryPrimitives.WriteUInt16BigEndian(ms.GetBuffer().AsSpan((int)ms.Position, 2), (ushort)value.Length);
                        ms.Position += 2;
                    }

                    // Value data
                    bw.Write(value);
                }
            }
        }

        // Status flags
        if (!hasMoreRows)
        {
            // Fetch completed marker
            var completedMarker = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(completedMarker, (ushort)OracleProtocolConstants.FetchCompleted);
            bw.Write(completedMarker);
        }

        var fetchData = ms.ToArray();
        await WriteDataPacketAsync(TtiFunctionCode.Fetch, fetchData, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an execute response for DML operations.
    /// </summary>
    /// <param name="rowsAffected">Number of rows affected.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteExecuteResponseAsync(long rowsAffected, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Execute complete marker
        bw.Write((byte)0x02);

        // Rows affected (4 bytes, big-endian)
        var rowsBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rowsBytes, (int)Math.Min(rowsAffected, int.MaxValue));
        bw.Write(rowsBytes);

        // Success indicator
        bw.Write((byte)OracleProtocolConstants.ReturnCodeSuccess);

        // End of data
        bw.Write((byte)OracleProtocolConstants.EndOfData);

        var executeData = ms.ToArray();
        await WriteDataPacketAsync(TtiFunctionCode.Execute, executeData, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a commit/rollback response.
    /// </summary>
    /// <param name="isCommit">True for commit, false for rollback.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteTransactionResponseAsync(bool isCommit, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Transaction complete marker
        bw.Write((byte)0x01);

        // Success code
        bw.Write((byte)OracleProtocolConstants.ReturnCodeSuccess);

        var txData = ms.ToArray();
        await WriteDataPacketAsync(isCommit ? TtiFunctionCode.Commit : TtiFunctionCode.Rollback, txData, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the version/protocol negotiation response.
    /// </summary>
    /// <param name="version">Server version string.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    public async Task WriteVersionResponseAsync(string version, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // TTC version
        bw.Write((byte)6); // Oracle 10g+ TTC version

        // Character set (AL32UTF8 = 873)
        BinaryPrimitives.WriteUInt16BigEndian(ms.GetBuffer().AsSpan((int)ms.Position, 2), 873);
        ms.Position += 2;

        // Flags
        bw.Write((byte)0x01);

        // Version string
        var versionBytes = Encoding.ASCII.GetBytes(version);
        bw.Write((byte)versionBytes.Length);
        bw.Write(versionBytes);

        var versionData = ms.ToArray();
        await WriteDataPacketAsync(TtiFunctionCode.VersionExchange, versionData, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases resources used by the TnsPacketWriter.
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
