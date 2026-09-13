variable "name" {
  description = "Stable capability name used for tags."
  type        = string
}

variable "vpc_cidr" {
  description = "CIDR assigned to the network capability."
  type        = string
}

variable "availability_zones" {
  description = "Ordered availability zones used by the subnets."
  type        = list(string)

  validation {
    condition     = length(var.availability_zones) >= 2
    error_message = "At least two availability zones are required."
  }
}

variable "private_subnet_cidrs" {
  description = "Private subnet CIDRs, one per availability zone."
  type        = list(string)
}

variable "public_subnet_cidrs" {
  description = "Public subnet CIDRs, one per availability zone."
  type        = list(string)
}

variable "nat_gateway_mode" {
  description = "Egress topology: none, single, or one_per_az."
  type        = string
  default     = "single"

  validation {
    condition     = contains(["none", "single", "one_per_az"], var.nat_gateway_mode)
    error_message = "nat_gateway_mode must be none, single, or one_per_az."
  }
}

variable "tags" {
  description = "Tags applied to all taggable resources."
  type        = map(string)
  default     = {}
}

variable "flow_log_kms_key_arn" {
  description = "Optional KMS key ARN used to encrypt VPC flow logs."
  type        = string
  default     = null
}

variable "flow_log_retention_days" {
  description = "VPC flow-log retention."
  type        = number
  default     = 365
}
