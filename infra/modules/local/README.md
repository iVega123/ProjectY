# Local capability implementations

These modules satisfy `infra/modules/CONTRACT.md`. LocalStack is used only for
AWS APIs available in its free image (S3, Secrets Manager and KMS). Protocol
engines are real upstream containers, and Kubernetes is a real kind cluster.

The shared `container_engine` module is private implementation machinery. Its
Docker IDs never cross a capability output.
