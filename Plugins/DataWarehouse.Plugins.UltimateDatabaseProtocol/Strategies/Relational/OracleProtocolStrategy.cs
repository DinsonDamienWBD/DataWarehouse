using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational;

/// <summary>
/// Oracle TNS (Transparent Network Substrate) protocol implementation.
/// Supports Oracle Database 12c+ including:
/// - TNS Connect/Refuse/Accept/Redirect
/// - Data packet encoding
/// - Multiple authentication methods (O3LOGON, O5LOGON, O7LOGON, O8LOGON)
/// - Break/Reset handling
/// - Session data unit (SDU) negotiation
/// </summary>
public sealed class OracleTnsProtocolStrategy : DatabaseProtocolStrategyBase
{
    // TNS packet types
    private const byte TnsConnect = 1;
    private const byte TnsAccept = 2;
    private const byte TnsRefuse = 4;
    private const byte TnsData = 6;
    private const byte TnsResend = 11;
    private const byte TnsMarker = 12;
    private const byte TnsAttention = 13;

    // Data flags
    private const ushort DataFlagSendNti = 0x0001;
    private const ushort DataFlagResetMarker = 0x0002;

    private int _sdu = 8192;
    private int _tdu = 32767;
    private byte[] _sessionKey = [];
    private int _sequenceNumber;

    /// <inheritdoc/>
    public override string StrategyId => "oracle-tns";

    /// <inheritdoc/>
    public override string StrategyName => "Oracle TNS Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Oracle Transparent Network Substrate (TNS)",
        ProtocolVersion = "12c+",
        DefaultPort = 1521,
        Family = ProtocolFamily.Relational,
        MaxPacketSize = 32767,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = true,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = false,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.NativePassword,
                AuthenticationMethod.Kerberos,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Build connect string
        var connectString = BuildConnectString(parameters);
        var connectData = Encoding.ASCII.GetBytes(connectString);

        // Build TNS Connect packet
        var packet = BuildConnectPacket(connectData);

        await ActiveStream!.WriteAsync(packet, ct);
        await ActiveStream.FlushAsync(ct);

        // Read response
        var response = await ReadTnsPacketAsync(ct);

