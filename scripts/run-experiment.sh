#!/usr/bin/env bash
#
# DOG-52 — Reusable experiment runner for the M4 chaos experiments.
#
# Drives one labelled chaos experiment end-to-end:
#   1. preflight: order-service + dashboard-api healthy
#   2. reset every chaos row to disabled, then enable the target scenario
#   3. capture run_start_utc (used to slice the metrics window)
#   4. POST N orders at an optional rate (default: as fast as bash + curl will go)
#   5. wait for the pipeline to drain — declare drained when no new metric row
#      lands in the time window for $DRAIN_IDLE_SEC consecutive seconds, or
#      $DRAIN_TIMEOUT_SEC has elapsed
#   6. capture run_end_utc; disable chaos
#   7. dump metrics rows in [run_start_utc, run_end_utc] to a per-run folder,
#      compute per-service counts + p50/p95 durations, write summary.txt
#
# This runs the same way for the baseline (--scenario none) and every chaos
# experiment (DOG-53..DOG-57). The captured CSV is the source of truth for
# each experiment's thesis writeup.
#
# Usage:
#   scripts/run-experiment.sh \
#     --experiment-id exp1-baseline \
#     --orders 50 \
#     --scenario none
#
#   scripts/run-experiment.sh \
#     --experiment-id exp2-payment-delay \
#     --orders 50 \
#     --scenario delayed_payment \
#     --params '{"delay_ms":5000}'
#
# Pre-flight expects `docker compose up -d` to already be running and every
# service to be healthy.
#
# Exit codes:
#   0 - run completed (metrics dumped; the analyst interprets results)
#   1 - usage / preflight error
#   2 - drain timed out before the pipeline went idle
set -uo pipefail

# ---- Defaults --------------------------------------------------------------

EXPERIMENT_ID=""
ORDERS=50
SCENARIO="none"
PARAMS='{}'
RATE_PER_SEC=""

ORDER_API="${ORDER_API:-http://localhost:5294}"
DASHBOARD_API="${DASHBOARD_API:-http://localhost:5000}"
PG_CONTAINER="${PG_CONTAINER:-reservoir-postgres}"
PG_USER="${PG_USER:-reservoir}"
PG_DB="${PG_DB:-reservoir}"

# Drain heuristic: stop polling once $DRAIN_IDLE_SEC consecutive seconds pass
# with no new metric rows in the time window, or after $DRAIN_TIMEOUT_SEC
# total seconds — whichever comes first.
DRAIN_IDLE_SEC="${DRAIN_IDLE_SEC:-5}"
DRAIN_TIMEOUT_SEC="${DRAIN_TIMEOUT_SEC:-180}"

OUT_ROOT="${OUT_ROOT:-docs/experiments/runs}"

# ---- Arg parsing -----------------------------------------------------------

usage() {
  sed -n '3,40p' "$0"
  exit 1
}

while [ $# -gt 0 ]; do
  case "$1" in
    --experiment-id) EXPERIMENT_ID="$2"; shift 2 ;;
    --orders)        ORDERS="$2";        shift 2 ;;
    --scenario)      SCENARIO="$2";      shift 2 ;;
    --params)        PARAMS="$2";        shift 2 ;;
    --rate)          RATE_PER_SEC="$2";  shift 2 ;;
    -h|--help)       usage ;;
    *) echo "unknown arg: $1" >&2; usage ;;
  esac
done

[ -n "$EXPERIMENT_ID" ] || { echo "ERROR: --experiment-id is required" >&2; usage; }
[[ "$ORDERS" =~ ^[0-9]+$ ]] || { echo "ERROR: --orders must be an integer" >&2; exit 1; }

RUN_TAG="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_DIR="$OUT_ROOT/${EXPERIMENT_ID}-${RUN_TAG}"
mkdir -p "$RUN_DIR"
RUN_LOG="$RUN_DIR/run.log"

log()     { printf '[%s] %s\n' "$(date -u +%H:%M:%SZ)" "$*" | tee -a "$RUN_LOG"; }
section() { log ""; log "=== $* ==="; }
die()     { log "ERROR: $*"; exit 1; }

