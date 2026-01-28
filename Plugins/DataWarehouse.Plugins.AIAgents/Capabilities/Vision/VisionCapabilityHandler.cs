// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DataWarehouse.Plugins.AIAgents.Capabilities;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Vision;

// Type aliases for types defined in AIAgentPlugin.cs
using IAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;
using VisionRequest = DataWarehouse.Plugins.AIAgents.VisionRequest;
using UsageLimits = DataWarehouse.Plugins.AIAgents.UsageLimits;

/// <summary>
/// Handles visual understanding capabilities including image analysis, OCR,
/// document analysis, and diagram understanding.
/// </summary>
/// <remarks>
/// <para>
/// This handler implements the Vision capability domain with the following sub-capabilities:
/// </para>
/// <list type="bullet">
/// <item><description>ImageAnalysis - Describe and analyze image content</description></item>
/// <item><description>ImageGeneration - Generate images from text descriptions</description></item>
/// <item><description>OCR - Extract text from images</description></item>
/// <item><description>DocumentAnalysis - Analyze structured documents and forms</description></item>
/// <item><description>DiagramUnderstanding - Interpret diagrams, charts, and technical drawings</description></item>
/// </list>
/// </remarks>
public sealed class VisionCapabilityHandler : ICapabilityHandler
{
    private readonly Func<string, IAIProvider?> _providerResolver;
    private readonly VisionConfig _config;

    /// <summary>
    /// Image analysis capability identifier.
    /// </summary>
    public const string ImageAnalysis = "ImageAnalysis";

    /// <summary>
    /// Image generation capability identifier.
    /// </summary>
    public const string ImageGeneration = "ImageGeneration";

    /// <summary>
    /// OCR capability identifier.
    /// </summary>
    public const string OCR = "OCR";

    /// <summary>
    /// Document analysis capability identifier.
    /// </summary>
    public const string DocumentAnalysis = "DocumentAnalysis";

    /// <summary>
    /// Diagram understanding capability identifier.
    /// </summary>
    public const string DiagramUnderstanding = "DiagramUnderstanding";

    /// <inheritdoc/>
    public string CapabilityDomain => "Vision";

