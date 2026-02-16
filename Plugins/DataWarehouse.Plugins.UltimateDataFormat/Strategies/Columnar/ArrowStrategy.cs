using DataWarehouse.SDK.Contracts.DataFormat;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Columnar;

/// <summary>
/// Apache Arrow IPC format strategy.
/// In-memory columnar data format with zero-copy reads and language-agnostic schema.
/// Optimized for inter-process communication and analytics.
/// </summary>
public sealed class ArrowStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "arrow";

    public override string DisplayName => "Apache Arrow";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = true,
        CompressionAware = false, // Arrow IPC itself doesn't compress, but can use compression codec
        RandomAccess = true,
        SelfDescribing = true,
        SupportsHierarchicalData = false,
        SupportsBinaryData = true
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "arrow",
        Extensions = new[] { ".arrow", ".feather" },
        MimeTypes = new[] { "application/vnd.apache.arrow", "application/vnd.apache.arrow.file" },
        DomainFamily = DomainFamily.Analytics,
        Description = "Apache Arrow columnar in-memory format with IPC serialization",
        SpecificationVersion = "1.0",
        SpecificationUrl = "https://arrow.apache.org/docs/format/Columnar.html"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        // Arrow IPC files (Feather v2) start with magic bytes: 0xFF 0xFF 0xFF 0xFF (4 bytes)
        // Followed by flatbuffer-encoded metadata
        if (stream.Length < 8)
            return false;

        stream.Position = 0;
        var buffer = new byte[4];
        var bytesRead = await stream.ReadAsync(buffer, 0, 4, ct);

        if (bytesRead != 4)
            return false;

        // Check for Arrow IPC continuation marker (0xFF 0xFF 0xFF 0xFF)
        // or Feather v1 magic "FEA1"
        bool isArrowIpc = buffer[0] == 0xFF && buffer[1] == 0xFF && buffer[2] == 0xFF && buffer[3] == 0xFF;
        bool isFeatherV1 = buffer[0] == 'F' && buffer[1] == 'E' && buffer[2] == 'A' && buffer[3] == '1';

        return isArrowIpc || isFeatherV1;
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        try
        {
            using var reader = new ArrowFileReader(input, leaveOpen: true);
            var result = new List<Dictionary<string, object?>>();

            // Read all record batches
            RecordBatch? batch;
            while ((batch = await reader.ReadNextRecordBatchAsync(ct)) != null)
            {
                // Convert each batch to row-oriented format
                for (int row = 0; row < batch.Length; row++)
                {
                    var rowDict = new Dictionary<string, object?>();
                    for (int col = 0; col < batch.ColumnCount; col++)
                    {
                        var column = batch.Column(col);
                        var value = GetArrowValue(column, row);
                        rowDict[batch.Schema.GetFieldByIndex(col).Name] = value;
                    }
                    result.Add(rowDict);
                }
            }

            return DataFormatResult.Success(result);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail(DataFormatErrorCode.ParseError, $"Arrow parsing failed: {ex.Message}");
        }
    }

    private static object? GetArrowValue(IArrowArray column, int index)
    {
        if (column.IsNull(index))
            return null;

        return column switch
        {
            Int32Array int32Array => int32Array.GetValue(index),
            Int64Array int64Array => int64Array.GetValue(index),
            FloatArray floatArray => floatArray.GetValue(index),
            DoubleArray doubleArray => doubleArray.GetValue(index),
            BooleanArray boolArray => boolArray.GetValue(index),
            StringArray strArray => strArray.GetString(index),
            BinaryArray binArray => binArray.GetBytes(index)?.ToArray(),
            _ => column.ToString()
        };
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

            // Infer Arrow schema from first row
            var firstRow = rowList[0];
            var fields = new List<Field>();
            foreach (var kvp in firstRow)
            {
                var arrowType = InferArrowType(kvp.Value);
                fields.Add(new Field(kvp.Key, arrowType, nullable: true));
            }

            var schema = new Schema(fields, null);

            // Build arrays for each column
            var arrays = new IArrowArray[fields.Count];
            for (int colIdx = 0; colIdx < fields.Count; colIdx++)
            {
                var field = fields[colIdx];
                var columnValues = rowList.Select(r => r.GetValueOrDefault(field.Name)).ToList();
                arrays[colIdx] = BuildArrowArray(field.DataType, columnValues);
            }

            var recordBatch = new RecordBatch(schema, arrays, rowList.Count);

            // Write using ArrowFileWriter
            using var writer = new ArrowFileWriter(output, schema, leaveOpen: true);
            await writer.WriteRecordBatchAsync(recordBatch, ct);
            await writer.WriteEndAsync(ct);

            return DataFormatResult.Success(null);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail(DataFormatErrorCode.SerializationError, $"Arrow serialization failed: {ex.Message}");
        }
    }

    private static IArrowType InferArrowType(object? value)
    {
        return value switch
        {
            int => Int32Type.Default,
            long => Int64Type.Default,
            float => FloatType.Default,
            double => DoubleType.Default,
            bool => BooleanType.Default,
            string => StringType.Default,
            byte[] => BinaryType.Default,
            _ => StringType.Default
        };
    }

    private static IArrowArray BuildArrowArray(IArrowType type, List<object?> values)
    {
        return type switch
        {
            Int32Type => new Int32Array.Builder().AppendRange(values.Cast<int?>()).Build(),
            Int64Type => new Int64Array.Builder().AppendRange(values.Cast<long?>()).Build(),
            FloatType => new FloatArray.Builder().AppendRange(values.Cast<float?>()).Build(),
            DoubleType => new DoubleArray.Builder().AppendRange(values.Cast<double?>()).Build(),
            BooleanType => new BooleanArray.Builder().AppendRange(values.Cast<bool?>()).Build(),
            StringType => new StringArray.Builder().AppendRange(values.Select(v => v?.ToString())).Build(),
            _ => new StringArray.Builder().AppendRange(values.Select(v => v?.ToString())).Build()
        };
    }

    protected override async Task<FormatSchema?> ExtractSchemaCoreAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var reader = new ArrowFileReader(stream, leaveOpen: true);
            var schema = reader.Schema;

            var fields = schema.FieldsList.Select(f => new SchemaField
            {
                Name = f.Name,
                DataType = f.DataType.Name,
                IsNullable = f.IsNullable,
                Description = $"Arrow field: {f.DataType.Name}"
            }).ToArray();

            return new FormatSchema
            {
                Name = "arrow_schema",
                SchemaType = "arrow",
                RawSchema = schema.ToString() ?? string.Empty,
                Fields = fields
            };
        }
        catch
        {
            return null;
        }
    }
    }

    protected override async Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        // Basic validation: check for Arrow IPC magic bytes
        if (stream.Length < 8)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "File too small to be valid Arrow IPC file (minimum 8 bytes)"
            });
        }

        // Check magic bytes at start
        if (!await DetectFormatCoreAsync(stream, ct))
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "Missing Arrow IPC magic bytes (0xFF 0xFF 0xFF 0xFF) or Feather magic"
            });
        }

        // Full validation would require parsing flatbuffer schema and RecordBatches
        // Check for minimum viable structure: magic + metadata length + schema
        if (stream.Length < 16)
        {
            return FormatValidationResult.Invalid(new ValidationError
            {
                Message = "Arrow IPC file too small to contain valid schema"
            });
        }

        return FormatValidationResult.Valid;
    }
}
