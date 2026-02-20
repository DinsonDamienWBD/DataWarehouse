using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
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
public sealed class SelfEmulatingObjectsPlugin : ComputePluginBase
{
    private IKernelContext? _context;
    private WasmViewer.ViewerBundler? _bundler;
    private WasmViewer.ViewerRuntime? _runtime;
    private readonly BoundedDictionary<string, List<WasmViewer.SelfEmulatingObjectSnapshot>> _snapshots = new BoundedDictionary<string, List<WasmViewer.SelfEmulatingObjectSnapshot>>(1000);

    public override string Id => "com.datawarehouse.selfemulating";
    public override string Name => "Self-Emulating Objects";
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string RuntimeType => "SelfEmulating";
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;
        _bundler = new WasmViewer.ViewerBundler(_context!, MessageBus!);
        _runtime = new WasmViewer.ViewerRuntime(_context!, MessageBus!);

        // Subscribe to object creation events
        MessageBus!.Subscribe("selfemulating.bundle", HandleBundleRequestAsync);
        MessageBus!.Subscribe("selfemulating.view", HandleViewRequestAsync);

        // Subscribe to lifecycle operations
        MessageBus!.Subscribe("selfemulating.snapshot", HandleSnapshotRequestAsync);
        MessageBus!.Subscribe("selfemulating.rollback", HandleRollbackRequestAsync);
        MessageBus!.Subscribe("selfemulating.replay", HandleReplayRequestAsync);

