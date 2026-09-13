output "connection" {
  description = "Portable AMQP connection contract."
  value = {
    endpoint         = var.name
    port             = 5672
    secret_reference = "${var.name}/client"
  }
}

output "bootstrap_secret" {
  description = "Credential material persisted into LocalStack Secrets Manager by the environment."
  value = {
    username = "projecty_client"
    password = random_password.client.result
  }
  sensitive = true
}
