using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.SelfEmulatingObjects.WasmViewer;

/// <summary>
/// 86.1: Viewer Bundler - combines data with WASM viewer into self-contained object.
/// 86.3: Viewer Library - pre-built WASM viewers for common formats.
/// 86.7: Metadata Preservation - stores format metadata alongside data.
/// 86.8: Viewer Versioning - tracks viewer versions for reproducibility.
/// </summary>
public sealed class ViewerBundler
{
    private readonly IKernelContext _context;
    private readonly IMessageBus _messageBus;
    private readonly Dictionary<string, ViewerInfo> _viewerLibrary;

    public ViewerBundler(IKernelContext context, IMessageBus messageBus)
    {
        _context = context;
        _messageBus = messageBus;
        _viewerLibrary = InitializeViewerLibrary();
    }

    /// <summary>
    /// 86.3: Pre-built WASM viewers for common formats.
    /// </summary>
    private static Dictionary<string, ViewerInfo> InitializeViewerLibrary()
    {
        // In production, these would be actual compiled .wasm files.
        // For this implementation, we define viewer metadata and placeholders.
        // Real WASM viewers would be built from Rust/AssemblyScript/C++ sources.

        return new Dictionary<string, ViewerInfo>
        {
            ["pdf"] = new ViewerInfo
            {
                Format = "pdf",
                ViewerName = "PDFViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("pdf-viewer"),
                Description = "PDF viewer built with pdf.js compiled to WASM",
                SupportedFeatures = new[] { "page-navigation", "zoom", "search", "text-selection" }
            },
            ["png"] = new ViewerInfo
            {
                Format = "png",
                ViewerName = "ImageViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("image-viewer"),
                Description = "PNG image viewer with zoom and pan",
                SupportedFeatures = new[] { "zoom", "pan", "rotate", "metadata-display" }
            },
            ["jpeg"] = new ViewerInfo
            {
                Format = "jpeg",
                ViewerName = "ImageViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("image-viewer"),
                Description = "JPEG image viewer with EXIF support",
                SupportedFeatures = new[] { "zoom", "pan", "rotate", "exif-display" }
            },
            ["gif"] = new ViewerInfo
            {
                Format = "gif",
                ViewerName = "ImageViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("image-viewer"),
                Description = "GIF viewer with animation support",
                SupportedFeatures = new[] { "zoom", "pan", "animation-control" }
            },
            ["html"] = new ViewerInfo
            {
                Format = "html",
                ViewerName = "HTMLViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("html-viewer"),
                Description = "Sandboxed HTML viewer",
                SupportedFeatures = new[] { "render", "sanitize", "css-support" }
            },
            ["json"] = new ViewerInfo
            {
                Format = "json",
                ViewerName = "JSONViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("json-viewer"),
                Description = "JSON viewer with syntax highlighting and tree view",
                SupportedFeatures = new[] { "syntax-highlighting", "tree-view", "search", "pretty-print" }
            },
            ["mp4"] = new ViewerInfo
            {
                Format = "mp4",
                ViewerName = "VideoViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("video-viewer"),
                Description = "MP4 video viewer",
                SupportedFeatures = new[] { "play", "pause", "seek", "speed-control" }
            },
            ["binary"] = new ViewerInfo
            {
                Format = "binary",
                ViewerName = "HexViewer",
                Version = "1.0.0",
                WasmBytes = GeneratePlaceholderWasm("hex-viewer"),
                Description = "Hexadecimal viewer for binary data",
                SupportedFeatures = new[] { "hex-display", "ascii-display", "search", "goto-offset" }
            }
        };
    }

    /// <summary>
    /// Generates a placeholder WASM module for a viewer.
    /// In production, this would load actual compiled WASM files.
    /// </summary>
    private static byte[] GeneratePlaceholderWasm(string viewerType)
    {
        // WASM magic number: \0asm
        var wasm = new List<byte> { 0x00, 0x61, 0x73, 0x6D };

        // WASM version: 1
        wasm.AddRange(BitConverter.GetBytes(1u));

        // Add a custom section with viewer type identifier
        // Section ID 0 (custom section)
        wasm.Add(0x00);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes($"viewer:{viewerType}");
        var sectionSize = 1 + nameBytes.Length; // name length + name
        wasm.Add((byte)sectionSize);
        wasm.Add((byte)nameBytes.Length);
        wasm.AddRange(nameBytes);

        return wasm.ToArray();
    }

    /// <summary>
    /// 86.1: Bundles data with appropriate WASM viewer.
    /// 86.7: Preserves metadata alongside data.
    /// 86.8: Tracks viewer version.
    /// </summary>
    public Task<SelfEmulatingObject> BundleWithViewerAsync(byte[] data, string format)
    {
        if (!_viewerLibrary.TryGetValue(format, out var viewerInfo))
        {
            _context.LogWarning($"No viewer found for format {format}, using binary viewer");
            viewerInfo = _viewerLibrary["binary"];
        }

        var bundled = new SelfEmulatingObject
        {
            Id = Guid.NewGuid().ToString("N"),
            Data = data,
            ViewerWasm = viewerInfo.WasmBytes,
            Format = format,
            ViewerName = viewerInfo.ViewerName,
            ViewerVersion = viewerInfo.Version,
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>
            {
                ["format"] = format,
                ["size"] = data.Length.ToString(),
                ["bundledAt"] = DateTime.UtcNow.ToString("O"),
                ["viewerName"] = viewerInfo.ViewerName,
                ["viewerVersion"] = viewerInfo.Version,
                ["viewerDescription"] = viewerInfo.Description,
                ["supportedFeatures"] = string.Join(",", viewerInfo.SupportedFeatures),
                ["formatSignature"] = GetFormatSignature(data)
            }
        };

        _context.LogInfo($"Bundled {data.Length} bytes with {viewerInfo.ViewerName} v{viewerInfo.Version}");
        return Task.FromResult(bundled);
    }

    /// <summary>
    /// Extracts format signature (first 16 bytes in hex) for verification.
    /// </summary>
    private static string GetFormatSignature(byte[] data)
    {
        var signatureLength = Math.Min(16, data.Length);
        var signature = new byte[signatureLength];
        Array.Copy(data, signature, signatureLength);
        return Convert.ToHexString(signature);
    }

    /// <summary>
    /// Gets information about available viewers.
    /// </summary>
    public IReadOnlyDictionary<string, ViewerInfo> GetAvailableViewers()
    {
        return _viewerLibrary;
    }
}

/// <summary>
/// Information about a WASM viewer.
/// </summary>
public sealed class ViewerInfo
{
    public required string Format { get; init; }
    public required string ViewerName { get; init; }
    public required string Version { get; init; }
    public required byte[] WasmBytes { get; init; }
    public required string Description { get; init; }
    public required string[] SupportedFeatures { get; init; }
}

/// <summary>
/// 86.6: Self-emulating object with data and embedded viewer.
/// 86.7: Includes comprehensive metadata.
/// </summary>
public sealed class SelfEmulatingObject
{
    public required string Id { get; init; }
    public required byte[] Data { get; init; }
    public required byte[] ViewerWasm { get; init; }
    public required string Format { get; init; }
    public required string ViewerName { get; init; }
    public required string ViewerVersion { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required Dictionary<string, string> Metadata { get; init; }
}
