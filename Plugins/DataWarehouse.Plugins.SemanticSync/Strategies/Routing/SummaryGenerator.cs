using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.Routing;

/// <summary>
/// Generates AI-powered or extraction-based summaries of data items at varying fidelity levels.
/// Used internally by <see cref="BandwidthAwareSummaryRouter"/> to produce reduced-fidelity
/// representations when bandwidth constraints prevent full data sync.
/// </summary>
/// <remarks>
/// <para>
/// When an <see cref="IAIProvider"/> is available and the data is text-based (UTF-8 decodable),
/// AI-driven summarization produces semantically meaningful summaries. Without AI or for binary
/// data, an extraction fallback takes leading bytes proportional to the target fidelity level.
/// </para>
/// <para>
/// Reconstruction from summaries is inherently lossy. If an embedding is available and AI is
/// configured, expansion is attempted; otherwise the summary text is returned as raw UTF-8 bytes.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Summary routing")]
internal sealed class SummaryGenerator
{
    private static readonly SearchValues<char> KeyValueSeparators = SearchValues.Create([':', '=']);

    private readonly IAIProvider? _aiProvider;

    /// <summary>
    /// Initializes a new instance of <see cref="SummaryGenerator"/>.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for intelligent summarization. Pass null to use extraction fallback exclusively.</param>
    public SummaryGenerator(IAIProvider? aiProvider = null)
    {
        _aiProvider = aiProvider;
    }

    /// <summary>
    /// Generates a summary of the given raw data at the specified target fidelity level.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item being summarized.</param>
    /// <param name="rawData">The full raw data payload.</param>
    /// <param name="targetFidelity">The target fidelity level controlling summary detail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DataSummary"/> containing the summary text, embedding, and compression metadata.</returns>
    public async Task<DataSummary> GenerateAsync(
        string dataId,
        ReadOnlyMemory<byte> rawData,
        SyncFidelity targetFidelity,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);

        if (rawData.IsEmpty)
        {
            return new DataSummary(
                DataId: dataId,
                SummaryText: string.Empty,
                SummaryEmbedding: Array.Empty<byte>(),
                SourceFidelity: targetFidelity,
                CompressionRatio: 0.0,
                GeneratedAt: DateTimeOffset.UtcNow);
        }

        bool isTextBased = TryDecodeUtf8(rawData.Span, out string? textContent);

        string summaryText;
        byte[] embedding = Array.Empty<byte>();

        if (_aiProvider is { IsAvailable: true } && isTextBased && textContent is not null)
        {
            summaryText = await GenerateAiSummaryAsync(textContent, targetFidelity, ct).ConfigureAwait(false);
            embedding = await GenerateEmbeddingBytesAsync(summaryText, ct).ConfigureAwait(false);
        }
        else
        {
            summaryText = GenerateExtractionFallback(rawData, targetFidelity, isTextBased, textContent);

            if (_aiProvider is { IsAvailable: true } && summaryText.Length > 0)
            {
                embedding = await GenerateEmbeddingBytesAsync(summaryText, ct).ConfigureAwait(false);
            }
        }

        int summarySize = Encoding.UTF8.GetByteCount(summaryText);
        double compressionRatio = rawData.Length > 0
            ? (double)summarySize / rawData.Length
            : 0.0;

