provider "docker" {}

provider "kind" {}

provider "aws" {
  region                      = var.aws_region
  access_key                  = var.localstack_access_key
  secret_key                  = var.localstack_secret_key
  skip_credentials_validation = true
  skip_metadata_api_check     = true
  skip_region_validation      = true
  skip_requesting_account_id  = true
  s3_use_path_style           = true

  endpoints {
    kms            = var.localstack_endpoint
    s3             = var.localstack_endpoint
    secretsmanager = var.localstack_endpoint
    sts            = var.localstack_endpoint
  }

  default_tags {
    tags = {
      Environment = "local"
      ManagedBy   = "terraform"
      Project     = "projecty"
    }
  }
}

provider "kubernetes" {
  host                   = module.kubernetes_cluster.cluster.endpoint
  client_certificate     = module.kubernetes_cluster.cluster.client_certificate
  client_key             = module.kubernetes_cluster.cluster.client_key
  cluster_ca_certificate = module.kubernetes_cluster.cluster.cluster_ca_certificate
}
