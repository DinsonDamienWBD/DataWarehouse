using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Analyzers
{
    /// <summary>
    /// Document analyzer for structured formats: HTML, XML, JSON, Office documents, PDF.
    /// </summary>
    public sealed class DocumentAnalyzer : IContentAnalyzer
    {
        private readonly TextAnalyzer _textAnalyzer;

        public string[] SupportedContentTypes => new[]
        {
            "text/html",
            "application/xhtml+xml",
            "application/xml",
            "text/xml",
            "application/json",
            "application/pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        };

        public DocumentAnalyzer()
        {
            _textAnalyzer = new TextAnalyzer();
        }

        public bool CanAnalyze(string contentType)
        {
            var normalizedType = NormalizeContentType(contentType);
            return SupportedContentTypes.Any(t =>
                normalizedType.Equals(t, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<string> ExtractTextAsync(Stream content, string contentType, CancellationToken ct = default)
        {
            var normalizedType = NormalizeContentType(contentType);

            return normalizedType switch
            {
                "text/html" or "application/xhtml+xml" => await ExtractHtmlTextAsync(content, ct),
                "application/xml" or "text/xml" => await ExtractXmlTextAsync(content, ct),
                "application/json" => await ExtractJsonTextAsync(content, ct),
                "application/pdf" => await ExtractPdfTextAsync(content, ct),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => await ExtractDocxTextAsync(content, ct),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => await ExtractXlsxTextAsync(content, ct),
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => await ExtractPptxTextAsync(content, ct),
                _ => throw new NotSupportedException($"Content type not supported: {contentType}")
            };
        }

        public async Task<ContentAnalysisResult> AnalyzeAsync(
            Stream content,
            string contentType,
            ContentAnalysisOptions options,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Read content into memory for multiple passes
                using var memoryStream = new MemoryStream();
                await content.CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;

                // Extract text
                var text = await ExtractTextAsync(memoryStream, contentType, ct);

                if (text.Length > options.MaxTextLength)
                {
                    text = text[..options.MaxTextLength];
                }

                // Create a new stream from text for further analysis
                using var textStream = new MemoryStream(Encoding.UTF8.GetBytes(text));

                // Delegate to text analyzer for the actual analysis
                var result = await _textAnalyzer.AnalyzeAsync(textStream, "text/plain", options, ct);

                result.Metadata["originalContentType"] = contentType;
                result.Metadata["extractedFromDocument"] = true;

                sw.Stop();
                return result with
                {
                    DetectedContentType = contentType,
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ContentAnalysisResult
                {
                    Success = false,
                    ErrorMessage = $"Document analysis failed: {ex.Message}",
                    Duration = sw.Elapsed
                };
            }
        }

        #region HTML Extraction

        private async Task<string> ExtractHtmlTextAsync(Stream content, CancellationToken ct)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var html = await reader.ReadToEndAsync(ct);

            // Remove scripts and styles
            html = Regex.Replace(html, @"<(script|style|noscript)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);

            // Remove comments
            html = Regex.Replace(html, @"<!--[\s\S]*?-->", " ");

            // Convert block elements to line breaks
            html = Regex.Replace(html, @"<(br|p|div|h[1-6]|li|tr)[^>]*>", "\n", RegexOptions.IgnoreCase);

            // Remove all tags
            html = Regex.Replace(html, @"<[^>]+>", " ");

            // Decode HTML entities
            html = DecodeHtmlEntities(html);

            // Normalize whitespace
            html = Regex.Replace(html, @"\s+", " ").Trim();

            return html;
        }

        private static string DecodeHtmlEntities(string text)
        {
            var entities = new Dictionary<string, string>
            {
                ["&nbsp;"] = " ", ["&amp;"] = "&", ["&lt;"] = "<", ["&gt;"] = ">",
                ["&quot;"] = "\"", ["&apos;"] = "'", ["&copy;"] = "(c)",
                ["&mdash;"] = "-", ["&ndash;"] = "-",
                ["&lsquo;"] = "'", ["&rsquo;"] = "'", ["&ldquo;"] = "\"", ["&rdquo;"] = "\""
            };

            foreach (var (entity, replacement) in entities)
            {
                text = text.Replace(entity, replacement);
            }

            // Numeric entities
            text = Regex.Replace(text, @"&#(\d+);", m =>
            {
                if (int.TryParse(m.Groups[1].Value, out var code) && code < 0x10000)
                    return ((char)code).ToString();
                return m.Value;
            });

            return text;
        }

        #endregion

        #region XML Extraction

        private async Task<string> ExtractXmlTextAsync(Stream content, CancellationToken ct)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null,
                    Async = true
                };

                var textBuilder = new StringBuilder();

                using var reader = XmlReader.Create(content, settings);
                while (await reader.ReadAsync())
                {
                    ct.ThrowIfCancellationRequested();

                    if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                    {
                        var text = reader.Value.Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (textBuilder.Length > 0)
                                textBuilder.Append(' ');
                            textBuilder.Append(text);
                        }
                    }
                }

                return Regex.Replace(textBuilder.ToString(), @"\s+", " ").Trim();
            }
            catch (XmlException)
            {
                // Fall back to regex extraction for malformed XML
                content.Position = 0;
                using var reader = new StreamReader(content, Encoding.UTF8);
                var xml = await reader.ReadToEndAsync(ct);
                xml = Regex.Replace(xml, @"<[^>]+>", " ");
                return Regex.Replace(xml, @"\s+", " ").Trim();
            }
        }

        #endregion

        #region JSON Extraction

        private async Task<string> ExtractJsonTextAsync(Stream content, CancellationToken ct)
        {
            using var reader = new StreamReader(content, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(ct);

            var textBuilder = new StringBuilder();
            ExtractJsonStrings(json, textBuilder);

            return textBuilder.ToString().Trim();
        }

        private void ExtractJsonStrings(string json, StringBuilder output)
        {
            var inString = false;
            var currentString = new StringBuilder();
            var escape = false;

            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];

                if (escape)
                {
                    if (inString)
                    {
                        currentString.Append(c switch
                        {
                            'n' => ' ',
                            'r' => ' ',
                            't' => ' ',
                            _ => c
                        });
                    }
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                {
                    if (inString)
                    {
                        var value = currentString.ToString();
                        currentString.Clear();

                        // Add meaningful text (skip URLs, IDs, dates)
                        if (value.Length > 2 && !IsSkippableJsonValue(value))
                        {
                            if (output.Length > 0)
                                output.Append(' ');
                            output.Append(value);
                        }
                    }
                    inString = !inString;
                }
                else if (inString)
                {
                    currentString.Append(c);
                }
            }
        }

        private static bool IsSkippableJsonValue(string value)
        {
            // Skip URLs
            if (value.StartsWith("http://") || value.StartsWith("https://"))
                return true;

            // Skip base64-like data
            if (value.Length > 100 && Regex.IsMatch(value, @"^[A-Za-z0-9+/=]+$"))
                return true;

            // Skip GUIDs
            if (Regex.IsMatch(value, @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
                return true;

            // Skip ISO dates
            if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2})?"))
                return true;

            return false;
        }

        #endregion

        #region PDF Extraction

        private async Task<string> ExtractPdfTextAsync(Stream content, CancellationToken ct)
        {
            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            var data = memoryStream.ToArray();

            // Check for PDF signature
            if (data.Length < 5 || data[0] != '%' || data[1] != 'P' || data[2] != 'D' || data[3] != 'F')
            {
                return string.Empty;
            }

            var pdfText = Encoding.Latin1.GetString(data);
            var textBuilder = new StringBuilder();

            // Extract text from streams
            var streamRegex = new Regex(@"stream\s*\r?\n([\s\S]*?)\r?\nendstream", RegexOptions.Compiled);
            var textShowRegex = new Regex(@"\(([^)]*)\)\s*Tj|\[([^\]]*)\]\s*TJ", RegexOptions.Compiled);

            var streams = streamRegex.Matches(pdfText);
            foreach (Match stream in streams)
            {
                ct.ThrowIfCancellationRequested();

                var streamContent = stream.Groups[1].Value;

                // Try to decompress if FlateDecode
                var decompressed = TryDecompressFlate(streamContent);
                if (decompressed != null)
                {
                    streamContent = decompressed;
                }

                var textMatches = textShowRegex.Matches(streamContent);
                foreach (Match textMatch in textMatches)
                {
                    var text = textMatch.Groups[1].Success
                        ? textMatch.Groups[1].Value
                        : ExtractTJText(textMatch.Groups[2].Value);

                    text = DecodePdfString(text);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (textBuilder.Length > 0 && !textBuilder.ToString().EndsWith(" "))
                            textBuilder.Append(' ');
                        textBuilder.Append(text);
                    }
                }
            }

            return Regex.Replace(textBuilder.ToString(), @"\s+", " ").Trim();
        }

        private static string ExtractTJText(string tjContent)
        {
            var result = new StringBuilder();
            var inString = false;
            var parenDepth = 0;

            foreach (var c in tjContent)
            {
                if (c == '(')
                {
                    if (parenDepth == 0)
                        inString = true;
                    else
                        result.Append(c);
                    parenDepth++;
                }
                else if (c == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                        inString = false;
                    else
                        result.Append(c);
                }
                else if (inString)
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        private static string? TryDecompressFlate(string content)
        {
            try
            {
                var bytes = Encoding.Latin1.GetBytes(content);
                using var input = new MemoryStream(bytes);

                // Skip zlib header (2 bytes) if present
                if (bytes.Length > 2 && bytes[0] == 0x78)
                {
                    input.Position = 2;
                }

                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                return Encoding.Latin1.GetString(output.ToArray());
            }
            catch
            {
                return null;
            }
        }

        private static string DecodePdfString(string text)
        {
            var result = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    var next = text[i + 1];
                    switch (next)
                    {
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case '(':
                        case ')':
                        case '\\': result.Append(next); break;
                        default: result.Append(next); break;
                    }
                    i++;
                }
                else
                {
                    result.Append(text[i]);
                }
            }

            return result.ToString();
        }

        #endregion

        #region Office Document Extraction

        private async Task<string> ExtractDocxTextAsync(Stream content, CancellationToken ct)
        {
            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            memoryStream.Position = 0;

            var textBuilder = new StringBuilder();

            try
            {
                using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

                var documentEntry = archive.GetEntry("word/document.xml");
                if (documentEntry != null)
                {
                    using var docStream = documentEntry.Open();
                    using var reader = new StreamReader(docStream, Encoding.UTF8);
                    var xml = await reader.ReadToEndAsync(ct);

                    // Extract text from <w:t> elements
                    var matches = Regex.Matches(xml, @"<w:t[^>]*>([^<]*)</w:t>");
                    foreach (Match match in matches)
                    {
                        var text = match.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            textBuilder.Append(text);
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                // Not a valid ZIP/DOCX file
                return string.Empty;
            }

            return Regex.Replace(textBuilder.ToString(), @"\s+", " ").Trim();
        }

        private async Task<string> ExtractXlsxTextAsync(Stream content, CancellationToken ct)
        {
            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            memoryStream.Position = 0;

            var textBuilder = new StringBuilder();
            var sharedStrings = new List<string>();

            try
            {
                using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

                // Load shared strings
                var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
                if (sharedStringsEntry != null)
                {
                    using var ssStream = sharedStringsEntry.Open();
                    using var reader = new StreamReader(ssStream, Encoding.UTF8);
                    var xml = await reader.ReadToEndAsync(ct);

                    var matches = Regex.Matches(xml, @"<t[^>]*>([^<]*)</t>");
                    foreach (Match match in matches)
                    {
                        sharedStrings.Add(match.Groups[1].Value);
                    }
                }

                // Extract sheet data
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith("xl/worksheets/sheet") && entry.FullName.EndsWith(".xml"))
                    {
                        ct.ThrowIfCancellationRequested();

                        using var sheetStream = entry.Open();
                        using var reader = new StreamReader(sheetStream, Encoding.UTF8);
                        var xml = await reader.ReadToEndAsync(ct);

                        // Extract cell values
                        var cellMatches = Regex.Matches(xml, @"<c[^>]*(?:t=""s"")?[^>]*><v>(\d+)</v></c>");
                        foreach (Match match in cellMatches)
                        {
                            if (int.TryParse(match.Groups[1].Value, out var idx) && idx < sharedStrings.Count)
                            {
                                var value = sharedStrings[idx];
                                if (!string.IsNullOrWhiteSpace(value) && !double.TryParse(value, out _))
                                {
                                    if (textBuilder.Length > 0)
                                        textBuilder.Append(' ');
                                    textBuilder.Append(value);
                                }
                            }
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                return string.Empty;
            }

            return Regex.Replace(textBuilder.ToString(), @"\s+", " ").Trim();
        }

        private async Task<string> ExtractPptxTextAsync(Stream content, CancellationToken ct)
        {
            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            memoryStream.Position = 0;

            var textBuilder = new StringBuilder();

            try
            {
                using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith("ppt/slides/slide") && entry.FullName.EndsWith(".xml"))
                    {
                        ct.ThrowIfCancellationRequested();

                        using var slideStream = entry.Open();
                        using var reader = new StreamReader(slideStream, Encoding.UTF8);
                        var xml = await reader.ReadToEndAsync(ct);

                        // Extract text from <a:t> elements
                        var matches = Regex.Matches(xml, @"<a:t>([^<]*)</a:t>");
                        foreach (Match match in matches)
                        {
                            var text = match.Groups[1].Value;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                if (textBuilder.Length > 0)
                                    textBuilder.Append(' ');
                                textBuilder.Append(text);
                            }
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                return string.Empty;
            }

            return Regex.Replace(textBuilder.ToString(), @"\s+", " ").Trim();
        }

        #endregion

        private static string NormalizeContentType(string contentType)
        {
            var idx = contentType.IndexOf(';');
            return idx >= 0 ? contentType[..idx].Trim().ToLowerInvariant() : contentType.Trim().ToLowerInvariant();
        }
    }
}
