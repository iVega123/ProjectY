output "network" {
  description = "Network capability re-exported to preserve the state chain."
  value       = local.network
}

output "connections" {
  description = "Provider-neutral data connections passed to 20-platform."
  value = {
    cache             = module.cache.connection
    command_bus       = module.command_bus.connection
    object_store      = module.object_store.connection
    time_series_store = module.time_series_store.connection
  }
}

output "object_store" {
  description = "Logical object-store metadata."
  value       = module.object_store.bucket
}
