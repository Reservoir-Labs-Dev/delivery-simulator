#!/usr/bin/env bash
#
# DOG-48 — Broker restart survival chaos scenario.
#
# Posts N orders, restarts RabbitMQ mid-flow, and verifies every order reaches
# `delivery.deliveries.status = COMPLETED` after the broker comes back. The
# building blocks the system relies on for this property:
#
#   - Durable queues (durable:true on every QueueDeclare in services/*/Consumer/)
#   - Persistent messages (DeliveryMode=2 in RabbitMqEventPublisher + ConsumerRetry)
#   - Persisted broker state (docker volume `rabbitmq-data` -> /var/lib/rabbitmq)
#   - Auto-recovering connections (RabbitMQ.Client default)
#
# Pre-flight expects `docker compose up -d` to already be running.
#
# Exit codes:
#   0 - PASS (all orders completed)
#   1 - usage / preflight error
#   2 - FAIL (one or more orders did not complete before POLL_TIMEOUT_SEC)
#
set -euo pipefail

# ---- Configuration ---------------------------------------------------------

ORDER_COUNT="${ORDER_COUNT:-10}"
WAIT_BEFORE_RESTART_MS="${WAIT_BEFORE_RESTART_MS:-300}"
POLL_TIMEOUT_SEC="${POLL_TIMEOUT_SEC:-90}"

ORDER_API="${ORDER_API:-http://localhost:5294}"
PG_CONTAINER="${PG_CONTAINER:-reservoir-postgres}"
PG_USER="${PG_USER:-reservoir}"
PG_DB="${PG_DB:-reservoir}"
RABBIT_CONTAINER="${RABBIT_CONTAINER:-reservoir-rabbitmq}"
RABBIT_HEALTH_TIMEOUT_SEC="${RABBIT_HEALTH_TIMEOUT_SEC:-60}"

EVIDENCE_DIR="${EVIDENCE_DIR:-docs/test/evidence/dog-48}"
mkdir -p "$EVIDENCE_DIR"
RUN_TAG="$(date -u +%Y%m%dT%H%M%SZ)"
RUN_LOG="$EVIDENCE_DIR/run-${RUN_TAG}.log"

log()  { printf '[%s] %s\n' "$(date -u +%H:%M:%SZ)" "$*" | tee -a "$RUN_LOG"; }
fail() { log "FAIL: $*"; exit 2; }
die()  { log "ERROR: $*"; exit 1; }

# ---- Pre-flight ------------------------------------------------------------

log "=== DOG-48 broker restart survival ==="
log "ORDER_COUNT=$ORDER_COUNT  WAIT_BEFORE_RESTART_MS=$WAIT_BEFORE_RESTART_MS  POLL_TIMEOUT_SEC=$POLL_TIMEOUT_SEC"

command -v curl   >/dev/null || die "curl not found"
command -v docker >/dev/null || die "docker not found"

docker inspect -f '{{.State.Running}}' "$RABBIT_CONTAINER" >/dev/null 2>&1 \
  || die "RabbitMQ container '$RABBIT_CONTAINER' not running. Run 'docker compose up -d' first."
docker inspect -f '{{.State.Running}}' "$PG_CONTAINER" >/dev/null 2>&1 \
  || die "Postgres container '$PG_CONTAINER' not running."

curl -fsS "$ORDER_API/health" >/dev/null || die "order-service /health unreachable at $ORDER_API"

# ---- Phase 1: post orders --------------------------------------------------

log "--- Phase 1: posting $ORDER_COUNT orders to $ORDER_API/orders"
declare -a ORDER_IDS=()

