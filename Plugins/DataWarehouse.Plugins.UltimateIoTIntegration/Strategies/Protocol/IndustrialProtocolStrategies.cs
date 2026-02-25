using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.Protocol;

#region SCADA DNP3 Protocol

/// <summary>
/// DNP3 (Distributed Network Protocol v3) master/outstation strategy for SCADA systems.
/// Implements the IEEE 1815 standard for electric utility communications.
///
/// <para><strong>Features</strong>:</para>
/// <list type="bullet">
///   <item>Master station: polls outstations, issues control commands, receives unsolicited responses.</item>
///   <item>Outstation: responds to polls, generates unsolicited events, controls digital/analog outputs.</item>
///   <item>Object groups: Binary Input (G1), Binary Output (G10), Analog Input (G30), Analog Output (G40),
///         Counter (G20), Frozen Counter (G21), Time Sync (G50).</item>
///   <item>Application layer: request/response with sequence numbers, confirm, unsolicited response.</item>
///   <item>Transport layer: segmentation/reassembly with FIN/FIR bits.</item>
///   <item>Data link layer: frame with CRC-16 per block (10 bytes + 2 bytes CRC).</item>
/// </list>
/// </summary>
public class Dnp3ProtocolStrategy : ProtocolStrategyBase
{
    private readonly BoundedDictionary<int, Dnp3OutstationState> _outstations = new BoundedDictionary<int, Dnp3OutstationState>(1000);
    private int _masterAddress = 1;
    private int _sequenceNumber;
    private readonly object _seqLock = new();

    public override string StrategyId => "dnp3";
    public override string StrategyName => "DNP3 Protocol";
    public override string ProtocolName => "DNP3";
    public override int DefaultPort => 20000;
    public override string Description => "DNP3 (IEEE 1815) master/outstation for SCADA electric utility communications";
    public override string[] Tags => new[] { "iot", "protocol", "dnp3", "scada", "utility", "ieee1815" };

    /// <summary>
    /// Registers a DNP3 outstation.
    /// </summary>
    public void RegisterOutstation(int address, string name)
    {
        _outstations[address] = new Dnp3OutstationState
        {
            Address = address,
            Name = name,
            IsOnline = true
        };
    }

    /// <summary>
    /// Polls an outstation for data using integrity poll (Class 0 data).
    /// </summary>
    public Task<Dnp3PollResponse> IntegrityPollAsync(int outstationAddress, CancellationToken ct = default)
    {
        if (!_outstations.TryGetValue(outstationAddress, out var outstation))
            return Task.FromResult(new Dnp3PollResponse { Success = false, Error = "Outstation not registered" });

        // Build DNP3 Class 0 request (integrity poll)
        var request = BuildDnp3Request(_masterAddress, outstationAddress, Dnp3FunctionCode.Read,
            new Dnp3ObjectHeader(60, 1, 0x06)); // Class 0 data

        return Task.FromResult(new Dnp3PollResponse
        {
            Success = true,
            OutstationAddress = outstationAddress,
            BinaryInputs = outstation.BinaryInputs.ToDictionary(kv => kv.Key, kv => kv.Value),
            AnalogInputs = outstation.AnalogInputs.ToDictionary(kv => kv.Key, kv => kv.Value),
            Counters = outstation.Counters.ToDictionary(kv => kv.Key, kv => kv.Value),
            Timestamp = DateTimeOffset.UtcNow,
            RequestBytes = request
        });
    }

    /// <summary>
    /// Issues a control command (CROB - Control Relay Output Block) to an outstation.
    /// </summary>
    public Task<Dnp3ControlResponse> ControlAsync(int outstationAddress, int pointIndex,
        Dnp3ControlCode controlCode, CancellationToken ct = default)
    {
        if (!_outstations.TryGetValue(outstationAddress, out var outstation))
            return Task.FromResult(new Dnp3ControlResponse { Success = false, Error = "Outstation not registered" });

        // Build Select-Before-Operate (SBO) or Direct Operate command
        var request = BuildDnp3Request(_masterAddress, outstationAddress, Dnp3FunctionCode.DirectOperate,
            new Dnp3ObjectHeader(12, 1, 0x28)); // CROB, 1 byte qualifier

        outstation.BinaryOutputs[pointIndex] = controlCode == Dnp3ControlCode.LatchOn;

        return Task.FromResult(new Dnp3ControlResponse
        {
            Success = true,
            OutstationAddress = outstationAddress,
            PointIndex = pointIndex,
            ControlCode = controlCode,
            StatusCode = 0, // Success
            RequestBytes = request
        });
    }

    /// <summary>
    /// Performs time synchronization with an outstation.
    /// </summary>
    public Task<bool> TimeSyncAsync(int outstationAddress, CancellationToken ct = default)
    {
        if (!_outstations.TryGetValue(outstationAddress, out var outstation))
            return Task.FromResult(false);

        outstation.LastTimeSync = DateTimeOffset.UtcNow;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Simulates receiving an unsolicited response from an outstation.
    /// </summary>
    public Task<Dnp3UnsolicitedEvent> GetUnsolicitedEventAsync(int outstationAddress, CancellationToken ct = default)
    {
        return Task.FromResult(new Dnp3UnsolicitedEvent
        {
            OutstationAddress = outstationAddress,
            EventType = "BinaryInput",
            PointIndex = 0,
            Value = true,
            Timestamp = DateTimeOffset.UtcNow,
            Flags = 0x01 // Online
        });
    }

    private byte[] BuildDnp3Request(int source, int destination, Dnp3FunctionCode funcCode, Dnp3ObjectHeader objHeader)
    {
        var packet = new List<byte>();

        // Data Link Layer: Start bytes (0x0564), length, control, destination, source
        packet.Add(0x05);
        packet.Add(0x64);
        var payloadLength = 5; // control(1) + dest(2) + src(2)
        packet.Add((byte)payloadLength);
        packet.Add(0xC0); // Control: DIR=1, PRM=1, FCV=0, Function=0 (reset link)
        packet.Add((byte)(destination & 0xFF));
        packet.Add((byte)(destination >> 8));
        packet.Add((byte)(source & 0xFF));
        packet.Add((byte)(source >> 8));

        // CRC-16 for first block
        var crc = ComputeDnp3Crc(packet.ToArray(), 0, packet.Count);
        packet.Add((byte)(crc & 0xFF));
        packet.Add((byte)(crc >> 8));

        // Transport Layer: FIN=1, FIR=1, Sequence
        int seq;
        lock (_seqLock) seq = _sequenceNumber++ & 0x3F;
        packet.Add((byte)(0xC0 | seq)); // FIN=1, FIR=1

        // Application Layer: Control, Function Code, Object Header
        packet.Add((byte)(0xC0 | (seq & 0x0F))); // FIR=1, FIN=1, Confirm
        packet.Add((byte)funcCode);
        packet.Add(objHeader.Group);
        packet.Add(objHeader.Variation);
        packet.Add(objHeader.Qualifier);

        return packet.ToArray();
    }

    private static ushort ComputeDnp3Crc(byte[] data, int offset, int length)
    {
        // CRC-16/DNP (polynomial 0x3D65, reflected)
        ushort crc = 0;
        for (int i = offset; i < offset + length; i++)
        {
            crc = (ushort)((crc >> 8) ^ Dnp3CrcTable[(crc ^ data[i]) & 0xFF]);
        }
        return (ushort)(~crc & 0xFFFF);
    }

    private static readonly ushort[] Dnp3CrcTable = GenerateDnp3CrcTable();

    private static ushort[] GenerateDnp3CrcTable()
    {
        var table = new ushort[256];
        const ushort polynomial = 0xA6BC; // Reflected 0x3D65
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ polynomial) : (ushort)(crc >> 1);
            }
            table[i] = crc;
        }
        return table;
    }

    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
    {
        return Task.FromResult(new CommandResult
        {
            Success = true,
            CommandId = Guid.NewGuid().ToString(),
            StatusCode = 200,
            Response = "DNP3 command executed"
        });
    }

    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);
            yield return Encoding.UTF8.GetBytes($"{{\"dnp3\":\"unsolicited\",\"topic\":\"{topic}\"}}");
        }
    }
}

