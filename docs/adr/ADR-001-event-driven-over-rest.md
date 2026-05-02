# ADR-001: Event-Driven Communication over REST

## Status
Accepted

## Context
Our system consists of 4 microservices that need to communicate with each other. To demonstrate fault-tolerance patterns meaningfully, services must be able to fail, slow down, or recover independently without bringing down the whole system.

## Decision
Services communicate exclusively via events through RabbitMQ topic exchange. No direct HTTP calls between services.

## Rationale
Event-driven communication creates loose coupling — services don't need to know about each other, only about the events they emit and consume. This enables independent deployability and makes chaos scenarios meaningful: you can drop, delay, or duplicate a message in a way that is impossible to replicate cleanly with HTTP calls. Fowler (2017) identifies event notification as the simplest and most effective pattern for decoupling services. Hohpe & Woolf (2003) provide the foundational patterns we implement — Dead Letter Channel and Idempotent Receiver — which only make sense in a message-driven system.

## Trade-offs
- Debugging is harder — no single place in code describes the full flow
- Consistency is eventual — services may temporarily have different views of the world
- More infrastructure required — RabbitMQ must be running and healthy

## References
- Hohpe & Woolf, *Enterprise Integration Patterns*, 2003
- Fowler, *What do you mean by event-driven*, 2017
