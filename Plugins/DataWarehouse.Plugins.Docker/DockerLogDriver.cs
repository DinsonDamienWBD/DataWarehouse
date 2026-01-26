using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Docker
{
    /// <summary>
    /// Docker Log Driver implementation for shipping container logs to DataWarehouse.
    /// Implements Docker Log Driver plugin protocol for structured logging.
    ///
    /// Features:
    /// - Structured JSON logging
    /// - Log rotation with configurable size and count
    /// - Real-time log streaming
    /// - Compression for archived logs
    /// - Multi-container support
    /// - Timestamp-based log retrieval
    /// - Label-based log filtering
    /// - Buffer management for performance
    /// - Graceful shutdown with log flush
    /// </summary>
    internal sealed class DockerLogDriver : IDisposable
    {
        private readonly DockerPluginConfig _config;
        private readonly ConcurrentDictionary<string, ContainerLogContext> _activeContainers = new();
        private readonly SemaphoreSlim _logLock = new(1, 1);
        private readonly Timer _flushTimer;
        private bool _disposed;

        private const int DefaultBufferSize = 4096;
        private const int DefaultMaxLogFileSize = 10 * 1024 * 1024; // 10MB
        private const int DefaultMaxLogFiles = 5;

        /// <summary>
        /// Initializes the Docker Log Driver.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        public DockerLogDriver(DockerPluginConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Periodic flush to ensure logs are persisted
            _flushTimer = new Timer(
                FlushAllBuffers,
                null,
                TimeSpan.FromSeconds(_config.LogFlushIntervalSeconds),
                TimeSpan.FromSeconds(_config.LogFlushIntervalSeconds));
        }

        /// <summary>
        /// Initializes the log driver, creating necessary directories.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public Task InitializeAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(_config.LogBasePath);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gracefully shuts down the log driver, flushing all pending logs.
        /// </summary>
        public async Task ShutdownAsync()
        {
            await _flushTimer.DisposeAsync();
            await FlushAllBuffersAsync();

            foreach (var context in _activeContainers.Values)
            {
                await context.DisposeAsync();
            }
            _activeContainers.Clear();
        }

        /// <summary>
        /// Starts logging for a container.
        /// </summary>
        /// <param name="containerId">Container ID to log.</param>
        /// <param name="containerName">Optional container name for easier identification.</param>
        /// <param name="config">Optional logging configuration.</param>
        public async Task StartLoggingAsync(
            string containerId,
            string? containerName,
            Dictionary<string, string>? config = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(containerId);

            await _logLock.WaitAsync();
            try
            {
                if (_activeContainers.ContainsKey(containerId))
                {
                    // Already logging for this container
                    return;
                }

                var logDir = Path.Combine(_config.LogBasePath, containerId);
                Directory.CreateDirectory(logDir);

                var context = new ContainerLogContext
                {
                    ContainerId = containerId,
                    ContainerName = containerName ?? containerId,
                    LogDirectory = logDir,
                    CurrentLogFile = Path.Combine(logDir, "current.log"),
                    MaxFileSize = ParseConfigValue(config, "max-size", DefaultMaxLogFileSize),
                    MaxFiles = ParseConfigValue(config, "max-file", DefaultMaxLogFiles),
                    CompressRotated = ParseConfigBool(config, "compress", true),
                    Labels = ExtractLabels(config),
                    StartedAt = DateTime.UtcNow
                };

                // Initialize log writer
                context.Writer = new StreamWriter(
                    new FileStream(context.CurrentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read),
                    Encoding.UTF8,
                    bufferSize: DefaultBufferSize);

                _activeContainers[containerId] = context;

                // Write startup entry
                await WriteLogEntryAsync(context, new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Stream = "system",
                    Message = $"Logging started for container {containerName ?? containerId}",
                    ContainerId = containerId,
                    ContainerName = containerName
                });
            }
            finally
            {
                _logLock.Release();
            }
        }

        /// <summary>
        /// Stops logging for a container.
        /// </summary>
        /// <param name="containerId">Container ID to stop logging.</param>
        public async Task StopLoggingAsync(string containerId)
        {
            ArgumentException.ThrowIfNullOrEmpty(containerId);

            await _logLock.WaitAsync();
            try
            {
                if (_activeContainers.TryRemove(containerId, out var context))
                {
                    // Write shutdown entry
                    await WriteLogEntryAsync(context, new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Stream = "system",
                        Message = $"Logging stopped for container {context.ContainerName}",
                        ContainerId = containerId,
                        ContainerName = context.ContainerName
                    });

                    await context.DisposeAsync();
                }
            }
            finally
            {
                _logLock.Release();
            }
        }

        /// <summary>
        /// Writes a log entry for a container.
        /// </summary>
        /// <param name="containerId">Container ID.</param>
        /// <param name="stream">Log stream (stdout, stderr, system).</param>
        /// <param name="message">Log message.</param>
        /// <param name="timestamp">Optional timestamp (defaults to now).</param>
        public async Task WriteLogAsync(
            string containerId,
            string stream,
            string message,
            DateTime? timestamp = null)
        {
            if (!_activeContainers.TryGetValue(containerId, out var context))
            {
                throw new InvalidOperationException($"Logging not started for container: {containerId}");
            }

            var entry = new LogEntry
            {
                Timestamp = timestamp ?? DateTime.UtcNow,
                Stream = stream,
                Message = message,
                ContainerId = containerId,
                ContainerName = context.ContainerName,
                Labels = context.Labels
            };

            await WriteLogEntryAsync(context, entry);
        }

        /// <summary>
        /// Reads logs for a container.
        /// </summary>
        /// <param name="containerId">Container ID.</param>
        /// <param name="config">Read configuration options.</param>
        /// <returns>Collection of log entries.</returns>
        public async Task<IReadOnlyList<LogEntry>> ReadLogsAsync(
            string containerId,
            Dictionary<string, string>? config = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(containerId);

            var logDir = Path.Combine(_config.LogBasePath, containerId);
            if (!Directory.Exists(logDir))
            {
                return Array.Empty<LogEntry>();
            }

            var logs = new List<LogEntry>();

            // Parse config options
            var since = ParseConfigDateTime(config, "since");
            var until = ParseConfigDateTime(config, "until");
            var tail = ParseConfigValue(config, "tail", 0);
            var follow = ParseConfigBool(config, "follow", false);
            var stdout = ParseConfigBool(config, "stdout", true);
            var stderr = ParseConfigBool(config, "stderr", true);

            // Read from all log files (current + rotated)
            var logFiles = Directory.GetFiles(logDir, "*.log")
                .Concat(Directory.GetFiles(logDir, "*.log.gz"))
                .OrderBy(f => File.GetCreationTimeUtc(f))
                .ToList();

            foreach (var logFile in logFiles)
            {
                var entries = await ReadLogFileAsync(logFile, since, until, stdout, stderr);
                logs.AddRange(entries);
            }

            // Apply tail if specified
            if (tail > 0 && logs.Count > tail)
            {
                logs = logs.TakeLast(tail).ToList();
            }

            return logs;
        }

        /// <summary>
        /// Gets the capabilities of this log driver.
        /// </summary>
        /// <returns>Log driver capabilities.</returns>
        public LogDriverCapabilities GetCapabilities()
        {
            return new LogDriverCapabilities
            {
                ReadLogs = true
            };
        }

        /// <summary>
        /// Gets the health status of the log driver.
        /// </summary>
        /// <returns>Health status string.</returns>
        public string GetHealthStatus()
        {
            try
            {
                if (!Directory.Exists(_config.LogBasePath))
                {
                    return "unhealthy: log base path does not exist";
                }

                // Check write access
                var testFile = Path.Combine(_config.LogBasePath, ".health_check");
                File.WriteAllText(testFile, DateTime.UtcNow.ToString("O"));
                File.Delete(testFile);

                return $"healthy: {_activeContainers.Count} active containers";
            }
            catch (Exception ex)
            {
                return $"unhealthy: {ex.Message}";
            }
        }

        /// <summary>
        /// Disposes resources used by the log driver.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _flushTimer.Dispose();
                _logLock.Dispose();
                foreach (var context in _activeContainers.Values)
                {
                    context.Dispose();
                }
                _disposed = true;
            }
        }

        #region Private Methods

        private async Task WriteLogEntryAsync(ContainerLogContext context, LogEntry entry)
        {
            await context.WriteLock.WaitAsync();
            try
            {
                if (context.Writer == null)
                {
                    return;
                }

                var json = JsonSerializer.Serialize(entry);
                await context.Writer.WriteLineAsync(json);
                context.BytesWritten += Encoding.UTF8.GetByteCount(json) + 1;

                // Check if rotation is needed
                if (context.BytesWritten >= context.MaxFileSize)
                {
                    await RotateLogFileAsync(context);
                }
            }
            finally
            {
                context.WriteLock.Release();
            }
        }

        private async Task RotateLogFileAsync(ContainerLogContext context)
        {
            await context.Writer!.FlushAsync();
            await context.Writer.DisposeAsync();

            // Rename current to timestamped file
            var rotatedName = $"container-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log";
            var rotatedPath = Path.Combine(context.LogDirectory, rotatedName);
            File.Move(context.CurrentLogFile, rotatedPath);

            // Compress if enabled
            if (context.CompressRotated)
            {
                await CompressLogFileAsync(rotatedPath);
            }

            // Clean up old files
            await CleanupOldLogFilesAsync(context);

            // Start new log file
            context.Writer = new StreamWriter(
                new FileStream(context.CurrentLogFile, FileMode.Create, FileAccess.Write, FileShare.Read),
                Encoding.UTF8,
                bufferSize: DefaultBufferSize);
            context.BytesWritten = 0;
        }

        private static async Task CompressLogFileAsync(string filePath)
        {
            var compressedPath = filePath + ".gz";
            await using var input = File.OpenRead(filePath);
            await using var output = File.Create(compressedPath);
            await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
            await input.CopyToAsync(gzip);
            File.Delete(filePath);
        }

        private static Task CleanupOldLogFilesAsync(ContainerLogContext context)
        {
            var files = Directory.GetFiles(context.LogDirectory, "*.log*")
                .Where(f => f != context.CurrentLogFile)
                .OrderByDescending(f => File.GetCreationTimeUtc(f))
                .Skip(context.MaxFiles - 1) // Keep MaxFiles - 1 rotated files (plus current)
                .ToList();

            foreach (var file in files)
            {
                File.Delete(file);
            }

            return Task.CompletedTask;
        }

        private async Task<IReadOnlyList<LogEntry>> ReadLogFileAsync(
            string filePath,
            DateTime? since,
            DateTime? until,
            bool stdout,
            bool stderr)
        {
            var entries = new List<LogEntry>();
            Stream? stream = null;

            try
            {
                if (filePath.EndsWith(".gz"))
                {
                    var fileStream = File.OpenRead(filePath);
                    stream = new GZipStream(fileStream, CompressionMode.Decompress);
                }
                else
                {
                    stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<LogEntry>(line);
                        if (entry == null)
                        {
                            continue;
                        }

                        // Apply filters
                        if (since.HasValue && entry.Timestamp < since.Value)
                        {
                            continue;
                        }

                        if (until.HasValue && entry.Timestamp > until.Value)
                        {
                            continue;
                        }

                        if (!stdout && entry.Stream == "stdout")
                        {
                            continue;
                        }

                        if (!stderr && entry.Stream == "stderr")
                        {
                            continue;
                        }

                        entries.Add(entry);
                    }
                    catch (JsonException)
                    {
                        // Skip malformed entries
                    }
                }
            }
            finally
            {
                if (stream != null)
                {
                    await stream.DisposeAsync();
                }
            }

            return entries;
        }

        private void FlushAllBuffers(object? state)
        {
            _ = FlushAllBuffersAsync();
        }

        private async Task FlushAllBuffersAsync()
        {
            foreach (var context in _activeContainers.Values)
            {
                await context.WriteLock.WaitAsync();
                try
                {
                    if (context.Writer != null)
                    {
                        await context.Writer.FlushAsync();
                    }
                }
                finally
                {
                    context.WriteLock.Release();
                }
            }
        }

        private static int ParseConfigValue(Dictionary<string, string>? config, string key, int defaultValue)
        {
            if (config == null || !config.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            // Parse with size suffix support (e.g., "10m" for 10MB)
            if (int.TryParse(value, out var result))
            {
                return result;
            }

            var number = value.TrimEnd('k', 'K', 'm', 'M', 'g', 'G');
            if (!int.TryParse(number, out result))
            {
                return defaultValue;
            }

            return value.ToLowerInvariant().Last() switch
            {
                'k' => result * 1024,
                'm' => result * 1024 * 1024,
                'g' => result * 1024 * 1024 * 1024,
                _ => result
            };
        }

        private static bool ParseConfigBool(Dictionary<string, string>? config, string key, bool defaultValue)
        {
            if (config == null || !config.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            return value.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => defaultValue
            };
        }

        private static DateTime? ParseConfigDateTime(Dictionary<string, string>? config, string key)
        {
            if (config == null || !config.TryGetValue(key, out var value))
            {
                return null;
            }

            if (DateTime.TryParse(value, out var result))
            {
                return result;
            }

            // Support relative time (e.g., "1h", "30m")
            if (value.EndsWith('h') && int.TryParse(value.TrimEnd('h'), out var hours))
            {
                return DateTime.UtcNow.AddHours(-hours);
            }

            if (value.EndsWith('m') && int.TryParse(value.TrimEnd('m'), out var minutes))
            {
                return DateTime.UtcNow.AddMinutes(-minutes);
            }

            return null;
        }

        private static Dictionary<string, string>? ExtractLabels(Dictionary<string, string>? config)
        {
            if (config == null)
            {
                return null;
            }

            var labels = new Dictionary<string, string>();
            foreach (var kvp in config)
            {
                if (kvp.Key.StartsWith("labels:", StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.StartsWith("label:", StringComparison.OrdinalIgnoreCase))
                {
                    var labelKey = kvp.Key.Substring(kvp.Key.IndexOf(':') + 1);
                    labels[labelKey] = kvp.Value;
                }
            }

            return labels.Count > 0 ? labels : null;
        }

        #endregion
    }

    /// <summary>
    /// Context for an actively logging container.
    /// </summary>
    internal sealed class ContainerLogContext : IDisposable, IAsyncDisposable
    {
        public string ContainerId { get; init; } = string.Empty;
        public string ContainerName { get; init; } = string.Empty;
        public string LogDirectory { get; init; } = string.Empty;
        public string CurrentLogFile { get; init; } = string.Empty;
        public int MaxFileSize { get; init; }
        public int MaxFiles { get; init; }
        public bool CompressRotated { get; init; }
        public Dictionary<string, string>? Labels { get; init; }
        public DateTime StartedAt { get; init; }
        public StreamWriter? Writer { get; set; }
        public long BytesWritten { get; set; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public void Dispose()
        {
            Writer?.Dispose();
            WriteLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Writer != null)
            {
                await Writer.DisposeAsync();
            }
            WriteLock.Dispose();
        }
    }

    /// <summary>
    /// Represents a single log entry.
    /// </summary>
    public sealed class LogEntry
    {
        /// <summary>
        /// Timestamp when the log was generated.
        /// </summary>
        public DateTime Timestamp { get; init; }

        /// <summary>
        /// Log stream (stdout, stderr, system).
        /// </summary>
        public string Stream { get; init; } = "stdout";

        /// <summary>
        /// Log message content.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// Container ID that generated this log.
        /// </summary>
        public string? ContainerId { get; init; }

        /// <summary>
        /// Container name for easier identification.
        /// </summary>
        public string? ContainerName { get; init; }

        /// <summary>
        /// Optional labels attached to this log entry.
        /// </summary>
        public Dictionary<string, string>? Labels { get; init; }
    }

    /// <summary>
    /// Log driver capabilities as per Docker Log Driver plugin protocol.
    /// </summary>
    public sealed class LogDriverCapabilities
    {
        /// <summary>
        /// Whether the driver supports reading logs.
        /// </summary>
        public bool ReadLogs { get; init; }
    }
}
