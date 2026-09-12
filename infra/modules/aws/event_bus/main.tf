resource "aws_cloudwatch_log_group" "broker" {
  name              = "/projecty/${var.name}/broker"
  retention_in_days = var.log_retention_days
  kms_key_id        = var.kms_key_arn
  tags              = merge(var.tags, { Name = "${var.name}-broker-logs" })
}

resource "aws_msk_cluster" "this" {
  cluster_name           = var.name
  kafka_version          = var.kafka_version
  number_of_broker_nodes = var.broker_count

  broker_node_group_info {
    client_subnets  = var.subnet_ids
    instance_type   = var.broker_instance_type
    security_groups = var.security_group_ids

    storage_info {
      ebs_storage_info {
        volume_size = var.broker_storage_gib
      }
    }
  }

  client_authentication {
    sasl {
      iam = true
    }
    unauthenticated = false
  }

  encryption_info {
    encryption_at_rest_kms_key_arn = var.kms_key_arn

    encryption_in_transit {
      client_broker = "TLS"
      in_cluster    = true
    }
  }

  logging_info {
    broker_logs {
      cloudwatch_logs {
        enabled   = true
        log_group = aws_cloudwatch_log_group.broker.name
      }
    }
  }

  tags = merge(var.tags, { Name = var.name })

  lifecycle {
    precondition {
      condition     = var.broker_count % length(var.subnet_ids) == 0
      error_message = "broker_count must be a multiple of the subnet count."
    }
  }
}

