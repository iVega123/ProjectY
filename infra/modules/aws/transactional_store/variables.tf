variable "name" {
  description = "Logical transactional-store name."
  type        = string
}

variable "database_name" {
  description = "Initial database exposed by the capability."
  type        = string
}

variable "administrator_name" {
  description = "Bootstrap administrator; workloads receive separate credentials."
  type        = string
  default     = "projecty_admin"
}

variable "subnet_ids" {
  description = "Private subnet IDs for the store."
  type        = list(string)
}

variable "security_group_ids" {
  description = "Security groups allowed to reach the store."
  type        = list(string)
}

variable "instance_class" {
  description = "Database instance class selected by the environment profile."
  type        = string
}

variable "engine_version" {
  description = "Optional PostgreSQL engine version."
  type        = string
  default     = null
}

variable "parameter_group_family" {
  description = "PostgreSQL parameter-group family matching engine_version."
  type        = string
  default     = "postgres17"
}

variable "allocated_storage_gib" {
  description = "Initial gp3 storage allocation in GiB."
  type        = number
  default     = 20
}

variable "maximum_storage_gib" {
  description = "Maximum autoscaled storage in GiB."
  type        = number
  default     = 100
}

variable "multi_az" {
  description = "Whether the store survives an availability-zone loss."
  type        = bool
  default     = false
}

variable "backup_retention_days" {
  description = "Automated backup retention."
  type        = number
  default     = 7
}

variable "deletion_protection" {
  description = "AWS-side deletion protection in addition to layer protection."
  type        = bool
  default     = true
}

variable "skip_final_snapshot" {
  description = "Permit deletion without a final snapshot in ephemeral profiles."
  type        = bool
  default     = false
}

variable "kms_key_arn" {
  description = "Optional customer-managed KMS key ARN."
  type        = string
  default     = null
}

variable "secret_recovery_window_days" {
  description = "Secrets Manager recovery window; zero permits immediate ephemeral recreation."
  type        = number
  default     = 7
}

variable "tags" {
  description = "Tags applied to the transactional store."
  type        = map(string)
  default     = {}
}
