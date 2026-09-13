variable "aws_region" {
  type        = string
  description = "AWS-compatible region exposed by LocalStack."
  default     = "us-east-1"
}

variable "localstack_endpoint" {
  type        = string
  description = "LocalStack edge endpoint visible from Terraform on the host."
  default     = "http://127.0.0.1:4566"
}

variable "localstack_access_key" {
  type        = string
  description = "Non-production request-signing identity accepted by LocalStack."
  default     = "localstack"
  sensitive   = true
}

variable "localstack_secret_key" {
  type        = string
  description = "Non-production request-signing credential accepted by LocalStack."
  default     = "localstack"
  sensitive   = true
}
