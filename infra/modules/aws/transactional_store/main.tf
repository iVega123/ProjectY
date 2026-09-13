resource "random_password" "administrator" {
  length           = 32
  special          = true
  override_special = "!#$%&*+-=?^_"
}

resource "aws_db_subnet_group" "this" {
  name       = var.name
  subnet_ids = var.subnet_ids
  tags       = merge(var.tags, { Name = var.name })
}

data "aws_partition" "current" {}

resource "aws_iam_role" "monitoring" {
  name = "${var.name}-database-monitoring"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect = "Allow"
      Principal = {
        Service = "monitoring.rds.amazonaws.com"
      }
      Action = "sts:AssumeRole"
    }]
  })

  tags = merge(var.tags, { Name = "${var.name}-database-monitoring" })
}

resource "aws_iam_role_policy_attachment" "monitoring" {
  role       = aws_iam_role.monitoring.name
  policy_arn = "arn:${data.aws_partition.current.partition}:iam::aws:policy/service-role/AmazonRDSEnhancedMonitoringRole"
}

resource "aws_db_parameter_group" "this" {
  name   = var.name
  family = var.parameter_group_family

  parameter {
    name         = "log_statement"
    value        = "all"
    apply_method = "pending-reboot"
  }

  parameter {
    name         = "rds.force_ssl"
    value        = "1"
    apply_method = "pending-reboot"
  }

  tags = merge(var.tags, { Name = var.name })
}

resource "aws_db_instance" "this" {
  # A profile that promises no AZ survival may deliberately set multi_az=false;
  # the mid/high profiles set it true and document the resulting RTO/RPO.
  #checkov:skip=CKV_AWS_157:Multi-AZ is an explicit cost-profile recovery promise.
  identifier = var.name

  engine         = "postgres"
  engine_version = var.engine_version
  instance_class = var.instance_class

  db_name  = var.database_name
  username = var.administrator_name
  password = random_password.administrator.result
  port     = 5432

  allocated_storage     = var.allocated_storage_gib
  max_allocated_storage = var.maximum_storage_gib
  storage_type          = "gp3"
  storage_encrypted     = true
  kms_key_id            = var.kms_key_arn

  db_subnet_group_name   = aws_db_subnet_group.this.name
  parameter_group_name   = aws_db_parameter_group.this.name
  vpc_security_group_ids = var.security_group_ids
  publicly_accessible    = false
  multi_az               = var.multi_az

  backup_retention_period             = var.backup_retention_days
  copy_tags_to_snapshot               = true
  delete_automated_backups            = false
  deletion_protection                 = var.deletion_protection
  skip_final_snapshot                 = var.skip_final_snapshot
  final_snapshot_identifier           = var.skip_final_snapshot ? null : "${var.name}-final"
  auto_minor_version_upgrade          = true
  performance_insights_enabled        = true
  performance_insights_kms_key_id     = var.kms_key_arn
  enabled_cloudwatch_logs_exports     = ["postgresql", "upgrade"]
  iam_database_authentication_enabled = true
  monitoring_interval                 = 60
  monitoring_role_arn                 = aws_iam_role.monitoring.arn

  tags = merge(var.tags, { Name = var.name })
}

resource "aws_secretsmanager_secret" "administrator" {
  # This bootstrap credential is never mounted by a workload. Rotation is handled
  # only while provisioning per-service database principals in the data layer.
  #checkov:skip=CKV2_AWS_57:Bootstrap secret rotation is replaced by per-service credentials before workloads deploy.
  name                    = "${var.name}/bootstrap"
  description             = "Bootstrap credential for ${var.name}; never mounted by workloads."
  kms_key_id              = var.kms_key_arn
  recovery_window_in_days = var.secret_recovery_window_days
  tags                    = merge(var.tags, { Name = "${var.name}-bootstrap" })
}

resource "aws_secretsmanager_secret_version" "administrator" {
  secret_id = aws_secretsmanager_secret.administrator.id
  secret_string = jsonencode({
    engine   = "postgres"
    host     = aws_db_instance.this.address
    port     = aws_db_instance.this.port
    dbname   = var.database_name
    username = var.administrator_name
    password = random_password.administrator.result
  })
}
