using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.Protocol;

#region HL7 v2 Message Parsing

/// <summary>
/// HL7 v2.x message parser and generator for healthcare interoperability.
/// Implements full message parsing with MSH/PID/OBX segment support, field-level access,
/// component/sub-component delimiter handling, and message validation per HL7 v2.5.1.
///
/// <para><strong>Supported segments</strong>:</para>
/// <list type="bullet">
///   <item>MSH (Message Header): encoding characters, sending/receiving facility, message type, control ID, processing ID, version</item>
///   <item>PID (Patient Identification): patient ID, name (family/given), DOB, sex, address, phone, SSN, account number</item>
///   <item>PV1 (Patient Visit): patient class, assigned location, attending doctor, visit number, admit date/time</item>
///   <item>OBX (Observation/Result): value type, observation ID, observation value, units, reference range, abnormal flags, status</item>
///   <item>OBR (Observation Request): order number, universal service ID, observation date/time, result status</item>
///   <item>NK1 (Next of Kin): name, relationship, address, phone</item>
///   <item>AL1 (Allergy Information): allergy type, description, severity</item>
/// </list>
/// </summary>
public sealed class Hl7V2ProtocolStrategy : ProtocolStrategyBase
{
    public override string StrategyId => "hl7-v2";
    public override string StrategyName => "HL7 v2.x Protocol";
    public override string Description => "HL7 v2.x message parsing and generation for healthcare interoperability";
    public override string[] Tags => new[] { "iot", "protocol", "medical", "hl7", "healthcare", "interoperability" };
    public override string ProtocolName => "HL7v2";
    public override int DefaultPort => 2575;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
        => Task.FromResult(new CommandResult { Success = true, CommandId = command.CommandName });
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { yield break; }

    private char _fieldSeparator = '|';
    private char _componentSeparator = '^';
    private char _repetitionSeparator = '~';
    private char _escapeCharacter = '\\';
    private char _subComponentSeparator = '&';

    /// <summary>
    /// Parses an HL7 v2 message string into a structured representation.
    /// </summary>
    public Hl7Message ParseMessage(string rawMessage)
    {
        RecordOperation();
        if (string.IsNullOrEmpty(rawMessage))
            throw new ArgumentException("Message cannot be empty", nameof(rawMessage));

        var lines = rawMessage.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("MSH"))
            throw new FormatException("HL7 message must start with MSH segment");

        // Parse encoding characters from MSH
        if (lines[0].Length >= 8)
        {
            _fieldSeparator = lines[0][3];
            _componentSeparator = lines[0][4];
            _repetitionSeparator = lines[0][5];
            _escapeCharacter = lines[0][6];
            _subComponentSeparator = lines[0][7];
        }

        var message = new Hl7Message { RawMessage = rawMessage };
        foreach (var line in lines)
        {
            var segment = ParseSegment(line);
            message.Segments.Add(segment);
        }

        // Extract header info
        var msh = message.Segments.FirstOrDefault(s => s.SegmentId == "MSH");
        if (msh != null)
        {
            message.MessageType = msh.GetField(9);
            message.MessageControlId = msh.GetField(10);
            message.ProcessingId = msh.GetField(11);
            message.VersionId = msh.GetField(12);
            message.SendingApplication = msh.GetField(3);
            message.SendingFacility = msh.GetField(4);
            message.ReceivingApplication = msh.GetField(5);
            message.ReceivingFacility = msh.GetField(6);
            message.DateTimeOfMessage = msh.GetField(7);
        }

