mock_provider "aws" {}
mock_provider "docker" {}
mock_provider "kind" {}
mock_provider "kubernetes" {}
mock_provider "random" {}

run "local_composition_plans_without_cloud_credentials" {
  command = plan

  assert {
    condition     = output.cluster.name == "projecty-local"
    error_message = "The local composition must keep the stable ProjectY kind cluster name."
  }

  assert {
    condition     = length(output.connections) == 7
    error_message = "Every portable capability must be represented in the local composition."
  }
}
