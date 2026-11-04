#!/usr/bin/env bash
# S-FLEET-02's headroom sampler (#349; the rig spec signed on #326, point 5): one CSV row every
# INTERVAL seconds (10) until killed, beside a running bench. It reads the HOST (the Lima `bench`
# VM, or the runner) from /proc, so it needs no vmstat or iostat, and the two containers the
# release rig starts, found by their image tags (CampaignReleaseRig.PostgresImage / RabbitImage).
#
#   scripts/bench-headroom.sh <out.csv> [interval-seconds]
#
# Columns: the sample's UTC instant; host CPU busy and iowait % over the interval; host memory
# used and available (MiB); disk reads and writes (KiB/s) and the busy share of the busiest disk;
# PostgreSQL and RabbitMQ container CPU % and memory (from `docker stats`); RabbitMQ ready and
# unacknowledged messages over all queues; PostgreSQL backends and cumulative commits, block reads
# and block hits (`pg_stat_activity`, `pg_stat_database`). A container that is not up yet leaves
# its columns empty rather than stopping the sampler, so it can start before the bench does.
set -uo pipefail
OUT=${1:?usage: $0 <out.csv> [interval-seconds]}
INTERVAL=${2:-10}
PG_IMAGE=${GOLDPATH_BENCH_PG_IMAGE:-postgres:17-alpine}
MQ_IMAGE=${GOLDPATH_BENCH_MQ_IMAGE:-rabbitmq:4-alpine}

cpu() { awk '/^cpu /{print $2+$3+$4+$7+$8+$9, $6, $2+$3+$4+$5+$6+$7+$8+$9}' /proc/stat; }   # busy iowait total
# Whole disks only (no partitions): sectors read and written summed, then each disk's
# cumulative milliseconds doing I/O as name=ms, so the busiest disk is judged on its own delta.
disk() { awk '$3 ~ /^(sd|vd|xvd)[a-z]+$|^nvme[0-9]+n[0-9]+$/ {r+=$6; w+=$10; io=io " " $3 "=" $13} END {print r+0, w+0, io}' /proc/diskstats; }
busiest() {   # <previous name=ms list> <current name=ms list> → the largest per-disk delta in ms
  awk -v a="$1" -v b="$2" 'BEGIN {n=split(a, p, " "); for (k=1; k<=n; k++) {split(p[k], f, "="); prev[f[1]]=f[2]}
    m=split(b, c, " "); best=0; for (k=1; k<=m; k++) {split(c[k], f, "="); d=f[2]-prev[f[1]]; if (d>best) best=d}; print best}'
}
container() { docker ps -q --filter "ancestor=$1" | head -1; }
stats() { [ -n "$1" ] && docker stats --no-stream --format '{{.CPUPerc}},{{.MemUsage}}' "$1" 2>/dev/null | sed -E 's/%//; s/ \/.*//' || echo ","; }

echo "utc,host_cpu_busy_pct,host_iowait_pct,host_mem_used_mib,host_mem_available_mib,disk_read_kib_s,disk_write_kib_s,disk_busiest_util_pct,pg_cpu_pct,pg_mem,mq_cpu_pct,mq_mem,mq_ready,mq_unacked,pg_backends,pg_xact_commit,pg_blks_read,pg_blks_hit" > "$OUT"
read -r b0 i0 t0 < <(cpu); read -r r0 w0 u0 < <(disk)
# A fixed cadence, not a fixed sleep: `docker stats` and the two `docker exec` calls take seconds,
# and sleeping INTERVAL after them would stretch every row. The rates divide by the real elapsed time.
next=$(date +%s); s0=$next
while true; do
  next=$((next + INTERVAL))
  pause=$((next - $(date +%s))); [ "$pause" -gt 0 ] && sleep "$pause"
  s1=$(date +%s); elapsed=$((s1 - s0)); [ "$elapsed" -gt 0 ] || elapsed=1
  read -r b1 i1 t1 < <(cpu); read -r r1 w1 u1 < <(disk)
  u=$(busiest "$u0" "$u1")
  dt=$((t1 - t0)); [ "$dt" -gt 0 ] || dt=1
  host=$(awk -v b=$((b1-b0)) -v i=$((i1-i0)) -v t=$dt -v r=$((r1-r0)) -v w=$((w1-w0)) -v u="$u" -v s="$elapsed" \
    'BEGIN {printf "%.1f,%.1f", 100*b/t, 100*i/t; m="";
            while ((getline l < "/proc/meminfo") > 0) { split(l, f, " "); v[f[1]]=f[2] }
            printf ",%d,%d,%.0f,%.0f,%.1f", (v["MemTotal:"]-v["MemAvailable:"])/1024, v["MemAvailable:"]/1024, r*512/1024/s, w*512/1024/s, 100*u/(s*1000) }')
  pg=$(container "$PG_IMAGE"); mq=$(container "$MQ_IMAGE")
  pgs=$(stats "$pg"); mqs=$(stats "$mq")
  queues=","; [ -n "$mq" ] && queues=$(docker exec "$mq" rabbitmqctl -q list_queues messages_ready messages_unacknowledged 2>/dev/null \
    | awk 'NF==2 && $1 ~ /^[0-9]+$/ {r+=$1; u+=$2} END {printf "%d,%d", r, u}')
  db=",,,"; [ -n "$pg" ] && db=$(docker exec "$pg" psql -U postgres -tAF, -c \
    "select (select count(*) from pg_stat_activity), sum(xact_commit), sum(blks_read), sum(blks_hit) from pg_stat_database" 2>/dev/null | head -1)
  echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$host,${pgs:-,},${mqs:-,},${queues:-,},${db:-,,,}" >> "$OUT"
  b0=$b1; i0=$i1; t0=$t1; r0=$r1; w0=$w1; u0=$u1; s0=$s1
done