    /// <inheritdoc/>
    public string DisplayName => "Visual Understanding";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => new[]
    {
        ImageAnalysis,
        ImageGeneration,
        OCR,
        DocumentAnalysis,
        DiagramUnderstanding
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="VisionCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve providers by name.</param>
    /// <param name="config">Optional configuration settings.</param>
    public VisionCapabilityHandler(
        Func<string, IAIProvider?> providerResolver,
        VisionConfig? config = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _config = config ?? new VisionConfig();
    }

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability))
            return false;

        // Check full domain.capability
        var fullCapability = $"{CapabilityDomain}.{capability}";
        if (instanceConfig.DisabledCapabilities.Contains(fullCapability))
            return false;

        // Check quota tier restrictions
        var limits = UsageLimits.GetDefaultLimits(instanceConfig.QuotaTier);

        // Vision requires at least Pro tier
        if (!limits.VisionEnabled)
            return false;

        return true;
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var provider))
            return provider;

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out provider))
            return provider;

        // Default providers based on capability
        return capability switch
        {
            ImageGeneration => "openai", // DALL-E
            OCR => "google", // Good at document OCR
            DocumentAnalysis => "anthropic", // Claude excels at document understanding
            DiagramUnderstanding => "anthropic", // Claude excels at technical content
            _ => _config.DefaultProvider
        };
    }

    /// <summary>
    /// Analyzes an image and returns a description.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The image analysis request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The image analysis result.</returns>
    public async Task<CapabilityResult<ImageAnalysisResult>> AnalyzeImageAsync(
        InstanceCapabilityConfig instanceConfig,
        ImageAnalysisRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, ImageAnalysis))
            return CapabilityResult<ImageAnalysisResult>.Disabled(ImageAnalysis);

        if (string.IsNullOrEmpty(request.ImageUrl) && string.IsNullOrEmpty(request.ImageBase64))
            return CapabilityResult<ImageAnalysisResult>.Fail("Either ImageUrl or ImageBase64 must be provided.", "NO_IMAGE");

        var providerName = GetProviderForCapability(instanceConfig, ImageAnalysis);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<ImageAnalysisResult>.NoProvider(ImageAnalysis);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<ImageAnalysisResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (!provider.SupportsVision)
            return CapabilityResult<ImageAnalysisResult>.Fail($"Provider '{providerName}' does not support vision.", "VISION_NOT_SUPPORTED");

        try
        {
            var sw = Stopwatch.StartNew();

            var prompt = BuildAnalysisPrompt(request);

            var visionRequest = new VisionRequest
            {
                Model = request.Model ?? provider.DefaultVisionModel ?? _config.DefaultModel,
                ImageUrl = request.ImageUrl,
                ImageBase64 = request.ImageBase64,
                Prompt = prompt,
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens
            };

            var response = await provider.VisionAsync(visionRequest, ct);
            sw.Stop();

            // Parse structured response if requested
            var analysis = ParseAnalysisResponse(response.Content, request.OutputFormat);

            return CapabilityResult<ImageAnalysisResult>.Ok(
                new ImageAnalysisResult
                {
                    Description = analysis.Description,
                    Tags = analysis.Tags,
                    Objects = analysis.Objects,
                    Colors = analysis.Colors,
                    Text = analysis.Text,
                    Confidence = analysis.Confidence,
                    RawResponse = response.Content
                },
                providerName,
                visionRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<ImageAnalysisResult>.Fail(ex.Message, "IMAGE_ANALYSIS_ERROR");
        }
    }

    /// <summary>
    /// Extracts text from an image using OCR.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The OCR request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The OCR result.</returns>
    public async Task<CapabilityResult<OCRResult>> ExtractTextAsync(
        InstanceCapabilityConfig instanceConfig,
        OCRRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, OCR))
            return CapabilityResult<OCRResult>.Disabled(OCR);

        if (string.IsNullOrEmpty(request.ImageUrl) && string.IsNullOrEmpty(request.ImageBase64))
            return CapabilityResult<OCRResult>.Fail("Either ImageUrl or ImageBase64 must be provided.", "NO_IMAGE");

        var providerName = GetProviderForCapability(instanceConfig, OCR);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<OCRResult>.NoProvider(OCR);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<OCRResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (!provider.SupportsVision)
            return CapabilityResult<OCRResult>.Fail($"Provider '{providerName}' does not support vision.", "VISION_NOT_SUPPORTED");

        try
        {
            var sw = Stopwatch.StartNew();

            var prompt = BuildOCRPrompt(request);

            var visionRequest = new VisionRequest
            {
                Model = request.Model ?? provider.DefaultVisionModel ?? _config.DefaultModel,
                ImageUrl = request.ImageUrl,
                ImageBase64 = request.ImageBase64,
                Prompt = prompt,
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens
            };

            var response = await provider.VisionAsync(visionRequest, ct);
            sw.Stop();

            var ocrResult = ParseOCRResponse(response.Content, request.OutputFormat);

            return CapabilityResult<OCRResult>.Ok(
                ocrResult,
                providerName,
                visionRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<OCRResult>.Fail(ex.Message, "OCR_ERROR");
        }
    }

    /// <summary>
    /// Analyzes a document image (invoices, forms, etc.).
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The document analysis request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The document analysis result.</returns>
    public async Task<CapabilityResult<DocumentAnalysisResult>> AnalyzeDocumentAsync(
        InstanceCapabilityConfig instanceConfig,
        DocumentAnalysisRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, DocumentAnalysis))
            return CapabilityResult<DocumentAnalysisResult>.Disabled(DocumentAnalysis);

        if (string.IsNullOrEmpty(request.ImageUrl) && string.IsNullOrEmpty(request.ImageBase64))
            return CapabilityResult<DocumentAnalysisResult>.Fail("Either ImageUrl or ImageBase64 must be provided.", "NO_IMAGE");

        var providerName = GetProviderForCapability(instanceConfig, DocumentAnalysis);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<DocumentAnalysisResult>.NoProvider(DocumentAnalysis);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<DocumentAnalysisResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (!provider.SupportsVision)
            return CapabilityResult<DocumentAnalysisResult>.Fail($"Provider '{providerName}' does not support vision.", "VISION_NOT_SUPPORTED");

        try
        {
            var sw = Stopwatch.StartNew();

            var prompt = BuildDocumentAnalysisPrompt(request);

            var visionRequest = new VisionRequest
            {
                Model = request.Model ?? provider.DefaultVisionModel ?? _config.DefaultModel,
                ImageUrl = request.ImageUrl,
                ImageBase64 = request.ImageBase64,
                Prompt = prompt,
                MaxTokens = request.MaxTokens ?? 4096
            };

            var response = await provider.VisionAsync(visionRequest, ct);
            sw.Stop();

            var docResult = ParseDocumentAnalysisResponse(response.Content, request.DocumentType);

            return CapabilityResult<DocumentAnalysisResult>.Ok(
                docResult,
                providerName,
                visionRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<DocumentAnalysisResult>.Fail(ex.Message, "DOCUMENT_ANALYSIS_ERROR");
        }
    }

    /// <summary>
    /// Analyzes a diagram, chart, or technical drawing.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The diagram analysis request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The diagram analysis result.</returns>
    public async Task<CapabilityResult<DiagramAnalysisResult>> AnalyzeDiagramAsync(
        InstanceCapabilityConfig instanceConfig,
        DiagramAnalysisRequest request,
        CancellationToken ct = default)
    {
        if (!IsCapabilityEnabled(instanceConfig, DiagramUnderstanding))
            return CapabilityResult<DiagramAnalysisResult>.Disabled(DiagramUnderstanding);

        if (string.IsNullOrEmpty(request.ImageUrl) && string.IsNullOrEmpty(request.ImageBase64))
            return CapabilityResult<DiagramAnalysisResult>.Fail("Either ImageUrl or ImageBase64 must be provided.", "NO_IMAGE");

        var providerName = GetProviderForCapability(instanceConfig, DiagramUnderstanding);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<DiagramAnalysisResult>.NoProvider(DiagramUnderstanding);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<DiagramAnalysisResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (!provider.SupportsVision)
            return CapabilityResult<DiagramAnalysisResult>.Fail($"Provider '{providerName}' does not support vision.", "VISION_NOT_SUPPORTED");

        try
        {
            var sw = Stopwatch.StartNew();

            var prompt = BuildDiagramAnalysisPrompt(request);

            var visionRequest = new VisionRequest
            {
                Model = request.Model ?? provider.DefaultVisionModel ?? _config.DefaultModel,
                ImageUrl = request.ImageUrl,
                ImageBase64 = request.ImageBase64,
                Prompt = prompt,
                MaxTokens = request.MaxTokens ?? 4096
            };

            var response = await provider.VisionAsync(visionRequest, ct);
            sw.Stop();

            var diagramResult = ParseDiagramAnalysisResponse(response.Content, request.DiagramType);

            return CapabilityResult<DiagramAnalysisResult>.Ok(
                diagramResult,
                providerName,
                visionRequest.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<DiagramAnalysisResult>.Fail(ex.Message, "DIAGRAM_ANALYSIS_ERROR");
        }
    }

    #region Private Helpers

    private static string BuildAnalysisPrompt(ImageAnalysisRequest request)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(request.Prompt))
        {
            sb.AppendLine(request.Prompt);
        }
        else
        {
            sb.AppendLine("Analyze this image in detail.");
        }

        if (request.IncludeTags)
            sb.AppendLine("Include relevant tags/keywords.");

        if (request.IncludeObjects)
            sb.AppendLine("List all identifiable objects with their positions.");

        if (request.IncludeColors)
            sb.AppendLine("Describe the dominant colors.");

        if (request.IncludeText)
            sb.AppendLine("Extract any visible text.");

        if (request.OutputFormat == OutputFormat.JSON)
        {
            sb.AppendLine("\nRespond in JSON format with keys: description, tags, objects, colors, text.");
        }

        return sb.ToString();
    }

    private static string BuildOCRPrompt(OCRRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extract all text from this image.");

        if (request.Language != null)
            sb.AppendLine($"The text is primarily in {request.Language}.");

        if (request.PreserveLayout)
            sb.AppendLine("Preserve the original layout and formatting as much as possible.");

        if (request.OutputFormat == OutputFormat.JSON)
        {
            sb.AppendLine("\nRespond in JSON format with keys: text, blocks (array of text blocks with positions), confidence.");
        }

        return sb.ToString();
    }

    private static string BuildDocumentAnalysisPrompt(DocumentAnalysisRequest request)
    {
        var typePrompt = request.DocumentType switch
        {
            DocumentType.Invoice => "This is an invoice. Extract: vendor name, invoice number, date, line items, totals, tax, and payment details.",
            DocumentType.Receipt => "This is a receipt. Extract: store name, date, items purchased, amounts, and total.",
            DocumentType.Form => "This is a form. Extract all field labels and their filled values.",
            DocumentType.Contract => "This is a contract. Extract: parties, key terms, dates, and signature information.",
            DocumentType.IdDocument => "This is an ID document. Extract: name, ID number, dates, and other identifying information. Note: Do not expose sensitive data if not authorized.",
            DocumentType.BusinessCard => "This is a business card. Extract: name, title, company, phone, email, and address.",
            _ => "Analyze this document and extract all relevant information."
        };

        var sb = new StringBuilder();
        sb.AppendLine(typePrompt);

        if (request.ExtractTables)
            sb.AppendLine("Extract any tables in a structured format.");

        if (request.ExtractSignatures)
            sb.AppendLine("Note any signatures or signature fields.");

        if (request.OutputFormat == OutputFormat.JSON)
        {
            sb.AppendLine("\nRespond in JSON format with appropriate keys for the document type.");
        }

        return sb.ToString();
    }

    private static string BuildDiagramAnalysisPrompt(DiagramAnalysisRequest request)
    {
        var typePrompt = request.DiagramType switch
        {
            DiagramType.Flowchart => "This is a flowchart. Identify all shapes, their labels, and the flow of connections.",
            DiagramType.UML => "This is a UML diagram. Identify classes, relationships, methods, and attributes.",
            DiagramType.ERDiagram => "This is an ER diagram. Identify entities, attributes, and relationships.",
            DiagramType.NetworkDiagram => "This is a network diagram. Identify nodes, connections, and network topology.",
            DiagramType.Chart => "This is a chart/graph. Identify the type, axes, data series, and key values.",
            DiagramType.Architecture => "This is an architecture diagram. Identify components, layers, and integrations.",
            DiagramType.Wireframe => "This is a UI wireframe. Identify UI components, layout, and interactions.",
            DiagramType.Schematic => "This is a technical schematic. Identify components, connections, and specifications.",
            _ => "Analyze this diagram and describe its structure and meaning."
        };

        var sb = new StringBuilder();
        sb.AppendLine(typePrompt);

        if (request.ExtractDataValues)
            sb.AppendLine("Extract specific data values where visible.");

        if (request.DescribeRelationships)
            sb.AppendLine("Describe the relationships and connections between elements.");

        if (request.GenerateMermaid && request.DiagramType is DiagramType.Flowchart or DiagramType.UML or DiagramType.ERDiagram)
            sb.AppendLine("Also generate an equivalent Mermaid diagram code.");

        if (request.OutputFormat == OutputFormat.JSON)
        {
            sb.AppendLine("\nRespond in JSON format with appropriate keys for the diagram type.");
        }

        return sb.ToString();
    }

    private static ImageAnalysisResult ParseAnalysisResponse(string response, OutputFormat format)
    {
        if (format == OutputFormat.JSON)
        {
            try
            {
                return JsonSerializer.Deserialize<ImageAnalysisResult>(response)
                    ?? new ImageAnalysisResult { Description = response };
            }
            catch
            {
                // Fall through to plain text parsing
            }
        }

        return new ImageAnalysisResult
        {
            Description = response,
            RawResponse = response
        };
    }

    private static OCRResult ParseOCRResponse(string response, OutputFormat format)
    {
        if (format == OutputFormat.JSON)
        {
            try
            {
                return JsonSerializer.Deserialize<OCRResult>(response)
                    ?? new OCRResult { Text = response };
            }
            catch
            {
                // Fall through
            }
        }

        return new OCRResult
        {
            Text = response,
            RawResponse = response
        };
    }

    private static DocumentAnalysisResult ParseDocumentAnalysisResponse(string response, DocumentType type)
    {
        try
        {
            // Try to parse as JSON first
            var result = JsonSerializer.Deserialize<DocumentAnalysisResult>(response);
            if (result != null)
            {
                result.DocumentType = type;
                return result;
            }
        }
        catch
        {
            // Fall through
        }

        return new DocumentAnalysisResult
        {
            DocumentType = type,
            Summary = response,
            RawResponse = response
        };
    }

    private static DiagramAnalysisResult ParseDiagramAnalysisResponse(string response, DiagramType type)
    {
        try
        {
            var result = JsonSerializer.Deserialize<DiagramAnalysisResult>(response);
            if (result != null)
            {
                result.DiagramType = type;
                return result;
            }
        }
        catch
        {
            // Fall through
        }

        return new DiagramAnalysisResult
        {
            DiagramType = type,
            Description = response,
            RawResponse = response
        };
    }

    #endregion
}

