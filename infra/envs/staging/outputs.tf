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

output "auth_user_pool_id" {
  description = "Staging Cognito user pool ID."
  value       = module.auth.user_pool_id
}

output "auth_user_pool_arn" {
  description = "Staging Cognito user pool ARN."
  value       = module.auth.user_pool_arn
}

output "auth_web_client_id" {
  description = "Staging web SPA app client ID (authorization-code + PKCE)."
  value       = module.auth.web_client_id
}

output "auth_desktop_client_id" {
  description = "Staging desktop app client ID (loopback-redirect PKCE)."
  value       = module.auth.desktop_client_id
}

output "auth_domain" {
  description = "Staging hosted auth domain (Cognito-provided)."
  value       = module.auth.domain
}

output "auth_issuer_url" {
  description = "OIDC issuer the API will validate staging JWTs against (wired up with the first authenticated endpoint)."
  value       = module.auth.issuer_url
}
