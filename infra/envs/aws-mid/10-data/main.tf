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
