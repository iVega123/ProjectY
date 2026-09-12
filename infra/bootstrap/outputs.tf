output "state_bucket" {
  description = "Repository variable AWS_TERRAFORM_STATE_BUCKET."
  value       = aws_s3_bucket.state.bucket
}

output "plan_role_arn" {
  description = "Repository variable AWS_TERRAFORM_PLAN_ROLE_ARN."
  value       = aws_iam_role.plan.arn
}

output "apply_role_arn" {
  description = "Repository variable AWS_TERRAFORM_APPLY_ROLE_ARN."
  value       = aws_iam_role.apply.arn
}