        return new DataSummary(
            DataId: dataId,
            SummaryText: summaryText,
            SummaryEmbedding: embedding,
            SourceFidelity: targetFidelity,
            CompressionRatio: compressionRatio,
            GeneratedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Attempts best-effort reconstruction of raw data from a summary.
    /// The result is inherently lossy as summaries discard content.
    /// </summary>
    /// <param name="summary">The summary to reconstruct from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Best-effort reconstruction as raw bytes.</returns>
    public async Task<ReadOnlyMemory<byte>> ReconstructAsync(DataSummary summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (string.IsNullOrEmpty(summary.SummaryText))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        // If AI available and we have an embedding, attempt AI-driven expansion
        if (_aiProvider is { IsAvailable: true } && summary.SummaryEmbedding.Length > 0)
        {
            try
            {
                var request = new AIRequest
                {
                    Prompt = $"Expand the following summary back into a detailed representation. Provide the expanded content only, no commentary.\n\nSummary:\n{summary.SummaryText}",
                    SystemMessage = "You are a data reconstruction engine. Expand summaries into detailed data representations faithfully.",
                    MaxTokens = 4096,
                    Temperature = 0.1f
                };

                var response = await _aiProvider.CompleteAsync(request, ct).ConfigureAwait(false);
                if (response.Success && !string.IsNullOrEmpty(response.Content))
                {
                    return Encoding.UTF8.GetBytes(response.Content);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Fall through to best-effort return
            }
        }

        // Best effort: return summary text as UTF-8 bytes
        return Encoding.UTF8.GetBytes(summary.SummaryText);
    }

    private async Task<string> GenerateAiSummaryAsync(
        string textContent,
        SyncFidelity targetFidelity,
        CancellationToken ct)
    {
        string prompt = targetFidelity switch
        {
            SyncFidelity.Summarized =>
                $"Summarize the following data, preserving key facts in 3 sentences. Provide only the summary, no commentary.\n\n{TruncateForPrompt(textContent)}",
            SyncFidelity.Metadata =>
                $"Extract only the field names, types, and structural schema from the following data. Provide a compact listing.\n\n{TruncateForPrompt(textContent)}",
            SyncFidelity.Standard =>
                $"Create a moderately detailed summary of the following data, preserving important relationships and values. Provide only the summary.\n\n{TruncateForPrompt(textContent)}",
            SyncFidelity.Detailed =>
                $"Create a detailed summary of the following data, preserving most structure and content. Only omit audit trails and version history.\n\n{TruncateForPrompt(textContent)}",
            _ =>
                $"Summarize the following data concisely.\n\n{TruncateForPrompt(textContent)}"
        };

        var request = new AIRequest
        {
            Prompt = prompt,
            SystemMessage = "You are a data summarization engine. Produce concise, accurate summaries.",
            MaxTokens = 2048,
            Temperature = 0.2f
        };

        try
        {
            var response = await _aiProvider!.CompleteAsync(request, ct).ConfigureAwait(false);
            if (response.Success && !string.IsNullOrEmpty(response.Content))
            {
                return response.Content;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Fall through to extraction fallback
        }

        // AI call failed, use extraction fallback
        return GenerateExtractionFallback(
            Encoding.UTF8.GetBytes(textContent),
            targetFidelity,
            isTextBased: true,
            textContent);
    }

    private static string GenerateExtractionFallback(
        ReadOnlyMemory<byte> rawData,
        SyncFidelity targetFidelity,
        bool isTextBased,
        string? textContent)
    {
        int dataLength = rawData.Length;

        int extractBytes = targetFidelity switch
        {
            SyncFidelity.Summarized => Math.Max(1, (int)(dataLength * 0.10)),
            SyncFidelity.Metadata => Math.Min(256, dataLength),
            SyncFidelity.Standard => Math.Max(1, (int)(dataLength * 0.30)),
            SyncFidelity.Detailed => Math.Max(1, (int)(dataLength * 0.60)),
            _ => dataLength
        };

        extractBytes = Math.Min(extractBytes, dataLength);

        if (isTextBased && textContent is not null)
        {
            int charLimit = Math.Min(extractBytes, textContent.Length);
            string extracted = textContent[..charLimit];

            if (targetFidelity == SyncFidelity.Metadata)
            {
                return ExtractMetadataFromText(textContent, extractBytes);
            }

            return extracted;
        }

        // Binary data: extract header bytes with hex representation
        var span = rawData.Span[..extractBytes];
        var sb = new StringBuilder(extractBytes * 3 + 64);
        sb.Append($"[binary:{dataLength}bytes,extracted:{extractBytes}bytes] ");

        if (targetFidelity == SyncFidelity.Metadata)
        {
            // For metadata, provide only size and header signature
            int headerSize = Math.Min(16, extractBytes);
            sb.Append("header:");
            for (int i = 0; i < headerSize; i++)
            {
                sb.Append(span[i].ToString("X2"));
            }
        }
        else
        {
            for (int i = 0; i < Math.Min(extractBytes, 512); i++)
            {
                sb.Append(span[i].ToString("X2"));
            }
            if (extractBytes > 512)
            {
                sb.Append("...");
            }
        }

        return sb.ToString();
    }

    private static string ExtractMetadataFromText(string text, int maxBytes)
    {
        // Try to parse as JSON and extract field names/types
        try
        {
            using var doc = JsonDocument.Parse(text);
            var sb = new StringBuilder(maxBytes);
            ExtractJsonSchema(doc.RootElement, sb, depth: 0, maxDepth: 3);
            string schema = sb.ToString();
            return schema.Length > maxBytes ? schema[..maxBytes] : schema;
        }
        catch (JsonException)
        {
            // Not JSON - extract line-based structure hints
            var lines = text.AsSpan();
            var sb = new StringBuilder(maxBytes);
            sb.Append("[text-metadata] ");

            int pos = 0;
            int lineCount = 0;
            foreach (var line in text.Split('\n'))
            {
                if (pos + line.Length > maxBytes) break;
                string trimmed = line.TrimEnd();
                if (trimmed.Contains(':') || trimmed.Contains('='))
                {
                    int sep = trimmed.AsSpan().IndexOfAny(KeyValueSeparators);
                    if (sep > 0 && sep < 64)
                    {
                        string key = trimmed[..sep].Trim();
                        sb.AppendLine(key);
                        pos += key.Length + 1;
                        lineCount++;
                    }
                }
                if (lineCount > 50) break;
            }

            return sb.Length > 0 ? sb.ToString() : text[..Math.Min(maxBytes, text.Length)];
        }
    }

    private static void ExtractJsonSchema(JsonElement element, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        string indent = new(' ', depth * 2);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.AppendLine($"{indent}{{");
                foreach (var prop in element.EnumerateObject())
                {
                    string typeHint = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => "string",
                        JsonValueKind.Number => "number",
                        JsonValueKind.True or JsonValueKind.False => "boolean",
                        JsonValueKind.Array => $"array[{prop.Value.GetArrayLength()}]",
                        JsonValueKind.Object => "object",
                        JsonValueKind.Null => "null",
                        _ => "unknown"
                    };
                    sb.AppendLine($"{indent}  \"{prop.Name}\": {typeHint}");

                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        ExtractJsonSchema(prop.Value, sb, depth + 1, maxDepth);
                    }
                }
                sb.AppendLine($"{indent}}}");
                break;

            case JsonValueKind.Array:
                int len = element.GetArrayLength();
                sb.AppendLine($"{indent}[{len} items]");
                if (len > 0)
                {
                    ExtractJsonSchema(element[0], sb, depth + 1, maxDepth);
                }
                break;
        }
    }

