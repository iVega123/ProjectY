provider "aws" {
  region = var.aws_region

  default_tags {
    tags = merge(var.tags, {
      Environment = "aws-mid"
      ManagedBy   = "terraform"
      Project     = "projecty"
    })
  }
}

data "terraform_remote_state" "data" {
  backend = "s3"
  config = {
    bucket       = var.state_bucket
    key          = "aws-mid/10-data/terraform.tfstate"
    region       = var.aws_region
    encrypt      = true
    use_lockfile = true
  }
}

locals {
  network = data.terraform_remote_state.data.outputs.network
}

module "kubernetes_cluster" {
  source = "../../../modules/aws/kubernetes_cluster"

  name                = "projecty-aws-mid"
  kubernetes_version  = "1.35"
  subnet_ids          = local.network.private_subnet_ids
  node_instance_types = ["m7g.large"]
  node_capacity_type  = "ON_DEMAND"
  node_minimum        = 3
  node_desired        = 3
  node_maximum        = 6
  public_api          = false
  tags                = var.tags
}
