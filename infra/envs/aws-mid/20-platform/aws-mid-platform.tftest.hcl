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

override_data {
  target = data.terraform_remote_state.data
  values = {
    outputs = {
      network = {
        id                 = "vpc-0123456789abcdef0"
        cidr               = "10.42.0.0/16"
        private_subnet_ids = ["subnet-00000000000000001", "subnet-00000000000000002", "subnet-00000000000000003"]
      }
      connections = {
        cache               = { endpoint = "cache.test", port = 6379, secret_reference = "projecty-aws-mid-cache/client" }
        command_bus         = { endpoint = "rabbit.test", port = 5671, secret_reference = "projecty-aws-mid-commands/client" }
        object_store        = { endpoint = "https://objects.test", port = 443, secret_reference = null }
        time_series_store   = { endpoint = "cassandra.test", port = 9142, secret_reference = null }
        transactional_store = { endpoint = "sql.test", port = 26257, secret_reference = null }
      }
      database_secret_references = {
        billing      = "projecty-aws-mid-sql/billing/sql"
        identity     = "projecty-aws-mid-sql/identity/sql"
        rental-core  = "projecty-aws-mid-sql/rental-core/sql"
        risk-pricing = "projecty-aws-mid-sql/risk-pricing/sql"
      }
      object_store = { name = "projecty-test-objects", region = "us-east-1" }
    }
  }
}

run "mid_platform_plans_three_az_cluster_and_identities" {
  command = plan

  variables {
    state_bucket = "projecty-test-state"
  }

  assert {
    condition     = output.connections.event_bus.endpoint == "projecty-kafka-kafka-bootstrap.projecty.svc.cluster.local"
    error_message = "Mid workloads must use the stable Strimzi bootstrap alias."
  }
}
