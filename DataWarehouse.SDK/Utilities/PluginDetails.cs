using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.SDK.Utilities
{
    /// <summary>
    /// Describes a plugin's identity and metadata.
    /// </summary>
    public class PluginDescriptor
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public PluginCategory Category { get; init; }
        public string Description { get; init; } = string.Empty;
        public List<string> Tags { get; init; } = new();
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    public class PluginDependency
    {
        public string RequiredInterface { get; init; } = string.Empty;  // "IMetadataIndex"
        public bool IsOptional { get; init; }
        public string Reason { get; init; } = string.Empty;  // "Needed for manifest lookups"
    }

    public class PluginCapabilityDescriptor
    {
        private string _capabilityId = string.Empty;

        /// <summary>
        /// Unique capability identifier (e.g., "storage.s3.put").
        /// </summary>
        public string CapabilityId
        {
            get => _capabilityId;
            init => _capabilityId = value;
        }

        /// <summary>
        /// Alias for CapabilityId for convenience and backward compatibility.
        /// </summary>
        public string Name
        {
            get => _capabilityId;
            init => _capabilityId = value;
        }

        /// <summary>
        /// Human-readable display name for the capability.
        /// </summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>
        /// Description of what this capability does.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// Category of the capability (Storage, Security, etc.).
        /// </summary>
        public CapabilityCategory Category { get; init; }

        /// <summary>
        /// Whether this capability requires explicit approval before execution.
        /// </summary>
        public bool RequiresApproval { get; init; }

        /// <summary>
        /// Permission required to invoke this capability.
        /// </summary>
        public Permission RequiredPermission { get; init; }

        /// <summary>
        /// JSON schema defining the parameter structure.
        /// </summary>
        public string ParameterSchemaJson { get; init; } = "{}";

        /// <summary>
        /// Optional parameters dictionary for capability metadata.
        /// </summary>
        public Dictionary<string, object>? Parameters { get; init; }
    }

    /// <summary>
    /// Provides execution context for plugin capability invocations.
    /// AI-native context with access to kernel services and security.
    /// </summary>
    public interface IExecutionContext
    {
        /// <summary>
        /// The user or agent invoking this capability.
        /// </summary>
        string UserId { get; }

        /// <summary>
        /// Security context for permission checks.
        /// </summary>
        ISecurityContext SecurityContext { get; }

        /// <summary>
        /// Access to kernel logging services.
        /// </summary>
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message, Exception? ex = null);

        /// <summary>
        /// AI-native: Allows plugins to request confirmation from AI agents or users.
        /// </summary>
        Task<bool> RequestApprovalAsync(string message, string? reason = null);

        /// <summary>
        /// AI-native: Get a capability result from another plugin (for chaining operations).
        /// </summary>
        Task<object?> InvokeCapabilityAsync(string capabilityId, Dictionary<string, object> parameters);

        /// <summary>
        /// Get configuration value for the plugin.
        /// </summary>
        T? GetConfig<T>(string key, T? defaultValue = default);

        /// <summary>
        /// Cancellation token for long-running operations.
        /// </summary>
        CancellationToken CancellationToken { get; }
    }

    /// <summary>
    /// Represents a message sent to a plugin for external signals or events.
    /// AI-native: supports both structured and unstructured messages.
    /// </summary>
    public class PluginMessage
    {
        /// <summary>
        /// Message type identifier (e.g., "config.changed", "system.shutdown", "ai.query").
        /// </summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>
        /// Message payload as a dictionary for structured access.
        /// Use this for key-value payload data.
        /// </summary>
        public Dictionary<string, object?> Payload { get; init; } = new();

        /// <summary>
        /// Timestamp of the message.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Source of the message (e.g., "Kernel", "AI Agent", "User").
        /// </summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>
        /// Optional correlation ID for tracking related messages.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// AI-native: Natural language description of the message for LLM processing.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Additional metadata for the message.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Creates a new PluginMessage with the specified type and payload.
        /// </summary>
        public static PluginMessage Create(string type, Dictionary<string, object?>? payload = null, string? correlationId = null)
        {
            return new PluginMessage
            {
                Type = type,
                Payload = payload ?? new Dictionary<string, object?>(),
                CorrelationId = correlationId,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Standard response for message operations.
    /// </summary>
    public class MessageResponse
    {
        /// <summary>
        /// Whether the operation was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Response data (can be any object).
        /// </summary>
        public object? Data { get; init; }

        /// <summary>
        /// Error message if the operation failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Creates a successful response with data.
        /// </summary>
        public static MessageResponse Ok(object? data = null) => new() { Success = true, Data = data };

        /// <summary>
        /// Creates an error response with a message.
        /// </summary>
        public static MessageResponse Error(string message) => new() { Success = false, ErrorMessage = message };
    }
}