#region Configuration

/// <summary>
/// Configuration for the vision capability handler.
/// </summary>
public sealed class VisionConfig
{
    /// <summary>Default provider for vision operations.</summary>
    public string DefaultProvider { get; init; } = "openai";

    /// <summary>Default model for vision operations.</summary>
    public string DefaultModel { get; init; } = "gpt-4o";

    /// <summary>Default maximum tokens for vision responses.</summary>
    public int DefaultMaxTokens { get; init; } = 2048;
}

/// <summary>
/// Output format for vision responses.
/// </summary>
public enum OutputFormat
{
    /// <summary>Plain text response.</summary>
    Text,
    /// <summary>Structured JSON response.</summary>
    JSON
}

#endregion

#region Request Types

/// <summary>
/// Request for image analysis.
/// </summary>
public sealed class ImageAnalysisRequest
{
    /// <summary>URL of the image to analyze.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Base64-encoded image data.</summary>
    public string? ImageBase64 { get; init; }

    /// <summary>Specific prompt for analysis.</summary>
    public string? Prompt { get; init; }

    /// <summary>Whether to include descriptive tags.</summary>
    public bool IncludeTags { get; init; } = true;

    /// <summary>Whether to detect and list objects.</summary>
    public bool IncludeObjects { get; init; }

