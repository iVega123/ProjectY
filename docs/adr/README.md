# Architecture decision records

Numbered, stable records of the decisions behind this repository. ADR 0000 is
the audit that started everything; every record after it traces back to one or
more of its findings.

Records are written from [`template.md`](template.md). The section most other
templates omit — *what was explicitly rejected* — is the one worth the most: it
is what separates a decision from a preference.

| # | Decision | Answers |
|---|---|---|
| [0000](0000-architecture-audit.md) | The audit is the baseline | Why a flawed system is preserved rather than rewritten |
| [0001](0001-polyglot-technology-choices.md) | Technology enters by workload, not by résumé | Why six languages and five stores, and which one would be cut first |
| [0002](0002-development-loop-and-containers.md) | One command starts it, and saving a file costs seconds | Why Tilt over Kubernetes-first, and the container invariants |
| [0003](0003-observability-and-fault-tolerance.md) | Failure behaviour is declared before it is implemented | Why the degradation table exists, and why rate limiting fails open while token verification fails closed |
| [0004](0004-cloud-portability-by-protocol.md) | Choose the protocol, not the vendor | Why DynamoDB, SQS and Cognito were refused, and why there is no `CloudProvider` interface |
| [0005](0005-repository-and-publication-strategy.md) | Variants are overlays; branches are citations | Why there is no branch per cloud, and how cost profiles are chosen |
| [0006](0006-secret-loading-and-jwt-boundaries.md) | Secret loading and JWT trust boundaries | How finding C3 was closed |
| [0007](0007-authenticated-queue-boundary.md) | Authenticate domain-writing queue messages | How finding C5 was closed |
| [0008](0008-single-trust-boundary.md) | One trust boundary, at the edge | Why domain services stop parsing tokens, and what is traded for it |
| [0009](0009-exactly-once-effect.md) | Exactly-once effect is layered and bounded | Which layer provides each guarantee, how it fails, and what is deliberately not promised |
| [0010](0010-motorcycle-retirement-protocol.md) | Serialize motorcycle retirement with rental claims | How retirement races rentals and how legacy orphan plates remain resolvable |
| [0011](0011-deployment-variants-as-overlays.md) | Keep deployment variants as overlays | What lives in the base, what lives in an overlay, and why not a branch |
| [0012](0012-identity-and-rider-domains.md) | The identity and rider domains get their own services | Where AuthGate and RiderManager go, why identity moves to Go, and which identifier is canonical |
| [0013](0013-asymmetric-token-signing.md) | Tokens are signed asymmetrically and validated from a JWKS | Why every validator currently holds a minting key, and what replaces it |
| [0014](0014-read-aggregation-at-the-bff.md) | Read aggregation belongs to the BFF | Why there is no GraphQL, and what would bring it back |
| [0015](0015-event-contracts-and-carried-state.md) | Event contracts: keys, compatibility and carried state | Why the partition key is never the licence plate, and why compatibility is FULL |
| [0016](0016-risk-scoring-off-the-request-path.md) | Risk scoring stays off the request path | Why a circuit breaker in front of fraud scoring is an attack surface |
| [0017](0017-session-lifetime-and-revocation.md) | Session lifetime, revocation and refresh tokens | Why revocation takes five minutes, and why Redis is never the source of truth |

## Reading order

Records 0000–0005 are the design trail and read in sequence. ADR 0008 belongs
to that trail but was written later, after review showed the trust-boundary
decision was implied everywhere and recorded nowhere; read it after 0001.
Records 0006, 0007, 0010 and 0011 are decisions taken while closing individual
findings or building a specific piece, and read on their own. ADR 0012 refines
0001 by separating technology role from service inventory; ADR 0013 supersedes
the signing half of 0006. Numbers are never reused or reassigned — 0011 was
renumbered once, from a 0008 that collided with an existing record.
ADR 0009 is the executable consistency contract and refines the shorter
outbox/inbox statement in ADR 0003.
ADR 0010 applies that contract to the cross-database motorcycle-retirement race.

The full findings list, with remediation status and the commit that closed each
one, lives in
[`../AUDITORIA-ARQUITETURA-SEGURANCA.md`](../AUDITORIA-ARQUITETURA-SEGURANCA.md).
It is a living document: findings are annotated when fixed, never deleted.
