terraform {
  required_version = ">= 1.10.0, < 2.0.0"
  required_providers {
    cockroach = {
      source  = "cockroachdb/cockroach"
      version = "1.22.0"
    }
    random = {
      source  = "hashicorp/random"
      version = ">= 3.6.0, < 4.0.0"
    }
  }
}
