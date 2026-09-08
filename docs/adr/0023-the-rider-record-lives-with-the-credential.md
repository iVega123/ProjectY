# ADR 0023 — The rider record lives with the credential

- **Status:** Accepted
- **Date:** 2026-09-08
- **Supersedes:** the `rider-core` half of ADR 0012
- **Related findings:** B10, Contradição 03

## Context

ADR 0012 created two services where there had been none. `identity`, in Go, for
users, roles, credentials and token issuance. `rider-core`, in .NET, for the
rider's CNPJ, CNH number and type, date of birth, and the pointer to the object
`media-guard` stored. The stated reason for the split:

> Not folded into `identity`: credentials and regulatory documents have
> different lifecycles and different sensitivity. Merging them means the
> credential store also holds CNH numbers.

That reasoning is sound, and it was written about a system that did not exist.
In the system that does exist, `AuthGate.Model.RiderUser` extends
`ApplicationUser` and Entity Framework maps both to `AspNetUsers` — one table,
table-per-hierarchy. `CNPJ`, `CNHNumber`, `CNHType` and `DateOfBirth` are
columns beside `PasswordHash`. **The credential store already holds the CNH
numbers.** `rider-core` would not have preserved a separation; it would have
created one.

Meanwhile the backlog went the other way and never said so. Issue #136 is
titled "identity (Go) — token issuance, JWKS and the rider domain" and scopes
the rider half of `RiderManager` into `identity`; issue #138 places
`GET /riders?ids=…` in `identity` by name. `rider-core` has no issue, no
epic and no code. The record and the plan disagreed, and #136 is where code
gets written against one of them.

## Decision

**`identity` owns both. Seven services, not eight.**

The schema keeps them apart where it costs nothing:

- `users` — e-mail, password hash, name, type. What a login reads.
- `riders` — CNPJ, date of birth, CNH number and type, the object key, whether
  the document was verified. What the risk pipeline reads.

`riders.user_id` is primary key **and** foreign key. A rider has no identifier
of its own, which makes ADR 0012's "one identifier, everywhere" structural
rather than a convention: `rentals.rider_id`, `rider_positions.rider_id` and
the JWT `sub` are the same value because there is no second value to diverge
from. Finding B10 — `Rider.Id` and `Rider.UserId` used as if they were one key
— cannot regenerate against a schema with one key.

The file/record split from ADR 0012 survives whole: `media-guard` owns the
bytes, `identity` owns the row that says which rider, which object, which
expiry.

## Alternatives considered

- **Build `rider-core` as specified.** An eighth image, an eighth deployment,
  and a language boundary between a rider's e-mail and a rider's CNH. Every
  registration becomes either a synchronous call between two services or an
  event with an inbox on the far side, for data written once and read together.
  ADR 0012's own consequences section already names breadth as this project's
  main misreading risk; this is the cheapest place to stop adding to it.
- **Fold riders into `rental-core`.** Rejected in ADR 0012 and still rejected,
  for the reason given there: it puts CNPJ and CNH in the same store as rentals
  and money.
- **Keep the split as a schema, not a service** — `riders` under its own
  Postgres schema and its own role. Rejected as ceremony: the process holding
  the credentials would still hold the connection that reads the CNH numbers,
  so the separation would be visible in the DDL and absent in the threat model.

## What was explicitly rejected

The blast-radius argument, and it was a real argument. ADR 0012 closed audit
finding A2 by giving each service its own store and its own role, and this
record spends part of that. Whoever compromises the `identity` process reaches
the password hashes **and** the CNH numbers, and that is the price.

What is not given up: they are separate tables with separate grants, so the
day a reason appears to separate the processes, no data has to move. And in
this system the rider record is not independently valuable — `rider_id` **is**
the identity subject, so an attacker holding the credential store already
knows which rider is which.

Also rejected: leaving ADR 0012 unamended and writing the code the other way.
A record that the code contradicts is worse than no record, because the next
person trusts it.

## Consequences

- Issue #138's batch endpoint has an owner that exists.
- ADR 0012's service inventory reads seven, not eight. Its rider-core section
  stands as history; this record is the correction.
- `identity` becomes the service with the most sensitive table in the system,
  and its blast radius is the whole rider domain. Anything that widens its
  attack surface — a write endpoint, a new dependency — is a decision, not a
  detail.
- `RiderManager` has nothing left that another service does not own. Retiring
  it is the last step of #136, not a follow-up someone might forget.

## Follow-up

- ADR 0012 — the record this one corrects.
- ADR 0024 — the token shape that ships with the same service.
- #136 (identity), #138 (read composition), #73 (media-guard owns the file).
