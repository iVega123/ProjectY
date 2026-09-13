provider "aws" {
  region = var.aws_region
}

data "terraform_remote_state" "platform" {
  backend = "s3"
  config = {
    bucket       = var.state_bucket
    key          = "aws-mid/20-platform/terraform.tfstate"
    region       = var.aws_region
    encrypt      = true
    use_lockfile = true
  }
}

data "aws_eks_cluster_auth" "this" {
  name = data.terraform_remote_state.platform.outputs.cluster.name
}

provider "kubernetes" {
  host                   = "https://${data.terraform_remote_state.platform.outputs.cluster_connection.endpoint}"
  cluster_ca_certificate = base64decode(data.terraform_remote_state.platform.outputs.cluster.certificate_authority)
  token                  = data.aws_eks_cluster_auth.this.token
}

provider "helm" {
  kubernetes = {
    host                   = "https://${data.terraform_remote_state.platform.outputs.cluster_connection.endpoint}"
    cluster_ca_certificate = base64decode(data.terraform_remote_state.platform.outputs.cluster.certificate_authority)
    token                  = data.aws_eks_cluster_auth.this.token
  }
}

locals {
  connections = data.terraform_remote_state.platform.outputs.connections
}

resource "kubernetes_namespace_v1" "projecty" {
  metadata {
    name = "projecty"
    labels = {
      "pod-security.kubernetes.io/enforce"         = "restricted"
      "pod-security.kubernetes.io/enforce-version" = "latest"
      "projecty.io/environment"                    = "aws-mid"
    }
  }
}

resource "kubernetes_config_map_v1" "cloud_runtime" {
  metadata {
    name      = "projecty-cloud-runtime"
    namespace = kubernetes_namespace_v1.projecty.metadata[0].name
  }

  data = {
    REDIS_URL               = "rediss://${local.connections.cache.endpoint}:${local.connections.cache.port}"
    RabbitMQ__HostName      = local.connections.command_bus.endpoint
    RabbitMQ__Port          = tostring(local.connections.command_bus.port)
    CASSANDRA_HOST          = local.connections.time_series_store.endpoint
    CASSANDRA_PORT          = tostring(local.connections.time_series_store.port)
    COCKROACH_HOST          = local.connections.transactional_store.endpoint
    COCKROACH_PORT          = tostring(local.connections.transactional_store.port)
    KAFKA_BOOTSTRAP_SERVERS = "${local.connections.event_bus.endpoint}:${local.connections.event_bus.port}"
    Kafka__BootstrapServers = "${local.connections.event_bus.endpoint}:${local.connections.event_bus.port}"
    S3_ENDPOINT             = local.connections.object_store.endpoint
    S3_BUCKET               = data.terraform_remote_state.platform.outputs.object_store.name
    AWS_DEFAULT_REGION      = var.aws_region
    PROJECTY_CACHE_SECRET   = local.connections.cache.secret_reference
    PROJECTY_COMMAND_SECRET = local.connections.command_bus.secret_reference
  }
}

resource "helm_release" "strimzi" {
  name       = "projecty-strimzi"
  repository = "https://strimzi.io/charts/"
  chart      = "strimzi-kafka-operator"
  version    = "1.2.0"
  namespace  = kubernetes_namespace_v1.projecty.metadata[0].name

  atomic          = true
  cleanup_on_fail = true
  wait            = true
  timeout         = 600
}

resource "terraform_data" "manifests" {
  input = {
    cluster       = data.terraform_remote_state.platform.outputs.cluster.name
    manifest_hash = sha256(join("", [for file in sort(fileset("${path.root}/../../../../deploy", "**/*.yaml")) : filesha256("${path.root}/../../../../deploy/${file}")]))
  }

  provisioner "local-exec" {
    working_dir = path.root
    interpreter = ["bash", "-c"]
    command     = <<-EOT
      set -euo pipefail
      aws eks update-kubeconfig --name '${self.input.cluster}' --region '${var.aws_region}' --kubeconfig '.terraform/projecty-kubeconfig'
      kubectl --kubeconfig '.terraform/projecty-kubeconfig' apply --server-side --field-manager=terraform-workloads -k '../../../../deploy/overlays/aws'
    EOT
  }

  depends_on = [kubernetes_config_map_v1.cloud_runtime, helm_release.strimzi]
}
