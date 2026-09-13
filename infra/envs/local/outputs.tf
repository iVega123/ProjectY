output "connections" {
  description = "Provider-neutral capability connections on the shared Docker network."
  value = {
    cache               = module.cache.connection
    command_bus         = module.command_bus.connection
    event_bus           = module.event_bus.connection
    kubernetes_cluster  = module.kubernetes_cluster.connection
    object_store        = module.object_store.connection
    time_series_store   = module.time_series_store.connection
    transactional_store = module.transactional_store.connection
  }
}

output "object_store" {
  description = "Logical LocalStack S3 bucket metadata."
  value       = module.object_store.bucket
}

output "cluster" {
  description = "Logical kind cluster metadata."
  value = {
    name = local.name
  }
}