    /// <summary>Whether to analyze colors.</summary>
    public bool IncludeColors { get; init; }

    /// <summary>Whether to extract visible text.</summary>
    public bool IncludeText { get; init; }

    /// <summary>Output format preference.</summary>
    public OutputFormat OutputFormat { get; init; } = OutputFormat.Text;

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Request for OCR text extraction.
/// </summary>
public sealed class OCRRequest
{
    /// <summary>URL of the image.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Base64-encoded image data.</summary>
    public string? ImageBase64 { get; init; }

    /// <summary>Expected language of text.</summary>
    public string? Language { get; init; }

    /// <summary>Whether to preserve original layout.</summary>
    public bool PreserveLayout { get; init; } = true;

    /// <summary>Output format preference.</summary>
    public OutputFormat OutputFormat { get; init; } = OutputFormat.Text;

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Request for document analysis.
/// </summary>
public sealed class DocumentAnalysisRequest
{
    /// <summary>URL of the document image.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Base64-encoded document image.</summary>
    public string? ImageBase64 { get; init; }

    /// <summary>Type of document to analyze.</summary>
    public DocumentType DocumentType { get; init; } = DocumentType.Unknown;

    /// <summary>Whether to extract tables.</summary>
    public bool ExtractTables { get; init; } = true;

