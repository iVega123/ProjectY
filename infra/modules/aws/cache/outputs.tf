output "connection" {
  description = "Portable RESP connection contract."
  value = {
    endpoint         = aws_elasticache_replication_group.this.primary_endpoint_address
    port             = aws_elasticache_replication_group.this.port
    secret_reference = aws_secretsmanager_secret.client.name
  }
}

