#!/usr/bin/env bash
#
# DOG-50 — Manual smoke test of every M3 chaos scenario, automated.
#
# For each toggleable scenario (DOG-44/45/46/47) the script:
#   1. resets chaos.chaos_config to all-disabled
#   2. flips one scenario on via POST /chaos/set
#   3. POSTs a small batch of orders
#   4. waits for steady state, then queries Postgres + RabbitMQ for the
#      scenario-specific oracle that proves the chaos actually fired
#   5. flips the scenario back off
#   6. records PASS/FAIL in a per-run evidence log
#
# Broker restart (DOG-48) is the 5th scenario; we delegate to its own
# harness rather than re-implement it.
#
# Pre-flight expects `docker compose up -d` to already be running and
# every service to be healthy.
#
# Exit codes:
#   0 - all scenarios PASS
#   1 - usage / preflight error
#   2 - one or more scenarios FAIL
set -uo pipefail

# ---- Configuration ---------------------------------------------------------

ORDERS_PER_SCENARIO="${ORDERS_PER_SCENARIO:-3}"
ORDER_API="${ORDER_API:-http://localhost:5294}"
DASHBOARD_API="${DASHBOARD_API:-http://localhost:5000}"
RABBIT_MGMT="${RABBIT_MGMT:-http://localhost:15672}"
RABBIT_USER="${RABBIT_USER:-reservoir}"
RABBIT_PASS="${RABBIT_PASS:-reservoir}"
PG_CONTAINER="${PG_CONTAINER:-reservoir-postgres}"
PG_USER="${PG_USER:-reservoir}"
PG_DB="${PG_DB:-reservoir}"

# How long to wait for the synchronous pipeline portion (payment + kitchen +
# delivery happy path) to drain after a batch is posted. Delivery alone
# takes ~600-1500ms baseline, kitchen ~300-600ms, payment ~200-500ms; the
# slowdown scenario multiplies the kitchen portion. 30s is comfortable.
DRAIN_TIMEOUT_SEC="${DRAIN_TIMEOUT_SEC:-30}"

# Delivery-failure-loop drives 1+2+4=7s of retry TTL + handler time per
# order before reaching the DLQ. With 3 orders running concurrently the
# DLQ should fill within ~15s.
DLQ_TIMEOUT_SEC="${DLQ_TIMEOUT_SEC:-25}"

EVIDENCE_DIR="${EVIDENCE_DIR:-docs/test/evidence/dog-50}"
mkdir -p "$EVIDENCE_DIR"
RUN_TAG="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_LOG="$EVIDENCE_DIR/run-${RUN_TAG}.log"

log()       { printf '[%s] %s\n' "$(date -u +%H:%M:%SZ)" "$*" | tee -a "$RUN_LOG"; }
section()   { log ""; log "=== $* ==="; }
die()       { log "ERROR: $*"; exit 1; }

# Scenario verdicts collected here for the final summary.
declare -A VERDICTS=()
declare -a ORDERED_SCENARIOS=(delayed_payment delivery_failure_loop duplicate_events kitchen_slowdown broker_restart)

# ---- Pre-flight ------------------------------------------------------------

log "=== DOG-50 chaos smoke tests ==="
log "ORDERS_PER_SCENARIO=$ORDERS_PER_SCENARIO  RUN_TAG=$RUN_TAG"

command -v curl   >/dev/null || die "curl not found"
command -v docker >/dev/null || die "docker not found"
curl -fsS "$ORDER_API/health"     >/dev/null || die "order-service /health unreachable at $ORDER_API"
curl -fsS "$DASHBOARD_API/health" >/dev/null || die "dashboard-api /health unreachable at $DASHBOARD_API"

# ---- Helpers ---------------------------------------------------------------

# JSON-escape a value for the dashboard-api POST body.
chaos_set() {
  # Use a plain default assignment rather than ${3:-{}} — bash's parser
  # mishandles the nested braces and silently produces `{}}` (extra }).
  local name="$1" enabled="$2" params="${3-}"
  [ -z "$params" ] && params='{}'
  curl -fsS -X POST -H 'Content-Type: application/json' \
    -d "{\"name\":\"$name\",\"enabled\":$enabled,\"params\":$params}" \
    "$DASHBOARD_API/chaos/set" >/dev/null
}

# Disable every known toggleable scenario.
reset_chaos() {
  log "  reset: disabling every chaos row"
  for name in delayed_payment delivery_failure_loop duplicate_events kitchen_slowdown; do
    chaos_set "$name" false '{}' || true
  done
}

