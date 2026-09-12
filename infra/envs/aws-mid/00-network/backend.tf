terraform {
  backend "s3" {
    key          = "aws-mid/00-network/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}
