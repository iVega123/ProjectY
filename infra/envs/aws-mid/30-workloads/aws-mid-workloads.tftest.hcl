mock_provider "aws" {}
mock_provider "kubernetes" {}
mock_provider "helm" {}

override_data {
  target = data.aws_eks_cluster_auth.this
  values = { token = "mock-eks-token" }
}

override_data {
  target = data.terraform_remote_state.platform
  values = {
    outputs = {
      cluster = {
        name                  = "projecty-aws-mid"
        certificate_authority = "bW9jay1jYQ=="
      }
      cluster_connection = { endpoint = "eks.test", port = 443, secret_reference = null }
      connections = {
        cache               = { endpoint = "cache.test", port = 6379, secret_reference = "projecty-aws-mid-cache/client" }
        command_bus         = { endpoint = "rabbit.test", port = 5671, secret_reference = "projecty-aws-mid-commands/client" }
        event_bus           = { endpoint = "projecty-kafka-kafka-bootstrap.projecty.svc.cluster.local", port = 9092, secret_reference = null }
        object_store        = { endpoint = "https://objects.test", port = 443, secret_reference = null }
        time_series_store   = { endpoint = "cassandra.test", port = 9142, secret_reference = null }
        transactional_store = { endpoint = "sql.test", port = 26257, secret_reference = null }
      }
      object_store = { name = "projecty-test-objects", region = "us-east-1" }
    }
  }
}

run "mid_workloads_plan_with_strimzi" {
  command = plan

  variables {
    state_bucket = "projecty-test-state"
  }

  assert {
    condition     = output.profile.survives == "one Availability Zone"
    error_message = "Mid must state its Availability Zone survival promise."
  }
}
