output "policy_review" {
  description = "Human-readable grants; resource identifiers intentionally stay in 20-platform state."
  value = {
    for name, service in var.services : name => [
      for statement in service.statements : {
        sid     = statement.sid
        reason  = statement.reason
        actions = statement.actions
      }
    ]
  }
}

output "role_arns" {
  description = "Role identifiers for platform controllers; never re-export to 30-workloads."
  value       = { for name, role in aws_iam_role.service : name => role.arn }
}
