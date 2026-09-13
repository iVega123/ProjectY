output "network" {
  description = "Network capability re-exported to preserve the state chain."
  value       = local.network
}

output "connections" {
  description = "Provider-neutral data connections passed to 20-platform."
  value = {
    cache               = module.cache.connection
    command_bus         = module.command_bus.connection
    object_store        = module.object_store.connection
    time_series_store   = module.time_series_store.connection
    transactional_store = module.transactional_store.connection
  }
}

output "database_secret_references" {
  description = "Logical SQL secret names keyed by the owning service."
  value       = module.transactional_store.service_secret_references
}

output "object_store" {
  description = "Logical object-store metadata."
  value       = module.object_store.bucket
}
