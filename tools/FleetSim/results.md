# FleetSim results

One row per run of the matrix in [README.md](README.md). The simulator writes
the request-side numbers to `results/<command>-<agents>-<timestamp>.json`;
the host-side columns are read by hand from `docker stats` during the run
and from `counts.sql` after it. Everything ran on one machine, so every
number is an upper bound on cost. Fill in and keep; the JSON files stay local.

| # | Run | Date | Batches/s (target) | p50 / p95 / p99 ms | 429s | Drain s | Server CPU % | Server mem MB | DB CPU % | DB mem MB | cpu_mem rows | drive rows | machine_latest |
|---|-----|------|--------------------|--------------------|------|---------|--------------|---------------|----------|-----------|--------------|------------|----------------|
| 1 | `steady --agents 50 --minutes 10` | | | | | - | | | | | | | |
| 2 | `steady --agents 200 --minutes 10` | 2026-10-29 | 3.33 (3.33) | 11 / 17 / 48 | 0 | - | 1–3 | 76–110 | ~1 | 80–90 | 2,001 | 804 | 200 |
| 3 | `steady --agents 1000 --minutes 10` | 2026-10-29 | 16.67 (16.67) | 16 / 26 / 55 | 4 | - | ~8 (first minute ~40, peak 90) | 65–100 | ~9, peak 31 | 75–95 | 10,000 | 4,008 | 1,000 |
| 4 | `storm --agents 200 --backlog-hours 6` | 2026-10-29 | 200 batches over a 149 s drain | 268 / 424 / 578 | 0 | 149 | 5–7, 5 s peaks to 45 | 90–110 | 25–64, 5 s peaks to 97 | 80–90 | 72,000 | 28,800 | 200 |
| 5 | `storm --agents 1000 --backlog-hours 6` | 2026-10-29 | 1,002 requests over a 150 s drain | 351 / 722 / 1,137 | 2 | 149.5 | ~13, peak 95 | ~90 | ~240 (2.4 cores), peak 421 | ~140 | 360,000 | 144,000 | 1,000 |
| 6 | `storm --agents 1000 --backlog-hours 6 --no-jitter` | 2026-10-29 | 9,492 requests over a 161 s drain | 2,137 / 7,893 / 8,129 | 8,492 | 160.7 | ~34, peak 331 | ~215 | ~200 (2 cores), peak 856 | ~147 | 360,000 | 144,000 | 1,000 |
| 7 | `storm --agents 200 --backlog-hours 24` | | | | | | | | | | | | |
| 8 | `storm --agents 200 --backlog-hours 6 --lost-ack-pct 20` | | | | | | | | | | | | |

Notes:

- Server CPU and memory: `docker stats mm-server` while the run is going
  (peak, or the steady value for steady runs). Database: `docker stats mm-db`.
- Row counts: `counts.sql` on a database that was fresh before the run, so
  the numbers are the run's own. Expected: cpu_mem rows = agents × cpu_mem
  samples per agent; drive rows = 2 × agents × drive samples per agent;
  machine_latest = agents (+1 if the real agent was posting).
- Run 8: rows sent exceeds rows acked by the resends, and the row counts
  still equal the expected values. That is the idempotency key doing its job.
- Runs 2 and 4 on 2026-10-29 overlapped: the storm was started 81 s into
  the steady run, so it drained on top of 3.3 batches/s of live traffic and
  the steady's later minutes include the drain. Both are therefore slightly
  harsher than the matrix intends. Run 2 had 2 × 5xx out of 2,001, the chunk
  deadlock below at 3 s into the fresh database, both retried; the row
  counts (74,001 cpu_mem = 72,000 + 2,001; 29,604 drive = 28,800 + 804)
  confirm nothing was lost. CPU figures are `docker stats` percentages of
  one core, sampled every 5 s; the ranges are 30 s averages.
