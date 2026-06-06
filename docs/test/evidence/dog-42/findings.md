# DOG-42 — Findings

**Test executed:** 2026-06-06 14:35 UTC
**Tester:** Automated execution by Claude during DOG-42 procedure authoring
**Commit at test time:** `0a04327` (origin/develop) on branch `feature/DOG-42-dlq-retry-manual-test`
**Order ID:** `be079afd-2a12-4e28-8638-cb5515f0c6ed`
**Event ID:** `a9097647-5386-4ed3-9992-c6208ae5ce70`

## Observed retry cadence

| Failure | Timestamp (UTC) | Delta from previous |
|---|---|---|
| 1 (initial delivery) | 14:35:35.490 | — |
| 2 (after `payment.retry.1`) | 14:35:36.568 | **1.078s** ≈ 1000ms TTL ✓ |
| 3 (after `payment.retry.2`) | 14:35:38.584 | **2.016s** ≈ 2000ms TTL ✓ |
| 4 (after `payment.retry.3`) | 14:35:42.593 | **4.009s** ≈ 4000ms TTL ✓ |

All retry intervals match ARCH-003 §3.2 within scheduling jitter (~80ms overhead on the 1s retry, consistent with broker TTL precision + EF Core idempotency-check round trip).

## Final queue state

| Queue | Depth | Expected |
|---|---|---|
| `payment.dlq` | **1** | 1 ✓ |
| `payment.queue`, `payment.retry.{1,2,3}` | 0 | 0 ✓ |
| `kitchen.dlq` | 0 | 0 ✓ (DOG-37 isolation) |
| `delivery.dlq` | 0 | 0 ✓ (DOG-37 isolation) |
| `order.status.dlq` | 0 | 0 ✓ |

## DLQ message inspection

Retrieved via `GET /api/queues/%2F/payment.dlq/get` (non-destructive requeue).

| Field | Value |
|---|---|
| `properties.message_id` | `a9097647-5386-4ed3-9992-c6208ae5ce70` (= eventId) ✓ |
| `properties.headers.x-retry-count` | **3** ✓ |
| `properties.headers.x-death` | chain: `payment.queue` (rejected) ← `payment.retry.3` (expired) ✓ |
| `properties.headers.x-last-death-queue` | `payment.queue` ✓ |
| `properties.delivery_mode` | `2` (persistent) ✓ |
| `properties.content_type` | `application/json` ✓ |
| `payload.orderId` | matches probe order ✓ |
| `routing_key` | `order.created` (preserved for replay) ✓ |

Note: `x-death` lists 2 hops, not 4. This is RabbitMQ's documented behaviour — `x-death` records dead-lettering events on this message, not consumer-side ack-and-republish steps. Because `ConsumerRetry` ack-and-republishes (rather than nacking the primary queue), only the final `payment.retry.3 → orders.exchange → payment.queue → payment.dlx` hops appear in `x-death`. The authoritative attempt counter is the project's explicit `x-retry-count` header, exactly per ARCH-003 §5 rationale. This is the value DOG-40 also stamps onto event payloads.

## Postgres side-effect check

| Query | Result | Expected |
|---|---|---|
| `count(*)` from `payments.payment_records` where `order_id = be079afd-…` | **0** | 0 ✓ |
| `count(*)` from `payments.processed_event_ids` where `event_id = a9097647-…` | **0** | 0 ✓ |
| Cross-check: total `payments.payment_records` (sanity probe alone) | **1** | 1 ✓ |

The exception is thrown by `PaymentSimulator.SimulateAsync` before the EF Core transaction is opened, so all 4 attempts roll back cleanly with zero rows persisted. No "double processing" by trivial elimination — no processing at all.

## DOG-42 acceptance status

| Acceptance bullet | Status | Evidence |
|---|---|---|
| 3 retries logged | ✅ Pass | `payment-logs.txt` shows 4 delivery attempts + `Retries exhausted (3/3)` |
| Retry cadence 1s/2s/4s | ✅ Pass | Log timestamps, deltas within 80ms of TTL |
| Message lands in `payment.dlq` | ✅ Pass | DLQ depth = 1 |
| DLQ isolation (sibling DLQs empty) | ✅ Pass | `kitchen.dlq`, `delivery.dlq`, `order.status.dlq` all 0 |
| `x-retry-count` = 3 on dead-lettered message | ✅ Pass | management API output above |
| No double-processing | ✅ Pass | 0 rows in both `payment_records` and `processed_event_ids` for the failing event |
| Dashboard shows `retryCount=3` + red status | ⏸️ **Deferred** | Out of scope today; see §5.4 of the procedure and the follow-up ticket. |

## Sanity-check after cleanup

Post-revert smoke order `27f6c88c-4793-4cfc-be70-35c4ef93da1b` reached `DELIVERED` in ~4 seconds. Happy path restored, branch working tree clean of `services/` modifications.

## Anomalies / deviations from §5 of the procedure

- The procedure asked the tester to use the RabbitMQ Management UI for the DLQ message inspection. This run used the `/api/queues/.../get` HTTP endpoint directly (programmatic). Both surfaces query the same broker state; results are identical. The UI is preferable for human-driven runs.
- The original procedure example payload used the wrong field names (`name`/`quantityCount` instead of `itemId`/`quantity`) and the wrong Order Service port (`5001` instead of `5294`). Both fixed in `dog-42-dlq-retry-verification.md`.

## Evidence captured

- `payment-logs.txt` — filtered consumer log slice with timestamps
- `dlq-message-raw.txt` — `rabbitmqadmin` short form
- `dlq-message-full.txt` — `rabbitmqadmin --format=long`
- `dlq-message.json` — full management-API response with properties + headers
- `postgres-state.txt` — cross-check counts
- `findings.md` — this file
