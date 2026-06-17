#!/usr/bin/env bash
#
# DOG-134 — Recovery experiment runner (Experiment 7: heal + drain the DLQ).
#
# Where run-experiment.sh measures a fault while it is active, this measures
# what happens *after* a fault is healed: it drives a total delivery outage so
# every order is preserved in delivery.dlq (the DOG-55 "contain + preserve"
# result), then heals the fault and replays the DLQ via the DOG-134
# `POST /admin/dlq/replay` endpoint, and measures two recovery properties:
#
#   - recovery rate = recovered orders / dead-lettered orders (is anything lost?)
#   - MTTR          = time from initiating recovery to the last dead-lettered
#                     order completing successfully.
#
# Phases:
#   1. preflight: order-service, dashboard-api, delivery-service healthy
#   2. clean slate: disable all chaos, purge delivery.dlq (drop prior-run residue)
#   3. warmup: WARMUP_ORDERS with chaos off, drain, cooldown (steady state)
#   4. outage: enable delivery_failure_loop @ fail_probability=1.0, post ORDERS,
#      wait until delivery.dlq depth == ORDERS (every order preserved, not lost)
#   5. heal: disable delivery_failure_loop (delivery succeeds again)
#   6. recover: capture recovery_start_utc, POST /admin/dlq/replay (drains DLQ
#      back onto orders.exchange/order.ready under publisher confirms)
#   7. measure: wait until ORDERS delivery SUCCESS rows land post-recovery,
#      compute recovery rate + MTTR from the DB clock, dump CSV + summary
#
# Pre-flight expects `docker compose up -d` to already be running and healthy.
#
# Usage:
#   scripts/run-recovery-experiment.sh --experiment-id exp7-recovery --orders 50
#
# Exit codes:
#   0 - run completed and every dead-lettered order recovered
#   1 - usage / preflight error
#   2 - recovery incomplete (DLQ did not fill, or not all orders recovered)
set -uo pipefail

# ---- Defaults --------------------------------------------------------------

EXPERIMENT_ID=""
ORDERS=50
SCENARIO="delivery_failure_loop"   # the fault we heal from
WARMUP_ORDERS="${WARMUP_ORDERS:-20}"

ORDER_API="${ORDER_API:-http://localhost:5294}"
DASHBOARD_API="${DASHBOARD_API:-http://localhost:5000}"
DELIVERY_API="${DELIVERY_API:-http://localhost:5097}"

RABBIT_MGMT="${RABBIT_MGMT:-http://localhost:15672}"
RABBIT_USER="${RABBIT_USER:-reservoir}"
RABBIT_PASS="${RABBIT_PASS:-reservoir}"
DLQ_NAME="${DLQ_NAME:-delivery.dlq}"

PG_CONTAINER="${PG_CONTAINER:-reservoir-postgres}"
PG_USER="${PG_USER:-reservoir}"
PG_DB="${PG_DB:-reservoir}"

# All 50 orders must walk the full 1+2+4s retry ladder before dead-lettering,
# so allow generous time for the DLQ to fill and for recovery to complete.
DLQ_FILL_TIMEOUT_SEC="${DLQ_FILL_TIMEOUT_SEC:-120}"
RECOVERY_TIMEOUT_SEC="${RECOVERY_TIMEOUT_SEC:-120}"
DRAIN_IDLE_SEC="${DRAIN_IDLE_SEC:-5}"
COOLDOWN_SEC="${COOLDOWN_SEC:-3}"

OUT_ROOT="${OUT_ROOT:-docs/experiments/runs}"

# ---- Arg parsing -----------------------------------------------------------

usage() { sed -n '3,40p' "$0"; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --experiment-id) EXPERIMENT_ID="$2"; shift 2 ;;
    --orders)        ORDERS="$2";        shift 2 ;;
    --warmup)        WARMUP_ORDERS="$2"; shift 2 ;;
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

section "DOG-134 recovery experiment runner"
log "experiment_id = $EXPERIMENT_ID"
log "orders        = $ORDERS"
log "scenario      = $SCENARIO (healed mid-run)"
log "run_dir       = $RUN_DIR"

