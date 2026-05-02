# Reliability Notes

Research notes covering RabbitMQ Reliability Guide, AWS Exponential Backoff and Jitter, and MS Retry Pattern.

## RabbitMQ Reliability Guide

RabbitMQ ensures no messages are lost through several mechanisms:

- **Acknowledgements** — consumers ack only after finishing processing, not on receipt. If a consumer crashes before acking, the message is redelivered. Publishers also receive confirms from the broker that the message was received.
- **Durable queues and persistent messages** — queues and messages must be explicitly marked durable/persistent, otherwise they are lost on broker restart.
- **Replication** — quorum queues are replicated across nodes. If one node fails, others continue without interruption. A new leader is elected automatically.
- **Heartbeats** — detect dead or unresponsive connections so failures are caught quickly rather than hanging indefinitely.
- **Redelivery** — messages can be redelivered in case of failure. RabbitMQ sets a `redelivered` flag so consumers can detect duplicates.

## AWS — Exponential Backoff and Jitter

When a request fails, the simplest solution is to retry. But retries increase load on the system — if the failure was caused by overload, retrying makes it worse.

**Backoff** — instead of retrying immediately, the client waits before retrying. Exponential backoff increases the delay after each attempt (e.g. 1s, 2s, 4s) up to a configured maximum. This keeps load more even.

**The problem with plain exponential backoff** — if multiple clients fail at the same time, they all back off to the same delay and retry together, causing load spikes in bursts.

**Jitter** — adds a random amount to the backoff delay so retries are spread out over time. Formula: `sleep = random(0, min(cap, base * 2^attempt))`. This distributes retries evenly and reduces contention significantly.

**Timeouts** — clients must set timeouts on all remote calls. Too high wastes resources; too low causes unnecessary retries.

## MS Retry Pattern

Transient faults in distributed systems are expected and should be handled gracefully.

**Three retry strategies:**
- **Cancel** — fault is non-transient, retrying won't help. Raise an exception immediately.
- **Retry immediately** — fault is rare or unusual (e.g. corrupted packet). Retry once right away.
- **Retry with delay** — most common case. Wait before retrying, increase delay exponentially.

**Key considerations:**
- Retry policy should match the service — aggressive retries on a struggling service make things worse.
- Operations must be idempotent before retrying safely — retrying a non-idempotent operation can cause duplicate side effects.
- **Nested retries are dangerous** — if service A retries 3x and calls service B which also retries 3x, load on the downstream multiplies. Retry at one layer only.
- Log failed attempts as informational, only log final failure as an error.

## Relevance to Our System

- We use **durable queues and persistent messages** in RabbitMQ to survive broker restarts.
- Consumers ack only after processing — combined with our **idempotent consumers** (`processed_event_ids` table) this handles redelivery safely.
- We implement **exponential backoff with jitter** — 3 attempts with delays of 1s, 2s, 4s — at the consumer level only, avoiding nested retry amplification.
- Failed messages after max retries go to the **DLQ** via RabbitMQ DLX for inspection and replay.
