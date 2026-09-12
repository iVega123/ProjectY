variable "aws_region" {
  description = "AWS region holding Terraform state."
  type        = string
  default     = "us-east-1"
}

variable "github_repository" {
  description = "GitHub owner/repository allowed to assume the Terraform roles."
  type        = string
}

variable "state_bucket_name" {
  description = "Globally unique S3 bucket name for all layered state."
  type        = string
}

variable "tags" {
  description = "Account-level tags."
  type        = map(string)
  default = {
    ManagedBy = "terraform"
    Project   = "projecty"
    Scope     = "foundation"
  }
}