# ---- Preflight -------------------------------------------------------------

command -v curl   >/dev/null || die "curl not found"
command -v docker >/dev/null || die "docker not found"
curl -fsS "$ORDER_API/health"     >/dev/null || die "order-service /health unreachable at $ORDER_API"
curl -fsS "$DASHBOARD_API/health" >/dev/null || die "dashboard-api /health unreachable at $DASHBOARD_API"
curl -fsS "$DELIVERY_API/health"  >/dev/null || die "delivery-service /health unreachable at $DELIVERY_API"

pg() { docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -t -A -c "$1"; }
pg_count() { pg "$1" 2>/dev/null | tr -d '[:space:]'; }

dlq_depth() {
  curl -sf -u "$RABBIT_USER:$RABBIT_PASS" "$RABBIT_MGMT/api/queues/%2F/$DLQ_NAME" 2>/dev/null \
    | grep -oE '"messages":[0-9]+' | head -1 | cut -d: -f2
}

chaos_set() {
  local name="$1" enabled="$2" params="${3:-{\}}"
  curl -fsS -X POST -H 'Content-Type: application/json' \
    -d "{\"name\":\"$name\",\"enabled\":$enabled,\"params\":$params}" \
    "$DASHBOARD_API/chaos/set" >/dev/null
}

post_order() {
  local i="$1"
  curl -fsS -X POST -H 'Content-Type: application/json' \
    -d "{\"customerId\":\"cust-${EXPERIMENT_ID}-${RUN_TAG}-${i}\",\"currency\":\"USD\",\"items\":[{\"itemId\":\"item-${i}\",\"name\":\"Item ${i}\",\"quantity\":1,\"unitPriceCents\":900}]}" \
    "$ORDER_API/orders" >/dev/null
}
post_batch() { local count="$1" prefix="$2" i; for i in $(seq 1 "$count"); do post_order "${prefix}${i}" || log "  warn: POST ${prefix}${i} failed"; done; }