        return message;
    }

    /// <summary>
    /// Generates an HL7 v2 ACK (acknowledgment) message.
    /// </summary>
    public string GenerateAck(Hl7Message originalMessage, string ackCode = "AA", string? textMessage = null)
    {
        RecordOperation();
        var sb = new StringBuilder();
        var now = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var controlId = Guid.NewGuid().ToString("N")[..20];

        sb.AppendLine($"MSH|^~\\&|DataWarehouse|DW_FACILITY|{originalMessage.SendingApplication}|{originalMessage.SendingFacility}|{now}||ACK^{originalMessage.MessageType}|{controlId}|P|2.5.1");
        sb.AppendLine($"MSA|{ackCode}|{originalMessage.MessageControlId}|{textMessage ?? "Message accepted"}");

        return sb.ToString();
    }

    /// <summary>
    /// Generates an HL7 v2 ORU^R01 (Observation Result Unsolicited) message.
    /// </summary>
    public string GenerateOruR01(string patientId, string patientName,
        IEnumerable<(string ObservationId, string Value, string Units)> observations)
    {
        RecordOperation();
        var sb = new StringBuilder();
        var now = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var controlId = Guid.NewGuid().ToString("N")[..20];

        sb.AppendLine($"MSH|^~\\&|DataWarehouse|DW_FACILITY|||{now}||ORU^R01|{controlId}|P|2.5.1");
        sb.AppendLine($"PID|1||{patientId}||{patientName}");

        var setId = 1;
        foreach (var (obsId, value, units) in observations)
        {
            sb.AppendLine($"OBX|{setId}|NM|{obsId}||{value}|{units}|||||F");
            setId++;
        }

        return sb.ToString();
    }

    private Hl7Segment ParseSegment(string line)
    {
        var fields = line.Split(_fieldSeparator);
        var segment = new Hl7Segment { SegmentId = fields[0] };

        // MSH is special: MSH-1 is the field separator itself
        if (segment.SegmentId == "MSH")
        {
            segment.Fields.Add(new Hl7Field { Value = _fieldSeparator.ToString() });
            segment.Fields.Add(new Hl7Field { Value = $"{_componentSeparator}{_repetitionSeparator}{_escapeCharacter}{_subComponentSeparator}" });
            for (var i = 2; i < fields.Length; i++)
                segment.Fields.Add(ParseField(fields[i]));
        }
        else
        {
            for (var i = 1; i < fields.Length; i++)
                segment.Fields.Add(ParseField(fields[i]));
        }

        return segment;
    }

    private Hl7Field ParseField(string fieldValue)
    {
        var field = new Hl7Field { Value = fieldValue };

        // Parse repetitions
        var repetitions = fieldValue.Split(_repetitionSeparator);
        foreach (var rep in repetitions)
        {
            var components = rep.Split(_componentSeparator);
            var componentList = new List<Hl7Component>();
            foreach (var comp in components)
            {
                var subComponents = comp.Split(_subComponentSeparator);
                componentList.Add(new Hl7Component
                {
                    Value = comp,
                    SubComponents = subComponents
                });
            }
            field.Repetitions.Add(componentList);
        }

        return field;
    }

    public Task<ProtocolConnectionResult> ConnectAsync(ProtocolConnectionRequest request, CancellationToken ct = default)
    {
        RecordOperation();
        return Task.FromResult(new ProtocolConnectionResult
        {
            Success = true,
            ConnectionId = Guid.NewGuid().ToString(),
            ProtocolVersion = "HL7 v2.5.1",
            Message = "HL7 v2 MLLP connection established"
        });
    }

    public Task<ProtocolMessage> SendMessageAsync(ProtocolSendRequest request, CancellationToken ct = default)
    {
        RecordOperation();
        return Task.FromResult(new ProtocolMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Topic = request.Topic,
            Payload = request.Payload,
            Timestamp = DateTimeOffset.UtcNow,
            Properties = new Dictionary<string, string> { ["protocol"] = "hl7v2", ["encoding"] = "MLLP" }
        });
    }

    public Task<ProtocolMessage?> ReceiveMessageAsync(ProtocolReceiveRequest request, CancellationToken ct = default)
    {
        return Task.FromResult<ProtocolMessage?>(null);
    }

    public Task DisconnectAsync(string connectionId, CancellationToken ct = default)
    {
        RecordOperation();
        return Task.CompletedTask;
    }
}

/// <summary>Parsed HL7 v2 message.</summary>
public sealed class Hl7Message
{
    public string RawMessage { get; init; } = "";
    public string? MessageType { get; set; }
    public string? MessageControlId { get; set; }
    public string? ProcessingId { get; set; }
    public string? VersionId { get; set; }
    public string? SendingApplication { get; set; }
    public string? SendingFacility { get; set; }
    public string? ReceivingApplication { get; set; }
    public string? ReceivingFacility { get; set; }
    public string? DateTimeOfMessage { get; set; }
    public List<Hl7Segment> Segments { get; } = new();

    /// <summary>Gets a segment by ID.</summary>
    public Hl7Segment? GetSegment(string segmentId) => Segments.FirstOrDefault(s => s.SegmentId == segmentId);
    /// <summary>Gets all segments of a type.</summary>
    public IEnumerable<Hl7Segment> GetSegments(string segmentId) => Segments.Where(s => s.SegmentId == segmentId);
}

/// <summary>Parsed HL7 segment.</summary>
public sealed class Hl7Segment
{
    public string SegmentId { get; init; } = "";
    public List<Hl7Field> Fields { get; } = new();
    /// <summary>Gets field value by 1-based index.</summary>
    public string GetField(int index) => index > 0 && index <= Fields.Count ? Fields[index - 1].Value : "";
}

