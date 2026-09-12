# ProjectY infrastructure

Terraform is organised by capability and by blast radius:

- `modules/aws`: AWS implementations of portable capability contracts.
- `modules/local`: local implementations with the same contracts (introduced by
  `envs/local`).
- `envs/<environment>/{00-network,10-data,20-platform,30-workloads}`: independent
  state roots. A layer may read only the immediately preceding layer.

Environment values belong in `envs`, never in a module. Workloads receive DNS
endpoints and Kubernetes Secret names; cloud resource ARNs stay below the
workloads boundary.

See [the module contract](modules/CONTRACT.md) before adding another provider.
