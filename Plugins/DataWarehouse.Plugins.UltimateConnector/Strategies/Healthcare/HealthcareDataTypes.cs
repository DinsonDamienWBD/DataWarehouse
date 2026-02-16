using System;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Healthcare
{
    // DICOM Types

    /// <summary>
    /// Represents a parsed DICOM study with associated metadata and images.
    /// </summary>
    /// <param name="StudyInstanceUid">Unique identifier for the study (DICOM tag 0020,000D).</param>
    /// <param name="PatientId">Patient identifier (DICOM tag 0010,0020).</param>
    /// <param name="PatientName">Patient name (DICOM tag 0010,0010).</param>
    /// <param name="StudyDate">Date the study was performed (DICOM tag 0008,0020).</param>
    /// <param name="Modality">Imaging modality used (DICOM tag 0008,0060).</param>
    /// <param name="Description">Study description (DICOM tag 0008,1030).</param>
    /// <param name="Images">Collection of images associated with this study.</param>
    public record DicomStudy(
        string StudyInstanceUid,
        string PatientId,
        string PatientName,
        string StudyDate,
        string Modality,
        string Description,
        DicomImage[] Images);

    /// <summary>
    /// Represents a single DICOM image with pixel data and metadata.
    /// </summary>
    /// <param name="SopInstanceUid">Unique identifier for this image instance (DICOM tag 0008,0018).</param>
    /// <param name="Rows">Number of rows in the image (DICOM tag 0028,0010).</param>
    /// <param name="Columns">Number of columns in the image (DICOM tag 0028,0011).</param>
    /// <param name="BitsAllocated">Number of bits allocated per pixel (DICOM tag 0028,0100).</param>
    /// <param name="PhotometricInterpretation">Photometric interpretation (DICOM tag 0028,0004).</param>
    /// <param name="PixelData">Raw pixel data array (DICOM tag 7FE0,0010).</param>
    public record DicomImage(
        string SopInstanceUid,
        int Rows,
        int Columns,
        int BitsAllocated,
        string PhotometricInterpretation,
        byte[] PixelData);

    /// <summary>
    /// Represents a single DICOM tag with its group, element, name, and value.
    /// </summary>
    /// <param name="Group">Tag group number (upper 16 bits of tag).</param>
    /// <param name="Element">Tag element number (lower 16 bits of tag).</param>
    /// <param name="Name">Human-readable name for the tag.</param>
    /// <param name="Value">String representation of the tag value.</param>
    public record DicomTag(
        ushort Group,
        ushort Element,
        string Name,
        string Value);

    // HL7 Types

    /// <summary>
    /// Represents a parsed HL7 v2.x message with extracted segments and metadata.
    /// </summary>
    /// <param name="MessageType">Message type from MSH-9 (e.g., ADT^A01).</param>
    /// <param name="MessageControlId">Message control ID from MSH-10.</param>
    /// <param name="Segments">Collection of parsed segments in the message.</param>
    /// <param name="RawMessage">Original raw HL7 message text.</param>
    public record Hl7ParsedMessage(
        string MessageType,
        string MessageControlId,
        Hl7Segment[] Segments,
        string RawMessage);

    /// <summary>
    /// Represents a single HL7 segment with its identifier and field values.
    /// </summary>
    /// <param name="SegmentId">Segment identifier (e.g., MSH, PID, OBX).</param>
    /// <param name="Fields">Array of field values within the segment.</param>
    public record Hl7Segment(
        string SegmentId,
        string[] Fields);

    // FHIR Types

    /// <summary>
    /// Wraps a FHIR resource with extracted metadata and JSON content.
    /// </summary>
    /// <param name="ResourceType">FHIR resource type (e.g., Patient, Observation).</param>
    /// <param name="Id">Resource identifier.</param>
    /// <param name="Meta">Resource metadata including version and last updated timestamp.</param>
    /// <param name="JsonContent">Full JSON content of the resource as a JsonElement.</param>
    public record FhirResourceWrapper(
        string ResourceType,
        string Id,
        FhirMeta Meta,
        JsonElement JsonContent);

    /// <summary>
    /// FHIR resource metadata.
    /// </summary>
    /// <param name="VersionId">Resource version identifier.</param>
    /// <param name="LastUpdated">Timestamp when the resource was last updated.</param>
    public record FhirMeta(
        string? VersionId,
        DateTimeOffset? LastUpdated);
}
