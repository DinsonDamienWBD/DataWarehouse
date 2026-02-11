using DataWarehouse.SDK.Contracts.DataFormat;

namespace DataWarehouse.Plugins.UltimateDataFormat.Strategies.Text;

/// <summary>
/// TOML (Tom's Obvious Minimal Language) format strategy.
/// Basic implementation for configuration files with key=value and [section] syntax.
/// </summary>
public sealed class TomlStrategy : DataFormatStrategyBase
{
    public override string StrategyId => "toml";

    public override string DisplayName => "TOML";

    public override DataFormatCapabilities Capabilities => new()
    {
        Bidirectional = true,
        Streaming = false,
        SchemaAware = false,
        CompressionAware = false,
        RandomAccess = false,
        SelfDescribing = true,
        SupportsHierarchicalData = true,
        SupportsBinaryData = false
    };

    public override FormatInfo FormatInfo => new()
    {
        FormatId = "toml",
        Extensions = new[] { ".toml" },
        MimeTypes = new[] { "application/toml" },
        DomainFamily = DomainFamily.General,
        Description = "Tom's Obvious Minimal Language - configuration file format",
        SpecificationVersion = "1.0.0",
        SpecificationUrl = "https://toml.io/en/v1.0.0"
    };

    protected override async Task<bool> DetectFormatCoreAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var bytesRead = await stream.ReadAsync(buffer, ct);

        if (bytesRead == 0)
            return false;

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimStart();

        // Check for TOML markers: [section] or key = value
        return text.Contains('[') ||
               text.Contains('=') ||
               text.StartsWith("#");
    }

    public override async Task<DataFormatResult> ParseAsync(Stream input, DataFormatContext context, CancellationToken ct = default)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        try
        {
            var startPosition = input.Position;
            using var reader = new StreamReader(input, leaveOpen: true);

            var data = new Dictionary<string, object>();
            var currentSection = "";
            var sectionData = new Dictionary<string, string>();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;

                line = line.Trim();

                // Section header
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    if (!string.IsNullOrEmpty(currentSection))
                    {
                        data[currentSection] = new Dictionary<string, string>(sectionData);
                        sectionData.Clear();
                    }

                    currentSection = line.Substring(1, line.Length - 2);
                }
                else if (line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim();
                        var value = parts[1].Trim().Trim('"');

                        if (string.IsNullOrEmpty(currentSection))
                        {
                            data[key] = value;
                        }
                        else
                        {
                            sectionData[key] = value;
                        }
                    }
                }
            }

            // Add last section
            if (!string.IsNullOrEmpty(currentSection) && sectionData.Count > 0)
            {
                data[currentSection] = sectionData;
            }

            var bytesProcessed = input.Position - startPosition;
            return DataFormatResult.Ok(data, bytesProcessed);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"TOML parsing failed: {ex.Message}");
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
            using var writer = new StreamWriter(output, leaveOpen: true);

            if (data is Dictionary<string, object> dict)
            {
                // Write top-level keys
                foreach (var kvp in dict.Where(x => x.Value is string))
                {
                    await writer.WriteLineAsync($"{kvp.Key} = \"{kvp.Value}\"".AsMemory(), ct);
                }

                // Write sections
                foreach (var kvp in dict.Where(x => x.Value is Dictionary<string, string>))
                {
                    await writer.WriteLineAsync($"\n[{kvp.Key}]".AsMemory(), ct);

                    if (kvp.Value is Dictionary<string, string> section)
                    {
                        foreach (var item in section)
                        {
                            await writer.WriteLineAsync($"{item.Key} = \"{item.Value}\"".AsMemory(), ct);
                        }
                    }
                }
            }
            else
            {
                return DataFormatResult.Fail("Data must be Dictionary<string, object>");
            }

            await writer.FlushAsync(ct);
            var bytesProcessed = output.Position - startPosition;
            return DataFormatResult.Ok(null, bytesProcessed);
        }
        catch (Exception ex)
        {
            return DataFormatResult.Fail($"TOML serialization failed: {ex.Message}");
        }
    }

    protected override Task<FormatValidationResult> ValidateCoreAsync(Stream stream, FormatSchema? schema, CancellationToken ct)
    {
        // Basic validation - accept any text
        return Task.FromResult(FormatValidationResult.Valid);
    }
}
