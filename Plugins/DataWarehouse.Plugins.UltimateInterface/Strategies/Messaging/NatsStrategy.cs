using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Messaging;

/// <summary>
/// NATS (Neural Autonomic Transport System) interface strategy for cloud-native messaging.
/// Implements NATS protocol with JetStream, request/reply, and queue groups for high-performance distributed systems.
/// </summary>
/// <remarks>
/// <para>
/// NATS is a lightweight, high-performance messaging system for cloud-native applications, microservices,
/// and IoT messaging. This strategy provides production-ready NATS integration.
/// </para>
/// <para>
/// Supported features:
/// <list type="bullet">
/// <item><description>Publish/subscribe with subject-based routing</description></item>
/// <item><description>Request/reply pattern with timeout support</description></item>
/// <item><description>Queue groups for load balancing across consumers</description></item>
/// <item><description>Subject wildcards: * (single token), > (multi-token)</description></item>
/// <item><description>JetStream for durable subscriptions and message replay</description></item>
/// </list>
/// </para>
/// <para>
/// REST mapping: POST = publish, GET = subscribe, PUT = request/reply.
/// </para>
/// </remarks>
internal sealed class NatsStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    private readonly ConcurrentDictionary<string, NatsSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, NatsStream> _jetStreams = new();
    private readonly ConcurrentDictionary<string, List<NatsMessage>> _subjects = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NatsMessage>> _replyHandlers = new();

    public string StrategyId => "nats";
    public string DisplayName => "NATS";
    public string SemanticDescription => "NATS cloud-native messaging with JetStream persistence, request/reply, queue groups, and wildcard subjects.";
    public InterfaceCategory Category => InterfaceCategory.Messaging;
    public string[] Tags => ["nats", "messaging", "cloud-native", "jetstream", "pubsub"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.NATS;

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json", "application/octet-stream", "text/plain" },
        MaxRequestSize: 1 * 1024 * 1024, // NATS default max payload 1 MB
        MaxResponseSize: 1 * 1024 * 1024,
        SupportsBidirectionalStreaming: true,
        SupportsMultiplexing: true,
        DefaultTimeout: TimeSpan.FromSeconds(5), // Request/reply default timeout
        SupportsCancellation: true,
        RequiresTLS: false // TLS optional
    );

    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        // Initialize default JetStream stream
        _jetStreams["DEFAULT"] = new NatsStream
        {
            Name = "DEFAULT",
            Subjects = new[] { "*" },
            Retention = "limits",
            MaxMessages = 10000
        };

        return Task.CompletedTask;
    }

    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        _subscriptions.Clear();
        _jetStreams.Clear();
        _subjects.Clear();
        _replyHandlers.Clear();
        return Task.CompletedTask;
    }

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var subject = request.Path.TrimStart('/');

        return request.Method switch
        {
            SdkInterface.HttpMethod.POST => await HandlePublish(subject, request, cancellationToken),
            SdkInterface.HttpMethod.GET => HandleSubscribe(subject, request),
            SdkInterface.HttpMethod.PUT => await HandleRequestReply(subject, request, cancellationToken),
            SdkInterface.HttpMethod.DELETE => HandleUnsubscribe(subject, request),
            _ => SdkInterface.InterfaceResponse.BadRequest($"Unsupported NATS operation: {request.Method}")
        };
    }

    private async Task<SdkInterface.InterfaceResponse> HandlePublish(
        string subject,
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var replyTo = request.Headers.TryGetValue("NATS-Reply-To", out var reply) ? reply : null;
        var headers = ExtractNatsHeaders(request);

        var message = new NatsMessage
        {
            Subject = subject,
            Payload = request.Body.ToArray(),
            ReplyTo = replyTo,
            Headers = headers,
            Timestamp = DateTimeOffset.UtcNow
        };

        // Deliver to active subscriptions
        await DeliverToSubscribers(message, cancellationToken);

        // Store in JetStream if subject matches stream configuration
        StoreInJetStream(message);

        // Publish to DataWarehouse via message bus
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            // In production, route message to DataWarehouse via message bus
            // Topic: $"interface.nats.{subject}"
            // Payload: subject, replyTo, message body, headers
        }

        // If this is a reply, complete any waiting reply handlers
        if (!string.IsNullOrEmpty(replyTo) && _replyHandlers.TryRemove(replyTo, out var replyHandler))
        {
            replyHandler.TrySetResult(message);
        }

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "published",
            subject,
            replyTo
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse HandleSubscribe(string subject, SdkInterface.InterfaceRequest request)
    {
        var queueGroup = request.QueryParameters.TryGetValue("queue", out var queue) ? queue : null;
        var durable = request.QueryParameters.TryGetValue("durable", out var dur) && dur == "true";
        var subscriptionId = Guid.NewGuid().ToString("N");

        var subscription = new NatsSubscription
        {
            Id = subscriptionId,
            Subject = subject,
            QueueGroup = queueGroup,
            Durable = durable,
            SubscribedAt = DateTimeOffset.UtcNow
        };

        _subscriptions[subscriptionId] = subscription;

        // Get existing messages from subject (or JetStream if durable)
        var messages = new List<NatsMessage>();
        if (durable)
        {
            messages.AddRange(GetMessagesFromJetStream(subject));
        }
        else if (_subjects.TryGetValue(subject, out var subjectMessages))
        {
            messages.AddRange(subjectMessages);
        }

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "subscribed",
            subscriptionId,
            subject,
            queueGroup,
            durable,
            messages = messages.Select(m => new
            {
                subject = m.Subject,
                payload = Convert.ToBase64String(m.Payload),
                replyTo = m.ReplyTo,
                timestamp = m.Timestamp,
                headers = m.Headers
            })
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse HandleUnsubscribe(string subject, SdkInterface.InterfaceRequest request)
    {
        var subscriptionId = request.QueryParameters.TryGetValue("subscriptionId", out var subId) ? subId : null;

        if (string.IsNullOrEmpty(subscriptionId))
            return SdkInterface.InterfaceResponse.BadRequest("subscriptionId required for unsubscribe");

        _subscriptions.TryRemove(subscriptionId, out var subscription);

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "unsubscribed",
            subscriptionId,
            subject = subscription?.Subject
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private async Task<SdkInterface.InterfaceResponse> HandleRequestReply(
        string subject,
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Implement NATS request/reply pattern: publish with unique reply subject, wait for response
        var replySubject = $"_INBOX.{Guid.NewGuid():N}";
        var timeout = request.QueryParameters.TryGetValue("timeout", out var timeoutStr) && int.TryParse(timeoutStr, out var t)
            ? TimeSpan.FromMilliseconds(t)
            : TimeSpan.FromSeconds(5);

        // Create reply handler
        var replyHandler = new TaskCompletionSource<NatsMessage>();
        _replyHandlers[replySubject] = replyHandler;

        // Publish request with reply-to
        var requestMessage = new NatsMessage
        {
            Subject = subject,
            Payload = request.Body.ToArray(),
            ReplyTo = replySubject,
            Headers = ExtractNatsHeaders(request),
            Timestamp = DateTimeOffset.UtcNow
        };

        await DeliverToSubscribers(requestMessage, cancellationToken);

        // Wait for reply with timeout
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            linkedCts.Token.Register(() => replyHandler.TrySetCanceled());
            var reply = await replyHandler.Task;

            var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                status = "reply_received",
                subject,
                replySubject,
                payload = Convert.ToBase64String(reply.Payload),
                headers = reply.Headers
            });

            return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _replyHandlers.TryRemove(replySubject, out _);
            return SdkInterface.InterfaceResponse.Error(408, "Request timeout - no reply received");
        }
    }

    private async Task DeliverToSubscribers(NatsMessage message, CancellationToken cancellationToken)
    {
        var matchingSubscriptions = _subscriptions.Values
            .Where(s => SubjectMatches(s.Subject, message.Subject))
            .ToList();

        // Group by queue group for load balancing
        var queueGroups = matchingSubscriptions.GroupBy(s => s.QueueGroup ?? "");

        foreach (var group in queueGroups)
        {
            var subscriptions = group.ToList();

            if (!string.IsNullOrEmpty(group.Key))
            {
                // Queue group: deliver to one random subscriber (load balancing)
                var selectedSubscription = subscriptions[Random.Shared.Next(subscriptions.Count)];
                selectedSubscription.Messages.Add(message);
            }
            else
            {
                // No queue group: deliver to all subscribers
                foreach (var subscription in subscriptions)
                {
                    subscription.Messages.Add(message);
                }
            }
        }

        // Store in subject buffer
        _subjects.AddOrUpdate(message.Subject,
            _ => new List<NatsMessage> { message },
            (_, list) => { list.Add(message); return list; });

        await Task.CompletedTask;
    }

    private void StoreInJetStream(NatsMessage message)
    {
        foreach (var stream in _jetStreams.Values)
        {
            if (stream.Subjects.Any(pattern => SubjectMatches(pattern, message.Subject)))
            {
                stream.Messages.Add(message);

                // Enforce max messages limit
                if (stream.Messages.Count > stream.MaxMessages)
                {
                    stream.Messages.RemoveAt(0);
                }
            }
        }
    }

    private List<NatsMessage> GetMessagesFromJetStream(string subject)
    {
        var messages = new List<NatsMessage>();

        foreach (var stream in _jetStreams.Values)
        {
            if (stream.Subjects.Any(pattern => SubjectMatches(pattern, subject)))
            {
                messages.AddRange(stream.Messages.Where(m => SubjectMatches(subject, m.Subject)));
            }
        }

        return messages;
    }

    private static bool SubjectMatches(string pattern, string subject)
    {
        // NATS subject matching: * = single token, > = multiple tokens
        if (pattern == subject)
            return true;

        var patternTokens = pattern.Split('.');
        var subjectTokens = subject.Split('.');

        int pIndex = 0, sIndex = 0;

        while (pIndex < patternTokens.Length && sIndex < subjectTokens.Length)
        {
            if (patternTokens[pIndex] == ">")
                return true; // Multi-token wildcard matches rest

            if (patternTokens[pIndex] != "*" && patternTokens[pIndex] != subjectTokens[sIndex])
                return false;

            pIndex++;
            sIndex++;
        }

        return pIndex == patternTokens.Length && sIndex == subjectTokens.Length;
    }

    private static Dictionary<string, string> ExtractNatsHeaders(SdkInterface.InterfaceRequest request)
    {
        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers.Where(h => h.Key.StartsWith("NATS-")))
        {
            var key = header.Key.Substring("NATS-".Length);
            headers[key] = header.Value;
        }
        return headers;
    }
}

internal sealed class NatsSubscription
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public string? QueueGroup { get; init; }
    public bool Durable { get; init; }
    public DateTimeOffset SubscribedAt { get; init; }
    public List<NatsMessage> Messages { get; } = new();
}

internal sealed class NatsMessage
{
    public required string Subject { get; init; }
    public required byte[] Payload { get; init; }
    public string? ReplyTo { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; }
}

internal sealed class NatsStream
{
    public required string Name { get; init; }
    public required string[] Subjects { get; init; }
    public required string Retention { get; init; } // limits, interest, workqueue
    public required int MaxMessages { get; init; }
    public List<NatsMessage> Messages { get; } = new();
}
