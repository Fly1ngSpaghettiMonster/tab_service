using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace TabService.Live;

/// <summary>
/// Remembers which viewer connections watch which session, so closing a session can
/// cut off its viewers immediately instead of waiting for them to disconnect.
/// </summary>
public sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _bySession = new();
    private readonly ConcurrentDictionary<string, Guid> _byConnection = new();

    public void AddViewer(Guid sessionId, HubCallerContext context)
    {
        _bySession.GetOrAdd(sessionId, _ => new()).TryAdd(context.ConnectionId, context);
        _byConnection[context.ConnectionId] = sessionId;
    }

    public void Remove(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var sessionId)) return;
        if (_bySession.TryGetValue(sessionId, out var viewers))
        {
            viewers.TryRemove(connectionId, out _);
            if (viewers.IsEmpty) _bySession.TryRemove(sessionId, out _);
        }
    }

    public int ViewerCount(Guid sessionId) => _bySession.TryGetValue(sessionId, out var v) ? v.Count : 0;

    /// <summary>Detaches and returns all viewer connections of a session.</summary>
    public IReadOnlyList<HubCallerContext> TakeViewers(Guid sessionId)
    {
        if (!_bySession.TryRemove(sessionId, out var viewers)) return [];
        foreach (var id in viewers.Keys) _byConnection.TryRemove(id, out _);
        return viewers.Values.ToList();
    }
}
