output "bucket_name" {
  description = "S3 bucket holding the built web app."
  value       = aws_s3_bucket.site.bucket
}

output "distribution_id" {
  description = "CloudFront distribution ID (for cache invalidation)."
  value       = aws_cloudfront_distribution.site.id
}

output "url" {
  description = "Public HTTPS URL for this environment."
  value       = "https://${var.domain_name}"
}
