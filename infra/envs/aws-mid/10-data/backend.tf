terraform {
  backend "s3" {
    key          = "aws-mid/10-data/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}
