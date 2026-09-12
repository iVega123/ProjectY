variable "name" {
  description = "Globally unique bucket name."
  type        = string
}

variable "kms_key_arn" {
  description = "Optional customer-managed KMS key ARN. AWS-managed S3 encryption is used when null."
  type        = string
  default     = null
}

variable "force_destroy" {
  description = "Whether Terraform may delete non-empty buckets. Intended only for ephemeral profiles."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags applied to the object store."
  type        = map(string)
  default     = {}
}
