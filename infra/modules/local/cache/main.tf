variable "name" {
  type        = string
  description = "Logical cache name."
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
  image        = "valkey/valkey:8.1-alpine"
  network_name = var.network_name
  command      = ["valkey-server", "--requirepass", random_password.client.result]
  ports        = [{ internal = 6379, external = 6379 }]
  data_path    = "/data"
  healthcheck  = ["CMD-SHELL", "valkey-cli -a '${random_password.client.result}' ping | grep PONG"]
}
