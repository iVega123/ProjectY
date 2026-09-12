variable "name" {
  description = "Logical Kubernetes cluster name."
  type        = string
}

variable "kubernetes_version" {
  description = "Kubernetes control-plane version."
  type        = string
}

variable "subnet_ids" {
  description = "Subnets used by control-plane interfaces and nodes."
  type        = list(string)
}

variable "node_instance_types" {
  description = "Permitted node instance types selected by the profile."
  type        = list(string)
}

variable "node_capacity_type" {
  description = "ON_DEMAND or SPOT capacity."
  type        = string
  default     = "ON_DEMAND"

  validation {
    condition     = contains(["ON_DEMAND", "SPOT"], var.node_capacity_type)
    error_message = "node_capacity_type must be ON_DEMAND or SPOT."
  }
}

variable "node_minimum" {
  description = "Minimum node count."
  type        = number
  default     = 2
}

variable "node_desired" {
  description = "Desired node count."
  type        = number
  default     = 2
}

variable "node_maximum" {
  description = "Maximum node count."
  type        = number
  default     = 4
}

variable "public_api" {
  description = "Whether the Kubernetes API is reachable from the public internet."
  type        = bool
  default     = false
}

variable "public_api_cidrs" {
  description = "CIDRs allowed to reach a public API endpoint."
  type        = list(string)
  default     = []
}

variable "log_retention_days" {
  description = "Control-plane log retention."
  type        = number
  default     = 365
}

variable "tags" {
  description = "Tags applied to cluster resources."
  type        = map(string)
  default     = {}
}
