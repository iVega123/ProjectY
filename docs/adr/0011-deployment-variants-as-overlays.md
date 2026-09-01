# ADR 0011: Keep deployment variants as overlays

## Status

Accepted on 2026-08-30.

## Context

ProjectY needs a self-hosted topology now and will later add AWS, other cloud
targets, and several cost profiles. Keeping each variant on a long-lived branch
would make every fix a repeated merge and allow the variants to drift until
some no longer build.

Article branches have a different purpose: they preserve an immutable citation
for a published article. Treating a deployment environment as that kind of
snapshot confuses archival history with active configuration.

## Decision

- `deploy/base/` owns the environment-neutral application model: image
  identities, service contracts, dependency order, health probes, networks,
  and persistent data requirements.
- `deploy/overlays/<variant>/` owns values and mechanics that change by target,
  including build contexts, host ports, bind mounts, restart policy, runtime
  mode, credentials wiring, and provider endpoints.
- Every runnable deployment selects an overlay. Tilt and manual local commands
  use `deploy/overlays/selfhost/compose.yaml`, never the base directly.
- New deployment variants are added as overlays in this tree and delivered by
  short-lived task branches through the normal GitFlow.
- Branches may be frozen only as immutable article citations. They are not a
  deployment-variant mechanism and never receive environment fixes.

## Consequences

- A fix to the canonical topology is made once and is immediately visible to
  every overlay.
- Environment-specific review is localized to the selected overlay, while the
  base can be reviewed without local ports, insecure runtime flags, or host
  paths.
- An overlay must be kept valid against the current base in CI; adding a new
  target adds validation, not a permanent merge lane.
- Compose 2.20 or newer is required because the self-hosted entrypoint uses the
  top-level `include` element to merge the base and its override.
