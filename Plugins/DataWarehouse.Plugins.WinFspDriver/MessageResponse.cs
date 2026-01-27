namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Helper class for creating standardized message responses.
/// </summary>
public sealed class MessageResponse
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Response data if the operation succeeded.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Creates a success response with the specified data.
    /// </summary>
    /// <param name="data">The response data.</param>
    /// <returns>A success response.</returns>
    public static MessageResponse Ok(object? data = null)
    {
        return new MessageResponse
        {
            Success = true,
            Data = data
        };
    }

    /// <summary>
    /// Creates an error response with the specified message.
    /// </summary>
    /// <param name="error">The error message.</param>
    /// <returns>An error response.</returns>
    public static MessageResponse Error(string error)
    {
        return new MessageResponse
        {
            Success = false,
            ErrorMessage = error
        };
    }

    /// <summary>
    /// Converts the response to a dictionary for message payloads.
    /// </summary>
    /// <returns>Dictionary representation of the response.</returns>
    public Dictionary<string, object> ToDictionary()
    {
        var dict = new Dictionary<string, object>
        {
            ["success"] = Success
        };

        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            dict["error"] = ErrorMessage;
        }

        if (Data != null)
        {
            dict["data"] = Data;
        }

        return dict;
    }
}
