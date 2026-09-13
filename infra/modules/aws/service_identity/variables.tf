variable "cluster_name" {
  description = "Kubernetes cluster that owns the service accounts."
  type        = string
}

variable "namespace" {
  description = "Kubernetes namespace containing the service accounts."
  type        = string
}

variable "services" {
  description = "One independently reviewable IAM policy per Kubernetes service account."
  type = map(object({
    statements = list(object({
      sid       = string
      reason    = string
      actions   = list(string)
      resources = list(string)
    }))
  }))

  validation {
    condition = alltrue(flatten([
      for service in values(var.services) : [
        for statement in service.statements :
        length(statement.reason) >= 20 &&
        length(statement.actions) > 0 &&
        length(statement.resources) > 0 &&
        !contains(statement.actions, "*") &&
        !contains(statement.resources, "*")
      ]
    ]))
    error_message = "Every grant needs a concrete action/resource and a reviewable reason."
  }
}

variable "tags" {
  description = "Tags applied to workload identities."
  type        = map(string)
  default     = {}
}
