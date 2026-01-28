using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using System.Text;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Analyzers
{
    /// <summary>
    /// Image content analyzer using AI vision capabilities.
    /// Extracts text (OCR), descriptions, and semantic information from images.
    /// </summary>
    public sealed class ImageAnalyzer : IContentAnalyzer
    {
        private readonly IAIProvider? _aiProvider;

        public string[] SupportedContentTypes => new[]
        {
            "image/jpeg",
            "image/png",
            "image/gif",
            "image/webp",
            "image/bmp",
            "image/tiff"
        };

        public ImageAnalyzer(IAIProvider? aiProvider = null)
        {
            _aiProvider = aiProvider;
        }

        public bool CanAnalyze(string contentType)
        {
            var normalizedType = NormalizeContentType(contentType);
            return SupportedContentTypes.Any(t =>
                normalizedType.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                normalizedType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<string> ExtractTextAsync(Stream content, string contentType, CancellationToken ct = default)
        {
            // Check if AI provider supports image analysis
            if (_aiProvider == null || !_aiProvider.IsAvailable ||
                !_aiProvider.Capabilities.HasFlag(AICapabilities.ImageAnalysis))
            {
                // Fall back to basic image metadata extraction
                return await ExtractImageMetadataAsync(content, ct);
            }

            // Use AI to extract text and description
            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            var imageBytes = memoryStream.ToArray();
            var base64Image = Convert.ToBase64String(imageBytes);

            var request = new AIRequest
            {
                Prompt = "Please extract all visible text from this image and provide a brief description of what the image shows. Format your response as: TEXT: [extracted text] DESCRIPTION: [image description]",
                SystemMessage = "You are an OCR and image analysis system. Extract text accurately and describe images concisely.",
                ExtendedParameters = new Dictionary<string, object>
                {
                    ["image"] = base64Image,
                    ["image_content_type"] = contentType
                }
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (response.Success)
            {
                return ParseImageAnalysisResponse(response.Content);
            }

            return string.Empty;
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
                // Read image into memory
                using var memoryStream = new MemoryStream();
                await content.CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;

                var metadata = new Dictionary<string, object>
                {
                    ["contentType"] = contentType,
                    ["imageSize"] = memoryStream.Length
                };

                // Extract basic image information
                var imageInfo = ExtractBasicImageInfo(memoryStream.ToArray());
                foreach (var (key, value) in imageInfo)
                {
                    metadata[key] = value;
                }

                memoryStream.Position = 0;

                // Extract text using AI if available
                string text = string.Empty;
                if (options.ExtractText)
                {
                    text = await ExtractTextAsync(memoryStream, contentType, ct);
                }

                // Generate image description and analysis if AI is available
                string? description = null;
                List<string> detectedObjects = new();
                float[]? embeddings = null;

                if (_aiProvider != null && _aiProvider.IsAvailable)
                {
                    memoryStream.Position = 0;
                    var analysisResult = await AnalyzeImageWithAIAsync(memoryStream, contentType, options, ct);

                    description = analysisResult.Description;
                    detectedObjects = analysisResult.DetectedObjects;
                    embeddings = analysisResult.Embeddings;

                    metadata["aiAnalyzed"] = true;
                    metadata["description"] = description ?? "";
                    metadata["detectedObjects"] = detectedObjects;
                }

                sw.Stop();

                return new ContentAnalysisResult
                {
                    Text = text,
                    DetectedContentType = contentType,
                    Embeddings = embeddings,
                    Keywords = detectedObjects.Select(o => new WeightedKeyword
                    {
                        Keyword = o,
                        Weight = 0.8f,
                        Frequency = 1
                    }).ToList(),
                    Success = true,
                    Duration = sw.Elapsed,
                    Metadata = metadata,
                    SummaryResult = description != null ? new SummarizationResult
                    {
                        Summary = description,
                        Type = SummarizationType.Abstractive,
                        UsedAI = true,
                        Duration = sw.Elapsed
                    } : null
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ContentAnalysisResult
                {
                    Success = false,
                    ErrorMessage = $"Image analysis failed: {ex.Message}",
                    Duration = sw.Elapsed
                };
            }
        }

        private async Task<string> ExtractImageMetadataAsync(Stream content, CancellationToken ct)
        {
            // Basic metadata extraction without AI
            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            var data = memoryStream.ToArray();

            var metadata = ExtractBasicImageInfo(data);
            var sb = new StringBuilder();

            foreach (var (key, value) in metadata)
            {
                sb.Append($"{key}: {value} ");
            }

            return sb.ToString().Trim();
        }

        private Dictionary<string, object> ExtractBasicImageInfo(byte[] data)
        {
            var info = new Dictionary<string, object>();

            if (data.Length < 8)
                return info;

            // Detect image format and dimensions
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                // JPEG
                info["format"] = "JPEG";
                var dims = ExtractJpegDimensions(data);
                if (dims.HasValue)
                {
                    info["width"] = dims.Value.Width;
                    info["height"] = dims.Value.Height;
                }
            }
            else if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                // PNG
                info["format"] = "PNG";
                if (data.Length > 24)
                {
                    info["width"] = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                    info["height"] = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
                }
            }
            else if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            {
                // GIF
                info["format"] = "GIF";
                if (data.Length > 10)
                {
                    info["width"] = data[6] | (data[7] << 8);
                    info["height"] = data[8] | (data[9] << 8);
                }
            }
            else if (data[0] == 0x42 && data[1] == 0x4D)
            {
                // BMP
                info["format"] = "BMP";
                if (data.Length > 26)
                {
                    info["width"] = data[18] | (data[19] << 8) | (data[20] << 16) | (data[21] << 24);
                    info["height"] = data[22] | (data[23] << 8) | (data[24] << 16) | (data[25] << 24);
                }
            }

            info["fileSize"] = data.Length;

            return info;
        }

        private (int Width, int Height)? ExtractJpegDimensions(byte[] data)
        {
            int i = 2;
            while (i < data.Length - 8)
            {
                if (data[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = data[i + 1];

                // SOF markers (Start of Frame)
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    if (i + 9 < data.Length)
                    {
                        var height = (data[i + 5] << 8) | data[i + 6];
                        var width = (data[i + 7] << 8) | data[i + 8];
                        return (width, height);
                    }
                }

                // Skip to next marker
                if (marker == 0xD8 || marker == 0xD9 || marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                }
                else if (i + 4 < data.Length)
                {
                    var length = (data[i + 2] << 8) | data[i + 3];
                    i += 2 + length;
                }
                else
                {
                    break;
                }
            }

            return null;
        }

        private async Task<ImageAIAnalysisResult> AnalyzeImageWithAIAsync(
            Stream content,
            string contentType,
            ContentAnalysisOptions options,
            CancellationToken ct)
        {
            if (_aiProvider == null || !_aiProvider.IsAvailable)
            {
                return new ImageAIAnalysisResult();
            }

            using var memoryStream = new MemoryStream();
            await content.CopyToAsync(memoryStream, ct);
            var imageBytes = memoryStream.ToArray();
            var base64Image = Convert.ToBase64String(imageBytes);

            // Request comprehensive image analysis
            var request = new AIRequest
            {
                Prompt = @"Analyze this image and provide:
1. A detailed description (2-3 sentences)
2. List of main objects/elements visible
3. Any text visible in the image
4. Overall category (photo, diagram, screenshot, document, etc.)

Format your response as:
DESCRIPTION: [description]
OBJECTS: [comma-separated list]
TEXT: [extracted text or 'none']
CATEGORY: [category]",
                SystemMessage = "You are an image analysis system. Provide accurate, concise analysis.",
                ExtendedParameters = new Dictionary<string, object>
                {
                    ["image"] = base64Image,
                    ["image_content_type"] = contentType
                }
            };

            var response = await _aiProvider.CompleteAsync(request, ct);

            if (!response.Success)
            {
                return new ImageAIAnalysisResult();
            }

            var result = ParseDetailedImageAnalysis(response.Content);

            // Generate embeddings if requested
            if (options.GenerateEmbeddings && _aiProvider.Capabilities.HasFlag(AICapabilities.Embeddings))
            {
                var textForEmbedding = $"{result.Description} {string.Join(" ", result.DetectedObjects)}";
                if (!string.IsNullOrEmpty(result.ExtractedText))
                {
                    textForEmbedding += " " + result.ExtractedText;
                }

                result.Embeddings = await _aiProvider.GetEmbeddingsAsync(textForEmbedding, ct);
            }

            return result;
        }

        private string ParseImageAnalysisResponse(string response)
        {
            var textMatch = System.Text.RegularExpressions.Regex.Match(response, @"TEXT:\s*(.+?)(?:DESCRIPTION:|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
            var descMatch = System.Text.RegularExpressions.Regex.Match(response, @"DESCRIPTION:\s*(.+?)$", System.Text.RegularExpressions.RegexOptions.Singleline);

            var result = new StringBuilder();

            if (textMatch.Success && textMatch.Groups[1].Value.Trim().ToLower() != "none")
            {
                result.Append(textMatch.Groups[1].Value.Trim());
            }

            if (descMatch.Success)
            {
                if (result.Length > 0)
                    result.Append(" ");
                result.Append(descMatch.Groups[1].Value.Trim());
            }

            return result.ToString();
        }

        private ImageAIAnalysisResult ParseDetailedImageAnalysis(string response)
        {
            var result = new ImageAIAnalysisResult();

            var descMatch = System.Text.RegularExpressions.Regex.Match(response, @"DESCRIPTION:\s*(.+?)(?:OBJECTS:|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (descMatch.Success)
            {
                result.Description = descMatch.Groups[1].Value.Trim();
            }

            var objectsMatch = System.Text.RegularExpressions.Regex.Match(response, @"OBJECTS:\s*(.+?)(?:TEXT:|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (objectsMatch.Success)
            {
                result.DetectedObjects = objectsMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(o => o.Trim())
                    .Where(o => !string.IsNullOrEmpty(o))
                    .ToList();
            }

            var textMatch = System.Text.RegularExpressions.Regex.Match(response, @"TEXT:\s*(.+?)(?:CATEGORY:|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (textMatch.Success)
            {
                var text = textMatch.Groups[1].Value.Trim();
                if (!text.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    result.ExtractedText = text;
                }
            }

            var categoryMatch = System.Text.RegularExpressions.Regex.Match(response, @"CATEGORY:\s*(.+?)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (categoryMatch.Success)
            {
                result.Category = categoryMatch.Groups[1].Value.Trim();
            }

            return result;
        }

        private static string NormalizeContentType(string contentType)
        {
            var idx = contentType.IndexOf(';');
            return idx >= 0 ? contentType[..idx].Trim().ToLowerInvariant() : contentType.Trim().ToLowerInvariant();
        }

        private sealed class ImageAIAnalysisResult
        {
            public string? Description { get; set; }
            public List<string> DetectedObjects { get; set; } = new();
            public string? ExtractedText { get; set; }
            public string? Category { get; set; }
            public float[]? Embeddings { get; set; }
        }
    }
}
