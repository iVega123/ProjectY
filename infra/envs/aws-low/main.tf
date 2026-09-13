provider "aws" {
  region = var.aws_region

  default_tags {
    tags = merge(var.tags, {
      Environment = "aws-low"
      ManagedBy   = "terraform"
      Project     = "projecty"
    })
  }
}

data "aws_caller_identity" "current" {}
data "aws_partition" "current" {}

locals {
  name = "projecty-aws-low"
  profile = {
    kafka               = "single Apache Kafka container"
    transactional_store = "single CockroachDB container with a daily dump"
    rto                 = "hours"
    rpo                 = "24h"
    survives            = "nothing"
  }
}

module "network" {
  source = "../../modules/aws/network"

  name                 = local.name
  vpc_cidr             = "10.40.0.0/16"
  availability_zones   = var.availability_zones
  private_subnet_cidrs = ["10.40.0.0/20", "10.40.16.0/20"]
  public_subnet_cidrs  = ["10.40.128.0/24", "10.40.129.0/24"]
  nat_gateway_mode     = "single"
  tags                 = var.tags
}

module "kubernetes_cluster" {
  source = "../../modules/aws/kubernetes_cluster"

  name                = local.name
  kubernetes_version  = "1.35"
  subnet_ids          = module.network.network.private_subnet_ids
  node_instance_types = ["m7g.large"]
  node_capacity_type  = "ON_DEMAND"
  node_minimum        = 1
  node_desired        = 1
  node_maximum        = 2
  public_api          = false
  tags                = var.tags
}

module "daily_dump_store" {
  source = "../../modules/aws/object_store"

  name          = "projecty-${data.aws_caller_identity.current.account_id}-${var.aws_region}-low-dumps"
  force_destroy = true
  tags          = var.tags
}

module "backup_identity" {
  source = "../../modules/aws/service_identity"

  cluster_name = module.kubernetes_cluster.cluster.name
  namespace    = "projecty"
  services = {
    cockroach-backup = {
      statements = [{
        sid     = "WriteDailyDatabaseDump"
        reason  = "The scheduled backup job writes and verifies only the low-profile dump prefix."
        actions = ["s3:AbortMultipartUpload", "s3:GetObject", "s3:ListBucket", "s3:PutObject"]
        resources = [
          "arn:${data.aws_partition.current.partition}:s3:::${module.daily_dump_store.bucket.name}",
          "arn:${data.aws_partition.current.partition}:s3:::${module.daily_dump_store.bucket.name}/cockroach/*",
        ]
      }]
    }
    external-secrets-reader = {
      statements = [{
        sid       = "ReadProfileSecrets"
        reason    = "The External Secrets controller reads only low-profile runtime secret objects."
        actions   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue"]
        resources = ["arn:${data.aws_partition.current.partition}:secretsmanager:${var.aws_region}:${data.aws_caller_identity.current.account_id}:secret:projecty-aws-low-*"]
      }]
    }
  }
  tags = var.tags
}

resource "terraform_data" "profile_contract" {
  input = local.profile
}
