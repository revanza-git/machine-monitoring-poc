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

## The three commands

- **`prepare`** registers the simulated machines with the server. It writes
  `machines.json` with SIM-0001..N and their API keys; it sends no traffic.
- **`steady`** is a normal day at fleet scale: N machines reporting at the
  design cadence for a set number of minutes.
- **`storm`** is the recovery after an outage: N machines that each hold
  hours of unsent samples, posting the moment the server is back.

Run `prepare` once, restart the server once so it reads the registry, then
run `steady` and `storm` as often as you like. A smaller run uses the first
N of the registered machines.

```text
prepare --agents N [--out PATH] [--server-settings PATH]
steady  --agents N [--interval 60] [--minutes 10] [--server URL] [--out PATH] [--backoff-max 300] [--lost-ack-pct 0] [--seed 1]
storm   --agents N [--backlog-hours 6] [--no-jitter] [--backoff-max 300] [--timeout-minutes 15] [--server URL] [--out PATH] [--lost-ack-pct 0] [--seed 1]
```

**prepare** writes `machines.json` (default `deploy/config/machines.json`,
which the server container mounts). The file starts with every machine from
`src/Server/appsettings.json`, because .NET configuration merges arrays by
index and index 0 of the second file would otherwise evict WS-DEV-01.
Earlier SIM entries are replaced. The server reads the registry at startup:
`docker compose restart server`.

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
spread over minutes). With the default maximum of 300 s that means **nothing
happens for the first 150 s**; the progress lines show zero batches and the
run has not hung. `--no-jitter` is the worst case: everyone at t=0. The run
ends when every outbox is empty or at `--timeout-minutes`.

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

In Docker, next to the server and database on the compose network, so the
traffic never crosses Docker Desktop's port proxy and a reviewer without
Windows can still see a populated portal:

```powershell
docker compose run --rm fleetsim prepare --agents 1000          # register SIM-0001..1000 in deploy/config/machines.json
docker compose restart server                                   # the server reads the registry at startup
docker compose run --rm fleetsim steady --agents 200 --minutes 10   # 200 machines reporting normally for ten minutes
docker compose run --rm fleetsim storm --agents 200 --backlog-hours 6   # 200 machines draining a six-hour backlog
```

While `steady` runs, the portal at <https://localhost:7181> shows the SIM
machines online with moving CPU and memory bars beside the real one; when
it ends they turn off together. After a `storm`, click any SIM machine and
pick the one-hour range: the backlog is there with its original timestamps.

The service sits behind a compose profile, so `docker compose up -d` never
starts it. `machines.json` lands in `deploy/config/` and the JSON summaries in
`tools/FleetSim/results/`, both on the host. `docker stats` then gives server,
database and simulator figures in one place.

On the host instead, against the same server:

```powershell
dotnet run --project tools/FleetSim -- prepare --agents 1000    # same file, same restart afterwards
docker compose restart server
dotnet run --project tools/FleetSim -- steady --agents 200 --minutes 10
dotnet run --project tools/FleetSim -- storm --agents 200 --backlog-hours 6
```

Certificate validation is off (the server runs on the development
certificate), and the tool says so on every start.

## Smoke test, five minutes

Checks that every path works before any measuring. The results of one such
pass are in [results.md](results.md).

1. **Unit tests**, no server needed. `tests/FleetSim.Tests` drives one
   upload step through every outcome against a stub, plus outbox, backlog,
   stats, prepare and flags:

   ```powershell
   dotnet test MachineMonitoring.slnx
   ```

2. **Steady.** Expect about 60 batches at the 1.0/s target, all `2xx`, 75
   rows sent and acked, depth 0. In the portal five SIM machines are online
   during the minute and off afterwards.

   ```powershell
   docker compose run --rm fleetsim steady --agents 5 --interval 5 --minutes 1
   ```

3. **Storm that drains.** Expect 5 batches of 72 rows, 360 rows acked, no
   429, a drain time of a few seconds after the 5 to 10 s jitter wait.

   ```powershell
   docker compose run --rm fleetsim storm --agents 5 --backlog-hours 1 --backoff-max 10
   ```

4. **Jitter against no jitter.** Same backlog, same server, run twice. With
   jitter (shrunk to 20 s so the run takes seconds, not minutes) the agents
   start between 10 and 20 s in: expect no or very few 429s, max in flight in
   single digits, p50 around 50 ms. Without jitter all 40 post at once
   against `Ingest:MaxInFlight` 16: expect roughly half of them throttled,
   max in flight 40, and every agent still drained inside ten seconds. The
   first is the design's "reconnect spread over minutes"; the second is the
   throttle holding when the spread is not there.

   ```powershell
   docker compose run --rm fleetsim storm --agents 40 --backlog-hours 1 --backoff-max 20
   docker compose run --rm fleetsim storm --agents 40 --backlog-hours 1 --no-jitter
   ```

5. **Lost acks.** Expect rows sent above 720 and rows acked exactly 720,
   with a few `acks lost`. Then count: the database gained exactly 600
   cpu_mem rows and 240 drive rows (10 agents × 60 and 10 × 12 × 2 drives),
   so every resend was dropped by the idempotency key.

   ```powershell
   docker compose run --rm fleetsim storm --agents 10 --backlog-hours 1 --no-jitter --backoff-max 10 --lost-ack-pct 30
   Get-Content tools/FleetSim/counts.sql | docker exec -i mm-db psql -U mm -d monitoring -f -
   ```

6. **Two negative checks.** Ctrl+C during any run prints the summary so far,
   sends the goodbyes and exits cleanly. An unknown flag such as
   `--intervall 5` prints the usage with the flag named.

Reading a result file: `outcomes` should be `2xx` only unless the run was
meant to draw 429s; `rows_sent` equals `rows_acked` unless acks were lost on
purpose; for storms `drain_s` is the time the recovery took and
`max_in_flight` how hard the server was hit. `achieved_per_s` is diluted by
a storm's silent jitter wait, so read `drain_s` instead.

The smoke runs leave SIM machines in the database and the portal. To reset:
`docker compose down -v` then `docker compose up -d`; `machines.json` keeps
the registrations.

## Run matrix

Fresh database (`docker compose down -v; docker compose up -d`) before 2, 3,
5 and 6, so the row counts and the database figures are the run's own.
Record the numbers in [results.md](results.md). Storms use the default 300 s
backoff maximum here, so each starts with 150 to 300 s of silence: that is
the design as written.

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

Row counts after a run, from PowerShell (which has no `<` redirection):

```powershell
Get-Content tools/FleetSim/counts.sql | docker exec -i mm-db psql -U mm -d monitoring -f -
```

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
