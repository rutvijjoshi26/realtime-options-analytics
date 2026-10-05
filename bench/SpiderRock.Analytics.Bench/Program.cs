using System.Diagnostics;
using SpiderRock.Analytics;
using SpiderRock.Analytics.Feed;
using SpiderRock.Analytics.Model;
using SpiderRock.Analytics.Pricing;

// Throughput harness for the analytics pipeline.
//
// Measures the two stages that matter separately, because they have very
// different costs and conflating them hides where the budget actually goes:
//   1. codec     - decode a datagram off the wire
//   2. analytics - solve implied vol and compute all five greeks
//
// Reports per-operation latency percentiles rather than a single mean: an
// average hides the deep-wing contracts where the solver falls back to
// bisection, and those are exactly the ones worth knowing about.

const int WarmupIterations = 50_000;
const int MeasureIterations = 500_000;

var valuationDate = new DateOnly(2026, 9, 1);
var engine = new AnalyticsEngine(new AnalyticsOptions
{
    RiskFreeRate = 0.045,
    DividendYield = 0.0,
    ValuationDate = valuationDate
});

Console.WriteLine("SpiderRock options analytics - throughput benchmark");
Console.WriteLine($"Runtime: {Environment.Version}, {Environment.ProcessorCount} logical cores");
Console.WriteLine($"Server GC: {System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine();

var quotes = BuildSyntheticChain(valuationDate, count: 20_000);
Console.WriteLine($"Synthetic chain: {quotes.Length:N0} quotes across {quotes.Select(q => q.Key.Expiry).Distinct().Count()} expiries");
Console.WriteLine();

// --- Stage 1: codec ---------------------------------------------------------
var buffer = new byte[QuoteCodec.MessageSize];
QuoteCodec.Encode(quotes[0], buffer);

for (int i = 0; i < WarmupIterations; i++) QuoteCodec.TryDecode(buffer, out _);

var codecLatencies = new double[100_000];
var sw = Stopwatch.StartNew();
for (int i = 0; i < codecLatencies.Length; i++)
{
    long start = Stopwatch.GetTimestamp();
    QuoteCodec.TryDecode(buffer, out _);
    codecLatencies[i] = TicksToNanos(Stopwatch.GetTimestamp() - start);
}
sw.Stop();

long codecBulkStart = Stopwatch.GetTimestamp();
for (int i = 0; i < MeasureIterations; i++) QuoteCodec.TryDecode(buffer, out _);
double codecSeconds = TicksToNanos(Stopwatch.GetTimestamp() - codecBulkStart) / 1e9;

Report("Decode (single quote)", codecLatencies, MeasureIterations / codecSeconds);

// --- Stage 2: full analytics ------------------------------------------------
for (int i = 0; i < WarmupIterations; i++)
    engine.OnQuote(quotes[i % quotes.Length]);

var analyticLatencies = new double[100_000];
for (int i = 0; i < analyticLatencies.Length; i++)
{
    long start = Stopwatch.GetTimestamp();
    engine.OnQuote(quotes[i % quotes.Length]);
    analyticLatencies[i] = TicksToNanos(Stopwatch.GetTimestamp() - start);
}

long analyticsBulkStart = Stopwatch.GetTimestamp();
for (int i = 0; i < MeasureIterations; i++)
    engine.OnQuote(quotes[i % quotes.Length]);
double analyticsSeconds = TicksToNanos(Stopwatch.GetTimestamp() - analyticsBulkStart) / 1e9;

Report("Implied vol + 5 greeks", analyticLatencies, MeasureIterations / analyticsSeconds);

// --- Stage 3: surface refit -------------------------------------------------
foreach (var q in quotes) engine.OnQuote(q);

long refitStart = Stopwatch.GetTimestamp();
const int refitRuns = 200;
int smileCount = 0;
for (int i = 0; i < refitRuns; i++)
    smileCount = engine.RefitSurface("BENCH").Count;
double refitMillis = TicksToNanos(Stopwatch.GetTimestamp() - refitStart) / 1e6 / refitRuns;

Console.WriteLine($"Surface refit ({smileCount} expiries, {engine.TrackedContracts:N0} contracts): {refitMillis:F3} ms/refit");
Console.WriteLine();
Console.WriteLine($"Engine totals: {engine.ComputedCount:N0} computed, {engine.SolverFailureCount:N0} solver failures");

static void Report(string name, double[] latencies, double opsPerSecond)
{
    Array.Sort(latencies);
    Console.WriteLine($"{name}");
    Console.WriteLine($"  throughput : {opsPerSecond:N0} ops/sec");
    Console.WriteLine($"  p50        : {Percentile(latencies, 0.50):F1} ns");
    Console.WriteLine($"  p99        : {Percentile(latencies, 0.99):F1} ns");
    Console.WriteLine($"  p99.9      : {Percentile(latencies, 0.999):F1} ns");
    Console.WriteLine();
}

static double Percentile(double[] sorted, double p)
{
    int index = (int)Math.Ceiling(p * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

static double TicksToNanos(long ticks) => ticks * 1_000_000_000.0 / Stopwatch.Frequency;

static OptionQuote[] BuildSyntheticChain(DateOnly valuationDate, int count)
{
    // A chain shaped like a real one: several expiries, strikes spanning deep
    // ITM to deep OTM, and a skewed vol surface. The wings are the point -
    // benchmarking only at-the-money contracts would flatter the solver by
    // never exercising the bisection fallback.
    var expiries = new[] { 7, 30, 60, 120, 250, 500 }
        .Select(d => valuationDate.AddDays(d)).ToArray();

    const double spot = 450.0;
    var quotes = new List<OptionQuote>(count);
    long seq = 0;

    while (quotes.Count < count)
    {
        foreach (var expiry in expiries)
        {
            double t = (expiry.DayNumber - valuationDate.DayNumber) / 365.0;

            for (double k = spot * 0.6; k <= spot * 1.45; k += 2.5)
            {
                double x = Math.Log(k / spot);
                double vol = Math.Max(0.12, 0.22 - 0.12 * x + 0.45 * x * x);

                foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
                {
                    double fair = BlackScholes.Price(spot, k, t, 0.045, 0.0, vol, right);
                    if (fair < 0.02) continue; // Below a penny-wide market; a real feed would not quote it.

                    double halfSpread = Math.Max(0.01, fair * 0.01);
                    quotes.Add(new OptionQuote(
                        new OptionKey("BENCH", expiry, (decimal)Math.Round(k, 2), right),
                        fair - halfSpread, fair + halfSpread, spot, ++seq, DateTime.UtcNow));

                    if (quotes.Count >= count) return quotes.ToArray();
                }
            }
        }
    }

    return quotes.ToArray();
}
