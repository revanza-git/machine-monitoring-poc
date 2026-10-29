# FleetSim results

One row per run of the matrix in [README.md](README.md). The simulator writes
the request-side numbers to `results/<command>-<agents>-<timestamp>.json`;
the host-side columns are read by hand from `docker stats` during the run
and from `counts.sql` after it. Everything ran on one machine, so every
number is an upper bound on cost. Fill in and keep; the JSON files stay local.

| # | Run | Date | Batches/s (target) | p50 / p95 / p99 ms | 429s | Drain s | Server CPU % | Server mem MB | DB CPU % | DB mem MB | cpu_mem rows | drive rows | machine_latest |
|---|-----|------|--------------------|--------------------|------|---------|--------------|---------------|----------|-----------|--------------|------------|----------------|
| 1 | `steady --agents 50 --minutes 10` | | | | | - | | | | | | | |
| 2 | `steady --agents 200 --minutes 10` | | | | | - | | | | | | | |
| 3 | `steady --agents 1000 --minutes 10` | | | | | - | | | | | | | |
| 4 | `storm --agents 200 --backlog-hours 6` | | | | | | | | | | | | |
| 5 | `storm --agents 1000 --backlog-hours 6` | | | | | | | | | | | | |
| 6 | `storm --agents 1000 --backlog-hours 6 --no-jitter` | | | | | | | | | | | | |
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
