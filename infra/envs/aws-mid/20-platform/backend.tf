terraform {
  backend "s3" {
    key          = "aws-mid/20-platform/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}
