# Machine Monitoring PoC

Proof of concept for the machine monitoring design: a .NET agent on each
Windows machine samples CPU, memory and drives into a local SQLite outbox and
pushes batches to one ASP.NET Core server, which persists them in
PostgreSQL + TimescaleDB and serves a small portal.

This README grows with the code. The final version covers prerequisites, how
to build and run, what is implemented, simplifications, what was left out, and
what would change in production.

## Layout

| Path | Role |
|------|------|
| `src/Shared` | DTOs shared by agent and server (the wire contract) |
| `src/Agent` | Worker: samplers, SQLite outbox, uploader, config cache |
| `src/Server` | ASP.NET Core: `POST /v1/samples`, `GET /api/*`, static portal |
| `docker-compose.yml` | TimescaleDB (PostgreSQL 17) for local use |
| `scripts/` | Helper scripts to run the server, the agent and the outage demo |

## Prerequisites

- .NET SDK 10.0
- Docker Desktop (for the database only)
- Windows (the agent reads Windows performance counters)
- The ASP.NET Core dev certificate, trusted: `dotnet dev-certs https --trust`.
  If you would rather not trust it, the agent's Development profile sets
  `TrustAnyServerCertificate`, which skips validation (development only).

## Run (target reviewer path)

```powershell
docker compose up -d
.\scripts\run-server.ps1   # in one terminal
.\scripts\run-agent.ps1    # in another
# then open https://localhost:7181
```

The agent's Development profile uses short intervals (2 s CPU tick, 10 s
cpu_mem, 30 s drives) so a demo shows data within seconds. `appsettings.json`
holds the design values (60 s and 5 min).

## Progress

Each step is one commit, in this order.

- [x] 1. Skeleton: solution, projects, Compose file, README plan
- [x] 2. Shared contracts: sample batch, heartbeat, ack, config
- [x] 3. Server: `POST /v1/samples` validates and acks (in memory)
- [x] 4. Agent: CPU (averaged over the interval), memory and drive samplers
- [x] 5. Agent: SQLite outbox with increasing sequence numbers, persisted install id
- [x] 6. Agent: uploader with batching and delete-after-ack
- [x] 7. Server: PostgreSQL + TimescaleDB schema applied on startup
- [x] 8. Server: ingest persists history and upserts `machine_latest` in one transaction, idempotent
- [x] 9. Agent: backoff with jitter, batch cap, `429 Retry-After` handling
- [x] 10. Server: `429` back-pressure under an in-flight limit
- [x] 11. Server: query API for fleet and per-machine metrics
- [ ] 12. Portal: fleet table and one chart
- [ ] 13. Versioned config in the ack reply, heartbeat fields
- [ ] 14. Hourly continuous aggregate, compression and retention policies
- [ ] 15. Stretch: Windows Service install flag, project root walk
- [ ] 16. Outage demo script
- [ ] 17. Final README

## Implemented so far

- **Wire contract** (`src/Shared`): `SampleBatch` carries `machine_id`,
  `install_id`, agent version, `samples[]` and a `heartbeat`; the reply is
  `ack_through` plus config when the agent's config version is behind. Sample
  payloads stay raw JSON so the outbox and ingest are kind-agnostic.
- **Ingest** (`src/Server/Ingest`): identity from the `X-Api-Key` header
  (registered under `Machines` in `appsettings.json`), never from the body.
  Batches are shape-checked (seq strictly increasing, known kinds, at most
  500 rows) and acked. The store is in memory until step 8.
- **Samplers** (`src/Agent/Sampling`): CPU from the
  `Processor Information\% Processor Time\_Total` counter, read every tick and
  averaged over the interval; memory from `GlobalMemoryStatusEx`; fixed ready
  drives from `DriveInfo`. One timer drives all three.
- **Outbox** (`src/Agent/Outbox.cs`): every sample is written to
  `data/outbox.db` (SQLite, WAL, `synchronous=FULL`) before anything is sent.
  `seq` is `INTEGER PRIMARY KEY AUTOINCREMENT`, so it is never reused even
  after acknowledged rows are deleted. The install id is stored in the same
  file, so sequence and install id reset together if the file is wiped.
- **Uploader** (`src/Agent/Uploader.cs`): on its own timer it posts the oldest
  rows (at most 500) with a heartbeat to `POST /v1/samples` and deletes rows
  only after the reply's `ack_through` covers them. A full batch is followed
  immediately by the next one so a backlog drains quickly. A heartbeat-only
  post still goes out when the outbox is empty.
- **Schema** (`src/Server/Db/schema.sql`): `machines`, `machine_latest`, and
  the hypertables `cpu_mem_samples` and `drive_samples`, exactly as in the
  design. Embedded in the server and applied on every start (idempotent, with
  a retry loop so `docker compose up -d` followed by `dotnet run` works). The
  idempotency key `(machine_id, install_id, seq)` is a unique index; TimescaleDB
  requires the partition column in it, so `time` is included, which changes
  nothing because a resend carries the same `sampled_at`.
- **Persistence** (`src/Server/Ingest/PostgresSampleStore.cs`): one
  transaction per batch upserts the `machines` row, inserts history rows with
  `ON CONFLICT DO NOTHING` (one `NpgsqlBatch`, one round trip), and upserts
  `machine_latest` from the heartbeat plus the newest sample of each kind.
  A heartbeat-only post refreshes `last_seen` without blanking the sample
  columns. A payload that does not parse is logged and acknowledged rather
  than rejected, so a poison row cannot wedge an agent.
- **Resilience, agent side** (`src/Agent/Backoff.cs`, `Uploader.cs`): every
  failure (connection refused, timeout, 5xx, bad certificate) takes the same
  path: exponential backoff with jitter (5 s doubling to 5 min in the design
  profile, 2 s to 30 s in Development) while sampling continues and the
  outbox grows. A 429 honours the server's `Retry-After` plus jitter. On
  recovery the backlog drains in 500-row batches. The outbox is capped at 7
  days; past that the oldest rows are dropped with an error log, which is
  the one place the "no data loss" promise ends by design.
- **Resilience, server side** (`src/Server/Ingest/IngestThrottle.cs`): a
  semaphore bounds how many batches are inside the database transaction at
  once (`Ingest:MaxInFlight`, default 16). Beyond that the server answers
  `429` with `Retry-After` immediately instead of queueing. Shedding load is
  free because the rows are still safe in the agent's outbox. Try it with
  `$env:Ingest__MaxInFlight=1` and a few parallel posts.
- **Query API** (`src/Server/Api`): `GET /api/machines` is one query over
  `machines` joined to `machine_latest` and never scans history. Status is
  computed at read time with hysteresis: unresponsive after three heartbeat
  intervals of silence, online again on the next heartbeat.
  `GET /api/machines/{id}/metrics?from&to` picks the resolution from the
  range (raw up to 2 h, 5-minute buckets up to 2 days, hourly beyond) so a
  chart never reads more than a few thousand points. Buckets are computed
  with `time_bucket` on the fly until the continuous aggregate lands.
