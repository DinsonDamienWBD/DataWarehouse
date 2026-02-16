// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Routes NLP queries through the MessageBridge to the server-side UltimateInterfacePlugin
/// for AI-powered intent parsing, knowledge bank queries, and capability lookups.
/// Provides graceful degradation when the intelligence stack is unavailable.
/// </summary>
public sealed class NlpMessageBusRouter
{
    private readonly MessageBridge _bridge;
    private readonly InstanceManager? _instanceManager;

    /// <summary>
    /// Creates a new NlpMessageBusRouter.
    /// </summary>
    /// <param name="bridge">The message bridge for sending messages to the server.</param>
    /// <param name="instanceManager">Optional instance manager for connection state checks.</param>
    public NlpMessageBusRouter(MessageBridge bridge, InstanceManager? instanceManager = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
        _instanceManager = instanceManager;
    }

    /// <summary>
    /// Gets whether the message bus router is available (connected to an instance).
    /// </summary>
    public bool IsAvailable => _bridge.IsConnected;

    /// <summary>
    /// Routes a natural language query to the server-side intelligence stack for parsing.
    /// Sends an nlp.parse-intent message through the bridge and returns the parsed command intent.
    /// </summary>
    /// <param name="query">The natural language query to parse.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A CommandIntent with ProcessedByAI=true if the server parsed successfully,
    /// or null if the router is unavailable or the server failed to parse.
    /// </returns>
    public async Task<CommandIntent?> RouteQueryAsync(string query, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var message = new Message
            {
                Id = Guid.NewGuid().ToString(),
                Type = MessageType.Request,
                Command = "nlp.parse-intent",
                Data = new Dictionary<string, object>
                {
                    ["query"] = query,
                    ["source"] = "cli-nlp"
                }
            };

            var response = await _bridge.SendAsync(message).WaitAsync(TimeSpan.FromSeconds(10), ct);

            if (response?.Data == null)
                return null;

            // Parse response data
            var command = response.Data.GetValueOrDefault("command")?.ToString();
            if (string.IsNullOrEmpty(command))
                return null;

            var confidence = 0.0;
            if (response.Data.TryGetValue("confidence", out var confObj))
            {
                if (confObj is double d) confidence = d;
                else if (confObj is float f) confidence = f;
                else double.TryParse(confObj?.ToString(), out confidence);
            }

            var explanation = response.Data.GetValueOrDefault("explanation")?.ToString();

            var parameters = new Dictionary<string, object?>();
            if (response.Data.TryGetValue("parameters", out var paramsObj) &&
                paramsObj is Dictionary<string, object> paramDict)
            {
                foreach (var kvp in paramDict)
                {
                    parameters[kvp.Key] = kvp.Value;
                }
            }

            return new CommandIntent
            {
                CommandName = command,
                Parameters = parameters,
                OriginalInput = query,
                Confidence = confidence,
                Explanation = explanation ?? $"Server-side NLP: {command}",
                ProcessedByAI = true
            };
        }
        catch
        {
            // Router failed -- caller will fall back to local processing
            return null;
        }
    }

    /// <summary>
    /// Routes a knowledge query to the server-side Knowledge Bank for structured answers.
    /// Used for queries like "What encryption algorithms are available?"
    /// </summary>
    /// <param name="query">The knowledge query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A structured response with answer text and related capabilities,
    /// or null if the router is unavailable or the query failed.
    /// </returns>
    public async Task<KnowledgeQueryResult?> RouteKnowledgeQueryAsync(string query, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var message = new Message
            {
                Id = Guid.NewGuid().ToString(),
                Type = MessageType.Request,
                Command = "knowledge.query",
                Data = new Dictionary<string, object>
                {
                    ["query"] = query,
                    ["topic"] = "*"
                }
            };

            var response = await _bridge.SendAsync(message).WaitAsync(TimeSpan.FromSeconds(10), ct);

            if (response?.Data == null)
                return null;

            var answer = response.Data.GetValueOrDefault("answer")?.ToString();
            if (string.IsNullOrEmpty(answer))
                return null;

            var relatedCapabilities = new List<string>();
            if (response.Data.TryGetValue("relatedCapabilities", out var capsObj) &&
                capsObj is IEnumerable<object> capList)
            {
                relatedCapabilities.AddRange(capList.Select(c => c.ToString() ?? ""));
            }

            return new KnowledgeQueryResult
            {
                Answer = answer,
                RelatedCapabilities = relatedCapabilities,
                Source = "server-knowledge-bank"
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Routes a capability lookup query through the message bus as a fallback
    /// when the intelligence stack is unavailable. Performs keyword matching
    /// against the server's live capability registry.
    /// </summary>
    /// <param name="keyword">The keyword to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// List of matching capability names, or null if the router is unavailable.
    /// </returns>
    public async Task<List<string>?> RouteCapabilityLookupAsync(string keyword, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var message = new Message
            {
                Id = Guid.NewGuid().ToString(),
                Type = MessageType.Request,
                Command = "capability.search",
                Data = new Dictionary<string, object>
                {
                    ["keyword"] = keyword
                }
            };

            var response = await _bridge.SendAsync(message).WaitAsync(TimeSpan.FromSeconds(10), ct);

            if (response?.Data == null)
                return null;

            var capabilities = new List<string>();
            if (response.Data.TryGetValue("matches", out var matchesObj) &&
                matchesObj is IEnumerable<object> matchList)
            {
                capabilities.AddRange(matchList.Select(m => m.ToString() ?? ""));
            }

            return capabilities;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Result of a knowledge bank query routed through the message bus.
/// </summary>
public sealed record KnowledgeQueryResult
{
    /// <summary>The answer text from the knowledge bank.</summary>
    public required string Answer { get; init; }

    /// <summary>Related capabilities that may help the user.</summary>
    public List<string> RelatedCapabilities { get; init; } = new();

    /// <summary>The source of the answer (e.g., "server-knowledge-bank").</summary>
    public string? Source { get; init; }
}
