using System.Buffers.Binary;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.NoSQL;

/// <summary>
/// Memcached binary and text protocol implementation.
/// Supports both the text protocol (ASCII) and binary protocol including:
/// - GET, SET, ADD, REPLACE, DELETE, INCR, DECR
/// - APPEND, PREPEND
/// - CAS (Check and Set) operations
/// - STATS, VERSION, FLUSH_ALL
/// - Multi-get operations
/// - SASL authentication (binary protocol)
/// </summary>
public sealed class MemcachedProtocolStrategy : DatabaseProtocolStrategyBase
{
    // Binary protocol magic bytes
    private const byte RequestMagic = 0x80;
    private const byte ResponseMagic = 0x81;

    // Binary protocol opcodes
    private const byte OpGet = 0x00;
    private const byte OpSet = 0x01;
    private const byte OpAdd = 0x02;
    private const byte OpReplace = 0x03;
    private const byte OpDelete = 0x04;
    private const byte OpIncrement = 0x05;
    private const byte OpDecrement = 0x06;
    private const byte OpQuit = 0x07;
    private const byte OpFlush = 0x08;
    private const byte OpGetQ = 0x09;
    private const byte OpNoop = 0x0a;
    private const byte OpVersion = 0x0b;
    private const byte OpGetK = 0x0c;
    private const byte OpGetKQ = 0x0d;
    private const byte OpAppend = 0x0e;
    private const byte OpPrepend = 0x0f;
    private const byte OpStat = 0x10;
    private const byte OpSetQ = 0x11;
    private const byte OpAddQ = 0x12;
    private const byte OpReplaceQ = 0x13;
    private const byte OpDeleteQ = 0x14;
    private const byte OpSaslListMechs = 0x20;
    private const byte OpSaslAuth = 0x21;
    private const byte OpSaslStep = 0x22;
    private const byte OpTouch = 0x1c;
    private const byte OpGat = 0x1d;

    // Binary protocol status codes
    private const ushort StatusNoError = 0x0000;
    private const ushort StatusKeyNotFound = 0x0001;
    private const ushort StatusKeyExists = 0x0002;
    private const ushort StatusValueTooLarge = 0x0003;
    private const ushort StatusInvalidArguments = 0x0004;
    private const ushort StatusItemNotStored = 0x0005;
    private const ushort StatusAuthError = 0x0020;
    private const ushort StatusAuthContinue = 0x0021;

    private bool _useBinaryProtocol = true;
    private string _serverVersion = "";
    private uint _opaque;
    private StreamReader? _textReader;
    private StreamWriter? _textWriter;

    /// <inheritdoc/>
    public override string StrategyId => "memcached";

    /// <inheritdoc/>
    public override string StrategyName => "Memcached Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Memcached Binary/Text Protocol",
        ProtocolVersion = "1.6",
        DefaultPort = 11211,
        Family = ProtocolFamily.NoSQL,
        MaxPacketSize = 1024 * 1024, // 1 MB default
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = false,
            SupportsPreparedStatements = false,
            SupportsCursors = false,
            SupportsStreaming = false,
            SupportsBatch = true, // Multi-get
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = false,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.SASL
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Try to get version to verify connection
        try
        {
            // Try binary protocol first
            var versionResponse = await SendBinaryCommandAsync(OpVersion, "", null, 0, 0, ct);
            if (versionResponse.Status == StatusNoError)
            {
                _useBinaryProtocol = true;
                _serverVersion = Encoding.UTF8.GetString(versionResponse.Value ?? []);
                return;
            }
        }
        catch
        {

            // Fall back to text protocol
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        // Initialize text protocol
        _useBinaryProtocol = false;
        _textReader = new StreamReader(ActiveStream!, Encoding.UTF8, leaveOpen: true);
        _textWriter = new StreamWriter(ActiveStream!, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        await _textWriter.WriteLineAsync("version");
        var line = await _textReader.ReadLineAsync(ct);
        if (line != null && line.StartsWith("VERSION "))
        {
            _serverVersion = line[8..];
        }
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parameters.Password))
        {
            return;
        }

