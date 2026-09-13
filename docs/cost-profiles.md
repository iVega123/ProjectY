# AWS cost and recovery profiles

Prices were checked on **2026-09-13**. Estimates use 730 hours/month, Linux
On-Demand capacity, `us-east-1` public prices, USD, no free tier, commitments,
Savings Plans, support plan, credits, or tax. They are screening estimates, not
quotes: the AWS Pricing Calculator and a Cockroach Labs quote remain mandatory
before a real deployment.

## Promise first, bill second

| Profile | Fixed planning estimate | Kafka | Transactional store | RTO | RPO | Survives |
|---|---:|---|---|---|---|---|
| `aws-low` | **~$172.27/month** | single Apache Kafka container | single CockroachDB container; daily logical dump to S3 | hours | 24 hours | nothing |
| `aws-mid` | **~$1,143.31/month** | Strimzi, three brokers on EKS | CockroachDB Cloud `STANDARD`, 2 provisioned vCPUs, one region | minutes | ~5 minutes | one Availability Zone |
| `aws-high` | **~$5,856.13/month minimum** | two three-broker MSK provisioned clusters and MSK Replicator | CockroachDB Cloud `ADVANCED`, three nodes in each of three regions | seconds | ~0; bounded by Kafka replication lag | one AWS region |

`aws-high` is review-only and **must never be applied**. The estimate is enough
to explain that choice; spending the amount would not add evidence to this
repository. CI validates and mock-plans it without cloud credentials, while the
deployment workflow applies only `aws-mid` after environment review.

## Reproducible fixed-cost arithmetic

The estimates deliberately show multiplication instead of hiding the capacity
inside a calculator export.

### Low

| Item | Calculation | Monthly |
|---|---:|---:|
| EKS standard-support control plane | 1 × $0.10 × 730 | $73.00 |
| EC2 `m7g.large` | 1 × $0.0816 × 730 | $59.57 |
| NAT gateway | 1 × $0.045 × 730 | $32.85 |
| NAT public IPv4 | 1 × $0.005 × 730 | $3.65 |
| EKS node gp3 volume | 40 GiB × $0.08 | $3.20 |
| **Fixed subtotal** | | **$172.27** |

S3 dump bytes and requests are usage-based and excluded. A restore replaces the
single node/volumes, downloads the latest dump, and recreates the container data
plane; that is why this cheap profile promises hours and accepts up to 24 hours
of data loss.

### Mid

| Item | Calculation | Monthly |
|---|---:|---:|
| EKS standard-support control plane | 1 × $0.10 × 730 | $73.00 |
| EC2 `m7g.large` nodes | 3 × $0.0816 × 730 | $178.70 |
| NAT gateways | 3 × $0.045 × 730 | $98.55 |
| NAT public IPv4 | 3 × $0.005 × 730 | $10.95 |
| CockroachDB Standard minimum | $0.18 × 730 | $131.40 |
| Amazon MQ RabbitMQ `mq.m7g.large` cluster | 3 × $0.2734 × 730 | $598.75 |
| Amazon MQ broker storage | 45 GiB × $0.10 | $4.50 |
| ElastiCache Valkey `cache.t4g.small` | 2 × $0.0256 × 730 | $37.38 |
| EKS node gp3 volumes | 3 × 40 GiB × $0.08 | $9.60 |
| Strimzi broker gp3 volumes | 3 × 2 GiB × $0.08 | $0.48 |
| **Fixed subtotal** | | **$1,143.31** |

Strimzi keeps Kafka on the three EKS nodes already purchased and preserves the
portable Kafka protocol and operator model. MSK Serverless has a **$547.50/month
cluster floor** at 730 hours before partition, ingress, egress, or storage fees;
AWS's published 31-day example totals **$1,299.60**. The issue's earlier wording
that Serverless alone exceeded "the rest of Mid combined" is no longer literally true
after Mid gained a production-sized three-node Amazon MQ cluster and
CockroachDB Standard. The defensible current statement is narrower: Serverless
adds a large fixed floor, while Strimzi consumes capacity Mid already needs; the
published Serverless example exceeds this profile's entire fixed subtotal.

### High

| Item | Calculation | Monthly |
|---|---:|---:|
| EKS standard-support control planes | 2 × $0.10 × 730 | $146.00 |
| EC2 `m7g.large` nodes | 6 × $0.0816 × 730 | $357.41 |
| NAT gateways | 6 × $0.045 × 730 | $197.10 |
| NAT public IPv4 | 6 × $0.005 × 730 | $21.90 |
| MSK `kafka.m7g.large` brokers | 6 × $0.204 × 730 | $893.52 |
| MSK broker storage | 600 GiB × $0.10 | $60.00 |
| MSK Replicator fixed charge | 1 × $0.30 × 730 | $219.00 |
| CockroachDB Advanced planning minimum | 9 nodes × ($0.60 per 4-vCPU starting unit) × 730 | $3,942.00 |
| EKS node gp3 volumes | 6 × 40 GiB × $0.08 | $19.20 |
| **Fixed minimum** | | **$5,856.13** |

The Advanced number is an explicit linear planning inference from Cockroach
Labs' published 4-vCPU starting price, not a vendor quote. Region-specific AWS
prices can differ; the table normalizes both regions to `us-east-1` so reviewers
can compare topology changes. Replicator data processing ($0.08/GB), regional
transfer, NAT bytes, logs, metrics, backups, Cockroach storage, S3, Keyspaces,
load balancers, DNS, PrivateLink, and application traffic are excluded. At only
1 TiB/month of replicated Kafka data, Replicator processing alone adds $81.92,
before cross-region transfer.

## The second vendor is intentional

CockroachDB Cloud is not an AWS service. It runs on AWS and Mid reaches it over
PrivateLink, but it has a separate account, invoice, access model, support path,
and incident surface. Its cost is therefore shown as a separate line instead of
being disguised as AWS spend. What this buys is stronger portability: local and
managed profiles run the same CockroachDB engine over the PostgreSQL wire
protocol.

The cluster and SQL principals are still infrastructure as code through the
[official CockroachDB Terraform provider](https://registry.terraform.io/providers/cockroachdb/cockroach/latest/docs/resources/cluster).
The current public plan names are `Basic`, `Standard`, and `Advanced`; managed
ProjectY profiles intentionally use `STANDARD` and `ADVANCED` in provider
configuration.

## Pricing sources

- [CockroachDB pricing](https://www.cockroachlabs.com/pricing/) — current plan
  names and the Standard/Advanced starting rates.
- [Amazon EKS pricing](https://aws.amazon.com/eks/pricing/) — $0.10 per
  standard-support cluster-hour.
- [Amazon VPC pricing](https://aws.amazon.com/vpc/pricing/) — NAT gateway and
  public IPv4 hourly rates.
- [Amazon MSK pricing](https://aws.amazon.com/msk/pricing/) — provisioned broker,
  storage, Serverless example, and Replicator rates.
- [Amazon MQ pricing](https://aws.amazon.com/amazon-mq/pricing/) — current
  three-node `mq.m7g.large` RabbitMQ example and EBS rate.
- [Amazon ElastiCache pricing](https://aws.amazon.com/elasticache/pricing/) and
  [AWS public price list](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonElastiCache/current/us-east-1/index.json) — Valkey node rate.
- [Amazon EC2 public price list](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonEC2/current/us-east-1/index.json) — Linux `m7g.large` and gp3 rates.

Recheck every source and record a fresh date whenever capacity, region, or a
provider version changes.