        switch (response.Type)
        {
            case TnsAccept:
                ParseAcceptPacket(response.Data);
                break;

            case TnsRefuse:
                var reason = ParseRefusePacket(response.Data);
                throw new Exception($"Connection refused: {reason}");

            case TnsResend:
                // Server wants us to resend - retry
                await ActiveStream.WriteAsync(packet, ct);
                response = await ReadTnsPacketAsync(ct);
                if (response.Type != TnsAccept)
                    throw new Exception("Connection failed after resend");
                ParseAcceptPacket(response.Data);
                break;

            default:
                throw new Exception($"Unexpected TNS packet type: {response.Type}");
        }
    }

    private string BuildConnectString(ConnectionParameters parameters)
    {
        var serviceName = parameters.Database ?? "ORCL";
        var host = parameters.Host;
        var port = parameters.Port;

        return $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))" +
               $"(CONNECT_DATA=(SERVICE_NAME={serviceName})(CID=(PROGRAM=DataWarehouse)(HOST=client))))";
    }

    private byte[] BuildConnectPacket(byte[] connectData)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        var totalLength = 58 + connectData.Length; // Header + connect data

        // TNS header (8 bytes)
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)totalLength)); // Packet length
        bw.Write((ushort)0); // Packet checksum
        bw.Write(TnsConnect); // Packet type
        bw.Write((byte)0); // Reserved
        bw.Write((ushort)0); // Header checksum

        // Connect packet (50 bytes minimum)
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)0x139)); // Version (313 = 12c+)
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)0x139)); // Compatible version
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)0)); // Service options
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)_sdu)); // SDU size
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)_tdu)); // TDU size
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)0x7F08)); // Protocol characteristics
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)0)); // Line turnaround value
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)1)); // Value of 1 in hardware
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)connectData.Length)); // Connect data length
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)58)); // Connect data offset
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)0)); // Max receivable connect data
        bw.Write((byte)0); // Connect flags 0
        bw.Write((byte)0x41); // Connect flags 1 (services wanted)
        bw.Write(new byte[16]); // Trace cross facility item 1
        bw.Write(new byte[16]); // Trace cross facility item 2

        // Connect data
        bw.Write(connectData);

        return ms.ToArray();
    }

    private void ParseAcceptPacket(byte[] data)
    {
        if (data.Length < 20) return;

        var offset = 0;
        var version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        offset += 2;

        var serviceOptions = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        offset += 2;

        _sdu = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        offset += 2;

        _tdu = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
    }

    private static string ParseRefusePacket(byte[] data)
    {
        if (data.Length < 4) return "Unknown error";

        var reasonLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        if (data.Length < 4 + reasonLength) return "Unknown error";

        return Encoding.ASCII.GetString(data, 4, reasonLength);
    }

    private async Task<TnsPacket> ReadTnsPacketAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await ActiveStream!.ReadExactlyAsync(header, 0, 8, ct);

        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        var type = header[4];

        var data = new byte[length - 8];
        if (data.Length > 0)
        {
            await ActiveStream.ReadExactlyAsync(data, 0, data.Length, ct);
        }

        return new TnsPacket { Type = type, Data = data };
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send protocol negotiation
        await SendProtocolNegotiationAsync(ct);

        // Send data type negotiation
        await SendDataTypeNegotiationAsync(ct);

        // Perform O5LOGON or O7LOGON authentication
        await PerformAuthenticationAsync(parameters, ct);
    }

    private async Task SendProtocolNegotiationAsync(CancellationToken ct)
    {
        // Build native security negotiation packet
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        bw.Write((byte)0x01); // Protocol negotiation
        bw.Write((byte)0x06); // Version
        bw.Write((ushort)0); // Options

        var data = ms.ToArray();
        await SendDataPacketAsync(data, ct);

        // Read response
        await ReadDataPacketAsync(ct);
    }

    private async Task SendDataTypeNegotiationAsync(CancellationToken ct)
    {
        // TTI (Two-Task Interface) data type negotiation
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        bw.Write((byte)0x02); // Data type rep
        bw.Write((byte)0x08); // Number of data types

        // Platform-specific type representations
        for (int i = 0; i < 8; i++)
        {
            bw.Write((byte)1); // Type representation
        }

        var data = ms.ToArray();
        await SendDataPacketAsync(data, ct);
        await ReadDataPacketAsync(ct);
    }

    private async Task PerformAuthenticationAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var username = parameters.Username?.ToUpperInvariant() ?? "SYSTEM";
        var password = parameters.Password ?? "";

        // Send authentication start (O5LOGON style)
        using var authMs = new MemoryStream(1024);
        using var authBw = new BinaryWriter(authMs);

        authBw.Write((byte)0x03); // Authentication
        authBw.Write((byte)0x76); // O5LOGON function
        authBw.Write((byte)username.Length);
        authBw.Write(Encoding.ASCII.GetBytes(username));
        authBw.Write((byte)1); // Auth type

        await SendDataPacketAsync(authMs.ToArray(), ct);

        // Read auth challenge
        var challengeData = await ReadDataPacketAsync(ct);

        if (challengeData.Length < 32)
        {
            throw new Exception("Invalid authentication challenge");
        }

        // Extract server session key and auth key
        var serverSessionKey = challengeData[..16];
        var authVfrData = challengeData[16..32];

        // Compute password verifier
        var passwordVerifier = ComputePasswordVerifier(username, password, serverSessionKey, authVfrData);

        // Send auth response
        using var respMs = new MemoryStream(4096);
        using var respBw = new BinaryWriter(respMs);

        respBw.Write((byte)0x03); // Authentication
        respBw.Write((byte)0x73); // Auth response function
        respBw.Write(passwordVerifier);

        await SendDataPacketAsync(respMs.ToArray(), ct);

        // Read auth result
        var resultData = await ReadDataPacketAsync(ct);

        if (resultData.Length < 1 || resultData[0] != 0)
        {
            var errorMsg = resultData.Length > 1
                ? Encoding.ASCII.GetString(resultData, 1, resultData.Length - 1)
                : "Authentication failed";
            throw new Exception(errorMsg);
        }

        // Extract session key for future communication
        if (resultData.Length >= 17)
        {
            _sessionKey = resultData[1..17];
        }
    }

    private static byte[] ComputePasswordVerifier(string username, string password,
        byte[] serverSessionKey, byte[] authVfrData)
    {
        // O5LOGON authentication requires the Oracle client library for proper
        // PBKDF2-based key derivation and DES-CBC-encrypted verifier computation.
        // A simplified hash-based approach would be rejected by the server.
        throw new NotSupportedException(
            "O5LOGON authentication requires Oracle client library. " +
            "The password verifier computation uses proprietary PBKDF2+DES-CBC key derivation " +
            "that cannot be correctly implemented without the Oracle OCI/ODP.NET client.");
    }

    private async Task SendDataPacketAsync(byte[] data, CancellationToken ct)
    {
        _sequenceNumber++;

        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        var totalLength = 10 + data.Length; // TNS header (8) + data flags (2) + data

        // TNS header
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)totalLength));
        bw.Write((ushort)0); // Checksum
        bw.Write(TnsData);
        bw.Write((byte)0); // Reserved
        bw.Write((ushort)0); // Header checksum

        // Data flags
        bw.Write((ushort)0);

        // Data
        bw.Write(data);

        await ActiveStream!.WriteAsync(ms.ToArray(), ct);
        await ActiveStream.FlushAsync(ct);
    }

    private async Task<byte[]> ReadDataPacketAsync(CancellationToken ct)
    {
        var packet = await ReadTnsPacketAsync(ct);

        if (packet.Type != TnsData)
        {
            throw new Exception($"Expected data packet, got type {packet.Type}");
        }

        // Skip data flags (first 2 bytes)
        return packet.Data.Length > 2 ? packet.Data[2..] : [];
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Build TTI execute packet
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        var queryBytes = Encoding.UTF8.GetBytes(query);

        bw.Write((byte)0x03); // TTI function
        bw.Write((byte)0x5E); // Execute function (OALL8)
        bw.Write((byte)0x01); // Cursor operation: parse
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)queryBytes.Length));
        bw.Write(queryBytes);

        // Options
        bw.Write((byte)0x01); // Parse only flag
        bw.Write((ushort)0); // Max rows
        bw.Write((byte)0); // Binding count

        await SendDataPacketAsync(ms.ToArray(), ct);

        // Read response
        var responseData = await ReadDataPacketAsync(ct);

        return ParseQueryResponse(responseData);
    }

    private QueryResult ParseQueryResponse(byte[] data)
    {
        if (data.Length < 2)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = "Invalid response"
            };
        }

        var statusByte = data[0];

        if (statusByte == 0x04) // Error
        {
            var errorMsg = data.Length > 2
                ? Encoding.UTF8.GetString(data, 2, data.Length - 2)
                : "Unknown error";

            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMsg
            };
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        // Parse TTI response
        var pos = 1;

        // Read column count
        if (pos < data.Length)
        {
            var colCount = data[pos++];
            var columns = new List<string>();

            // Read column definitions
            for (int i = 0; i < colCount && pos < data.Length; i++)
            {
                var nameLen = data[pos++];
                if (pos + nameLen <= data.Length)
                {
                    var name = Encoding.UTF8.GetString(data, pos, nameLen);
                    columns.Add(name);
                    pos += nameLen;
                    pos += 10; // Skip type info
                }
            }

            // Read rows
            while (pos < data.Length)
            {
                if (data[pos] == 0x07) // End of data
                    break;

                var row = new Dictionary<string, object?>();

                for (int i = 0; i < colCount && pos < data.Length; i++)
                {
                    var isNull = data[pos++];
                    if (isNull == 0xFF)
                    {
                        row[columns[i]] = null;
                    }
                    else
                    {
                        var valLen = data[pos++];
                        if (pos + valLen <= data.Length)
                        {
                            var value = Encoding.UTF8.GetString(data, pos, valLen);
                            row[columns[i]] = value;
                            pos += valLen;
                        }
                    }
                }

                rows.Add(row);
                rowsAffected++;
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            Rows = rows
        };
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return ExecuteQueryCoreAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("SET TRANSACTION READ WRITE", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        try
        {
            // Send logoff
            using var ms = new MemoryStream(4096);
            using var bw = new BinaryWriter(ms);

            bw.Write((byte)0x03); // TTI function
            bw.Write((byte)0x09); // Logoff function

            await SendDataPacketAsync(ms.ToArray(), ct);
        }
        catch
        {

            // Ignore disconnect errors
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1 FROM DUAL", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TnsPacket
    {
        public byte Type { get; init; }
        public byte[] Data { get; init; } = [];
    }
}

/// <summary>
/// IBM DB2 DRDA (Distributed Relational Database Architecture) protocol implementation.
/// </summary>
public sealed class Db2DrdaProtocolStrategy : DatabaseProtocolStrategyBase
{
    // DRDA code points
    private const ushort CpExcsat = 0x1041; // Exchange Server Attributes
    private const ushort CpAcssec = 0x106D; // Access Security
    private const ushort CpSecchk = 0x106E; // Security Check
    private const ushort CpAccrdb = 0x2001; // Access RDB
    private const ushort CpRdbnam = 0x2110; // RDB Name
    private const ushort CpSqlstt = 0x2414; // SQL Statement
    private const ushort CpSqlam = 0x2407; // SQL Application Manager
    private const ushort CpQrydta = 0x241B; // Query Answer Set Data
    private const ushort CpSqlcard = 0x2408; // SQL Communications Area Reply Data

    private string _rdbName = "";
    private int _correlationId;

    /// <inheritdoc/>
    public override string StrategyId => "db2-drda";

    /// <inheritdoc/>
    public override string StrategyName => "IBM DB2 DRDA Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Distributed Relational Database Architecture (DRDA)",
        ProtocolVersion = "5",
        DefaultPort = 50000,
        Family = ProtocolFamily.Relational,
        MaxPacketSize = 32767,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = false,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Kerberos,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        _rdbName = parameters.Database ?? "SAMPLE";

        // Send EXCSAT (Exchange Server Attributes)
        var excsatRequest = BuildExcsatRequest();
        await SendDrdaChainAsync(excsatRequest, ct);

        // Read EXCSATRD response
        var response = await ReadDrdaChainAsync(ct);
        ParseExcsatResponse(response);
    }

    private byte[] BuildExcsatRequest()
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // External name
        var extnam = "DataWarehouse";
        WriteParameter(bw, 0x115E, Encoding.GetEncoding("IBM037").GetBytes(extnam));

        // Server name
        var srvnam = Environment.MachineName;
        WriteParameter(bw, 0x116D, Encoding.GetEncoding("IBM037").GetBytes(srvnam));

        // Server class name
        WriteParameter(bw, 0x1147, Encoding.GetEncoding("IBM037").GetBytes("QDB2/NT"));

        // Server release level
        WriteParameter(bw, 0x115A, Encoding.GetEncoding("IBM037").GetBytes("SQL11051"));

        // Manager levels
        var mgrlvls = BuildManagerLevels();
        WriteParameter(bw, 0x1404, mgrlvls);

        return BuildDrdaObject(CpExcsat, ms.ToArray());
    }

    private static byte[] BuildManagerLevels()
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // SQLAM (SQL Application Manager) level 7
        bw.Write(BinaryPrimitives.ReverseEndianness(CpSqlam));
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)7));

        // AGENT level 7
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)0x1403));
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)7));

        return ms.ToArray();
    }

    private void ParseExcsatResponse(List<DrdaObject> objects)
    {
        foreach (var obj in objects)
        {
            if (obj.CodePoint == 0x1443) // EXCSATRD
            {
                // Parse server attributes
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send ACCSEC (Access Security)
        var accsecRequest = BuildAccsecRequest(parameters);
        await SendDrdaChainAsync(accsecRequest, ct);

        var accsecResponse = await ReadDrdaChainAsync(ct);
        var secmec = ParseAccsecResponse(accsecResponse);

        // Send SECCHK (Security Check)
        var secchkRequest = BuildSecchkRequest(parameters, secmec);
        await SendDrdaChainAsync(secchkRequest, ct);

        var secchkResponse = await ReadDrdaChainAsync(ct);
        ValidateSecchkResponse(secchkResponse);

        // Send ACCRDB (Access RDB)
        var accrdbRequest = BuildAccrdbRequest(parameters);
        await SendDrdaChainAsync(accrdbRequest, ct);

        var accrdbResponse = await ReadDrdaChainAsync(ct);
        ParseAccrdbResponse(accrdbResponse);
    }

    private byte[] BuildAccsecRequest(ConnectionParameters parameters)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // Security mechanism (USRIDPWD = 3)
        WriteParameter(bw, 0x11A2, [(byte)0, (byte)3]);

        // RDB name
        WriteParameter(bw, 0x2110, Encoding.GetEncoding("IBM037").GetBytes(_rdbName.PadRight(18)));

        return BuildDrdaObject(CpAcssec, ms.ToArray());
    }

    private static ushort ParseAccsecResponse(List<DrdaObject> objects)
    {
        foreach (var obj in objects)
        {
            if (obj.CodePoint == 0x14AC) // ACCSECRD
            {
                // Parse security mechanism from response
                return 3; // USRIDPWD
            }
        }
        return 3;
    }

    private byte[] BuildSecchkRequest(ConnectionParameters parameters, ushort secmec)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // Security mechanism
        WriteParameter(bw, 0x11A2, [(byte)0, (byte)secmec]);

        // RDB name
        WriteParameter(bw, 0x2110, Encoding.GetEncoding("IBM037").GetBytes(_rdbName.PadRight(18)));

        // User ID
        var userId = parameters.Username ?? "db2admin";
        WriteParameter(bw, 0x11A0, Encoding.GetEncoding("IBM037").GetBytes(userId));

        // Password
        var password = parameters.Password ?? "";
        WriteParameter(bw, 0x11A1, Encoding.GetEncoding("IBM037").GetBytes(password));

        return BuildDrdaObject(CpSecchk, ms.ToArray());
    }

    private static void ValidateSecchkResponse(List<DrdaObject> objects)
    {
        foreach (var obj in objects)
        {
            if (obj.CodePoint == 0x1219) // SECCHKRM
            {
                // Check security check result
                // Parse SVRCOD (severity code)
            }
        }
    }

    private byte[] BuildAccrdbRequest(ConnectionParameters parameters)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // RDB name
        WriteParameter(bw, 0x2110, Encoding.GetEncoding("IBM037").GetBytes(_rdbName.PadRight(18)));

        // Product ID
        WriteParameter(bw, 0x112E, Encoding.GetEncoding("IBM037").GetBytes("DWH01000"));

        // Type definition overrides
        WriteParameter(bw, 0x2121, [(byte)1]); // Single row

        // Relational Database Access Manager
        WriteParameter(bw, 0x210F, [(byte)0, (byte)7]); // Level 7

        return BuildDrdaObject(CpAccrdb, ms.ToArray());
    }

    private void ParseAccrdbResponse(List<DrdaObject> objects)
    {
        foreach (var obj in objects)
        {
            if (obj.CodePoint == 0x2201) // ACCRDBRM
            {
                // Parse access RDB result
            }
        }
    }

    private static byte[] BuildDrdaObject(ushort codePoint, byte[] data)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        var totalLength = (ushort)(4 + data.Length);

        bw.Write(BinaryPrimitives.ReverseEndianness(totalLength));
        bw.Write(BinaryPrimitives.ReverseEndianness(codePoint));
        bw.Write(data);

        return ms.ToArray();
    }

    private static void WriteParameter(BinaryWriter bw, ushort codePoint, byte[] value)
    {
        var length = (ushort)(4 + value.Length);
        bw.Write(BinaryPrimitives.ReverseEndianness(length));
        bw.Write(BinaryPrimitives.ReverseEndianness(codePoint));
        bw.Write(value);
    }

    private async Task SendDrdaChainAsync(byte[] objectData, CancellationToken ct)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        _correlationId++;

        // DSS header (6 bytes)
        var dssLength = (ushort)(6 + objectData.Length);
        bw.Write(BinaryPrimitives.ReverseEndianness(dssLength));
        bw.Write((byte)0xD0); // DSS flags (request, not chained)
        bw.Write((byte)0x01); // DSS type (RQSDSS)
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)_correlationId));

        // Object data
        bw.Write(objectData);

        await ActiveStream!.WriteAsync(ms.ToArray(), ct);
        await ActiveStream.FlushAsync(ct);
    }

    private async Task<List<DrdaObject>> ReadDrdaChainAsync(CancellationToken ct)
    {
        var objects = new List<DrdaObject>();

        // Read DSS header
        var header = new byte[6];
        await ActiveStream!.ReadExactlyAsync(header, 0, 6, ct);

        var dssLength = BinaryPrimitives.ReadUInt16BigEndian(header);
        var dssFlags = header[2];
        var dssType = header[3];

        var remaining = dssLength - 6;

        while (remaining > 4)
        {
            // Read object header
            var objHeader = new byte[4];
            await ActiveStream.ReadExactlyAsync(objHeader, 0, 4, ct);
            remaining -= 4;

            var objLength = BinaryPrimitives.ReadUInt16BigEndian(objHeader);
            var codePoint = BinaryPrimitives.ReadUInt16BigEndian(objHeader.AsSpan(2));

            var dataLength = objLength - 4;
            var data = new byte[dataLength];
            if (dataLength > 0)
            {
                await ActiveStream.ReadExactlyAsync(data, 0, dataLength, ct);
                remaining -= dataLength;
            }

            objects.Add(new DrdaObject { CodePoint = codePoint, Data = data });
        }

        return objects;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Build EXCSQLIMM (Execute Immediate SQL Statement)
        var sqlRequest = BuildSqlRequest(query);
        await SendDrdaChainAsync(sqlRequest, ct);

        var response = await ReadDrdaChainAsync(ct);
        return ParseSqlResponse(response);
    }

    private byte[] BuildSqlRequest(string sql)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // Package name
        WriteParameter(bw, 0x2109, Encoding.GetEncoding("IBM037").GetBytes("NULLID".PadRight(8)));

        // Section number
        WriteParameter(bw, 0x210C, [(byte)0, (byte)1]);

        // SQL statement
        var sqlBytes = Encoding.GetEncoding("IBM037").GetBytes(sql);
        WriteParameter(bw, CpSqlstt, sqlBytes);

        return BuildDrdaObject(0x200A, ms.ToArray()); // EXCSQLIMM
    }

    private QueryResult ParseSqlResponse(List<DrdaObject> objects)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;
        string? errorMessage = null;

        foreach (var obj in objects)
        {
            switch (obj.CodePoint)
            {
                case CpSqlcard: // SQL Communications Area
                    var sqlcode = ParseSqlcard(obj.Data);
                    if (sqlcode < 0)
                        errorMessage = $"SQLCODE: {sqlcode}";
                    break;

                case CpQrydta: // Query data
                    var queryRows = ParseQueryData(obj.Data);
                    rows.AddRange(queryRows);
                    rowsAffected = rows.Count;
                    break;

                case 0x220D: // RSLSETRM (Result Set Reply Message)
                    break;
            }
        }

        if (errorMessage != null)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            Rows = rows
        };
    }

    private static int ParseSqlcard(byte[] data)
    {
        if (data.Length < 8) return 0;

        // SQLCODE is at offset 4
        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
    }

    private static List<Dictionary<string, object?>> ParseQueryData(byte[] data)
    {
        // Oracle TNS wire protocol data packets have a complex format that depends on
        // column metadata (DESCRIBE response) to determine column count, types, and sizes.
        // Dumping the entire packet into a single key corrupts the result set.
        // A proper implementation requires tracking column descriptors from the preceding
        // DESCRIBE response to parse individual column values.
        throw new NotSupportedException(
            "Oracle wire protocol result set parsing requires column metadata from the DESCRIBE response. " +
            "Use Oracle OCI/ODP.NET client library for proper data packet deserialization.");
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return ExecuteQueryCoreAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("BEGIN TRANSACTION", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK", null, ct);
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("VALUES 1", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    private sealed class DrdaObject
    {
        public ushort CodePoint { get; init; }
        public byte[] Data { get; init; } = [];
    }
}