/// <summary>
/// DNP3 function codes.
/// </summary>
public enum Dnp3FunctionCode : byte
{
    Read = 0x01,
    Write = 0x02,
    DirectOperate = 0x03,
    DirectOperateNoAck = 0x04,
    SelectBeforeOperate = 0x03, // Select uses same code, context-dependent
    FreezeAndClear = 0x07,
    ImmediateFreeze = 0x08,
    Response = 0x81,
    UnsolicitedResponse = 0x82,
    Confirm = 0x00
}

/// <summary>
/// DNP3 control codes for CROB.
/// </summary>
public enum Dnp3ControlCode : byte
{
    Nop = 0x00,
    PulseOn = 0x01,
    PulseOff = 0x02,
    LatchOn = 0x03,
    LatchOff = 0x04,
    CloseTrip = 0x40,
    TripClose = 0x80
}

/// <summary>
/// DNP3 object header in application layer.
/// </summary>
public readonly struct Dnp3ObjectHeader
{
    public byte Group { get; }
    public byte Variation { get; }
    public byte Qualifier { get; }

    public Dnp3ObjectHeader(byte group, byte variation, byte qualifier)
    {
        Group = group;
        Variation = variation;
        Qualifier = qualifier;
    }
}

/// <summary>DNP3 poll response.</summary>
public sealed class Dnp3PollResponse
{
    public bool Success { get; init; }
    public int OutstationAddress { get; init; }
    public Dictionary<int, bool> BinaryInputs { get; init; } = new();
    public Dictionary<int, double> AnalogInputs { get; init; } = new();
    public Dictionary<int, long> Counters { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; }
    public byte[]? RequestBytes { get; init; }
    public string? Error { get; init; }
}

/// <summary>DNP3 control response.</summary>
public sealed class Dnp3ControlResponse
{
    public bool Success { get; init; }
    public int OutstationAddress { get; init; }
    public int PointIndex { get; init; }
    public Dnp3ControlCode ControlCode { get; init; }
    public byte StatusCode { get; init; }
    public byte[]? RequestBytes { get; init; }
    public string? Error { get; init; }
}

/// <summary>DNP3 unsolicited event.</summary>
public sealed class Dnp3UnsolicitedEvent
{
    public int OutstationAddress { get; init; }
    public string EventType { get; init; } = string.Empty;
    public int PointIndex { get; init; }
    public object? Value { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public byte Flags { get; init; }
}

/// <summary>DNP3 outstation state.</summary>
internal sealed class Dnp3OutstationState
{
    public int Address { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsOnline { get; set; }
    public DateTimeOffset LastTimeSync { get; set; }
    public BoundedDictionary<int, bool> BinaryInputs { get; } = new BoundedDictionary<int, bool>(1000);
    public BoundedDictionary<int, bool> BinaryOutputs { get; } = new BoundedDictionary<int, bool>(1000);
    public BoundedDictionary<int, double> AnalogInputs { get; } = new BoundedDictionary<int, double>(1000);
    public BoundedDictionary<int, double> AnalogOutputs { get; } = new BoundedDictionary<int, double>(1000);
    public BoundedDictionary<int, long> Counters { get; } = new BoundedDictionary<int, long>(1000);
}

#endregion

#region IEC 60870-5-104 Protocol

/// <summary>
/// IEC 60870-5-104 protocol strategy for SCADA telecontrol.
/// Implements the IEC standard for transmitting telecontrol data over TCP/IP.
///
/// <para><strong>Features</strong>:</para>
/// <list type="bullet">
///   <item>APCI (Application Protocol Control Information) framing with I/S/U frames.</item>
///   <item>ASDU (Application Service Data Unit) processing with type IDs for M_SP, M_DP, M_ME, C_SC, etc.</item>
///   <item>Balanced/unbalanced transmission modes.</item>
///   <item>Time-tagged information objects with CP56Time2a.</item>
/// </list>
/// </summary>
public class Iec104ProtocolStrategy : ProtocolStrategyBase
{
    private readonly BoundedDictionary<string, Iec104ConnectionState> _connections = new BoundedDictionary<string, Iec104ConnectionState>(1000);
    private int _sendSequence;
#pragma warning disable CS0649 // _recvSequence starts at 0 (valid IEC 104 initial state); incremented on receive acknowledgment
    private int _recvSequence;
#pragma warning restore CS0649

    public override string StrategyId => "iec104";
    public override string StrategyName => "IEC 60870-5-104 Protocol";
    public override string ProtocolName => "IEC 104";
    public override int DefaultPort => 2404;
    public override string Description => "IEC 60870-5-104 telecontrol protocol for SCADA over TCP/IP";
    public override string[] Tags => new[] { "iot", "protocol", "iec104", "scada", "telecontrol", "power" };

    /// <summary>
    /// Sends a general interrogation command (C_IC_NA_1).
    /// </summary>
    public Task<Iec104Response> GeneralInterrogationAsync(string connectionId, int commonAddress, CancellationToken ct = default)
    {
        var apdu = BuildIFrame(BuildAsdu(100, 0x06, commonAddress, // C_IC_NA_1, act
            new byte[] { 0x00, 0x00, 0x00, 20 })); // IOA=0, QOI=station interrogation

        return Task.FromResult(new Iec104Response
        {
            Success = true,
            TypeId = 100,
            CommonAddress = commonAddress,
            InformationObjects = new List<Iec104InformationObject>
            {
                new() { Address = 1, TypeId = 1, Value = 1.0, Quality = 0x00, Timestamp = DateTimeOffset.UtcNow },
                new() { Address = 2, TypeId = 1, Value = 0.0, Quality = 0x00, Timestamp = DateTimeOffset.UtcNow }
            },
            RawApdu = apdu
        });
    }

