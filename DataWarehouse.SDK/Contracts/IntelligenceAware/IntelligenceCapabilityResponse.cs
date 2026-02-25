using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    /// <summary>
    /// Response payload for Intelligence discovery requests.
    /// Sent by T90 in response to discovery requests on the <see cref="IntelligenceTopics.Discover"/> topic.
    /// </summary>
    public sealed record IntelligenceCapabilityResponse
    {
        /// <summary>
        /// Gets or sets whether Intelligence is available.
        /// </summary>
        public bool Available { get; init; } = true;

        /// <summary>
        /// Gets or sets the available capabilities.
        /// </summary>
        public IntelligenceCapabilities Capabilities { get; init; }

        /// <summary>
        /// Gets or sets the T90 version string.
        /// </summary>
        public string Version { get; init; } = "1.0.0";

        /// <summary>
        /// Gets or sets the plugin ID of the Intelligence provider.
        /// </summary>
        public string PluginId { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the plugin name.
        /// </summary>
        public string PluginName { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the list of active AI provider names.
        /// </summary>
        public string[] ActiveProviders { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the list of active vector store names.
        /// </summary>
        public string[] ActiveVectorStores { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the list of active knowledge graph names.
        /// </summary>
        public string[] ActiveKnowledgeGraphs { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the list of active feature names.
        /// </summary>
        public string[] ActiveFeatures { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets when this response was generated.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets or sets additional metadata about the Intelligence system.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Converts the response to a dictionary for message bus transmission.
        /// </summary>
        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                ["available"] = Available,
                ["capabilities"] = (long)Capabilities,
                ["version"] = Version,
                ["pluginId"] = PluginId,
                ["pluginName"] = PluginName,
                ["activeProviders"] = ActiveProviders,
                ["activeVectorStores"] = ActiveVectorStores,
                ["activeKnowledgeGraphs"] = ActiveKnowledgeGraphs,
                ["activeFeatures"] = ActiveFeatures,
                ["timestamp"] = Timestamp,
                ["metadata"] = Metadata
            };
        }

        /// <summary>
        /// Creates a response from a dictionary payload.
        /// </summary>
        public static IntelligenceCapabilityResponse FromDictionary(Dictionary<string, object> payload)
        {
            var response = new IntelligenceCapabilityResponse();

            if (payload.TryGetValue("available", out var avail) && avail is bool available)
                response = response with { Available = available };

            if (payload.TryGetValue("capabilities", out var caps))
            {
                if (caps is IntelligenceCapabilities ic)
                    response = response with { Capabilities = ic };
                else if (caps is long longVal)
                    response = response with { Capabilities = (IntelligenceCapabilities)longVal };
                else if (long.TryParse(caps?.ToString(), out var parsed))
                    response = response with { Capabilities = (IntelligenceCapabilities)parsed };
            }

            if (payload.TryGetValue("version", out var ver) && ver is string version)
                response = response with { Version = version };

            if (payload.TryGetValue("pluginId", out var pid) && pid is string pluginId)
                response = response with { PluginId = pluginId };

            if (payload.TryGetValue("pluginName", out var pname) && pname is string pluginName)
                response = response with { PluginName = pluginName };

            if (payload.TryGetValue("activeProviders", out var ap) && ap is string[] providers)
                response = response with { ActiveProviders = providers };

            if (payload.TryGetValue("activeVectorStores", out var avs) && avs is string[] vectorStores)
                response = response with { ActiveVectorStores = vectorStores };

            if (payload.TryGetValue("activeKnowledgeGraphs", out var akg) && akg is string[] graphs)
                response = response with { ActiveKnowledgeGraphs = graphs };

            if (payload.TryGetValue("activeFeatures", out var af) && af is string[] features)
                response = response with { ActiveFeatures = features };

            return response;
        }
    }

    /// <summary>
    /// Discovery request payload sent to the Intelligence system.
    /// </summary>
    public sealed record IntelligenceDiscoveryRequest
    {
        /// <summary>
        /// Gets or sets the ID of the plugin making the request.
        /// </summary>
        public string RequestorId { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the name of the plugin making the request.
        /// </summary>
        public string RequestorName { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets specific capabilities being requested (optional).
        /// If set, only these capabilities are checked.
        /// </summary>
        public IntelligenceCapabilities? RequestedCapabilities { get; init; }

        /// <summary>
        /// Gets or sets when the request was made.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Converts the request to a dictionary for message bus transmission.
        /// </summary>
        public Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>
            {
                ["requestorId"] = RequestorId,
                ["requestorName"] = RequestorName,
                ["timestamp"] = Timestamp
            };

            if (RequestedCapabilities.HasValue)
                dict["requestedCapabilities"] = (long)RequestedCapabilities.Value;

            return dict;
        }

        /// <summary>
        /// Creates a request from a dictionary payload.
        /// </summary>
        public static IntelligenceDiscoveryRequest FromDictionary(Dictionary<string, object> payload)
        {
            var request = new IntelligenceDiscoveryRequest();

            if (payload.TryGetValue("requestorId", out var rid) && rid is string requestorId)
                request = request with { RequestorId = requestorId };

            if (payload.TryGetValue("requestorName", out var rname) && rname is string requestorName)
                request = request with { RequestorName = requestorName };

            if (payload.TryGetValue("requestedCapabilities", out var rc))
            {
                if (rc is IntelligenceCapabilities ic)
                    request = request with { RequestedCapabilities = ic };
                else if (rc is long longVal)
                    request = request with { RequestedCapabilities = (IntelligenceCapabilities)longVal };
            }

            return request;
        }
    }
}
