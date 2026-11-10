using Microsoft.Extensions.Options;
using Npgsql;

namespace TabService.Data;

public static class Database
{
    /// <summary>Applies schema.sql and the retention policy, retrying while Postgres is still starting.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        var options = services.GetRequiredService<IOptions<TabOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Database));

        await using var stream = typeof(Database).Assembly.GetManifestResourceStream("TabService.Data.schema.sql")
            ?? throw new InvalidOperationException("schema.sql resource missing");
        var schema = await new StreamReader(stream).ReadToEndAsync(ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(ct);
                await using (var cmd = new NpgsqlCommand(schema, conn))
                    await cmd.ExecuteNonQueryAsync(ct);

                // Timescale drops whole chunks older than the retention period.
                await using (var cmd = new NpgsqlCommand(
                    """
                    SELECT remove_retention_policy('gps_samples', if_exists => TRUE);
                    SELECT add_retention_policy('gps_samples', make_interval(days => @days));
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("days", options.RetentionDays);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                logger.LogInformation("Database schema ready (retention {Days} days)", options.RetentionDays);
                return;
            }
            catch (Exception e) when (attempt < 30 && e is NpgsqlException or System.Net.Sockets.SocketException)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, e.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
