output "connection" {
  description = "Portable S3 API connection contract."
  value = {
    endpoint         = var.endpoint
    port             = 4566
    secret_reference = null
  }
}

output "bucket" {
  description = "Logical object-store metadata."
  value       = module.aws_implementation.bucket
}
