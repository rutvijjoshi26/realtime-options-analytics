using MySqlConnector;
using SpiderRock.Analytics.Model;
using SpiderRock.Analytics.Surface;

namespace SpiderRock.Analytics.Storage;

/// <summary>
/// Persists CLOSE-cadence snapshots to MySQL and serves historical queries.
/// </summary>
public sealed class CloseSnapshotRepository
{
    private readonly string _connectionString;

    public CloseSnapshotRepository(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Writes a full session snapshot in one batched, transactional upsert.
    ///
    /// Row-at-a-time inserts across a full options chain are dominated by round
    /// trip latency, so this batches into multi-row INSERT statements and wraps
    /// the batch in a single transaction: either the whole session lands or none
    /// of it does, which keeps a partially-written close out of the historical
    /// record.
    /// </summary>
    public async Task<int> SaveAnalyticsAsync(
        DateOnly sessionDate,
        IReadOnlyCollection<OptionAnalytic> analytics,
        CancellationToken token = default)
    {
        if (analytics.Count == 0) return 0;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(token);
        await using var tx = await conn.BeginTransactionAsync(token);

        const int batchSize = 500;
        int written = 0;

        foreach (var chunk in analytics.Chunk(batchSize))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var sql = new System.Text.StringBuilder(
                "INSERT INTO close_analytics (session_date, underlying, expiry, strike, `right`, " +
                "underlying_price, years_to_expiry, implied_vol, delta, gamma, vega, theta, rho, " +
                "sequence_number, captured_at) VALUES ");

            for (int i = 0; i < chunk.Length; i++)
            {
                if (i > 0) sql.Append(',');
                sql.Append($"(@d{i},@u{i},@e{i},@k{i},@r{i},@s{i},@t{i},@v{i},@dl{i},@gm{i},@vg{i},@th{i},@rh{i},@sq{i},@ts{i})");

                var a = chunk[i];
                cmd.Parameters.AddWithValue($"@d{i}", sessionDate.ToDateTime(TimeOnly.MinValue).Date);
                cmd.Parameters.AddWithValue($"@u{i}", a.Key.Underlying);
                cmd.Parameters.AddWithValue($"@e{i}", a.Key.Expiry.ToDateTime(TimeOnly.MinValue).Date);
                cmd.Parameters.AddWithValue($"@k{i}", a.Key.Strike);
                cmd.Parameters.AddWithValue($"@r{i}", a.Key.Right == OptionRight.Call ? "C" : "P");
                cmd.Parameters.AddWithValue($"@s{i}", a.UnderlyingPrice);
                cmd.Parameters.AddWithValue($"@t{i}", a.YearsToExpiry);
                cmd.Parameters.AddWithValue($"@v{i}", a.ImpliedVol);
                cmd.Parameters.AddWithValue($"@dl{i}", a.Delta);
                cmd.Parameters.AddWithValue($"@gm{i}", a.Gamma);
                cmd.Parameters.AddWithValue($"@vg{i}", a.Vega);
                cmd.Parameters.AddWithValue($"@th{i}", a.Theta);
                cmd.Parameters.AddWithValue($"@rh{i}", a.Rho);
                cmd.Parameters.AddWithValue($"@sq{i}", a.SequenceNumber);
                cmd.Parameters.AddWithValue($"@ts{i}", a.TimestampUtc);
            }

            // Re-running the close job for a session should correct the record,
            // not fail on the primary key.
            sql.Append(" AS new ON DUPLICATE KEY UPDATE " +
                "underlying_price=new.underlying_price, years_to_expiry=new.years_to_expiry, " +
                "implied_vol=new.implied_vol, delta=new.delta, gamma=new.gamma, vega=new.vega, " +
                "theta=new.theta, rho=new.rho, sequence_number=new.sequence_number, " +
                "captured_at=new.captured_at");

            cmd.CommandText = sql.ToString();
            written += await cmd.ExecuteNonQueryAsync(token);
        }

        await tx.CommitAsync(token);
        return written;
    }

