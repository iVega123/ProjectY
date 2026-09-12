terraform {
  required_version = ">= 1.10.0, < 2.0.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = ">= 6.0.0, < 7.0.0"
    }
    random = {
      source  = "hashicorp/random"
      version = ">= 3.6.0, < 4.0.0"
    }
  }
}

provider "aws" {
  region                      = "us-east-1"
  skip_credentials_validation = true
  skip_metadata_api_check     = true
  skip_requesting_account_id  = true
}

locals {
  tags = {
    Environment = "contract-test"
    ManagedBy   = "terraform"
    Project     = "projecty"
  }
}

module "network" {
  source = "../../modules/aws/network"

  name                 = "projecty-contract"
  vpc_cidr             = "10.42.0.0/16"
  availability_zones   = ["us-east-1a", "us-east-1b"]
  private_subnet_cidrs = ["10.42.0.0/20", "10.42.16.0/20"]
  public_subnet_cidrs  = ["10.42.128.0/24", "10.42.129.0/24"]
  tags                 = local.tags
}

module "transactional_store" {
  source = "../../modules/aws/transactional_store"

  name                = "projecty-contract"
  database_name       = "projecty"
  subnet_ids          = ["subnet-00000000000000001", "subnet-00000000000000002"]
  security_group_ids  = ["sg-00000000000000001"]
  instance_class      = "db.t4g.micro"
  deletion_protection = true
  skip_final_snapshot = false
  tags                = local.tags
}

module "event_bus" {
  source = "../../modules/aws/event_bus"

  name                 = "projecty-contract"
  subnet_ids           = ["subnet-00000000000000001", "subnet-00000000000000002", "subnet-00000000000000003"]
  security_group_ids   = ["sg-00000000000000001"]
  kafka_version        = "3.9.x"
  broker_instance_type = "kafka.m7g.large"
  tags                 = local.tags
}

module "command_bus" {
  source = "../../modules/aws/command_bus"

  name               = "projecty-contract"
  subnet_ids         = ["subnet-00000000000000001"]
  security_group_ids = ["sg-00000000000000001"]
  host_instance_type = "mq.t3.micro"
  engine_version     = "4.1"
  tags               = local.tags
}

module "cache" {
  source = "../../modules/aws/cache"

  name               = "projecty-contract"
  subnet_ids         = ["subnet-00000000000000001", "subnet-00000000000000002"]
  security_group_ids = ["sg-00000000000000001"]
  node_type          = "cache.t4g.micro"
  tags               = local.tags
}

module "time_series_store" {
  source = "../../modules/aws/time_series_store"

  name   = "projecty_contract"
  region = "us-east-1"
  tags   = local.tags
}

module "object_store" {
  source = "../../modules/aws/object_store"

  name = "projecty-contract-example"
  tags = local.tags
}

module "kubernetes_cluster" {
  source = "../../modules/aws/kubernetes_cluster"

  name                = "projecty-contract"
  kubernetes_version  = "1.35"
  subnet_ids          = ["subnet-00000000000000001", "subnet-00000000000000002"]
  node_instance_types = ["m7g.large"]
  tags                = local.tags
}

