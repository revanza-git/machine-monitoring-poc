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
