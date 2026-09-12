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

module "network" {
  source = "../../../modules/aws/network"

  name                 = "projecty-aws-mid"
  vpc_cidr             = "10.42.0.0/16"
  availability_zones   = var.availability_zones
  private_subnet_cidrs = ["10.42.0.0/20", "10.42.16.0/20", "10.42.32.0/20"]
  public_subnet_cidrs  = ["10.42.128.0/24", "10.42.129.0/24", "10.42.130.0/24"]
  nat_gateway_mode     = "one_per_az"
  tags                 = var.tags
}