    /// <summary>Whether to identify signatures.</summary>
    public bool ExtractSignatures { get; init; }

    /// <summary>Output format preference.</summary>
    public OutputFormat OutputFormat { get; init; } = OutputFormat.JSON;

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Type of document for analysis.
/// </summary>
public enum DocumentType
{
    /// <summary>Unknown document type.</summary>
    Unknown,
    /// <summary>Invoice document.</summary>
    Invoice,
    /// <summary>Receipt.</summary>
    Receipt,
    /// <summary>Form or application.</summary>
    Form,
    /// <summary>Contract or agreement.</summary>
    Contract,
    /// <summary>ID document (license, passport).</summary>
    IdDocument,
    /// <summary>Business card.</summary>
    BusinessCard
}

/// <summary>
/// Request for diagram analysis.
/// </summary>
public sealed class DiagramAnalysisRequest
{
    /// <summary>URL of the diagram image.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Base64-encoded diagram image.</summary>
    public string? ImageBase64 { get; init; }

    /// <summary>Type of diagram.</summary>
    public DiagramType DiagramType { get; init; } = DiagramType.Unknown;

    /// <summary>Whether to extract data values from charts.</summary>
    public bool ExtractDataValues { get; init; } = true;

    /// <summary>Whether to describe relationships between elements.</summary>
    public bool DescribeRelationships { get; init; } = true;

    /// <summary>Whether to generate Mermaid code for the diagram.</summary>
    public bool GenerateMermaid { get; init; }

