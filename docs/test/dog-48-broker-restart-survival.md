# DOG-48 — Broker restart survival

**Status:** Verification procedure + automated harness
**Linked issue:** DOG-48 (M3 — Chaos Engine)
**Depends on:** durable queues + persistent messages (DOG-37), persisted broker volume (`rabbitmq-data`)
**Harness:** `scripts/chaos-broker-restart.sh`

---

## 1. Goal

Prove that an in-flight order survives a hard broker bounce. Specifically:

1. N orders are POSTed to `order-service` and start moving through the pipeline.
2. RabbitMQ is restarted while messages are in flight (some in queues, some mid-handler).
3. After the broker returns to `healthy`, every one of the N orders must reach `delivery.deliveries.status = COMPLETED`.

This is the canonical evidence the thesis cites for "the system survives a broker outage". No service code is added — DOG-48 exercises mechanisms that were already wired in M2 (DOG-37):

| Mechanism | Where | Evidence |
|---|---|---|
| Durable queues | `services/*/Consumer/*Consumer.cs` (every `QueueDeclare`) | `durable: true` |
| Persistent messages | `shared/Reservoir.BuildingBlocks/Messaging/RabbitMqEventPublisher.cs`, `ConsumerRetry.cs` | `props.DeliveryMode = 2` |
| Durable exchanges | same files | `durable: true` on every `ExchangeDeclare` |
| Persisted broker state | `docker-compose.yml` | `rabbitmq-data:/var/lib/rabbitmq` |
| Auto-recovering connections | RabbitMQ.Client default | reconnect on `Connection.Recovery*` events |

---

## 2. Prerequisites

| Check | How |
|---|---|
| Docker stack up | `docker compose up -d` returns; `docker compose ps` shows every service `running` (and `healthy` where applicable) |
| Order API reachable | `curl -fsS http://localhost:5294/health` returns `{"status":"ok"}` |
| Postgres + RabbitMQ containers running | `docker inspect -f '{{.State.Running}}' reservoir-postgres reservoir-rabbitmq` returns `true true` |
| Clean baseline (recommended) | `docker compose down -v && docker compose up -d` if prior chaos runs left DLQ residue (see §4.3) |

---

## 3. Run the harness

```bash
bash scripts/chaos-broker-restart.sh
```

Default parameters (override via env vars):

| Variable | Default | What it controls |
|---|---|---|
| `ORDER_COUNT` | `10` | Number of orders POSTed in Phase 1 |
| `WAIT_BEFORE_RESTART_MS` | `300` | How long to wait after Phase 1 before the bounce — tuned so some messages are already in flight, others still queued |
| `POLL_TIMEOUT_SEC` | `90` | Phase 4 deadline. 90s gives ~75s of slack after the typical 15s broker recovery |
| `RABBIT_HEALTH_TIMEOUT_SEC` | `60` | Phase 3 deadline waiting for `docker inspect ... .State.Health.Status == healthy` |
| `ORDER_API` | `http://localhost:5294` | Where Phase 1 POSTs go |

A timestamped run log is written to `docs/test/evidence/dog-48/run-<UTC>.log` for citation.

Exit codes: `0` = PASS, `1` = pre-flight error (stack not up, etc), `2` = FAIL (≥ 1 order did not complete in time).

---

## 4. What the harness does

### 4.1 Phase 1 — drive load

POSTs `ORDER_COUNT` orders to `order-service /orders`. Each response carries an `orderId` which the script collects. These IDs anchor every downstream assertion — no time-window heuristics.

### 4.2 Phase 2 — bounce mid-flow

After `WAIT_BEFORE_RESTART_MS` (300ms by default), runs `docker restart reservoir-rabbitmq`. At this moment the typical state is roughly:

- some `order.created` messages already ack'd by `payment.queue`
- one or two payments mid-`SimulateAsync` (sleeping on `Task.Delay`)
- `payment.succeeded` / `order.ready` partially flushed
- the trailing few orders still buffered in `payment.queue`

The bounce is intentionally hard: `docker restart` kills the container with the AMQP listener still serving traffic.

### 4.3 Phase 3 — wait for broker

Polls `docker inspect -f '{{.State.Health.Status}}' reservoir-rabbitmq` until it returns `healthy`. Recovery is typically ~15 s — the same `check_port_connectivity` healthcheck that gates initial startup (`docker-compose.yml:20`). If the broker fails to recover within `RABBIT_HEALTH_TIMEOUT_SEC` the script aborts with exit `1`.

While Phase 3 runs, the .NET `RabbitMQ.Client` instances inside each service container are in their connection-recovery state machine — they will re-declare exchanges and queues on reconnect (declarations are idempotent because everything is `durable: true`).

### 4.4 Phase 4 — poll Postgres

