locals {
  name                 = "projecty-local"
  container_host       = "host.docker.internal"
  localstack_container = "projecty-localstack"
}

module "network" {
  source = "../../modules/local/network"

  name = "${local.name}-capabilities"
}

module "localstack" {
  source = "../../modules/local/localstack"

  name         = local.localstack_container
  network_name = module.network.network.name
}

resource "terraform_data" "localstack_ready" {
  input = module.localstack.endpoint

  provisioner "local-exec" {
    command = "curl --fail --silent --show-error --retry 30 --retry-delay 1 --retry-connrefused ${self.input}/_localstack/health"
  }

  depends_on = [module.localstack]
}

module "transactional_store" {
  source = "../../modules/local/transactional_store"

  name         = "${local.name}-cockroach"
  network_name = module.network.network.name
}

module "event_bus" {
  source = "../../modules/local/event_bus"

  name         = "${local.name}-kafka"
  network_name = module.network.network.name
}

module "command_bus" {
  source = "../../modules/local/command_bus"

  name         = "${local.name}-rabbitmq"
  network_name = module.network.network.name
}

module "cache" {
  source = "../../modules/local/cache"

  name         = "${local.name}-valkey"
  network_name = module.network.network.name
}

module "time_series_store" {
  source = "../../modules/local/time_series_store"

  name         = "${local.name}-cassandra"
  network_name = module.network.network.name
}

resource "aws_kms_key" "data" {
  #checkov:skip=CKV2_AWS_64:LocalStack has no account boundary to express in a production-grade key policy; aws-high/aws-mid own real account policies.
  description             = "LocalStack-only key for ProjectY development data"
  deletion_window_in_days = 7
  enable_key_rotation     = true

  depends_on = [terraform_data.localstack_ready]
}

resource "aws_kms_alias" "data" {
  name          = "alias/projecty-local-data"
  target_key_id = aws_kms_key.data.key_id
}

module "object_store" {
  source = "../../modules/local/object_store"

  name        = "projecty-local-media"
  endpoint    = module.localstack.container_endpoint
  kms_key_arn = aws_kms_key.data.arn

  depends_on = [terraform_data.localstack_ready]
}

resource "aws_secretsmanager_secret" "capability" {
  for_each = {
    cache       = module.cache.connection.secret_reference
    command_bus = module.command_bus.connection.secret_reference
  }

  #checkov:skip=CKV_AWS_149:The LocalStack free-tier KMS implementation is exercised separately; these disposable bootstrap secrets never leave the workstation.
  #checkov:skip=CKV2_AWS_57:Rotating an ephemeral local bootstrap credential would not exercise the managed rotation service used by AWS profiles.
  name                    = each.value
  recovery_window_in_days = 0

  depends_on = [terraform_data.localstack_ready]
}

resource "aws_secretsmanager_secret_version" "capability" {
  for_each = aws_secretsmanager_secret.capability

  secret_id = each.value.id
  secret_string = jsonencode(
    each.key == "cache" ? module.cache.bootstrap_secret : module.command_bus.bootstrap_secret
  )
}

module "kubernetes_cluster" {
  source = "../../modules/local/kubernetes_cluster"

  name = local.name
}

resource "kubernetes_namespace_v1" "projecty" {
  metadata {
    name = "projecty"
    labels = {
      "pod-security.kubernetes.io/enforce"         = "restricted"
      "pod-security.kubernetes.io/enforce-version" = "latest"
      "projecty.io/environment"                    = "local-terraform"
    }
  }

  depends_on = [module.kubernetes_cluster]
}

resource "kubernetes_config_map_v1" "cloud_runtime" {
  metadata {
    name      = "projecty-cloud-runtime"
    namespace = kubernetes_namespace_v1.projecty.metadata[0].name
  }

  data = {
    AWS_DEFAULT_REGION               = var.aws_region
    AWS_ENDPOINT_URL_KMS             = "http://${local.container_host}:4566"
    AWS_ENDPOINT_URL_S3              = "http://${local.container_host}:4566"
    AWS_ENDPOINT_URL_SECRETS_MANAGER = "http://${local.container_host}:4566"
    CASSANDRA_HOST                   = local.container_host
    CASSANDRA_PORT                   = "9042"
    KAFKA_BOOTSTRAP_SERVERS          = "${local.container_host}:29092"
    RabbitMQ__HostName               = local.container_host
    RabbitMQ__Port                   = "5672"
    REDIS_URL                        = "redis://${local.container_host}:6379"
    S3_BUCKET                        = module.object_store.bucket.name
    S3_ENDPOINT                      = "http://${local.container_host}:4566"
    PROJECTY_CACHE_SECRET            = module.cache.connection.secret_reference
    PROJECTY_COMMAND_SECRET          = module.command_bus.connection.secret_reference
  }
}
