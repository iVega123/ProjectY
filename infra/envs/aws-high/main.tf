provider "aws" {
  alias  = "primary"
  region = var.primary_region

  default_tags {
    tags = merge(var.tags, {
      Environment = "aws-high"
      ManagedBy   = "terraform"
      Project     = "projecty"
      RegionRole  = "primary"
    })
  }
}

provider "aws" {
  alias  = "secondary"
  region = var.secondary_region

  default_tags {
    tags = merge(var.tags, {
      Environment = "aws-high"
      ManagedBy   = "terraform"
      Project     = "projecty"
      RegionRole  = "secondary"
    })
  }
}

provider "cockroach" {}

data "aws_caller_identity" "current" {
  provider = aws.primary
}

data "aws_partition" "current" {
  provider = aws.primary
}

locals {
  name = "projecty-aws-high"
  service_principals = [
    "billing",
    "identity",
    "rental-core",
    "risk-pricing",
  ]
  profile = {
    kafka               = "MSK provisioned in two regions with MSK Replicator"
    transactional_store = "CockroachDB Cloud Advanced across three regions"
    rto                 = "seconds"
    rpo                 = "approximately zero"
    survives            = "one AWS region"
    execution_policy    = "review-only; never apply"
  }
}

module "network_primary" {
  source    = "../../modules/aws/network"
  providers = { aws = aws.primary }

  name                 = "${local.name}-use1"
  vpc_cidr             = "10.50.0.0/16"
  availability_zones   = var.primary_availability_zones
  private_subnet_cidrs = ["10.50.0.0/20", "10.50.16.0/20", "10.50.32.0/20"]
  public_subnet_cidrs  = ["10.50.128.0/24", "10.50.129.0/24", "10.50.130.0/24"]
  nat_gateway_mode     = "one_per_az"
  tags                 = var.tags
}

module "network_secondary" {
  source    = "../../modules/aws/network"
  providers = { aws = aws.secondary }

  name                 = "${local.name}-usw2"
  vpc_cidr             = "10.51.0.0/16"
  availability_zones   = var.secondary_availability_zones
  private_subnet_cidrs = ["10.51.0.0/20", "10.51.16.0/20", "10.51.32.0/20"]
  public_subnet_cidrs  = ["10.51.128.0/24", "10.51.129.0/24", "10.51.130.0/24"]
  nat_gateway_mode     = "one_per_az"
  tags                 = var.tags
}

module "kubernetes_primary" {
  source    = "../../modules/aws/kubernetes_cluster"
  providers = { aws = aws.primary }

  name                = "${local.name}-use1"
  kubernetes_version  = "1.35"
  subnet_ids          = module.network_primary.network.private_subnet_ids
  node_instance_types = ["m7g.large"]
  node_capacity_type  = "ON_DEMAND"
  node_minimum        = 3
  node_desired        = 3
  node_maximum        = 6
  public_api          = false
  tags                = var.tags
}

module "kubernetes_secondary" {
  source    = "../../modules/aws/kubernetes_cluster"
  providers = { aws = aws.secondary }

  name                = "${local.name}-usw2"
  kubernetes_version  = "1.35"
  subnet_ids          = module.network_secondary.network.private_subnet_ids
  node_instance_types = ["m7g.large"]
  node_capacity_type  = "ON_DEMAND"
  node_minimum        = 3
  node_desired        = 3
  node_maximum        = 6
  public_api          = false
  tags                = var.tags
}

resource "aws_security_group" "event_bus_primary" {
  provider = aws.primary

  name        = "${local.name}-event-bus"
  description = "Kafka TLS and replication traffic in the primary VPC"
  vpc_id      = module.network_primary.network.id

  ingress {
    description = "Kafka IAM TLS from the primary VPC"
    from_port   = 9098
    to_port     = 9098
    protocol    = "tcp"
    cidr_blocks = [module.network_primary.network.cidr]
  }

  egress {
    description = "Kafka TLS to the remote replication endpoint"
    from_port   = 9098
    to_port     = 9098
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_security_group" "event_bus_secondary" {
  provider = aws.secondary

  name        = "${local.name}-event-bus"
  description = "Kafka TLS and replication traffic in the secondary VPC"
  vpc_id      = module.network_secondary.network.id

  ingress {
    description = "Kafka IAM TLS from the secondary VPC"
    from_port   = 9098
    to_port     = 9098
    protocol    = "tcp"
    cidr_blocks = [module.network_secondary.network.cidr]
  }

  egress {
    description = "Kafka TLS to the remote replication endpoint"
    from_port   = 9098
    to_port     = 9098
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

module "event_bus_primary" {
  source    = "../../modules/aws/event_bus"
  providers = { aws = aws.primary }

  name                 = "${local.name}-use1-events"
  subnet_ids           = module.network_primary.network.private_subnet_ids
  security_group_ids   = [aws_security_group.event_bus_primary.id]
  kafka_version        = "4.1.x"
  broker_instance_type = "kafka.m7g.large"
  broker_count         = 3
  broker_storage_gib   = 100
  tags                 = var.tags
}

module "event_bus_secondary" {
  source    = "../../modules/aws/event_bus"
  providers = { aws = aws.secondary }

  name                 = "${local.name}-usw2-events"
  subnet_ids           = module.network_secondary.network.private_subnet_ids
  security_group_ids   = [aws_security_group.event_bus_secondary.id]
  kafka_version        = "4.1.x"
  broker_instance_type = "kafka.m7g.large"
  broker_count         = 3
  broker_storage_gib   = 100
  tags                 = var.tags
}

resource "aws_iam_role" "msk_replicator" {
  provider = aws.primary
  name     = "${local.name}-msk-replicator"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect = "Allow"
      Principal = {
        Service = "kafka.amazonaws.com"
      }
      Action = "sts:AssumeRole"
      Condition = {
        StringEquals = {
          "aws:SourceAccount" = data.aws_caller_identity.current.account_id
        }
      }
    }]
  })
  tags = var.tags
}

