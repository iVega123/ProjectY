variable "name" {
  type        = string
  description = "Local object-store bucket name."
}

variable "endpoint" {
  type        = string
  description = "S3-compatible endpoint visible to local containers."
}

variable "kms_key_arn" {
  type        = string
  description = "KMS key hosted by LocalStack."
}

module "aws_implementation" {
  source = "../../aws/object_store"

  name          = var.name
  kms_key_arn   = var.kms_key_arn
  force_destroy = true
  tags = {
    Environment = "local"
    ManagedBy   = "terraform"
    Project     = "projecty"
  }
}
