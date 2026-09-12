output "endpoint" {
  description = "LocalStack edge endpoint on the developer host."
  value       = "http://127.0.0.1:4566"
}

output "container_endpoint" {
  description = "LocalStack edge endpoint on the shared Docker network."
  value       = "http://${var.name}:4566"
}
