using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
            return _exceptionLog.ToArray()[^Math.Min(count, _exceptionLog.Count)..];
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

    #region Standardized Domain Exceptions

    /// <summary>
    /// Base exception for all DataWarehouse domain exceptions.
    /// </summary>
    public abstract class DataWarehouseException : Exception
    {
        /// <summary>Error code for programmatic handling.</summary>
        public string ErrorCode { get; }

        /// <summary>Component where error originated.</summary>
        public string Component { get; }

        /// <summary>Whether this error is transient and can be retried.</summary>
        public bool IsTransient { get; }

        /// <summary>Suggested retry delay if transient.</summary>
        public TimeSpan? RetryAfter { get; init; }

        protected DataWarehouseException(
            string message,
            string errorCode,
            string component,
            bool isTransient = false,
            Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Component = component;
            IsTransient = isTransient;
        }
    }

    /// <summary>
    /// Exception for storage-related errors.
    /// </summary>
    public sealed class StorageException : DataWarehouseException
    {
        public Uri? StorageUri { get; init; }
        public string? ContainerId { get; init; }

        public StorageException(
            string message,
            string errorCode = "STORAGE_ERROR",
            string component = "Storage",
            bool isTransient = false,
            Exception? innerException = null)
            : base(message, errorCode, component, isTransient, innerException)
        {
        }

        public static StorageException NotFound(string path, string? containerId = null) =>
            new($"Storage item not found: {path}", "STORAGE_NOT_FOUND", "Storage")
            {
                StorageUri = Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var uri) ? uri : null,
                ContainerId = containerId
            };

        public static StorageException QuotaExceeded(string containerId, long current, long limit) =>
            new($"Storage quota exceeded for container '{containerId}': {current}/{limit} bytes",
                "STORAGE_QUOTA_EXCEEDED", "Storage")
            {
                ContainerId = containerId
            };

        public static StorageException Unavailable(string provider, Exception? inner = null) =>
            new($"Storage provider '{provider}' is unavailable", "STORAGE_UNAVAILABLE", "Storage",
                isTransient: true, innerException: inner)
            {
                RetryAfter = TimeSpan.FromSeconds(30)
            };
    }

    /// <summary>
    /// Exception for plugin-related errors.
    /// </summary>
    public sealed class PluginException : DataWarehouseException
    {
        public string PluginId { get; }
        public string? PluginVersion { get; init; }

        public PluginException(
            string message,
            string pluginId,
            string errorCode = "PLUGIN_ERROR",
            bool isTransient = false,
            Exception? innerException = null)
            : base(message, errorCode, $"Plugin:{pluginId}", isTransient, innerException)
        {
            PluginId = pluginId;
        }

        public static PluginException LoadFailed(string pluginId, Exception inner) =>
            new($"Failed to load plugin '{pluginId}'", pluginId, "PLUGIN_LOAD_FAILED",
                innerException: inner);

        public static PluginException InitializationFailed(string pluginId, Exception inner) =>
            new($"Plugin '{pluginId}' initialization failed", pluginId, "PLUGIN_INIT_FAILED",
                innerException: inner);

        public static PluginException NotFound(string pluginId) =>
            new($"Plugin '{pluginId}' not found", pluginId, "PLUGIN_NOT_FOUND");
    }

    /// <summary>
    /// Exception for security-related errors.
    /// </summary>
    public sealed class SecurityException : DataWarehouseException
    {
        public string? PrincipalId { get; init; }
        public string? ResourceId { get; init; }
        public string? RequiredPermission { get; init; }

        public SecurityException(
            string message,
            string errorCode = "SECURITY_ERROR",
            Exception? innerException = null)
            : base(message, errorCode, "Security", isTransient: false, innerException)
        {
        }

        public static SecurityException AccessDenied(string principalId, string resourceId, string permission) =>
            new($"Access denied: '{principalId}' lacks '{permission}' permission on '{resourceId}'",
                "ACCESS_DENIED")
            {
                PrincipalId = principalId,
                ResourceId = resourceId,
                RequiredPermission = permission
            };

        public static SecurityException AuthenticationFailed(string reason) =>
            new($"Authentication failed: {reason}", "AUTH_FAILED");

        public static SecurityException TokenExpired() =>
            new("Security token has expired", "TOKEN_EXPIRED");

        public static SecurityException PathTraversalAttempt(string path) =>
            new($"Path traversal attack detected: {path}", "PATH_TRAVERSAL");
    }

    /// <summary>
    /// Exception for configuration errors.
    /// </summary>
    public sealed class ConfigurationException : DataWarehouseException
    {
        public string? ConfigKey { get; init; }
        public object? InvalidValue { get; init; }

        public ConfigurationException(
            string message,
            string? configKey = null,
            Exception? innerException = null)
            : base(message, "CONFIG_ERROR", "Configuration", isTransient: false, innerException)
        {
            ConfigKey = configKey;
        }

        public static ConfigurationException MissingRequired(string key) =>
            new($"Required configuration key '{key}' is missing") { ConfigKey = key };

        public static ConfigurationException InvalidValue(string key, object? value, string reason) =>
            new($"Configuration key '{key}' has invalid value: {reason}")
            {
                ConfigKey = key,
                InvalidValue = value
            };
    }

    /// <summary>
    /// Exception for rate limiting.
    /// </summary>
    public sealed class RateLimitException : DataWarehouseException
    {
        public string ClientId { get; }
        public int CurrentRate { get; init; }
        public int MaxRate { get; init; }

        public RateLimitException(
            string clientId,
            TimeSpan retryAfter)
            : base($"Rate limit exceeded for client '{clientId}'", "RATE_LIMITED", "RateLimiter",
                isTransient: true)
        {
            ClientId = clientId;
            RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// Exception for compliance violations.
    /// </summary>
    public sealed class ComplianceException : DataWarehouseException
    {
        public ComplianceFramework Framework { get; }
        public string Requirement { get; }

        public ComplianceException(
            ComplianceFramework framework,
            string requirement,
            string message)
            : base($"[{framework}] Compliance violation - {requirement}: {message}",
                $"COMPLIANCE_{framework}", "Compliance")
        {
            Framework = framework;
            Requirement = requirement;
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
        /// Wraps an exception with additional context.
        /// </summary>
        public static DataWarehouseException WithContext<T>(
            this T exception,
            string additionalContext) where T : DataWarehouseException
        {
            // Re-create with additional context in message
            return exception switch
            {
                StorageException se => new StorageException(
                    $"{se.Message} | Context: {additionalContext}",
                    se.ErrorCode, se.Component, se.IsTransient, se)
                { StorageUri = se.StorageUri, ContainerId = se.ContainerId, RetryAfter = se.RetryAfter },

                PluginException pe => new PluginException(
                    $"{pe.Message} | Context: {additionalContext}",
                    pe.PluginId, pe.ErrorCode, pe.IsTransient, pe)
                { PluginVersion = pe.PluginVersion, RetryAfter = pe.RetryAfter },

                _ => exception
            };
        }

        /// <summary>
        /// Determines if an exception represents a transient failure that can be retried.
        /// </summary>
        public static bool IsTransientFailure(this Exception exception)
        {
            return exception switch
            {
                DataWarehouseException dwe => dwe.IsTransient,
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
            if (exception is DataWarehouseException dwe && dwe.RetryAfter.HasValue)
            {
                return dwe.RetryAfter.Value;
            }

            // Exponential backoff with jitter
            var baseDelay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
            return baseDelay + jitter;
        }
    }

    #endregion
}
