variable "name" {
  type        = string
  description = "Logical CQL store name."
}

variable "network_name" {
  type        = string
  description = "Docker network name."
}

module "engine" {
  source = "../container_engine"

  name         = var.name
  image        = "cassandra:5.0.9"
  network_name = var.network_name
  environment = [
    "CASSANDRA_CLUSTER_NAME=projecty-local",
    "MAX_HEAP_SIZE=512M",
    "HEAP_NEWSIZE=128M",
  ]
  ports       = [{ internal = 9042, external = 9042 }]
  data_path   = "/var/lib/cassandra"
  healthcheck = ["CMD-SHELL", "cqlsh -e 'SELECT now() FROM system.local' >/dev/null"]
}
