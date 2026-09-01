# ADR 0012 — The identity and rider domains get their own services

- **Status:** Accepted
- **Date:** 2026-09-01
- **Related findings:** B10
- **Refines:** ADR 0001

## Context

ADR 0001 lists **technology roles** — which runtime serves which kind of work.
That table then became the service inventory in ADR 0002's image list and in the
cloud topology, without anyone deciding it should. Six roles, six images.

Two legacy domains never got a row. The strangler migration maps `moto-hub` and
`rental-operations` onto `rental-core`, which matches
`deploy/db/cockroach/001_init.sql` exactly — `motorcycles` and `rentals`, and
nothing else. `auth-gate` and `rider-manager` had no target at all.

The rest of the design already depends on both existing:

- ADR 0006 and ADR 0008 name AuthGate the sole token issuer.
- `rentals.rider_id STRING NOT NULL` is a reference with no owning service.
- Cassandra partitions `rider_positions` by `rider_id`.
- `risk-pricing` cross-checks an OCR'd CNH number against what the rider
  registered — a record that no service owns.
- `media-guard` owns the CNH *file*; nothing owns the *record* that links a
  rider to the stored object.

## Decision

**Eight services, not six.** `identity` and `rider-core` join the target
architecture.

### identity — Go

Users, roles, credentials and token issuance. Its own database.

Go is adopted here on **ecosystem grounds, not workload** — and this record says
so plainly rather than inventing a workload argument after the fact. Identity's
work is CRUD, password hashing and token minting, all on the cold path: login
happens once per session, and the gateway does the per-request verification.
There is no latency or concurrency case, which is exactly what every other row
of ADR 0001 rests on.

What Go does bring: it is the lingua franca of identity infrastructure, with the
strongest OIDC and OAuth2 library ecosystem of any option here, and it is the one
major backend language absent from this stack.

Naming the different basis keeps ADR 0001's rule intact. Bending the rule
silently would corrupt the thing that makes that record credible.

### rider-core — .NET

Rider records: CNPJ, CNH number and type, date of birth, and the pointer to the
object `media-guard` stored. Its own database.

Not folded into `rental-core`: that merges two bounded contexts, and — the
stronger reason — it puts CNPJ and CNH in the same store as rentals and money.
A separate store with its own IAM role keeps the blast radius small, which is
the same argument that closed finding A2.

Not folded into `identity`: credentials and regulatory documents have different
lifecycles and different sensitivity. Merging them means the credential store
also holds CNH numbers.

### The file/record split

`media-guard` owns the **file** pipeline — validate the real bytes, strip EXIF,
re-encode, store, issue access. `rider-core` owns the **record** — which rider,
which object key, which expiry. Both earlier documents left this ambiguous and
implied `media-guard` did both.

### One identifier, everywhere

`rider_id` appears in three places in the target design: `rentals.rider_id`,
Cassandra's `rider_positions.rider_id`, and the JWT `sub`. **These are the same
value.** The identity subject is the rider identifier; `rider-core` keys its
records on it and mints no surrogate of its own.

Finding B10 was `Rider.Id` and `Rider.UserId` used as if they were one key, which
threw `ArgumentException("Rider not found")` on the image path. Task #99
reconciled the legacy usage, but the model still carries both fields and no
record said which one is canonical. Without this decision the same bug
regenerates across service boundaries, where it is far harder to catch than
inside one class.

## Alternatives considered

- **Keep identity in .NET.** The cheaper and workload-justified option, and the
  one originally recommended: C1, C2, C3 and ADR 0007's signed queue envelope
  were all earned inside AuthGate, and a rewrite means earning them again.
  Overridden deliberately to widen ecosystem coverage; the cost is accepted, not
  overlooked.
- **Fold identity into the gateway.** Rejected: ADR 0008 mitigates the gateway
  being a single point of failure for authentication precisely by it holding no
  state. A user database destroys that property.
- **Cognito or another managed identity provider.** Rejected in ADR 0004 as a
  one-way door, and because authentication is part of what this repository sets
  out to demonstrate.
- **Rust, Elixir, Python or Node for identity.** Each has a workload argument in
  ADR 0001 that identity does not match — hostile input at high throughput, long
  lived connections, statistical work, I/O fan-out with shared types.

## What was explicitly rejected

Retrofitting a workload justification for Go. It is an ecosystem choice, and the
record states it as one.

`rider-core` minting its own surrogate key for riders.

## Consequences

- **Seven languages on a solo repository.** ADR 0005 and the evidence review
  already name breadth as the main misreading risk for this project. This makes
  it worse, and the countermeasure does not change: the deep column stays
  distributed consistency, which identity does not feed.
- **Password migration is the real work, not the rewrite.** AuthGate stores
  ASP.NET Identity PBKDF2 hashes. The Go service must verify that format on
  first login and re-hash to Argon2id on success, or every existing user is
  locked out. Dual-verify is the migration path; a forced reset is the fallback,
  and it is worse.
- **ADR 0007's signed queue envelope is issued by AuthGate today.** The Go
  service must reproduce it byte-for-byte or the authenticated queue boundary
  regresses and finding C5 reopens.
- Two more databases, two more IAM roles, two more images in the supply chain.

## Follow-up

- ADR 0013 — asymmetric signing, shipped with this move rather than after it.
- Epic and tasks for the two services, the schemas and the identifier migration.
