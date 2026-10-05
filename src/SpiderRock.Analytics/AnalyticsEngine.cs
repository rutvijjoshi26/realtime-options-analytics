using System.Collections.Concurrent;
using SpiderRock.Analytics.Model;
using SpiderRock.Analytics.Pricing;
using SpiderRock.Analytics.Surface;

namespace SpiderRock.Analytics;

public sealed class AnalyticsOptions
{
    /// <summary>Risk-free rate, continuously compounded.</summary>
    public double RiskFreeRate { get; init; } = 0.045;

    /// <summary>Continuous dividend yield on the underlying.</summary>
    public double DividendYield { get; init; } = 0.0;

    /// <summary>
    /// Widest bid/ask spread, as a fraction of mid, still considered a tradable
    /// two-sided market. Wider than this and the mid is not informative enough
    /// to imply a vol from, so the quote is counted and dropped.
    /// </summary>
    public double MaxRelativeSpread { get; init; } = 0.60;

    public DateOnly? ValuationDate { get; init; }
}

/// <summary>
/// The analytics core: consumes quotes, produces implied vol and risk factors,
/// maintains the live surface, and publishes to subscribers.
///
/// The current analytic per contract lives in a ConcurrentDictionary so that API
/// reads never block the feed thread. Publishing is fire-and-forget through an
/// event; a slow WebSocket client must not be able to apply backpressure to the
/// feed.
/// </summary>
public sealed class AnalyticsEngine
{
    private readonly AnalyticsOptions _options;
    private readonly ConcurrentDictionary<OptionKey, OptionAnalytic> _current = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DateOnly, FittedSmile>> _surfaces = new();
    private readonly ConcurrentDictionary<string, double> _underlyingPrices = new();

    private long _computed;
    private long _solverFailures;

    public AnalyticsEngine(AnalyticsOptions? options = null) => _options = options ?? new AnalyticsOptions();

    public event Action<OptionAnalytic>? AnalyticPublished;

    public long ComputedCount => Interlocked.Read(ref _computed);
    public long SolverFailureCount => Interlocked.Read(ref _solverFailures);
    public int TrackedContracts => _current.Count;

    /// <summary>
    /// Processes one quote end to end. Returns the analytic if one could be
    /// produced, or null when the quote was unusable - a crossed market, a
    /// spread too wide to imply from, an expired contract, or a price outside
    /// the no-arbitrage bounds.
    /// </summary>
    public OptionAnalytic? OnQuote(in OptionQuote quote, UpdateReason reason = UpdateReason.Tick)
    {
        if (!quote.IsTwoSided) return null;

        double mid = quote.Mid;
        if (mid <= 0) return null;
        if (quote.Spread / mid > _options.MaxRelativeSpread) return null;

        double t = YearsToExpiry(quote.Key.Expiry);
        if (t <= 0) return null;

        var result = ImpliedVolSolver.Solve(
            mid, quote.UnderlyingPrice, (double)quote.Key.Strike, t,
            _options.RiskFreeRate, _options.DividendYield, quote.Key.Right);

        if (!result.IsUsable)
        {
            Interlocked.Increment(ref _solverFailures);
            return null;
        }

        var greeks = BlackScholes.ComputeGreeks(
            quote.UnderlyingPrice, (double)quote.Key.Strike, t,
            _options.RiskFreeRate, _options.DividendYield, result.Vol, quote.Key.Right);

        var analytic = new OptionAnalytic(
            quote.Key, quote.UnderlyingPrice, t, result.Vol,
            greeks.Delta, greeks.Gamma, greeks.Vega, greeks.Theta, greeks.Rho,
            reason, quote.SequenceNumber, quote.TimestampUtc);

        // Late-arriving quotes must not overwrite a newer analytic. UDP reorders,
        // so without this guard a delayed packet silently rewinds the book.
        _current.AddOrUpdate(quote.Key, analytic,
            (_, existing) => analytic.SequenceNumber >= existing.SequenceNumber ? analytic : existing);

        _underlyingPrices[quote.Key.Underlying] = quote.UnderlyingPrice;
        Interlocked.Increment(ref _computed);

        AnalyticPublished?.Invoke(analytic);
        return analytic;
    }

    public double YearsToExpiry(DateOnly expiry)
    {
        var today = _options.ValuationDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return (expiry.DayNumber - today.DayNumber) / 365.0;
    }

    public bool TryGetAnalytic(OptionKey key, out OptionAnalytic analytic) => _current.TryGetValue(key, out analytic);

    public IReadOnlyCollection<OptionAnalytic> Snapshot() => _current.Values.ToArray();

    public IReadOnlyCollection<OptionAnalytic> SnapshotFor(string underlying) =>
        _current.Values.Where(a => a.Key.Underlying == underlying).ToArray();

    public IReadOnlyCollection<string> Underlyings() => _underlyingPrices.Keys.ToArray();

    /// <summary>
    /// Refits every expiry for one underlying from the current analytics. Called
    /// on the LOOP cadence rather than per tick: a surface refit touches the
    /// whole chain, and doing that on every quote would spend the entire budget
    /// refitting curves nobody read.
    /// </summary>
    public IReadOnlyCollection<FittedSmile> RefitSurface(string underlying)
    {
        if (!_underlyingPrices.TryGetValue(underlying, out double spot))
            return Array.Empty<FittedSmile>();

        var byExpiry = _current.Values
            .Where(a => a.Key.Underlying == underlying && double.IsFinite(a.ImpliedVol))
            .GroupBy(a => a.Key.Expiry);

        var fitted = new List<FittedSmile>();
        var perUnderlying = _surfaces.GetOrAdd(underlying, _ => new ConcurrentDictionary<DateOnly, FittedSmile>());

        foreach (var group in byExpiry)
        {
            double t = YearsToExpiry(group.Key);
            if (t <= 0) continue;

            double forward = spot * Math.Exp((_options.RiskFreeRate - _options.DividendYield) * t);

            var points = group.Select(a =>
            {
                double x = Math.Log((double)a.Key.Strike / forward);
                // Weight by vega: a vol error on a high-vega contract moves the
                // price far more than the same error on a deep wing, so the fit
                // should care proportionally more about getting it right.
                double weight = Math.Max(a.Vega, 1e-6);
                return new SmilePoint(x, a.ImpliedVol, weight);
            }).ToList();

            var smile = VolSurfaceFitter.Fit(group.Key, t, forward, points);
            perUnderlying[group.Key] = smile;
            fitted.Add(smile);
        }

        return fitted;
    }

    public IReadOnlyCollection<FittedSmile> GetSurface(string underlying) =>
        _surfaces.TryGetValue(underlying, out var s) ? s.Values.ToArray() : Array.Empty<FittedSmile>();
}
