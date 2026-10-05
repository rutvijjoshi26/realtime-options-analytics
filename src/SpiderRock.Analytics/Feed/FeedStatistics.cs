namespace SpiderRock.Analytics.Feed;

/// <summary>
/// Counters the feed handler maintains for operational visibility.
///
/// Gap detection is the important one: UDP delivery is unreliable and
/// out-of-order, so the handler tracks the highest sequence number seen per
/// source and reports how many messages went missing. Without this, a dropped
/// packet shows up much later as an inexplicably stale analytic, which is
/// exactly the kind of bug that is unreasonably hard to diagnose after the fact.
/// </summary>
public sealed class FeedStatistics
{
    private long _received;
    private long _decodeFailures;
    private long _rejectedQuotes;
    private long _gapCount;
    private long _gapMessages;
    private long _outOfOrder;
    private long _lastSequence = -1;

    public long Received => Interlocked.Read(ref _received);
    public long DecodeFailures => Interlocked.Read(ref _decodeFailures);
    public long RejectedQuotes => Interlocked.Read(ref _rejectedQuotes);
    public long GapCount => Interlocked.Read(ref _gapCount);
    public long GapMessages => Interlocked.Read(ref _gapMessages);
    public long OutOfOrder => Interlocked.Read(ref _outOfOrder);
    public long LastSequence => Interlocked.Read(ref _lastSequence);

    public void OnReceived() => Interlocked.Increment(ref _received);
    public void OnDecodeFailure() => Interlocked.Increment(ref _decodeFailures);
    public void OnRejectedQuote() => Interlocked.Increment(ref _rejectedQuotes);

    /// <summary>
    /// Records a sequence number and classifies it as in-order, a gap, or a late
    /// arrival. Called from the single feed-receive loop, so the read-modify-write
    /// on _lastSequence does not need to be atomic as a unit; the Interlocked
    /// write is there so the stats endpoint can read a torn-free value.
    /// </summary>
    public void OnSequence(long sequence)
    {
        long last = Interlocked.Read(ref _lastSequence);

        if (last < 0)
        {
            Interlocked.Exchange(ref _lastSequence, sequence);
            return;
        }

        if (sequence == last + 1)
        {
            Interlocked.Exchange(ref _lastSequence, sequence);
            return;
        }

        if (sequence > last + 1)
        {
            Interlocked.Increment(ref _gapCount);
            Interlocked.Add(ref _gapMessages, sequence - last - 1);
            Interlocked.Exchange(ref _lastSequence, sequence);
            return;
        }

        // sequence <= last: duplicate or late delivery.
        Interlocked.Increment(ref _outOfOrder);
    }

    public FeedStatsSnapshot Snapshot() => new(
        Received, DecodeFailures, RejectedQuotes, GapCount, GapMessages, OutOfOrder, LastSequence);
}

public readonly record struct FeedStatsSnapshot(
    long Received,
    long DecodeFailures,
    long RejectedQuotes,
    long GapCount,
    long GapMessages,
    long OutOfOrder,
    long LastSequence);
