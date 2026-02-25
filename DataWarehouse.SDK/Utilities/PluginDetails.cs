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

    public class PluginParameterDescriptor
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public bool Required { get; init; }
        public string Description { get; init; } = string.Empty;
        public object? DefaultValue { get; init; }
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
        /// Unique message identifier.
        /// </summary>
        public string MessageId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Message type identifier (e.g., "config.changed", "system.shutdown", "ai.query").
        /// </summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>
        /// Alias for Type for compatibility.
        /// </summary>
        public string MessageType { get => Type; init => Type = value; }

        /// <summary>
        /// Plugin ID that sent this message.
        /// </summary>
        public string SourcePluginId { get; init; } = string.Empty;

        /// <summary>
        /// Message payload as a dictionary for structured access.
        /// Use this for key-value payload data.
        /// </summary>
        public Dictionary<string, object> Payload { get; init; } = new();

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
        /// HMAC signature of the message for authentication.
        /// Populated by IAuthenticatedMessageBus when publishing to authenticated topics.
        /// Null for unauthenticated messages.
        /// </summary>
        public byte[]? Signature { get; set; }

        /// <summary>
        /// Unique nonce for replay protection.
        /// Automatically generated by IAuthenticatedMessageBus when signing.
        /// </summary>
        public string? Nonce { get; set; }

        /// <summary>
        /// Expiration time for replay protection.
        /// Messages received after this time should be rejected.
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// The identity of the principal on whose behalf this message is being sent.
        /// UNIVERSAL ENFORCEMENT: Every message MUST carry this identity.
        /// Access control uses Identity.EffectivePrincipalId (never ActorId) for authorization.
        /// </summary>
        public DataWarehouse.SDK.Security.CommandIdentity? Identity { get; init; }

        /// <summary>
        /// Creates a new PluginMessage with the specified type and payload.
        /// </summary>
        public static PluginMessage Create(string type, Dictionary<string, object>? payload = null, string? correlationId = null)
        {
            return new PluginMessage
            {
                Type = type,
                Payload = payload ?? new Dictionary<string, object>(),
                CorrelationId = correlationId,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// ISO-06 (CVSS 4.8): Allowed types for payload values to prevent type confusion attacks.
        /// Only serializable, known-safe types are permitted.
        /// Complex objects that could be used for deserialization attacks are rejected.
        /// </summary>
        private static readonly HashSet<Type> AllowedPayloadTypes = new()
        {
            typeof(string),
            typeof(int),
            typeof(long),
            typeof(double),
            typeof(float),
            typeof(decimal),
            typeof(bool),
            typeof(byte[]),
            typeof(DateTime),
            typeof(DateTimeOffset),
            typeof(Guid),
            typeof(List<string>),
            typeof(string[]),
            typeof(Dictionary<string, string>),
            // Allow CommandIdentity specifically (used in Identity-carrying messages)
            typeof(DataWarehouse.SDK.Security.CommandIdentity),
        };

        /// <summary>
        /// ISO-06: Validates that a payload value is a known-safe type.
        /// Returns true if the type is allowed, false if it could be used for type confusion.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <returns>True if the value type is allowed in message payloads.</returns>
        public static bool IsAllowedPayloadType(object? value)
        {
            if (value == null)
                return true;

            var type = value.GetType();

            // Direct type match
            if (AllowedPayloadTypes.Contains(type))
                return true;

            // Allow primitive types and enums
            if (type.IsPrimitive || type.IsEnum)
                return true;

            // Allow Dictionary<string, object> (nested payloads) -- values checked recursively
            if (type == typeof(Dictionary<string, object>))
                return true;

            // Allow IReadOnlyList<string> and similar
            if (value is IReadOnlyList<string> || value is IEnumerable<string>)
                return true;

            return false;
        }

        /// <summary>
        /// ISO-06: Validates all values in a payload dictionary.
        /// Throws <see cref="ArgumentException"/> if any value has a disallowed type.
        /// </summary>
        /// <param name="payload">The payload to validate.</param>
        /// <exception cref="ArgumentException">Thrown when a payload value has an unsafe type.</exception>
        public static void ValidatePayloadTypes(Dictionary<string, object>? payload)
        {
            if (payload == null) return;

            foreach (var (key, value) in payload)
            {
                if (!IsAllowedPayloadType(value))
                {
                    throw new ArgumentException(
                        $"Payload key '{key}' contains disallowed type '{value?.GetType().FullName}'. " +
                        "Only serializable, known-safe types are permitted in message payloads (ISO-06). " +
                        "Allowed types: string, int, long, double, float, decimal, bool, byte[], " +
                        "DateTime, DateTimeOffset, Guid, List<string>, string[], Dictionary<string, string>.");
                }

                // Recursively validate nested dictionaries
                if (value is Dictionary<string, object> nested)
                {
                    ValidatePayloadTypes(nested);
                }
            }
        }

        /// <summary>
        /// ISO-06: Creates a new PluginMessage with validated payload types.
        /// Rejects complex objects that could be used for type confusion attacks.
        /// </summary>
        public static PluginMessage CreateValidated(
            string type,
            Dictionary<string, object>? payload = null,
            string? correlationId = null)
        {
            ValidatePayloadTypes(payload);
            return Create(type, payload, correlationId);
        }
    }

}
