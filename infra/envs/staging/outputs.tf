output "bucket_name" {
  description = "S3 bucket the staging web build is synced to."
  value       = module.web_hosting.bucket_name
}

output "distribution_id" {
  description = "CloudFront distribution ID for staging (cache invalidation)."
  value       = module.web_hosting.distribution_id
}

output "url" {
  value = module.web_hosting.url
}
