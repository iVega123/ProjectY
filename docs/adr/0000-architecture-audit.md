# ADR 0000 — The audit is the baseline

- **Status:** Accepted
- **Date:** 2026-08-26

## Context

This repository began as four ASP.NET Core services for motorcycle rentals,
written as a backend challenge. Rather than redesign from taste, the starting
point was a full manual review of the source, the container topology, the
database schema, and the delivery pipeline.

The review found **42 issues**: 5 critical security flaws, 11 high-risk
findings, 14 architectural gaps, and 12 delivery or code-quality problems. The
headline is not the count. It is that the service boundaries were reasonable on
paper while the system had **no effective perimeter**: authorization was
reimplemented by copy-paste in four services, the copies had already diverged,
the signing key was shared and committed, and every backing store was published
on the host with example credentials.

## Decision

The audit is ADR zero. Every subsequent decision record traces back to one or
more of its findings, and every remediation commit names the finding it closes.
The audit document is kept as a **living remediation record** — findings are not
deleted when fixed, they are annotated with status and the commit that closed
them.

The full evidence, impact and source locations live in
[`docs/AUDITORIA-ARQUITETURA-SEGURANCA.md`](../AUDITORIA-ARQUITETURA-SEGURANCA.md).
That document is deliberately not duplicated here: a copy of a maintained
document diverges from it on the first edit.

## Alternatives considered

- **Rewrite from scratch, skip the audit.** Faster to start, and it discards the
  most valuable artifact this repository has. A greenfield rewrite demonstrates
  building; migrating a system with known defects while it stays up demonstrates
  judgement under constraint.
- **Fix silently, present only the finished state.** This is what most portfolio
  repositories do. It removes the only part of the story that is hard to
  fabricate — a public account of what your own code got wrong.
- **Audit, but keep it private.** Same loss, less honesty.

## What was explicitly rejected

Presenting the redesign without the baseline. The redesign is only legible as
competence because the problems it answers are documented, specific, and
attributable to real files and line numbers.

## Consequences

- The repository preserves a deliberately flawed baseline. It must never be
  presented as safe to run outside an isolated environment, and the README says
  so.
- Every epic and task references the findings it closes, so coverage is
  auditable: a finding without an owning issue is a gap, not a decision.
- The correction order is fixed by dependency rather than by severity — closing
  the open entry points, then externalizing secrets, then establishing one trust
  boundary, then making messaging durable, then removing the duplicated code.

## Follow-up

- [Epic 2 — Critical audit fixes](https://github.com/iVega123/ProjectY/issues/3)
- ADRs 0001–0005 carry the redesign decisions.
