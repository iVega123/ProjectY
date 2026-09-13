variable "name" {
  type        = string
  description = "CockroachDB Cloud cluster name."
}

variable "plan" {
  type        = string
  description = "Current CockroachDB Cloud product plan."

  validation {
    condition     = contains(["STANDARD", "ADVANCED"], var.plan)
    error_message = "plan must use a current managed tier: STANDARD or ADVANCED."
  }
}

variable "regions" {
  type = list(object({
    name       = string
    node_count = optional(number)
  }))
  description = "AWS regions and, for Advanced, nodes per region."

  validation {
    condition     = length(var.regions) > 0
    error_message = "At least one CockroachDB Cloud region is required."
  }
}

variable "service_principals" {
  type        = set(string)
  description = "Application services that receive distinct SQL principals."
}

variable "standard_vcpus" {
  type        = number
  description = "Provisioned vCPU ceiling for the Standard plan."
  default     = 2
}

variable "advanced_vcpus_per_node" {
  type        = number
  description = "Dedicated vCPUs assigned to every Advanced node."
  default     = 4
}

variable "advanced_storage_gib_per_node" {
  type        = number
  description = "Dedicated storage assigned to every Advanced node."
  default     = 100
}

variable "backup_frequency_minutes" {
  type        = number
  description = "Managed backup interval that establishes the profile RPO target."

  validation {
    condition     = contains([5, 10, 15, 30, 60, 240, 1440], var.backup_frequency_minutes)
    error_message = "backup_frequency_minutes is not supported by CockroachDB Cloud."
  }
}

variable "backup_retention_days" {
  type        = number
  description = "Managed backup retention."
  default     = 30
}

variable "delete_protection" {
  type        = bool
  description = "Protect the managed cluster from accidental deletion."
  default     = true
}

variable "labels" {
  type        = map(string)
  description = "CockroachDB Cloud billing and ownership labels."
  default     = {}
}
