// EXAMPLE: How to integrate ConnectorIntegrationStrategy in UltimateIntelligencePlugin.cs
//
// This file is NOT compiled - it's just an example for documentation purposes.
// Copy the relevant parts to your main plugin class.

#if FALSE // This file is excluded from compilation

using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.UltimateIntelligence.Strategies.ConnectorIntegration;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateIntelligence
{
    /// <summary>
    /// Example integration in the main UltimateIntelligencePlugin class.
    /// </summary>
    public partial class UltimateIntelligencePlugin
    {
        private ConnectorIntegrationStrategy? _connectorIntegration;
        private readonly IMessageBus _messageBus;
        private readonly ILogger _logger;

        /// <summary>
        /// Initialize connector integration during plugin startup.
        /// </summary>
        public async Task InitializeConnectorIntegrationAsync(CancellationToken ct)
        {
            // Get configuration for integration mode
            var integrationMode = GetConnectorIntegrationModeFromConfig();

            if (integrationMode == ConnectorIntegrationMode.Disabled)
            {
                _logger.LogInformation("Connector integration is disabled via configuration");
                return;
            }

            _logger.LogInformation("Initializing connector integration in {Mode} mode", integrationMode);

            // Create and initialize the connector integration strategy
            _connectorIntegration = new ConnectorIntegrationStrategy(_messageBus, _logger);

            // Set AI providers if available
            var aiProvider = GetActiveAIProvider();
            if (aiProvider != null)
            {
                _connectorIntegration.SetAIProvider(aiProvider);
                _logger.LogInformation("Configured connector integration with AI provider: {ProviderId}",
                    aiProvider.ProviderId);
            }

            // Set vector store if available
            var vectorStore = GetActiveVectorStore();
            if (vectorStore != null)
            {
                _connectorIntegration.SetVectorStore(vectorStore);
                _logger.LogInformation("Configured connector integration with vector store");
            }

            // Initialize the strategy with the specified mode
            await _connectorIntegration.InitializeAsync(integrationMode, ct);

            _logger.LogInformation("Connector integration initialized successfully");
        }

        /// <summary>
        /// Get connector integration mode from configuration.
        /// </summary>
        private ConnectorIntegrationMode GetConnectorIntegrationModeFromConfig()
        {
            // Option 1: From appsettings.json / configuration
            // var modeString = _configuration.GetValue<string>("Intelligence:ConnectorIntegration:Mode", "AsyncObservation");

            // Option 2: From environment variable
            // var modeString = Environment.GetEnvironmentVariable("INTELLIGENCE_CONNECTOR_MODE") ?? "AsyncObservation";

            // Option 3: Hardcoded default (for this example)
            var modeString = "Hybrid"; // Change to "Disabled", "AsyncObservation", "SyncTransformation", or "Hybrid"

            if (Enum.TryParse<ConnectorIntegrationMode>(modeString, ignoreCase: true, out var mode))
            {
                return mode;
            }

            _logger.LogWarning("Invalid connector integration mode '{Mode}', defaulting to AsyncObservation", modeString);
            return ConnectorIntegrationMode.AsyncObservation;
        }

        /// <summary>
        /// Get the currently active AI provider (stub - implement based on your plugin design).
        /// </summary>
        private IAIProvider? GetActiveAIProvider()
        {
            // TODO: Return the configured AI provider
            // Example: return _aiProviders.FirstOrDefault(p => p.IsAvailable);
            return null;
        }

        /// <summary>
        /// Get the currently active vector store (stub - implement based on your plugin design).
        /// </summary>
        private IVectorStore? GetActiveVectorStore()
        {
            // TODO: Return the configured vector store
            // Example: return _vectorStores.FirstOrDefault(v => v.IsAvailable);
            return null;
        }

        /// <summary>
        /// Dispose connector integration during plugin shutdown.
        /// </summary>
        public void DisposeConnectorIntegration()
        {
            if (_connectorIntegration != null)
            {
                _logger.LogInformation("Disposing connector integration");
                _connectorIntegration.Dispose();
                _connectorIntegration = null;
            }
        }
    }

    // ============================================================================
    // CONFIGURATION EXAMPLE (appsettings.json)
    // ============================================================================
    /*
    {
      "Intelligence": {
        "ConnectorIntegration": {
          "Mode": "Hybrid",
          "TransformTimeout": 5
        },
        "Providers": {
          "DefaultProvider": "openai",
          "OpenAI": {
            "ApiKey": "sk-...",
            "Model": "gpt-4"
          }
        }
      }
    }
    */

    // ============================================================================
    // USAGE IN PLUGIN LIFECYCLE
    // ============================================================================
    /*
    public override async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting UltimateIntelligence plugin");

        // Initialize AI providers
        await InitializeAIProvidersAsync(ct);

        // Initialize vector stores
        await InitializeVectorStoresAsync(ct);

        // Initialize knowledge graphs
        await InitializeKnowledgeGraphsAsync(ct);

        // Initialize features
        await InitializeFeaturesAsync(ct);

        // Initialize connector integration (NEW!)
        await InitializeConnectorIntegrationAsync(ct);

        _logger.LogInformation("UltimateIntelligence plugin started successfully");
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Stopping UltimateIntelligence plugin");

        // Dispose connector integration (NEW!)
        DisposeConnectorIntegration();

        // Other cleanup...

        _logger.LogInformation("UltimateIntelligence plugin stopped successfully");
    }
    */
}

#endif
