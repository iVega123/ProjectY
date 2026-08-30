# AWS overlay placeholder

This directory is reserved for the AWS deployment variant. It will contain only
AWS-specific composition: managed-service endpoints, ingress and storage
classes, workload identity, secret-provider references, image registries, and
the selected cost profile. Canonical service contracts and dependency topology
remain under `deploy/base/`.

The AWS overlay will land with the AWS/Terraform epic. It must be developed in
the same repository tree and merged through short-lived task branches; it is
not a long-lived environment branch.