- Runs 3, 5 and 6 on 2026-10-29 each had a fresh database and nothing else
  posting. Storm latencies in run 6 include the Retry-After waits, since
  every 429 is a request. Row counts were exact in all three: 10,000 +
  4,008 for the steady, 360,000 + 144,000 for each storm. A first attempt at
  these three was discarded: a stopped run script kept running on Windows
  and overlapped the next one, and `machines.json` held only 200 entries at
  the time (a `prepare --agents 200` had been run), so 800 agents were
  answered 401. Both are recorded under Findings.

## Smoke runs, 2026-10-29

Server and database in their containers, 1,000 machines registered, the
simulator from the host for the first run and from its container for the
rest. Not performance figures; they show each path works.

| Run | Result |
|-----|--------|
| `steady --agents 5 --interval 5 --minutes 1` | 60 batches at the 1.0/s target, 59 × 2xx and 1 × 5xx (the deadlock below, retried by the agent), 75 rows sent and acked, p50 13 ms, all five machines online during the run and off after the goodbye |
| `storm --agents 5 --backlog-hours 1 --backoff-max 10` | 5 batches, 360 rows acked, depth 0, drained in 4.2 s, no 429 |
| `storm --agents 40 --backlog-hours 1 --no-jitter` | 40 × 2xx and 24 × 429 against `MaxInFlight` 16, every agent drained, 2,880 rows acked in 7.9 s, max in flight 40 as seen by the client |
| `storm --agents 10 --backlog-hours 1 --no-jitter --backoff-max 10 --lost-ack-pct 30` | 2 acks lost, 864 rows sent, 720 acked, database gained exactly 600 cpu_mem and 240 drive rows: the resends were dropped |

## Findings

Things the runs turned up that are not numbers.

- **The harness does not stop on 401.** With `machines.json` holding 200
  entries, a 1,000-agent run let 800 agents collect `4xx-other` and back
  off for ten minutes while the portal showed 200 machines. A 401 means the
  registry is wrong, which no retry will fix; the run should fail at once
  and say "run prepare". Open, harness only.
- **Jitter against the throttle at 1,000.** Same 432,000 rows: with jitter
  150 s and two 429s, without it 161 s and 8,492. The design's spread is
  worth almost every rejection and costs nothing in recovery time; the
  throttle alone also gets everyone drained, in 11 s more, at the price of
  nine rejections per agent and a database working at two to eight cores
  instead of two to four.

- **Deadlock on the first batches into a new chunk** (smoke run, 5 agents,
  2026-10-29). TimescaleDB opens a new weekly chunk on Thursdays at 00:00
  UTC. Creating it attaches the chunk's foreign key to `machines`, which
  takes a `ShareRowExclusiveLock` on that table; every batch has already
  locked its own `machines` row by the upsert at the top of the transaction.
  Two first-contact batches arriving together can therefore deadlock: one
  holds the `machines` row and waits for the chunk, the other holds the chunk
  creation and waits for `machines`. PostgreSQL kills one (`40P01`), the
  server answers 500, and the agent retries after its backoff, so nothing is
  lost: that path worked as designed. Reproduced on the fresh database of
  run 2 (two victims at 3 s, both retried): two fresh databases out of two.
  The options were to move the `machines` upsert out of the batch
  transaction (the machine row is identity, not data, and can exist before
  its first sample), to retry a batch once on `40P01`, or to drop the
  foreign keys from the hypertables as TimescaleDB recommends for
  write-heavy tables. **Fixed in step 22** with the first: the upsert
  commits on its own before the transaction begins, so no `machines` row is
  locked while a chunk is created. Retrying would have hidden the cause and
  dropping the keys would have changed the design's schema. Verified on
  2026-10-29 with a fresh database before each run, so the first batches had
  to create the week's chunks under load: `storm --agents 40 --backlog-hours 1
  --no-jitter` gave 40 × 2xx, 24 × 429, no 5xx; `steady --agents 200
  --minutes 2` gave 400 × 2xx, no 5xx. The server log had no `40P01` in
  either run, where every earlier fresh start had two.
