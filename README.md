# Machine Monitoring PoC

Proof of concept for the machine monitoring design: a .NET agent on each
Windows machine samples CPU, memory and drives into a local SQLite outbox and
pushes batches to one ASP.NET Core server, which persists them in
PostgreSQL + TimescaleDB and serves a small portal.

This README is filled in as the PoC grows. The final version will cover
prerequisites, how to build and run, what is implemented, simplifications,
what was left out, and what would change in production.

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

## Run (target reviewer path)

```powershell
docker compose up -d
.\scripts\run-server.ps1   # in one terminal
.\scripts\run-agent.ps1    # in another
# then open https://localhost:7181
```

## Build plan

Each step is one commit, in this order:

1. Skeleton: solution, projects, Compose file, README plan
2. Shared contracts: sample batch, heartbeat, ack, config
3. Server: `POST /v1/samples` validates and acks (in memory)
4. Agent: CPU (averaged over the interval), memory and drive samplers
5. Agent: SQLite outbox with increasing sequence numbers, persisted install id
6. Agent: uploader with batching and delete-after-ack
7. Server: PostgreSQL + TimescaleDB schema applied on startup
8. Server: ingest persists history and upserts `machine_latest` in one transaction, idempotent
9. Agent: backoff with jitter, batch cap, `429 Retry-After` handling
10. Server: `429` back-pressure under an in-flight limit
11. Server: query API for fleet and per-machine metrics
12. Portal: fleet table and one chart
13. Versioned config in the ack reply, heartbeat fields
14. Hourly continuous aggregate, compression and retention policies
15. Stretch: Windows Service install flag, project root walk
16. Outage demo script
17. Final README
