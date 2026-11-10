using Npgsql;

namespace TabService.Data;

public sealed record SessionRow(
    Guid Id,
    DateTimeOffset StartedAtUtc,
    string? LocationSource,
    byte[] OwnerTokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    int LastSequence)
{
    public bool IsOpen => ClosedAt is null;

    public SessionInfo ToInfo() => new(Id, StartedAtUtc, LocationSource, CreatedAt, LastSeenAt, LastSequence, ClosedAt, CloseReason);
}

public sealed class SessionStore(NpgsqlDataSource db)
{
    private const string Columns =
        "id, started_at_utc, location_source, owner_token_hash, created_at, last_seen_at, closed_at, close_reason, last_sequence";

    public async Task<Guid> CreateAsync(
        string? clientTripId, DateTimeOffset startedAtUtc, string? locationSource,
        byte[] ownerTokenHash, byte[] viewerTokenHash, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.CreateCommand(
            """
            INSERT INTO share_sessions (id, client_trip_id, started_at_utc, location_source, owner_token_hash, viewer_token_hash)
            VALUES (@id, @trip, @started, @source, @owner, @viewer)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("trip", (object?)clientTripId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("started", startedAtUtc.ToUniversalTime());
        cmd.Parameters.AddWithValue("source", (object?)locationSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("owner", ownerTokenHash);
        cmd.Parameters.AddWithValue("viewer", viewerTokenHash);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public Task<SessionRow?> GetAsync(Guid id, CancellationToken ct) =>
        QuerySingleAsync($"SELECT {Columns} FROM share_sessions WHERE id = @p", id, ct);

    public Task<SessionRow?> GetByViewerTokenHashAsync(byte[] hash, CancellationToken ct) =>
        QuerySingleAsync($"SELECT {Columns} FROM share_sessions WHERE viewer_token_hash = @p", hash, ct);

    /// <summary>Closes an open session. Returns false if it was already closed or does not exist.</summary>
    public async Task<bool> CloseAsync(Guid id, string reason, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE share_sessions SET closed_at = now(), close_reason = @reason WHERE id = @id AND closed_at IS NULL");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("reason", reason);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Records upload activity and returns the highest sequence received so far.</summary>
    public async Task<int> TouchAsync(Guid id, int highestSequence, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            UPDATE share_sessions SET last_seen_at = now(), last_sequence = GREATEST(last_sequence, @seq)
            WHERE id = @id RETURNING last_sequence
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("seq", highestSequence);
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? -1);
    }

    public async Task<List<Guid>> CloseStaleAsync(TimeSpan idle, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            UPDATE share_sessions SET closed_at = now(), close_reason = @reason
            WHERE closed_at IS NULL AND last_seen_at < now() - @idle RETURNING id
            """);
        cmd.Parameters.AddWithValue("reason", CloseReasons.Expired);
        cmd.Parameters.AddWithValue("idle", idle);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>Deletes sessions (and their samples) created longer ago than the retention period.</summary>
    public async Task<int> DeleteOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            WITH old AS (DELETE FROM share_sessions WHERE created_at < now() - @age RETURNING id),
                 gone AS (DELETE FROM gps_samples WHERE session_id IN (SELECT id FROM old))
            SELECT count(*) FROM old
            """);
        cmd.Parameters.AddWithValue("age", age);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Every stored session with its sample count and time span, newest first (admin dashboard).</summary>
    public async Task<List<AdminSessionSummary>> ListAllAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT s.id, s.client_trip_id, s.started_at_utc, s.location_source, s.created_at, s.last_seen_at,
                   s.closed_at, s.close_reason, st.n, st.first_time, st.last_time
            FROM share_sessions s
            LEFT JOIN LATERAL (
                SELECT count(*) AS n, min(g.time) AS first_time, max(g.time) AS last_time
                FROM gps_samples g WHERE g.session_id = s.id
            ) st ON true
            ORDER BY s.created_at DESC
            """);
        var list = new List<AdminSessionSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AdminSessionSummary(
                r.GetGuid(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetFieldValue<DateTimeOffset>(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetFieldValue<DateTimeOffset>(4),
                r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10)));
        }
        return list;
    }

    private async Task<SessionRow?> QuerySingleAsync(string sql, object parameter, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("p", parameter);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SessionRow(
            r.GetGuid(0),
            r.GetFieldValue<DateTimeOffset>(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.GetFieldValue<byte[]>(3),
            r.GetFieldValue<DateTimeOffset>(4),
            r.GetFieldValue<DateTimeOffset>(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.GetInt32(8));
    }
}
