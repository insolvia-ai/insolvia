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

# Passed straight through, so the env roots and the deploy workflows' existing
# `terraform output -raw marketing_ecr_repository_url` keep working now that the
# repository is shared rather than module-owned.
output "ecr_repository_url" {
  description = "ECR repository URL the SSR image is pushed to (shared across environments)."
  value       = var.ecr_repository_url
}

output "ssr_alias_name" {
  description = "Alias serving traffic. The deploy workflow shifts it only after the new version passes its smoke test."
  value       = aws_lambda_alias.live.name
}

output "ssr_function_name" {
  description = "SSR Lambda function name (for update-function-code in CI)."
  value       = aws_lambda_function.ssr.function_name
}

output "url" {
  description = "Canonical site URL."
  value       = "https://${var.www_domain}"
}
