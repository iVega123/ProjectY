output "deployment" {
  description = "Content-addressed workload deployment evidence."
  value = {
    cluster       = terraform_data.manifests.output.cluster
    manifest_hash = terraform_data.manifests.output.manifest_hash
  }
}

output "profile" {
  description = "Explicit availability-zone recovery promise."
  value = {
    kafka               = "Strimzi with three brokers on EKS"
    transactional_store = "CockroachDB Cloud Standard in one region"
    rto                 = "minutes"
    rpo                 = "approximately five minutes"
    survives            = "one Availability Zone"
  }
}
