# AWS workload identity boundaries

EKS Pod Identity binds exactly one IAM role to each application ServiceAccount.
There are no access keys in Kubernetes Secrets or GitHub. The Pod Identity agent
injects short-lived container credentials through the AWS SDK default chain.

| ServiceAccount | Allowed AWS capability | Why |
|---|---|---|
| `api-gateway` | read its RESP credential | rate-limit state only |
| `identity` | read/write/delete `identity/` objects; read its SQL credential | rider documents and the identity database principal belong to identity |
| `rental-core` | read its AMQP and SQL credentials | rental commands and the rental database principal belong to rental core |
| `media-guard` | read/write `media/` objects | inspect uploads and publish results |
| `billing` | read its SQL credential | the billing database principal belongs to billing |
| `risk-pricing` | read `risk-pricing/` objects and its SQL credential | versioned policy input and the risk database principal only |
| `telemetry` | select/modify its Keyspaces tables | tracking facts belong to telemetry |
| `console` | none | the BFF reaches services over HTTP |

Every grant carries a reason in `20-platform/main.tf`. SQL users are distinct
CockroachDB principals; Pod Identity permits each workload to retrieve only its
own bootstrap secret. Empty policies are intentional proof that deploying on
AWS does not imply AWS API access.

## Negative authorization check

After `20-platform` and `30-workloads` are applied, run an allowed and denied
call from each ServiceAccount. A denied call must contain `AccessDenied`; a
timeout or DNS failure is not authorization evidence.

```bash
kubectl -n projecty run identity-denied --rm -i --restart=Never \
  --image=public.ecr.aws/aws-cli/aws-cli:2.31.30 \
  --overrides='{"spec":{"serviceAccountName":"identity"}}' -- \
  secretsmanager get-secret-value --secret-id projecty-aws-mid-cache/client

kubectl -n projecty run rental-core-allowed --rm -i --restart=Never \
  --image=public.ecr.aws/aws-cli/aws-cli:2.31.30 \
  --overrides='{"spec":{"serviceAccountName":"rental-core"}}' -- \
  secretsmanager describe-secret --secret-id projecty-aws-mid-commands/client
```

Repeat the matrix using the foreign target recorded beside each service in the
acceptance artifact. CloudTrail must show the role session tagged with both
`kubernetes-namespace=projecty` and the expected `kubernetes-service-account`.
