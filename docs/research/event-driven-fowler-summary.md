# Event-Driven Patterns — Fowler (2017)

Fowler argues that "event-driven" is an overloaded term covering four distinct patterns, each with different tradeoffs.

## 1. Event Notification

A system sends events to inform other systems that something has changed. The sender usually does not expect a direct response. This creates **low coupling** and is easy to implement.

However, if multiple systems depend on chains of notifications, the overall workflow becomes hard to understand, debug, and maintain. Fowler warns against using events as hidden commands, where a sender expects an action but disguises it as an event.

## 2. Event-Carried State Transfer

Here, events contain enough data for receiving systems to update their own local copies of information. For example, customer updates can be sent so other services don’t need to query the main customer database.

Benefits include:

- Better resilience if the source system is unavailable
- Lower latency
- Reduced load on the main system

The downside is increased complexity and duplicated data.

## 3. Event Sourcing

Instead of storing only current state, every change is stored as an event. Current system state can always be rebuilt by replaying the event history.

Benefits:

- Strong audit trail
- Ability to recreate past states
- Ability to test hypothetical scenarios
- Flexible derived views of data

Challenges:

- Complex implementation
- Handling schema changes over time
- Difficulties when external systems are involved

## 4. CQRS (Command Query Responsibility Segregation)

CQRS separates write operations (commands) from read operations (queries), often using different models for each. It is not inherently event-driven, but is often combined with event-based patterns.

It can simplify complex systems with many reads and fewer writes, but also adds architectural complexity.

## Relevance to Our System

We use Event Notification as the primary pattern — services emit domain events to RabbitMQ and don't wait for a response. We also apply light Event-Carried State Transfer — events carry enough payload for consumers to act without calling back to the source.
