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

output "api_ecr_repository_url" {
  description = "ECR repository the API deploy workflow pushes staging images to."
  value       = module.api_service.ecr_repository_url
}

output "api_lambda_function_name" {
  description = "Staging API Lambda name (deploy target for update-function-code/-configuration)."
  value       = module.api_service.lambda_function_name
}

output "api_domain" {
  description = "Hostname the staging API serves."
  value       = module.api_service.domain_name
}

output "api_http_api_id" {
  description = "Staging HTTP API id."
  value       = module.api_service.http_api_id
}

output "api_waitlist_table_name" {
  description = "Staging waitlist DynamoDB table."
  value       = module.api_service.waitlist_table_name
}

output "api_alarms_topic_arn" {
  description = "SNS topic for staging API alarms — subscribe by hand (Terraform manages no subscriptions)."
  value       = module.api_service.alarms_topic_arn
}