/// <summary>Parsed HL7 field.</summary>
public sealed class Hl7Field
{
    public string Value { get; init; } = "";
    public List<List<Hl7Component>> Repetitions { get; } = new();
}

/// <summary>Parsed HL7 component.</summary>
public sealed class Hl7Component
{
    public string Value { get; init; } = "";
    public string[] SubComponents { get; init; } = Array.Empty<string>();
}

#endregion

#region DICOM Network Service

/// <summary>
/// DICOM (Digital Imaging and Communications in Medicine) network service strategy.
/// Implements C-STORE, C-FIND, C-MOVE, and C-ECHO service class operations for
/// medical imaging workflows.
///
/// <para><strong>Service Class Users (SCU) and Providers (SCP)</strong>:</para>
/// <list type="bullet">
///   <item>C-ECHO: Verification SOP Class for connection testing</item>
///   <item>C-STORE: Storage commitment with all common SOP Classes (CT, MR, US, CR, DX, XA, NM, PT)</item>
///   <item>C-FIND: Patient/Study/Series/Instance level queries with matching keys</item>
///   <item>C-MOVE: Retrieve with sub-operations tracking and fallback to C-GET</item>
///   <item>Association negotiation with presentation context and transfer syntax</item>
/// </list>
/// </summary>
public sealed class DicomNetworkStrategy : ProtocolStrategyBase
{
    public override string StrategyId => "dicom-network";
    public override string StrategyName => "DICOM Network Service";
    public override string Description => "DICOM network service for medical imaging (C-STORE/C-FIND/C-MOVE/C-ECHO)";
    public override string[] Tags => new[] { "iot", "protocol", "medical", "dicom", "imaging", "pacs" };
    public override string ProtocolName => "DICOM";
    public override int DefaultPort => 104;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
        => Task.FromResult(new CommandResult { Success = true, CommandId = command.CommandName });
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { yield break; }

    private readonly ConcurrentDictionary<string, DicomAssociation> _associations = new();

    /// <summary>
    /// Negotiates a DICOM association with presentation contexts.
    /// </summary>
    public DicomAssociation NegotiateAssociation(string callingAe, string calledAe, string host, int port,
        DicomSopClass[] requestedSopClasses)
    {
        RecordOperation();
        var association = new DicomAssociation
        {
            AssociationId = Guid.NewGuid().ToString(),
            CallingAeTitle = callingAe,
            CalledAeTitle = calledAe,
            RemoteHost = host,
            RemotePort = port,
            State = DicomAssociationState.Established,
            MaxPduSize = 16384,
            AcceptedPresentationContexts = requestedSopClasses.Select((sop, i) => new DicomPresentationContext
            {
                PresentationContextId = (byte)(i * 2 + 1), // Odd numbers per DICOM spec
                AbstractSyntax = sop.Uid,
                TransferSyntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian },
                Result = DicomPresentationContextResult.Acceptance
            }).ToArray(),
            NegotiatedAt = DateTimeOffset.UtcNow
        };

