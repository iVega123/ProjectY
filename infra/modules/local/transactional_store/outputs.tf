output "connection" {
  description = "Portable PostgreSQL-wire connection contract."
  value = {
    endpoint         = var.name
    port             = 26257
    secret_reference = null
  }
}
