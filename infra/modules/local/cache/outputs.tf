output "connection" {
  description = "Portable RESP connection contract."
  value = {
    endpoint         = var.name
    port             = 6379
    secret_reference = "${var.name}/client"
  }
}

output "bootstrap_secret" {
  description = "Credential material persisted into LocalStack Secrets Manager by the environment."
  value = {
    token = random_password.client.result
  }
  sensitive = true
}
