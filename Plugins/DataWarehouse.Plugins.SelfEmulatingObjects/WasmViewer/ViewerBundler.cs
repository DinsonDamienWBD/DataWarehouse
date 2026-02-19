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
    /// Minimal WASM viewer modules with render export. In deployment, these are
    /// replaced with full viewers compiled from source (Rust/AssemblyScript).
    /// </summary>
    private static Dictionary<string, ViewerInfo> InitializeViewerLibrary()
    {
        return new Dictionary<string, ViewerInfo>
        {
            ["pdf"] = new ViewerInfo
            {
                Format = "pdf",
                ViewerName = "PDFViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("pdf-viewer"),
                Description = "PDF viewer built with pdf.js compiled to WASM",
                SupportedFeatures = new[] { "page-navigation", "zoom", "search", "text-selection" }
            },
            ["png"] = new ViewerInfo
            {
                Format = "png",
                ViewerName = "ImageViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("image-viewer"),
                Description = "PNG image viewer with zoom and pan",
                SupportedFeatures = new[] { "zoom", "pan", "rotate", "metadata-display" }
            },
            ["jpeg"] = new ViewerInfo
            {
                Format = "jpeg",
                ViewerName = "ImageViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("image-viewer"),
                Description = "JPEG image viewer with EXIF support",
                SupportedFeatures = new[] { "zoom", "pan", "rotate", "exif-display" }
            },
            ["gif"] = new ViewerInfo
            {
                Format = "gif",
                ViewerName = "ImageViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("image-viewer"),
                Description = "GIF viewer with animation support",
                SupportedFeatures = new[] { "zoom", "pan", "animation-control" }
            },
            ["html"] = new ViewerInfo
            {
                Format = "html",
                ViewerName = "HTMLViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("html-viewer"),
                Description = "Sandboxed HTML viewer",
                SupportedFeatures = new[] { "render", "sanitize", "css-support" }
            },
            ["json"] = new ViewerInfo
            {
                Format = "json",
                ViewerName = "JSONViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("json-viewer"),
                Description = "JSON viewer with syntax highlighting and tree view",
                SupportedFeatures = new[] { "syntax-highlighting", "tree-view", "search", "pretty-print" }
            },
            ["mp4"] = new ViewerInfo
            {
                Format = "mp4",
                ViewerName = "VideoViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("video-viewer"),
                Description = "MP4 video viewer",
                SupportedFeatures = new[] { "play", "pause", "seek", "speed-control" }
            },
            ["binary"] = new ViewerInfo
            {
                Format = "binary",
                ViewerName = "HexViewer",
                Version = "1.0.0",
                WasmBytes = GenerateMinimalWasmModule("hex-viewer"),
                Description = "Hexadecimal viewer for binary data",
                SupportedFeatures = new[] { "hex-display", "ascii-display", "search", "goto-offset" }
            }
        };
    }

    /// <summary>
    /// Generates a minimal valid WASM module with a render export function.
    /// The module contains: type section (function signature), function section,
    /// export section ("render"), code section (returns 0/success), and a custom
    /// section with viewer type metadata.
    /// </summary>
    private static byte[] GenerateMinimalWasmModule(string viewerType)
    {
        var wasm = new List<byte>();

        // WASM magic number: \0asm
        wasm.AddRange(new byte[] { 0x00, 0x61, 0x73, 0x6D });
        // WASM version: 1
        wasm.AddRange(new byte[] { 0x01, 0x00, 0x00, 0x00 });

        // --- Section 1: Type section ---
        // Defines function type: (i32, i32) -> i32
        // func_type = 0x60, param_count=2, i32=0x7F, i32=0x7F, result_count=1, i32=0x7F
        var typeSection = new byte[]
        {
            0x01,       // 1 type entry
            0x60,       // func type marker
            0x02,       // 2 params
            0x7F, 0x7F, // i32, i32
            0x01,       // 1 result
            0x7F        // i32
        };
        wasm.Add(0x01); // section ID: Type
        WriteLeb128(wasm, typeSection.Length);
        wasm.AddRange(typeSection);

        // --- Section 3: Function section ---
        // Declares 1 function using type index 0
        var funcSection = new byte[]
        {
            0x01, // 1 function
            0x00  // type index 0
        };
        wasm.Add(0x03); // section ID: Function
        WriteLeb128(wasm, funcSection.Length);
        wasm.AddRange(funcSection);

        // --- Section 7: Export section ---
        // Exports function 0 as "render"
        var exportName = System.Text.Encoding.UTF8.GetBytes("render");
        var exportSection = new List<byte>();
        exportSection.Add(0x01); // 1 export
        exportSection.Add((byte)exportName.Length);
        exportSection.AddRange(exportName);
        exportSection.Add(0x00); // export kind: function
        exportSection.Add(0x00); // function index 0
        wasm.Add(0x07); // section ID: Export
        WriteLeb128(wasm, exportSection.Count);
        wasm.AddRange(exportSection);

        // --- Section 10: Code section ---
        // Function body: return i32.const 0 (success)
        var funcBody = new byte[]
        {
            0x00,       // local decl count = 0
            0x41, 0x00, // i32.const 0
            0x0B        // end
        };
        var codeSection = new List<byte>();
        codeSection.Add(0x01); // 1 function body
        codeSection.Add((byte)funcBody.Length); // body size
        codeSection.AddRange(funcBody);
        wasm.Add(0x0A); // section ID: Code
        WriteLeb128(wasm, codeSection.Count);
        wasm.AddRange(codeSection);

        // --- Section 0: Custom section (viewer metadata) ---
        var metaName = System.Text.Encoding.UTF8.GetBytes($"viewer:{viewerType}");
        var customSection = new List<byte>();
        customSection.Add((byte)metaName.Length);
        customSection.AddRange(metaName);
        wasm.Add(0x00); // section ID: Custom
        WriteLeb128(wasm, customSection.Count);
        wasm.AddRange(customSection);

        return wasm.ToArray();
    }

    /// <summary>
    /// Writes an unsigned integer as LEB128 encoding.
    /// </summary>
    private static void WriteLeb128(List<byte> output, int value)
    {
        var remaining = (uint)value;
        do
        {
            var b = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (remaining != 0)
                b |= 0x80;
            output.Add(b);
        } while (remaining != 0);
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
