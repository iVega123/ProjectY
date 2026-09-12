variable "name" {
  description = "Logical cache name."
  type        = string
}

variable "subnet_ids" {
  description = "Private subnets for the cache."
  type        = list(string)
}

variable "security_group_ids" {
  description = "Security groups allowed to reach the cache."
  type        = list(string)
}

variable "node_type" {
  description = "Cache node type selected by the environment profile."
  type        = string
}

variable "replica_count" {
  description = "Replica count in addition to the primary."
  type        = number
  default     = 0
}

variable "kms_key_arn" {
  description = "Optional customer-managed KMS key ARN."
  type        = string
  default     = null
}

variable "secret_recovery_window_days" {
  description = "Secrets Manager recovery window."
  type        = number
  default     = 7
}

variable "tags" {
  description = "Tags applied to the cache."
  type        = map(string)
  default     = {}
}