section "DOG-52 experiment runner"
log "experiment_id = $EXPERIMENT_ID"
log "orders        = $ORDERS"
log "scenario      = $SCENARIO"
log "params        = $PARAMS"
log "rate          = ${RATE_PER_SEC:-unbounded} orders/sec"
log "run_dir       = $RUN_DIR"

# ---- Preflight -------------------------------------------------------------

command -v curl   >/dev/null || die "curl not found"
command -v docker >/dev/null || die "docker not found"
curl -fsS "$ORDER_API/health"     >/dev/null || die "order-service /health unreachable at $ORDER_API"
curl -fsS "$DASHBOARD_API/health" >/dev/null || die "dashboard-api /health unreachable at $DASHBOARD_API"

pg() {
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -t -A -c "$1"
}
pg_count() {
  pg "$1" 2>/dev/null | tr -d '[:space:]'
}

# ---- Chaos setup -----------------------------------------------------------

chaos_set() {
  local name="$1" enabled="$2" params="${3-}"
  [ -z "$params" ] && params='{}'
  curl -fsS -X POST -H 'Content-Type: application/json' \
    -d "{\"name\":\"$name\",\"enabled\":$enabled,\"params\":$params}" \
    "$DASHBOARD_API/chaos/set" >/dev/null
}

# Disable every known toggleable scenario so a previous run never bleeds into
# this one. broker_restart is not toggled via /chaos/set — it's driven by the
# DOG-48 harness — so we don't touch it here.
reset_chaos() {
  log "resetting all chaos rows to disabled"
  for name in delayed_payment delivery_failure_loop duplicate_events kitchen_slowdown; do
    chaos_set "$name" false '{}' || true
  done
}

reset_chaos
if [ "$SCENARIO" != "none" ]; then
  log "enabling scenario: $SCENARIO with params=$PARAMS"
  chaos_set "$SCENARIO" true "$PARAMS" \
    || die "failed to enable scenario $SCENARIO"
fi

# Capture the run window as ISO-8601 UTC. Postgres parses these directly.
RUN_START_UTC="$(date -u +%Y-%m-%dT%H:%M:%S.%6NZ)"
log "run_start_utc = $RUN_START_UTC"

# ---- Drive orders ----------------------------------------------------------

section "posting $ORDERS orders"

post_order() {
  local i="$1"
  curl -fsS -X POST -H 'Content-Type: application/json' \
    -d "{\"customerId\":\"cust-${EXPERIMENT_ID}-${RUN_TAG}-${i}\",\"currency\":\"USD\",\"items\":[{\"itemId\":\"item-${i}\",\"name\":\"Item ${i}\",\"quantity\":1,\"unitPriceCents\":900}]}" \
    "$ORDER_API/orders" >/dev/null
}

# Optional pacing: sleep 1/RATE seconds between POSTs.
sleep_between=""
if [ -n "$RATE_PER_SEC" ]; then
  sleep_between="$(awk -v r="$RATE_PER_SEC" 'BEGIN{printf "%.4f", 1/r}')"
  log "pacing: sleeping ${sleep_between}s between orders"
fi

post_start_epoch="$(date +%s)"
for i in $(seq 1 "$ORDERS"); do
  post_order "$i" || log "  warn: POST order $i failed"
  [ -n "$sleep_between" ] && sleep "$sleep_between"
done
post_end_epoch="$(date +%s)"
log "posted $ORDERS orders in $(( post_end_epoch - post_start_epoch ))s"

# ---- Wait for drain --------------------------------------------------------

section "waiting for pipeline to drain"

window_clause="started_at >= '$RUN_START_UTC'::timestamptz"
count_query="SELECT COUNT(*) FROM metrics.metrics WHERE $window_clause"

prev_count="-1"
unchanged_for=0
deadline=$(( $(date +%s) + DRAIN_TIMEOUT_SEC ))
drained=0
while [ "$(date +%s)" -lt "$deadline" ]; do
  current="$(pg_count "$count_query")"
  current="${current:-0}"
  if [ "$current" = "$prev_count" ]; then
    unchanged_for=$(( unchanged_for + 1 ))
  else
    log "  metric rows: $current"
    unchanged_for=0
  fi
  prev_count="$current"
  if [ "$unchanged_for" -ge "$DRAIN_IDLE_SEC" ]; then
    drained=1
    break
  fi
  sleep 1
