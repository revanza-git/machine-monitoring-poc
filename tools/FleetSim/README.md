# FleetSim

A console app that simulates a fleet of agents posting to the real server, so
the design's scale numbers (sized for 200 machines, designed to hold at
1,000) can be measured instead of estimated.

It tests the **server**, not the agent. The virtual agents read no counters
and keep their outbox in memory: their values come from seeded random walks
and describe nothing real. Everything from the queue onwards is the agent's
own behaviour: the wire contract from `src/Shared`, the agent's `Backoff.cs`
(linked into this project, not copied), the uploader's pacing (a full batch
goes again at once, a 429 waits `Retry-After` plus jitter, anything else
waits the backoff), delete only what the ack covers, the config version taken
from the first ack, a stopping heartbeat at the end. The server cannot tell a
virtual agent from a real one, and the portal shows them side by side.

## Commands

```text
prepare --agents N [--out PATH] [--server-settings PATH]
steady  --agents N [--interval 60] [--minutes 10] [--server URL] [--out PATH] [--backoff-max 300] [--lost-ack-pct 0] [--seed 1]
storm   --agents N [--backlog-hours 6] [--no-jitter] [--backoff-max 300] [--timeout-minutes 15] [--server URL] [--out PATH] [--lost-ack-pct 0] [--seed 1]
```

**prepare** registers SIM-0001..N with the server by writing `machines.json`
(default `deploy/config/machines.json`, which the server container mounts).
The file starts with every machine from `src/Server/appsettings.json`,
because .NET configuration merges arrays by index and index 0 of the second
file would otherwise evict WS-DEV-01. Earlier SIM entries are replaced. The
server reads the registry at startup: `docker compose restart server`.

**steady** is the design's normal minute at fleet scale. Each agent's first
post is offset by a random fraction of the interval; then every cycle it
queues a cpu_mem sample, every fifth cycle a drive sample too, and uploads.
Target rate is `agents / interval` batches per second. Runs for `--minutes`,
or until Ctrl+C, then prints the summary so far.

**storm** is step 3 of the design's "server down for six hours". Every agent
starts with a backlog at historical timestamps ending now (6 h = 432 rows,
24 h = 1,728), and the server has just come back. By default each agent first
waits `Backoff.Jitter(backoff-max)`, which is where a real agent's backoff
sits after a long outage (50 to 100 % of the maximum, so the fleet reconnects
spread over minutes). `--no-jitter` is the worst case: everyone at t=0. The
run ends when every outbox is empty or at `--timeout-minutes`.

`--lost-ack-pct P` makes an agent ignore P % of successful replies, as if the
ack never arrived: the rows stay and are resent after the backoff. The server
must drop the resend as already seen, so the row counts in the database
still equal rows acked. That is the design's idempotency promise under test.

Every 30 s one progress line: elapsed, batches and rate against target,
counts by outcome (`2xx`, `429`, `5xx`, `4xx-other`, `exception:<type>`),
p99 so far, total outbox depth, requests in flight. The final summary adds
p50 / p95 / p99 / max latency (wall time of the POST including reading the
reply), rows sent and acked, agents not drained, max in flight, and for
storms the drain time (first post to last ack) and the 429 distribution per
agent. The summary is written as JSON to `--out`
(default `results/<command>-<agents>-<yyyyMMdd-HHmm>.json`).

## Running

On the host, against a server on `https://localhost:7181`:

```powershell
dotnet run --project tools/FleetSim -- prepare --agents 1000
docker compose restart server
dotnet run --project tools/FleetSim -- steady --agents 200 --minutes 10
dotnet run --project tools/FleetSim -- storm --agents 200 --backlog-hours 6
```

Certificate validation is off (the server runs on the development
certificate), and the tool says so on every start.

## Run matrix

Fresh database (`docker compose down -v; docker compose up -d`) before 2, 3,
5 and 6, so the row counts and the database figures are the run's own.
Record the numbers in [results.md](results.md).

| # | Command | Proves |
|---|---------|--------|
| 1 | `steady --agents 50 --minutes 10` | harness works, baseline |
| 2 | `steady --agents 200 --minutes 10` | ~3 batches/s, the design's sized fleet |
| 3 | `steady --agents 1000 --minutes 10` | ~17 batches/s at the design's hold-at size |
| 4 | `storm --agents 200 --backlog-hours 6` | the six-hour outage at the sized fleet |
| 5 | `storm --agents 1000 --backlog-hours 6` | the design's literal scenario: its "~430k rows fleet-wide" is 1,000 machines × 432 |
| 6 | `storm --agents 1000 --backlog-hours 6 --no-jitter` | the throttle under the worst case |
| 7 | `storm --agents 200 --backlog-hours 24` | full-batch-go-again draining, four batches per agent |
| 8 | `storm --agents 200 --backlog-hours 6 --lost-ack-pct 20` | resends are dropped by the idempotency key |

Row counts after a run: `docker exec -i mm-db psql -U mm -d monitoring -f - < tools/FleetSim/counts.sql`.

## Scope, honestly

- Simulator, server and database all run on one machine (the server and
  database on one host is the design's own hosting assumption; the simulator
  sharing it is extra), so every number is an upper bound on cost.
- Two drives per agent, where the design budgets about one: drive rows come
  out at twice the design's volume. Harsher, not equal.
- No project samples. They are nightly and a handful of rows; the ingest
  load is cpu_mem and drives.
- The outage itself (posts failing while the backoff grows) is agent
  behaviour and is shown by `scripts/outage-demo.ps1` with one real agent.
  The storm starts at the moment the server comes back.
- A virtual agent queues samples only when it is about to upload, so during
  a backoff wait nothing new is queued. The real sampler keeps queueing. For
  a running server, which is what steady tests, the two are the same.