    public async Task<int> SaveSurfaceAsync(
        DateOnly sessionDate,
        string underlying,
        IReadOnlyCollection<FittedSmile> smiles,
        CancellationToken token = default)
    {
        if (smiles.Count == 0) return 0;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(token);
        await using var tx = await conn.BeginTransactionAsync(token);

        int written = 0;
        foreach (var smile in smiles)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO close_surface (session_date, underlying, expiry, years_to_expiry, forward, " +
                "coef_a, coef_b, coef_c, point_count, rms_error, arbitrage_free, arbitrage_note, captured_at) " +
                "VALUES (@d,@u,@e,@t,@f,@a,@b,@c,@n,@rms,@af,@note,@ts) AS new " +
                "ON DUPLICATE KEY UPDATE years_to_expiry=new.years_to_expiry, forward=new.forward, " +
                "coef_a=new.coef_a, coef_b=new.coef_b, coef_c=new.coef_c, point_count=new.point_count, " +
                "rms_error=new.rms_error, arbitrage_free=new.arbitrage_free, arbitrage_note=new.arbitrage_note, " +
                "captured_at=new.captured_at";

            cmd.Parameters.AddWithValue("@d", sessionDate.ToDateTime(TimeOnly.MinValue).Date);
            cmd.Parameters.AddWithValue("@u", underlying);
            cmd.Parameters.AddWithValue("@e", smile.Expiry.ToDateTime(TimeOnly.MinValue).Date);
            cmd.Parameters.AddWithValue("@t", smile.YearsToExpiry);
            cmd.Parameters.AddWithValue("@f", smile.Forward);
            cmd.Parameters.AddWithValue("@a", smile.A);
            cmd.Parameters.AddWithValue("@b", smile.B);
            cmd.Parameters.AddWithValue("@c", smile.C);
            cmd.Parameters.AddWithValue("@n", smile.PointCount);
            cmd.Parameters.AddWithValue("@rms", double.IsFinite(smile.RmsError) ? smile.RmsError : 0.0);
            cmd.Parameters.AddWithValue("@af", smile.IsArbitrageFree ? 1 : 0);
            cmd.Parameters.AddWithValue("@note", (object?)smile.ArbitrageNote ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow);

            written += await cmd.ExecuteNonQueryAsync(token);
        }

        await tx.CommitAsync(token);
        return written;
    }

    /// <summary>Historical chain query: every contract for one name on one session.</summary>
    public async Task<IReadOnlyList<OptionAnalytic>> QueryChainAsync(
        string underlying,
        DateOnly sessionDate,
        CancellationToken token = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT underlying, expiry, strike, `right`, underlying_price, years_to_expiry, implied_vol, " +
            "delta, gamma, vega, theta, rho, sequence_number, captured_at " +
            "FROM close_analytics WHERE underlying=@u AND session_date=@d " +
            "ORDER BY expiry, strike, `right`";
        cmd.Parameters.AddWithValue("@u", underlying);
        cmd.Parameters.AddWithValue("@d", sessionDate.ToDateTime(TimeOnly.MinValue).Date);

        var results = new List<OptionAnalytic>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var key = new OptionKey(
                reader.GetString(0),
                DateOnly.FromDateTime(reader.GetDateTime(1)),
                reader.GetDecimal(2),
                reader.GetString(3) == "C" ? OptionRight.Call : OptionRight.Put);

            results.Add(new OptionAnalytic(
                key, reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6),
                reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9),
                reader.GetDouble(10), reader.GetDouble(11),
                UpdateReason.Close, reader.GetInt64(12), reader.GetDateTime(13)));
        }

        return results;
    }

    public async Task EnsureSchemaAsync(CancellationToken token = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM close_analytics LIMIT 1";
        await cmd.ExecuteScalarAsync(token);
    }
}