        if (!_useBinaryProtocol)
        {
            // Text protocol doesn't support authentication
            throw new NotSupportedException("Memcached text protocol does not support authentication");
        }

        // SASL authentication
        var mechResponse = await SendBinaryCommandAsync(OpSaslListMechs, "", null, 0, 0, ct);
        var mechanisms = Encoding.UTF8.GetString(mechResponse.Value ?? []).Split(' ');

        if (mechanisms.Contains("PLAIN"))
        {
            // PLAIN mechanism: \0username\0password
            var authData = $"\0{parameters.Username}\0{parameters.Password}";
            var authBytes = Encoding.UTF8.GetBytes(authData);

            var authResponse = await SendBinaryCommandAsync(
                OpSaslAuth, "PLAIN", authBytes, 0, 0, ct);

            if (authResponse.Status != StatusNoError)
            {
                throw new InvalidOperationException($"Authentication failed: {GetStatusMessage(authResponse.Status)}");
            }
        }
        else
        {
            throw new NotSupportedException($"No supported SASL mechanism found. Available: {string.Join(", ", mechanisms)}");
        }
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var parts = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = "Empty command"
            };
        }

        var command = parts[0].ToUpperInvariant();

        return _useBinaryProtocol
            ? await ExecuteBinaryCommandAsync(command, parts, parameters, ct)
            : await ExecuteTextCommandAsync(command, parts, parameters, ct);
    }

    private async Task<QueryResult> ExecuteBinaryCommandAsync(
        string command,
        string[] parts,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        switch (command)
        {
            case "GET":
            case "GETS":
                {
                    if (parts.Length < 2)
                        return new QueryResult { Success = false, ErrorMessage = "GET requires key" };

                    var keys = parts.Skip(1).ToArray();
                    if (keys.Length == 1)
                    {
                        var response = await SendBinaryCommandAsync(OpGet, keys[0], null, 0, 0, ct);
                        if (response.Status == StatusNoError)
                        {
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["key"] = keys[0],
                                ["value"] = Encoding.UTF8.GetString(response.Value ?? []),
                                ["flags"] = response.Flags,
                                ["cas"] = response.Cas
                            });
                            rowsAffected = 1;
                        }
                        else if (response.Status != StatusKeyNotFound)
                        {
                            return new QueryResult
                            {
                                Success = false,
                                ErrorMessage = GetStatusMessage(response.Status),
                                ErrorCode = response.Status.ToString()
                            };
                        }
                    }
                    else
                    {
                        // Multi-get using quiet commands
                        foreach (var key in keys[..^1])
                        {
                            await SendBinaryCommandNoResponseAsync(OpGetKQ, key, null, 0, 0, ct);
                        }
                        // Last key uses non-quiet to get final response
                        var lastResponse = await SendBinaryCommandAsync(OpGetK, keys[^1], null, 0, 0, ct);

                        // Read all queued responses
                        while (true)
                        {
                            try
                            {
                                var response = await ReadBinaryResponseAsync(ct);
                                if (response.Key != null)
                                {
                                    rows.Add(new Dictionary<string, object?>
                                    {
                                        ["key"] = response.Key,
                                        ["value"] = Encoding.UTF8.GetString(response.Value ?? []),
                                        ["flags"] = response.Flags,
                                        ["cas"] = response.Cas
                                    });
                                    rowsAffected++;
                                }

                                if (response.Key == keys[^1])
                                    break;
                            }
                            catch
                            {
                                break;
                            }
                        }

                        if (lastResponse.Status == StatusNoError && lastResponse.Key != null)
                        {
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["key"] = lastResponse.Key,
                                ["value"] = Encoding.UTF8.GetString(lastResponse.Value ?? []),
                                ["flags"] = lastResponse.Flags,
                                ["cas"] = lastResponse.Cas
                            });
                            rowsAffected++;
                        }
                    }
                    break;
                }

            case "SET":
                {
                    if (parts.Length < 3)
                        return new QueryResult { Success = false, ErrorMessage = "SET requires key and value" };

                    var key = parts[1];
                    var value = string.Join(" ", parts.Skip(2));
                    var flags = (uint)(parameters?.GetValueOrDefault("flags") ?? 0);
                    var expiry = (uint)(parameters?.GetValueOrDefault("expiry") ?? 0);
                    var cas = (ulong)(parameters?.GetValueOrDefault("cas") ?? 0UL);

                    var response = await SendBinaryCommandAsync(
                        OpSet, key, Encoding.UTF8.GetBytes(value), flags, expiry, ct, cas);

                    if (response.Status != StatusNoError)
                    {
                        return new QueryResult
                        {
                            Success = false,
                            ErrorMessage = GetStatusMessage(response.Status),
                            ErrorCode = response.Status.ToString()
                        };
                    }

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["cas"] = response.Cas,
                        ["stored"] = true
                    });
                    rowsAffected = 1;
                    break;
                }

            case "ADD":
                {
                    if (parts.Length < 3)
                        return new QueryResult { Success = false, ErrorMessage = "ADD requires key and value" };

                    var key = parts[1];
                    var value = string.Join(" ", parts.Skip(2));
                    var flags = (uint)(parameters?.GetValueOrDefault("flags") ?? 0);
                    var expiry = (uint)(parameters?.GetValueOrDefault("expiry") ?? 0);

                    var response = await SendBinaryCommandAsync(
                        OpAdd, key, Encoding.UTF8.GetBytes(value), flags, expiry, ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["stored"] = response.Status == StatusNoError,
                        ["exists"] = response.Status == StatusKeyExists
                    });
                    rowsAffected = response.Status == StatusNoError ? 1 : 0;
                    break;
                }

            case "REPLACE":
                {
                    if (parts.Length < 3)
                        return new QueryResult { Success = false, ErrorMessage = "REPLACE requires key and value" };

                    var key = parts[1];
                    var value = string.Join(" ", parts.Skip(2));
                    var flags = (uint)(parameters?.GetValueOrDefault("flags") ?? 0);
                    var expiry = (uint)(parameters?.GetValueOrDefault("expiry") ?? 0);

                    var response = await SendBinaryCommandAsync(
                        OpReplace, key, Encoding.UTF8.GetBytes(value), flags, expiry, ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["stored"] = response.Status == StatusNoError,
                        ["not_found"] = response.Status == StatusKeyNotFound
                    });
                    rowsAffected = response.Status == StatusNoError ? 1 : 0;
                    break;
                }

            case "DELETE":
                {
                    if (parts.Length < 2)
                        return new QueryResult { Success = false, ErrorMessage = "DELETE requires key" };

                    var key = parts[1];
                    var response = await SendBinaryCommandAsync(OpDelete, key, null, 0, 0, ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["deleted"] = response.Status == StatusNoError,
                        ["not_found"] = response.Status == StatusKeyNotFound
                    });
                    rowsAffected = response.Status == StatusNoError ? 1 : 0;
                    break;
                }

            case "INCR":
            case "INCREMENT":
                {
                    if (parts.Length < 2)
                        return new QueryResult { Success = false, ErrorMessage = "INCR requires key" };

                    var key = parts[1];
                    var delta = parts.Length > 2 && ulong.TryParse(parts[2], out var d) ? d : 1UL;
                    var initial = (ulong)(parameters?.GetValueOrDefault("initial") ?? 0UL);
                    var expiry = (uint)(parameters?.GetValueOrDefault("expiry") ?? 0);

                    var response = await SendBinaryIncrDecrAsync(OpIncrement, key, delta, initial, expiry, ct);

                    if (response.Status != StatusNoError)
                    {
                        return new QueryResult
                        {
                            Success = false,
                            ErrorMessage = GetStatusMessage(response.Status),
                            ErrorCode = response.Status.ToString()
                        };
                    }

                    var newValue = BinaryPrimitives.ReadUInt64BigEndian(response.Value ?? new byte[8]);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["value"] = newValue
                    });
                    rowsAffected = 1;
                    break;
                }

            case "DECR":
            case "DECREMENT":
                {
                    if (parts.Length < 2)
                        return new QueryResult { Success = false, ErrorMessage = "DECR requires key" };

                    var key = parts[1];
                    var delta = parts.Length > 2 && ulong.TryParse(parts[2], out var d) ? d : 1UL;
                    var initial = (ulong)(parameters?.GetValueOrDefault("initial") ?? 0UL);
                    var expiry = (uint)(parameters?.GetValueOrDefault("expiry") ?? 0);

                    var response = await SendBinaryIncrDecrAsync(OpDecrement, key, delta, initial, expiry, ct);

                    if (response.Status != StatusNoError)
                    {
                        return new QueryResult
                        {
                            Success = false,
                            ErrorMessage = GetStatusMessage(response.Status),
                            ErrorCode = response.Status.ToString()
                        };
                    }

                    var newValue = BinaryPrimitives.ReadUInt64BigEndian(response.Value ?? new byte[8]);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["value"] = newValue
                    });
                    rowsAffected = 1;
                    break;
                }

            case "APPEND":
                {
                    if (parts.Length < 3)
                        return new QueryResult { Success = false, ErrorMessage = "APPEND requires key and value" };

                    var key = parts[1];
                    var value = string.Join(" ", parts.Skip(2));

                    var response = await SendBinaryCommandAsync(
                        OpAppend, key, Encoding.UTF8.GetBytes(value), 0, 0, ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["stored"] = response.Status == StatusNoError
                    });
                    rowsAffected = response.Status == StatusNoError ? 1 : 0;
                    break;
                }

            case "PREPEND":
                {
                    if (parts.Length < 3)
                        return new QueryResult { Success = false, ErrorMessage = "PREPEND requires key and value" };

                    var key = parts[1];
                    var value = string.Join(" ", parts.Skip(2));

                    var response = await SendBinaryCommandAsync(
                        OpPrepend, key, Encoding.UTF8.GetBytes(value), 0, 0, ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["stored"] = response.Status == StatusNoError
                    });
                    rowsAffected = response.Status == StatusNoError ? 1 : 0;
                    break;
                }

            case "TOUCH":
                {
                    if (parts.Length < 3)
                        return new QueryResult { Success = false, ErrorMessage = "TOUCH requires key and expiry" };

                    var key = parts[1];
                    // P2-2715: use TryParse to avoid FormatException on non-numeric expiry values.
                    if (!uint.TryParse(parts[2], out var expiry))
                        return new QueryResult { Success = false, ErrorMessage = $"TOUCH: invalid expiry value '{parts[2]}'" };

                    var response = await SendBinaryCommandAsync(OpTouch, key, null, 0, expiry, ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["touched"] = response.Status == StatusNoError
                    });
                    rowsAffected = response.Status == StatusNoError ? 1 : 0;
                    break;
                }

            case "STATS":
                {
                    var statKey = parts.Length > 1 ? parts[1] : "";
                    var response = await SendBinaryCommandAsync(OpStat, statKey, null, 0, 0, ct);

                    // Stats returns multiple responses terminated by empty key
                    while (response.Key != null && response.Key.Length > 0)
                    {
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["stat"] = response.Key,
                            ["value"] = Encoding.UTF8.GetString(response.Value ?? [])
                        });
                        rowsAffected++;
                        response = await ReadBinaryResponseAsync(ct);
                    }
                    break;
                }

            case "VERSION":
                {
                    var response = await SendBinaryCommandAsync(OpVersion, "", null, 0, 0, ct);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["version"] = Encoding.UTF8.GetString(response.Value ?? [])
                    });
                    rowsAffected = 1;
                    break;
                }

            case "FLUSH_ALL":
            case "FLUSH":
                {
                    var expiry = parts.Length > 1 && uint.TryParse(parts[1], out var e) ? e : 0u;
                    var response = await SendBinaryCommandAsync(OpFlush, "", null, 0, expiry, ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["flushed"] = response.Status == StatusNoError
                    });
                    rowsAffected = response.Status == StatusNoError ? 1 : 0;
                    break;
                }

            default:
                return new QueryResult
                {
                    Success = false,
                    ErrorMessage = $"Unknown command: {command}"
                };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            Rows = rows
        };
    }

    private async Task<QueryResult> ExecuteTextCommandAsync(
        string command,
        string[] parts,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_textWriter == null || _textReader == null)
            throw new InvalidOperationException("Text protocol not initialized");

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long rowsAffected = 0;

        switch (command)
        {
            case "GET":
            case "GETS":
                {
                    await _textWriter.WriteLineAsync(string.Join(" ", parts));

                    while (true)
                    {
                        var line = await _textReader.ReadLineAsync(ct);
                        if (line == null || line == "END")
                            break;

                        if (line.StartsWith("VALUE "))
                        {
                            var valueParts = line.Split(' ');
                            var key = valueParts[1];
                            var flags = uint.Parse(valueParts[2]);
                            var length = int.Parse(valueParts[3]);
                            var cas = valueParts.Length > 4 ? ulong.Parse(valueParts[4]) : 0UL;

                            var valueBuffer = new char[length];
                            await _textReader.ReadBlockAsync(valueBuffer, 0, length);
                            await _textReader.ReadLineAsync(ct); // consume \r\n

                            rows.Add(new Dictionary<string, object?>
                            {
                                ["key"] = key,
                                ["value"] = new string(valueBuffer),
                                ["flags"] = flags,
                                ["cas"] = cas
                            });
                            rowsAffected++;
                        }
                    }
                    break;
                }

            case "SET":
            case "ADD":
            case "REPLACE":
            case "APPEND":
            case "PREPEND":
                {
                    if (parts.Length < 3)
                        return new QueryResult { Success = false, ErrorMessage = $"{command} requires key and value" };

                    var key = parts[1];
                    var value = string.Join(" ", parts.Skip(2));
                    var flags = parameters?.GetValueOrDefault("flags") ?? 0;
                    var expiry = parameters?.GetValueOrDefault("expiry") ?? 0;

                    await _textWriter.WriteLineAsync($"{command.ToLowerInvariant()} {key} {flags} {expiry} {value.Length}");
                    await _textWriter.WriteLineAsync(value);

                    var response = await _textReader.ReadLineAsync(ct);
                    var stored = response == "STORED";

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = key,
                        ["stored"] = stored,
                        ["response"] = response
                    });
                    rowsAffected = stored ? 1 : 0;
                    break;
                }

            case "DELETE":
                {
                    if (parts.Length < 2)
                        return new QueryResult { Success = false, ErrorMessage = "DELETE requires key" };

                    await _textWriter.WriteLineAsync(string.Join(" ", parts));
                    var response = await _textReader.ReadLineAsync(ct);

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["key"] = parts[1],
                        ["deleted"] = response == "DELETED",
                        ["response"] = response
                    });
                    rowsAffected = response == "DELETED" ? 1 : 0;
                    break;
                }

            case "INCR":
            case "DECR":
                {
                    if (parts.Length < 2)
                        return new QueryResult { Success = false, ErrorMessage = $"{command} requires key" };

                    var delta = parts.Length > 2 ? parts[2] : "1";
                    await _textWriter.WriteLineAsync($"{command.ToLowerInvariant()} {parts[1]} {delta}");

                    var response = await _textReader.ReadLineAsync(ct);

                    if (ulong.TryParse(response, out var newValue))
                    {
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["key"] = parts[1],
                            ["value"] = newValue
                        });
                        rowsAffected = 1;
                    }
                    else
                    {
                        return new QueryResult
                        {
                            Success = false,
                            ErrorMessage = response ?? "Unknown error"
                        };
                    }
                    break;
                }

            case "STATS":
                {
                    await _textWriter.WriteLineAsync(string.Join(" ", parts));

                    while (true)
                    {
                        var line = await _textReader.ReadLineAsync(ct);
                        if (line == null || line == "END")
                            break;

                        if (line.StartsWith("STAT "))
                        {
                            var statParts = line.Split(' ', 3);
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["stat"] = statParts[1],
                                ["value"] = statParts.Length > 2 ? statParts[2] : ""
                            });
                            rowsAffected++;
                        }
                    }
                    break;
                }

            case "VERSION":
                {
                    await _textWriter.WriteLineAsync("version");
                    var response = await _textReader.ReadLineAsync(ct);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["version"] = response?.StartsWith("VERSION ") == true ? response[8..] : response
                    });
                    rowsAffected = 1;
                    break;
                }

            case "FLUSH_ALL":
                {
                    await _textWriter.WriteLineAsync(string.Join(" ", parts));
                    var response = await _textReader.ReadLineAsync(ct);
                    rows.Add(new Dictionary<string, object?> { ["flushed"] = response == "OK" });
                    rowsAffected = response == "OK" ? 1 : 0;
                    break;
                }

            default:
                return new QueryResult
                {
                    Success = false,
                    ErrorMessage = $"Unknown command: {command}"
                };
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rowsAffected,
            Rows = rows
        };
    }

    private async Task<BinaryResponse> SendBinaryCommandAsync(
        byte opcode,
        string key,
        byte[]? value,
        uint flags,
        uint expiry,
        CancellationToken ct,
        ulong cas = 0)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        byte extraLength = 0;
        byte[]? extras = null;

        // Set extras based on opcode
        if (opcode is OpSet or OpAdd or OpReplace or OpSetQ or OpAddQ or OpReplaceQ)
        {
            extraLength = 8;
            extras = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(extras.AsSpan(0), flags);
            BinaryPrimitives.WriteUInt32BigEndian(extras.AsSpan(4), expiry);
        }
        else if (opcode == OpFlush)
        {
            if (expiry > 0)
            {
                extraLength = 4;
                extras = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(extras.AsSpan(0), expiry);
            }
        }
        else if (opcode is OpTouch or OpGat)
        {
            extraLength = 4;
            extras = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(extras.AsSpan(0), expiry);
        }

        var totalBodyLength = extraLength + keyBytes.Length + (value?.Length ?? 0);
        var header = new byte[24];

        header[0] = RequestMagic;
        header[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)keyBytes.Length);
        header[4] = extraLength;
        header[5] = 0; // Data type
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 0); // Reserved/vbucket
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)totalBodyLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), ++_opaque);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16), cas);

        await ActiveStream!.WriteAsync(header, ct);
        if (extras != null)
            await ActiveStream.WriteAsync(extras, ct);
        if (keyBytes.Length > 0)
            await ActiveStream.WriteAsync(keyBytes, ct);
        if (value != null)
            await ActiveStream.WriteAsync(value, ct);
        await ActiveStream.FlushAsync(ct);

        return await ReadBinaryResponseAsync(ct);
    }

    private async Task SendBinaryCommandNoResponseAsync(
        byte opcode,
        string key,
        byte[]? value,
        uint flags,
        uint expiry,
        CancellationToken ct)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var totalBodyLength = keyBytes.Length + (value?.Length ?? 0);
        var header = new byte[24];

        header[0] = RequestMagic;
        header[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)keyBytes.Length);
        header[4] = 0; // No extras for quiet gets
        header[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)totalBodyLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), ++_opaque);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16), 0);

        await ActiveStream!.WriteAsync(header, ct);
        if (keyBytes.Length > 0)
            await ActiveStream.WriteAsync(keyBytes, ct);
        if (value != null)
            await ActiveStream.WriteAsync(value, ct);
    }

    private async Task<BinaryResponse> SendBinaryIncrDecrAsync(
        byte opcode,
        string key,
        ulong delta,
        ulong initial,
        uint expiry,
        CancellationToken ct)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var extras = new byte[20];
        BinaryPrimitives.WriteUInt64BigEndian(extras.AsSpan(0), delta);
        BinaryPrimitives.WriteUInt64BigEndian(extras.AsSpan(8), initial);
        BinaryPrimitives.WriteUInt32BigEndian(extras.AsSpan(16), expiry);

        var totalBodyLength = 20 + keyBytes.Length;
        var header = new byte[24];

        header[0] = RequestMagic;
        header[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)keyBytes.Length);
        header[4] = 20; // Extra length
        header[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)totalBodyLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), ++_opaque);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16), 0);

        await ActiveStream!.WriteAsync(header, ct);
        await ActiveStream.WriteAsync(extras, ct);
        await ActiveStream.WriteAsync(keyBytes, ct);
        await ActiveStream.FlushAsync(ct);

        return await ReadBinaryResponseAsync(ct);
    }

    private async Task<BinaryResponse> ReadBinaryResponseAsync(CancellationToken ct)
    {
        var header = new byte[24];
        await ActiveStream!.ReadExactlyAsync(header, 0, 24, ct);

        if (header[0] != ResponseMagic)
            throw new InvalidOperationException($"Invalid response magic: 0x{header[0]:X2}");

        var opcode = header[1];
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        var extraLength = header[4];
        var status = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
        var totalBodyLength = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        var opaque = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        var cas = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(16));

        uint flags = 0;
        string? key = null;
        byte[]? value = null;

        if (totalBodyLength > 0)
        {
            var body = new byte[totalBodyLength];
            await ActiveStream.ReadExactlyAsync(body, 0, totalBodyLength, ct);

            var offset = 0;
            if (extraLength >= 4)
            {
                flags = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0));
            }
            offset += extraLength;

            if (keyLength > 0)
            {
                key = Encoding.UTF8.GetString(body, offset, keyLength);
                offset += keyLength;
            }

            var valueLength = totalBodyLength - extraLength - keyLength;
            if (valueLength > 0)
            {
                value = new byte[valueLength];
                Buffer.BlockCopy(body, offset, value, 0, valueLength);
            }
        }

        return new BinaryResponse
        {
            Opcode = opcode,
            Status = status,
            Opaque = opaque,
            Cas = cas,
            Flags = flags,
            Key = key,
            Value = value
        };
    }

    private static string GetStatusMessage(ushort status)
    {
        return status switch
        {
            StatusNoError => "No error",
            StatusKeyNotFound => "Key not found",
            StatusKeyExists => "Key exists",
            StatusValueTooLarge => "Value too large",
            StatusInvalidArguments => "Invalid arguments",
            StatusItemNotStored => "Item not stored",
            StatusAuthError => "Authentication error",
            StatusAuthContinue => "Authentication continue",
            _ => $"Unknown error: 0x{status:X4}"
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
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        throw new NotSupportedException("Memcached does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Memcached does not support transactions");
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        throw new NotSupportedException("Memcached does not support transactions");
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        if (_useBinaryProtocol)
        {
            try
            {
                await SendBinaryCommandAsync(OpQuit, "", null, 0, 0, ct);
            }
            catch
            {

                // Ignore quit errors
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }
        else if (_textWriter != null)
        {
            try
            {
                await _textWriter.WriteLineAsync("quit");
            }
            catch
            {

                // Ignore
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            if (_useBinaryProtocol)
            {
                var response = await SendBinaryCommandAsync(OpNoop, "", null, 0, 0, ct);
                return response.Status == StatusNoError;
            }
            else if (_textWriter != null && _textReader != null)
            {
                await _textWriter.WriteLineAsync("version");
                var response = await _textReader.ReadLineAsync(ct);
                return response?.StartsWith("VERSION") == true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _textReader?.Dispose();
        _textWriter?.Dispose();
        _textReader = null;
        _textWriter = null;
        await base.CleanupConnectionAsync();
    }

    private sealed class BinaryResponse
    {
        public byte Opcode { get; init; }
        public ushort Status { get; init; }
        public uint Opaque { get; init; }
        public ulong Cas { get; init; }
        public uint Flags { get; init; }
        public string? Key { get; init; }
        public byte[]? Value { get; init; }
    }
}
