terraform {
  required_version = ">= 1.10.0, < 2.0.0"
  required_providers {
    kind = {
      source  = "tehcyx/kind"
      version = "0.11.0"
    }
  }
}
