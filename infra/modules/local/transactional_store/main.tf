variable "name" {
  type        = string
  description = "Logical transactional-store name."
}

variable "network_name" {
  type        = string
  description = "Docker network name."
}

module "engine" {
  source = "../container_engine"

  name         = var.name
  image        = "cockroachdb/cockroach:v26.3.1"
  network_name = var.network_name
  command      = ["start-single-node", "--insecure", "--store=/cockroach/cockroach-data"]
  ports = [
    { internal = 26257, external = 26257 },
    { internal = 8080, external = 18080 },
  ]
  data_path   = "/cockroach/cockroach-data"
  healthcheck = ["CMD-SHELL", "cockroach sql --insecure --host=localhost:26257 --execute='SELECT 1' >/dev/null"]
}
