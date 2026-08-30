# ADR 0002 — One command starts it, and saving a file costs seconds

- **Status:** Accepted
- **Date:** 2026-08-28

## Context

A repository with eighteen containers that takes fifteen minutes to start stops
being a demonstration — whoever cloned it closes the tab. The audited baseline
made this worse in three specific ways: startup ordering was a `sleep 20`, two
Dockerfiles had their `ENTRYPOINT` commented out and relied on the compose
`command`, and that command used `sh -c`, which puts a shell at PID 1 so
`SIGTERM` never reaches the application (finding M14).

## Decision

**Tilt orchestrates a Compose stack, gated on real health checks, with the
variants living as overlays rather than branches.**

- Two profiles: the default brings up the core; `-- --full` adds the rest.
- `depends_on` with `condition: service_healthy` replaces every `sleep`.
- Observability starts **before** the services, so the first traces are not lost.
- `live_update` syncs source and restarts in place, so a save costs seconds
  instead of a full image rebuild.
- `local_resource` owns migrations, schema creation and topic creation — setup
  steps that live in README prose are steps nobody runs.

Three invariants hold for all six images: multi-stage build leaving the
toolchain behind, a non-root user in the final image, and an exec-form
`ENTRYPOINT`.

Each service exposes three distinct probes. Liveness says the process responds;
readiness says it can serve; startup grants a grace period for slow boots.
Collapsing them is how a service gets killed while warming up, or kept in
rotation while it cannot serve. A tripped circuit breaker must **not** make a
service unready — it still serves everything not depending on the broken
upstream.

## Alternatives considered

- **Kubernetes from the start.** More faithful to production, and it adds a
  layer of debugging while more basic things are still broken. Kubernetes
  arrives later, on kind, once the manifests are worth testing.
- **Plain `docker compose up`.** No fast inner loop, no dependency graph, no
  buttons. The `live_update` path is most of the value.
- **Keeping the `sleep` and moving on.** It appears to work until the machine is
  slower than the person who wrote it.

## What was explicitly rejected

`sh -c` as the container command. It is what removes graceful shutdown, and it
is also why two images cannot run outside Compose. The build context is likewise
scoped per service rather than to the repository root — with the root as
context, `COPY . .` drags in the whole monorepo including `.git`, and the
per-service `.dockerignore` files never apply (finding B5).

## Consequences

- The first build is slow; subsequent loops are seconds. The README states the
  real first-run time rather than an optimistic one.
- The overlay structure exists from day one even with a single variant. Adding
  it later, with three consumers of the manifests, costs roughly ten times more.
- Compose is a development substrate, not production. Databases run insecure and
  without TLS, on an internal network, and the file says so at the top.

## Follow-up

- [Epic 4 — Local development loop](https://github.com/iVega123/ProjectY/issues/5)
- [Epic 10 — Local Kubernetes and signed admission](https://github.com/iVega123/ProjectY/issues/11)
