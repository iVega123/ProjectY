# CockroachDB Cloud capability implementation

The transactional store uses the official `cockroachdb/cockroach` provider.
Managed profiles use the current CockroachDB Cloud plan names `STANDARD` and
`ADVANCED`; the former `Serverless` and `Dedicated` product names must not be
used as tiers.

The module creates one SQL principal per owning application service. Credential
material is returned only as a sensitive bootstrap value so the environment can
persist it in its own secret provider. PrivateLink acceptance remains in the
environment because the AWS VPC endpoint belongs to the network state.
