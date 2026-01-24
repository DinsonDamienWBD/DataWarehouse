using DataWarehouse.SDK.Contracts;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataWarehouse.Plugins.ContentProcessing
{
    /// <summary>
    /// Production-ready text extraction plugin for DataWarehouse.
    /// Extracts text from various document formats including plain text, HTML, XML, JSON, CSV,
    /// PDF, and Office documents (DOCX, XLSX). Supports encoding detection and metadata extraction.
    /// </summary>
    public sealed class TextExtractionPlugin : ContentProcessorPluginBase
    {
        public override string Id => "datawarehouse.contentprocessing.textextraction";
        public override string Name => "Text Extraction Processor";
        public override string Version => "1.0.0";
        public override ContentProcessingType ProcessingType => ContentProcessingType.TextExtraction;

        public override string[] SupportedContentTypes => new[]
        {
            "text/*",
            "application/json",
            "application/xml",
            "text/xml",
            "text/html",
            "text/csv",
            "application/pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "application/msword",
            "application/vnd.ms-excel"
        };

        private readonly EncodingDetector _encodingDetector;
        private readonly HtmlTextExtractor _htmlExtractor;
        private readonly XmlTextExtractor _xmlExtractor;
        private readonly JsonTextExtractor _jsonExtractor;
        private readonly CsvTextExtractor _csvExtractor;
        private readonly PdfTextExtractor _pdfExtractor;
        private readonly OfficeDocumentExtractor _officeExtractor;

        private long _documentsProcessed;
        private long _bytesProcessed;

        public TextExtractionPlugin()
        {
            _encodingDetector = new EncodingDetector();
            _htmlExtractor = new HtmlTextExtractor();
            _xmlExtractor = new XmlTextExtractor();
            _jsonExtractor = new JsonTextExtractor();
            _csvExtractor = new CsvTextExtractor();
            _pdfExtractor = new PdfTextExtractor();
            _officeExtractor = new OfficeDocumentExtractor();
        }

        public override async Task<ContentProcessingResult> ProcessAsync(
            Stream content,
            string contentType,
            Dictionary<string, object>? context = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                if (!SupportsContentType(contentType))
                {
                    return FailureResult($"Unsupported content type: {contentType}", sw.Elapsed);
                }

                // Read content into memory for processing
                using var memoryStream = new MemoryStream();
                await content.CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;

                var bytes = memoryStream.ToArray();
                Interlocked.Add(ref _bytesProcessed, bytes.Length);

                // Extract text based on content type
                var (extractedText, metadata) = await ExtractTextAsync(bytes, contentType, ct);

                Interlocked.Increment(ref _documentsProcessed);
                sw.Stop();

                return SuccessResult(
                    extractedText: extractedText,
                    metadata: metadata,
                    duration: sw.Elapsed);
            }
            catch (OperationCanceledException)
            {
                return FailureResult("Text extraction cancelled", sw.Elapsed);
            }
            catch (Exception ex)
            {
                return FailureResult($"Text extraction failed: {ex.Message}", sw.Elapsed);
            }
        }

        private async Task<(string Text, Dictionary<string, object> Metadata)> ExtractTextAsync(
            byte[] data,
            string contentType,
            CancellationToken ct)
        {
            var normalizedType = NormalizeContentType(contentType);
            var metadata = new Dictionary<string, object>
            {
                ["originalContentType"] = contentType,
                ["byteLength"] = data.Length
            };

            string extractedText;

            switch (normalizedType)
            {
                case "text/html":
                    extractedText = await _htmlExtractor.ExtractAsync(data, _encodingDetector, metadata, ct);
                    break;

                case "application/xml":
                case "text/xml":
                    extractedText = await _xmlExtractor.ExtractAsync(data, _encodingDetector, metadata, ct);
                    break;

                case "application/json":
                    extractedText = await _jsonExtractor.ExtractAsync(data, _encodingDetector, metadata, ct);
                    break;

                case "text/csv":
                    extractedText = await _csvExtractor.ExtractAsync(data, _encodingDetector, metadata, ct);
                    break;

                case "application/pdf":
                    extractedText = await _pdfExtractor.ExtractAsync(data, metadata, ct);
                    break;

                case "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
                    extractedText = await _officeExtractor.ExtractDocxAsync(data, metadata, ct);
                    break;

                case "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet":
                    extractedText = await _officeExtractor.ExtractXlsxAsync(data, metadata, ct);
                    break;

                case "application/vnd.openxmlformats-officedocument.presentationml.presentation":
                    extractedText = await _officeExtractor.ExtractPptxAsync(data, metadata, ct);
                    break;

                default:
                    // Plain text - detect encoding and extract
                    extractedText = await ExtractPlainTextAsync(data, metadata, ct);
                    break;
            }

            metadata["extractedLength"] = extractedText.Length;
            metadata["wordCount"] = CountWords(extractedText);

            return (extractedText, metadata);
        }

        private async Task<string> ExtractPlainTextAsync(
            byte[] data,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var (encoding, confidence) = _encodingDetector.DetectEncoding(data);
            metadata["detectedEncoding"] = encoding.EncodingName;
            metadata["encodingConfidence"] = confidence;

            return await Task.Run(() => encoding.GetString(data), ct);
        }

        private static string NormalizeContentType(string contentType)
        {
            // Remove charset and other parameters
            var idx = contentType.IndexOf(';');
            var baseType = idx >= 0 ? contentType[..idx].Trim() : contentType.Trim();
            return baseType.ToLowerInvariant();
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            return Regex.Matches(text, @"\b\w+\b").Count;
        }

        /// <summary>
        /// Gets processing statistics.
        /// </summary>
        public TextExtractionStatistics GetStatistics()
        {
            return new TextExtractionStatistics
            {
                DocumentsProcessed = Interlocked.Read(ref _documentsProcessed),
                BytesProcessed = Interlocked.Read(ref _bytesProcessed)
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsHtml"] = true;
            metadata["SupportsXml"] = true;
            metadata["SupportsJson"] = true;
            metadata["SupportsCsv"] = true;
            metadata["SupportsPdf"] = true;
            metadata["SupportsDocx"] = true;
            metadata["SupportsXlsx"] = true;
            metadata["SupportsPptx"] = true;
            metadata["SupportsEncodingDetection"] = true;
            metadata["DocumentsProcessed"] = Interlocked.Read(ref _documentsProcessed);
            metadata["BytesProcessed"] = Interlocked.Read(ref _bytesProcessed);
            return metadata;
        }
    }

    /// <summary>
    /// Text extraction statistics.
    /// </summary>
    public sealed class TextExtractionStatistics
    {
        public long DocumentsProcessed { get; init; }
        public long BytesProcessed { get; init; }
    }

    #region Encoding Detection

    /// <summary>
    /// Encoding detector supporting UTF-8, UTF-16, ASCII, and Latin-1.
    /// Uses BOM detection and statistical analysis for encoding inference.
    /// </summary>
    internal sealed class EncodingDetector
    {
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
        private static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };
        private static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };
        private static readonly byte[] Utf32LeBom = { 0xFF, 0xFE, 0x00, 0x00 };
        private static readonly byte[] Utf32BeBom = { 0x00, 0x00, 0xFE, 0xFF };

        public (Encoding Encoding, double Confidence) DetectEncoding(byte[] data)
        {
            if (data.Length == 0)
                return (Encoding.UTF8, 1.0);

            // Check for BOM
            var bomResult = DetectByBom(data);
            if (bomResult != null)
                return (bomResult, 1.0);

            // Statistical analysis
            return DetectByStatistics(data);
        }

        private Encoding? DetectByBom(byte[] data)
        {
            if (data.Length >= 4)
            {
                if (StartsWith(data, Utf32BeBom))
                    return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
                if (StartsWith(data, Utf32LeBom))
                    return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
            }

            if (data.Length >= 3 && StartsWith(data, Utf8Bom))
                return Encoding.UTF8;

            if (data.Length >= 2)
            {
                if (StartsWith(data, Utf16BeBom))
                    return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                if (StartsWith(data, Utf16LeBom))
                    return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            }

            return null;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length)
                return false;

            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i])
                    return false;
            }

            return true;
        }

        private (Encoding Encoding, double Confidence) DetectByStatistics(byte[] data)
        {
            var sampleSize = Math.Min(data.Length, 8192);

            // Check for null bytes (indicates UTF-16)
            var nullCount = 0;
            var highByteCount = 0;
            var invalidUtf8Sequences = 0;

            for (int i = 0; i < sampleSize; i++)
            {
                if (data[i] == 0)
                    nullCount++;
                if (data[i] > 127)
                    highByteCount++;
            }

            // High null ratio suggests UTF-16
            var nullRatio = (double)nullCount / sampleSize;
            if (nullRatio > 0.1)
            {
                // Check if null bytes appear at even or odd positions
                var evenNulls = 0;
                var oddNulls = 0;
                for (int i = 0; i < sampleSize; i++)
                {
                    if (data[i] == 0)
                    {
                        if (i % 2 == 0)
                            evenNulls++;
                        else
                            oddNulls++;
                    }
                }

                if (oddNulls > evenNulls * 2)
                    return (Encoding.Unicode, 0.8); // UTF-16 LE
                if (evenNulls > oddNulls * 2)
                    return (Encoding.BigEndianUnicode, 0.8); // UTF-16 BE
            }

            // Validate UTF-8 sequences
            invalidUtf8Sequences = CountInvalidUtf8Sequences(data, sampleSize);

            if (invalidUtf8Sequences == 0 && highByteCount > 0)
            {
                return (Encoding.UTF8, 0.95); // Valid UTF-8 with multi-byte chars
            }

            if (invalidUtf8Sequences == 0)
            {
                return (Encoding.UTF8, 0.9); // ASCII-compatible UTF-8
            }

            // Fall back to Latin-1 if high bytes present but not valid UTF-8
            var highByteRatio = (double)highByteCount / sampleSize;
            if (highByteRatio > 0.01)
            {
                return (Encoding.Latin1, 0.7);
            }

            // Default to ASCII/UTF-8
            return (Encoding.ASCII, 0.8);
        }

        private static int CountInvalidUtf8Sequences(byte[] data, int length)
        {
            var invalidCount = 0;
            int i = 0;

            while (i < length)
            {
                var b = data[i];

                if (b <= 0x7F)
                {
                    // ASCII
                    i++;
                }
                else if (b >= 0xC2 && b <= 0xDF)
                {
                    // 2-byte sequence
                    if (i + 1 >= length || !IsContinuation(data[i + 1]))
                        invalidCount++;
                    i += 2;
                }
                else if (b >= 0xE0 && b <= 0xEF)
                {
                    // 3-byte sequence
                    if (i + 2 >= length || !IsContinuation(data[i + 1]) || !IsContinuation(data[i + 2]))
                        invalidCount++;
                    i += 3;
                }
                else if (b >= 0xF0 && b <= 0xF4)
                {
                    // 4-byte sequence
                    if (i + 3 >= length || !IsContinuation(data[i + 1]) ||
                        !IsContinuation(data[i + 2]) || !IsContinuation(data[i + 3]))
                        invalidCount++;
                    i += 4;
                }
                else
                {
                    // Invalid start byte
                    invalidCount++;
                    i++;
                }
            }

            return invalidCount;
        }

        private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
    }

    #endregion

    #region HTML Text Extractor

    /// <summary>
    /// Extracts text from HTML content with tag stripping and whitespace normalization.
    /// Also extracts metadata from title, meta tags, and structured data.
    /// </summary>
    internal sealed class HtmlTextExtractor
    {
        private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex ScriptStyleRegex = new(@"<(script|style|noscript)[^>]*>[\s\S]*?</\1>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CommentRegex = new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"[\s\u00A0]+", RegexOptions.Compiled);
        private static readonly Regex TitleRegex = new(@"<title[^>]*>([\s\S]*?)</title>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MetaRegex = new(
            @"<meta\s+(?:[^>]*?\s+)?(?:name|property)\s*=\s*[""']([^""']+)[""'][^>]*?\s+content\s*=\s*[""']([^""']*)[""'][^>]*?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MetaAltRegex = new(
            @"<meta\s+(?:[^>]*?\s+)?content\s*=\s*[""']([^""']*)[""'][^>]*?\s+(?:name|property)\s*=\s*[""']([^""']+)[""'][^>]*?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, string> HtmlEntities = new()
        {
            ["&nbsp;"] = " ", ["&amp;"] = "&", ["&lt;"] = "<", ["&gt;"] = ">",
            ["&quot;"] = "\"", ["&apos;"] = "'", ["&copy;"] = "(c)", ["&reg;"] = "(R)",
            ["&trade;"] = "(TM)", ["&mdash;"] = "-", ["&ndash;"] = "-",
            ["&lsquo;"] = "'", ["&rsquo;"] = "'", ["&ldquo;"] = "\"", ["&rdquo;"] = "\"",
            ["&bull;"] = "*", ["&hellip;"] = "...", ["&euro;"] = "EUR",
            ["&pound;"] = "GBP", ["&yen;"] = "JPY"
        };

        public Task<string> ExtractAsync(
            byte[] data,
            EncodingDetector encodingDetector,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var (encoding, _) = encodingDetector.DetectEncoding(data);
            var html = encoding.GetString(data);

            // Extract metadata
            ExtractHtmlMetadata(html, metadata);

            // Remove scripts, styles, and comments
            var text = ScriptStyleRegex.Replace(html, " ");
            text = CommentRegex.Replace(text, " ");

            // Convert block elements to line breaks
            text = Regex.Replace(text, @"<(br|p|div|h[1-6]|li|tr|td|th)[^>]*>", "\n", RegexOptions.IgnoreCase);

            // Remove remaining tags
            text = TagRegex.Replace(text, " ");

            // Decode HTML entities
            text = DecodeHtmlEntities(text);

            // Normalize whitespace
            text = WhitespaceRegex.Replace(text, " ");
            text = Regex.Replace(text, @"\n\s*\n", "\n\n");
            text = text.Trim();

            return Task.FromResult(text);
        }

        private void ExtractHtmlMetadata(string html, Dictionary<string, object> metadata)
        {
            // Extract title
            var titleMatch = TitleRegex.Match(html);
            if (titleMatch.Success)
            {
                var title = TagRegex.Replace(titleMatch.Groups[1].Value, "").Trim();
                title = DecodeHtmlEntities(title);
                metadata["title"] = title;
            }

            // Extract meta tags
            foreach (Match match in MetaRegex.Matches(html))
            {
                var name = match.Groups[1].Value.ToLowerInvariant();
                var content = DecodeHtmlEntities(match.Groups[2].Value);
                ProcessMetaTag(name, content, metadata);
            }

            foreach (Match match in MetaAltRegex.Matches(html))
            {
                var content = DecodeHtmlEntities(match.Groups[1].Value);
                var name = match.Groups[2].Value.ToLowerInvariant();
                ProcessMetaTag(name, content, metadata);
            }
        }

        private void ProcessMetaTag(string name, string content, Dictionary<string, object> metadata)
        {
            switch (name)
            {
                case "author":
                case "dc.creator":
                    metadata["author"] = content;
                    break;
                case "description":
                case "og:description":
                    metadata["description"] = content;
                    break;
                case "keywords":
                    metadata["keywords"] = content.Split(',').Select(k => k.Trim()).ToArray();
                    break;
                case "date":
                case "dc.date":
                case "article:published_time":
                    if (DateTime.TryParse(content, out var date))
                        metadata["date"] = date;
                    break;
            }
        }

        private static string DecodeHtmlEntities(string text)
        {
            foreach (var entity in HtmlEntities)
            {
                text = text.Replace(entity.Key, entity.Value);
            }

            // Numeric entities
            text = Regex.Replace(text, @"&#(\d+);", m =>
            {
                if (int.TryParse(m.Groups[1].Value, out var code) && code < 0x10000)
                    return ((char)code).ToString();
                return m.Value;
            });

            text = Regex.Replace(text, @"&#x([0-9A-Fa-f]+);", m =>
            {
                if (int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                    null, out var code) && code < 0x10000)
                    return ((char)code).ToString();
                return m.Value;
            });

            return text;
        }
    }

    #endregion

    #region XML Text Extractor

    /// <summary>
    /// Extracts text content from XML documents, preserving structure.
    /// </summary>
    internal sealed class XmlTextExtractor
    {
        public Task<string> ExtractAsync(
            byte[] data,
            EncodingDetector encodingDetector,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            try
            {
                using var stream = new MemoryStream(data);
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null,
                    IgnoreWhitespace = false
                };

                var textBuilder = new StringBuilder();
                var depth = 0;

                using var reader = XmlReader.Create(stream, settings);

                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();

                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                            depth++;
                            // Check for common metadata elements
                            if (depth <= 2)
                            {
                                ExtractXmlMetadata(reader, metadata);
                            }
                            break;

                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                            var text = reader.Value.Trim();
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (textBuilder.Length > 0)
                                    textBuilder.Append(' ');
                                textBuilder.Append(text);
                            }
                            break;

                        case XmlNodeType.EndElement:
                            depth--;
                            break;
                    }
                }

                var result = NormalizeWhitespace(textBuilder.ToString());
                return Task.FromResult(result);
            }
            catch (XmlException)
            {
                // Fall back to regex-based extraction for malformed XML
                var (encoding, _) = encodingDetector.DetectEncoding(data);
                var text = encoding.GetString(data);

                // Remove XML tags
                text = Regex.Replace(text, @"<[^>]+>", " ");
                text = NormalizeWhitespace(text);

                return Task.FromResult(text);
            }
        }

        private void ExtractXmlMetadata(XmlReader reader, Dictionary<string, object> metadata)
        {
            var localName = reader.LocalName.ToLowerInvariant();

            switch (localName)
            {
                case "title":
                case "name":
                    if (!metadata.ContainsKey("title"))
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (!string.IsNullOrEmpty(text))
                            metadata["title"] = text;
                    }
                    break;

                case "author":
                case "creator":
                    if (!metadata.ContainsKey("author"))
                    {
                        var text = reader.ReadElementContentAsString().Trim();
                        if (!string.IsNullOrEmpty(text))
                            metadata["author"] = text;
                    }
                    break;

                case "date":
                case "created":
                case "modified":
                    var dateText = reader.ReadElementContentAsString().Trim();
                    if (DateTime.TryParse(dateText, out var date))
                        metadata[localName] = date;
                    break;
            }
        }

        private static string NormalizeWhitespace(string text)
        {
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }

    #endregion

    #region JSON Text Extractor

    /// <summary>
    /// Extracts text content from JSON documents.
    /// Extracts all string values and concatenates them.
    /// </summary>
    internal sealed class JsonTextExtractor
    {
        public Task<string> ExtractAsync(
            byte[] data,
            EncodingDetector encodingDetector,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var (encoding, _) = encodingDetector.DetectEncoding(data);
            var json = encoding.GetString(data);

            var textBuilder = new StringBuilder();
            var keyStack = new Stack<string>();

            ExtractJsonStrings(json, textBuilder, keyStack, metadata, ct);

            var result = textBuilder.ToString().Trim();
            return Task.FromResult(result);
        }

        private void ExtractJsonStrings(
            string json,
            StringBuilder textBuilder,
            Stack<string> keyStack,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var inString = false;
            var isKey = true;
            var currentString = new StringBuilder();
            var currentKey = string.Empty;
            var escape = false;
            var braceDepth = 0;

            for (int i = 0; i < json.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var c = json[i];

                if (escape)
                {
                    if (inString)
                    {
                        currentString.Append(c switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
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

                        if (isKey)
                        {
                            currentKey = value;
                            isKey = false;
                        }
                        else
                        {
                            // This is a string value
                            ProcessJsonValue(currentKey, value, textBuilder, metadata);
                            currentKey = string.Empty;
                        }
                    }
                    inString = !inString;
                }
                else if (inString)
                {
                    currentString.Append(c);
                }
                else
                {
                    switch (c)
                    {
                        case '{':
                            braceDepth++;
                            isKey = true;
                            break;
                        case '}':
                            braceDepth--;
                            break;
                        case '[':
                        case ']':
                            break;
                        case ':':
                            break;
                        case ',':
                            isKey = braceDepth > 0;
                            break;
                    }
                }
            }
        }

        private void ProcessJsonValue(string key, string value, StringBuilder textBuilder, Dictionary<string, object> metadata)
        {
            var lowerKey = key.ToLowerInvariant();

            // Check for metadata fields
            switch (lowerKey)
            {
                case "title":
                case "name":
                    if (!metadata.ContainsKey("title"))
                        metadata["title"] = value;
                    break;
                case "author":
                case "creator":
                    if (!metadata.ContainsKey("author"))
                        metadata["author"] = value;
                    break;
                case "description":
                    if (!metadata.ContainsKey("description"))
                        metadata["description"] = value;
                    break;
                case "date":
                case "created":
                case "createdat":
                case "modified":
                case "modifiedat":
                    if (DateTime.TryParse(value, out var date))
                        metadata[lowerKey.Replace("at", "")] = date;
                    break;
            }

            // Add meaningful text (skip URLs, IDs, dates)
            if (value.Length > 2 && !IsSkippableValue(value))
            {
                if (textBuilder.Length > 0)
                    textBuilder.Append(' ');
                textBuilder.Append(value);
            }
        }

        private static bool IsSkippableValue(string value)
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
    }

    #endregion

    #region CSV Text Extractor

    /// <summary>
    /// Extracts text from CSV files.
    /// Combines headers and data into readable text.
    /// </summary>
    internal sealed class CsvTextExtractor
    {
        public Task<string> ExtractAsync(
            byte[] data,
            EncodingDetector encodingDetector,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var (encoding, _) = encodingDetector.DetectEncoding(data);
            var csv = encoding.GetString(data);

            var textBuilder = new StringBuilder();
            var lines = csv.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0)
                return Task.FromResult(string.Empty);

            // Parse header
            var headers = ParseCsvLine(lines[0]);
            metadata["columnCount"] = headers.Length;
            metadata["rowCount"] = lines.Length - 1;
            metadata["headers"] = headers;

            // Extract text from all cells
            foreach (var line in lines)
            {
                ct.ThrowIfCancellationRequested();

                var values = ParseCsvLine(line);
                foreach (var value in values)
                {
                    var trimmed = value.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !IsNumeric(trimmed))
                    {
                        if (textBuilder.Length > 0)
                            textBuilder.Append(' ');
                        textBuilder.Append(trimmed);
                    }
                }
            }

            return Task.FromResult(textBuilder.ToString());
        }

        private static string[] ParseCsvLine(string line)
        {
            var values = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++; // Skip escaped quote
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        values.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            values.Add(current.ToString());
            return values.ToArray();
        }

        private static bool IsNumeric(string value)
        {
            return double.TryParse(value, out _);
        }
    }

    #endregion

    #region PDF Text Extractor

    /// <summary>
    /// Extracts text from PDF documents by parsing the PDF structure.
    /// Handles basic text streams, content extraction, and metadata.
    /// </summary>
    internal sealed class PdfTextExtractor
    {
        private static readonly Regex StreamRegex = new(
            @"stream\s*\r?\n([\s\S]*?)\r?\nendstream",
            RegexOptions.Compiled);

        private static readonly Regex TextShowRegex = new(
            @"\(([^)]*)\)\s*Tj|\[([^\]]*)\]\s*TJ",
            RegexOptions.Compiled);

        private static readonly Regex InfoRegex = new(
            @"/(\w+)\s*\(([^)]*)\)",
            RegexOptions.Compiled);

        public Task<string> ExtractAsync(
            byte[] data,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var textBuilder = new StringBuilder();

            // Check for PDF signature
            if (data.Length < 5 || data[0] != '%' || data[1] != 'P' || data[2] != 'D' || data[3] != 'F')
            {
                return Task.FromResult(string.Empty);
            }

            var pdfText = Encoding.Latin1.GetString(data);

            // Extract metadata from Info dictionary
            ExtractPdfMetadata(pdfText, metadata);

            // Extract text from streams
            var streams = StreamRegex.Matches(pdfText);
            foreach (Match stream in streams)
            {
                ct.ThrowIfCancellationRequested();

                var content = stream.Groups[1].Value;

                // Try to decompress if FlateDecode
                var decompressed = TryDecompressFlate(content);
                if (decompressed != null)
                {
                    content = decompressed;
                }

                // Extract text operators
                var textMatches = TextShowRegex.Matches(content);
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

            // Also try direct text extraction for uncompressed content
            var directText = ExtractDirectText(pdfText);
            if (!string.IsNullOrEmpty(directText) && textBuilder.Length == 0)
            {
                textBuilder.Append(directText);
            }

            var result = NormalizeWhitespace(textBuilder.ToString());
            return Task.FromResult(result);
        }

        private void ExtractPdfMetadata(string pdfText, Dictionary<string, object> metadata)
        {
            // Look for Info dictionary
            var infoStart = pdfText.IndexOf("/Info");
            if (infoStart < 0)
                return;

            var searchArea = pdfText.Substring(Math.Max(0, infoStart - 1000),
                Math.Min(2000, pdfText.Length - infoStart + 1000));

            var matches = InfoRegex.Matches(searchArea);
            foreach (Match match in matches)
            {
                var key = match.Groups[1].Value.ToLowerInvariant();
                var value = DecodePdfString(match.Groups[2].Value);

                switch (key)
                {
                    case "title":
                        metadata["title"] = value;
                        break;
                    case "author":
                    case "creator":
                        metadata["author"] = value;
                        break;
                    case "subject":
                        metadata["description"] = value;
                        break;
                    case "creationdate":
                        if (TryParsePdfDate(value, out var date))
                            metadata["created"] = date;
                        break;
                    case "moddate":
                        if (TryParsePdfDate(value, out var modDate))
                            metadata["modified"] = modDate;
                        break;
                }
            }
        }

        private static string ExtractTJText(string tjContent)
        {
            var result = new StringBuilder();
            var inString = false;
            var parenDepth = 0;

            for (int i = 0; i < tjContent.Length; i++)
            {
                var c = tjContent[i];

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
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();

                // Skip zlib header (2 bytes) if present
                if (bytes.Length > 2 && bytes[0] == 0x78)
                {
                    input.Position = 2;
                }

                deflate.CopyTo(output);
                return Encoding.Latin1.GetString(output.ToArray());
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractDirectText(string pdfText)
        {
            var result = new StringBuilder();

            // Look for BT...ET text blocks
            var btMatches = Regex.Matches(pdfText, @"BT\s*([\s\S]*?)\s*ET", RegexOptions.IgnoreCase);
            foreach (Match btMatch in btMatches)
            {
                var block = btMatch.Groups[1].Value;
                var textMatches = TextShowRegex.Matches(block);

                foreach (Match textMatch in textMatches)
                {
                    var text = textMatch.Groups[1].Success
                        ? textMatch.Groups[1].Value
                        : ExtractTJText(textMatch.Groups[2].Value);

                    text = DecodePdfString(text);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (result.Length > 0)
                            result.Append(' ');
                        result.Append(text);
                    }
                }
            }

            return result.ToString();
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
                        case 'n':
                            result.Append('\n');
                            break;
                        case 'r':
                            result.Append('\r');
                            break;
                        case 't':
                            result.Append('\t');
                            break;
                        case '(':
                        case ')':
                        case '\\':
                            result.Append(next);
                            break;
                        default:
                            // Octal escape
                            if (char.IsDigit(next))
                            {
                                var octal = new StringBuilder();
                                octal.Append(next);
                                i++;
                                while (i + 1 < text.Length && octal.Length < 3 && char.IsDigit(text[i + 1]))
                                {
                                    octal.Append(text[++i]);
                                }
                                if (int.TryParse(octal.ToString(), out var code))
                                {
                                    result.Append((char)Convert.ToInt32(octal.ToString(), 8));
                                }
                            }
                            else
                            {
                                result.Append(next);
                            }
                            break;
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

        private static bool TryParsePdfDate(string dateStr, out DateTime result)
        {
            result = DateTime.MinValue;

            // PDF date format: D:YYYYMMDDHHmmSSOHH'mm'
            if (dateStr.StartsWith("D:") && dateStr.Length >= 10)
            {
                dateStr = dateStr.Substring(2);

                if (dateStr.Length >= 8)
                {
                    var year = int.Parse(dateStr.Substring(0, 4));
                    var month = int.Parse(dateStr.Substring(4, 2));
                    var day = int.Parse(dateStr.Substring(6, 2));

                    var hour = dateStr.Length >= 10 ? int.Parse(dateStr.Substring(8, 2)) : 0;
                    var minute = dateStr.Length >= 12 ? int.Parse(dateStr.Substring(10, 2)) : 0;
                    var second = dateStr.Length >= 14 ? int.Parse(dateStr.Substring(12, 2)) : 0;

                    result = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                    return true;
                }
            }

            return DateTime.TryParse(dateStr, out result);
        }

        private static string NormalizeWhitespace(string text)
        {
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }

    #endregion

    #region Office Document Extractor

    /// <summary>
    /// Extracts text from Office Open XML documents (DOCX, XLSX, PPTX).
    /// Parses the XML inside the ZIP archive to extract text content and metadata.
    /// </summary>
    internal sealed class OfficeDocumentExtractor
    {
        // DOCX XML namespaces
        private const string WordProcessingNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string CorePropertiesNs = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        private const string DcNs = "http://purl.org/dc/elements/1.1/";

        // XLSX XML namespaces
        private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        // PPTX XML namespaces
        private const string PresentationNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
        private const string DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";

        public async Task<string> ExtractDocxAsync(
            byte[] data,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var textBuilder = new StringBuilder();

            try
            {
                using var stream = new MemoryStream(data);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                // Extract core properties (metadata)
                await ExtractCorePropertiesAsync(archive, metadata, ct);

                // Extract document text from word/document.xml
                var documentEntry = archive.GetEntry("word/document.xml");
                if (documentEntry != null)
                {
                    using var docStream = documentEntry.Open();
                    var doc = await LoadXmlAsync(docStream, ct);

                    // Extract paragraphs
                    var paragraphs = doc.GetElementsByTagName("p", WordProcessingNs);
                    foreach (XmlNode para in paragraphs)
                    {
                        ct.ThrowIfCancellationRequested();

                        var paraText = ExtractTextFromNode(para);
                        if (!string.IsNullOrWhiteSpace(paraText))
                        {
                            if (textBuilder.Length > 0)
                                textBuilder.AppendLine();
                            textBuilder.Append(paraText);
                        }
                    }
                }

                // Also extract text from headers/footers
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith("word/header") ||
                        entry.FullName.StartsWith("word/footer"))
                    {
                        using var hfStream = entry.Open();
                        var hfDoc = await LoadXmlAsync(hfStream, ct);

                        var paragraphs = hfDoc.GetElementsByTagName("p", WordProcessingNs);
                        foreach (XmlNode para in paragraphs)
                        {
                            var paraText = ExtractTextFromNode(para);
                            if (!string.IsNullOrWhiteSpace(paraText))
                            {
                                textBuilder.Append(' ').Append(paraText);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                metadata["extractionError"] = ex.Message;
            }

            return textBuilder.ToString().Trim();
        }

        public async Task<string> ExtractXlsxAsync(
            byte[] data,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var textBuilder = new StringBuilder();

            try
            {
                using var stream = new MemoryStream(data);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                // Extract core properties
                await ExtractCorePropertiesAsync(archive, metadata, ct);

                // Load shared strings
                var sharedStrings = new List<string>();
                var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
                if (sharedStringsEntry != null)
                {
                    using var ssStream = sharedStringsEntry.Open();
                    var ssDoc = await LoadXmlAsync(ssStream, ct);

                    var siElements = ssDoc.GetElementsByTagName("si", SpreadsheetNs);
                    foreach (XmlNode si in siElements)
                    {
                        sharedStrings.Add(ExtractTextFromNode(si));
                    }
                }

                metadata["sharedStringCount"] = sharedStrings.Count;

                // Extract sheet data
                var sheetCount = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith("xl/worksheets/sheet") &&
                        entry.FullName.EndsWith(".xml"))
                    {
                        ct.ThrowIfCancellationRequested();
                        sheetCount++;

                        using var sheetStream = entry.Open();
                        var sheetDoc = await LoadXmlAsync(sheetStream, ct);

                        var cells = sheetDoc.GetElementsByTagName("c", SpreadsheetNs);
                        foreach (XmlNode cell in cells)
                        {
                            var type = cell.Attributes?["t"]?.Value;
                            var valueNode = cell.SelectSingleNode("*[local-name()='v']");

                            if (valueNode != null)
                            {
                                string cellValue;

                                if (type == "s" && int.TryParse(valueNode.InnerText, out var idx) &&
                                    idx < sharedStrings.Count)
                                {
                                    cellValue = sharedStrings[idx];
                                }
                                else
                                {
                                    cellValue = valueNode.InnerText;
                                }

                                if (!string.IsNullOrWhiteSpace(cellValue) && !IsNumeric(cellValue))
                                {
                                    if (textBuilder.Length > 0)
                                        textBuilder.Append(' ');
                                    textBuilder.Append(cellValue);
                                }
                            }
                        }
                    }
                }

                metadata["sheetCount"] = sheetCount;
            }
            catch (Exception ex)
            {
                metadata["extractionError"] = ex.Message;
            }

            return textBuilder.ToString().Trim();
        }

        public async Task<string> ExtractPptxAsync(
            byte[] data,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var textBuilder = new StringBuilder();

            try
            {
                using var stream = new MemoryStream(data);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                // Extract core properties
                await ExtractCorePropertiesAsync(archive, metadata, ct);

                // Extract slide content
                var slideCount = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith("ppt/slides/slide") &&
                        entry.FullName.EndsWith(".xml"))
                    {
                        ct.ThrowIfCancellationRequested();
                        slideCount++;

                        using var slideStream = entry.Open();
                        var slideDoc = await LoadXmlAsync(slideStream, ct);

                        // Extract text from text frames
                        var textElements = slideDoc.GetElementsByTagName("t", DrawingNs);
                        foreach (XmlNode textNode in textElements)
                        {
                            var text = textNode.InnerText.Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                if (textBuilder.Length > 0)
                                    textBuilder.Append(' ');
                                textBuilder.Append(text);
                            }
                        }
                    }
                }

                metadata["slideCount"] = slideCount;

                // Also extract from slide notes
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.StartsWith("ppt/notesSlides/") &&
                        entry.FullName.EndsWith(".xml"))
                    {
                        using var notesStream = entry.Open();
                        var notesDoc = await LoadXmlAsync(notesStream, ct);

                        var textElements = notesDoc.GetElementsByTagName("t", DrawingNs);
                        foreach (XmlNode textNode in textElements)
                        {
                            var text = textNode.InnerText.Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                textBuilder.Append(' ').Append(text);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                metadata["extractionError"] = ex.Message;
            }

            return textBuilder.ToString().Trim();
        }

        private async Task ExtractCorePropertiesAsync(
            ZipArchive archive,
            Dictionary<string, object> metadata,
            CancellationToken ct)
        {
            var coreEntry = archive.GetEntry("docProps/core.xml");
            if (coreEntry == null)
                return;

            using var coreStream = coreEntry.Open();
            var coreDoc = await LoadXmlAsync(coreStream, ct);

            // Extract Dublin Core elements
            var title = coreDoc.GetElementsByTagName("title", DcNs);
            if (title.Count > 0)
                metadata["title"] = title[0]!.InnerText;

            var creator = coreDoc.GetElementsByTagName("creator", DcNs);
            if (creator.Count > 0)
                metadata["author"] = creator[0]!.InnerText;

            var subject = coreDoc.GetElementsByTagName("subject", DcNs);
            if (subject.Count > 0)
                metadata["description"] = subject[0]!.InnerText;

            var created = coreDoc.GetElementsByTagName("created", "http://purl.org/dc/terms/");
            if (created.Count > 0 && DateTime.TryParse(created[0]!.InnerText, out var createdDate))
                metadata["created"] = createdDate;

            var modified = coreDoc.GetElementsByTagName("modified", "http://purl.org/dc/terms/");
            if (modified.Count > 0 && DateTime.TryParse(modified[0]!.InnerText, out var modifiedDate))
                metadata["modified"] = modifiedDate;

            var keywords = coreDoc.GetElementsByTagName("keywords", CorePropertiesNs);
            if (keywords.Count > 0)
            {
                metadata["keywords"] = keywords[0]!.InnerText
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .ToArray();
            }
        }

        private static async Task<XmlDocument> LoadXmlAsync(Stream stream, CancellationToken ct)
        {
            var doc = new XmlDocument();
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(stream, settings);
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
            }

            // Reset and load
            stream.Position = 0;
            doc.Load(stream);

            return doc;
        }

        private static string ExtractTextFromNode(XmlNode node)
        {
            var texts = new List<string>();

            void ExtractRecursive(XmlNode current)
            {
                if (current.Name.EndsWith(":t") || current.LocalName == "t")
                {
                    if (!string.IsNullOrEmpty(current.InnerText))
                        texts.Add(current.InnerText);
                }
                else
                {
                    foreach (XmlNode child in current.ChildNodes)
                    {
                        ExtractRecursive(child);
                    }
                }
            }

            ExtractRecursive(node);
            return string.Join("", texts);
        }

        private static bool IsNumeric(string value)
        {
            return double.TryParse(value, out _);
        }
    }

    #endregion
}
