using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DataWarehouse.SDK.Validation;

/// <summary>
/// Centralized guard clauses for all public SDK methods (VALID-01).
/// Use these at the entry point of every public method to validate inputs
/// before processing. Throws <see cref="ArgumentException"/> variants on failure.
/// </summary>
public static class Guards
{
    /// <summary>Default regex timeout for all pattern matching (100ms).</summary>
    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Validates that a string is not null, empty, or whitespace.</summary>
    public static string NotNullOrWhiteSpace(
        string? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }

    /// <summary>Validates that an object is not null.</summary>
    public static T NotNull<T>(
        T? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    /// <summary>Validates that a value is within a range (inclusive).</summary>
    public static T InRange<T>(
        T value, T min, T max,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be between {min} and {max}.");
        return value;
    }

    /// <summary>Validates that a value is positive (greater than zero).</summary>
    public static T Positive<T>(
        T value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
    {
        if (value.CompareTo(default!) <= 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
        return value;
    }

    /// <summary>Validates that a string matches a regex pattern (with timeout, VALID-04).</summary>
    public static string MatchesPattern(
        string value, Regex pattern, string? errorMessage = null,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        try
        {
            if (!pattern.IsMatch(value))
                throw new ArgumentException(errorMessage ?? $"Value does not match required pattern: {pattern}", paramName);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ArgumentException("Input validation timed out (possible ReDoS). Value rejected.", paramName);
        }
        return value;
    }

    /// <summary>Validates that a path does not contain traversal sequences (VALID-02).</summary>
    public static string SafePath(
        string path,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, paramName);

        if (path.Contains("..") || path.Contains("%2e%2e", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("%252e", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path traversal detected. Path must not contain '..' or encoded equivalents.", paramName);
        }

        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new ArgumentException($"Invalid path format: {ex.Message}", paramName, ex);
        }

        return path;
    }

    /// <summary>Validates that a string does not exceed the maximum length (VALID-03).</summary>
    public static string MaxLength(
        string? value, int maxLength,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value != null && value.Length > maxLength)
            throw new ArgumentException($"Value length ({value.Length}) exceeds maximum allowed ({maxLength}).", paramName);
        return value!;
    }

    /// <summary>Validates that a stream does not exceed the maximum size (VALID-03).</summary>
    public static Stream MaxSize(
        Stream stream, long maxSizeBytes,
        [CallerArgumentExpression(nameof(stream))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(stream, paramName);
        if (stream.CanSeek && stream.Length > maxSizeBytes)
            throw new ArgumentException($"Stream size ({stream.Length} bytes) exceeds maximum allowed ({maxSizeBytes} bytes).", paramName);
        return stream;
    }

    /// <summary>Validates that a collection does not exceed the maximum count (VALID-03).</summary>
    public static IReadOnlyCollection<T> MaxCount<T>(
        IReadOnlyCollection<T> collection, int maxCount,
        [CallerArgumentExpression(nameof(collection))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(collection, paramName);
        if (collection.Count > maxCount)
            throw new ArgumentException($"Collection count ({collection.Count}) exceeds maximum allowed ({maxCount}).", paramName);
        return collection;
    }
}
