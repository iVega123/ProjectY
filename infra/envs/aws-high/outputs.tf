output "profile" {
  description = "Explicit recovery promise and execution policy."
  value       = local.profile
}

output "kubernetes_connections" {
  description = "Kubernetes endpoints used by the regional traffic switch."
  value = {
    primary   = module.kubernetes_primary.connection
    secondary = module.kubernetes_secondary.connection
  }
}

output "event_bus_connections" {
  description = "Kafka endpoints used before and after regional failover."
  value = {
    primary   = module.event_bus_primary.connection
    secondary = module.event_bus_secondary.connection
  }
}

output "transactional_connection" {
  description = "Multi-region PostgreSQL-wire endpoint."
  value       = module.transactional_store.connection
}
