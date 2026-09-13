variable "name" {
  description = "Local capability network name."
  type        = string
}

resource "docker_network" "this" {
  name       = var.name
  attachable = true
  driver     = "bridge"
}

output "network" {
  description = "Local network capability."
  value = {
    id   = docker_network.this.id
    name = docker_network.this.name
  }
}
