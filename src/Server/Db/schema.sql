-- Machine monitoring schema. Applied on every server start; everything is
-- idempotent (IF NOT EXISTS) so it doubles as the migration for the PoC.
-- This is the table list from the design document, minus the admin-side
-- tables (groups, users, project_roots) that the PoC leaves out.

CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Relational: what exists. machine_id is a surrogate key so the registered
-- name and the credential can change without losing history.
CREATE TABLE IF NOT EXISTS machines (
    machine_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text        NOT NULL UNIQUE,   -- registered name, resolved from the credential
    hostname    text,                          -- machine_id as reported in the body (logging only)
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- Current state for the fleet overview: one upserted row per machine.
-- Never grows, so the overview never scans history.
CREATE TABLE IF NOT EXISTS machine_latest (
    machine_id     bigint PRIMARY KEY REFERENCES machines (machine_id),
    last_seen      timestamptz NOT NULL,       -- received_at of the last batch (heartbeat)
    sampled_at     timestamptz,                -- of the latest cpu_mem sample
    cpu_pct        double precision,
    mem_used_mb    bigint,
    mem_total_mb   bigint,
    drives         jsonb,                      -- latest drive snapshot, as sent
    drives_at      timestamptz,
    active_user    text,
    agent_version  text,
    install_id     uuid,
    outbox_depth   bigint,
    restart_count  int,
    last_error     text,
    config_version int
);
-- Added with the heartbeat fields: set by the final heartbeat of a clean
-- shutdown, cleared by the next normal one. Idempotent ALTER keeps the
-- "apply on every start" story working for databases created earlier.
ALTER TABLE machine_latest ADD COLUMN IF NOT EXISTS stopped_at timestamptz;
-- Added with the user sampler: the last user to sign in, from the registry,
-- so a machine with nobody at the keyboard still says whose it is.
ALTER TABLE machine_latest ADD COLUMN IF NOT EXISTS last_user text;
-- Added with commit charge: the design's third memory figure.
ALTER TABLE machine_latest ADD COLUMN IF NOT EXISTS commit_mb bigint;
ALTER TABLE machine_latest ADD COLUMN IF NOT EXISTS commit_limit_mb bigint;

-- History. Hypertables partitioned on time (sampled_at from the agent).
-- received_at is the server clock, kept beside it for clock-drift checks.
-- The unique index is the idempotency key (machine, install, seq); TimescaleDB
-- requires the partition column in every unique index, and a resend carries
-- the same sampled_at, so adding time changes nothing in practice.
CREATE TABLE IF NOT EXISTS cpu_mem_samples (
    time         timestamptz NOT NULL,
    machine_id   bigint      NOT NULL REFERENCES machines (machine_id),
    install_id   uuid        NOT NULL,
    seq          bigint      NOT NULL,
    received_at  timestamptz NOT NULL,
    cpu_pct      double precision NOT NULL,
    mem_used_mb  bigint      NOT NULL,
    mem_total_mb bigint      NOT NULL,
    UNIQUE (machine_id, install_id, seq, time)
);
SELECT create_hypertable('cpu_mem_samples', 'time', if_not_exists => TRUE);
CREATE INDEX IF NOT EXISTS cpu_mem_samples_machine_time ON cpu_mem_samples (machine_id, time DESC);
-- Commit charge came later: nullable, no default, which TimescaleDB allows
-- even on chunks that are already compressed. Older rows simply have NULL.
ALTER TABLE cpu_mem_samples ADD COLUMN IF NOT EXISTS commit_mb bigint;
ALTER TABLE cpu_mem_samples ADD COLUMN IF NOT EXISTS commit_limit_mb bigint;

-- One row per drive per sample.
CREATE TABLE IF NOT EXISTS drive_samples (
    time         timestamptz NOT NULL,
    machine_id   bigint      NOT NULL REFERENCES machines (machine_id),
    install_id   uuid        NOT NULL,
    seq          bigint      NOT NULL,
    received_at  timestamptz NOT NULL,
    drive        text        NOT NULL,
    total_bytes  bigint      NOT NULL,
    free_bytes   bigint      NOT NULL,
    UNIQUE (machine_id, install_id, seq, drive, time)
);
SELECT create_hypertable('drive_samples', 'time', if_not_exists => TRUE);
CREATE INDEX IF NOT EXISTS drive_samples_machine_time ON drive_samples (machine_id, time DESC);

-- One row per project folder per walk. as_of is when the walk finished;
-- the portal shows it beside every figure. Kept at full resolution for the
-- whole retention period: the volume is small and there is nothing to roll up.
CREATE TABLE IF NOT EXISTS project_sizes (
    time         timestamptz NOT NULL,
    machine_id   bigint      NOT NULL REFERENCES machines (machine_id),
    install_id   uuid        NOT NULL,
    seq          bigint      NOT NULL,
    received_at  timestamptz NOT NULL,
    root         text        NOT NULL,
    project_name text        NOT NULL,
    bytes        bigint      NOT NULL,
    files        int         NOT NULL,
    as_of        timestamptz NOT NULL,
    UNIQUE (machine_id, install_id, seq, project_name, time)
);
SELECT create_hypertable('project_sizes', 'time', if_not_exists => TRUE);
CREATE INDEX IF NOT EXISTS project_sizes_latest ON project_sizes (machine_id, root, project_name, time DESC);

-- ---------------------------------------------------------------------------
-- Rollups, compression and retention: policies, not jobs someone maintains.
-- ---------------------------------------------------------------------------

-- Hourly avg/min/max per machine, kept up to date by TimescaleDB as data
-- arrives. materialized_only = false means a query also sees the not-yet-
-- materialised tail, so the current hour is never missing from a chart.
-- The design also has a 5-minute rollup; the PoC shows the pattern once.
CREATE MATERIALIZED VIEW IF NOT EXISTS cpu_mem_hourly
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT time_bucket(INTERVAL '1 hour', time) AS bucket,
       machine_id,
       avg(cpu_pct)              AS cpu_avg,
       min(cpu_pct)              AS cpu_min,
       max(cpu_pct)              AS cpu_max,
       avg(mem_used_mb)::bigint  AS mem_used_avg,
       max(mem_total_mb)         AS mem_total_max
FROM cpu_mem_samples
GROUP BY bucket, machine_id
WITH NO DATA;

SELECT add_continuous_aggregate_policy('cpu_mem_hourly',
    start_offset      => INTERVAL '3 hours',
    end_offset        => INTERVAL '1 minute',
    schedule_interval => INTERVAL '10 minutes',
    if_not_exists     => TRUE);

-- Raw chunks older than a week are compressed (roughly 10 to 20x), segmented
-- by machine so a per-machine time-range query decompresses only its own data.
ALTER TABLE cpu_mem_samples SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'machine_id',
    timescaledb.compress_orderby   = 'time DESC');
SELECT add_compression_policy('cpu_mem_samples', INTERVAL '7 days', if_not_exists => TRUE);

ALTER TABLE drive_samples SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'machine_id',
    timescaledb.compress_orderby   = 'time DESC');
SELECT add_compression_policy('drive_samples', INTERVAL '7 days', if_not_exists => TRUE);

-- Thirteen months, not twelve, so last March is still there in March.
SELECT add_retention_policy('cpu_mem_samples', INTERVAL '13 months', if_not_exists => TRUE);
SELECT add_retention_policy('drive_samples',   INTERVAL '13 months', if_not_exists => TRUE);
SELECT add_retention_policy('cpu_mem_hourly',  INTERVAL '13 months', if_not_exists => TRUE);
SELECT add_retention_policy('project_sizes',   INTERVAL '13 months', if_not_exists => TRUE);
