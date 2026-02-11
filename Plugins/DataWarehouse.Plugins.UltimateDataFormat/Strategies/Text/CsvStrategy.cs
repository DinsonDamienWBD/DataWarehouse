using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Text;

/// <summary>
/// CSV (Comma-Separated Values) format strategy.
/// Supports parsing and serialization of tabular data with streaming capability.
/// </summary>
public sealed class CsvStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "csv";

    public override string DisplayName => "CSV";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = true,
        SchemaAware = false,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = false,
        SupportsHierarchicalData = false,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "csv",
        Extensions = new[] { ".csv", ".tsv" },
        MimeTypes = new[] { "text/csv", "text/tab-separated-values" },
        DomainFamily = DomainFamily.General,
        Description = "Comma-Separated Values - simple tabular data format",
        SpecificationVersion = "RFC 4180",
        SpecificationUrl = "https://tools.ietf.org/html/rfc4180"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[512];
        var bytesRead = await stream.ReadAsync(buffer, ct);

        if (bytesRead == 0)
            return false;

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
        var lines = text.Split('\n', 2);
        if (lines.Length == 0)
            return false;

        var firstLine = lines[0].Trim();
        // Check for comma or tab delimiters
        return firstLine.Contains(',') || firstLine.Contains('\t');
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        try
        {
            var startPosition = input.Position;
            var delimiter = context.Options?.ContainsKey("delimiter") == true
                ? context.Options["delimiter"]?.ToString() ?? ","
                : ",";

            var rows = new List<string[]>();
            using var reader = new StreamReader(input, leaveOpen: true);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line != null)
                {
                    rows.Add(ParseCsvLine(line, delimiter));
                }
            }

            var bytesProcessed = input.Position - startPosition;
            return DataFormatResult.Ok(rows, bytesProcessed, rows.Count);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"CSV parsing failed: {ex.Message}");
        }
    }

    public override async Task<DataFormatResult> SerializeAsync(object data, Stream output, DataFormatContext context, CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        try
        {
            var startPosition = output.Position;
            var delimiter = context.Options?.ContainsKey("delimiter") == true
                ? context.Options["delimiter"]?.ToString() ?? ","
                : ",";

            using var writer = new StreamWriter(output, leaveOpen: true);

            if (data is List<string[]> rows)
            {
                foreach (var row in rows)
                {
                    var line = string.Join(delimiter, row.Select(EscapeCsvField));
                    await writer.WriteLineAsync(line.AsMemory(), ct);
                }
            }
            else
            {
                return DataFormatResult.Fail("Data must be List<string[]>");
            }

            await writer.FlushAsync(ct);
            var bytesProcessed = output.Position - startPosition;
            return DataFormatResult.Ok(null, bytesProcessed);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"CSV serialization failed: {ex.Message}");
        }
    }

    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        // CSV validation is lenient - any text is valid
        return Task.FromResult(FormatValidationResult.Valid);
    }

    private static string[] ParseCsvLine(string line, string delimiter)
    {
        var fields = new List<string>();
        var currentField = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote
                    currentField.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c.ToString() == delimiter && !inQuotes)
            {
                fields.Add(currentField.ToString());
                currentField.Clear();
            }
            else
            {
                currentField.Append(c);
            }
        }

        fields.Add(currentField.ToString());
        return fields.ToArray();
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}
