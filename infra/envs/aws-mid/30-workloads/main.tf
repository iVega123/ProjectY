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
    S3_ENDPOINT             = local.connections.object_store.endpoint
    S3_BUCKET               = data.terraform_remote_state.platform.outputs.object_store.name
    AWS_DEFAULT_REGION      = var.aws_region
    PROJECTY_CACHE_SECRET   = local.connections.cache.secret_reference
    PROJECTY_COMMAND_SECRET = local.connections.command_bus.secret_reference
  }
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

  depends_on = [kubernetes_config_map_v1.cloud_runtime]
}
