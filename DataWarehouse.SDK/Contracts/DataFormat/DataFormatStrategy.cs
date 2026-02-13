using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.DataFormat
{
    /// <summary>
    /// Strategy for data format parsing, serialization, and conversion.
    /// Provides bidirectional conversion, streaming support, schema awareness, and compression awareness.
    /// </summary>
    public interface IDataFormatStrategy
    {
        /// <summary>
        /// Unique identifier for this format strategy (e.g., "json", "parquet", "dicom").
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Human-readable display name (e.g., "JSON", "Apache Parquet", "DICOM Medical Imaging").
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Capabilities of this format strategy.
        /// </summary>
        DataFormatCapabilities Capabilities { get; }

        /// <summary>
        /// Format information including extensions, MIME types, and domain family.
        /// </summary>
        FormatInfo FormatInfo { get; }

        /// <summary>
        /// Detects if the given stream is in this format.
        /// </summary>
        /// <param name="stream">Stream to analyze (must support seeking).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the format is detected, false otherwise.</returns>
        Task<bool> DetectFormatAsync(Stream stream, CancellationToken ct = default);

        /// <summary>
        /// Parses data from the format into a structured representation.
        /// </summary>
        /// <param name="input">Input stream in this format.</param>
        /// <param name="context">Parse context with options and metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Parsed data result.</returns>
        Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);

        /// <summary>
        /// Serializes structured data into this format.
        /// </summary>
        /// <param name="data">Data to serialize.</param>
        /// <param name="output">Output stream for serialized data.</param>
        /// <param name="context">Serialization context with options and metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Serialization result.</returns>
        Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);

        /// <summary>
        /// Converts data from this format to another format.
        /// </summary>
        /// <param name="input">Input stream in this format.</param>
        /// <param name="targetStrategy">Target format strategy.</param>
        /// <param name="output">Output stream for converted data.</param>
        /// <param name="context">Conversion context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Conversion result.</returns>
        Task<DataFormatResult> ConvertToAsync(Stream input, IDataFormatStrategy targetStrategy, Stream output, DataFormatContext context, CancellationToken ct = default);

        /// <summary>
        /// Extracts schema information from the data stream.
        /// Only applicable if Capabilities.SchemaAware is true.
        /// </summary>
        /// <param name="stream">Stream to extract schema from.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Schema information, or null if not applicable.</returns>
        Task<FormatSchema?> ExtractSchemaAsync(Stream stream, CancellationToken ct = default);

        /// <summary>
        /// Validates data against the format specification.
        /// </summary>
        /// <param name="stream">Stream to validate.</param>
        /// <param name="schema">Optional schema to validate against.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Validation result.</returns>
        Task<FormatValidationResult> ValidateAsync(Stream stream, FormatSchema? schema = null, CancellationToken ct = default);
    }

    /// <summary>
    /// Capabilities of a data format strategy.
    /// </summary>
    public sealed record DataFormatCapabilities
    {
        /// <summary>
        /// Supports both reading (parsing) and writing (serializing).
        /// </summary>
        public bool Bidirectional { get; init; }

        /// <summary>
        /// Supports streaming/chunked processing for large files.
        /// </summary>
        public bool Streaming { get; init; }

        /// <summary>
        /// Format includes schema/metadata (e.g., Parquet, Avro, DICOM).
        /// </summary>
        public bool SchemaAware { get; init; }

        /// <summary>
        /// Format natively supports compression (e.g., GZIP, Brotli, LZ4).
        /// </summary>
        public bool CompressionAware { get; init; }

        /// <summary>
        /// Format supports random access to data (e.g., Parquet columns, HDF5 datasets).
        /// </summary>
        public bool RandomAccess { get; init; }

        /// <summary>
        /// Format is self-describing (includes format version and metadata).
        /// </summary>
        public bool SelfDescribing { get; init; }

        /// <summary>
        /// Format supports nested/hierarchical data structures.
        /// </summary>
        public bool SupportsHierarchicalData { get; init; }

        /// <summary>
        /// Format supports binary data efficiently.
        /// </summary>
        public bool SupportsBinaryData { get; init; }

        /// <summary>
        /// Creates a capabilities record with all features enabled.
        /// </summary>
        public static DataFormatCapabilities Full => new()
        {
            Bidirectional = true,
            Streaming = true,
            SchemaAware = true,
            CompressionAware = true,
            RandomAccess = true,
            SelfDescribing = true,
            SupportsHierarchicalData = true,
            SupportsBinaryData = true
        };

        /// <summary>
        /// Creates a capabilities record with minimal features.
        /// </summary>
        public static DataFormatCapabilities Basic => new()
        {
            Bidirectional = true,
            Streaming = false,
            SchemaAware = false,
            CompressionAware = false,
            RandomAccess = false,
            SelfDescribing = false,
            SupportsHierarchicalData = false,
            SupportsBinaryData = false
        };
    }

    /// <summary>
    /// Domain family categorization for data formats.
    /// Used to optimize format selection and conversion strategies.
    /// </summary>
    public enum DomainFamily
    {
        /// <summary>
        /// General-purpose formats (JSON, XML, CSV, Parquet, Avro).
        /// </summary>
        General,

        /// <summary>
        /// Analytics and data warehouse formats (Parquet, ORC, Iceberg, Delta Lake).
        /// </summary>
        Analytics,

        /// <summary>
        /// Scientific and research formats (HDF5, NetCDF, FITS).
        /// </summary>
        Scientific,

        /// <summary>
        /// Geospatial formats (GeoJSON, Shapefile, GeoTIFF, KML).
        /// </summary>
        Geospatial,

        /// <summary>
        /// Healthcare and medical formats (DICOM, HL7 FHIR, NIFTI).
        /// </summary>
        Healthcare,

        /// <summary>
        /// Finance and trading formats (FIX, SWIFT, XBRL).
        /// </summary>
        Finance,

        /// <summary>
        /// Media and entertainment formats (OpenEXR, USD, AAF, MXF).
        /// </summary>
        Media,

        /// <summary>
        /// Engineering and CAD formats (STEP, IGES, STL, JT).
        /// </summary>
        Engineering,

        /// <summary>
        /// AI/ML model formats (ONNX, SafeTensors, TensorFlow SavedModel).
        /// </summary>
        MachineLearning,

        /// <summary>
        /// Simulation and CFD formats (VTK, CGNS, OpenFOAM).
        /// </summary>
        Simulation,

        /// <summary>
        /// Weather and climate formats (GRIB, BUFR, NetCDF).
        /// </summary>
        Climate,

        /// <summary>
        /// Electronics and EDA formats (Gerber, GDSII, SPICE).
        /// </summary>
        Electronics,

        /// <summary>
        /// Geophysics and seismology formats (SEG-Y, miniSEED, SAC).
        /// </summary>
        Geophysics,

        /// <summary>
        /// Energy sector formats (LAS, DLIS, WITSML, RESQML).
        /// </summary>
        Energy,

        /// <summary>
        /// Bioinformatics formats (FASTA, BAM, VCF, PDB).
        /// </summary>
        Bioinformatics,

        /// <summary>
        /// Astronomy formats (FITS, ASDF, VOTable).
        /// </summary>
        Astronomy,

        /// <summary>
        /// Physics formats (ENDF, HepMC, ROOT).
        /// </summary>
        Physics,

        /// <summary>
        /// Materials science formats (CIF, POSCAR, DM3).
        /// </summary>
        Materials,

        /// <summary>
        /// Spectroscopy formats (JCAMP-DX, mzML, ANDI-MS).
        /// </summary>
        Spectroscopy,

        /// <summary>
        /// Construction and BIM formats (IFC, CityGML, LandXML).
        /// </summary>
        Construction,

        /// <summary>
        /// Manufacturing and ERP formats (SAP IDoc, EDIFACT, MTConnect).
        /// </summary>
        Manufacturing,

        /// <summary>
        /// Audio production formats (MusicXML, BWF, ADM).
        /// </summary>
        Audio,

        /// <summary>
        /// Animation and motion capture formats (BVH, C3D, Alembic).
        /// </summary>
        Animation,

        /// <summary>
        /// Point cloud and LiDAR formats (LAS, E57, PCD).
        /// </summary>
        PointCloud,

        /// <summary>
        /// Robotics formats (URDF, ROS bag, MCAP).
        /// </summary>
        Robotics,

        /// <summary>
        /// Aerospace and defense formats (ARINC 429, IRIG 106, NITF).
        /// </summary>
        Aerospace,

        /// <summary>
        /// Agriculture formats (ISOXML, AgXML, ADAPT).
        /// </summary>
        Agriculture,

        /// <summary>
        /// Cultural heritage formats (CIDOC-CRM, LIDO, TEI).
        /// </summary>
        Heritage,

        /// <summary>
        /// Linguistics formats (TextGrid, ELAN, CoNLL-U).
        /// </summary>
        Linguistics,

        /// <summary>
        /// Neuroscience formats (NIfTI, BIDS, EDF, NWB).
        /// </summary>
        Neuroscience,

        /// <summary>
        /// Statistics and survey formats (SPSS, SAS, Stata).
        /// </summary>
        Statistics,

        /// <summary>
        /// Digital forensics formats (E01, AFF4, VMDK).
        /// </summary>
        Forensics,

        /// <summary>
        /// Navigation and GNSS formats (RINEX, SP3, NMEA).
        /// </summary>
        Navigation,

        /// <summary>
        /// Quantum computing formats (OpenQASM, Quil, QPY).
        /// </summary>
        Quantum,

        /// <summary>
        /// Non-destructive testing formats (DICONDE, PAUT).
        /// </summary>
        NDT
    }

    /// <summary>
    /// Format information including extensions, MIME types, and domain classification.
    /// </summary>
    public sealed record FormatInfo
    {
        /// <summary>
        /// Unique format identifier (e.g., "json", "parquet", "dicom").
        /// </summary>
        public required string FormatId { get; init; }

        /// <summary>
        /// File extensions associated with this format (e.g., [".json", ".geojson"]).
        /// </summary>
        public required IReadOnlyList<string> Extensions { get; init; }

        /// <summary>
        /// MIME types for this format (e.g., ["application/json"]).
        /// </summary>
        public required IReadOnlyList<string> MimeTypes { get; init; }

        /// <summary>
        /// Domain family this format belongs to.
        /// </summary>
        public required DomainFamily DomainFamily { get; init; }

        /// <summary>
        /// Optional description of the format.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Optional version of the format specification (e.g., "1.0", "2.0").
        /// </summary>
        public string? SpecificationVersion { get; init; }

        /// <summary>
        /// Optional URL to format specification or documentation.
        /// </summary>
        public string? SpecificationUrl { get; init; }
    }

    /// <summary>
    /// Result of a format operation (parse, serialize, convert, validate).
    /// </summary>
    public sealed record DataFormatResult
    {
        /// <summary>
        /// Whether the operation succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Parsed data (for parse operations).
        /// </summary>
        public object? Data { get; init; }

        /// <summary>
        /// Number of bytes processed.
        /// </summary>
        public long BytesProcessed { get; init; }

        /// <summary>
        /// Number of records/rows processed (if applicable).
        /// </summary>
        public long? RecordsProcessed { get; init; }

        /// <summary>
        /// Error message if operation failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Warnings encountered during processing.
        /// </summary>
        public IReadOnlyList<string>? Warnings { get; init; }

        /// <summary>
        /// Metadata extracted during processing.
        /// </summary>
        public IDictionary<string, object>? Metadata { get; init; }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static DataFormatResult Ok(object? data = null, long bytesProcessed = 0, long? recordsProcessed = null) => new()
        {
            Success = true,
            Data = data,
            BytesProcessed = bytesProcessed,
            RecordsProcessed = recordsProcessed
        };

        /// <summary>
        /// Creates a failed result with error message.
        /// </summary>
        public static DataFormatResult Fail(string errorMessage) => new()
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Context for format operations (parse, serialize, convert).
    /// </summary>
    public sealed class DataFormatContext
    {
        /// <summary>
        /// Options specific to the format (e.g., JSON indent, Parquet compression).
        /// </summary>
        public IDictionary<string, object>? Options { get; init; }

        /// <summary>
        /// Schema to use for parsing/serialization (if applicable).
        /// </summary>
        public FormatSchema? Schema { get; init; }

        /// <summary>
        /// Maximum number of records to process (for streaming/sampling).
        /// </summary>
        public long? MaxRecords { get; init; }

        /// <summary>
        /// Whether to validate data during processing.
        /// </summary>
        public bool ValidateData { get; init; }

        /// <summary>
        /// Whether to extract metadata during processing.
        /// </summary>
        public bool ExtractMetadata { get; init; }

        /// <summary>
        /// User-provided metadata to include in output.
        /// </summary>
        public IDictionary<string, object>? UserMetadata { get; init; }
    }

    /// <summary>
    /// Schema information extracted from or applied to data.
    /// </summary>
    public sealed record FormatSchema
    {
        /// <summary>
        /// Schema name or identifier.
        /// </summary>
        public string? Name { get; init; }

        /// <summary>
        /// Schema version.
        /// </summary>
        public string? Version { get; init; }

        /// <summary>
        /// Field/column definitions.
        /// </summary>
        public IReadOnlyList<SchemaField>? Fields { get; init; }

        /// <summary>
        /// Raw schema representation (e.g., JSON schema, XML XSD, Avro schema).
        /// </summary>
        public string? RawSchema { get; init; }

        /// <summary>
        /// Schema format/type (e.g., "json-schema", "avro", "xsd").
        /// </summary>
        public string? SchemaType { get; init; }
    }

    /// <summary>
    /// Schema field definition.
    /// </summary>
    public sealed record SchemaField
    {
        /// <summary>
        /// Field name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Field data type (e.g., "string", "int64", "float", "timestamp").
        /// </summary>
        public required string DataType { get; init; }

        /// <summary>
        /// Whether the field is nullable.
        /// </summary>
        public bool Nullable { get; init; }

        /// <summary>
        /// Optional field description.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Nested fields (for complex/hierarchical types).
        /// </summary>
        public IReadOnlyList<SchemaField>? NestedFields { get; init; }
    }

    /// <summary>
    /// Result of format validation.
    /// </summary>
    public sealed record FormatValidationResult
    {
        /// <summary>
        /// Whether the data is valid.
        /// </summary>
        public required bool IsValid { get; init; }

        /// <summary>
        /// Validation errors.
        /// </summary>
        public IReadOnlyList<ValidationError>? Errors { get; init; }

        /// <summary>
        /// Validation warnings.
        /// </summary>
        public IReadOnlyList<ValidationWarning>? Warnings { get; init; }

        /// <summary>
        /// Creates a valid result.
        /// </summary>
        public static FormatValidationResult Valid => new() { IsValid = true };

        /// <summary>
        /// Creates an invalid result with errors.
        /// </summary>
        public static FormatValidationResult Invalid(params ValidationError[] errors) => new()
        {
            IsValid = false,
            Errors = errors
        };
    }

    /// <summary>
    /// Validation error.
    /// </summary>
    public sealed record ValidationError
    {
        /// <summary>
        /// Error message.
        /// </summary>
        public required string Message { get; init; }

        /// <summary>
        /// Field/path where error occurred.
        /// </summary>
        public string? Path { get; init; }

        /// <summary>
        /// Line number (for text formats).
        /// </summary>
        public long? LineNumber { get; init; }

        /// <summary>
        /// Byte offset where error occurred.
        /// </summary>
        public long? ByteOffset { get; init; }
    }

    /// <summary>
    /// Validation warning.
    /// </summary>
    public sealed record ValidationWarning
    {
        /// <summary>
        /// Warning message.
        /// </summary>
        public required string Message { get; init; }

        /// <summary>
        /// Field/path where warning occurred.
        /// </summary>
        public string? Path { get; init; }
    }

    /// <summary>
    /// Abstract base class for data format strategies.
    /// Provides infrastructure for format detection, conversion, and schema extraction.
    /// </summary>
    public abstract class DataFormatStrategyBase : StrategyBase, IDataFormatStrategy
    {
        /// <inheritdoc/>
        public override abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string DisplayName { get; }

        /// <summary>
        /// Bridges DisplayName to the StrategyBase.Name contract.
        /// </summary>
        public override string Name => DisplayName;

        /// <inheritdoc/>
        public abstract DataFormatCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public abstract FormatInfo FormatInfo { get; }

        /// <inheritdoc/>
        public virtual async Task<bool> DetectFormatAsync(Stream stream, CancellationToken ct = default)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!stream.CanSeek)
                throw new ArgumentException("Stream must support seeking for format detection.", nameof(stream));

            var originalPosition = stream.Position;
            try
            {
                return await DetectFormatCoreAsync(stream, ct).ConfigureAwait(false);
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }

        /// <summary>
        /// Core format detection logic. Override in derived classes.
        /// Stream position will be restored after detection.
        /// </summary>
        protected abstract Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct);

        /// <inheritdoc/>
        public abstract Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default);

        /// <inheritdoc/>
        public virtual async Task<DataFormatResult> ConvertToAsync(
            Stream input,
            IDataFormatStrategy targetStrategy,
            Stream output,
            DataFormatContext context,
            CancellationToken ct = default)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (targetStrategy == null)
                throw new ArgumentNullException(nameof(targetStrategy));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            // Default conversion: parse → serialize
            var parseResult = await ParseAsync(input, context, ct).ConfigureAwait(false);
            if (!parseResult.Success)
                return parseResult;

            return await targetStrategy.SerializeAsync(parseResult.Data!, output, context, ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public virtual Task<FormatSchema?> ExtractSchemaAsync(Stream stream, CancellationToken ct = default)
        {
            if (!Capabilities.SchemaAware)
                return Task.FromResult<FormatSchema?>(null);

            return ExtractSchemaCoreAsync(stream, ct);
        }

        /// <summary>
        /// Core schema extraction logic. Override in derived classes if SchemaAware is true.
        /// </summary>
        protected virtual Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
        {
            return Task.FromResult<FormatSchema?>(null);
        }

        /// <inheritdoc/>
        public virtual async Task<FormatValidationResult> ValidateAsync(Stream stream, FormatSchema? schema = null, CancellationToken ct = default)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            return await ValidateCoreAsync(stream, schema, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Core validation logic. Override in derived classes.
        /// </summary>
        protected abstract Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct);
    }
}
