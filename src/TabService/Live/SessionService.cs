using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TabService.Data;
using TabService.Hubs;
using TabService.Security;

namespace TabService.Live;

public sealed class SessionService(
    SessionStore sessions,
    ConnectionTracker tracker,
    IHubContext<TripHub> hub,
    IOptions<TabOptions> options,
    ILogger<SessionService> log)
{
    public async Task<CreateSessionResponse> CreateAsync(CreateSessionRequest req, CancellationToken ct)
    {
        var ownerToken = Tokens.New();
        var viewerToken = Tokens.New();
        var id = await sessions.CreateAsync(
            Trim(req.ClientTripId, 64), req.StartedAtUtc ?? DateTimeOffset.UtcNow, Trim(req.LocationSource, 128),
            Tokens.Hash(ownerToken), Tokens.Hash(viewerToken), ct);
        log.LogInformation("Session {Session} created", id);

        // The viewer token goes in the fragment: browsers never send it to any server, so it stays out of logs.
        var viewerUrl = $"{options.Value.PublicViewerUrl.TrimEnd('/')}#{viewerToken}";
        return new CreateSessionResponse(id, ownerToken, viewerUrl);
    }

    /// <summary>Closes a session, tells its viewers, and disconnects them.</summary>
    public async Task<bool> CloseAsync(Guid id, string reason, CancellationToken ct)
    {
        var closed = await sessions.CloseAsync(id, reason, ct);
        if (closed) log.LogInformation("Session {Session} closed ({Reason})", id, reason);
        await DisconnectViewersAsync(id, reason);
        return closed;
    }

    public async Task DisconnectViewersAsync(Guid id, string reason)
    {
        try
        {
            await hub.Clients.Group(TripHub.Group(id)).SendAsync(HubEvents.SessionClosed, reason);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Failed to notify viewers of {Session}", id);
        }
        foreach (var viewer in tracker.TakeViewers(id)) viewer.Abort();
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