        _context?.LogInfo("Self-Emulating Objects plugin initialized with snapshot/rollback/replay lifecycle");
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
    /// Handles snapshot requests: creates a timestamped deep copy of the object.
    /// </summary>
    private async Task HandleSnapshotRequestAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("bundledObject", out var bundledObj) &&
            bundledObj is WasmViewer.SelfEmulatingObject obj)
        {
            var description = message.Payload.TryGetValue("description", out var descObj)
                ? descObj as string
                : null;

            // Deep copy: clone byte arrays and metadata dictionary
            var snapshot = new WasmViewer.SelfEmulatingObjectSnapshot
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                ObjectId = obj.Id,
                Object = new WasmViewer.SelfEmulatingObject
                {
                    Id = obj.Id,
                    Data = (byte[])obj.Data.Clone(),
                    ViewerWasm = (byte[])obj.ViewerWasm.Clone(),
                    Format = obj.Format,
                    ViewerName = obj.ViewerName,
                    ViewerVersion = obj.ViewerVersion,
                    CreatedAt = obj.CreatedAt,
                    Metadata = new Dictionary<string, string>(obj.Metadata)
                },
                CreatedAt = DateTime.UtcNow,
                Description = description
            };

            var snapList = _snapshots.GetOrAdd(obj.Id, _ => new List<WasmViewer.SelfEmulatingObjectSnapshot>());
            lock (snapList)
            {
                snapList.Add(snapshot);
            }

            await MessageBus!.PublishAsync("selfemulating.snapshot.created", new PluginMessage
            {
                Type = "selfemulating.snapshot.created",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["snapshotId"] = snapshot.SnapshotId,
                    ["objectId"] = obj.Id,
                    ["createdAt"] = snapshot.CreatedAt,
                    ["description"] = description ?? string.Empty
                }
            });

            _context?.LogInfo($"Created snapshot {snapshot.SnapshotId} for object {obj.Id}");
        }
    }

    /// <summary>
    /// Handles rollback requests: restores an object to a previous snapshot state.
    /// </summary>
    private async Task HandleRollbackRequestAsync(PluginMessage message)
    {
        var objectId = message.Payload.TryGetValue("objectId", out var idObj) ? idObj as string : null;
        var snapshotId = message.Payload.TryGetValue("snapshotId", out var snapIdObj) ? snapIdObj as string : null;

        if (string.IsNullOrEmpty(objectId))
        {
            _context?.LogError("Rollback request missing objectId");
            return;
        }

        if (!_snapshots.TryGetValue(objectId, out var snapList))
        {
            _context?.LogError($"No snapshots found for object {objectId}");
            return;
        }

        WasmViewer.SelfEmulatingObjectSnapshot? target;
        lock (snapList)
        {
            target = !string.IsNullOrEmpty(snapshotId)
                ? snapList.Find(s => s.SnapshotId == snapshotId)
                : snapList.Count > 0 ? snapList[^1] : null; // Latest snapshot if no ID specified
        }

        if (target == null)
        {
            _context?.LogError($"Snapshot not found for object {objectId}, snapshotId={snapshotId}");
            return;
        }

        // Return a deep copy of the snapshot's object
        var restored = new WasmViewer.SelfEmulatingObject
        {
            Id = target.Object.Id,
            Data = (byte[])target.Object.Data.Clone(),
            ViewerWasm = (byte[])target.Object.ViewerWasm.Clone(),
            Format = target.Object.Format,
            ViewerName = target.Object.ViewerName,
            ViewerVersion = target.Object.ViewerVersion,
            CreatedAt = target.Object.CreatedAt,
            Metadata = new Dictionary<string, string>(target.Object.Metadata)
        };

        await MessageBus!.PublishAsync("selfemulating.rollback.complete", new PluginMessage
        {
            Type = "selfemulating.rollback.complete",
            SourcePluginId = Id,
            Payload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["snapshotId"] = target.SnapshotId,
                ["restoredObject"] = restored,
                ["restoredFrom"] = target.CreatedAt
            }
        });

        _context?.LogInfo($"Rolled back object {objectId} to snapshot {target.SnapshotId}");
    }

    /// <summary>
    /// Handles replay requests: replays an object through all its snapshots chronologically,
    /// executing each version's viewer to produce output per version.
    /// </summary>
    private async Task HandleReplayRequestAsync(PluginMessage message)
    {
        var objectId = message.Payload.TryGetValue("objectId", out var idObj) ? idObj as string : null;

        if (string.IsNullOrEmpty(objectId))
        {
            _context?.LogError("Replay request missing objectId");
            return;
        }

        if (!_snapshots.TryGetValue(objectId, out var snapList))
        {
            _context?.LogError($"No snapshots found for object {objectId}");
            return;
        }

        List<WasmViewer.SelfEmulatingObjectSnapshot> orderedSnapshots;
        lock (snapList)
        {
            orderedSnapshots = snapList.OrderBy(s => s.CreatedAt).ToList();
        }

        var replayResults = new List<Dictionary<string, object>>();

        foreach (var snapshot in orderedSnapshots)
        {
            try
            {
                var output = _runtime != null
                    ? await _runtime.ExecuteViewerAsync(snapshot.Object)
                    : Array.Empty<byte>();

                replayResults.Add(new Dictionary<string, object>
                {
                    ["snapshotId"] = snapshot.SnapshotId,
                    ["createdAt"] = snapshot.CreatedAt,
                    ["viewerVersion"] = snapshot.Object.ViewerVersion,
                    ["output"] = output,
                    ["success"] = true
                });
            }
            catch (Exception ex)
            {
                replayResults.Add(new Dictionary<string, object>
                {
                    ["snapshotId"] = snapshot.SnapshotId,
                    ["createdAt"] = snapshot.CreatedAt,
                    ["viewerVersion"] = snapshot.Object.ViewerVersion,
                    ["error"] = ex.Message,
                    ["success"] = false
                });
            }
        }

        await MessageBus!.PublishAsync("selfemulating.replay.complete", new PluginMessage
        {
            Type = "selfemulating.replay.complete",
            SourcePluginId = Id,
            Payload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["snapshotCount"] = orderedSnapshots.Count,
                ["results"] = replayResults
            }
        });

        _context?.LogInfo($"Replayed {orderedSnapshots.Count} snapshots for object {objectId}");
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
        metadata["SnapshotSupport"] = true;
        metadata["RollbackSupport"] = true;
        metadata["ReplaySupport"] = true;
        metadata["LifecycleOperations"] = new[] { "snapshot", "rollback", "replay" };
        return metadata;
    }


    #region Hierarchy ComputePluginBase Abstract Methods
    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["status"] = "executed", ["plugin"] = Id });
    #endregion
}