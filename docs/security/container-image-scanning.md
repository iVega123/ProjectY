# Container image SBOM and vulnerability gate

The root CI workflow builds every changed service that currently has a Dockerfile:
AuthGate, MotoHub, RentalOperations, and RiderManager. A change to the CI workflow
builds all four. References in `deploy/overlays/selfhost/compose.yaml` whose
build contexts do not yet exist are outside this gate until their Dockerfiles
are added.

Each current image uses the repository root as its build context, and the root
`.dockerignore` limits that context to the four application projects and `Shared/`.
This makes the Dockerfiles self-contained for Visual Studio and command-line builds
without sending `.git/` or local secrets. The equivalent standalone commands are:

```bash
docker build -f AuthGate/AuthGate/Dockerfile -t projecty/auth-gate .
docker build -f MotoHub/MotoHub/Dockerfile -t projecty/moto-hub .
docker build -f RentalOperations/RentalOperations/Dockerfile -t projecty/rental-operations .
docker build -f RiderManager/RiderManager/Dockerfile -t projecty/rider-manager .
```

Local Linux/amd64 measurements after the chiseled-runtime rewrite:

| Image | Local image size |
|---|---:|
| AuthGate | 67.1 MiB |
| MotoHub | 65.7 MiB |
| RentalOperations | 80.0 MiB |
| RiderManager | 66.4 MiB |

All four final images run as the built-in non-root `app` user and declare an
exec-form `ENTRYPOINT`; Compose supplies no application command.

For each image, CI:

1. builds a Linux/amd64 OCI layout with a BuildKit SPDX SBOM attestation;
2. verifies that the OCI layout contains that attestation;
3. uses Syft to produce a standalone SPDX JSON SBOM;
4. has Grype scan that SBOM and fail on any critical vulnerability, fixed or unfixed;
5. has Trivy independently scan the OCI image and apply the same critical threshold;
6. uploads the standalone SBOM and attested OCI layout for 14 days as the
   `image-security-<image>-<commit>` workflow artifact.

The SBOM can therefore be downloaded without a registry, while the OCI layout keeps
the build-time attestation attached to the image. On a push to `main`, CI publishes
that exact scanned OCI digest to GHCR, attaches GitHub SLSA build provenance, signs
it keylessly with Cosign through GitHub OIDC, and verifies both before the required
CI job can pass. Pull requests and non-default branches build and scan but cannot
publish packages or request signing identities.

## Vulnerability exceptions

An exception is a time-boxed risk acceptance, not a permanent allowlist. Only the
repository owner, `@iVega123`, may approve one. The approval must be recorded in a
dedicated pull request linked to an issue containing:

- the vulnerability ID, affected image and exact package/PURL;
- why the finding is not exploitable or cannot yet be remediated;
- compensating controls and a named remediation owner;
- an expiry date no more than 30 days after approval.

The pull request adds the narrowest equivalent rule to both `.grype.yaml` and
`.trivyignore.yaml`. A Trivy rule must include `id`, `purls`, `statement`, and
`expired_at`; the matching Grype rule must include the vulnerability and package
name/version. Broad package-only rules and waivers without an expiry are rejected.
When the date expires, Trivy stops suppressing the finding and CI blocks again. The
remediation owner must remove both entries when the vulnerability is fixed or the
exception expires.

Example (documentation only):

```yaml
# .trivyignore.yaml
vulnerabilities:
  - id: CVE-YYYY-NNNN
    purls:
      - pkg:deb/debian/example@1.0.0
    statement: "Approved in #123 until the base image is patched; owner: @name."
    expired_at: 2026-09-15

# .grype.yaml
ignore:
  - vulnerability: CVE-YYYY-NNNN
    package:
      name: example
      version: 1.0.0
```

Emergency waivers use the same pull request and metadata. They may be merged by the
repository owner before a second review, but expire after at most 72 hours.
