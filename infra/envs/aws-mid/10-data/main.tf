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

provider "cockroach" {}

data "terraform_remote_state" "network" {
  backend = "s3"
  config = {
    bucket       = var.state_bucket
    key          = "aws-mid/00-network/terraform.tfstate"
    region       = var.aws_region
    encrypt      = true
    use_lockfile = true
  }
}

data "aws_caller_identity" "current" {}

locals {
  network = data.terraform_remote_state.network.outputs.network
  name    = "projecty-aws-mid"
}

module "transactional_store" {
  source = "../../../modules/cockroach/transactional_store"

  name                     = "${local.name}-sql"
  plan                     = "STANDARD"
  regions                  = [{ name = var.aws_region }]
  service_principals       = ["billing", "identity", "rental-core", "risk-pricing"]
  standard_vcpus           = 2
  backup_frequency_minutes = 5
  backup_retention_days    = 30
  delete_protection        = true
  labels = merge(var.tags, {
    environment = "aws-mid"
    managed-by  = "terraform"
    project     = "projecty"
  })
}

resource "aws_security_group" "transactional_store" {
  name        = "${local.name}-transactional-store"
  description = "CockroachDB PostgreSQL wire through PrivateLink"
  vpc_id      = local.network.id

  ingress {
    description = "PostgreSQL wire from the ProjectY VPC"
    from_port   = 26257
    to_port     = 26257
    protocol    = "tcp"
    cidr_blocks = [local.network.cidr]
  }
}

resource "aws_vpc_endpoint" "transactional_store" {
  vpc_id              = local.network.id
  service_name        = module.transactional_store.private_endpoint_services[var.aws_region]
  vpc_endpoint_type   = "Interface"
  subnet_ids          = local.network.private_subnet_ids
  security_group_ids  = [aws_security_group.transactional_store.id]
  private_dns_enabled = false

  tags = merge(var.tags, { Name = "${local.name}-transactional-store" })
}

resource "cockroach_private_endpoint_connection" "transactional_store" {
  cluster_id  = module.transactional_store.private_endpoint_cluster_id
  endpoint_id = aws_vpc_endpoint.transactional_store.id
}

resource "aws_secretsmanager_secret" "database_principal" {
  for_each = module.transactional_store.service_secret_references

  #checkov:skip=CKV_AWS_149:The profile secret KMS key is composed when customer-managed key ownership is enabled.
  #checkov:skip=CKV2_AWS_57:Rotation requires a coordinated SQL password rollout and is owned by the workload release transaction.
  name                    = each.value
  description             = "CockroachDB SQL principal owned by ${each.key}."
  recovery_window_in_days = 0
  tags                    = var.tags
}

resource "aws_secretsmanager_secret_version" "database_principal" {
  for_each = aws_secretsmanager_secret.database_principal

  secret_id     = each.value.id
  secret_string = jsonencode(module.transactional_store.service_secrets[each.key])
}

resource "terraform_data" "data_loss_guard" {
  input = "Removing the data layer requires explicit, capability-targeted teardown."

  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_security_group" "cache" {
  name        = "${local.name}-cache"
  description = "RESP over TLS from the ProjectY VPC"
  vpc_id      = local.network.id

  ingress {
    description = "RESP TLS"
    from_port   = 6379
    to_port     = 6379
    protocol    = "tcp"
    cidr_blocks = [local.network.cidr]
  }

  egress {
    description = "Return traffic within the VPC"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = [local.network.cidr]
  }
}

resource "aws_security_group" "command_bus" {
  name        = "${local.name}-command-bus"
  description = "AMQP over TLS from the ProjectY VPC"
  vpc_id      = local.network.id

  ingress {
    description = "AMQP TLS"
    from_port   = 5671
    to_port     = 5671
    protocol    = "tcp"
    cidr_blocks = [local.network.cidr]
  }

  egress {
    description = "Return traffic within the VPC"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = [local.network.cidr]
  }
}

module "cache" {
  source = "../../../modules/aws/cache"

  name                        = "${local.name}-cache"
  subnet_ids                  = local.network.private_subnet_ids
  security_group_ids          = [aws_security_group.cache.id]
  node_type                   = "cache.t4g.small"
  replica_count               = 1
  secret_recovery_window_days = 0
  tags                        = var.tags
}

module "command_bus" {
  source = "../../../modules/aws/command_bus"

  name                        = "${local.name}-commands"
  subnet_ids                  = local.network.private_subnet_ids
  security_group_ids          = [aws_security_group.command_bus.id]
  host_instance_type          = "mq.m7g.large"
  engine_version              = "4.1"
  high_availability           = true
  secret_recovery_window_days = 0
  tags                        = var.tags
}

module "time_series_store" {
  source = "../../../modules/aws/time_series_store"

  name   = "projecty_aws_mid"
  region = var.aws_region
  tags   = var.tags
}

module "object_store" {
  source = "../../../modules/aws/object_store"

  name          = "projecty-${data.aws_caller_identity.current.account_id}-${var.aws_region}-aws-mid"
  force_destroy = true
  tags          = var.tags
}
