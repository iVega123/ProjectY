variable "name" {
  description = "CQL keyspace name."
  type        = string
}

variable "region" {
  description = "AWS region containing the CQL endpoint."
  type        = string
}

variable "tags" {
  description = "Tags applied to the keyspace."
  type        = map(string)
  default     = {}
}

