output "bucket_name" {
  description = "S3 bucket the desktop build artifacts are uploaded to."
  value       = aws_s3_bucket.artifacts.bucket
}

output "distribution_id" {
  description = "CloudFront distribution ID (for cache invalidation when an artifact is overwritten in place)."
  value       = aws_cloudfront_distribution.artifacts.id
}

output "url" {
  description = "Public HTTPS base URL artifacts are handed out under. Unlinked by design (D8) — nothing on the marketing site points here."
  value       = "https://${var.domain_name}"
}
