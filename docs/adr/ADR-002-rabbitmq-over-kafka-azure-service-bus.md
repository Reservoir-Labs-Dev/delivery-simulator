# ADR-002: RabbitMQ over Kafka / Azure Service Bus

## Status
Accepted

## Context
Our system requires a message broker to carry events between four microservices (Order → Payment → Kitchen → Delivery). The broker must support dead-lettering for failed messages, run locally in Docker without external dependencies, and be operationally simple for a two-person academic project. Three options were evaluated: RabbitMQ, Apache Kafka, and Azure Service Bus.

## Decision
We use RabbitMQ as the sole message broker.

## Rationale

**Dead Letter Exchange (DLX) is built in.** RabbitMQ natively routes undeliverable messages to a dead-letter exchange after a configurable retry limit, with no additional infrastructure or application-level machinery required [1]. This directly implements the Dead Letter Channel pattern (Hohpe & Woolf, 2003) that our reliability design depends on. Kafka has no native DLQ mechanism — dead-lettering requires an additional consumer and a separate topic, adding significant complexity. Azure Service Bus supports dead-lettering but only as a managed cloud service.

**Runs locally in Docker with no cloud dependency.** RabbitMQ ships as an official Docker image (`rabbitmq:3-management`) and starts in seconds. Our entire system — services, broker, and databases — runs with a single `docker compose up`. Kafka requires a ZooKeeper instance or KRaft configuration, making local setup meaningfully heavier. Azure Service Bus has no local emulator; development and testing require a live Azure subscription, introducing cloud lock-in and cost.

**Management UI out of the box.** The `rabbitmq:3-management` image exposes a web UI at `:15672` that shows queue depths, message rates, DLQ contents, and binding topology in real time. For a thesis demo and for chaos experiment observation, this is directly useful. Kafka's ecosystem tooling (Kafka UI, AKHQ) is third-party and requires separate setup.

**Appropriate scale for our workload.** Our experiment design targets 50–100 concurrent orders. The RabbitMQ Reliability Guide documents throughput in the tens of thousands of messages per second on modest hardware [1] — several orders of magnitude beyond our requirement. Kafka's advantages (partitioned log, high-throughput replay) are relevant at scales and use cases that do not apply here.

**Simpler operational model.** RabbitMQ's concepts — exchanges, queues, bindings, routing keys — map directly onto our event topology and onto the EIP patterns in our literature review. The AMQP client libraries for .NET (`RabbitMQ.Client`) are mature and well-documented. Kafka's partitioning model and consumer group semantics would add conceptual overhead without corresponding benefit.

## Trade-offs

**Lower maximum throughput than Kafka.** Kafka is designed for sustained high-throughput log ingestion and can handle millions of messages per second across a cluster. RabbitMQ's throughput is sufficient for our workload but would not scale to production event-streaming use cases. This is an accepted limitation given our scale.

**No native log replay.** Kafka retains messages as an immutable log; any consumer can replay from any offset at any time. RabbitMQ messages are consumed and acknowledged — once processed, they are gone (unless routed to the DLQ). We implement replay manually at the DLQ level: messages that exhaust retries are held in the DLQ for inspection and can be re-published. This satisfies our reliability requirements but is not equivalent to Kafka's full replay capability.

**Single broker, single point of failure.** Our Docker Compose setup runs one RabbitMQ node. RabbitMQ supports quorum queues for replication across a cluster, but we do not configure clustering. A broker restart will interrupt message flow until RabbitMQ recovers. We mitigate this with durable queues, persistent messages, and chaos scenario 5 (broker restart survival), which validates that services reconnect and resume correctly.

## Alternatives rejected

**Apache Kafka** — operationally heavier locally (requires ZooKeeper or KRaft), no built-in DLQ, consumer group model adds complexity not justified at our scale, and native log replay is not a system requirement.

**Azure Service Bus** — no local emulator, requires a live Azure subscription for any development or testing, introduces cloud lock-in incompatible with our goal of a fully self-contained Docker Compose environment.

## References
[1] VMware / Broadcom, *RabbitMQ Reliability Guide*, 2024. https://www.rabbitmq.com/docs/reliability
