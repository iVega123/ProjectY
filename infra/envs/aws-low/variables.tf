variable "aws_region" {
  description = "AWS region used by the disposable low-cost profile."
  type        = string
  default     = "us-east-1"
}

variable "availability_zones" {
  description = "Two subnet locations; the single workload node remains the intentional failure domain."
  type        = list(string)
  default     = ["us-east-1a", "us-east-1b"]
}

variable "tags" {
  description = "Additional tags merged with the profile identity."
  type        = map(string)
  default     = {}
}
