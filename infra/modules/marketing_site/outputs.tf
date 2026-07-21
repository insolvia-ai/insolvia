output "distribution_id" {
  description = "CloudFront distribution ID (for cache invalidation in CI)."
  value       = aws_cloudfront_distribution.site.id
}

output "distribution_domain_name" {
  description = "CloudFront distribution domain name."
  value       = aws_cloudfront_distribution.site.domain_name
}

output "assets_bucket_name" {
  description = "S3 bucket the hashed client build (build/client) is synced to."
  value       = aws_s3_bucket.assets.id
}

output "ecr_repository_url" {
  description = "ECR repository URL the SSR image is pushed to."
  value       = aws_ecr_repository.ssr.repository_url
}

output "ssr_function_name" {
  description = "SSR Lambda function name (for update-function-code in CI)."
  value       = aws_lambda_function.ssr.function_name
}

output "url" {
  description = "Canonical site URL."
  value       = "https://${var.www_domain}"
}
