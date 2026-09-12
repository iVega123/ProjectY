output "connection" {
  description = "Portable S3 API connection contract."
  value = {
    endpoint         = "https://${aws_s3_bucket.this.bucket_regional_domain_name}"
    port             = 443
    secret_reference = null
  }
}

output "bucket" {
  description = "Logical object-store metadata."
  value = {
    name   = aws_s3_bucket.this.bucket
    region = aws_s3_bucket.this.region
  }
}
