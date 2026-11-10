-- Idempotent schema, applied on every startup.
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS share_sessions (
    id                uuid PRIMARY KEY,
    client_trip_id    text,
    started_at_utc    timestamptz NOT NULL,
    location_source   text,
    owner_token_hash  bytea NOT NULL,
    viewer_token_hash bytea NOT NULL UNIQUE,
    created_at        timestamptz NOT NULL DEFAULT now(),
    last_seen_at      timestamptz NOT NULL DEFAULT now(),
    closed_at         timestamptz,
    close_reason      text,
    last_sequence     integer NOT NULL DEFAULT -1
);

CREATE INDEX IF NOT EXISTS share_sessions_open ON share_sessions (last_seen_at) WHERE closed_at IS NULL;
CREATE INDEX IF NOT EXISTS share_sessions_created ON share_sessions (created_at);

-- Raw GPS fixes, exactly as the phone recorded them.
CREATE TABLE IF NOT EXISTS gps_samples (
    session_id           uuid NOT NULL,
    sequence             integer NOT NULL,
    time                 timestamptz NOT NULL,
    received_at          timestamptz NOT NULL DEFAULT now(),
    latitude             double precision NOT NULL,
    longitude            double precision NOT NULL,
    speed_mps            double precision,
    accuracy_m           double precision,
    altitude_m           double precision,
    heading              double precision,
    provider             text,
    satellites_used      integer,
    satellites_in_view   integer,
    speed_accuracy_mps   double precision,
    bearing_accuracy_deg double precision
);

SELECT create_hypertable('gps_samples', by_range('time', INTERVAL '1 day'), if_not_exists => TRUE);

-- Unique per (session, sequence); must contain the partitioning column. Makes uploads idempotent.
CREATE UNIQUE INDEX IF NOT EXISTS gps_samples_session_sequence ON gps_samples (session_id, sequence, time);