# Post one order; echoes its orderId.
post_order() {
  local tag="$1" i="$2"
  curl -fsS -X POST -H 'Content-Type: application/json' \
    -d "{\"customerId\":\"cust-DOG50-${RUN_TAG}-${tag}-${i}\",\"currency\":\"USD\",\"items\":[{\"itemId\":\"item-${i}\",\"name\":\"Burger ${i}\",\"quantity\":1,\"unitPriceCents\":900}]}" \
    "$ORDER_API/orders" \
    | grep -oE '"orderId":"[^"]+"' | head -1 | sed 's/.*:"//;s/"//'
}

# Build a quoted SQL IN-list from a bash array of UUIDs.
sql_in_list() {
  local first=1 out=""
  for v in "$@"; do
    if [ $first -eq 1 ]; then out="'$v'"; first=0
    else out="$out,'$v'"; fi
  done
  printf '%s' "$out"
}

pg_count() {
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -t -A -c "$1" 2>/dev/null | tr -d '[:space:]'
}

rabbit_depth() {
  curl -sf -u "$RABBIT_USER:$RABBIT_PASS" "$RABBIT_MGMT/api/queues/%2F/$1" 2>/dev/null \
    | grep -oE '"messages":[0-9]+' | head -1 | cut -d: -f2
}

# Wait until $1 (a pg_count expression) equals $2, or DRAIN_TIMEOUT_SEC elapses.
# $3 is a human label for the log line.
wait_for_count() {
  local query="$1" target="$2" label="$3" timeout="${4:-$DRAIN_TIMEOUT_SEC}"
  local deadline=$(( $(date +%s) + timeout ))
  local current=""
  while [ "$(date +%s)" -lt "$deadline" ]; do
    current="$(pg_count "$query")"
    if [ "$current" = "$target" ]; then
      log "  $label = $current/$target"
      return 0
    fi
    sleep 1
  done
  log "  $label = $current/$target (TIMEOUT after ${timeout}s)"
  return 1
}

record() {
  VERDICTS[$1]="$2"
  log "VERDICT [$1]: $2"
}

# ---- Scenario 1: delayed_payment ------------------------------------------

scenario_delayed_payment() {
  section "Scenario 1/5 — delayed_payment"
  reset_chaos
  local DELAY_MS=2000
  chaos_set delayed_payment true "{\"delay_ms\":$DELAY_MS}"
  log "  POSTing $ORDERS_PER_SCENARIO orders with delay_ms=$DELAY_MS"

  local -a ids=()
  for i in $(seq 1 "$ORDERS_PER_SCENARIO"); do
    ids+=("$(post_order delayed "$i")")
  done
  local in_list; in_list="$(sql_in_list "${ids[@]}")"

  # Oracle 1: payment_service log shows the chaos warning for at least one of the orderIds.
  # Oracle 2: every order eventually has a payment_record with status=COMPLETED.
  log "  awaiting $ORDERS_PER_SCENARIO SUCCEEDED payment_records"
  if wait_for_count \
       "SELECT COUNT(*) FROM payments.payment_records WHERE order_id IN ($in_list) AND status='SUCCEEDED'" \
       "$ORDERS_PER_SCENARIO" "succeeded payments"; then
    local hit
    hit=$(docker logs --since 60s reservoir-payment 2>&1 \
            | grep -c "Chaos \[delayed_payment\] active" || true)
    log "  payment-service: '$hit' delayed_payment chaos warnings in last 60s"
    if [ "$hit" -ge "$ORDERS_PER_SCENARIO" ]; then
      record delayed_payment PASS
    else
      record delayed_payment "FAIL (only $hit chaos warnings, expected >= $ORDERS_PER_SCENARIO)"
    fi
  else
    record delayed_payment "FAIL (payments did not complete in time)"
  fi

  chaos_set delayed_payment false '{}'
}

# ---- Scenario 2: delivery_failure_loop ------------------------------------