The script issues a single Postgres query in a loop:

```sql
SELECT COUNT(*) FROM delivery.deliveries
WHERE order_id IN (<the N orderIds>) AND status = 'COMPLETED';
```

When this returns `N`, the test passes. Polling every 2 s up to `POLL_TIMEOUT_SEC`.

### 4.5 Phase 5 — per-stage breakdown

Prints a 4-row summary of how far the test orderIds got:

```
   stage   | count
-----------+-------
 orders    |    10
 payments  |    10
 kitchen   |    10
 delivered |    10
```

A clean PASS has all four equal to `ORDER_COUNT`. Anything less locates the stage that lost messages.

DLQ depths are also printed but are **informational** — they are absolute, so they include residue from earlier chaos runs (DOG-45/DOG-47 can leave entries in `delivery.dlq` / `kitchen.dlq`). The canonical evidence is the per-stage row count for THIS run's orderIds.

---

## 5. Reference run (2026-06-06)

Executed on `feature/DOG-48-broker-restart-survival` against the default 4-service compose stack:

```
[15:56:56Z] === DOG-48 broker restart survival ===
[15:56:56Z] ORDER_COUNT=10  WAIT_BEFORE_RESTART_MS=300  POLL_TIMEOUT_SEC=90
[15:56:57Z] --- Phase 1: posting 10 orders to http://localhost:5294/orders
[15:56:58Z] Posted 10 orders; first=2da9ea25-... last=420f33ab-...
[15:56:59Z] --- Phase 2: sleeping 300ms then restarting reservoir-rabbitmq
[15:57:01Z] docker restart issued
[15:57:01Z] --- Phase 3: waiting up to 60s for reservoir-rabbitmq to be healthy
[15:57:13Z] broker healthy after 14s
[15:57:13Z] --- Phase 4: polling delivery.deliveries until all 10 orders are COMPLETED
[15:57:14Z] completed=3 / 10
[15:57:16Z] completed=6 / 10
[15:57:19Z] completed=10 / 10
[15:57:19Z] --- Phase 5: per-stage row counts
   stage   | count
-----------+-------
 orders    |    10
 payments  |    10
 kitchen   |    10
 delivered |    10
[15:57:20Z] PASS: all 10 orders survived the broker restart and reached COMPLETED.
```

Timeline: bounce issued at `t = +0s`, broker healthy at `t = +14s`, every order COMPLETED by `t = +18s` — i.e. the pipeline drained within ~4 s of the broker accepting AMQP again.

---

## 6. Failure modes worth knowing

| Symptom | Likely cause |
|---|---|
| Phase 4 times out, `delivered < ORDER_COUNT` but other stages = `ORDER_COUNT` | A consumer connection didn't auto-recover. Check `docker logs reservoir-delivery` for `AlreadyClosedException` without a follow-on `Connection.Recovery` event. |
| Phase 4 times out, `payments < ORDER_COUNT` | Messages lost between publisher and `payment.queue`. Check that `payment.queue` was `durable: true` and that `DeliveryMode = 2` was actually set. |
| Phase 3 never reaches healthy | `rabbitmq-data` volume corrupted. `docker compose down -v && docker compose up -d` to recreate it. |
| Pre-flight: `RabbitMQ container 'reservoir-rabbitmq' not running` | Stack isn't up. Run `docker compose up -d` first. |

---

## 7. Evidence checklist (for thesis Results chapter)

- [ ] `docs/test/evidence/dog-48/run-<UTC>.log` — captured by the harness automatically
- [ ] Screenshot of the RabbitMQ management UI **after** the bounce (`http://localhost:15672`, Queues tab) — shows queues re-declared and empty
- [ ] Phase 5 row counts (4× `ORDER_COUNT`) — copied from the run log
- [ ] Broker downtime measured (the `broker healthy after Ns` line)
- [ ] Pipeline drain time (last `completed=N / N` line minus the `broker healthy` line)

---

## 8. Findings (to be filled in by tester for the canonical thesis run)

- **Test executed on:** YYYY-MM-DD HH:MM (timezone)
- **Tester:** name
- **Commit at test time:** `git rev-parse HEAD`
- **`ORDER_COUNT` / `WAIT_BEFORE_RESTART_MS`:** …, …
- **Broker downtime (s):** …
- **Drain time after recovery (s):** …
- **Phase 5 row counts (orders / payments / kitchen / delivered):** …, …, …, …
- **Anomalies / deviations from §5:** …
- **DOG-48 acceptance status:** PASS / FAIL

---

## Related documents

- DOG-37 — per-service DLX/DLQ (durability foundation)
- DOG-42 verification — companion procedure for retry/DLQ behaviour
- DOG-50 — the M3 canonical chaos run (will cite this scenario among others)