for i in $(seq 1 "$ORDER_COUNT"); do
  resp="$(curl -fsS -X POST -H 'Content-Type: application/json' \
    -d "{\"customerId\":\"cust-DOG48-${RUN_TAG}-${i}\",\"currency\":\"USD\",\"items\":[{\"itemId\":\"item-${i}\",\"name\":\"Burger ${i}\",\"quantity\":1,\"unitPriceCents\":900}]}" \
    "$ORDER_API/orders")"
  oid="$(printf '%s' "$resp" | grep -oE '"orderId":"[^"]+"' | head -1 | sed 's/.*:"//;s/"//')"
  [ -n "$oid" ] || die "Could not parse orderId from response: $resp"
  ORDER_IDS+=("$oid")
done

log "Posted ${#ORDER_IDS[@]} orders; first=${ORDER_IDS[0]} last=${ORDER_IDS[-1]}"

# ---- Phase 2: kill the broker mid-flow ------------------------------------

log "--- Phase 2: sleeping ${WAIT_BEFORE_RESTART_MS}ms then restarting $RABBIT_CONTAINER"
# Use `sleep` with fractional seconds (bash/coreutils support it).
sleep "$(awk -v ms="$WAIT_BEFORE_RESTART_MS" 'BEGIN { print ms/1000 }')"
restart_t0="$(date +%s)"
docker restart "$RABBIT_CONTAINER" >/dev/null
log "docker restart issued"

# ---- Phase 3: wait for broker to be healthy again -------------------------

log "--- Phase 3: waiting up to ${RABBIT_HEALTH_TIMEOUT_SEC}s for $RABBIT_CONTAINER to be healthy"
deadline=$(( $(date +%s) + RABBIT_HEALTH_TIMEOUT_SEC ))
while [ "$(date +%s)" -lt "$deadline" ]; do
  state="$(docker inspect -f '{{.State.Health.Status}}' "$RABBIT_CONTAINER" 2>/dev/null || echo unknown)"
  if [ "$state" = "healthy" ]; then
    log "broker healthy after $(( $(date +%s) - restart_t0 ))s"
    break
  fi
  sleep 1
done
[ "$state" = "healthy" ] || die "broker did not return to healthy within ${RABBIT_HEALTH_TIMEOUT_SEC}s (last state=$state)"

# ---- Phase 4: poll Postgres until every order is COMPLETED ----------------

# Build a single SQL IN-list once.
order_in_list=""
for oid in "${ORDER_IDS[@]}"; do
  if [ -z "$order_in_list" ]; then
    order_in_list="'$oid'"
  else
    order_in_list="$order_in_list,'$oid'"
  fi
done

log "--- Phase 4: polling delivery.deliveries until all $ORDER_COUNT orders are COMPLETED (timeout ${POLL_TIMEOUT_SEC}s)"
poll_deadline=$(( $(date +%s) + POLL_TIMEOUT_SEC ))
completed_count=0
while [ "$(date +%s)" -lt "$poll_deadline" ]; do
  completed_count="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -t -A \
    -c "SELECT COUNT(*) FROM delivery.deliveries WHERE order_id IN ($order_in_list) AND status = 'COMPLETED';" \
    2>/dev/null | tr -d '[:space:]')"
  log "completed=$completed_count / $ORDER_COUNT"
  if [ "$completed_count" = "$ORDER_COUNT" ]; then
    break
  fi
  sleep 2
done

# ---- Phase 5: verdict + per-stage breakdown -------------------------------

log "--- Phase 5: per-stage row counts"
docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c \
  "SELECT 'orders'   AS stage, COUNT(*) FROM orders.orders          WHERE \"Id\"   IN ($order_in_list) UNION ALL
   SELECT 'payments',          COUNT(*) FROM payments.payment_records WHERE order_id IN ($order_in_list) UNION ALL
   SELECT 'kitchen',           COUNT(*) FROM kitchen.kitchen_orders   WHERE order_id IN ($order_in_list) UNION ALL
   SELECT 'delivered',         COUNT(*) FROM delivery.deliveries      WHERE order_id IN ($order_in_list) AND status = 'COMPLETED';" \
  | tee -a "$RUN_LOG"

# Capture DLQ depths for the run log. Note: these are absolute depths and may
# include residue from prior chaos runs; the canonical evidence is Phase 5's
# per-stage row count for the orderIds posted by THIS run.
log "--- DLQ depths (absolute; not deltas)"
for q in payment.dlq kitchen.dlq delivery.dlq order.status.dlq; do
  depth="$(curl -sf -u reservoir:reservoir "http://localhost:15672/api/queues/%2F/$q" 2>/dev/null \
    | grep -oE '"messages":[0-9]+' | head -1 | cut -d: -f2)"
  log "  $q = ${depth:-unreachable}"
done

if [ "$completed_count" = "$ORDER_COUNT" ]; then
  log "PASS: all $ORDER_COUNT orders survived the broker restart and reached COMPLETED."
  log "Evidence: $RUN_LOG"
  exit 0
fi

fail "only $completed_count of $ORDER_COUNT orders completed before ${POLL_TIMEOUT_SEC}s timeout."
