using SpiderRock.Analytics.Feed;
using SpiderRock.Analytics.Model;
using SpiderRock.Analytics.Pricing;
using Xunit;

namespace SpiderRock.Analytics.Tests;

public class QuoteCodecTests
{
    private static OptionQuote Sample() => new(
        new OptionKey("SPY", new DateOnly(2026, 12, 18), 505.50m, OptionRight.Put),
        3.25, 3.45, 502.10, 987654321L, DateTime.UtcNow);

    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var original = Sample();
        Span<byte> buffer = stackalloc byte[QuoteCodec.MessageSize];
        int written = QuoteCodec.Encode(original, buffer);

        Assert.Equal(QuoteCodec.MessageSize, written);
        Assert.True(QuoteCodec.TryDecode(buffer, out var decoded));

        Assert.Equal(original.Key, decoded.Key);
        Assert.Equal(original.Bid, decoded.Bid, 10);
        Assert.Equal(original.Ask, decoded.Ask, 10);
        Assert.Equal(original.UnderlyingPrice, decoded.UnderlyingPrice, 10);
        Assert.Equal(original.SequenceNumber, decoded.SequenceNumber);
    }

    [Fact]
    public void Decode_RejectsTruncatedDatagram()
    {
        var buffer = new byte[QuoteCodec.MessageSize];
        QuoteCodec.Encode(Sample(), buffer);

        Assert.False(QuoteCodec.TryDecode(buffer.AsSpan(0, QuoteCodec.MessageSize - 1), out _));
        Assert.False(QuoteCodec.TryDecode(ReadOnlySpan<byte>.Empty, out _));
    }

    /// <summary>
    /// A corrupt packet must be rejected, never thrown on: an exception in the
    /// decode path would stop the receive loop and take the whole feed down.
    /// </summary>
    [Fact]
    public void Decode_RejectsGarbageWithoutThrowing()
    {
        var rng = new Random(20260903);
        int accepted = 0;

        for (int i = 0; i < 20_000; i++)
        {
            var buffer = new byte[QuoteCodec.MessageSize];
            rng.NextBytes(buffer);

            var exception = Record.Exception(() => QuoteCodec.TryDecode(buffer, out var q));
            Assert.Null(exception);

            if (QuoteCodec.TryDecode(buffer, out _)) accepted++;
        }

        // Random bytes should essentially never form a valid date + finite prices.
        Assert.True(accepted < 20, $"{accepted} random packets decoded as valid");
    }

    [Fact]
    public void Decode_RejectsInvalidExpiryAndRight()
    {
        var buffer = new byte[QuoteCodec.MessageSize];
        QuoteCodec.Encode(Sample(), buffer);

        var corruptedDate = (byte[])buffer.Clone();
        // Month 13 -> packed yyyyMMdd of 20261318.
        BitConverter.GetBytes(20261318).CopyTo(corruptedDate, 8);
        Assert.False(QuoteCodec.TryDecode(corruptedDate, out _));

        var corruptedRight = (byte[])buffer.Clone();
        corruptedRight[20] = 7;
        Assert.False(QuoteCodec.TryDecode(corruptedRight, out _));
    }
}

public class FeedStatisticsTests
{
    [Fact]
    public void DetectsGapsAndCountsMissingMessages()
    {
        var stats = new FeedStatistics();
        foreach (long seq in new long[] { 1, 2, 3, 7, 8 })
            stats.OnSequence(seq);

        Assert.Equal(1, stats.GapCount);
        Assert.Equal(3, stats.GapMessages); // 4, 5, 6
        Assert.Equal(8, stats.LastSequence);
        Assert.Equal(0, stats.OutOfOrder);
    }

    [Fact]
    public void CountsLateArrivalsWithoutRewindingSequence()
    {
        var stats = new FeedStatistics();
        foreach (long seq in new long[] { 1, 2, 5, 3, 6 })
            stats.OnSequence(seq);

        Assert.Equal(1, stats.OutOfOrder);
        Assert.Equal(6, stats.LastSequence);
    }
}

public class AnalyticsEngineTests
{
    private static readonly DateOnly Today = new(2026, 9, 1);
    private static readonly DateOnly Expiry = new(2027, 3, 1);

    private static AnalyticsEngine NewEngine() => new(new AnalyticsOptions
    {
        RiskFreeRate = 0.045,
        DividendYield = 0.0,
        ValuationDate = Today
    });

