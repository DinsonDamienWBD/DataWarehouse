using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    public sealed class DicomConnectionStrategy : HealthcareConnectionStrategyBase
    {
        public override string StrategyId => "dicom";
        public override string DisplayName => "DICOM";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to DICOM medical imaging systems";
        public override string[] Tags => new[] { "dicom", "healthcare", "imaging", "pacs", "radiology" };

        public DicomConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var parts = (config.ConnectionString ?? throw new ArgumentException("Connection string required")).Split(':');
            var client = new TcpClient();
            await client.ConnectAsync(parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p104) ? p104 : 104, ct);
            return new DefaultConnectionHandle(client, new Dictionary<string, object> { ["protocol"] = "DICOM" });
        }

        // Finding 1919: Use live socket probe instead of stale Connected flag.
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<TcpClient>();
            if (!client.Connected) return false;
            try { await client.GetStream().WriteAsync(Array.Empty<byte>(), ct); return true; }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<TcpClient>().Close(); return Task.CompletedTask; }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "DICOM server connected" : "DICOM server disconnected", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<(bool IsValid, string[] Errors)> ValidateHl7Async(IConnectionHandle handle, string hl7Message, CancellationToken ct = default)
        {
            // DICOM uses its own protocol
            return Task.FromResult((false, new[] { "DICOM uses DICOM protocol for medical imaging, not HL7 v2. Use Hl7v2ConnectionStrategy for HL7 validation." }));
        }

        public override Task<string> QueryFhirAsync(IConnectionHandle handle, string resourceType, string? query = null, CancellationToken ct = default)
        {
            // DICOM uses C-FIND, not FHIR
            return Task.FromResult("{\"error\":\"DICOM uses C-FIND/C-MOVE/C-GET for queries. For FHIR ImagingStudy resources, use FhirR4ConnectionStrategy.\"}");
        }

        /// <summary>
        /// Parses a DICOM file from raw binary data using manual parsing.
        /// Reads the DICOM preamble, magic number, and extracts standard tags.
        /// </summary>
        /// <param name="dicomData">Raw DICOM file bytes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Parsed DICOM study with extracted metadata and pixel data.</returns>
        /// <exception cref="ArgumentException">Thrown if the data is not a valid DICOM file.</exception>
        public async Task<DicomStudy> ParseDicomFileAsync(byte[] dicomData, CancellationToken ct = default)
        {
            // Finding 1921: Removed no-op await; synchronous parsing is returned as completed Task via caller awaiting.
            ct.ThrowIfCancellationRequested();

            if (dicomData.Length < 132)
                throw new ArgumentException("Data too short to be a valid DICOM file");

            // Check DICOM magic number "DICM" at offset 128
            if (dicomData[128] != 'D' || dicomData[129] != 'I' || dicomData[130] != 'C' || dicomData[131] != 'M')
                throw new ArgumentException("Invalid DICOM magic number. Expected 'DICM' at offset 128.");

            var tags = new Dictionary<uint, string>();
            byte[]? pixelData = null;
            int offset = 132; // Start after preamble (128 bytes) + magic (4 bytes)

            // Parse DICOM data elements (tag-VR-length-value sequences)
            while (offset < dicomData.Length - 8)
            {
                ct.ThrowIfCancellationRequested();

                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(dicomData.AsSpan(offset, 2));
                ushort element = BinaryPrimitives.ReadUInt16LittleEndian(dicomData.AsSpan(offset + 2, 2));
                uint tag = ((uint)group << 16) | element;
                offset += 4;

                // Read VR (Value Representation) - 2 bytes for explicit VR
                string vr = Encoding.ASCII.GetString(dicomData, offset, 2);
                offset += 2;

                uint length;
                if (IsExplicitVrLong(vr))
                {
                    // VRs like OB, OW, SQ, UN have 2 reserved bytes then 4-byte length
                    offset += 2; // Skip reserved bytes
                    if (offset + 4 > dicomData.Length) break;
                    length = BinaryPrimitives.ReadUInt32LittleEndian(dicomData.AsSpan(offset, 4));
                    offset += 4;
                }
                else
                {
                    // Short VRs have 2-byte length immediately after VR
                    if (offset + 2 > dicomData.Length) break;
                    length = BinaryPrimitives.ReadUInt16LittleEndian(dicomData.AsSpan(offset, 2));
                    offset += 2;
                }

                if (length == 0xFFFFFFFF || offset + length > dicomData.Length)
                {
                    // Undefined length or exceeds bounds - stop parsing
                    break;
                }

                // Extract pixel data tag (7FE0,0010)
                if (tag == 0x7FE00010)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent((int)length);
                    try
                    {
                        Array.Copy(dicomData, offset, buffer, 0, Math.Min((int)length, dicomData.Length - offset));
                        pixelData = new byte[length];
                        Array.Copy(buffer, pixelData, (int)length);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                else if (length > 0 && length < 1024) // Only parse reasonable-length text tags
                {
                    try
                    {
                        string value = Encoding.UTF8.GetString(dicomData, offset, (int)length).Trim('\0', ' ');
                        tags[tag] = value;
                    }
                    catch
                    {

                        // Ignore unparseable values
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                    }
                }

                offset += (int)length;
            }

            // Extract standard tags
            string studyInstanceUid = tags.TryGetValue(0x0020000D, out var uid) ? uid : "UNKNOWN";
            string patientId = tags.TryGetValue(0x00100020, out var pid) ? pid : "UNKNOWN";
            string patientName = tags.TryGetValue(0x00100010, out var pname) ? pname : "UNKNOWN";
            string studyDate = tags.TryGetValue(0x00080020, out var sdate) ? sdate : "UNKNOWN";
            string modality = tags.TryGetValue(0x00080060, out var mod) ? mod : "UNKNOWN";
            string description = tags.TryGetValue(0x00081030, out var desc) ? desc : "";
            string sopInstanceUid = tags.TryGetValue(0x00080018, out var sop) ? sop : "UNKNOWN";
            string rowsStr = tags.TryGetValue(0x00280010, out var rows_str) ? rows_str : "0";
            string columnsStr = tags.TryGetValue(0x00280011, out var cols_str) ? cols_str : "0";
            string bitsAllocatedStr = tags.TryGetValue(0x00280100, out var bits_str) ? bits_str : "0";
            string photometricInterpretation = tags.TryGetValue(0x00280004, out var photo) ? photo : "UNKNOWN";

            int rows = int.TryParse(rowsStr, out int r) ? r : 0;
            int columns = int.TryParse(columnsStr, out int c) ? c : 0;
            int bitsAllocated = int.TryParse(bitsAllocatedStr, out int b) ? b : 0;

            var image = new DicomImage(
                sopInstanceUid,
                rows,
                columns,
                bitsAllocated,
                photometricInterpretation,
                pixelData ?? Array.Empty<byte>());

            return new DicomStudy(
                studyInstanceUid,
                patientId,
                patientName,
                studyDate,
                modality,
                description,
                new[] { image });
        }

        private static bool IsExplicitVrLong(string vr)
        {
            return vr == "OB" || vr == "OW" || vr == "OF" || vr == "SQ" || vr == "UN" || vr == "UC" || vr == "UR" || vr == "UT";
        }
    }
}
