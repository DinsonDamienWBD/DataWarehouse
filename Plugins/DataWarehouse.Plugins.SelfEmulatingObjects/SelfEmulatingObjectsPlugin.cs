using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.SelfEmulatingObjects;

/// <summary>
/// Self-Emulating Objects Plugin - T86
///
/// Bundles data with WASM viewers for long-term format preservation.
/// Objects are self-contained and viewable decades later without external software.
///
/// Sub-tasks implemented:
/// - 86.1: Viewer Bundler (combine data + WASM viewer)
/// - 86.2: Format Detector (auto-detect format and select viewer)
/// - 86.3: Viewer Library (pre-built WASM viewers for common formats)
/// - 86.4: Viewer Runtime (execute WASM viewers in sandbox)
/// - 86.5: Security Sandbox (isolate execution with limits)
/// - 86.6: Viewer API (standard interface for viewers)
/// - 86.7: Metadata Preservation (store format metadata)
/// - 86.8: Viewer Versioning (track viewer versions)
/// </summary>
public sealed class SelfEmulatingObjectsPlugin : FeaturePluginBase
{
    private IKernelContext? _context;
    private WasmViewer.ViewerBundler? _bundler;
    private WasmViewer.ViewerRuntime? _runtime;

    public override string Id => "com.datawarehouse.selfemulating";
    public override string Name => "Self-Emulating Objects";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;
        _bundler = new WasmViewer.ViewerBundler(_context!, MessageBus!);
        _runtime = new WasmViewer.ViewerRuntime(_context!, MessageBus!);

        // Subscribe to object creation events
        MessageBus!.Subscribe("selfemulating.bundle", HandleBundleRequestAsync);
        MessageBus!.Subscribe("selfemulating.view", HandleViewRequestAsync);

        _context?.LogInfo("Self-Emulating Objects plugin initialized");
        return await base.OnHandshakeAsync(request);
    }

    /// <summary>
    /// Handles requests to bundle data with a viewer.
    /// </summary>
    private async Task HandleBundleRequestAsync(PluginMessage message)
    {
        if (_bundler == null)
        {
            _context?.LogError("Bundler not initialized");
            return;
        }

        if (message.Payload.TryGetValue("data", out var dataObj))
        {
            var data = dataObj as byte[] ?? Array.Empty<byte>();
            var format = message.Payload.TryGetValue("format", out var formatObj)
                ? formatObj as string
                : null;

            // 86.2: Auto-detect format if not provided
            if (string.IsNullOrEmpty(format))
            {
                format = DetectFormat(data);
            }

            // 86.1: Bundle data with viewer
            var bundled = await _bundler.BundleWithViewerAsync(data, format!);

            // Send bundled object back via message bus
            await MessageBus!.PublishAsync("selfemulating.bundled", new PluginMessage
            {
                Type = "selfemulating.bundled",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["bundledObject"] = bundled,
                    ["format"] = format!,
                    ["size"] = data.Length
                }
            });

            _context?.LogInfo($"Bundled {data.Length} bytes as {format} format");
        }
    }

    /// <summary>
    /// Handles requests to view a bundled object.
    /// </summary>
    private async Task HandleViewRequestAsync(PluginMessage message)
    {
        if (_runtime == null)
        {
            _context?.LogError("Runtime not initialized");
            return;
        }

        if (message.Payload.TryGetValue("bundledObject", out var bundledObj) &&
            bundledObj is WasmViewer.SelfEmulatingObject bundled)
        {
            // 86.4: Execute viewer in sandboxed environment
            var output = await _runtime.ExecuteViewerAsync(bundled);

            // Send viewing result back
            await MessageBus!.PublishAsync("selfemulating.viewed", new PluginMessage
            {
                Type = "selfemulating.viewed",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["output"] = output,
                    ["format"] = bundled.Format,
                    ["viewerVersion"] = bundled.ViewerVersion
                }
            });

            _context?.LogInfo($"Executed viewer for {bundled.Format} format");
        }
    }

    /// <summary>
    /// 86.2: Format detection based on magic numbers and file signatures.
    /// </summary>
    private static string DetectFormat(byte[] data)
    {
        if (data.Length < 4)
            return "binary";

        // PDF: %PDF
        if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
            return "pdf";

        // PNG: \x89PNG
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "png";

        // JPEG: \xFF\xD8\xFF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "jpeg";

        // GIF: GIF8
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
            return "gif";

        // ZIP/DOCX/XLSX: PK\x03\x04
        if (data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04)
            return "zip";

        // BMP: BM
        if (data[0] == 0x42 && data[1] == 0x4D)
            return "bmp";

        // TIFF: II or MM
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))
            return "tiff";

        // WebP: RIFF...WEBP
        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "webp";

        // MP4/Video: ftyp
        if (data.Length >= 8 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
            return "mp4";

        // Text-based detection for HTML/XML/JSON
        if (data.Length > 0 && data[0] == '<')
            return "html";

        if (data.Length > 0 && (data[0] == '{' || data[0] == '['))
            return "json";

        return "binary";
    }

    public override Task StartAsync(CancellationToken ct)
    {
        _context?.LogInfo("Self-Emulating Objects plugin started");
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        _context?.LogInfo("Self-Emulating Objects plugin stopping");
        return Task.CompletedTask;
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "SelfEmulatingObjects";
        metadata["SupportedFormats"] = new[] { "pdf", "png", "jpeg", "gif", "zip", "bmp", "tiff", "webp", "mp4", "html", "json", "binary" };
        metadata["ViewerBundling"] = true;
        metadata["FormatAutoDetection"] = true;
        metadata["SandboxedExecution"] = true;
        return metadata;
    }
}
