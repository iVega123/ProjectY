output "profile" {
  description = "Explicit recovery promise selected by this environment."
  value       = local.profile
}

output "cluster_connection" {
  description = "Portable Kubernetes API connection."
  value       = module.kubernetes_cluster.connection
}

output "daily_dump_bucket" {
  description = "S3 bucket consumed by the scheduled CockroachDB dump job."
  value       = module.daily_dump_store.bucket
}
