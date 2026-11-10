using Npgsql;
using NpgsqlTypes;

namespace TabService.Data;

public sealed class SampleStore(NpgsqlDataSource db)
{
    /// <summary>
    /// Inserts a batch in one statement. Samples already stored (same session + sequence) are skipped,
    /// so re-sending after a reconnect is harmless. Returns the sequences that were actually new.
    /// </summary>
    public async Task<HashSet<int>> InsertAsync(Guid sessionId, IReadOnlyList<SampleDto> samples, CancellationToken ct)
    {
        if (samples.Count == 0) return [];

        await using var cmd = db.CreateCommand(
            """
            INSERT INTO gps_samples (session_id, sequence, time, latitude, longitude, speed_mps, accuracy_m, altitude_m,
                                     heading, provider, satellites_used, satellites_in_view, speed_accuracy_mps, bearing_accuracy_deg)
            SELECT @sid, u.* FROM unnest(@seq, @time, @lat, @lon, @speed, @acc, @alt, @heading, @provider,
                                         @satUsed, @satView, @speedAcc, @bearingAcc)
                AS u(sequence, time, latitude, longitude, speed_mps, accuracy_m, altitude_m, heading, provider,
                     satellites_used, satellites_in_view, speed_accuracy_mps, bearing_accuracy_deg)
            -- A sequence is stored once per session, even if re-sent with a different timestamp.
            WHERE NOT EXISTS (SELECT 1 FROM gps_samples g WHERE g.session_id = @sid AND g.sequence = u.sequence)
            ON CONFLICT DO NOTHING
            RETURNING sequence
            """);
        var p = cmd.Parameters;
        p.AddWithValue("sid", sessionId);
        p.Add(Array("seq", NpgsqlDbType.Integer, samples.Select(s => (object?)s.Sequence)));
        p.Add(Array("time", NpgsqlDbType.TimestampTz, samples.Select(s => (object?)s.TimestampUtc.UtcDateTime)));
        p.Add(Array("lat", NpgsqlDbType.Double, samples.Select(s => (object?)s.Latitude)));
        p.Add(Array("lon", NpgsqlDbType.Double, samples.Select(s => (object?)s.Longitude)));
        p.Add(Array("speed", NpgsqlDbType.Double, samples.Select(s => (object?)s.SpeedMps)));
        p.Add(Array("acc", NpgsqlDbType.Double, samples.Select(s => (object?)s.AccuracyMeters)));
        p.Add(Array("alt", NpgsqlDbType.Double, samples.Select(s => (object?)s.AltitudeMeters)));
        p.Add(Array("heading", NpgsqlDbType.Double, samples.Select(s => (object?)s.Heading)));
        p.Add(Array("provider", NpgsqlDbType.Text, samples.Select(s => (object?)s.Provider)));
        p.Add(Array("satUsed", NpgsqlDbType.Integer, samples.Select(s => (object?)s.SatellitesUsed)));
        p.Add(Array("satView", NpgsqlDbType.Integer, samples.Select(s => (object?)s.SatellitesInView)));
        p.Add(Array("speedAcc", NpgsqlDbType.Double, samples.Select(s => (object?)s.SpeedAccuracyMps)));
        p.Add(Array("bearingAcc", NpgsqlDbType.Double, samples.Select(s => (object?)s.BearingAccuracyDeg)));

        var inserted = new HashSet<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) inserted.Add(reader.GetInt32(0));
        return inserted;
    }

    public async Task<List<SampleDto>> GetAllAsync(Guid sessionId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT sequence, time, latitude, longitude, speed_mps, accuracy_m, altitude_m, heading, provider,
                   satellites_used, satellites_in_view, speed_accuracy_mps, bearing_accuracy_deg
            FROM gps_samples WHERE session_id = @sid ORDER BY sequence
            """);
        cmd.Parameters.AddWithValue("sid", sessionId);
        var list = new List<SampleDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SampleDto(
                r.GetInt32(0),
                r.GetFieldValue<DateTimeOffset>(1),
                r.GetDouble(2),
                r.GetDouble(3),
                NullableDouble(r, 4),
                NullableDouble(r, 5),
                NullableDouble(r, 6),
                NullableDouble(r, 7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetInt32(10),
                NullableDouble(r, 11),
                NullableDouble(r, 12)));
        }
        return list;
    }

    private static double? NullableDouble(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    // object?[] with DBNull for missing values: the simplest array shape Npgsql accepts for nullable columns.
    private static NpgsqlParameter Array(string name, NpgsqlDbType elementType, IEnumerable<object?> values) =>
        new(name, NpgsqlDbType.Array | elementType) { Value = values.Select(v => v ?? DBNull.Value).ToArray() };
}