    /// <summary>Output format preference.</summary>
    public OutputFormat OutputFormat { get; init; } = OutputFormat.JSON;

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>
/// Type of diagram for analysis.
/// </summary>
public enum DiagramType
{
    /// <summary>Unknown diagram type.</summary>
    Unknown,
    /// <summary>Flowchart diagram.</summary>
    Flowchart,
    /// <summary>UML diagram.</summary>
    UML,
    /// <summary>Entity-Relationship diagram.</summary>
    ERDiagram,
    /// <summary>Network topology diagram.</summary>
    NetworkDiagram,
    /// <summary>Chart or graph.</summary>
    Chart,
    /// <summary>Architecture diagram.</summary>
    Architecture,
    /// <summary>UI wireframe.</summary>
    Wireframe,
    /// <summary>Technical schematic.</summary>
    Schematic
}

#endregion

#region Result Types

/// <summary>
/// Result of image analysis.
/// </summary>
public sealed class ImageAnalysisResult
{
    /// <summary>Description of the image.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Tags/keywords for the image.</summary>
    public List<string>? Tags { get; init; }

    /// <summary>Detected objects with positions.</summary>
    public List<DetectedObject>? Objects { get; init; }

    /// <summary>Dominant colors in the image.</summary>
    public List<string>? Colors { get; init; }

    /// <summary>Text detected in the image.</summary>
    public string? Text { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Raw response from the provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// A detected object in an image.
/// </summary>
public sealed class DetectedObject
{
    /// <summary>Name/label of the object.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Confidence score (0-1).</summary>
    public double Confidence { get; init; }

    /// <summary>Bounding box (x, y, width, height) as percentages.</summary>
    public double[]? BoundingBox { get; init; }
}

/// <summary>
/// Result of OCR text extraction.
/// </summary>
public sealed class OCRResult
{
    /// <summary>Extracted text content.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Text blocks with position information.</summary>
    public List<TextBlock>? Blocks { get; init; }

    /// <summary>Overall confidence score.</summary>
    public double? Confidence { get; init; }

    /// <summary>Detected language.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Raw response from the provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// A block of text with position information.
/// </summary>
public sealed class TextBlock
{
    /// <summary>Text content of the block.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Confidence score (0-1).</summary>
    public double Confidence { get; init; }

    /// <summary>Bounding box (x, y, width, height) as percentages.</summary>
    public double[]? BoundingBox { get; init; }
}

/// <summary>
/// Result of document analysis.
/// </summary>
public sealed class DocumentAnalysisResult
{
    /// <summary>Type of document analyzed.</summary>
    public DocumentType DocumentType { get; set; }

    /// <summary>Summary of the document.</summary>
    public string? Summary { get; init; }

    /// <summary>Extracted fields as key-value pairs.</summary>
    public Dictionary<string, object>? Fields { get; init; }

    /// <summary>Extracted tables.</summary>
    public List<ExtractedTable>? Tables { get; init; }

    /// <summary>Whether signatures were detected.</summary>
    public bool HasSignatures { get; init; }

    /// <summary>Confidence score (0-1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Raw response from the provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// An extracted table from a document.
/// </summary>
public sealed class ExtractedTable
{
    /// <summary>Table headers.</summary>
    public List<string>? Headers { get; init; }

    /// <summary>Table rows (each row is a list of cell values).</summary>
    public List<List<string>>? Rows { get; init; }

    /// <summary>Table title if detected.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Result of diagram analysis.
/// </summary>
public sealed class DiagramAnalysisResult
{
    /// <summary>Type of diagram analyzed.</summary>
    public DiagramType DiagramType { get; set; }

    /// <summary>Description of the diagram.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Elements/nodes in the diagram.</summary>
    public List<DiagramElement>? Elements { get; init; }

    /// <summary>Connections/relationships between elements.</summary>
    public List<DiagramConnection>? Connections { get; init; }

    /// <summary>Data values extracted from charts.</summary>
    public Dictionary<string, object>? DataValues { get; init; }

    /// <summary>Generated Mermaid code if requested.</summary>
    public string? MermaidCode { get; init; }

    /// <summary>Raw response from the provider.</summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// An element/node in a diagram.
/// </summary>
public sealed class DiagramElement
{
    /// <summary>Element identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Element label/name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Element type (shape, class, entity, etc.).</summary>
    public string? Type { get; init; }

    /// <summary>Properties of the element.</summary>
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// A connection/relationship in a diagram.
/// </summary>
public sealed class DiagramConnection
{
    /// <summary>Source element ID.</summary>
    public string FromId { get; init; } = string.Empty;

    /// <summary>Target element ID.</summary>
    public string ToId { get; init; } = string.Empty;

    /// <summary>Connection label.</summary>
    public string? Label { get; init; }

    /// <summary>Connection type.</summary>
    public string? Type { get; init; }
}

#endregion