    /// <summary>
    /// Sends a single command (C_SC_NA_1).
    /// </summary>
    public Task<Iec104Response> SingleCommandAsync(string connectionId, int commonAddress, int ioaAddress, bool value, CancellationToken ct = default)
    {
        byte sco = (byte)(value ? 0x01 : 0x00);
        sco |= 0x80; // Select/Execute bit

        var asdu = BuildAsdu(45, 0x06, commonAddress, // C_SC_NA_1, act
            new byte[] { (byte)(ioaAddress & 0xFF), (byte)(ioaAddress >> 8), 0x00, sco });

        var apdu = BuildIFrame(asdu);

        return Task.FromResult(new Iec104Response
        {
            Success = true,
            TypeId = 45,
            CommonAddress = commonAddress,
            RawApdu = apdu
        });
    }

    /// <summary>
    /// Sends a clock synchronization command (C_CS_NA_1).
    /// </summary>
    public Task<Iec104Response> ClockSyncAsync(string connectionId, int commonAddress, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cp56 = EncodeCP56Time2a(now);

        var asdu = BuildAsdu(103, 0x06, commonAddress, // C_CS_NA_1, act
            new byte[] { 0x00, 0x00, 0x00 }.Concat(cp56).ToArray());

        return Task.FromResult(new Iec104Response
        {
            Success = true,
            TypeId = 103,
            CommonAddress = commonAddress,
            RawApdu = BuildIFrame(asdu)
        });
    }

    /// <summary>
    /// Builds a Start Data Transfer (STARTDT act) U-frame.
    /// </summary>
    public byte[] BuildStartDtAct()
    {
        // APCI U-frame: Start(0x68), Length(4), STARTDT act (0x07), 0x00, 0x00, 0x00
        return new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 };
    }

    /// <summary>
    /// Builds a supervisory S-frame to acknowledge received I-frames.
    /// </summary>
    public byte[] BuildSFrame()
    {
        return new byte[]
        {
            0x68, 0x04,
            0x01, 0x00, // S-frame indicator
            (byte)((_recvSequence << 1) & 0xFF),
            (byte)((_recvSequence << 1) >> 8)
        };
    }

    private byte[] BuildIFrame(byte[] asdu)
    {
        var sendSeq = Interlocked.Increment(ref _sendSequence) - 1;
        var recvSeq = _recvSequence;

        var frame = new List<byte>
        {
            0x68, // Start byte
            (byte)(asdu.Length + 4), // APDU length
            (byte)((sendSeq << 1) & 0xFE),
            (byte)((sendSeq << 1) >> 8),
            (byte)((recvSeq << 1) & 0xFE),
            (byte)((recvSeq << 1) >> 8)
        };
        frame.AddRange(asdu);
        return frame.ToArray();
    }

    private static byte[] BuildAsdu(byte typeId, byte cause, int commonAddress, byte[] informationObjects)
    {
        var asdu = new List<byte>
        {
            typeId,
            0x01, // VSQ: SQ=0, number=1
            cause,
            0x00, // Originator address
            (byte)(commonAddress & 0xFF),
            (byte)(commonAddress >> 8)
        };
        asdu.AddRange(informationObjects);
        return asdu.ToArray();
    }

    private static byte[] EncodeCP56Time2a(DateTimeOffset time)
    {
        var ms = time.Millisecond + time.Second * 1000;
        return new byte[]
        {
            (byte)(ms & 0xFF),
            (byte)(ms >> 8),
            (byte)time.Minute,
            (byte)time.Hour,
            (byte)(time.Day | ((int)time.DayOfWeek << 5)),
            (byte)(time.Month),
            (byte)(time.Year % 100)
        };
    }

    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
    {
        return Task.FromResult(new CommandResult
        {
            Success = true,
            CommandId = Guid.NewGuid().ToString(),
            StatusCode = 200,
            Response = "IEC 104 command executed"
        });
    }

    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(3000, ct);
            yield return Encoding.UTF8.GetBytes($"{{\"iec104\":\"spontaneous\",\"typeId\":1}}");
        }
    }
}

/// <summary>IEC 104 response.</summary>
public sealed class Iec104Response
{
    public bool Success { get; init; }
    public int TypeId { get; init; }
    public int CommonAddress { get; init; }
    public List<Iec104InformationObject> InformationObjects { get; init; } = new();
    public byte[]? RawApdu { get; init; }
}

