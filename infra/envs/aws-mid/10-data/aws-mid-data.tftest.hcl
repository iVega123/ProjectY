mock_provider "aws" {}
mock_provider "cockroach" {}
mock_provider "random" {}

override_data {
  target = data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

override_data {
  target = data.terraform_remote_state.network
  values = {
    outputs = {
      network = {
        id                 = "vpc-0123456789abcdef0"
        cidr               = "10.42.0.0/16"
        private_subnet_ids = ["subnet-00000000000000001", "subnet-00000000000000002", "subnet-00000000000000003"]
        public_subnet_ids  = ["subnet-00000000000000004", "subnet-00000000000000005", "subnet-00000000000000006"]
        availability_zones = ["us-east-1a", "us-east-1b", "us-east-1c"]
      }
    }
  }
}

run "mid_data_plans_with_managed_cockroach" {
  command = plan

  variables {
    state_bucket = "projecty-test-state"
  }

  assert {
    condition     = length(output.database_secret_references) == 4
    error_message = "Each transactional owner must receive a distinct SQL principal."
  }
}
