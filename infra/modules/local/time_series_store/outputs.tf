output "connection" {
  description = "Portable CQL connection contract."
  value = {
    endpoint         = var.name
    port             = 9042
    secret_reference = null
  }
}

output "keyspace" {
  description = "Logical CQL metadata."
  value = {
    name = "projecty"
  }
}
