using Microsoft.Extensions.Options;
using TabService.Data;
using TabService.Live;

namespace TabService;

/// <summary>
/// Housekeeping every hour: closes sessions the phone abandoned (no uploads for StaleSessionHours)
/// and deletes sessions older than RetentionDays. Old samples are dropped by the Timescale retention policy.
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory scopes,
    IOptions<TabOptions> options,
    ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Retention run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionStore>();
        var service = scope.ServiceProvider.GetRequiredService<SessionService>();
        var opts = options.Value;

        foreach (var id in await sessions.CloseStaleAsync(TimeSpan.FromHours(opts.StaleSessionHours), ct))
        {
            log.LogInformation("Session {Session} expired after {Hours} h without uploads", id, opts.StaleSessionHours);
            await service.DisconnectViewersAsync(id, CloseReasons.Expired);
        }

        var deleted = await sessions.DeleteOlderThanAsync(TimeSpan.FromDays(opts.RetentionDays), ct);
        if (deleted > 0) log.LogInformation("Deleted {Count} sessions older than {Days} days", deleted, opts.RetentionDays);
    }
}
