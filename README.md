# Machine Monitoring PoC

Proof of concept for the Machine Monitoring Solution design: a .NET agent on
each Windows machine samples CPU, memory and drives into a local SQLite
outbox and pushes batches over HTTPS to one ASP.NET Core server, which
persists them in PostgreSQL + TimescaleDB and serves a small browser portal.

The five things the brief asked to see, and where they live:

| Asked for | Where |
|-----------|-------|
| A Windows agent collecting CPU, memory and disk | `src/Agent/Sampling`, `src/Agent/SamplingLoop.cs` |
| Sending the data to a central API | `src/Agent/Uploader.cs` → `POST /v1/samples` in `src/Server/Ingest` |
| Persisting the received data | `src/Server/Db/schema.sql`, `src/Server/Ingest/PostgresSampleStore.cs` |
| A simple way to retrieve or view the data | `src/Server/Api/QueryApi.cs`, `src/Server/wwwroot/index.html` |
| Resilience when the server is unavailable | `src/Agent/Outbox.cs`, `src/Agent/Backoff.cs`, `scripts/outage-demo.ps1` |

The commit history is one step per commit, in the order listed under
[Progress](#progress), so the evolution can be followed commit by commit.

## Prerequisites

- Windows 10 or 11 (the agent reads Windows performance counters and Win32
  memory APIs; it is Windows-only by design)
- .NET SDK 10.0 (built with 10.0.402)
- Docker Desktop, for the database only
- The ASP.NET Core development certificate. Either trust it once with
  `dotnet dev-certs https --trust`, or leave the agent's Development profile
  as is: it sets `TrustAnyServerCertificate`, which skips validation and logs
  a warning. Development only.

NuGet packages: `Npgsql`, `Microsoft.Data.Sqlite`,
`System.Diagnostics.PerformanceCounter`,
`Microsoft.Extensions.Hosting.WindowsServices`. The portal loads Chart.js from
a CDN. No ORM, no front-end build.

## Build and run

```powershell
docker compose up -d                 # TimescaleDB on PostgreSQL 17, port 5432, user mm / mm, db monitoring
dotnet build MachineMonitoring.slnx
dotnet test MachineMonitoring.slnx   # tests/Agent.Tests: sampler logic, WTS smoke test, heartbeat wire shape
.\scripts\run-server.ps1             # terminal 1: https://localhost:7181 (schema is applied on start)
.\scripts\run-agent.ps1              # terminal 2: samples this machine and uploads every 10 s
```

Then open <https://localhost:7181>. Within about 30 seconds the fleet table
shows this machine as online with its CPU, memory, drives and active user, and
the chart fills in. Both scripts run with the Development profile, which uses
short intervals (2 s CPU tick, 10 s cpu_mem, 30 s drives, 2 min project walk,
10 s upload) so a demo shows data quickly. `appsettings.json` in each project
holds the design values (60 s, 5 min, daily walk).

The projects table stays empty until the agent is given a folder to size:
create the git-ignored `src/Server/appsettings.Local.json` with
`{ "AgentConfig": { "ProjectRoot": "C:/path/to/your/repos" } }`. The server
reloads it without a restart; if the agent is already running, also bump
`AgentConfig:Version` so it picks the change up on its next heartbeat.

Useful endpoints:

| Endpoint | Purpose |
|----------|---------|
| `POST /v1/samples` | Ingest. Needs `X-Api-Key`. Body and reply shapes are in `src/Shared`. |
| `GET /api/machines` | Fleet overview from `machine_latest`, with status. |
| `GET /api/machines/{id}/metrics?from&to` | CPU and memory points; resolution chosen from the range. |
| `GET /api/projects` | Latest project folder sizes per machine, grouped by project. |
| `GET /health` | Liveness. |

The one registered machine is `WS-DEV-01` with key `dev-key-ws-dev-01`, in
the server's `appsettings.json` under `Machines`. To run a second agent, add
another entry and point the second agent's `Agent:ApiKey` at it.

## Outage demo

```powershell
docker compose up -d
dotnet build MachineMonitoring.slnx
.\scripts\outage-demo.ps1            # -OutageSeconds 60 by default
```

The script starts the server and the agent in their own windows, lets the
first batches be acknowledged, kills the server for a minute, then restarts
it. In the agent window you see uploads fail, retries backing off with
jitter while the outbox depth climbs, and on recovery one batch draining the
backlog. The script then queries the API: the chart points sampled during the
outage are present with their original timestamps, so the portal shows a
delay, never a hole. Finish by pressing Ctrl+C in the agent window: the final
heartbeat flags a clean stop and the fleet table shows the machine as off,
whereas killing the process leaves it unresponsive after three missed
heartbeats.

## What is implemented

**Agent** (`src/Agent`, a .NET worker that is a console app by default)

- `CpuSampler` reads `Processor Information\% Processor Time\_Total` every
  tick and reports the average when the interval closes, so a value is a mean
  over the window, not a snapshot. `MemorySampler` calls `GlobalMemoryStatusEx`.
  `DriveSampler` keeps fixed, ready drives only. `SamplingLoop` is the one
  timer that drives them.
- `Outbox` writes every sample to `data/outbox.db` (SQLite, WAL,
  `synchronous=FULL`) before anything is sent. `seq` is
  `INTEGER PRIMARY KEY AUTOINCREMENT`, so it is never reused even after
  acknowledged rows are deleted. The install id lives in the same file, so
  sequence and install id reset together if the file is wiped.
- `Uploader` posts the oldest rows (at most 500) with a heartbeat and deletes
  rows only after the reply's `ack_through` covers them. A full batch is
  followed immediately by the next one. A heartbeat-only post goes out when
  the outbox is empty.
- `Backoff`: every failure (connection refused, timeout, 5xx, bad
  certificate) waits an exponentially growing, jittered delay while sampling
  continues; a 429 honours `Retry-After`. The outbox is capped at 7 days and
  logs an error if it ever drops rows.
- `ConfigCache` persists the last server config in the outbox's meta table
  and reports its version in every heartbeat. Intervals change without a
  restart. The heartbeat also carries active user, outbox depth, restart
  count, last upload error and a `stopping` flag on clean shutdown.
- `UserSampler` asks `WTSEnumerateSessions` for the user of the active
  console session and the registry's `LastLoggedOnUser` for the last user to
  sign in. The process identity is never used: under the Service Control
  Manager it is LocalSystem, which is not who is at the keyboard.
- `ProjectWalker` sizes each first-level folder under the configured project
  root on a lowest-priority thread, on its own schedule.
- `ServiceInstaller`: `--install` and `--uninstall` register the same binary
  as a Windows Service with auto start and restart-twice-then-stop recovery.

**Server** (`src/Server`, one ASP.NET Core app)

- `POST /v1/samples`: identity from the `X-Api-Key` header, never the body.
  `IngestThrottle` bounds batches in flight and answers 429 with
  `Retry-After` beyond that. `BatchValidator` checks shape. The reply carries
  config only when the agent's version is behind.
- `PostgresSampleStore`: one transaction per batch upserts the machine,
  inserts history with `ON CONFLICT DO NOTHING` (one `NpgsqlBatch`, one round
  trip) and upserts `machine_latest`. A resend stores nothing new and acks the
  same number. A payload that does not parse is logged and acked rather than
  rejected, so a poison row cannot wedge an agent.
- `schema.sql` is the design's tables made literal, embedded in the server
  and applied idempotently on every start: `machines`, `machine_latest`,
  hypertables `cpu_mem_samples`, `drive_samples`, `project_sizes`; an hourly
  continuous aggregate with avg/min/max; compression after 7 days; 13-month
  retention. All as policies, not jobs.
- `QueryApi`: the fleet overview is one query over `machine_latest` and never
  scans history. Status is computed at read time with hysteresis
  (unresponsive after three missed heartbeats, online on the next one, off
  after a stopping heartbeat). Metrics pick raw, 5-minute or hourly
  resolution from the range; the hourly path reads the continuous aggregate.
- `wwwroot/index.html`: fleet table, one CPU/memory chart on a single
  0 to 100 % axis with a real-time x-axis, and a projects table. Refreshes
  every 10 s. Chart.js, no framework, no build step.

**Shared** (`src/Shared`): the wire contract as records, snake_case JSON, so
agent and server can never disagree on the shape.

## Assumptions and simplifications

| Design | PoC | Why |
|--------|-----|-----|
| Mutual TLS with AD CS machine certificates | Per-machine API key in `X-Api-Key` over the dev HTTPS certificate | mTLS needs a domain CA. Identity is still taken from the credential, never the body. |
| Machines registered and approved in the admin area | `Machines` list in the server's `appsettings.json` | No admin area in the PoC. |
| Config per machine or group, edited in the admin area | One fleet-wide `AgentConfig` in appsettings, hot-reloaded | Same protocol (versioned, sent only when behind), simplest possible source. Bump `Version` when values change: agents compare versions, not values. |
| Nightly folder walk kept current by the NTFS USN journal; allocated size on disk | Timed full walk; logical file size | The journal subscription is the one piece that is real engineering effort; the walk shows the data path. |
| 5-minute and hourly rollups | Hourly continuous aggregate; 5-minute buckets computed on the fly | Shows the pattern once; two days of raw rows bucket cheaply. |
| Status transitions tracked by the server | Status computed from `last_seen` at read time | Gives the same hysteresis with no state to maintain. |
| Dedup key `(machine_id, install_id, seq)` | Unique index also includes `time` | TimescaleDB requires the partition column in unique indexes. A resend carries the same `sampled_at`, so nothing changes in practice. |
| Agent state under ProgramData, MSI install | `data/` beside the content root, `--install` via sc.exe | PoC-sized version of the same idea. |
| Agent validates the server certificate | Development profile sets `TrustAnyServerCertificate` | So the reviewer path works without trusting the dev certificate first. Off in `appsettings.json`. |
| Clock drift flagged beyond 5 min | `received_at` stored beside `sampled_at`, not yet compared | The data is there; the check is a one-line query. |

Machine-specific overrides (such as a real `ProjectRoot`) go in the
git-ignored `src/Server/appsettings.Local.json`, so local paths never reach
the repository.

## Left out on purpose

Admin area, groups, users and roles, Entra ID single sign-on, React, MSI
rollout through Group Policy or Intune, agent self-update, the USN journal
and `FileSystemWatcher` fallback, inventory (WMI) and the Machines page, the
5-minute rollup, authentication on the query API and portal, and automated
tests beyond `tests/Agent.Tests` (user sampler and heartbeat wire shape).
The brief asked for the main end-to-end flow, and each of these would have
added surface without changing how the flow works.

## What I would do differently in production

- **Identity**: mTLS with AD CS auto-enrolled, non-exportable machine
  certificates; thumbprint to machine mapping and the pending-approval flow
  in the database, with an admin UI. Unknown certificates never auto-register.
- **Configuration**: per machine and group in the database, edited in the
  admin area, with the same versioned push; a config hash rather than a
  hand-bumped version number.
- **Schema management**: a real migration tool (DbUp or Flyway) instead of
  one idempotent script, with the TimescaleDB policies as migrations.
- **Tests**: unit tests for `Backoff`, `BatchValidator`, `MachineStatus` and
  `Outbox`; integration tests for ingest against a Testcontainers
  TimescaleDB, including the resend and poison-row cases; a contract test on
  the shared DTOs.
- **Observability**: structured logs through OpenTelemetry, metrics for
  batches per second, rejected batches, 429s, queue depth per machine and
  ingest latency; alerts on server disk at 70 % and 85 %.
- **Agent hardening**: state under `%ProgramData%`, a signed MSI, code-signed
  binaries, the USN journal for folder sizes, allocated size on disk, Event
  Log integration, and a watchdog on the sampler thread.
- **Query API**: authentication through Entra ID, pagination, a 5-minute
  continuous aggregate, and the clock-drift flag from `received_at`.
- **Operations**: database on its own volume with backups and restore
  drills, the API behind a reverse proxy with a real certificate, a
  connection-pool-sized ingest limit, and a load test at 1,000 agents before
  the first rollout.

## Layout

| Path | Role |
|------|------|
| `src/Shared` | DTOs shared by agent and server (the wire contract) |
| `src/Agent` | Worker: samplers, SQLite outbox, uploader, config cache, project walker, service installer |
| `src/Server` | ASP.NET Core: `POST /v1/samples`, `GET /api/*`, schema, static portal |
| `tests/Agent.Tests` | xunit: session-picking rules, a WTS smoke test, heartbeat JSON shape |
| `docker-compose.yml` | TimescaleDB (PostgreSQL 17) for local use |
| `scripts/` | `run-server.ps1`, `run-agent.ps1`, `outage-demo.ps1` |
| `CLAUDE.md` | Constraints and working style for AI-assisted sessions |

## Progress

Each step is at least one commit, in this order. Repo housekeeping and README
fixes sit between them as their own commits.

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
- [x] 12. Portal: fleet table and one chart
- [x] 13. Versioned config in the ack reply, heartbeat fields
- [x] 14. Hourly continuous aggregate, compression and retention policies
- [x] 15. Stretch: Windows Service install flag, project root walk
- [x] 16. Outage demo script
- [x] 17. Final README
- [x] 18. Review follow-up (issue #1): active and last logged-on user from WTS and the registry, first tests

## Troubleshooting

- **Agent logs "certificate" errors**: trust the dev certificate
  (`dotnet dev-certs https --trust`) or run with the Development profile.
- **Server logs "Database not ready; retrying"**: PostgreSQL is still
  starting; the server retries for a minute. Check `docker compose ps`.
- **Port 7181 or 5432 in use**: change `applicationUrl` in
  `src/Server/Properties/launchSettings.json` and `Agent:ServerUrl`, or the
  port mapping in `docker-compose.yml` and `ConnectionStrings:Monitoring`.
- **Reset everything**: `docker compose down -v` drops the database;
  deleting `src/Agent/data/` gives the agent a new install id and seq 1.
