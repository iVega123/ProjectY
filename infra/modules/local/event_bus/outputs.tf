output "connection" {
  description = "Portable Kafka connection contract."
  value = {
    endpoint         = var.name
    port             = 9092
    secret_reference = null
  }
}

output "cluster" {
  description = "Logical event-bus metadata."
  value = {
    name = var.name
  }
}
