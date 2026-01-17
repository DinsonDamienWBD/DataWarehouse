using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.AI
{
    /// <summary>
    /// AI-agnostic provider interface. Supports OpenAI, Claude, Copilot, Ollama,
    /// and any future AI/LLM providers without SDK changes.
    /// </summary>
    public interface IAIProvider
    {
        /// <summary>
        /// Provider identifier (e.g., "openai", "anthropic", "ollama", "azure", "copilot").
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// Human-readable provider name.
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Whether this provider is currently available and configured.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Supported capabilities of this provider.
        /// </summary>
        AICapabilities Capabilities { get; }

        /// <summary>
        /// Generate a completion from a prompt.
        /// </summary>
        Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default);

        /// <summary>
        /// Generate a streaming completion from a prompt.
        /// </summary>
        IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, CancellationToken ct = default);

        /// <summary>
        /// Generate embeddings for text.
        /// </summary>
        Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default);

        /// <summary>
        /// Generate embeddings for multiple texts (batch).
        /// </summary>
        Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default);
    }

    /// <summary>
    /// AI provider capabilities flags.
    /// </summary>
    [Flags]
    public enum AICapabilities
    {
        None = 0,
        TextCompletion = 1,
        ChatCompletion = 2,
        Streaming = 4,
        Embeddings = 8,
        ImageGeneration = 16,
        ImageAnalysis = 32,
        FunctionCalling = 64,
        CodeGeneration = 128,
        All = TextCompletion | ChatCompletion | Streaming | Embeddings | ImageGeneration | ImageAnalysis | FunctionCalling | CodeGeneration
    }

    /// <summary>
    /// AI request model - provider agnostic.
    /// </summary>
    public class AIRequest
    {
        /// <summary>
        /// The prompt or user message.
        /// </summary>
        public string Prompt { get; init; } = string.Empty;

        /// <summary>
        /// Optional system message for context.
        /// </summary>
        public string? SystemMessage { get; init; }

        /// <summary>
        /// Chat history for multi-turn conversations.
        /// </summary>
        public List<AIChatMessage> ChatHistory { get; init; } = new();

        /// <summary>
        /// Model to use (provider-specific, e.g., "gpt-4", "claude-3-opus").
        /// Null means use provider default.
        /// </summary>
        public string? Model { get; init; }

        /// <summary>
        /// Maximum tokens in response.
        /// </summary>
        public int? MaxTokens { get; init; }

        /// <summary>
        /// Temperature for response randomness (0.0-2.0).
        /// </summary>
        public float? Temperature { get; init; }

        /// <summary>
        /// Optional function/tool definitions for function calling.
        /// </summary>
        public List<AIFunction>? Functions { get; init; }

        /// <summary>
        /// Additional provider-specific parameters.
        /// </summary>
        public Dictionary<string, object> ExtendedParameters { get; init; } = new();
    }

    /// <summary>
    /// AI response model - provider agnostic.
    /// </summary>
    public class AIResponse
    {
        /// <summary>
        /// The generated content.
        /// </summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>
        /// Whether the response was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if not successful.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Reason the response stopped (e.g., "stop", "length", "function_call").
        /// </summary>
        public string? FinishReason { get; init; }

        /// <summary>
        /// Function call request if the model wants to call a function.
        /// </summary>
        public AIFunctionCall? FunctionCall { get; init; }

        /// <summary>
        /// Token usage statistics.
        /// </summary>
        public AIUsage? Usage { get; init; }

        /// <summary>
        /// Provider-specific response metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Streaming chunk from AI provider.
    /// </summary>
    public class AIStreamChunk
    {
        /// <summary>
        /// The content delta in this chunk.
        /// </summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>
        /// Whether this is the final chunk.
        /// </summary>
        public bool IsFinal { get; init; }

        /// <summary>
        /// Finish reason (only set on final chunk).
        /// </summary>
        public string? FinishReason { get; init; }
    }

    /// <summary>
    /// Chat message for multi-turn conversations.
    /// </summary>
    public class AIChatMessage
    {
        /// <summary>
        /// Role of the message sender (e.g., "user", "assistant", "system", "function").
        /// </summary>
        public string Role { get; init; } = "user";

        /// <summary>
        /// Content of the message.
        /// </summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>
        /// Function name if this is a function result.
        /// </summary>
        public string? FunctionName { get; init; }
    }

    /// <summary>
    /// Function definition for function calling.
    /// </summary>
    public class AIFunction
    {
        /// <summary>
        /// Function name.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Function description for the AI.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// JSON schema for function parameters.
        /// </summary>
        public string ParametersSchema { get; init; } = "{}";
    }

    /// <summary>
    /// Function call request from AI.
    /// </summary>
    public class AIFunctionCall
    {
        /// <summary>
        /// Function name to call.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Arguments as JSON string.
        /// </summary>
        public string Arguments { get; init; } = "{}";
    }

    /// <summary>
    /// Token usage statistics.
    /// </summary>
    public class AIUsage
    {
        /// <summary>
        /// Tokens used in the prompt.
        /// </summary>
        public int PromptTokens { get; init; }

        /// <summary>
        /// Tokens used in the completion.
        /// </summary>
        public int CompletionTokens { get; init; }

        /// <summary>
        /// Total tokens used.
        /// </summary>
        public int TotalTokens => PromptTokens + CompletionTokens;
    }

    /// <summary>
    /// Registry for AI providers. Allows runtime registration and selection.
    /// </summary>
    public interface IAIProviderRegistry
    {
        /// <summary>
        /// Register an AI provider.
        /// </summary>
        void Register(IAIProvider provider);

        /// <summary>
        /// Unregister an AI provider.
        /// </summary>
        void Unregister(string providerId);

        /// <summary>
        /// Get a specific provider by ID.
        /// </summary>
        IAIProvider? GetProvider(string providerId);

        /// <summary>
        /// Get all registered providers.
        /// </summary>
        IEnumerable<IAIProvider> GetAllProviders();

        /// <summary>
        /// Get the default/preferred provider.
        /// </summary>
        IAIProvider? GetDefaultProvider();

        /// <summary>
        /// Set the default provider.
        /// </summary>
        void SetDefaultProvider(string providerId);

        /// <summary>
        /// Get providers that support specific capabilities.
        /// </summary>
        IEnumerable<IAIProvider> GetProvidersWithCapabilities(AICapabilities required);
    }
}
