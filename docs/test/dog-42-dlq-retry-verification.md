# DOG-42 — Manual verification: DLQ routing + retry counts

**Status:** Verification procedure
**Linked issue:** DOG-42 (M2 — Reliability Patterns)
**Depends on:** DOG-37 (per-service DLX/DLQ), DOG-38 (exponential-backoff retry), DOG-39 (idempotent consumer), DOG-40 (retry/outcome on events)
**Related architecture:** ARCH-003 §3, §5

---

## 1. Goal

Prove, end-to-end against the real broker and database, that a permanently-failing message is:

1. Delivered to the primary queue 4 times in total (initial + 3 retries).
2. Retried at the documented 1s / 2s / 4s exponential-backoff cadence.
3. Routed to its **own** service DLQ (`payment.dlq`) after the 3rd retry, with no leakage to sibling DLQs.
4. **Not** double-processed — no `payment_records` row, no `processed_event_ids` row for the failing event.

This is the canonical broker-level evidence the Implementation and Results chapters will cite.

> **Scope note — dashboard assertion deferred.** DOG-42's plan text also calls for "dashboard shows retryCount=3 + red status". The current `ConsumerRetry.HandleFailure` does not publish anything to `orders.exchange`, and `StatusTranslator` has no routing key for retry/dead-letter events — so the swim-lane cannot observe this scenario today. See §5.4 and the follow-up ticket. The broker-level evidence below is sufficient for thesis citation; the dashboard piece is a separable UX concern.

---

## 2. Prerequisites

| Check | How |
|---|---|
| Branch reflects merged M2 work | `git log --oneline | head -10` shows DOG-37, 38, 39, 40, 41 |
| Docker stack up | `docker compose up -d` returns; `docker compose ps` shows all services `running` (and `healthy` where applicable) |
| RabbitMQ management UI reachable | http://localhost:15672 (guest/guest) — see §5.2 |
| `psql` available, or use `docker compose exec postgres psql` | Used for §5.3 |

---

## 3. Failure injection (temporary code patch)

The plan calls for "temporarily make Payment Service throw on a specific order". Because no chaos toggle exists yet (DOG-43 is M3), the test is performed by applying a one-line patch to `PaymentSimulator`, running a single order through, then reverting.

### 3.1 Apply the patch

Edit `services/payment/Simulation/PaymentSimulator.cs` and add one line at the top of `SimulateAsync`:

```csharp
public async Task<PaymentOutcome> SimulateAsync(OrderCreatedEvent order, CancellationToken ct)
{
    // DOG-42 manual test: force the consumer's retry+DLQ path. Revert before committing.
    throw new InvalidOperationException("DOG-42 forced failure");

    // ----- original body below; unreachable for the duration of the test -----
    var delayMs = Random.Shared.Next(_options.MinDelayMs, _options.MaxDelayMs + 1);
    // ... (unchanged) ...
}
```

Why this works (cf. `services/payment/Consumer/PaymentConsumer.cs:88-109`): the exception propagates out of `OrderCreatedHandler.HandleAsync` before any DB write or publish, so the consumer's `catch` block routes the delivery into `ConsumerRetry.HandleFailure`, which is exactly the path DOG-42 exists to verify.

### 3.2 Why a code edit and not a config flag

Adding a permanent `ForceFailure` option to `PaymentSimulatorOptions` would overlap with M3's chaos infrastructure (DOG-43). The disposable patch keeps the M2/M3 ticket boundary clean.

### 3.3 Rebuild + restart payment service only

```bash
docker compose build payment
docker compose up -d --no-deps payment
docker compose logs -f payment   # leave open in a second terminal for §5.1
```

---

## 4. Drive the test

1. Open the RabbitMQ management UI at http://localhost:15672 → **Queues** tab. Sort by name. Note the depth of `payment.queue`, `payment.retry.1`, `payment.retry.2`, `payment.retry.3`, `payment.dlq` (should all be 0).

2. Record the start time. POST exactly one order via the Order Service API (port `5294` per `docker-compose.yml`):

   ```bash
   curl -s -X POST http://localhost:5294/orders \
     -H 'Content-Type: application/json' \
     -d '{
           "customerId": "00000000-0000-0000-0000-0000000000d4",
           "items": [
             { "name": "DOG-42 probe", "quantityCount": 1, "unitPriceCents": 1000 }
           ],
           "currency": "USD"
         }'
   ```

   Capture `orderId` and `eventId` from the response — they anchor every check below.

3. Watch the `payment` container logs for ~10s (1 + 2 + 4 = 7s of retry TTL, plus initial + final processing). The test is complete when you see the "Retries exhausted" warning.

---

## 5. Verification

### 5.1 Payment Service logs — 4 attempts at 1s / 2s / 4s

Expected log sequence (`grep` on the captured `orderId` or the literal `DOG-42 forced failure` marker):

| Order | Source | Message |
|---|---|---|
| 1 | `PaymentConsumer` | `Failed to process message ... scheduling retry or dead-lettering` |
| 2 | `ConsumerRetry` | `Scheduling retry 1/3 ... via payment.retry.1` |
| 3 | — | (1 second elapses; retry queue TTL) |
| 4 | `PaymentConsumer` | `Failed to process message ...` |
| 5 | `ConsumerRetry` | `Scheduling retry 2/3 ... via payment.retry.2` |
| 6 | — | (2 seconds elapse) |
| 7 | `PaymentConsumer` | `Failed to process message ...` |
| 8 | `ConsumerRetry` | `Scheduling retry 3/3 ... via payment.retry.3` |
| 9 | — | (4 seconds elapse) |
| 10 | `PaymentConsumer` | `Failed to process message ...` |
| 11 | `ConsumerRetry` | `Retries exhausted (3/3) for message ... dead-lettering to payment.dlq` |

