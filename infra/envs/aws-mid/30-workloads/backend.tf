terraform {
  backend "s3" {
    key          = "aws-mid/30-workloads/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}
