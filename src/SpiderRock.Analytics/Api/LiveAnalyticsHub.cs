using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpiderRock.Analytics.Model;

namespace SpiderRock.Analytics.Api;

/// <summary>
/// WebSocket fan-out for live analytics, in the spirit of a streaming market
/// data API: a client connects, subscribes to a set of underlyings, and
/// receives an analytic message whenever one is recomputed.
///
/// The central design constraint is that a slow or dead client must never slow
/// the feed. Each subscriber therefore owns a bounded queue; when it fills, the
/// oldest pending message is dropped and a counter is incremented, rather than
/// blocking the publisher. For market data this is the right trade: a client
/// that has fallen behind wants the newest analytic, not a backlog of stale ones.
/// </summary>
public sealed class LiveAnalyticsHub
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ILogger<LiveAnalyticsHub>? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LiveAnalyticsHub(ILogger<LiveAnalyticsHub>? logger = null) => _logger = logger;

    public int SubscriberCount => _subscribers.Count;
    public long TotalDropped => _subscribers.Values.Sum(s => s.Dropped);

    private sealed class Subscriber
    {
        public required WebSocket Socket { get; init; }
        public required HashSet<string> Underlyings { get; init; }
        public readonly ConcurrentQueue<string> Queue = new();
        public readonly SemaphoreSlim Signal = new(0);
        public long Dropped;
        public const int MaxQueueDepth = 1024;
    }

    /// <summary>
    /// Called from the engine's publish event, i.e. on the feed thread. Must be
    /// non-blocking and must not throw: an exception here would propagate into
    /// quote processing.
    /// </summary>
    public void Publish(in OptionAnalytic analytic)
    {
        if (_subscribers.IsEmpty) return;

        string? payload = null;
        string underlying = analytic.Key.Underlying;

        foreach (var sub in _subscribers.Values)
        {
            if (sub.Underlyings.Count > 0 && !sub.Underlyings.Contains(underlying)) continue;

            // Serialize lazily and at most once per publish, only if at least
            // one subscriber actually wants this underlying.
            payload ??= JsonSerializer.Serialize(ToWire(analytic), JsonOptions);

            if (sub.Queue.Count >= Subscriber.MaxQueueDepth)
            {
                sub.Queue.TryDequeue(out _);
                Interlocked.Increment(ref sub.Dropped);
            }

            sub.Queue.Enqueue(payload);
            try { sub.Signal.Release(); } catch (SemaphoreFullException) { /* saturated; the pump will drain */ }
        }
    }

    /// <summary>Runs one client connection until it disconnects or the server stops.</summary>
    public async Task RunClientAsync(WebSocket socket, IEnumerable<string> underlyings, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var subscriber = new Subscriber
        {
            Socket = socket,
            Underlyings = new HashSet<string>(underlyings, StringComparer.OrdinalIgnoreCase)
        };

        _subscribers[id] = subscriber;
        _logger?.LogInformation("WebSocket client {Id} subscribed to {Count} underlying(s)",
            id, subscriber.Underlyings.Count == 0 ? "all" : subscriber.Underlyings.Count.ToString());

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await subscriber.Signal.WaitAsync(token);
                while (subscriber.Queue.TryDequeue(out var message))
                {
                    if (socket.State != WebSocketState.Open) return;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException ex)
        {
            _logger?.LogInformation("WebSocket client {Id} disconnected: {Message}", id, ex.Message);
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            _logger?.LogInformation("WebSocket client {Id} removed ({Dropped} message(s) dropped)",
                id, subscriber.Dropped);
        }
    }

    private static object ToWire(in OptionAnalytic a) => new
    {
        underlying = a.Key.Underlying,
        expiry = a.Key.Expiry.ToString("yyyy-MM-dd"),
        strike = a.Key.Strike,
        right = a.Key.Right == OptionRight.Call ? "C" : "P",
        underlyingPrice = a.UnderlyingPrice,
        yearsToExpiry = a.YearsToExpiry,
        impliedVol = a.ImpliedVol,
        delta = a.Delta,
        gamma = a.Gamma,
        vega = a.Vega,
        theta = a.Theta,
        rho = a.Rho,
        reason = a.Reason.ToString().ToUpperInvariant(),
        sequenceNumber = a.SequenceNumber,
        timestamp = a.TimestampUtc
    };
}
