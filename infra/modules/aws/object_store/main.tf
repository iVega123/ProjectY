resource "aws_s3_bucket" "this" {
  # Event fan-out is deliberately composed by the owning workload because this
  # capability has no queue or function in its portable contract.
  #checkov:skip=CKV2_AWS_62:Notifications require a consumer and belong in the workloads layer.
  # Access logs must target the account audit bucket to avoid recursive logging;
  # that platform bucket is attached by the environment, not this leaf module.
  #checkov:skip=CKV_AWS_18:Central access logging is an environment-level platform control.
  # Cross-region replication exists only in aws-high, which is never applied.
  #checkov:skip=CKV_AWS_144:Replication is selected by the regional-survival profile.
  bucket        = var.name
  force_destroy = var.force_destroy
  tags          = merge(var.tags, { Name = var.name })
}

resource "aws_s3_bucket_public_access_block" "this" {
  bucket = aws_s3_bucket.this.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "this" {
  bucket = aws_s3_bucket.this.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_versioning" "this" {
  bucket = aws_s3_bucket.this.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "this" {
  bucket = aws_s3_bucket.this.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = var.kms_key_arn == null ? "AES256" : "aws:kms"
      kms_master_key_id = var.kms_key_arn
    }
    bucket_key_enabled = var.kms_key_arn != null
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "this" {
  bucket = aws_s3_bucket.this.id

  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"

    filter {}

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }

    noncurrent_version_expiration {
      noncurrent_days = 30
    }
  }

  depends_on = [aws_s3_bucket_versioning.this]
}
