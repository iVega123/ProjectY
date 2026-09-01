# ADR 0010 — Motorcycle retirement is serialized with rental claims

- **Status:** Accepted
- **Date:** 2026-09-01
- **Deciders:** ProjectY maintainers

## Context

MotoHub and RentalOperations use different databases, so checking for a rental
and then deleting a motorcycle cannot be one local transaction. A rental could
previously be inserted between the check and the physical delete, leaving a
plate that no longer resolved in MotoHub.

## Decision

RentalOperations is the serialization authority for a motorcycle's rentable
state. The `MotorcycleClaims` MongoDB collection has one document per licence
plate. Rental creation atomically inserts an `ActiveRental` claim; retirement
atomically inserts a `Retired` claim. MongoDB's unique `_id` constraint means
only one can win. The losing operation returns `409 Conflict`.

After acquiring a retirement claim, MotoHub sets `RetiredAtUtc` and
`RetirementReason` instead of deleting the row. Retired motorcycles are omitted
from the collection endpoint but remain available by plate for historical
rentals. A retirement claim is intentionally retained when the MotoHub write
has an ambiguous failure: a retry can finish the soft delete, while releasing
the claim could admit a rental after retirement had committed.

On upgrade, the Mongo initializer creates claims for active legacy rentals. It
removes rental claims older than five minutes only after confirming that their
owned rental is absent or no longer active. This repairs ambiguous failed
inserts without deleting a live rental's protection. It
also sends every historical rental plate to MotoHub. A plate whose physical
motorcycle row was already deleted is recreated once as a retired placeholder
with reason `LegacyOrphanBackfill`; unavailable metadata is recorded as such.
This deliberately preserves referential resolution instead of inventing the
lost model and year. RentalOperations does not become ready if a non-empty
backfill cannot reach MotoHub.

## Consequences

- A concurrent rental and retirement have one MongoDB arbitration point.
- Completing a rental removes only the claim owned by that rental.
- Licence plates are never reused after retirement, preserving historical
  identity.
- A failed retirement can temporarily fail closed until the delete is retried.
- Startup reconciliation is a cross-service data migration, so Compose starts
  MotoHub before RentalOperations.

## Alternatives considered

- **Check MotoHub before inserting a rental.** This remains a stale read and
  cannot close the cross-database race.
- **MongoDB multi-document transactions.** The supported standalone MongoDB
  deployment does not provide a replica-set transaction boundary.
- **Release the retirement claim on every MotoHub error.** A timeout can hide a
  committed soft delete, so releasing would re-open the orphan race.
- **Drop legacy orphan rentals.** Financial and rider history is more valuable
  than incomplete motorcycle metadata, so a marked placeholder is retained.

## Traceability

- [Issue #97 — M12 soft-delete motorcycles](https://github.com/iVega123/ProjectY/issues/97)
- Audit finding M12.
