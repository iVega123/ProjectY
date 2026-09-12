output "network" {
  description = "Network capability re-exported to preserve the state chain."
  value       = local.network
}

output "connections" {
  description = "Data connections re-exported to 30-workloads."
  value       = data.terraform_remote_state.data.outputs.connections
}

output "object_store" {
  description = "Object-store metadata re-exported to 30-workloads."
  value       = data.terraform_remote_state.data.outputs.object_store
}

output "cluster" {
  description = "Kubernetes capability consumed by 30-workloads."
  value       = module.kubernetes_cluster.cluster
  sensitive   = true
}

output "cluster_connection" {
  description = "Provider-neutral Kubernetes API connection."
  value       = module.kubernetes_cluster.connection
}
