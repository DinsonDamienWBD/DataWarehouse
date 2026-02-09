using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure
{
    #region Exception Handling Standards

    /// <summary>
    /// Provides standardized exception handling patterns for production-grade code.
    /// All exception handling in the codebase should use these patterns for consistency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>EXCEPTION HANDLING POLICY:</b>
    /// </para>
    /// <list type="number">
    /// <item>NEVER swallow exceptions silently - always log with context</item>
    /// <item>ALWAYS preserve stack traces - use ExceptionDispatchInfo or throw;</item>
    /// <item>ALWAYS add contextual information when re-throwing</item>
    /// <item>Use specific exception types over generic Exception</item>
    /// <item>Validate inputs at public API boundaries</item>
    /// </list>
    /// </remarks>
    public static class ExceptionHandler
    {
        private static readonly ConcurrentQueue<ExceptionRecord> _exceptionLog = new();
        private static Action<ExceptionRecord>? _exceptionCallback;
        private static int _maxLogSize = 10000;

        /// <summary>
        /// Sets the callback invoked when exceptions are logged.
        /// </summary>
        public static void SetExceptionCallback(Action<ExceptionRecord> callback)
        {
            _exceptionCallback = callback;
        }

        /// <summary>
        /// Sets the maximum number of exceptions to keep in the log.
        /// </summary>
        public static void SetMaxLogSize(int maxSize)
        {
            _maxLogSize = Math.Max(100, maxSize);
        }

        /// <summary>
        /// Gets recent exceptions from the log.
        /// </summary>
        public static IEnumerable<ExceptionRecord> GetRecentExceptions(int count = 100)
        {
            var arr = _exceptionLog.ToArray();
            var start = Math.Max(0, arr.Length - count);
            for (int i = start; i < arr.Length; i++)
            {
                yield return arr[i];
            }
        }

        /// <summary>
        /// Logs an exception with full context. Use this in catch blocks.
        /// </summary>
        /// <param name="ex">The exception to log.</param>
        /// <param name="message">Additional context message.</param>
        /// <param name="component">The component where the exception occurred.</param>
        /// <param name="operation">The operation being performed.</param>
        /// <param name="metadata">Additional metadata for debugging.</param>
        /// <param name="memberName">Auto-captured member name.</param>
        /// <param name="filePath">Auto-captured file path.</param>
        /// <param name="lineNumber">Auto-captured line number.</param>
        public static void LogException(
            Exception ex,
            string message,
            string component,
            string? operation = null,
            IDictionary<string, object>? metadata = null,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0)
        {
            var record = new ExceptionRecord
            {
                Exception = ex,
                Message = message,
                Component = component,
                Operation = operation ?? memberName,
                MemberName = memberName,
                FilePath = filePath,
                LineNumber = lineNumber,
                Timestamp = DateTimeOffset.UtcNow,
                ThreadId = Environment.CurrentManagedThreadId,
                Metadata = metadata != null
                    ? new Dictionary<string, object>(metadata)
                    : new Dictionary<string, object>()
            };

            // Add to log
            _exceptionLog.Enqueue(record);
            while (_exceptionLog.Count > _maxLogSize)
            {
                _exceptionLog.TryDequeue(out _);
            }

            // Invoke callback
            _exceptionCallback?.Invoke(record);
        }

        /// <summary>
        /// Executes an action with standardized exception handling.
        /// </summary>
        public static void Execute(
            Action action,
            string component,
            string operation,
            Action<Exception>? onError = null)
        {
            try
            {
                action();
            }
            catch (OperationCanceledException)
            {
                throw; // Don't log cancellation as an error
            }
            catch (Exception ex)
            {
                LogException(ex, $"Error in {operation}", component, operation);
                onError?.Invoke(ex);
                throw;
            }
        }

        /// <summary>
        /// Executes an action with standardized exception handling, returning a result.
        /// </summary>
        public static T Execute<T>(
            Func<T> action,
            string component,
            string operation,
            Action<Exception>? onError = null)
        {
            try
            {
                return action();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogException(ex, $"Error in {operation}", component, operation);
                onError?.Invoke(ex);
                throw;
            }
        }

        /// <summary>
        /// Executes an async operation with standardized exception handling.
        /// </summary>
        public static async Task ExecuteAsync(
            Func<Task> action,
            string component,
            string operation,
            Action<Exception>? onError = null,
            CancellationToken ct = default)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogException(ex, $"Error in {operation}", component, operation);
                onError?.Invoke(ex);
                throw;
            }
        }

        /// <summary>
        /// Executes an async operation with standardized exception handling, returning a result.
        /// </summary>
        public static async Task<T> ExecuteAsync<T>(
            Func<Task<T>> action,
            string component,
            string operation,
            Action<Exception>? onError = null,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogException(ex, $"Error in {operation}", component, operation);
                onError?.Invoke(ex);
                throw;
            }
        }

        /// <summary>
        /// Executes an action safely, catching and logging any exceptions without re-throwing.
        /// Use sparingly - only for non-critical operations like cleanup.
        /// </summary>
        public static bool TryExecute(
            Action action,
            string component,
            string operation,
            out Exception? exception)
        {
            try
            {
                action();
                exception = null;
                return true;
            }
            catch (Exception ex)
            {
                LogException(ex, $"Non-critical error in {operation}", component, operation);
                exception = ex;
                return false;
            }
        }

        /// <summary>
        /// Executes an async action safely, catching and logging any exceptions without re-throwing.
        /// </summary>
        public static async Task<(bool Success, Exception? Exception)> TryExecuteAsync(
            Func<Task> action,
            string component,
            string operation)
        {
            try
            {
                await action().ConfigureAwait(false);
                return (true, null);
            }
            catch (Exception ex)
            {
                LogException(ex, $"Non-critical error in {operation}", component, operation);
                return (false, ex);
            }
        }
    }

    /// <summary>
    /// Record of an exception for logging and diagnostics.
    /// </summary>
    public sealed class ExceptionRecord
    {
        /// <summary>The actual exception.</summary>
        public required Exception Exception { get; init; }

        /// <summary>Context message.</summary>
        public required string Message { get; init; }

        /// <summary>Component where exception occurred.</summary>
        public required string Component { get; init; }

        /// <summary>Operation being performed.</summary>
        public string? Operation { get; init; }

        /// <summary>Member name (auto-captured).</summary>
        public string MemberName { get; init; } = "";

        /// <summary>File path (auto-captured).</summary>
        public string FilePath { get; init; } = "";

        /// <summary>Line number (auto-captured).</summary>
        public int LineNumber { get; init; }

        /// <summary>When the exception occurred.</summary>
        public DateTimeOffset Timestamp { get; init; }

        /// <summary>Thread ID where exception occurred.</summary>
        public int ThreadId { get; init; }

        /// <summary>Additional metadata.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    #endregion

    #region Specialized Domain Exceptions

    /// <summary>
    /// Exception for storage-related errors.
    /// Extends DataWarehouseException with storage-specific context.
    /// </summary>
    public sealed class StorageOperationException : DataWarehouseException
    {
        /// <summary>The storage URI involved.</summary>
        public Uri? StorageUri { get; init; }

        /// <summary>The container ID involved.</summary>
        public string? ContainerId { get; init; }

        /// <summary>Whether this is a transient failure that can be retried.</summary>
        public bool IsTransient { get; init; }

        /// <summary>Suggested retry delay if transient.</summary>
        public TimeSpan? RetryAfter { get; init; }

        public StorageOperationException(ErrorCode errorCode, string message, string? correlationId = null)
            : base(errorCode, message, correlationId)
        {
        }

        public StorageOperationException(ErrorCode errorCode, string message, Exception innerException, string? correlationId = null)
            : base(errorCode, message, innerException, correlationId)
        {
        }

        public static StorageOperationException NotFound(string path, string? containerId = null) =>
            new(ErrorCode.NotFound, $"Storage item not found: {path}")
            {
                StorageUri = Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var uri) ? uri : null,
                ContainerId = containerId,
                IsTransient = false
            };

        public static StorageOperationException QuotaExceeded(string containerId, long current, long limit) =>
            new(ErrorCode.ResourceExhausted, $"Storage quota exceeded for container '{containerId}': {current}/{limit} bytes")
            {
                ContainerId = containerId,
                IsTransient = false
            };

        public static StorageOperationException Unavailable(string provider, Exception? inner = null)
        {
            return inner != null
                ? new StorageOperationException(ErrorCode.ServiceUnavailable, $"Storage provider '{provider}' is unavailable", inner)
                  { IsTransient = true, RetryAfter = TimeSpan.FromSeconds(30) }
                : new StorageOperationException(ErrorCode.ServiceUnavailable, $"Storage provider '{provider}' is unavailable")
                  { IsTransient = true, RetryAfter = TimeSpan.FromSeconds(30) };
        }
    }

    /// <summary>
    /// Exception for plugin-related errors.
    /// </summary>
    public sealed class PluginOperationException : DataWarehouseException
    {
        /// <summary>The plugin identifier.</summary>
        public string PluginId { get; }

        /// <summary>The plugin version if known.</summary>
        public string? PluginVersion { get; init; }

        /// <summary>Whether this is a transient failure.</summary>
        public bool IsTransient { get; init; }

        public PluginOperationException(string pluginId, ErrorCode errorCode, string message, string? correlationId = null)
            : base(errorCode, message, correlationId)
        {
            PluginId = pluginId;
        }

        public PluginOperationException(string pluginId, ErrorCode errorCode, string message, Exception innerException, string? correlationId = null)
            : base(errorCode, message, innerException, correlationId)
        {
            PluginId = pluginId;
        }

        public static PluginOperationException LoadFailed(string pluginId, Exception inner) =>
            new(pluginId, ErrorCode.InternalError, $"Failed to load plugin '{pluginId}'", inner);

        public static PluginOperationException InitializationFailed(string pluginId, Exception inner) =>
            new(pluginId, ErrorCode.InternalError, $"Plugin '{pluginId}' initialization failed", inner);

        public static PluginOperationException NotFound(string pluginId) =>
            new(pluginId, ErrorCode.NotFound, $"Plugin '{pluginId}' not found");
    }

    /// <summary>
    /// Exception for security-related errors.
    /// </summary>
    public sealed class SecurityOperationException : DataWarehouseException
    {
        /// <summary>The principal ID involved.</summary>
        public string? PrincipalId { get; init; }

        /// <summary>The resource ID involved.</summary>
        public string? ResourceId { get; init; }

        /// <summary>The required permission.</summary>
        public string? RequiredPermission { get; init; }

        public SecurityOperationException(ErrorCode errorCode, string message, string? correlationId = null)
            : base(errorCode, message, correlationId)
        {
        }

        public static SecurityOperationException AccessDenied(string principalId, string resourceId, string permission) =>
            new(ErrorCode.Forbidden, $"Access denied: '{principalId}' lacks '{permission}' permission on '{resourceId}'")
            {
                PrincipalId = principalId,
                ResourceId = resourceId,
                RequiredPermission = permission
            };

        public static SecurityOperationException AuthenticationFailed(string reason) =>
            new(ErrorCode.Unauthorized, $"Authentication failed: {reason}");

        public static SecurityOperationException TokenExpired() =>
            new(ErrorCode.Unauthorized, "Security token has expired");

        public static SecurityOperationException PathTraversalAttempt(string path) =>
            new(ErrorCode.Forbidden, $"Path traversal attack detected: {path}");
    }

    /// <summary>
    /// Exception for configuration errors.
    /// </summary>
    public sealed class ConfigurationOperationException : DataWarehouseException
    {
        /// <summary>The configuration key involved.</summary>
        public string? ConfigKey { get; init; }

        /// <summary>The invalid value if applicable.</summary>
        public object? InvalidConfigValue { get; init; }

        public ConfigurationOperationException(string message, string? configKey = null, string? correlationId = null)
            : base(ErrorCode.InvalidInput, message, correlationId)
        {
            ConfigKey = configKey;
        }

        public static ConfigurationOperationException MissingRequired(string key) =>
            new($"Required configuration key '{key}' is missing") { ConfigKey = key };

        public static ConfigurationOperationException InvalidValue(string key, object? value, string reason) =>
            new($"Configuration key '{key}' has invalid value: {reason}")
            {
                ConfigKey = key,
                InvalidConfigValue = value
            };
    }

    /// <summary>
    /// Exception for rate limiting.
    /// </summary>
    public sealed class RateLimitOperationException : DataWarehouseException
    {
        /// <summary>The client that was rate limited.</summary>
        public string ClientId { get; }

        /// <summary>Current request rate.</summary>
        public int CurrentRate { get; init; }

        /// <summary>Maximum allowed rate.</summary>
        public int MaxRate { get; init; }

        /// <summary>Time to wait before retrying.</summary>
        public TimeSpan RetryAfter { get; }

        public RateLimitOperationException(string clientId, TimeSpan retryAfter, string? correlationId = null)
            : base(ErrorCode.RateLimited, $"Rate limit exceeded for client '{clientId}'", correlationId)
        {
            ClientId = clientId;
            RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// Exception for compliance violations.
    /// </summary>
    public sealed class ComplianceViolationException : DataWarehouseException
    {
        /// <summary>The compliance framework violated.</summary>
        public ComplianceFramework Framework { get; }

        /// <summary>The specific requirement violated.</summary>
        public string Requirement { get; }

        public ComplianceViolationException(ComplianceFramework framework, string requirement, string message, string? correlationId = null)
            : base(ErrorCode.Forbidden, $"[{framework}] Compliance violation - {requirement}: {message}", correlationId)
        {
            Framework = framework;
            Requirement = requirement;
        }
    }

    /// <summary>
    /// Exception thrown when FailClosed recovery behavior is triggered.
    /// Indicates that corruption was detected and the affected block/shard has been sealed.
    /// No reads or writes are permitted until manual intervention resolves the issue.
    /// </summary>
    public sealed class FailClosedCorruptionException : DataWarehouseException
    {
        /// <summary>
        /// The block ID that has been sealed due to corruption.
        /// </summary>
        public Guid BlockId { get; }

        /// <summary>
        /// The version of the object that was affected.
        /// </summary>
        public int Version { get; }

        /// <summary>
        /// The expected hash from the manifest.
        /// </summary>
        public string ExpectedHash { get; }

        /// <summary>
        /// The actual hash computed from the corrupted data.
        /// </summary>
        public string ActualHash { get; }

        /// <summary>
        /// The storage instance where corruption was detected.
        /// </summary>
        public string AffectedInstance { get; }

        /// <summary>
        /// List of affected shard indices, if applicable.
        /// Null if the entire block is affected.
        /// </summary>
        public IReadOnlyList<int>? AffectedShards { get; }

        /// <summary>
        /// Timestamp when the corruption was detected and block was sealed.
        /// </summary>
        public DateTimeOffset SealedAt { get; }

        /// <summary>
        /// Incident ID for tracking and audit purposes.
        /// </summary>
        public Guid IncidentId { get; }

        /// <summary>
        /// Creates a new FailClosedCorruptionException with full details.
        /// </summary>
        /// <param name="blockId">The block ID that was sealed.</param>
        /// <param name="version">The object version.</param>
        /// <param name="expectedHash">Expected hash from manifest.</param>
        /// <param name="actualHash">Actual computed hash.</param>
        /// <param name="affectedInstance">Storage instance where corruption was detected.</param>
        /// <param name="affectedShards">Optional list of affected shard indices.</param>
        /// <param name="incidentId">Incident ID for tracking.</param>
        /// <param name="correlationId">Correlation ID for tracing.</param>
        public FailClosedCorruptionException(
            Guid blockId,
            int version,
            string expectedHash,
            string actualHash,
            string affectedInstance,
            IReadOnlyList<int>? affectedShards = null,
            Guid? incidentId = null,
            string? correlationId = null)
            : base(
                ErrorCode.DataCorruption,
                $"FAIL CLOSED: Corruption detected in block {blockId} version {version}. " +
                $"Block has been sealed - no reads or writes permitted. " +
                $"Expected hash: {expectedHash}, Actual hash: {actualHash}. " +
                $"Manual intervention required. Incident ID: {incidentId ?? Guid.NewGuid()}",
                correlationId)
        {
            BlockId = blockId;
            Version = version;
            ExpectedHash = expectedHash;
            ActualHash = actualHash;
            AffectedInstance = affectedInstance;
            AffectedShards = affectedShards;
            SealedAt = DateTimeOffset.UtcNow;
            IncidentId = incidentId ?? Guid.NewGuid();
        }

        /// <summary>
        /// Creates a FailClosedCorruptionException for a specific block.
        /// </summary>
        public static FailClosedCorruptionException Create(
            Guid blockId,
            int version,
            string expectedHash,
            string actualHash,
            string affectedInstance,
            IReadOnlyList<int>? affectedShards = null)
        {
            return new FailClosedCorruptionException(
                blockId,
                version,
                expectedHash,
                actualHash,
                affectedInstance,
                affectedShards);
        }
    }

    /// <summary>
    /// Supported compliance frameworks.
    /// </summary>
    public enum ComplianceFramework
    {
        HIPAA,
        PCI_DSS,
        SOX,
        GDPR,
        FedRAMP,
        SOC2,
        ISO27001
    }

    #endregion

    #region Exception Handling Extensions

    /// <summary>
    /// Extension methods for standardized exception handling.
    /// </summary>
    public static class ExceptionHandlingExtensions
    {
        /// <summary>
        /// Determines if an exception represents a transient failure that can be retried.
        /// </summary>
        public static bool IsTransientFailure(this Exception exception)
        {
            return exception switch
            {
                StorageOperationException soe => soe.IsTransient,
                PluginOperationException poe => poe.IsTransient,
                RateLimitOperationException => true,
                TimeoutException => true,
                TaskCanceledException => false, // User cancellation, don't retry
                OperationCanceledException => false,
                System.Net.Http.HttpRequestException => true,
                System.IO.IOException => true,
                _ => false
            };
        }

        /// <summary>
        /// Gets the suggested retry delay for a transient exception.
        /// </summary>
        public static TimeSpan GetRetryDelay(this Exception exception, int attempt = 1)
        {
            // Check for specific retry after values
            if (exception is StorageOperationException soe && soe.RetryAfter.HasValue)
            {
                return soe.RetryAfter.Value;
            }

            if (exception is RateLimitOperationException rle)
            {
                return rle.RetryAfter;
            }

            // Exponential backoff with jitter
            var baseDelay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
            return baseDelay + jitter;
        }
    }

    #endregion
}