    private static OptionQuote QuoteAtVol(AnalyticsEngine engine, double strike, double vol, double spread, long seq)
    {
        double t = engine.YearsToExpiry(Expiry);
        double fair = BlackScholes.Price(100, strike, t, 0.045, 0.0, vol, OptionRight.Call);
        return new OptionQuote(
            new OptionKey("TEST", Expiry, (decimal)strike, OptionRight.Call),
            fair - spread / 2, fair + spread / 2, 100.0, seq, DateTime.UtcNow);
    }

    [Fact]
    public void OnQuote_RecoversTheVolThatGeneratedTheMid()
    {
        var engine = NewEngine();
        var analytic = engine.OnQuote(QuoteAtVol(engine, 100, 0.28, 0.02, 1));

        Assert.NotNull(analytic);
        Assert.Equal(0.28, analytic!.Value.ImpliedVol, 3);
        Assert.InRange(analytic.Value.Delta, 0.0, 1.0);
        Assert.True(analytic.Value.Vega > 0);
    }

    [Fact]
    public void OnQuote_RejectsCrossedAndLockedMarkets()
    {
        var engine = NewEngine();
        var key = new OptionKey("TEST", Expiry, 100m, OptionRight.Call);

        var crossed = new OptionQuote(key, 5.0, 4.0, 100, 1, DateTime.UtcNow);
        var locked = new OptionQuote(key, 4.0, 4.0, 100, 2, DateTime.UtcNow);
        var zeroBid = new OptionQuote(key, 0.0, 4.0, 100, 3, DateTime.UtcNow);

        Assert.Null(engine.OnQuote(crossed));
        Assert.Null(engine.OnQuote(locked));
        Assert.Null(engine.OnQuote(zeroBid));
        Assert.Equal(0, engine.ComputedCount);
    }

    [Fact]
    public void OnQuote_RejectsSpreadWiderThanThreshold()
    {
        var engine = NewEngine();
        // A market this wide carries no usable information in its mid.
        var wide = QuoteAtVol(engine, 100, 0.28, spread: 14.0, seq: 1);
        Assert.Null(engine.OnQuote(wide));
    }

    /// <summary>
    /// UDP reorders. A late packet carrying an older sequence number must not
    /// overwrite the newer analytic already published for that contract.
    /// </summary>
    [Fact]
    public void OnQuote_LateArrivalDoesNotRewindTheBook()
    {
        var engine = NewEngine();
        var key = new OptionKey("TEST", Expiry, 100m, OptionRight.Call);

        engine.OnQuote(QuoteAtVol(engine, 100, 0.20, 0.02, seq: 10));
        engine.OnQuote(QuoteAtVol(engine, 100, 0.40, 0.02, seq: 11));
        engine.OnQuote(QuoteAtVol(engine, 100, 0.30, 0.02, seq: 5)); // late

        Assert.True(engine.TryGetAnalytic(key, out var current));
        Assert.Equal(11, current.SequenceNumber);
        Assert.Equal(0.40, current.ImpliedVol, 3);
    }

    [Fact]
    public void RefitSurface_ProducesArbitrageFreeSmileFromCleanChain()
    {
        var engine = NewEngine();
        long seq = 0;

        // Generate a chain from a known, well-behaved skew.
        foreach (double k in new[] { 80.0, 85.0, 90.0, 95.0, 100.0, 105.0, 110.0, 115.0, 120.0 })
        {
            double x = Math.Log(k / 100.0);
            double vol = 0.25 - 0.10 * x + 0.30 * x * x;
            engine.OnQuote(QuoteAtVol(engine, k, vol, 0.02, ++seq));
        }

        var smiles = engine.RefitSurface("TEST");
        var smile = Assert.Single(smiles);

        Assert.True(smile.IsArbitrageFree, smile.ArbitrageNote);
        Assert.True(smile.RmsError < 0.01, $"fit error too high: {smile.RmsError}");
        Assert.True(smile.Skew < 0, "expected a negative skew from the generating function");
        Assert.Equal(9, smile.PointCount);
    }

    [Fact]
    public void Engine_TracksSolverFailures()
    {
        var engine = NewEngine();
        var key = new OptionKey("TEST", Expiry, 100m, OptionRight.Call);

        // A two-sided market whose mid exceeds the underlying price entirely:
        // passes the quote filters, fails the no-arbitrage bound in the solver.
        var absurd = new OptionQuote(key, 150.0, 151.0, 100.0, 1, DateTime.UtcNow);
        Assert.Null(engine.OnQuote(absurd));
        Assert.Equal(1, engine.SolverFailureCount);
    }
}
