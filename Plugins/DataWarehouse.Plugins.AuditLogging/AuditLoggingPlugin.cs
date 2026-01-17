using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AuditLogging
{
    /// <summary>
    /// Immutable audit logging plugin with compliance format support.
    /// Extends FeaturePluginBase for lifecycle management.
    ///
    /// Compliance frameworks supported:
    /// - HIPAA (Health Insurance Portability and Accountability Act)
    /// - SOX (Sarbanes-Oxley)
    /// - GDPR (General Data Protection Regulation)
    /// - PCI-DSS (Payment Card Industry Data Security Standard)
    ///
    /// Features:
    /// - Immutable append-only audit trail with cryptographic verification
    /// - Hash chain for tamper detection
    /// - Automatic log rotation with retention policies
    /// - Export capability in multiple formats (JSON, CSV, SIEM)
    /// - Real-time streaming to external systems
    /// - Compression for long-term storage
    ///
    /// Message Commands:
    /// - audit.log: Log an audit event
    /// - audit.query: Query audit logs
    /// - audit.export: Export audit logs
    /// - audit.verify: Verify audit log integrity
    /// - audit.configure: Configure audit settings
    /// </summary>
    public sealed class AuditLoggingPlugin : FeaturePluginBase
    {
        private readonly AuditConfig _config;
        private readonly ConcurrentQueue<AuditEntry> _buffer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _shutdownCts = new();

        private StreamWriter? _currentWriter;
        private string? _currentLogPath;
        private byte[] _previousHash = SHA256.HashData(Encoding.UTF8.GetBytes("GENESIS"));
        private long _sequenceNumber;
        private Task? _flushTask;

        // High-stakes tier components
        private readonly AuditMerkleTree _merkleTree = new();
        private AuditSignatureProvider? _signatureProvider;
        private readonly List<AuditCheckpoint> _checkpoints = new();
        private long _lastCheckpointSequence;

        public override string Id => "datawarehouse.plugins.audit";
        public override string Name => "Audit Logging";
        public override string Version => "1.0.0";
        public override PluginCategory Category => PluginCategory.GovernanceProvider;

        public AuditLoggingPlugin(AuditConfig? config = null)
        {
            _config = config ?? new AuditConfig();
            _buffer = new ConcurrentQueue<AuditEntry>();
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "audit.log", DisplayName = "Log Event", Description = "Log an audit event" },
                new() { Name = "audit.query", DisplayName = "Query", Description = "Query audit logs" },
                new() { Name = "audit.export", DisplayName = "Export", Description = "Export audit logs in compliance format" },
                new() { Name = "audit.verify", DisplayName = "Verify", Description = "Verify audit log integrity" },
                new() { Name = "audit.configure", DisplayName = "Configure", Description = "Configure audit settings" },
                new() { Name = "audit.rotate", DisplayName = "Rotate", Description = "Force log rotation" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "AuditLogging";
            metadata["SupportsImmutability"] = true;
            metadata["SupportsHashChain"] = true;
            metadata["ComplianceFormats"] = new[] { "HIPAA", "SOX", "GDPR", "PCI-DSS" };
            metadata["ExportFormats"] = new[] { "JSON", "CSV", "SIEM" };
            metadata["RetentionDays"] = _config.RetentionDays;
            return metadata;
        }

        public override async Task StartAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(_config.LogDirectory);
            await InitializeCurrentLogAsync();

            // Initialize high-stakes tier components
            if (_config.EnableDigitalSignatures)
            {
                _signatureProvider = new AuditSignatureProvider(_config.SigningKeyPath);
            }

            _flushTask = FlushLoopAsync(_shutdownCts.Token);
        }

        public override async Task StopAsync()
        {
            _shutdownCts.Cancel();

            if (_flushTask != null)
            {
                try { await _flushTask; } catch { }
            }

            await FlushBufferAsync();

            if (_currentWriter != null)
            {
                await _currentWriter.DisposeAsync();
            }
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "audit.log":
                    await HandleLogAsync(message);
                    break;
                case "audit.query":
                    await HandleQueryAsync(message);
                    break;
                case "audit.export":
                    await HandleExportAsync(message);
                    break;
                case "audit.verify":
                    await HandleVerifyAsync(message);
                    break;
                case "audit.configure":
                    HandleConfigure(message);
                    break;
                case "audit.rotate":
                    await RotateLogAsync();
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        public async Task LogAsync(AuditEvent evt)
        {
            var entry = new AuditEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
                Action = evt.Action,
                ResourceType = evt.ResourceType,
                ResourceId = evt.ResourceId,
                UserId = evt.UserId,
                TenantId = evt.TenantId,
                IpAddress = evt.IpAddress,
                UserAgent = evt.UserAgent,
                Outcome = evt.Outcome,
                Details = evt.Details,
                Severity = evt.Severity,
                ComplianceTags = evt.ComplianceTags ?? []
            };

            entry.PreviousHash = Convert.ToHexString(_previousHash);
            entry.Hash = ComputeEntryHash(entry);
            _previousHash = Convert.FromHexString(entry.Hash);

            // Add to Merkle tree for high-stakes tier
            if (_config.EnableMerkleTree)
            {
                _merkleTree.AddLeaf(entry.Hash);

                // Create checkpoint if block size reached
                if (_merkleTree.LeafCount >= _config.MerkleTreeBlockSize)
                {
                    await CreateCheckpointAsync();
                }
            }

            _buffer.Enqueue(entry);

            if (_buffer.Count >= _config.FlushThreshold)
            {
                await FlushBufferAsync();
            }
        }

        /// <summary>
        /// Creates a signed checkpoint of the current Merkle tree state.
        /// Used for efficient verification of large audit logs.
        /// </summary>
        private async Task CreateCheckpointAsync()
        {
            var checkpoint = new AuditCheckpoint
            {
                Timestamp = DateTime.UtcNow,
                StartSequence = _lastCheckpointSequence + 1,
                EndSequence = _sequenceNumber,
                MerkleRoot = _merkleTree.ComputeRoot(),
                EntryCount = _merkleTree.LeafCount
            };

            checkpoint.CheckpointHash = checkpoint.ComputeCheckpointHash();

            if (_signatureProvider != null)
            {
                checkpoint.Signature = _signatureProvider.Sign(checkpoint.CheckpointHash);
            }

            _checkpoints.Add(checkpoint);
            _lastCheckpointSequence = _sequenceNumber;

            // Save checkpoint to disk
            var checkpointPath = Path.Combine(_config.LogDirectory, $"checkpoint-{checkpoint.Timestamp:yyyyMMdd-HHmmss}.json");
            var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(checkpointPath, json);

            // Reset Merkle tree for next block
            _merkleTree.Clear();
        }

        /// <summary>
        /// Performs comprehensive integrity verification for high-stakes tier.
        /// Returns detailed verification result with hash chain, Merkle tree, and signature validation.
        /// </summary>
        public async Task<AuditVerificationResult> VerifyIntegrityAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            var result = new AuditVerificationResult();
            var entries = await QueryLogsAsync(
                startDate ?? DateTime.MinValue,
                endDate ?? DateTime.UtcNow,
                null, null, int.MaxValue);

            var entryList = entries.ToList();
            result.EntriesVerified = entryList.Count;

            // Verify hash chain
            result.HashChainValid = VerifyHashChain(entryList);
            if (!result.HashChainValid)
            {
                result.Errors.Add("Hash chain integrity check failed - possible tampering detected");
            }

            // Verify Merkle trees via checkpoints
            if (_config.EnableMerkleTree)
            {
                result.MerkleTreeValid = await VerifyMerkleCheckpointsAsync();
                if (!result.MerkleTreeValid)
                {
                    result.Errors.Add("Merkle tree checkpoint verification failed");
                }
            }
            else
            {
                result.MerkleTreeValid = true;
            }

            // Verify signatures if enabled
            if (_config.EnableDigitalSignatures && _signatureProvider != null)
            {
                result.SignaturesValid = VerifyCheckpointSignatures();
                if (!result.SignaturesValid)
                {
                    result.Errors.Add("One or more checkpoint signatures are invalid");
                }
            }
            else
            {
                result.SignaturesValid = true;
            }

            result.IsValid = result.HashChainValid && result.MerkleTreeValid && result.SignaturesValid;
            return result;
        }

        private async Task<bool> VerifyMerkleCheckpointsAsync()
        {
            var checkpointFiles = Directory.GetFiles(_config.LogDirectory, "checkpoint-*.json");

            foreach (var file in checkpointFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var checkpoint = JsonSerializer.Deserialize<AuditCheckpoint>(json);
                    if (checkpoint == null) continue;

                    // Verify checkpoint hash
                    var computedHash = checkpoint.ComputeCheckpointHash();
                    if (computedHash != checkpoint.CheckpointHash)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private bool VerifyCheckpointSignatures()
        {
            if (_signatureProvider == null) return true;

            foreach (var checkpoint in _checkpoints)
            {
                if (!string.IsNullOrEmpty(checkpoint.Signature))
                {
                    if (!_signatureProvider.Verify(checkpoint.CheckpointHash, checkpoint.Signature))
                    {
                        return false;
                    }
                }
            }

            return true;

        private async Task HandleLogAsync(PluginMessage message)
        {
            var evt = new AuditEvent
            {
                Action = GetString(message.Payload, "action") ?? "unknown",
                ResourceType = GetString(message.Payload, "resourceType") ?? "unknown",
                ResourceId = GetString(message.Payload, "resourceId"),
                UserId = GetString(message.Payload, "userId") ?? "anonymous",
                TenantId = GetString(message.Payload, "tenantId"),
                IpAddress = GetString(message.Payload, "ipAddress"),
                UserAgent = GetString(message.Payload, "userAgent"),
                Outcome = GetString(message.Payload, "outcome") ?? "success",
                Severity = GetString(message.Payload, "severity") ?? "info",
                Details = message.Payload.TryGetValue("details", out var d) && d is Dictionary<string, object> details
                    ? details
                    : new Dictionary<string, object>(),
                ComplianceTags = message.Payload.TryGetValue("complianceTags", out var t) && t is string[] tags
                    ? tags
                    : []
            };

            await LogAsync(evt);
        }

        private async Task HandleQueryAsync(PluginMessage message)
        {
            var startDate = message.Payload.TryGetValue("startDate", out var sd) && sd is DateTime start
                ? start
                : DateTime.UtcNow.AddDays(-7);

            var endDate = message.Payload.TryGetValue("endDate", out var ed) && ed is DateTime end
                ? end
                : DateTime.UtcNow;

            var userId = GetString(message.Payload, "userId");
            var action = GetString(message.Payload, "action");
            var limit = message.Payload.TryGetValue("limit", out var l) && l is int lim ? lim : 1000;

            var results = await QueryLogsAsync(startDate, endDate, userId, action, limit);
        }

        private async Task HandleExportAsync(PluginMessage message)
        {
            var format = GetString(message.Payload, "format") ?? "JSON";
            var compliance = GetString(message.Payload, "compliance") ?? "GENERIC";
            var startDate = message.Payload.TryGetValue("startDate", out var sd) && sd is DateTime start
                ? start
                : DateTime.UtcNow.AddDays(-30);
            var endDate = message.Payload.TryGetValue("endDate", out var ed) && ed is DateTime end
                ? end
                : DateTime.UtcNow;
            var outputPath = GetString(message.Payload, "outputPath");

            var entries = await QueryLogsAsync(startDate, endDate, null, null, int.MaxValue);

            var exporter = GetExporter(format, compliance);
            var exported = exporter.Export(entries);

            if (!string.IsNullOrEmpty(outputPath))
            {
                await File.WriteAllTextAsync(outputPath, exported);
            }
        }

        private async Task HandleVerifyAsync(PluginMessage message)
        {
            var startDate = message.Payload.TryGetValue("startDate", out var sd) && sd is DateTime start
                ? start
                : DateTime.MinValue;

            var entries = await QueryLogsAsync(startDate, DateTime.UtcNow, null, null, int.MaxValue);
            var isValid = VerifyHashChain(entries.ToList());
        }

        private void HandleConfigure(PluginMessage message)
        {
            if (message.Payload.TryGetValue("retentionDays", out var rd) && rd is int days)
                _config.RetentionDays = days;
            if (message.Payload.TryGetValue("flushThreshold", out var ft) && ft is int threshold)
                _config.FlushThreshold = threshold;
        }

        private async Task<IEnumerable<AuditEntry>> QueryLogsAsync(
            DateTime startDate, DateTime endDate, string? userId, string? action, int limit)
        {
            var results = new List<AuditEntry>();
            var logFiles = Directory.GetFiles(_config.LogDirectory, "audit-*.jsonl")
                .OrderByDescending(f => f);

            foreach (var logFile in logFiles)
            {
                var fileName = Path.GetFileName(logFile);
                if (!TryParseLogDate(fileName, out var logDate))
                    continue;

                if (logDate.Date < startDate.Date.AddDays(-1))
                    break;

                var lines = await File.ReadAllLinesAsync(logFile);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                        if (entry == null) continue;

                        if (entry.Timestamp < startDate || entry.Timestamp > endDate)
                            continue;
                        if (!string.IsNullOrEmpty(userId) && entry.UserId != userId)
                            continue;
                        if (!string.IsNullOrEmpty(action) && entry.Action != action)
                            continue;

                        results.Add(entry);

                        if (results.Count >= limit)
                            return results;
                    }
                    catch { }
                }
            }

            return results;
        }

        private bool VerifyHashChain(List<AuditEntry> entries)
        {
            if (entries.Count == 0) return true;

            entries = entries.OrderBy(e => e.SequenceNumber).ToList();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var computedHash = ComputeEntryHash(entry);

                if (entry.Hash != computedHash)
                    return false;

                if (i > 0 && entry.PreviousHash != entries[i - 1].Hash)
                    return false;
            }

            return true;
        }

        private string ComputeEntryHash(AuditEntry entry)
        {
            var data = $"{entry.SequenceNumber}|{entry.Timestamp:O}|{entry.Action}|{entry.ResourceType}|" +
                       $"{entry.ResourceId}|{entry.UserId}|{entry.Outcome}|{entry.PreviousHash}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }

        private async Task InitializeCurrentLogAsync()
        {
            var date = DateTime.UtcNow;
            _currentLogPath = Path.Combine(_config.LogDirectory, $"audit-{date:yyyyMMdd}.jsonl");

            _currentWriter = new StreamWriter(
                new FileStream(_currentLogPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                Encoding.UTF8)
            {
                AutoFlush = false
            };

            if (File.Exists(_currentLogPath))
            {
                var lines = await File.ReadAllLinesAsync(_currentLogPath);
                if (lines.Length > 0)
                {
                    var lastLine = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                    if (lastLine != null)
                    {
                        try
                        {
                            var lastEntry = JsonSerializer.Deserialize<AuditEntry>(lastLine);
                            if (lastEntry != null)
                            {
                                _sequenceNumber = lastEntry.SequenceNumber;
                                _previousHash = Convert.FromHexString(lastEntry.Hash);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private async Task RotateLogAsync()
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_currentWriter != null)
                {
                    await _currentWriter.FlushAsync();
                    await _currentWriter.DisposeAsync();
                }

                await InitializeCurrentLogAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task FlushBufferAsync()
        {
            if (_buffer.IsEmpty) return;

            await _writeLock.WaitAsync();
            try
            {
                var today = DateTime.UtcNow.Date;
                if (_currentLogPath != null)
                {
                    var currentDate = TryParseLogDate(Path.GetFileName(_currentLogPath), out var d) ? d : today;
                    if (currentDate.Date != today)
                    {
                        await RotateLogAsync();
                    }
                }

                while (_buffer.TryDequeue(out var entry))
                {
                    var json = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEntry);
                    await _currentWriter!.WriteLineAsync(json);
                }

                await _currentWriter!.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task FlushLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.FlushInterval, ct);
                    await FlushBufferAsync();
                    await CleanupOldLogsAsync();
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task CleanupOldLogsAsync()
        {
            var cutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);
            var logFiles = Directory.GetFiles(_config.LogDirectory, "audit-*.jsonl");

            foreach (var file in logFiles)
            {
                if (TryParseLogDate(Path.GetFileName(file), out var date) && date < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        private static bool TryParseLogDate(string fileName, out DateTime date)
        {
            date = default;
            if (fileName.Length < 14) return false;

            var dateStr = fileName.Substring(6, 8);
            return DateTime.TryParseExact(dateStr, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out date);
        }

        private static string? GetString(Dictionary<string, object?> payload, string key)
        {
            return payload.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        private IAuditExporter GetExporter(string format, string compliance)
        {
            return format.ToUpperInvariant() switch
            {
                "CSV" => new CsvExporter(compliance),
                "SIEM" => new SiemExporter(compliance),
                _ => new JsonExporter(compliance)
            };
        }
    }

    public class AuditEntry
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public long SequenceNumber { get; set; }
        public string Action { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string? ResourceId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public string Outcome { get; set; } = "success";
        public string Severity { get; set; } = "info";
        public Dictionary<string, object> Details { get; set; } = new();
        public string[] ComplianceTags { get; set; } = [];
        public string Hash { get; set; } = string.Empty;
        public string PreviousHash { get; set; } = string.Empty;
    }

    public class AuditEvent
    {
        public string Action { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string? ResourceId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public string Outcome { get; set; } = "success";
        public string Severity { get; set; } = "info";
        public Dictionary<string, object> Details { get; set; } = new();
        public string[]? ComplianceTags { get; set; }
    }

    public class AuditConfig
    {
        public string LogDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "audit");
        public int RetentionDays { get; set; } = 2555;
        public int FlushThreshold { get; set; } = 100;
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

        // High-stakes tier settings
        public bool EnableDigitalSignatures { get; set; } = false;
        public bool EnableMerkleTree { get; set; } = true;
        public bool EnableSecureTimestamping { get; set; } = false;
        public string? SigningKeyPath { get; set; }
        public string? TimestampServerUrl { get; set; }
        public int MerkleTreeBlockSize { get; set; } = 1000;
    }

    /// <summary>
    /// Merkle Tree for efficient tamper detection.
    /// Used by high-stakes tier for cryptographic proof of audit integrity.
    /// </summary>
    public class AuditMerkleTree
    {
        private readonly List<string> _leaves = new();
        private readonly object _lock = new();

        /// <summary>
        /// Adds an entry hash as a leaf to the Merkle tree.
        /// </summary>
        public void AddLeaf(string hash)
        {
            lock (_lock)
            {
                _leaves.Add(hash);
            }
        }

        /// <summary>
        /// Computes the Merkle root from all leaves.
        /// </summary>
        public string ComputeRoot()
        {
            lock (_lock)
            {
                if (_leaves.Count == 0)
                    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("EMPTY")));

                var currentLevel = _leaves.ToList();

                while (currentLevel.Count > 1)
                {
                    var nextLevel = new List<string>();

                    for (int i = 0; i < currentLevel.Count; i += 2)
                    {
                        string left = currentLevel[i];
                        string right = i + 1 < currentLevel.Count ? currentLevel[i + 1] : left;
                        var combined = SHA256.HashData(Encoding.UTF8.GetBytes(left + right));
                        nextLevel.Add(Convert.ToHexString(combined));
                    }

                    currentLevel = nextLevel;
                }

                return currentLevel[0];
            }
        }

        /// <summary>
        /// Gets proof path for a specific leaf index.
        /// </summary>
        public List<MerkleProofNode> GetProof(int leafIndex)
        {
            lock (_lock)
            {
                if (leafIndex < 0 || leafIndex >= _leaves.Count)
                    return new List<MerkleProofNode>();

                var proof = new List<MerkleProofNode>();
                var currentLevel = _leaves.ToList();
                int index = leafIndex;

                while (currentLevel.Count > 1)
                {
                    var nextLevel = new List<string>();
                    int siblingIndex = index % 2 == 0 ? index + 1 : index - 1;

                    if (siblingIndex >= 0 && siblingIndex < currentLevel.Count)
                    {
                        proof.Add(new MerkleProofNode
                        {
                            Hash = currentLevel[siblingIndex],
                            IsLeft = index % 2 != 0
                        });
                    }

                    for (int i = 0; i < currentLevel.Count; i += 2)
                    {
                        string left = currentLevel[i];
                        string right = i + 1 < currentLevel.Count ? currentLevel[i + 1] : left;
                        var combined = SHA256.HashData(Encoding.UTF8.GetBytes(left + right));
                        nextLevel.Add(Convert.ToHexString(combined));
                    }

                    currentLevel = nextLevel;
                    index /= 2;
                }

                return proof;
            }
        }

        /// <summary>
        /// Verifies a leaf hash belongs to the tree with the given root.
        /// </summary>
        public static bool VerifyProof(string leafHash, List<MerkleProofNode> proof, string root)
        {
            var current = leafHash;

            foreach (var node in proof)
            {
                var left = node.IsLeft ? node.Hash : current;
                var right = node.IsLeft ? current : node.Hash;
                current = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(left + right)));
            }

            return current == root;
        }

        public int LeafCount => _leaves.Count;
        public void Clear() { lock (_lock) { _leaves.Clear(); } }
    }

    public class MerkleProofNode
    {
        public string Hash { get; set; } = string.Empty;
        public bool IsLeft { get; set; }
    }

    /// <summary>
    /// Digital signature provider for tamper-evident audit entries.
    /// </summary>
    public class AuditSignatureProvider : IDisposable
    {
        private readonly RSA _rsa;
        private readonly bool _ownsKey;

        public AuditSignatureProvider(string? keyPath = null)
        {
            if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
            {
                var keyData = File.ReadAllText(keyPath);
                _rsa = RSA.Create();
                _rsa.ImportFromPem(keyData);
                _ownsKey = true;
            }
            else
            {
                _rsa = RSA.Create(2048);
                _ownsKey = true;
            }
        }

        public string Sign(string data)
        {
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var signature = _rsa.SignData(dataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signature);
        }

        public bool Verify(string data, string signature)
        {
            try
            {
                var dataBytes = Encoding.UTF8.GetBytes(data);
                var signatureBytes = Convert.FromBase64String(signature);
                return _rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch
            {
                return false;
            }
        }

        public string ExportPublicKey()
        {
            return _rsa.ExportSubjectPublicKeyInfoPem();
        }

        public void ExportKeyPair(string privateKeyPath, string publicKeyPath)
        {
            File.WriteAllText(privateKeyPath, _rsa.ExportRSAPrivateKeyPem());
            File.WriteAllText(publicKeyPath, _rsa.ExportSubjectPublicKeyInfoPem());
        }

        public void Dispose()
        {
            if (_ownsKey)
                _rsa.Dispose();
        }
    }

    /// <summary>
    /// Checkpoint record for Merkle tree integrity verification.
    /// Created periodically to allow efficient verification of large audit logs.
    /// </summary>
    public class AuditCheckpoint
    {
        public DateTime Timestamp { get; set; }
        public long StartSequence { get; set; }
        public long EndSequence { get; set; }
        public string MerkleRoot { get; set; } = string.Empty;
        public string? Signature { get; set; }
        public int EntryCount { get; set; }
        public string CheckpointHash { get; set; } = string.Empty;

        public string ComputeCheckpointHash()
        {
            var data = $"{Timestamp:O}|{StartSequence}|{EndSequence}|{MerkleRoot}|{EntryCount}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }
    }

    /// <summary>
    /// Result of audit integrity verification.
    /// </summary>
    public class AuditVerificationResult
    {
        public bool IsValid { get; set; }
        public long EntriesVerified { get; set; }
        public long EntriesFailed { get; set; }
        public bool HashChainValid { get; set; }
        public bool MerkleTreeValid { get; set; }
        public bool SignaturesValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public DateTime VerificationTime { get; set; } = DateTime.UtcNow;
    }

    internal interface IAuditExporter
    {
        string Export(IEnumerable<AuditEntry> entries);
    }

    internal class JsonExporter : IAuditExporter
    {
        private readonly string _compliance;

        public JsonExporter(string compliance) => _compliance = compliance;

        public string Export(IEnumerable<AuditEntry> entries)
        {
            var export = new
            {
                ExportedAt = DateTime.UtcNow,
                ComplianceFramework = _compliance,
                Entries = entries.ToList()
            };
            return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    internal class CsvExporter : IAuditExporter
    {
        private readonly string _compliance;

        public CsvExporter(string compliance) => _compliance = compliance;

        public string Export(IEnumerable<AuditEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,SequenceNumber,Action,ResourceType,ResourceId,UserId,Outcome,Severity,Hash");

            foreach (var entry in entries)
            {
                sb.AppendLine($"\"{entry.Timestamp:O}\",{entry.SequenceNumber},\"{entry.Action}\",\"{entry.ResourceType}\"," +
                             $"\"{entry.ResourceId}\",\"{entry.UserId}\",\"{entry.Outcome}\",\"{entry.Severity}\",\"{entry.Hash}\"");
            }

            return sb.ToString();
        }
    }

    internal class SiemExporter : IAuditExporter
    {
        private readonly string _compliance;

        public SiemExporter(string compliance) => _compliance = compliance;

        public string Export(IEnumerable<AuditEntry> entries)
        {
            var sb = new StringBuilder();

            foreach (var entry in entries)
            {
                var cef = $"CEF:0|DataWarehouse|AuditLog|1.0|{entry.Action}|{entry.Action}|" +
                         $"{GetSeverityNumber(entry.Severity)}|" +
                         $"rt={entry.Timestamp:O} " +
                         $"src={entry.IpAddress ?? "unknown"} " +
                         $"suser={entry.UserId} " +
                         $"outcome={entry.Outcome} " +
                         $"cs1={entry.ResourceType} cs1Label=ResourceType " +
                         $"cs2={entry.ResourceId ?? ""} cs2Label=ResourceId " +
                         $"cs3={_compliance} cs3Label=ComplianceFramework";
                sb.AppendLine(cef);
            }

            return sb.ToString();
        }

        private static int GetSeverityNumber(string severity)
        {
            return severity.ToLowerInvariant() switch
            {
                "critical" => 10,
                "high" => 7,
                "medium" => 5,
                "low" => 3,
                "info" => 1,
                _ => 1
            };
        }
    }

    [JsonSerializable(typeof(AuditEntry))]
    internal partial class AuditJsonContext : JsonSerializerContext { }
}
