variable "name" {
  description = "Logical command-bus name."
  type        = string
}

variable "subnet_ids" {
  description = "Private subnets used by the command bus."
  type        = list(string)
}

variable "security_group_ids" {
  description = "Security groups allowed to use AMQP."
  type        = list(string)
}

variable "host_instance_type" {
  description = "Broker instance type selected by the environment profile."
  type        = string
}

variable "engine_version" {
  description = "RabbitMQ engine version."
  type        = string
}

variable "high_availability" {
  description = "Whether the broker spans availability zones."
  type        = bool
  default     = false
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
  description = "Tags applied to the command bus."
  type        = map(string)
  default     = {}
}