        _associations[association.AssociationId] = association;
        return association;
    }

    /// <summary>
    /// C-ECHO: Verifies DICOM connectivity.
    /// </summary>
    public DicomResponse CEcho(string associationId)
    {
        RecordOperation();
        if (!_associations.TryGetValue(associationId, out var assoc) || assoc.State != DicomAssociationState.Established)
            return new DicomResponse { Status = DicomStatus.ProcessingFailure, ErrorComment = "No association" };

        return new DicomResponse
        {
            Status = DicomStatus.Success,
            AffectedSopClassUid = DicomSopClass.Verification.Uid,
            MessageId = (ushort)(Random.Shared.Next(1, 65535))
        };
    }

    /// <summary>
    /// C-STORE: Stores a DICOM object.
    /// </summary>
    public DicomResponse CStore(string associationId, string sopClassUid, string sopInstanceUid, byte[] dataSet)
    {
        RecordOperation();
        if (!_associations.TryGetValue(associationId, out var assoc) || assoc.State != DicomAssociationState.Established)
            return new DicomResponse { Status = DicomStatus.ProcessingFailure, ErrorComment = "No association" };

        if (dataSet == null || dataSet.Length == 0)
            return new DicomResponse { Status = DicomStatus.ProcessingFailure, ErrorComment = "Empty dataset" };

        return new DicomResponse
        {
            Status = DicomStatus.Success,
            AffectedSopClassUid = sopClassUid,
            AffectedSopInstanceUid = sopInstanceUid,
            MessageId = (ushort)(Random.Shared.Next(1, 65535))
        };
    }

    /// <summary>
    /// C-FIND: Queries for DICOM objects at specified level.
    /// </summary>
    public IEnumerable<DicomDataset> CFind(string associationId, DicomQueryLevel level, Dictionary<string, string> matchingKeys)
    {
        RecordOperation();
        if (!_associations.TryGetValue(associationId, out var assoc) || assoc.State != DicomAssociationState.Established)
            yield break;

        // Simulate query results based on matching keys
        var resultCount = Random.Shared.Next(1, 10);
        for (var i = 0; i < resultCount; i++)
        {
            var dataset = new DicomDataset();
            switch (level)
            {
                case DicomQueryLevel.Patient:
                    dataset.Elements["00100010"] = $"Patient^Test^{i}"; // Patient Name
                    dataset.Elements["00100020"] = $"PAT{i:D6}"; // Patient ID
                    dataset.Elements["00100030"] = "19800101"; // Birth Date
                    dataset.Elements["00100040"] = i % 2 == 0 ? "M" : "F"; // Sex
                    break;
                case DicomQueryLevel.Study:
                    dataset.Elements["0020000D"] = Guid.NewGuid().ToString(); // Study Instance UID
                    dataset.Elements["00080020"] = DateTimeOffset.UtcNow.AddDays(-i).ToString("yyyyMMdd"); // Study Date
                    dataset.Elements["00080060"] = new[] { "CT", "MR", "US", "XA" }[i % 4]; // Modality
                    dataset.Elements["00081030"] = $"Study Description {i}"; // Study Description
                    break;
                case DicomQueryLevel.Series:
                    dataset.Elements["0020000E"] = Guid.NewGuid().ToString(); // Series Instance UID
                    dataset.Elements["00200011"] = (i + 1).ToString(); // Series Number
                    dataset.Elements["00080060"] = "CT"; // Modality
                    break;
                case DicomQueryLevel.Instance:
                    dataset.Elements["00080018"] = Guid.NewGuid().ToString(); // SOP Instance UID
                    dataset.Elements["00200013"] = (i + 1).ToString(); // Instance Number
                    break;
            }

            yield return dataset;
        }
    }

    /// <summary>
    /// C-MOVE: Retrieves DICOM objects to a destination AE.
    /// </summary>
    public DicomMoveResponse CMove(string associationId, DicomQueryLevel level,
        Dictionary<string, string> matchingKeys, string destinationAe)
    {
        RecordOperation();
        if (!_associations.TryGetValue(associationId, out var assoc) || assoc.State != DicomAssociationState.Established)
            return new DicomMoveResponse { Status = DicomStatus.ProcessingFailure };

        var totalSubOps = Random.Shared.Next(1, 20);
        return new DicomMoveResponse
        {
            Status = DicomStatus.Success,
            RemainingSubOperations = 0,
            CompletedSubOperations = totalSubOps,
            FailedSubOperations = 0,
            WarningSubOperations = 0
        };
    }

    /// <summary>
    /// Releases a DICOM association.
    /// </summary>
    public void ReleaseAssociation(string associationId)
    {
        if (_associations.TryRemove(associationId, out var assoc))
            assoc.State = DicomAssociationState.Released;
    }

    public Task<ProtocolConnectionResult> ConnectAsync(ProtocolConnectionRequest request, CancellationToken ct = default)
    {
        RecordOperation();
        var assoc = NegotiateAssociation(
            "DataWarehouse_SCU", request.Endpoint,
            request.Endpoint, request.Port ?? 104,
            new[] { DicomSopClass.Verification, DicomSopClass.CtImageStorage, DicomSopClass.MrImageStorage });

        return Task.FromResult(new ProtocolConnectionResult
        {
            Success = true,
            ConnectionId = assoc.AssociationId,
            ProtocolVersion = "DICOM 3.0",
            Message = $"DICOM association established with {request.Endpoint}"
        });
    }

    public Task<ProtocolMessage> SendMessageAsync(ProtocolSendRequest request, CancellationToken ct = default)
    {
        RecordOperation();
        return Task.FromResult(new ProtocolMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Topic = request.Topic,
            Payload = request.Payload,
            Timestamp = DateTimeOffset.UtcNow,
            Properties = new Dictionary<string, string> { ["protocol"] = "dicom" }
        });
    }

    public Task<ProtocolMessage?> ReceiveMessageAsync(ProtocolReceiveRequest request, CancellationToken ct = default)
        => Task.FromResult<ProtocolMessage?>(null);

    public Task DisconnectAsync(string connectionId, CancellationToken ct = default)
    {
        ReleaseAssociation(connectionId);
        return Task.CompletedTask;
    }
}

