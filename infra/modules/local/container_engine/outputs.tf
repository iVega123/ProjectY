output "container" {
  description = "Implementation-private container metadata."
  value = {
    name = docker_container.this.name
  }
}