scenario_delivery_failure_loop() {
  section "Scenario 2/5 — delivery_failure_loop"
  reset_chaos
  local dlq_before
  dlq_before="$(rabbit_depth delivery.dlq)"
  log "  delivery.dlq depth before: ${dlq_before:-unreachable}"
  chaos_set delivery_failure_loop true '{}'
  log "  POSTing $ORDERS_PER_SCENARIO orders"

  local -a ids=()
  for i in $(seq 1 "$ORDERS_PER_SCENARIO"); do
    ids+=("$(post_order delivfail "$i")")
  done
  local in_list; in_list="$(sql_in_list "${ids[@]}")"

  # Oracle 1: NO row in delivery.deliveries with status=COMPLETED for these orderIds
  #           (the chaos throws before any DB write).
  # Oracle 2: delivery.dlq grew by ORDERS_PER_SCENARIO.
  log "  waiting up to ${DLQ_TIMEOUT_SEC}s for ${ORDERS_PER_SCENARIO} new delivery.dlq messages"
  local deadline=$(( $(date +%s) + DLQ_TIMEOUT_SEC ))
  local dlq_after="" delta=""
  while [ "$(date +%s)" -lt "$deadline" ]; do
    dlq_after="$(rabbit_depth delivery.dlq)"
    delta=$(( ${dlq_after:-0} - ${dlq_before:-0} ))
    if [ "$delta" -ge "$ORDERS_PER_SCENARIO" ]; then
      break
    fi
    sleep 1
  done
  log "  delivery.dlq after: ${dlq_after:-unreachable}  (delta=${delta})"

  local completed
  completed="$(pg_count "SELECT COUNT(*) FROM delivery.deliveries WHERE order_id IN ($in_list) AND status='COMPLETED'")"
  log "  delivery.deliveries COMPLETED for run orderIds: $completed (expected 0)"

  if [ "$delta" -ge "$ORDERS_PER_SCENARIO" ] && [ "$completed" = "0" ]; then
    record delivery_failure_loop PASS
  else
    record delivery_failure_loop "FAIL (delta=$delta expected>=$ORDERS_PER_SCENARIO, completed=$completed expected 0)"
  fi

  chaos_set delivery_failure_loop false '{}'
}

# ---- Scenario 3: duplicate_events -----------------------------------------

scenario_duplicate_events() {
  section "Scenario 3/5 — duplicate_events"
  reset_chaos
  local COUNT=3
  chaos_set duplicate_events true "{\"count\":$COUNT}"
  log "  POSTing $ORDERS_PER_SCENARIO orders with publish count=$COUNT"

  local -a ids=()
  for i in $(seq 1 "$ORDERS_PER_SCENARIO"); do
    ids+=("$(post_order dup "$i")")
  done
  local in_list; in_list="$(sql_in_list "${ids[@]}")"

  # Oracle: each order ends with EXACTLY ONE payment_record (idempotent consumer wins).
  log "  awaiting $ORDERS_PER_SCENARIO SUCCEEDED payment_records"
  if wait_for_count \
       "SELECT COUNT(*) FROM payments.payment_records WHERE order_id IN ($in_list) AND status='SUCCEEDED'" \
       "$ORDERS_PER_SCENARIO" "succeeded payments"; then
    local distinct_orders total_records
    distinct_orders="$(pg_count "SELECT COUNT(DISTINCT order_id) FROM payments.payment_records WHERE order_id IN ($in_list) AND status='SUCCEEDED'")"
    total_records="$(pg_count "SELECT COUNT(*) FROM payments.payment_records WHERE order_id IN ($in_list) AND status='SUCCEEDED'")"
    local dup_logs
    dup_logs=$(docker logs --since 90s reservoir-payment 2>&1 \
               | grep -cE "Skipping duplicate" || true)
    log "  distinct orderIds with payment: $distinct_orders / $ORDERS_PER_SCENARIO"
    log "  total payment_records (should equal distinct): $total_records"
    log "  payment-service 'Skipping duplicate' log lines in last 90s: $dup_logs (expected >= $(( ORDERS_PER_SCENARIO * (COUNT - 1) )))"
    if [ "$distinct_orders" = "$ORDERS_PER_SCENARIO" ] && [ "$total_records" = "$ORDERS_PER_SCENARIO" ] && [ "$dup_logs" -ge "$(( ORDERS_PER_SCENARIO * (COUNT - 1) ))" ]; then
      record duplicate_events PASS
    else
      record duplicate_events "FAIL (distinct=$distinct_orders total=$total_records dup_logs=$dup_logs)"
    fi
  else
    record duplicate_events "FAIL (payments did not complete in time)"
  fi

  chaos_set duplicate_events false '{}'
}

# ---- Scenario 4: kitchen_slowdown -----------------------------------------