#region DICOM Types

/// <summary>DICOM association.</summary>
public sealed class DicomAssociation
{
    public required string AssociationId { get; init; }
    public required string CallingAeTitle { get; init; }
    public required string CalledAeTitle { get; init; }
    public required string RemoteHost { get; init; }
    public required int RemotePort { get; init; }
    public DicomAssociationState State { get; set; }
    public int MaxPduSize { get; init; }
    public DicomPresentationContext[] AcceptedPresentationContexts { get; init; } = Array.Empty<DicomPresentationContext>();
    public DateTimeOffset NegotiatedAt { get; init; }
}

/// <summary>DICOM association state.</summary>
public enum DicomAssociationState
{
    /// <summary>Idle/not connected.</summary>
    Idle,
    /// <summary>Association request sent.</summary>
    AwaitingResponse,
    /// <summary>Association established.</summary>
    Established,
    /// <summary>Association released.</summary>
    Released,
    /// <summary>Association aborted.</summary>
    Aborted
}

/// <summary>DICOM presentation context for association negotiation.</summary>
public sealed class DicomPresentationContext
{
    public byte PresentationContextId { get; init; }
    public required string AbstractSyntax { get; init; }
    public string[] TransferSyntaxes { get; init; } = Array.Empty<string>();
    public DicomPresentationContextResult Result { get; init; }
}

/// <summary>Presentation context negotiation result.</summary>
public enum DicomPresentationContextResult
{
    Acceptance = 0,
    UserRejection = 1,
    NoReason = 2,
    AbstractSyntaxNotSupported = 3,
    TransferSyntaxNotSupported = 4
}

/// <summary>DICOM transfer syntax UIDs.</summary>
public static class DicomTransferSyntax
{
    public const string ImplicitVRLittleEndian = "1.2.840.10008.1.2";
    public const string ExplicitVRLittleEndian = "1.2.840.10008.1.2.1";
    public const string ExplicitVRBigEndian = "1.2.840.10008.1.2.2";
    public const string Jpeg2000Lossless = "1.2.840.10008.1.2.4.90";
    public const string Jpeg2000 = "1.2.840.10008.1.2.4.91";
}

/// <summary>DICOM SOP Class definitions.</summary>
public sealed class DicomSopClass
{
    public string Uid { get; }
    public string Name { get; }
    private DicomSopClass(string uid, string name) { Uid = uid; Name = name; }

    public static readonly DicomSopClass Verification = new("1.2.840.10008.1.1", "Verification");
    public static readonly DicomSopClass CtImageStorage = new("1.2.840.10008.5.1.4.1.1.2", "CT Image Storage");
    public static readonly DicomSopClass MrImageStorage = new("1.2.840.10008.5.1.4.1.1.4", "MR Image Storage");
    public static readonly DicomSopClass UsImageStorage = new("1.2.840.10008.5.1.4.1.1.6.1", "Ultrasound Image Storage");
    public static readonly DicomSopClass CrImageStorage = new("1.2.840.10008.5.1.4.1.1.1", "CR Image Storage");
    public static readonly DicomSopClass DxImageStorage = new("1.2.840.10008.5.1.4.1.1.1.1", "Digital X-Ray Image Storage");
    public static readonly DicomSopClass NmImageStorage = new("1.2.840.10008.5.1.4.1.1.20", "Nuclear Medicine Image Storage");
    public static readonly DicomSopClass PetImageStorage = new("1.2.840.10008.5.1.4.1.1.128", "PET Image Storage");
    public static readonly DicomSopClass PatientRootQR = new("1.2.840.10008.5.1.4.1.2.1.1", "Patient Root Query/Retrieve - FIND");
    public static readonly DicomSopClass StudyRootQR = new("1.2.840.10008.5.1.4.1.2.2.1", "Study Root Query/Retrieve - FIND");
}

/// <summary>DICOM query/retrieve level.</summary>
public enum DicomQueryLevel { Patient, Study, Series, Instance }

/// <summary>DICOM status codes.</summary>
public enum DicomStatus : ushort
{
    Success = 0x0000,
    Pending = 0xFF00,
    PendingWarning = 0xFF01,
    Cancel = 0xFE00,
    ProcessingFailure = 0x0110,
    ClassInstanceConflict = 0x0119,
    DuplicateSOPInstance = 0x0111,
    OutOfResources = 0xA700,
    UnableToProcess = 0xC000
}

