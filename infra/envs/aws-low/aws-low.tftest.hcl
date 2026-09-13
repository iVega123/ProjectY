mock_provider "aws" {}

override_data {
  target = data.aws_partition.current
  values = { partition = "aws" }
}

override_data {
  target = data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

override_data {
  target = module.kubernetes_cluster.data.aws_partition.current
  values = { partition = "aws" }
}

override_data {
  target = module.kubernetes_cluster.data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

run "low_profile_plans_without_cloud_credentials" {
  command = plan

  assert {
    condition     = output.profile.rpo == "24h"
    error_message = "The low profile must make its 24-hour data-loss promise explicit."
  }

  assert {
    condition     = output.profile.survives == "nothing"
    error_message = "A single-node data plane must not claim an availability guarantee."
  }
}
