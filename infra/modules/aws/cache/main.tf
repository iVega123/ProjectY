resource "random_password" "token" {
  length           = 32
  special          = true
  override_special = "!&#$^<>-"
}

resource "aws_elasticache_subnet_group" "this" {
  name       = var.name
  subnet_ids = var.subnet_ids
}

resource "aws_elasticache_replication_group" "this" {
  # A single-node low profile intentionally does not claim AZ survival. Mid/high
  # set replica_count > 0, which enables both Multi-AZ and automatic failover.
  #checkov:skip=CKV2_AWS_50:Availability is selected and documented by each cost profile.
  replication_group_id = var.name
  description          = "RESP cache capability for ${var.name}"

  engine               = "valkey"
  node_type            = var.node_type
  port                 = 6379
  num_cache_clusters   = var.replica_count + 1
  parameter_group_name = "default.valkey8"

  subnet_group_name  = aws_elasticache_subnet_group.this.name
  security_group_ids = var.security_group_ids

  at_rest_encryption_enabled = true
  transit_encryption_enabled = true
  auth_token                 = random_password.token.result
  kms_key_id                 = var.kms_key_arn

  automatic_failover_enabled = var.replica_count > 0
  multi_az_enabled           = var.replica_count > 0
  apply_immediately          = false
  auto_minor_version_upgrade = true
  snapshot_retention_limit   = 7

  tags = merge(var.tags, { Name = var.name })
}

resource "aws_secretsmanager_secret" "client" {
  # ElastiCache token rotation is an environment-level two-token rollout; keeping
  # it beside the workload rollout avoids invalidating live clients mid-apply.
  #checkov:skip=CKV2_AWS_57:Rotation is composed atomically by the data and workloads layers.
  name                    = "${var.name}/client"
  description             = "RESP client credential for ${var.name}."
  kms_key_id              = var.kms_key_arn
  recovery_window_in_days = var.secret_recovery_window_days
  tags                    = merge(var.tags, { Name = "${var.name}-client" })
}

resource "aws_secretsmanager_secret_version" "client" {
  secret_id = aws_secretsmanager_secret.client.id
  secret_string = jsonencode({
    endpoint = aws_elasticache_replication_group.this.primary_endpoint_address
    port     = aws_elasticache_replication_group.this.port
    token    = random_password.token.result
    tls      = true
  })
}
