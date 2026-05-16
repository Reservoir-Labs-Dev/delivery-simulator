# ADR-006: React over Blazor for the Dashboard Frontend

## Status
Accepted

## Context
We need a frontend for the real-time order dashboard. The two realistic options given our stack are React and Blazor, since we are already on .NET for the backend. We had to pick one and get a working project set up.

## Decision
We went with React.

## Rationale
Neither of us has used Blazor before and the deadline is July. Blazor Server ties the UI to a permanent .NET connection which felt like adding another failure point to a system that already has enough moving parts. Blazor WASM ships the entire .NET runtime to the browser which is overkill for a dashboard that is basically a table and some status indicators. React we already know, the `@microsoft/signalr` npm package works well with SignalR hubs, and we can get a working real-time connection in an afternoon rather than spending a week figuring out a new framework. Choosing the tool you know is a valid engineering decision when time is the constraint.

## Trade-offs
The dashboard ends up as a separate Node-based build instead of a unified .NET solution. That means two different runtimes in Docker Compose. Not a big deal for our setup but worth noting.

## Alternatives rejected
**Blazor Server** — requires a persistent server connection to render anything, adds complexity we don't need.
**Blazor WASM** — ships ~10 MB .NET runtime to the browser, SignalR integration is less straightforward than the JS client.
