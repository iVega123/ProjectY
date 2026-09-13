mock_provider "aws" {}

run "mid_network_plans_without_cloud_credentials" {
  command = plan

  assert {
    condition     = length(output.network.availability_zones) == 3
    error_message = "Mid must spread the network across three Availability Zones."
  }
}
