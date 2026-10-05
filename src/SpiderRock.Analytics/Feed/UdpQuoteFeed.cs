using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpiderRock.Analytics.Model;

namespace SpiderRock.Analytics.Feed;

public sealed class FeedOptions
{
    public int Port { get; init; } = 9301;
    public string BindAddress { get; init; } = "127.0.0.1";

    /// <summary>Optional multicast group to join, mirroring a production multicast feed.</summary>
    public string? MulticastGroup { get; init; }

    /// <summary>How often to recompute surfaces and republish on the LOOP cadence.</summary>
    public TimeSpan LoopInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// UDP feed handler. Receives quote datagrams, decodes them, and drives the
/// analytics engine on the TICK cadence, while a timer drives the LOOP cadence
/// surface refit.
///
/// The receive loop deliberately does no analytics work beyond the decode and
/// the engine call: everything expensive is either amortized onto the LOOP timer
/// or handed to subscribers asynchronously, so the socket keeps draining.
/// </summary>
public sealed class UdpQuoteFeed : BackgroundService
{
    private readonly FeedOptions _options;
    private readonly AnalyticsEngine _engine;
    private readonly ILogger<UdpQuoteFeed>? _logger;

    public FeedStatistics Statistics { get; } = new();

    public UdpQuoteFeed(FeedOptions options, AnalyticsEngine engine, ILogger<UdpQuoteFeed>? logger = null)
    {
        _options = options;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // A generous receive buffer is the cheapest defence against drops during
        // a burst; the kernel absorbs the spike instead of discarding datagrams
        // while this loop is between reads.
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Parse(_options.BindAddress), _options.Port));

        if (_options.MulticastGroup is { } group)
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(group), IPAddress.Parse(_options.BindAddress)));
            _logger?.LogInformation("Joined multicast group {Group} on port {Port}", group, _options.Port);
        }

        _logger?.LogInformation("Feed listening on {Address}:{Port}", _options.BindAddress, _options.Port);

        var loopTask = RunLoopCadenceAsync(stoppingToken);
        var buffer = new byte[2048];

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int received;
                try
                {
                    received = await socket.ReceiveAsync(buffer, SocketFlags.None, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // ConnectionReset on UDP means an ICMP port-unreachable came
                    // back from a previous send. It says nothing about this
                    // socket's ability to keep receiving, so log and continue
                    // rather than tearing the feed down.
                    _logger?.LogWarning(ex, "Socket error on receive; continuing");
                    continue;
                }

                Statistics.OnReceived();

                if (!QuoteCodec.TryDecode(buffer.AsSpan(0, received), out var quote))
                {
                    Statistics.OnDecodeFailure();
                    continue;
                }

                Statistics.OnSequence(quote.SequenceNumber);

                if (_engine.OnQuote(quote, UpdateReason.Tick) is null)
                    Statistics.OnRejectedQuote();
            }
        }
        finally
        {
            await loopTask;
        }
    }

    private async Task RunLoopCadenceAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.LoopInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (var underlying in _engine.Underlyings())
                {
                    var smiles = _engine.RefitSurface(underlying);
                    foreach (var smile in smiles)
                    {
                        if (!smile.IsArbitrageFree)
                            _logger?.LogWarning("Arbitrage check failed for {Underlying} {Expiry}: {Note}",
                                underlying, smile.Expiry, smile.ArbitrageNote);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
