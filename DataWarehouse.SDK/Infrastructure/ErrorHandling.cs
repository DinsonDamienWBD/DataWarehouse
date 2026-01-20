using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Infrastructure
{
    /// <summary>
    /// Standard error codes for DataWarehouse operations.
    /// </summary>
    public enum ErrorCode
    {
        /// <summary>
        /// Input provided was invalid or malformed.
        /// </summary>
        InvalidInput = 1000,

        /// <summary>
        /// The requested resource was not found.
        /// </summary>
        NotFound = 1001,

        /// <summary>
        /// Authentication failed or credentials are missing.
        /// </summary>
        Unauthorized = 1002,

        /// <summary>
        /// Access to the resource is forbidden.
        /// </summary>
        Forbidden = 1003,

        /// <summary>
        /// The operation timed out.
        /// </summary>
        Timeout = 1004,

        /// <summary>
        /// Rate limit has been exceeded.
        /// </summary>
        RateLimited = 1005,

        /// <summary>
        /// The service is temporarily unavailable.
        /// </summary>
        ServiceUnavailable = 1006,

        /// <summary>
        /// An internal error occurred.
        /// </summary>
        InternalError = 1007,

        /// <summary>
        /// Validation of input or data failed.
        /// </summary>
        ValidationFailed = 1008,

        /// <summary>
        /// A resource (memory, connections, etc.) has been exhausted.
        /// </summary>
        ResourceExhausted = 1009
    }

    /// <summary>
    /// Custom exception for DataWarehouse operations with enhanced context and error tracking.
    /// Thread-safe and designed for production use.
    /// </summary>
    public class DataWarehouseException : Exception
    {
        private static readonly Regex SensitivePatternRegex = new(
            @"(password|secret|key|token|credential|apikey|api_key|auth|bearer|jwt|session|private)[\s]*[=:]\s*['""]?([^'""&\s]{3,})['""]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        private static readonly Regex EmailPatternRegex = new(
            @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        private static readonly Regex CreditCardPatternRegex = new(
            @"\b(?:\d{4}[-\s]?){3}\d{4}\b",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        private readonly ConcurrentDictionary<string, object> _context;

        /// <summary>
        /// Gets the error code associated with this exception.
        /// </summary>
        public ErrorCode ErrorCode { get; }

        /// <summary>
        /// Gets the unique correlation ID for tracking this error across systems.
        /// </summary>
        public string CorrelationId { get; }

        /// <summary>
        /// Gets the contextual information associated with this exception.
        /// The returned dictionary is a snapshot and modifications will not affect the exception.
        /// </summary>
        public IReadOnlyDictionary<string, object> Context => new Dictionary<string, object>(_context);

        /// <summary>
        /// Gets the timestamp when this exception was created.
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DataWarehouseException"/> class.
        /// </summary>
        /// <param name="errorCode">The error code.</param>
        /// <param name="message">The error message.</param>
        /// <param name="correlationId">Optional correlation ID. If not provided, a new one is generated.</param>
        public DataWarehouseException(ErrorCode errorCode, string message, string? correlationId = null)
            : base(SanitizeSensitiveData(message))
        {
            ErrorCode = errorCode;
            CorrelationId = correlationId ?? GenerateCorrelationId();
            Timestamp = DateTimeOffset.UtcNow;
            _context = new ConcurrentDictionary<string, object>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DataWarehouseException"/> class with an inner exception.
        /// </summary>
        /// <param name="errorCode">The error code.</param>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        /// <param name="correlationId">Optional correlation ID. If not provided, a new one is generated.</param>
        public DataWarehouseException(ErrorCode errorCode, string message, Exception innerException, string? correlationId = null)
            : base(SanitizeSensitiveData(message), innerException)
        {
            ErrorCode = errorCode;
            CorrelationId = correlationId ?? GenerateCorrelationId();
            Timestamp = DateTimeOffset.UtcNow;
            _context = new ConcurrentDictionary<string, object>();
        }

        /// <summary>
        /// Adds contextual information to the exception.
        /// </summary>
        /// <param name="key">The context key.</param>
        /// <param name="value">The context value.</param>
        /// <returns>This exception for fluent chaining.</returns>
        public DataWarehouseException WithContext(string key, object value)
        {
            ArgumentNullException.ThrowIfNull(key);
            _context[key] = value ?? "<null>";
            return this;
        }

        /// <summary>
        /// Adds multiple context entries to the exception.
        /// </summary>
        /// <param name="contextEntries">The context entries to add.</param>
        /// <returns>This exception for fluent chaining.</returns>
        public DataWarehouseException WithContext(IEnumerable<KeyValuePair<string, object>> contextEntries)
        {
            ArgumentNullException.ThrowIfNull(contextEntries);
            foreach (var entry in contextEntries)
            {
                _context[entry.Key] = entry.Value ?? "<null>";
            }
            return this;
        }

        /// <summary>
        /// Sanitizes sensitive data from a message string.
        /// Removes or masks passwords, API keys, tokens, emails, and credit card numbers.
        /// </summary>
        /// <param name="message">The message to sanitize.</param>
        /// <returns>The sanitized message.</returns>
        public static string SanitizeSensitiveData(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            try
            {
                // Mask sensitive key-value pairs
                var result = SensitivePatternRegex.Replace(message, m =>
                    $"{m.Groups[1].Value}=[REDACTED]");

                // Mask email addresses
                result = EmailPatternRegex.Replace(result, match =>
                {
                    var email = match.Value;
                    var atIndex = email.IndexOf('@');
                    if (atIndex > 2)
                    {
                        return $"{email[0]}***@{email[(atIndex + 1)..]}";
                    }
                    return "***@***";
                });

                // Mask credit card numbers
                result = CreditCardPatternRegex.Replace(result, "****-****-****-****");

                return result;
            }
            catch (RegexMatchTimeoutException)
            {
                // If regex times out, return a generic message to avoid exposing sensitive data
                return "[Message sanitization timed out - content redacted for safety]";
            }
        }

        /// <summary>
        /// Returns a string representation of the exception including error code and correlation ID.
        /// </summary>
        public override string ToString()
        {
            var contextInfo = _context.Count > 0
                ? $", Context: {{{string.Join(", ", _context.Select(kv => $"{kv.Key}={kv.Value}"))}}}"
                : string.Empty;

            return $"[{ErrorCode}] CorrelationId: {CorrelationId}, Timestamp: {Timestamp:O}{contextInfo} - {Message}";
        }

        private static string GenerateCorrelationId()
        {
            return $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        }
    }

    /// <summary>
    /// Static class providing thread-safe input validation methods.
    /// All methods throw <see cref="DataWarehouseException"/> with <see cref="ErrorCode.ValidationFailed"/> on failure.
    /// </summary>
    public static class InputValidator
    {
        private static readonly HashSet<char> InvalidPathChars = new(Path.GetInvalidPathChars());
        private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());

        /// <summary>
        /// Validates that a string is not null, empty, or whitespace.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated value.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static string NotNullOrEmpty(
            string? value,
            [CallerArgumentExpression(nameof(value))] string? parameterName = null,
            string? correlationId = null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "value"}' cannot be null, empty, or whitespace.",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "NotNullOrEmpty");
            }
            return value;
        }

        /// <summary>
        /// Validates that a value is not null.
        /// </summary>
        /// <typeparam name="T">The type of value to validate.</typeparam>
        /// <param name="value">The value to validate.</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated value.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static T NotNull<T>(
            T? value,
            [CallerArgumentExpression(nameof(value))] string? parameterName = null,
            string? correlationId = null) where T : class
        {
            if (value is null)
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "value"}' cannot be null.",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "NotNull");
            }
            return value;
        }

        /// <summary>
        /// Validates that a string is a valid URI.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <param name="allowedSchemes">Optional array of allowed URI schemes. If null, http and https are allowed.</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated URI.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static Uri ValidUri(
            string? value,
            string[]? allowedSchemes = null,
            [CallerArgumentExpression(nameof(value))] string? parameterName = null,
            string? correlationId = null)
        {
            NotNullOrEmpty(value, parameterName, correlationId);

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "value"}' is not a valid URI: {SanitizationHelper.SanitizeForLogging(value!)}",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "ValidUri")
                    .WithContext("ProvidedValue", SanitizationHelper.SanitizeForLogging(value!));
            }

            var schemes = allowedSchemes ?? ["http", "https"];
            if (!schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"URI scheme '{uri.Scheme}' is not allowed. Allowed schemes: {string.Join(", ", schemes)}",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "ValidUri")
                    .WithContext("Scheme", uri.Scheme)
                    .WithContext("AllowedSchemes", string.Join(", ", schemes));
            }

            return uri;
        }

        /// <summary>
        /// Validates that a string is a valid file system path.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <param name="mustExist">If true, validates that the path exists.</param>
        /// <param name="allowDirectories">If true, directories are valid; if false, only files are valid.</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated path.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static string ValidPath(
            string? value,
            bool mustExist = false,
            bool allowDirectories = true,
            [CallerArgumentExpression(nameof(value))] string? parameterName = null,
            string? correlationId = null)
        {
            NotNullOrEmpty(value, parameterName, correlationId);

            // Check for invalid path characters
            if (value!.Any(c => InvalidPathChars.Contains(c)))
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "value"}' contains invalid path characters.",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "ValidPath");
            }

            // Check for path traversal attempts
            var normalizedPath = Path.GetFullPath(value!);
            var pathRoot = Path.GetPathRoot(value) ?? "/";
            if (value?.Contains("..") == true && !normalizedPath.StartsWith(Path.GetFullPath(pathRoot)))
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "value"}' contains potentially dangerous path traversal.",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "ValidPath")
                    .WithContext("Issue", "PathTraversal");
            }

            if (mustExist)
            {
                var fileExists = File.Exists(value);
                var dirExists = Directory.Exists(value);

                if (!fileExists && !dirExists)
                {
                    throw new DataWarehouseException(
                        ErrorCode.NotFound,
                        $"Path '{SanitizationHelper.SanitizeForLogging(value)}' does not exist.",
                        correlationId)
                        .WithContext("ParameterName", parameterName ?? "unknown")
                        .WithContext("ValidationType", "ValidPath");
                }

                if (!allowDirectories && dirExists && !fileExists)
                {
                    throw new DataWarehouseException(
                        ErrorCode.ValidationFailed,
                        $"Parameter '{parameterName ?? "value"}' must be a file, not a directory.",
                        correlationId)
                        .WithContext("ParameterName", parameterName ?? "unknown")
                        .WithContext("ValidationType", "ValidPath");
                }
            }

            return value!;
        }

        /// <summary>
        /// Validates that a numeric value is within a specified range.
        /// </summary>
        /// <typeparam name="T">The numeric type.</typeparam>
        /// <param name="value">The value to validate.</param>
        /// <param name="min">The minimum allowed value (inclusive).</param>
        /// <param name="max">The maximum allowed value (inclusive).</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated value.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static T InRange<T>(
            T value,
            T min,
            T max,
            [CallerArgumentExpression(nameof(value))] string? parameterName = null,
            string? correlationId = null) where T : IComparable<T>
        {
            if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "value"}' must be between {min} and {max}. Actual value: {value}",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "InRange")
                    .WithContext("MinValue", min?.ToString() ?? "null")
                    .WithContext("MaxValue", max?.ToString() ?? "null")
                    .WithContext("ActualValue", value?.ToString() ?? "null");
            }
            return value;
        }

        /// <summary>
        /// Validates that a string does not exceed a maximum length.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <param name="maxLength">The maximum allowed length.</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated value.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static string MaxLength(
            string? value,
            int maxLength,
            [CallerArgumentExpression(nameof(value))] string? parameterName = null,
            string? correlationId = null)
        {
            if (maxLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength), "Maximum length must be non-negative.");
            }

            // Null is valid for MaxLength - only validate if there's a value
            if (value is null)
            {
                return value!;
            }

            if (value.Length > maxLength)
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "value"}' exceeds maximum length of {maxLength}. Actual length: {value.Length}",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "MaxLength")
                    .WithContext("MaxLength", maxLength)
                    .WithContext("ActualLength", value.Length);
            }
            return value;
        }

        /// <summary>
        /// Validates that a string matches a regular expression pattern.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <param name="pattern">The regex pattern to match.</param>
        /// <param name="regexOptions">Optional regex options.</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated value.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static string ValidRegex(
            string? value,
            string pattern,
            RegexOptions regexOptions = RegexOptions.None,
            [CallerArgumentExpression(nameof(value))] string? parameterName = null,
            string? correlationId = null)
        {
            NotNullOrEmpty(value, parameterName, correlationId);
            NotNullOrEmpty(pattern, nameof(pattern), correlationId);

            try
            {
                // Use a timeout to prevent ReDoS attacks
                var regex = new Regex(pattern, regexOptions, TimeSpan.FromMilliseconds(500));
                if (!regex.IsMatch(value!))
                {
                    throw new DataWarehouseException(
                        ErrorCode.ValidationFailed,
                        $"Parameter '{parameterName ?? "value"}' does not match the required pattern.",
                        correlationId)
                        .WithContext("ParameterName", parameterName ?? "unknown")
                        .WithContext("ValidationType", "ValidRegex")
                        .WithContext("Pattern", pattern);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new DataWarehouseException(
                    ErrorCode.Timeout,
                    $"Regex validation timed out for parameter '{parameterName ?? "value"}'.",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "ValidRegex")
                    .WithContext("Pattern", pattern);
            }
            catch (ArgumentException ex)
            {
                throw new DataWarehouseException(
                    ErrorCode.InvalidInput,
                    $"Invalid regex pattern provided: {ex.Message}",
                    correlationId)
                    .WithContext("Pattern", pattern);
            }

            return value!;
        }

        /// <summary>
        /// Validates a collection is not null or empty.
        /// </summary>
        /// <typeparam name="T">The type of elements in the collection.</typeparam>
        /// <param name="collection">The collection to validate.</param>
        /// <param name="parameterName">The parameter name for error messages.</param>
        /// <param name="correlationId">Optional correlation ID for error tracking.</param>
        /// <returns>The validated collection.</returns>
        /// <exception cref="DataWarehouseException">Thrown when validation fails.</exception>
        public static IEnumerable<T> NotNullOrEmpty<T>(
            IEnumerable<T>? collection,
            [CallerArgumentExpression(nameof(collection))] string? parameterName = null,
            string? correlationId = null)
        {
            if (collection is null || !collection.Any())
            {
                throw new DataWarehouseException(
                    ErrorCode.ValidationFailed,
                    $"Parameter '{parameterName ?? "collection"}' cannot be null or empty.",
                    correlationId)
                    .WithContext("ParameterName", parameterName ?? "unknown")
                    .WithContext("ValidationType", "NotNullOrEmpty");
            }
            return collection;
        }
    }

    /// <summary>
    /// Static class providing thread-safe methods for sanitizing sensitive data.
    /// All methods are safe to call from multiple threads concurrently.
    /// </summary>
    public static class SanitizationHelper
    {
        private static readonly Regex ApiKeyPatternRegex = new(
            @"([a-zA-Z0-9_-]{20,})",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        /// <summary>
        /// Masks an API key, showing only the first and last few characters.
        /// </summary>
        /// <param name="apiKey">The API key to mask.</param>
        /// <param name="visibleChars">Number of characters to show at start and end. Default is 4.</param>
        /// <returns>The masked API key.</returns>
        public static string MaskApiKey(string? apiKey, int visibleChars = 4)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return "[NO_KEY]";
            }

            visibleChars = Math.Max(1, Math.Min(visibleChars, apiKey.Length / 4));

            if (apiKey.Length <= visibleChars * 2)
            {
                return new string('*', apiKey.Length);
            }

            var prefix = apiKey[..visibleChars];
            var suffix = apiKey[^visibleChars..];
            var maskedLength = apiKey.Length - (visibleChars * 2);
            return $"{prefix}{new string('*', Math.Min(maskedLength, 8))}{suffix}";
        }

        /// <summary>
        /// Masks a password completely or with a fixed mask.
        /// </summary>
        /// <param name="password">The password to mask.</param>
        /// <param name="showLength">If true, shows asterisks matching password length (max 16). Default is false.</param>
        /// <returns>The masked password.</returns>
        public static string MaskPassword(string? password, bool showLength = false)
        {
            if (string.IsNullOrEmpty(password))
            {
                return "[NO_PASSWORD]";
            }

            if (showLength)
            {
                // Cap at 16 to avoid revealing very long password lengths
                var displayLength = Math.Min(password.Length, 16);
                return new string('*', displayLength);
            }

            return "********";
        }

        /// <summary>
        /// Sanitizes a string for safe logging, removing or masking sensitive information.
        /// </summary>
        /// <param name="input">The input string to sanitize.</param>
        /// <param name="maxLength">Maximum length of the output. Default is 1000.</param>
        /// <returns>The sanitized string safe for logging.</returns>
        public static string SanitizeForLogging(string? input, int maxLength = 1000)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "[EMPTY]";
            }

            try
            {
                // First apply the general sanitization from DataWarehouseException
                var sanitized = DataWarehouseException.SanitizeSensitiveData(input);

                // Mask anything that looks like a long API key or token
                sanitized = ApiKeyPatternRegex.Replace(sanitized, match =>
                {
                    var value = match.Value;
                    // Only mask if it looks like a key (mix of chars, certain length patterns)
                    if (value.Length >= 32 && (value.Contains('-') || value.All(c => char.IsLetterOrDigit(c) || c == '_')))
                    {
                        return MaskApiKey(value);
                    }
                    return value;
                });

                // Truncate if too long
                if (sanitized.Length > maxLength)
                {
                    sanitized = string.Concat(sanitized.AsSpan(0, maxLength - 13), "[TRUNCATED]");
                }

                // Remove control characters except newlines and tabs
                sanitized = new string(sanitized
                    .Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t')
                    .ToArray());

                return sanitized;
            }
            catch (RegexMatchTimeoutException)
            {
                // If sanitization times out, return a safe fallback
                var truncated = input.Length > 50 ? input[..50] + "..." : input;
                return $"[SANITIZATION_TIMEOUT: {truncated.Length} chars]";
            }
        }

        /// <summary>
        /// Masks a connection string, hiding sensitive parts like passwords.
        /// </summary>
        /// <param name="connectionString">The connection string to mask.</param>
        /// <returns>The masked connection string.</returns>
        public static string MaskConnectionString(string? connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                return "[NO_CONNECTION_STRING]";
            }

            try
            {
                // Pattern matches common connection string password formats
                var patterns = new[]
                {
                    (@"(password|pwd)\s*=\s*[^;]+", "$1=********", RegexOptions.IgnoreCase),
                    (@"(secret|key)\s*=\s*[^;]+", "$1=********", RegexOptions.IgnoreCase),
                    (@"(accountkey|accesskey)\s*=\s*[^;]+", "$1=********", RegexOptions.IgnoreCase)
                };

                var result = connectionString;
                foreach (var (pattern, replacement, options) in patterns)
                {
                    var regex = new Regex(pattern, options | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
                    result = regex.Replace(result, replacement);
                }

                return result;
            }
            catch (RegexMatchTimeoutException)
            {
                return "[CONNECTION_STRING_MASKED]";
            }
        }

        /// <summary>
        /// Creates a safe dictionary for logging by masking sensitive keys.
        /// </summary>
        /// <param name="dictionary">The dictionary to sanitize.</param>
        /// <param name="sensitiveKeys">Keys that should be masked. If null, uses default sensitive keys.</param>
        /// <returns>A new dictionary with sensitive values masked.</returns>
        public static IDictionary<string, string> SanitizeDictionary(
            IDictionary<string, string>? dictionary,
            IEnumerable<string>? sensitiveKeys = null)
        {
            if (dictionary is null || dictionary.Count == 0)
            {
                return new Dictionary<string, string>();
            }

            var defaultSensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "password", "pwd", "secret", "key", "token", "apikey", "api_key",
                "accesskey", "access_key", "credential", "auth", "bearer", "jwt",
                "private", "connectionstring", "connection_string"
            };

            var keysToMask = sensitiveKeys is not null
                ? new HashSet<string>(sensitiveKeys, StringComparer.OrdinalIgnoreCase)
                : defaultSensitiveKeys;

            var result = new Dictionary<string, string>(dictionary.Count);
            foreach (var kvp in dictionary)
            {
                var isSensitive = keysToMask.Any(sk =>
                    kvp.Key.Contains(sk, StringComparison.OrdinalIgnoreCase));

                result[kvp.Key] = isSensitive ? "********" : kvp.Value;
            }

            return result;
        }
    }
}
