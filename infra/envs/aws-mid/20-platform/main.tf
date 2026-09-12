provider "aws" {
  region = var.aws_region

  default_tags {
    tags = merge(var.tags, {
      Environment = "aws-mid"
      ManagedBy   = "terraform"
      Project     = "projecty"
    })
  }
}

data "aws_partition" "current" {}

data "aws_caller_identity" "current" {}

data "terraform_remote_state" "data" {
  backend = "s3"
  config = {
    bucket       = var.state_bucket
    key          = "aws-mid/10-data/terraform.tfstate"
    region       = var.aws_region
    encrypt      = true
    use_lockfile = true
  }
}

locals {
  network = data.terraform_remote_state.data.outputs.network
}

module "kubernetes_cluster" {
  source = "../../../modules/aws/kubernetes_cluster"

  name                = "projecty-aws-mid"
  kubernetes_version  = "1.35"
  subnet_ids          = local.network.private_subnet_ids
  node_instance_types = ["m7g.large"]
  node_capacity_type  = "ON_DEMAND"
  node_minimum        = 3
  node_desired        = 3
  node_maximum        = 6
  public_api          = false
  tags                = var.tags
}

locals {
  object_bucket = data.terraform_remote_state.data.outputs.object_store.name
  connections   = data.terraform_remote_state.data.outputs.connections

  service_policies = {
    api-gateway = {
      statements = [{
        sid       = "ReadOwnCacheCredential"
        reason    = "The edge gateway authenticates only to its RESP rate-limit cache."
        actions   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue"]
        resources = ["arn:${data.aws_partition.current.partition}:secretsmanager:${var.aws_region}:${data.aws_caller_identity.current.account_id}:secret:${local.connections.cache.secret_reference}-??????"]
      }]
    }
    identity = {
      statements = [{
        sid       = "ManageIdentityDocuments"
        reason    = "Identity owns rider documents below its isolated object prefix."
        actions   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
        resources = ["arn:${data.aws_partition.current.partition}:s3:::${local.object_bucket}/identity/*"]
      }]
    }
    rental-core = {
      statements = [{
        sid       = "ReadOwnCommandCredential"
        reason    = "Rental core alone owns the AMQP command queues and their client credential."
        actions   = ["secretsmanager:DescribeSecret", "secretsmanager:GetSecretValue"]
        resources = ["arn:${data.aws_partition.current.partition}:secretsmanager:${var.aws_region}:${data.aws_caller_identity.current.account_id}:secret:${local.connections.command_bus.secret_reference}-??????"]
      }]
    }
    media-guard = {
      statements = [{
        sid       = "InspectUploadedMedia"
        reason    = "Media guard reads only untrusted uploads and writes only scan results."
        actions   = ["s3:GetObject", "s3:PutObject"]
        resources = ["arn:${data.aws_partition.current.partition}:s3:::${local.object_bucket}/media/*"]
      }]
    }
    billing = {
      statements = []
    }
    risk-pricing = {
      statements = [{
        sid       = "ReadPricingPolicy"
        reason    = "Risk pricing reads only the versioned policy document it evaluates."
        actions   = ["s3:GetObject"]
        resources = ["arn:${data.aws_partition.current.partition}:s3:::${local.object_bucket}/risk-pricing/*"]
      }]
    }
    telemetry = {
      statements = [{
        sid       = "OwnTrackingFacts"
        reason    = "Telemetry reads and writes only tables in its CQL tracking keyspace."
        actions   = ["cassandra:Select", "cassandra:Modify"]
        resources = ["arn:${data.aws_partition.current.partition}:cassandra:${var.aws_region}:${data.aws_caller_identity.current.account_id}:/keyspace/projecty_aws_mid/table/*"]
      }]
    }
    console = {
      statements = []
    }
  }
}

module "service_identity" {
  source = "../../../modules/aws/service_identity"

  cluster_name = module.kubernetes_cluster.cluster.name
  namespace    = "projecty"
  services     = local.service_policies
  tags         = var.tags
}
