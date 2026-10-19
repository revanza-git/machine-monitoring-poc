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
- [ ] 7. Server: PostgreSQL + TimescaleDB schema applied on startup
- [ ] 8. Server: ingest persists history and upserts `machine_latest` in one transaction, idempotent
- [ ] 9. Agent: backoff with jitter, batch cap, `429 Retry-After` handling
- [ ] 10. Server: `429` back-pressure under an in-flight limit
- [ ] 11. Server: query API for fleet and per-machine metrics
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