Verify:

- **4 distinct "Failed to process" entries** — initial delivery + 3 retries.
- The gap between consecutive failures is **1s, 2s, 4s** (±200ms tolerance for scheduling jitter).
- The terminal log line is the `Retries exhausted` warning, not another retry.

Save the filtered log slice as `docs/test/evidence/dog-42/payment-logs.txt`.

### 5.2 RabbitMQ management UI

Once the test finishes, in the **Queues** tab confirm:

| Queue | Expected steady-state depth | Notes |
|---|---|---|
| `payment.queue` | 0 | message left after final delivery |
| `payment.retry.1` | 0 | drained by TTL |
| `payment.retry.2` | 0 | drained by TTL |
| `payment.retry.3` | 0 | drained by TTL |
| **`payment.dlq`** | **1** | the dead-lettered message |
| `kitchen.dlq` | 0 | **failure isolation** check (DOG-37) |
| `delivery.dlq` | 0 | **failure isolation** check (DOG-37) |
| `order.status.dlq` | 0 | unrelated to the payment failure |

Then click `payment.dlq` → **Get messages** → `Ack Mode: Reject requeue true` (non-destructive). Expand the message and capture:

- **Properties → message_id** equals the inbound `eventId` from §4 step 2.
- **Properties → headers → x-retry-count** equals `3`.
- **Properties → headers → x-death** contains entries for `payment.retry.1`, `payment.retry.2`, `payment.retry.3` (preserved by RabbitMQ for replay).
- **Payload** is the original `order.created` JSON.

Screenshot the Queues tab and the expanded DLQ message. Save under `docs/test/evidence/dog-42/`.

### 5.3 Postgres — no double-processing

The exception is thrown before any DB write (cf. `services/payment/Handlers/OrderCreatedHandler.cs:63-83`), so absence of side effects is the assertion:

```bash
docker compose exec postgres psql -U reservoir -d reservoir -c \
  "SELECT count(*) AS rows FROM payments.payment_records WHERE order_id = '<orderId-from-step-2>';"
# expected: 0

docker compose exec postgres psql -U reservoir -d reservoir -c \
  "SELECT count(*) AS rows FROM payments.processed_event_ids WHERE event_id = '<eventId-from-step-2>';"
# expected: 0
```

Both must return `0`. A non-zero `payment_records` count would mean a retry partially committed (i.e. side-effects without idempotency). A non-zero `processed_event_ids` count would mean we recorded "processed" without actually processing — also a bug.

Save the command output as `docs/test/evidence/dog-42/postgres-state.txt`.

### 5.4 Dashboard — known limitation

**Expected (today):** the swim-lane row for this order stays in `Created` state with `retryCount = 0` throughout the test, even while retries are firing in the background.

**Why:** `ConsumerRetry.HandleFailure` (shared in `Reservoir.BuildingBlocks.Messaging`) operates purely on the broker — it does not publish a status event. The dashboard's `StatusTranslator` (`services/dashboard-api/Handlers/StatusTranslator.cs`) only translates the six business event keys; there is no key for "retry scheduled" or "dead-lettered". A handler exception therefore bypasses every dashboard publication path.

**Implication for DOG-42 acceptance:** the broker + Postgres evidence above proves the reliability machinery works. The dashboard-visibility leg of the acceptance criterion is deferred to the follow-up ticket: **"Publish retry/DLQ status events to SignalR"** (proposed). Treat the dashboard-side bullet as a known shortfall, not a test failure.

---

## 6. Cleanup

```bash
# 1. Purge the DLQ entry so subsequent runs start from a clean slate.
#    Via RabbitMQ UI: Queues → payment.dlq → Purge.

# 2. Revert the simulator patch.
git checkout -- services/payment/Simulation/PaymentSimulator.cs

# 3. Rebuild and bring payment back to its normal behaviour.
docker compose build payment
docker compose up -d --no-deps payment
```

Confirm the revert: `git diff services/payment/Simulation/PaymentSimulator.cs` is empty, and a fresh order placed via the API now flows happy-path to `delivery.completed`.

---

## 7. Evidence checklist (for thesis Results chapter)

- [ ] `payment-logs.txt` — 4 failure entries with timestamps showing 1s/2s/4s gaps
- [ ] `rabbitmq-queues.png` — Queues tab post-test (`payment.dlq` = 1, sibling DLQs = 0)
- [ ] `rabbitmq-dlq-message.png` — expanded DLQ message showing `x-retry-count: 3` and the `x-death` array
- [ ] `postgres-state.txt` — both queries return 0
- [ ] `findings.md` — populate §8 below

---

## 8. Findings (to be filled in by tester)

- **Test executed on:** YYYY-MM-DD HH:MM (timezone)
- **Tester:** name
- **Commit at test time:** `git rev-parse HEAD`
- **Order ID:** …
- **Event ID:** …
- **Observed retry gaps (ms):** retry 1 = …, retry 2 = …, retry 3 = …
- **`payment.dlq` final depth:** …
- **Sibling DLQs depth (kitchen / delivery / order.status):** …, …, …
- **`processed_event_ids` count for failed event:** …
- **`payment_records` count for order:** …
- **Anomalies / deviations from §5:** …
- **DOG-42 acceptance status:** broker + DB criteria pass / fail; dashboard criterion deferred per §5.4.

---

## Related documents

- ARCH-003 — RabbitMQ topology (the system under test)
- DOG-37 / DOG-38 — the implementations being verified
- DOG-50 — the analogous M3 manual test for chaos scenarios (will reuse §5's evidence pattern)
