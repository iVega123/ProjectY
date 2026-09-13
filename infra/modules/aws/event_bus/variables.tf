variable "name" {
  description = "Logical event-bus name."
  type        = string
}

variable "subnet_ids" {
  description = "Private subnets for Kafka brokers."
  type        = list(string)
}

variable "security_group_ids" {
  description = "Security groups allowed to reach Kafka."
  type        = list(string)
}

variable "kafka_version" {
  description = "Kafka protocol implementation version."
  type        = string
}

variable "broker_instance_type" {
  description = "Broker instance type selected by the environment profile."
  type        = string
}

variable "broker_count" {
  description = "Number of brokers; must be a multiple of the subnet count."
  type        = number
  default     = 3
}

variable "broker_storage_gib" {
  description = "EBS volume size per broker."
  type        = number
  default     = 100
}

variable "kms_key_arn" {
  description = "Optional customer-managed KMS key ARN."
  type        = string
  default     = null
}

variable "log_retention_days" {
  description = "Broker log retention in CloudWatch Logs."
  type        = number
  default     = 365
}

variable "tags" {
  description = "Tags applied to the event bus."
  type        = map(string)
  default     = {}
}
