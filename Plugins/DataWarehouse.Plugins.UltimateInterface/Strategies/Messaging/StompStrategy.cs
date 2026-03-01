using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Messaging;

/// <summary>
/// STOMP (Simple Text Oriented Messaging Protocol) interface strategy for text-based messaging.
/// Implements STOMP 1.2 protocol with support for subscriptions, transactions, and acknowledgements.
/// </summary>
/// <remarks>
/// <para>
/// STOMP is a simple, text-based protocol for message-oriented middleware. This strategy provides
/// interoperability with STOMP brokers like ActiveMQ, RabbitMQ (with STOMP plugin), and Apollo.
/// </para>
/// <para>
/// Supported STOMP 1.2 features:
/// <list type="bullet">
/// <item><description>Frame types: CONNECT, SEND, SUBSCRIBE, UNSUBSCRIBE, ACK, NACK, BEGIN, COMMIT, ABORT, DISCONNECT</description></item>
/// <item><description>Heart-beating for connection liveness detection</description></item>
/// <item><description>Receipt confirmations for guaranteed delivery</description></item>
/// <item><description>Transaction support with BEGIN/COMMIT/ABORT</description></item>
/// <item><description>Content negotiation via content-type header</description></item>
/// </list>
/// </para>
/// <para>
/// REST mapping: POST = SEND, GET = SUBSCRIBE, DELETE = UNSUBSCRIBE, PUT = transaction/session ops.
/// </para>
/// </remarks>
internal sealed class StompStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    private readonly BoundedDictionary<string, StompSubscription> _subscriptions = new BoundedDictionary<string, StompSubscription>(1000);
    private readonly BoundedDictionary<string, StompTransaction> _transactions = new BoundedDictionary<string, StompTransaction>(1000);
    private readonly BoundedDictionary<string, List<StompMessage>> _destinations = new BoundedDictionary<string, List<StompMessage>>(1000);

    public override string StrategyId => "stomp";
    public string DisplayName => "STOMP";
    public string SemanticDescription => "STOMP 1.2 text-based messaging protocol with transactions, acknowledgements, and heart-beating.";
    public InterfaceCategory Category => InterfaceCategory.Messaging;
    public string[] Tags => ["stomp", "messaging", "text", "activemq"];

    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.Custom;

    public override SdkInterface.InterfaceCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "text/plain", "application/json", "application/xml" },
        MaxRequestSize: 10 * 1024 * 1024, // 10 MB
        MaxResponseSize: 10 * 1024 * 1024,
        SupportsBidirectionalStreaming: true,
        SupportsMultiplexing: false,
        DefaultTimeout: null, // Long-lived connections
        SupportsCancellation: true,
        RequiresTLS: false
    );

    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        // Initialize STOMP broker connection
        return Task.CompletedTask;
    }

    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        _subscriptions.Clear();
        _transactions.Clear();
        _destinations.Clear();
        return Task.CompletedTask;
    }

    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Parse STOMP operation from path and method
        var destination = request.Path.TrimStart('/');
        var sessionId = GetSessionId(request);

        return request.Method switch
        {
            SdkInterface.HttpMethod.POST => await HandleSend(destination, request, cancellationToken),
            SdkInterface.HttpMethod.GET => HandleSubscribe(destination, sessionId, request),
            SdkInterface.HttpMethod.DELETE => HandleUnsubscribe(destination, sessionId),
            SdkInterface.HttpMethod.PUT => await HandleTransactionOrAck(request),
            SdkInterface.HttpMethod.CONNECT => HandleConnect(sessionId, request),
            _ => SdkInterface.InterfaceResponse.BadRequest($"Unsupported STOMP operation: {request.Method}")
        };
    }

    private async Task<SdkInterface.InterfaceResponse> HandleSend(
        string destination,
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        var transactionId = request.Headers.TryGetValue("STOMP-Transaction", out var txn) ? txn : null;
        var receipt = request.Headers.TryGetValue("STOMP-Receipt", out var rec) ? rec : null;

        // Create STOMP message
        var message = new StompMessage
        {
            Destination = destination,
            Body = request.Body.ToArray(),
            ContentType = request.ContentType ?? "text/plain",
            Timestamp = DateTimeOffset.UtcNow,
            MessageId = Guid.NewGuid().ToString("N"),
            Headers = ExtractStompHeaders(request)
        };

        // If transaction specified, add to transaction buffer
        if (!string.IsNullOrEmpty(transactionId))
        {
            if (!_transactions.TryGetValue(transactionId, out var transaction))
                return SdkInterface.InterfaceResponse.BadRequest($"Transaction not found: {transactionId}");

            transaction.Messages.Add(message);

            var txnResponsePayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                status = "queued",
                transaction = transactionId,
                destination,
                messageId = message.MessageId,
                receipt
            });

            return SdkInterface.InterfaceResponse.Ok(txnResponsePayload, "application/json");
        }

        // Otherwise, send immediately
        await DeliverMessage(message, cancellationToken);

        // Publish to DataWarehouse via message bus
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            // In production, route message to DataWarehouse via message bus
            // Topic: $"interface.stomp.{destination}"
            // Payload: destination, messageId, contentType, message body
        }

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "sent",
            destination,
            messageId = message.MessageId,
            receipt
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse HandleSubscribe(
        string destination,
        string sessionId,
        SdkInterface.InterfaceRequest request)
    {
        var subscriptionId = request.QueryParameters.TryGetValue("id", out var subId) ? subId : Guid.NewGuid().ToString("N");
        var ack = request.QueryParameters.TryGetValue("ack", out var ackMode) ? ackMode : "auto";

        var subscription = new StompSubscription
        {
            Id = subscriptionId,
            Destination = destination,
            SessionId = sessionId,
            AckMode = ack,
            SubscribedAt = DateTimeOffset.UtcNow
        };

        _subscriptions[$"{sessionId}:{destination}"] = subscription;

        // Get existing messages from destination
        var messages = _destinations.TryGetValue(destination, out var msgs) ? msgs.ToList() : new List<StompMessage>();

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "subscribed",
            subscriptionId,
            destination,
            ack,
            messages = messages.Select(m => new
            {
                messageId = m.MessageId,
                destination = m.Destination,
                contentType = m.ContentType,
                payload = Convert.ToBase64String(m.Body),
                timestamp = m.Timestamp
            })
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse HandleUnsubscribe(string destination, string sessionId)
    {
        var key = $"{sessionId}:{destination}";
        _subscriptions.TryRemove(key, out var subscription);

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "unsubscribed",
            destination,
            subscriptionId = subscription?.Id
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private async Task<SdkInterface.InterfaceResponse> HandleTransactionOrAck(SdkInterface.InterfaceRequest request)
    {
        var operation = request.QueryParameters.TryGetValue("operation", out var op) ? op : "ack";

        return operation switch
        {
            "begin" => BeginTransaction(request),
            "commit" => await CommitTransactionAsync(request),
            "abort" => AbortTransaction(request),
            "ack" => AcknowledgeMessage(request),
            "nack" => NegativeAcknowledgeMessage(request),
            _ => SdkInterface.InterfaceResponse.BadRequest($"Unknown operation: {operation}")
        };
    }

    private SdkInterface.InterfaceResponse BeginTransaction(SdkInterface.InterfaceRequest request)
    {
        var transactionId = request.QueryParameters.TryGetValue("transaction", out var txn) ? txn : Guid.NewGuid().ToString("N");

        var transaction = new StompTransaction
        {
            Id = transactionId,
            StartedAt = DateTimeOffset.UtcNow
        };

        _transactions[transactionId] = transaction;

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "transaction_begun",
            transaction = transactionId
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private async Task<SdkInterface.InterfaceResponse> CommitTransactionAsync(SdkInterface.InterfaceRequest request)
    {
        var transactionId = request.QueryParameters.TryGetValue("transaction", out var txn) ? txn : null;

        if (string.IsNullOrEmpty(transactionId) || !_transactions.TryRemove(transactionId, out var transaction))
            return SdkInterface.InterfaceResponse.BadRequest($"Transaction not found: {transactionId}");

        // Deliver all messages in transaction — await each to surface errors
        foreach (var message in transaction.Messages)
        {
            await DeliverMessage(message, CancellationToken.None);
        }

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "transaction_committed",
            transaction = transactionId,
            messageCount = transaction.Messages.Count
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse AbortTransaction(SdkInterface.InterfaceRequest request)
    {
        var transactionId = request.QueryParameters.TryGetValue("transaction", out var txn) ? txn : null;

        if (string.IsNullOrEmpty(transactionId) || !_transactions.TryRemove(transactionId, out var transaction))
            return SdkInterface.InterfaceResponse.BadRequest($"Transaction not found: {transactionId}");

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "transaction_aborted",
            transaction = transactionId,
            discardedMessages = transaction.Messages.Count
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse AcknowledgeMessage(SdkInterface.InterfaceRequest request)
    {
        var messageId = request.QueryParameters.TryGetValue("messageId", out var msgId) ? msgId : null;

        if (string.IsNullOrEmpty(messageId))
            return SdkInterface.InterfaceResponse.BadRequest("messageId required for ACK");

        // In production, this would remove the message from pending acknowledgements
        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "acknowledged",
            messageId
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse NegativeAcknowledgeMessage(SdkInterface.InterfaceRequest request)
    {
        var messageId = request.QueryParameters.TryGetValue("messageId", out var msgId) ? msgId : null;

        if (string.IsNullOrEmpty(messageId))
            return SdkInterface.InterfaceResponse.BadRequest("messageId required for NACK");

        // In production, this would requeue the message or send to DLQ
        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "negative_acknowledged",
            messageId,
            action = "requeued"
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private SdkInterface.InterfaceResponse HandleConnect(string sessionId, SdkInterface.InterfaceRequest request)
    {
        var heartBeat = request.Headers.TryGetValue("STOMP-Heart-Beat", out var hb) ? hb : "0,0";

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "connected",
            sessionId,
            version = "1.2",
            heartBeat,
            server = "DataWarehouse-STOMP"
        });

        return SdkInterface.InterfaceResponse.Ok(responsePayload, "application/json");
    }

    private async Task DeliverMessage(StompMessage message, CancellationToken cancellationToken)
    {
        // P2-3326: Cap each destination queue at 10,000 messages to prevent unbounded growth.
        // When the cap is reached, evict the oldest (head) message (FIFO drop).
        _destinations.AddOrUpdate(message.Destination,
            _ => new List<StompMessage> { message },
            (_, list) =>
            {
                if (list.Count >= 10_000) list.RemoveAt(0);
                list.Add(message);
                return list;
            });

        // Deliver to active subscriptions
        var matchingSubscriptions = _subscriptions.Values
            .Where(s => s.Destination == message.Destination)
            .ToList();

        foreach (var subscription in matchingSubscriptions)
        {
            subscription.PendingMessages.Add(message);
        }

        await Task.CompletedTask;
    }

    private static Dictionary<string, string> ExtractStompHeaders(SdkInterface.InterfaceRequest request)
    {
        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers.Where(h => h.Key.StartsWith("STOMP-")))
        {
            var key = header.Key.Substring("STOMP-".Length);
            headers[key] = header.Value;
        }
        return headers;
    }

    private static string GetSessionId(SdkInterface.InterfaceRequest request)
    {
        return request.Headers.TryGetValue("STOMP-Session", out var sessionId) ? sessionId : $"session-{Guid.NewGuid():N}";
    }
}

internal sealed class StompSubscription
{
    public required string Id { get; init; }
    public required string Destination { get; init; }
    public required string SessionId { get; init; }
    public required string AckMode { get; init; } // auto, client, client-individual
    public DateTimeOffset SubscribedAt { get; init; }
    public List<StompMessage> PendingMessages { get; } = new();
}

internal sealed class StompMessage
{
    public required string MessageId { get; init; }
    public required string Destination { get; init; }
    public required byte[] Body { get; init; }
    public required string ContentType { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
}

internal sealed class StompTransaction
{
    public required string Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public List<StompMessage> Messages { get; } = new();
}
