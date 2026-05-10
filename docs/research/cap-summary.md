# CAP Theorem — Research Summary

Sources: Brewer, E., "Towards Robust Distributed Systems", PODC Keynote, 2000.
Gilbert, S. and Lynch, N., "Brewer's Conjecture and the Feasibility of Consistent, Available, Partition-Tolerant Web Services", ACM SIGACT News, vol. 33, no. 2, 2002.

---

## What the theorem says

Brewer proposed in his 2000 PODC keynote that a distributed system cannot simultaneously guarantee all three of the following properties:

- **Consistency (C)** — every read returns the most recent write or an error. All nodes see the same data at the same time.
- **Availability (A)** — every request receives a response (not an error), though it may not be the most recent data.
- **Partition tolerance (P)** — the system keeps operating even when network partitions drop or delay messages between nodes.

Gilbert and Lynch formalised this in 2002 using a proof by contradiction. They modelled a system with two nodes that cannot communicate during a partition. If you write to node 1, node 2 cannot see it. If the system must stay available, node 2 must respond to reads — but it will return stale data, violating consistency. If the system must stay consistent, node 2 must refuse to respond until it can sync with node 1 — violating availability. You can only have two of the three.

The important nuance Brewer added later (2012): partition tolerance is not really optional. Network partitions happen in any distributed system — hardware fails, cables are cut, packets are dropped. The real choice is what you do *when* a partition occurs: do you sacrifice consistency to stay available, or do you sacrifice availability to stay consistent?

---

## CP vs AP — the actual trade-off

**CP systems** (consistent + partition tolerant): when a partition occurs, the system refuses to serve requests it cannot answer correctly. Correct but potentially unavailable. Example: a banking transaction system — it's better to reject than to give a wrong balance.

**AP systems** (available + partition tolerant): when a partition occurs, the system keeps serving requests using whatever data it has, accepting that different nodes may temporarily disagree. Example: a social media feed, a shopping cart, an order pipeline.

---

## How this applies to our system

We are an AP system. We choose availability over consistency.

When a partition occurs between our services (say, PaymentService cannot reach KitchenService), we do not halt the pipeline. PaymentService publishes `payment.succeeded` to RabbitMQ, and KitchenService will consume it when it comes back online. During the partition, the `orders` schema and the `kitchen` schema will temporarily show different states for the same order — orders schema says `PAYMENT_SUCCEEDED`, kitchen schema has no record yet.

This is intentional and expected. The consistency we sacrifice is *immediate* consistency. What we accept in return is *eventual* consistency: all services will eventually process their events and converge on the same final state. The `processed_event_ids` table guarantees that when a service reconnects and processes a delayed event, it does so exactly once.

This trade-off is central to why our architecture works the way it does. An event-driven pipeline with RabbitMQ is inherently AP — the broker absorbs the partition by holding messages until consumers recover. The DLQ is the mechanism we use when eventual consistency is not enough: if a message fails permanently, we surface it for inspection rather than silently dropping it or blocking the pipeline.

In the thesis, the CAP theorem provides the theoretical grounding for why consistency is eventual in our system — it is not a design shortcut, it is a consequence of choosing availability and partition tolerance in a distributed setting.
