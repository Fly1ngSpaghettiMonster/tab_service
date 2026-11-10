namespace TabService;

/// <summary>
/// One raw GPS fix, exactly as recorded by the phone (mirrors GpsSample in tab_mobile/src/geo/stats.ts).
/// No filtering or smoothing happens on the server, so every client can derive the same results.
/// </summary>
public sealed record SampleDto(
    int Sequence,
    DateTimeOffset TimestampUtc,
    double Latitude,
    double Longitude,
    double? SpeedMps,
    double? AccuracyMeters,
    double? AltitudeMeters,
    double? Heading,
    string? Provider,
    int? SatellitesUsed,
    int? SatellitesInView,
    double? SpeedAccuracyMps,
    double? BearingAccuracyDeg);

public sealed record CreateSessionRequest(string? ClientTripId, DateTimeOffset? StartedAtUtc, string? LocationSource);

public sealed record CreateSessionResponse(Guid SessionId, string OwnerToken, string ViewerUrl);

public sealed record CloseSessionRequest(string? Reason);

/// <summary>What a viewer learns about a session.</summary>
public sealed record SessionInfo(
    Guid SessionId,
    DateTimeOffset StartedAtUtc,
    string? LocationSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    int LastSequence,
    DateTimeOffset? ClosedAt,
    string? CloseReason);

/// <summary>One row of the admin dashboard's session list.</summary>
public sealed record AdminSessionSummary(
    Guid SessionId,
    string? ClientTripId,
    DateTimeOffset StartedAtUtc,
    string? LocationSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    long SampleCount,
    DateTimeOffset? FirstSampleUtc,
    DateTimeOffset? LastSampleUtc);

public sealed record Snapshot(SessionInfo Session, IReadOnlyList<SampleDto> Samples);

public static class CloseReasons
{
    public const string Ended = "ended";
    public const string Revoked = "revoked";
    public const string Expired = "expired";

    public static bool IsClientReason(string? reason) => reason is Ended or Revoked;
}