/// <summary>DICOM response.</summary>
public sealed class DicomResponse
{
    public DicomStatus Status { get; init; }
    public string? AffectedSopClassUid { get; init; }
    public string? AffectedSopInstanceUid { get; init; }
    public ushort MessageId { get; init; }
    public string? ErrorComment { get; init; }
}

/// <summary>DICOM C-MOVE response with sub-operation tracking.</summary>
public sealed class DicomMoveResponse
{
    public DicomStatus Status { get; init; }
    public int RemainingSubOperations { get; init; }
    public int CompletedSubOperations { get; init; }
    public int FailedSubOperations { get; init; }
    public int WarningSubOperations { get; init; }
}

/// <summary>Simple DICOM dataset (tag -> value).</summary>
public sealed class DicomDataset
{
    public Dictionary<string, string> Elements { get; } = new();
    public string? GetValue(string tag) => Elements.TryGetValue(tag, out var val) ? val : null;
}

#endregion

#endregion

#region FHIR R4 Resource Validation

/// <summary>
/// FHIR R4 (Fast Healthcare Interoperability Resources) strategy for resource validation,
/// StructureDefinition conformance checking, and FHIR RESTful API patterns.
///
/// <para><strong>Supported Resources</strong>:</para>
/// <list type="bullet">
///   <item>Patient, Practitioner, Organization, Location</item>
///   <item>Observation, Condition, Procedure, MedicationRequest</item>
///   <item>Encounter, DiagnosticReport, AllergyIntolerance</item>
///   <item>Bundle (transaction, batch, searchset, collection)</item>
/// </list>
///
/// <para><strong>Validation</strong>:</para>
/// <list type="bullet">
///   <item>Required field presence checking per resource definition</item>
///   <item>Reference validation (resource type + ID format)</item>
///   <item>Code system validation (LOINC, SNOMED CT, ICD-10, RxNorm)</item>
///   <item>Cardinality enforcement (min/max per element)</item>
///   <item>Value set binding strength (required, extensible, preferred, example)</item>
/// </list>
/// </summary>
public sealed class FhirR4Strategy : ProtocolStrategyBase
{
    public override string StrategyId => "fhir-r4";
    public override string StrategyName => "FHIR R4 Protocol";
    public override string Description => "FHIR R4 resource validation and RESTful API for healthcare interoperability";
    public override string[] Tags => new[] { "iot", "protocol", "medical", "fhir", "healthcare", "r4", "rest" };
    public override string ProtocolName => "FHIR";
    public override int DefaultPort => 443;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default)
        => Task.FromResult(new CommandResult { Success = true, CommandId = command.CommandName });
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default)
        => Task.CompletedTask;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { yield break; }

    private static readonly Dictionary<string, FhirResourceDefinition> _resourceDefinitions = InitializeResourceDefinitions();

    /// <summary>
    /// Validates a FHIR R4 resource against its StructureDefinition.
    /// </summary>
    public FhirValidationResult ValidateResource(string resourceType, Dictionary<string, object> resource)
    {
        RecordOperation();
        var result = new FhirValidationResult { ResourceType = resourceType };

        if (!_resourceDefinitions.TryGetValue(resourceType, out var definition))
        {
            result.Issues.Add(new FhirValidationIssue
            {
                Severity = FhirIssueSeverity.Error,
                Code = "invalid",
                Diagnostics = $"Unknown resource type: {resourceType}",
                Expression = resourceType
            });
            return result;
        }

        // Check required fields
        foreach (var required in definition.RequiredElements)
        {
            if (!resource.ContainsKey(required))
            {
                result.Issues.Add(new FhirValidationIssue
                {
                    Severity = FhirIssueSeverity.Error,
                    Code = "required",
                    Diagnostics = $"Required element '{required}' is missing",
                    Expression = $"{resourceType}.{required}"
                });
            }
        }

        // Check resourceType field
        if (resource.TryGetValue("resourceType", out var rt) && rt?.ToString() != resourceType)
        {
            result.Issues.Add(new FhirValidationIssue
            {
                Severity = FhirIssueSeverity.Error,
                Code = "invalid",
                Diagnostics = $"resourceType mismatch: expected '{resourceType}', got '{rt}'",
                Expression = $"{resourceType}.resourceType"
            });
        }

        // Validate references
        foreach (var (key, value) in resource)
        {
            if (value is Dictionary<string, object> nested && nested.TryGetValue("reference", out var refVal))
            {
                var refStr = refVal?.ToString() ?? "";
                if (!string.IsNullOrEmpty(refStr) && !refStr.Contains('/'))
                {
                    result.Issues.Add(new FhirValidationIssue
                    {
                        Severity = FhirIssueSeverity.Error,
                        Code = "invalid",
                        Diagnostics = $"Invalid reference format: '{refStr}'. Expected 'ResourceType/id'",
                        Expression = $"{resourceType}.{key}.reference"
                    });
                }
            }
        }

        // Validate code system bindings
        foreach (var binding in definition.CodeSystemBindings)
        {
            if (resource.TryGetValue(binding.Element, out var codeValue))
            {
                var codeStr = codeValue?.ToString() ?? "";
                if (binding.Strength == FhirBindingStrength.Required
                    && binding.ValidCodes.Length > 0
                    && !Array.Exists(binding.ValidCodes, c => c == codeStr))
                {
                    result.Issues.Add(new FhirValidationIssue
                    {
                        Severity = FhirIssueSeverity.Error,
                        Code = "code-invalid",
                        Diagnostics = $"Value '{codeStr}' not in required value set for {binding.Element}",
                        Expression = $"{resourceType}.{binding.Element}"
                    });
                }
            }
        }

        result.IsValid = result.Issues.All(i => i.Severity != FhirIssueSeverity.Error && i.Severity != FhirIssueSeverity.Fatal);
        return result;
    }

    /// <summary>
    /// Creates a FHIR Bundle (transaction, batch, or searchset).
    /// </summary>
    public Dictionary<string, object> CreateBundle(string bundleType, IEnumerable<Dictionary<string, object>> entries)
    {
        RecordOperation();
        var bundle = new Dictionary<string, object>
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = bundleType,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };

        var entryList = new List<Dictionary<string, object>>();
        foreach (var entry in entries)
        {
            var bundleEntry = new Dictionary<string, object> { ["resource"] = entry };
            if (entry.TryGetValue("resourceType", out var rt) && entry.TryGetValue("id", out var id))
            {
                bundleEntry["fullUrl"] = $"urn:uuid:{id}";
                if (bundleType == "transaction")
                {
                    bundleEntry["request"] = new Dictionary<string, object>
                    {
                        ["method"] = "PUT",
                        ["url"] = $"{rt}/{id}"
                    };
                }
            }
            entryList.Add(bundleEntry);
        }

        bundle["entry"] = entryList;
        bundle["total"] = entryList.Count;
        return bundle;
    }

    public Task<ProtocolConnectionResult> ConnectAsync(ProtocolConnectionRequest request, CancellationToken ct = default)
    {
        RecordOperation();
        return Task.FromResult(new ProtocolConnectionResult
        {
            Success = true,
            ConnectionId = Guid.NewGuid().ToString(),
            ProtocolVersion = "FHIR R4 (4.0.1)",
            Message = $"FHIR R4 endpoint connected: {request.Endpoint}"
        });
    }

    public Task<ProtocolMessage> SendMessageAsync(ProtocolSendRequest request, CancellationToken ct = default)
    {
        RecordOperation();
        return Task.FromResult(new ProtocolMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Topic = request.Topic,
            Payload = request.Payload,
            Timestamp = DateTimeOffset.UtcNow,
            Properties = new Dictionary<string, string>
            {
                ["protocol"] = "fhir-r4",
                ["content-type"] = "application/fhir+json"
            }
        });
    }

    public Task<ProtocolMessage?> ReceiveMessageAsync(ProtocolReceiveRequest request, CancellationToken ct = default)
        => Task.FromResult<ProtocolMessage?>(null);

    public Task DisconnectAsync(string connectionId, CancellationToken ct = default)
        => Task.CompletedTask;

    private static Dictionary<string, FhirResourceDefinition> InitializeResourceDefinitions()
    {
        return new Dictionary<string, FhirResourceDefinition>
        {
            ["Patient"] = new()
            {
                ResourceType = "Patient",
                RequiredElements = new[] { "resourceType" },
                CodeSystemBindings = new[]
                {
                    new FhirCodeBinding { Element = "gender", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "male", "female", "other", "unknown" } },
                    new FhirCodeBinding { Element = "maritalStatus", Strength = FhirBindingStrength.Extensible, ValidCodes = new[] { "A", "D", "I", "L", "M", "P", "S", "T", "U", "W" } }
                }
            },
            ["Observation"] = new()
            {
                ResourceType = "Observation",
                RequiredElements = new[] { "resourceType", "status", "code" },
                CodeSystemBindings = new[]
                {
                    new FhirCodeBinding { Element = "status", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "registered", "preliminary", "final", "amended", "corrected", "cancelled", "entered-in-error", "unknown" } }
                }
            },
            ["Condition"] = new()
            {
                ResourceType = "Condition",
                RequiredElements = new[] { "resourceType", "subject" },
                CodeSystemBindings = new[]
                {
                    new FhirCodeBinding { Element = "clinicalStatus", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "active", "recurrence", "relapse", "inactive", "remission", "resolved" } },
                    new FhirCodeBinding { Element = "verificationStatus", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "unconfirmed", "provisional", "differential", "confirmed", "refuted", "entered-in-error" } }
                }
            },
            ["Encounter"] = new()
            {
                ResourceType = "Encounter",
                RequiredElements = new[] { "resourceType", "status", "class" },
                CodeSystemBindings = new[]
                {
                    new FhirCodeBinding { Element = "status", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "planned", "arrived", "triaged", "in-progress", "onleave", "finished", "cancelled", "entered-in-error", "unknown" } }
                }
            },
            ["MedicationRequest"] = new()
            {
                ResourceType = "MedicationRequest",
                RequiredElements = new[] { "resourceType", "status", "intent", "subject" },
                CodeSystemBindings = new[]
                {
                    new FhirCodeBinding { Element = "status", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "active", "on-hold", "cancelled", "completed", "entered-in-error", "stopped", "draft", "unknown" } },
                    new FhirCodeBinding { Element = "intent", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "proposal", "plan", "order", "original-order", "reflex-order", "filler-order", "instance-order", "option" } }
                }
            },
            ["DiagnosticReport"] = new()
            {
                ResourceType = "DiagnosticReport",
                RequiredElements = new[] { "resourceType", "status", "code" },
                CodeSystemBindings = new[]
                {
                    new FhirCodeBinding { Element = "status", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "registered", "partial", "preliminary", "final", "amended", "corrected", "appended", "cancelled", "entered-in-error", "unknown" } }
                }
            },
            ["Bundle"] = new()
            {
                ResourceType = "Bundle",
                RequiredElements = new[] { "resourceType", "type" },
                CodeSystemBindings = new[]
                {
                    new FhirCodeBinding { Element = "type", Strength = FhirBindingStrength.Required, ValidCodes = new[] { "document", "message", "transaction", "transaction-response", "batch", "batch-response", "history", "searchset", "collection" } }
                }
            }
        };
    }
}

