namespace DataWarehouse.Shared.Models;

/// <summary>
/// Message type for communication with DataWarehouse instances
/// </summary>
public enum MessageType
{
    Request,
    Response,
    Event,
    Error
}

/// <summary>
/// Message for DataWarehouse instance communication
/// </summary>
public class Message
{
    /// <summary>
    /// Unique message identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Message type
    /// </summary>
    public MessageType Type { get; set; }

    /// <summary>
    /// Correlation ID for request/response pairing
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Command to execute (e.g., "storage.list", "encryption.enable")
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Message data payload
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();

    /// <summary>
    /// Error message if Type is Error
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Timestamp when message was created
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
