using DataWarehouse.SDK.Contracts.DataFormat;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Columnar;

/// <summary>
/// Apache Parquet columnar storage format strategy.
/// Parquet stores data in column-oriented fashion with embedded schema and compression.
/// Optimized for analytics workloads with efficient column-level access.
/// </summary>
public sealed class ParquetStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "parquet";

    public override string DisplayName => "Apache Parquet";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = true,
        CompressionAware = true,
        RandomAccess = true,
        SelfDescribing = true,
        SupportsHierarchicalData = false,
        SupportsBinaryData = true
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "parquet",
        Extensions = new[] { ".parquet" },
        MimeTypes = new[] { "application/parquet", "application/x-parquet" },
        DomainFamily = DomainFamily.Analytics,
        Description = "Apache Parquet columnar storage format with compression and schema",
        SpecificationVersion = "2.0",
        SpecificationUrl = "https://parquet.apache.org/docs/"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // Parquet files have "PAR1" magic bytes at the end (footer marker)
        if (stream.Length < 8)
            return false;

        // Check last 4 bytes for "PAR1"
        stream.Position = stream.Length - 4;
        var buffer = new byte[4];
        var bytesRead = await stream.ReadAsync(buffer, 0, 4, ct);

        if (bytesRead != 4)
            return false;

        // Check for PAR1 magic bytes
        return buffer[0] == 'P' && buffer[1] == 'A' && buffer[2] == 'R' && buffer[3] == '1';
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            // Use Parquet.Net to read the file
            using var reader = await ParquetReader.CreateAsync(input, leaveStreamOpen: true, cancellationToken: ct);

            var result = new List<Dictionary<string, object?>>();

            // Read all row groups
            for (int i = 0; i < reader.RowGroupCount; i++)
            {
                using var rowGroupReader = reader.OpenRowGroupReader(i);

                // Read all columns for this row group
                var columnData = new List<DataColumn>();
                foreach (var field in reader.Schema.Fields)
                {
                    var column = await rowGroupReader.ReadColumnAsync(field, ct);
                    columnData.Add(column);
                }

                // Convert to row-oriented format
                if (columnData.Count > 0 && columnData[0].Data.Length > 0)
                {
                    for (int row = 0; row < columnData[0].Data.Length; row++)
                    {
                        var rowDict = new Dictionary<string, object?>();
                        for (int col = 0; col < columnData.Count; col++)
                        {
                            rowDict[reader.Schema.Fields[col].Name] = columnData[col].Data.GetValue(row);
                        }
                        result.Add(rowDict);
                    }
                }
            }

            return DataFormatResult.Success(result);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail(DataFormatErrorCode.ParseError, $"Parquet parsing failed: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            // Convert data to list of dictionaries
            var rows = data as IEnumerable<Dictionary<string, object?>>
                ?? throw new ArgumentException("Data must be IEnumerable<Dictionary<string, object?>>");

            var rowList = rows.ToList();
            if (rowList.Count == 0)
            {
                return DataFormatResult.Fail(DataFormatErrorCode.InvalidData, "No data to serialize");
            }

            // Infer schema from first row
            var firstRow = rowList[0];
            var fields = new List<DataField>();
            foreach (var kvp in firstRow)
            {
                var dataType = InferParquetDataType(kvp.Value);
                fields.Add(new DataField(kvp.Key, dataType));
            }

            var schema = new ParquetSchema(fields);

            // Write using Parquet.Net
            using var writer = await ParquetWriter.CreateAsync(schema, output, cancellationToken: ct);
            writer.CompressionMethod = CompressionMethod.Snappy;

            // Write row group
            using var rowGroup = writer.CreateRowGroup();
            foreach (var field in schema.Fields)
            {
                var columnData = rowList.Select(r => r.GetValueOrDefault(field.Name)).ToArray();
                var column = new DataColumn(field, columnData);
                await rowGroup.WriteColumnAsync(column, ct);
            }

            return DataFormatResult.Success(null);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail(DataFormatErrorCode.SerializationError, $"Parquet serialization failed: {ex.Message}");
        }
    }

    private static DataType InferParquetDataType(object? value)
    {
        return value switch
        {
            null => DataType.String,
            int => DataType.Int32,
            long => DataType.Int64,
            float => DataType.Float,
            double => DataType.Double,
            bool => DataType.Boolean,
            string => DataType.String,
            DateTime => DataType.DateTimeOffset,
            DateTimeOffset => DataType.DateTimeOffset,
            byte[] => DataType.ByteArray,
            _ => DataType.String
        };
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true, cancellationToken: ct);

            var fields = reader.Schema.Fields.Select(f => new SchemaField
            {
                Name = f.Name,
                DataType = f.DataType.ToString(),
                IsNullable = f.IsNullable,
                Description = $"Parquet field: {f.DataType}"
            }).ToArray();

            return new FormatSchema
            {
                Name = "parquet_schema",
                SchemaType = "parquet",
                RawSchema = reader.Schema.ToString() ?? string.Empty,
                Fields = fields
            };
        }
        catch
        {
            return null;
        }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        // Basic validation: check for PAR1 footer
        if (stream.Length < 12) // Minimum: magic(4) + footer_len(4) + magic(4)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "File too small to be valid Parquet file (minimum 12 bytes)"
            });
        }

        // Check PAR1 magic at end
        if (!await DetectFormatCoreAsync(stream, ct))
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "Missing PAR1 magic bytes at end of file"
            });
        }

        // Check PAR1 magic at start
        stream.Position = 0;
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, 0, 4, ct);

        if (header[0] != 'P' || header[1] != 'A' || header[2] != 'R' || header[3] != '1')
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "Missing PAR1 magic bytes at start of file"
            });
        }

        // Full validation would require parsing footer metadata
        return FormatValidationResult.Valid;
    }
}
