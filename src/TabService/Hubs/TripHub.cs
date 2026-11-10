using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TabService.Data;
using TabService.Live;
using TabService.Security;

namespace TabService.Hubs;

/// <summary>Names of server-to-client events.</summary>
public static class HubEvents
{
    /// <summary>Payload: SampleDto[] of newly stored samples, ordered by sequence.</summary>
    public const string Samples = "Samples";

    /// <summary>Payload: close reason ("ended", "revoked", "expired"). The server disconnects right after.</summary>
    public const string SessionClosed = "SessionClosed";
}

/// <summary>
/// One hub for both sides of a session.
/// Phone (publisher): Resume, PublishSamples, authorized with the per-session owner token.
/// Browser (viewer): Join, authorized with the viewer token from the share link.
/// Admin dashboard: AdminJoin, authorized with the admin key (any session, open or closed).
/// Tokens are passed as method arguments, never in the URL, so they do not show up in proxy logs.
/// </summary>
public sealed class TripHub(
    SessionStore sessions,
    SampleStore samples,
    ConnectionTracker tracker,
    IOptions<TabOptions> options,
    ILogger<TripHub> log) : Hub
{
    // Error messages the phone reacts to. HubException messages are the only ones sent to clients.
    public const string ErrorUnauthorized = "unauthorized";
    public const string ErrorClosed = "session-closed";
    public const string ErrorNotFound = "not-found";

    public static string Group(Guid sessionId) => $"session:{sessionId}";

    /// <summary>Called by the phone after (re)connecting. Returns the highest sequence the server has.</summary>
    public async Task<int> Resume(Guid sessionId, string ownerToken)
    {
        var session = await AuthorizeOwnerAsync(sessionId, ownerToken);
        return session.LastSequence;
    }

    /// <summary>Stores a batch of raw samples and forwards new ones to viewers. Returns the highest sequence stored.</summary>
    public async Task<int> PublishSamples(Guid sessionId, string ownerToken, SampleDto[] batch)
    {
        var opts = options.Value;
        if (batch.Length > opts.MaxBatchSize) throw new HubException($"batch-too-large:{opts.MaxBatchSize}");

        var session = await AuthorizeOwnerAsync(sessionId, ownerToken);
        if (batch.Length == 0) return session.LastSequence;

        var now = DateTimeOffset.UtcNow;
        var oldest = now.AddDays(-opts.RetentionDays);
        var newest = now.AddHours(1);
        var valid = batch
            .Where(s => s.Sequence >= 0
                && s.Latitude is >= -90 and <= 90 && s.Longitude is >= -180 and <= 180
                && s.TimestampUtc >= oldest && s.TimestampUtc <= newest)
            .DistinctBy(s => s.Sequence)
            .OrderBy(s => s.Sequence)
            .ToList();
        if (valid.Count < batch.Length)
            log.LogWarning("Session {Session}: dropped {Count} invalid samples", sessionId, batch.Length - valid.Count);

        var ct = Context.ConnectionAborted;
        var inserted = await samples.InsertAsync(sessionId, valid, ct);
        // Acknowledge the whole batch, invalid samples included, so the phone never retries them forever.
        var last = await sessions.TouchAsync(sessionId, batch.Max(s => s.Sequence), ct);

        if (inserted.Count > 0)
            await Clients.Group(Group(sessionId)).SendAsync(HubEvents.Samples, valid.Where(s => inserted.Contains(s.Sequence)).ToList(), ct);
        return last;
    }

    /// <summary>
    /// Called by a viewer with the token from the share link. Returns everything recorded so far;
    /// afterwards the viewer receives Samples events. A few samples may arrive both in the snapshot
    /// and as an event, so viewers should de-duplicate by sequence.
    /// </summary>
    public async Task<Snapshot> Join(string viewerToken)
    {
        var ct = Context.ConnectionAborted;
        var session = await sessions.GetByViewerTokenHashAsync(Tokens.Hash(viewerToken ?? ""), ct);
        if (session is null || !session.IsOpen) throw new HubException(ErrorNotFound);

        // Join the group before reading the snapshot, so no sample falls between the two.
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(session.Id), ct);
        tracker.AddViewer(session.Id, Context);

        // The session may have been closed meanwhile; then this viewer would have been missed.
        var current = await sessions.GetAsync(session.Id, ct);
        if (current is null || !current.IsOpen)
        {
            tracker.Remove(Context.ConnectionId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(session.Id), ct);
            throw new HubException(ErrorNotFound);
        }

        return new Snapshot(current.ToInfo(), await samples.GetAllAsync(session.Id, ct));
    }

    /// <summary>
    /// Admin dashboard: any session, open or closed, by id. Returns everything recorded; for an open
    /// session the caller also receives live Samples / SessionClosed events, like a viewer.
    /// Admins are not tracked as viewers, so closing a share does not disconnect them.
    /// </summary>
    public async Task<Snapshot> AdminJoin(string adminKey, Guid sessionId)
    {
        if (!IsAdmin(adminKey, options.Value)) throw new HubException(ErrorUnauthorized);
        var ct = Context.ConnectionAborted;
        var session = await sessions.GetAsync(sessionId, ct) ?? throw new HubException(ErrorNotFound);
        // Join the group before reading the snapshot, so no sample falls between the two.
        if (session.IsOpen) await Groups.AddToGroupAsync(Context.ConnectionId, Group(session.Id), ct);
        var current = await sessions.GetAsync(session.Id, ct) ?? throw new HubException(ErrorNotFound);
        return new Snapshot(current.ToInfo(), await samples.GetAllAsync(session.Id, ct));
    }

    public static bool IsAdmin(string? key, TabOptions opts) =>
        opts.AdminKey.Length > 0 && Tokens.EqualsConstantTime(key ?? "", opts.AdminKey);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private async Task<SessionRow> AuthorizeOwnerAsync(Guid sessionId, string ownerToken)
    {
        var session = await sessions.GetAsync(sessionId, Context.ConnectionAborted);
        if (session is null || !Tokens.Matches(ownerToken, session.OwnerTokenHash))
            throw new HubException(ErrorUnauthorized);
        if (!session.IsOpen) throw new HubException(ErrorClosed);
        return session;
    }
}