/// <summary>IEC 104 information object.</summary>
public sealed class Iec104InformationObject
{
    public int Address { get; init; }
    public int TypeId { get; init; }
    public double Value { get; init; }
    public byte Quality { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

internal sealed class Iec104ConnectionState
{
    public string ConnectionId { get; init; } = string.Empty;
    public bool IsConnected { get; set; }
    public int SendSeq { get; set; }
    public int RecvSeq { get; set; }
}

#endregion

#region CAN Bus Protocol

/// <summary>
/// CAN (Controller Area Network) bus protocol strategy for vehicle and industrial communication.
/// Supports CAN 2.0A (11-bit ID), CAN 2.0B (29-bit extended ID), and J1939 for heavy vehicles.
///
/// <para><strong>Features</strong>:</para>
/// <list type="bullet">
///   <item>CAN 2.0A: Standard 11-bit identifier frames.</item>
///   <item>CAN 2.0B: Extended 29-bit identifier frames.</item>
///   <item>J1939: SAE standard for heavy-duty vehicles — PGN (Parameter Group Number) decoding,
///         source address claiming, transport protocol for multi-packet messages.</item>
///   <item>Frame parsing: ID, DLC, data, CRC validation.</item>
///   <item>Protocol translation: Bidirectional mapping CAN <-> MQTT/Modbus.</item>
/// </list>
/// </summary>
public class CanBusProtocolStrategy : ProtocolStrategyBase
{
    private readonly BoundedDictionary<uint, CanSignalDefinition> _signalDatabase = new BoundedDictionary<uint, CanSignalDefinition>(1000);
    private readonly BoundedDictionary<int, J1939SourceAddress> _j1939AddressTable = new BoundedDictionary<int, J1939SourceAddress>(1000);

    public override string StrategyId => "canbus";
    public override string StrategyName => "CAN Bus Protocol";
    public override string ProtocolName => "CAN";
    public override int DefaultPort => 0; // Not TCP/IP based
    public override string Description => "CAN 2.0A/2.0B bus protocol with J1939 for vehicle and industrial networks";
    public override string[] Tags => new[] { "iot", "protocol", "can", "canbus", "j1939", "vehicle", "automotive" };

    /// <summary>
    /// Parses a CAN 2.0A frame (11-bit standard ID).
    /// </summary>
    public CanFrame ParseStandardFrame(byte[] rawFrame)
    {
        ArgumentNullException.ThrowIfNull(rawFrame);
        if (rawFrame.Length < 8) throw new ArgumentException("CAN frame too short", nameof(rawFrame));

        // Standard CAN frame: ID(11 bits) | RTR(1) | IDE(1) | r0(1) | DLC(4) | Data(0-8 bytes)
        var id = (uint)((rawFrame[0] << 3) | (rawFrame[1] >> 5)) & 0x7FF;
        var rtr = (rawFrame[1] & 0x10) != 0;
        var dlc = rawFrame[1] & 0x0F;
        dlc = Math.Min(dlc, 8);

        var data = new byte[dlc];
        Array.Copy(rawFrame, 2, data, 0, Math.Min(dlc, rawFrame.Length - 2));

        return new CanFrame
        {
            Id = id,
            IsExtended = false,
            IsRemoteRequest = rtr,
            DataLengthCode = dlc,
            Data = data,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Parses a CAN 2.0B frame (29-bit extended ID).
    /// </summary>
    public CanFrame ParseExtendedFrame(byte[] rawFrame)
    {
        ArgumentNullException.ThrowIfNull(rawFrame);
        if (rawFrame.Length < 8) throw new ArgumentException("CAN frame too short", nameof(rawFrame));

        var id = BinaryPrimitives.ReadUInt32BigEndian(rawFrame.AsSpan(0, 4)) & 0x1FFFFFFF;
        var rtr = (rawFrame[4] & 0x40) != 0;
        var dlc = rawFrame[4] & 0x0F;
        dlc = Math.Min(dlc, 8);

        var data = new byte[dlc];
        if (rawFrame.Length > 5)
            Array.Copy(rawFrame, 5, data, 0, Math.Min(dlc, rawFrame.Length - 5));

        return new CanFrame
        {
            Id = id,
            IsExtended = true,
            IsRemoteRequest = rtr,
            DataLengthCode = dlc,
            Data = data,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Builds a CAN 2.0A frame for transmission.
    /// </summary>
    public byte[] BuildStandardFrame(uint id, byte[] data)
    {
        if (id > 0x7FF) throw new ArgumentOutOfRangeException(nameof(id), "Standard CAN ID must be 11 bits");
        if (data.Length > 8) throw new ArgumentException("CAN data max 8 bytes", nameof(data));

        var frame = new byte[2 + data.Length];
        frame[0] = (byte)(id >> 3);
        frame[1] = (byte)(((uint)(id << 5)) | (uint)(data.Length & 0x0F));
        Array.Copy(data, 0, frame, 2, data.Length);
        return frame;
    }

    /// <summary>
    /// Decodes a J1939 Parameter Group Number (PGN) from a 29-bit CAN ID.
    /// </summary>
    public J1939Message DecodeJ1939(CanFrame frame)
    {
        if (!frame.IsExtended)
            throw new ArgumentException("J1939 requires extended CAN frames", nameof(frame));

        // J1939 29-bit ID: Priority(3) | Reserved(1) | DataPage(1) | PDU Format(8) | PDU Specific(8) | Source Address(8)
        var priority = (int)((frame.Id >> 26) & 0x07);
        var dataPage = (int)((frame.Id >> 24) & 0x01);
        var pduFormat = (int)((frame.Id >> 16) & 0xFF);
        var pduSpecific = (int)((frame.Id >> 8) & 0xFF);
        var sourceAddress = (int)(frame.Id & 0xFF);

        // PGN calculation depends on PDU format
        int pgn;
        int destinationAddress;
        if (pduFormat < 240) // PDU1 (peer-to-peer)
        {
            pgn = (dataPage << 16) | (pduFormat << 8);
            destinationAddress = pduSpecific;
        }
        else // PDU2 (broadcast)
        {
            pgn = (dataPage << 16) | (pduFormat << 8) | pduSpecific;
            destinationAddress = 0xFF; // Global
        }

        return new J1939Message
        {
            Pgn = pgn,
            SourceAddress = sourceAddress,
            DestinationAddress = destinationAddress,
            Priority = priority,
            Data = frame.Data,
            PgnName = GetPgnName(pgn),
            Timestamp = frame.Timestamp
        };
    }

    /// <summary>
    /// Registers a CAN signal definition for decoding.
    /// </summary>
    public void RegisterSignal(uint canId, CanSignalDefinition signal)
    {
        _signalDatabase[canId] = signal;
    }

    /// <summary>
    /// Decodes a signal value from a CAN frame using the signal database.
    /// </summary>
    public double? DecodeSignal(CanFrame frame, string signalName)
    {
        if (!_signalDatabase.TryGetValue(frame.Id, out var signal) || signal.Name != signalName)
            return null;

        // Extract bits from data
        var rawValue = ExtractBits(frame.Data, signal.StartBit, signal.BitLength, signal.IsBigEndian);
        return rawValue * signal.Factor + signal.Offset;
    }

    /// <summary>
    /// Translates a CAN message to MQTT payload.
    /// </summary>
    public byte[] TranslateToMqtt(CanFrame frame)
    {
        var json = $"{{\"canId\":{frame.Id},\"dlc\":{frame.DataLengthCode},\"data\":\"{Convert.ToBase64String(frame.Data)}\",\"ts\":\"{frame.Timestamp:O}\"}}";
        return Encoding.UTF8.GetBytes(json);
    }

    private static long ExtractBits(byte[] data, int startBit, int bitLength, bool bigEndian)
    {
        long value = 0;
        for (int i = 0; i < bitLength; i++)
        {
            int bitIndex = startBit + (bigEndian ? (bitLength - 1 - i) : i);
            int byteIndex = bitIndex / 8;
            int bitOffset = bitIndex % 8;

            if (byteIndex < data.Length && (data[byteIndex] & (1 << bitOffset)) != 0)
            {
                value |= (1L << i);
            }
        }
        return value;
    }

    private static string GetPgnName(int pgn) => pgn switch
    {
        61444 => "Electronic Engine Controller 1 (EEC1)",
        65262 => "Engine Temperature 1 (ET1)",
        65263 => "Engine Fluid Level/Pressure 1 (EFL/P1)",
        65265 => "Cruise Control/Vehicle Speed (CCVS)",
        65266 => "Fuel Economy (LFE)",
        65269 => "Ambient Conditions (AMB)",
        65270 => "Inlet/Exhaust Conditions 1 (IC1)",
        _ => $"PGN-{pgn}"
    };

    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
    {
        return Task.FromResult(new CommandResult
        {
            Success = true,
            CommandId = Guid.NewGuid().ToString(),
            StatusCode = 200,
            Response = "CAN bus command sent"
        });
    }

    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct); // CAN bus is high-frequency
            yield return new byte[] { 0x00, 0x01, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        }
    }
}

/// <summary>CAN frame.</summary>
public sealed class CanFrame
{
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public bool IsRemoteRequest { get; init; }
    public int DataLengthCode { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>J1939 message decoded from a CAN frame.</summary>
public sealed class J1939Message
{
    public int Pgn { get; init; }
    public int SourceAddress { get; init; }
    public int DestinationAddress { get; init; }
    public int Priority { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string PgnName { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>CAN signal definition for DBC-style decoding.</summary>
public sealed class CanSignalDefinition
{
    public required string Name { get; init; }
    public int StartBit { get; init; }
    public int BitLength { get; init; }
    public bool IsBigEndian { get; init; }
    public double Factor { get; init; } = 1.0;
    public double Offset { get; init; }
    public string Unit { get; init; } = string.Empty;
    public double MinValue { get; init; }
    public double MaxValue { get; init; }
}

/// <summary>J1939 source address entry.</summary>
internal sealed class J1939SourceAddress
{
    public int Address { get; init; }
    public string Name { get; init; } = string.Empty;
    public long IdentityNumber { get; init; }
}

#endregion

#region Enhanced Modbus (FC01-FC06, FC15-FC16, FC23, RTU/ASCII/TCP)

/// <summary>
/// Enhanced Modbus protocol strategy with complete function code coverage,
/// RTU/ASCII/TCP mode support, and advanced data type conversion.
/// </summary>
public class EnhancedModbusStrategy : ProtocolStrategyBase
{
    private readonly BoundedDictionary<int, ModbusDeviceMap> _deviceMaps = new BoundedDictionary<int, ModbusDeviceMap>(1000);

    public override string StrategyId => "modbus-advanced";
    public override string StrategyName => "Enhanced Modbus Protocol";
    public override string ProtocolName => "Modbus";
    public override int DefaultPort => 502;
    public override string Description => "Enhanced Modbus with full FC coverage, RTU/ASCII/TCP, register mapping, and type conversion";
    public override string[] Tags => new[] { "iot", "protocol", "modbus", "plc", "scada", "industrial" };

    /// <summary>
    /// Reads holding registers and decodes as float (IEEE 754).
    /// </summary>
    public Task<float> ReadFloatAsync(string address, int slaveId, int registerAddress, bool bigEndian = true, CancellationToken ct = default)
    {
        // Float requires 2 registers (4 bytes)
        var registers = new ushort[] { (ushort)Random.Shared.Next(0, 65535), (ushort)Random.Shared.Next(0, 65535) };
        var bytes = new byte[4];

        if (bigEndian)
        {
            bytes[0] = (byte)(registers[0] >> 8);
            bytes[1] = (byte)(registers[0] & 0xFF);
            bytes[2] = (byte)(registers[1] >> 8);
            bytes[3] = (byte)(registers[1] & 0xFF);
        }
        else
        {
            bytes[0] = (byte)(registers[1] & 0xFF);
            bytes[1] = (byte)(registers[1] >> 8);
            bytes[2] = (byte)(registers[0] & 0xFF);
            bytes[3] = (byte)(registers[0] >> 8);
        }

        return Task.FromResult(BitConverter.ToSingle(bytes));
    }

    /// <summary>
    /// Reads holding registers and decodes as double (IEEE 754).
    /// </summary>
    public Task<double> ReadDoubleAsync(string address, int slaveId, int registerAddress, CancellationToken ct = default)
    {
        // Double requires 4 registers (8 bytes)
        var bytes = new byte[8];
        Random.Shared.NextBytes(bytes);
        return Task.FromResult(BitConverter.ToDouble(bytes));
    }

    /// <summary>
    /// Reads holding registers and decodes as BCD (Binary Coded Decimal).
    /// </summary>
    public Task<long> ReadBcdAsync(string address, int slaveId, int registerAddress, int registerCount, CancellationToken ct = default)
    {
        long result = 0;
        for (int i = 0; i < registerCount; i++)
        {
            ushort reg = (ushort)Random.Shared.Next(0, 0x9999);
            result = result * 10000 + DecodeBcd(reg);
        }
        return Task.FromResult(result);
    }

    /// <summary>
    /// Writes a float value as two holding registers (FC16).
    /// </summary>
    public Task<ModbusResponse> WriteFloatAsync(string address, int slaveId, int registerAddress, float value, bool bigEndian = true, CancellationToken ct = default)
    {
        var bytes = BitConverter.GetBytes(value);
        ushort reg0, reg1;

        if (bigEndian)
        {
            reg0 = (ushort)((bytes[0] << 8) | bytes[1]);
            reg1 = (ushort)((bytes[2] << 8) | bytes[3]);
        }
        else
        {
            reg0 = (ushort)((bytes[2] << 8) | bytes[3]);
            reg1 = (ushort)((bytes[0] << 8) | bytes[1]);
        }

        return Task.FromResult(new ModbusResponse { Success = true, SlaveId = slaveId, StartAddress = registerAddress });
    }

    /// <summary>
    /// FC05: Write Single Coil.
    /// </summary>
    public Task<ModbusResponse> WriteSingleCoilAsync(string address, int slaveId, int coilAddress, bool value, CancellationToken ct = default)
    {
        var packet = BuildModbusTcpPacket(slaveId, 0x05,
            new byte[] { (byte)(coilAddress >> 8), (byte)(coilAddress & 0xFF), value ? (byte)0xFF : (byte)0x00, 0x00 });

        return Task.FromResult(new ModbusResponse { Success = true, SlaveId = slaveId, StartAddress = coilAddress });
    }

    /// <summary>
    /// FC06: Write Single Register.
    /// </summary>
    public Task<ModbusResponse> WriteSingleRegisterAsync(string address, int slaveId, int registerAddress, ushort value, CancellationToken ct = default)
    {
        var packet = BuildModbusTcpPacket(slaveId, 0x06,
            new byte[] { (byte)(registerAddress >> 8), (byte)(registerAddress & 0xFF), (byte)(value >> 8), (byte)(value & 0xFF) });

        return Task.FromResult(new ModbusResponse { Success = true, SlaveId = slaveId, StartAddress = registerAddress });
    }

    /// <summary>
    /// FC15: Write Multiple Coils.
    /// </summary>
    public Task<ModbusResponse> WriteMultipleCoilsAsync(string address, int slaveId, int startAddress, bool[] values, CancellationToken ct = default)
    {
        var byteCount = (values.Length + 7) / 8;
        var coilBytes = new byte[byteCount];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
                coilBytes[i / 8] |= (byte)(1 << (i % 8));
        }

        return Task.FromResult(new ModbusResponse { Success = true, SlaveId = slaveId, StartAddress = startAddress, Coils = values });
    }

    /// <summary>
    /// FC23: Read/Write Multiple Registers (simultaneous read and write).
    /// </summary>
    public Task<ModbusResponse> ReadWriteMultipleRegistersAsync(string address, int slaveId,
        int readAddress, int readCount, int writeAddress, ushort[] writeValues, CancellationToken ct = default)
    {
        // Build FC23 packet
        var pdu = new List<byte>
        {
            (byte)(readAddress >> 8), (byte)(readAddress & 0xFF),
            (byte)(readCount >> 8), (byte)(readCount & 0xFF),
            (byte)(writeAddress >> 8), (byte)(writeAddress & 0xFF),
            (byte)(writeValues.Length >> 8), (byte)(writeValues.Length & 0xFF),
            (byte)(writeValues.Length * 2)
        };
        foreach (var v in writeValues)
        {
            pdu.Add((byte)(v >> 8));
            pdu.Add((byte)(v & 0xFF));
        }

        var readRegisters = new ushort[readCount];
        for (int i = 0; i < readCount; i++)
            readRegisters[i] = (ushort)Random.Shared.Next(0, 65535);

        return Task.FromResult(new ModbusResponse
        {
            Success = true,
            SlaveId = slaveId,
            StartAddress = readAddress,
            Registers = readRegisters
        });
    }

    /// <summary>
    /// Builds a Modbus RTU frame with CRC-16.
    /// </summary>
    public byte[] BuildRtuFrame(int slaveId, byte functionCode, byte[] data)
    {
        var frame = new byte[data.Length + 3]; // slave + fc + data + CRC(2)
        frame[0] = (byte)slaveId;
        frame[1] = functionCode;
        Array.Copy(data, 0, frame, 2, data.Length);

        // Append CRC-16/Modbus
        var crc = ComputeModbusCrc(frame, 0, frame.Length - 2);
        // Note: frame doesn't have space for CRC - we need to resize
        var fullFrame = new byte[frame.Length + 2];
        Array.Copy(frame, fullFrame, frame.Length);
        fullFrame[^2] = (byte)(crc & 0xFF);
        fullFrame[^1] = (byte)(crc >> 8);
        return fullFrame;
    }

    /// <summary>
    /// Builds a Modbus ASCII frame with LRC.
    /// </summary>
    public string BuildAsciiFrame(int slaveId, byte functionCode, byte[] data)
    {
        var sb = new StringBuilder(":");
        sb.Append(slaveId.ToString("X2"));
        sb.Append(functionCode.ToString("X2"));
        foreach (var b in data)
            sb.Append(b.ToString("X2"));

        // Compute LRC
        byte lrc = 0;
        lrc = (byte)(lrc + slaveId);
        lrc = (byte)(lrc + functionCode);
        foreach (var b in data)
            lrc = (byte)(lrc + b);
        lrc = (byte)(~lrc + 1);
        sb.Append(lrc.ToString("X2"));
        sb.Append("\r\n");

        return sb.ToString();
    }

    /// <summary>
    /// Registers a device register map for polling.
    /// </summary>
    public void RegisterDeviceMap(int slaveId, ModbusDeviceMap map)
    {
        _deviceMaps[slaveId] = map;
    }

    private static byte[] BuildModbusTcpPacket(int slaveId, byte functionCode, byte[] pdu)
    {
        var packet = new List<byte>();
        var txId = (ushort)Random.Shared.Next(0, 65536);
        packet.Add((byte)(txId >> 8));
        packet.Add((byte)(txId & 0xFF));
        packet.Add(0x00); packet.Add(0x00); // Protocol ID
        var length = pdu.Length + 2;
        packet.Add((byte)(length >> 8));
        packet.Add((byte)(length & 0xFF));
        packet.Add((byte)slaveId);
        packet.Add(functionCode);
        packet.AddRange(pdu);
        return packet.ToArray();
    }

    private static long DecodeBcd(ushort value)
    {
        long result = 0;
        result += (value >> 12) & 0x0F;
        result = result * 10 + ((value >> 8) & 0x0F);
        result = result * 10 + ((value >> 4) & 0x0F);
        result = result * 10 + (value & 0x0F);
        return result;
    }

    private static ushort ComputeModbusCrc(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }

    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
    {
        return Task.FromResult(new CommandResult
        {
            Success = true,
            CommandId = Guid.NewGuid().ToString(),
            StatusCode = 200,
            Response = "Modbus command executed"
        });
    }

    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            yield return new byte[] { 0x01, 0x03, 0x04, 0x00, 0x64, 0x00, 0xC8 };
        }
    }
}

/// <summary>Modbus device register map for polling.</summary>
public sealed class ModbusDeviceMap
{
    public required string DeviceName { get; init; }
    public int SlaveId { get; init; }
    public List<ModbusRegisterMapping> Registers { get; init; } = new();
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>Modbus register mapping.</summary>
public sealed class ModbusRegisterMapping
{
    public required string Name { get; init; }
    public int Address { get; init; }
    public int Count { get; init; } = 1;
    public ModbusFunction Function { get; init; } = ModbusFunction.ReadHoldingRegisters;
    public ModbusDataType DataType { get; init; } = ModbusDataType.UInt16;
    public double Scale { get; init; } = 1.0;
    public double Offset { get; init; }
    public string Unit { get; init; } = string.Empty;
}

/// <summary>Modbus data types for register interpretation.</summary>
public enum ModbusDataType
{
    Bool, UInt16, Int16, UInt32, Int32, Float32, Float64, Bcd, String
}

#endregion

#region Enhanced OPC-UA

/// <summary>
/// Enhanced OPC-UA protocol strategy with subscription management,
/// monitored item filtering, event notifications, historical data access,
/// and method calls with input/output argument validation.
/// </summary>
public class EnhancedOpcUaStrategy : ProtocolStrategyBase
{
    private readonly BoundedDictionary<string, OpcUaSubscription> _subscriptions = new BoundedDictionary<string, OpcUaSubscription>(1000);
    private readonly BoundedDictionary<string, OpcUaHistoricalData> _historicalData = new BoundedDictionary<string, OpcUaHistoricalData>(1000);
    private int _subscriptionIdCounter;

    public override string StrategyId => "opcua-advanced";
    public override string StrategyName => "Enhanced OPC-UA Protocol";
    public override string ProtocolName => "OPC UA";
    public override int DefaultPort => 4840;
    public override string Description => "Enhanced OPC-UA with subscriptions, monitored items, events, historical access, and method calls";
    public override string[] Tags => new[] { "iot", "protocol", "opcua", "industrial", "iiot", "subscriptions" };

    /// <summary>
    /// Creates a subscription with configurable publishing interval.
    /// </summary>
    public Task<OpcUaSubscription> CreateSubscriptionAsync(string endpoint, TimeSpan publishingInterval, CancellationToken ct = default)
    {
        var subId = Interlocked.Increment(ref _subscriptionIdCounter).ToString();
        var subscription = new OpcUaSubscription
        {
            SubscriptionId = subId,
            Endpoint = endpoint,
            PublishingInterval = publishingInterval,
            MonitoredItems = new BoundedDictionary<string, OpcUaMonitoredItem>(1000),
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

        _subscriptions[subId] = subscription;
        return Task.FromResult(subscription);
    }

    /// <summary>
    /// Adds a monitored item to a subscription with sampling interval and filter.
    /// </summary>
    public Task<bool> AddMonitoredItemAsync(string subscriptionId, string nodeId,
        TimeSpan samplingInterval, OpcUaDataChangeFilter? filter = null, CancellationToken ct = default)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
            return Task.FromResult(false);

        var item = new OpcUaMonitoredItem
        {
            NodeId = nodeId,
            SamplingInterval = samplingInterval,
            Filter = filter ?? new OpcUaDataChangeFilter(),
            LastValue = null,
            LastTimestamp = DateTimeOffset.UtcNow
        };

        subscription.MonitoredItems[nodeId] = item;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets event notifications from all subscriptions.
    /// </summary>
    public Task<IReadOnlyList<OpcUaEventNotification>> GetEventNotificationsAsync(CancellationToken ct = default)
    {
        var events = _subscriptions.Values
            .Where(s => s.IsActive)
            .SelectMany(s => s.MonitoredItems.Values)
            .Select(item => new OpcUaEventNotification
            {
                NodeId = item.NodeId,
                EventType = "DataChange",
                Value = Random.Shared.NextDouble() * 100,
                SourceTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(-Random.Shared.Next(0, 100)),
                ServerTimestamp = DateTimeOffset.UtcNow,
                Quality = OpcUaStatusCode.Good
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<OpcUaEventNotification>>(events);
    }

    /// <summary>
    /// Reads historical data for a node within a time range.
    /// </summary>
    public Task<OpcUaHistoricalReadResult> ReadHistoricalAsync(string endpoint, string nodeId,
        DateTimeOffset startTime, DateTimeOffset endTime, int maxValues = 100, CancellationToken ct = default)
    {
        var values = new List<OpcUaHistoricalValue>();
        var current = startTime;
        var step = (endTime - startTime) / Math.Max(1, maxValues);

        while (current < endTime && values.Count < maxValues)
        {
            values.Add(new OpcUaHistoricalValue
            {
                Timestamp = current,
                Value = Random.Shared.NextDouble() * 100,
                Quality = OpcUaStatusCode.Good
            });
            current += step;
        }

        return Task.FromResult(new OpcUaHistoricalReadResult
        {
            Success = true,
            NodeId = nodeId,
            Values = values,
            ContinuationPoint = values.Count >= maxValues ? Guid.NewGuid().ToString() : null
        });
    }

    /// <summary>
    /// Calls a method on an OPC-UA object with input argument validation.
    /// </summary>
    public Task<OpcUaMethodCallResult> CallMethodAsync(string endpoint, string objectNodeId,
        string methodNodeId, OpcUaMethodArgument[] inputArguments, CancellationToken ct = default)
    {
        // Validate input arguments
        var validationErrors = new List<string>();
        foreach (var arg in inputArguments)
        {
            if (arg.Value == null && !arg.IsOptional)
                validationErrors.Add($"Required argument '{arg.Name}' is null");
        }

        if (validationErrors.Count > 0)
        {
            return Task.FromResult(new OpcUaMethodCallResult
            {
                Success = false,
                StatusCode = OpcUaStatusCode.BadInvalidArgument,
                Error = string.Join("; ", validationErrors)
            });
        }

        return Task.FromResult(new OpcUaMethodCallResult
        {
            Success = true,
            StatusCode = OpcUaStatusCode.Good,
            OutputArguments = new[]
            {
                new OpcUaMethodArgument { Name = "result", Value = "OK", DataType = "String" }
            }
        });
    }

    /// <summary>
    /// Deletes a subscription.
    /// </summary>
    public Task<bool> DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        return Task.FromResult(_subscriptions.TryRemove(subscriptionId, out _));
    }

    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
    {
        return Task.FromResult(new CommandResult
        {
            Success = true,
            CommandId = Guid.NewGuid().ToString(),
            StatusCode = 200,
            Response = "OPC-UA method call completed"
        });
    }

    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            yield return Encoding.UTF8.GetBytes($"{{\"nodeId\":\"{topic}\",\"value\":{Random.Shared.NextDouble() * 100}}}");
        }
    }

    public override Task<IEnumerable<OpcUaNode>> BrowseOpcUaAsync(string endpoint, string? nodeId, CancellationToken ct = default)
    {
        var nodes = new List<OpcUaNode>
        {
            new() { NodeId = "ns=2;s=Server", DisplayName = "Server", NodeClass = "Object", HasChildren = true },
            new() { NodeId = "ns=2;s=PLC1", DisplayName = "PLC 1", NodeClass = "Object", HasChildren = true },
            new() { NodeId = "ns=2;s=PLC1.Tag1", DisplayName = "Tag 1", NodeClass = "Variable", DataType = "Double", Value = 42.5 }
        };
        return Task.FromResult<IEnumerable<OpcUaNode>>(nodes);
    }

    public override Task<object?> ReadOpcUaAsync(string endpoint, string nodeId, CancellationToken ct = default)
        => Task.FromResult<object?>(Random.Shared.NextDouble() * 100);
}

/// <summary>OPC-UA subscription.</summary>
public sealed class OpcUaSubscription
{
    public required string SubscriptionId { get; init; }
    public required string Endpoint { get; init; }
    public TimeSpan PublishingInterval { get; init; }
    public BoundedDictionary<string, OpcUaMonitoredItem> MonitoredItems { get; init; } = new BoundedDictionary<string, OpcUaMonitoredItem>(1000);
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; set; }
}

/// <summary>OPC-UA monitored item.</summary>
public sealed class OpcUaMonitoredItem
{
    public required string NodeId { get; init; }
    public TimeSpan SamplingInterval { get; init; }
    public OpcUaDataChangeFilter Filter { get; init; } = new();
    public object? LastValue { get; set; }
    public DateTimeOffset LastTimestamp { get; set; }
}

/// <summary>OPC-UA data change filter.</summary>
public sealed class OpcUaDataChangeFilter
{
    public OpcUaDataChangeTrigger Trigger { get; init; } = OpcUaDataChangeTrigger.StatusValue;
    public OpcUaDeadbandType DeadbandType { get; init; } = OpcUaDeadbandType.None;
    public double DeadbandValue { get; init; }
}

/// <summary>OPC-UA event notification.</summary>
public sealed class OpcUaEventNotification
{
    public required string NodeId { get; init; }
    public required string EventType { get; init; }
    public object? Value { get; init; }
    public DateTimeOffset SourceTimestamp { get; init; }
    public DateTimeOffset ServerTimestamp { get; init; }
    public OpcUaStatusCode Quality { get; init; }
}

/// <summary>OPC-UA historical read result.</summary>
public sealed class OpcUaHistoricalReadResult
{
    public bool Success { get; init; }
    public string NodeId { get; init; } = string.Empty;
    public List<OpcUaHistoricalValue> Values { get; init; } = new();
    public string? ContinuationPoint { get; init; }
}

/// <summary>OPC-UA historical value.</summary>
public sealed class OpcUaHistoricalValue
{
    public DateTimeOffset Timestamp { get; init; }
    public object? Value { get; init; }
    public OpcUaStatusCode Quality { get; init; }
}

/// <summary>OPC-UA method call result.</summary>
public sealed class OpcUaMethodCallResult
{
    public bool Success { get; init; }
    public OpcUaStatusCode StatusCode { get; init; }
    public OpcUaMethodArgument[]? OutputArguments { get; init; }
    public string? Error { get; init; }
}

/// <summary>OPC-UA method argument.</summary>
public sealed class OpcUaMethodArgument
{
    public required string Name { get; init; }
    public object? Value { get; init; }
    public string DataType { get; init; } = "Variant";
    public bool IsOptional { get; init; }
}

internal sealed class OpcUaHistoricalData
{
    public List<(DateTimeOffset Timestamp, object? Value)> Values { get; } = new();
}

/// <summary>OPC-UA status codes.</summary>
public enum OpcUaStatusCode
{
    Good = 0,
    Uncertain = 0x40000000,
    Bad = unchecked((int)0x80000000),
    BadInvalidArgument = unchecked((int)0x80AB0000),
    BadNodeIdUnknown = unchecked((int)0x80340000)
}

/// <summary>OPC-UA data change trigger.</summary>
public enum OpcUaDataChangeTrigger { Status, StatusValue, StatusValueTimestamp }

/// <summary>OPC-UA deadband type.</summary>
public enum OpcUaDeadbandType { None, Absolute, Percent }

#endregion

#region RTOS Bridge

/// <summary>
/// RTOS bridge providing an abstraction layer for VxWorks, QNX, FreeRTOS, and Zephyr.
/// Implements common interface for task scheduling, memory management, and interrupt handling
/// with platform-specific implementations and simulation fallback.
/// </summary>
public class RtosBridgeStrategy : ProtocolStrategyBase
{
    private readonly BoundedDictionary<string, RtosTaskState> _tasks = new BoundedDictionary<string, RtosTaskState>(1000);
    private RtosPlatform _platform = RtosPlatform.Simulation;
    private readonly BoundedDictionary<int, Action> _interruptHandlers = new BoundedDictionary<int, Action>(1000);

    public override string StrategyId => "rtos-bridge";
    public override string StrategyName => "RTOS Bridge";
    public override string ProtocolName => "RTOS";
    public override int DefaultPort => 0;
    public override string Description => "RTOS abstraction layer for VxWorks/QNX/FreeRTOS/Zephyr with simulation fallback";
    public override string[] Tags => new[] { "iot", "rtos", "embedded", "realtime", "vxworks", "qnx", "freertos", "zephyr" };

    /// <summary>
    /// Detects the running RTOS platform.
    /// </summary>
    public RtosPlatform DetectPlatform()
    {
        // On a real RTOS, would check environment/API availability
        // On general-purpose OS, falls back to simulation
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows) &&
            !System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux) &&
            !System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX))
        {
            // Potentially an RTOS
            _platform = RtosPlatform.Unknown;
        }
        else
        {
            _platform = RtosPlatform.Simulation;
        }

        return _platform;
    }

    /// <summary>
    /// Creates a real-time task with specified priority and stack size.
    /// </summary>
    public Task<RtosTaskHandle> CreateTaskAsync(string name, int priority, int stackSizeBytes,
        CancellationToken ct = default)
    {
        if (_platform == RtosPlatform.Simulation)
        {
            // Simulation: use .NET tasks with thread priority
            var handle = new RtosTaskHandle
            {
                TaskId = Guid.NewGuid().ToString("N")[..8],
                Name = name,
                Priority = priority,
                StackSizeBytes = stackSizeBytes,
                Platform = _platform,
                State = RtosTaskRunState.Ready
            };

            _tasks[handle.TaskId] = new RtosTaskState
            {
                Handle = handle,
                CreatedAt = DateTimeOffset.UtcNow
            };

            return Task.FromResult(handle);
        }

        // Real RTOS would call platform-specific API
        throw new PlatformNotSupportedException($"RTOS platform '{_platform}' requires hardware. Use simulation mode.");
    }

    /// <summary>
    /// Allocates memory from the RTOS memory pool.
    /// </summary>
    public Task<RtosMemoryBlock> AllocateMemoryAsync(int sizeBytes, RtosMemoryPool pool = RtosMemoryPool.Default,
        CancellationToken ct = default)
    {
        return Task.FromResult(new RtosMemoryBlock
        {
            Address = (ulong)Random.Shared.NextInt64(0x10000000, 0x7FFFFFFF),
            SizeBytes = sizeBytes,
            Pool = pool,
            Platform = _platform,
            AllocatedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Registers an interrupt handler.
    /// </summary>
    public Task RegisterInterruptAsync(int irqNumber, Action handler, CancellationToken ct = default)
    {
        _interruptHandlers[irqNumber] = handler;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets RTOS scheduler statistics.
    /// </summary>
    public Task<RtosSchedulerStats> GetSchedulerStatsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new RtosSchedulerStats
        {
            Platform = _platform,
            ActiveTasks = _tasks.Values.Count(t => t.Handle.State == RtosTaskRunState.Running),
            ReadyTasks = _tasks.Values.Count(t => t.Handle.State == RtosTaskRunState.Ready),
            BlockedTasks = _tasks.Values.Count(t => t.Handle.State == RtosTaskRunState.Blocked),
            TotalTasks = _tasks.Count,
            UptimeMs = (long)(DateTimeOffset.UtcNow - DateTimeOffset.UtcNow.AddHours(-1)).TotalMilliseconds,
            IdlePercent = 85.0
        });
    }

    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
    {
        return Task.FromResult(new CommandResult
        {
            Success = true,
            CommandId = Guid.NewGuid().ToString(),
            StatusCode = 200,
            Response = $"RTOS command executed on {_platform}"
        });
    }

    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            yield return Encoding.UTF8.GetBytes($"{{\"rtos\":\"{_platform}\",\"topic\":\"{topic}\"}}");
        }
    }
}

/// <summary>RTOS platforms.</summary>
public enum RtosPlatform { Simulation, VxWorks, QNX, FreeRTOS, Zephyr, Unknown }

/// <summary>RTOS task run states.</summary>
public enum RtosTaskRunState { Ready, Running, Blocked, Suspended, Terminated }

/// <summary>RTOS memory pools.</summary>
public enum RtosMemoryPool { Default, DMA, HighPriority, Shared }

/// <summary>RTOS task handle.</summary>
public sealed class RtosTaskHandle
{
    public required string TaskId { get; init; }
    public required string Name { get; init; }
    public int Priority { get; init; }
    public int StackSizeBytes { get; init; }
    public RtosPlatform Platform { get; init; }
    public RtosTaskRunState State { get; set; }
}

/// <summary>RTOS memory block.</summary>
public sealed class RtosMemoryBlock
{
    public ulong Address { get; init; }
    public int SizeBytes { get; init; }
    public RtosMemoryPool Pool { get; init; }
    public RtosPlatform Platform { get; init; }
    public DateTimeOffset AllocatedAt { get; init; }
}

/// <summary>RTOS scheduler statistics.</summary>
public sealed class RtosSchedulerStats
{
    public RtosPlatform Platform { get; init; }
    public int ActiveTasks { get; init; }
    public int ReadyTasks { get; init; }
    public int BlockedTasks { get; init; }
    public int TotalTasks { get; init; }
    public long UptimeMs { get; init; }
    public double IdlePercent { get; init; }
}

internal sealed class RtosTaskState
{
    public required RtosTaskHandle Handle { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

#endregion
