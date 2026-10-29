# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

A proof of concept for a machine monitoring design (see the design document
the owner submitted; the README summarises it). A .NET agent on each Windows
machine samples CPU, memory and drives into a local SQLite outbox and pushes
batches to one ASP.NET Core server, which persists them in PostgreSQL +
TimescaleDB and serves a small portal.

It is a hiring assessment. The commit history is part of the deliverable and
the owner must be able to explain every file in an interview. Prefer plain,
readable code over frameworks and cleverness.

## Hard constraints

- .NET 10, `net10.0` in every project. SDK 10.0.402.
- Npgsql with hand-written SQL. No Entity Framework. The schema in the design
  document is the schema in the code (`src/Server/Db/schema.sql`).
- `Microsoft.Data.Sqlite` for the agent outbox.
- Timestamps are UTC everywhere: `sampled_at` from the agent, `received_at`
  from the server.
- Push only. The agent initiates every connection; nothing listens on the
  workstation.
- The agent never runs in Docker (it reads real Windows counters). The
  database and the server do. Reviewer path: `deploy\export-dev-cert.ps1`
  once, `docker compose up -d`, `dotnet run` for the agent, open the browser.
  The server can still run on the host with `dotnet run` for stepping through
  it, after `docker compose stop server`.
- Keep files small and named after the design concept they implement
  (`Outbox.cs`, `Uploader.cs`, `CpuSampler.cs`, `IngestEndpoint.cs`).

## Layout

- `src/Shared` DTOs shared by agent and server (the wire contract). Snake_case
  JSON via `JsonDefaults`.
- `src/Agent` worker host: `Sampling/*` samplers, `SamplingLoop`, outbox,
  uploader, config cache.
- `src/Server` ASP.NET Core: `Auth/*` API-key identity, `Ingest/*`
  `POST /v1/samples`, `Db/*`, `Api/*`, `wwwroot/index.html`.
- `docker-compose.yml` TimescaleDB on PostgreSQL 17 (user `mm`, password
  `mm`, database `monitoring`, port 5432) and the server (`src/Server/Dockerfile`,
  `https://localhost:7181`, Production profile).
- `deploy/` certificate export script plus git-ignored `certs/` and `config/`
  that the server container mounts. `appsettings.Local.json` (machine-specific
  overrides such as `AgentConfig:ProjectRoot`) and `machines.json` (registry
  written by FleetSim `prepare`) live in `deploy/config/` for the container or
  in `src/Server/` for a host-run server; `ConfigDir` selects the folder. Never
  put local paths in a committed file.
- `scripts/` run-server, run-agent, outage demo.
- `tests/Agent.Tests` xunit: pure sampler logic plus Windows smoke tests.

## Commands

```powershell
dotnet build MachineMonitoring.slnx
dotnet test MachineMonitoring.slnx
.\deploy\export-dev-cert.ps1                                             # once
docker compose up -d                                                     # db + server, https://localhost:7181
dotnet run --project src/Agent/Agent.csproj                              # Development profile: short intervals
docker compose stop server; dotnet run --project src/Server/Server.csproj --launch-profile https   # host-run server instead
```

The Development profile of the agent uses short intervals (2 s tick, 10 s
cpu_mem, 30 s drives) so demos show data quickly. `appsettings.json` holds the
design values (60 s / 300 s).

## Working style

- One build step per commit, in the order listed in the README. Never squash,
  never "fix stuff" messages. Subject line names the step; body says what and
  why in a few sentences.
- No Co-Authored-By or Claude trailers in commits: the README already states
  that the work is AI-assisted.
- Commit and push to `origin main` after each completed step.
- Before writing code for a step, explain in a few sentences what it does and
  why it is shaped that way. Build, and run when runnable, before committing.
- When a design detail is ambiguous, pick the option closest to the design
  document and record it in the README's simplifications section.
- Update the README progress list in the same commit as the step it marks.