done

RUN_END_UTC="$(date -u +%Y-%m-%dT%H:%M:%S.%6NZ)"
log "run_end_utc   = $RUN_END_UTC"

if [ "$drained" -ne 1 ]; then
  log "WARNING: drain timeout — pipeline still producing rows after ${DRAIN_TIMEOUT_SEC}s"
fi

# ---- Tear down chaos -------------------------------------------------------

reset_chaos

# ---- Capture results -------------------------------------------------------

section "capturing results"

METRICS_CSV="$RUN_DIR/metrics.csv"

# We slice to the run window. Mirroring dashboard-api's /metrics/export.csv
# format keeps downstream analyst tooling (pandas, spreadsheets) compatible
# between the live endpoint and the per-run snapshot.
docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -A -F',' -t -c "
  SELECT id, order_id, service_name,
         to_char(started_at  AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'),
         to_char(completed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'),
         (EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)::bigint AS duration_ms,
         retry_count, outcome
  FROM metrics.metrics
  WHERE started_at >= '$RUN_START_UTC'::timestamptz
    AND started_at <  '$RUN_END_UTC'::timestamptz
  ORDER BY started_at;
" > "$METRICS_CSV.body"

{
  echo "id,order_id,service_name,started_at,completed_at,duration_ms,retry_count,outcome"
  cat "$METRICS_CSV.body"
} > "$METRICS_CSV"
rm -f "$METRICS_CSV.body"

row_count="$(($(wc -l < "$METRICS_CSV") - 1))"
log "metric rows in window: $row_count → $METRICS_CSV"

# Per-service summary computed directly in Postgres. percentile_cont gives us
# proper interpolated quantiles, which awk over the CSV would not.
SUMMARY="$RUN_DIR/summary.txt"
{
  echo "experiment_id   = $EXPERIMENT_ID"
  echo "run_tag         = $RUN_TAG"
  echo "scenario        = $SCENARIO"
  echo "params          = $PARAMS"
  echo "orders_posted   = $ORDERS"
  echo "run_start_utc   = $RUN_START_UTC"
  echo "run_end_utc     = $RUN_END_UTC"
  echo "metric_rows     = $row_count"
  echo
  echo "Per-service outcome counts:"
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c "
    SELECT service_name, outcome, COUNT(*) AS n
    FROM metrics.metrics
    WHERE started_at >= '$RUN_START_UTC'::timestamptz
      AND started_at <  '$RUN_END_UTC'::timestamptz
    GROUP BY service_name, outcome
    ORDER BY service_name, outcome;
  "
  echo "Per-service duration (ms):"
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c "
    SELECT service_name,
           COUNT(*) AS n,
           ROUND(AVG(EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)) AS avg_ms,
           ROUND(percentile_cont(0.50) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)) AS p50_ms,
           ROUND(percentile_cont(0.95) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)) AS p95_ms
    FROM metrics.metrics
    WHERE started_at >= '$RUN_START_UTC'::timestamptz
      AND started_at <  '$RUN_END_UTC'::timestamptz
    GROUP BY service_name
    ORDER BY service_name;
  "
  echo "Retry distribution (delivery — the only stage that retries today):"
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c "
    SELECT service_name, retry_count, COUNT(*) AS n
    FROM metrics.metrics
    WHERE started_at >= '$RUN_START_UTC'::timestamptz
      AND started_at <  '$RUN_END_UTC'::timestamptz
    GROUP BY service_name, retry_count
    ORDER BY service_name, retry_count;
  "
  echo "DLQ-bound rows (FAILED with retry_count = 3):"
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c "
    SELECT service_name, COUNT(*) AS n
    FROM metrics.metrics
    WHERE started_at >= '$RUN_START_UTC'::timestamptz
      AND started_at <  '$RUN_END_UTC'::timestamptz
      AND outcome = 'FAILED'
      AND retry_count = 3
    GROUP BY service_name
    ORDER BY service_name;
  "
} > "$SUMMARY" 2>&1

log "summary       → $SUMMARY"
log "raw csv       → $METRICS_CSV"
log "run log       → $RUN_LOG"
log "DONE."

if [ "$drained" -ne 1 ]; then
  exit 2
fi
exit 0
