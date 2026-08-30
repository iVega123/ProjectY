# ADR 0005 — Variants are overlays; branches are citations

- **Status:** Accepted
- **Date:** 2026-08-29

## Context

The project needs several deployment variants — self-hosted, AWS, other clouds
later, and three cost profiles — and it also feeds a public article series where
readers want to clone and follow along.

The obvious shape is a branch per variant. It is also how a portfolio repository
dies: every fix travels five times, and within two months three branches no
longer build. A repository with broken branches communicates the opposite of the
intent.

## Decision

**Variants live as overlays on `main`. Branches exist only as frozen
citations.**

```
main                          # everything, always green
├── services/                 # exactly one copy of the code
├── deploy/
│   ├── base/
│   └── overlays/{selfhost,aws,gcp}
├── infra/envs/{local,aws-low,aws-mid,aws-high}
└── docs/adr/

article/01-audit              # cut at publication, never maintained
article/02-polyglot
```

The README states the rule in one sentence, so nobody opens a pull request
against a frozen branch, and no fix is ever cherry-picked into one.

**Cost profiles are chosen by promise, not by budget.** The organising rule: you
pick a profile by the recovery time and data loss you are willing to promise,
and the budget then tells you whether the promise is payable.

| | Low | Mid | High |
|---|---|---|---|
| Cost | ~$40/mo | ~$300/mo | ~$2,500+/mo |
| Kafka | container | Strimzi on cluster | MSK provisioned |
| RTO / RPO | hours / 24h | minutes / ~5min | seconds / ~0 |
| Survives | nothing | an availability zone | a region |

The two axes — substrate and cost profile — are independent, and the matrix is
deliberately not filled: five cells, not nine. `aws-high` exists as reviewable
Terraform and is never applied.

**AWS is ephemeral by design.** The always-on demonstration is the self-hosted
stack; the cloud is `apply`, record, `destroy`. This is not a budget excuse — it
is the evidence: if something does not come back after a destroy, it was created
by hand and nobody noticed.

## Alternatives considered

- **A branch per variant.** Gives the reader a clean clone target, at the cost
  of the maintenance tax above. The frozen article branches recover the reader
  experience without the tax.
- **A separate repository per cloud.** Same drift, more of it, and the shared
  application code has to be vendored or published as packages.
- **Keeping AWS running.** Roughly $850/month with MSK Serverless alone
  outweighing everything else. Prohibitive, and it would make the portfolio
  depend on a recurring bill.

## What was explicitly rejected

Filling the whole substrate × cost matrix. Cells that teach nothing are work
without a reader.

## Consequences

- The repository is in English — README, ADRs, commit messages, code comments —
  because the target is international roles and technical writing is an evaluated
  part of the application.
- Every fix lands once. The overlay structure must exist before there are
  several consumers of the manifests, or retrofitting it becomes expensive.
- The article series is the closest available proxy for a multiplier effect that
  a solo repository cannot demonstrate on its own.

## Follow-up

- [Epic 12 — Article series](https://github.com/iVega123/ProjectY/issues/13)
- [Freeze one article branch per part](https://github.com/iVega123/ProjectY/issues/94)
- [Three cost profiles](https://github.com/iVega123/ProjectY/issues/86)
