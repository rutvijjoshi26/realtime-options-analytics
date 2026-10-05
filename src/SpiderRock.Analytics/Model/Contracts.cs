namespace SpiderRock.Analytics.Model;

public enum OptionRight
{
    Call,
    Put
}

/// <summary>
/// Cadence on which an analytic record was produced. Mirrors the three update
/// modes an options analytics feed typically publishes on:
///   TICK  - recomputed because top-of-book for this option changed
///   LOOP  - recomputed on a fixed timer regardless of quote activity
///   CLOSE - end-of-session snapshot, persisted for historical query
/// </summary>
public enum UpdateReason
{
    Tick,
    Loop,
    Close
}

/// <summary>
/// An option contract identifier. Immutable and used as a dictionary key, so it
/// is a record struct to keep the hot path allocation-free.
/// </summary>
public readonly record struct OptionKey(
    string Underlying,
    DateOnly Expiry,
    decimal Strike,
    OptionRight Right)
{
    public override string ToString() =>
        $"{Underlying} {Expiry:yyyyMMdd} {Strike:0.##}{(Right == OptionRight.Call ? "C" : "P")}";
}

/// <summary>
/// A raw NBBO quote as it arrives off the feed, before any analytics are applied.
/// </summary>
public readonly record struct OptionQuote(
    OptionKey Key,
    double Bid,
    double Ask,
    double UnderlyingPrice,
    long SequenceNumber,
    DateTime TimestampUtc)
{
    public double Mid => (Bid + Ask) / 2.0;
    public double Spread => Ask - Bid;

    /// <summary>
    /// A quote is only usable for implied vol if it is a genuine two-sided market.
    /// Crossed and locked books are the single most common source of garbage
    /// implied vols, so they are rejected at the edge rather than downstream.
    /// </summary>
    public bool IsTwoSided => Bid > 0 && Ask > 0 && Ask > Bid;
}

/// <summary>
/// The computed analytic record published to clients: implied volatility plus
/// the risk factors derived at that volatility, with the underlying price the
/// calculation was synchronized against.
/// </summary>
public readonly record struct OptionAnalytic(
    OptionKey Key,
    double UnderlyingPrice,
    double YearsToExpiry,
    double ImpliedVol,
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    double Rho,
    UpdateReason Reason,
    long SequenceNumber,
    DateTime TimestampUtc);
