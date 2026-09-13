variable "name" {
  type        = string
  description = "Logical command-bus name."
}

variable "network_name" {
  type        = string
  description = "Docker network name."
}

resource "random_password" "client" {
  length  = 32
  special = false
}

module "engine" {
  source = "../container_engine"

  name         = var.name
  image        = "rabbitmq:4.3.5-management"
  network_name = var.network_name
  environment = [
    "RABBITMQ_DEFAULT_USER=projecty_client",
    "RABBITMQ_DEFAULT_PASS=${random_password.client.result}",
    "RABBITMQ_DEFAULT_VHOST=projecty-rental",
  ]
  ports = [
    { internal = 5672, external = 5672 },
    { internal = 15672, external = 15672 },
  ]
  data_path   = "/var/lib/rabbitmq"
  healthcheck = ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
}
