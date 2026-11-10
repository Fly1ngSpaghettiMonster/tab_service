# TAB service

The backend for **live trip sharing** from the TAB Android app (`tab_mobile`).

While the phone shares a trip, it streams the trip's **raw** GPS samples here. The service stores them in TimescaleDB and pushes them to viewers who hold the share link. Nothing is filtered or smoothed on the server, so any UI can derive the same route and statistics as the phone.

Stack: ASP.NET Core (.NET 10) minimal API, SignalR, PostgreSQL + TimescaleDB, Docker Compose.

## Run

```bash
cp .env.example .env      # set POSTGRES_PASSWORD and TAB_API_KEY (e.g. `openssl rand -hex 24`)
docker compose up -d --build
curl http://localhost:8080/health        # {"status":"ok"}
```

The schema and the retention policy are applied automatically at startup (`src/TabService/Data/schema.sql`).

Postgres has no published port. To inspect the data:

```bash
docker compose exec db psql -U tab -d tab
```

| Env var | Default | Meaning |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | (required) | Database password. Letters and digits only. |
| `TAB_API_KEY` | (required, ≥ 16 chars) | Key the phone must send to create a session. Same value as `EXPO_PUBLIC_TAB_API_KEY` in the app. |
| `TAB_ADMIN_KEY` | empty (admin off) | Unlocks the admin dashboard and `GET /api/admin/sessions`. At least 32 characters. Must differ from `TAB_API_KEY`. |
| `TAB_PUBLIC_VIEWER_URL` | `https://tab.aabder.xyz/view` | Base of the share link. The viewer token is appended as `#fragment`. |
| `TAB_ALLOWED_ORIGINS` | empty | Comma-separated CORS origins, only needed if the viewer is served from another origin. |
| `TAB_RETENTION_DAYS` | `60` | Sessions and samples older than this are deleted. |
| `TAB_HTTP_PORT` | `8080` | Host port of the API. |

## Nginx Proxy Manager

Create a **Proxy Host**:

- **Domain:** `tab.aabder.xyz`
- **Scheme / Forward host / port:** `http` / the server's LAN IP (or the container name if NPM shares a Docker network with it) / `8080`
- **Websockets Support: ON.** This is required: SignalR uses WebSockets, and the toggle adds the `Upgrade`/`Connection` headers.
- **SSL tab:** your certificate, plus *Force SSL*, *HTTP/2* and *HSTS*.

You don't need NPM's **Streams**. Streams forward raw TCP/UDP ports, and all of this service's traffic is HTTPS on 443.

Only NPM should be able to reach port 8080. The service trusts the `X-Forwarded-*` headers it gets, so don't forward 8080 on your router.

## Data flow

```
phone ── POST /api/sessions (X-Api-Key) ──────────────► session + owner token + share link
phone ── SignalR /hubs/trip: Resume, PublishSamples ──► TimescaleDB ──► Samples event ──► viewers
viewer ─ SignalR /hubs/trip: Join(viewerToken) ───────► snapshot, then live Samples events
phone ── POST /api/sessions/{id}/close (Bearer owner) ► link dead, viewers disconnected
```

## API

### `POST /api/sessions`

Header: `X-Api-Key: <TAB_API_KEY>`.

Request:

```json
{ "clientTripId": "42", "startedAtUtc": "2026-11-06T10:00:00Z", "locationSource": "fused high-accuracy 1000ms" }
```

Response:

```json
{ "sessionId": "…", "ownerToken": "…", "viewerUrl": "https://tab.aabder.xyz/view#<viewerToken>" }
```

Errors: `401` for a wrong key, `429` when rate limited (10/min per IP).

### `POST /api/sessions/{id}/close`

Header: `Authorization: Bearer <ownerToken>`.

Request: `{ "reason": "ended" | "revoked" }`.

- Returns `204`.
- The link stops working immediately. Viewers receive `SessionClosed(reason)` and are disconnected.

### `GET /api/view`

Header: `X-Viewer-Token: <token from the link fragment>`.

- Returns the snapshot `{ session, samples[] }` with all raw samples, ordered by `sequence`.
- Returns `404` if the token is unknown or the session is closed.

### `GET /api/admin/sessions`

