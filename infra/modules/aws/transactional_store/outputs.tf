output "connection" {
  description = "Portable PostgreSQL-wire connection contract."
  value = {
    endpoint         = aws_db_instance.this.address
    port             = aws_db_instance.this.port
    secret_reference = aws_secretsmanager_secret.administrator.name
  }
}

output "database" {
  description = "Logical transactional-store metadata."
  value = {
    name = var.database_name
  }
}
