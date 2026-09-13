resource "docker_image" "this" {
  name         = var.image
  keep_locally = true
}

resource "docker_volume" "data" {
  count = var.data_path == null ? 0 : 1
  name  = "${var.name}-data"
}

resource "docker_container" "this" {
  name         = var.name
  hostname     = var.name
  image        = docker_image.this.image_id
  command      = var.command
  env          = var.environment
  must_run     = true
  restart      = "unless-stopped"
  init         = true
  stop_timeout = 30

  networks_advanced {
    name    = var.network_name
    aliases = [var.name]
  }

  dynamic "ports" {
    for_each = var.ports
    content {
      internal = ports.value.internal
      external = ports.value.external
      ip       = "127.0.0.1"
      protocol = "tcp"
    }
  }

  dynamic "volumes" {
    for_each = var.data_path == null ? [] : [var.data_path]
    content {
      volume_name    = docker_volume.data[0].name
      container_path = volumes.value
    }
  }

  healthcheck {
    test         = var.healthcheck
    interval     = "5s"
    timeout      = "3s"
    retries      = 30
    start_period = "10s"
  }
}