Header: `X-Admin-Key: <TAB_ADMIN_KEY>`.

- Returns every stored session, open or closed, newest first. Each entry has `sessionId`, `clientTripId`, `startedAtUtc`, `closedAt`, `closeReason`, `sampleCount`, `firstSampleUtc` and `lastSampleUtc`.
- Returns `401` for a wrong key, or when the admin key is not configured.

The web dashboard opens at `https://tab.aabder.xyz/view#<TAB_ADMIN_KEY>`. The domain root (`/`) redirects to `/view/`.

### `GET /health`

Checks the database connection.

### SignalR hub `/hubs/trip`

Use JSON protocol. Clients may use WebSockets only (`skipNegotiation: true`).

| Caller | Method | Returns |
| --- | --- | --- |
| phone | `Resume(sessionId, ownerToken)` | the highest stored sequence (-1 if none). Call it after every (re)connect. |
| phone | `PublishSamples(sessionId, ownerToken, SampleDto[] ≤ 500)` | the highest stored sequence. Duplicate sequences are ignored. |
| viewer | `Join(viewerToken)` | `Snapshot { session, samples[] }` |
| admin | `AdminJoin(adminKey, sessionId)` | `Snapshot` of any session. While it is open, live events follow as for a viewer. |

Events from server to viewer:

- `Samples(SampleDto[])`: newly stored samples only.
- `SessionClosed(reason)`: `ended`, `revoked` or `expired`.

A sample can show up both in the snapshot and in a `Samples` event, so viewers should de-duplicate by `sequence`.

Hub errors are `HubException` messages: `unauthorized`, `session-closed`, `not-found`, `batch-too-large:N`.

`SampleDto` is the phone's raw fix (camelCase JSON):

```json
{ "sequence": 0, "timestampUtc": "2026-11-06T10:00:01Z", "latitude": 47.47, "longitude": 19.05,
  "speedMps": 12.3, "accuracyMeters": 4.2, "altitudeMeters": 110.0, "heading": 87.0, "provider": "fused",
  "satellitesUsed": 14, "satellitesInView": 27, "speedAccuracyMps": 0.4, "bearingAccuracyDeg": 3.1 }
```

The phone sends only valid coordinates and timestamps. Samples with out-of-range coordinates or implausible timestamps are dropped, but they are still acknowledged.

## Database

- **`share_sessions`:** one row per share. It holds token hashes (never the tokens), `closed_at` / `close_reason`, `last_seen_at` and `last_sequence`.
- **`gps_samples`:** a Timescale hypertable on `time`, one row per raw fix, keyed by `(session_id, sequence)`.

Retention works in two ways:

- A Timescale policy drops sample chunks older than `TAB_RETENTION_DAYS`.
- An hourly job deletes old sessions together with their samples. The same job closes sessions that have had no uploads for 12 h (`expired`), for example when the phone died mid-trip.

## Security notes

- **Tokens:** the owner and viewer tokens are 256-bit random values. Only their SHA-256 hashes are stored.
- **The viewer token is in the URL fragment** (`#…`). Browsers never send the fragment in requests, so it never appears in NPM or server logs. Clients pass it in a header or a hub argument. The phone's owner token travels in hub arguments for the same reason.
- **API key:** `TAB_API_KEY` protects session creation. It is built into the app, so anyone with the APK can extract it. That is acceptable for a single owner. If it leaks, rotate it in `.env` and in the app's `.env.local`.
- **Admin key:** `TAB_ADMIN_KEY` reveals every stored trip, so keep it out of the app and long (e.g. `openssl rand -hex 32`). The dashboard reads it from the link fragment, moves it into the tab's session storage and removes it from the address bar. Admin requests are limited to 120/min per IP; the key length is what makes guessing hopeless.
- **Revocation:** closing a share stops serving both new and old data and disconnects viewers. A viewer can still keep whatever they already saw.
- **Location privacy:** a shared trip reveals where it starts and ends, which may be your home. Consider starting to share after leaving, or a future privacy-zone trim.
- **Rate limits:** per IP, 10/min for creating sessions, 60/min for snapshots and 120/min for hub connections.
- **Network:** the database is only reachable inside the Compose network.
