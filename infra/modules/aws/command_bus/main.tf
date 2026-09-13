resource "random_password" "client" {
  length           = 32
  special          = true
  override_special = "!#$%&*+-?^_"
}

resource "aws_mq_broker" "this" {
  broker_name = var.name

  engine_type        = "RABBITMQ"
  engine_version     = var.engine_version
  host_instance_type = var.host_instance_type
  deployment_mode    = var.high_availability ? "CLUSTER_MULTI_AZ" : "SINGLE_INSTANCE"

  publicly_accessible        = false
  subnet_ids                 = var.high_availability ? var.subnet_ids : [var.subnet_ids[0]]
  security_groups            = var.security_group_ids
  auto_minor_version_upgrade = true

  authentication_strategy = "SIMPLE"

  user {
    username = "projecty_client"
    password = random_password.client.result
  }

  encryption_options {
    use_aws_owned_key = var.kms_key_arn == null
    kms_key_id        = var.kms_key_arn
  }

  logs {
    general = true
    audit   = true
  }

  tags = merge(var.tags, { Name = var.name })
}

resource "aws_secretsmanager_secret" "client" {
  # Rotation for Amazon MQ requires a customer Lambda and is composed in the data
  # layer, where the previous credential can remain valid during the broker update.
  #checkov:skip=CKV2_AWS_57:Rotation belongs to the environment transaction, not the capability module.
  name                    = "${var.name}/client"
  description             = "AMQP client credential for ${var.name}."
  kms_key_id              = var.kms_key_arn
  recovery_window_in_days = var.secret_recovery_window_days
  tags                    = merge(var.tags, { Name = "${var.name}-client" })
}

resource "aws_secretsmanager_secret_version" "client" {
  secret_id = aws_secretsmanager_secret.client.id
  secret_string = jsonencode({
    endpoint = aws_mq_broker.this.instances[0].endpoints[0]
    port     = 5671
    username = "projecty_client"
    password = random_password.client.result
    tls      = true
  })
}
