-- Row counts after a run, to compare with the simulator's rows_acked:
--   docker exec -i mm-db psql -U mm -d monitoring -f - < tools/FleetSim/counts.sql
-- Each virtual agent sends two drives per drive sample, so drive_samples
-- holds twice the drive sample count.
SELECT count(*) AS cpu_mem_samples FROM cpu_mem_samples;
SELECT count(*) AS drive_samples   FROM drive_samples;
SELECT count(*) AS machine_latest  FROM machine_latest;
SELECT job_id, proc_name, schedule_interval FROM timescaledb_information.jobs ORDER BY job_id;