    private async Task<byte[]> GenerateEmbeddingBytesAsync(string text, CancellationToken ct)
    {
        if (_aiProvider is null || !_aiProvider.IsAvailable ||
            !_aiProvider.Capabilities.HasFlag(AICapabilities.Embeddings))
        {
            return Array.Empty<byte>();
        }

        try
        {
            float[] floats = await _aiProvider.GetEmbeddingsAsync(text, ct).ConfigureAwait(false);
            byte[] bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> data, out string? text)
    {
        text = null;
        if (data.IsEmpty) return false;

        // Check for common binary file signatures
        if (data.Length >= 2)
        {
            // PNG, JPEG, GIF, ZIP, PDF, ELF, etc.
            if ((data[0] == 0x89 && data.Length > 1 && data[1] == 0x50) || // PNG
                (data[0] == 0xFF && data[1] == 0xD8) || // JPEG
                (data[0] == 0x50 && data[1] == 0x4B) || // ZIP/DOCX/JAR
                (data[0] == 0x25 && data[1] == 0x50) || // PDF
                (data[0] == 0x7F && data[1] == 0x45))   // ELF
            {
                return false;
            }
        }

        try
        {
            // Try to decode as UTF-8 with strict validation
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = encoding.GetString(data);

            // Sanity check: high ratio of control characters indicates binary
            int controlCount = 0;
            int checkLength = Math.Min(text.Length, 1024);
            for (int i = 0; i < checkLength; i++)
            {
                char c = text[i];
                if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                {
                    controlCount++;
                }
            }

            if (checkLength > 0 && (double)controlCount / checkLength > 0.1)
            {
                text = null;
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TruncateForPrompt(string text, int maxChars = 8192)
    {
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + "\n[...truncated...]";
    }
}