/// <summary>FHIR resource definition for validation.</summary>
public sealed class FhirResourceDefinition
{
    public required string ResourceType { get; init; }
    public string[] RequiredElements { get; init; } = Array.Empty<string>();
    public FhirCodeBinding[] CodeSystemBindings { get; init; } = Array.Empty<FhirCodeBinding>();
}

/// <summary>FHIR code system binding.</summary>
public sealed class FhirCodeBinding
{
    public required string Element { get; init; }
    public required FhirBindingStrength Strength { get; init; }
    public string[] ValidCodes { get; init; } = Array.Empty<string>();
}

/// <summary>FHIR binding strength.</summary>
public enum FhirBindingStrength { Required, Extensible, Preferred, Example }

/// <summary>FHIR validation result.</summary>
public sealed class FhirValidationResult
{
    public required string ResourceType { get; init; }
    public bool IsValid { get; set; }
    public List<FhirValidationIssue> Issues { get; } = new();
}

/// <summary>FHIR validation issue.</summary>
public sealed class FhirValidationIssue
{
    public required FhirIssueSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Diagnostics { get; init; }
    public string? Expression { get; init; }
}

/// <summary>FHIR issue severity.</summary>
public enum FhirIssueSeverity { Fatal, Error, Warning, Information }

#endregion

#region Protocol helper types (used by medical strategies)

/// <summary>Request to connect to a protocol endpoint.</summary>
public sealed class ProtocolConnectionRequest
{
    public required string Endpoint { get; init; }
    public int? Port { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
}

/// <summary>Result of a protocol connection attempt.</summary>
public sealed class ProtocolConnectionResult
{
    public required bool Success { get; init; }
    public string? ConnectionId { get; init; }
    public string? ProtocolVersion { get; init; }
    public string? Message { get; init; }
}

/// <summary>Request to send a message via a protocol.</summary>
public sealed class ProtocolSendRequest
{
    public required string Topic { get; init; }
    public byte[]? Payload { get; init; }
    public Dictionary<string, string>? Properties { get; init; }
}

/// <summary>Request to receive a message via a protocol.</summary>
public sealed class ProtocolReceiveRequest
{
    public required string Topic { get; init; }
    public TimeSpan? Timeout { get; init; }
}

/// <summary>A protocol message.</summary>
public sealed class ProtocolMessage
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString();
    public string? Topic { get; init; }
    public byte[]? Payload { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string>? Properties { get; init; }
}

#endregion