resource "aws_iam_role_policy_attachment" "msk_replicator" {
  provider = aws.primary

  role       = aws_iam_role.msk_replicator.name
  policy_arn = "arn:${data.aws_partition.current.partition}:iam::aws:policy/service-role/AWSMSKReplicatorExecutionRole"
}

resource "aws_msk_replicator" "primary_to_secondary" {
  provider = aws.secondary

  replicator_name            = "${local.name}-use1-to-usw2"
  description                = "Asynchronous Kafka topic and consumer-offset copy for regional failover."
  service_execution_role_arn = aws_iam_role.msk_replicator.arn

  kafka_cluster {
    amazon_msk_cluster {
      msk_cluster_arn = module.event_bus_primary.cluster_arn
    }
    vpc_config {
      subnet_ids          = module.network_primary.network.private_subnet_ids
      security_groups_ids = [aws_security_group.event_bus_primary.id]
    }
  }

  kafka_cluster {
    amazon_msk_cluster {
      msk_cluster_arn = module.event_bus_secondary.cluster_arn
    }
    vpc_config {
      subnet_ids          = module.network_secondary.network.private_subnet_ids
      security_groups_ids = [aws_security_group.event_bus_secondary.id]
    }
  }

  replication_info_list {
    source_kafka_cluster_arn = module.event_bus_primary.cluster_arn
    target_kafka_cluster_arn = module.event_bus_secondary.cluster_arn
    target_compression_type  = "GZIP"

    consumer_group_replication {
      consumer_groups_to_replicate        = [".*"]
      detect_and_copy_new_consumer_groups = true
      synchronise_consumer_group_offsets  = true
    }

    topic_replication {
      topics_to_replicate                  = [".*"]
      detect_and_copy_new_topics           = true
      copy_topic_configurations            = true
      copy_access_control_lists_for_topics = false

      starting_position {
        type = "LATEST"
      }

      topic_name_configuration {
        type = "IDENTICAL"
      }
    }
  }

  tags       = var.tags
  depends_on = [aws_iam_role_policy_attachment.msk_replicator]
}

module "transactional_store" {
  source = "../../modules/cockroach/transactional_store"

  name = "${local.name}-sql"
  plan = "ADVANCED"
  regions = [
    { name = var.primary_region, node_count = 3 },
    { name = var.secondary_region, node_count = 3 },
    { name = "us-east-2", node_count = 3 },
  ]
  service_principals            = local.service_principals
  advanced_vcpus_per_node       = 4
  advanced_storage_gib_per_node = 100
  backup_frequency_minutes      = 5
  backup_retention_days         = 30
  delete_protection             = true
  labels = merge(var.tags, {
    environment = "aws-high"
    managed-by  = "terraform"
    project     = "projecty"
  })
}

resource "aws_security_group" "transactional_store_primary" {
  provider = aws.primary

  name        = "${local.name}-transactional-store"
  description = "CockroachDB PostgreSQL wire through primary-region PrivateLink"
  vpc_id      = module.network_primary.network.id

  ingress {
    description = "PostgreSQL wire from the primary VPC"
    from_port   = 26257
    to_port     = 26257
    protocol    = "tcp"
    cidr_blocks = [module.network_primary.network.cidr]
  }
}

resource "aws_security_group" "transactional_store_secondary" {
  provider = aws.secondary

  name        = "${local.name}-transactional-store"
  description = "CockroachDB PostgreSQL wire through secondary-region PrivateLink"
  vpc_id      = module.network_secondary.network.id

  ingress {
    description = "PostgreSQL wire from the secondary VPC"
    from_port   = 26257
    to_port     = 26257
    protocol    = "tcp"
    cidr_blocks = [module.network_secondary.network.cidr]
  }
}

resource "aws_vpc_endpoint" "transactional_store_primary" {
  provider = aws.primary

  vpc_id              = module.network_primary.network.id
  service_name        = module.transactional_store.private_endpoint_services[var.primary_region]
  vpc_endpoint_type   = "Interface"
  subnet_ids          = module.network_primary.network.private_subnet_ids
  security_group_ids  = [aws_security_group.transactional_store_primary.id]
  private_dns_enabled = false
  tags                = merge(var.tags, { Name = "${local.name}-transactional-store" })
}

resource "aws_vpc_endpoint" "transactional_store_secondary" {
  provider = aws.secondary

  vpc_id              = module.network_secondary.network.id
  service_name        = module.transactional_store.private_endpoint_services[var.secondary_region]
  vpc_endpoint_type   = "Interface"
  subnet_ids          = module.network_secondary.network.private_subnet_ids
  security_group_ids  = [aws_security_group.transactional_store_secondary.id]
  private_dns_enabled = false
  tags                = merge(var.tags, { Name = "${local.name}-transactional-store" })
}

resource "cockroach_private_endpoint_connection" "transactional_store_primary" {
  cluster_id  = module.transactional_store.private_endpoint_cluster_id
  endpoint_id = aws_vpc_endpoint.transactional_store_primary.id
}

resource "cockroach_private_endpoint_connection" "transactional_store_secondary" {
  cluster_id  = module.transactional_store.private_endpoint_cluster_id
  endpoint_id = aws_vpc_endpoint.transactional_store_secondary.id
}

resource "terraform_data" "profile_contract" {
  input = local.profile
}
