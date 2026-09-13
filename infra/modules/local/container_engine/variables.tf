variable "name" {
  description = "Stable container and DNS name."
  type        = string
}

variable "image" {
  description = "Pinned engine image."
  type        = string
}

variable "network_name" {
  description = "Docker network shared by local capabilities."
  type        = string
}

variable "command" {
  description = "Optional container command."
  type        = list(string)
  default     = []
}

variable "environment" {
  description = "Container environment entries in KEY=value form."
  type        = set(string)
  default     = []
  sensitive   = true
}

variable "ports" {
  description = "Ports published to the developer host."
  type = list(object({
    internal = number
    external = number
  }))
}

variable "data_path" {
  description = "Optional container path backed by a named volume."
  type        = string
  default     = null
}

variable "healthcheck" {
  description = "OCI healthcheck command."
  type        = list(string)
}
