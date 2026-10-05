using System.Buffers.Binary;
using System.Text;
using SpiderRock.Analytics.Model;

namespace SpiderRock.Analytics.Feed;

/// <summary>
/// Wire codec for the binary NBBO quote datagram.
///
/// Fixed 56-byte layout, little-endian, with the underlying symbol in a padded
/// 8-byte ASCII field. A fixed layout means the decoder never allocates and
/// never scans, which matters because this runs once per inbound packet on the
/// feed thread:
///
///   [0..7]   underlying, ASCII, null padded
///   [8..11]  expiry as yyyyMMdd packed int
///   [12..19] strike, double
///   [20]     right (0 = call, 1 = put)
///   [21..23] padding
///   [24..31] bid, double
///   [32..39] ask, double
///   [40..47] underlying price, double
///   [48..55] sequence number, long
/// </summary>
public static class QuoteCodec
{
    public const int MessageSize = 56;

    public static int Encode(in OptionQuote quote, Span<byte> buffer)
    {
        if (buffer.Length < MessageSize)
            throw new ArgumentException($"buffer must be at least {MessageSize} bytes", nameof(buffer));

        buffer[..MessageSize].Clear();

        int written = Encoding.ASCII.GetBytes(quote.Key.Underlying, buffer[..8]);
        if (written > 8) throw new ArgumentException("underlying symbol exceeds 8 bytes");

        int packedExpiry = quote.Key.Expiry.Year * 10000 + quote.Key.Expiry.Month * 100 + quote.Key.Expiry.Day;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..12], packedExpiry);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer[12..20], (double)quote.Key.Strike);
        buffer[20] = (byte)(quote.Key.Right == OptionRight.Call ? 0 : 1);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer[24..32], quote.Bid);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer[32..40], quote.Ask);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer[40..48], quote.UnderlyingPrice);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[48..56], quote.SequenceNumber);

        return MessageSize;
    }

    /// <summary>
    /// Decodes a datagram. Returns false on any malformed packet rather than
    /// throwing: a feed handler that throws on bad input stops consuming the
    /// socket, and one corrupt packet should never take the feed down.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out OptionQuote quote)
    {
        quote = default;
        if (buffer.Length < MessageSize) return false;

        int nul = buffer[..8].IndexOf((byte)0);
        int symbolLength = nul < 0 ? 8 : nul;
        if (symbolLength == 0) return false;

        string underlying = Encoding.ASCII.GetString(buffer[..symbolLength]);

        int packedExpiry = BinaryPrimitives.ReadInt32LittleEndian(buffer[8..12]);
        int year = packedExpiry / 10000;
        int month = packedExpiry / 100 % 100;
        int day = packedExpiry % 100;
        if (year < 1900 || year > 2999 || month is < 1 or > 12 || day is < 1 or > 31) return false;

        DateOnly expiry;
        try { expiry = new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return false; }

        double strike = BinaryPrimitives.ReadDoubleLittleEndian(buffer[12..20]);
        if (!double.IsFinite(strike) || strike <= 0) return false;

        byte rightByte = buffer[20];
        if (rightByte > 1) return false;

        double bid = BinaryPrimitives.ReadDoubleLittleEndian(buffer[24..32]);
        double ask = BinaryPrimitives.ReadDoubleLittleEndian(buffer[32..40]);
        double under = BinaryPrimitives.ReadDoubleLittleEndian(buffer[40..48]);
        if (!double.IsFinite(bid) || !double.IsFinite(ask) || !double.IsFinite(under)) return false;

        long seq = BinaryPrimitives.ReadInt64LittleEndian(buffer[48..56]);

        var key = new OptionKey(underlying, expiry, (decimal)strike,
            rightByte == 0 ? OptionRight.Call : OptionRight.Put);

        quote = new OptionQuote(key, bid, ask, under, seq, DateTime.UtcNow);
        return true;
    }
}
