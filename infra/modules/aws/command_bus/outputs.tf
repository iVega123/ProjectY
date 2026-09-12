output "connection" {
  description = "Portable AMQP connection contract."
  value = {
    endpoint         = aws_mq_broker.this.instances[0].endpoints[0]
    port             = 5671
    secret_reference = aws_secretsmanager_secret.client.name
  }
}