scenario_kitchen_slowdown() {
  section "Scenario 4/5 — kitchen_slowdown"
  reset_chaos
  local FACTOR=5
  chaos_set kitchen_slowdown true "{\"factor\":$FACTOR}"
  log "  POSTing $ORDERS_PER_SCENARIO orders with factor=$FACTOR"

  local -a ids=()
  for i in $(seq 1 "$ORDERS_PER_SCENARIO"); do
    ids+=("$(post_order slow "$i")")
  done
  local in_list; in_list="$(sql_in_list "${ids[@]}")"

  # Oracle 1: every order reaches kitchen_orders.status=READY.
  # Oracle 2: prep_duration_ms reflects the factor (>= 3x typical baseline of 500ms => 1500ms).
  # We use a generous lower bound (1200ms) to tolerate baseline variance, since
  # factor=5 over a baseline of ~300-600ms yields ~1500-3000ms.
  log "  awaiting $ORDERS_PER_SCENARIO READY kitchen_orders"
  if wait_for_count \
       "SELECT COUNT(*) FROM kitchen.kitchen_orders WHERE order_id IN ($in_list) AND status='READY'" \
       "$ORDERS_PER_SCENARIO" "ready kitchen_orders"; then
    local long_preps avg_ms
    long_preps="$(pg_count "SELECT COUNT(*) FROM kitchen.kitchen_orders WHERE order_id IN ($in_list) AND prep_duration_ms >= 1200")"
    avg_ms="$(pg_count "SELECT COALESCE(AVG(prep_duration_ms),0)::int FROM kitchen.kitchen_orders WHERE order_id IN ($in_list)")"
    local hit
    hit=$(docker logs --since 60s reservoir-kitchen 2>&1 \
            | grep -c "Chaos \[kitchen_slowdown\] active" || true)
    log "  kitchen_orders with prep_duration_ms>=1200: $long_preps / $ORDERS_PER_SCENARIO"
    log "  avg prep_duration_ms: $avg_ms"
    log "  kitchen-service chaos warnings in last 60s: $hit"
    if [ "$long_preps" = "$ORDERS_PER_SCENARIO" ] && [ "$hit" -ge "$ORDERS_PER_SCENARIO" ]; then
      record kitchen_slowdown PASS
    else
      record kitchen_slowdown "FAIL (long_preps=$long_preps expected $ORDERS_PER_SCENARIO, chaos_warnings=$hit)"
    fi
  else
    record kitchen_slowdown "FAIL (kitchen_orders did not reach READY in time)"
  fi

  chaos_set kitchen_slowdown false '{}'
}

# ---- Scenario 5: broker_restart -------------------------------------------

scenario_broker_restart() {
  section "Scenario 5/5 — broker_restart (delegates to DOG-48 harness)"
  reset_chaos
  if [ ! -f scripts/chaos-broker-restart.sh ]; then
    record broker_restart "FAIL (scripts/chaos-broker-restart.sh missing)"
    return
  fi
  log "  invoking scripts/chaos-broker-restart.sh with ORDER_COUNT=$ORDERS_PER_SCENARIO"
  if ORDER_COUNT="$ORDERS_PER_SCENARIO" \
     EVIDENCE_DIR="$EVIDENCE_DIR/broker-restart" \
     bash scripts/chaos-broker-restart.sh >>"$RUN_LOG" 2>&1; then
    record broker_restart PASS
  else
    local code=$?
    record broker_restart "FAIL (chaos-broker-restart.sh exit=$code)"
  fi
}

# ---- Run -------------------------------------------------------------------

scenario_delayed_payment
scenario_delivery_failure_loop
scenario_duplicate_events
scenario_kitchen_slowdown
scenario_broker_restart

# ---- Summary --------------------------------------------------------------

section "Summary"
fail_count=0
for s in "${ORDERED_SCENARIOS[@]}"; do
  v="${VERDICTS[$s]:-MISSING}"
  log "  $s: $v"
  case "$v" in
    PASS) ;;
    *)    fail_count=$(( fail_count + 1 )) ;;
  esac
done

log ""
log "Evidence: $RUN_LOG"
if [ "$fail_count" -eq 0 ]; then
  log "ALL PASS — $((${#ORDERED_SCENARIOS[@]})) / $((${#ORDERED_SCENARIOS[@]})) scenarios green."
  exit 0
fi
log "FAIL — $fail_count of $((${#ORDERED_SCENARIOS[@]})) scenarios did not pass."
exit 2