# Drain helper: block until no new metric row lands for DRAIN_IDLE_SEC seconds.
wait_for_drain() {
  local since="$1" label="${2:-drain}" timeout="${3:-60}"
  local q="SELECT COUNT(*) FROM metrics.metrics WHERE started_at >= '$since'::timestamptz"
  local prev="-1" unchanged=0 deadline current
  deadline=$(( $(date +%s) + timeout ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    current="$(pg_count "$q")"; current="${current:-0}"
    if [ "$current" = "$prev" ]; then unchanged=$(( unchanged + 1 )); else log "  [$label] rows: $current"; unchanged=0; fi
    prev="$current"
    [ "$unchanged" -ge "$DRAIN_IDLE_SEC" ] && break
    sleep 1
  done
}

# ---- Phase 2: clean slate --------------------------------------------------

section "clean slate: disable chaos + purge $DLQ_NAME"
for name in delayed_payment delivery_failure_loop duplicate_events kitchen_slowdown; do
  chaos_set "$name" false '{}' || true
done
curl -fsS -u "$RABBIT_USER:$RABBIT_PASS" -X DELETE "$RABBIT_MGMT/api/queues/%2F/$DLQ_NAME/contents" >/dev/null 2>&1 || true
sleep 1
start_depth="$(dlq_depth)"; start_depth="${start_depth:-?}"
log "$DLQ_NAME depth after purge = $start_depth"
[ "$start_depth" = "0" ] || log "WARNING: DLQ not empty after purge (depth=$start_depth) — fill target is adjusted below"

# ---- Phase 3: warmup -------------------------------------------------------

if [ "$WARMUP_ORDERS" -gt 0 ]; then
  section "warmup: $WARMUP_ORDERS orders (chaos off, discarded)"
  WARMUP_START_UTC="$(date -u +%Y-%m-%dT%H:%M:%S.%6NZ)"
  post_batch "$WARMUP_ORDERS" "warmup-${RUN_TAG}-"
  wait_for_drain "$WARMUP_START_UTC" "warmup" 90
  log "cooldown ${COOLDOWN_SEC}s"; sleep "$COOLDOWN_SEC"
fi

# ---- Phase 4: outage (preserve every order in the DLQ) ---------------------

section "outage: enable $SCENARIO @ fail_probability=1.0, post $ORDERS orders"
chaos_set "$SCENARIO" true '{"fail_probability":1.0}' || die "failed to enable $SCENARIO"
OUTAGE_START_UTC="$(date -u +%Y-%m-%dT%H:%M:%S.%6NZ)"
post_batch "$ORDERS" ""
log "posted $ORDERS orders; waiting for all to dead-letter into $DLQ_NAME"

# Completion signal comes from the DB (a delivery row with outcome=FAILED and
# retry_count=3 is written when an order exhausts its retry budget → DLQ). This
# is lag-free, unlike the RabbitMQ management `messages` stat which refreshes on
# an interval; dlq_depth() is used only as an independent cross-check.
dlq_q="SELECT COUNT(*) FROM metrics.metrics WHERE service_name='delivery' AND outcome='FAILED' AND retry_count=3 AND started_at >= '$OUTAGE_START_UTC'::timestamptz"
deadline=$(( $(date +%s) + DLQ_FILL_TIMEOUT_SEC ))
dlq_rows=0
while [ "$(date +%s)" -lt "$deadline" ]; do
  dlq_rows="$(pg_count "$dlq_q")"; dlq_rows="${dlq_rows:-0}"
  log "  [outage] dead-lettered (FAILED,retry=3): $dlq_rows / $ORDERS   (DLQ depth ~$(dlq_depth))"
  [ "$dlq_rows" -ge "$ORDERS" ] && break
  sleep 2
done
if [ "${dlq_rows:-0}" -lt "$ORDERS" ]; then
  log "WARNING: only $dlq_rows/$ORDERS orders reached the DLQ before ${DLQ_FILL_TIMEOUT_SEC}s"
fi
dead_lettered="$dlq_rows"
log "dead-lettered (preserved in DLQ) = $dead_lettered"

# ---- Phase 5: heal ---------------------------------------------------------

section "heal: disable $SCENARIO (delivery will succeed again)"
chaos_set "$SCENARIO" false '{}' || die "failed to heal $SCENARIO"
sleep 1

# ---- Phase 6: recover (replay the DLQ) -------------------------------------

section "recover: POST $DELIVERY_API/admin/dlq/replay"
RECOVERY_START_UTC="$(date -u +%Y-%m-%dT%H:%M:%S.%6NZ)"
t0="$(date +%s.%N)"
replay_resp="$(curl -fsS -X POST "$DELIVERY_API/admin/dlq/replay")" || die "replay endpoint failed"
log "replay response: $replay_resp"
replayed="$(printf '%s' "$replay_resp" | grep -oE '"replayed":[0-9]+' | head -1 | cut -d: -f2)"
replayed="${replayed:-0}"
log "replayed = $replayed message(s)"

# ---- Phase 7: measure recovery ---------------------------------------------

section "measure: waiting for $replayed orders to complete delivery"
recovered_q="SELECT COUNT(*) FROM metrics.metrics
  WHERE service_name='delivery' AND outcome='SUCCESS'
    AND started_at >= '$RECOVERY_START_UTC'::timestamptz"
deadline=$(( $(date +%s) + RECOVERY_TIMEOUT_SEC ))
recovered=0
while [ "$(date +%s)" -lt "$deadline" ]; do
  recovered="$(pg_count "$recovered_q")"; recovered="${recovered:-0}"
  log "  [recover] delivered: $recovered / $replayed"
  [ "$recovered" -ge "$replayed" ] && [ "$replayed" -gt 0 ] && break
  sleep 1
done
t1="$(date +%s.%N)"
RECOVERY_END_UTC="$(date -u +%Y-%m-%dT%H:%M:%S.%6NZ)"
end_depth="$(dlq_depth)"; end_depth="${end_depth:-?}"

wall_mttr="$(awk -v a="$t0" -v b="$t1" 'BEGIN{printf "%.3f", b-a}')"

# DB-clock MTTR: recovery_start → last recovered order completed. More precise
# than wall-clock (no polling jitter), single clock source.
db_mttr_ms="$(pg_count "SELECT COALESCE(ROUND(EXTRACT(EPOCH FROM (MAX(completed_at) - '$RECOVERY_START_UTC'::timestamptz)) * 1000), 0)
  FROM metrics.metrics
  WHERE service_name='delivery' AND outcome='SUCCESS'
    AND started_at >= '$RECOVERY_START_UTC'::timestamptz")"

# ---- Capture results -------------------------------------------------------

section "capturing results"
METRICS_CSV="$RUN_DIR/metrics.csv"
docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -A -F',' -t -c "
  SELECT id, order_id, service_name,
         to_char(started_at  AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'),
         to_char(completed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'),
         (EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)::bigint AS duration_ms,
         retry_count, outcome
  FROM metrics.metrics
  WHERE started_at >= '$OUTAGE_START_UTC'::timestamptz
    AND started_at <  '$RECOVERY_END_UTC'::timestamptz
  ORDER BY started_at;
" > "$METRICS_CSV.body"
{ echo "id,order_id,service_name,started_at,completed_at,duration_ms,retry_count,outcome"; cat "$METRICS_CSV.body"; } > "$METRICS_CSV"
rm -f "$METRICS_CSV.body"
row_count="$(($(wc -l < "$METRICS_CSV") - 1))"

recovery_rate="n/a"
if [ "${dead_lettered:-0}" -gt 0 ]; then
  recovery_rate="$(awk -v r="$recovered" -v d="$dead_lettered" 'BEGIN{printf "%.1f%%", (r/d)*100}')"
fi

SUMMARY="$RUN_DIR/summary.txt"
{
  echo "experiment_id      = $EXPERIMENT_ID"
  echo "run_tag            = $RUN_TAG"
  echo "orders_posted      = $ORDERS"
  echo "outage_start_utc   = $OUTAGE_START_UTC"
  echo "recovery_start_utc = $RECOVERY_START_UTC"
  echo "recovery_end_utc   = $RECOVERY_END_UTC"
  echo "metric_rows        = $row_count"
  echo
  echo "--- Recovery result ---"
  echo "dead_lettered (preserved in $DLQ_NAME) = $dead_lettered"
  echo "replayed (POST /admin/dlq/replay)      = $replayed"
  echo "recovered (delivery SUCCESS post-heal) = $recovered"
  echo "recovery_rate                          = $recovery_rate"
  echo "$DLQ_NAME depth after recovery         = $end_depth"
  echo "MTTR (DB clock, recovery→last success) = ${db_mttr_ms} ms"
  echo "MTTR (wall clock, replay→all drained)  = ${wall_mttr} s"
  echo
  echo "Per-service outcome counts (outage→recovery window):"
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c "
    SELECT service_name, outcome, COUNT(*) AS n
    FROM metrics.metrics
    WHERE started_at >= '$OUTAGE_START_UTC'::timestamptz AND started_at < '$RECOVERY_END_UTC'::timestamptz
    GROUP BY service_name, outcome ORDER BY service_name, outcome;"
  echo "Recovered-order delivery latency (post-heal SUCCESS rows, ms):"
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c "
    SELECT COUNT(*) AS n,
           ROUND(AVG(EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)) AS avg_ms,
           ROUND(percentile_cont(0.50) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)) AS p50_ms,
           ROUND(percentile_cont(0.95) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000)) AS p95_ms
    FROM metrics.metrics
    WHERE service_name='delivery' AND outcome='SUCCESS' AND started_at >= '$RECOVERY_START_UTC'::timestamptz;"
} > "$SUMMARY" 2>&1

log "summary       → $SUMMARY"
log "raw csv       → $METRICS_CSV ($row_count rows)"
log "recovery_rate = $recovery_rate   MTTR(db) = ${db_mttr_ms}ms   DLQ now = $end_depth"
log "DONE."

{ [ "${recovered:-0}" -ge "${dead_lettered:-1}" ] && [ "${dead_lettered:-0}" -gt 0 ]; } || exit 2
exit 0
