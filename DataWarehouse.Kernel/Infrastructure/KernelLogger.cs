using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Kernel.Infrastructure
{
    /// <summary>
    /// Production-ready structured logging infrastructure for the kernel.
    /// Supports multiple log targets, structured data, and log levels.
    /// </summary>
    public class KernelLogger : IKernelContext
    {
        private readonly ConcurrentQueue<LogEntry> _logBuffer = new();
        private readonly List<ILogTarget> _targets = new();
        private readonly KernelLoggerConfig _config;
        private readonly object _targetLock = new();
        private readonly Timer _flushTimer;
        private long _totalLogs;
        private long _errorCount;
        private long _warningCount;

        public string Name { get; }
        public LogLevel MinimumLevel { get; set; }

        /// <summary>
        /// Creates a new kernel logger with optional configuration.
        /// </summary>
        public KernelLogger(string name = "Kernel", KernelLoggerConfig? config = null)
        {
            Name = name;
            _config = config ?? new KernelLoggerConfig();
            MinimumLevel = _config.MinimumLevel;

            // Add default console target if enabled
            if (_config.EnableConsoleLogging)
            {
                AddTarget(new ConsoleLogTarget());
            }

            // Start background flush timer
            _flushTimer = new Timer(
                _ => Flush(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }

        #region IKernelContext Implementation

        public void LogInfo(string message)
        {
            Log(LogLevel.Information, message);
        }

        public void LogError(string message, Exception? ex = null)
        {
            Log(LogLevel.Error, message, ex);
        }

        public void LogWarning(string message)
        {
            Log(LogLevel.Warning, message);
        }

        public void LogDebug(string message)
        {
            Log(LogLevel.Debug, message);
        }

        #endregion

        #region Structured Logging

        /// <summary>
        /// Logs a message with structured data.
        /// </summary>
        public void Log(LogLevel level, string message, Exception? exception = null, Dictionary<string, object>? properties = null)
        {
            if (level < MinimumLevel)
                return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                LoggerName = Name,
                Message = message,
                Exception = exception,
                Properties = properties ?? new Dictionary<string, object>(),
                ThreadId = Environment.CurrentManagedThreadId
            };

            Interlocked.Increment(ref _totalLogs);

            if (level == LogLevel.Error || level == LogLevel.Critical)
                Interlocked.Increment(ref _errorCount);
            else if (level == LogLevel.Warning)
                Interlocked.Increment(ref _warningCount);

            if (_config.BufferLogs)
            {
                _logBuffer.Enqueue(entry);

                // Immediate flush for errors/critical
                if (level >= LogLevel.Error)
                {
                    Flush();
                }
            }
            else
            {
                WriteToTargets(entry);
            }
        }

        /// <summary>
        /// Logs with structured properties.
        /// </summary>
        public void Log(LogLevel level, string message, params (string Key, object Value)[] properties)
        {
            var props = properties.ToDictionary(p => p.Key, p => p.Value);
            Log(level, message, null, props);
        }

        /// <summary>
        /// Creates a scoped logger with additional context.
        /// </summary>
        public IDisposable BeginScope(string scopeName, Dictionary<string, object>? properties = null)
        {
            return new LogScope(this, scopeName, properties);
        }

        #endregion

        #region Log Targets

        /// <summary>
        /// Adds a log target.
        /// </summary>
        public void AddTarget(ILogTarget target)
        {
            lock (_targetLock)
            {
                _targets.Add(target);
            }
        }

        /// <summary>
        /// Removes a log target.
        /// </summary>
        public void RemoveTarget(ILogTarget target)
        {
            lock (_targetLock)
            {
                _targets.Remove(target);
            }
        }

        private void WriteToTargets(LogEntry entry)
        {
            List<ILogTarget> targets;
            lock (_targetLock)
            {
                targets = _targets.ToList();
            }

            foreach (var target in targets)
            {
                try
                {
                    target.Write(entry);
                }
                catch
                {
                    // Don't let logging failures crash the application
                }
            }
        }

        /// <summary>
        /// Flushes buffered logs to targets.
        /// </summary>
        public void Flush()
        {
            while (_logBuffer.TryDequeue(out var entry))
            {
                WriteToTargets(entry);
            }

            List<ILogTarget> targets;
            lock (_targetLock)
            {
                targets = _targets.ToList();
            }

            foreach (var target in targets)
            {
                try
                {
                    target.Flush();
                }
                catch
                {
                    // Ignore flush errors
                }
            }
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Gets logging statistics.
        /// </summary>
        public LoggingStats GetStats()
        {
            return new LoggingStats
            {
                TotalLogs = Interlocked.Read(ref _totalLogs),
                ErrorCount = Interlocked.Read(ref _errorCount),
                WarningCount = Interlocked.Read(ref _warningCount),
                BufferedCount = _logBuffer.Count,
                TargetCount = _targets.Count
            };
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            _flushTimer.Dispose();
            Flush();

            lock (_targetLock)
            {
                foreach (var target in _targets.OfType<IDisposable>())
                {
                    target.Dispose();
                }
                _targets.Clear();
            }
        }

        #endregion
    }

    #region Log Entry

    /// <summary>
    /// Represents a structured log entry.
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string LoggerName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new();
        public int ThreadId { get; set; }
        public string? ScopeName { get; set; }
    }

    /// <summary>
    /// Log level enumeration.
    /// </summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Critical = 5,
        None = 6
    }

    #endregion

    #region Log Targets

    /// <summary>
    /// Interface for log output targets.
    /// </summary>
    public interface ILogTarget
    {
        void Write(LogEntry entry);
        void Flush();
    }

    /// <summary>
    /// Console log target with color support.
    /// </summary>
    public class ConsoleLogTarget : ILogTarget
    {
        private readonly object _consoleLock = new();

        public void Write(LogEntry entry)
        {
            lock (_consoleLock)
            {
                var originalColor = Console.ForegroundColor;

                Console.ForegroundColor = entry.Level switch
                {
                    LogLevel.Critical => ConsoleColor.Magenta,
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Warning => ConsoleColor.Yellow,
                    LogLevel.Information => ConsoleColor.White,
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Trace => ConsoleColor.DarkGray,
                    _ => ConsoleColor.White
                };

                var levelStr = entry.Level.ToString().ToUpperInvariant()[..3];
                Console.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{levelStr}] [{entry.LoggerName}] {entry.Message}");

                if (entry.Exception != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"  Exception: {entry.Exception.GetType().Name}: {entry.Exception.Message}");
                    if (entry.Exception.StackTrace != null)
                    {
                        Console.WriteLine($"  {entry.Exception.StackTrace.Split('\n').FirstOrDefault()?.Trim()}");
                    }
                }

                if (entry.Properties.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    foreach (var prop in entry.Properties.Take(5))
                    {
                        Console.WriteLine($"  {prop.Key}: {prop.Value}");
                    }
                }

                Console.ForegroundColor = originalColor;
            }
        }

        public void Flush()
        {
            // Console output is already flushed automatically
        }
    }

    /// <summary>
    /// File log target with rotation support.
    /// </summary>
    public class FileLogTarget : ILogTarget, IDisposable
    {
        private readonly string _basePath;
        private readonly long _maxFileSize;
        private readonly int _maxFiles;
        private StreamWriter? _writer;
        private string _currentFile = string.Empty;
        private long _currentSize;
        private readonly object _fileLock = new();

        public FileLogTarget(string basePath, long maxFileSize = 10 * 1024 * 1024, int maxFiles = 10)
        {
            _basePath = basePath;
            _maxFileSize = maxFileSize;
            _maxFiles = maxFiles;
            EnsureWriter();
        }

        public void Write(LogEntry entry)
        {
            lock (_fileLock)
            {
                EnsureWriter();
                RotateIfNeeded();

                var json = JsonSerializer.Serialize(new
                {
                    entry.Timestamp,
                    Level = entry.Level.ToString(),
                    entry.LoggerName,
                    entry.Message,
                    Exception = entry.Exception?.ToString(),
                    entry.Properties,
                    entry.ThreadId
                });

                _writer?.WriteLine(json);
                _currentSize += json.Length + Environment.NewLine.Length;
            }
        }

        public void Flush()
        {
            lock (_fileLock)
            {
                _writer?.Flush();
            }
        }

        private void EnsureWriter()
        {
            if (_writer != null) return;

            var dir = Path.GetDirectoryName(_basePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _currentFile = $"{_basePath}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.log";
            _writer = new StreamWriter(_currentFile, append: true) { AutoFlush = true };
            _currentSize = new FileInfo(_currentFile).Length;
        }

        private void RotateIfNeeded()
        {
            if (_currentSize < _maxFileSize) return;

            _writer?.Dispose();
            _writer = null;

            // Clean up old files
            var files = Directory.GetFiles(Path.GetDirectoryName(_basePath) ?? ".", $"{Path.GetFileName(_basePath)}*.log")
                .OrderByDescending(f => f)
                .Skip(_maxFiles)
                .ToList();

            foreach (var file in files)
            {
                try { File.Delete(file); } catch { /* Best-effort cleanup */ }
            }

            EnsureWriter();
        }

        public void Dispose()
        {
            lock (_fileLock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    /// <summary>
    /// In-memory log target for testing and diagnostics.
    /// </summary>
    public class MemoryLogTarget : ILogTarget
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private readonly int _maxEntries;

        public MemoryLogTarget(int maxEntries = 1000)
        {
            _maxEntries = maxEntries;
        }

        public void Write(LogEntry entry)
        {
            _entries.Enqueue(entry);

            while (_entries.Count > _maxEntries)
            {
                _entries.TryDequeue(out _);
            }
        }

        public void Flush()
        {
            // In-memory target has no buffering to flush
        }

        public IReadOnlyList<LogEntry> GetEntries() => _entries.ToList();

        public IReadOnlyList<LogEntry> GetErrors() =>
            _entries.Where(e => e.Level >= LogLevel.Error).ToList();

        public void Clear()
        {
            while (_entries.TryDequeue(out _)) { }
        }
    }

    #endregion

    #region Configuration and Utilities

    /// <summary>
    /// Configuration for the kernel logger.
    /// </summary>
    public class KernelLoggerConfig
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

        /// <summary>
        /// Enables console logging. Defaults to <c>false</c> — library code must not write to
        /// <see cref="Console"/> in production service/container deployments (Cat 8, finding 951).
        /// Set to <c>true</c> only for local development or CLI tools where a console is present.
        /// </summary>
        public bool EnableConsoleLogging { get; set; } = false;
        public bool BufferLogs { get; set; } = true;
        public string? LogFilePath { get; set; }
        public long MaxLogFileSize { get; set; } = 10 * 1024 * 1024;
        public int MaxLogFiles { get; set; } = 10;
    }

    /// <summary>
    /// Logging statistics.
    /// </summary>
    public class LoggingStats
    {
        public long TotalLogs { get; set; }
        public long ErrorCount { get; set; }
        public long WarningCount { get; set; }
        public int BufferedCount { get; set; }
        public int TargetCount { get; set; }
    }

    /// <summary>
    /// Log scope for contextual logging.
    /// </summary>
    internal class LogScope : IDisposable
    {
        private readonly KernelLogger _logger;
        private readonly string _scopeName;

        public LogScope(KernelLogger logger, string scopeName, Dictionary<string, object>? properties)
        {
            _logger = logger;
            _scopeName = scopeName;
            _logger.Log(LogLevel.Debug, $"Entering scope: {scopeName}", null, properties);
        }

        public void Dispose()
        {
            _logger.Log(LogLevel.Debug, $"Exiting scope: {_scopeName}");
        }
    }

    #endregion

    #region Kernel Context Interface

    /// <summary>
    /// Interface for kernel context (logging, configuration, services).
    /// </summary>
    public interface IKernelContext
    {
        void LogInfo(string message);
        void LogError(string message, Exception? ex = null);
        void LogWarning(string message);
        void LogDebug(string message);
    }

    /// <summary>
    /// Null kernel context for testing (no-op logging).
    /// </summary>
    public class NullKernelContext : IKernelContext
    {
        public static NullKernelContext Instance { get; } = new();

        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }
    }

    #endregion
}
