mock_provider "aws" {
  alias = "primary"
}

mock_provider "aws" {
  alias = "secondary"
}

mock_provider "cockroach" {}
mock_provider "random" {}

override_data {
  target = data.aws_partition.current
  values = { partition = "aws" }
}

override_data {
  target = data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

override_data {
  target = module.kubernetes_primary.data.aws_partition.current
  values = { partition = "aws" }
}

override_data {
  target = module.kubernetes_primary.data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

override_data {
  target = module.kubernetes_secondary.data.aws_partition.current
  values = { partition = "aws" }
}

override_data {
  target = module.kubernetes_secondary.data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

run "high_profile_plans_without_paid_accounts" {
  command = plan

  assert {
    condition     = output.profile.survives == "one AWS region"
    error_message = "The high profile must encode its regional-survival promise."
  }

  assert {
    condition     = output.profile.execution_policy == "review-only; never apply"
    error_message = "The high-cost topology must remain explicitly review-only."
  }
}
