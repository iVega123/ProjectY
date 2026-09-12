output "deployment" {
  description = "Content-addressed workload deployment evidence."
  value = {
    cluster       = terraform_data.manifests.output.cluster
    manifest_hash = terraform_data.manifests.output.manifest_hash
  }
}
