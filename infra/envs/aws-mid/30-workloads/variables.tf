variable "aws_region" {
  description = "AWS deployment region."
  type        = string
  default     = "us-east-1"
}

variable "state_bucket" {
  description = "S3 bucket containing lower-layer state."
  type        = string
}
