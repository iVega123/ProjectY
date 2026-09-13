variable "name" {
  type        = string
  description = "LocalStack container name."
}

variable "network_name" {
  type        = string
  description = "Docker network name."
}

module "engine" {
  source = "../container_engine"

  name         = var.name
  image        = "localstack/localstack:2026.8.2"
  network_name = var.network_name
  environment = [
    "DEBUG=0",
    "PERSISTENCE=1",
    "SERVICES=kms,s3,secretsmanager,sts",
  ]
  ports       = [{ internal = 4566, external = 4566 }]
  data_path   = "/var/lib/localstack"
  healthcheck = ["CMD-SHELL", "curl -fsS http://localhost:4566/_localstack/health | grep -q '\"s3\"'"]
}
