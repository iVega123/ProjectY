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

## Reading order

Records 0000–0005 are the design trail and read in sequence. Records 0006 and
onward are remediation decisions taken while closing individual findings, and
read on their own.

The full findings list, with remediation status and the commit that closed each
one, lives in
[`../AUDITORIA-ARQUITETURA-SEGURANCA.md`](../AUDITORIA-ARQUITETURA-SEGURANCA.md).
It is a living document: findings are annotated when fixed, never deleted.
