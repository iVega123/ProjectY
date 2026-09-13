variable "aws_region" {
  description = "AWS deployment region."
  type        = string
  default     = "us-east-1"
}

variable "availability_zones" {
  description = "Three AZs used by the availability-zone-surviving profile."
  type        = list(string)
  default     = ["us-east-1a", "us-east-1b", "us-east-1c"]
}

variable "tags" {
  description = "Additional tags merged with the environment identity."
  type        = map(string)
  default     = {}
}
