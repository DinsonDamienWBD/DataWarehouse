using System.Buffers.Binary;
using System.Text;

namespace DataWarehouse.Plugins.TdsProtocol.Protocol;

/// <summary>
/// Writes TDS protocol packets and messages to a network stream.
/// Handles packet fragmentation, token serialization, and encryption.
/// Thread-safe for single-writer scenarios.
/// </summary>
public sealed class TdsPacketWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly int _packetSize;
    private readonly object _writeLock = new();
    private byte _packetId;
    private bool _disposed;

    /// <summary>
    /// TDS packet header size in bytes.
    /// </summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// Initializes a new instance of the <see cref="TdsPacketWriter"/> class.
    /// </summary>
    /// <param name="stream">The network stream to write to.</param>
    /// <param name="packetSize">The maximum packet size (default: 4096).</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public TdsPacketWriter(Stream stream, int packetSize = 4096)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _packetSize = Math.Max(512, Math.Min(packetSize, 32768));
    }

    /// <summary>
    /// Writes a PRELOGIN response packet.
    /// </summary>
    /// <param name="response">The PRELOGIN response data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WritePreLoginResponseAsync(PreLoginResponse response, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var ms = new MemoryStream();

        // Build option headers
        var options = new List<(PreLoginOption Token, byte[] Data)>();

        // Version (major.minor.build.subbuild)
        var versionData = new byte[6];
        versionData[0] = (byte)response.ServerVersion.Major;
        versionData[1] = (byte)response.ServerVersion.Minor;
        BinaryPrimitives.WriteUInt16BigEndian(versionData.AsSpan(2, 2), (ushort)response.ServerVersion.Build);
        BinaryPrimitives.WriteUInt16BigEndian(versionData.AsSpan(4, 2), (ushort)response.ServerVersion.Revision);
        options.Add((PreLoginOption.Version, versionData));

        // Encryption
        options.Add((PreLoginOption.Encryption, new[] { (byte)response.Encryption }));

        // Instance name (empty string terminated with null)
        options.Add((PreLoginOption.Instance, new byte[] { 0 }));

        // Thread ID
        var threadIdData = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(threadIdData, response.ThreadId);
        options.Add((PreLoginOption.ThreadId, threadIdData));

        // MARS
        options.Add((PreLoginOption.Mars, new[] { (byte)(response.MarsEnabled ? 1 : 0) }));

        // Calculate offsets
        var headerSize = options.Count * 5 + 1; // 5 bytes per option + terminator
        var dataOffset = (ushort)headerSize;

        // Write option headers
        foreach (var (token, data) in options)
        {
            ms.WriteByte((byte)token);
            var offsetBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(offsetBytes, dataOffset);
            ms.Write(offsetBytes, 0, 2);
            var lengthBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, (ushort)data.Length);
            ms.Write(lengthBytes, 0, 2);
            dataOffset += (ushort)data.Length;
        }

        // Write terminator
        ms.WriteByte((byte)PreLoginOption.Terminator);

        // Write option data
        foreach (var (_, data) in options)
        {
            ms.Write(data, 0, data.Length);
        }

        await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
    }

    /// <summary>
    /// Writes a LOGIN7 acknowledgment response.
    /// </summary>
    /// <param name="state">The connection state.</param>
    /// <param name="serverName">The server name to report.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteLoginAckAsync(TdsConnectionState state, string serverName, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var ms = new MemoryStream();

        // LOGINACK token
        ms.WriteByte((byte)TdsTokenType.LoginAck);

        // Length placeholder - we'll fill this in later
        var lengthPos = ms.Position;
        ms.WriteByte(0);
        ms.WriteByte(0);

        // Interface (SQL Server)
        ms.WriteByte(1);

        // TDS version
        var versionBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(versionBytes, state.TdsVersion);
        ms.Write(versionBytes, 0, 4);

        // Program name
        var programName = serverName;
        var programNameBytes = Encoding.Unicode.GetBytes(programName);
        ms.WriteByte((byte)programName.Length);
        ms.Write(programNameBytes, 0, programNameBytes.Length);

        // Server version (major.minor.build.subbuild)
        ms.WriteByte(16); // Major
        ms.WriteByte(0);  // Minor
        BinaryPrimitives.WriteUInt16BigEndian(ms.GetBuffer().AsSpan((int)ms.Position, 2), 1000);
        ms.Position += 2;

        // Calculate and write length
        var tokenLength = (ushort)(ms.Position - lengthPos - 2);
        var buffer = ms.GetBuffer();
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan((int)lengthPos, 2), tokenLength);

        // Write ENVCHANGE for database
        WriteEnvChangeToken(ms, EnvChangeType.Database, state.Database, "");

        // Write ENVCHANGE for language
        WriteEnvChangeToken(ms, EnvChangeType.Language, state.Language, "");

        // Write ENVCHANGE for packet size
        WriteEnvChangeToken(ms, EnvChangeType.PacketSize, state.PacketSize.ToString(), "4096");

        // Write INFO message
        WriteInfoToken(ms, 5701, 10, 0, $"Changed database context to '{state.Database}'.", serverName, "", 1);
        WriteInfoToken(ms, 5703, 10, 0, $"Changed language setting to {state.Language}.", serverName, "", 1);

        // Write DONE token
        WriteDoneToken(ms, DoneStatus.Final, 0, 0);

        await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
    }

    /// <summary>
    /// Writes query results with column metadata, rows, and DONE token.
    /// </summary>
    /// <param name="result">The query result.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteQueryResultAsync(TdsQueryResult result, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var ms = new MemoryStream();

        // Write any info messages first
        foreach (var msg in result.Messages)
        {
            WriteInfoToken(ms, msg.Number, msg.Severity, msg.State, msg.Text, msg.ServerName, msg.ProcedureName, msg.LineNumber);
        }

        // Check for error
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            WriteErrorToken(ms, result.ErrorNumber, result.ErrorSeverity, result.ErrorState,
                result.ErrorMessage, "", "", 1);
            WriteDoneToken(ms, DoneStatus.Error | DoneStatus.Final, 0, 0);
            await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
            return;
        }

        // Write column metadata if there are columns
        if (result.Columns.Count > 0)
        {
            WriteColMetadataToken(ms, result.Columns);

            // Write rows
            foreach (var row in result.Rows)
            {
                WriteRowToken(ms, result.Columns, row);
            }
        }

        // Write DONE token
        var status = DoneStatus.Final;
        if (result.Columns.Count > 0)
        {
            status |= DoneStatus.Count;
        }

        WriteDoneToken(ms, status, 0, (ulong)result.Rows.Count);

        await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
    }

    /// <summary>
    /// Writes a DONE token for commands that don't return rows.
    /// </summary>
    /// <param name="rowsAffected">Number of rows affected.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteDoneAsync(long rowsAffected, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var ms = new MemoryStream();
        var status = DoneStatus.Final | DoneStatus.Count;
        WriteDoneToken(ms, status, 0, (ulong)rowsAffected);

        await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
    }

    /// <summary>
    /// Writes an error response.
    /// </summary>
    /// <param name="errorNumber">SQL Server error number.</param>
    /// <param name="severity">Error severity (11-25).</param>
    /// <param name="state">Error state.</param>
    /// <param name="message">Error message text.</param>
    /// <param name="serverName">Server name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteErrorAsync(int errorNumber, byte severity, byte state, string message, string serverName, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var ms = new MemoryStream();

        WriteErrorToken(ms, errorNumber, severity, state, message, serverName, "", 1);
        WriteDoneToken(ms, DoneStatus.Error | DoneStatus.Final, 0, 0);

        await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
    }

    /// <summary>
    /// Writes an info message.
    /// </summary>
    /// <param name="number">Message number.</param>
    /// <param name="message">Message text.</param>
    /// <param name="serverName">Server name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteInfoAsync(int number, string message, string serverName, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var ms = new MemoryStream();

        WriteInfoToken(ms, number, TdsSeverity.Informational, 1, message, serverName, "", 1);
        WriteDoneToken(ms, DoneStatus.Final, 0, 0);

        await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
    }

    /// <summary>
    /// Writes prepared statement response (for sp_prepare).
    /// </summary>
    /// <param name="handle">The prepared statement handle.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WritePrepareResponseAsync(int handle, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var ms = new MemoryStream();

        // Write RETURNSTATUS token with the handle
        ms.WriteByte((byte)TdsTokenType.ReturnStatus);
        var handleBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(handleBytes, handle);
        ms.Write(handleBytes, 0, 4);

        // Write DONEPROC token
        WriteDoneProcToken(ms, DoneStatus.Final | DoneStatus.Count, 0, 0);

        await WritePacketAsync(TdsPacketType.TabularResult, ms.ToArray(), true, ct);
    }

    /// <summary>
    /// Writes an ENVCHANGE token.
    /// </summary>
    private void WriteEnvChangeToken(Stream ms, EnvChangeType type, string newValue, string oldValue)
    {
        ms.WriteByte((byte)TdsTokenType.EnvChange);

        using var tokenData = new MemoryStream();

        tokenData.WriteByte((byte)type);

        // New value
        var newValueBytes = Encoding.Unicode.GetBytes(newValue);
        tokenData.WriteByte((byte)newValue.Length);
        tokenData.Write(newValueBytes, 0, newValueBytes.Length);

        // Old value
        var oldValueBytes = Encoding.Unicode.GetBytes(oldValue);
        tokenData.WriteByte((byte)oldValue.Length);
        if (oldValueBytes.Length > 0)
        {
            tokenData.Write(oldValueBytes, 0, oldValueBytes.Length);
        }

        // Write length
        var lengthBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)tokenData.Length);
        ms.Write(lengthBytes, 0, 2);

        tokenData.Position = 0;
        tokenData.CopyTo(ms);
    }

    /// <summary>
    /// Writes an ERROR token.
    /// </summary>
    private void WriteErrorToken(Stream ms, int number, byte severity, byte state, string message, string serverName, string procName, int lineNumber)
    {
        ms.WriteByte((byte)TdsTokenType.Error);

        using var tokenData = new MemoryStream();

        // Error number
        var numberBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(numberBytes, number);
        tokenData.Write(numberBytes, 0, 4);

        // State
        tokenData.WriteByte(state);

        // Severity
        tokenData.WriteByte(severity);

        // Message
        var messageBytes = Encoding.Unicode.GetBytes(message);
        var msgLenBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(msgLenBytes, (ushort)message.Length);
        tokenData.Write(msgLenBytes, 0, 2);
        tokenData.Write(messageBytes, 0, messageBytes.Length);

        // Server name
        var serverBytes = Encoding.Unicode.GetBytes(serverName);
        tokenData.WriteByte((byte)serverName.Length);
        if (serverBytes.Length > 0)
        {
            tokenData.Write(serverBytes, 0, serverBytes.Length);
        }

        // Procedure name
        var procBytes = Encoding.Unicode.GetBytes(procName);
        tokenData.WriteByte((byte)procName.Length);
        if (procBytes.Length > 0)
        {
            tokenData.Write(procBytes, 0, procBytes.Length);
        }

        // Line number
        var lineBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lineBytes, lineNumber);
        tokenData.Write(lineBytes, 0, 4);

        // Write token length
        var lengthBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)tokenData.Length);
        ms.Write(lengthBytes, 0, 2);

        tokenData.Position = 0;
        tokenData.CopyTo(ms);
    }

    /// <summary>
    /// Writes an INFO token.
    /// </summary>
    private void WriteInfoToken(Stream ms, int number, byte severity, byte state, string message, string serverName, string procName, int lineNumber)
    {
        ms.WriteByte((byte)TdsTokenType.Info);

        using var tokenData = new MemoryStream();

        // Info number
        var numberBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(numberBytes, number);
        tokenData.Write(numberBytes, 0, 4);

        // State
        tokenData.WriteByte(state);

        // Severity
        tokenData.WriteByte(severity);

        // Message
        var messageBytes = Encoding.Unicode.GetBytes(message);
        var msgLenBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(msgLenBytes, (ushort)message.Length);
        tokenData.Write(msgLenBytes, 0, 2);
        tokenData.Write(messageBytes, 0, messageBytes.Length);

        // Server name
        var serverBytes = Encoding.Unicode.GetBytes(serverName);
        tokenData.WriteByte((byte)serverName.Length);
        if (serverBytes.Length > 0)
        {
            tokenData.Write(serverBytes, 0, serverBytes.Length);
        }

        // Procedure name
        var procBytes = Encoding.Unicode.GetBytes(procName);
        tokenData.WriteByte((byte)procName.Length);
        if (procBytes.Length > 0)
        {
            tokenData.Write(procBytes, 0, procBytes.Length);
        }

        // Line number
        var lineBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lineBytes, lineNumber);
        tokenData.Write(lineBytes, 0, 4);

        // Write token length
        var lengthBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)tokenData.Length);
        ms.Write(lengthBytes, 0, 2);

        tokenData.Position = 0;
        tokenData.CopyTo(ms);
    }

    /// <summary>
    /// Writes a COLMETADATA token.
    /// </summary>
    private void WriteColMetadataToken(Stream ms, List<TdsColumnMetadata> columns)
    {
        ms.WriteByte((byte)TdsTokenType.ColMetadata);

        // Column count
        var countBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(countBytes, (ushort)columns.Count);
        ms.Write(countBytes, 0, 2);

        foreach (var column in columns)
        {
            WriteColumnMetadata(ms, column);
        }
    }

    /// <summary>
    /// Writes metadata for a single column.
    /// </summary>
    private void WriteColumnMetadata(Stream ms, TdsColumnMetadata column)
    {
        // User type (0 for regular columns)
        var userTypeBytes = new byte[4];
        ms.Write(userTypeBytes, 0, 4);

        // Flags
        var flagsBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(flagsBytes, column.Flags);
        ms.Write(flagsBytes, 0, 2);

        // Type info
        ms.WriteByte((byte)column.DataType);

        // Type-specific metadata
        switch (column.DataType)
        {
            case TdsDataType.Int:
            case TdsDataType.BigInt:
            case TdsDataType.SmallInt:
            case TdsDataType.TinyInt:
            case TdsDataType.Bit:
            case TdsDataType.Float:
            case TdsDataType.Real:
            case TdsDataType.DateTime:
            case TdsDataType.SmallDateTime:
                // Fixed-length types - no additional metadata
                break;

            case TdsDataType.IntN:
            case TdsDataType.FloatN:
            case TdsDataType.BitN:
            case TdsDataType.MoneyN:
            case TdsDataType.DateTimeN:
                // Nullable fixed types - write max length
                ms.WriteByte((byte)column.MaxLength);
                break;

            case TdsDataType.VarChar:
            case TdsDataType.Char:
                // Non-unicode string - max length + collation
                var lenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(lenBytes, (ushort)column.MaxLength);
                ms.Write(lenBytes, 0, 2);
                WriteCollation(ms, column.Collation);
                break;

            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
                // Unicode string - max length in bytes + collation
                var nlenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(nlenBytes, (ushort)(column.MaxLength * 2));
                ms.Write(nlenBytes, 0, 2);
                WriteCollation(ms, column.Collation);
                break;

            case TdsDataType.VarBinary:
            case TdsDataType.Binary:
                // Binary - max length
                var binLenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(binLenBytes, (ushort)column.MaxLength);
                ms.Write(binLenBytes, 0, 2);
                break;

            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
                // Decimal - length, precision, scale
                ms.WriteByte((byte)column.MaxLength);
                ms.WriteByte(column.Precision);
                ms.WriteByte(column.Scale);
                break;

            case TdsDataType.UniqueIdentifier:
                // GUID - write length (16)
                ms.WriteByte(16);
                break;

            case TdsDataType.Date:
            case TdsDataType.Time:
            case TdsDataType.DateTime2:
            case TdsDataType.DateTimeOffset:
                // Date/time types - scale
                ms.WriteByte(column.Scale);
                break;

            default:
                // For any other type, assume variable length
                if (column.MaxLength > 0)
                {
                    var defaultLenBytes = new byte[2];
                    BinaryPrimitives.WriteUInt16LittleEndian(defaultLenBytes, (ushort)column.MaxLength);
                    ms.Write(defaultLenBytes, 0, 2);
                }
                break;
        }

        // Column name
        var nameBytes = Encoding.Unicode.GetBytes(column.Name);
        ms.WriteByte((byte)column.Name.Length);
        ms.Write(nameBytes, 0, nameBytes.Length);
    }

    /// <summary>
    /// Writes collation info.
    /// </summary>
    private void WriteCollation(Stream ms, uint collation)
    {
        var collationBytes = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(collationBytes.AsSpan(0, 4), collation);
        collationBytes[4] = 0; // Sort ID
        ms.Write(collationBytes, 0, 5);
    }

    /// <summary>
    /// Writes a ROW token with column values.
    /// </summary>
    private void WriteRowToken(Stream ms, List<TdsColumnMetadata> columns, List<object?> values)
    {
        ms.WriteByte((byte)TdsTokenType.Row);

        for (var i = 0; i < columns.Count && i < values.Count; i++)
        {
            WriteColumnValue(ms, columns[i], values[i]);
        }
    }

    /// <summary>
    /// Writes a column value.
    /// </summary>
    private void WriteColumnValue(Stream ms, TdsColumnMetadata column, object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            WriteNullValue(ms, column);
            return;
        }

        switch (column.DataType)
        {
            case TdsDataType.Int:
                var intBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(intBytes, Convert.ToInt32(value));
                ms.Write(intBytes, 0, 4);
                break;

            case TdsDataType.BigInt:
                var longBytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(longBytes, Convert.ToInt64(value));
                ms.Write(longBytes, 0, 8);
                break;

            case TdsDataType.SmallInt:
                var shortBytes = new byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(shortBytes, Convert.ToInt16(value));
                ms.Write(shortBytes, 0, 2);
                break;

            case TdsDataType.TinyInt:
                ms.WriteByte(Convert.ToByte(value));
                break;

            case TdsDataType.Bit:
                ms.WriteByte(Convert.ToBoolean(value) ? (byte)1 : (byte)0);
                break;

            case TdsDataType.Float:
                var doubleBytes = BitConverter.GetBytes(Convert.ToDouble(value));
                ms.Write(doubleBytes, 0, 8);
                break;

            case TdsDataType.Real:
                var floatBytes = BitConverter.GetBytes(Convert.ToSingle(value));
                ms.Write(floatBytes, 0, 4);
                break;

            case TdsDataType.IntN:
                var intNVal = Convert.ToInt32(value);
                ms.WriteByte(4);
                var intNBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(intNBytes, intNVal);
                ms.Write(intNBytes, 0, 4);
                break;

            case TdsDataType.FloatN:
                var floatNVal = Convert.ToDouble(value);
                ms.WriteByte(8);
                var floatNBytes = BitConverter.GetBytes(floatNVal);
                ms.Write(floatNBytes, 0, 8);
                break;

            case TdsDataType.BitN:
                ms.WriteByte(1);
                ms.WriteByte(Convert.ToBoolean(value) ? (byte)1 : (byte)0);
                break;

            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
                var nstrVal = value.ToString() ?? "";
                var nstrBytes = Encoding.Unicode.GetBytes(nstrVal);
                var nstrLenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(nstrLenBytes, (ushort)nstrBytes.Length);
                ms.Write(nstrLenBytes, 0, 2);
                ms.Write(nstrBytes, 0, nstrBytes.Length);
                break;

            case TdsDataType.VarChar:
            case TdsDataType.Char:
                var strVal = value.ToString() ?? "";
                var strBytes = Encoding.ASCII.GetBytes(strVal);
                var strLenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(strLenBytes, (ushort)strBytes.Length);
                ms.Write(strLenBytes, 0, 2);
                ms.Write(strBytes, 0, strBytes.Length);
                break;

            case TdsDataType.VarBinary:
            case TdsDataType.Binary:
                var binVal = value as byte[] ?? Array.Empty<byte>();
                var binLenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(binLenBytes, (ushort)binVal.Length);
                ms.Write(binLenBytes, 0, 2);
                ms.Write(binVal, 0, binVal.Length);
                break;

            case TdsDataType.UniqueIdentifier:
                var guidVal = value is Guid g ? g : Guid.Parse(value.ToString() ?? Guid.Empty.ToString());
                ms.WriteByte(16);
                ms.Write(guidVal.ToByteArray(), 0, 16);
                break;

            case TdsDataType.DateTime:
            case TdsDataType.DateTimeN:
                var dtVal = value is DateTime dt ? dt : DateTime.Parse(value.ToString() ?? DateTime.MinValue.ToString());
                WriteDateTimeValue(ms, dtVal, column.DataType == TdsDataType.DateTimeN);
                break;

            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
                var decVal = Convert.ToDecimal(value);
                WriteDecimalValue(ms, decVal, column.Precision, column.Scale);
                break;

            default:
                // Default to string representation
                var defaultStr = value.ToString() ?? "";
                var defaultBytes = Encoding.Unicode.GetBytes(defaultStr);
                var defaultLenBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(defaultLenBytes, (ushort)defaultBytes.Length);
                ms.Write(defaultLenBytes, 0, 2);
                ms.Write(defaultBytes, 0, defaultBytes.Length);
                break;
        }
    }

    /// <summary>
    /// Writes a NULL value for the given column type.
    /// </summary>
    private void WriteNullValue(Stream ms, TdsColumnMetadata column)
    {
        switch (column.DataType)
        {
            case TdsDataType.IntN:
            case TdsDataType.FloatN:
            case TdsDataType.BitN:
            case TdsDataType.MoneyN:
            case TdsDataType.DateTimeN:
            case TdsDataType.UniqueIdentifier:
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
                // Nullable fixed types - write 0 length
                ms.WriteByte(0);
                break;

            case TdsDataType.NVarChar:
            case TdsDataType.VarChar:
            case TdsDataType.NChar:
            case TdsDataType.Char:
            case TdsDataType.VarBinary:
            case TdsDataType.Binary:
                // Variable length types - write 0xFFFF for NULL
                ms.WriteByte(0xFF);
                ms.WriteByte(0xFF);
                break;

            default:
                // For non-nullable types, we still need to write something
                // This shouldn't happen in practice with proper schema
                ms.WriteByte(0);
                break;
        }
    }

    /// <summary>
    /// Writes a DateTime value.
    /// </summary>
    private void WriteDateTimeValue(Stream ms, DateTime value, bool isNullable)
    {
        // SQL Server datetime: days since 1900-01-01 + time in 1/300 second intervals
        var baseDate = new DateTime(1900, 1, 1);
        var days = (value.Date - baseDate).Days;
        var time = (int)((value.TimeOfDay.TotalSeconds * 300) + 0.5);

        if (isNullable)
        {
            ms.WriteByte(8);
        }

        var dayBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(dayBytes, days);
        var timeBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(timeBytes, time);

        ms.Write(dayBytes, 0, 4);
        ms.Write(timeBytes, 0, 4);
    }

    /// <summary>
    /// Writes a decimal value.
    /// </summary>
    private void WriteDecimalValue(Stream ms, decimal value, byte precision, byte scale)
    {
        var isNegative = value < 0;
        value = Math.Abs(value);

        // Scale the value
        var scaledValue = value * (decimal)Math.Pow(10, scale);
        var bigInt = new System.Numerics.BigInteger(scaledValue);
        var bytes = bigInt.ToByteArray();

        // Determine length based on precision
        var len = precision switch
        {
            <= 9 => 5,
            <= 19 => 9,
            <= 28 => 13,
            _ => 17
        };

        ms.WriteByte((byte)len);
        ms.WriteByte(isNegative ? (byte)0 : (byte)1);

        // Write value bytes (pad to length - 1)
        var valueBytes = new byte[len - 1];
        Array.Copy(bytes, 0, valueBytes, 0, Math.Min(bytes.Length, len - 1));
        ms.Write(valueBytes, 0, valueBytes.Length);
    }

    /// <summary>
    /// Writes a DONE token.
    /// </summary>
    private void WriteDoneToken(Stream ms, DoneStatus status, ushort curCmd, ulong rowCount)
    {
        ms.WriteByte((byte)TdsTokenType.Done);

        var statusBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(statusBytes, (ushort)status);
        ms.Write(statusBytes, 0, 2);

        var curCmdBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(curCmdBytes, curCmd);
        ms.Write(curCmdBytes, 0, 2);

        var rowCountBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(rowCountBytes, rowCount);
        ms.Write(rowCountBytes, 0, 8);
    }

    /// <summary>
    /// Writes a DONEPROC token.
    /// </summary>
    private void WriteDoneProcToken(Stream ms, DoneStatus status, ushort curCmd, ulong rowCount)
    {
        ms.WriteByte((byte)TdsTokenType.DoneProc);

        var statusBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(statusBytes, (ushort)status);
        ms.Write(statusBytes, 0, 2);

        var curCmdBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(curCmdBytes, curCmd);
        ms.Write(curCmdBytes, 0, 2);

        var rowCountBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(rowCountBytes, rowCount);
        ms.Write(rowCountBytes, 0, 8);
    }

    /// <summary>
    /// Writes a complete message as one or more TDS packets.
    /// </summary>
    private async Task WritePacketAsync(TdsPacketType type, byte[] data, bool endOfMessage, CancellationToken ct)
    {
        var maxBodySize = _packetSize - HeaderSize;
        var offset = 0;

        while (offset < data.Length)
        {
            var remaining = data.Length - offset;
            var chunkSize = Math.Min(remaining, maxBodySize);
            var isLast = (offset + chunkSize) >= data.Length;

            var status = isLast && endOfMessage ? TdsPacketStatus.EndOfMessage : TdsPacketStatus.Normal;

            await WriteSinglePacketAsync(type, data, offset, chunkSize, status, ct);
            offset += chunkSize;
        }

        // If data is empty, still send a packet
        if (data.Length == 0)
        {
            await WriteSinglePacketAsync(type, Array.Empty<byte>(), 0, 0,
                endOfMessage ? TdsPacketStatus.EndOfMessage : TdsPacketStatus.Normal, ct);
        }
    }

    /// <summary>
    /// Writes a single TDS packet with header.
    /// </summary>
    private async Task WriteSinglePacketAsync(TdsPacketType type, byte[] data, int offset, int length, TdsPacketStatus status, CancellationToken ct)
    {
        var packet = new byte[HeaderSize + length];

        // Header
        packet[0] = (byte)type;
        packet[1] = (byte)status;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)(HeaderSize + length));
        packet[4] = 0; // SPID (set by server)
        packet[5] = 0;
        packet[6] = _packetId++;
        packet[7] = 0; // Window

        // Body
        if (length > 0)
        {
            Buffer.BlockCopy(data, offset, packet, HeaderSize, length);
        }

        await _stream.WriteAsync(packet, ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>
    /// Throws if the writer has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TdsPacketWriter));
        }
    }

    /// <summary>
    /// Disposes the writer.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
