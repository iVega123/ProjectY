# ADR 0021: Next.js is the operations window and BFF

Status: Accepted. Issue: #76. Date: 2026-09-06.

## Workload and decision

React owns interactive map state, trace inspection and streamed batch progress.
Next.js Route Handlers provide the same-origin BFF and share TypeScript response
types with the client. Leaflet plots actual Phoenix position events on
OpenStreetMap tiles; it never synthesizes motion. Disconnects retain the last
position and show its increasing age. Sending device geolocation requires an
explicit action and browser permission.

Each create action sends a fresh W3C trace context through the Rust gateway.
The BFF queries Tempo for that trace, including later asynchronous spans. The
waterfall displays measured service/span durations, without fabricated hops.
Load batches stream individual HTTP results while fixed Prometheus queries
report queue depth, gateway 429s and rolling rental p99. Missing series show
unavailable, not zero. A batch contains 1–100 real rental attempts; the gateway
retains authoritative rate and concurrency limits. Use disposable test fleets.

## Identity boundary

The existing gateway requires asymmetric JWKS tokens; legacy AuthGate's login
still issues incompatible symmetric tokens. Identity migration remains #136.
Until a compatible provider is configured, the console accepts a gateway access
token, validates it by an authorized gateway read, then stores it in a HttpOnly,
SameSite=Strict cookie. TLS origins also set Secure. Tokens are never persisted
in browser storage or returned by session reads. Mutations require the configured
Origin. The BFF revalidates the session through the gateway on each operation.

Tracking tickets bind the validated subject and an active rental from that
subject's current page, expire after two minutes, and renew on reconnect.
Trace access requires a signed one-hour grant bound to the session subject and
the exact trace created by the BFF. Tempo and Prometheus URLs and metric queries
are server configuration, never arbitrary client targets. Only normalized span
names, services, timing and status are returned, not arbitrary span attributes.

## Alternatives and cost

A static SPA would require browser access and CORS/authentication for internal
observability endpoints. A separate BFF runtime adds another deployment and
duplicates types. Next.js keeps aggregation and interactive UI together, at the
cost of Node memory, a framework security/update cadence and server rendering
complexity. It is not a domain service. Map tiles and display fonts require
internet; position and trace data come only from the local stack.

## Operation

The normal polyglot overlay exposes the console on localhost:3001. The isolated
`scripts/Run-LoadTest.ps1 -PrepareOnly -Polyglot` profile exposes it on
localhost:13001 and telemetry on localhost:14000, reusing the repository's
test-only issuer and seeded fleet. This issuer is never enabled by the normal
application overlay. The advanced BFF/migration scope in #138 stays separate.
