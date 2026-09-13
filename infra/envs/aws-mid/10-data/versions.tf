terraform {
  required_version = ">= 1.10.0, < 2.0.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = ">= 6.0.0, < 7.0.0"
    }
    cockroach = {
      source  = "cockroachdb/cockroach"
      version = "1.22.0"
    }
  }
}
